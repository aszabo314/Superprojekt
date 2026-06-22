namespace Superprojekt

open Aardvark.Base
open System.Text
open System.Text.Json

// Set once in Program.fs before Boot.run from the /s/{token} entry route.
module StudyBoot =
    let mutable entryToken : string option = None

module StudyApi =

    let private post (serverUrl : string) (path : string) (json : string) : Async<int * string> =
        async {
            use content = new System.Net.Http.StringContent(json, Encoding.UTF8, "application/json")
            let! resp = Http.client.PostAsync(serverUrl.TrimEnd('/') + path, content) |> Async.AwaitTask
            let! body = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
            return int resp.StatusCode, body
        }

    let private errorOf (status : int) (body : string) =
        try
            let e = JsonDocument.Parse(body).RootElement
            match e.TryGetProperty "error" with
            | true, v -> v.GetString()
            | _ -> sprintf "HTTP %d" status
        with _ -> if body.Length > 0 && body.Length < 200 then body else sprintf "HTTP %d" status

    let private q (s : string) =
        "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""

    let createSession
            (serverUrl : string)
            (token : string option)
            (demo : (string * StudyCondition) option) : Async<Result<StudySessionInit, string>> =
        async {
            let req =
                match demo with
                | Some (studyId, cond) ->
                    sprintf "{\"demo\":true,\"studyId\":%s,\"condition\":%s}" (q studyId) (q (StudyCondition.tag cond))
                | None ->
                    sprintf "{\"token\":%s}" (q (token |> Option.defaultValue ""))
            try
                let! status, body = post serverUrl "/study/session" req
                if status >= 200 && status < 300 then
                    let e = JsonDocument.Parse(body).RootElement
                    let lastStep =
                        match e.TryGetProperty "lastPhaseId", e.TryGetProperty "lastStepId" with
                        | (true, p), (true, s) -> Some (p.GetString(), s.GetString())
                        | _ -> None
                    return Result.Ok {
                        SessionId = e.GetProperty("sessionId").GetString()
                        Condition = StudyCondition.ofTag (e.GetProperty("condition").GetString())
                        Demo      = e.GetProperty("demo").GetBoolean()
                        Resumed   = e.GetProperty("resumed").GetBoolean()
                        LastStep  = lastStep
                        Config    = StudyConfig.parsePublic (e.GetProperty "configPublic")
                    }
                else
                    return Result.Error (errorOf status body)
            with ex ->
                return Result.Error ex.Message
        }

    let listStudies (serverUrl : string) : Async<string[]> =
        async {
            let! json = Http.client.GetStringAsync(serverUrl.TrimEnd('/') + "/study/list") |> Async.AwaitTask
            return
                JsonDocument.Parse(json).RootElement.EnumerateArray()
                |> Seq.map (fun e -> e.GetString())
                |> Seq.toArray
        }

    // Batched telemetry; the payload is pre-rendered JSON event objects.
    let postEvents (serverUrl : string) (sid : string) (eventsJson : string list) : Async<bool> =
        async {
            try
                let body = sprintf "{\"events\":[%s]}" (String.concat "," eventsJson)
                let! status, _ = post serverUrl (sprintf "/study/%s/events" sid) body
                return status >= 200 && status < 300
            with _ -> return false
        }

    // Returns (tutorial-gold correctness, screened).
    let postAnswer
            (serverUrl : string)
            (sid : string)
            (questionId : string)
            (valueJson : string)
            (confidence : int option) : Async<Result<bool option * bool, string>> =
        async {
            try
                let body =
                    sprintf "{\"questionId\":%s,\"value\":%s%s}" (q questionId) valueJson
                        (match confidence with Some c -> sprintf ",\"confidence\":%d" c | None -> "")
                let! status, respBody = post serverUrl (sprintf "/study/%s/answers" sid) body
                if status >= 200 && status < 300 then
                    let e = JsonDocument.Parse(respBody).RootElement
                    let correct =
                        match e.TryGetProperty "correct" with
                        | true, c when c.ValueKind = JsonValueKind.True -> Some true
                        | true, c when c.ValueKind = JsonValueKind.False -> Some false
                        | _ -> None
                    let screened =
                        match e.TryGetProperty "screened" with
                        | true, s -> s.ValueKind = JsonValueKind.True
                        | _ -> false
                    return Result.Ok (correct, screened)
                else
                    return Result.Error (errorOf status respBody)
            with ex ->
                return Result.Error ex.Message
        }

    let postTransforms
            (serverUrl : string)
            (sid : string)
            (label : string)
            (perMesh : (string * M44d) list) : Async<bool> =
        async {
            try
                let inv = System.Globalization.CultureInfo.InvariantCulture
                let m44 (m : M44d) =
                    [| m.M00; m.M01; m.M02; m.M03; m.M10; m.M11; m.M12; m.M13
                       m.M20; m.M21; m.M22; m.M23; m.M30; m.M31; m.M32; m.M33 |]
                    |> Array.map (fun v -> v.ToString("G17", inv))
                    |> String.concat ","
                let pm =
                    perMesh
                    |> List.map (fun (mesh, t) -> sprintf "%s:[%s]" (q mesh) (m44 t))
                    |> String.concat ","
                let body = sprintf "{\"label\":%s,\"perMesh\":{%s}}" (q label) pm
                let! status, _ = post serverUrl (sprintf "/study/%s/transforms" sid) body
                return status >= 200 && status < 300
            with _ -> return false
        }

    let postWorkspace (serverUrl : string) (sid : string) (workspaceJson : string) : Async<bool> =
        async {
            try
                let body =
                    sprintf "{\"workspaceJson\":%s}"
                        (JsonSerializer.Serialize(workspaceJson : string))
                let! status, _ = post serverUrl (sprintf "/study/%s/workspace" sid) body
                return status >= 200 && status < 300
            with _ -> return false
        }

    let postAdvance (serverUrl : string) (sid : string) (phaseId : string) (stepId : string) : Async<bool> =
        async {
            try
                let body = sprintf "{\"phaseId\":%s,\"stepId\":%s}" (q phaseId) (q stepId)
                let! status, _ = post serverUrl (sprintf "/study/%s/advance" sid) body
                return status >= 200 && status < 300
            with _ -> return false
        }

    let getComplete (serverUrl : string) (sid : string) : Async<Result<string, string>> =
        async {
            try
                let url = sprintf "%s/study/%s/complete" (serverUrl.TrimEnd('/')) sid
                let! resp = Http.client.GetAsync(url) |> Async.AwaitTask
                let! body = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
                if resp.IsSuccessStatusCode then
                    return Result.Ok (JsonDocument.Parse(body).RootElement.GetProperty("code").GetString())
                else
                    return Result.Error (errorOf (int resp.StatusCode) body)
            with ex ->
                return Result.Error ex.Message
        }
