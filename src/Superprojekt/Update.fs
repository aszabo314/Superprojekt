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
        | SetVisible(name, v) ->
            let activePickingLayer =
                if not v && model.ActivePickingLayer = Some name then None
                else model.ActivePickingLayer
            invalidateProbes
                { model with
                    MeshVisible = Map.add name v model.MeshVisible
                    ActivePickingLayer = activePickingLayer }
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
        | LogDebug s ->
            let log = model.DebugLog.InsertAt(0, s)
            let log = if log.Count > 20 then IndexList.take 20 log else log
            { model with DebugLog = log }
        | ToggleGhostSilhouette ->
            { model with GhostSilhouette = not model.GhostSilhouette }
        | SetGhostOpacity v ->
            { model with GhostOpacity = v }
        | SetShadingStrength v ->
            { model with ShadingStrength = v }
        | SetSlopeThresholdDeg v ->
            { model with SlopeThresholdDeg = v }
        | ToggleAnchorGhostMode ->
            { model with AnchorGhostMode = not model.AnchorGhostMode }
        | SetQuickPinRadius v ->
            { model with QuickPinRadius = max 0.01 v }
        | SetReferencePeek held ->
            if model.ReferencePeekHeld = held then model
            else { model with ReferencePeekHeld = held }

        | SetRegView v ->
            // Only meaningful once a solve exists (the view disables it otherwise).
            if model.RegView = v || Map.isEmpty model.SolvedTransforms then model
            else invalidateProbes (invalidateRings { model with RegView = v })
        | SetReferenceMesh mesh ->
            // Reference change invalidates any solve (it was relative to the old
            // reference): drop SolvedTransforms, snap back to Before, invalidate
            // probes/rings, then re-seed all correspondence-enabled pins.
            let model =
                invalidateProbes (invalidateRings
                    { model with
                        Registration = { model.Registration with ReferenceMesh = mesh }
                        SolvedTransforms = Map.empty
                        RegView = RegBefore
                        CorrSetMode = false; CorrPreview = None })
            match mesh with
            | Some _ -> seedAnchors env model (correspondenceEnabledIds model)
            | None -> model

        // Weighted rigid solve per visible moving mesh with ≥3 in-ROI pairs, in
        // parallel. Writes SolvedTransform directly. Anchors are mesh-local, so the
        // pairs are taken at the load pose, giving an absolute solved transform a
        // re-solve replaces wholesale.
        | SolveCoarse ->
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
                    showToast env "Need ≥3 correspondence markers on a visible moving mesh" model
                else
                    for mesh in solvable do
                        let pairs = pairsFor mesh
                        let pinIds = pairs |> Array.map (fun (pinId, _, _) -> pinId)
                        let rmsBefore =
                            sqrt ((pairs |> Array.sumBy (fun (_, ra, mp) -> (mp - ra).LengthSquared)) / float pairs.Length)
                        // (refAnchor world, moving anchor at load pose = own-frame point, weight 1).
                        let queryPairs = pairs |> Array.map (fun (_, ra, mp) -> ra, mp, 1.0)
                        task {
                            try
                                let! world, residuals, eigen, collinear =
                                    Query.lsqPairs ApiConfig.apiBase.Value mesh queryPairs
                                    |> Async.StartAsTask
                                let pairResiduals = Array.zip pinIds residuals
                                env.Emit [CoarseSolved(mesh, world, pairResiduals, rmsBefore, eigen, collinear)]
                            with ex ->
                                env.Emit [CoarseFailed(mesh, ex.Message)]
                        } |> ignore
                    { model with Registration = { reg with Running = true } }
        | CoarseSolved(mesh, world, pairResiduals, rmsBefore, eigenvalues, collinear) ->
            // lsqPairs returns the absolute world transform mapping the load-pose
            // moving anchors onto the reference; store it as the SolvedTransform.
            let scale = DatasetScale.forMesh model.DatasetScales mesh
            let solvedRender =
                RigidTransform.worldToRender scale model.CommonCentroid (Trafo3d(world, world.Inverse))
            let rmsAfter =
                if pairResiduals.Length = 0 then 0.0
                else sqrt ((pairResiduals |> Array.sumBy (fun (_, r) -> r * r)) / float pairResiduals.Length)
            let sp =
                pairResiduals |> Array.fold (fun sp (pinId, r) ->
                    updateCorr pinId (fun c -> { c with Residuals = Map.add mesh r c.Residuals }) sp)
                    model.ScanPins
            let lastEntry = {
                Stage           = StageCoarse
                RmsBefore       = rmsBefore
                RmsAfter        = rmsAfter
                Conditioning    = Some { Eigenvalues = eigenvalues; CollinearityWarning = collinear }
                PerPinResiduals = Some (Map.ofArray pairResiduals)
                Timestamp       = System.DateTime.UtcNow
            }
            invalidateRings (invalidateProbes
                { model with
                    SolvedTransforms = Map.add mesh solvedRender model.SolvedTransforms
                    RegView = RegAfter
                    ScanPins = sp
                    LastSolve = Map.add mesh lastEntry model.LastSolve
                    Registration = { model.Registration with Running = false } })
        | CoarseFailed(mesh, reason) ->
            showToast env (sprintf "Solve failed (%s)" (Primitives.shortName mesh))
                { model with
                    DebugLog = model.DebugLog.InsertAt(0, sprintf "coarse solve failed (%s): %s" mesh reason)
                    Registration = { model.Registration with Running = false } }

        | AnchorsSeeded(refUpdates, seeded, inRoi) ->
            let sp =
                refUpdates |> Array.fold (fun sp (pinId, ra, dist) ->
                    updateCorr pinId (fun c -> { c with RefAnchor = Some ra; RefDistance = dist }) sp)
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
        | ReseedMesh(pinId, mesh) ->
            match HashMap.tryFind pinId model.ScanPins.Pins with
            | Some pin when (ScanPin.correspondence pin |> Option.isSome) ->
                if model.Registration.ReferenceMesh.IsSome then reseedOneMesh env model pinId mesh
                else showToast env "Set a ★ reference mesh first to re-seed" model
            | _ -> model
        | AnchorSeedFailed reason ->
            showToast env "Correspondence seeding failed — see debug log"
                { model with
                    DebugLog = model.DebugLog.InsertAt(0, sprintf "correspondence seeding failed: %s" reason) }
        | ShowToast text ->
            showToast env text model
        | ClearToast ->
            if model.Toast.IsNone then model else { model with Toast = None }

        | SetMeshSensorType(name, sensor) ->
            { model with MeshSensorTypes = Map.add name sensor model.MeshSensorTypes }
        | SetHeatmapMode m ->
            { model with HeatmapMode = m }
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
                { model with
                    SceneBounds = padded
                    MeshBounds = perMesh }
        | DatasetsLoaded datasets ->
            { model with Datasets = datasets |> Array.toList }
        | SetActiveDataset dataset ->
            if model.ActiveDataset = Some dataset then model
            else
                { model with
                    ActiveDataset = Some dataset
                    ScanPins = ScanPinModel.initial
                    Selection = Selection.initial
                    MeshSolo = NoSolo
                    MeshBounds = Map.empty
                    ActivePickingLayer = None
                    SolvedTransforms = Map.empty
                    LoadTransforms = Map.empty
                    RegView = RegBefore
                    Toast = None }
        | JumpToMesh meshName ->
            match Map.tryFind meshName model.DatasetCentroids with
            | Some centroid ->
                let renderPos = (centroid - model.CommonCentroid) * DatasetScale.forMesh model.DatasetScales meshName
                let radius =
                    if model.SceneBounds.IsInvalid then 50.0
                    else model.SceneBounds.Size.Length * 0.6
                env.Emit [CameraMessage (OrbitMessage.SetTargetCenter(true, AnimationKind.Tanh, renderPos))]
                env.Emit [CameraMessage (OrbitMessage.SetTargetRadius(true, radius))]
            | None -> ()
            model
        | SetRenderingMode m ->
            { model with RenderingMode = m }
        | ToggleMeshSolo name ->
            invalidateProbes (
                match model.MeshSolo with
                | Solo(soloName, restore) when soloName = name ->
                    { model with MeshVisible = restore; MeshSolo = NoSolo }
                | Solo(_, restore) ->
                    let vis = restore |> Map.map (fun k _ -> k = name)
                    { model with MeshVisible = vis; MeshSolo = Solo(name, restore) }
                | NoSolo ->
                    let restore = model.MeshVisible
                    let vis =
                        model.MeshNames |> IndexList.toSeq
                        |> Seq.map (fun n -> n, n = name) |> Map.ofSeq
                    { model with MeshVisible = vis; MeshSolo = Solo(name, restore) })
        | ShowAllMeshes ->
            let vis = model.MeshNames |> IndexList.toSeq |> Seq.map (fun n -> n, true) |> Map.ofSeq
            invalidateProbes { model with MeshVisible = vis; MeshSolo = NoSolo }
        | HideAllMeshes ->
            let vis = model.MeshNames |> IndexList.toSeq |> Seq.map (fun n -> n, false) |> Map.ofSeq
            invalidateProbes { model with MeshVisible = vis; MeshSolo = NoSolo }
        | ResetCamera ->
            let center, radius =
                if model.SceneBounds.IsInvalid then V3d.Zero, 50.0
                else V3d.Zero, max 1.0 (model.SceneBounds.Size.Length * 0.6)
            env.Emit [CameraMessage (OrbitMessage.SetTargetCenter(true, AnimationKind.Tanh, center))]
            env.Emit [CameraMessage (OrbitMessage.SetTargetRadius(true, radius))]
            model
        | ToggleGearPopover ->
            { model with GearPopoverOpen = not model.GearPopoverOpen }
        | RenamePin(id, name) ->
            match HashMap.tryFind id model.ScanPins.Pins with
            | Some pin ->
                let nm = name.Trim()
                let nm = if nm = "" then pin.Name else nm
                let sp = { model.ScanPins with Pins = HashMap.add id { pin with Name = nm } model.ScanPins.Pins }
                { model with ScanPins = sp }
            | None -> model
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
            ScanPinUpdate.handleMsg env model msg
        | SetHovered h ->
            if model.Selection.Hovered = h then model
            else { model with Selection = { model.Selection with Hovered = h } }
        | SetSelectedPoint m ->
            if model.Selection.SelectedPoint = m then model
            else { model with Selection = { model.Selection with SelectedPoint = m } }
        | SetWorkflowStep step ->
            // Entering/leaving Inspect (re)drives the central-3D variance map, so drop
            // its cache + bump the generation.
            if model.WorkflowStep = step then model
            else
                bumpSurfaceDist ()
                bumpFocusDist ()
                { model with WorkflowStep = step; SurfaceDistance = Map.empty; FocusDist = Map.empty
                             CorrSetMode = false; CorrPreview = None }
        | SetInspectChannel ch ->
            { model with InspectChannel = ch }
        | SetFocusProjection p ->
            { model with FocusProjection = p }
        | SetFocusPeekReference held ->
            // Peeking the reference suspends set-correspondence; drop a stale ghost.
            if model.FocusPeekReference = held then model
            else { model with FocusPeekReference = held; CorrPreview = (if held then None else model.CorrPreview) }
        | SetFocusedMesh None ->
            { model with Selection = { model.Selection with FocusedMesh = None }
                         CorrSetMode = false; CorrPreview = None }
        | SetFocusedMesh (Some m) ->
            // Promote to the large single (links rail + dock + focus enlargement).
            // Switching target cancels any in-progress set-correspondence.
            if model.Selection.FocusedMesh = Some m then model
            else { model with Selection = { model.Selection with FocusedMesh = Some m }
                              CorrSetMode = false; CorrPreview = None }
        | PickCorrespondenceAt(pinId, mesh, world) ->
            // Set the mesh's correspondence marker for the pin, constrained to the
            // pin's probe-cylinder ROI. Stored mesh-local via the displayed transform.
            match HashMap.tryFind pinId model.ScanPins.Pins with
            | Some pin ->
                match ScanPin.correspondence pin with
                | Some c ->
                    let axis = ScanPin.axis pin
                    let v = world - pin.Centre
                    let axial = Vec.dot v axis
                    let radial = (v - axis * axial).Length
                    if radial <= pin.InnerRadius && abs axial <= ScanPin.fixedProbeLength * 0.5 then
                        let own = (ModelTransforms.displayedWorld model mesh).Backward.TransformPos world
                        let sp = updateCorr pinId (fun corr ->
                                    { corr with
                                        Anchors = Map.add mesh { Point = own; Source = AnchorPick3D } corr.Anchors
                                        InRoi   = Map.add mesh true corr.InRoi }) model.ScanPins
                        // Placement ends set-correspondence mode and clears the ghost.
                        showToast env "Correspondence placed"
                            { model with ScanPins = sp; CorrSetMode = false; CorrPreview = None }
                    else showToast env "Pick is outside the pin ROI" model
                | None -> model
            | None -> model
        | ToggleCorrSetMode ->
            // Toggling off cancels (no commit) and drops the ghost; the focus tile
            // redraws the committed marker since the anchor was never touched.
            if model.CorrSetMode then { model with CorrSetMode = false; CorrPreview = None }
            else { model with CorrSetMode = true; CorrPreview = None }
        | CorrPreviewComputed p ->
            if model.CorrSetMode then { model with CorrPreview = p } else model
        | ToggleOutlines ->
            { model with OutlineMode = not model.OutlineMode }
        // Keep orientation, animate centre + radius so the target subtends ~25% of
        // viewport height. User nav input overrides via the orbit machinery.
        | FlyTo(target, aspect) ->
            let cW, rW = FlyToMath.boundingSphere target
            let scale = DatasetScale.active model.ActiveDataset model.DatasetScales
            let centreR = (cW - model.CommonCentroid) * scale
            let dist = FlyToMath.distance (FlyToMath.fovY 90.0 aspect) (rW * scale)
            env.Emit [CameraMessage (OrbitMessage.SetTargetCenter(true, AnimationKind.Tanh, centreR))
                      CameraMessage (OrbitMessage.SetTargetRadius(true, dist))]
            model
        // Diagnostics route through existing handlers; focus/highlight = open target + 1.5s pulse.
        | NavTo action ->
            let pulse (selector : string) =
                try JSRuntime.Instance.InvokeVoid("SuperPulse", selector) with _ -> ()
            match action with
            | ReseedCorrespondence _ ->
                seedAnchors env model (correspondenceEnabledIds model)
            | SelectPinOpenCard pinId ->
                pulse ".pin-inspector"
                { model with
                    Selection = { model.Selection with SelectedPin = Some pinId }
                    WorkflowStep = Correspondence }
            | HighlightReferenceColumn ->
                pulse ".rail-mesh-list"
                { model with MenuOpen = true; WorkflowStep = Overview }
            | RunCoarse -> env.Emit [SolveCoarse]; model
            | RunFine | CommitPending | DiscardPending -> model

    // All-meshes variance: per reference vertex, the std of each visible moving
    // mesh's signed distance (target = reference, ref = moving). Debounced via the
    // surface-distance generation/CTS.
    let private ensureVariance (env : Env<Message>) (model : Model) : Model =
        if model.WorkflowStep <> Inspect then model
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
                let moving =
                    model.MeshNames |> IndexList.toList
                    |> List.filter (fun n -> n <> refMesh && (Map.tryFind n model.MeshVisible |> Option.defaultValue true))
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
        |> ScanPinUpdate.ensureRings env
        |> ensureVariance env
        |> ensureFocusDist env

