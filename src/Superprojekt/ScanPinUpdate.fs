namespace Superprojekt

open Aardvark.Base
open Aardworx.WebAssembly
open FSharp.Data.Adaptive
open Aardvark.Dom
open Superprojekt

module ScanPinUpdate =

    let mutable lineQueryCts : System.Threading.CancellationTokenSource =
        new System.Threading.CancellationTokenSource()

    let private assignColors (meshNames : IndexList<string>) =
        meshNames |> IndexList.toArray |> Array.mapi (fun i n -> n, Primitives.meshColor i) |> Map.ofArray

    let activeScale (model : Model) =
        model.ActiveDataset
        |> Option.bind (fun ds -> Map.tryFind ds model.DatasetScales)
        |> Option.defaultValue 1.0

    // Metric defaults: 5 m hard core, +1 m falloff delta (i.e. absolute
    // FalloffRadius = 6 m). Constants — datasets in real-world metres get the
    // same starting pin regardless of scene size.
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
            Payload              = Point { ReliabilityWeight = 1.0 }
            HostMeshName         = model.ActivePickingLayer
            CorrespondenceLinkId = None
            CreationCameraState  = cam
            CreatedAt            = System.DateTime.UtcNow
            DatasetColors        = assignColors model.MeshNames
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
                    { pin with InnerRadius = r'; FalloffRadius = r' + delta }
                else pin)
            | None -> sp

        | SetFalloffDelta d ->
            match ScanPinModel.activePlacementId sp with
            | Some id -> sp |> updatePin id (fun pin ->
                if pin.Phase = PinPhase.Placement then
                    { pin with FalloffRadius = pin.InnerRadius + max 0.0 d }
                else pin)
            | None -> sp

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
                    { pin with Payload = payload })

        | SetReliabilityWeight(id, w) ->
            sp |> updatePin id (fun pin ->
                match pin.Payload with
                | Point _ ->
                    let w = clamp 0.0 1.0 w
                    { pin with Payload = Point { ReliabilityWeight = w } }
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
