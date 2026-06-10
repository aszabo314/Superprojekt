namespace Superprojekt

open Aardvark.Base
open Aardworx.WebAssembly
open Microsoft.JSInterop
open FSharp.Data.Adaptive
open Aardvark.Dom
open Superprojekt

module ServerActions =

    let loadDataset (env : Env<Message>) (dataset : string) =
        task {
            try
                let! cs = MeshData.fetchCentroids ApiConfig.apiBase.Value dataset
                env.Emit [CentroidsLoaded cs]
            with _ -> ()
            try
                let! bboxes = MeshData.fetchBboxes ApiConfig.apiBase.Value dataset
                env.Emit [SceneBoundsLoaded bboxes]
            with _ -> ()
        } |> ignore

    let init (env : Env<Message>) =
        task {
            try
                let! datasets = MeshData.fetchDatasets ApiConfig.apiBase.Value
                env.Emit [DatasetsLoaded datasets]
                let! autoLoad = MeshData.fetchDefaultDataset ApiConfig.apiBase.Value
                if not (System.String.IsNullOrEmpty autoLoad) && datasets |> Array.contains autoLoad then
                    env.Emit [SetActiveDataset autoLoad]
                    loadDataset env autoLoad
            with _ -> ()
        } |> ignore

module Update =

    let mutable private hoverProbeCts : System.Threading.CancellationTokenSource =
        new System.Threading.CancellationTokenSource()

    let private invalidateProbes (model : Model) =
        { model with
            ScanPins = ScanPinModel.invalidateProbes model.ScanPins
            HoverProbe = None }

    let private updateCore (env : Env<Message>) (model : Model) (msg : Message) =
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
                SceneBounds      = Box3d.Invalid
                DatasetCentroids =
                    let perMesh = centroids |> Array.fold (fun m (n, c) -> Map.add n c m) model.DatasetCentroids
                    if dataset <> "" then Map.add dataset common perMesh else perMesh }
        | SetVisible(name, v) ->
            let activePickingLayer =
                if not v && model.ActivePickingLayer = Some name then None
                else model.ActivePickingLayer
            invalidateProbes
                { model with
                    MeshVisible = Map.add name v model.MeshVisible
                    ActivePickingLayer = activePickingLayer }
        | ToggleMenu ->
            let sp = model.ScanPins
            if ScanPinModel.isPlacing sp then model
            else { model with MenuOpen = not model.MenuOpen }
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
        | LogDebug s ->
            let log = model.DebugLog.InsertAt(0, s)
            let log = if log.Count > 20 then IndexList.take 20 log else log
            { model with DebugLog = log }
        | ToggleFullscreen ->
            { model with FullscreenOn = not model.FullscreenOn }
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
            invalidateProbes { model with Registration = { model.Registration with ReferenceMesh = mesh } }
        | ResetMeshTransforms ->
            invalidateProbes
                { model with
                    MeshTransforms = Map.empty
                    Registration = { model.Registration with Running = false } }
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
                    let scale = DatasetScale.active model.ActiveDataset model.DatasetScales
                    let cc = model.CommonCentroid
                    let anchors =
                        match reg.Mode with
                        | TraditionalIcp -> [||]
                        | RegionRestrictedIcp ->
                            model.ScanPins.Pins |> HashMap.toSeq
                            |> Seq.choose (fun (_, pin) ->
                                if pin.Phase = PinPhase.Committed then
                                    // pin.Centre and pin.FalloffRadius are
                                    // already world-space metres.
                                    let w =
                                        match pin.Payload with
                                        | Point pp -> pp.ReliabilityWeight
                                        | _ -> 1.0
                                    Some (pin.Centre, pin.FalloffRadius, w)
                                else None)
                            |> Array.ofSeq
                    let eps =
                        match reg.Mode with
                        | TraditionalIcp -> 0.0
                        | RegionRestrictedIcp -> 0.05
                    for mov in visibleMeshes do
                        let initial =
                            Map.tryFind mov model.MeshTransforms
                            |> Option.map (fun t -> (RigidTransform.renderToWorld scale cc t).Forward)
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
                    { model with Registration = { reg with Running = true } }
        | RegistrationComplete(mesh, trafo, _conv, resi) ->
            let meshScale = DatasetScale.forMesh model.DatasetScales mesh
            let renderTrafo = RigidTransform.worldToRender meshScale model.CommonCentroid trafo
            let mt = Map.add mesh renderTrafo model.MeshTransforms
            let meshRms =
                if resi.Length = 0 then 0.0
                else sqrt ((resi |> Array.sumBy (fun x -> x * x)) / float resi.Length)
            let algoMap = Map.add mesh meshRms model.MeshAlgorithmResidual
            invalidateProbes
                { model with
                    MeshTransforms = mt
                    MeshAlgorithmResidual = algoMap
                    Registration = { model.Registration with Running = false } }
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
                let perMesh = bboxes |> Array.fold (fun m (n, b) -> Map.add n b m) Map.empty
                // Synthetic panorama pose: no dataset ships real imagery.
                let panos =
                    if union.IsValid then
                        let name = model.ActiveDataset |> Option.defaultValue "scene"
                        [ { Name = name; EyeWorld = union.Center + V3d(0.0, 0.0, 2.0); Yaw = 0.0 } ]
                    else []
                { model with
                    SceneBounds = padded
                    MeshBounds = perMesh
                    Panoramas = panos
                    SelectedPanorama = 0 }
        | DatasetsLoaded datasets ->
            { model with Datasets = datasets |> Array.toList }
        | SetActiveDataset dataset ->
            if model.ActiveDataset = Some dataset then model
            else
                { model with
                    ActiveDataset = Some dataset
                    ScanPins = ScanPinModel.initial
                    MeshSolo = NoSolo
                    MeshBounds = Map.empty
                    Panoramas = []
                    SelectedPanorama = 0
                    ActivePickingLayer = None
                    LassoDrawing = None
                    LassoVolume = None
                    LassoEnabled = true
                    CardSystem = { model.CardSystem with Cards = model.CardSystem.Cards |> HashMap.map (fun _ c -> { c with Visible = false }) } }
        | SetDatasetScale(dataset, scale) ->
            { model with DatasetScales = Map.add dataset scale model.DatasetScales }
        | JumpToMesh meshName ->
            match Map.tryFind meshName model.DatasetCentroids with
            | Some centroid ->
                let renderPos = (centroid - model.CommonCentroid) * DatasetScale.forMesh model.DatasetScales meshName
                let radius =
                    if model.SceneBounds.IsInvalid then 50.0
                    else model.SceneBounds.Size.Length * 0.6
                env.Emit [CameraMessage (OrbitMessage.SetTargetCenter(true, AnimationKind.Tanh, renderPos))]
                env.Emit [CameraMessage (OrbitMessage.SetTargetRadius(true, radius))]
            | None -> ()
            model
        | SetRenderingMode m ->
            { model with RenderingMode = m }
        | ToggleMeshSolo name ->
            invalidateProbes (
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
                    { model with MeshVisible = vis; MeshSolo = Solo(name, restore) })
        | ShowAllMeshes ->
            let vis = model.MeshNames |> IndexList.toSeq |> Seq.map (fun n -> n, true) |> Map.ofSeq
            invalidateProbes { model with MeshVisible = vis; MeshSolo = NoSolo }
        | HideAllMeshes ->
            let vis = model.MeshNames |> IndexList.toSeq |> Seq.map (fun n -> n, false) |> Map.ofSeq
            invalidateProbes { model with MeshVisible = vis; MeshSolo = NoSolo }
        | ResetCamera ->
            let center, radius =
                if model.SceneBounds.IsInvalid then V3d.Zero, 50.0
                else V3d.Zero, max 1.0 (model.SceneBounds.Size.Length * 0.6)
            env.Emit [CameraMessage (OrbitMessage.SetTargetCenter(true, AnimationKind.Tanh, center))]
            env.Emit [CameraMessage (OrbitMessage.SetTargetRadius(true, radius))]
            model
        | SetLassoCardPos pos ->
            { model with LassoCardPos = Some pos }
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
            { model with ScanPins = scanPins; LassoDrawing = Some { Vertices = [||] }; LassoEnabled = true }
        | ToggleLassoEnabled ->
            { model with LassoEnabled = not model.LassoEnabled }
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
                { model with LassoDrawing = None; LassoVolume = Some volume; LassoEnabled = true }
            | _ ->
                { model with LassoDrawing = None }
        | LassoCancel ->
            { model with LassoDrawing = None }
        | LassoClear ->
            { model with LassoDrawing = None; LassoVolume = None; LassoEnabled = true }
        | CardMsg msg ->
            { model with CardSystem = CardUpdate.update msg model.CardSystem }
        | ScanPinMsg msg ->
            ScanPinUpdate.handleMsg env model msg
        | SaveWorkspace ->
            let json = Persistence.serialize model
            try
                JSRuntime.Instance.InvokeVoid("SuperWorkspaceSave", "workspace.json", json)
            with _ -> ()
            model
        | LoadWorkspaceJson json ->
            match Persistence.apply json model with
            | Result.Ok m -> m
            | Result.Error err ->
                { model with DebugLog = model.DebugLog.InsertAt(0, sprintf "workspace load failed: %s" err) }
        | StartRetarget target ->
            let pins =
                model.ScanPins.Pins
                |> HashMap.toSeq
                |> Seq.choose (fun (_, p) ->
                    if p.Phase = PinPhase.Committed then Some p else None)
                |> Array.ofSeq
            if pins.Length = 0 then
                { model with DebugLog = model.DebugLog.InsertAt(0, "retarget: no committed pins") }
            else
                task {
                    try
                        let! candidates =
                            pins
                            |> Array.map (fun p ->
                                async {
                                    let! res = Query.closestPoint ApiConfig.apiBase.Value target 0 p.Centre
                                    return
                                        match res with
                                        | Some r ->
                                            let dist = sqrt (float r.distanceSquared)
                                            {
                                                PinId = p.Id
                                                OriginalCentre = p.Centre
                                                OriginalHostMesh = p.HostMeshName
                                                FalloffRadius = p.FalloffRadius
                                                ProjectedCentre = r.point
                                                ProjectionDistance = dist
                                                TargetMesh = target
                                                Decision = RetargetUndecided
                                            }
                                        | None ->
                                            // No projection — flag with a sentinel large distance
                                            {
                                                PinId = p.Id
                                                OriginalCentre = p.Centre
                                                OriginalHostMesh = p.HostMeshName
                                                FalloffRadius = p.FalloffRadius
                                                ProjectedCentre = p.Centre
                                                ProjectionDistance = System.Double.PositiveInfinity
                                                TargetMesh = target
                                                Decision = RetargetReject
                                            }
                                })
                            |> Async.Parallel
                        env.Emit [RetargetCandidatesReady candidates]
                    with ex ->
                        env.Emit [LogDebug (sprintf "retarget projection failed: %s" ex.Message)]
                } |> ignore
                { model with Retarget = RetargetProjecting target }
        | RetargetCandidatesReady candidates ->
            { model with Retarget = RetargetReviewing candidates }
        | SetRetargetDecision(pinId, decision) ->
            match model.Retarget with
            | RetargetReviewing cs ->
                let updated =
                    cs |> Array.map (fun c ->
                        if c.PinId = pinId then { c with Decision = decision } else c)
                { model with Retarget = RetargetReviewing updated }
            | _ -> model
        | CommitRetarget ->
            match model.Retarget with
            | RetargetReviewing cs ->
                let mutable pins = model.ScanPins.Pins
                for c in cs do
                    if c.Decision = RetargetAccept then
                        match HashMap.tryFind c.PinId pins with
                        | Some p ->
                            let p' =
                                { p with
                                    Centre = c.ProjectedCentre
                                    HostMeshName = Some c.TargetMesh
                                    Probe = ProbeNone }
                            pins <- HashMap.add c.PinId p' pins
                        | None -> ()
                { model with
                    ScanPins = { model.ScanPins with Pins = pins }
                    Retarget = RetargetIdle }
            | _ -> model
        | CancelRetarget ->
            { model with Retarget = RetargetIdle }
        // Transient hover probe (spec §7.4): radius = 5% of the scene bbox
        // diagonal, auto length, declared reference mesh (or active picking
        // layer / first visible). Not cached; superseded by the next
        // Ctrl-click via the CancellationTokenSource.
        | HoverProbeAt(screenPx, world) ->
            let visible =
                model.MeshNames |> IndexList.toList
                |> List.filter (fun n -> Map.tryFind n model.MeshVisible |> Option.defaultValue true)
            match visible with
            | [] -> model
            | _ ->
                let refMesh =
                    model.Registration.ReferenceMesh |> Option.filter (fun r -> List.contains r visible)
                    |> Option.orElse (model.ActivePickingLayer |> Option.filter (fun l -> List.contains l visible))
                    |> Option.defaultValue (List.head visible)
                let radius =
                    if model.SceneBounds.IsInvalid then 5.0
                    else max 0.5 (model.SceneBounds.Size.Length * 0.05)
                let cc = model.CommonCentroid
                let meshes =
                    visible |> List.map (fun n ->
                        let t =
                            match Map.tryFind n model.MeshTransforms with
                            | Some rt -> (RigidTransform.renderToWorld (DatasetScale.forMesh model.DatasetScales n) cc rt).Forward
                            | None -> M44d.Identity
                        n, t)
                hoverProbeCts.Cancel()
                hoverProbeCts <- new System.Threading.CancellationTokenSource()
                let token = hoverProbeCts.Token
                task {
                    try
                        let! res =
                            Query.probe ApiConfig.apiBase.Value meshes refMesh world radius 0.0 4096
                            |> Async.StartAsTask
                        if not token.IsCancellationRequested then
                            match res with
                            | Result.Ok r -> env.Emit [HoverProbeResult (ProbeReady r)]
                            | Result.Error e -> env.Emit [HoverProbeResult (ProbeError e)]
                        do! System.Threading.Tasks.Task.Delay(8000, token)
                        if not token.IsCancellationRequested then
                            env.Emit [ClearHoverProbe]
                    with
                    | :? System.OperationCanceledException -> ()
                    | ex ->
                        if not token.IsCancellationRequested then
                            env.Emit [HoverProbeResult (ProbeError ex.Message)]
                } |> ignore
                { model with HoverProbe = Some { ScreenPos = screenPx; Anchor = world; Probe = ProbeRunning } }
        | HoverProbeResult st ->
            match model.HoverProbe with
            | Some h when h.Probe = ProbeRunning -> { model with HoverProbe = Some { h with Probe = st } }
            | _ -> model
        | ClearHoverProbe ->
            hoverProbeCts.Cancel()
            if model.HoverProbe.IsNone then model else { model with HoverProbe = None }
        | TogglePanorama ->
            { model with PanoramaOpen = not model.PanoramaOpen }
        | PanoramasGenerated ps ->
            { model with Panoramas = ps; SelectedPanorama = 0 }
        | SelectPanorama i ->
            { model with SelectedPanorama = i }
        | SetPanoramaMode m ->
            { model with PanoramaMode = m }
        | SetPanoramaBlend b ->
            { model with PanoramaBlend = clamp 0.0 1.0 b }
        | FlyToPanorama i ->
            match List.tryItem i model.Panoramas with
            | Some p ->
                let scale = DatasetScale.active model.ActiveDataset model.DatasetScales
                let eyeR = (p.EyeWorld - model.CommonCentroid) * scale
                let r =
                    if model.SceneBounds.IsInvalid then 5.0
                    else max 1.0 (model.SceneBounds.Size.Length * scale * 0.12)
                let fwd = V3d(cos p.Yaw, sin p.Yaw, 0.0)
                let center = eyeR + fwd * r
                env.Emit [CameraMessage (OrbitMessage.SetTarget(true, center, r, p.Yaw + Constant.Pi, 0.05))]
                model
            | None -> model

    let update (env : Env<Message>) (model : Model) (msg : Message) =
        updateCore env model msg |> ScanPinUpdate.ensureProbe env
