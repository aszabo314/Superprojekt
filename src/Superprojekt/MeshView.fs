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

    // Effective registration view: the committed RegView, flipped while the before/after
    // PEEK is held (§spring-loaded hold — purely visual).
    let effectiveRegView (model : AdaptiveModel) =
        (model.RegView, model.RegPeekHeld) ||> AVal.map2 (fun v held ->
            match held, v with
            | true, RegBefore -> RegAfter
            | true, RegAfter -> RegBefore
            | false, v -> v)

    let displayedMeshT (model : AdaptiveModel) (name : string) =
        (effectiveRegView model, model.SolvedTransforms, model.LoadTransforms)
        |||> AVal.map3 (fun view solved load ->
            match view, Map.tryFind name solved with
            | RegAfter, Some t -> t
            | _ -> Map.tryFind name load |> Option.defaultValue Trafo3d.Identity)

    // Per-vertex triangle shape quality (incident-face mean of 4√3·A/Σl², clamped
    // 0..1; 1 = equilateral, →0 = thin/degenerate). Shared by the 3D shape heatmap
    // buffer and the 2D focus tile shape overlay.
    let shapeQuality (pos : V3f[]) (idx : int[]) : float32[] =
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
        q

    // Saturated end of a diverging/sequential scalar map: 95th percentile of the
    // finite |values| (1e20 = sentinel for "no value"), floored at 1e-3.
    let robustHi (arr : float32[]) =
        let valid = arr |> Array.choose (fun x -> if abs x < 1e20f then Some (abs x) else None)
        if valid.Length = 0 then 1.0f
        else
            Array.sortInPlace valid
            max 1e-3f valid.[int (0.95 * float (valid.Length - 1))]

    // Pin blobs as a 32-slot uniform array, metric → render space (centre xyz,
    // inner radius w), for the mesh shader's pin-isolation filter. The live
    // placement hover is appended as a transient "flashlight" blob.
    let private pinBlobUniforms (placementPreview : aval<V3d option>) (model : AdaptiveModel) =
        let datasetScale =
            (model.ActiveDataset, model.DatasetScales) ||> AVal.map2 DatasetScale.active
        let pinBlobs =
            let pinsF =
                model.ScanPins.Pins
                |> AMap.map (fun _ p -> p.Centre, p.InnerRadius)
                |> AMap.toAVal
                |> AVal.map (fun pinsMap -> HashMap.toArray pinsMap |> Array.map snd)
            (pinsF, model.CommonCentroid, datasetScale)
            |||> AVal.map3 (fun pins cc scale ->
                pins |> Array.map (fun (centre, innerR) ->
                    let cr = ScanPin.renderCentre cc scale centre
                    V4f(float32 cr.X, float32 cr.Y, float32 cr.Z, float32 (ScanPin.renderLength scale innerR))))
        // Placement hover → flashlight blob (already render space), sized to
        // QuickPinRadius — previews exactly where the new pin will land.
        let previewBlob =
            (placementPreview, model.QuickPinRadius, datasetScale)
            |||> AVal.map3 (fun prev qr scale ->
                prev |> Option.map (fun p ->
                    V4f(float32 p.X, float32 p.Y, float32 p.Z, float32 (ScanPin.renderLength scale qr))))
        let blobsArr =
            (pinBlobs, previewBlob) ||> AVal.map2 (fun pins prev ->
                let all = match prev with Some b -> Array.append pins [| b |] | None -> pins
                let n = min all.Length MeshShader.MaxBlobs
                let centres = Array.zeroCreate<V4f> MeshShader.MaxBlobs
                for i in 0 .. n - 1 do centres.[i] <- all.[i]
                n, centres)
        blobsArr |> AVal.map fst,
        blobsArr |> AVal.map snd

    let buildScene (loadFinished : string -> unit) (clip : aval<int * V4f * V4f>) (placementPreview : aval<V3d option>) (wheelIsolation : aval<string option>) (model : AdaptiveModel) : aset<ISceneNode> =
        let renderingModeInt =
            model.RenderingMode |> AVal.map (function
                | Textured     -> 0
                | Shaded       -> 1
                | SlopeColor   -> 2)
        let meshIndices =
            model.MeshNames |> AList.toAVal |> AVal.map (fun names ->
                names |> Seq.mapi (fun i n -> n, i) |> Map.ofSeq)
        let palette = Primitives.meshPaletteV4d

        let blobCount, blobs = pinBlobUniforms placementPreview model
        let clipCount  = clip |> AVal.map (fun (c, _, _) -> c)
        let clipPlane0 = clip |> AVal.map (fun (_, p, _) -> p)
        let clipPlane1 = clip |> AVal.map (fun (_, _, p) -> p)
        // Force pin isolation on while placing an anchor: the terrain drops to
        // ghost and only the existing pins + the live hover blob read solid — a
        // "flashlight" revealing where the new pin lands (auto-restored, no model
        // mutation).
        // Pin isolation = the persistent per-mode default (AnchorGhostMode) OR the
        // spring-loaded hold modifier (IsolatePeekHeld), forced on while placing.
        let anchorGhost =
            (model.AnchorGhostMode, model.ScanPins.Placement, model.IsolatePeekHeld)
            |||> AVal.map3 (fun on pl held ->
                match pl with
                | AnchorPlacement -> 1
                | _ -> if on || held then 1 else 0)
        model.MeshNames |> AList.map (fun name ->
            let loaded = loadMeshAsync (fun () -> loadFinished name) name
            // Isolation: reference peek wins, then wheelIsolation (Alt-wheel /
            // hover peek), else plain visibility.
            let isActive =
                AVal.custom (fun t ->
                        match wheelIsolation.GetValue t with
                        | Some iso -> iso = name
                        | None ->
                            let vis = Map.tryFind name (model.MeshVisible.GetValue t) |> Option.defaultValue true
                            // Inspect central 3D (§C): no solo → the reference carries the
                            // variance aggregate solid and moving meshes drop to the ghost
                            // floor; solo m → m stays solid (it paints its own difference /
                            // displacement field). An intrinsic channel keeps all solid.
                            let isSolo = match model.MeshSolo.GetValue t with Solo(s, _) -> s = name | _ -> false
                            // A mesh carrying its own intrinsic heatmap stays solid so
                            // that error layer reads even in the Inspect no-solo aggregate.
                            let hasHeatmap =
                                (Map.tryFind name (model.MeshHeatmap.GetValue t) |> Option.defaultValue HeatOff) <> HeatOff
                            let inspectGhost =
                                model.WorkflowStep.GetValue t = Inspect
                                && not hasHeatmap
                                && not isSolo
                                && (match (model.Registration.GetValue t).ReferenceMesh with
                                    | Some rf -> rf <> name
                                    | None -> false)
                            vis && not inspectGhost)
            let scale = scaleFor model name
            let meshT = displayedMeshT model name
            // Sensor origin = the mesh's panorama/camera centre (PanoCenters,
            // absolute world → mesh frame → render); no entry ⇒ the mesh origin.
            // Drives the incidence + range heatmaps from the real sensor, not the
            // interactive camera. RangeMax normalises range by the farthest mesh-bbox
            // corner from that same sensor (so an off-surface origin doesn't skew it).
            let fullTrafo = meshTrafo model.CommonCentroid loaded scale meshT
            let sensorOrigin =
                (fullTrafo, model.PanoCenters, loaded.centroid) |||> AVal.map3 (fun t panos c ->
                    let local = match Map.tryFind name panos with Some w -> w - c | None -> V3d.Zero
                    V3f (t.Forward.TransformPos local))
            let rangeMax =
                AVal.custom (fun t ->
                    let s = scale.GetValue t
                    let panoW =
                        match Map.tryFind name (model.PanoCenters.GetValue t) with
                        | Some w -> w
                        | None -> loaded.centroid.GetValue t
                    let r =
                        match Map.tryFind name (model.MeshBounds.GetValue t) with
                        | Some (b : Box3d) when not b.IsInvalid ->
                            let dx = max (abs (b.Min.X - panoW.X)) (abs (b.Max.X - panoW.X))
                            let dy = max (abs (b.Min.Y - panoW.Y)) (abs (b.Max.Y - panoW.Y))
                            let dz = max (abs (b.Min.Z - panoW.Z)) (abs (b.Max.Z - panoW.Z))
                            sqrt (dx*dx + dy*dy + dz*dz)
                        | _ -> loaded.localMaxR.GetValue t
                    float32 (max 1e-6 (r * s)))
            // Inactive meshes still render (as ghost); gate only on load state.
            let renderEnabled =
                loaded.fvc |> AVal.map (fun c -> c > 3)
            let meshColor =
                meshIndices |> AVal.map (fun m ->
                    let i = Map.tryFind name m |> Option.defaultValue 0
                    V4f palette.[i % palette.Length])
            // What this mesh paints in the MAIN 3D view (Inspect only) — the encoding
            // + the per-vertex scalar array (§C):
            //   reference, ensemble (no moving-mesh solo) → variance      (enc 2, SurfaceDistance)
            //   soloed moving mesh + Difference channel    → signed dist   (enc 1, FocusDist)
            //   soloed moving mesh + Displacement channel  → |load→solved| (enc 3, client-computed)
            // Reading WorkflowStep / Registration / MeshSolo first keeps non-Inspect and
            // non-soloed meshes cheap (they fall straight through to (0, None)).
            let inspectField : aval<int * float32[] option> =
                AVal.custom (fun t ->
                    if model.WorkflowStep.GetValue t <> Inspect then (0, None)
                    else
                        let rf = (model.Registration.GetValue t).ReferenceMesh
                        if Some name = rf then
                            match Map.tryFind name (model.SurfaceDistance.GetValue t) with
                            | Some arr -> (2, Some arr)
                            | None -> (0, None)
                        elif (match model.MeshSolo.GetValue t with Solo(s, _) -> s = name | _ -> false) then
                            match model.InspectChannel.GetValue t with
                            | ChDifference ->
                                match Map.tryFind name (model.FocusDist.GetValue t) with
                                | Some arr -> (1, Some arr)
                                | None -> (0, None)
                            | ChDisplacement ->
                                loaded.pos.GetValue t |> ignore
                                match loaded.mesh.Value, Map.tryFind name (model.SolvedTransforms.GetValue t) with
                                | Some md, Some _ ->
                                    let pos = md.positions
                                    let sc  = scale.GetValue t
                                    let c0  = loaded.centroid.GetValue t
                                    let cc  = model.CommonCentroid.GetValue t
                                    let baseT = Trafo3d.Translation(c0 - cc) * Trafo3d.Scale sc
                                    let fwd (m : Map<string, Trafo3d>) =
                                        (baseT * (Map.tryFind name m |> Option.defaultValue Trafo3d.Identity)).Forward
                                    let loadF   = fwd (model.LoadTransforms.GetValue t)
                                    let solvedF = fwd (model.SolvedTransforms.GetValue t)
                                    let mag = Array.init pos.Length (fun i ->
                                        let p = V3d pos.[i]
                                        float32 ((solvedF.TransformPos p - loadF.TransformPos p).Length / sc))
                                    (3, Some mag)
                                | _ -> (0, None)
                        else (0, None))
            let distArr = inspectField |> AVal.map snd
            let distBuf =
                (distArr, loaded.pos) ||> AVal.map2 (fun d _pos ->
                    match d with
                    | Some arr -> ArrayBuffer arr :> IBuffer
                    | None ->
                        match loaded.mesh.Value with
                        | Some md -> ArrayBuffer (Array.zeroCreate<float32> md.positions.Length) :> IBuffer
                        | None -> ArrayBuffer [| 0.0f; 0.0f; 0.0f |] :> IBuffer)
            // Shape heatmap: per-vertex triangle quality. Recomputed once on load.
            let shapeBuf =
                loaded.pos |> AVal.map (fun _ ->
                    match loaded.mesh.Value with
                    | Some md -> ArrayBuffer (shapeQuality md.positions md.indices) :> IBuffer
                    | None -> ArrayBuffer [| 0.0f; 0.0f; 0.0f |] :> IBuffer)
            let distEncoding = inspectField |> AVal.map fst
            // Saturated end of the map: robust (95th pct |value|). The difference map
            // (enc 1) is gear-scalable (DiffRangeScale) to match the focus tile.
            let distScale =
                (distArr, distEncoding, model.DiffRangeScale) |||> AVal.map3 (fun d enc sc ->
                    match d with
                    | Some arr -> let hi = robustHi arr in (if enc = 1 then hi * float32 sc else hi)
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
                    // The ghost FLOOR is the global GhostSilhouette toggle: on → faint
                    // context, off → hidden (α discarded). Solo/peek/isolation all send
                    // non-emphasized meshes to that same floor (§A.3) — when the floor is
                    // off, peek/isolation hide the others rather than dim them.
                    Sg.Uniform("GhostOpacity",
                        AVal.custom (fun t ->
                            let floorOn = model.GhostSilhouette.GetValue t
                            // Inspect arity by rendering (§T5): when a mesh is soloed
                            // (two-mesh difference / single-mesh intrinsic), every OTHER
                            // mesh — the reference included — renders as an EMPTY OUTLINE
                            // (fill discarded; the always-on outline pass keeps its
                            // silhouette for overlap context), regardless of the floor.
                            let inspectSoloOther =
                                model.WorkflowStep.GetValue t = Inspect
                                && (match model.MeshSolo.GetValue t with Solo(s, _) -> s <> name | NoSolo -> false)
                            if inspectSoloOther then 0.0f
                            else
                                match wheelIsolation.GetValue t with
                                | Some iso when iso <> name -> (if floorOn then 0.15f else 0.0f)
                                | _ ->
                                    if floorOn
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
                    // Per-mesh intrinsic error layer (set from the Overview mesh list),
                    // respected in every workflow mode. Suppressed while this mesh paints
                    // an Inspect comparison field (distEncoding ≠ 0) so the 2-mesh /
                    // before-after encodings win where they apply.
                    Sg.Uniform("HeatmapMode",
                        (model.MeshHeatmap, distEncoding) ||> AVal.map2 (fun mh enc ->
                            if enc <> 0 then 0
                            else match Map.tryFind name mh |> Option.defaultValue HeatOff with
                                 | HeatOff -> 0 | HeatIncidence -> 1 | HeatRange -> 2 | HeatShape -> 3))
                    Sg.Uniform("SensorOrigin",         sensorOrigin)
                    Sg.Uniform("RangeMax",             rangeMax)
                    // Show-overlays modifier (§T8): greyscale the mesh while held; the
                    // pin geometry (separate) keeps its colour.
                    Sg.Uniform("Greyscale", model.ShowOverlaysHeld |> AVal.map (fun on -> if on then 1.0f else 0.0f))
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

    // Outline G-buffer: every visible mesh rendered solid with OutlineGBuffer.shade,
    // consumed by OutlineView's offscreen pass.
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
                let scale = scaleFor model name
                let meshT = displayedMeshT model name
                // Render every loaded mesh into the outline G-buffer regardless of
                // visibility, so disabled / isolated-away meshes still get crisp
                // silhouette outlines + world-Z isolines on top of their ghost fill.
                let renderEnabled =
                    loaded.fvc |> AVal.map (fun c -> c > 3)
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

    // Silhouette-only outline G-buffer for whichever mesh is the current reference
    // (ContourSpacing 0 ⇒ no isolines, just the depth-break silhouette), in a fixed
    // colour. Feeds OutlineView.buildFromNode for the focus single's reference overlay.
    // The node set is stable (one per mesh, keyed by name); only the reference's node is
    // Active, gated by `show` (the focus single passes "a reference exists and it isn't
    // the shown mesh"), so a reference change just flips Active — no rebuild.
    let buildReferenceOutlineNode (model : AdaptiveModel) (view : aval<Trafo3d>) (proj : aval<Trafo3d>) (color : V4f) (show : aval<bool>) : ISceneNode =
        let refNameA = model.Registration |> AVal.map (fun r -> r.ReferenceMesh)
        let nodes =
            model.MeshNames |> AList.map (fun name ->
                let loaded = loadMeshAsync (fun () -> ()) name
                let scale = scaleFor model name
                let meshT = displayedMeshT model name
                let active =
                    (refNameA, show, loaded.fvc) |||> AVal.map3 (fun rf s c ->
                        s && c > 3 && rf = Some name)
                sg {
                    Sg.Active active
                    Sg.Trafo (meshTrafo model.CommonCentroid loaded scale meshT)
                    Sg.Shader {
                        DefaultSurfaces.trafo
                        OutlineGBuffer.shade
                    }
                    Sg.Uniform("MeshColor", AVal.constant color)
                    Sg.Uniform("ContourSpacing", AVal.constant 0.0f)
                    Sg.VertexAttributes(
                        HashMap.ofList [
                            string DefaultSemantic.Positions, BufferView(loaded.pos, typeof<V3f>)
                            string DefaultSemantic.Normals,   BufferView(loaded.nrm, typeof<V3f>)
                        ])
                    Sg.Index(BufferView(loaded.idx, typeof<int>))
                    Sg.Render loaded.fvc
                }) |> AList.toASet
        sg {
            Sg.View view
            Sg.Proj proj
            Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
            nodes
        }
