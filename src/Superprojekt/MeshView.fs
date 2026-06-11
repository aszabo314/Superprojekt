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

// Elevation-cursor slicing-plane highlight parameters, world-space metres.
// Built in View.fs from the chart cursor (priority) or the 3D hover point
// inside the effective pin's probe cylinder; None = highlight off.
type CursorHighlight =
    {
        Origin    : V3d
        Normal    : V3d
        Clip      : bool
        PinCentre : V3d
        PinRadius : float
        CylLength : float
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
        // Trafo composition is postfix (a * b applies a first): base maps
        // mesh-local → render space, THEN the registration trafo (a
        // render-space map, see RigidTransform) applies. The previous `t * b`
        // order applied the render-space map to mesh-local coordinates —
        // invisible for translations at dataset scale 1, but wrong for
        // scaled datasets and large landmark rotations, and inconsistent
        // with every renderToWorld-based query path.
        (base_, meshTransform) ||> AVal.map2 (fun b t -> b * t)

    let private scaleFor (model : AdaptiveModel) (name : string) =
        model.DatasetScales |> AVal.map (fun m -> DatasetScale.forMesh m name)

    // Committed render trafo, and the effective one (committed ∘ pending
    // preview delta) every mesh renders with while a solve preview is open.
    let committedMeshT (model : AdaptiveModel) (name : string) =
        model.MeshTransforms |> AVal.map (fun m ->
            Map.tryFind name m |> Option.defaultValue Trafo3d.Identity)

    let effectiveMeshT (model : AdaptiveModel) (name : string) =
        (model.MeshTransforms, model.PendingReg) ||> AVal.map2 (fun m pending ->
            let c = Map.tryFind name m |> Option.defaultValue Trafo3d.Identity
            match PendingRegistration.delta name pending with
            | Some d -> RegLog.effective c d
            | None -> c)

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

    // Smoothstep half-width of the contact-line highlight band (metres) and
    // the darkening applied to the rest of an intersected mesh.
    [<Literal>]
    let private cursorHighlightWidth = 0.2
    [<Literal>]
    let private cursorDarken = 0.85f

    let buildScene (loadFinished : string -> unit) (cursor : aval<CursorHighlight option>) (model : AdaptiveModel) : aset<ISceneNode> =
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
        // Cursor-plane uniforms shared by every mesh, converted metric →
        // render space once. CursorActive is the only per-mesh one (below).
        let cursorRender =
            let datasetScaleA =
                (model.ActiveDataset, model.DatasetScales) ||> AVal.map2 DatasetScale.active
            (cursor, model.CommonCentroid, datasetScaleA) |||> AVal.map3 (fun cOpt cc s ->
                match cOpt with
                | Some c ->
                    V3f (ScanPin.renderCentre cc s c.Origin),
                    V3f c.Normal,
                    (if c.Clip then 1 else 0),
                    V3f (ScanPin.renderCentre cc s c.PinCentre),
                    float32 (ScanPin.renderLength s c.PinRadius),
                    float32 (ScanPin.renderLength s c.CylLength),
                    float32 (ScanPin.renderLength s cursorHighlightWidth)
                | None -> V3f.Zero, V3f.OOI, 0, V3f.Zero, 0.0f, 0.0f, 0.0f)
        let cursorOrigin = cursorRender |> AVal.map (fun (o, _, _, _, _, _, _) -> o)
        let cursorNormal = cursorRender |> AVal.map (fun (_, n, _, _, _, _, _) -> n)
        let cursorClip   = cursorRender |> AVal.map (fun (_, _, c, _, _, _, _) -> c)
        let cursorPinC   = cursorRender |> AVal.map (fun (_, _, _, p, _, _, _) -> p)
        let cursorPinR   = cursorRender |> AVal.map (fun (_, _, _, _, r, _, _) -> r)
        let cursorCylLen = cursorRender |> AVal.map (fun (_, _, _, _, _, l, _) -> l)
        let cursorWidth  = cursorRender |> AVal.map (fun (_, _, _, _, _, _, w) -> w)
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
        let heatmapModeInt =
            model.HeatmapMode |> AVal.map (function
                | HeatOff -> 0
                | HeatProvenance -> 1
                | HeatDiff -> 2)
        let diffSigmaRef =
            (model.Registration, model.MeshSensorTypes, model.MeshDatasetErrors)
            |||> AVal.map3 (fun reg sensors overrides ->
                match reg.ReferenceMesh with
                | Some r -> Provenance.datasetError overrides sensors r |> float32
                | None -> 0.0f)
        let provThreshold =
            model.ProvenanceThreshold |> AVal.map float32
        let falloffZoneOnly =
            model.FalloffZoneOnly |> AVal.map (fun on -> if on then 1 else 0)
        model.MeshNames |> AList.map (fun name ->
            let loaded = loadMeshAsync (fun () -> loadFinished name) name
            // One-shot 3D anchor pick: the target mesh is the only solid one
            // (forced visible), the reference shows at α 0.3, everything else
            // ghosts — all shader-level, so nothing needs restoring after.
            let isActive =
                (model.MeshVisible, chartHighlight, model.AnchorPick) |||> AVal.map3 (fun m h ap ->
                    match ap with
                    | Some pick -> pick.Mesh = name
                    | None ->
                        let vis = Map.tryFind name m |> Option.defaultValue true
                        match h with
                        | Some hm -> vis && hm = name
                        | None -> vis)
            let scale = scaleFor model name
            // Effective pose: committed ∘ pending preview delta.
            let meshT = effectiveMeshT model name
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
            // Diff-mode inputs: per-mesh algo residual before/after the
            // pending solve, and the inverse preview delta that maps a
            // preview-pose fragment back to its committed-pose position.
            // Meshes without a pending delta diff to zero (→ masked).
            let diffData =
                (model.PendingReg, model.MeshAlgorithmResidual) ||> AVal.map2 (fun pending algo ->
                    let before = Map.tryFind name algo |> Option.defaultValue 0.0 |> float32
                    match pending |> Option.bind (fun pr -> Map.tryFind name pr.Results) with
                    | Some r -> float32 r.RmsAfter, before, M44f r.Delta.Backward
                    | None -> before, before, M44f.Identity)
            // "All meshes the plane intersects": the cursor effect (darken +
            // band) only activates on meshes whose registered-world bbox
            // touches the highlight slab — and, when clipped, the cylinder's
            // bounding sphere. World-metric math; conservative on rotation.
            let cursorActive =
                AVal.custom (fun t ->
                    match cursor.GetValue t with
                    | None -> 0
                    | Some c ->
                        match Map.tryFind name (model.MeshBounds.GetValue t) with
                        | None -> 1
                        | Some box ->
                            let tw =
                                let committed =
                                    Map.tryFind name (model.MeshTransforms.GetValue t)
                                    |> Option.defaultValue Trafo3d.Identity
                                let eff =
                                    match PendingRegistration.delta name (model.PendingReg.GetValue t) with
                                    | Some d -> RegLog.effective committed d
                                    | None -> committed
                                RigidTransform.renderToWorld
                                    (DatasetScale.forMesh (model.DatasetScales.GetValue t) name)
                                    (model.CommonCentroid.GetValue t) eff
                            let mutable dMin = infinity
                            let mutable dMax = -infinity
                            let mutable bMin = V3d(infinity, infinity, infinity)
                            let mutable bMax = V3d(-infinity, -infinity, -infinity)
                            for ix in 0 .. 1 do
                                for iy in 0 .. 1 do
                                    for iz in 0 .. 1 do
                                        let corner =
                                            V3d((if ix = 0 then box.Min.X else box.Max.X),
                                                (if iy = 0 then box.Min.Y else box.Max.Y),
                                                (if iz = 0 then box.Min.Z else box.Max.Z))
                                        let p = tw.Forward.TransformPos corner
                                        let d = Vec.dot (p - c.Origin) c.Normal
                                        if d < dMin then dMin <- d
                                        if d > dMax then dMax <- d
                                        bMin <- V3d(min bMin.X p.X, min bMin.Y p.Y, min bMin.Z p.Z)
                                        bMax <- V3d(max bMax.X p.X, max bMax.Y p.Y, max bMax.Z p.Z)
                            let slabHit = dMin <= cursorHighlightWidth && dMax >= -cursorHighlightWidth
                            let cylHit =
                                if not c.Clip then true
                                else
                                    let q = c.PinCentre
                                    let cl =
                                        V3d(clamp bMin.X bMax.X q.X,
                                            clamp bMin.Y bMax.Y q.Y,
                                            clamp bMin.Z bMax.Z q.Z)
                                    let bound = sqrt (c.PinRadius * c.PinRadius + 0.25 * c.CylLength * c.CylLength)
                                    (cl - q).Length <= bound
                            if slabHit && cylHit then 1 else 0)
            let surface =
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
                    // GhostSilhouette off → 0 → the shader's ghost path
                    // discards. Anchor-pick mode wins over the chart-column
                    // highlight; both are explicit user gestures with fixed
                    // alphas independent of the silhouette toggle.
                    Sg.Uniform("GhostOpacity",
                        AVal.custom (fun t ->
                            match model.AnchorPick.GetValue t with
                            | Some pick when pick.Mesh <> name ->
                                if (model.Registration.GetValue t).ReferenceMesh = Some name
                                then 0.3f else 0.08f
                            | _ ->
                                match chartHighlight.GetValue t with
                                | Some hm when hm <> name -> 0.2f
                                | _ ->
                                    if model.GhostSilhouette.GetValue t
                                    then float32 (model.GhostOpacity.GetValue t)
                                    else 0.0f))
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
                    Sg.Uniform("HeatmapMode",       heatmapModeInt)
                    Sg.Uniform("ProvThreshold",     provThreshold)
                    Sg.Uniform("FalloffZoneOnly",   falloffZoneOnly)
                    Sg.Uniform("MeshDatasetError",  meshDatasetErr)
                    Sg.Uniform("MeshAlgoResidual",  meshAlgoRes)
                    Sg.Uniform("DiffAlgoAfter",     diffData |> AVal.map (fun (a, _, _) -> a))
                    Sg.Uniform("DiffAlgoBefore",    diffData |> AVal.map (fun (_, b, _) -> b))
                    Sg.Uniform("DiffInvDelta",      diffData |> AVal.map (fun (_, _, m) -> m))
                    Sg.Uniform("DiffSigmaRef",      diffSigmaRef)
                    Sg.Uniform("CursorActive",         cursorActive)
                    Sg.Uniform("CursorPlaneOrigin",    cursorOrigin)
                    Sg.Uniform("CursorPlaneNormal",    cursorNormal)
                    Sg.Uniform("CursorHighlightWidth", cursorWidth)
                    Sg.Uniform("CursorDarken",         AVal.constant cursorDarken)
                    Sg.Uniform("CursorClip",           cursorClip)
                    Sg.Uniform("CursorPinCentre",      cursorPinC)
                    Sg.Uniform("CursorPinRadius",      cursorPinR)
                    Sg.Uniform("CursorCylLength",      cursorCylLen)
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
            // While this mesh has a pending preview delta, additionally show
            // its committed pose through the shader's uniform-ghost path with
            // a distinct slate tint. Ghost fragments write far depth, so
            // picks pass through to the previewed surface.
            let ghostActive =
                (renderEnabled, model.PendingReg) ||> AVal.map2 (fun r pending ->
                    r && (PendingRegistration.delta name pending |> Option.isSome))
            let committedGhost =
                sg {
                    Sg.Active ghostActive
                    Sg.Trafo (meshTrafo model.CommonCentroid loaded scale (committedMeshT model name))
                    Sg.Shader {
                        DefaultSurfaces.trafo
                        DefaultSurfaces.diffuseTexture
                        MeshShader.shade
                    }
                    Sg.NoEvents
                    Sg.Uniform("DiffuseColorTexture", loaded.tex)
                    Sg.Uniform("MeshActive",      AVal.constant false)
                    Sg.Uniform("GhostOpacity",    AVal.constant 0.2f)
                    Sg.Uniform("RenderingMode",   AVal.constant 1)
                    Sg.Uniform("MeshColor",       AVal.constant (V4f(0.45f, 0.49f, 0.55f, 1.0f)))
                    Sg.Uniform("ShadingStrength", AVal.constant 0.0f)
                    Sg.Uniform("SlopeThreshold",  AVal.constant 0.5f)
                    Sg.Uniform("LassoPlaneCount", AVal.constant 0)
                    Sg.Uniform("LassoPlanes",     lassoPlanes)
                    Sg.Uniform("BlobCount",       AVal.constant 0)
                    Sg.Uniform("Blobs",           blobs)
                    Sg.Uniform("BlobFalloffs",    blobFalloffs)
                    Sg.Uniform("AnchorGhost",     AVal.constant 0)
                    Sg.Uniform("HeatmapMode",       AVal.constant 0)
                    Sg.Uniform("ProvThreshold",     AVal.constant 1.0f)
                    Sg.Uniform("FalloffZoneOnly",   AVal.constant 0)
                    Sg.Uniform("MeshDatasetError",  AVal.constant 0.0f)
                    Sg.Uniform("MeshAlgoResidual",  AVal.constant 0.0f)
                    Sg.Uniform("DiffAlgoAfter",     AVal.constant 0.0f)
                    Sg.Uniform("DiffAlgoBefore",    AVal.constant 0.0f)
                    Sg.Uniform("DiffInvDelta",      AVal.constant M44f.Identity)
                    Sg.Uniform("DiffSigmaRef",      AVal.constant 0.0f)
                    Sg.Uniform("CursorActive",         AVal.constant 0)
                    Sg.Uniform("CursorPlaneOrigin",    cursorOrigin)
                    Sg.Uniform("CursorPlaneNormal",    cursorNormal)
                    Sg.Uniform("CursorHighlightWidth", cursorWidth)
                    Sg.Uniform("CursorDarken",         AVal.constant cursorDarken)
                    Sg.Uniform("CursorClip",           cursorClip)
                    Sg.Uniform("CursorPinCentre",      cursorPinC)
                    Sg.Uniform("CursorPinRadius",      cursorPinR)
                    Sg.Uniform("CursorCylLength",      cursorCylLen)
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
            sg { surface; committedGhost }
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
                let meshT = effectiveMeshT model name
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
                    if useTransforms then effectiveMeshT model name
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
