module StudyConfig

// Study definitions on disk: studies/{studyId}/config.json (public, served to
// the client) + secret.json (never served: planted answers, TRE check points,
// gold thresholds). Parsing + startup validation live here, Giraffe-free, so
// Supertests can compile this file directly.

open System
open System.IO
open System.Text.Json
open Aardvark.Base
open Superprojekt

type CheckPair = { Ref : V3d; Mov : V3d }

type SecretAnswer =
    | SecretChoice of int
    | SecretNumber of value : float * tol : float
    | SecretPolygon of V2d[]          // sceneClick: accepted region (world XY)

type StudySecret = {
    Answers           : Map<string, SecretAnswer>
    // per moving mesh: corresponding point pairs on stable / moving terrain.
    CheckPoints       : Map<string, CheckPair[] * CheckPair[]>
    GoldFailThreshold : int
}

module StudySecret =
    let private v3 (e : JsonElement) =
        let a = e.EnumerateArray() |> Seq.map (fun x -> x.GetDouble()) |> Array.ofSeq
        V3d(a.[0], a.[1], a.[2])

    let private pairs (e : JsonElement) =
        e.EnumerateArray()
        |> Seq.map (fun p -> { Ref = v3 (p.GetProperty "ref"); Mov = v3 (p.GetProperty "mov") })
        |> Array.ofSeq

    let parse (root : JsonElement) : StudySecret =
        let answers =
            match root.TryGetProperty "answers" with
            | true, a when a.ValueKind = JsonValueKind.Object ->
                a.EnumerateObject()
                |> Seq.map (fun p ->
                    let v = p.Value
                    let ans =
                        match v.ValueKind with
                        | JsonValueKind.Number -> SecretChoice (v.GetInt32())
                        | JsonValueKind.Object ->
                            match v.TryGetProperty "polygon" with
                            | true, poly ->
                                SecretPolygon (
                                    poly.EnumerateArray()
                                    |> Seq.map (fun q ->
                                        let a = q.EnumerateArray() |> Seq.map (fun x -> x.GetDouble()) |> Array.ofSeq
                                        V2d(a.[0], a.[1]))
                                    |> Array.ofSeq)
                            | _ ->
                                let value = v.GetProperty("value").GetDouble()
                                let tol =
                                    match v.TryGetProperty "tol" with
                                    | true, t -> t.GetDouble()
                                    | _ -> 0.0
                                SecretNumber (value, tol)
                        | _ -> failwithf "unsupported secret answer for %s" p.Name
                    p.Name, ans)
                |> Map.ofSeq
            | _ -> Map.empty
        let checkPoints =
            match root.TryGetProperty "checkPoints" with
            | true, c when c.ValueKind = JsonValueKind.Object ->
                c.EnumerateObject()
                |> Seq.map (fun p ->
                    let stable = match p.Value.TryGetProperty "stable" with true, v -> pairs v | _ -> [||]
                    let moving = match p.Value.TryGetProperty "moving" with true, v -> pairs v | _ -> [||]
                    p.Name, (stable, moving))
                |> Map.ofSeq
            | _ -> Map.empty
        let threshold =
            match root.TryGetProperty "goldFailThreshold" with
            | true, t -> t.GetInt32()
            | _ -> 3
        { Answers = answers; CheckPoints = checkPoints; GoldFailThreshold = threshold }

type LoadedStudy = {
    Id         : string
    PublicJson : string            // served verbatim as configPublic
    Public     : StudyConfigPublic
    Secret     : StudySecret
}

// ───────────────────────────── validation ──────────────────────────────

let validate
        (datasetExists : string -> bool)
        (publicJson : string)
        (cfg : StudyConfigPublic)
        (secret : StudySecret) : string list =
    let errs = ResizeArray<string>()
    let err fmt = Printf.kprintf errs.Add fmt

    if String.IsNullOrWhiteSpace cfg.StudyId then err "studyId missing"
    if not (datasetExists cfg.DatasetTutorial) then err "datasetTutorial '%s' does not exist" cfg.DatasetTutorial
    if not (datasetExists cfg.DatasetMain) then err "datasetMain '%s' does not exist" cfg.DatasetMain

    for KeyValue(cond, disabled) in cfg.DisabledFeatures do
        if cond <> "FULL" && cond <> "NUM" then err "unknown condition '%s'" cond
        for f in disabled do
            if not (List.contains f StudyFeature.all) then err "condition %s disables unknown feature '%s'" cond f

    let allQuestions =
        cfg.Phases |> List.collect (fun ph -> ph.Steps |> List.choose (fun st -> st.Question))
    let qids = allQuestions |> List.map (fun q -> q.Id)
    if List.length qids <> List.length (List.distinct qids) then err "duplicate question ids"

    // Questionnaire steps answer one grid item per item — their ids count too.
    let answerableIds =
        qids @ (cfg.Phases |> List.collect (fun ph ->
            ph.Steps |> List.choose (fun st ->
                match st.Kind with KQuestionnaire _ -> st.Question |> Option.map (fun q -> q.Id) | _ -> None)))
        |> Set.ofList

    for ph in cfg.Phases do
        if List.isEmpty ph.Steps then err "phase %s has no steps" ph.Id
        match ph.Dataset with
        | Some d when d <> "tutorial" && d <> "main" -> err "phase %s: unknown dataset tag '%s'" ph.Id d
        | _ -> ()
        for f in ph.AllowedFeatures do
            if not (List.contains f StudyFeature.all) then err "phase %s allows unknown feature '%s'" ph.Id f
        let stepIds = ph.Steps |> List.map (fun s -> s.Id)
        if List.length stepIds <> List.length (List.distinct stepIds) then err "phase %s: duplicate step ids" ph.Id
        for st in ph.Steps do
            match st.Anchor with
            | Some a when not (List.contains a StudyFeature.anchors) ->
                err "step %s/%s: unknown anchor '%s'" ph.Id st.Id a
            | _ -> ()
            match st.Kind with
            | KQuestion when st.Question.IsNone -> err "step %s/%s: question step without question" ph.Id st.Id
            | KQuestionnaire key when not (Map.containsKey key cfg.Questionnaires) ->
                err "step %s/%s: unknown questionnaire '%s'" ph.Id st.Id key
            | _ -> ()
            match st.RetryStepId with
            | Some rid when not (List.contains rid stepIds) ->
                err "step %s/%s: retryStep '%s' not in phase" ph.Id st.Id rid
            | _ -> ()
            let events, answers = Predicate.references st.Completion
            for et in events do
                if not (List.contains et StudyEvent.all) then
                    err "step %s/%s: predicate references unknown event '%s'" ph.Id st.Id et
            for qid in answers do
                if not (Set.contains qid answerableIds) then
                    err "step %s/%s: predicate references unknown question '%s'" ph.Id st.Id qid

    for q in allQuestions do
        if q.Gold && not (Map.containsKey q.Id secret.Answers) then
            err "gold question '%s' has no secret answer" q.Id

    // The public file must not smuggle planted answers — reject suspicious
    // keys outright (the served JSON is this file verbatim).
    let rec scanKeys (e : JsonElement) =
        match e.ValueKind with
        | JsonValueKind.Object ->
            for p in e.EnumerateObject() do
                let n = p.Name.ToLowerInvariant()
                if n.Contains "secret" || n = "answers" || n = "checkpoints" || n = "goldanswer" then
                    err "config.json contains forbidden key '%s'" p.Name
                scanKeys p.Value
        | JsonValueKind.Array -> for x in e.EnumerateArray() do scanKeys x
        | _ -> ()
    scanKeys (JsonDocument.Parse(publicJson).RootElement)

    List.ofSeq errs

// ─────────────────────────── disk discovery ────────────────────────────

// studies/ resolved like MeshLoader's data/: walk up from the app base dir.
let private findStudiesRoot () =
    let mutable dir = AppContext.BaseDirectory
    let mutable result = None
    while result.IsNone && not (isNull dir) do
        let candidate = Path.Combine(dir, "studies")
        if Directory.Exists candidate then result <- Some candidate
        else dir <- Path.GetDirectoryName dir
    result

let studiesRoot = lazy (findStudiesRoot ())

let loadStudy (datasetExists : string -> bool) (dir : string) : Result<LoadedStudy, string list> =
    try
        let publicJson = File.ReadAllText (Path.Combine(dir, "config.json"))
        let cfg = StudyConfig.parsePublicString publicJson
        let secretPath = Path.Combine(dir, "secret.json")
        let secret =
            if File.Exists secretPath then
                StudySecret.parse (JsonDocument.Parse(File.ReadAllText secretPath).RootElement)
            else { Answers = Map.empty; CheckPoints = Map.empty; GoldFailThreshold = 3 }
        match validate datasetExists publicJson cfg secret with
        | [] -> Result.Ok { Id = Path.GetFileName dir; PublicJson = publicJson; Public = cfg; Secret = secret }
        | errs -> Result.Error errs
    with ex -> Result.Error [ ex.Message ]

// Discover + validate every study under a root; invalid studies are refused
// (reasons returned for the caller to log) and never served. The
// MeshLoader-backed cache lives in StudyHandlers so this file stays
// compilable in Supertests.
let loadAll (datasetExists : string -> bool) (root : string) =
    let mutable ok = Map.empty
    let rejected = ResizeArray()
    if Directory.Exists root then
        for dir in Directory.GetDirectories root do
            if File.Exists (Path.Combine(dir, "config.json")) then
                match loadStudy datasetExists dir with
                | Result.Ok s -> ok <- Map.add s.Id s ok
                | Result.Error errs -> rejected.Add (Path.GetFileName dir, errs)
    ok, List.ofSeq rejected
