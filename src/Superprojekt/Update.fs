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
            let indices = centroids |> Array.mapi (fun i (n,_) -> n,i) |> HashMap.ofArray
            // Identity: meshes load unregistered.
            let loadTransforms = centroids |> Array.fold (fun m (n, _) -> Map.add n Trafo3d.Identity m) Map.empty
            let dataset =
                if centroids.Length > 0 then
                    let n = fst centroids.[0] in let s = n.IndexOf('/') in if s >= 0 then n.[..s-1] else ""
                else ""
            { model with
                MeshNames        = names
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
                ReferenceMesh    = (if centroids.Length > 0 then Some (fst centroids.[0]) else None)
                DatasetCentroids =
                    // Fresh map — entries never accumulate across dataset switches.
                    let perMesh = centroids |> Array.fold (fun m (n, c) -> Map.add n c m) Map.empty
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
            { model with GhostOpacity = clamp 0.0 1.0 v }
        | SetShadingStrength v ->
            { model with ShadingStrength = clamp 0.0 1.0 v }
        | SetSlopeThresholdDeg v ->
            { model with SlopeThresholdDeg = clamp 1.0 89.0 v }
        | SetOutlineThreshold v ->
            // Floor = the Rgba8 G-buffer quantization step (~0.004); below it the
            // staircase risers of a smooth slope read as false outline bands.
            { model with OutlineThreshold = clamp 0.0001 0.01 v }
        | SetOutlineWidth v ->
            { model with OutlineWidthPx = clamp 1.0 8.0 v }
        | SetIsolineBands v ->
            { model with IsolineBands = max 1.0 v }
        | SetIsolineOpacity v ->
            { model with IsolineOpacity = clamp 0.0 1.0 v }
        | ToggleAnchorGhostMode ->
            { model with AnchorGhostMode = not model.AnchorGhostMode }
        | SetQuickPinRadius v ->
            { model with QuickPinRadius = max 0.01 v }
        | SetFlagScale v ->
            { model with FlagScale = clamp 0.1 10.0 v }
        | SetBrushDotPx v ->
            { model with BrushDotPx = clamp 4.0 60.0 v }
        // Geometry knobs invalidate the slice caches (the ensureSlices postlude
        // refetches); the percentile is view-only.
        | SetSliceNSamples v ->
            { model with SliceNSamples = max 1.0 v; ScanPins = ScanPinModel.invalidateSlices model.ScanPins }
        | SetSliceContextCount v ->
            { model with SliceContextCount = clamp 0.0 4.0 (round v); ScanPins = ScanPinModel.invalidateSlices model.ScanPins }
        | SetSliceContextSpacing v ->
            { model with SliceContextSpacing = clamp 0.02 0.5 v; ScanPins = ScanPinModel.invalidateSlices model.ScanPins }
        | SetSliceVertPercentile v ->
            { model with SliceVertPercentile = clamp 0.5 1.0 v }
        | SetRegView v ->
            // Editing is Before-only — switching the view cancels an armed
            // correspondence editor.
            if model.RegView = v || Map.isEmpty model.SolvedTransforms then model
            else { applyRegView v model with CorrArm = None; CorrPreview = None }
        | SetRegPeek held ->
            // Purely visual (the displayed transform flips); no probe/ring invalidation.
            // Solved-gate lives HERE so every entry path (button, hotkey I) behaves alike.
            if model.RegPeekHeld = held || (held && Map.isEmpty model.SolvedTransforms) then model
            else { model with RegPeekHeld = held }
        | SetReferenceMesh mesh when model.ReferenceMesh = mesh ->
            model    // idempotent: re-setting the same reference must not wipe a solve
        | SetReferenceMesh mesh ->
            // Reference change invalidates any solve (it was relative to the old reference).
            bumpSolveGen ()
            let hadSolve = not (Map.isEmpty model.SolvedTransforms)
            let model =
                invalidateProbes (invalidateRings
                    { model with
                        ReferenceMesh = mesh
                        SolvedTransforms = Map.empty
                        SolveInputs = None
                        RegView = RegBefore
                        CorrArm = None; CorrPreview = None })
            let model =
                match mesh with
                | Some _ -> seedAnchors env model (allPinIds model)
                | None -> model
            // Invalidation → Before is the one automatic pose transition; make it explicit.
            if hadSolve then showToast env "Registration cleared — the reference changed" model
            else model

        // Anchors are mesh-local, so the pairs are taken at the load pose — the
        // solve yields an absolute transform a re-solve replaces wholesale.
        | SolveCoarse ->
            // Any other correspondence action cancels a live 3D pick.
            let model = { model with CorrArm = None; CorrPreview = None }
            match model.ReferenceMesh with
            | None -> showToast env "Designate a reference mesh (★) first" model
            | Some refMesh ->
                let moving =
                    model.MeshNames |> IndexList.toList |> List.filter (fun n -> n <> refMesh)
                let enabledPins =
                    model.ScanPins.Pins |> HashMap.toList
                    |> List.choose (fun (_, p) ->
                        let c = ScanPin.correspondence p
                        c.RefAnchor |> Option.map (fun ra -> p.Id, ra, c.Anchors))
                let pairsFor mesh =
                    enabledPins
                    |> List.choose (fun (pinId, ra, anchors) ->
                        match Map.tryFind mesh anchors with
                        | Some a -> Some (pinId, ra, a.Point)
                        | None -> None)
                    |> Array.ofList
                let solvable = moving |> List.filter (fun m -> (pairsFor m).Length >= 3)
                if List.isEmpty solvable then
                    showToast env "No mesh has ≥3 correspondence markers yet" model
                else
                    // Unsolvable meshes keep no SolvedTransform (stay at their load
                    // pose). Results land as ONE CoarseSolved batch, stamped with
                    // the generation this solve was issued under.
                    bumpSolveGen ()
                    let gen = solveGen
                    task {
                        let! results =
                            solvable
                            |> List.map (fun mesh -> async {
                                // (refAnchor world, moving anchor at load pose = own-frame point, weight 1).
                                let queryPairs = pairsFor mesh |> Array.map (fun (_, ra, mp) -> ra, mp, 1.0)
                                try
                                    let! world = Query.lsqPairs ApiConfig.apiBase.Value mesh queryPairs
                                    return Choice1Of2 (mesh, world)
                                with ex ->
                                    return Choice2Of2 (mesh, ex.Message) })
                            |> Async.Parallel
                            |> Async.StartAsTask
                        let solved = results |> Array.choose (function Choice1Of2 r -> Some r | _ -> None)
                        let failed = results |> Array.choose (function Choice2Of2 r -> Some r | _ -> None)
                        env.Emit [CoarseSolved(gen, solved, failed)]
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
                    let total = List.length moving
                    let unsolvable = moving |> List.filter (fun m -> (pairsFor m).Length < 3)
                    let summary =
                        match unsolvable with
                        | [] -> sprintf "Solving %d of %d meshes…" n total
                        | first :: rest ->
                            let need = max 1 (3 - (pairsFor first).Length)
                            let extra = if List.isEmpty rest then "" else sprintf " (+%d more)" (List.length rest)
                            sprintf "Solving %d of %d; %s needs %d more%s"
                                n total (Primitives.shortName first) need extra
                    showToast env summary { model with SolveInputs = Some snapshot }
        | CoarseSolved(gen, solved, failed) ->
            let model =
                (model, failed) ||> Array.fold (fun m (mesh, reason) ->
                    { m with DebugLog = m.DebugLog.InsertAt(0, sprintf "coarse solve failed (%s): %s" mesh reason) })
            if gen <> solveGen then model    // registration cleared or re-solved while in flight
            else
                let model =
                    match failed with
                    | [||] -> model
                    | _ -> showToast env (sprintf "Solve failed (%s)" (failed |> Array.map (fst >> Primitives.shortName) |> String.concat ", ")) model
                if Array.isEmpty solved then model
                else
                    // lsqPairs returns the absolute world transform mapping the
                    // load-pose moving anchors onto the reference.
                    let st =
                        (model.SolvedTransforms, solved) ||> Array.fold (fun st (mesh, world) ->
                            let scale = DatasetScale.forMesh model.DatasetScales mesh
                            Map.add mesh (RigidTransform.worldToRender scale model.CommonCentroid (Trafo3d(world, world.Inverse))) st)
                    invalidateRings (invalidateProbes
                        { model with
                            SolvedTransforms = st
                            RegView = RegAfter })

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
        | ShowToast s ->
            showToast env s model
        | ClearToast ->
            if model.Toast.IsNone then model else { model with Toast = None }

        | SetMeshHeatmap(mesh, m) ->
            // Store HeatOff as removal so the map stays sparse (default lookup = off).
            let mh = if m = HeatOff then Map.remove mesh model.MeshHeatmap else Map.add mesh m model.MeshHeatmap
            { model with MeshHeatmap = mh }
        | SetShapeThreshold v ->
            { model with ShapeThreshold = clamp 0.0 1.0 v }
        | FocusDistComputed(gen, mesh, arr) ->
            if gen = focusDistGen && model.WorkflowStep = Inspect then
                { model with FocusDist = Map.add mesh arr model.FocusDist }
            else model
        | FocusDistOtherComputed(gen, mesh, arr) ->
            if gen = focusDistGen && model.WorkflowStep = Inspect then
                { model with FocusDistOther = Map.add mesh arr model.FocusDistOther }
            else model
        | SceneBoundsLoaded bboxes ->
            if bboxes.Length = 0 then model
            else
                let union =
                    bboxes |> Array.fold (fun (acc : Box3d) (_, b, _) -> acc.ExtendedBy b) Box3d.Invalid
                let padded = Box3d(union.Min - V3d.III, union.Max + V3d.III)
                let perMesh = bboxes |> Array.fold (fun m (n, b, _) -> Map.add n b m) Map.empty
                let spacing = bboxes |> Array.fold (fun m (n, _, s) -> if s > 0.0 then Map.add n s m else m) Map.empty
                let m =
                    { model with
                        SceneBounds = padded
                        MeshBounds = perMesh
                        MeshSpacing = spacing }
                // Rest the camera on the default reference mesh (last load step, so
                // PanoCenters/centroids are in): its panorama centre, framed to its own
                // bounds rather than the whole scene. One-shot per dataset load.
                let center, radius =
                    match m.ReferenceMesh |> Option.bind (fun r -> Map.tryFind r perMesh |> Option.map (fun b -> r, b)) with
                    | Some (r, b) ->
                        let scale = DatasetScale.forMesh m.DatasetScales r
                        ModelTransforms.panoCenterRender m r, max 1.0 (b.Size.Length * scale * 0.6)
                    | None ->
                        ModelTransforms.firstPanoCenterRender m, max 1.0 (padded.Size.Length * 0.6)
                env.Emit [CameraMessage (OrbitMessage.SetTargetCenter(AnimationKind.Tanh, center))
                          CameraMessage (OrbitMessage.SetTargetRadius(radius))]
                m
        | DatasetsLoaded datasets ->
            { model with Datasets = datasets |> Array.toList }
        | SetActiveDataset dataset ->
            if model.ActiveDataset = Some dataset then model
            else
                // Everything keyed by the old dataset's meshes/pins must go, and the
                // scalar-map generations bump so old-dataset fetches land dead.
                bumpSolveGen ()
                bumpFocusDist ()
                { model with
                    ActiveDataset = Some dataset
                    ScanPins = ScanPinModel.initial
                    Selection = Selection.initial
                    MeshSolo = None
                    MeshBounds = Map.empty
                    MeshSpacing = Map.empty
                    SolvedTransforms = Map.empty
                    SolveInputs = None
                    LoadTransforms = Map.empty
                    RegView = RegBefore
                    LocateBackup = None
                    BrushedSamples = Set.empty
                    PointProbe = None
                    CorrArm = None
                    CorrPreview = None
                    FocusDist = Map.empty
                    FocusDistOther = Map.empty
                    MeshHeatmap = Map.empty
                    ReferenceMesh = None
                    Toast = None }
        | SetRenderingMode m ->
            { model with RenderingMode = m }
        | ToggleGearPopover ->
            { model with GearPopoverOpen = not model.GearPopoverOpen }
        | ScanPinMsg (PlaceAnchor _ as msg) ->
            // A freshly placed pin is a registration pin immediately; seed it
            // against the reference (if any) so its markers appear at once. Pins
            // and their correspondences exist in the BEFORE state — refused in
            // After (never a silent view switch; arming is refused there too, this
            // is the safety net for a view toggled mid-placement).
            if model.RegView = RegAfter then
                showToast env "Correspondences are edited in the Before state — switch the view" model
            else
                let model = ScanPinUpdate.handleMsg env model msg
                match model.ReferenceMesh, Selection.pin model.Selection.Active with
                | Some _, Some id -> seedAnchors env model [id]
                | _ -> model
        | ScanPinMsg msg ->
            // Correspondence/pin edits are Before-only: starting a placement or
            // resizing a pin in the After view is refused with a prompt to switch —
            // the displayed pose never changes as a side effect.
            match msg with
            | (EnterAnchorPlacement | SetInnerRadius _) when model.RegView = RegAfter ->
                showToast env "Correspondences are edited in the Before state — switch the view" model
            | _ ->
                let m = ScanPinUpdate.handleMsg env model msg
                match msg with
                | EnterAnchorPlacement | DeletePin _ ->
                    { m with CorrArm = None; CorrPreview = None }
                // Probe/slice failures are otherwise invisible (the pin just stays
                // blank until an unrelated invalidation) — surface them.
                | ProbeFailed(_, e) | ProbeOtherFailed(_, e) | SliceFailed(_, e) | SliceOtherFailed(_, e) ->
                    showToast env "Pin measurement failed — see debug log (⚙)"
                        { m with DebugLog = m.DebugLog.InsertAt(0, sprintf "pin query failed: %s" e) }
                | _ -> m
        | SetHovered h ->
            if model.Selection.Hovered = h then model
            else { model with Selection = { model.Selection with Hovered = h } }
        | SetWorkflowStep step ->
            // Entering/leaving Inspect (re)drives the difference maps, so drop
            // their cache + bump the generation. Per-mode default: pin isolation
            // defaults on in Correspondence, off elsewhere (the hold modifier overrides
            // momentarily where it's off). A workflow switch ends any mesh isolation
            // and drops the locate backup + the hover peek, both bound to the
            // previous mode's view.
            if model.WorkflowStep = step then model
            else
                bumpFocusDist ()
                exitSolo
                    { model with WorkflowStep = step
                                 FocusDist = Map.empty; FocusDistOther = Map.empty
                                 PointProbe = None
                                 AnchorGhostMode = (step = Correspondence)
                                 CorrArm = None; CorrPreview = None; BrushedSamples = Set.empty
                                 LocateBackup = None
                                 Selection = { model.Selection with Hovered = None } }
        | SetNearCut v ->
            { model with NearCutFrac = clamp 0.0 1.25 v }
        | SetSelection selRaw ->
            // Dangling-pin guard: a stale click can outlive its pin.
            let sel =
                // Same degradation as pin deletion: SelCell → its mesh, SelPin → none.
                match selRaw with
                | SelPin p when not (HashMap.containsKey p model.ScanPins.Pins) -> SelNone
                | SelCell (p, m) when not (HashMap.containsKey p model.ScanPins.Pins) -> SelMesh m
                | s -> s
            if model.Selection.Active = sel then model
            else
                let model =
                    { model with Selection = { model.Selection with Active = sel }
                                 CorrArm = None; CorrPreview = None }
                // Pin isolation (AnchorGhostMode) is Register-exclusive: the mode
                // default (SetWorkflowStep — on in Correspondence, off elsewhere) is
                // its only automatic driver; selection never mutates it, so in
                // Inspect the meshes always stay fully shown.
                match sel with
                | SelNone ->
                    if model.WorkflowStep = Inspect then exitSolo model
                    else model
                | SelMesh _ ->
                    // Inspect renders the ensemble as-is: a selected mesh is
                    // emphasized (accent outline; its difference field is already
                    // painted) but NEVER isolates — any leftover locate isolation
                    // ends here.
                    if model.WorkflowStep = Inspect then exitSolo model
                    else model
                | SelPin _ ->
                    if model.WorkflowStep = Inspect then exitSolo model
                    else model
                | SelCell (_, mesh) ->
                    // The locate: solo the mesh, backup-captured for a single
                    // BackOutLocate. No main-3D camera move — the zoom stays the
                    // cell's double-click.
                    let backup =
                        match model.LocateBackup with
                        | Some _ -> model.LocateBackup
                        | None -> Some { PrevSolo = model.MeshSolo }
                    enterSolo mesh { model with LocateBackup = backup }
        | PickCorrespondenceAt(pinId, mesh, world) ->
            // The point is stored mesh-local via the displayed transform, so the
            // before/after toggle moves it. BEFORE-ONLY: a pick against the solved
            // pose would store a point whose Before position is off-surface/outside
            // the pin — the entry points force Before; this is the safety net for a
            // view toggled mid-edit. The Peek hold counts as After too: the raycast
            // hits the peeked (solved) geometry. A committed pick DISARMS the editor
            // (one click = one edit); an out-of-ROI click stays armed so the toast's
            // "try again" needs no re-arm.
            if model.RegView = RegAfter
               || (model.RegPeekHeld && not (Map.isEmpty model.SolvedTransforms)) then
                showToast env "Correspondences are edited in the Before state — switch the view" model
            else
            match HashMap.tryFind pinId model.ScanPins.Pins with
            | Some pin ->
                if (world - pin.Centre).Length > pin.InnerRadius then
                    showToast env "Pick inside the pin ROI" { model with CorrPreview = None }
                else
                    let own = (ModelTransforms.displayedWorld model mesh).Backward.TransformPos world
                    let isRef = model.ReferenceMesh = Some mesh
                    let sp =
                        updateCorr pinId (fun corr ->
                            if isRef then
                                { corr with RefAnchor = Some own; InRoi = Map.add mesh true corr.InRoi }
                            else
                                { corr with
                                    Anchors = Map.add mesh { Point = own; Source = AnchorPick3D } corr.Anchors
                                    InRoi   = Map.add mesh true corr.InRoi }) model.ScanPins
                    // Commit confirmation: a brief flash marker at the committed
                    // point (generation bump restarts the animation per pick).
                    let gen = match model.CorrFlash with Some (_, g) -> g + 1 | None -> 1
                    corrFlashCts.Cancel()
                    corrFlashCts <- new System.Threading.CancellationTokenSource()
                    let token = corrFlashCts.Token
                    task {
                        try
                            do! System.Threading.Tasks.Task.Delay(700, token)
                            if not token.IsCancellationRequested then env.Emit [ClearCorrFlash]
                        with _ -> ()
                    } |> ignore
                    { model with ScanPins = sp; CorrArm = None; CorrPreview = None
                                 CorrFlash = Some (world, gen) }
            | None -> model
        | ClearCorrFlash ->
            if model.CorrFlash.IsNone then model else { model with CorrFlash = None }
        | SetPointProbe p ->
            if model.PointProbe = p then model else { model with PointProbe = p }
        | ToggleCorrArm(pinId, mesh) ->
            // Edits are Before-only: arming in the After view is refused with a
            // prompt (never a silent view switch). The mesh isolation while armed
            // is a view-layer effect (wheelIsolation reads CorrArm).
            if model.CorrArm = Some(pinId, mesh) then
                { model with CorrArm = None; CorrPreview = None }
            elif model.RegView = RegAfter then
                showToast env "Correspondences are edited in the Before state — switch the view" model
            else
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
        // Fly the main 3D to a metric-world point, keeping orientation.
        | FlyToPoint(world, radius) ->
            let scale = DatasetScale.active model.ActiveDataset model.DatasetScales
            let centreR = ScanPin.renderCentre model.CommonCentroid scale world
            env.Emit [CameraMessage (OrbitMessage.SetTargetCenter(AnimationKind.Tanh, centreR))
                      CameraMessage (OrbitMessage.SetTargetRadius(max 0.2 (radius * scale)))]
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
        | FlyToSensor mesh ->
            // The dataset-load framing, per mesh: the sensor (panorama centre,
            // mesh-origin fallback), framed to the mesh's own bounds.
            let center = ModelTransforms.panoCenterRender model mesh
            let radius =
                match Map.tryFind mesh model.MeshBounds with
                | Some b when not b.IsInvalid ->
                    max 1.0 (b.Size.Length * DatasetScale.forMesh model.DatasetScales mesh * 0.6)
                | _ -> model.Camera.radius
            env.Emit [CameraMessage (OrbitMessage.SetTargetCenter(AnimationKind.Tanh, center))
                      CameraMessage (OrbitMessage.SetTargetRadius radius)]
            model
        | BackOutLocate ->
            // Restores the isolation only — the camera never moves on a
            // single-click action.
            match model.LocateBackup with
            | None -> model
            | Some b ->
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
                    model.ReferenceMesh = Some s.RefMesh
                    && s.Pins |> Map.forall (fun pinId (ra, meshPts) ->
                        match HashMap.tryFind pinId model.ScanPins.Pins with
                        | None -> false
                        | Some pin ->
                            let c = ScanPin.correspondence pin
                            c.RefAnchor = Some ra
                            && meshPts |> Map.forall (fun mesh pt ->
                                match Map.tryFind mesh c.Anchors with
                                | Some a -> a.Point = pt
                                | None -> false))
                if intact then model
                else
                    bumpSolveGen ()
                    showToast env "Registration cleared — its correspondences changed"
                        (invalidateRings (invalidateProbes
                            { model with
                                SolvedTransforms = Map.empty
                                SolveInputs = None
                                RegView = RegBefore
                                BrushedSamples = Set.empty }))

    // Inspect Difference channel: per shown moving mesh, fetch its signed distance
    // to the reference (the mesh's own served vertex order) for the 3D + focus
    // difference maps. Generation-guarded debounce; per-pose pairing: the Other
    // pose is fetched only once a solve exists, in the same batch.
    let private ensureFocusDist (env : Env<Message>) (model : Model) : Model =
        if model.WorkflowStep <> Inspect then model
        else
            match model.ReferenceMesh with
            | None -> model
            | Some refMesh ->
                // Shown moving meshes: under solo only the isolated mesh needs its field.
                let moving =
                    model.MeshNames |> IndexList.toList
                    |> List.filter (fun n -> n <> refMesh && MeshVisibility.shown model.MeshSolo n)
                let otherView = RegView.other model.RegView
                let solved = not (Map.isEmpty model.SolvedTransforms)
                let wanted =
                    [ for m in moving do
                        if not (Map.containsKey m model.FocusDist) then yield m, model.RegView, false
                        if solved && not (Map.containsKey m model.FocusDistOther) then yield m, otherView, true ]
                if List.isEmpty wanted || focusDistReqGen = focusDistGen then model
                else
                    focusDistReqGen <- focusDistGen
                    let gen = focusDistGen
                    // M3C2 is the sole difference metric (region-distance mode 0).
                    let mode = 0
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
                                    | Some (m, d, false) -> env.Emit [FocusDistComputed(gen, m, d)]
                                    | Some (m, d, true) -> env.Emit [FocusDistOtherComputed(gen, m, d)]
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
        |> ensureFocusDist env

