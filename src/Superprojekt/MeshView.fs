namespace Superprojekt

open Aardvark.Base
open Aardworx.WebAssembly
open Aardvark.Rendering
open FSharp.Data.Adaptive
open Aardvark.Dom

type LoadedMesh =
    {
        centroid : aval<V3d>
        pos  : aval<IBuffer>
        tc   : aval<IBuffer>
        nrm  : aval<IBuffer>
        idx  : aval<IBuffer>
        tex  : aval<ITexture>
        fvc  : aval<int>
        mesh : MeshData option ref
    }

module MeshView =

    let apiBase = ApiConfig.apiBase

    let private meshes = System.Collections.Generic.Dictionary<string, LoadedMesh>()

    let loadMeshAsync (finished : unit -> unit) (name : string) : LoadedMesh =
        match meshes.TryGetValue(name) with
        | true, m -> m
        | _ ->
            let ccc = cval V3d.Zero
            let m =
                {
                    centroid = ccc
                    pos  = cval (ArrayBuffer [| V3f.Zero; V3f.Zero; V3f.Zero |] :> IBuffer)
                    tc   = cval (ArrayBuffer [| V2f.Zero; V2f.Zero; V2f.Zero |] :> IBuffer)
                    nrm  = cval (ArrayBuffer [| V3f.OOI; V3f.OOI; V3f.OOI |] :> IBuffer)
                    idx  = cval (ArrayBuffer [| 0; 1; 2 |] :> IBuffer)
                    tex  = cval<ITexture> (AVal.force DefaultTextures.checkerboard)
                    fvc  = cval 3
                    mesh = ref None
                }
            meshes.[name] <- m
            task {
                try
                    let! mesh = MeshData.fetch apiBase.Value name 0
                    m.mesh.Value <- Some mesh
                    transact (fun () ->
                        ccc.Value <- mesh.centroid
                        (m.pos :?> cval<IBuffer>).Value <- ArrayBuffer mesh.positions
                        (m.tc  :?> cval<IBuffer>).Value <- ArrayBuffer mesh.uvs
                        (m.nrm :?> cval<IBuffer>).Value <- ArrayBuffer mesh.normals
                        (m.idx :?> cval<IBuffer>).Value <- ArrayBuffer mesh.indices
                        (m.fvc :?> cval<int>).Value     <- mesh.indices.Length
                    )
                    let! img = JSImage.load mesh.atlasUrl
                    transact (fun () -> (m.tex :?> cval<ITexture>).Value <- JSTexture(img, true))

                    finished()
                with e ->
                    Log.error "failed to load mesh %s: %A" name e
            } |> ignore
            m

    let private meshTrafo
        (commonCentroid : aval<V3d>) (loaded : LoadedMesh)
        (meshScale : aval<float>) (meshTransform : aval<Trafo3d>) =
        let base_ =
            (commonCentroid, loaded.centroid, meshScale) |||> AVal.map3 (fun common mesh scale ->
                Trafo3d.Translation(mesh - common) * Trafo3d.Scale(scale))
        (base_, meshTransform) ||> AVal.map2 (fun b t -> t * b)

    let private scaleFor (model : AdaptiveModel) (name : string) =
        model.DatasetScales |> AVal.map (fun m -> DatasetScale.forMesh m name)

    let visibleMeshNames (model : AdaptiveModel) =
        let visible = AVal.force model.MeshVisible
        model.MeshNames |> AList.toAVal |> AVal.force |> IndexList.toList
        |> List.filter (fun n -> Map.tryFind n visible |> Option.defaultValue true)

    // Pin anchor blobs as 32-slot uniform arrays, converted metric → render
    // space here. Shared by the mesh shader (isolation + provenance) and the
    // fusion shader (conditioning).
    let private pinBlobUniforms (model : AdaptiveModel) =
        let datasetScale =
            (model.ActiveDataset, model.DatasetScales) ||> AVal.map2 DatasetScale.active
        let blobsArr =
            (model.ScanPins.Pins |> AMap.toAVal, model.CommonCentroid, datasetScale)
            |||> AVal.map3 (fun pinsMap cc scale ->
                let pins  = HashMap.toArray pinsMap |> Array.map snd
                let n     = min pins.Length MeshShader.MaxBlobs
                let centres  = Array.zeroCreate<V4f> MeshShader.MaxBlobs
                let falloffs = Array.zeroCreate<V4f> MeshShader.MaxBlobs
                for i in 0 .. n - 1 do
                    let p  = pins.[i]
                    let cr = (p.Centre - cc) * scale
                    let ir = float32 (p.InnerRadius   * scale)
                    let fr = float32 (p.FalloffRadius * scale)
                    centres.[i]  <- V4f(float32 cr.X, float32 cr.Y, float32 cr.Z, ir)
                    falloffs.[i] <- V4f(fr, 0.0f, 0.0f, 0.0f)
                n, centres, falloffs)
        blobsArr |> AVal.map (fun (n, _, _) -> n),
        blobsArr |> AVal.map (fun (_, c, _) -> c),
        blobsArr |> AVal.map (fun (_, _, f) -> f)

    let buildScene (loadFinished : string -> unit) (model : AdaptiveModel) : aset<ISceneNode> =
        let renderingModeInt =
            model.RenderingMode |> AVal.map (function
                | Textured     -> 0
                | Shaded       -> 1
                | SlopeColor   -> 2)
        let meshIndices =
            model.MeshNames |> AList.toAVal |> AVal.map (fun names ->
                names |> Seq.mapi (fun i n -> n, i) |> Map.ofSeq)
        let palette = Primitives.meshPaletteV4d

        // LassoEnabled gates the count to 0 so a disabled lasso has no effect
        // while the volume is kept for re-enabling.
        let lassoPlaneCount =
            (model.LassoVolume, model.LassoEnabled) ||> AVal.map2 (fun lv on ->
                match lv with
                | Some v when on -> min v.Planes.Length MeshShader.MaxLassoPlanes
                | _              -> 0)
        let lassoPlanes =
            model.LassoVolume |> AVal.map (fun lv ->
                let arr = Array.zeroCreate<V4f> MeshShader.MaxLassoPlanes
                match lv with
                | Some v ->
                    let n = min v.Planes.Length MeshShader.MaxLassoPlanes
                    for i in 0 .. n - 1 do
                        let p = v.Planes.[i]
                        arr.[i] <- V4f(float32 p.X, float32 p.Y, float32 p.Z, float32 p.W)
                | None -> ()
                arr)

        let blobCount, blobs, blobFalloffs = pinBlobUniforms model
        // Chart column highlight (hover wins over sticky): the highlighted
        // mesh renders normally, every other mesh drops to the shader's
        // uniform-ghost path (MeshActive=false) at a fixed 0.2 alpha —
        // independent of the GhostSilhouette toggle, because this is an
        // explicit user gesture.
        let chartHighlight =
            (model.ChartHoverMesh, model.ChartStickyMesh)
            ||> AVal.map2 (fun hov sticky -> hov |> Option.orElse sticky)
        let anchorGhost =
            model.AnchorGhostMode |> AVal.map (fun on -> if on then 1 else 0)
        let provenanceOn =
            model.ProvenanceHeatmap |> AVal.map (fun on -> if on then 1 else 0)
        let provThreshold =
            model.ProvenanceThreshold |> AVal.map float32
        let falloffZoneOnly =
            model.FalloffZoneOnly |> AVal.map (fun on -> if on then 1 else 0)
        model.MeshNames |> AList.map (fun name ->
            let loaded = loadMeshAsync (fun () -> loadFinished name) name
            let isActive =
                (model.MeshVisible, chartHighlight) ||> AVal.map2 (fun m h ->
                    let vis = Map.tryFind name m |> Option.defaultValue true
                    match h with
                    | Some hm -> vis && hm = name
                    | None -> vis)
            let scale = scaleFor model name
            let meshT =
                model.MeshTransforms |> AVal.map (fun m ->
                    Map.tryFind name m |> Option.defaultValue Trafo3d.Identity)
            // Inactive meshes still render (as ghost); gate only on load state
            // and fusion mode (the fusion composite replaces the normal pass).
            let renderEnabled =
                (loaded.fvc, model.FusionMode) ||> AVal.map2 (fun c f -> c > 3 && not f)
            let meshColor =
                meshIndices |> AVal.map (fun m ->
                    let i = Map.tryFind name m |> Option.defaultValue 0
                    V4f palette.[i % palette.Length])
            let meshDatasetErr =
                (model.MeshSensorTypes, model.MeshDatasetErrors)
                ||> AVal.map2 (fun sensors overrides ->
                    Provenance.datasetError overrides sensors name |> float32)
            let meshAlgoRes =
                model.MeshAlgorithmResidual
                |> AVal.map (fun m ->
                    Map.tryFind name m |> Option.defaultValue 0.0 |> float32)
            sg {
                Sg.Active renderEnabled
                Sg.Trafo (meshTrafo model.CommonCentroid loaded scale meshT)
                Sg.Shader {
                    DefaultSurfaces.trafo
                    DefaultSurfaces.diffuseTexture
                    MeshShader.shade
                }
                Sg.Uniform("DiffuseColorTexture", loaded.tex)
                Sg.Uniform("MeshActive",      isActive)
                // GhostSilhouette off → 0 → the shader's ghost path discards.
                Sg.Uniform("GhostOpacity",
                    (model.GhostSilhouette, model.GhostOpacity, chartHighlight)
                    |||> AVal.map3 (fun on op h ->
                        match h with
                        | Some hm when hm <> name -> 0.2f
                        | _ -> if on then float32 op else 0.0f))
                Sg.Uniform("RenderingMode",   renderingModeInt)
                Sg.Uniform("MeshColor",       meshColor)
                Sg.Uniform("ShadingStrength", model.ShadingStrength |> AVal.map float32)
                Sg.Uniform("SlopeThreshold",
                    model.SlopeThresholdDeg |> AVal.map (fun d ->
                        sin (d * System.Math.PI / 180.0) |> float32))
                Sg.Uniform("LassoPlaneCount", lassoPlaneCount)
                Sg.Uniform("LassoPlanes",     lassoPlanes)
                Sg.Uniform("BlobCount",       blobCount)
                Sg.Uniform("Blobs",           blobs)
                Sg.Uniform("BlobFalloffs",    blobFalloffs)
                Sg.Uniform("AnchorGhost",     anchorGhost)
                Sg.Uniform("ProvenanceHeatmap", provenanceOn)
                Sg.Uniform("ProvThreshold",     provThreshold)
                Sg.Uniform("FalloffZoneOnly",   falloffZoneOnly)
                Sg.Uniform("MeshDatasetError",  meshDatasetErr)
                Sg.Uniform("MeshAlgoResidual",  meshAlgoRes)
                Sg.VertexAttributes(
                    HashMap.ofList [
                        string DefaultSemantic.Positions,               BufferView(loaded.pos, typeof<V3f>)
                        string DefaultSemantic.DiffuseColorCoordinates, BufferView(loaded.tc,  typeof<V2f>)
                        string DefaultSemantic.Normals,                 BufferView(loaded.nrm, typeof<V3f>)
                    ]
                )
                Sg.Index(BufferView(loaded.idx, typeof<int>))
                Sg.Render loaded.fvc
            }
        ) |> AList.toASet

    let buildFusionNode (model : AdaptiveModel) (view : aval<Trafo3d>) (proj : aval<Trafo3d>) : ISceneNode =
        let blobCount, blobs, blobFalloffs = pinBlobUniforms model
        // Before any registration the meshes aren't aligned, so fusing is
        // meaningless: show only the reference mesh (fusionNotice explains).
        let hasRegistered =
            model.MeshTransforms |> AVal.map (fun m -> not (Map.isEmpty m))
        let refMesh =
            model.Registration |> AVal.map (fun r -> r.ReferenceMesh)
        let nodes =
            model.MeshNames |> AList.map (fun name ->
                let loaded = loadMeshAsync (fun () -> ()) name
                let isActive =
                    model.MeshVisible |> AVal.map (fun m -> Map.tryFind name m |> Option.defaultValue true)
                let scale = scaleFor model name
                let meshT =
                    model.MeshTransforms |> AVal.map (fun m ->
                        Map.tryFind name m |> Option.defaultValue Trafo3d.Identity)
                let regGate =
                    (hasRegistered, refMesh) ||> AVal.map2 (fun reg rm ->
                        match rm with
                        | Some r -> reg || r = name
                        | None   -> true)
                let renderEnabled =
                    (loaded.fvc, isActive, regGate) |||> AVal.map3 (fun c a g -> c > 3 && a && g)
                let meshDatasetErr =
                    (model.MeshSensorTypes, model.MeshDatasetErrors)
                    ||> AVal.map2 (fun sensors overrides ->
                        Provenance.datasetError overrides sensors name |> float32)
                let meshAlgoRes =
                    model.MeshAlgorithmResidual
                    |> AVal.map (fun m -> Map.tryFind name m |> Option.defaultValue 0.0 |> float32)
                sg {
                    Sg.Active renderEnabled
                    Sg.Trafo (meshTrafo model.CommonCentroid loaded scale meshT)
                    Sg.Shader {
                        DefaultSurfaces.trafo
                        DefaultSurfaces.diffuseTexture
                        FusionShader.shade
                    }
                    Sg.Uniform("DiffuseColorTexture", loaded.tex)
                    Sg.Uniform("MeshDatasetError", meshDatasetErr)
                    Sg.Uniform("MeshAlgoResidual", meshAlgoRes)
                    Sg.VertexAttributes(
                        HashMap.ofList [
                            string DefaultSemantic.Positions,               BufferView(loaded.pos, typeof<V3f>)
                            string DefaultSemantic.DiffuseColorCoordinates, BufferView(loaded.tc,  typeof<V2f>)
                            string DefaultSemantic.Normals,                 BufferView(loaded.nrm, typeof<V3f>)
                        ]
                    )
                    Sg.Index(BufferView(loaded.idx, typeof<int>))
                    Sg.Render loaded.fvc
                }
            ) |> AList.toASet
        sg {
            Sg.View view
            Sg.Proj proj
            Sg.Uniform("BlobCount",    blobCount)
            Sg.Uniform("Blobs",        blobs)
            Sg.Uniform("BlobFalloffs", blobFalloffs)
            nodes
        }

    // useTransforms = false renders the reference state (identity transforms,
    // all visible) for Photo mode; true renders the live state for Render mode.
    let buildPanoramaNode (model : AdaptiveModel) (useTransforms : bool) (view : aval<Trafo3d>) (proj : aval<Trafo3d>) : ISceneNode =
        let nodes =
            model.MeshNames |> AList.map (fun name ->
                let loaded = loadMeshAsync (fun () -> ()) name
                let isActive =
                    if useTransforms then
                        model.MeshVisible |> AVal.map (fun m -> Map.tryFind name m |> Option.defaultValue true)
                    else AVal.constant true
                let scale = scaleFor model name
                let meshT =
                    if useTransforms then
                        model.MeshTransforms |> AVal.map (fun m ->
                            Map.tryFind name m |> Option.defaultValue Trafo3d.Identity)
                    else AVal.constant Trafo3d.Identity
                let renderEnabled =
                    (loaded.fvc, isActive) ||> AVal.map2 (fun c a -> c > 3 && a)
                sg {
                    Sg.Active renderEnabled
                    Sg.Trafo (meshTrafo model.CommonCentroid loaded scale meshT)
                    Sg.Shader {
                        DefaultSurfaces.trafo
                        DefaultSurfaces.diffuseTexture
                        PanoramaShader.shade
                    }
                    Sg.Uniform("DiffuseColorTexture", loaded.tex)
                    Sg.VertexAttributes(
                        HashMap.ofList [
                            string DefaultSemantic.Positions,               BufferView(loaded.pos, typeof<V3f>)
                            string DefaultSemantic.DiffuseColorCoordinates, BufferView(loaded.tc,  typeof<V2f>)
                            string DefaultSemantic.Normals,                 BufferView(loaded.nrm, typeof<V3f>)
                        ]
                    )
                    Sg.Index(BufferView(loaded.idx, typeof<int>))
                    Sg.Render loaded.fvc
                }
            ) |> AList.toASet
        sg {
            Sg.View view
            Sg.Proj proj
            nodes
        }
