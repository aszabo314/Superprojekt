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

module StudyUpdate =

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

    // Deterministic clean state on study entry/exit: everything a dataset
    // switch resets, plus registration state (a participant always starts
    // from identity transforms).
    let private resetScene (model : Model) =
        { model with
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

    let handleMsg (env : Env<Message>) (model : Model) (msg : StudyMessage) : Model =
        match msg with
        | StudyJoin token ->
            join env (Some token) None
            { model with Study = Some StudyJoining }
        | StudyStartDemo (studyId, cond) ->
            join env None (Some (studyId, cond))
            { model with Study = Some StudyJoining; GearPopoverOpen = false }
        | StudySessionFailed message ->
            { model with Study = Some (StudyFailed message) }
        | StudySessionStarted init ->
            let phaseIx, stepIx = resumePosition init.Config init.LastStep
            let rt =
                { StudyRuntime.initial with PhaseIx = phaseIx; StepIx = stepIx }
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
            model
        | StudyExitDemo ->
            // Demo sessions only — real sessions have no way back (§1).
            match model.Study with
            | Some (StudyActive s) when s.Demo ->
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
