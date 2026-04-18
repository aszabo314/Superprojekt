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
    | CycleMeshOrder     of int
    | ToggleRevolver
    | ToggleFullscreen
    | SetRevolverCenter         of V2d
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
    | CutResultsLoaded        of ScanPinId * Map<string, CutResult>
    | StratigraphyComputed    of ScanPinId * StratigraphyData * BandCache
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
    | ToggleExplorePopover
    | ToggleGearPopover
    | SetRevolverRadius of float
    | EditPin of ScanPinId

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
    | StartPlacement of FootprintMode
    | CancelPlacement
    | SetAnchor of point : V3d * renderPos : V3d * cameraForward : V3d
    | SetFootprintRadius of float
    | CloseFootprint
    | SetCutPlaneMode of CutPlaneMode
    | SetCutPlaneAngle of float
    | SetCutPlaneDistance of float
    | SetFootprintScale of float
    | SetPinLength of float
    | CommitPin
    | DeletePin of ScanPinId
    | SelectPin of ScanPinId option
    | FocusPin of ScanPinId
    | SetStratigraphyDisplay  of ScanPinId * StratigraphyDisplayMode
    | SetGhostClip            of ScanPinId * GhostClipMode
    | SetGhostClipCutPlane    of ScanPinId * bool
    | SetShowCutPlaneLines    of ScanPinId * bool
    | SetShowCylinderEdgeLines of ScanPinId * bool
    | ToggleBetweenSpaceEnabled
    | HoverBetweenSpace       of ScanPinId * columnIdx:int * hoverZ:float
    | PinBetweenSpaceHover    of ScanPinId
    | ClearBetweenSpaceHover  of ScanPinId

module CardUpdate =

    let private cardContentPinId (c : CardContent) =
        match c with
        | StratigraphyDiagram id -> id

    let update (msg : CardMessage) (cs : CardSystemModel) =
        match msg with
        | CreateCardsForPin(pinId, anchor) ->
            let hasCard =
                cs.Cards |> HashMap.exists (fun _ c ->
                    match c.Content with StratigraphyDiagram id when id = pinId -> true | _ -> false)
            if hasCard then
                let cards = cs.Cards |> HashMap.map (fun _ c ->
                    match c.Content with
                    | StratigraphyDiagram id when id = pinId ->
                        { c with Visible = true; Anchor = AnchorToWorldPoint anchor }
                    | _ -> { c with Visible = false })
                { cs with Cards = cards }
            else
                let hideOthers = cs.Cards |> HashMap.map (fun _ c -> { c with Visible = false })
                let stratId = CardId.create()
                let z = cs.NextZOrder
                let strat = { Id = stratId; Anchor = AnchorToWorldPoint anchor; Attachment = CardAttached; Size = V2d(310, 430); Content = StratigraphyDiagram pinId; Visible = true; ZOrder = z }
                let cards = hideOthers |> HashMap.add stratId strat
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

    let private meshColors =
        [| C4b(228uy,26uy,28uy); C4b(55uy,126uy,184uy); C4b(77uy,175uy,74uy); C4b(152uy,78uy,163uy)
           C4b(255uy,127uy,0uy); C4b(255uy,255uy,51uy); C4b(166uy,86uy,40uy); C4b(247uy,129uy,191uy); C4b(153uy,153uy,153uy) |]

    let private assignColors (meshNames : IndexList<string>) =
        meshNames |> IndexList.toArray |> Array.mapi (fun i n -> n, meshColors.[i % meshColors.Length]) |> Map.ofArray

    let private circleFootprint (radius : float) =
        let n = 32
        [ for i in 0 .. n - 1 -> let a = float i / float n * Constant.PiTimesTwo in V2d(cos a, sin a) * radius ]

    let private setRadius (pin : ScanPin) (r : float) =
        { pin with Prism = { pin.Prism with Footprint = { Vertices = circleFootprint r } } }

    let private makeDefaultPrism (anchor : V3d) (axis : V3d) (radius : float) =
        { AnchorPoint = anchor; AxisDirection = axis
          Footprint = { Vertices = circleFootprint radius }
          ExtentForward = 1.0; ExtentBackward = 3.0 }

    let private updatePin (id : ScanPinId) (f : ScanPin -> ScanPin) (sp : ScanPinModel) =
        match HashMap.tryFind id sp.Pins with
        | Some pin -> { sp with Pins = HashMap.add id (f pin) sp.Pins }
        | None -> sp

    let private updateTarget (f : ScanPin -> ScanPin) (sp : ScanPinModel) =
        match sp.ActivePlacement |> Option.orElse sp.SelectedPin with
        | Some id -> updatePin id f sp
        | None -> sp

    let update (model : Model) (msg : ScanPinMessage) (sp : ScanPinModel) =
        match msg with
        | StartPlacement mode ->
            let sp =
                match sp.ActivePlacement with
                | Some id ->
                    let selected = if sp.SelectedPin = Some id then None else sp.SelectedPin
                    { sp with Pins = HashMap.remove id sp.Pins; ActivePlacement = None; SelectedPin = selected }
                | None -> sp
            { sp with PlacingMode = Some mode }

        | CancelPlacement ->
            let sp =
                match sp.ActivePlacement with
                | Some id ->
                    let selected = if sp.SelectedPin = Some id then None else sp.SelectedPin
                    { sp with Pins = HashMap.remove id sp.Pins; ActivePlacement = None; SelectedPin = selected }
                | None -> sp
            { sp with PlacingMode = None }

        | SetAnchor(_worldPos, renderPos, camFwd) ->
            if sp.PlacingMode.IsNone then sp
            else
            let id = match sp.ActivePlacement with Some id -> id | None -> ScanPinId.create()
            let axis =
                match model.ReferenceAxis with
                | AlongWorldZ -> V3d.OOI
                | AlongCameraView -> -camFwd |> Vec.normalize
            let prism = makeDefaultPrism renderPos axis 1.0
            let cam = { Center = model.Camera.center; Radius = model.Camera.radius; Phi = model.Camera.phi; Theta = model.Camera.theta }
            let pin =
                { Id = id; Phase = PinPhase.Placement; Prism = prism
                  CutPlane = CutPlaneMode.AlongAxis 0.0
                  CreationCameraState = cam
                  CutResults = Map.empty
                  CutResultsPlane = CutPlaneMode.AlongAxis 0.0
                  DatasetColors = assignColors model.MeshNames
                  Stratigraphy = None
                  BandCache = None
                  StratigraphyDisplay = Undistorted
                  GhostClip = GhostClipOff
                  GhostClipCutPlane = false
                  ExtractedLines = ExtractedLinesMode.initial
                  BetweenSpaceHover = None }
            { sp with Pins = HashMap.add id pin sp.Pins; ActivePlacement = Some id; SelectedPin = Some id }

        | SetFootprintRadius radius ->
            match sp.ActivePlacement with
            | Some id -> sp |> updatePin id (fun pin ->
                if pin.Phase = PinPhase.Placement then setRadius pin (max 0.1 radius) else pin)
            | None -> sp

        | CloseFootprint -> sp

        | SetCutPlaneMode mode ->
            sp |> updateTarget (fun pin -> { pin with CutPlane = mode })

        | SetCutPlaneAngle deg ->
            sp |> updateTarget (fun pin -> { pin with CutPlane = CutPlaneMode.AlongAxis deg })

        | SetCutPlaneDistance dist ->
            sp |> updateTarget (fun pin -> { pin with CutPlane = CutPlaneMode.AcrossAxis dist })

        | SetFootprintScale scale ->
            match sp.ActivePlacement with
            | Some id -> sp |> updatePin id (fun pin ->
                if pin.Phase = PinPhase.Placement then setRadius pin (max 0.1 scale) else pin)
            | None -> sp

        | SetPinLength length ->
            sp |> updateTarget (fun pin ->
                { pin with Prism = { pin.Prism with ExtentBackward = max 0.5 length } })

        | CommitPin ->
            match sp.ActivePlacement with
            | Some id ->
                let cam = { Center = model.Camera.center; Radius = model.Camera.radius; Phi = model.Camera.phi; Theta = model.Camera.theta }
                let sp = sp |> updatePin id (fun pin -> { pin with Phase = PinPhase.Committed; CreationCameraState = cam })
                { sp with ActivePlacement = None; PlacingMode = None }
            | None -> sp

        | DeletePin id ->
            let selected = if sp.SelectedPin = Some id then None else sp.SelectedPin
            let active = if sp.ActivePlacement = Some id then None else sp.ActivePlacement
            { sp with Pins = HashMap.remove id sp.Pins; SelectedPin = selected; ActivePlacement = active }

        | SelectPin id ->
            { sp with SelectedPin = id }

        | FocusPin _ -> sp

        | SetStratigraphyDisplay(id, mode) ->
            sp |> updatePin id (fun pin -> { pin with StratigraphyDisplay = mode })

        | SetGhostClip(id, mode) ->
            sp |> updatePin id (fun pin -> { pin with GhostClip = mode })

        | SetGhostClipCutPlane(id, on) ->
            sp |> updatePin id (fun pin -> { pin with GhostClipCutPlane = on })

        | SetShowCutPlaneLines(id, on) ->
            sp |> updatePin id (fun pin -> { pin with ExtractedLines = { pin.ExtractedLines with ShowCutPlaneLines = on } })

        | SetShowCylinderEdgeLines(id, on) ->
            sp |> updatePin id (fun pin -> { pin with ExtractedLines = { pin.ExtractedLines with ShowCylinderEdgeLines = on } })

        | ToggleBetweenSpaceEnabled ->
            { sp with BetweenSpaceEnabled = not sp.BetweenSpaceEnabled }

        | HoverBetweenSpace(id, col, z) ->
            if not sp.BetweenSpaceEnabled then sp
            else
                sp |> updatePin id (fun pin ->
                    let pinned = pin.BetweenSpaceHover |> Option.map (fun h -> h.Pinned) |> Option.defaultValue false
                    if pinned then pin
                    else { pin with BetweenSpaceHover = Some { ColumnIdx = col; HoverZ = z; Pinned = false } })

        | PinBetweenSpaceHover id ->
            sp |> updatePin id (fun pin ->
                match pin.BetweenSpaceHover with
                | Some h -> { pin with BetweenSpaceHover = Some { h with Pinned = not h.Pinned } }
                | None -> pin)

        | ClearBetweenSpaceHover id ->
            sp |> updatePin id (fun pin ->
                match pin.BetweenSpaceHover with
                | Some h when h.Pinned -> pin
                | _ -> { pin with BetweenSpaceHover = None })

module Update =
    let private cutDebounce = ref (new System.Threading.CancellationTokenSource())
    let private stratDebounce = ref (new System.Threading.CancellationTokenSource())

    let update (env : Env<Message>) (model : Model) (msg : Message) =
        match msg with
        | CameraMessage msg ->
            let swallow =
                if AVal.force PinCylinderDrag.isActive then
                    match msg with
                    | OrbitMessage.PointerDown _ | OrbitMessage.PointerMove _ | OrbitMessage.PointerUp _ -> true
                    | _ -> false
                else false
            if swallow then model
            else { model with Camera = OrbitController.update (Env.map CameraMessage env) model.Camera msg }
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
            { model with MeshVisible = Map.add name v model.MeshVisible }
        | ToggleMenu ->
            let sp = model.ScanPins
            if sp.PlacingMode.IsSome || sp.ActivePlacement.IsSome then model
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
        | CycleMeshOrder delta ->
            let n = model.MeshOrder.Count
            let d = (delta % n) + n
            { model with MeshOrder = model.MeshOrder |> HashMap.map (fun _ idx -> (idx + d) % n) }
        | ToggleRevolver ->
            { model with RevolverOn = not model.RevolverOn }
        | ToggleFullscreen ->
            { model with FullscreenOn = not model.FullscreenOn }
        | SetRevolverCenter ndc ->
            { model with RevolverCenter = ndc }
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
                { model with
                    ClipBounds = padded
                    ClipBox = padded
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
                    Explore = { model.Explore with Enabled = false }
                    ExplorePopoverOpen = false
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
        | ToggleExplorePopover ->
            { model with ExplorePopoverOpen = not model.ExplorePopoverOpen }
        | ToggleGearPopover ->
            { model with GearPopoverOpen = not model.GearPopoverOpen }
        | SetRevolverRadius r ->
            { model with RevolverSettings = { model.RevolverSettings with CircleRadius = max 20.0 (min 400.0 r) } }
        | EditPin id ->
            let sp = model.ScanPins
            match HashMap.tryFind id sp.Pins with
            | Some pin ->
                let pin = { pin with Phase = PinPhase.Placement }
                let sp = { sp with Pins = HashMap.add id pin sp.Pins; ActivePlacement = Some id; SelectedPin = Some id; PlacingMode = None }
                { model with ScanPins = sp }
            | None -> model
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
        | CutResultsLoaded(pinId, results) ->
            let sp = model.ScanPins
            match HashMap.tryFind pinId sp.Pins with
            | Some pin ->
                let pin = { pin with CutResults = results; CutResultsPlane = pin.CutPlane }
                { model with ScanPins = { sp with Pins = HashMap.add pinId pin sp.Pins } }
            | None -> model
        | StratigraphyComputed(pinId, data, cache) ->
            let sp = model.ScanPins
            match HashMap.tryFind pinId sp.Pins with
            | Some pin ->
                let pin = { pin with Stratigraphy = Some data; BandCache = Some cache }
                { model with ScanPins = { sp with Pins = HashMap.add pinId pin sp.Pins } }
            | None -> model
        | ScanPinMsg msg ->
            let sp = model.ScanPins
            let sp' = ScanPinUpdate.update model msg sp
            let wasPlacing = sp.PlacingMode.IsSome || sp.ActivePlacement.IsSome
            let isPlacing = sp'.PlacingMode.IsSome || sp'.ActivePlacement.IsSome
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
            | SetAnchor(_, renderPos, _) ->
                env.Emit [CameraMessage (OrbitMessage.SetTargetCenter(true, AnimationKind.Tanh, renderPos))]
            | _ -> ()
            let needsCutUpdate =
                match msg with
                | SetAnchor _ | SetFootprintRadius _ | SetCutPlaneMode _ | SetCutPlaneAngle _ | SetCutPlaneDistance _ | SetFootprintScale _ | SetPinLength _ -> true
                | _ -> false
            if needsCutUpdate then
                let targetId = sp'.ActivePlacement |> Option.orElse sp'.SelectedPin
                match targetId with
                | Some id ->
                    match HashMap.tryFind id sp'.Pins with
                    | Some pin ->
                        let cc = model.CommonCentroid
                        let names = model.MeshNames
                        let prism = pin.Prism
                        let radius = match prism.Footprint.Vertices with v :: _ -> v.Length | _ -> 1.0
                        let axis = prism.AxisDirection |> Vec.normalize
                        let right, fwd = PinGeometry.axisFrame axis
                        let dataset = match IndexList.tryFirst names with Some n -> n.Split('/', 2).[0] | None -> ""
                        let scale = model.DatasetScales |> Map.tryFind dataset |> Option.defaultValue 1.0
                        let planePoint, planeNormal, axisU, axisV, extentU, extentV =
                            match pin.CutPlane with
                            | CutPlaneMode.AlongAxis angleDeg ->
                                let a = angleDeg * Constant.RadiansPerDegree
                                let planeDir = right * cos a + fwd * sin a
                                let normal = Vec.cross planeDir axis |> Vec.normalize
                                prism.AnchorPoint / scale + cc, normal, planeDir, axis, radius / scale, max prism.ExtentForward prism.ExtentBackward / scale
                            | CutPlaneMode.AcrossAxis dist ->
                                prism.AnchorPoint / scale + axis * dist / scale + cc, axis, right, fwd, radius / scale, radius / scale
                        let pinId = id
                        let cts = new System.Threading.CancellationTokenSource()
                        cutDebounce.Value.Cancel()
                        cutDebounce.Value <- cts
                        task {
                            try
                                do! System.Threading.Tasks.Task.Delay(300, cts.Token)
                                let nameArr = names |> Seq.toArray
                                let! batch =
                                    Query.planeIntersectionBatch ApiConfig.apiBase.Value nameArr planePoint planeNormal axisU axisV 0.5 extentU extentV
                                    |> Async.StartAsTask
                                if not cts.IsCancellationRequested then
                                    let results =
                                        batch
                                        |> List.choose (fun (name, segments) ->
                                            if segments.Length > 0 then
                                                let polylines = segments |> List.map (fun (a, b) -> [V2d(a.X * scale, a.Y * scale); V2d(b.X * scale, b.Y * scale)])
                                                Some (name, { MeshName = name; Polylines = polylines })
                                            else None)
                                        |> Map.ofList
                                    env.Emit [CutResultsLoaded(pinId, results)]
                            with
                            | :? System.Threading.Tasks.TaskCanceledException -> ()
                            | _ -> ()
                        } |> ignore
                    | None -> ()
                | None -> ()
            let needsStrat =
                match msg with
                | SetAnchor _ | SetFootprintRadius _ | SetFootprintScale _ | SetPinLength _ -> true
                | _ -> false
            if needsStrat then
                let targetId = sp'.ActivePlacement |> Option.orElse sp'.SelectedPin
                match targetId with
                | Some id ->
                    match HashMap.tryFind id sp'.Pins with
                    | Some pin ->
                        let names = model.MeshNames
                        let prism = pin.Prism
                        let dataset = match IndexList.tryFirst names with Some n -> n.Split('/', 2).[0] | None -> ""
                        let scale = model.DatasetScales |> Map.tryFind dataset |> Option.defaultValue 1.0
                        let pinId = id
                        let cts = new System.Threading.CancellationTokenSource()
                        stratDebounce.Value.Cancel()
                        stratDebounce.Value <- cts
                        task {
                            try
                                do! System.Threading.Tasks.Task.Delay(500, cts.Token)
                                if cts.Token.IsCancellationRequested then () else
                                let! data = Stratigraphy.compute ApiConfig.apiBase.Value dataset prism model.CommonCentroid scale 180 |> Async.StartAsTask
                                if not cts.Token.IsCancellationRequested then
                                    let cache = Stratigraphy.buildBandCache data
                                    if not cts.Token.IsCancellationRequested then
                                        env.Emit [StratigraphyComputed(pinId, data, cache)]
                            with
                            | :? System.Threading.Tasks.TaskCanceledException -> ()
                            | _ -> ()
                        } |> ignore
                    | None -> ()
                | None -> ()
            let model = { model with ScanPins = sp' }
            let selChanged = sp'.SelectedPin <> sp.SelectedPin || sp'.ActivePlacement <> sp.ActivePlacement
            if selChanged then
                let effectiveId = sp'.ActivePlacement |> Option.orElse sp'.SelectedPin
                match effectiveId with
                | Some id ->
                    match HashMap.tryFind id sp'.Pins with
                    | Some pin ->
                        let cs = CardUpdate.update (CreateCardsForPin(id, pin.Prism.AnchorPoint)) model.CardSystem
                        { model with CardSystem = cs }
                    | None ->
                        let cs = CardUpdate.update (RemoveCardsForPin id) model.CardSystem
                        { model with CardSystem = cs }
                | None ->
                    let cs = model.CardSystem
                    let cards = cs.Cards |> HashMap.map (fun _ c -> { c with Visible = false })
                    { model with CardSystem = { cs with Cards = cards } }
            else
                // Sync card anchor when active pin's prism moves
                let effectiveId = sp'.ActivePlacement |> Option.orElse sp'.SelectedPin
                match effectiveId with
                | Some id ->
                    match HashMap.tryFind id sp'.Pins with
                    | Some pin ->
                        let anchor = pin.Prism.AnchorPoint
                        let cs = model.CardSystem
                        let cards = cs.Cards |> HashMap.map (fun _ c ->
                            match c.Content with
                            | StratigraphyDiagram pid when pid = id ->
                                { c with Anchor = AnchorToWorldPoint anchor }
                            | _ -> c)
                        { model with CardSystem = { cs with Cards = cards } }
                    | None -> model
                | None -> model
