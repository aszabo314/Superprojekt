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
            | SetActiveDataset _ | ToggleFusionMode | StartRetarget _
            | ScanPinMsg EnterAnchorPlacement ->
                PendingRegistration.isPreview model.PendingReg
            | _ -> false
        if previewBlocked then
            showToast env "Blocked while previewing a registration result — commit or discard it first" model
        // §5 study-mode guard: gated messages no-op with a toast (silently for pointer/hover).
        elif StudyUpdate.blocked model msg then
            match msg with
            | CameraMessage _ | SetChartCursor _ | SetChartHoverMesh _ -> model
            | _ -> showToast env "Not available in this step" model
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
        | SetClipPlanes planes ->
            { model with ClipPlanes = planes }
        | ToggleCutaway ->
            { model with CutawayActive = not model.CutawayActive }
        | ToggleClipAboveIso ->
            { model with ClipAboveIso = not model.ClipAboveIso }
        | LockIsoPlane d ->
            let pid = ScanPinModel.effectivePinId model.ScanPins
            match pid |> Option.bind (fun id -> HashMap.tryFind id model.ScanPins.Pins) with
            | Some pin ->
                let pv = PendingRegistration.isPreview model.PendingReg
                // chart value d → 3D needs the reference-median offset (chart y=0 = pin.Centre + axis·RefOffset).
                let axis, refOffset =
                    match ScanPin.effectiveProbe pv pin with
                    | ProbeReady r -> r.Normal, r.RefOffset
                    | _ -> ScanPin.axis pin, 0.0
                let origin = pin.Centre + axis * (d + refOffset)
                let plane = { Origin = origin; Normal = axis }
                // Alt-click the same spot releases; a new spot relocks.
                let same =
                    match List.tryHead model.ClipPlanes with
                    | Some e -> (e.Origin - origin).Length < max 1e-3 (pin.InnerRadius * 0.1)
                    | None -> false
                if same then { model with ClipPlanes = [] }
                else { model with ClipPlanes = [plane] }
            | None -> model
        | ToggleRuler ->
            { model with RulerActive = not model.RulerActive }

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
                let refMesh = model.Registration.ReferenceMesh |> Option.defaultValue ""
                let step = RegLog.buildStep System.DateTime.UtcNow refMesh pr (regState model)
                let st' = RegLog.commit step (regState model)
                let model =
                    reanchorCards
                        { applyRegState st' (stepDeltas true model step) model with
                            Registration = { model.Registration with Running = false } }
                // Registration-complete cascade: all probes + contact rings (algo RMS already swapped above).
                invalidateProbes (exitPreview model)
            | _ -> model
        | DiscardRegistration ->
            // §1: clears the pending delta only — committed probes stay valid; rings recompute at committed pose.
            { exitPreview model with Registration = { model.Registration with Running = false } }
        | RollbackRegStep ->
            match RegLog.rollback (regState model) with
            | Some (st', step) ->
                let model =
                    reanchorCards
                        { applyRegState st' (stepDeltas false model step) model with
                            // diagnostics of the rolled-back step are stale
                            LastSolve = LastSolve.afterRollback step model.LastSolve }
                invalidateProbes (exitPreview model)
            | None -> model
        | ResetRegistration ->
            // §8.4: roll back every logged step (un-baking anchors), then drop unlogged
            // leftovers (legacy workspaces) → identity transforms + empty log.
            let rec rollAll (model : Model) =
                match RegLog.rollback (regState model) with
                | Some (st', step) ->
                    rollAll (applyRegState st' (stepDeltas false model step) model)
                | None -> model
            let model = reanchorCards (rollAll model)
            invalidateProbes (exitPreview
                { model with
                    MeshTransforms = Map.empty
                    MeshAlgorithmResidual = Map.empty
                    LastSolve = Map.empty
                    Registration = { model.Registration with Running = false } })

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
        | SetAnchor(pinId, mesh, point, source) ->
            { model with ScanPins = setAnchor pinId mesh point source model.ScanPins }
        | StartAnchorPick(pinId, mesh) ->
            if PendingRegistration.isPreview model.PendingReg then
                showToast env "Correspondence-marker picking is disabled while a solve preview is pending" model
            else
                { model with AnchorPick = Some { PinId = pinId; Mesh = mesh } }
        | CancelAnchorPick ->
            { model with AnchorPick = None }
        | AnchorPickHit world ->
            match model.AnchorPick with
            | Some ap when model.Registration.ReferenceMesh = Some ap.Mesh ->
                // F10: a 3D pick on the reference mesh moves RefAnchor (the marker every pair is measured from).
                let dist =
                    match HashMap.tryFind ap.PinId model.ScanPins.Pins with
                    | Some pin -> (world - pin.Centre).Length
                    | None -> 0.0
                let sp =
                    updateCorr ap.PinId (fun c -> { c with RefAnchor = Some world; RefDistance = dist }) model.ScanPins
                { model with ScanPins = sp; AnchorPick = None }
            | Some ap ->
                let sp = setAnchor ap.PinId ap.Mesh world AnchorPick3D model.ScanPins
                // Auto-advance to the next moving mesh (panel order after current) lacking a marker for this pin.
                let next =
                    match HashMap.tryFind ap.PinId sp.Pins |> Option.bind ScanPin.correspondence with
                    | Some corr ->
                        let refMesh = model.Registration.ReferenceMesh
                        let meshes = model.MeshNames |> IndexList.toList
                        let candidates =
                            match List.tryFindIndex ((=) ap.Mesh) meshes with
                            | Some i -> List.skip (i + 1) meshes @ List.truncate i meshes
                            | None -> meshes
                        candidates
                        |> List.tryFind (fun m ->
                            Some m <> refMesh && m <> ap.Mesh
                            && not (Map.containsKey m corr.Anchors))
                    | None -> None
                { model with
                    ScanPins = sp
                    AnchorPick = next |> Option.map (fun m -> { ap with Mesh = m }) }
            | None -> model

        // Patch small-multiples picker (spec §7.2).
        | OpenPatchPicker pinId ->
            if PendingRegistration.isPreview model.PendingReg then
                showToast env "Patch picking is disabled while a solve preview is pending" model
            else
                let pinOpt = HashMap.tryFind pinId model.ScanPins.Pins
                let refMeshOpt = model.Registration.ReferenceMesh
                match pinOpt, refMeshOpt with
                | Some pin, Some refMesh ->
                    let refAnchor =
                        ScanPin.correspondence pin
                        |> Option.bind (fun c -> c.RefAnchor)
                        |> Option.defaultValue pin.Centre
                    let radius = pin.InnerRadius
                    let visible =
                        model.MeshNames |> IndexList.toList
                        |> List.filter (fun n -> Map.tryFind n model.MeshVisible |> Option.defaultValue true)
                    let trafos =
                        (model.MeshNames |> IndexList.toList)
                        |> List.map (fun m -> m, ModelTransforms.committedWorld model m)
                        |> Map.ofList
                    let anchorOf mesh =
                        ScanPin.correspondence pin
                        |> Option.bind (fun c -> Map.tryFind mesh c.Anchors)
                        |> Option.map (fun a -> a.Point)
                    let atlasUrl (mesh : string) =
                        let i = mesh.IndexOf '/'
                        if i < 0 then ""
                        else
                            sprintf "%s/datasets/%s/mesh/%s/0/atlas"
                                (ApiConfig.apiBase.Value.TrimEnd('/')) (mesh.[.. i - 1]) (mesh.[i + 1 ..])
                    patchPickerCts.Cancel()
                    patchPickerCts <- new System.Threading.CancellationTokenSource()
                    let token = patchPickerCts.Token
                    task {
                        try
                            do! System.Threading.Tasks.Task.Delay(250, token)
                            // Reference patch first — its fitted frame is the shared frame for every other mesh.
                            let refT = Map.tryFind refMesh trafos |> Option.defaultValue Trafo3d.Identity
                            let cRef = refT.Backward.TransformPos refAnchor
                            let! refPts, refTris, refDirM, normalM =
                                Query.patchInFrame ApiConfig.apiBase.Value refMesh cRef radius 200000 None
                                |> Async.StartAsTask
                            let normalW = (refT.Forward.TransformDir normalM).Normalized
                            let refDirW =
                                let r = refT.Forward.TransformDir refDirM
                                let proj = r - normalW * Vec.dot r normalW
                                if proj.Length > 1e-9 then proj.Normalized else V3d.OIO
                            let leftW = Vec.cross normalW refDirW
                            let refEntry = {
                                Mesh      = refMesh
                                Centre    = refAnchor
                                Points    =
                                    refPts |> Array.map (fun (uv2, wp, atlasUv) ->
                                        let world = refT.Forward.TransformPos wp
                                        uv2, Vec.dot (world - refAnchor) normalW, atlasUv)
                                Triangles = refTris
                                Crosshair = V2d.Zero
                                AtlasUrl  = atlasUrl refMesh
                            }
                            let! moving =
                                visible
                                |> List.filter ((<>) refMesh)
                                |> List.map (fun mesh -> async {
                                    try
                                        let t = Map.tryFind mesh trafos |> Option.defaultValue Trafo3d.Identity
                                        let! centreW =
                                            match anchorOf mesh with
                                            | Some p -> async.Return (Some p)
                                            | None -> async {
                                                // Auto seed if no anchor yet.
                                                let! res =
                                                    Query.closestPoint ApiConfig.apiBase.Value mesh 0
                                                        (t.Backward.TransformPos refAnchor)
                                                return res |> Option.map (fun r -> t.Forward.TransformPos r.point)
                                              }
                                        match centreW with
                                        | None -> return None
                                        | Some cw ->
                                            let frame =
                                                (t.Backward.TransformDir normalW),
                                                (t.Backward.TransformDir refDirW)
                                            let! pts, tris, _, _ =
                                                Query.patchInFrame ApiConfig.apiBase.Value mesh
                                                    (t.Backward.TransformPos cw) radius 200000 (Some frame)
                                            let points =
                                                pts |> Array.map (fun (uv2, wp, atlasUv) ->
                                                    let world = t.Forward.TransformPos wp
                                                    uv2, Vec.dot (world - cw) normalW, atlasUv)
                                            let cross =
                                                V2d(Vec.dot (refAnchor - cw) refDirW,
                                                    Vec.dot (refAnchor - cw) leftW)
                                            return Some {
                                                Mesh = mesh; Centre = cw; Points = points
                                                Triangles = tris
                                                Crosshair = cross; AtlasUrl = atlasUrl mesh
                                            }
                                    with _ -> return None
                                })
                                |> Async.Parallel
                                |> Async.StartAsTask
                            if not token.IsCancellationRequested then
                                let entries = refEntry :: (moving |> Array.choose id |> List.ofArray)
                                env.Emit [PatchPickerReady(pinId, normalW, refDirW, radius, entries)]
                        with
                        | :? System.OperationCanceledException -> ()
                        | ex ->
                            if not token.IsCancellationRequested then
                                env.Emit [PatchPickerFailed ex.Message]
                    } |> ignore
                    { model with
                        PatchPicker = Some {
                            PinId = pinId; Normal = V3d.OOI; RefDir = V3d.OIO
                            Radius = radius; Entries = []; Running = true
                            Shaded = (model.PatchPicker |> Option.map (fun p -> p.Shaded) |> Option.defaultValue false) } }
                | _ -> showToast env "Patch picking needs a reference mesh (★)" model
        | ClosePatchPicker ->
            patchPickerCts.Cancel()
            { model with PatchPicker = None }
        | TogglePatchShaded ->
            match model.PatchPicker with
            | Some pp -> { model with PatchPicker = Some { pp with Shaded = not pp.Shaded } }
            | None -> model
        | PatchPickerReady(pinId, normal, refDir, radius, entries) ->
            match model.PatchPicker with
            | Some pp when pp.PinId = pinId ->
                { model with
                    PatchPicker = Some { pp with Normal = normal; RefDir = refDir; Radius = radius; Entries = entries; Running = false } }
            | _ -> model
        | PatchPickerFailed reason ->
            match model.PatchPicker with
            | Some pp ->
                showToast env (sprintf "Patch sampling failed: %s" reason)
                    { model with PatchPicker = Some { pp with Running = false } }
            | None -> model
        | PatchPickerClick(mesh, u, v, h) ->
            match model.PatchPicker with
            | Some pp ->
                match pp.Entries |> List.tryFind (fun e -> e.Mesh = mesh) with
                | Some entry when Some mesh <> model.Registration.ReferenceMesh ->
                    // (u,v,h) from the cell's triangle hit-test; the frame is orthonormal,
                    // so this is the exact surface point at committed poses — no server ray needed.
                    let left = Vec.cross pp.Normal pp.RefDir
                    let point = entry.Centre + pp.RefDir * u + left * v + pp.Normal * h
                    env.Emit [SetAnchor(pp.PinId, mesh, point, AnchorPatch2D)]
                    model
                | _ -> model
            | None -> model
        | ShowToast text ->
            showToast env text model
        | ClearToast ->
            if model.Toast.IsNone then model else { model with Toast = None }

        | SetMeshSensorType(name, sensor) ->
            { model with MeshSensorTypes = Map.add name sensor model.MeshSensorTypes }
        | SetMeshDatasetError(name, valueOpt) ->
            match valueOpt with
            | Some v -> { model with MeshDatasetErrors = Map.add name v model.MeshDatasetErrors }
            | None -> { model with MeshDatasetErrors = Map.remove name model.MeshDatasetErrors }
        | SetHeatmapMode m ->
            match m with
            | HeatDiff when not (PendingRegistration.isPreview model.PendingReg) ->
                model
            | HeatDiff ->
                let prev = match model.HeatmapMode with HeatDiff -> model.HeatmapPrev | cur -> cur
                { model with HeatmapMode = HeatDiff; HeatmapPrev = prev }
            | m ->
                { model with HeatmapMode = m; HeatmapPrev = m }
        | SetProvenanceThreshold v ->
            { model with ProvenanceThreshold = v }
        | ToggleSurfaceDistance ->
            bumpSurfaceDist ()
            if model.SurfaceDistOn then
                { model with SurfaceDistOn = false; SurfaceDistance = Map.empty; SurfaceDistBrush = None }
            else
                // Turning on: encoding paints the soloed (chart-sticky) mesh. Auto-pick one
                // (current solo if a valid moving mesh, else selected pin's host, else first visible moving).
                match model.Registration.ReferenceMesh with
                | None ->
                    showToast env "Set a ★ reference mesh first to map signed distance" model
                | Some refMesh ->
                    let visibleMoving =
                        model.MeshNames |> IndexList.toList
                        |> List.filter (fun n ->
                            n <> refMesh && (Map.tryFind n model.MeshVisible |> Option.defaultValue true))
                    let chosen =
                        match model.ChartStickyMesh with
                        | Some m when List.contains m visibleMoving -> Some m
                        | _ ->
                            let host =
                                model.ScanPins.SelectedPin
                                |> Option.bind (fun id -> HashMap.tryFind id model.ScanPins.Pins)
                                |> Option.bind (fun p -> p.HostMeshName)
                            match host with
                            | Some h when List.contains h visibleMoving -> Some h
                            | _ -> List.tryHead visibleMoving
                    match chosen with
                    | None ->
                        showToast env "No visible moving mesh to map"
                            { model with SurfaceDistOn = true; SurfaceDistance = Map.empty }
                    | Some m ->
                        { model with SurfaceDistOn = true; ChartStickyMesh = Some m; SurfaceDistance = Map.empty }
        | SurfaceDistanceComputed(mesh, dist) ->
            // drop if the soloed mesh changed since the fetch was issued.
            if model.SurfaceDistOn && model.ChartStickyMesh = Some mesh then
                { model with SurfaceDistance = Map.add mesh dist model.SurfaceDistance }
            else model
        | SurfaceDistanceFailed(_, reason) ->
            showToast env "Surface-distance query failed — is the server up to date? (restart it)"
                { model with DebugLog = model.DebugLog.InsertAt(0, sprintf "region-distance failed: %s" reason) }
        | SetSurfaceDistBrush b ->
            if model.SurfaceDistBrush = b then model else { model with SurfaceDistBrush = b }
        | ToggleFusionMode ->
            { model with FusionMode = not model.FusionMode }

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
                // Synthetic pose: no dataset ships real imagery.
                let panos =
                    if union.IsValid then
                        let name = model.ActiveDataset |> Option.defaultValue "scene"
                        [ { Name = name; EyeWorld = union.Center + V3d(0.0, 0.0, 2.0); Yaw = 0.0 } ]
                    else []
                { model with
                    SceneBounds = padded
                    MeshBounds = perMesh
                    Panoramas = panos
                    SelectedPanorama = 0 }
        | DatasetsLoaded datasets ->
            { model with Datasets = datasets |> Array.toList }
        | SetActiveDataset dataset ->
            if model.ActiveDataset = Some dataset then model
            else
                { model with
                    ActiveDataset = Some dataset
                    ScanPins = ScanPinModel.initial
                    ChartCursor = None
                    ChartHoverMesh = None
                    ChartStickyMesh = None
                    MeshSolo = NoSolo
                    MeshBounds = Map.empty
                    Panoramas = []
                    SelectedPanorama = 0
                    ActivePickingLayer = None
                    LassoDrawing = None
                    LassoVolume = None
                    LassoEnabled = true
                    PendingReg = None
                    AnchorPick = None
                    PatchPicker = None
                    Toast = None
                    HeatmapMode = (match model.HeatmapMode with HeatDiff -> model.HeatmapPrev | m -> m)
                    CardSystem = { model.CardSystem with Cards = model.CardSystem.Cards |> HashMap.map (fun _ c -> { c with Visible = false }) } }
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
        | SetLassoCardPos pos ->
            { model with LassoCardPos = Some pos }
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
        | SetActivePickingLayer name ->
            { model with ActivePickingLayer = name }
        | LassoBegin ->
            let scanPins =
                match model.ScanPins.Placement with
                | AnchorPlacement -> { model.ScanPins with Placement = PlacementIdle }
                | _ -> model.ScanPins
            { model with ScanPins = scanPins; LassoDrawing = Some { Vertices = [||] }; LassoEnabled = true }
        | ToggleLassoEnabled ->
            { model with LassoEnabled = not model.LassoEnabled }
        | LassoAddVertex p ->
            match model.LassoDrawing with
            | Some d -> { model with LassoDrawing = Some { Vertices = Array.append d.Vertices [| p |] } }
            | None -> model
        | LassoCommit(viewTrafo, projTrafo, vpSize) ->
            match model.LassoDrawing with
            | Some d when d.Vertices.Length >= 3 ->
                let poly = d.Vertices
                let n = poly.Length
                let toNdc (px : V2d) =
                    V2d(2.0 * px.X / float vpSize.X - 1.0,
                        1.0 - 2.0 * px.Y / float vpSize.Y)
                let vp = viewTrafo * projTrafo
                let camPos = viewTrafo.Backward.TransformPos V3d.Zero
                let dirs =
                    poly |> Array.map (fun px ->
                        let ndc = toNdc px
                        let pNear = vp.Backward.TransformPosProj(V3d(ndc, -1.0))
                        (pNear - camPos) |> Vec.normalize)
                let planes =
                    Array.init n (fun i ->
                        let d0 = dirs.[i]
                        let d1 = dirs.[(i + 1) % n]
                        let normal = Vec.cross d0 d1 |> Vec.normalize
                        let offset = -(Vec.dot normal camPos)
                        V4d(normal.X, normal.Y, normal.Z, offset))
                let centroidNdc =
                    let mutable s = V2d.Zero
                    for px in poly do
                        s <- s + toNdc px
                    s / float n
                let centroidWorld =
                    vp.Backward.TransformPosProj(V3d(centroidNdc, 0.0))
                let outside =
                    planes |> Array.sumBy (fun p ->
                        let d = p.X * centroidWorld.X + p.Y * centroidWorld.Y + p.Z * centroidWorld.Z + p.W
                        if d > 0.0 then 1 else 0)
                let planes =
                    if outside > n / 2 then planes |> Array.map (fun p -> -p)
                    else planes
                let volume = { Planes = planes; ScreenPolygon = poly; CommitVpSize = vpSize }
                { model with LassoDrawing = None; LassoVolume = Some volume; LassoEnabled = true }
            | _ ->
                { model with LassoDrawing = None }
        | LassoCancel ->
            { model with LassoDrawing = None }
        | LassoClear ->
            { model with LassoDrawing = None; LassoVolume = None; LassoEnabled = true }
        | CardMsg msg ->
            { model with CardSystem = CardUpdate.update msg model.CardSystem }
        | ScanPinMsg msg ->
            ScanPinUpdate.handleMsg env model msg
        | SaveWorkspace ->
            let json = Persistence.serialize model
            try
                JSRuntime.Instance.InvokeVoid("SuperWorkspaceSave", "workspace.json", json)
            with _ -> ()
            model
        | LoadWorkspaceJson json ->
            match Persistence.apply json model with
            | Result.Ok m -> m
            | Result.Error err ->
                { model with DebugLog = model.DebugLog.InsertAt(0, sprintf "workspace load failed: %s" err) }
        | StartRetarget target ->
            let pins =
                model.ScanPins.Pins
                |> HashMap.toSeq
                |> Seq.choose (fun (_, p) ->
                    if p.Phase = PinPhase.Committed then Some p else None)
                |> Array.ofSeq
            if pins.Length = 0 then
                { model with DebugLog = model.DebugLog.InsertAt(0, "retarget: no committed pins") }
            else
                task {
                    try
                        let! candidates =
                            pins
                            |> Array.map (fun p ->
                                async {
                                    let! res = Query.closestPoint ApiConfig.apiBase.Value target 0 p.Centre
                                    return
                                        match res with
                                        | Some r ->
                                            let dist = sqrt (float r.distanceSquared)
                                            {
                                                PinId = p.Id
                                                OriginalHostMesh = p.HostMeshName
                                                InnerRadius = p.InnerRadius
                                                ProjectedCentre = r.point
                                                ProjectionDistance = dist
                                                TargetMesh = target
                                                Decision = RetargetUndecided
                                            }
                                        | None ->
                                            // No projection — flag with a sentinel large distance
                                            {
                                                PinId = p.Id
                                                OriginalHostMesh = p.HostMeshName
                                                InnerRadius = p.InnerRadius
                                                ProjectedCentre = p.Centre
                                                ProjectionDistance = System.Double.PositiveInfinity
                                                TargetMesh = target
                                                Decision = RetargetReject
                                            }
                                })
                            |> Async.Parallel
                        env.Emit [RetargetCandidatesReady candidates]
                    with ex ->
                        env.Emit [LogDebug (sprintf "retarget projection failed: %s" ex.Message)]
                } |> ignore
                { model with Retarget = RetargetProjecting target }
        | RetargetCandidatesReady candidates ->
            { model with Retarget = RetargetReviewing candidates }
        | SetRetargetDecision(pinId, decision) ->
            match model.Retarget with
            | RetargetReviewing cs ->
                let updated =
                    cs |> Array.map (fun c ->
                        if c.PinId = pinId then { c with Decision = decision } else c)
                { model with Retarget = RetargetReviewing updated }
            | _ -> model
        | CommitRetarget ->
            match model.Retarget with
            | RetargetReviewing cs ->
                let mutable pins = model.ScanPins.Pins
                let mutable moved = []
                for c in cs do
                    if c.Decision = RetargetAccept then
                        match HashMap.tryFind c.PinId pins with
                        | Some p ->
                            let p' =
                                { p with
                                    Centre = c.ProjectedCentre
                                    HostMeshName = Some c.TargetMesh
                                    Probe = ProbeNone
                                    ContactRings = RingsNone }
                            pins <- HashMap.add c.PinId p' pins
                            moved <- c.PinId :: moved
                        | None -> ()
                let model =
                    { model with
                        ScanPins = { model.ScanPins with Pins = pins }
                        Retarget = RetargetIdle }
                // §4: a moved pin re-seeds its refAnchor + Auto anchors (manual picks survive).
                seedAnchors env model moved
            | _ -> model
        | CancelRetarget ->
            { model with Retarget = RetargetIdle }
        // Transient hover probe: QuickPinRadius, auto length, declared reference mesh (or
        // active picking layer / first visible). Not cached; next Ctrl-click cancels via the CTS.
        | HoverProbeAt(screenPx, world) ->
            let visible =
                model.MeshNames |> IndexList.toList
                |> List.filter (fun n -> Map.tryFind n model.MeshVisible |> Option.defaultValue true)
            match visible with
            | [] -> model
            | _ ->
                let refMesh =
                    model.Registration.ReferenceMesh |> Option.filter (fun r -> List.contains r visible)
                    |> Option.orElse (model.ActivePickingLayer |> Option.filter (fun l -> List.contains l visible))
                    |> Option.defaultValue (List.head visible)
                let radius = max 0.01 model.QuickPinRadius
                // Effective transforms: under a pending preview the hover probe reflects what is on screen.
                let meshes =
                    visible |> List.map (fun n -> n, (ModelTransforms.effectiveWorld model n).Forward)
                hoverProbeCts.Cancel()
                hoverProbeCts <- new System.Threading.CancellationTokenSource()
                let token = hoverProbeCts.Token
                task {
                    try
                        let! res =
                            Query.probe ApiConfig.apiBase.Value meshes refMesh world radius 0.0 4096
                            |> Async.StartAsTask
                        if not token.IsCancellationRequested then
                            match res with
                            | Result.Ok r -> env.Emit [HoverProbeResult (ProbeReady r)]
                            | Result.Error e -> env.Emit [HoverProbeResult (ProbeError e)]
                        do! System.Threading.Tasks.Task.Delay(8000, token)
                        if not token.IsCancellationRequested then
                            env.Emit [ClearHoverProbe]
                    with
                    | :? System.OperationCanceledException -> ()
                    | ex ->
                        if not token.IsCancellationRequested then
                            env.Emit [HoverProbeResult (ProbeError ex.Message)]
                } |> ignore
                { model with HoverProbe = Some { ScreenPos = screenPx; Anchor = world; Radius = radius; Probe = ProbeRunning } }
        | HoverProbeResult st ->
            match model.HoverProbe with
            | Some h when h.Probe = ProbeRunning -> { model with HoverProbe = Some { h with Probe = st } }
            | _ -> model
        | ClearHoverProbe ->
            hoverProbeCts.Cancel()
            if model.HoverProbe.IsNone then model else { model with HoverProbe = None }
        // Chart 2D-3D linking. Hover messages fire per pointer-move; the no-change
        // guards keep that churn out of the adaptive graph (field-projection rule).
        | SetChartCursor c ->
            if model.ChartCursor = c then model else { model with ChartCursor = c }
        | SetChartHoverMesh m ->
            if model.ChartHoverMesh = m then model else { model with ChartHoverMesh = m }
        | SetWorkflowPinHover h ->
            if model.WorkflowPinHover = h then model else { model with WorkflowPinHover = h }
        | SetCorrMarkerHover h ->
            if model.CorrMarkerHover = h then model else { model with CorrMarkerHover = h }

        // Stale guard: only land grids if they still belong to the open pin.
        | DetailGridsComputed(pinId, grids) ->
            if model.DetailGridPin <> Some pinId then model
            else
                let g = grids |> Array.fold (fun acc (mesh, grid) -> Map.add mesh (GridReady grid) acc) model.DetailGrids
                { model with DetailGrids = g }
        | ChartColumnClick mesh ->
            let sticky = if model.ChartStickyMesh = Some mesh then None else Some mesh
            // soloed mesh changed → the surface map (keyed by it) is stale.
            bumpSurfaceDist ()
            { model with ChartStickyMesh = sticky; SurfaceDistance = Map.empty; SurfaceDistBrush = None }
        | ClearChartSticky ->
            if model.ChartStickyMesh.IsNone && model.SurfaceDistBrush.IsNone then model
            else (bumpSurfaceDist (); { model with ChartStickyMesh = None; SurfaceDistance = Map.empty; SurfaceDistBrush = None })
        | TogglePanorama ->
            { model with PanoramaOpen = not model.PanoramaOpen }
        | SetPanoramaMode m ->
            { model with PanoramaMode = m }
        | SetPanoramaBlend b ->
            { model with PanoramaBlend = clamp 0.0 1.0 b }
        | StudiesLoaded studies ->
            { model with StudiesAvailable = studies |> Array.toList }
        | ToggleWorkflowPanel ->
            { model with WorkflowPanelOpen = not model.WorkflowPanelOpen; WorkflowPinHover = None }
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
        | StudyMsg smsg ->
            StudyUpdate.handleMsg env model smsg
        | FlyToPanorama i ->
            match List.tryItem i model.Panoramas with
            | Some p ->
                let scale = DatasetScale.active model.ActiveDataset model.DatasetScales
                let eyeR = (p.EyeWorld - model.CommonCentroid) * scale
                let r =
                    if model.SceneBounds.IsInvalid then 5.0
                    else max 1.0 (model.SceneBounds.Size.Length * scale * 0.12)
                let fwd = V3d(cos p.Yaw, sin p.Yaw, 0.0)
                let center = eyeR + fwd * r
                env.Emit [CameraMessage (OrbitMessage.SetTarget(true, center, r, p.Yaw + Constant.Pi, 0.05))]
                model
            | None -> model

    // Rulers / locked iso-plane are scoped to the effective (selected/adjusting) pin:
    // they reset when it changes (incl. deselect). Cutaway is global, deliberately not reset.
    let private effectivePinId (m : Model) = ScanPinModel.effectivePinId m.ScanPins

    let private clearSectioningOnPinChange (before : Model) (after : Model) =
        if effectivePinId before = effectivePinId after then after
        else
            let after =
                if after.RulerActive then { after with RulerActive = false } else after
            let after =
                if after.CorrMarkerHover.IsSome then { after with CorrMarkerHover = None } else after
            if List.isEmpty after.ClipPlanes then after else { after with ClipPlanes = [] }

    // A2 postlude: surface-map on + a soloed column → lazily fetch that mesh's per-vertex
    // signed distance (committed pose), debounced, at most once per invalidation generation.
    let private ensureSurfaceDistance (env : Env<Message>) (model : Model) : Model =
        if not model.SurfaceDistOn then model
        else
            match model.ChartStickyMesh, model.Registration.ReferenceMesh with
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
                            Query.regionDistance ApiConfig.apiBase.Value mesh 0 refMesh 0 targetT refT
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

    let update (env : Env<Message>) (model : Model) (msg : Message) =
        let updated =
            updateCore env model msg
            |> ScanPinUpdate.ensureProbe env
            |> ScanPinUpdate.ensureProbePreview env
            |> ScanPinUpdate.ensureRings env
            |> ScanPinUpdate.ensureDetailGrids env
            |> ensureSurfaceDistance env
            |> clearSectioningOnPinChange model
        StudyUpdate.postlude env model updated msg

