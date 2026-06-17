module StudyStore

// File-based study stores (no DB): per study `studies/{id}/data/` holds
// sessions.jsonl, events-{sid}.jsonl, answers-{sid}.jsonl, advance-{sid}.jsonl,
// transforms-{sid}.jsonl, workspace-{sid}.json, scores-{sid}.json. Append-only;
// writes serialized per session / per study via named locks. All functions
// take explicit paths so Supertests can run them against a temp dir.

open System
open System.IO
open System.Text
open System.Text.Json
open System.Security.Cryptography
open System.Collections.Concurrent
open Aardvark.Base
open Superprojekt
open StudyConfig

let private locks = ConcurrentDictionary<string, obj>()
let private lockFor (key : string) = locks.GetOrAdd(key, fun _ -> obj ())

let private esc (s : string) =
    let sb = StringBuilder()
    for c in s do
        match c with
        | '"' -> sb.Append "\\\"" |> ignore
        | '\\' -> sb.Append "\\\\" |> ignore
        | '\n' -> sb.Append "\\n" |> ignore
        | '\r' -> sb.Append "\\r" |> ignore
        | '\t' -> sb.Append "\\t" |> ignore
        | c when c < ' ' -> sb.Append(sprintf "\\u%04x" (int c)) |> ignore
        | c -> sb.Append c |> ignore
    sb.ToString()
let private q (s : string) = "\"" + esc s + "\""

let private appendLine (path : string) (line : string) =
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.AppendAllText(path, line + "\n")

let private readLines (path : string) =
    if File.Exists path then File.ReadAllLines path |> Array.filter (fun l -> l.Trim().Length > 0)
    else [||]

let dataDirOf (studiesRoot : string) (studyId : string) =
    Path.Combine(studiesRoot, studyId, "data")

// ───────────────────────────── sessions ─────────────────────────────────

type SessionRecord = {
    Sid       : string
    Token     : string option
    Condition : StudyCondition
    Demo      : bool
    CreatedAt : DateTime
    Status    : string                 // active | completed | screened
}

let private sessionLine (s : SessionRecord) =
    sprintf "{\"sid\":%s,\"token\":%s,\"condition\":%s,\"demo\":%b,\"createdAt\":%s,\"status\":%s}"
        (q s.Sid)
        (match s.Token with Some t -> q t | None -> "null")
        (q (StudyCondition.tag s.Condition))
        s.Demo
        (q (s.CreatedAt.ToString "O"))
        (q s.Status)

let private parseSession (line : string) =
    let e = JsonDocument.Parse(line).RootElement
    let tok =
        match e.TryGetProperty "token" with
        | true, v when v.ValueKind = JsonValueKind.String -> Some (v.GetString())
        | _ -> None
    {
        Sid       = e.GetProperty("sid").GetString()
        Token     = tok
        Condition = StudyCondition.ofTag (e.GetProperty("condition").GetString())
        Demo      = e.GetProperty("demo").GetBoolean()
        CreatedAt = DateTime.Parse(e.GetProperty("createdAt").GetString(), null, Globalization.DateTimeStyles.RoundtripKind)
        Status    = e.GetProperty("status").GetString()
    }

let private sessionsPath (dataDir : string) = Path.Combine(dataDir, "sessions.jsonl")

// Latest record per sid wins (status changes are appended as full records).
let readSessions (dataDir : string) : SessionRecord list =
    readLines (sessionsPath dataDir)
    |> Array.map parseSession
    |> Array.fold (fun (order, m) s ->
        (if Map.containsKey s.Sid m then order else s.Sid :: order), Map.add s.Sid s m)
        ([], Map.empty)
    |> fun (order, m) -> order |> List.rev |> List.map (fun sid -> m.[sid])

let findSession (dataDir : string) (sid : string) =
    readSessions dataDir |> List.tryFind (fun s -> s.Sid = sid)

let setStatus (dataDir : string) (sid : string) (status : string) =
    lock (lockFor (dataDir + "/sessions")) (fun () ->
        match findSession dataDir sid with
        | Some s when s.Status <> status -> appendLine (sessionsPath dataDir) (sessionLine { s with Status = status })
        | _ -> ())

// ────────────────────────────── tokens ──────────────────────────────────

let tokensPath (studiesRoot : string) (studyId : string) =
    Path.Combine(studiesRoot, studyId, "tokens.jsonl")

let readTokens (path : string) =
    readLines path
    |> Array.map (fun l -> JsonDocument.Parse(l).RootElement.GetProperty("token").GetString())

let generateTokens (path : string) (now : DateTime) (n : int) =
    lock (lockFor path) (fun () ->
        let fresh =
            Array.init n (fun _ ->
                let bytes = RandomNumberGenerator.GetBytes 9
                Convert.ToBase64String(bytes).Replace("+", "a").Replace("/", "b").Replace("=", ""))
        for t in fresh do
            appendLine path (sprintf "{\"token\":%s,\"createdAt\":%s}" (q t) (q (now.ToString "O")))
        fresh)

// ─────────────────────── session creation / resume ──────────────────────

type SessionStart =
    | Fresh   of SessionRecord
    | Resumed of SessionRecord
    | Refused of status : int * message : string

// Balanced assignment (§3): count non-demo sessions with status active or
// completed per condition, assign the smaller (tie → random); atomic under
// the per-study sessions lock.
let private balancedCondition (sessions : SessionRecord list) (rnd : Random) =
    let counted = sessions |> List.filter (fun s -> not s.Demo && (s.Status = "active" || s.Status = "completed"))
    let nFull = counted |> List.filter (fun s -> s.Condition = CondFull) |> List.length
    let nNum = counted |> List.filter (fun s -> s.Condition = CondNum) |> List.length
    if nFull < nNum then CondFull
    elif nNum < nFull then CondNum
    elif rnd.Next 2 = 0 then CondFull
    else CondNum

let createSession
        (dataDir : string)
        (tokensFile : string)
        (now : DateTime)
        (rnd : Random)
        (token : string option)
        (demo : bool)
        (demoCondition : StudyCondition option) : SessionStart =
    lock (lockFor (dataDir + "/sessions")) (fun () ->
        let sessions = readSessions dataDir
        if demo then
            let s = {
                Sid       = "demo-" + Guid.NewGuid().ToString("N").Substring(0, 12)
                Token     = None
                Condition = demoCondition |> Option.defaultValue CondFull
                Demo      = true
                CreatedAt = now
                Status    = "active"
            }
            appendLine (sessionsPath dataDir) (sessionLine s)
            Fresh s
        else
            match token with
            | None -> Refused (400, "token required")
            | Some tok ->
                if not (readTokens tokensFile |> Array.contains tok) then
                    Refused (403, "invalid token")
                else
                    // One token = one session (§10): an existing session for
                    // this token resumes if active, refuses otherwise.
                    match sessions |> List.tryFind (fun s -> s.Token = Some tok) with
                    | Some existing when existing.Status = "active" -> Resumed existing
                    | Some existing when existing.Status = "screened" -> Refused (409, "screened")
                    | Some _ -> Refused (409, "study already completed")
                    | None ->
                        let s = {
                            Sid       = Guid.NewGuid().ToString("N").Substring(0, 16)
                            Token     = Some tok
                            Condition = balancedCondition sessions rnd
                            Demo      = false
                            CreatedAt = now
                            Status    = "active"
                        }
                        appendLine (sessionsPath dataDir) (sessionLine s)
                        Fresh s)

// ─────────────────────────── per-session files ──────────────────────────

let private eventsPath dataDir sid = Path.Combine(dataDir, sprintf "events-%s.jsonl" sid)
let private answersPath dataDir sid = Path.Combine(dataDir, sprintf "answers-%s.jsonl" sid)
let private advancePath dataDir sid = Path.Combine(dataDir, sprintf "advance-%s.jsonl" sid)
let private transformsPath dataDir sid = Path.Combine(dataDir, sprintf "transforms-%s.jsonl" sid)
let workspacePath (dataDir : string) (sid : string) = Path.Combine(dataDir, sprintf "workspace-%s.json" sid)
let scoresPath (dataDir : string) (sid : string) = Path.Combine(dataDir, sprintf "scores-%s.json" sid)

let appendEvents (dataDir : string) (sid : string) (now : DateTime) (events : JsonElement seq) =
    lock (lockFor (dataDir + "/" + sid)) (fun () ->
        for e in events do
            let line =
                sprintf "{\"recv\":%s,\"event\":%s}" (q (now.ToString "O")) (e.GetRawText())
            appendLine (eventsPath dataDir sid) line)

// ────────────────────────────── answers ─────────────────────────────────

let private answerCorrect (secret : SecretAnswer) (value : JsonElement) =
    try
        match secret with
        | SecretChoice i -> value.ValueKind = JsonValueKind.Number && value.GetInt32() = i
        | SecretNumber (v, tol) ->
            value.ValueKind = JsonValueKind.Number && abs (value.GetDouble() - v) <= tol + 1e-12
        | SecretPolygon poly ->
            value.ValueKind = JsonValueKind.Array
            && (let a = value.EnumerateArray() |> Seq.map (fun x -> x.GetDouble()) |> Array.ofSeq
                a.Length >= 2 && StudyConfig.insidePolygon poly (V2d(a.[0], a.[1])))
    with _ -> false

type AnswerOutcome = {
    Correct  : bool option       // Some only for tutorial gold questions
    Screened : bool
}

// Append the answer; for tutorial-phase gold questions also evaluate
// correctness (the single exception to "scores never reach the client", §4)
// and screen out after `goldFailThreshold` distinct wrong submissions of one
// check.
let appendAnswer
        (study : LoadedStudy)
        (dataDir : string)
        (sid : string)
        (now : DateTime)
        (questionId : string)
        (value : JsonElement)
        (confidence : int option) : AnswerOutcome =
    lock (lockFor (dataDir + "/" + sid)) (fun () ->
        let questionPhase =
            study.Public.Phases
            |> List.mapi (fun i ph -> i, ph)
            |> List.tryFind (fun (_, ph) ->
                ph.Steps |> List.exists (fun st ->
                    match st.Question with Some qu -> qu.Id = questionId | None -> false))
        let question =
            questionPhase
            |> Option.bind (fun (_, ph) ->
                ph.Steps |> List.tryPick (fun st ->
                    st.Question |> Option.filter (fun qu -> qu.Id = questionId)))
        let tutorialGold =
            match questionPhase, question with
            | Some (ix, _), Some qu -> qu.Gold && Study.isTutorialPhase study.Public ix
            | _ -> false
        let correct =
            if tutorialGold then
                Map.tryFind questionId study.Secret.Answers
                |> Option.map (fun s -> answerCorrect s value)
            else None
        let line =
            sprintf "{\"t\":%s,\"questionId\":%s,\"value\":%s,\"confidence\":%s%s}"
                (q (now.ToString "O")) (q questionId) (value.GetRawText())
                (match confidence with Some c -> string c | None -> "null")
                (match correct with Some c -> sprintf ",\"correct\":%b" c | None -> "")
        appendLine (answersPath dataDir sid) line
        let screened =
            match correct with
            | Some false ->
                let fails =
                    readLines (answersPath dataDir sid)
                    |> Array.filter (fun l ->
                        let e = JsonDocument.Parse(l).RootElement
                        e.GetProperty("questionId").GetString() = questionId
                        && (match e.TryGetProperty "correct" with
                            | true, c -> c.ValueKind = JsonValueKind.False
                            | _ -> false))
                    |> Array.length
                fails >= study.Secret.GoldFailThreshold
            | _ -> false
        if screened then setStatus dataDir sid "screened"
        { Correct = correct; Screened = screened })

// ────────────────────────────── advance ─────────────────────────────────

let advancedSteps (dataDir : string) (sid : string) =
    readLines (advancePath dataDir sid)
    |> Array.map (fun l ->
        let e = JsonDocument.Parse(l).RootElement
        e.GetProperty("phaseId").GetString(), e.GetProperty("stepId").GetString())

// Order-validated progress mirror: accept only the next step in config order
// (or an idempotent repeat of an already-recorded one, which the tutorial
// retry path produces).
let recordAdvance
        (study : LoadedStudy)
        (dataDir : string)
        (sid : string)
        (now : DateTime)
        (phaseId : string)
        (stepId : string) : Result<unit, string> =
    lock (lockFor (dataDir + "/" + sid)) (fun () ->
        let order = Study.stepOrder study.Public
        if not (List.contains (phaseId, stepId) order) then
            Result.Error (sprintf "unknown step %s/%s" phaseId stepId)
        else
            let recorded = advancedSteps dataDir sid
            if recorded |> Array.contains (phaseId, stepId) then Result.Ok ()
            else
                match List.tryItem recorded.Length order with
                | Some expected when expected = (phaseId, stepId) ->
                    appendLine (advancePath dataDir sid)
                        (sprintf "{\"t\":%s,\"phaseId\":%s,\"stepId\":%s}" (q (now.ToString "O")) (q phaseId) (q stepId))
                    Result.Ok ()
                | Some (ep, es) -> Result.Error (sprintf "out of order: expected %s/%s" ep es)
                | None -> Result.Error "all steps already recorded")

let lastAdvance (dataDir : string) (sid : string) =
    advancedSteps dataDir sid |> Array.tryLast

// ─────────────────────── transforms + TRE scoring ───────────────────────

// TRE against secret check-point pairs: RMS of |T·mov − ref| over the stable
// and moving sets separately (§3). Never returned to clients.
let treFor (pairs : CheckPair[]) (t : M44d) =
    if pairs.Length = 0 then 0.0
    else
        let sum = pairs |> Array.sumBy (fun p -> (t.TransformPos p.Mov - p.Ref).LengthSquared)
        sqrt (sum / float pairs.Length)

let postTransforms
        (study : LoadedStudy)
        (dataDir : string)
        (sid : string)
        (now : DateTime)
        (label : string)
        (perMesh : Map<string, M44d>) =
    lock (lockFor (dataDir + "/" + sid)) (fun () ->
        let m44 (m : M44d) =
            [| m.M00; m.M01; m.M02; m.M03; m.M10; m.M11; m.M12; m.M13
               m.M20; m.M21; m.M22; m.M23; m.M30; m.M31; m.M32; m.M33 |]
            |> Array.map (fun v -> v.ToString("G17", Globalization.CultureInfo.InvariantCulture))
            |> String.concat ","
        let tj =
            perMesh |> Map.toSeq
            |> Seq.map (fun (mesh, t) -> sprintf "%s:[%s]" (q mesh) (m44 t))
            |> String.concat ","
        appendLine (transformsPath dataDir sid)
            (sprintf "{\"t\":%s,\"label\":%s,\"perMesh\":{%s}}" (q (now.ToString "O")) (q label) tj)
        // Score every mesh that has check points; missing meshes score at
        // identity (the participant never moved them).
        let scores =
            study.Secret.CheckPoints
            |> Map.toSeq
            |> Seq.map (fun (mesh, (stable, moving)) ->
                let t = Map.tryFind mesh perMesh |> Option.defaultValue M44d.Identity
                let f (v : float) = v.ToString("G17", Globalization.CultureInfo.InvariantCulture)
                sprintf "%s:{\"stable\":%s,\"moving\":%s}" (q mesh) (f (treFor stable t)) (f (treFor moving t)))
            |> String.concat ","
        let entry =
            sprintf "{\"t\":%s,\"label\":%s,\"tre\":{%s}}" (q (now.ToString "O")) (q label) scores
        // scores-{sid}.json is a JSON array, rewritten under the sid lock.
        let path = scoresPath dataDir sid
        let existing =
            if File.Exists path then
                let txt = (File.ReadAllText path).Trim().TrimStart('[').TrimEnd(']').Trim()
                if txt.Length > 0 then txt + "," else ""
            else ""
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllText(path, "[" + existing + entry + "]"))

let hasTransformsLabel (dataDir : string) (sid : string) (label : string) =
    readLines (transformsPath dataDir sid)
    |> Array.exists (fun l -> JsonDocument.Parse(l).RootElement.GetProperty("label").GetString() = label)

let saveWorkspace (dataDir : string) (sid : string) (json : string) =
    lock (lockFor (dataDir + "/" + sid)) (fun () ->
        let path = workspacePath dataDir sid
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllText(path, json))

// ───────────────────────── completion code ──────────────────────────────

// Server HMAC secret: studies/server-secret.txt, created on first use.
let serverSecret (studiesRoot : string) : byte[] =
    let path = Path.Combine(studiesRoot, "server-secret.txt")
    lock (lockFor path) (fun () ->
        if File.Exists path then Convert.FromHexString((File.ReadAllText path).Trim())
        else
            let bytes = RandomNumberGenerator.GetBytes 32
            Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
            File.WriteAllText(path, Convert.ToHexString bytes)
            bytes)

let completionCode (secret : byte[]) (sid : string) =
    use h = new HMACSHA256(secret)
    let mac = h.ComputeHash(Encoding.UTF8.GetBytes sid)
    Convert.ToHexString(mac).Substring(0, 8)

// §3: code issued only when every non-optional step has an advance record
// and a `final` transforms post exists.
let complete
        (study : LoadedStudy)
        (dataDir : string)
        (secret : byte[])
        (sid : string) : Result<string, string> =
    lock (lockFor (dataDir + "/" + sid)) (fun () ->
        let recorded = advancedSteps dataDir sid |> Set.ofArray
        let missing =
            study.Public.Phases
            |> List.collect (fun ph ->
                ph.Steps
                |> List.filter (fun st -> not st.Optional)
                |> List.map (fun st -> ph.Id, st.Id))
            |> List.filter (fun k -> not (Set.contains k recorded))
        if not (List.isEmpty missing) then
            Result.Error (sprintf "%d required steps missing" (List.length missing))
        elif not (hasTransformsLabel dataDir sid "final") then
            Result.Error "no final transforms post"
        else
            setStatus dataDir sid "completed"
            Result.Ok (completionCode secret sid))
