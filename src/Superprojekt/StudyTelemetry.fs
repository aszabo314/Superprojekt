namespace Superprojekt

// Telemetry batcher. Module-level mutable state like the reducer's
// CancellationTokenSource refs — the queue never enters the Elm model. Flush
// every 5 s / 50 events, immediately on phaseEnter / stepComplete and on page
// hide; retry with exponential backoff; bounded queue drops oldest
// throttled-type events first.
module StudyTelemetry =

    let private queue = System.Collections.Generic.List<string * string>()   // (type, rendered json)
    let private gate = obj ()
    let private sw = System.Diagnostics.Stopwatch()
    let mutable private sid : string option = None
    let mutable private backoffMs = 0
    let mutable private nextTryAt = 0L
    let mutable private frames = 0
    let private maxQueue = 600
    let private throttleMs = 5000L
    let private throttleLast = System.Collections.Generic.Dictionary<string, int64>()

    let private renderEvent (t : int64) (etype : string) (payload : string) =
        sprintf "{\"t\":%d,\"type\":\"%s\",\"payload\":%s}" t etype payload

    let private takeBatch () =
        lock gate (fun () ->
            let batch = queue |> Seq.map snd |> List.ofSeq
            queue.Clear()
            batch)

    let private requeue (batch : string list) =
        lock gate (fun () ->
            queue.InsertRange(0, batch |> List.map (fun j -> "", j)))

    let private flushAsync () =
        match sid with
        | Some sessionId when queue.Count > 0 && sw.ElapsedMilliseconds >= nextTryAt ->
            let batch = takeBatch ()
            if not (List.isEmpty batch) then
                async {
                    let! ok = StudyApi.postEvents ApiConfig.apiBase.Value sessionId batch
                    if ok then
                        backoffMs <- 0
                    else
                        requeue batch
                        backoffMs <- min 60000 (max 5000 (backoffMs * 2))
                        nextTryAt <- sw.ElapsedMilliseconds + int64 backoffMs
                } |> Async.Start
        | _ -> ()

    let flushNow () = flushAsync ()

    // Both loops end with the session (sid = None stops them).
    let private startLoops (mySession : string) =
        task {
            while sid = Some mySession do
                do! System.Threading.Tasks.Task.Delay 5000
                if sid = Some mySession then flushAsync ()
        } |> ignore
        task {
            while sid = Some mySession do
                frames <- 0
                do! System.Threading.Tasks.Task.Delay 30000
                if sid = Some mySession && frames > 0 then
                    let fps = float frames / 30.0
                    let t = sw.ElapsedMilliseconds
                    lock gate (fun () ->
                        queue.Add("fpsSample", renderEvent t "fpsSample" (sprintf "{\"fps\":%.1f}" fps)))
        } |> ignore

    let start (sessionId : string) =
        sid <- Some sessionId
        sw.Restart()
        backoffMs <- 0
        nextTryAt <- 0L
        lock gate (fun () -> queue.Clear())
        throttleLast.Clear()
        startLoops sessionId

    let stop () =
        flushAsync ()
        sid <- None

    let frameTick () =
        if sid.IsSome then frames <- frames + 1

    let record (etype : string) (payload : string) =
        match sid with
        | None -> ()
        | Some _ ->
            let now = sw.ElapsedMilliseconds
            let throttled = List.contains etype StudyEvent.throttled
            let drop =
                throttled
                && (match throttleLast.TryGetValue etype with
                    | true, last -> now - last < throttleMs
                    | _ -> false)
            if not drop then
                if throttled then throttleLast.[etype] <- now
                let line = renderEvent now etype payload
                let count =
                    lock gate (fun () ->
                        if queue.Count >= maxQueue then
                            let ix = queue.FindIndex(fun (t, _) -> List.contains t StudyEvent.throttled)
                            queue.RemoveAt(if ix >= 0 then ix else 0)
                        queue.Add(etype, line)
                        queue.Count)
                if count >= 50 then flushAsync ()
