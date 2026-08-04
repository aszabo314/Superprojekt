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
        // Mean of the (sampled) unit vertex normals — |value| ≤ 1 measures how
        // strongly they cluster; feeds the project-wide up-normal. Zero until load.
        meanNormal : aval<V3d>
        mesh : MeshData option ref
    }

module MeshView =

    let apiBase = ApiConfig.apiBase

    let private meshes = System.Collections.Generic.Dictionary<string, LoadedMesh>()
    // Completion callbacks accumulate per name: several scene builders (tiles,
    // offscreen passes, up-normal) request a mesh before the main pass does,
    // but only the FIRST call starts the fetch — dropping a later caller's
    // callback loses the main pass's MeshesLoaded report (dead peeks).
    let private pendingFinished = System.Collections.Generic.Dictionary<string, ResizeArray<unit -> unit>>()

    let loadMeshAsync (finished : unit -> unit) (name : string) : LoadedMesh =
        match meshes.TryGetValue(name) with
        | true, m ->
            // Cache hit: the callback must still fire — Model.MeshesLoaded
            // resets per dataset and gates the peeks. Loaded ⇒ now; still in
            // flight ⇒ queue on the load's pending list.
            if (m.mesh : MeshData option ref).Value.IsSome then finished ()
            else pendingFinished.[name].Add finished
            m
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
                    meanNormal = cval V3d.Zero
                    mesh = ref None
                }
            meshes.[name] <- m
            let pending = ResizeArray [ finished ]
            pendingFinished.[name] <- pending
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
                        let meanN =
                            let ns = mesh.normals
                            if ns.Length = 0 then V3d.Zero
                            else
                                let stride = max 1 (ns.Length / 10000)
                                let mutable acc = V3d.Zero
                                let mutable cnt = 0
                                let mutable i = 0
                                while i < ns.Length do
                                    let v = V3d ns.[i]
                                    if v.Length > 1e-6 then
                                        acc <- acc + v.Normalized
                                        cnt <- cnt + 1
                                    i <- i + stride
                                if cnt = 0 then V3d.Zero else acc / float cnt
                        (m.meanNormal :?> cval<V3d>).Value <- meanN
                    )
                    let! img = JSImage.load mesh.atlasUrl
                    transact (fun () -> (m.tex :?> cval<ITexture>).Value <- JSTexture(img, true))

                    for f in pending do f ()
                    pending.Clear()
                with e ->
                    Log.error "failed to load mesh %s: %A" name e
            } |> ignore
            m

    // Public: every render trafo is built through this, so the
    // composition-order pitfall below has exactly one home.
    let meshTrafo
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

    // Does `name` blink to its as-loaded pose while the POSE peek holds? At
    // Matrix the subject is the WHOLE graph (there is no REF/MOV at graph
    // scope — every mesh drops to as-loaded at once); inside the pair
    // workspace only the pair's MOV does (REF/MOV from the tree, nearer-root =
    // REF). The vis peek doesn't use this: it swaps the effective isolate
    // instead (see the shown-rule contexts).
    let private peekPoseAt (held : bool) (model : AdaptiveModel) (t : FSharp.Data.Adaptive.AdaptiveToken) (name : string) =
        held &&
        (match model.Focus.GetValue t with
         | FocusMatrix -> true
         | FocusPair | FocusPin ->
            match (model.Sel.GetValue t).Pair with
            | Some (a, b) -> snd (MatrixNav.pairRefMov (model.RegGraph.GetValue t) a b) = name
            | None -> false)

    // The pose the mesh currently SHOWS: the composed graph pose — flipped to
    // the AS-LOADED baseline while the pose peek holds this mesh (visual layer
    // only; ModelTransforms stays committed for reducer-side queries).
    // Same geometry, different trafo uniform ⇒ the swap is instant and both
    // states are GPU-resident by construction.
    let displayedMeshT (model : AdaptiveModel) (name : string) =
        AVal.custom (fun t ->
            let load () =
                Map.tryFind name (model.LoadTransforms.GetValue t) |> Option.defaultValue Trafo3d.Identity
            if peekPoseAt (model.PeekPose.GetValue t) model t name then load ()
            else
                match Map.tryFind name (model.ComposedPoses.GetValue t) with
                | Some tr -> tr
                | None -> load ())

    // Token-based sibling of displayedMeshT in metric world, for AVal.custom
    // computes that place mesh-local data (pin anchors, markers). Reads hit the
    // caller's token directly; never build transient avals for this (see
    // CLAUDE.md). Pose-peek-aware like displayedMeshT, so surface-riding
    // annotations follow the blink.
    let displayedWorldAt (model : AdaptiveModel) (t : FSharp.Data.Adaptive.AdaptiveToken) (mesh : string) =
        let scale = DatasetScale.forMesh (model.DatasetScales.GetValue t) mesh
        let cc = model.CommonCentroid.GetValue t
        let load () =
            Map.tryFind mesh (model.LoadTransforms.GetValue t) |> Option.defaultValue Trafo3d.Identity
        let disp =
            if peekPoseAt (model.PeekPose.GetValue t) model t mesh then load ()
            else
                match Map.tryFind mesh (model.ComposedPoses.GetValue t) with
                | Some s -> s
                | None -> load ()
        RigidTransform.renderToWorld scale cc disp

    // Per-vertex triangle shape quality (incident-face mean of 4√3·A/Σl², clamped
    // 0..1; 1 = equilateral, →0 = thin/degenerate). Shared by the 3D shape heatmap
    // buffer and the survey-tile shape overlay.
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

    // THE in-cell scale, shared by the 3D map uniforms and the legend: the
    // pin-ROI samples when any exist, else the per-vertex distance
    // distribution (ErrorRange.ofDistances — a pinless cell would otherwise
    // normalize to the ±cap default and read as an all-white wash).
    let cellRange (ce : (ScanPinId * Query.PairPinError)[] option) (cd : float32[] option) =
        match ce with
        | Some cells when cells |> Array.exists (fun (_, r) -> r.Samples.Length > 0) ->
            ErrorRange.ofSamples (cells |> Seq.collect (fun (_, r) -> r.Samples))
        | _ ->
            match cd with
            | Some d -> ErrorRange.ofDistances d
            | None -> ErrorRange.ofSamples Seq.empty

    // At Matrix the pose peek is a STATE toggle, not a geometry blink: it flips
    // the error field in lockstep with the poses, so the colours always
    // describe what is on screen. Before = every edge measured with BOTH
    // endpoints at their as-loaded baselines, After = the composed residual.
    // The pair workspace is untouched — it pairs before/after per EDGE, and no
    // peek reaches its error at all.
    let graphSideAt (model : AdaptiveModel) (t : FSharp.Data.Adaptive.AdaptiveToken) =
        if model.PeekPose.GetValue t then EdgeBefore else EdgeAfter

    let private graphBlocks (model : AdaptiveModel) (t : FSharp.Data.Adaptive.AdaptiveToken) (side : EdgeSide) =
        (match side with
         | EdgeBefore -> model.GraphErrorBefore.GetValue t
         | EdgeAfter -> model.GraphError.GetValue t)
        |> Option.defaultValue [||]

    // THE canonical inspect sample stream of the current scope, in gid order:
    // the selected pair's pins (all MOV-vs-REF) inside the workspace, every
    // established edge's pins (each child vs ITS parent) at Matrix, in the
    // peeked state. Every gid-addressed consumer — the 3D dots, the hover
    // search, the readouts — walks this one stream, so the brush means the same
    // thing at both scopes.
    let inspectBlocksAt (model : AdaptiveModel) (t : FSharp.Data.Adaptive.AdaptiveToken) : InspectBlock[] =
        match model.Focus.GetValue t with
        | FocusMatrix -> graphBlocks model t (graphSideAt model t)
        | FocusPair | FocusPin ->
            match (model.Sel.GetValue t).Pair, model.CellError.GetValue t with
            | Some (a, b), Some cells ->
                let refM, movM = MatrixNav.pairRefMov (model.RegGraph.GetValue t) a b
                cells |> Array.map (fun (pid, r) -> { Mov = movM; Ref = refM; Pin = pid; Err = r })
            | _ -> [||]

    // THE inspect scale at the CURRENT scope, shared by that scope's map
    // uniforms, diagram and legend: the pair cell inside the workspace, the
    // whole graph at Matrix (the pooled edge samples, else the pooled
    // per-vertex distributions of every registered child).
    // At Matrix it is deliberately peek-BLIND — read from the BEFORE (larger)
    // state and held across the flip. Renormalizing per state would recolour
    // the residual to full range and the flip would show no improvement at all.
    let inspectRangeAt (model : AdaptiveModel) (t : FSharp.Data.Adaptive.AdaptiveToken) =
        match model.Focus.GetValue t with
        | FocusPair | FocusPin ->
            cellRange (model.CellError.GetValue t) (model.CellDist.GetValue t)
        | FocusMatrix ->
            let blocks =
                match graphBlocks model t EdgeBefore with
                | [||] -> graphBlocks model t EdgeAfter
                | b -> b
            if blocks |> Array.exists (fun b -> b.Err.Samples.Length > 0) then
                ErrorRange.ofSamples (blocks |> Seq.collect (fun b -> b.Err.Samples))
            else
                let dists =
                    match model.GraphDistBefore.GetValue t with
                    | d when Map.isEmpty d -> model.GraphDist.GetValue t
                    | d -> d
                if Map.isEmpty dists then ErrorRange.ofSamples Seq.empty
                else ErrorRange.ofDistances (dists |> Map.toArray |> Array.collect snd)

    // Brushing is a whole render MODE (colour isolation) wherever dots can
    // exist — the pair cell, or the whole graph at Matrix.
    let brushActiveAt (model : AdaptiveModel) (t : FSharp.Data.Adaptive.AdaptiveToken) =
        not (Set.isEmpty (model.BrushedSamples.GetValue t)) && (inspectBlocksAt model t).Length > 0

    // The graph error map's participants: while it paints (Matrix, map on,
    // ≥1 edge) the edge CHILDREN alone stay solid — exactly the meshes that
    // carry a parent-relative error, i.e. the painted set. Everything else
    // (the reference root, unregistered meshes) keeps only its outline: a
    // white surface there would read as "registered and fine".
    // None = no narrowing (every other state).
    let graphMapScopeAt (model : AdaptiveModel) (t : FSharp.Data.Adaptive.AdaptiveToken) : Set<string> option =
        match model.Focus.GetValue t with
        | FocusPair | FocusPin -> None
        | FocusMatrix ->
            let g = model.RegGraph.GetValue t
            if not (model.CellMapOn.GetValue t) || not (RegGraph.hasEdges g) then None
            else g.Edges |> Map.toSeq |> Seq.map fst |> Set.ofSeq |> Some

    // THE brush colour-isolation frame: (REF, MOV) of the selected pair while
    // brushed dots exist, else None. MOV owns the samples — it is the one solid
    // (whitened) surface and the dots' anchor; REF becomes the gold footprint.
    // There is no such frame at graph scope: many meshes own dots at once, so
    // nothing is isolated and the reference root keeps its own gold.
    // Token form (never build this aval inside another aval's compute).
    let brushFrameAt (model : AdaptiveModel) (t : FSharp.Data.Adaptive.AdaptiveToken) : (string * string) option =
        if Set.isEmpty (model.BrushedSamples.GetValue t) then None
        else
            match model.Focus.GetValue t, (model.Sel.GetValue t).Pair with
            | (FocusPair | FocusPin), Some (a, b) ->
                Some (MatrixNav.pairRefMov (model.RegGraph.GetValue t) a b)
            | _ -> None

    // The error map's (REF, MOV) frame inside the pair workspace: while the
    // map paints (map on, no brush — the brush mode suppresses it) the REF
    // carries no colour and drops out of the scene entirely; MOV enters as a
    // DEFAULT isolate exactly like the brush frame's, so an explicit lock and
    // every transient preview still win and the mode composes. No frame at
    // Matrix — graphMapScopeAt narrows that level instead.
    let mapFrameAt (model : AdaptiveModel) (t : FSharp.Data.Adaptive.AdaptiveToken) : (string * string) option =
        if not (model.CellMapOn.GetValue t) then None
        elif not (Set.isEmpty (model.BrushedSamples.GetValue t)) then None
        else
            match model.Focus.GetValue t, (model.Sel.GetValue t).Pair with
            | (FocusPair | FocusPin), Some (a, b) ->
                Some (MatrixNav.pairRefMov (model.RegGraph.GetValue t) a b)
            | _ -> None

    // Error-map isolation = a render MODE like the brush's: while ANY scope's
    // map paints, meshes carrying no error colour vanish outright (their ghost
    // floor drops to 0) and every mesh outline goes greyscale — the map is the
    // only colour on screen.
    let mapIsolationAt (model : AdaptiveModel) (t : FSharp.Data.Adaptive.AdaptiveToken) =
        Set.isEmpty (model.BrushedSamples.GetValue t) &&
        ((graphMapScopeAt model t).IsSome || (mapFrameAt model t).IsSome)

    // The committed isolate lock with both render-mode DEFAULT isolates folded
    // in (an explicit tile lock always wins; brush wins over map — the brush
    // suppresses the map anyway). The ONE composition every effective-
    // narrowing site reads (buildScene, View.shownNow, the pin marker rule).
    let committedIsoLockAt (model : AdaptiveModel) (t : FSharp.Data.Adaptive.AdaptiveToken) =
        MeshVisibility.withBrushIsolate (brushFrameAt model t |> Option.map snd)
            (model.TileIsolate.GetValue t)
        |> MeshVisibility.withBrushIsolate (mapFrameAt model t |> Option.map snd)

    // Per-vertex shape-quality buffer of a loaded mesh (recomputed once on load).
    let private shapeBufOf (loaded : LoadedMesh) =
        loaded.pos |> AVal.map (fun _ ->
            match loaded.mesh.Value with
            | Some md -> ArrayBuffer (shapeQuality md.positions md.indices) :> IBuffer
            | None -> ArrayBuffer [| 0.0f; 0.0f; 0.0f |] :> IBuffer)

    // Shared saturation end (world metres) of the Range heatmap: the farthest
    // own-bbox corner from each mesh's own sensor, maxed over ALL meshes — ONE
    // scale so range colours are comparable across meshes. Feeds the 3D
    // RangeMax uniform (main pass + survey tiles) and the Range legend.
    // 0 until bounds load (consumers fall back per mesh).
    let rangeMaxWorld (model : AdaptiveModel) : aval<float> =
        AVal.custom (fun t ->
            let bounds = model.MeshBounds.GetValue t
            let cents = model.DatasetCentroids.GetValue t
            let names = model.MeshNames.Content.GetValue t |> IndexList.toList
            let mutable mx = 0.0
            for name in names do
                match Map.tryFind name bounds with
                | Some (b : Box3d) when not b.IsInvalid ->
                    let sensor = Map.tryFind name cents |> Option.defaultValue b.Center
                    let dx = max (abs (b.Min.X - sensor.X)) (abs (b.Max.X - sensor.X))
                    let dy = max (abs (b.Min.Y - sensor.Y)) (abs (b.Max.Y - sensor.Y))
                    let dz = max (abs (b.Min.Z - sensor.Z)) (abs (b.Max.Z - sensor.Z))
                    let r = sqrt (dx * dx + dy * dy + dz * dz)
                    if r > mx then mx <- r
                | _ -> ()
            mx)

    // ONE average up-normal per project: the mean of every loaded mesh's mean
    // unit normal. Significant (terrain-like — the normals cluster around one
    // direction) when the resultant length exceeds 0.5 → Some (normalized),
    // the global pin/flag orientation; else None and pins keep their per-pin
    // probe axis.
    let projectUpNormal (model : AdaptiveModel) : aval<V3d option> =
        let namesA = model.MeshNames.Content
        AVal.custom (fun t ->
            let names = namesA.GetValue t |> IndexList.toList
            let mutable acc = V3d.Zero
            let mutable cnt = 0
            for name in names do
                let lm = loadMeshAsync (fun () -> ()) name
                let mn = lm.meanNormal.GetValue t
                if mn.Length > 1e-6 then
                    acc <- acc + mn
                    cnt <- cnt + 1
            if cnt = 0 then None
            else
                let u = acc / float cnt
                if u.Length > 0.5 then Some u.Normalized else None)

    // In-view near-plane cut (D1): (forward, distance, band) render-space
    // uniforms from the model fraction × the orbit radius; band ≈ screen-
    // constant (a fixed fraction of the cut distance). Dist 0 = off.
    let nearCutUniforms (model : AdaptiveModel) =
        let cutFwd = model.Camera.view |> AVal.map (fun cv -> V3f cv.Forward)
        let cutDist =
            (model.NearCutFrac, model.Camera.radius) ||> AVal.map2 (fun f r ->
                if f <= 1e-3 then 0.0f else float32 (f * r))
        let cutBand = cutDist |> AVal.map (fun d -> d * 0.008f)
        cutFwd, cutDist, cutBand

    // The far twin (shares CutFwd): the slider's right end (≥ 2.495) is off.
    let farCutUniforms (model : AdaptiveModel) =
        let dist =
            (model.FarCutFrac, model.Camera.radius) ||> AVal.map2 (fun f r ->
                if f >= 2.495 then 0.0f else float32 (f * r))
        let band = dist |> AVal.map (fun d -> d * 0.008f)
        dist, band

    // Pin blobs as a 32-slot uniform array, metric → render space (centre xyz,
    // inner radius w), for the mesh shader's pin-isolation filter.
    let private pinBlobUniforms (model : AdaptiveModel) =
        let datasetScale =
            (model.ActiveDataset, model.DatasetScales) ||> AVal.map2 DatasetScale.active
        let pinBlobs =
            // Focus-scoped like the pin scene nodes; the centre rides its anchor
            // mesh's displayed pose (token reads → the poses stay tracked).
            let pinsF =
                model.ScanPins.Pins
                |> AMap.map (fun _ p -> p.AnchorMesh, p.CentreLocal, p.InnerRadius, p.Pair)
                |> AMap.toAVal
            AVal.custom (fun t ->
                let pins = pinsF.GetValue t
                let cc = model.CommonCentroid.GetValue t
                let scale = datasetScale.GetValue t
                let focus = model.Focus.GetValue t
                let selPair = (model.Sel.GetValue t).Pair
                [| for (_, (anchorMesh, centreLocal, innerR, pair)) in HashMap.toSeq pins do
                    if MeshVisibility.pinShown focus selPair pair then
                        let world = (displayedWorldAt model t anchorMesh).Forward.TransformPos centreLocal
                        let cr = ScanPin.renderCentre cc scale world
                        yield V4f(float32 cr.X, float32 cr.Y, float32 cr.Z, float32 (ScanPin.renderLength scale innerR))
                   // The in-flight draft's area is a blob too — under the
                   // Isolate-pins view the in-edit area must read opaque like
                   // any committed patch.
                   match model.ScanPins.Placement.GetValue t with
                   | PlacementActive d ->
                        match d.Area with
                        | Some (m, local) when MeshVisibility.pinShown focus selPair d.Pair ->
                            let world = (displayedWorldAt model t m).Forward.TransformPos local
                            let cr = ScanPin.renderCentre cc scale world
                            yield V4f(float32 cr.X, float32 cr.Y, float32 cr.Z,
                                      float32 (ScanPin.renderLength scale d.Radius))
                        | _ -> ()
                   | PlacementIdle -> () |])
        let blobsArr =
            pinBlobs |> AVal.map (fun pins ->
                let n = min pins.Length MeshShader.MaxBlobs
                let centres = Array.zeroCreate<V4f> MeshShader.MaxBlobs
                for i in 0 .. n - 1 do centres.[i] <- pins.[i]
                n, centres)
        blobsArr |> AVal.map fst,
        blobsArr |> AVal.map snd

    // name → display index, shared by the main pass and the offscreen outline passes.
    let private meshIndicesA (model : AdaptiveModel) =
        model.MeshNames |> AList.toAVal |> AVal.map (fun names ->
            names |> Seq.mapi (fun i n -> n, i) |> Map.ofSeq)

    // Per-mesh preamble of the offscreen outline passes: the loaded mesh and its
    // displayed-pose render trafo (positions-only consumers).
    let private offscreenMesh (model : AdaptiveModel) (name : string) =
        let loaded = loadMeshAsync (fun () -> ()) name
        loaded, meshTrafo model.CommonCentroid loaded (scaleFor model name) (displayedMeshT model name)

    let buildScene (loadFinished : string -> unit) (clip : aval<int * V4f * V4f>) (model : AdaptiveModel) : aset<ISceneNode> =
        let renderingModeInt =
            model.RenderingMode |> AVal.map (function
                | Textured     -> 0
                | Shaded       -> 1
                | SlopeColor   -> 2)
        let meshIndices = meshIndicesA model
        let palette = Primitives.meshPaletteV4d

        let blobCount, blobs = pinBlobUniforms model
        let cutFwdU, cutDistU, cutBandU = nearCutUniforms model
        let farCutDistU, farCutBandU = farCutUniforms model
        let rangeWorldA = rangeMaxWorld model
        let clipCount  = clip |> AVal.map (fun (c, _, _) -> c)
        let clipPlane0 = clip |> AVal.map (fun (_, p, _) -> p)
        let clipPlane1 = clip |> AVal.map (fun (_, _, p) -> p)
        // Pin isolation = the persistent per-LEVEL default (AnchorGhostMode
        // holds one flag per rail stop) — SUSPENDED while the centre pick is
        // armed (aiming a whole-pin move needs the full terrain); derived, so
        // the stored toggle restores itself the moment the suspension ends.
        let anchorGhostOn =
            AVal.custom (fun t ->
                LevelFlags.get (model.Focus.GetValue t) (model.AnchorGhostMode.GetValue t)
                && model.ArmedPick.GetValue t <> Some ArmCentre)
        let anchorGhost = anchorGhostOn |> AVal.map (fun on -> if on then 1 else 0)
        // The ONE error range of the current scope (metres, spanning 0, capped
        // ±0.5) — shared by the map uniforms, the diagram and the legend so
        // every false-colour read is comparable.
        let cellRangeA = AVal.custom (inspectRangeAt model)
        // Brushing is the sole focus: while dots exist the whole scene whitens
        // so only they carry colour (one flag for every mesh — inside the pair
        // workspace the frame that decides WHICH mesh stays solid rides the
        // isolate; at graph scope the dots' owners are many, so none is).
        let colorIsolate =
            AVal.custom (fun t -> if brushActiveAt model t then 1.0f else 0.0f)
        // Error-map isolation: the map's colours stand alone — every mesh
        // without error colour loses even its ghost.
        let mapIsoA = AVal.custom (mapIsolationAt model)
        // The shown rule's shared inputs (the ONE effective narrowing) — ONE
        // context aval, N cheap per-mesh bool projections.
        let shownCtx =
            AVal.custom (fun t ->
                let focus = model.Focus.GetValue t
                let sel = model.Sel.GetValue t
                let hp = model.MatrixHoverPair.GetValue t
                let isoLock = committedIsoLockAt model t
                let isoRaw, pfRaw =
                    MeshVisibility.effectiveNarrowing (model.PinFocusHover.GetValue t)
                        (model.ArmedPick.GetValue t) (model.TileIsolateHover.GetValue t)
                        isoLock sel.Point
                // The vis peek swaps a pair-mesh isolate to the OTHER pair
                // mesh while held (same spot, other epoch) — derived only,
                // release reverts because the lock itself never moves. The
                // Pin level's point narrowing swaps with it (Sel.Point rides
                // the same lock — scope ∩ iso would go empty otherwise).
                let iso, pf =
                    match isoRaw, sel.Pair with
                    | Some m, Some (a, b) when model.PeekVis.GetValue t && (m = a || m = b) ->
                        let other = if m = a then b else a
                        Some other, (pfRaw |> Option.map (fun x -> if x = m then other else x))
                    | _ -> isoRaw, pfRaw
                focus, sel.Pair, iso, hp, graphMapScopeAt model t, pf)
        model.MeshNames |> AList.map (fun name ->
            let loaded = loadMeshAsync (fun () -> loadFinished name) name
            // The ONE shown rule: Matrix shows every mesh (tile isolate /
            // matrix hover narrow it); Pair isolates the selected pair, Pin
            // narrows further to the effective focus mesh (the rest drop to
            // the ghost floor).
            let isActive =
                shownCtx |> AVal.map (fun (focus, selPair, iso, hp, gs, pf) ->
                    MeshVisibility.shown focus selPair iso hp gs pf name)
            let scale = scaleFor model name
            let meshT = displayedMeshT model name
            // Sensor origin = the mesh origin (the radial-scan pipeline centres
            // each OPC on its scan station), ridden through the displayed pose.
            // Drives the incidence + range heatmaps from the real sensor, not the
            // interactive camera. RangeMax = the GLOBAL all-mesh saturation end
            // (rangeMaxWorld) so range colours compare across meshes; local
            // fallback while bounds are pending.
            let fullTrafo = meshTrafo model.CommonCentroid loaded scale meshT
            let sensorOrigin =
                fullTrafo |> AVal.map (fun t -> V3f (t.Forward.TransformPos V3d.Zero))
            let rangeMax =
                (rangeWorldA, scale, loaded.localMaxR) |||> AVal.map3 (fun g s lr ->
                    float32 (max 1e-6 ((if g > 0.0 then g else lr) * s)))
            // Inactive meshes still render (as ghost); gate on load state only.
            let renderEnabled = loaded.fvc |> AVal.map (fun n -> n > 3)
            let meshColor =
                meshIndices |> AVal.map (fun m ->
                    let i = Map.tryFind name m |> Option.defaultValue 0
                    V4f palette.[i % palette.Length])
            // False-colour: the MOVING mesh paints its signed distance vs its
            // reference — never the reference against itself, and nothing is
            // isolated. In the pair workspace that is the cell's MOV vs REF; at
            // Matrix every registered CHILD paints against its PARENT at once
            // (the union of the per-edge moving-side maps, all on the one
            // shared ramp). Brushing = sole focus: a non-empty brush suppresses
            // the map (the dots carry the values).
            let cellPaint : aval<float32[] option> =
                AVal.custom (fun t ->
                    let painting =
                        model.CellMapOn.GetValue t && Set.isEmpty (model.BrushedSamples.GetValue t)
                    match model.Focus.GetValue t with
                    | FocusMatrix ->
                        // The peeked state's buffer: both are resident, so the
                        // key costs one attribute upload, never a refetch.
                        let dists =
                            match graphSideAt model t with
                            | EdgeBefore -> model.GraphDistBefore.GetValue t
                            | EdgeAfter -> model.GraphDist.GetValue t
                        if painting then Map.tryFind name dists else None
                    | FocusPair | FocusPin ->
                        match (model.Sel.GetValue t).Pair with
                        | Some (a, b) when painting ->
                            let _, mov = MatrixNav.pairRefMov (model.RegGraph.GetValue t) a b
                            if mov <> name then None
                            else
                                match model.CellDist.GetValue t with
                                | Some dist ->
                                    // The Pin level narrows the map to the selected
                                    // pin's ROI sphere: outside vertices get the
                                    // 3e30 keep-base sentinel (1e30 stays the
                                    // server's no-data grey). Distances compare in
                                    // MOV's own frame — rigid poses preserve them.
                                    let roi =
                                        match model.Focus.GetValue t, (model.Sel.GetValue t).Pin with
                                        | FocusPin, Some id ->
                                            HashMap.tryFind id (model.ScanPins.Pins.Content.GetValue t)
                                            |> Option.map (fun p ->
                                                let w = (displayedWorldAt model t p.AnchorMesh).Forward.TransformPos p.CentreLocal
                                                (displayedWorldAt model t name).Backward.TransformPos w, p.InnerRadius)
                                        | _ -> None
                                    match roi, loaded.mesh.Value with
                                    | Some (centreOwn, r), Some md ->
                                        // Served positions are centroid-subtracted.
                                        let c = V3f (centreOwn - md.centroid)
                                        let r2 = float32 (r * r)
                                        Some (Array.init dist.Length (fun i ->
                                            if i < md.positions.Length && (md.positions.[i] - c).LengthSquared <= r2
                                            then dist.[i] else 3.0e30f))
                                    | _ -> Some dist
                                | None -> None
                        | _ -> None)
            let distBuf =
                (cellPaint, loaded.pos) ||> AVal.map2 (fun d _ ->
                    match d with
                    | Some arr -> ArrayBuffer arr :> IBuffer
                    | None ->
                        match loaded.mesh.Value with
                        | Some md -> ArrayBuffer (Array.zeroCreate<float32> md.positions.Length) :> IBuffer
                        | None -> ArrayBuffer [| 0.0f; 0.0f; 0.0f |] :> IBuffer)
            let shapeBuf = shapeBufOf loaded
            let distEncoding = cellPaint |> AVal.map (fun d -> if d.IsSome then 1 else 0)
            // Map ends from the unified pair range: enc 1 saturates at (lo, hi).
            let distScale =
                (distEncoding, cellRangeA) ||> AVal.map2 (fun enc (_, hi) ->
                    if enc = 1 then float32 hi else 1.0f)
            let distLoNeg = cellRangeA |> AVal.map (fun (lo, _) -> float32 (abs lo))
            let diffIsoStep =
                (distEncoding, cellRangeA) ||> AVal.map2 (fun enc (lo, hi) ->
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
                    // context, off → hidden (α discarded). Nav scoping and pin
                    // isolation send non-emphasized meshes to this same floor.
                    Sg.Uniform("GhostOpacity",
                        AVal.custom (fun t ->
                            let floorOn = model.GhostSilhouette.GetValue t
                            // "Isolate pins" (armed-centre suspension included)
                            // and the error-map isolation both zero the context
                            // floor — only the coloured signal reads.
                            if anchorGhostOn.GetValue t then 0.0f
                            elif mapIsoA.GetValue t then 0.0f
                            elif floorOn then float32 (model.GhostOpacity.GetValue t)
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
                    Sg.Uniform("CutFwd",               cutFwdU)
                    Sg.Uniform("CutDist",              cutDistU)
                    Sg.Uniform("CutBand",              cutBandU)
                    Sg.Uniform("FarCutDist",           farCutDistU)
                    Sg.Uniform("FarCutBand",           farCutBandU)
                    Sg.Uniform("DistanceEncoding",     distEncoding)
                    Sg.Uniform("DistScale",            distScale)
                    Sg.Uniform("DistLoNeg",            distLoNeg)
                    Sg.Uniform("DiffIsoStep",          diffIsoStep)
                    // Per-mesh intrinsic error layer (set from the survey mesh list).
                    Sg.Uniform("HeatmapMode",
                        model.MeshHeatmap |> AVal.map (fun mh ->
                            match Map.tryFind name mh |> Option.defaultValue HeatOff with
                            | HeatOff -> 0 | HeatIncidence -> 1 | HeatRange -> 2 | HeatShape -> 3))
                    Sg.Uniform("SensorOrigin",         sensorOrigin)
                    Sg.Uniform("RangeMax",             rangeMax)
                    Sg.Uniform("ShapeThreshold",       model.ShapeThreshold |> AVal.map float32)
                    // The painted mesh swaps its base to plain near-white — no
                    // photo texture competes under the false colour; the brush's
                    // colour isolation whitens EVERY mesh the same way.
                    Sg.Uniform("InspectPlain",
                        (distEncoding, colorIsolate) ||> AVal.map2 (fun e ci ->
                            if e = 1 || ci > 0.5f then 1.0f else 0.0f))
                    Sg.Uniform("ColorIsolate", colorIsolate)
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

    // Per-mesh slot gate of the outline composites, indexed like MeshId
    // ((index+1)/255) and — same numbering — like the coverage channels.
    // `slots` = the display indices left on; None = every mesh.
    let private maskOf (slots : Set<int> option) =
        let on = V4f(1.0f, 0.0f, 0.0f, 0.0f)
        match slots with
        | None -> Array.create 32 on
        | Some s -> Array.init 32 (fun i -> if Set.contains i s then on else V4f.Zero)

    // The G-buffer composite's gate (silhouettes + elevation isolines). The
    // G-buffer holds every mesh regardless of visibility, so under brush colour
    // isolation only the solid MOV may keep its lines — a ghosted mesh's
    // palette silhouette would be colour competing with the dots.
    let outlineMask (model : AdaptiveModel) : aval<V4f[]> =
        let idxA = meshIndicesA model
        AVal.custom (fun t ->
            match brushFrameAt model t with
            | Some (_, mov) ->
                maskOf (Some (Map.tryFind mov (idxA.GetValue t) |> Option.toList |> Set.ofList))
            | None -> maskOf None)

    // The FOOTPRINT composite's gate: under colour isolation the pair alone —
    // MOV's own contour plus the reference's, which the colours below repaint
    // gold (the REF's whole contribution: context, no competing surface).
    let footprintMask (model : AdaptiveModel) : aval<V4f[]> =
        let idxA = meshIndicesA model
        AVal.custom (fun t ->
            match brushFrameAt model t with
            | Some (refM, mov) ->
                let idx = idxA.GetValue t
                maskOf (Some ([refM; mov] |> List.choose (fun m -> Map.tryFind m idx) |> Set.ofList))
            | None -> maskOf None)

    // Outline G-buffer: every mesh rendered solid with OutlineGBuffer.shade,
    // consumed by OutlineView's offscreen pass.
    let buildOutlineNode (model : AdaptiveModel) (view : aval<Trafo3d>) (proj : aval<Trafo3d>) : ISceneNode =
        let meshIndices = meshIndicesA model
        let palette = Primitives.meshPaletteV4d
        // World-Z isoline spacing (render-space Z step), shared across meshes so
        // the band parity lines up. Camera-adaptive: the spacing follows the
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
                let loaded, trafo = offscreenMesh model name
                // Every loaded mesh renders into the G-buffer (visibility gates the
                // main pass only).
                let active = loaded.fvc |> AVal.map (fun n -> n > 3)
                let meshColor =
                    meshIndices |> AVal.map (fun m ->
                        let i = Map.tryFind name m |> Option.defaultValue 0
                        V4f palette.[i % palette.Length])
                let meshId =
                    meshIndices |> AVal.map (fun m ->
                        float32 ((Map.tryFind name m |> Option.defaultValue 0) + 1) / 255.0f)
                sg {
                    Sg.Active active
                    Sg.Trafo trafo
                    Sg.Shader {
                        DefaultSurfaces.trafo
                        OutlineGBuffer.shade
                    }
                    Sg.Uniform("MeshColor", meshColor)
                    Sg.Uniform("MeshId", meshId)
                    Sg.Uniform("ContourSpacing", contourSpacing)
                    Sg.VertexAttributes(
                        HashMap.ofList [
                            string DefaultSemantic.Positions, BufferView(loaded.pos, typeof<V3f>)
                        ])
                    Sg.Index(BufferView(loaded.idx, typeof<int>))
                    Sg.Render loaded.fvc
                }
            ) |> AList.toASet
        let cutFwdU, cutDistU, cutBandU = nearCutUniforms model
        let farCutDistU, _ = farCutUniforms model
        sg {
            Sg.View view
            Sg.Proj proj
            // The G-buffer follows both cuts so lines of cut-away geometry
            // vanish with them.
            Sg.Uniform("CutFwd",  cutFwdU)
            Sg.Uniform("CutDist", cutDistU)
            Sg.Uniform("CutBand", cutBandU)
            Sg.Uniform("FarCutDist", farCutDistU)
            Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
            nodes
        }

    // Per-mesh screen-space coverage for the footprint-contour pass: every mesh
    // accumulates additively into its own channel at its
    // displayed pose — no depth buffer, no occlusion — so the coverage composite
    // can outline each mesh separately even where meshes overlap or are hidden.
    // Channels cap at 8 (2×Rgba8 MRT); meshes beyond keep only the combined
    // union outline. `activeA` gates the whole pass (the per-tile MRTs render
    // only while their overlap gate can read them; the main view passes true).
    let buildCoverageNode (model : AdaptiveModel) (activeA : aval<bool>)
                          (view : aval<Trafo3d>) (proj : aval<Trafo3d>) : ISceneNode =
        let meshIndices = meshIndicesA model
        let nodes =
            model.MeshNames |> AList.map (fun name ->
                let loaded, trafo = offscreenMesh model name
                let channel = meshIndices |> AVal.map (fun m -> Map.tryFind name m |> Option.defaultValue 0)
                let active =
                    AVal.custom (fun t ->
                        activeA.GetValue t
                        && loaded.fvc.GetValue t > 3 && (channel.GetValue t) < 8)
                sg {
                    Sg.Active active
                    Sg.Trafo trafo
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

    // …with the brush frame's REFERENCE repainted gold: under colour isolation
    // the reference surface is gone and its footprint contour IS the spatial
    // frame, so it reads in the reference gold like the strip's root overlay.
    // Under error-map isolation instead, every footprint drops to its
    // luminance grey — identity colour would compete with the map.
    let coverageColorsA (model : AdaptiveModel) : aval<V4f[]> =
        let idxA = meshIndicesA model
        AVal.custom (fun t ->
            match brushFrameAt model t with
            | Some (refM, _) ->
                match Map.tryFind refM (idxA.GetValue t) with
                | Some i when i < 8 ->
                    let cs = Array.copy coverageColors
                    cs.[i] <- V4f(V3f Primitives.refGoldV3d, 1.0f)
                    cs
                | _ -> coverageColors
            | None ->
                if mapIsolationAt model t then
                    coverageColors |> Array.map (fun c ->
                        let g = 0.299f * c.X + 0.587f * c.Y + 0.114f * c.Z
                        V4f(g, g, g, c.W))
                else coverageColors)

    // The reference root ALONE into coverage channel 0, from a tile's own
    // camera — every ortho tile/pane overlays this footprint as the gold
    // reference outline (occlusion-free, unobscured by the tile's mesh).
    // Gated by the strip's visibility so hidden tiles pay nothing.
    let buildRootCoverageNode (model : AdaptiveModel) (active : aval<bool>)
                              (view : aval<Trafo3d>) (proj : aval<Trafo3d>) : ISceneNode =
        let nodes =
            model.RegGraph
            |> AVal.map (fun g -> g.Root |> Option.toList |> IndexList.ofList)
            |> AList.ofAVal
            |> AList.map (fun name ->
                let loaded, trafo = offscreenMesh model name
                let a = (active, loaded.fvc) ||> AVal.map2 (fun on c -> on && c > 3)
                sg {
                    Sg.Active a
                    Sg.Trafo trafo
                    Sg.Shader {
                        DefaultSurfaces.trafo
                        OutlineCoverage.shade
                    }
                    Sg.Uniform("CoverageChannel", AVal.constant 0)
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

    // Overlap-preview uniforms: the on-flag + the active pair's coverage-
    // channel selectors over the two MRT targets (channel = display index,
    // 0-3 → target0, 4-7 → target1 — the OutlineCoverage layout). The gate
    // lights for the MATRIX HOVER (pair preview) and for the pin-LOCATION
    // interactions at Pair/Pin — the ○ New pin hover and the armed centre
    // pick — where only the overlap is a valid spot. A pair mesh beyond the
    // 8-channel cap disables the preview outright rather than half-testing.
    let overlapPreviewUniforms (model : AdaptiveModel) =
        let idxA = meshIndicesA model
        let pairIdx =
            AVal.custom (fun t ->
                let pair =
                    match model.Focus.GetValue t with
                    | FocusMatrix -> model.MatrixHoverPair.GetValue t
                    | FocusPair | FocusPin ->
                        match (model.Sel.GetValue t).Pair with
                        | Some p when model.NewPinHover.GetValue t
                                      || model.ArmedPick.GetValue t = Some ArmCentre -> Some p
                        | _ -> None
                match pair with
                | Some (a, b) ->
                    let idx = idxA.GetValue t
                    let ia = Map.tryFind a idx |> Option.defaultValue 8
                    let ib = Map.tryFind b idx |> Option.defaultValue 8
                    if ia < 8 && ib < 8 then Some (ia, ib) else None
                | None -> None)
        let sel (k : int) (target : int) =
            if k / 4 = target then
                match k % 4 with
                | 0 -> V4f.IOOO | 1 -> V4f.OIOO | 2 -> V4f.OOIO | _ -> V4f.OOOI
            else V4f.Zero
        pairIdx |> AVal.map (fun o -> if o.IsSome then 1 else 0),
        pairIdx |> AVal.map (function Some (ia, _) -> sel ia 0 | None -> V4f.Zero),
        pairIdx |> AVal.map (function Some (ia, _) -> sel ia 1 | None -> V4f.Zero),
        pairIdx |> AVal.map (function Some (_, ib) -> sel ib 0 | None -> V4f.Zero),
        pairIdx |> AVal.map (function Some (_, ib) -> sel ib 1 | None -> V4f.Zero)

    // One strip-tile mesh: the mesh `name` with the full shipped shader,
    // inspection modes off but the per-mesh survey heatmap live. `overlap` =
    // (other pair mesh — adaptive, Some while this tile's mesh sits in the
    // selected pair — plus the tile-camera coverage MRT): the isolate-overlap
    // gate engages while a placement is armed — solid only where the MRT
    // covers the pixel in BOTH pair channels, the rest at the ghost floor —
    // exactly the valid placement area.
    let buildPaneScene
        (model : AdaptiveModel)
        (name : string)
        (paneActive : aval<bool>)
        (overlap : aval<string option> * aval<IBackendTexture> * aval<IBackendTexture>)
        (viewportSize : aval<V2i>) : ISceneNode =
        let loaded = loadMeshAsync (fun () -> ()) name
        let scale = scaleFor model name
        let fullTrafo = meshTrafo model.CommonCentroid loaded scale (displayedMeshT model name)
        let meshIndices = meshIndicesA model
        let palette = Primitives.meshPaletteV4d
        let meshColor =
            meshIndices |> AVal.map (fun m ->
                let i = Map.tryFind name m |> Option.defaultValue 0
                V4f palette.[i % palette.Length])
        let placing =
            model.ScanPins.Placement |> AVal.map (function PlacementActive _ -> true | PlacementIdle -> false)
        let otherA, cov0, cov1 = overlap
        // The overlap gate needs BOTH pair channels below the 8-channel MRT cap;
        // beyond it the gate disengages outright rather than half-testing.
        let pairIdx =
            (placing, otherA, meshIndices) |||> AVal.map3 (fun pl other idx ->
                match other with
                | Some o when pl ->
                    let ia = Map.tryFind name idx |> Option.defaultValue 8
                    let ib = Map.tryFind o idx |> Option.defaultValue 8
                    if ia < 8 && ib < 8 then Some (ia, ib) else None
                | _ -> None)
        let sel (k : int) (target : int) =
            if k / 4 = target then
                match k % 4 with
                | 0 -> V4f.IOOO | 1 -> V4f.OIOO | 2 -> V4f.OOIO | _ -> V4f.OOOI
            else V4f.Zero
        let active =
            (paneActive, loaded.fvc) ||> AVal.map2 (fun a fvc -> a && fvc > 3)
        let zeroBuf =
            loaded.pos |> AVal.map (fun _ ->
                match loaded.mesh.Value with
                | Some md -> ArrayBuffer (Array.zeroCreate<float32> md.positions.Length) :> IBuffer
                | None -> ArrayBuffer [| 0.0f; 0.0f; 0.0f |] :> IBuffer)
        // IBackendTexture → ITexture through AVal.map (aval is invariant — a
        // direct upcast of the aval itself does not typecheck).
        let covTex0, covTex1 =
            cov0 |> AVal.map (fun t -> t :> ITexture), cov1 |> AVal.map (fun t -> t :> ITexture)
        // Survey heatmap inputs (same sensor/range conventions as the main pass).
        let sensorOrigin =
            fullTrafo |> AVal.map (fun t -> V3f (t.Forward.TransformPos V3d.Zero))
        let rangeMax =
            (rangeMaxWorld model, scale, loaded.localMaxR) |||> AVal.map3 (fun g s lr ->
                float32 (max 1e-6 ((if g > 0.0 then g else lr) * s)))
        sg {
            Sg.Active active
            Sg.Trafo fullTrafo
            Sg.Shader {
                DefaultSurfaces.trafo
                DefaultSurfaces.diffuseTexture
                MeshShader.shade
            }
            Sg.Uniform("ViewportSize", viewportSize)
            Sg.Uniform("Coverage0", covTex0)
            Sg.Uniform("Coverage1", covTex1)
            Sg.Uniform("OverlapPreview", pairIdx |> AVal.map (fun o -> if o.IsSome then 1 else 0))
            Sg.Uniform("OverlapSelA0", pairIdx |> AVal.map (function Some (ia, _) -> sel ia 0 | None -> V4f.Zero))
            Sg.Uniform("OverlapSelA1", pairIdx |> AVal.map (function Some (ia, _) -> sel ia 1 | None -> V4f.Zero))
            Sg.Uniform("OverlapSelB0", pairIdx |> AVal.map (function Some (_, ib) -> sel ib 0 | None -> V4f.Zero))
            Sg.Uniform("OverlapSelB1", pairIdx |> AVal.map (function Some (_, ib) -> sel ib 1 | None -> V4f.Zero))
            Sg.Uniform("DiffuseColorTexture", loaded.tex)
            Sg.Uniform("MeshActive",      AVal.constant true)
            Sg.Uniform("GhostOpacity",
                (model.GhostSilhouette, model.GhostOpacity) ||> AVal.map2 (fun on o ->
                    if on then float32 o else 0.0f))
            Sg.Uniform("RenderingMode",
                model.RenderingMode |> AVal.map (function
                    | Textured -> 0 | Shaded -> 1 | SlopeColor -> 2))
            Sg.Uniform("MeshColor",       meshColor)
            Sg.Uniform("ShadingStrength", model.ShadingStrength |> AVal.map float32)
            Sg.Uniform("SlopeThreshold",
                model.SlopeThresholdDeg |> AVal.map (fun d ->
                    sin (d * System.Math.PI / 180.0) |> float32))
            Sg.Uniform("BlobCount",       AVal.constant 0)
            Sg.Uniform("Blobs",           AVal.constant (Array.zeroCreate<V4f> MeshShader.MaxBlobs))
            Sg.Uniform("AnchorGhost",     AVal.constant 0)
            Sg.Uniform("ClipPlaneCount",  AVal.constant 0)
            Sg.Uniform("ClipPlane0",      AVal.constant V4f.Zero)
            Sg.Uniform("ClipPlane1",      AVal.constant V4f.Zero)
            Sg.Uniform("CutFwd",          AVal.constant V3f.OOI)
            Sg.Uniform("CutDist",         AVal.constant 0.0f)
            Sg.Uniform("CutBand",         AVal.constant 0.0f)
            Sg.Uniform("FarCutDist",      AVal.constant 0.0f)
            Sg.Uniform("FarCutBand",      AVal.constant 0.0f)
            Sg.Uniform("DistanceEncoding", AVal.constant 0)
            Sg.Uniform("DistScale",       AVal.constant 1.0f)
            Sg.Uniform("DistLoNeg",       AVal.constant 1.0f)
            Sg.Uniform("DiffIsoStep",     AVal.constant 0.0f)
            Sg.Uniform("HeatmapMode",
                model.MeshHeatmap |> AVal.map (fun mh ->
                    match Map.tryFind name mh |> Option.defaultValue HeatOff with
                    | HeatOff -> 0 | HeatIncidence -> 1 | HeatRange -> 2 | HeatShape -> 3))
            Sg.Uniform("SensorOrigin",    sensorOrigin)
            Sg.Uniform("RangeMax",        rangeMax)
            Sg.Uniform("ShapeThreshold",  model.ShapeThreshold |> AVal.map float32)
            Sg.Uniform("InspectPlain",    AVal.constant 0.0f)
            // The strip keeps its own colours — the brush's colour isolation is
            // the main view's mode.
            Sg.Uniform("ColorIsolate",    AVal.constant 0.0f)
            Sg.VertexAttributes(
                HashMap.ofList [
                    string DefaultSemantic.Positions,               BufferView(loaded.pos, typeof<V3f>)
                    string DefaultSemantic.DiffuseColorCoordinates, BufferView(loaded.tc,  typeof<V2f>)
                    string DefaultSemantic.Normals,                 BufferView(loaded.nrm, typeof<V3f>)
                    "SurfaceDist",                                  BufferView(zeroBuf, typeof<float32>)
                    "ShapeQ",                                       BufferView(shapeBufOf loaded, typeof<float32>)
                ]
            )
            Sg.Index(BufferView(loaded.idx, typeof<int>))
            Sg.Render loaded.fvc
        }
