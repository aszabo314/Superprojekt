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

    // Metric default hard-core radius.
    let defaultInnerRadius (_ : Model) = 5.0

    // worldCentre is world-space metres.
    let private makeAnchor (model : Model) (id : ScanPinId) (worldCentre : V3d) =
        let inner   = defaultInnerRadius model
        {
            Id                   = id
            Name                 = PinNames.generate id
            Phase                = PinPhase.Placement
            Centre               = worldCentre
            InnerRadius          = inner
            Correspondence       = None
            HostMeshName         = model.ActivePickingLayer
            CreatedAt            = System.DateTime.UtcNow
            DatasetColors        = assignColors model.MeshNames
            Probe                = ProbeNone
            ProbePreview         = ProbeNone
            ContactRings         = RingsNone
        }

    let private updatePin (id : ScanPinId) (f : ScanPin -> ScanPin) (sp : ScanPinModel) =
        match HashMap.tryFind id sp.Pins with
        | Some pin -> { sp with Pins = HashMap.add id (f pin) sp.Pins }
        | None -> sp

    let private discardActivePin (sp : ScanPinModel) =
        match ScanPinModel.activePlacementId sp with
        | Some id ->
            let selected = if sp.SelectedPin = Some id then None else sp.SelectedPin
            { sp with Pins = HashMap.remove id sp.Pins; SelectedPin = selected }
        | None -> sp

    let update (model : Model) (msg : ScanPinMessage) (sp : ScanPinModel) =
        match msg with
        | EnterAnchorPlacement ->
            let sp = discardActivePin sp
            { sp with Placement = AnchorPlacement }

        | CancelPlacement ->
            let sp = discardActivePin sp
            { sp with Placement = PlacementIdle }

        | PlaceAnchor worldCentre ->
            match sp.Placement with
            | AnchorPlacement ->
                let id = ScanPinId.create()
                let pin = makeAnchor model id worldCentre
                { sp with
                    Pins = HashMap.add id pin sp.Pins
                    Placement = AdjustingPin id
                    SelectedPin = Some id }
            | _ -> sp

        // InnerRadius = the "hard truth" (full opacity + probe weight inside).
        // Applies to the effective pin (placement flyout OR the dock's selected
        // pin), invalidating probe/preview/rings for lazy recompute.
        | SetInnerRadius r ->
            match ScanPinModel.effectivePinId sp with
            | Some id -> sp |> updatePin id (fun pin ->
                let r' = max 0.01 r
                { pin with InnerRadius = r'; Probe = ProbeNone; ProbePreview = ProbeNone; ContactRings = RingsNone })
            | None -> sp

        | RepositionPin (id, centre) ->
            // Move live during adjustment; probe + rings recompute.
            if ScanPinModel.activePlacementId sp = Some id then
                sp |> updatePin id (fun pin ->
                    { pin with Centre = centre; Probe = ProbeNone; ContactRings = RingsNone })
            else sp

        | CommitPin ->
            match ScanPinModel.activePlacementId sp with
            | Some id ->
                let sp = sp |> updatePin id (fun pin -> { pin with Phase = PinPhase.Committed })
                { sp with Placement = PlacementIdle }
            | None -> sp

        | DeletePin id ->
            let selected = if sp.SelectedPin = Some id then None else sp.SelectedPin
            let wasActive = ScanPinModel.activePlacementId sp = Some id
            let placement = if wasActive then PlacementIdle else sp.Placement
            { sp with Pins = HashMap.remove id sp.Pins; SelectedPin = selected; Placement = placement }

        | SelectPin id ->
            { sp with SelectedPin = id }

        // Stale guard: results only land while still ProbeRunning; any intervening invalidation wins.
        | ProbeComputed(id, result) ->
            sp |> updatePin id (fun pin ->
                if pin.Probe = ProbeRunning then { pin with Probe = ProbeReady result } else pin)

        | ProbeFailed(id, reason) ->
            sp |> updatePin id (fun pin ->
                if pin.Probe = ProbeRunning then { pin with Probe = ProbeError reason } else pin)

        | ProbePreviewComputed(id, result) ->
            sp |> updatePin id (fun pin ->
                if pin.ProbePreview = ProbeRunning then { pin with ProbePreview = ProbeReady result } else pin)

        | ProbePreviewFailed(id, reason) ->
            sp |> updatePin id (fun pin ->
                if pin.ProbePreview = ProbeRunning then { pin with ProbePreview = ProbeError reason } else pin)

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
        match msg with
        | PlaceAnchor _ ->
            match ScanPinModel.activePlacementId sp' with
            | Some id ->
                match HashMap.tryFind id sp'.Pins with
                | Some pin ->
                    let scale = activeScale model
                    let renderCentre = ScanPin.renderCentre model.CommonCentroid scale pin.Centre
                    env.Emit [CameraMessage (OrbitMessage.SetTargetCenter(true, AnimationKind.Tanh, renderCentre))]
                | None -> ()
            | None -> ()
        | _ -> ()
        // The bottom-dock inspector reads SelectedPin directly — no floating
        // card to create/position here.
        { model with ScanPins = sp' }

    // Lazy probe trigger, postlude after every reducer step: the effective pin
    // (card open) with an invalidated probe gets one debounced server query.
    // Drags coalesce; stale responses dropped by the ProbeRunning guard above.
    let ensureProbe (env : Env<Message>) (model : Model) : Model =
        let sp = model.ScanPins
        let effective =
            ScanPinModel.effectivePinId sp
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
                // pin.Probe is always the committed-pose probe; the preview pose gets its own ProbePreview.
                let meshes =
                    visible |> List.map (fun n -> n, (ModelTransforms.committedWorld model n).Forward)
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

    // Lazy preview-probe trigger (split violin): while a preview is pending, the effective pin
    // also gets a probe under effective (committed ∘ pending-delta) transforms.
    // Same debounce + stale-guard discipline as ensureProbe, separate token.
    let mutable previewProbeCts : System.Threading.CancellationTokenSource =
        new System.Threading.CancellationTokenSource()
    let mutable private previewProbeOwner : ScanPinId option = None

    let ensureProbePreview (env : Env<Message>) (model : Model) : Model =
        if not (PendingRegistration.isPreview model.PendingReg) then model
        else
            let sp = model.ScanPins
            let effective =
                ScanPinModel.effectivePinId sp
                |> Option.bind (fun id -> HashMap.tryFind id sp.Pins)
            match effective with
            | Some pin when (match pin.ProbePreview with ProbeNone -> true | _ -> false) ->
                let visible =
                    model.MeshNames |> IndexList.toList
                    |> List.filter (fun n -> Map.tryFind n model.MeshVisible |> Option.defaultValue true)
                match visible with
                | [] -> model
                | _ ->
                    let refMesh =
                        model.Registration.ReferenceMesh |> Option.filter (fun r -> List.contains r visible)
                        |> Option.orElse (pin.HostMeshName |> Option.filter (fun h -> List.contains h visible))
                        |> Option.defaultValue (List.head visible)
                    let meshes =
                        visible |> List.map (fun n -> n, (ModelTransforms.effectiveWorld model n).Forward)
                    let id = pin.Id
                    let centre = pin.Centre
                    let radius = pin.InnerRadius
                    let length = ScanPin.fixedProbeLength
                    previewProbeCts.Cancel()
                    previewProbeCts <- new System.Threading.CancellationTokenSource()
                    let sp =
                        match previewProbeOwner with
                        | Some prev when prev <> id ->
                            match HashMap.tryFind prev sp.Pins with
                            | Some p when p.ProbePreview = ProbeRunning ->
                                { sp with Pins = HashMap.add prev { p with ProbePreview = ProbeNone } sp.Pins }
                            | _ -> sp
                        | _ -> sp
                    previewProbeOwner <- Some id
                    let token = previewProbeCts.Token
                    task {
                        try
                            do! System.Threading.Tasks.Task.Delay(250, token)
                            let! res =
                                Query.probe ApiConfig.apiBase.Value meshes refMesh centre radius length 8192
                                |> Async.StartAsTask
                            if not token.IsCancellationRequested then
                                match res with
                                | Result.Ok r -> env.Emit [ScanPinMsg (ProbePreviewComputed(id, r))]
                                | Result.Error e -> env.Emit [ScanPinMsg (ProbePreviewFailed(id, e))]
                        with
                        | :? System.OperationCanceledException -> ()
                        | ex ->
                            if not token.IsCancellationRequested then
                                env.Emit [ScanPinMsg (ProbePreviewFailed(id, ex.Message))]
                    } |> ignore
                    { model with ScanPins = { sp with Pins = HashMap.add id { pin with ProbePreview = ProbeRunning } sp.Pins } }
            | _ -> model

    // Lazy contact-ring trigger, postlude after every reducer step: every RingsNone pin gets
    // one debounced fan-out over ALL meshes (visibility only gates rendering, so toggling never
    // recomputes). Transforms are rigid: sphere intersected in each mesh's own frame
    // (inverse-transformed centre), rings mapped back. Effective transforms → rings follow a pending preview.
    let ensureRings (env : Env<Message>) (model : Model) : Model =
        let sp = model.ScanPins
        let pending =
            sp.Pins |> HashMap.toList
            |> List.filter (fun (_, p) -> p.ContactRings = RingsNone)
        if List.isEmpty pending || model.MeshNames.Count = 0 then model
        else
            let meshes =
                model.MeshNames |> IndexList.toList |> List.map (fun n ->
                    n, ModelTransforms.effectiveWorld model n)
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
