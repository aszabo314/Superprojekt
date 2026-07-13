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
                SolveInputs      = None
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
        | SetShowOverlays held ->
            if model.ShowOverlaysHeld = held then model
            else { model with ShowOverlaysHeld = held }
        | SetRegView v ->
            // Only meaningful once a solve exists (the view disables it otherwise).
            // The pose-baked pair caches swap in place (UpdateHelpers.applyRegView).
            // Switching the view also cancels an armed correspondence editor —
            // editing is Before-only, so an editor armed for the other view is moot.
            if model.RegView = v || Map.isEmpty model.SolvedTransforms then model
            else { applyRegView v model with CorrArm = None; CorrPreview = None }
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
                        SolveInputs = None
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
                    // Record the exact correspondence data this solve consumes —
                    // the validity postlude clears the registration if any of it
                    // is later deleted or moved.
                    let snapshot =
                        let pins =
                            (Map.empty, solvable) ||> List.fold (fun acc mesh ->
                                (acc, pairsFor mesh) ||> Array.fold (fun acc (pinId, ra, mp) ->
                                    let _, meshPts = acc |> Map.tryFind pinId |> Option.defaultValue (ra, Map.empty)
                                    Map.add pinId (ra, Map.add mesh mp meshPts) acc))
                        { RefMesh = refMesh; Pins = pins }
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
                    showToast env summary { model with SolveInputs = Some snapshot }
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
            // Record ROI membership and drop the stale auto marker of every
            // re-evaluated (pin, mesh) — the seeded fold below re-adds the accepted
            // ones, so an auto anchor that no longer qualifies (out of the pin
            // sphere, even if still within measurement reach) cannot linger. Manual
            // picks are never touched here.
            let sp =
                inRoi |> Array.fold (fun sp (pinId, mesh, inside) ->
                    updateCorr pinId (fun c ->
                        let anchors =
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
        | SetShapeThreshold v ->
            { model with ShapeThreshold = clamp 0.0 1.0 v }
        | VarianceComputed(mesh, arr) ->
            // Keep only if still in Inspect and this is the reference mesh.
            if model.WorkflowStep = Inspect && model.Registration.ReferenceMesh = Some mesh then
                { model with SurfaceDistance = Map.add mesh arr model.SurfaceDistance }
            else model
        | VarianceOtherComputed(mesh, arr) ->
            if model.WorkflowStep = Inspect && model.Registration.ReferenceMesh = Some mesh then
                { model with SurfaceDistanceOther = Map.add mesh arr model.SurfaceDistanceOther }
            else model
        | FocusDistComputed(mesh, arr) ->
            if model.WorkflowStep = Inspect then
                { model with FocusDist = Map.add mesh arr model.FocusDist }
            else model
        | FocusDistOtherComputed(mesh, arr) ->
            if model.WorkflowStep = Inspect then
                { model with FocusDistOther = Map.add mesh arr model.FocusDistOther }
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
                // Rest the camera on the default reference mesh (last load step, so
                // PanoCenters/centroids are in): its panorama centre, framed to its own
                // bounds rather than the whole scene. One-shot per dataset load.
                let center, radius =
                    match m.Registration.ReferenceMesh |> Option.bind (fun r -> Map.tryFind r perMesh |> Option.map (fun b -> r, b)) with
                    | Some (r, b) ->
                        let scale = DatasetScale.forMesh m.DatasetScales r
                        ModelTransforms.panoCenterRender m r, max 1.0 (b.Size.Length * scale * 0.6)
                    | None ->
                        ModelTransforms.firstPanoCenterRender m, max 1.0 (padded.Size.Length * 0.6)
                env.Emit [CameraMessage (OrbitMessage.SetTargetCenter(true, AnimationKind.Tanh, center))
                          CameraMessage (OrbitMessage.SetTargetRadius(true, radius))]
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
                    SolveInputs = None
                    LoadTransforms = Map.empty
                    RegView = RegBefore
                    LocateBackup = None
                    BrushedSamples = Set.empty
                    Toast = None }
        | SetRenderingMode m ->
            { model with RenderingMode = m }
        | ToggleGearPopover ->
            { model with GearPopoverOpen = not model.GearPopoverOpen }
        | SetActivePickingLayer name ->
            { model with ActivePickingLayer = name }
        | ScanPinMsg (PlaceAnchor _ as msg) ->
            // A freshly placed pin is a registration pin immediately; seed it
            // against the reference (if any) so its markers appear at once. Pins
            // and their correspondences exist in the BEFORE state — snap the view
            // back in case it was toggled to After mid-placement (seeding itself
            // always evaluates at the Before pose regardless).
            let model = applyRegView RegBefore model
            let model = ScanPinUpdate.handleMsg env model msg
            match model.Registration.ReferenceMesh, Selection.pin model.Selection.Active with
            | Some _, Some id -> seedAnchors env model [id]
            | _ -> model
        | ScanPinMsg msg ->
            // Correspondence/pin edits are Before-only: starting a placement or
            // resizing a pin snaps the committed view back to Before first.
            let model =
                match msg with
                | EnterAnchorPlacement | SetInnerRadius _ -> applyRegView RegBefore model
                | _ -> model
            let m = ScanPinUpdate.handleMsg env model msg
            // Inspect: losing the selected pin with its deletion returns pin
            // isolation to the Inspect default (off).
            let m =
                if m.WorkflowStep <> Inspect then m
                else
                    match msg with
                    | DeletePin _ when (Selection.pin m.Selection.Active).IsNone -> { m with AnchorGhostMode = false }
                    | _ -> m
            // Starting placement / deleting a pin cancels the armed editor.
            match msg with
            | EnterAnchorPlacement | DeletePin _ ->
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
                    { model with WorkflowStep = step
                                 SurfaceDistance = Map.empty; SurfaceDistanceOther = Map.empty
                                 FocusDist = Map.empty; FocusDistOther = Map.empty
                                 AnchorGhostMode = (step = Correspondence)
                                 CorrArm = None; CorrPreview = None; BrushedSamples = Set.empty
                                 LocateBackup = None
                                 Selection = { model.Selection with Hovered = None } }
        | SetInspectChannel ch ->
            { model with InspectChannel = ch }
        | SetFocusProjection p ->
            { model with FocusProjection = p }
        | SetSelection selRaw ->
            // Dangling-pin guard: a stale click can outlive its pin.
            let sel =
                match selRaw with
                | SelPin p when not (HashMap.containsKey p model.ScanPins.Pins) -> SelNone
                | SelCell (p, _) when not (HashMap.containsKey p model.ScanPins.Pins) -> SelNone
                | s -> s
            if model.Selection.Active = sel then model
            else
                // Switching the selection cancels any in-progress correspondence edit.
                let model =
                    { model with Selection = { model.Selection with Active = sel }
                                 CorrArm = None; CorrPreview = None }
                // A selected mesh must be visible — the focus single resolves against
                // the raw toggles, so selecting a hidden mesh re-enables it.
                let ensureVisible m model =
                    if Map.tryFind m model.MeshVisible |> Option.defaultValue true then model
                    else setMeshVisible (Map.add m true model.MeshVisible) model
                match sel with
                | SelNone ->
                    if model.WorkflowStep = Inspect then { exitSolo model with AnchorGhostMode = false }
                    else model
                | SelMesh m ->
                    let model = ensureVisible m model
                    // Inspect focus policy (§C): a moving mesh isolates with the
                    // reference (it paints its own difference/displacement field) and
                    // swaps pin isolation off; the reference returns to the ensemble.
                    if model.WorkflowStep = Inspect then
                        if model.Registration.ReferenceMesh <> Some m then
                            { enterSolo m model with AnchorGhostMode = false }
                        else exitSolo model
                    else model
                | SelPin _ ->
                    // Inspect: pin focus swaps mesh isolation off, pin isolation on.
                    if model.WorkflowStep = Inspect then { exitSolo model with AnchorGhostMode = true }
                    else model
                | SelCell (_, mesh) ->
                    // The locate: solo the mesh (backup-captured for a single
                    // BackOutLocate), force Top so the focus framing maths is valid;
                    // an Inspect locate also lights the pin ROI. No camera — the 3D
                    // zoom stays the cell's double-click.
                    let backup =
                        match model.LocateBackup with
                        | Some _ -> model.LocateBackup
                        | None ->
                            Some { PrevSolo = model.MeshSolo; PrevVisible = model.MeshVisible
                                   PrevCenter = model.Camera.center; PrevRadius = model.Camera.radius
                                   PrevPhi = model.Camera.phi; PrevTheta = model.Camera.theta }
                    let model = ensureVisible mesh model
                    enterSolo mesh
                        { model with
                            LocateBackup = backup
                            FocusProjection = ProjTop
                            AnchorGhostMode = (model.WorkflowStep = Inspect) || model.AnchorGhostMode }
        | PickCorrespondenceAt(pinId, mesh, world) ->
            // Set the (pin, mesh) correspondence point at the picked surface point,
            // stored mesh-local via the displayed transform (so the before/after toggle
            // moves it). BEFORE-ONLY: correspondences are edited in the Before state
            // exclusively — a pick against the solved pose would store a point whose
            // Before position is off-surface/outside the pin. The entry points force
            // Before (arm button, placement); this is the safety net for a view
            // toggled mid-edit. ROI-clamped (§T4 — no point outside the pin sphere).
            // Editing the reference mesh moves its RefAnchor; any other mesh sets its
            // anchor. A committed pick DISARMS the editor (one click = one edit); an
            // out-of-ROI click keeps it armed so the toast's "try again" needs no re-arm.
            // The Peek hold counts as After too: the raycast hits the peeked (solved)
            // geometry, so a commit would store an off-surface Before point.
            if model.RegView = RegAfter
               || (model.RegPeekHeld && not (Map.isEmpty model.SolvedTransforms)) then
                showToast env "Correspondences are edited in the Before state — switch the view" model
            else
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
                        { model with ScanPins = sp; CorrArm = None; CorrPreview = None }
                | None -> model
            | None -> model
        | ToggleCorrArm(pinId, mesh) ->
            // Arm/disarm the unified editor for (pin, mesh). Arming snaps the view to
            // BEFORE (correspondences are edited in that state only), isolates the
            // mesh (via wheelIsolation reading CorrArm), brings the linked focus onto
            // it, selects the pin, and cancels pin placement. Re-issuing disarms.
            if model.CorrArm = Some(pinId, mesh) then
                { model with CorrArm = None; CorrPreview = None }
            else
                let model = applyRegView RegBefore model
                { model with
                    CorrArm = Some(pinId, mesh)
                    CorrPreview = None
                    ScanPins = { model.ScanPins with Placement = PlacementIdle }
                    Selection = { model.Selection with Active = SelCell(pinId, mesh) } }
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
        // Single back-out: restore the camera + solo/visibility captured at the first
        // cell locate and clear the backup.
        | BackOutLocate ->
            match model.LocateBackup with
            | None -> model
            | Some b ->
                env.Emit [CameraMessage (OrbitMessage.SetTarget(false, b.PrevCenter, b.PrevRadius, b.PrevPhi, b.PrevTheta))]
                setMeshVisible b.PrevVisible
                    { model with
                        MeshSolo = b.PrevSolo
                        LocateBackup = None }

    // Registration provenance: a solve is only as valid as the correspondences it
    // consumed (SolveInputs). If any tracked pin was deleted, or any tracked
    // refAnchor/anchor point no longer matches (moved by a pick, killed by a pin
    // resize), the solved poses are stale — clear them and snap back to Before.
    // Values are compared exactly: untouched anchors are structurally copied
    // through updates, so only a real edit differs.
    let private ensureSolveValidity (env : Env<Message>) (model : Model) : Model =
        if Map.isEmpty model.SolvedTransforms then model
        else
            match model.SolveInputs with
            | None -> model
            | Some s ->
                let intact =
                    model.Registration.ReferenceMesh = Some s.RefMesh
                    && s.Pins |> Map.forall (fun pinId (ra, meshPts) ->
                        match HashMap.tryFind pinId model.ScanPins.Pins with
                        | None -> false
                        | Some pin ->
                            match ScanPin.correspondence pin with
                            | None -> false
                            | Some c ->
                                c.RefAnchor = Some ra
                                && meshPts |> Map.forall (fun mesh pt ->
                                    match Map.tryFind mesh c.Anchors with
                                    | Some a -> a.Point = pt
                                    | None -> false))
                if intact then model
                else
                    showToast env "Registration cleared — its correspondences changed"
                        (invalidateRings (invalidateProbes
                            { model with
                                SolvedTransforms = Map.empty
                                SolveInputs = None
                                RegView = RegBefore
                                BrushedSamples = Set.empty }))

    // All-meshes variance: per reference vertex, the std of each visible moving
    // mesh's signed distance (target = reference, ref = moving). Debounced via the
    // surface-distance generation/CTS. Both Before/After poses are cached (Other is
    // fetched only once a solve exists) so the reg toggle/peek never repaints stale
    // values — the committed pose lands first, the opposite pose follows.
    let private ensureVariance (env : Env<Message>) (model : Model) : Model =
        // The variance aggregate only paints in the no-solo ensemble — don't fetch
        // while a mesh is isolated (exitSolo resets the visibility map, which
        // invalidates and re-arms this fetch).
        if model.WorkflowStep <> Inspect || model.MeshSolo.IsSome then model
        else
            match model.Registration.ReferenceMesh with
            | Some refMesh when surfaceDistReqGen <> surfaceDistGen ->
                let needMain = not (Map.containsKey refMesh model.SurfaceDistance)
                let needOther =
                    not (Map.isEmpty model.SolvedTransforms)
                    && not (Map.containsKey refMesh model.SurfaceDistanceOther)
                let moving =
                    model.MeshNames |> IndexList.toList
                    |> List.filter (fun n -> n <> refMesh && (Map.tryFind n model.MeshVisible |> Option.defaultValue true))
                if (not needMain && not needOther) || List.length moving < 2 then model
                else
                    surfaceDistReqGen <- surfaceDistGen
                    let jobsAt view =
                        let refT = (ModelTransforms.displayedWorldAt view model refMesh).Forward
                        moving |> List.map (fun m ->
                            let mT = (ModelTransforms.displayedWorldAt view model m).Forward
                            Query.regionDistance ApiConfig.apiBase.Value refMesh 0 m 0 refT mT 0)
                    let otherView = match model.RegView with RegBefore -> RegAfter | RegAfter -> RegBefore
                    let mainJobs  = if needMain then Some (jobsAt model.RegView) else None
                    let otherJobs = if needOther then Some (jobsAt otherView) else None
                    let aggregate (results : float32[][]) =
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
                        outv
                    surfaceDistCts.Cancel()
                    surfaceDistCts <- new System.Threading.CancellationTokenSource()
                    let token = surfaceDistCts.Token
                    task {
                        try
                            do! System.Threading.Tasks.Task.Delay(150, token)
                            match mainJobs with
                            | Some jobs ->
                                let! results = jobs |> Async.Parallel |> Async.StartAsTask
                                if not token.IsCancellationRequested && results.Length >= 2 then
                                    env.Emit [VarianceComputed(refMesh, aggregate results)]
                            | None -> ()
                            match otherJobs with
                            | Some jobs ->
                                let! results = jobs |> Async.Parallel |> Async.StartAsTask
                                if not token.IsCancellationRequested && results.Length >= 2 then
                                    env.Emit [VarianceOtherComputed(refMesh, aggregate results)]
                            | None -> ()
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
    // Same generation-guarded debounce as ensureVariance, same per-pose pairing:
    // the Other pose is fetched only once a solve exists, in the same batch.
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
                let otherView = match model.RegView with RegBefore -> RegAfter | RegAfter -> RegBefore
                let solved = not (Map.isEmpty model.SolvedTransforms)
                let wanted =
                    [ for m in moving do
                        if not (Map.containsKey m model.FocusDist) then yield m, model.RegView, false
                        if solved && not (Map.containsKey m model.FocusDistOther) then yield m, otherView, true ]
                if List.isEmpty wanted || focusDistReqGen = focusDistGen then model
                else
                    focusDistReqGen <- focusDistGen
                    let mode = if model.ExtrinsicZDiff then 1 else 0
                    let jobs =
                        wanted |> List.map (fun (m, view, isOther) ->
                            let refT = (ModelTransforms.displayedWorldAt view model refMesh).Forward
                            let mT = (ModelTransforms.displayedWorldAt view model m).Forward
                            async {
                                try
                                    let! d = Query.regionDistance ApiConfig.apiBase.Value m 0 refMesh 0 mT refT mode
                                    return Some (m, d, isOther)
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
                                    match r with
                                    | Some (m, d, false) -> env.Emit [FocusDistComputed(m, d)]
                                    | Some (m, d, true) -> env.Emit [FocusDistOtherComputed(m, d)]
                                    | None -> ()
                        with
                        | :? System.OperationCanceledException -> ()
                        | _ -> ()
                    } |> ignore
                    model

    let update (env : Env<Message>) (model : Model) (msg : Message) =
        updateCore env model msg
        |> ensureSolveValidity env
        |> ScanPinUpdate.ensureProbe env
        |> ScanPinUpdate.ensureSlices env
        |> ScanPinUpdate.ensureRings env
        |> ensureVariance env
        |> ensureFocusDist env

