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
    | ScanPinMsg              of ScanPinMessage
    | PinViewCameraMessage    of OrbitMessage

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
          ExtentForward = 100.0; ExtentBackward = 100.0 }

    let private dummyCutResults (meshNames : IndexList<string>) (prism : SelectionPrism) (_cutPlane : CutPlaneMode) =
        let r = match prism.Footprint.Vertices with v :: _ -> v.Length | _ -> 1.0
        meshNames |> IndexList.toArray |> Array.mapi (fun i name ->
            let offset = float i * 0.3
            let pts = [ for x in -20 .. 20 -> V2d(float x * r * 0.1, sin (float x * 0.3 + offset) * r * 0.2 + offset * r * 0.15) ]
            name, { MeshName = name; Polylines = [pts] }
        ) |> Map.ofArray

    let update (model : Model) (msg : ScanPinMessage) (sp : ScanPinModel) =
        match msg with
        | StartPlacement mode ->
            let sp =
                match sp.ActivePlacement with
                | Some id -> { sp with Pins = HashMap.remove id sp.Pins; ActivePlacement = None }
                | None -> sp
            { sp with PlacingMode = Some mode }

        | CancelPlacement ->
            let sp =
                match sp.ActivePlacement with
                | Some id -> { sp with Pins = HashMap.remove id sp.Pins; ActivePlacement = None }
                | None -> sp
            { sp with PlacingMode = None }

        | SetAnchor(_worldPos, renderPos, camFwd) ->
            let sp =
                match sp.ActivePlacement with
                | Some oldId -> { sp with Pins = HashMap.remove oldId sp.Pins }
                | None -> sp
            let id = ScanPinId.create()
            let axis = -camFwd |> Vec.normalize
            let prism = makeDefaultPrism renderPos axis 1.0
            let cam = { Center = model.Camera.center; Radius = model.Camera.radius; Phi = model.Camera.phi; Theta = model.Camera.theta }
            let pin =
                { Id = id; Phase = PinPhase.Placement; Prism = prism
                  CutPlane = CutPlaneMode.AlongAxis 0.0
                  CreationCameraState = cam
                  CutResults = dummyCutResults model.MeshNames prism (CutPlaneMode.AlongAxis 0.0)
                  DatasetColors = assignColors model.MeshNames }
            { sp with Pins = HashMap.add id pin sp.Pins; ActivePlacement = Some id; SelectedPin = Some id; PlacingMode = None }

        | SetFootprintRadius radius ->
            match sp.ActivePlacement with
            | Some id ->
                match HashMap.tryFind id sp.Pins with
                | Some pin when pin.Phase = PinPhase.Placement ->
                    let r = max 0.1 radius
                    let prism = makeDefaultPrism pin.Prism.AnchorPoint pin.Prism.AxisDirection r
                    let pin = { pin with Prism = prism; CutResults = dummyCutResults model.MeshNames prism pin.CutPlane }
                    { sp with Pins = HashMap.add id pin sp.Pins }
                | _ -> sp
            | None -> sp

        | CloseFootprint -> sp

        | SetCutPlaneMode mode ->
            match sp.ActivePlacement with
            | Some id ->
                match HashMap.tryFind id sp.Pins with
                | Some pin when pin.Phase = PinPhase.Placement ->
                    let pin = { pin with CutPlane = mode; CutResults = dummyCutResults model.MeshNames pin.Prism mode }
                    { sp with Pins = HashMap.add id pin sp.Pins }
                | _ -> sp
            | None -> sp

        | SetCutPlaneAngle deg ->
            match sp.ActivePlacement with
            | Some id ->
                match HashMap.tryFind id sp.Pins with
                | Some pin when pin.Phase = PinPhase.Placement ->
                    let mode = CutPlaneMode.AlongAxis deg
                    let pin = { pin with CutPlane = mode; CutResults = dummyCutResults model.MeshNames pin.Prism mode }
                    { sp with Pins = HashMap.add id pin sp.Pins }
                | _ -> sp
            | None -> sp

        | SetCutPlaneDistance dist ->
            match sp.ActivePlacement with
            | Some id ->
                match HashMap.tryFind id sp.Pins with
                | Some pin when pin.Phase = PinPhase.Placement ->
                    let mode = CutPlaneMode.AcrossAxis dist
                    let pin = { pin with CutPlane = mode; CutResults = dummyCutResults model.MeshNames pin.Prism mode }
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
                    let pin = { pin with Prism = prism; CutResults = dummyCutResults model.MeshNames prism pin.CutPlane }
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
        | PinViewCameraMessage msg ->
            { model with PinViewCamera = OrbitController.update (Env.map PinViewCameraMessage env) model.PinViewCamera msg }
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
            let model = { model with ScanPins = sp' }
            if sp'.SelectedPin <> sp.SelectedPin then
                match sp'.SelectedPin with
                | Some id ->
                    match HashMap.tryFind id sp'.Pins with
                    | Some pin ->
                        let r = match pin.Prism.Footprint.Vertices with v :: _ -> v.Length * 4.0 | _ -> 4.0
                        { model with PinViewCamera = OrbitState.create V3d.Zero model.Camera.phi model.Camera.theta r Button.Left Button.Middle }
                    | None -> model
                | None -> model
            else model
