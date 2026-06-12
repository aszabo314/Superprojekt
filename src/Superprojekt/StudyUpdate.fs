namespace Superprojekt

open Aardvark.Base
open FSharp.Data.Adaptive
open Aardvark.Dom
open Superprojekt

// Moved here from Update.fs so the study lifecycle (which switches datasets)
// can share it; callers are unchanged.
module ServerActions =

    let loadDataset (env : Env<Message>) (dataset : string) =
        task {
            try
                let! cs = MeshData.fetchCentroids ApiConfig.apiBase.Value dataset
                env.Emit [CentroidsLoaded cs]
            with _ -> ()
            try
                let! bboxes = MeshData.fetchBboxes ApiConfig.apiBase.Value dataset
                env.Emit [SceneBoundsLoaded bboxes]
            with _ -> ()
        } |> ignore

    let init (env : Env<Message>) =
        task {
            try
                let! datasets = MeshData.fetchDatasets ApiConfig.apiBase.Value
                env.Emit [DatasetsLoaded datasets]
                match StudyBoot.entryToken with
                | Some token ->
                    // /s/{token}: no default dataset — the study session
                    // decides what loads.
                    env.Emit [StudyMsg (StudyJoin token)]
                | None ->
                    try
                        let! studies = StudyApi.listStudies ApiConfig.apiBase.Value
                        env.Emit [StudiesLoaded studies]
                    with _ -> ()
                    let! autoLoad = MeshData.fetchDefaultDataset ApiConfig.apiBase.Value
                    if not (System.String.IsNullOrEmpty autoLoad) && datasets |> Array.contains autoLoad then
                        env.Emit [SetActiveDataset autoLoad]
                        loadDataset env autoLoad
            with _ -> ()
        } |> ignore

// Telemetry-event derivation: one central diff over (model before, model
// after, message) so the reducer branches stay clean. The same event stream
// feeds the predicate engine and (SWP8) the telemetry batcher.
module StudyEvents =

    let private j (s : string) =
        "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""

    let private obj' (kvs : (string * string) list) =
        match kvs with
        | [] -> "{}"
        | _ -> "{" + (kvs |> List.map (fun (k, v) -> sprintf "%s:%s" (j k) v) |> String.concat ",") + "}"

    let private lastFineMode (log : RegStep list) =
        log |> List.tryPick (fun st ->
            match st.Inputs with
            | FineInputs (mode, _) -> Some mode
            | _ -> None)

    let private stageTag (inputs : RegInputs) =
        match inputs with
        | CoarseInputs _ -> "coarse"
        | FineInputs _ -> "fine"

    let private solveFinished (stage : RegStage) (before : Model) (after : Model) =
        match before.PendingReg, after.PendingReg with
        | Some b, Some a ->
            b.Stage = stage && a.Stage = stage && b.Expected > 0 && a.Expected = 0
            && not (Map.isEmpty a.Results)
        | _ -> false

    let private rmsPayload (pr : PendingRegistration) =
        let perMesh =
            pr.Results |> Map.toList
            |> List.map (fun (mesh, r) ->
                sprintf "%s:{\"rmsBefore\":%.6g,\"rmsAfter\":%.6g}" (j mesh) r.RmsBefore r.RmsAfter)
            |> String.concat ","
        sprintf "{\"perMesh\":{%s}}" perMesh

    // Pin centre vs the coarse non-secret moving-region outline (§9 P2).
    let private committedPinInMoving (before : Model) (after : Model) (cfg : StudyConfigPublic) =
        if cfg.MovingPolygon.Length < 3 then false
        else
            match before.ScanPins.Placement with
            | AdjustingPin id ->
                match HashMap.tryFind id after.ScanPins.Pins with
                | Some pin when pin.Phase = PinPhase.Committed ->
                    StudyConfig.insidePolygon cfg.MovingPolygon pin.Centre.XY
                | _ -> false
            | _ -> false

    let derive (before : Model) (after : Model) (msg : Message) : (string * string) list =
        let session =
            match after.Study with
            | Some (StudyActive s) -> Some s
            | _ -> None
        match session with
        | None -> []
        | Some s ->
            let events = ResizeArray<string * string>()
            let add t p = events.Add(t, p)

            // session / position transitions
            let beforeActive =
                match before.Study with
                | Some (StudyActive b) -> Some b.Runtime
                | _ -> None
            match msg with
            | StudyMsg (StudySessionStarted init) when beforeActive.IsNone ->
                add (if init.Resumed then "sessionResumed" else "sessionStart")
                    (obj' [ "condition", j (StudyCondition.tag s.Condition); "demo", string s.Demo ])
            | _ -> ()
            let posBefore = beforeActive |> Option.map (fun rt -> rt.PhaseIx, rt.StepIx)
            let posAfter = s.Runtime.PhaseIx, s.Runtime.StepIx
            if beforeActive.IsSome && posBefore <> Some posAfter then
                match posBefore with
                | Some (pb, sb) when fst posAfter > pb || (fst posAfter = pb && snd posAfter > sb) ->
                    match Study.stepAt s.Config pb sb with
                    | Some st -> add "stepComplete" (obj' [ "stepId", j st.Id ])
                    | None -> ()
                | _ -> ()
                if Some (fst posAfter) <> (posBefore |> Option.map fst) then
                    match Study.phaseAt s.Config (fst posAfter) with
                    | Some ph -> add "phaseEnter" (obj' [ "phaseId", j ph.Id ])
                    | None -> ()
                match Study.currentStep s with
                | Some st ->
                    add "stepEnter" (obj' [ "stepId", j st.Id ])
                    match st.Question with
                    | Some qu -> add "questionShown" (obj' [ "questionId", j qu.Id ])
                    | None -> ()
                | None -> ()

            // user actions
            match msg with
            | CameraMessage _ ->
                let cb, ca = before.Camera, after.Camera
                if ca.targetPhi <> cb.targetPhi || ca.targetTheta <> cb.targetTheta then add "orbit" "{}"
                if ca.targetRadius <> cb.targetRadius then add "zoom" "{}"
            | SetActivePickingLayer (Some l) when before.ActivePickingLayer <> Some l ->
                add "layerCycled" (obj' [ "layer", j l ])
            | ToggleMeshSolo m -> add "soloToggled" (obj' [ "mesh", j m ])
            | SetVisible (m, v) -> add "meshVisToggled" (obj' [ "mesh", j m; "visible", string v ])
            | ScanPinMsg (PlaceAnchor _) -> add "pinPlaced" "{}"
            | ScanPinMsg CommitPin ->
                add "pinCommitted" "{}"
                // §9 P2: the soft warning targets registration landmarks —
                // in the measurement phase placing pins ON the moving region
                // is the task, so gate on the solve feature being available.
                if committedPinInMoving before after s.Config
                   && Study.featureVisibleIn s "coarseSolve" then
                    add "pinInMoving" "{}"
            | ScanPinMsg (DeletePin _) -> add "pinDeleted" "{}"
            | SetAnchor (ScanPinId.ScanPinId pid, mesh, _, source) ->
                add "anchorSet" (obj' [ "pinId", j (string pid); "mesh", j mesh
                                        "source", j (string source) ])
            | AnchorPickHit _ -> add "anchorSet" (obj' [ "source", j "pick3d" ])
            | ApplyAnchorReview -> add "anchorAccepted" "{}"
            | ToggleCorrespondence _ -> add "correspondenceToggled" "{}"
            | RunRegistration ->
                let cur =
                    match before.Registration.Mode with
                    | TraditionalIcp -> "traditional-icp"
                    | RegionRestrictedIcp -> "region-icp"
                match lastFineMode before.RegistrationLog with
                | Some prev when prev <> cur -> add "solveAlternativeRun" (obj' [ "mode", j cur ])
                | _ -> ()
            | CommitRegistration ->
                match before.PendingReg with
                | Some pr when after.RegistrationLog.Length > before.RegistrationLog.Length ->
                    add "committed" (obj' [ "stage", j (stageTag pr.Inputs)
                                            "n", string after.RegistrationLog.Length ])
                | _ -> ()
            | RollbackRegStep when after.RegistrationLog.Length < before.RegistrationLog.Length ->
                add "rolledBack" "{}"
            | DiscardRegistration when PendingRegistration.isPreview before.PendingReg ->
                add "discarded" "{}"
            | SetHeatmapMode m when after.HeatmapMode = m && before.HeatmapMode <> m ->
                add "heatmapMode" (obj' [ "mode", j (HeatmapMode.tag m) ])
            | CardMsg (CreateCardsForPin (ScanPinId.ScanPinId pid, _)) ->
                add "cardOpened" (obj' [ "pinId", j (string pid) ])
            | CardMsg (RemoveCardsForPin _) -> add "cardClosed" "{}"
            | SetChartCursor (Some _) -> add "chartHover" "{}"
            | StudyMsg (StudySetChoice (qid, _))
            | StudyMsg (StudySetNumber (qid, _))
            | StudyMsg (StudySetText (qid, _))
            | StudyMsg (StudySetGridItem (qid, _, _))
            | StudyMsg (StudySetConfidence (qid, _)) ->
                add "answerChanged" (obj' [ "questionId", j qid ])
            | StudyMsg (StudySceneClickHit world) ->
                match beforeActive |> Option.bind (fun rt -> rt.SceneClickArm) with
                | Some qid ->
                    add "flagMarked" (obj' [ "questionId", j qid
                                             "point", sprintf "[%.3f,%.3f,%.3f]" world.X world.Y world.Z ])
                | None -> ()
            | StudyMsg StudySetAsFinal -> add "finalRestored" "{}"
            | _ -> ()

            // solver completion (the per-mesh result messages count down
            // Expected; the milestone fires when the last one lands)
            if solveFinished StageCoarse before after then
                add "coarseSolved" (rmsPayload after.PendingReg.Value)
                add "previewShown" "{}"
            if solveFinished StageFine before after then
                add "fineSolved" (rmsPayload after.PendingReg.Value)
                add "previewShown" "{}"

            List.ofSeq events

module StudyUpdate =

    // §5 update-level guard: a user-action message originating from a gated
    // feature no-ops with a toast while a study runs. Result messages
    // (solver callbacks, loads) and study/runtime messages always pass.
    let private messageGate (msg : Message) : string option =
        match msg with
        | CameraMessage (OrbitMessage.PointerDown _ | OrbitMessage.PointerMove _
                        | OrbitMessage.PointerUp _ | OrbitMessage.Wheel _) -> Some "navigation"
        | SetActivePickingLayer (Some _) -> Some "layerCycle"
        | ScanPinMsg (EnterAnchorPlacement | PlaceAnchor _ | CommitPin) -> Some "pinPlace"
        | ScanPinMsg (SetInnerRadius _ | SetFalloffDelta _ | ChangePayloadType _
                     | SetReliabilityWeight _ | SetLineMode _ | SetProbeLength _
                     | ToggleProbeLockOrder _ | SetProbeXRange _ | DeletePin _) -> Some "pinEdit"
        | EditPin _ -> Some "pinEdit"
        | CardMsg (CreateCardsForPin _) -> Some "pinCard"
        | HoverProbeAt _ -> Some "hoverProbe"
        | SetHeatmapMode HeatProvenance -> Some "heatmap"
        | SetHeatmapMode HeatDiff -> Some "heatmapDiff"
        | SetChartCursor (Some _) | ChartColumnClick _ -> Some "violinChart"
        | SolveCoarse -> Some "coarseSolve"
        | RunRegistration | SetRegistrationMode _ -> Some "fineSolve"
        | CommitRegistration -> Some "commit"
        | RollbackRegStep | ResetRegistration -> Some "rollback"
        | DiscardRegistration | SetReferenceMesh _ -> Some "registrationCard"
        | ToggleCorrespondence _ | SetAnchorDecision _ | ApplyAnchorReview
        | SetAnchor _ | StartAnchorPick _ | OpenPatchPicker _
        | PatchPickerClick _ -> Some "coarseSolve"
        | ToggleMenu | SetVisible _ | ToggleMeshSolo _ | ShowAllMeshes
        | HideAllMeshes | JumpToMesh _ | SetRenderingMode _ -> Some "meshPanel"
        | SetMeshSensorType _ | SetMeshDatasetError _
        | SetProvenanceThreshold _ | ToggleFalloffZoneOnly -> Some "errorMetadata"
        | _ -> None

    // Whole subsystems with no feature id are Full-mode only (§5 hidden list).
    let private fullOnly (msg : Message) =
        match msg with
        | ToggleFusionMode | TogglePanorama | SelectPanorama _ | SetPanoramaMode _
        | SetPanoramaBlend _ | FlyToPanorama _
        | LassoBegin | ToggleLassoEnabled
        | SaveWorkspace | LoadWorkspaceJson _
        | StartRetarget _ | SetRetargetDecision _ | CommitRetarget
        | ToggleGearPopover | SetDatasetScale _
        | ToggleFullscreen | ToggleGhostSilhouette | SetGhostOpacity _
        | ToggleAnchorGhostMode | SetShadingStrength _ | SetSlopeThresholdDeg _ -> true
        | _ -> false

    // None = allowed; Some reason = blocked.
    let blocked (model : Model) (msg : Message) : bool =
        match model.Study with
        | Some (StudyActive s) ->
            fullOnly msg
            || (match messageGate msg with
                | Some fid -> not (Study.featureVisibleIn s fid)
                | None -> false)
        | _ -> false

    let private join (env : Env<Message>) (token : string option) (demo : (string * StudyCondition) option) =
        task {
            let! r = StudyApi.createSession ApiConfig.apiBase.Value token demo |> Async.StartAsTask
            match r with
            | Result.Ok init -> env.Emit [StudyMsg (StudySessionStarted init)]
            | Result.Error e -> env.Emit [StudyMsg (StudySessionFailed e)]
        } |> ignore

    // Resume = the step after the last advanced one (§10); a fully-advanced
    // session stays on its last step (complete is fetched there).
    let private resumePosition (cfg : StudyConfigPublic) (lastStep : (string * string) option) =
        match lastStep with
        | None -> 0, 0
        | Some (phaseId, stepId) ->
            match cfg.Phases |> List.tryFindIndex (fun p -> p.Id = phaseId) with
            | None -> 0, 0
            | Some pi ->
                let si =
                    cfg.Phases.[pi].Steps
                    |> List.tryFindIndex (fun s -> s.Id = stepId)
                    |> Option.defaultValue 0
                Study.nextPosition cfg pi si |> Option.defaultValue (pi, si)

    // Deterministic clean state on study entry/exit and on phase dataset
    // switches: everything a dataset switch resets, plus registration state
    // (a participant always starts from identity transforms — tutorial
    // registration must never leak into the main task's history, reference
    // or commit#n labels) and mesh visibility (a demo started from Full mode
    // must not inherit hidden meshes).
    let private resetScene (model : Model) =
        { model with
            MeshVisible = model.MeshNames |> IndexList.toSeq |> Seq.map (fun n -> n, true) |> Map.ofSeq
            ScanPins = ScanPinModel.initial
            ChartCursor = None
            ChartHoverMesh = None
            ChartStickyMesh = None
            MeshSolo = NoSolo
            ActivePickingLayer = None
            LassoDrawing = None
            LassoVolume = None
            LassoEnabled = true
            PendingReg = None
            AnchorReview = AnchorReviewIdle
            AnchorPick = None
            PatchPicker = None
            Toast = None
            HoverProbe = None
            FusionMode = false
            PanoramaOpen = false
            MeshTransforms = Map.empty
            MeshAlgorithmResidual = Map.empty
            Registration = RegistrationState.initial
            RegistrationLog = []
            HeatmapMode = HeatOff
            HeatmapPrev = HeatOff
            CardSystem = { model.CardSystem with Cards = HashMap.map (fun _ c -> { c with Visible = false }) model.CardSystem.Cards } }

    let private switchDataset (env : Env<Message>) (model : Model) (dataset : string) =
        if model.ActiveDataset <> Some dataset then
            env.Emit [SetActiveDataset dataset]
            ServerActions.loadDataset env dataset

    let private inv = System.Globalization.CultureInfo.InvariantCulture

    let private valueJson (v : AnswerValue) =
        match v with
        | AChoice i -> string i
        | ANumber x -> x.ToString("G17", inv)
        | AText t -> System.Text.Json.JsonSerializer.Serialize(t : string)
        | APoint p ->
            sprintf "[%s,%s,%s]" (p.X.ToString("G17", inv)) (p.Y.ToString("G17", inv)) (p.Z.ToString("G17", inv))
        | AGrid g ->
            "{" + (g |> Map.toList |> List.map (fun (i, x) -> sprintf "\"%d\":%s" i (x.ToString("G17", inv))) |> String.concat ",") + "}"

    // §7: answers post immediately on change (idempotent upsert by question
    // id) and again on Next. Tutorial gold responses come back as
    // StudyGoldResult — the single server→client correctness channel.
    let private submitAnswerNow (env : Env<Message>) (sid : string) (qid : string) (draft : AnswerDraft) =
        match draft.Value with
        | None -> ()
        | Some v ->
            task {
                let! r =
                    StudyApi.postAnswer ApiConfig.apiBase.Value sid qid (valueJson v) draft.Confidence
                    |> Async.StartAsTask
                match r with
                | Result.Ok (Some correct, screened) ->
                    env.Emit [StudyMsg (StudyGoldResult(qid, correct, screened))]
                | Result.Ok (None, true) ->
                    env.Emit [StudyMsg (StudyGoldResult(qid, true, true))]
                | _ -> ()
            } |> ignore

    // Change-driven posts are coalesced per question (500 ms) so per-keystroke
    // text input and slider drags don't flood the server, and transient radio
    // selections don't burn tutorial-gold attempts (every *posted* wrong
    // answer counts toward the screen-out threshold). Next posts immediately.
    let private answerCts = System.Collections.Generic.Dictionary<string, System.Threading.CancellationTokenSource>()

    let private cancelPendingAnswer (qid : string) =
        match answerCts.TryGetValue qid with
        | true, cts -> cts.Cancel()
        | _ -> ()

    let private submitAnswer (env : Env<Message>) (sid : string) (qid : string) (draft : AnswerDraft) =
        if draft.Value.IsSome then
            cancelPendingAnswer qid
            let cts = new System.Threading.CancellationTokenSource()
            answerCts.[qid] <- cts
            let token = cts.Token
            task {
                try
                    do! System.Threading.Tasks.Task.Delay(500, token)
                    if not token.IsCancellationRequested then
                        submitAnswerNow env sid qid draft
                with _ -> ()
            } |> ignore

    let private updateRuntime (model : Model) (f : StudySession -> StudyRuntime) =
        match model.Study with
        | Some (StudyActive s) ->
            let rt = f s
            let rt = Study.reevaluate s.Config rt (Study.isTutorialPhase s.Config rt.PhaseIx)
            { model with Study = Some (StudyActive { s with Runtime = rt }) }
        | _ -> model

    let private setDraft
            (env : Env<Message>)
            (model : Model)
            (qid : string)
            (f : AnswerDraft -> AnswerDraft) =
        match model.Study with
        | Some (StudyActive s) ->
            let draft =
                Map.tryFind qid s.Runtime.AnswersDraft
                |> Option.defaultValue AnswerDraft.empty
                |> f
            submitAnswer env s.SessionId qid draft
            updateRuntime model (fun s ->
                { s.Runtime with AnswersDraft = Map.add qid draft s.Runtime.AnswersDraft })
        | _ -> model

    // World-space committed transforms of every loaded mesh — the payload of
    // /transforms posts (labels commit#n / final).
    let postTransforms (model : Model) (sid : string) (label : string) =
        let perMesh =
            model.MeshNames |> IndexList.toList
            |> List.map (fun mesh -> mesh, (ModelTransforms.committedWorld model mesh).Forward)
        if not (List.isEmpty perMesh) then
            StudyApi.postTransforms ApiConfig.apiBase.Value sid label perMesh
            |> Async.Ignore |> Async.Start

    let private fetchCompletion (env : Env<Message>) (sid : string) =
        task {
            let! r = StudyApi.getComplete ApiConfig.apiBase.Value sid |> Async.StartAsTask
            match r with
            | Result.Ok code -> env.Emit [StudyMsg (StudyCompletionCode code)]
            | Result.Error reason -> env.Emit [StudyMsg (StudyCompletionFailed reason)]
        } |> ignore

    let private isLastPosition (cfg : StudyConfigPublic) (phaseIx : int) (stepIx : int) =
        Study.nextPosition cfg phaseIx stepIx |> Option.isNone

    let private isExitPhase (cfg : StudyConfigPublic) (phaseIx : int) =
        phaseIx = List.length cfg.Phases - 1

    let mutable private lastNextTick = 0

    let handleMsg (env : Env<Message>) (model : Model) (msg : StudyMessage) : Model =
        match msg with
        | StudyJoin token ->
            join env (Some token) None
            { model with Study = Some StudyJoining }
        | StudyStartDemo (studyId, cond) ->
            join env None (Some (studyId, cond))
            { model with Study = Some StudyJoining; GearPopoverOpen = false }
        | StudySessionFailed message ->
            // A screened token gets its own polite page, not the error page.
            if message = "screened" then { model with Study = Some StudyScreened }
            else { model with Study = Some (StudyFailed message) }
        | StudySessionStarted init ->
            StudyTelemetry.start init.SessionId
            let phaseIx, stepIx = resumePosition init.Config init.LastStep
            let rt =
                { StudyRuntime.initial with PhaseIx = phaseIx; StepIx = stepIx; ResumedNotice = init.Resumed }
                |> fun rt -> Study.reevaluate init.Config rt (Study.isTutorialPhase init.Config phaseIx)
            let session = {
                SessionId = init.SessionId
                Condition = init.Condition
                Demo      = init.Demo
                Config    = init.Config
                Runtime   = rt
            }
            let dataset =
                Study.datasetAtPhase init.Config phaseIx
                |> Option.defaultValue init.Config.DatasetTutorial
            let model = resetScene { model with Study = Some (StudyActive session); MenuOpen = false }
            switchDataset env model dataset
            // Resuming directly onto the final step: the entry transition
            // that normally fetches the code never fires (the `final`
            // transforms from the pre-reload life satisfy the server check).
            if isLastPosition init.Config phaseIx stepIx then
                fetchCompletion env init.SessionId
            model
        | StudyExitDemo ->
            // Demo sessions only — real sessions have no way back (§1).
            match model.Study with
            | Some (StudyActive s) when s.Demo ->
                StudyTelemetry.stop ()
                task {
                    try
                        let! autoLoad = MeshData.fetchDefaultDataset ApiConfig.apiBase.Value
                        if not (System.String.IsNullOrEmpty autoLoad) then
                            env.Emit [SetActiveDataset autoLoad]
                            ServerActions.loadDataset env autoLoad
                    with _ -> ()
                } |> ignore
                resetScene { model with Study = None }
            | _ -> model

        // §4 Next: enabled iff StepSatisfied; posts the advance mirror, moves
        // on, switches dataset on phase boundaries that declare one. The
        // tick guard absorbs double-clicks — without it a second click lands
        // on the (instantly satisfied) next instruction step and skips it.
        | StudyNext ->
            match model.Study with
            | Some (StudyActive s) when s.Runtime.StepSatisfied
                                        && System.Environment.TickCount - lastNextTick >= 400 ->
                lastNextTick <- System.Environment.TickCount
                let cfg = s.Config
                let rt = s.Runtime
                match Study.phaseAt cfg rt.PhaseIx, Study.stepAt cfg rt.PhaseIx rt.StepIx with
                | Some phase, Some step ->
                    StudyApi.postAdvance ApiConfig.apiBase.Value s.SessionId phase.Id step.Id
                    |> Async.Ignore |> Async.Start
                    // final value wins server-side by timestamp (§7) — posted
                    // immediately, superseding any pending debounced post
                    match Study.effectiveQuestion cfg step with
                    | Some qu ->
                        match Map.tryFind qu.Id rt.AnswersDraft with
                        | Some draft ->
                            cancelPendingAnswer qu.Id
                            submitAnswerNow env s.SessionId qu.Id draft
                        | None -> ()
                    | None -> ()
                    match Study.nextPosition cfg rt.PhaseIx rt.StepIx with
                    | None -> model
                    | Some (pIx, sIx) ->
                        let dsBefore = Study.datasetAtPhase cfg rt.PhaseIx
                        let dsAfter = Study.datasetAtPhase cfg pIx
                        let datasetSwitch = pIx <> rt.PhaseIx && dsBefore <> dsAfter
                        let rt' =
                            { rt with
                                PhaseIx = pIx
                                StepIx = sIx
                                OverlayOpen = true
                                SceneClickArm = None
                                ResumedNotice = false
                                AdvancePosted = Set.add (phase.Id + "/" + step.Id) rt.AdvancePosted
                                // predicate counts are cumulative per
                                // dataset epoch (see IMPLEMENTATION_NOTES)
                                EventCounts = if datasetSwitch then Map.empty else rt.EventCounts }
                        let rt' = Study.reevaluate cfg rt' (Study.isTutorialPhase cfg pIx)
                        let model = { model with Study = Some (StudyActive { s with Runtime = rt' }) }
                        // The dataset boundary is a clean slate: tutorial
                        // pins, transforms, history and the ★ reference must
                        // not leak into the main task.
                        let model = if datasetSwitch then resetScene model else model
                        if datasetSwitch then
                            match dsAfter with
                            | Some ds -> switchDataset env model ds
                            | None -> ()
                        // entering the exit phase = "final": post transforms
                        // + auto-upload the workspace (§8/§10)
                        if pIx <> rt.PhaseIx && isExitPhase cfg pIx then
                            postTransforms model s.SessionId "final"
                            StudyApi.postWorkspace ApiConfig.apiBase.Value s.SessionId (Persistence.serialize model)
                            |> Async.Ignore |> Async.Start
                        // the last step shows the completion code (§9 P6)
                        if isLastPosition cfg pIx sIx && rt'.CompletionCode.IsNone then
                            fetchCompletion env s.SessionId
                        model
                | _ -> model
            | _ -> model

        | StudyReopenOverlay ->
            updateRuntime model (fun s -> { s.Runtime with OverlayOpen = true })
        | StudyCloseOverlay ->
            updateRuntime model (fun s -> { s.Runtime with OverlayOpen = false })

        // §4 tutorial gold: correctness is server-evaluated; 2 fails on one
        // check re-show the relevant tutorial step, the 3rd screens out.
        | StudyGoldResult (qid, correct, screened) ->
            match model.Study with
            | Some (StudyActive s) ->
                if screened then
                    StudyTelemetry.stop ()
                    { model with Study = Some StudyScreened }
                else
                    let rt = s.Runtime
                    let fails =
                        if correct then rt.GoldFails
                        else Map.add qid ((Map.tryFind qid rt.GoldFails |> Option.defaultValue 0) + 1) rt.GoldFails
                    let failCount = Map.tryFind qid fails |> Option.defaultValue 0
                    updateRuntime model (fun s ->
                        let rt = { s.Runtime with GoldStatus = Map.add qid correct rt.GoldStatus; GoldFails = fails }
                        if not correct && failCount >= 2 then
                            { rt with
                                StepIx = Study.retryStepIx s.Config rt.PhaseIx rt.StepIx
                                OverlayOpen = true }
                        else rt)
            | _ -> model

        | StudyCompletionCode code ->
            updateRuntime model (fun s -> { s.Runtime with CompletionCode = Some code })
        | StudyCompletionFailed reason ->
            { model with DebugLog = model.DebugLog.InsertAt(0, sprintf "completion refused: %s" reason) }

        // §9 P4: "Set as final" exists only in study mode; posts final
        // transforms and fires the finalRestored event (via StudyEvents).
        | StudySetAsFinal ->
            match model.Study with
            | Some (StudyActive s) ->
                postTransforms model s.SessionId "final"
                { model with Toast = Some "Current state marked as final" }
            | _ -> model

        | StudySetChoice (qid, ix) ->
            setDraft env model qid (fun d -> { d with Value = Some (AChoice ix) })
        | StudySetNumber (qid, v) ->
            setDraft env model qid (fun d -> { d with Value = Some (ANumber v) })
        | StudySetText (qid, t) ->
            setDraft env model qid (fun d -> { d with Value = Some (AText t) })
        | StudySetGridItem (qid, item, v) ->
            setDraft env model qid (fun d ->
                let grid = match d.Value with Some (AGrid g) -> g | _ -> Map.empty
                { d with Value = Some (AGrid (Map.add item v grid)) })
        | StudySetConfidence (qid, c) ->
            setDraft env model qid (fun d -> { d with Confidence = Some c })

        | StudyArmSceneClick qid ->
            // The armed pick owns the next tap — an active pin placement
            // would otherwise keep its ghost preview under the crosshair.
            let scanPins =
                match model.ScanPins.Placement with
                | AnchorPlacement -> { model.ScanPins with Placement = PlacementIdle }
                | _ -> model.ScanPins
            updateRuntime { model with ScanPins = scanPins }
                (fun s -> { s.Runtime with SceneClickArm = Some qid })
        | StudyCancelSceneClick ->
            updateRuntime model (fun s -> { s.Runtime with SceneClickArm = None })
        | StudySceneClickHit world ->
            match model.Study with
            | Some (StudyActive s) ->
                match s.Runtime.SceneClickArm with
                | Some qid ->
                    let model =
                        updateRuntime model (fun s ->
                            { s.Runtime with
                                Flags = Map.add qid world s.Runtime.Flags
                                SceneClickArm = None })
                    setDraft env model qid (fun d -> { d with Value = Some (APoint world) })
                | None -> model
            | _ -> model

    // Update-loop postlude (runs after every reducer step while a study is
    // active): derive telemetry events, feed the predicate counts, advance
    // Seq milestones, refresh StepSatisfied — and fire the transforms post
    // that §8 ties to commits.
    let postlude (env : Env<Message>) (before : Model) (after : Model) (msg : Message) : Model =
        match after.Study with
        | Some (StudyActive s) ->
            // A blocked (gated) message no-opped in the reducer — it must not
            // count toward predicates or telemetry either.
            let events = if blocked before msg then [] else StudyEvents.derive before after msg
            if List.isEmpty events then after
            else
                for etype, payload in events do
                    StudyTelemetry.record etype payload
                if events |> List.exists (fun (t, _) -> t = "phaseEnter" || t = "stepComplete") then
                    StudyTelemetry.flushNow ()
                if events |> List.exists (fst >> (=) "pinInMoving") then
                    env.Emit [ShowToast "Heads up: this pin sits on the moving region — stable terrain anchors registration better"]
                match events |> List.tryFind (fst >> (=) "committed") with
                | Some _ ->
                    postTransforms after s.SessionId (sprintf "commit#%d" after.RegistrationLog.Length)
                | None -> ()
                let rt = Study.feedEvents (List.map fst events) s.Runtime
                let rt = Study.reevaluate s.Config rt (Study.isTutorialPhase s.Config rt.PhaseIx)
                { after with Study = Some (StudyActive { s with Runtime = rt }) }
        | _ -> after
