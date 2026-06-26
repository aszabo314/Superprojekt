namespace Superprojekt

open Aardvark.Base
open Aardworx.WebAssembly
open FSharp.Data.Adaptive
open Aardvark.Dom
open Superprojekt

module ScanPinUpdate =

    let mutable probeCts : System.Threading.CancellationTokenSource =
        new System.Threading.CancellationTokenSource()
    let mutable private probeOwner : ScanPinId option = None

    // Contact-ring queries run per pin (several can recompute at once after a
    // registration), so each pin gets its own debounce token.
    let private ringsCts =
        System.Collections.Generic.Dictionary<ScanPinId, System.Threading.CancellationTokenSource>()

    let private assignColors (meshNames : IndexList<string>) =
        meshNames |> IndexList.toArray |> Array.mapi (fun i n -> n, Primitives.meshColor i) |> Map.ofArray

    let activeScale (model : Model) =
        DatasetScale.active model.ActiveDataset model.DatasetScales

    let private makeAnchor (model : Model) (id : ScanPinId) (worldCentre : V3d) =
        {
            Id                   = id
            Name                 = PinNames.generate id
            Centre               = worldCentre
            InnerRadius          = max 0.01 model.QuickPinRadius
            Correspondence       = None
            HostMeshName         = model.ActivePickingLayer
            CreatedAt            = System.DateTime.UtcNow
            DatasetColors        = assignColors model.MeshNames
            Probe                = ProbeNone
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

        // Applies to the selected pin (its detail panel).
        | SetInnerRadius r ->
            match model.Selection.SelectedPin with
            | Some id -> sp |> updatePin id (fun pin ->
                let r' = max 0.01 r
                { pin with InnerRadius = r'; Probe = ProbeNone; ContactRings = RingsNone })
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

        | ContactRingsComputed(id, rings) ->
            sp |> updatePin id (fun pin ->
                if pin.ContactRings = RingsRunning then { pin with ContactRings = RingsReady rings } else pin)

    let handleMsg (env : Env<Message>) (model : Model) (msg : ScanPinMessage) =
        let sp = model.ScanPins
        let sp' = update model msg sp
        let wasPlacing = ScanPinModel.isPlacing sp
        let isPlacing = ScanPinModel.isPlacing sp'
        let model =
            if not wasPlacing && isPlacing then
                { model with SavedMenuOpen = Some model.MenuOpen; MenuOpen = true }
            elif wasPlacing && not isPlacing then
                let restored = model.SavedMenuOpen |> Option.defaultValue model.MenuOpen
                { model with MenuOpen = restored; SavedMenuOpen = None }
            else model
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

    // Lazy probe trigger, postlude after every reducer step: the effective pin
    // (card open) with an invalidated probe gets one debounced server query.
    // Drags coalesce; stale responses dropped by the ProbeRunning guard above.
    let ensureProbe (env : Env<Message>) (model : Model) : Model =
        let sp = model.ScanPins
        let effective =
            model.Selection.SelectedPin
            |> Option.bind (fun id -> HashMap.tryFind id sp.Pins)
        match effective with
        | Some pin when (match pin.Probe with ProbeNone -> true | _ -> false) ->
            let visible =
                model.MeshNames |> IndexList.toList
                |> List.filter (fun n -> Map.tryFind n model.MeshVisible |> Option.defaultValue true)
            let setProbe state =
                { model with ScanPins = { sp with Pins = HashMap.add pin.Id { pin with Probe = state } sp.Pins } }
            match visible with
            | [] -> setProbe (ProbeError "no visible meshes")
            | _ ->
                let refMesh =
                    model.Registration.ReferenceMesh |> Option.filter (fun r -> List.contains r visible)
                    |> Option.orElse (pin.HostMeshName |> Option.filter (fun h -> List.contains h visible))
                    |> Option.defaultValue (List.head visible)
                let meshes =
                    visible |> List.map (fun n -> n, (ModelTransforms.displayedWorld model n).Forward)
                let id = pin.Id
                let centre = pin.Centre
                let radius = pin.InnerRadius
                let length = ScanPin.fixedProbeLength
                probeCts.Cancel()
                probeCts <- new System.Threading.CancellationTokenSource()
                // The cancelled task never emits, so a different pin's in-flight probe would stay
                // ProbeRunning forever — reset it to ProbeNone so it lazily recomputes when reselected.
                let sp =
                    match probeOwner with
                    | Some prev when prev <> id ->
                        match HashMap.tryFind prev sp.Pins with
                        | Some p when p.Probe = ProbeRunning ->
                            { sp with Pins = HashMap.add prev { p with Probe = ProbeNone } sp.Pins }
                        | _ -> sp
                    | _ -> sp
                probeOwner <- Some id
                let token = probeCts.Token
                task {
                    try
                        do! System.Threading.Tasks.Task.Delay(250, token)
                        let! res =
                            Query.probe ApiConfig.apiBase.Value meshes refMesh centre radius length 8192
                            |> Async.StartAsTask
                        if not token.IsCancellationRequested then
                            match res with
                            | Result.Ok r -> env.Emit [ScanPinMsg (ProbeComputed(id, r))]
                            | Result.Error e -> env.Emit [ScanPinMsg (ProbeFailed(id, e))]
                    with
                    | :? System.OperationCanceledException -> ()
                    | ex ->
                        if not token.IsCancellationRequested then
                            env.Emit [ScanPinMsg (ProbeFailed(id, ex.Message))]
                } |> ignore
                { model with ScanPins = { sp with Pins = HashMap.add id { pin with Probe = ProbeRunning } sp.Pins } }
        | _ -> model

    // Lazy contact-ring trigger, postlude after every reducer step: every RingsNone pin gets
    // one debounced fan-out over ALL meshes (visibility only gates rendering, so toggling never
    // recomputes). Transforms are rigid: sphere intersected in each mesh's own frame
    // (inverse-transformed centre), rings mapped back. Displayed transforms → rings follow the before/after toggle.
    let ensureRings (env : Env<Message>) (model : Model) : Model =
        let sp = model.ScanPins
        let pending =
            sp.Pins |> HashMap.toList
            |> List.filter (fun (_, p) -> p.ContactRings = RingsNone)
        if List.isEmpty pending || model.MeshNames.Count = 0 then model
        else
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
