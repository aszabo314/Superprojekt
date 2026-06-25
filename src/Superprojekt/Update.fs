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
        // §6: while a solve preview is pending, block actions that change what it is relative to.
        let previewBlocked =
            match msg with
            | SetActiveDataset _
            | ScanPinMsg EnterAnchorPlacement ->
                PendingRegistration.isPreview model.PendingReg
            | _ -> false
        if previewBlocked then
            showToast env "Blocked while previewing a registration result — commit or discard it first" model
        else
        match msg with
        | CameraMessage msg ->
            { model with Camera = OrbitController.update (Env.map CameraMessage env) model.Camera msg }
        | CentroidsLoaded centroids ->
            let common  = if centroids.Length > 0 then centroids |> Array.averageBy snd else V3d.Zero
            let names   = centroids |> Array.map fst |> IndexList.ofArray
            let visible = centroids |> Array.fold (fun m (n, _) -> Map.add n true m) Map.empty
            let indices = centroids |> Array.mapi (fun i (n,_) -> n,i) |> HashMap.ofArray
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

        | SetRegistrationMode m ->
            { model with Registration = { model.Registration with Mode = m } }
        | SetReferenceMesh mesh ->
            // §3: probe invalidation + clear pending preview + re-seed all correspondence-enabled pins.
            let model = exitPreview model
            let model =
                invalidateProbes { model with Registration = { model.Registration with ReferenceMesh = mesh } }
            match mesh with
            | Some _ -> seedAnchors env model (correspondenceEnabledIds model)
            | None -> model

        // Stage 1: weighted rigid solve per visible moving mesh with ≥3 pairs, in parallel → PendingReg.
        | SolveCoarse ->
            let reg = model.Registration
            match reg.ReferenceMesh with
            | None -> model
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
                        | Some c when c.Enabled && c.RefAnchor.IsSome && p.Phase = PinPhase.Committed ->
                            Some (p.Id, c.RefAnchor.Value, 1.0, c.Anchors)
                        | _ -> None)
                let pairsFor mesh =
                    enabledPins
                    |> List.choose (fun (pinId, ra, rel, anchors) ->
                        match Map.tryFind mesh anchors with
                        | Some a -> Some (pinId, ra, a.Point, rel)
                        | None -> None)
                    |> Array.ofList
                let solvable, unsolved =
                    visibleMoving |> List.partition (fun m -> (pairsFor m).Length >= 3)
                if List.isEmpty solvable then model
                else
                    let inputs =
                        CoarseInputs (
                            enabledPins
                            |> List.map (fun (pinId, _, rel, anchors) ->
                                pinId, rel, anchors |> Map.map (fun _ a -> a.Source))
                            |> Array.ofList)
                    for mesh in solvable do
                        let pairs = pairsFor mesh
                        let pinIds = pairs |> Array.map (fun (pinId, _, _, _) -> pinId)
                        let wSum = pairs |> Array.sumBy (fun (_, _, _, w) -> max 0.0 w)
                        let rmsBefore =
                            if wSum <= 1e-12 then
                                sqrt ((pairs |> Array.sumBy (fun (_, ra, mp, _) -> (mp - ra).LengthSquared)) / float pairs.Length)
                            else
                                sqrt ((pairs |> Array.sumBy (fun (_, ra, mp, w) -> max 0.0 w * (mp - ra).LengthSquared)) / wSum)
                        let queryPairs = pairs |> Array.map (fun (_, ra, mp, w) -> ra, mp, w)
                        task {
                            try
                                let! delta, residuals, eigen, collinear =
                                    Query.lsqPairs ApiConfig.apiBase.Value mesh queryPairs
                                    |> Async.StartAsTask
                                let pairResiduals = Array.zip pinIds residuals
                                env.Emit [CoarseSolved(mesh, delta, pairResiduals, rmsBefore, eigen, collinear)]
                            with ex ->
                                env.Emit [CoarseFailed(mesh, ex.Message)]
                        } |> ignore
                    { model with
                        PendingReg = Some {
                            Stage    = StageCoarse
                            Mode     = "correspondence"
                            Inputs   = inputs
                            Results  = Map.empty
                            Unsolved = unsolved
                            Expected = List.length solvable }
                        Registration = { reg with Running = true } }
        | CoarseSolved(mesh, worldDelta, pairResiduals, rmsBefore, eigenvalues, collinear) ->
            match model.PendingReg with
            | Some pr when pr.Stage = StageCoarse ->
                let scale = DatasetScale.forMesh model.DatasetScales mesh
                let deltaRender =
                    RigidTransform.worldToRender scale model.CommonCentroid (Trafo3d(worldDelta, worldDelta.Inverse))
                let rmsAfter =
                    if pairResiduals.Length = 0 then 0.0
                    else sqrt ((pairResiduals |> Array.sumBy (fun (_, r) -> r * r)) / float pairResiduals.Length)
                let result = {
                    Delta         = deltaRender
                    RmsBefore     = rmsBefore
                    RmsAfter      = rmsAfter
                }
                let pr' = { pr with Results = Map.add mesh result pr.Results; Expected = max 0 (pr.Expected - 1) }
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
                clearPreviewProbes (invalidateRings
                    { model with
                        PendingReg = Some pr'
                        ScanPins = sp
                        LastSolve = Map.add mesh lastEntry model.LastSolve
                        Registration = { model.Registration with Running = pr'.Expected > 0 } })
            | _ -> model
        | CoarseFailed(mesh, reason) ->
            match model.PendingReg with
            | Some pr when pr.Stage = StageCoarse ->
                let pr' = { pr with Unsolved = mesh :: pr.Unsolved; Expected = max 0 (pr.Expected - 1) }
                { model with
                    PendingReg = Some pr'
                    DebugLog = model.DebugLog.InsertAt(0, sprintf "coarse solve failed (%s): %s" mesh reason)
                    Registration = { model.Registration with Running = pr'.Expected > 0 } }
            | _ -> model

        // Stage 2 · Fine: existing ICP, unchanged math; solves land in PendingReg as a delta vs committed.
        | RunRegistration ->
            let reg = model.Registration
            match reg.ReferenceMesh with
            | None -> model
            | Some refMesh ->
                let visibleMeshes =
                    model.MeshNames |> IndexList.toSeq
                    |> Seq.filter (fun n ->
                        n <> refMesh
                        && Map.tryFind n model.MeshVisible |> Option.defaultValue true)
                    |> Array.ofSeq
                if visibleMeshes.Length = 0 then model
                else
                    let anchorPins =
                        model.ScanPins.Pins |> HashMap.toSeq
                        |> Seq.choose (fun (_, pin) ->
                            if pin.Phase = PinPhase.Committed then
                                // pin.Centre and pin.InnerRadius are already world-space metres.
                                Some (pin.Id, (pin.Centre, pin.InnerRadius, 1.0))
                            else None)
                        |> Array.ofSeq
                    let anchors =
                        match reg.Mode with
                        | TraditionalIcp -> [||]
                        | RegionRestrictedIcp -> anchorPins |> Array.map snd
                    let eps =
                        match reg.Mode with
                        | TraditionalIcp -> 0.0
                        | RegionRestrictedIcp -> 0.05
                    let modeTag =
                        match reg.Mode with
                        | TraditionalIcp -> "traditional-icp"
                        | RegionRestrictedIcp -> "region-icp"
                    for mov in visibleMeshes do
                        let initial = (ModelTransforms.committedWorld model mov).Forward
                        let movName = mov
                        task {
                            try
                                let! trafo, conv, resi =
                                    Query.runIcp ApiConfig.apiBase.Value refMesh movName initial 50 30 anchors eps
                                    |> Async.StartAsTask
                                env.Emit [FineSolved(movName, trafo, conv, resi)]
                            with ex ->
                                env.Emit [FineFailed(movName, ex.Message)]
                        } |> ignore
                    { model with
                        PendingReg = Some {
                            Stage    = StageFine
                            Mode     = modeTag
                            Inputs   = FineInputs(modeTag, (match reg.Mode with
                                                            | RegionRestrictedIcp -> anchorPins |> Array.map fst
                                                            | TraditionalIcp -> [||]))
                            Results  = Map.empty
                            Unsolved = []
                            Expected = visibleMeshes.Length }
                        Registration = { reg with Running = true } }
        | FineSolved(mesh, world, conv, resi) ->
            match model.PendingReg with
            | Some pr when pr.Stage = StageFine ->
                // ICP returns the full world transform (iterates from committed initial); store the delta.
                let committedW = ModelTransforms.committedWorld model mesh
                let deltaW = committedW.Inverse * world
                let scale = DatasetScale.forMesh model.DatasetScales mesh
                let deltaRender = RigidTransform.worldToRender scale model.CommonCentroid deltaW
                let rmsAfter =
                    if resi.Length = 0 then 0.0
                    else sqrt ((resi |> Array.sumBy (fun x -> x * x)) / float resi.Length)
                let rmsBefore = if conv.Length > 0 then conv.[0] else rmsAfter
                let result = {
                    Delta         = deltaRender
                    RmsBefore     = rmsBefore
                    RmsAfter      = rmsAfter
                }
                let pr' = { pr with Results = Map.add mesh result pr.Results; Expected = max 0 (pr.Expected - 1) }
                let lastEntry = {
                    Stage           = StageFine
                    RmsBefore       = rmsBefore
                    RmsAfter        = rmsAfter
                    Conditioning    = None
                    PerPinResiduals = None
                    Timestamp       = System.DateTime.UtcNow
                }
                clearPreviewProbes (invalidateRings
                    { model with
                        PendingReg = Some pr'
                        LastSolve = Map.add mesh lastEntry model.LastSolve
                        Registration = { model.Registration with Running = pr'.Expected > 0 } })
            | _ -> model
        | FineFailed(mesh, reason) ->
            match model.PendingReg with
            | Some pr when pr.Stage = StageFine ->
                let pr' = { pr with Unsolved = mesh :: pr.Unsolved; Expected = max 0 (pr.Expected - 1) }
                { model with
                    PendingReg = Some pr'
                    DebugLog = model.DebugLog.InsertAt(0, sprintf "fine solve failed (%s): %s" mesh reason)
                    Registration = { model.Registration with Running = pr'.Expected > 0 } }
            | _ -> model

        | CommitRegistration ->
            match model.PendingReg with
            | Some pr when not (Map.isEmpty pr.Results) ->
                // Single commit (no history): apply each pending delta into the
                // committed render transforms, re-base correspondence anchors by
                // the applied world delta so they stay on the surface.
                let committed mesh = ModelTransforms.committedRender model mesh
                let after =
                    pr.Results |> Map.fold (fun m mesh r ->
                        Map.add mesh (RegLog.effective (committed mesh) r.Delta) m) model.MeshTransforms
                let worldDeltas =
                    pr.Results |> Map.map (fun mesh r ->
                        ModelTransforms.worldDelta model mesh (committed mesh)
                            (RegLog.effective (committed mesh) r.Delta))
                let model =
                    { model with
                        MeshTransforms = after
                        ScanPins = bakeAnchors worldDeltas model.ScanPins
                        Registration = { model.Registration with Running = false } }
                // Registration-complete cascade: all probes + contact rings.
                invalidateProbes (exitPreview model)
            | _ -> model
        | DiscardRegistration ->
            // §1: clears the pending delta only — committed probes stay valid; rings recompute at committed pose.
            { exitPreview model with Registration = { model.Registration with Running = false } }

        // Correspondence anchors (spec §4) + fallback picks (§8.1/§8.2).
        | ToggleCorrespondence pinId ->
            match HashMap.tryFind pinId model.ScanPins.Pins with
            | Some pin ->
                let next =
                    match pin.Correspondence with
                    | Some c -> { c with Enabled = not c.Enabled }
                    | None -> Correspondence.empty
                let sp =
                    { model.ScanPins with
                        Pins = HashMap.add pinId { pin with Correspondence = Some next } model.ScanPins.Pins }
                let model = { model with ScanPins = sp }
                if next.Enabled then
                    // C3: first registration pin of the session opens the readiness panel once.
                    let model =
                        if not requirementsSurfaced then
                            requirementsSurfaced <- true
                            { model with WorkflowPanelOpen = true }
                        else model
                    if model.Registration.ReferenceMesh.IsSome then seedAnchors env model [pinId]
                    else showToast env "Designate a reference mesh (★) to seed correspondence markers" model
                else model
            | None -> model
        | AnchorsSeeded(refUpdates, seeded) ->
            let sp =
                refUpdates |> Array.fold (fun sp (pinId, ra, dist) ->
                    updateCorr pinId (fun c -> { c with RefAnchor = Some ra; RefDistance = dist }) sp)
                    model.ScanPins
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
        | ShowToast text ->
            showToast env text model
        | ClearToast ->
            if model.Toast.IsNone then model else { model with Toast = None }

        | SetMeshSensorType(name, sensor) ->
            { model with MeshSensorTypes = Map.add name sensor model.MeshSensorTypes }
        | SetHeatmapMode m ->
            { model with HeatmapMode = m }
        | ToggleSurfaceDistance ->
            bumpSurfaceDist ()
            if model.SurfaceDistOn then
                { model with SurfaceDistOn = false; SurfaceDistance = Map.empty }
            else
                // Turning on: encoding paints the inspector's active moving-mesh row.
                // Auto-pick one (current InspectorMesh if valid, else first visible moving).
                match model.Registration.ReferenceMesh with
                | None ->
                    showToast env "Set a ★ reference mesh first to map signed distance" model
                | Some refMesh ->
                    let visibleMoving =
                        model.MeshNames |> IndexList.toList
                        |> List.filter (fun n ->
                            n <> refMesh && (Map.tryFind n model.MeshVisible |> Option.defaultValue true))
                    let chosen =
                        match model.InspectorMesh with
                        | Some m when List.contains m visibleMoving -> Some m
                        | _ -> List.tryHead visibleMoving
                    match chosen with
                    | None ->
                        showToast env "No visible moving mesh to map"
                            { model with SurfaceDistOn = true; VarianceOn = false; SurfaceDistance = Map.empty }
                    | Some m ->
                        { model with SurfaceDistOn = true; VarianceOn = false; InspectorMesh = Some m; SurfaceDistance = Map.empty }
        | ToggleVariance ->
            bumpSurfaceDist ()
            // Mutually exclusive with the single-mesh extrinsic map.
            { model with
                VarianceOn = not model.VarianceOn
                SurfaceDistOn = false
                SurfaceDistance = Map.empty }
        | VarianceComputed(mesh, arr) ->
            // Keep only if still in variance mode and this is the reference mesh.
            if model.VarianceOn && model.Registration.ReferenceMesh = Some mesh then
                { model with SurfaceDistance = Map.add mesh arr model.SurfaceDistance }
            else model
        | ToggleExtrinsicZDiff ->
            // Switch extrinsic measure (M3C2 ↔ Δz); drop the cache so it refetches.
            bumpSurfaceDist ()
            { model with ExtrinsicZDiff = not model.ExtrinsicZDiff; SurfaceDistance = Map.empty }
        | SurfaceDistanceComputed(mesh, dist) ->
            // drop if the active inspector mesh changed since the fetch was issued.
            if model.SurfaceDistOn && model.InspectorMesh = Some mesh then
                { model with SurfaceDistance = Map.add mesh dist model.SurfaceDistance }
            else model
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
                    InspectorMesh = None
                    MeshSolo = NoSolo
                    MeshBounds = Map.empty
                    ActivePickingLayer = None
                    PendingReg = None
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
        | EditPin id ->
            let sp = model.ScanPins
            match HashMap.tryFind id sp.Pins with
            | Some pin ->
                let pin = { pin with Phase = PinPhase.Placement }
                let sp = { sp with Pins = HashMap.add id pin sp.Pins; Placement = AdjustingPin id; SelectedPin = Some id }
                { model with ScanPins = sp }
            | None -> model
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
        | ScanPinMsg msg ->
            ScanPinUpdate.handleMsg env model msg
        | SetWorkflowPinHover h ->
            if model.WorkflowPinHover = h then model else { model with WorkflowPinHover = h }
        // Bottom-dock inspector: active moving-mesh row (B4 + extrinsic-map target).
        | SetInspectorMesh m ->
            if model.InspectorMesh = m then model
            else
                // active row drives the extrinsic surface map; drop its cache to refetch.
                if model.SurfaceDistOn then bumpSurfaceDist ()
                { model with InspectorMesh = m; SurfaceDistance = (if model.SurfaceDistOn then Map.empty else model.SurfaceDistance) }
        | ToggleWorkflowPanel ->
            { model with WorkflowPanelOpen = not model.WorkflowPanelOpen; WorkflowPinHover = None }
        | SetWorkflowStep step ->
            if model.WorkflowStep = step then model else { model with WorkflowStep = step }
        | SetFocusAxis a ->
            if model.FocusAxis = a then model else { model with FocusAxis = a }
        | ToggleFocusPanel ->
            { model with FocusOpen = not model.FocusOpen }
        | SetAlignMesh m ->
            if model.AlignMesh = m then model else { model with AlignMesh = m }
        | TogglePinFocus ->
            { model with PinFocusMode = not model.PinFocusMode }
        | SetMovementLayer m ->
            if model.MovementLayer = m then model else { model with MovementLayer = m }
        | ToggleOutlines ->
            { model with OutlineMode = not model.OutlineMode }
        // §5 translate-only coarse align: shift the moving mesh's committed
        // render-space transform by an in-plane delta. Blocked under a preview.
        | TranslateAlignMesh d ->
            if PendingRegistration.isPreview model.PendingReg then model
            else
                match model.AlignMesh with
                | Some mesh when Map.tryFind mesh model.MeshVisible |> Option.defaultValue true ->
                    let cur = Map.tryFind mesh model.MeshTransforms |> Option.defaultValue Trafo3d.Identity
                    invalidateRings
                        (invalidateProbes { model with MeshTransforms = Map.add mesh (cur * Trafo3d.Translation d) model.MeshTransforms })
                | _ -> model
        // Workflow §4: keep orientation, animate centre + radius so the target subtends
        // ~25% of viewport height. User nav input overrides via the orbit machinery.
        | FlyTo(target, aspect) ->
            let cW, rW = FlyToMath.boundingSphere target
            let scale = DatasetScale.active model.ActiveDataset model.DatasetScales
            let centreR = (cW - model.CommonCentroid) * scale
            let dist = FlyToMath.distance (FlyToMath.fovY 90.0 aspect) (rW * scale)
            env.Emit [CameraMessage (OrbitMessage.SetTargetCenter(true, AnimationKind.Tanh, centreR))
                      CameraMessage (OrbitMessage.SetTargetRadius(true, dist))]
            model
        // Workflow §5: diagnostics route through existing handlers; focus/highlight = open target + 1.5s pulse.
        | NavTo action ->
            let pulse (selector : string) =
                try JSRuntime.Instance.InvokeVoid("SuperPulse", selector) with _ -> ()
            match action with
            | ReseedCorrespondence _ ->
                seedAnchors env model (correspondenceEnabledIds model)
            | SelectPinOpenCard pinId ->
                env.Emit [ScanPinMsg (SelectPin (Some pinId))]
                pulse ".pc-corr"
                model
            | HighlightReferenceColumn ->
                pulse ".left-panel .mesh-list"
                { model with MenuOpen = true }
            | RunCoarse -> env.Emit [SolveCoarse]; model
            | RunFine -> env.Emit [RunRegistration]; model
            | CommitPending -> env.Emit [CommitRegistration]; model
            | DiscardPending -> env.Emit [DiscardRegistration]; model

    // A2 postlude: surface-map on + an active inspector mesh → lazily fetch that mesh's per-vertex
    // signed distance (committed pose), debounced, at most once per invalidation generation.
    let private ensureSurfaceDistance (env : Env<Message>) (model : Model) : Model =
        if not model.SurfaceDistOn then model
        else
            match model.InspectorMesh, model.Registration.ReferenceMesh with
            | Some mesh, Some refMesh
                  when mesh <> refMesh
                       && not (Map.containsKey mesh model.SurfaceDistance)
                       && surfaceDistReqGen <> surfaceDistGen
                       && (Map.tryFind mesh model.MeshVisible |> Option.defaultValue true) ->
                surfaceDistReqGen <- surfaceDistGen
                let targetT = (ModelTransforms.committedWorld model mesh).Forward
                let refT = (ModelTransforms.committedWorld model refMesh).Forward
                surfaceDistCts.Cancel()
                surfaceDistCts <- new System.Threading.CancellationTokenSource()
                let token = surfaceDistCts.Token
                task {
                    try
                        do! System.Threading.Tasks.Task.Delay(120, token)
                        let! dist =
                            Query.regionDistance ApiConfig.apiBase.Value mesh 0 refMesh 0 targetT refT (if model.ExtrinsicZDiff then 1 else 0)
                            |> Async.StartAsTask
                        if not token.IsCancellationRequested then
                            env.Emit [SurfaceDistanceComputed(mesh, dist)]
                    with
                    | :? System.OperationCanceledException -> ()
                    | ex ->
                        if not token.IsCancellationRequested then
                            env.Emit [SurfaceDistanceFailed(mesh, ex.Message)]
                } |> ignore
                model
            | _ -> model

    // §6 all-meshes variance: per reference vertex, the std of each visible
    // moving mesh's signed distance (target = reference, ref = moving). Shares
    // the surface-distance generation/CTS (mutually exclusive with the map).
    let private ensureVariance (env : Env<Message>) (model : Model) : Model =
        if not model.VarianceOn then model
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
                    let refT = (ModelTransforms.committedWorld model refMesh).Forward
                    let jobs =
                        moving |> List.map (fun m ->
                            let mT = (ModelTransforms.committedWorld model m).Forward
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

    let update (env : Env<Message>) (model : Model) (msg : Message) =
        let updated =
            updateCore env model msg
            |> ScanPinUpdate.ensureProbe env
            |> ScanPinUpdate.ensureProbePreview env
            |> ScanPinUpdate.ensureRings env
            |> ensureSurfaceDistance env
            |> ensureVariance env
        updated

