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

    // Token-based sibling of displayedMeshT in metric world — the pose the mesh
    // currently SHOWS (peek included), for AVal.custom computes that place
    // mesh-local data (anchors, slice caches). Reads hit the caller's token
    // directly; never build transient avals for this (see CLAUDE.md).
    let displayedWorldPeekAt (model : AdaptiveModel) (t : FSharp.Data.Adaptive.AdaptiveToken) (mesh : string) =
        let scale = DatasetScale.forMesh (model.DatasetScales.GetValue t) mesh
        let cc = model.CommonCentroid.GetValue t
        let view =
            match model.RegView.GetValue t, model.RegPeekHeld.GetValue t with
            | RegBefore, true -> RegAfter
            | RegAfter, true -> RegBefore
            | v, false -> v
        let disp =
            match view, Map.tryFind mesh (model.SolvedTransforms.GetValue t) with
            | RegAfter, Some s -> s
            | _ -> Map.tryFind mesh (model.LoadTransforms.GetValue t) |> Option.defaultValue Trafo3d.Identity
        RigidTransform.renderToWorld scale cc disp

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

    // The ONE Inspect error range (§C): signed (lo, hi) in metres from the pin ROI
    // samples, capped ±0.5 m (ScanPin.inspectRange). Shared by the 3D difference +
    // variance painters, the focus tiles/single, the sample dots' per-pin gradient
    // envelope, and the on-screen legend — so every false-colour map is comparable.
    let inspectRange (model : AdaptiveModel) : aval<float * float> =
        let pinsV = model.ScanPins.Pins |> AMap.toAVal
        let refV = model.Registration |> AVal.map (fun r -> r.ReferenceMesh)
        (pinsV, refV) ||> AVal.map2 (fun pins rf ->
            ScanPin.inspectRange rf (pins |> HashMap.toSeq |> Seq.map snd))

    // Shared saturation end (world metres) of the Range heatmap: the farthest
    // own-bbox corner from each mesh's own sensor, maxed over ALL meshes — ONE
    // scale so range colours are comparable across meshes (§B3). Feeds the 3D
    // RangeMax uniform, the focus mode-5 normalization and the Range legend.
    // 0 until bounds load (consumers fall back per mesh).
    let rangeMaxWorld (model : AdaptiveModel) : aval<float> =
        AVal.custom (fun t ->
            let bounds = model.MeshBounds.GetValue t
            let panos = model.PanoCenters.GetValue t
            let cents = model.DatasetCentroids.GetValue t
            let names = model.MeshNames.Content.GetValue t |> IndexList.toList
            let mutable mx = 0.0
            for name in names do
                match Map.tryFind name bounds with
                | Some (b : Box3d) when not b.IsInvalid ->
                    let sensor =
                        match Map.tryFind name panos with
                        | Some w -> w
                        | None -> Map.tryFind name cents |> Option.defaultValue b.Center
                    let dx = max (abs (b.Min.X - sensor.X)) (abs (b.Max.X - sensor.X))
                    let dy = max (abs (b.Min.Y - sensor.Y)) (abs (b.Max.Y - sensor.Y))
                    let dz = max (abs (b.Min.Z - sensor.Z)) (abs (b.Max.Z - sensor.Z))
                    let r = sqrt (dx * dx + dy * dy + dz * dz)
                    if r > mx then mx <- r
                | _ -> ()
            mx)

    // Saturation end (m) of the displacement map: the largest |load→solved| over
    // every solved mesh. The displacement of a rigid pose change is an affine
    // function of position, so its maximum over a mesh is attained at a bbox
    // corner — 8 corners per mesh, exact and cheap. One global value keeps the
    // displacement colours comparable across meshes (not capped at 0.5 m — a solve
    // may legitimately move a mesh further).
    let displacementRange (model : AdaptiveModel) : aval<float> =
        AVal.custom (fun t ->
            let solved = model.SolvedTransforms.GetValue t
            if Map.isEmpty solved then 1.0
            else
                let bounds = model.MeshBounds.GetValue t
                let loads = model.LoadTransforms.GetValue t
                let cc = model.CommonCentroid.GetValue t
                let scales = model.DatasetScales.GetValue t
                let mutable mx = 1e-6
                for KeyValue(name, sT) in solved do
                    match Map.tryFind name bounds with
                    | Some (b : Box3d) when not b.IsInvalid ->
                        let s = DatasetScale.forMesh scales name
                        let lT = Map.tryFind name loads |> Option.defaultValue Trafo3d.Identity
                        for corner in b.ComputeCorners() do
                            let rc = ScanPin.renderCentre cc s corner
                            let d = (sT.Forward.TransformPos rc - lT.Forward.TransformPos rc).Length / s
                            if d > mx then mx <- d
                    | _ -> ()
                mx)

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
        let inspectRangeA = inspectRange model
        let dispRangeA = displacementRange model
        let rangeWorldA = rangeMaxWorld model
        let clipCount  = clip |> AVal.map (fun (c, _, _) -> c)
        let clipPlane0 = clip |> AVal.map (fun (_, p, _) -> p)
        let clipPlane1 = clip |> AVal.map (fun (_, _, p) -> p)
        // Force pin isolation on while placing an anchor: the terrain drops to
        // ghost and only the existing pins + the live hover blob read solid — a
        // "flashlight" revealing where the new pin lands (auto-restored, no model
        // mutation).
        // Pin isolation = the persistent per-mode default (AnchorGhostMode),
        // forced on while placing.
        let anchorGhost =
            (model.AnchorGhostMode, model.ScanPins.Placement)
            ||> AVal.map2 (fun on pl ->
                match pl with
                | AnchorPlacement -> 1
                | _ -> if on then 1 else 0)
        model.MeshNames |> AList.map (fun name ->
            let loaded = loadMeshAsync (fun () -> loadFinished name) name
            // Isolation: wheelIsolation (Alt-wheel / hover peek / armed editor) wins,
            // then the solo overlay via MeshVisibility.shown (only the isolated mesh
            // solid, the rest at the ghost floor), else the per-mesh toggles + the
            // Inspect no-solo ensemble ghosting.
            let isActive =
                AVal.custom (fun t ->
                        match wheelIsolation.GetValue t with
                        | Some iso -> iso = name
                        | None ->
                            match model.MeshSolo.GetValue t with
                            | Some _ as solo ->
                                MeshVisibility.shown solo (model.MeshVisible.GetValue t) name
                            | None ->
                                let vis = Map.tryFind name (model.MeshVisible.GetValue t) |> Option.defaultValue true
                                // Selection emphasis (§A2) outside Inspect: the selected
                                // mesh reads solid, the rest drop to the ghost floor
                                // (Inspect routes mesh selection through the solo overlay).
                                match Selection.mesh (model.Selection.Active.GetValue t) with
                                | Some m when model.WorkflowStep.GetValue t <> Inspect ->
                                    vis && m = name
                                | _ ->
                                    // Inspect central 3D (§C), no solo: the reference carries
                                    // the variance aggregate solid, moving meshes drop to the
                                    // ghost floor. A mesh carrying its own intrinsic heatmap
                                    // stays solid so that error layer reads in the aggregate.
                                    let hasHeatmap =
                                        (Map.tryFind name (model.MeshHeatmap.GetValue t) |> Option.defaultValue HeatOff) <> HeatOff
                                    let inspectGhost =
                                        model.WorkflowStep.GetValue t = Inspect
                                        && not hasHeatmap
                                        && (match (model.Registration.GetValue t).ReferenceMesh with
                                            | Some r -> r <> name
                                            | None -> false)
                                    vis && not inspectGhost)
            let scale = scaleFor model name
            let meshT = displayedMeshT model name
            // Outline-only representation (§B4): the mesh BODY stands down entirely —
            // a zero ghost floor discards every non-emphasized fragment — while the
            // offscreen outline pre-pass (OutlineView; it renders ALL loaded meshes
            // independently of the main pass) keeps compositing this mesh's
            // silhouette + isolines. The lowest-fidelity representation; a mesh in
            // this state is exactly "outline, no fill". Policy (§B5): Inspect drops
            // every ghost fill — whatever is not emphasized there is outline-only,
            // so the active false-colour map is the only filled surface.
            let outlineOnly = model.WorkflowStep |> AVal.map ((=) Inspect)
            // Sensor origin = the mesh's panorama/camera centre (PanoCenters,
            // absolute world → mesh frame → render); no entry ⇒ the mesh origin.
            // Drives the incidence + range heatmaps from the real sensor, not the
            // interactive camera. RangeMax = the GLOBAL all-mesh saturation end
            // (§B3, rangeMaxWorld) so range colours compare across meshes; local
            // fallback while bounds are pending.
            let fullTrafo = meshTrafo model.CommonCentroid loaded scale meshT
            let sensorOrigin =
                (fullTrafo, model.PanoCenters, loaded.centroid) |||> AVal.map3 (fun t panos c ->
                    let local = match Map.tryFind name panos with Some w -> w - c | None -> V3d.Zero
                    V3f (t.Forward.TransformPos local))
            let rangeMax =
                (rangeWorldA, scale, loaded.localMaxR) |||> AVal.map3 (fun g s lr ->
                    float32 (max 1e-6 ((if g > 0.0 then g else lr) * s)))
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
            // The scalar caches are pose-baked pairs (main = committed, Other = the
            // opposite Before/After pose) — the reg peek selects the Other cache so
            // the paint flips with the geometry (peek is only reachable once solved;
            // Other not ready yet ⇒ unpainted while held, never stale values).
            let inspectField : aval<int * float32[] option> =
                AVal.custom (fun t ->
                    if model.WorkflowStep.GetValue t <> Inspect then (0, None)
                    // Brushing = sole focus (§A4): while samples are brushed every
                    // false-colour error map stands down — only the dots carry value.
                    elif not (Set.isEmpty (model.BrushedSamples.GetValue t)) then (0, None)
                    else
                        let rf = (model.Registration.GetValue t).ReferenceMesh
                        if Some name = rf then
                            // While a moving mesh is isolated the reference is plain
                            // context (the isolated mesh paints its field against it);
                            // the variance aggregate paints only in the no-solo ensemble.
                            if (model.MeshSolo.GetValue t).IsSome then (0, None)
                            else
                                let surf =
                                    if model.RegPeekHeld.GetValue t then model.SurfaceDistanceOther
                                    else model.SurfaceDistance
                                match Map.tryFind name (surf.GetValue t) with
                                | Some arr -> (2, Some arr)
                                | None -> (0, None)
                        elif model.MeshSolo.GetValue t = Some name then
                            match model.InspectChannel.GetValue t with
                            | ChDifference ->
                                let fd =
                                    if model.RegPeekHeld.GetValue t then model.FocusDistOther
                                    else model.FocusDist
                                match Map.tryFind name (fd.GetValue t) with
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
            // Map ends from the unified pin-derived range (§C): enc 1 saturates at
            // (lo, hi); enc 2 (variance σ, same units) at max(|lo|, hi); enc 3 at the
            // global displacement range.
            let distScale =
                (distEncoding, inspectRangeA, dispRangeA) |||> AVal.map3 (fun enc (lo, hi) disp ->
                    match enc with
                    | 1 -> float32 hi
                    | 2 -> float32 (max (abs lo) hi)
                    | 3 -> float32 disp
                    | _ -> 1.0f)
            let distLoNeg = inspectRangeA |> AVal.map (fun (lo, _) -> float32 (abs lo))
            let diffIsoStep =
                (distEncoding, inspectRangeA) ||> AVal.map2 (fun enc (lo, hi) ->
                    if enc = 1 then float32 (Primitives.Diff.isoStep lo hi) else 0.0f)
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
                            if outlineOnly.GetValue t then 0.0f
                            else
                                let floorOn = model.GhostSilhouette.GetValue t
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
                    Sg.Uniform("DistLoNeg",            distLoNeg)
                    Sg.Uniform("DiffIsoStep",          diffIsoStep)
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
                    Sg.Uniform("ShapeThreshold",       model.ShapeThreshold |> AVal.map float32)
                    // Show-overlays modifier (§T8): white-out the mesh while held; the
                    // pin geometry (separate) keeps its colour.
                    Sg.Uniform("Whiteout", model.ShowOverlaysHeld |> AVal.map (fun on -> if on then 1.0f else 0.0f))
                    // Inspect de-clutter (§B5): the false-colour map is the base — no
                    // photo texture competes in Inspect.
                    Sg.Uniform("InspectPlain", model.WorkflowStep |> AVal.map (fun s -> if s = Inspect then 1.0f else 0.0f))
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

    // Per-mesh outline state for the image-space outline pass. Flag semantics
    // (the edge composite's OutlineMask): 1 = silhouette + isolines · 0.5 =
    // silhouette only (Inspect pair view: everything except the isolated mesh and
    // the reference keeps just its contour).
    let private outlineFlagAt (model : AdaptiveModel) (t : FSharp.Data.Adaptive.AdaptiveToken) (name : string) =
        let silhouetteOnly =
            model.WorkflowStep.GetValue t = Inspect
            && (match model.MeshSolo.GetValue t with
                | Some s -> name <> s && (model.Registration.GetValue t).ReferenceMesh <> Some name
                | None -> false)
        if silhouetteOnly then 0.5f else 1.0f

    // The composite's per-mesh line gate, indexed like MeshId ((index+1)/255).
    let outlineMask (model : AdaptiveModel) : aval<V4f[]> =
        AVal.custom (fun t ->
            let names = model.MeshNames.Content.GetValue t |> IndexList.toList
            let arr = Array.create 32 (V4f(1.0f, 0.0f, 0.0f, 0.0f))
            names |> List.iteri (fun i name ->
                if i < 32 then arr.[i] <- V4f(outlineFlagAt model t name, 0.0f, 0.0f, 0.0f))
            arr)

    // Outline G-buffer: every mesh rendered solid with OutlineGBuffer.shade,
    // consumed by OutlineView's offscreen pass.
    let buildOutlineNode (model : AdaptiveModel) (view : aval<Trafo3d>) (proj : aval<Trafo3d>) : ISceneNode =
        let meshIndices =
            model.MeshNames |> AList.toAVal |> AVal.map (fun names ->
                names |> Seq.mapi (fun i n -> n, i) |> Map.ofSeq)
        let palette = Primitives.meshPaletteV4d
        // World-Z isoline spacing (render-space Z step), shared across meshes so
        // the band parity lines up. Camera-adaptive (§B2): the spacing follows the
        // orbit distance (~24 contours across the view) SNAPPED to a nice 1/2/5
        // world-metre step — zooming out thins the lines in discrete ticks, orbiting
        // (constant radius) never changes them. The gear's IsolineBands sets the
        // densest allowed spacing (reached when zoomed close); the far end is capped
        // at ≥4 contours over the scene's Z range. The G-buffer encodes band parity
        // from this; the edge pass draws the lines.
        let contourSpacing =
            let datasetScaleA =
                (model.ActiveDataset, model.DatasetScales) ||> AVal.map2 DatasetScale.active
            AVal.custom (fun t ->
                let b = model.SceneBounds.GetValue t
                let s = datasetScaleA.GetValue t
                let bands = model.IsolineBands.GetValue t
                let zext = if b.IsInvalid then 0.0 else b.Size.Z
                let minSpacing = max 1e-6 (zext / max 1.0 bands)
                let rWorld = model.Camera.radius.GetValue t / max 1e-9 s
                let raw = max minSpacing (rWorld / 24.0)
                let nice =
                    let mag = 10.0 ** floor (log10 raw)
                    let n = raw / mag
                    (if n < 1.5 then 1.0 elif n < 3.5 then 2.0 elif n < 7.5 then 5.0 else 10.0) * mag
                let spacing =
                    if zext <= 0.0 then raw
                    else clamp minSpacing (max minSpacing (zext / 4.0)) nice
                float32 (spacing * s))
        let nodes =
            model.MeshNames |> AList.map (fun name ->
                let loaded = loadMeshAsync (fun () -> ()) name
                let scale = scaleFor model name
                let meshT = displayedMeshT model name
                // Every loaded mesh renders into the G-buffer (visibility gates the
                // main pass only).
                let active = loaded.fvc |> AVal.map (fun c -> c > 3)
                let meshColor =
                    meshIndices |> AVal.map (fun m ->
                        let i = Map.tryFind name m |> Option.defaultValue 0
                        V4f palette.[i % palette.Length])
                let meshId =
                    meshIndices |> AVal.map (fun m ->
                        float32 ((Map.tryFind name m |> Option.defaultValue 0) + 1) / 255.0f)
                // Silhouette-only context meshes get a small depth push so the
                // co-located inspected pair wins the G-buffer depth contest.
                let depthBias =
                    AVal.custom (fun t ->
                        if outlineFlagAt model t name = 0.5f then 5.0e-5f else 0.0f)
                sg {
                    Sg.Active active
                    Sg.Trafo (meshTrafo model.CommonCentroid loaded scale meshT)
                    Sg.Shader {
                        DefaultSurfaces.trafo
                        OutlineGBuffer.shade
                    }
                    Sg.Uniform("MeshColor", meshColor)
                    Sg.Uniform("MeshId", meshId)
                    Sg.Uniform("OutlineDepthBias", depthBias)
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

    // Per-mesh screen-space coverage for the footprint-contour pass (§outline
    // per-mesh): every mesh accumulates additively into its own channel at its
    // displayed pose — no depth buffer, no occlusion — so the coverage composite
    // can outline each mesh separately even where meshes overlap or are hidden.
    // Channels cap at 8 (2×Rgba8 MRT); meshes beyond keep only the combined
    // union outline.
    let buildCoverageNode (model : AdaptiveModel) (view : aval<Trafo3d>) (proj : aval<Trafo3d>) : ISceneNode =
        let meshIndices =
            model.MeshNames |> AList.toAVal |> AVal.map (fun names ->
                names |> Seq.mapi (fun i n -> n, i) |> Map.ofSeq)
        let nodes =
            model.MeshNames |> AList.map (fun name ->
                let loaded = loadMeshAsync (fun () -> ()) name
                let scale = scaleFor model name
                let meshT = displayedMeshT model name
                let channel = meshIndices |> AVal.map (fun m -> Map.tryFind name m |> Option.defaultValue 0)
                let active =
                    (loaded.fvc, channel) ||> AVal.map2 (fun c i -> c > 3 && i < 8)
                sg {
                    Sg.Active active
                    Sg.Trafo (meshTrafo model.CommonCentroid loaded scale meshT)
                    Sg.Shader {
                        DefaultSurfaces.trafo
                        OutlineCoverage.shade
                    }
                    Sg.Uniform("CoverageChannel", channel)
                    Sg.VertexAttributes(
                        HashMap.ofList [
                            string DefaultSemantic.Positions, BufferView(loaded.pos, typeof<V3f>)
                        ])
                    Sg.Index(BufferView(loaded.idx, typeof<int>))
                    Sg.Render loaded.fvc
                }) |> AList.toASet
        sg {
            Sg.View view
            Sg.Proj proj
            Sg.DepthTest (AVal.constant DepthTest.None)
            Sg.BlendMode (AVal.constant BlendMode.Add)
            nodes
        }

    // Palette colours for the footprint composite, indexed like the coverage channels.
    let coverageColors : V4f[] =
        Array.init 8 (fun i -> V4f Primitives.meshPaletteV4d.[i % Primitives.meshPaletteV4d.Length])

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
                    Sg.Uniform("MeshId", AVal.constant (1.0f / 255.0f))
                    Sg.Uniform("OutlineDepthBias", AVal.constant 0.0f)
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
