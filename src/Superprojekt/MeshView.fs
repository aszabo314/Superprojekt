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
        // Max |local vertex| (metric) = farthest point from the mesh origin
        // (= sensor); normalises the range heatmap false-colour.
        localMaxR : aval<float>
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
                    localMaxR = cval 1.0
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
                        let maxR = mesh.positions |> Array.fold (fun mx (p : V3f) -> max mx (float p.Length)) 0.0
                        (m.localMaxR :?> cval<float>).Value <- max 1e-6 maxR
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
        // Postfix composition (a * b applies a first): base maps mesh-local →
        // render space, THEN the registration trafo (a render-space map). Don't
        // flip to `t * b` — it applies the render-space map to mesh-local
        // coords, wrong for scaled datasets / large rotations and inconsistent
        // with the renderToWorld query paths.
        (base_, meshTransform) ||> AVal.map2 (fun b t -> b * t)

    let private scaleFor (model : AdaptiveModel) (name : string) =
        model.DatasetScales |> AVal.map (fun m -> DatasetScale.forMesh m name)

    let loadMeshT (model : AdaptiveModel) (name : string) =
        model.LoadTransforms |> AVal.map (fun m ->
            Map.tryFind name m |> Option.defaultValue Trafo3d.Identity)

    let displayedMeshT (model : AdaptiveModel) (name : string) =
        (model.RegView, model.SolvedTransforms, model.LoadTransforms)
        |||> AVal.map3 (fun view solved load ->
            match view, Map.tryFind name solved with
            | RegAfter, Some t -> t
            | _ -> Map.tryFind name load |> Option.defaultValue Trafo3d.Identity)

    // Pin blobs as a 32-slot uniform array, metric → render space (centre xyz,
    // inner radius w), for the mesh shader's pin-isolation filter.
    let private pinBlobUniforms (model : AdaptiveModel) =
        let datasetScale =
            (model.ActiveDataset, model.DatasetScales) ||> AVal.map2 DatasetScale.active
        let pinsF =
            model.ScanPins.Pins |> AMap.toAVal |> AVal.map (fun pinsMap ->
                HashMap.toArray pinsMap |> Array.map snd)
        let blobsArr =
            (pinsF, model.CommonCentroid, datasetScale)
            |||> AVal.map3 (fun pins cc scale ->
                let n     = min pins.Length MeshShader.MaxBlobs
                let centres  = Array.zeroCreate<V4f> MeshShader.MaxBlobs
                for i in 0 .. n - 1 do
                    let p  = pins.[i]
                    let cr = (p.Centre - cc) * scale
                    let ir = float32 (p.InnerRadius * scale)
                    centres.[i]  <- V4f(float32 cr.X, float32 cr.Y, float32 cr.Z, ir)
                n, centres)
        blobsArr |> AVal.map (fun (n, _) -> n),
        blobsArr |> AVal.map (fun (_, c) -> c)

    let buildScene (loadFinished : string -> unit) (clip : aval<int * V4f * V4f>) (wheelIsolation : aval<string option>) (model : AdaptiveModel) : aset<ISceneNode> =
        let renderingModeInt =
            model.RenderingMode |> AVal.map (function
                | Textured     -> 0
                | Shaded       -> 1
                | SlopeColor   -> 2)
        let meshIndices =
            model.MeshNames |> AList.toAVal |> AVal.map (fun names ->
                names |> Seq.mapi (fun i n -> n, i) |> Map.ofSeq)
        let palette = Primitives.meshPaletteV4d

        let blobCount, blobs = pinBlobUniforms model
        let clipCount  = clip |> AVal.map (fun (c, _, _) -> c)
        let clipPlane0 = clip |> AVal.map (fun (_, p, _) -> p)
        let clipPlane1 = clip |> AVal.map (fun (_, _, p) -> p)
        // Reference peek: while held with a reference set, the reference is the
        // only solid mesh — transient, no model mutation.
        let peekTarget =
            (model.ReferencePeekHeld, model.Registration) ||> AVal.map2 (fun held reg ->
                if held then reg.ReferenceMesh else None)
        // Auto-suspend pin isolation while placing an anchor so the terrain
        // stays visible for aiming (auto-restored, no model mutation).
        let anchorGhost =
            (model.AnchorGhostMode, model.ScanPins.Placement) ||> AVal.map2 (fun on pl ->
                match pl with
                | AnchorPlacement -> 0
                | _ -> if on then 1 else 0)
        model.MeshNames |> AList.map (fun name ->
            let loaded = loadMeshAsync (fun () -> loadFinished name) name
            // Isolation: reference peek wins, then wheelIsolation (Alt-wheel /
            // hover peek), else plain visibility.
            let isActive =
                AVal.custom (fun t ->
                    match peekTarget.GetValue t with
                    | Some target -> target = name
                    | None ->
                        match wheelIsolation.GetValue t with
                        | Some iso -> iso = name
                        | None ->
                            let vis = Map.tryFind name (model.MeshVisible.GetValue t) |> Option.defaultValue true
                            // Inspect central 3D: the reference carries the variance
                            // aggregate solid; moving meshes drop to faint ghost
                            // context — unless an intrinsic channel wants them solid.
                            let inspectGhost =
                                model.WorkflowStep.GetValue t = Inspect
                                && model.HeatmapMode.GetValue t = HeatOff
                                && (match (model.Registration.GetValue t).ReferenceMesh with
                                    | Some rf -> rf <> name
                                    | None -> false)
                            vis && not inspectGhost)
            let scale = scaleFor model name
            let meshT = displayedMeshT model name
            // Range-heatmap inputs: sensor origin (mesh-local 0,0,0) in render
            // space, and the normalising max range (metric maxR × dataset scale).
            let fullTrafo = meshTrafo model.CommonCentroid loaded scale meshT
            let sensorOrigin = fullTrafo |> AVal.map (fun t -> V3f (t.Forward.TransformPos V3d.Zero))
            let rangeMax = (loaded.localMaxR, scale) ||> AVal.map2 (fun r s -> float32 (max 1e-6 (r * s)))
            // Inactive meshes still render (as ghost); gate only on load state.
            let renderEnabled =
                loaded.fvc |> AVal.map (fun c -> c > 3)
            let meshColor =
                meshIndices |> AVal.map (fun m ->
                    let i = Map.tryFind name m |> Option.defaultValue 0
                    V4f palette.[i % palette.Length])
            // Per-vertex signed distance for this mesh (None unless soloed/
            // encoded). Projected early so a refetch doesn't churn other meshes.
            let myDist = model.SurfaceDistance |> AVal.map (Map.tryFind name)
            let distBuf =
                (myDist, loaded.pos) ||> AVal.map2 (fun d _pos ->
                    match d with
                    | Some arr -> ArrayBuffer arr :> IBuffer
                    | None ->
                        match loaded.mesh.Value with
                        | Some md -> ArrayBuffer (Array.zeroCreate<float32> md.positions.Length) :> IBuffer
                        | None -> ArrayBuffer [| 0.0f; 0.0f; 0.0f |] :> IBuffer)
            // Shape heatmap: per-vertex triangle quality (incident-face mean of
            // 4√3·A/Σl², clamped 0..1). Recomputed once when geometry loads.
            let shapeBuf =
                loaded.pos |> AVal.map (fun _ ->
                    match loaded.mesh.Value with
                    | Some md ->
                        let pos = md.positions
                        let idx = md.indices
                        let q   = Array.zeroCreate<float32> pos.Length
                        let cnt = Array.zeroCreate<int> pos.Length
                        let mutable f = 0
                        while f + 2 < idx.Length do
                            let a, b, c = idx.[f], idx.[f+1], idx.[f+2]
                            let pa, pb, pc = pos.[a], pos.[b], pos.[c]
                            let denom = (pb - pa).LengthSquared + (pc - pb).LengthSquared + (pa - pc).LengthSquared
                            let area  = 0.5f * (Vec.cross (pb - pa) (pc - pa)).Length
                            let ql    = if denom > 1e-12f then clamp 0.0f 1.0f (4.0f * 1.7320508f * area / denom) else 0.0f
                            q.[a] <- q.[a] + ql; cnt.[a] <- cnt.[a] + 1
                            q.[b] <- q.[b] + ql; cnt.[b] <- cnt.[b] + 1
                            q.[c] <- q.[c] + ql; cnt.[c] <- cnt.[c] + 1
                            f <- f + 3
                        for i in 0 .. pos.Length - 1 do
                            if cnt.[i] > 0 then q.[i] <- q.[i] / float32 cnt.[i]
                        ArrayBuffer q :> IBuffer
                    | None -> ArrayBuffer [| 0.0f; 0.0f; 0.0f |] :> IBuffer)
            // 2 = variance (sequential) on the reference mesh — the Inspect central
            // 3D aggregate. The single-mesh signed-distance difference moved to the
            // focus tiles, so this is the only per-vertex main-view map now.
            let distEncoding =
                AVal.custom (fun t ->
                    if (myDist.GetValue t).IsNone then 0
                    elif model.WorkflowStep.GetValue t = Inspect
                         && (model.Registration.GetValue t).ReferenceMesh = Some name then 2
                    else 0)
            // Saturated end of the diverging map: robust (95th pct |d|).
            let distScale =
                myDist |> AVal.map (function
                    | Some arr ->
                        let valid = arr |> Array.choose (fun x -> if abs x < 1e20f then Some (abs x) else None)
                        if valid.Length = 0 then 1.0f
                        else
                            Array.sortInPlace valid
                            max 1e-3f valid.[int (0.95 * float (valid.Length - 1))]
                    | None -> 1.0f)
            let surface =
                sg {
                    Sg.Active renderEnabled
                    Sg.Trafo fullTrafo
                    Sg.Shader {
                        DefaultSurfaces.trafo
                        DefaultSurfaces.diffuseTexture
                        MeshShader.shade
                    }
                    Sg.Uniform("DiffuseColorTexture", loaded.tex)
                    Sg.Uniform("MeshActive",      isActive)
                    // GhostSilhouette off → 0 → ghost path discards. Reference
                    // peek + wheel isolation dim the others at fixed alphas
                    // (explicit gestures, independent of the toggle).
                    Sg.Uniform("GhostOpacity",
                        AVal.custom (fun t ->
                            match peekTarget.GetValue t with
                            | Some target when target <> name -> 0.12f
                            | _ ->
                                match wheelIsolation.GetValue t with
                                | Some iso when iso <> name -> 0.15f
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
                    Sg.Uniform("BlobCount",       blobCount)
                    Sg.Uniform("Blobs",           blobs)
                    Sg.Uniform("AnchorGhost",     anchorGhost)
                    Sg.Uniform("ClipPlaneCount",       clipCount)
                    Sg.Uniform("ClipPlane0",           clipPlane0)
                    Sg.Uniform("ClipPlane1",           clipPlane1)
                    Sg.Uniform("DistanceEncoding",     distEncoding)
                    Sg.Uniform("DistScale",            distScale)
                    Sg.Uniform("HeatmapMode",          model.HeatmapMode |> AVal.map (function HeatOff -> 0 | HeatIncidence -> 1 | HeatRange -> 2 | HeatShape -> 3))
                    Sg.Uniform("SensorOrigin",         sensorOrigin)
                    Sg.Uniform("RangeMax",             rangeMax)
                    Sg.VertexAttributes(
                        HashMap.ofList [
                            string DefaultSemantic.Positions,               BufferView(loaded.pos, typeof<V3f>)
                            string DefaultSemantic.DiffuseColorCoordinates, BufferView(loaded.tc,  typeof<V2f>)
                            string DefaultSemantic.Normals,                 BufferView(loaded.nrm, typeof<V3f>)
                            "SurfaceDist",                                  BufferView(distBuf, typeof<float32>)
                            "ShapeQ",                                       BufferView(shapeBuf, typeof<float32>)
                        ]
                    )
                    Sg.Index(BufferView(loaded.idx, typeof<int>))
                    Sg.Render loaded.fvc
                }
            surface
        ) |> AList.toASet

    // Outline G-buffer: every visible mesh rendered solid with the
    // OutlineGBuffer shader (world normal + depth → target0, palette colour +
    // mask → target1). Consumed by OutlineView's offscreen pass.
    let buildOutlineNode (model : AdaptiveModel) (view : aval<Trafo3d>) (proj : aval<Trafo3d>) : ISceneNode =
        let meshIndices =
            model.MeshNames |> AList.toAVal |> AVal.map (fun names ->
                names |> Seq.mapi (fun i n -> n, i) |> Map.ofSeq)
        let palette = Primitives.meshPaletteV4d
        // World-Z isoline spacing (render-space Z step), shared across meshes so
        // the band parity lines up. Sized for IsolineBands bands over the scene
        // elevation range (SceneBounds is world-metric, render = metric × datasetScale).
        // The G-buffer encodes band parity from this; the edge pass draws the lines.
        let contourSpacing =
            let datasetScaleA =
                (model.ActiveDataset, model.DatasetScales) ||> AVal.map2 DatasetScale.active
            AVal.map3 (fun (b : Box3d) s bands ->
                let zext = if b.IsInvalid then 0.0 else b.Size.Z
                float32 (max 1e-6 (zext / max 1.0 bands) * s))
                model.SceneBounds datasetScaleA model.IsolineBands
        let nodes =
            model.MeshNames |> AList.map (fun name ->
                let loaded = loadMeshAsync (fun () -> ()) name
                let isActive =
                    model.MeshVisible |> AVal.map (fun m -> Map.tryFind name m |> Option.defaultValue true)
                let scale = scaleFor model name
                let meshT = displayedMeshT model name
                let renderEnabled =
                    (loaded.fvc, isActive) ||> AVal.map2 (fun c a -> c > 3 && a)
                let meshColor =
                    meshIndices |> AVal.map (fun m ->
                        let i = Map.tryFind name m |> Option.defaultValue 0
                        V4f palette.[i % palette.Length])
                sg {
                    Sg.Active renderEnabled
                    Sg.Trafo (meshTrafo model.CommonCentroid loaded scale meshT)
                    Sg.Shader {
                        DefaultSurfaces.trafo
                        OutlineGBuffer.shade
                    }
                    Sg.Uniform("MeshColor", meshColor)
                    Sg.Uniform("ContourSpacing", contourSpacing)
                    Sg.VertexAttributes(
                        HashMap.ofList [
                            string DefaultSemantic.Positions, BufferView(loaded.pos, typeof<V3f>)
                            string DefaultSemantic.Normals,   BufferView(loaded.nrm, typeof<V3f>)
                        ])
                    Sg.Index(BufferView(loaded.idx, typeof<int>))
                    Sg.Render loaded.fvc
                }
            ) |> AList.toASet
        sg {
            Sg.View view
            Sg.Proj proj
            Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
            nodes
        }
