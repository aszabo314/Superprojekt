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
    | GridEvalLoaded          of ScanPinId * GridEvalData
    | StratigraphyComputed    of ScanPinId * StratigraphyData
    | ScanPinMsg              of ScanPinMessage
    | TogglePinAxisVertical
    | ToggleDepthShade
    | ToggleIsolines
    | ToggleColorMode
    | CardMsg of CardMessage

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
    | CommitPin
    | DeletePin of ScanPinId
    | SelectPin of ScanPinId option
    | FocusPin of ScanPinId
    // V3 messages
    | SetStratigraphyDisplay  of ScanPinId * StratigraphyDisplayMode
    | SetGhostClip            of ScanPinId * GhostClipMode
    | SetShowCutPlaneLines    of ScanPinId * bool
    | SetShowCylinderEdgeLines of ScanPinId * bool
    | SetExplosionEnabled     of ScanPinId * bool
    | SetExplosionFactor      of ScanPinId * float
    | HoverBetweenSpace       of ScanPinId * columnIdx:int * hoverZ:float
    | PinBetweenSpaceHover    of ScanPinId
    | ClearBetweenSpaceHover  of ScanPinId

module CardUpdate =

    let private cardContentPinId (c : CardContent) =
        match c with
        | StratigraphyDiagram id | PinControls id -> id

    let update (msg : CardMessage) (cs : CardSystemModel) =
        match msg with
        | CreateCardsForPin(pinId, anchor) ->
            let hasCards =
                cs.Cards |> HashMap.exists (fun _ c -> cardContentPinId c.Content = pinId)
            if hasCards then
                let cards = cs.Cards |> HashMap.map (fun _ c ->
                    if cardContentPinId c.Content = pinId then { c with Visible = true; Anchor = match c.Anchor with AnchorToWorldPoint _ -> AnchorToWorldPoint anchor | a -> a }
                    else { c with Visible = false })
                { cs with Cards = cards }
            else
                let hideOthers = cs.Cards |> HashMap.map (fun _ c -> { c with Visible = false })
                let stratId = CardId.create()
                let ctrlId = CardId.create()
                let z = cs.NextZOrder
                let strat = { Id = stratId; Anchor = AnchorToWorldPoint anchor; Attachment = CardAttached; Size = V2d(310, 385); Content = StratigraphyDiagram pinId; Visible = true; ZOrder = z }
                let ctrl  = { Id = ctrlId;  Anchor = AnchorToCard(stratId, EdgeBottom); Attachment = CardAttached; Size = V2d(310, 160); Content = PinControls pinId; Visible = true; ZOrder = z + 1 }
                let cards = hideOthers |> HashMap.add stratId strat |> HashMap.add ctrlId ctrl
                { cs with Cards = cards; NextZOrder = z + 2 }

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

    let private makeDefaultPrism (anchor : V3d) (axis : V3d) (radius : float) =
        let n = 32
        let verts = [ for i in 0 .. n - 1 -> let a = float i / float n * Constant.PiTimesTwo in V2d(cos a, sin a) * radius ]
        { AnchorPoint = anchor; AxisDirection = axis
          Footprint = { Vertices = verts }
          ExtentForward = 5.0; ExtentBackward = 5.0 }

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
            let axis = if model.PinAxisVertical then V3d.OOI else -camFwd |> Vec.normalize
            let prism = makeDefaultPrism renderPos axis 1.0
            let cam = { Center = model.Camera.center; Radius = model.Camera.radius; Phi = model.Camera.phi; Theta = model.Camera.theta }
            let pin =
                { Id = id; Phase = PinPhase.Placement; Prism = prism
                  CutPlane = CutPlaneMode.AlongAxis 0.0
                  CreationCameraState = cam
                  CutResults = Map.empty
                  DatasetColors = assignColors model.MeshNames
                  GridEval = None
                  Stratigraphy = None
                  StratigraphyDisplay = Undistorted
                  GhostClip = GhostClipOff
                  ExtractedLines = ExtractedLinesMode.initial
                  Explosion = ExplosionState.initial
                  BetweenSpaceHover = None }
            { sp with Pins = HashMap.add id pin sp.Pins; ActivePlacement = Some id; SelectedPin = Some id }

        | SetFootprintRadius radius ->
            match sp.ActivePlacement with
            | Some id ->
                match HashMap.tryFind id sp.Pins with
                | Some pin when pin.Phase = PinPhase.Placement ->
                    let r = max 0.1 radius
                    let prism = makeDefaultPrism pin.Prism.AnchorPoint pin.Prism.AxisDirection r
                    let pin = { pin with Prism = prism }
                    { sp with Pins = HashMap.add id pin sp.Pins }
                | _ -> sp
            | None -> sp

        | CloseFootprint -> sp

        | SetCutPlaneMode mode ->
            let targetId = sp.ActivePlacement |> Option.orElse sp.SelectedPin
            match targetId with
            | Some id ->
                match HashMap.tryFind id sp.Pins with
                | Some pin ->
                    let pin = { pin with CutPlane = mode }
                    { sp with Pins = HashMap.add id pin sp.Pins }
                | _ -> sp
            | None -> sp

        | SetCutPlaneAngle deg ->
            let targetId = sp.ActivePlacement |> Option.orElse sp.SelectedPin
            match targetId with
            | Some id ->
                match HashMap.tryFind id sp.Pins with
                | Some pin ->
                    let pin = { pin with CutPlane = CutPlaneMode.AlongAxis deg }
                    { sp with Pins = HashMap.add id pin sp.Pins }
                | _ -> sp
            | None -> sp

        | SetCutPlaneDistance dist ->
            let targetId = sp.ActivePlacement |> Option.orElse sp.SelectedPin
            match targetId with
            | Some id ->
                match HashMap.tryFind id sp.Pins with
                | Some pin ->
                    let pin = { pin with CutPlane = CutPlaneMode.AcrossAxis dist }
                    { sp with Pins = HashMap.add id pin sp.Pins }
                | _ -> sp
            | None -> sp

        | SetFootprintScale scale ->
            match sp.ActivePlacement with
            | Some id ->
                match HashMap.tryFind id sp.Pins with
                | Some pin when pin.Phase = PinPhase.Placement ->
                    let s = max 0.1 scale
                    let prism = makeDefaultPrism pin.Prism.AnchorPoint pin.Prism.AxisDirection s
                    let pin = { pin with Prism = prism }
                    { sp with Pins = HashMap.add id pin sp.Pins }
                | _ -> sp
            | None -> sp

        | CommitPin ->
            match sp.ActivePlacement with
            | Some id ->
                match HashMap.tryFind id sp.Pins with
                | Some pin ->
                    let cam = { Center = model.Camera.center; Radius = model.Camera.radius; Phi = model.Camera.phi; Theta = model.Camera.theta }
                    let pin = { pin with Phase = PinPhase.Committed; CreationCameraState = cam }
                    { sp with Pins = HashMap.add id pin sp.Pins; ActivePlacement = None; PlacingMode = None }
                | None -> sp
            | None -> sp

        | DeletePin id ->
            let selected = if sp.SelectedPin = Some id then None else sp.SelectedPin
            let active = if sp.ActivePlacement = Some id then None else sp.ActivePlacement
            { sp with Pins = HashMap.remove id sp.Pins; SelectedPin = selected; ActivePlacement = active }

        | SelectPin id ->
            { sp with SelectedPin = id }

        | FocusPin _ -> sp

        | SetStratigraphyDisplay(id, mode) ->
            match HashMap.tryFind id sp.Pins with
            | Some pin -> { sp with Pins = HashMap.add id { pin with StratigraphyDisplay = mode } sp.Pins }
            | None -> sp

        | SetGhostClip(id, mode) ->
            match HashMap.tryFind id sp.Pins with
            | Some pin -> { sp with Pins = HashMap.add id { pin with GhostClip = mode } sp.Pins }
            | None -> sp

        | SetShowCutPlaneLines(id, on) ->
            match HashMap.tryFind id sp.Pins with
            | Some pin ->
                let el = { pin.ExtractedLines with ShowCutPlaneLines = on }
                { sp with Pins = HashMap.add id { pin with ExtractedLines = el } sp.Pins }
            | None -> sp

        | SetShowCylinderEdgeLines(id, on) ->
            match HashMap.tryFind id sp.Pins with
            | Some pin ->
                let el = { pin.ExtractedLines with ShowCylinderEdgeLines = on }
                { sp with Pins = HashMap.add id { pin with ExtractedLines = el } sp.Pins }
            | None -> sp

        | SetExplosionEnabled(id, on) ->
            match HashMap.tryFind id sp.Pins with
            | Some pin ->
                let ex = { pin.Explosion with Enabled = on }
                { sp with Pins = HashMap.add id { pin with Explosion = ex } sp.Pins }
            | None -> sp

        | SetExplosionFactor(id, f) ->
            match HashMap.tryFind id sp.Pins with
            | Some pin ->
                let ex = { pin.Explosion with ExpansionFactor = max 0.0 f }
                { sp with Pins = HashMap.add id { pin with Explosion = ex } sp.Pins }
            | None -> sp

        | HoverBetweenSpace(id, col, z) ->
            match HashMap.tryFind id sp.Pins with
            | Some pin ->
                let pinned = pin.BetweenSpaceHover |> Option.map (fun h -> h.Pinned) |> Option.defaultValue false
                if pinned then sp
                else
                    let h = { ColumnIdx = col; HoverZ = z; Pinned = false }
                    { sp with Pins = HashMap.add id { pin with BetweenSpaceHover = Some h } sp.Pins }
            | None -> sp

        | PinBetweenSpaceHover id ->
            match HashMap.tryFind id sp.Pins with
            | Some pin ->
                match pin.BetweenSpaceHover with
                | Some h ->
                    let h = { h with Pinned = not h.Pinned }
                    { sp with Pins = HashMap.add id { pin with BetweenSpaceHover = Some h } sp.Pins }
                | None -> sp
            | None -> sp

        | ClearBetweenSpaceHover id ->
            match HashMap.tryFind id sp.Pins with
            | Some pin ->
                match pin.BetweenSpaceHover with
                | Some h when h.Pinned -> sp
                | _ -> { sp with Pins = HashMap.add id { pin with BetweenSpaceHover = None } sp.Pins }
            | None -> sp

module Update =
    let private cutDebounce = ref (new System.Threading.CancellationTokenSource())
    let private gridDebounce = ref (new System.Threading.CancellationTokenSource())
    let private stratDebounce = ref (new System.Threading.CancellationTokenSource())

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
                DatasetCentroids = if dataset <> "" then Map.add dataset common model.DatasetCentroids else model.DatasetCentroids }
        | SetVisible(name, v) ->
            { model with MeshVisible = Map.add name v model.MeshVisible }
        | ToggleMenu ->
            { model with MenuOpen = not model.MenuOpen }
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
                { model with ClipBounds = padded; ClipBox = padded }
        | ToggleClip ->
            { model with ClipActive = not model.ClipActive }
        | SetClipBox box ->
            { model with ClipBox = box }
        | ResetClip ->
            { model with ClipBox = model.ClipBounds }
        | DatasetsLoaded datasets ->
            { model with Datasets = datasets |> Array.toList }
        | SetActiveDataset dataset ->
            { model with ActiveDataset = Some dataset }
        | SetDatasetScale(dataset, scale) ->
            { model with DatasetScales = Map.add dataset scale model.DatasetScales }
        | TogglePinAxisVertical ->
            { model with PinAxisVertical = not model.PinAxisVertical }
        | ToggleDepthShade ->
            { model with DepthShadeOn = not model.DepthShadeOn }
        | ToggleIsolines ->
            { model with IsolinesOn = not model.IsolinesOn }
        | ToggleColorMode ->
            { model with ColorMode = not model.ColorMode }
        | CardMsg msg ->
            { model with CardSystem = CardUpdate.update msg model.CardSystem }
        | CutResultsLoaded(pinId, results) ->
            let sp = model.ScanPins
            match HashMap.tryFind pinId sp.Pins with
            | Some pin ->
                let pin = { pin with CutResults = results }
                { model with ScanPins = { sp with Pins = HashMap.add pinId pin sp.Pins } }
            | None -> model
        | GridEvalLoaded(pinId, data) ->
            let sp = model.ScanPins
            match HashMap.tryFind pinId sp.Pins with
            | Some pin ->
                let pin = { pin with GridEval = Some data }
                { model with ScanPins = { sp with Pins = HashMap.add pinId pin sp.Pins } }
            | None -> model
        | StratigraphyComputed(pinId, data) ->
            let sp = model.ScanPins
            match HashMap.tryFind pinId sp.Pins with
            | Some pin ->
                let pin = { pin with Stratigraphy = Some data }
                { model with ScanPins = { sp with Pins = HashMap.add pinId pin sp.Pins } }
            | None -> model
        | ScanPinMsg msg ->
            let sp = model.ScanPins
            let sp' = ScanPinUpdate.update model msg sp
            match msg with
            | FocusPin id ->
                match HashMap.tryFind id sp.Pins with
                | Some pin ->
                    let c = pin.CreationCameraState
                    env.Emit [CameraMessage (OrbitMessage.SetTarget(true, c.Center, c.Radius, c.Phi, c.Theta))]
                | None -> ()
            | _ -> ()
            let needsCutUpdate =
                match msg with
                | SetAnchor _ | SetFootprintRadius _ | SetCutPlaneMode _ | SetCutPlaneAngle _ | SetCutPlaneDistance _ | SetFootprintScale _ -> true
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
                        let up = if abs axis.Z > 0.9 then V3d.OIO else V3d.OOI
                        let right = Vec.cross axis up |> Vec.normalize
                        let fwd = Vec.cross right axis |> Vec.normalize
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
                                let results = System.Collections.Generic.Dictionary<string, CutResult>()
                                for name in names do
                                    let! segments =
                                        Query.planeIntersection ApiConfig.apiBase.Value name 0 planePoint planeNormal axisU axisV 0.5 extentU extentV
                                        |> Async.StartAsTask
                                    if segments.Length > 0 then
                                        let polylines = segments |> List.map (fun (a, b) -> [V2d(a.X * scale, a.Y * scale); V2d(b.X * scale, b.Y * scale)])
                                        results.[name] <- { MeshName = name; Polylines = polylines }
                                if results.Count > 0 then
                                    env.Emit [CutResultsLoaded(pinId, results |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq)]
                            with
                            | :? System.Threading.Tasks.TaskCanceledException -> ()
                            | e ->
                                env.Emit [LogDebug (sprintf "computePlaneCuts ERROR: %s" (string e))]
                        } |> ignore
                    | None -> ()
                | None -> ()
            let needsStrat =
                match msg with
                | SetAnchor _ | SetFootprintRadius _ | SetFootprintScale _ -> true
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
                                do! System.Threading.Tasks.Task.Delay(400, cts.Token)
                                let! data = Stratigraphy.compute ApiConfig.apiBase.Value dataset prism model.CommonCentroid scale 360 |> Async.StartAsTask
                                if not cts.Token.IsCancellationRequested then
                                    env.Emit [StratigraphyComputed(pinId, data)]
                            with
                            | :? System.Threading.Tasks.TaskCanceledException -> ()
                            | e -> env.Emit [LogDebug (sprintf "stratigraphy ERROR: %s" (string e))]
                        } |> ignore
                    | None -> ()
                | None -> ()
            let needsGridEval =
                match msg with
                | SetAnchor _ | SetFootprintRadius _ | SetFootprintScale _ -> true
                | _ -> false
            if needsGridEval then
                let targetId = sp'.ActivePlacement |> Option.orElse sp'.SelectedPin
                match targetId with
                | Some id ->
                    match HashMap.tryFind id sp'.Pins with
                    | Some pin ->
                        let names = model.MeshNames
                        let prism = pin.Prism
                        let radius = match prism.Footprint.Vertices with v :: _ -> v.Length | _ -> 1.0
                        let dataset = match IndexList.tryFirst names with Some n -> n.Split('/', 2).[0] | None -> ""
                        let scale = model.DatasetScales |> Map.tryFind dataset |> Option.defaultValue 1.0
                        let anchor = prism.AnchorPoint / scale + model.CommonCentroid
                        let axis = prism.AxisDirection |> Vec.normalize
                        let pinId = id
                        let cts = new System.Threading.CancellationTokenSource()
                        gridDebounce.Value.Cancel()
                        gridDebounce.Value <- cts
                        task {
                            try
                                do! System.Threading.Tasks.Task.Delay(500, cts.Token)
                                let! result =
                                    Query.gridEval ApiConfig.apiBase.Value dataset anchor axis (radius / scale) 16 (prism.ExtentForward / scale) (prism.ExtentBackward / scale)
                                    |> Async.StartAsTask
                                env.Emit [GridEvalLoaded(pinId, result)]
                            with
                            | :? System.Threading.Tasks.TaskCanceledException -> ()
                            | e ->
                                env.Emit [LogDebug (sprintf "gridEval ERROR: %s" (string e))]
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
                            | StratigraphyDiagram pid | PinControls pid when pid = id ->
                                match c.Anchor with
                                | AnchorToWorldPoint _ -> { c with Anchor = AnchorToWorldPoint anchor }
                                | _ -> c
                            | _ -> c)
                        { model with CardSystem = { cs with Cards = cards } }
                    | None -> model
                | None -> model
