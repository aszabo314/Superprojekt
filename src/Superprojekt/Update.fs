namespace Superprojekt

open Aardvark.Base
open Aardworx.WebAssembly
open FSharp.Data.Adaptive
open Aardvark.Dom
open Superprojekt

type Message =
    | CameraMessage      of OrbitMessage
    | CentroidsLoaded    of (string * V3d)[]
    | LoadFinished       of string
    | SetVisible         of string * bool
    | ToggleMenu
    | FilteredMeshLoaded of string * V3d * int[]    // (mesh name, selection point, index buffer)
    | ClearFilteredMesh
    | LogDebug           of string
    | ToggleFullscreen
    | ToggleDifferenceRendering
    | ToggleGhostSilhouette
    | SetGhostOpacity of float
    | SetMinDifferenceDepth of float
    | SetMaxDifferenceDepth of float
    | ClipBoundsLoaded   of (string * Box3d)[]
    | ToggleClip
    | SetClipBox         of Box3d
    | ResetClip
    | DatasetsLoaded     of string[]
    | SetActiveDataset   of string
    | SetDatasetScale    of string * float
    | ScanPinMsg              of ScanPinMessage
    | JumpToMesh of string
    | ToggleColorMode
    | CardMsg of CardMessage
    | ExploreMsg of ExploreModeMessage
    | SetRenderingMode of RenderingMode
    | ToggleMeshSolo of string
    | ShowAllMeshes
    | HideAllMeshes
    | ResetCamera
    | SetExploreCardPos of V2d
    | ToggleGearPopover
    | EditPin of ScanPinId
    // V6 §D.1 — mesh-wheel
    | SetActivePickingLayer of string option
    // V6 §D.3 — polygonal lasso
    | LassoBegin
    | LassoAddVertex of V2d
    | LassoCommit    of viewTrafo:Trafo3d * projTrafo:Trafo3d * vpSize:V2i
    | LassoCancel
    | LassoClear

and ExploreModeMessage =
    | SetExploreEnabled of bool
    | SetHighlightMode of ExploreHighlightMode
    | SetSteepnessThreshold of float
    | SetDisagreementThreshold of float
    | SetReferenceAxisMode of ReferenceAxisMode

and CardMessage =
    | BringToFront of CardId
    | FinishDrag of CardId * finalPos:V2d
    | RedockCard of CardId
    | CreateCardsForPin of ScanPinId * anchor:V3d
    | RemoveCardsForPin of ScanPinId

and ScanPinMessage =
    | EnterAnchorPlacement
    | CancelPlacement
    | PlaceAnchor of centre:V3d
    | SetAnchorRadius of float
    | SetAnchorSigma of float
    | CommitPin
    | DeletePin of ScanPinId
    | SelectPin of ScanPinId option
    | FocusPin of ScanPinId
    // V6 §D.7 payloads — the flyout's Payload-type selector switches the
    // active payload (destroys + reinstantiates per §D.6.4). Reliability
    // weight on Point payloads is editable from both flyout and card.
    | ChangePayloadType of ScanPinId * PayloadKind
    | SetReliabilityWeight of ScanPinId * float
    // V6 §D.7.2 — line payload
    | SetLineMode of ScanPinId * LineMode
    | IsolineComputed of ScanPinId * V3d[] * elevation:float

module CardUpdate =

    let private cardContentPinId (c : CardContent) =
        match c with
        | PinCard id -> id

    let update (msg : CardMessage) (cs : CardSystemModel) =
        match msg with
        | CreateCardsForPin(pinId, anchor) ->
            let hasCard =
                cs.Cards |> HashMap.exists (fun _ c ->
                    match c.Content with PinCard id when id = pinId -> true | _ -> false)
            if hasCard then
                let cards = cs.Cards |> HashMap.map (fun _ c ->
                    match c.Content with
                    | PinCard id when id = pinId ->
                        { c with Visible = true; Anchor = AnchorToWorldPoint anchor }
                    | _ -> { c with Visible = false })
                { cs with Cards = cards }
            else
                let hideOthers = cs.Cards |> HashMap.map (fun _ c -> { c with Visible = false })
                let cardId = CardId.create()
                let z = cs.NextZOrder
                let card = { Id = cardId; Anchor = AnchorToWorldPoint anchor; Attachment = CardAttached; Size = V2d(310, 230); Content = PinCard pinId; Visible = true; ZOrder = z }
                let cards = hideOthers |> HashMap.add cardId card
                { cs with Cards = cards; NextZOrder = z + 1 }

        | RemoveCardsForPin pinId ->
            let cards = cs.Cards |> HashMap.map (fun _ c ->
                if cardContentPinId c.Content = pinId then { c with Visible = false } else c)
            { cs with Cards = cards }

        | FinishDrag(id, finalPos) ->
            match HashMap.tryFind id cs.Cards with
            | Some card ->
                { cs with Cards = HashMap.add id { card with Attachment = CardDetached finalPos } cs.Cards }
            | None -> cs

        | RedockCard id ->
            match HashMap.tryFind id cs.Cards with
            | Some card ->
                { cs with Cards = HashMap.add id { card with Attachment = CardAttached } cs.Cards }
            | None -> cs

        | BringToFront id ->
            match HashMap.tryFind id cs.Cards with
            | Some card ->
                let z = cs.NextZOrder
                { cs with Cards = HashMap.add id { card with ZOrder = z } cs.Cards; NextZOrder = z + 1 }
            | None -> cs

module ScanPinUpdate =

    let private assignColors (meshNames : IndexList<string>) =
        meshNames |> IndexList.toArray |> Array.mapi (fun i n -> n, Primitives.meshColor i) |> Map.ofArray

    /// V6 §D.6.1 default radius is 5% of the dataset's bounding-box diagonal.
    let defaultRadius (model : Model) =
        if model.ClipBounds.IsInvalid then 1.0
        else max 0.1 (model.ClipBounds.Size.Length * 0.05)

    let private makeAnchor (model : Model) (id : ScanPinId) (centre : V3d) (radius : float) =
        let sigma = radius * 0.5
        let cam = { Center = model.Camera.center; Radius = model.Camera.radius; Phi = model.Camera.phi; Theta = model.Camera.theta }
        {
            Id                   = id
            Phase                = PinPhase.Placement
            Centre               = centre
            Radius               = radius
            Sigma                = sigma
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

        | PlaceAnchor centre ->
            match sp.Placement with
            | AnchorPlacement ->
                let id = ScanPinId.create()
                let pin = makeAnchor model id centre (defaultRadius model)
                { sp with
                    Pins = HashMap.add id pin sp.Pins
                    Placement = AdjustingPin id
                    SelectedPin = Some id }
            | _ -> sp

        | SetAnchorRadius r ->
            match ScanPinModel.activePlacementId sp with
            | Some id -> sp |> updatePin id (fun pin ->
                if pin.Phase = PinPhase.Placement then
                    let r = max 0.05 r
                    { pin with Radius = r; Sigma = min pin.Sigma r }
                else pin)
            | None -> sp

        | SetAnchorSigma s ->
            match ScanPinModel.activePlacementId sp with
            | Some id -> sp |> updatePin id (fun pin ->
                if pin.Phase = PinPhase.Placement then
                    { pin with Sigma = clamp 0.01 pin.Radius s }
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
                    let payload = PayloadType.defaultFor pin.Radius pin.Centre pin.HostMeshName kind
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
                    // Mode change wipes the cached polyline; a fresh trace
                    // is fired by the View handler once the new mode lands.
                    { pin with Payload = Line { lp with Mode = mode; Points = [||]; ScalarVals = [||]; CrossMeshTraces = Map.empty } }
                | _ -> pin)

        | IsolineComputed(id, pts, _elevation) ->
            // Points stay in world space (matches §C.3); render-space
            // conversion happens at draw time in ScanPinScene.
            sp |> updatePin id (fun pin ->
                match pin.Payload with
                | Line lp ->
                    let scalars = pts |> Array.map (fun p -> p.Z)
                    { pin with Payload = Line { lp with Points = pts; ScalarVals = scalars } }
                | _ -> pin)

module Update =

    let update (env : Env<Message>) (model : Model) (msg : Message) =
        match msg with
        | CameraMessage msg ->
            { model with Camera = OrbitController.update (Env.map CameraMessage env) model.Camera msg }
        | CentroidsLoaded centroids ->
            let common  = if centroids.Length > 0 then centroids |> Array.averageBy snd else V3d.Zero
            let names   = centroids |> Array.map fst |> IndexList.ofArray
            let visible = centroids |> Array.fold (fun m (n, _) -> Map.add n true m) Map.empty
            let indices = centroids |> Array.mapi (fun i (n,_) -> n,i) |> HashMap.ofArray
            let dataset =
                if centroids.Length > 0 then
                    let n = fst centroids.[0] in let s = n.IndexOf('/') in if s >= 0 then n.[..s-1] else ""
                else ""
            { model with
                MeshNames        = names
                MeshVisible      = visible
                CommonCentroid   = common
                MeshOrder        = indices
                MeshesLoaded     = HashSet.empty
                Filtered         = HashMap.empty
                FilterCenter     = None
                ClipBounds       = Box3d.Invalid
                ClipBox          = Box3d(V3d(-1e10), V3d(1e10))
                DatasetCentroids =
                    let perMesh = centroids |> Array.fold (fun m (n, c) -> Map.add n c m) model.DatasetCentroids
                    if dataset <> "" then Map.add dataset common perMesh else perMesh }
        | SetVisible(name, v) ->
            // V6 §D.1: if the active picking layer becomes invisible, clear it
            // so the next mesh-wheel scroll starts from a clean slate.
            let activePickingLayer =
                if not v && model.ActivePickingLayer = Some name then None
                else model.ActivePickingLayer
            { model with
                MeshVisible = Map.add name v model.MeshVisible
                ActivePickingLayer = activePickingLayer }
        | ToggleMenu ->
            let sp = model.ScanPins
            if ScanPinModel.isPlacing sp then model
            else { model with MenuOpen = not model.MenuOpen }
        | FilteredMeshLoaded(name, selPt, indices) ->
            { model with Filtered = HashMap.add name indices model.Filtered; FilterCenter = Some selPt }
        | LoadFinished name ->
            let model = { model with MeshesLoaded = HashSet.add name model.MeshesLoaded }


            let missing = HashSet.difference (HashSet.ofSeq model.MeshNames) model.MeshesLoaded
            if missing.Count = 0 then
                let d = Window.Document.CreateElement("div")
                d.Id <- "loading-done"
                d.Style.Visibility <- "hidden"
                d.Style.Position <- "fixed"
                d.Style.PointerEvents <- "none"
                Window.Document.Body.AppendChild(d) |> ignore
            model
        | ClearFilteredMesh ->
            { model with Filtered = HashMap.empty; FilterCenter = None }
        | LogDebug s ->
            let log = model.DebugLog.InsertAt(0, s)
            let log = if log.Count > 20 then IndexList.take 20 log else log
            { model with DebugLog = log }
        | ToggleFullscreen ->
            { model with FullscreenOn = not model.FullscreenOn }
        | ToggleDifferenceRendering ->
            { model with DifferenceRendering = not model.DifferenceRendering }
        | ToggleGhostSilhouette ->
            { model with GhostSilhouette = not model.GhostSilhouette }
        | SetGhostOpacity v ->
            { model with GhostOpacity = v }
        | SetMinDifferenceDepth v ->
            { model with MinDifferenceDepth = v }
        | SetMaxDifferenceDepth v ->
            { model with MaxDifferenceDepth = v }
        | ClipBoundsLoaded bboxes ->
            if bboxes.Length = 0 then model
            else
                let union =
                    bboxes |> Array.fold (fun (acc : Box3d) (_, b) ->
                        Box3d(
                            V3d(min acc.Min.X b.Min.X, min acc.Min.Y b.Min.Y, min acc.Min.Z b.Min.Z),
                            V3d(max acc.Max.X b.Max.X, max acc.Max.Y b.Max.Y, max acc.Max.Z b.Max.Z)
                        )) Box3d.Invalid
                let padded = Box3d(union.Min - V3d.III, union.Max + V3d.III)
                let scale =
                    match model.ActiveDataset with
                    | Some d -> Map.tryFind d model.DatasetScales |> Option.defaultValue 1.0
                    | None -> 1.0
                let renderDiag = union.Size.Length * scale
                let disagreementDefault = clamp 0.001 1.0 (renderDiag * 1e-3)
                let perMesh = bboxes |> Array.fold (fun m (n, b) -> Map.add n b m) Map.empty
                { model with
                    ClipBounds = padded
                    ClipBox = padded
                    MeshBounds = perMesh
                    Explore = { model.Explore with DisagreementThreshold = disagreementDefault } }
        | ToggleClip ->
            { model with ClipActive = not model.ClipActive }
        | SetClipBox box ->
            { model with ClipBox = box }
        | ResetClip ->
            { model with ClipBox = model.ClipBounds }
        | DatasetsLoaded datasets ->
            { model with Datasets = datasets |> Array.toList }
        | SetActiveDataset dataset ->
            if model.ActiveDataset = Some dataset then model
            else
                { model with
                    ActiveDataset = Some dataset
                    ScanPins = ScanPinModel.initial
                    Filtered = HashMap.empty
                    FilterCenter = None
                    MeshSolo = NoSolo
                    MeshBounds = Map.empty
                    ActivePickingLayer = None
                    LassoDrawing = None
                    LassoVolume = None
                    Explore = { model.Explore with Enabled = false }
                    CardSystem = { model.CardSystem with Cards = model.CardSystem.Cards |> HashMap.map (fun _ c -> { c with Visible = false }) } }
        | SetDatasetScale(dataset, scale) ->
            { model with DatasetScales = Map.add dataset scale model.DatasetScales }
        | JumpToMesh meshName ->
            match Map.tryFind meshName model.DatasetCentroids with
            | Some centroid ->
                let renderPos = (centroid - model.CommonCentroid) * (model.DatasetScales |> Map.tryFind (meshName.Split('/', 2).[0]) |> Option.defaultValue 1.0)
                let radius =
                    if model.ClipBounds.IsInvalid then 50.0
                    else model.ClipBounds.Size.Length * 0.6
                env.Emit [CameraMessage (OrbitMessage.SetTargetCenter(true, AnimationKind.Tanh, renderPos))]
                env.Emit [CameraMessage (OrbitMessage.SetTargetRadius(true, radius))]
            | None -> ()
            model
        | ToggleColorMode ->
            { model with ColorMode = not model.ColorMode }
        | SetRenderingMode m ->
            { model with RenderingMode = m; ColorMode = (m = Shaded) }
        | ToggleMeshSolo name ->
            match model.MeshSolo with
            | Solo(soloName, restore) when soloName = name ->
                { model with MeshVisible = restore; MeshSolo = NoSolo }
            | Solo(_, restore) ->
                let vis = restore |> Map.map (fun k _ -> k = name)
                { model with MeshVisible = vis; MeshSolo = Solo(name, restore) }
            | NoSolo ->
                let restore = model.MeshVisible
                let vis =
                    model.MeshNames |> IndexList.toSeq
                    |> Seq.map (fun n -> n, n = name) |> Map.ofSeq
                { model with MeshVisible = vis; MeshSolo = Solo(name, restore) }
        | ShowAllMeshes ->
            let vis = model.MeshNames |> IndexList.toSeq |> Seq.map (fun n -> n, true) |> Map.ofSeq
            { model with MeshVisible = vis; MeshSolo = NoSolo }
        | HideAllMeshes ->
            let vis = model.MeshNames |> IndexList.toSeq |> Seq.map (fun n -> n, false) |> Map.ofSeq
            { model with MeshVisible = vis; MeshSolo = NoSolo }
        | ResetCamera ->
            let center, radius =
                if model.ClipBounds.IsInvalid then V3d.Zero, 50.0
                else V3d.Zero, max 1.0 (model.ClipBounds.Size.Length * 0.6)
            env.Emit [CameraMessage (OrbitMessage.SetTargetCenter(true, AnimationKind.Tanh, center))]
            env.Emit [CameraMessage (OrbitMessage.SetTargetRadius(true, radius))]
            model
        | SetExploreCardPos pos ->
            { model with ExploreCardPos = Some pos }
        | ToggleGearPopover ->
            { model with GearPopoverOpen = not model.GearPopoverOpen }
        | EditPin id ->
            let sp = model.ScanPins
            match HashMap.tryFind id sp.Pins with
            | Some pin ->
                let pin = { pin with Phase = PinPhase.Placement }
                let sp = { sp with Pins = HashMap.add id pin sp.Pins; Placement = AdjustingPin id; SelectedPin = Some id }
                { model with ScanPins = sp }
            | None -> model
        | SetActivePickingLayer name ->
            { model with ActivePickingLayer = name }
        | LassoBegin ->
            // Mutually exclusive with anchor placement — discard any
            // in-flight anchor before entering lasso mode.
            let scanPins =
                match model.ScanPins.Placement with
                | AnchorPlacement -> { model.ScanPins with Placement = PlacementIdle }
                | _ -> model.ScanPins
            { model with ScanPins = scanPins; LassoDrawing = Some { Vertices = [||] } }
        | LassoAddVertex p ->
            match model.LassoDrawing with
            | Some d -> { model with LassoDrawing = Some { Vertices = Array.append d.Vertices [| p |] } }
            | None -> model
        | LassoCommit(viewTrafo, projTrafo, vpSize) ->
            match model.LassoDrawing with
            | Some d when d.Vertices.Length >= 3 ->
                let poly = d.Vertices
                let n = poly.Length
                // NDC: y flipped from screen px.
                let toNdc (px : V2d) =
                    V2d(2.0 * px.X / float vpSize.X - 1.0,
                        1.0 - 2.0 * px.Y / float vpSize.Y)
                let vp = viewTrafo * projTrafo
                let camPos = viewTrafo.Backward.TransformPos V3d.Zero
                // Near-plane unprojection per polygon vertex.
                let dirs =
                    poly |> Array.map (fun px ->
                        let ndc = toNdc px
                        let pNear = vp.Backward.TransformPosProj(V3d(ndc, -1.0))
                        (pNear - camPos) |> Vec.normalize)
                let planes =
                    Array.init n (fun i ->
                        let d0 = dirs.[i]
                        let d1 = dirs.[(i + 1) % n]
                        let normal = Vec.cross d0 d1 |> Vec.normalize
                        let offset = -(Vec.dot normal camPos)
                        V4d(normal.X, normal.Y, normal.Z, offset))
                // Orientation-fix: the polygon centroid (back-projected to mid
                // depth) must satisfy every plane inequality with signed dist ≤ 0.
                // If most planes report it outside, flip all of them.
                let centroidNdc =
                    let mutable s = V2d.Zero
                    for px in poly do
                        s <- s + toNdc px
                    s / float n
                let centroidWorld =
                    vp.Backward.TransformPosProj(V3d(centroidNdc, 0.0))
                let outside =
                    planes |> Array.sumBy (fun p ->
                        let d = p.X * centroidWorld.X + p.Y * centroidWorld.Y + p.Z * centroidWorld.Z + p.W
                        if d > 0.0 then 1 else 0)
                let planes =
                    if outside > n / 2 then planes |> Array.map (fun p -> -p)
                    else planes
                let volume = { Planes = planes; ScreenPolygon = poly; CommitVpSize = vpSize }
                { model with LassoDrawing = None; LassoVolume = Some volume }
            | _ ->
                { model with LassoDrawing = None }
        | LassoCancel ->
            { model with LassoDrawing = None }
        | LassoClear ->
            { model with LassoDrawing = None; LassoVolume = None }
        | CardMsg msg ->
            { model with CardSystem = CardUpdate.update msg model.CardSystem }
        | ExploreMsg msg ->
            let e = model.Explore
            match msg with
            | SetExploreEnabled v -> { model with Explore = { e with Enabled = v } }
            | SetHighlightMode m -> { model with Explore = { e with HighlightMode = m } }
            | SetSteepnessThreshold v -> { model with Explore = { e with SteepnessThreshold = v } }
            | SetDisagreementThreshold v -> { model with Explore = { e with DisagreementThreshold = v } }
            | SetReferenceAxisMode m -> { model with ReferenceAxis = m }
        | ScanPinMsg msg ->
            let sp = model.ScanPins
            let sp' = ScanPinUpdate.update model msg sp
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
                        env.Emit [CameraMessage (OrbitMessage.SetTargetCenter(true, AnimationKind.Tanh, pin.Centre))]
                    | None -> ()
                | None -> ()
            | ChangePayloadType(id, LineKind)
            | SetLineMode(id, _) ->
                match HashMap.tryFind id sp'.Pins with
                | Some pin ->
                    match pin.Payload, pin.HostMeshName with
                    | Line { Mode = ElevationIsoline elev }, Some host ->
                        let scale =
                            model.ActiveDataset
                            |> Option.bind (fun ds -> Map.tryFind ds model.DatasetScales)
                            |> Option.defaultValue 1.0
                        let seedWorld = pin.Centre / scale + model.CommonCentroid
                        // Slider elevation is given in render-space units
                        // (matches pin.Centre.Z); undo the dataset scale
                        // and centroid offset so the server sees world Z.
                        let elevWorld = elev / scale + model.CommonCentroid.Z
                        task {
                            try
                                let! pts =
                                    Query.isoline ApiConfig.apiBase.Value host elevWorld seedWorld 4096
                                    |> Async.StartAsTask
                                env.Emit [ScanPinMsg (IsolineComputed(id, pts, elevWorld))]
                            with _ -> ()
                        } |> ignore
                    | _ -> ()
                | None -> ()
            | _ -> ()
            let model = { model with ScanPins = sp' }
            let selChanged = sp'.SelectedPin <> sp.SelectedPin || ScanPinModel.activePlacementId sp' <> ScanPinModel.activePlacementId sp
            if selChanged then
                let effectiveId = ScanPinModel.activePlacementId sp' |> Option.orElse sp'.SelectedPin
                match effectiveId with
                | Some id ->
                    match HashMap.tryFind id sp'.Pins with
                    | Some pin ->
                        let cs = CardUpdate.update (CreateCardsForPin(id, pin.Centre)) model.CardSystem
                        { model with CardSystem = cs }
                    | None ->
                        let cs = CardUpdate.update (RemoveCardsForPin id) model.CardSystem
                        { model with CardSystem = cs }
                | None ->
                    let cs = model.CardSystem
                    let cards = cs.Cards |> HashMap.map (fun _ c -> { c with Visible = false })
                    { model with CardSystem = { cs with Cards = cards } }
            else
                // Sync card anchor when active pin's centre moves
                let effectiveId = ScanPinModel.activePlacementId sp' |> Option.orElse sp'.SelectedPin
                match effectiveId with
                | Some id ->
                    match HashMap.tryFind id sp'.Pins with
                    | Some pin ->
                        let anchor = pin.Centre
                        let cs = model.CardSystem
                        let cards = cs.Cards |> HashMap.map (fun _ c ->
                            match c.Content with
                            | PinCard pid when pid = id ->
                                { c with Anchor = AnchorToWorldPoint anchor }
                            | _ -> c)
                        { model with CardSystem = { cs with Cards = cards } }
                    | None -> model
                | None -> model
