namespace Superprojekt

open Aardvark.Base
open Aardworx.WebAssembly
open Microsoft.JSInterop
open FSharp.Data.Adaptive
open Aardvark.Dom
open Superprojekt
open UpdateHelpers

module Update =

    let private updateCore (env : Env<Message>) (model : Model) (msg : Message) =
        match msg with
        | CameraMessage msg ->
            { model with Camera = OrbitController.update (Env.map CameraMessage env) model.Camera msg }
        | CentroidsLoaded centroids ->
            let common  = if centroids.Length > 0 then centroids |> Array.averageBy snd else V3d.Zero
            let names   = centroids |> Array.map fst |> IndexList.ofArray
            let visible = centroids |> Array.fold (fun m (n, _) -> Map.add n true m) Map.empty
            let indices = centroids |> Array.mapi (fun i (n,_) -> n,i) |> HashMap.ofArray
            // LoadTransform = the immutable baseline captured at load (identity; meshes load unregistered).
            let loadTransforms = centroids |> Array.fold (fun m (n, _) -> Map.add n Trafo3d.Identity m) Map.empty
            let dataset =
                if centroids.Length > 0 then
                    let n = fst centroids.[0] in let s = n.IndexOf('/') in if s >= 0 then n.[..s-1] else ""
                else ""
            { model with
                MeshNames        = names
                MeshVisible      = visible
                CommonCentroid   = common
                MeshOrder        = indices
                MeshesLoaded     = HashSet.empty
                SceneBounds      = Box3d.Invalid
                PanoCenters      = Map.empty
                LoadTransforms   = loadTransforms
                SolvedTransforms = Map.empty
                RegView          = RegBefore
                // Default reference = first mesh so reference-peek + registration UI work out of the box.
                Registration     =
                    { model.Registration with
                        ReferenceMesh = if centroids.Length > 0 then Some (fst centroids.[0]) else None }
                DatasetCentroids =
                    let perMesh = centroids |> Array.fold (fun m (n, c) -> Map.add n c m) model.DatasetCentroids
                    if dataset <> "" then Map.add dataset common perMesh else perMesh }
        | PanoCentersLoaded pcs ->
            { model with PanoCenters = Map.ofArray pcs }
        | SetVisible(name, v) ->
            // Visibility toggles are frozen while a mesh is isolated (the tile
            // buttons are disabled too; this is the reducer-side guard).
            if model.MeshSolo.IsSome then model
            else
                let activePickingLayer =
                    if not v && model.ActivePickingLayer = Some name then None
                    else model.ActivePickingLayer
                // Probes sample every mesh regardless of visibility (like contact rings),
                // so a visibility toggle keeps the matrix cells stable — no re-probe.
                // setMeshVisible refreshes the visibility-derived Inspect caches.
                setMeshVisible (Map.add name v model.MeshVisible)
                    { model with ActivePickingLayer = activePickingLayer }
        | ToggleMenu ->
            let sp = model.ScanPins
            if ScanPinModel.isPlacing sp then model
            else { model with MenuOpen = not model.MenuOpen }
        | LoadFinished name ->
            let model = { model with MeshesLoaded = HashSet.add name model.MeshesLoaded }

            let missing = HashSet.difference (HashSet.ofSeq model.MeshNames) model.MeshesLoaded
            if missing.Count = 0 then
                let d = Window.Document.CreateElement("div")
                d.Id <- "loading-done"
                d.Style.Visibility <- "hidden"
                d.Style.Position <- "fixed"
                d.Style.PointerEvents <- "none"
                Window.Document.Body.AppendChild(d) |> ignore
            model
        | ToggleGhostSilhouette ->
            { model with GhostSilhouette = not model.GhostSilhouette }
        | SetGhostOpacity v ->
            { model with GhostOpacity = v }
        | SetShadingStrength v ->
            { model with ShadingStrength = v }
        | SetSlopeThresholdDeg v ->
            { model with SlopeThresholdDeg = v }
        | SetOutlineThreshold v ->
            { model with OutlineThreshold = max 0.0 v }
        | SetIsolineBands v ->
            { model with IsolineBands = max 1.0 v }
        | ToggleAnchorGhostMode ->
            { model with AnchorGhostMode = not model.AnchorGhostMode }
        | SetQuickPinRadius v ->
            { model with QuickPinRadius = max 0.01 v }
        | SetIsolatePeek held ->
            if model.IsolatePeekHeld = held then model
            else { model with IsolatePeekHeld = held }
        | SetShowOverlays held ->
            if model.ShowOverlaysHeld = held then model
            else { model with ShowOverlaysHeld = held }
        | SetRegView v ->
            // Only meaningful once a solve exists (the view disables it otherwise).
            // Probes + slices: a ready (main, other) pair IS the two poses — swap in
            // place instead of refetching; rings have no other-pose cache, refetch.
            if model.RegView = v || Map.isEmpty model.SolvedTransforms then model
            else
                // Brushed gids index the canonical sample array of the committed
                // pose — the swap re-indexes it, so drop them.
                invalidateRings
                    { model with
                        RegView = v
                        BrushedSamples = Set.empty
                        ScanPins = ScanPinModel.swapProbeViews model.ScanPins }
        | SetRegPeek held ->
            // Purely visual (the displayed transform flips); no probe/ring invalidation.
            if model.RegPeekHeld = held then model else { model with RegPeekHeld = held }
        | SetReferenceMesh mesh ->
            // Reference change invalidates any solve (it was relative to the old reference):
            // drop SolvedTransforms, snap to Before, invalidate probes/rings, re-seed enabled pins.
            let model =
                invalidateProbes (invalidateRings
                    { model with
                        Registration = { model.Registration with ReferenceMesh = mesh }
                        SolvedTransforms = Map.empty
                        RegView = RegBefore
                        CorrArm = None; CorrPreview = None })
            match mesh with
            | Some _ -> seedAnchors env model (correspondenceEnabledIds model)
            | None -> model

        // Weighted rigid solve per visible moving mesh with ≥3 in-ROI pairs, in
        // parallel. Writes SolvedTransform directly. Anchors are mesh-local, so the
        // pairs are taken at the load pose, giving an absolute solved transform a
        // re-solve replaces wholesale.
        | SolveCoarse ->
            // Any other correspondence action cancels a live 3D pick.
            let model = { model with CorrArm = None; CorrPreview = None }
            let reg = model.Registration
            match reg.ReferenceMesh with
            | None -> showToast env "Designate a reference mesh (★) first" model
            | Some refMesh ->
                let visibleMoving =
                    model.MeshNames |> IndexList.toList
                    |> List.filter (fun n ->
                        n <> refMesh
                        && Map.tryFind n model.MeshVisible |> Option.defaultValue true)
                let enabledPins =
                    model.ScanPins.Pins |> HashMap.toList
                    |> List.choose (fun (_, p) ->
                        match ScanPin.correspondence p with
                        | Some c when c.RefAnchor.IsSome ->
                            Some (p.Id, c.RefAnchor.Value, c.Anchors)
                        | _ -> None)
                let pairsFor mesh =
                    enabledPins
                    |> List.choose (fun (pinId, ra, anchors) ->
                        match Map.tryFind mesh anchors with
                        | Some a -> Some (pinId, ra, a.Point)
                        | None -> None)
                    |> Array.ofList
                let solvable = visibleMoving |> List.filter (fun m -> (pairsFor m).Length >= 3)
                if List.isEmpty solvable then
                    showToast env "No mesh has ≥3 correspondence markers yet" model
                else
                    // Solve every solvable mesh in parallel; unsolvable meshes keep no
                    // SolvedTransform (stay at their load pose) and are flagged in the UI.
                    for mesh in solvable do
                        // (refAnchor world, moving anchor at load pose = own-frame point, weight 1).
                        let queryPairs = pairsFor mesh |> Array.map (fun (_, ra, mp) -> ra, mp, 1.0)
                        task {
                            try
                                let! world =
                                    Query.lsqPairs ApiConfig.apiBase.Value mesh queryPairs
                                    |> Async.StartAsTask
                                env.Emit [CoarseSolved(mesh, world)]
                            with ex ->
                                env.Emit [CoarseFailed(mesh, ex.Message)]
                        } |> ignore
                    let n = List.length solvable
                    let total = List.length visibleMoving
                    let unsolvable = visibleMoving |> List.filter (fun m -> (pairsFor m).Length < 3)
                    let summary =
                        match unsolvable with
                        | [] -> sprintf "Solving %d of %d meshes…" n total
                        | first :: rest ->
                            let need = max 1 (3 - (pairsFor first).Length)
                            let extra = if List.isEmpty rest then "" else sprintf " (+%d more)" (List.length rest)
                            sprintf "Solving %d of %d; %s needs %d more%s"
                                n total (Primitives.shortName first) need extra
                    showToast env summary model
        | CoarseSolved(mesh, world) ->
            // lsqPairs returns the absolute world transform mapping the load-pose
            // moving anchors onto the reference; store it as the SolvedTransform.
            let scale = DatasetScale.forMesh model.DatasetScales mesh
            let solvedRender =
                RigidTransform.worldToRender scale model.CommonCentroid (Trafo3d(world, world.Inverse))
            invalidateRings (invalidateProbes
                { model with
                    SolvedTransforms = Map.add mesh solvedRender model.SolvedTransforms
                    RegView = RegAfter })
        | CoarseFailed(mesh, reason) ->
            showToast env (sprintf "Solve failed (%s)" (Primitives.shortName mesh))
                { model with
                    DebugLog = model.DebugLog.InsertAt(0, sprintf "coarse solve failed (%s): %s" mesh reason) }

        | AnchorsSeeded(refUpdates, seeded, inRoi) ->
            let sp =
                refUpdates |> Array.fold (fun sp (pinId, ra) ->
                    updateCorr pinId (fun c -> { c with RefAnchor = Some ra }) sp)
                    model.ScanPins
            // Record ROI membership; drop a stale auto marker for any mesh that
            // resolved out-of-ROI (a manual pick is kept).
            let sp =
                inRoi |> Array.fold (fun sp (pinId, mesh, inside) ->
                    updateCorr pinId (fun c ->
                        let anchors =
                            if inside then c.Anchors
                            else
                                match Map.tryFind mesh c.Anchors with
                                | Some a when a.Source = AnchorAuto -> Map.remove mesh c.Anchors
                                | _ -> c.Anchors
                        { c with InRoi = Map.add mesh inside c.InRoi; Anchors = anchors }) sp)
                    sp
            // Seeded correspondence markers apply immediately (Auto source).
            let sp =
                seeded |> Array.fold (fun sp (pinId, mesh, point) ->
                    updateCorr pinId (fun corr ->
                        { corr with Anchors = Map.add mesh { Point = point; Source = AnchorAuto } corr.Anchors }) sp)
                    sp
            { model with ScanPins = sp }
        | AnchorSeedFailed reason ->
            showToast env "Correspondence seeding failed — see debug log"
                { model with
                    DebugLog = model.DebugLog.InsertAt(0, sprintf "correspondence seeding failed: %s" reason) }
        | ClearToast ->
            if model.Toast.IsNone then model else { model with Toast = None }

        | SetMeshHeatmap(mesh, m) ->
            // Store HeatOff as removal so the map stays sparse (default lookup = off).
            let mh = if m = HeatOff then Map.remove mesh model.MeshHeatmap else Map.add mesh m model.MeshHeatmap
            { model with MeshHeatmap = mh }
        | VarianceComputed(mesh, arr) ->
            // Keep only if still in Inspect and this is the reference mesh.
            if model.WorkflowStep = Inspect && model.Registration.ReferenceMesh = Some mesh then
                { model with SurfaceDistance = Map.add mesh arr model.SurfaceDistance }
            else model
        | FocusDistComputed(mesh, arr) ->
            if model.WorkflowStep = Inspect then
                { model with FocusDist = Map.add mesh arr model.FocusDist }
            else model
        | ToggleExtrinsicZDiff ->
            // The difference values change with the sub-mode (M3C2 ↔ Δz) → refetch.
            invalidateFocusDist { model with ExtrinsicZDiff = not model.ExtrinsicZDiff }
        | SurfaceDistanceFailed(_, reason) ->
            showToast env "Surface-distance query failed — is the server up to date? (restart it)"
                { model with DebugLog = model.DebugLog.InsertAt(0, sprintf "region-distance failed: %s" reason) }

        | SceneBoundsLoaded bboxes ->
            if bboxes.Length = 0 then model
            else
                let union =
                    bboxes |> Array.fold (fun (acc : Box3d) (_, b) ->
                        Box3d(
                            V3d(min acc.Min.X b.Min.X, min acc.Min.Y b.Min.Y, min acc.Min.Z b.Min.Z),
                            V3d(max acc.Max.X b.Max.X, max acc.Max.Y b.Max.Y, max acc.Max.Z b.Max.Z)
                        )) Box3d.Invalid
                let padded = Box3d(union.Min - V3d.III, union.Max + V3d.III)
                let perMesh = bboxes |> Array.fold (fun m (n, b) -> Map.add n b m) Map.empty
                let m =
                    { model with
                        SceneBounds = padded
                        MeshBounds = perMesh }
                // Rest the camera on the first mesh's panorama centre (last load step,
                // so PanoCenters/centroids are in). One-shot per dataset load.
                env.Emit [CameraMessage (OrbitMessage.SetTargetCenter(true, AnimationKind.Tanh, ModelTransforms.firstPanoCenterRender m))
                          CameraMessage (OrbitMessage.SetTargetRadius(true, max 1.0 (padded.Size.Length * 0.6)))]
                m
        | DatasetsLoaded datasets ->
            { model with Datasets = datasets |> Array.toList }
        | SetActiveDataset dataset ->
            if model.ActiveDataset = Some dataset then model
            else
                { model with
                    ActiveDataset = Some dataset
                    ScanPins = ScanPinModel.initial
                    Selection = Selection.initial
                    MeshSolo = None
                    MeshBounds = Map.empty
                    ActivePickingLayer = None
                    SolvedTransforms = Map.empty
                    LoadTransforms = Map.empty
                    RegView = RegBefore
                    LocateBackup = None
                    BrushedSamples = Set.empty
                    Toast = None }
        | SetRenderingMode m ->
            { model with RenderingMode = m }
        | ToggleMeshSolo name ->
            // Isolation is an overlay (MeshVisibility.shown); probes cover every mesh
            // so the matrix cells stay stable. Re-clicking the active ◐ deactivates
            // and resets every visibility toggle to ON; clicking another ◐ retargets.
            match model.MeshSolo with
            | Some s when s = name -> exitSolo model
            | _ -> enterSolo name model
        | ToggleGearPopover ->
            { model with GearPopoverOpen = not model.GearPopoverOpen }
        | SetActivePickingLayer name ->
            { model with ActivePickingLayer = name }
        | ScanPinMsg (PlaceAnchor _ as msg) ->
            // A freshly placed pin is a registration pin immediately; seed it
            // against the reference (if any) so its markers appear at once.
            let model = ScanPinUpdate.handleMsg env model msg
            match model.Registration.ReferenceMesh, model.Selection.SelectedPin with
            | Some _, Some id -> seedAnchors env model [id]
            | _ -> model
        | ScanPinMsg msg ->
            let m = ScanPinUpdate.handleMsg env model msg
            // Inspect isolation swap: focusing a pin turns mesh isolation off (the
            // visibility toggles reset to ON) and pin isolation on; losing the pin
            // (deselect / delete) returns pin isolation to the Inspect default (off).
            let m =
                if m.WorkflowStep <> Inspect then m
                else
                    match msg with
                    | SelectPin (Some _) -> { exitSolo m with AnchorGhostMode = true }
                    | SelectPin None -> { m with AnchorGhostMode = false }
                    | DeletePin _ when m.Selection.SelectedPin.IsNone -> { m with AnchorGhostMode = false }
                    | _ -> m
            // Starting placement / switching / deleting a pin cancels the armed editor.
            match msg with
            | EnterAnchorPlacement | SelectPin _ | DeletePin _ ->
                { m with CorrArm = None; CorrPreview = None }
            | _ -> m
        | SetHovered h ->
            if model.Selection.Hovered = h then model
            else { model with Selection = { model.Selection with Hovered = h } }
        | SetWorkflowStep step ->
            // Entering/leaving Inspect (re)drives the central-3D variance map, so drop
            // its cache + bump the generation. Per-mode default (§C): pin isolation
            // defaults on in Correspondence, off elsewhere (the hold modifier overrides
            // momentarily where it's off). A workflow switch ends any mesh isolation
            // (exitSolo resets the visibility toggles to ON) and drops the locate
            // backup + the hover peek, both bound to the previous mode's view.
            if model.WorkflowStep = step then model
            else
                bumpSurfaceDist ()
                bumpFocusDist ()
                exitSolo
                    { model with WorkflowStep = step; SurfaceDistance = Map.empty; FocusDist = Map.empty
                                 AnchorGhostMode = (step = Correspondence)
                                 CorrArm = None; CorrPreview = None; BrushedSamples = Set.empty
                                 LocateBackup = None
                                 Selection = { model.Selection with Hovered = None } }
        | SetInspectChannel ch ->
            { model with InspectChannel = ch }
        | SetFocusProjection p ->
            { model with FocusProjection = p }
        | SetFocusedMesh None ->
            { model with Selection = { model.Selection with FocusedMesh = None }
                         CorrArm = None; CorrPreview = None }
        | SetFocusedMesh (Some m) ->
            // Promote to the large single (links rail + dock + focus enlargement).
            // Switching target cancels any in-progress set-correspondence (focus + 3D).
            // Selection never moves a camera — double-click (ZoomToMesh) does.
            let changed = model.Selection.FocusedMesh <> Some m
            let model =
                if changed then
                    { model with Selection = { model.Selection with FocusedMesh = Some m }
                                 CorrArm = None; CorrPreview = None }
                else model
            // A focused mesh must be visible — the single and the focus-head buttons
            // resolve against the raw toggles, so focusing a hidden mesh re-enables it.
            let model =
                if Map.tryFind m model.MeshVisible |> Option.defaultValue true then model
                else setMeshVisible (Map.add m true model.MeshVisible) model
            // Inspect focus policy (§C): focusing a moving mesh isolates it with the
            // reference (it paints its own difference/displacement field) and swaps
            // pin isolation off; focusing the reference returns to the ensemble view.
            if model.WorkflowStep = Inspect then
                if model.Registration.ReferenceMesh <> Some m then
                    { enterSolo m model with AnchorGhostMode = false }
                else exitSolo model
            else model
        | PickCorrespondenceAt(pinId, mesh, world) ->
            // Set the (pin, mesh) correspondence point at the picked surface point,
            // stored mesh-local via the displayed transform (so the before/after toggle
            // moves it). ROI-clamped (§T4 — no point outside the pin). Editing the
            // reference mesh moves its RefAnchor; any other mesh sets its anchor. The
            // editor STAYS ARMED (only the aim ghost clears) so picks can be refined.
            match HashMap.tryFind pinId model.ScanPins.Pins with
            | Some pin ->
                match ScanPin.correspondence pin with
                | Some _ ->
                    if (world - pin.Centre).Length > pin.InnerRadius then
                        showToast env "Pick inside the pin ROI" { model with CorrPreview = None }
                    else
                        let own = (ModelTransforms.displayedWorld model mesh).Backward.TransformPos world
                        let isRef = model.Registration.ReferenceMesh = Some mesh
                        let sp =
                            updateCorr pinId (fun corr ->
                                if isRef then
                                    { corr with RefAnchor = Some own; InRoi = Map.add mesh true corr.InRoi }
                                else
                                    { corr with
                                        Anchors = Map.add mesh { Point = own; Source = AnchorPick3D } corr.Anchors
                                        InRoi   = Map.add mesh true corr.InRoi }) model.ScanPins
                        { model with ScanPins = sp; CorrPreview = None }
                | None -> model
            | None -> model
        | ToggleCorrArm(pinId, mesh) ->
            // Arm/disarm the unified editor for (pin, mesh). Arming isolates the mesh
            // (via wheelIsolation reading CorrArm), brings the linked focus onto it,
            // selects the pin, and cancels pin placement. Re-issuing disarms.
            if model.CorrArm = Some(pinId, mesh) then
                { model with CorrArm = None; CorrPreview = None }
            else
                { model with
                    CorrArm = Some(pinId, mesh)
                    CorrPreview = None
                    ScanPins = { model.ScanPins with Placement = PlacementIdle }
                    Selection = { model.Selection with SelectedPin = Some pinId; FocusedMesh = Some mesh } }
        | CorrPreviewComputed p ->
            if model.CorrArm.IsSome then { model with CorrPreview = p } else model
        | SetBrushedSamples ids ->
            // Cap the brushed set so a wide brush can't flood the 3D marker node.
            let s = ids |> List.truncate 200 |> Set.ofList
            if model.BrushedSamples = s then model else { model with BrushedSamples = s }
        // Tight fly to a metric-world point: centre on it, set the orbit radius
        // directly (close-in), keeping orientation. The 3D side of the double-click zooms.
        | FlyToPoint(world, radius) ->
            let scale = DatasetScale.active model.ActiveDataset model.DatasetScales
            let centreR = ScanPin.renderCentre model.CommonCentroid scale world
            env.Emit [CameraMessage (OrbitMessage.SetTargetCenter(true, AnimationKind.Tanh, centreR))
                      CameraMessage (OrbitMessage.SetTargetRadius(true, max 0.2 (radius * scale)))]
            model
        // 3D framing conventions for the double-click zoom grammar (the 2D focus side
        // lives in the FocusScene.* helpers called at the same click sites).
        | ZoomToMesh m ->
            (match Map.tryFind m model.MeshBounds with
             | Some b when not b.IsInvalid -> env.Emit [FlyToPoint(b.Center, max 0.5 (b.Size.Length * 0.6))]
             | _ -> ())
            model
        | ZoomToPin id ->
            (match HashMap.tryFind id model.ScanPins.Pins with
             | Some p -> env.Emit [FlyToPoint(p.Centre, max 0.5 (p.InnerRadius * 4.0))]
             | None -> ())
            model
        // Locate a correspondence (atomic "frame"): solo the mesh, focus it, force Top
        // so the focus pan/zoom maths is valid. No camera motion — that is the cell's
        // double-click (FlyToPoint + FocusScene.zoomOnWorldRadius). A back-out
        // snapshot is captured on the first locate of a session.
        | FrameCorrespondence(pinId, mesh) ->
            match HashMap.tryFind pinId model.ScanPins.Pins with
            | Some pin ->
                // The reference's marker is its RefAnchor — locate works on it like on
                // any moving-mesh marker (both are own-frame points).
                let anchorOwn =
                    ScanPin.correspondence pin |> Option.bind (fun c ->
                        if model.Registration.ReferenceMesh = Some mesh then c.RefAnchor
                        else Map.tryFind mesh c.Anchors |> Option.map (fun a -> a.Point))
                match anchorOwn with
                | Some _ ->
                    let backup =
                        match model.LocateBackup with
                        | Some _ -> model.LocateBackup
                        | None ->
                            Some { PrevSolo = model.MeshSolo; PrevVisible = model.MeshVisible
                                   PrevCenter = model.Camera.center; PrevRadius = model.Camera.radius
                                   PrevPhi = model.Camera.phi; PrevTheta = model.Camera.theta }
                    // The located mesh must resolve in the focus single (raw toggles) —
                    // re-enable it if hidden; the backup above restores the prior map.
                    let model =
                        if Map.tryFind mesh model.MeshVisible |> Option.defaultValue true then model
                        else setMeshVisible (Map.add mesh true model.MeshVisible) model
                    enterSolo mesh
                        { model with
                            Selection = { model.Selection with FocusedMesh = Some mesh; SelectedPin = Some pinId }
                            LocateBackup = backup
                            FocusProjection = ProjTop
                            // An Inspect locate also lights the pin ROI (pin isolation
                            // on, like a pin click); Correspondence keeps its default.
                            AnchorGhostMode = (model.WorkflowStep = Inspect) || model.AnchorGhostMode
                            CorrArm = None; CorrPreview = None }
                | None -> showToast env "No marker on that mesh to locate" model
            | None -> model
        // Single back-out: restore the camera + solo/visibility captured at the first
        // locate and clear the backup (the focus pan/zoom is reset from the view).
        | BackOutLocate ->
            match model.LocateBackup with
            | None -> model
            | Some b ->
                env.Emit [CameraMessage (OrbitMessage.SetTarget(false, b.PrevCenter, b.PrevRadius, b.PrevPhi, b.PrevTheta))]
                setMeshVisible b.PrevVisible
                    { model with
                        MeshSolo = b.PrevSolo
                        LocateBackup = None }

    // All-meshes variance: per reference vertex, the std of each visible moving
    // mesh's signed distance (target = reference, ref = moving). Debounced via the
    // surface-distance generation/CTS.
    let private ensureVariance (env : Env<Message>) (model : Model) : Model =
        // The variance aggregate only paints in the no-solo ensemble — don't fetch
        // while a mesh is isolated (exitSolo resets the visibility map, which
        // invalidates and re-arms this fetch).
        if model.WorkflowStep <> Inspect || model.MeshSolo.IsSome then model
        else
            match model.Registration.ReferenceMesh with
            | Some refMesh
                  when not (Map.containsKey refMesh model.SurfaceDistance)
                       && surfaceDistReqGen <> surfaceDistGen ->
                let moving =
                    model.MeshNames |> IndexList.toList
                    |> List.filter (fun n -> n <> refMesh && (Map.tryFind n model.MeshVisible |> Option.defaultValue true))
                if List.length moving < 2 then model
                else
                    surfaceDistReqGen <- surfaceDistGen
                    let refT = (ModelTransforms.displayedWorld model refMesh).Forward
                    let jobs =
                        moving |> List.map (fun m ->
                            let mT = (ModelTransforms.displayedWorld model m).Forward
                            Query.regionDistance ApiConfig.apiBase.Value refMesh 0 m 0 refT mT 0)
                    surfaceDistCts.Cancel()
                    surfaceDistCts <- new System.Threading.CancellationTokenSource()
                    let token = surfaceDistCts.Token
                    task {
                        try
                            do! System.Threading.Tasks.Task.Delay(150, token)
                            let! results = jobs |> Async.Parallel |> Async.StartAsTask
                            if not token.IsCancellationRequested && results.Length >= 2 then
                                let n = results.[0].Length
                                let outv = Array.zeroCreate<float32> n
                                for i in 0 .. n - 1 do
                                    let mutable sum = 0.0
                                    let mutable sum2 = 0.0
                                    let mutable cnt = 0
                                    for r in results do
                                        if i < r.Length then
                                            let v = float r.[i]
                                            if abs v < 1e20 then
                                                sum  <- sum + v
                                                sum2 <- sum2 + v * v
                                                cnt  <- cnt + 1
                                    if cnt >= 2 then
                                        let mean = sum / float cnt
                                        outv.[i] <- float32 (sqrt (max 0.0 (sum2 / float cnt - mean * mean)))
                                    else outv.[i] <- 1e30f
                                env.Emit [VarianceComputed(refMesh, outv)]
                        with
                        | :? System.OperationCanceledException -> ()
                        | ex ->
                            if not token.IsCancellationRequested then
                                env.Emit [SurfaceDistanceFailed(refMesh, ex.Message)]
                    } |> ignore
                    model
            | _ -> model

    // Inspect Difference channel: per visible moving mesh, fetch its signed distance
    // to the reference (the mesh's own served vertex order) for the focus heatmap.
    // Same generation-guarded debounce as ensureVariance.
    let private ensureFocusDist (env : Env<Message>) (model : Model) : Model =
        if model.WorkflowStep <> Inspect || model.InspectChannel <> ChDifference then model
        else
            match model.Registration.ReferenceMesh with
            | None -> model
            | Some refMesh ->
                // Shown moving meshes (isolation-aware): under solo only the isolated
                // mesh needs its field — and it needs it even if its raw toggle is off.
                let moving =
                    model.MeshNames |> IndexList.toList
                    |> List.filter (fun n ->
                        n <> refMesh && MeshVisibility.shown model.MeshSolo model.MeshVisible n)
                let missing = moving |> List.filter (fun m -> not (Map.containsKey m model.FocusDist))
                if List.isEmpty missing || focusDistReqGen = focusDistGen then model
                else
                    focusDistReqGen <- focusDistGen
                    let mode = if model.ExtrinsicZDiff then 1 else 0
                    let refT = (ModelTransforms.displayedWorld model refMesh).Forward
                    let jobs =
                        missing |> List.map (fun m ->
                            let mT = (ModelTransforms.displayedWorld model m).Forward
                            async {
                                try
                                    let! d = Query.regionDistance ApiConfig.apiBase.Value m 0 refMesh 0 mT refT mode
                                    return Some (m, d)
                                with _ -> return None
                            })
                    focusDistCts.Cancel()
                    focusDistCts <- new System.Threading.CancellationTokenSource()
                    let token = focusDistCts.Token
                    task {
                        try
                            do! System.Threading.Tasks.Task.Delay(150, token)
                            let! results = jobs |> Async.Parallel |> Async.StartAsTask
                            if not token.IsCancellationRequested then
                                for r in results do
                                    match r with Some (m, d) -> env.Emit [FocusDistComputed(m, d)] | None -> ()
                        with
                        | :? System.OperationCanceledException -> ()
                        | _ -> ()
                    } |> ignore
                    model

    let update (env : Env<Message>) (model : Model) (msg : Message) =
        updateCore env model msg
        |> ScanPinUpdate.ensureProbe env
        |> ScanPinUpdate.ensureSlices env
        |> ScanPinUpdate.ensureRings env
        |> ensureVariance env
        |> ensureFocusDist env

