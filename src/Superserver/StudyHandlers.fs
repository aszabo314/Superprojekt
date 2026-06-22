module StudyHandlers

// HTTP surface of study mode (§3/§11). Stores + scoring in StudyStore; config
// parsing/validation in StudyConfig. Secret file + scores are reachable through
// NO route, and studies/ sits outside wwwroot so static hosting can't serve it.

open System
open System.Net
open System.Collections.Concurrent
open System.Text.Json
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open Giraffe
open Aardvark.Base
open Superprojekt
open StudyConfig

let private datasetExists (d : string) = MeshLoader.datasets () |> Array.contains d

// Loaded once; invalid studies are refused. logStartup is called from
// Program.fs so the refusal reasons land in the server log.
let private loaded =
    lazy (
        match studiesRoot.Value with
        | Some root -> StudyConfig.loadAll datasetExists root, Some root
        | None -> (Map.empty, []), None)

let private studyById (id : string) = Map.tryFind id (fst (fst loaded.Value))

let logStartup (log : ILogger) =
    let (ok, rejected), root = loaded.Value
    match root with
    | None -> log.LogInformation "no studies/ directory found — study mode disabled"
    | Some r ->
        log.LogInformation("studies root {Root}: {Count} valid ({Ids})", r, ok.Count, ok |> Map.toList |> List.map fst |> String.concat ", ")
        for id, errs in rejected do
            log.LogWarning("study '{Id}' refused: {Reasons}", id, String.concat " | " errs)

// sid → owning study (sids are globally unique guids; cached after first scan).
let private sidIndex = ConcurrentDictionary<string, string>()

let private studyOfSid (sid : string) : (LoadedStudy * string) option =
    let root = snd loaded.Value
    match root with
    | None -> None
    | Some rootDir ->
        let resolve () =
            fst (fst loaded.Value)
            |> Map.toSeq
            |> Seq.tryPick (fun (id, study) ->
                let dataDir = StudyStore.dataDirOf rootDir id
                match StudyStore.findSession dataDir sid with
                | Some _ -> Some (id, study)
                | None -> None)
        match sidIndex.TryGetValue sid with
        | true, id -> studyById id |> Option.map (fun s -> s, StudyStore.dataDirOf rootDir id)
        | _ ->
            match resolve () with
            | Some (id, study) ->
                sidIndex.[sid] <- id
                Some (study, StudyStore.dataDirOf rootDir id)
            | None -> None

// WriteStringAsync IS the pipeline result — discarding its task and calling next
// would race the body write against response completion.
let private jsonText (s : string) : HttpHandler =
    fun (_ : HttpFunc) (ctx : HttpContext) ->
        ctx.SetContentType "application/json; charset=utf-8"
        ctx.WriteStringAsync s

// ────────────────────────────── session ─────────────────────────────────

[<CLIMutable>]
type SessionRequest = { Token : string; Demo : bool; StudyId : string; Condition : string }

let sessionHandler : HttpHandler =
    fun next ctx -> task {
        let log = ctx.GetLogger "Superserver"
        try
            let! req = ctx.BindJsonAsync<SessionRequest>()
            let root = snd loaded.Value
            match root with
            | None -> return! RequestErrors.notFound (text "no studies configured") next ctx
            | Some rootDir ->
                let respond (study : LoadedStudy) (start : StudyStore.SessionStart) = task {
                    match start with
                    | StudyStore.Refused (status, message) ->
                        ctx.SetStatusCode status
                        return! jsonText (sprintf "{\"error\":\"%s\"}" message) next ctx
                    | StudyStore.Fresh s | StudyStore.Resumed s ->
                        let resumed = match start with StudyStore.Resumed _ -> true | _ -> false
                        let dataDir = StudyStore.dataDirOf rootDir study.Id
                        let pos =
                            if resumed then
                                match StudyStore.lastAdvance dataDir s.Sid with
                                | Some (ph, st) -> sprintf ",\"lastPhaseId\":\"%s\",\"lastStepId\":\"%s\"" ph st
                                | None -> ""
                            else ""
                        log.LogInformation("study session {Sid} ({Study}, {Cond}, demo={Demo}, resumed={Resumed})",
                            s.Sid, study.Id, StudyCondition.tag s.Condition, s.Demo, resumed)
                        return! jsonText
                                    (sprintf "{\"sessionId\":\"%s\",\"condition\":\"%s\",\"demo\":%b,\"resumed\":%b%s,\"configPublic\":%s}"
                                        s.Sid (StudyCondition.tag s.Condition) s.Demo resumed pos study.PublicJson)
                                    next ctx
                }
                if req.Demo then
                    match studyById req.StudyId with
                    | None -> return! RequestErrors.notFound (text "unknown study") next ctx
                    | Some study ->
                        let dataDir = StudyStore.dataDirOf rootDir study.Id
                        let cond = if isNull req.Condition then None else Some (StudyCondition.ofTag req.Condition)
                        let start =
                            StudyStore.createSession dataDir (StudyStore.tokensPath rootDir study.Id)
                                DateTime.UtcNow Random.Shared None true cond
                        return! respond study start
                elif String.IsNullOrWhiteSpace req.Token then
                    return! RequestErrors.badRequest (text "token required") next ctx
                else
                    // The link only carries the token — find the study owning it.
                    let owner =
                        fst (fst loaded.Value)
                        |> Map.toSeq
                        |> Seq.tryFind (fun (id, _) ->
                            StudyStore.readTokens (StudyStore.tokensPath rootDir id)
                            |> Array.contains req.Token)
                    match owner with
                    | None ->
                        ctx.SetStatusCode 403
                        return! jsonText "{\"error\":\"invalid token\"}" next ctx
                    | Some (id, study) ->
                        let dataDir = StudyStore.dataDirOf rootDir id
                        let start =
                            StudyStore.createSession dataDir (StudyStore.tokensPath rootDir id)
                                DateTime.UtcNow Random.Shared (Some req.Token) false None
                        return! respond study start
        with ex ->
            log.LogError(ex, "study session failed")
            return! RequestErrors.badRequest (text ex.Message) next ctx
    }

let listHandler : HttpHandler =
    fun next ctx ->
        let ids = fst (fst loaded.Value) |> Map.toList |> List.map fst
        json ids next ctx

// ─────────────────────────── per-session posts ──────────────────────────

let private withSession (sid : string) (f : LoadedStudy -> string -> HttpHandler) : HttpHandler =
    fun next ctx ->
        match studyOfSid sid with
        | Some (study, dataDir) -> f study dataDir next ctx
        | None -> RequestErrors.notFound (text "unknown session") next ctx

let eventsHandler (sid : string) : HttpHandler =
    withSession sid (fun _study dataDir ->
        fun next ctx -> task {
            let! body = ctx.ReadBodyFromRequestAsync()
            let root = JsonDocument.Parse(body).RootElement
            let events =
                match root.TryGetProperty "events" with
                | true, e when e.ValueKind = JsonValueKind.Array -> e.EnumerateArray() |> Seq.toArray
                | _ -> [||]
            StudyStore.appendEvents dataDir sid DateTime.UtcNow events
            ctx.SetStatusCode 204
            return! next ctx
        })

let answersHandler (sid : string) : HttpHandler =
    withSession sid (fun study dataDir ->
        fun next ctx -> task {
            let! body = ctx.ReadBodyFromRequestAsync()
            let root = JsonDocument.Parse(body).RootElement
            let qid = root.GetProperty("questionId").GetString()
            let value = root.GetProperty "value"
            let confidence =
                match root.TryGetProperty "confidence" with
                | true, c when c.ValueKind = JsonValueKind.Number -> Some (c.GetInt32())
                | _ -> None
            let outcome = StudyStore.appendAnswer study dataDir sid DateTime.UtcNow qid value confidence
            let correctPart =
                match outcome.Correct with
                | Some c -> sprintf "\"correct\":%b," c
                | None -> ""
            return! jsonText (sprintf "{%s\"screened\":%b}" correctPart outcome.Screened) next ctx
        })

let transformsHandler (sid : string) : HttpHandler =
    withSession sid (fun study dataDir ->
        fun next ctx -> task {
            let! body = ctx.ReadBodyFromRequestAsync()
            let root = JsonDocument.Parse(body).RootElement
            let label = root.GetProperty("label").GetString()
            let perMesh =
                match root.TryGetProperty "perMesh" with
                | true, pm when pm.ValueKind = JsonValueKind.Object ->
                    pm.EnumerateObject()
                    |> Seq.map (fun p ->
                        let a = p.Value.EnumerateArray() |> Seq.map (fun x -> x.GetDouble()) |> Array.ofSeq
                        p.Name,
                        M44d(a.[0],  a.[1],  a.[2],  a.[3],
                             a.[4],  a.[5],  a.[6],  a.[7],
                             a.[8],  a.[9],  a.[10], a.[11],
                             a.[12], a.[13], a.[14], a.[15]))
                    |> Map.ofSeq
                | _ -> Map.empty
            StudyStore.postTransforms study dataDir sid DateTime.UtcNow label perMesh
            ctx.SetStatusCode 204
            return! next ctx
        })

let workspaceHandler (sid : string) : HttpHandler =
    withSession sid (fun _study dataDir ->
        fun next ctx -> task {
            let! body = ctx.ReadBodyFromRequestAsync()
            let root = JsonDocument.Parse(body).RootElement
            let ws =
                match root.TryGetProperty "workspaceJson" with
                | true, w when w.ValueKind = JsonValueKind.String -> w.GetString()
                | _ -> body
            StudyStore.saveWorkspace dataDir sid ws
            ctx.SetStatusCode 204
            return! next ctx
        })

let advanceHandler (sid : string) : HttpHandler =
    withSession sid (fun study dataDir ->
        fun next ctx -> task {
            let! body = ctx.ReadBodyFromRequestAsync()
            let root = JsonDocument.Parse(body).RootElement
            let phaseId = root.GetProperty("phaseId").GetString()
            let stepId = root.GetProperty("stepId").GetString()
            match StudyStore.recordAdvance study dataDir sid DateTime.UtcNow phaseId stepId with
            | Result.Ok () ->
                ctx.SetStatusCode 204
                return! next ctx
            | Result.Error reason ->
                ctx.SetStatusCode 409
                return! jsonText (sprintf "{\"error\":\"%s\"}" reason) next ctx
        })

let completeHandler (sid : string) : HttpHandler =
    withSession sid (fun study dataDir ->
        fun next ctx -> task {
            match snd loaded.Value with
            | None -> return! RequestErrors.notFound (text "no studies configured") next ctx
            | Some rootDir ->
                let secret = StudyStore.serverSecret rootDir
                match StudyStore.complete study dataDir secret sid with
                | Result.Ok code -> return! jsonText (sprintf "{\"code\":\"%s\"}" code) next ctx
                | Result.Error reason ->
                    ctx.SetStatusCode 409
                    return! jsonText (sprintf "{\"error\":\"%s\"}" reason) next ctx
        })

// ───────────────────── token generation (localhost) ─────────────────────

[<CLIMutable>]
type TokensRequest = { N : int }

let tokensHandler (studyId : string) : HttpHandler =
    fun next ctx -> task {
        let remote = ctx.Connection.RemoteIpAddress
        if isNull remote || not (IPAddress.IsLoopback remote) then
            return! RequestErrors.forbidden (text "localhost only") next ctx
        else
            match studyById studyId, snd loaded.Value with
            | Some _, Some rootDir ->
                let! req = ctx.BindJsonAsync<TokensRequest>()
                let n = max 1 (min 500 req.N)
                let fresh = StudyStore.generateTokens (StudyStore.tokensPath rootDir studyId) DateTime.UtcNow n
                return! json fresh next ctx
            | _ -> return! RequestErrors.notFound (text "unknown study") next ctx
    }
