namespace Superprojekt

open Aardvark.Base
open Aardworx.WebAssembly
open FSharp.Data.Adaptive
open Aardvark.Dom
open Superprojekt

module ScanPinUpdate =

    let mutable lineQueryCts : System.Threading.CancellationTokenSource =
        new System.Threading.CancellationTokenSource()

    let mutable probeCts : System.Threading.CancellationTokenSource =
        new System.Threading.CancellationTokenSource()
    let mutable private probeOwner : ScanPinId option = None

    // Contact-ring queries run per pin (several pins can recompute at once
    // after a registration), so each pin gets its own debounce token.
    let private ringsCts =
        System.Collections.Generic.Dictionary<ScanPinId, System.Threading.CancellationTokenSource>()

    let private assignColors (meshNames : IndexList<string>) =
        meshNames |> IndexList.toArray |> Array.mapi (fun i n -> n, Primitives.meshColor i) |> Map.ofArray

    let activeScale (model : Model) =
        DatasetScale.active model.ActiveDataset model.DatasetScales

    // Metric defaults: 5 m hard core, +1 m falloff delta.
    let defaultInnerRadius (_ : Model) = 5.0
    let defaultFalloffRadius (model : Model) = defaultInnerRadius model + 1.0

    // centre is in world-space metres.
    let private makeAnchor (model : Model) (id : ScanPinId) (worldCentre : V3d) =
        let inner   = defaultInnerRadius model
        let falloff = defaultFalloffRadius model
        let cam = { Center = model.Camera.center; Radius = model.Camera.radius; Phi = model.Camera.phi; Theta = model.Camera.theta }
        {
            Id                   = id
            Phase                = PinPhase.Placement
            Centre               = worldCentre
            InnerRadius          = inner
            FalloffRadius        = falloff
            Payload              = Point { ReliabilityWeight = 1.0; Correspondence = None }
            HostMeshName         = model.ActivePickingLayer
            CreationCameraState  = cam
            CreatedAt            = System.DateTime.UtcNow
            DatasetColors        = assignColors model.MeshNames
            Probe                = ProbeNone
            ProbePreview         = ProbeNone
            ProbeLengthOverride  = None
            ProbeLockOrder       = false
            ProbeXRange          = ProbeXAuto
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

        // InnerRadius is the "hard truth" and is unaffected by the falloff
        // slider or GhostOpacity changes. The falloff slider is *relative*:
        // its value is the delta added to InnerRadius. Moving the inner
        // slider preserves that delta (the falloff-zone thickness stays
        // constant) so the falloff slider doesn't jump under the user.
        | SetInnerRadius r ->
            match ScanPinModel.activePlacementId sp with
            | Some id -> sp |> updatePin id (fun pin ->
                if pin.Phase = PinPhase.Placement then
                    let r' = max 0.01 r
                    let delta = max 0.0 (pin.FalloffRadius - pin.InnerRadius)
                    { pin with InnerRadius = r'; FalloffRadius = r' + delta; Probe = ProbeNone; ContactRings = RingsNone }
                else pin)
            | None -> sp

        | SetFalloffDelta d ->
            match ScanPinModel.activePlacementId sp with
            | Some id -> sp |> updatePin id (fun pin ->
                if pin.Phase = PinPhase.Placement then
                    { pin with FalloffRadius = pin.InnerRadius + max 0.0 d }
                else pin)
            | None -> sp

        | RepositionPin (id, centre) ->
            // Move the pin live during adjustment; probe + rings recompute.
            if ScanPinModel.activePlacementId sp = Some id then
                sp |> updatePin id (fun pin ->
                    { pin with Centre = centre; Probe = ProbeNone; ContactRings = RingsNone })
            else sp

        | CommitPin ->
            match ScanPinModel.activePlacementId sp with
            | Some id ->
                let cam = { Center = model.Camera.center; Radius = model.Camera.radius; Phi = model.Camera.phi; Theta = model.Camera.theta }
                let sp = sp |> updatePin id (fun pin -> { pin with Phase = PinPhase.Committed; CreationCameraState = cam })
                { sp with Placement = PlacementIdle }
            | None -> sp

        | DeletePin id ->
            let selected = if sp.SelectedPin = Some id then None else sp.SelectedPin
            let wasActive = ScanPinModel.activePlacementId sp = Some id
            let placement = if wasActive then PlacementIdle else sp.Placement
            { sp with Pins = HashMap.remove id sp.Pins; SelectedPin = selected; Placement = placement }

        | SelectPin id ->
            { sp with SelectedPin = id }

        | FocusPin _ -> sp

        | ChangePayloadType(id, kind) ->
            sp |> updatePin id (fun pin ->
                if PayloadType.kind pin.Payload = kind then pin
                else
                    // PayloadType.defaultFor expects render-space (the Patch
                    // payload's Radius is render-space throughout the rest of
                    // the pipeline). Convert from metric here.
                    let scale = activeScale model
                    let payloadRadiusRender = ScanPin.renderLength scale pin.FalloffRadius
                    let renderCentre = ScanPin.renderCentre model.CommonCentroid scale pin.Centre
                    let payload = PayloadType.defaultFor payloadRadiusRender renderCentre pin.HostMeshName kind
                    { pin with Payload = payload; Probe = ProbeNone })

        | SetReliabilityWeight(id, w) ->
            sp |> updatePin id (fun pin ->
                match pin.Payload with
                | Point pp ->
                    let w = clamp 0.0 1.0 w
                    { pin with Payload = Point { pp with ReliabilityWeight = w } }
                | _ -> pin)

        | SetLineMode(id, mode) ->
            sp |> updatePin id (fun pin ->
                match pin.Payload with
                | Line lp ->
                    { pin with Payload = Line { lp with Mode = mode; Points = [||]; ScalarVals = [||]; CrossMeshTraces = Map.empty } }
                | _ -> pin)

        | IsolineComputed(id, pts, _elevation) ->
            sp |> updatePin id (fun pin ->
                match pin.Payload with
                | Line lp ->
                    let scalars = pts |> Array.map (fun p -> p.Z)
                    { pin with Payload = Line { lp with Points = pts; ScalarVals = scalars } }
                | _ -> pin)

        | RidgeComputed(id, pts, scalars) ->
            sp |> updatePin id (fun pin ->
                match pin.Payload with
                | Line lp ->
                    { pin with Payload = Line { lp with Points = pts; ScalarVals = scalars } }
                | _ -> pin)

        | LineCrossMeshComputed(id, mesh, pts, scalars) ->
            sp |> updatePin id (fun pin ->
                match pin.Payload with
                | Line lp ->
                    let map =
                        if pts.Length = 0 then Map.remove mesh lp.CrossMeshTraces
                        else Map.add mesh (pts, scalars) lp.CrossMeshTraces
                    { pin with Payload = Line { lp with CrossMeshTraces = map } }
                | _ -> pin)

        | PatchComputed(id, pts, refDir, normal) ->
            sp |> updatePin id (fun pin ->
                match pin.Payload with
                | Patch pp ->
                    let pp' =
                        { pp with
                            ProjectedPoints = pts
                            CompassNorth    = V2d(1.0, 0.0)
                            RefDirWorld     = refDir
                            NormalWorld     = normal }
                    { pin with Payload = Patch pp' }
                | _ -> pin)

        // Stale guard: results only land while the pin is still ProbeRunning;
        // anything that invalidated the probe in the meantime wins.
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

        | SetProbeLength(id, len) ->
            sp |> updatePin id (fun pin ->
                let len = len |> Option.map (fun l -> clamp 1.0 100.0 l)
                if pin.ProbeLengthOverride = len then pin
                else { pin with ProbeLengthOverride = len; Probe = ProbeNone })

        | ToggleProbeLockOrder id ->
            sp |> updatePin id (fun pin -> { pin with ProbeLockOrder = not pin.ProbeLockOrder })

        | SetProbeXRange(id, r) ->
            sp |> updatePin id (fun pin -> { pin with ProbeXRange = r })

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
        | FocusPin id ->
            match HashMap.tryFind id sp.Pins with
            | Some pin ->
                let c = pin.CreationCameraState
                env.Emit [CameraMessage (OrbitMessage.SetTarget(true, c.Center, c.Radius, c.Phi, c.Theta))]
            | None -> ()
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
        | ChangePayloadType(id, LineKind)
        | SetLineMode(id, _) ->
            match HashMap.tryFind id sp'.Pins with
            | Some pin ->
                let scale = activeScale model
                let seedWorld = pin.Centre
                let peers =
                    match pin.HostMeshName with
                    | Some host ->
                        model.MeshNames |> IndexList.toSeq
                        |> Seq.filter (fun n ->
                            n <> host
                            && Map.tryFind n model.MeshVisible |> Option.defaultValue true)
                        |> Array.ofSeq
                    | None -> [||]
                lineQueryCts.Cancel()
                lineQueryCts <- new System.Threading.CancellationTokenSource()
                let token = lineQueryCts.Token
                let emitIfLive m =
                    if not token.IsCancellationRequested then env.Emit [ScanPinMsg m]
                match pin.Payload, pin.HostMeshName with
                | Line { Mode = ElevationIsoline elev }, Some host ->
                    // elev is already world-space metres (LineMode is fed from
                    // pin.Centre.Z, which is metric).
                    let elevWorld = elev
                    let queryOne (name : string) (isHost : bool) =
                        task {
                            try
                                let! pts =
                                    Query.isoline ApiConfig.apiBase.Value name elevWorld seedWorld 4096
                                    |> Async.StartAsTask
                                if isHost then
                                    emitIfLive (IsolineComputed(id, pts, elevWorld))
                                else
                                    let scalars = pts |> Array.map (fun p -> p.Z)
                                    emitIfLive (LineCrossMeshComputed(id, name, pts, scalars))
                            with _ -> ()
                        } :> System.Threading.Tasks.Task
                    task {
                        try
                            do! System.Threading.Tasks.Task.Delay(250, token)
                            let all =
                                [|
                                    yield queryOne host true
                                    for peer in peers -> queryOne peer false
                                |]
                            do! System.Threading.Tasks.Task.WhenAll(all)
                        with
                        | :? System.OperationCanceledException -> ()
                        | _ -> ()
                    } |> ignore
                | Line { Mode = CurvatureRidge }, Some host ->
                    let queryOne (name : string) (isHost : bool) =
                        task {
                            try
                                let! pts, scalars =
                                    Query.curvatureRidge ApiConfig.apiBase.Value name seedWorld 0.4 4096
                                    |> Async.StartAsTask
                                if isHost then
                                    emitIfLive (RidgeComputed(id, pts, scalars))
                                else
                                    emitIfLive (LineCrossMeshComputed(id, name, pts, scalars))
                            with _ -> ()
                        } :> System.Threading.Tasks.Task
                    task {
                        try
                            do! System.Threading.Tasks.Task.Delay(250, token)
                            let all =
                                [|
                                    yield queryOne host true
                                    for peer in peers -> queryOne peer false
                                |]
                            do! System.Threading.Tasks.Task.WhenAll(all)
                        with
                        | :? System.OperationCanceledException -> ()
                        | _ -> ()
                    } |> ignore
                | _ -> ()
            | None -> ()
        | ChangePayloadType(id, PatchKind) ->
            match HashMap.tryFind id sp'.Pins with
            | Some pin ->
                match pin.Payload, pin.HostMeshName with
                | Patch pp, Some host ->
                    let scale = activeScale model
                    let centreWorld = pin.Centre
                    // pp.Radius stays render-space for the rest of the patch
                    // pipeline; convert to metres for the server query.
                    let radiusWorld = pp.Radius / scale
                    task {
                        try
                            let! pts, refDir, normal =
                                Query.patch ApiConfig.apiBase.Value host centreWorld radiusWorld 4096
                                |> Async.StartAsTask
                            env.Emit [ScanPinMsg (PatchComputed(id, pts, refDir, normal))]
                        with _ -> ()
                    } |> ignore
                | _ -> ()
            | None -> ()
        | _ -> ()
        let model = { model with ScanPins = sp' }
        let selChanged = sp'.SelectedPin <> sp.SelectedPin || ScanPinModel.activePlacementId sp' <> ScanPinModel.activePlacementId sp
        // CardSystem anchor is fed to Cards.projectToScreen, which uses the
        // viewTrafo over RENDER-space coordinates. Convert pin.Centre (metric)
        // → render-space before stashing it as the card anchor.
        let scale = activeScale model
        let cc = model.CommonCentroid
        let renderAnchor (pin : ScanPin) = ScanPin.renderCentre cc scale pin.Centre
        if selChanged then
            let effectiveId = ScanPinModel.activePlacementId sp' |> Option.orElse sp'.SelectedPin
            match effectiveId with
            | Some id ->
                match HashMap.tryFind id sp'.Pins with
                | Some pin ->
                    let cs = CardUpdate.update (CreateCardsForPin(id, renderAnchor pin)) model.CardSystem
                    { model with CardSystem = cs }
                | None ->
                    let cs = CardUpdate.update (RemoveCardsForPin id) model.CardSystem
                    { model with CardSystem = cs }
            | None ->
                let cs = model.CardSystem
                let cards = cs.Cards |> HashMap.map (fun _ c -> { c with Visible = false })
                { model with CardSystem = { cs with Cards = cards } }
        else
            let effectiveId = ScanPinModel.activePlacementId sp' |> Option.orElse sp'.SelectedPin
            match effectiveId with
            | Some id ->
                match HashMap.tryFind id sp'.Pins with
                | Some pin ->
                    let anchor = renderAnchor pin
                    let cs = model.CardSystem
                    let cards = cs.Cards |> HashMap.map (fun _ c ->
                        match c.Content with
                        | PinCard pid when pid = id ->
                            { c with Anchor = AnchorToWorldPoint anchor }
                        | _ -> c)
                    { model with CardSystem = { cs with Cards = cards } }
                | None -> model
            | None -> model

    // Lazy probe trigger, run as a postlude after every reducer step: the
    // effective pin (selected or being adjusted — i.e. its card is open) with
    // a Point payload and an invalidated probe gets one debounced server
    // query. Slider drags coalesce; stale responses are dropped by the
    // ProbeRunning guard above.
    let ensureProbe (env : Env<Message>) (model : Model) : Model =
        let sp = model.ScanPins
        let effective =
            ScanPinModel.activePlacementId sp
            |> Option.orElse sp.SelectedPin
            |> Option.bind (fun id -> HashMap.tryFind id sp.Pins)
        match effective with
        | Some pin when (match pin.Payload, pin.Probe with Point _, ProbeNone -> true | _ -> false) ->
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
                // pin.Probe is always the committed-pose probe; the preview
                // pose gets its own ProbePreview via ensureProbePreview.
                let meshes =
                    visible |> List.map (fun n -> n, (ModelTransforms.committedWorld model n).Forward)
                let id = pin.Id
                let centre = pin.Centre
                let radius = pin.InnerRadius
                let length = pin.ProbeLengthOverride |> Option.defaultValue 0.0
                probeCts.Cancel()
                probeCts <- new System.Threading.CancellationTokenSource()
                // The cancelled task never emits, so a different pin whose
                // probe was in flight would stay ProbeRunning forever — reset
                // it to ProbeNone so it lazily recomputes when reselected.
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

    // Lazy preview-probe trigger (split violin): while a registration preview
    // is pending, the effective pin additionally gets a probe under the
    // effective (committed ∘ pending-delta) transforms. Same debounce and
    // stale-guard discipline as ensureProbe, separate token.
    let mutable previewProbeCts : System.Threading.CancellationTokenSource =
        new System.Threading.CancellationTokenSource()
    let mutable private previewProbeOwner : ScanPinId option = None

    let ensureProbePreview (env : Env<Message>) (model : Model) : Model =
        if not (PendingRegistration.isPreview model.PendingReg) then model
        else
            let sp = model.ScanPins
            let effective =
                ScanPinModel.activePlacementId sp
                |> Option.orElse sp.SelectedPin
                |> Option.bind (fun id -> HashMap.tryFind id sp.Pins)
            match effective with
            | Some pin when (match pin.Payload, pin.ProbePreview with Point _, ProbeNone -> true | _ -> false) ->
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
                    let length = pin.ProbeLengthOverride |> Option.defaultValue 0.0
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

    // Lazy contact-ring trigger, run as a postlude after every reducer step:
    // every pin whose rings were invalidated (RingsNone) gets one debounced
    // server fan-out over ALL meshes — visibility only gates rendering, so
    // toggling a mesh never recomputes. Registration transforms are rigid:
    // the sphere is intersected in each mesh's own frame (inverse-transformed
    // centre) and the rings mapped back to registered world space. Effective
    // transforms: while a solve preview is pending the rings follow it
    // (invalidation on pending changes recomputes them).
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
