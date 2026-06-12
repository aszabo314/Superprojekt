namespace Superprojekt

// User-study mode: shared, WASM-free model types — config DTOs + parser,
// predicate engine, feature ids, telemetry event types. Compiled into the
// client, the server (config validation) and Supertests, so keep this file
// free of Aardvark.Dom / ASP.NET dependencies.

open System
open System.Text.Json
open Aardvark.Base

type StudyCondition =
    | CondFull
    | CondNum

module StudyCondition =
    let tag = function CondFull -> "FULL" | CondNum -> "NUM"
    let ofTag (s : string) = if s = "NUM" then CondNum else CondFull

module StudyFeature =
    // Fixed feature-id list (spec §5) — config validation rejects unknown ids.
    let all =
        [ "navigation"; "layerCycle"; "pinPlace"; "pinEdit"; "pinCard"
          "violinChart"; "hoverProbe"; "heatmap"; "heatmapDiff"
          "threeSourceBar"; "splitViolinPreview"; "registrationCard"
          "coarseSolve"; "fineSolve"; "commit"; "rollback"
          "meshPanel"; "errorMetadata"; "contactRings" ]

    // Element ids guided-action tooltips may anchor to.
    let anchors =
        [ "viewport"; "pinButton"; "pinList"; "registrationCard"; "meshPanel" ]

module StudyEvent =
    // Fixed telemetry event-type list (spec §8) plus the config-only synthetic
    // types referenced by §9 predicates.
    let all =
        [ "sessionStart"; "sessionResumed"; "phaseEnter"; "stepEnter"
          "stepComplete"; "orbit"; "zoom"; "layerCycled"; "soloToggled"
          "meshVisToggled"; "pinPlaced"; "pinCommitted"; "pinDeleted"
          "anchorSet"; "anchorAccepted"; "correspondenceToggled"
          "coarseSolved"; "fineSolved"; "previewShown"; "committed"
          "rolledBack"; "discarded"; "heatmapMode"; "cardOpened"; "cardClosed"
          "chartHover"; "questionShown"; "answerChanged"; "flagMarked"
          "fpsSample"; "error"; "pinInMoving"; "solveAlternativeRun"
          "finalRestored" ]

    // Low-value high-frequency types: dropped first when the queue overflows.
    let throttled = [ "orbit"; "zoom"; "chartHover"; "fpsSample" ]

// ─────────────────────────── predicate engine ───────────────────────────

type Predicate =
    | PAlways
    | PEvent of eventType : string * minCount : int
    | PAnd of Predicate list
    | POr of Predicate list
    | PSeq of Predicate list
    | PAnswerSubmitted of questionId : string

module Predicate =
    // Event counts are cumulative since the last dataset switch (see
    // IMPLEMENTATION_NOTES — the spec's §9 example predicates only check out
    // under this reading). Seq nodes additionally keep a monotone progress
    // counter per structural path: stage k is only evaluated once stages
    // 0..k-1 completed, and a completed stage never un-completes.

    let rec private satisfiedAt
            (counts : Map<string, int>)
            (answered : string -> bool)
            (progress : Map<string, int>)
            (path : string)
            (p : Predicate) : bool =
        match p with
        | PAlways -> true
        | PEvent (et, n) -> (Map.tryFind et counts |> Option.defaultValue 0) >= n
        | PAnswerSubmitted qid -> answered qid
        | PAnd ps ->
            ps |> List.mapi (fun i x -> satisfiedAt counts answered progress (sprintf "%s.%d" path i) x)
               |> List.forall id
        | POr ps ->
            ps |> List.mapi (fun i x -> satisfiedAt counts answered progress (sprintf "%s.%d" path i) x)
               |> List.exists id
        | PSeq ps ->
            (Map.tryFind path progress |> Option.defaultValue 0) >= List.length ps

    // Advance every Seq stage that became satisfiable; call after counts or
    // answers changed, then read `satisfied`.
    let rec private advanceAt
            (counts : Map<string, int>)
            (answered : string -> bool)
            (path : string)
            (p : Predicate)
            (progress : Map<string, int>) : Map<string, int> =
        match p with
        | PAlways | PEvent _ | PAnswerSubmitted _ -> progress
        | PAnd ps | POr ps ->
            ps |> List.mapi (fun i x -> i, x)
               |> List.fold (fun pr (i, x) -> advanceAt counts answered (sprintf "%s.%d" path i) x pr) progress
        | PSeq ps ->
            let arr = List.toArray ps
            let mutable pr = progress
            let mutable k = Map.tryFind path pr |> Option.defaultValue 0
            let mutable go = true
            while go && k < arr.Length do
                let childPath = sprintf "%s.%d" path k
                pr <- advanceAt counts answered childPath arr.[k] pr
                if satisfiedAt counts answered pr childPath arr.[k] then k <- k + 1
                else go <- false
            Map.add path k pr

    let advance counts answered (p : Predicate) (progress : Map<string, int>) =
        advanceAt counts answered "r" p progress

    let satisfied counts answered (p : Predicate) (progress : Map<string, int>) =
        satisfiedAt counts answered progress "r" p

    // Every event type / question id referenced — for config validation.
    let rec references (p : Predicate) : (string list * string list) =
        match p with
        | PAlways -> [], []
        | PEvent (et, _) -> [ et ], []
        | PAnswerSubmitted q -> [], [ q ]
        | PAnd ps | POr ps | PSeq ps ->
            let parts = ps |> List.map references
            parts |> List.collect fst, parts |> List.collect snd

    // JSON form: {"event":"orbit","min":1} | {"and":[…]} | {"or":[…]} |
    // {"seq":[…]} | {"answer":"T1"} | true
    let rec parse (e : JsonElement) : Predicate =
        match e.ValueKind with
        | JsonValueKind.True -> PAlways
        | JsonValueKind.Object ->
            let tryArr (name : string) =
                match e.TryGetProperty name with
                | true, v when v.ValueKind = JsonValueKind.Array ->
                    Some (v.EnumerateArray() |> Seq.map parse |> List.ofSeq)
                | _ -> None
            match e.TryGetProperty "event" with
            | true, ev ->
                let n =
                    match e.TryGetProperty "min" with
                    | true, m -> m.GetInt32()
                    | _ -> 1
                PEvent (ev.GetString(), n)
            | _ ->
                match e.TryGetProperty "answer" with
                | true, a -> PAnswerSubmitted (a.GetString())
                | _ ->
                    match tryArr "and", tryArr "or", tryArr "seq" with
                    | Some ps, _, _ -> PAnd ps
                    | _, Some ps, _ -> POr ps
                    | _, _, Some ps -> PSeq ps
                    | _ -> failwith "unrecognised predicate object"
        | _ -> failwith "unrecognised predicate"

// ───────────────────────────── config DTOs ──────────────────────────────

type QuestionKind =
    | SingleChoice of options : string[]
    | SceneClick
    | NumericQ of unit_ : string
    | FreeTextQ of minLen : int
    | LikertGrid of items : string[] * points : int   // points = scale steps; 101 → 0–100 slider

type StudyQuestion = {
    Id         : string
    Kind       : QuestionKind
    Confidence : bool
    Gold       : bool
    // Pre-rendered flag marker for measure-change questions (world metres).
    FlagPoint  : V3d option
}

type StepKind =
    | KInstruction
    | KGuidedAction
    | KQuestion
    | KQuestionnaire of key : string

type StudyStep = {
    Id         : string
    Kind       : StepKind
    Body       : string
    Anchor     : string option
    Completion : Predicate
    Question   : StudyQuestion option
    Optional   : bool
    // Tutorial gold questions: step shown again after 2 fails (defaults to
    // the nearest preceding guidedAction).
    RetryStepId : string option
}

type StudyPhase = {
    Id              : string
    Title           : string
    GoalLine        : string
    Dataset         : string option        // "tutorial" | "main"
    AllowedFeatures : string list
    Steps           : StudyStep list
}

type StudyConfigPublic = {
    StudyId          : string
    Title            : string
    DatasetTutorial  : string
    DatasetMain      : string
    DisabledFeatures : Map<string, string list>   // condition tag → feature ids
    Phases           : StudyPhase list
    Questionnaires   : Map<string, string[]>
    // Coarse, non-secret outline of the moving region (world XY metres) for
    // the soft pin-placement warning (§9 P2).
    MovingPolygon    : V2d[]
}

module StudyConfig =
    let private str (e : JsonElement) (name : string) (fallback : string) =
        match e.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
        | _ -> fallback

    let private strOpt (e : JsonElement) (name : string) =
        match e.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.String -> Some (v.GetString())
        | _ -> None

    let private boolAt (e : JsonElement) (name : string) (fallback : bool) =
        match e.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.True -> true
        | true, v when v.ValueKind = JsonValueKind.False -> false
        | _ -> fallback

    let private strings (e : JsonElement) =
        e.EnumerateArray() |> Seq.map (fun x -> x.GetString()) |> Array.ofSeq

    let likertPoints (questionnaireKey : string) =
        // §7: SUS 10×5-pt, Raw-TLX 6 sliders 0–100, ICE-T 7-pt.
        match questionnaireKey with
        | "sus" -> 5
        | "tlx" -> 101
        | _ -> 7

    let parseQuestion (e : JsonElement) : StudyQuestion =
        let kind =
            match str e "type" "" with
            | "singleChoice" ->
                let opts = match e.TryGetProperty "options" with true, v -> strings v | _ -> [||]
                SingleChoice opts
            | "sceneClick" -> SceneClick
            | "numeric" -> NumericQ (str e "unit" "")
            | "freeText" ->
                let minLen = match e.TryGetProperty "minLength" with true, v -> v.GetInt32() | _ -> 0
                FreeTextQ minLen
            | "likertGrid" ->
                let items = match e.TryGetProperty "items" with true, v -> strings v | _ -> [||]
                let points = match e.TryGetProperty "points" with true, v -> v.GetInt32() | _ -> 7
                LikertGrid (items, points)
            | other -> failwithf "unknown question type %s" other
        let flag =
            match e.TryGetProperty "flagPoint" with
            | true, v when v.ValueKind = JsonValueKind.Array ->
                let a = v.EnumerateArray() |> Seq.map (fun x -> x.GetDouble()) |> Array.ofSeq
                Some (V3d(a.[0], a.[1], a.[2]))
            | _ -> None
        {
            Id         = str e "id" ""
            Kind       = kind
            Confidence = boolAt e "confidence" false
            Gold       = boolAt e "gold" false
            FlagPoint  = flag
        }

    let parseStep (e : JsonElement) : StudyStep =
        let question =
            match e.TryGetProperty "question" with
            | true, q when q.ValueKind = JsonValueKind.Object -> Some (parseQuestion q)
            | _ -> None
        let kind =
            match str e "kind" "instruction" with
            | "guidedAction" -> KGuidedAction
            | "question" -> KQuestion
            | "questionnaire" -> KQuestionnaire (str e "questionnaire" "")
            | _ -> KInstruction
        let completion =
            match e.TryGetProperty "completion" with
            | true, c when c.ValueKind <> JsonValueKind.Null -> Predicate.parse c
            | _ ->
                match kind, question with
                | KQuestion, Some q -> PAnswerSubmitted q.Id
                | _ -> PAlways
        {
            Id          = str e "id" ""
            Kind        = kind
            Body        = str e "body" ""
            Anchor      = strOpt e "anchor"
            Completion  = completion
            Question    = question
            Optional    = boolAt e "optional" false
            RetryStepId = strOpt e "retryStep"
        }

    let parsePhase (e : JsonElement) : StudyPhase =
        {
            Id       = str e "id" ""
            Title    = str e "title" ""
            GoalLine = str e "goalLine" ""
            Dataset  = strOpt e "dataset"
            AllowedFeatures =
                match e.TryGetProperty "allowedFeatures" with
                | true, v -> strings v |> List.ofArray
                | _ -> []
            Steps =
                match e.TryGetProperty "steps" with
                | true, v -> v.EnumerateArray() |> Seq.map parseStep |> List.ofSeq
                | _ -> []
        }

    let parsePublic (root : JsonElement) : StudyConfigPublic =
        let conditions =
            match root.TryGetProperty "conditions" with
            | true, c when c.ValueKind = JsonValueKind.Object ->
                c.EnumerateObject()
                |> Seq.map (fun p ->
                    let dis =
                        match p.Value.TryGetProperty "disabledFeatures" with
                        | true, v -> strings v |> List.ofArray
                        | _ -> []
                    p.Name, dis)
                |> Map.ofSeq
            | _ -> Map.empty
        let questionnaires =
            match root.TryGetProperty "questionnaires" with
            | true, q when q.ValueKind = JsonValueKind.Object ->
                q.EnumerateObject() |> Seq.map (fun p -> p.Name, strings p.Value) |> Map.ofSeq
            | _ -> Map.empty
        let polygon =
            match root.TryGetProperty "movingPolygon" with
            | true, v when v.ValueKind = JsonValueKind.Array ->
                v.EnumerateArray()
                |> Seq.map (fun p ->
                    let a = p.EnumerateArray() |> Seq.map (fun x -> x.GetDouble()) |> Array.ofSeq
                    V2d(a.[0], a.[1]))
                |> Array.ofSeq
            | _ -> [||]
        {
            StudyId          = str root "studyId" ""
            Title            = str root "title" ""
            DatasetTutorial  = str root "datasetTutorial" ""
            DatasetMain      = str root "datasetMain" ""
            DisabledFeatures = conditions
            Phases =
                match root.TryGetProperty "phases" with
                | true, v -> v.EnumerateArray() |> Seq.map parsePhase |> List.ofSeq
                | _ -> []
            Questionnaires   = questionnaires
            MovingPolygon    = polygon
        }

    let parsePublicString (json : string) =
        parsePublic (JsonDocument.Parse(json).RootElement)

    // 2D point-in-polygon (even-odd) for the moving-region soft warning.
    let insidePolygon (poly : V2d[]) (p : V2d) =
        if poly.Length < 3 then false
        else
            let mutable inside = false
            let mutable j = poly.Length - 1
            for i in 0 .. poly.Length - 1 do
                let a = poly.[i]
                let b = poly.[j]
                if (a.Y > p.Y) <> (b.Y > p.Y)
                   && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X then
                    inside <- not inside
                j <- i
            inside

// ─────────────────────────── client runtime ────────────────────────────

type AnswerValue =
    | AChoice of int
    | APoint of V3d
    | ANumber of float
    | AText of string
    | AGrid of Map<int, float>

type AnswerDraft = {
    Value      : AnswerValue option
    Confidence : int option
}

module AnswerDraft =
    let empty = { Value = None; Confidence = None }

type StudyRuntime = {
    PhaseIx       : int
    StepIx        : int
    AnswersDraft  : Map<string, AnswerDraft>
    // Server-confirmed tutorial gold correctness per question id.
    GoldStatus    : Map<string, bool>
    GoldFails     : Map<string, int>
    // Cumulative since last dataset switch (predicate input).
    EventCounts   : Map<string, int>
    // Seq milestone progress, keyed "phaseIx:stepIx" + structural path.
    SeqProgress   : Map<string, Map<string, int>>
    StepSatisfied : bool
    OverlayOpen   : bool
    SceneClickArm : string option      // question id armed for a 3D pick
    Flags         : Map<string, V3d>   // marked scene points per question id
    CompletionCode : string option
    CommitCount   : int                // labels commit#1, commit#2, …
    AdvancePosted : Set<string>        // "phaseId/stepId" already advanced
    // §10 reload: progress kept, scene reset — shown until the next step.
    ResumedNotice : bool
}

type StudySession = {
    SessionId : string
    Condition : StudyCondition
    Demo      : bool
    Config    : StudyConfigPublic
    Runtime   : StudyRuntime
}

// Full-screen study pages outside the running app.
type StudyShell =
    | StudyJoining
    | StudyFailed of message : string
    | StudyScreened
    | StudyActive of StudySession

// /api/study/session response (shared between StudyApi and the reducer).
type StudySessionInit = {
    SessionId : string
    Condition : StudyCondition
    Demo      : bool
    Resumed   : bool
    LastStep  : (string * string) option   // last advanced (phaseId, stepId)
    Config    : StudyConfigPublic
}

module StudyRuntime =
    let initial = {
        PhaseIx       = 0
        StepIx        = 0
        AnswersDraft  = Map.empty
        GoldStatus    = Map.empty
        GoldFails     = Map.empty
        EventCounts   = Map.empty
        SeqProgress   = Map.empty
        StepSatisfied = false
        OverlayOpen   = true
        SceneClickArm = None
        Flags         = Map.empty
        CompletionCode = None
        CommitCount   = 0
        AdvancePosted = Set.empty
        ResumedNotice = false
    }

module Study =
    let phaseAt (cfg : StudyConfigPublic) (ix : int) = List.tryItem ix cfg.Phases
    let stepAt (cfg : StudyConfigPublic) (phaseIx : int) (stepIx : int) =
        phaseAt cfg phaseIx |> Option.bind (fun p -> List.tryItem stepIx p.Steps)

    let currentPhase (s : StudySession) = phaseAt s.Config s.Runtime.PhaseIx
    let currentStep (s : StudySession) = stepAt s.Config s.Runtime.PhaseIx s.Runtime.StepIx

    let datasetOf (cfg : StudyConfigPublic) (phase : StudyPhase) =
        match phase.Dataset with
        | Some "main" -> Some cfg.DatasetMain
        | Some "tutorial" -> Some cfg.DatasetTutorial
        | _ -> None

    // Dataset in effect at a phase = nearest dataset tag at or before it.
    let datasetAtPhase (cfg : StudyConfigPublic) (phaseIx : int) =
        cfg.Phases
        |> List.truncate (phaseIx + 1)
        |> List.rev
        |> List.tryPick (datasetOf cfg)

    let disabledFor (s : StudySession) =
        Map.tryFind (StudyCondition.tag s.Condition) s.Config.DisabledFeatures
        |> Option.defaultValue []

    // §5: visible = phase.allowedFeatures ∩ ¬condition.disabledFeatures.
    let featureVisibleIn (s : StudySession) (featureId : string) =
        match currentPhase s with
        | Some ph ->
            List.contains featureId ph.AllowedFeatures
            && not (List.contains featureId (disabledFor s))
        | None -> false

    // Full mode (no study) → everything visible.
    let featureVisible (shell : StudyShell option) (featureId : string) =
        match shell with
        | Some (StudyActive s) -> featureVisibleIn s featureId
        | Some _ -> false
        | None -> true

    let isActive (shell : StudyShell option) =
        match shell with Some (StudyActive _) -> true | _ -> false

    // Questionnaire steps synthesize their grid question from the config's
    // questionnaires map (sus → 5-pt, tlx → 0–100 sliders, icet → 7-pt).
    let effectiveQuestion (cfg : StudyConfigPublic) (step : StudyStep) : StudyQuestion option =
        match step.Question with
        | Some q -> Some q
        | None ->
            match step.Kind with
            | KQuestionnaire key ->
                Map.tryFind key cfg.Questionnaires
                |> Option.map (fun items ->
                    { Id = key
                      Kind = LikertGrid (items, StudyConfig.likertPoints key)
                      Confidence = false
                      Gold = false
                      FlagPoint = None })
            | _ -> None

    let private answeredKind (q : StudyQuestion) (draft : AnswerDraft) =
        let valueOk =
            match q.Kind, draft.Value with
            | _, None -> false
            | FreeTextQ minLen, Some (AText t) -> t.Trim().Length >= minLen
            | LikertGrid (items, _), Some (AGrid g) ->
                items.Length > 0 && Array.init items.Length id |> Array.forall (fun i -> Map.containsKey i g)
            | _, Some _ -> true
        valueOk && (not q.Confidence || draft.Confidence.IsSome)

    let answerPresent (rt : StudyRuntime) (q : StudyQuestion) =
        match Map.tryFind q.Id rt.AnswersDraft with
        | Some d -> answeredKind q d
        | None -> false

    let private seqKey (rt : StudyRuntime) = sprintf "%d:%d" rt.PhaseIx rt.StepIx

    // Re-evaluate the current step's completion after events/answers changed:
    // advances Seq milestones (monotone, survives step re-entry) and updates
    // StepSatisfied per the §4 gating rules.
    let reevaluate (cfg : StudyConfigPublic) (rt : StudyRuntime) (tutorialGoldGate : bool) =
        match stepAt cfg rt.PhaseIx rt.StepIx with
        | None -> { rt with StepSatisfied = false }
        | Some step ->
            let question = effectiveQuestion cfg step
            let answered qid =
                match question with
                | Some q when q.Id = qid -> answerPresent rt q
                | _ -> rt.AnswersDraft |> Map.tryFind qid |> Option.map (fun d -> d.Value.IsSome) |> Option.defaultValue false
            let key = seqKey rt
            let progress0 = Map.tryFind key rt.SeqProgress |> Option.defaultValue Map.empty
            let progress = Predicate.advance rt.EventCounts answered step.Completion progress0
            let predOk = Predicate.satisfied rt.EventCounts answered step.Completion progress
            let kindOk =
                match step.Kind, question with
                | KInstruction, _ -> true
                | KGuidedAction, _ -> true
                | KQuestion, Some q ->
                    answerPresent rt q
                    && (not (tutorialGoldGate && q.Gold)
                        || (Map.tryFind q.Id rt.GoldStatus |> Option.defaultValue false))
                | KQuestion, None -> true
                | KQuestionnaire _, Some q -> answerPresent rt q
                | KQuestionnaire _, None -> false
            { rt with
                SeqProgress = Map.add key progress rt.SeqProgress
                StepSatisfied = predOk && kindOk }

    let feedEvents (types : string list) (rt : StudyRuntime) =
        let counts =
            types |> List.fold (fun m t ->
                Map.add t ((Map.tryFind t m |> Option.defaultValue 0) + 1) m) rt.EventCounts
        { rt with EventCounts = counts }

    // Whether the phase at `ix` is on the tutorial dataset (gold answers gate
    // progress only there, §4).
    let isTutorialPhase (cfg : StudyConfigPublic) (phaseIx : int) =
        match datasetAtPhase cfg phaseIx, cfg.DatasetTutorial with
        | Some d, t -> d = t
        | None, _ -> false

    // Position of the step *after* (phaseIx, stepIx); None = study over.
    let nextPosition (cfg : StudyConfigPublic) (phaseIx : int) (stepIx : int) =
        match phaseAt cfg phaseIx with
        | None -> None
        | Some ph ->
            if stepIx + 1 < List.length ph.Steps then Some (phaseIx, stepIx + 1)
            elif phaseIx + 1 < List.length cfg.Phases then Some (phaseIx + 1, 0)
            else None

    // Flat config order of steps as (phaseId, stepId) — server advance
    // validation and resume use the same order.
    let stepOrder (cfg : StudyConfigPublic) =
        cfg.Phases |> List.collect (fun ph -> ph.Steps |> List.map (fun st -> ph.Id, st.Id))

    // Tutorial retry target: explicit retryStep, else nearest preceding
    // guidedAction in the same phase, else step 0.
    let retryStepIx (cfg : StudyConfigPublic) (phaseIx : int) (stepIx : int) =
        match phaseAt cfg phaseIx with
        | None -> 0
        | Some ph ->
            let steps = List.toArray ph.Steps
            let explicitIx =
                match (if stepIx < steps.Length then steps.[stepIx].RetryStepId else None) with
                | Some rid -> steps |> Array.tryFindIndex (fun s -> s.Id = rid)
                | None -> None
            match explicitIx with
            | Some i -> i
            | None ->
                let mutable found = 0
                for i in 0 .. min (stepIx - 1) (steps.Length - 1) do
                    if steps.[i].Kind = KGuidedAction then found <- i
                found
