namespace Superprojekt

open Aardvark.Base
open Aardworx.WebAssembly
open FSharp.Data.Adaptive
open Aardvark.Dom
open Superprojekt

module Update =

    let private worldToRenderRigid (scale : float) (cc : V3d) (worldT : Trafo3d) =
        Trafo3d.Scale(1.0 / scale)
        * Trafo3d.Translation(cc)
        * worldT
        * Trafo3d.Translation(-cc)
        * Trafo3d.Scale(scale)

    let private renderToWorldRigid (scale : float) (cc : V3d) (renderT : Trafo3d) =
        Trafo3d.Translation(-cc)
        * Trafo3d.Scale(scale)
        * renderT
        * Trafo3d.Scale(1.0 / scale)
        * Trafo3d.Translation(cc)

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
                SceneBounds      = Box3d.Invalid
                DatasetCentroids =
                    let perMesh = centroids |> Array.fold (fun m (n, c) -> Map.add n c m) model.DatasetCentroids
                    if dataset <> "" then Map.add dataset common perMesh else perMesh }
        | SetVisible(name, v) ->
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
        | SetShadingStrength v ->
            { model with ShadingStrength = v }
        | SetSlopeThresholdDeg v ->
            { model with SlopeThresholdDeg = v }
        | ToggleAnchorGhostMode ->
            { model with AnchorGhostMode = not model.AnchorGhostMode }

        | SetRegistrationMode m ->
            { model with Registration = { model.Registration with Mode = m } }
        | SetReferenceMesh mesh ->
            { model with Registration = { model.Registration with ReferenceMesh = mesh } }
        | ResetMeshTransforms ->
            { model with
                MeshTransforms = Map.empty
                Registration = { model.Registration with LastResiduals = [||]; ConvergenceLog = [||]; Running = false } }
        | RunRegistration ->
            let reg = model.Registration
            match reg.ReferenceMesh with
            | None -> model
            | Some refMesh ->
                let visibleMeshes =
                    model.MeshNames |> IndexList.toSeq
                    |> Seq.filter (fun n ->
                        n <> refMesh
                        && Map.tryFind n model.MeshVisible |> Option.defaultValue true)
                    |> Array.ofSeq
                if visibleMeshes.Length = 0 then model
                else
                    let scale =
                        model.ActiveDataset
                        |> Option.bind (fun ds -> Map.tryFind ds model.DatasetScales)
                        |> Option.defaultValue 1.0
                    let cc = model.CommonCentroid
                    let anchors =
                        match reg.Mode with
                        | TraditionalIcp -> [||]
                        | RegionRestrictedIcp
                        | PointPairPlusRefinement ->
                            model.ScanPins.Pins |> HashMap.toSeq
                            |> Seq.choose (fun (_, pin) ->
                                if pin.Phase = PinPhase.Committed then
                                    let centreWorld = pin.Centre / scale + cc
                                    let sigmaWorld = pin.Sigma / scale
                                    let w =
                                        match pin.Payload with
                                        | Point pp -> pp.ReliabilityWeight
                                        | _ -> 1.0
                                    Some (centreWorld, sigmaWorld, w)
                                else None)
                            |> Array.ofSeq
                    let eps =
                        match reg.Mode with
                        | TraditionalIcp -> 0.0
                        | _ -> 0.05
                    for mov in visibleMeshes do
                        let initial =
                            Map.tryFind mov model.MeshTransforms
                            |> Option.map (fun t -> (renderToWorldRigid scale cc t).Forward)
                            |> Option.defaultValue M44d.Identity
                        let movName = mov
                        task {
                            try
                                let! trafo, conv, resi =
                                    Query.runIcp ApiConfig.apiBase.Value refMesh movName initial 50 30 anchors eps
                                    |> Async.StartAsTask
                                env.Emit [RegistrationComplete(movName, trafo, conv, resi)]
                            with ex ->
                                env.Emit [RegistrationFailed (sprintf "%s: %s" movName ex.Message)]
                        } |> ignore
                    { model with Registration = { reg with Running = true; ConvergenceLog = [||]; LastResiduals = [||] } }
        | RegistrationProgress _ ->
            model
        | RegistrationComplete(mesh, trafo, conv, resi) ->
            let meshScale =
                let dataset = mesh.Split('/', 2).[0]
                Map.tryFind dataset model.DatasetScales |> Option.defaultValue 1.0
            let renderTrafo = worldToRenderRigid meshScale model.CommonCentroid trafo
            let mt = Map.add mesh renderTrafo model.MeshTransforms
            let iters =
                conv |> Array.mapi (fun i rms -> { Iter = i; Rms = rms })
            let meshRms =
                if resi.Length = 0 then 0.0
                else sqrt ((resi |> Array.sumBy (fun x -> x * x)) / float resi.Length)
            let algoMap = Map.add mesh meshRms model.MeshAlgorithmResidual
            { model with
                MeshTransforms = mt
                MeshAlgorithmResidual = algoMap
                Registration = { model.Registration with
                                    LastResiduals = resi
                                    ConvergenceLog = iters
                                    Running = false } }
        | RegistrationFailed err ->
            let log = model.DebugLog.InsertAt(0, sprintf "registration failed: %s" err)
            { model with
                DebugLog = log
                Registration = { model.Registration with Running = false } }

        | SetMeshSensorType(name, sensor) ->
            { model with MeshSensorTypes = Map.add name sensor model.MeshSensorTypes }
        | SetMeshDatasetError(name, valueOpt) ->
            match valueOpt with
            | Some v -> { model with MeshDatasetErrors = Map.add name v model.MeshDatasetErrors }
            | None -> { model with MeshDatasetErrors = Map.remove name model.MeshDatasetErrors }
        | ToggleProvenanceHeatmap ->
            { model with ProvenanceHeatmap = not model.ProvenanceHeatmap }
        | SetProvenanceThreshold v ->
            { model with ProvenanceThreshold = v }
        | ToggleFalloffZoneOnly ->
            { model with FalloffZoneOnly = not model.FalloffZoneOnly }
        | ToggleFusionMode ->
            { model with FusionMode = not model.FusionMode }

        | SaveWorkspace ->
            let json = Persistence.serialize model
            let escaped =
                json.Replace("\\", "\\\\").Replace("`", "\\`").Replace("$", "\\$")
            let stamp = System.DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")
            let script = Window.Document.CreateElement("script")
            script.InnerText <-
                sprintf """
                var b = new Blob([`%s`], {type:'application/json'});
                var u = URL.createObjectURL(b);
                var a = document.createElement('a');
                a.href = u; a.download = 'workspace-%s.scanpin.json';
                document.body.appendChild(a); a.click(); document.body.removeChild(a);
                URL.revokeObjectURL(u);
                """ escaped stamp
            Window.Document.Body.AppendChild(script) |> ignore
            Window.Document.Body.RemoveChild(script) |> ignore
            model

        | LoadWorkspace json ->
            match Persistence.deserialize model json with
            | Result.Ok m ->
                let log = m.DebugLog.InsertAt(0, "workspace loaded")
                { m with DebugLog = log }
            | Result.Error err ->
                let log = model.DebugLog.InsertAt(0, sprintf "workspace load failed: %s" err)
                { model with DebugLog = log }
        | SetMinDifferenceDepth v ->
            { model with MinDifferenceDepth = v }
        | SetMaxDifferenceDepth v ->
            { model with MaxDifferenceDepth = v }
        | SceneBoundsLoaded bboxes ->
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
                    SceneBounds = padded
                    MeshBounds = perMesh
                    Explore = { model.Explore with Disagreement = { model.Explore.Disagreement with Threshold = disagreementDefault } } }
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
                    if model.SceneBounds.IsInvalid then 50.0
                    else model.SceneBounds.Size.Length * 0.6
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
                if model.SceneBounds.IsInvalid then V3d.Zero, 50.0
                else V3d.Zero, max 1.0 (model.SceneBounds.Size.Length * 0.6)
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
                let toNdc (px : V2d) =
                    V2d(2.0 * px.X / float vpSize.X - 1.0,
                        1.0 - 2.0 * px.Y / float vpSize.Y)
                let vp = viewTrafo * projTrafo
                let camPos = viewTrafo.Backward.TransformPos V3d.Zero
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
            | SetSignalEnabled(sig_, on) ->
                let next =
                    match sig_ with
                    | FeatureConfidenceSignal -> { e with FeatureConfidence = { e.FeatureConfidence with Enabled = on } }
                    | DisagreementSignal      -> { e with Disagreement      = { e.Disagreement with Enabled = on } }
                { model with Explore = next }
            | SetSignalThreshold(sig_, v) ->
                let next =
                    match sig_ with
                    | FeatureConfidenceSignal -> { e with FeatureConfidence = { e.FeatureConfidence with Threshold = v } }
                    | DisagreementSignal      -> { e with Disagreement      = { e.Disagreement with Threshold = v } }
                { model with Explore = next }
            | SetSignalColor(sig_, c) ->
                let next =
                    match sig_ with
                    | FeatureConfidenceSignal -> { e with FeatureConfidence = { e.FeatureConfidence with Color = c } }
                    | DisagreementSignal      -> { e with Disagreement      = { e.Disagreement with Color = c } }
                { model with Explore = next }
            | SetMixMode m -> { model with Explore = { e with MixMode = m } }
            | SetReferenceAxisMode m -> { model with ReferenceAxis = m }
        | ScanPinMsg msg ->
            ScanPinUpdate.handleMsg env model msg
