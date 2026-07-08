namespace Superprojekt

open Aardvark.Base
open Aardworx.WebAssembly
open FSharp.Data.Adaptive
open Aardvark.Dom
open Superprojekt

module ScanPinUpdate =

    // Probe queries run per pin — every pin is probed so the left-rail matrix has a
    // cell for each (pin, mesh). One debounce token per pin (the next invalidation
    // cancels the previous; stale responses are dropped by the ProbeRunning guard).
    let private probeCtsMap =
        System.Collections.Generic.Dictionary<ScanPinId, System.Threading.CancellationTokenSource>()

    // Contact-ring queries run per pin (several can recompute at once after a
    // registration), so each pin gets its own debounce token.
    let private ringsCts =
        System.Collections.Generic.Dictionary<ScanPinId, System.Threading.CancellationTokenSource>()

    // Cross-section slice queries: same per-pin debounce discipline as the probes.
    let private sliceCtsMap =
        System.Collections.Generic.Dictionary<ScanPinId, System.Threading.CancellationTokenSource>()

    let private assignColors (meshNames : IndexList<string>) =
        meshNames |> IndexList.toArray |> Array.mapi (fun i n -> n, Primitives.meshColor i) |> Map.ofArray

    let activeScale (model : Model) =
        DatasetScale.active model.ActiveDataset model.DatasetScales

    let private makeAnchor (model : Model) (id : ScanPinId) (worldCentre : V3d) =
        let existing = model.ScanPins.Pins |> HashMap.toList |> List.map snd
        // Least-used palette slot (round-robin), ties → lowest index. Glyph + colour
        // share the slot (paired redundant coding).
        let slot =
            [ 0 .. Primitives.PinPalette.count - 1 ]
            |> List.map (fun i -> i, existing |> List.filter (fun p -> p.PinColor = Primitives.PinPalette.color i) |> List.length)
            |> List.minBy (fun (i, c) -> (c, i))
            |> fst
        // Collision-check the short name against existing pin names + mesh numbers.
        let taken =
            let pinNames = existing |> List.map (fun p -> p.ShortName) |> Set.ofList
            let meshNums = model.MeshOrder |> HashMap.toList |> List.map (fun (_, i) -> string (i + 1)) |> Set.ofList
            Set.union pinNames meshNums
        let (ScanPinId.ScanPinId g) = id
        let shortName = Primitives.PinIdentity.shortName taken (g.GetHashCode())
        {
            Id                   = id
            Glyph                = Primitives.PinPalette.glyph slot
            ShortName            = shortName
            PinColor             = Primitives.PinPalette.color slot
            Centre               = worldCentre
            InnerRadius          = max 0.01 model.QuickPinRadius
            Correspondence       = Some Correspondence.empty
            HostMeshName         = model.ActivePickingLayer
            CreatedAt            = System.DateTime.UtcNow
            DatasetColors        = assignColors model.MeshNames
            Probe                = ProbeNone
            ProbeOther           = ProbeNone
            Slice                = SliceNone
            SliceOther           = SliceNone
            ContactRings         = RingsNone
        }

    let private updatePin (id : ScanPinId) (f : ScanPin -> ScanPin) (sp : ScanPinModel) =
        match HashMap.tryFind id sp.Pins with
        | Some pin -> { sp with Pins = HashMap.add id (f pin) sp.Pins }
        | None -> sp

    let update (model : Model) (msg : ScanPinMessage) (sp : ScanPinModel) =
        match msg with
        | EnterAnchorPlacement ->
            { sp with Placement = AnchorPlacement }

        | CancelPlacement ->
            { sp with Placement = PlacementIdle }

        // Click-and-drop: the pin is created committed and placement ends. Radius
        // is edited afterwards from the pin's detail panel (dock).
        | PlaceAnchor worldCentre ->
            match sp.Placement with
            | AnchorPlacement ->
                let id = ScanPinId.create()
                let pin = makeAnchor model id worldCentre
                { sp with
                    Pins = HashMap.add id pin sp.Pins
                    Placement = PlacementIdle }
            | _ -> sp

        // Applies to the selected pin (its detail panel). Shrinking the sphere
        // kills every correspondence point that falls outside it — evaluated at
        // the BEFORE pose (the source-of-truth state for correspondences); the
        // solve-validity postlude then clears a registration that consumed a
        // killed point.
        | SetInnerRadius r ->
            match model.Selection.SelectedPin with
            | Some id -> sp |> updatePin id (fun pin ->
                let r' = max 0.01 r
                let corr =
                    ScanPin.correspondence pin |> Option.map (fun c ->
                        let refAnchor =
                            c.RefAnchor |> Option.filter (fun ra -> (ra - pin.Centre).Length <= r')
                        let anchors =
                            c.Anchors |> Map.filter (fun mesh a ->
                                let w = (ModelTransforms.displayedWorldAt RegBefore model mesh).Forward.TransformPos a.Point
                                (w - pin.Centre).Length <= r')
                        { c with RefAnchor = refAnchor; Anchors = anchors })
                ScanPin.withCorrespondence corr
                    { pin with InnerRadius = r'; Probe = ProbeNone; ProbeOther = ProbeNone
                               Slice = SliceNone; SliceOther = SliceNone; ContactRings = RingsNone })
            | None -> sp

        | DeletePin id ->
            { sp with Pins = HashMap.remove id sp.Pins }

        // Pin selection lives in Model.Selection (handled in handleMsg).
        | SelectPin _ -> sp

        // Stale guard: results only land while still ProbeRunning; any intervening invalidation wins.
        | ProbeComputed(id, result) ->
            sp |> updatePin id (fun pin ->
                if pin.Probe = ProbeRunning then { pin with Probe = ProbeReady result } else pin)

        | ProbeFailed(id, reason) ->
            sp |> updatePin id (fun pin ->
                if pin.Probe = ProbeRunning then { pin with Probe = ProbeError reason } else pin)

        | ProbeOtherComputed(id, result) ->
            sp |> updatePin id (fun pin ->
                if pin.ProbeOther = ProbeRunning then { pin with ProbeOther = ProbeReady result } else pin)

        | ProbeOtherFailed(id, reason) ->
            sp |> updatePin id (fun pin ->
                if pin.ProbeOther = ProbeRunning then { pin with ProbeOther = ProbeError reason } else pin)

        | SliceComputed(id, result) ->
            sp |> updatePin id (fun pin ->
                if pin.Slice = SliceRunning then { pin with Slice = SliceReady result } else pin)

        | SliceFailed(id, reason) ->
            sp |> updatePin id (fun pin ->
                if pin.Slice = SliceRunning then { pin with Slice = SliceError reason } else pin)

        | SliceOtherComputed(id, result) ->
            sp |> updatePin id (fun pin ->
                if pin.SliceOther = SliceRunning then { pin with SliceOther = SliceReady result } else pin)

        | SliceOtherFailed(id, reason) ->
            sp |> updatePin id (fun pin ->
                if pin.SliceOther = SliceRunning then { pin with SliceOther = SliceError reason } else pin)

        | ContactRingsComputed(id, rings) ->
            sp |> updatePin id (fun pin ->
                if pin.ContactRings = RingsRunning then { pin with ContactRings = RingsReady rings } else pin)

    let handleMsg (env : Env<Message>) (model : Model) (msg : ScanPinMessage) =
        let sp = model.ScanPins
        let sp' = update model msg sp
        // The pin just added by PlaceAnchor (exactly one new key), if any.
        let placedId =
            match msg with
            | PlaceAnchor _ ->
                sp'.Pins |> HashMap.toSeq |> Seq.map fst
                |> Seq.tryFind (fun id -> not (HashMap.containsKey id sp.Pins))
            | _ -> None
        // SelectPin sets the shared selection, a freshly placed pin becomes
        // selected, and a dangling selection (deleted pin) is dropped.
        let selection =
            let sel0 =
                match msg with
                | SelectPin id -> { model.Selection with SelectedPin = id }
                | PlaceAnchor _ ->
                    match placedId with
                    | Some id -> { model.Selection with SelectedPin = Some id }
                    | None -> model.Selection
                | _ -> model.Selection
            match sel0.SelectedPin with
            | Some id when not (HashMap.containsKey id sp'.Pins) -> { sel0 with SelectedPin = None }
            | _ -> sel0
        match placedId |> Option.bind (fun id -> HashMap.tryFind id sp'.Pins) with
        | Some pin ->
            let scale = activeScale model
            let renderCentre = ScanPin.renderCentre model.CommonCentroid scale pin.Centre
            env.Emit [CameraMessage (OrbitMessage.SetTargetCenter(true, AnimationKind.Tanh, renderCentre))]
        | None -> ()
        { model with ScanPins = sp'; Selection = selection }

    // Lazy probe trigger, postlude after every reducer step: EVERY pin with an
    // invalidated probe gets one debounced server query (so the rail matrix has a
    // before/after-aware cell per (pin, mesh)). Per-pin debounce; drags coalesce;
    // stale responses dropped by the ProbeRunning guard above.
    let ensureProbe (env : Env<Message>) (model : Model) : Model =
        let sp = model.ScanPins
        // Once a solve exists, the OTHER pose is probed too (ProbeOther) — it feeds
        // the violin chart's inactive Before/After half.
        let solved = not (Map.isEmpty model.SolvedTransforms)
        let pendingPin (p : ScanPin) =
            (match p.Probe with ProbeNone -> true | _ -> false)
            || (solved && (match p.ProbeOther with ProbeNone -> true | _ -> false))
        // Cheap exists-check first: this postlude runs on every message (incl. Rendered).
        if not (sp.Pins |> HashMap.exists (fun _ p -> pendingPin p)) then model
        else
            // Probe every mesh regardless of visibility (like ensureRings) so the rail
            // matrix has a stable cell per (pin, mesh) — visibility only gates rendering
            // + the distribution/3D consumers, never the probe itself.
            let allMeshes = model.MeshNames |> IndexList.toList
            match allMeshes with
            | [] -> model
            | _ ->
                let refMesh0 = model.Registration.ReferenceMesh |> Option.filter (fun r -> List.contains r allMeshes)
                let meshes = allMeshes |> List.map (fun n -> n, (ModelTransforms.displayedWorld model n).Forward)
                let otherView = match model.RegView with RegBefore -> RegAfter | RegAfter -> RegBefore
                let meshesOther = allMeshes |> List.map (fun n -> n, (ModelTransforms.displayedWorldAt otherView model n).Forward)
                let pending =
                    sp.Pins |> HashMap.toList
                    |> List.filter (fun (_, p) -> pendingPin p)
                let mutable pins = sp.Pins
                for (id, pin) in pending do
                    let refMesh =
                        refMesh0
                        |> Option.orElse (pin.HostMeshName |> Option.filter (fun h -> List.contains h allMeshes))
                        |> Option.defaultValue (List.head allMeshes)
                    match probeCtsMap.TryGetValue id with
                    | true, cts -> cts.Cancel()
                    | _ -> ()
                    let cts = new System.Threading.CancellationTokenSource()
                    probeCtsMap.[id] <- cts
                    let token = cts.Token
                    let centre = pin.Centre
                    let radius = pin.InnerRadius
                    let length = ScanPin.fixedProbeLength
                    let needMain  = match pin.Probe with ProbeNone -> true | _ -> false
                    let needOther = solved && (match pin.ProbeOther with ProbeNone -> true | _ -> false)
                    let fire ms ok fail =
                        task {
                            try
                                do! System.Threading.Tasks.Task.Delay(250, token)
                                let! res =
                                    Query.probe ApiConfig.apiBase.Value ms refMesh centre radius length 8192
                                    |> Async.StartAsTask
                                if not token.IsCancellationRequested then
                                    match res with
                                    | Result.Ok r -> env.Emit [ScanPinMsg (ok r)]
                                    | Result.Error e -> env.Emit [ScanPinMsg (fail e)]
                            with
                            | :? System.OperationCanceledException -> ()
                            | ex ->
                                if not token.IsCancellationRequested then
                                    env.Emit [ScanPinMsg (fail ex.Message)]
                        } |> ignore
                    if needMain then fire meshes (fun r -> ProbeComputed(id, r)) (fun e -> ProbeFailed(id, e))
                    if needOther then fire meshesOther (fun r -> ProbeOtherComputed(id, r)) (fun e -> ProbeOtherFailed(id, e))
                    let pin = if needMain then { pin with Probe = ProbeRunning } else pin
                    let pin = if needOther then { pin with ProbeOther = ProbeRunning } else pin
                    pins <- HashMap.add id pin pins
                { model with ScanPins = { sp with Pins = pins } }

    // Lazy cross-section trigger, postlude after every reducer step, mirroring
    // ensureProbe: every pin with an invalidated Slice (and SliceOther once a
    // solve exists) gets one debounced server query per pose. The slices feed
    // only the show-overlays hold, but are precomputed here so the hold itself
    // never fetches. All meshes regardless of visibility — visibility gates
    // rendering only.
    let ensureSlices (env : Env<Message>) (model : Model) : Model =
        let sp = model.ScanPins
        let solved = not (Map.isEmpty model.SolvedTransforms)
        let pendingPin (p : ScanPin) =
            (match p.Slice with SliceNone -> true | _ -> false)
            || (solved && (match p.SliceOther with SliceNone -> true | _ -> false))
        // Cheap exists-check first: this postlude runs on every message (incl. Rendered).
        if not (sp.Pins |> HashMap.exists (fun _ p -> pendingPin p)) then model
        else
            let allMeshes = model.MeshNames |> IndexList.toList
            match allMeshes with
            | [] -> model
            | _ ->
                let meshes = allMeshes |> List.map (fun n -> n, (ModelTransforms.displayedWorld model n).Forward)
                let otherView = match model.RegView with RegBefore -> RegAfter | RegAfter -> RegBefore
                let meshesOther = allMeshes |> List.map (fun n -> n, (ModelTransforms.displayedWorldAt otherView model n).Forward)
                let pending =
                    sp.Pins |> HashMap.toList
                    |> List.filter (fun (_, p) -> pendingPin p)
                let mutable pins = sp.Pins
                for (id, pin) in pending do
                    match sliceCtsMap.TryGetValue id with
                    | true, cts -> cts.Cancel()
                    | _ -> ()
                    let cts = new System.Threading.CancellationTokenSource()
                    sliceCtsMap.[id] <- cts
                    let token = cts.Token
                    let centre = pin.Centre
                    let radius = pin.InnerRadius
                    let offsets = ScanPin.sliceOffsets radius
                    let needMain  = match pin.Slice with SliceNone -> true | _ -> false
                    let needOther = solved && (match pin.SliceOther with SliceNone -> true | _ -> false)
                    let fire ms ok fail =
                        task {
                            try
                                do! System.Threading.Tasks.Task.Delay(250, token)
                                let! res =
                                    Query.slice ApiConfig.apiBase.Value ms centre
                                        ScanPin.sliceUDir ScanPin.sliceNormal radius offsets 140
                                    |> Async.StartAsTask
                                if not token.IsCancellationRequested then
                                    let s = {
                                        Extent  = radius
                                        Offsets = offsets
                                        Meshes  = res |> Array.map (fun (n, planes) -> { MeshName = n; Planes = planes })
                                    }
                                    env.Emit [ScanPinMsg (ok s)]
                            with
                            | :? System.OperationCanceledException -> ()
                            | ex ->
                                if not token.IsCancellationRequested then
                                    env.Emit [ScanPinMsg (fail ex.Message)]
                        } |> ignore
                    if needMain then fire meshes (fun s -> SliceComputed(id, s)) (fun e -> SliceFailed(id, e))
                    if needOther then fire meshesOther (fun s -> SliceOtherComputed(id, s)) (fun e -> SliceOtherFailed(id, e))
                    let pin = if needMain then { pin with Slice = SliceRunning } else pin
                    let pin = if needOther then { pin with SliceOther = SliceRunning } else pin
                    pins <- HashMap.add id pin pins
                { model with ScanPins = { sp with Pins = pins } }

    // Lazy contact-ring trigger, postlude after every reducer step: every RingsNone pin gets
    // one debounced fan-out over ALL meshes (visibility only gates rendering, so toggling never
    // recomputes). Transforms are rigid: sphere intersected in each mesh's own frame
    // (inverse-transformed centre), rings mapped back. Displayed transforms → rings follow the before/after toggle.
    let ensureRings (env : Env<Message>) (model : Model) : Model =
        let sp = model.ScanPins
        // Cheap exists-check first: this postlude runs on every message (incl. per-frame
        // Rendered), so avoid allocating the filtered list when nothing is pending.
        if model.MeshNames.Count = 0 || not (sp.Pins |> HashMap.exists (fun _ p -> p.ContactRings = RingsNone)) then model
        else
            let pending =
                sp.Pins |> HashMap.toList
                |> List.filter (fun (_, p) -> p.ContactRings = RingsNone)
            let meshes =
                model.MeshNames |> IndexList.toList |> List.map (fun n ->
                    n, ModelTransforms.displayedWorld model n)
            let mutable pins = sp.Pins
            for (pinId, pin) in pending do
                match ringsCts.TryGetValue pinId with
                | true, cts -> cts.Cancel()
                | _ -> ()
                let cts = new System.Threading.CancellationTokenSource()
                ringsCts.[pinId] <- cts
                let token = cts.Token
                let centre = pin.Centre
                let radius = pin.InnerRadius
                task {
                    try
                        do! System.Threading.Tasks.Task.Delay(250, token)
                        let! results =
                            meshes
                            |> List.map (fun (n, tw) -> async {
                                try
                                    let cOwn = tw.Backward.TransformPos centre
                                    let! rings = Query.contactRings ApiConfig.apiBase.Value n cOwn radius 4096
                                    let ringsWorld = rings |> Array.map (Array.map tw.Forward.TransformPos)
                                    return if ringsWorld.Length = 0 then None else Some (n, ringsWorld)
                                with _ -> return None })
                            |> Async.Parallel
                            |> Async.StartAsTask
                        if not token.IsCancellationRequested then
                            let map = results |> Array.choose (fun r -> r) |> Map.ofArray
                            env.Emit [ScanPinMsg (ContactRingsComputed(pinId, map))]
                    with
                    | :? System.OperationCanceledException -> ()
                    | _ -> ()
                } |> ignore
                pins <- HashMap.add pinId { pin with ContactRings = RingsRunning } pins
            { model with ScanPins = { sp with Pins = pins } }
