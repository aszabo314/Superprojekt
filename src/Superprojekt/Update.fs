namespace Superprojekt

open Aardvark.Base
open Aardworx.WebAssembly
open Microsoft.JSInterop
open FSharp.Data.Adaptive
open Aardvark.Dom
open Superprojekt
open UpdateHelpers

module Update =

    // The ONE focus-jump path: transient lenses (Setup isolate, matrix hover),
    // the spring-loaded peeks and the armed probe are level-scoped and die on
    // any jump; leaving Pin mid-placement (Esc or a rail jump) aborts the
    // transaction — full rollback, its picking panes are gone.
    let private jumpFocus (f : FocusLevel) (model : Model) =
        let sp =
            if model.Focus = FocusPin && f <> FocusPin
            then { model.ScanPins with Placement = PlacementIdle }
            else model.ScanPins
        { model with
            Focus = f; ScanPins = sp
            SetupIsolate = None; SetupIsolateHover = None; MatrixHoverPair = None
            PeekVis = false; PeekPose = false; ProbeArmed = false; ProbeReadout = None }

    // A step may retract what the current focus level depends on (pin deleted,
    // placement aborted, selection cleared) — demote to the nearest enabled
    // ancestor. Runs after every reducer step.
    let private normalizeFocus (model : Model) =
        let placing = match model.ScanPins.Placement with PlacementActive _ -> true | PlacementIdle -> false
        let rec fix f = if FocusLevel.enabled model.Sel placing f then f else fix (FocusLevel.parent f)
        let f = fix model.Focus
        if f = model.Focus then model else { model with Focus = f }

    let private updateCore (env : Env<Message>) (model : Model) (msg : Message) =
        match msg with
        | CameraMessage msg ->
            { model with Camera = OrbitController.update (Env.map CameraMessage env) model.Camera msg }
        | SetTileCam(mesh, cam) ->
            let cam = { cam with Radius = clamp 0.05 100000.0 cam.Radius }
            { model with TileCams = Map.add mesh cam model.TileCams }
        | CentroidsLoaded centroids ->
            let common  = if centroids.Length > 0 then centroids |> Array.averageBy snd else V3d.Zero
            let names   = centroids |> Array.map fst |> IndexList.ofArray
            let indices = centroids |> Array.mapi (fun i (n,_) -> n,i) |> HashMap.ofArray
            // Identity: meshes load unregistered.
            let loadTransforms = centroids |> Array.fold (fun m (n, _) -> Map.add n Trafo3d.Identity m) Map.empty
            let dataset =
                if centroids.Length > 0 then
                    let n = fst centroids.[0] in let s = n.IndexOf('/') in if s >= 0 then n.[..s-1] else ""
                else ""
            { model with
                MeshNames        = names
                CommonCentroid   = common
                MeshOrder        = indices
                MeshesLoaded     = HashSet.empty
                SceneBounds      = Box3d.Invalid
                PanoCenters      = Map.empty
                LoadTransforms   = loadTransforms
                // Default root = first mesh so the registration UI works out of the box.
                RegGraph         = { Root = (if centroids.Length > 0 then Some (fst centroids.[0]) else None)
                                     Edges = Map.empty }
                ComposedPoses    = Map.empty
                PairOverlaps     = Map.empty
                DatasetCentroids =
                    // Fresh map — entries never accumulate across dataset switches.
                    let perMesh = centroids |> Array.fold (fun m (n, c) -> Map.add n c m) Map.empty
                    if dataset <> "" then Map.add dataset common perMesh else perMesh }
        | PanoCentersLoaded pcs ->
            { model with PanoCenters = Map.ofArray pcs }
        | LoadFinished name ->
            // Cached-mesh revisits re-emit completions — only a FIRST landing
            // may append the loading-done marker (no duplicate divs).
            let wasNew = not (HashSet.contains name model.MeshesLoaded)
            let model = { model with MeshesLoaded = HashSet.add name model.MeshesLoaded }

            let missing = HashSet.difference (HashSet.ofSeq model.MeshNames) model.MeshesLoaded
            if wasNew && missing.Count = 0 then
                let d = Window.Document.CreateElement("div")
                d.Id <- "loading-done"
                d.Style.Visibility <- "hidden"
                d.Style.Position <- "fixed"
                d.Style.PointerEvents <- "none"
                Window.Document.Body.AppendChild(d) |> ignore
            model
        | ToggleGhostSilhouette ->
            { model with GhostSilhouette = not model.GhostSilhouette }
        | SetGhostOpacity v ->
            { model with GhostOpacity = clamp 0.0 1.0 v }
        | SetShadingStrength v ->
            { model with ShadingStrength = clamp 0.0 1.0 v }
        | SetSlopeThresholdDeg v ->
            { model with SlopeThresholdDeg = clamp 1.0 89.0 v }
        | SetOutlineThreshold v ->
            // Floor = the Rgba8 G-buffer quantization step (~0.004); below it the
            // staircase risers of a smooth slope read as false outline bands.
            { model with OutlineThreshold = clamp 0.0001 0.01 v }
        | SetOutlineWidth v ->
            { model with OutlineWidthPx = clamp 1.0 8.0 v }
        | SetIsolineBands v ->
            { model with IsolineBands = max 1.0 v }
        | SetIsolineOpacity v ->
            { model with IsolineOpacity = clamp 0.0 1.0 v }
        | ToggleAnchorGhostMode ->
            { model with AnchorGhostMode = not model.AnchorGhostMode }
        | SetQuickPinRadius v ->
            { model with QuickPinRadius = max 0.01 v }
        | SetFlagScale v ->
            { model with FlagScale = clamp 0.1 10.0 v }
        | SetRegRoot mesh when model.RegGraph.Root = Some mesh ->
            model    // idempotent: re-designating the same root must not touch the graph
        | SetRegRoot mesh ->
            let g = model.RegGraph
            // A root change clears the whole per-level selection — every
            // descendant level loses its subject (and any in-flight pin work).
            let model =
                { model with
                    Sel = FocusSelection.empty
                    ScanPins = { model.ScanPins with Placement = PlacementIdle } }
            let recomposeWith (g' : RegGraph) (m : Model) =
                invalidateCellError (invalidateRings (ModelTransforms.recomposePoses { m with RegGraph = g' }))
            let model =
                if RegGraph.hasEdges g && RegGraph.inTree g mesh then
                    // Re-root in place: the registration survives — the path
                    // edges reverse (the REF/MOV flip) and every composed pose
                    // recomposes relative to the new root.
                    showToast env "Re-rooted — registration kept, poses recomposed"
                        (recomposeWith (RegGraph.reroot mesh g) model)
                elif RegGraph.hasEdges g then
                    // The registered tree cannot hang off a mesh outside it.
                    showToast env "Registration cleared — the new root was outside the registered tree"
                        (recomposeWith { Root = Some mesh; Edges = Map.empty } model)
                else
                    recomposeWith { Root = Some mesh; Edges = Map.empty } model
            model
        | SetSetupIsolateHover h ->
            if model.SetupIsolateHover = h then model else { model with SetupIsolateHover = h }
        | SetMatrixHoverPair hp ->
            if model.MatrixHoverPair = hp then model else { model with MatrixHoverPair = hp }
        | ToggleSetupIsolate mesh ->
            { model with SetupIsolate = if model.SetupIsolate = Some mesh then None else Some mesh }
        | SetFocus f ->
            let placing = match model.ScanPins.Placement with PlacementActive _ -> true | PlacementIdle -> false
            if f = model.Focus || not (FocusLevel.enabled model.Sel placing f) then model
            else jumpFocus f model
        | FocusAscend ->
            if model.Focus = FocusSetup then model
            else jumpFocus (FocusLevel.parent model.Focus) model
        | SelectPair(a, b) ->
            let key = PairCell.key a b
            if model.Sel.Pair = Some key then
                // The remembered pair: the selection (incl. its pin memory)
                // stands — re-entering restores the last workspace state.
                jumpFocus FocusPair model
            else
                // A NEW pair cascade-clears its descendants and every in-cell
                // cache; a placement bound to the old pair rolls back. The Pin
                // panes keep their meshes' shared 2D tile cameras.
                invalidateCellError
                    { jumpFocus FocusPair model with
                        Sel = { Pair = Some key; Pin = None; Point = None }
                        ScanPins = { model.ScanPins with Placement = PlacementIdle } }
        | SelectPin id ->
            let valid =
                match HashMap.tryFind id model.ScanPins.Pins with
                | Some p -> model.Sel.Pair = Some p.Pair
                | None -> false
            if not valid || model.Sel.Pin = Some id then model
            else { model with Sel = { model.Sel with Pin = Some id; Point = None } }

        | ShowToast s ->
            showToast env s model
        | ClearToast ->
            if model.Toast.IsNone then model else { model with Toast = None }

        | SetMeshHeatmap(mesh, m) ->
            // Store HeatOff as removal so the map stays sparse (default lookup = off).
            let mh = if m = HeatOff then Map.remove mesh model.MeshHeatmap else Map.add mesh m model.MeshHeatmap
            { model with MeshHeatmap = mh }
        | SetShapeThreshold v ->
            { model with ShapeThreshold = clamp 0.0 1.0 v }
        | CellErrorComputed(gen, after, before) ->
            if gen <> cellErrorGen then model
            else { model with CellError = Some after; CellErrorBefore = before }
        | CellDistComputed(gen, dist) ->
            if gen <> cellErrorGen then model
            else { model with CellDist = Some dist }
        | SetBrushedSamples ids ->
            // Cap the brushed set so a wide brush can't flood the 3D marker node.
            let st = ids |> List.truncate 200 |> Set.ofList
            if model.BrushedSamples = st then model
            else { model with BrushedSamples = st; HoverSample = None; HoverReadout = None }
        | SetHoverSample gid ->
            if model.HoverSample = gid then model
            else { model with HoverSample = gid; HoverReadout = None }
        | HoverReadoutComputed(gen, gid, v) ->
            if gen <> cellErrorGen || model.HoverSample <> Some gid then model
            else { model with HoverReadout = Some (gid, v) }
        | ToggleProbeArmed ->
            // Disarm wipes the readout — the probe persists nothing.
            if model.ProbeArmed then { model with ProbeArmed = false; ProbeReadout = None }
            else { model with ProbeArmed = true; ProbeReadout = None }
        | ProbeReadoutComputed(gen, w, v) ->
            if gen <> cellErrorGen || not model.ProbeArmed then model
            else { model with ProbeReadout = Some (w, v) }
        | ToggleCellMap ->
            { model with CellMapOn = not model.CellMapOn }
        | SetPeekVis held ->
            // Pair scope only + both pair meshes GPU-resident — otherwise the
            // press does NOT peek (an unloaded state would blink a blank).
            // Releases always land.
            let ok =
                model.LoopPending.IsNone &&
                (match model.Focus, model.Sel.Pair with
                 | FocusPair, Some (a, b) ->
                    HashSet.contains a model.MeshesLoaded && HashSet.contains b model.MeshesLoaded
                 | _ -> false)
            if model.PeekVis = held || (held && not ok) then model
            else { model with PeekVis = held }
        | SetPeekPose held ->
            let ok =
                model.LoopPending.IsNone &&
                (match model.Focus, model.Sel.Pair with
                 | FocusPair, Some (a, b) ->
                    HashSet.contains a model.MeshesLoaded && HashSet.contains b model.MeshesLoaded
                 | _ -> false)
            if model.PeekPose = held || (held && not ok) then model
            else { model with PeekPose = held }
        | SelectLoopEdge sel ->
            (match model.LoopPending with
             | Some lp -> { model with LoopPending = Some { lp with Selected = sel } }
             | None -> model)
        | ConfirmLoopResolution ->
            (match model.LoopPending with
             | None -> model
             | Some lp ->
                bumpPairSolve ()
                let m = { model with LoopPending = None }
                match lp.Selected with
                | None ->
                    // Removing the NEW edge = the prior tree stands untouched.
                    showToast env "Loop resolved — the new edge was discarded" m
                | Some rc ->
                    let g2 = RegGraph.resolveLoop lp.Mov lp.Ref lp.Transform lp.Quality rc model.RegGraph
                    showToast env "Loop resolved — the tree re-hung through the new edge"
                        (invalidateCellError
                            (invalidateRings (ModelTransforms.recomposePoses { m with RegGraph = g2 }))))
        | CancelLoopResolution ->
            (match model.LoopPending with
             | None -> model
             | Some _ ->
                showToast env "Redundant edge discarded — the prior tree stands"
                    { model with LoopPending = None })
        | PairOverlapComputed(gen, results) ->
            if gen <> pairOverlapGen then model
            else
                let po =
                    (model.PairOverlaps, results) ||> Array.fold (fun po (a, b, ok) ->
                        Map.add (PairCell.key a b) ok po)
                { model with PairOverlaps = po }
        | SceneBoundsLoaded bboxes ->
            if bboxes.Length = 0 then model
            else
                let union =
                    bboxes |> Array.fold (fun (acc : Box3d) (_, b) -> acc.ExtendedBy b) Box3d.Invalid
                let padded = Box3d(union.Min - V3d.III, union.Max + V3d.III)
                let perMesh = bboxes |> Array.fold (fun m (n, b) -> Map.add n b m) Map.empty
                let m =
                    { model with
                        SceneBounds = padded
                        MeshBounds = perMesh }
                // Rest the camera on the default reference mesh (last load step, so
                // PanoCenters/centroids are in): its panorama centre, framed to its own
                // bounds rather than the whole scene. One-shot per dataset load.
                let center, radius =
                    match m.RegGraph.Root |> Option.bind (fun r -> Map.tryFind r perMesh |> Option.map (fun b -> r, b)) with
                    | Some (r, b) ->
                        let scale = DatasetScale.forMesh m.DatasetScales r
                        ModelTransforms.panoCenterRender m r, max 1.0 (b.Size.Length * scale * 0.6)
                    | None ->
                        ModelTransforms.firstPanoCenterRender m, max 1.0 (padded.Size.Length * 0.6)
                env.Emit [CameraMessage (OrbitMessage.SetTargetCenter(AnimationKind.Tanh, center))
                          CameraMessage (OrbitMessage.SetTargetRadius(radius))]
                m
        | DatasetsLoaded datasets ->
            { model with Datasets = datasets |> Array.toList }
        | SetActiveDataset dataset ->
            if model.ActiveDataset = Some dataset then model
            else
                // Everything keyed by the old dataset's meshes/pins must go, and the
                // scalar-map generations bump so old-dataset fetches land dead.
                bumpPairOverlap ()
                { model with
                    ActiveDataset = Some dataset
                    ScanPins = ScanPinModel.initial
                    MeshBounds = Map.empty
                    LoadTransforms = Map.empty
                    MeshHeatmap = Map.empty
                    SetupIsolate = None
                    SetupIsolateHover = None
                    MatrixHoverPair = None
                    RegGraph = RegGraph.empty
                    ComposedPoses = Map.empty
                    PairOverlaps = Map.empty
                    Focus = FocusSetup
                    Sel = FocusSelection.empty
                    TileCams = Map.empty
                    CellError = None
                    CellErrorBefore = None
                    CellDist = None
                    BrushedSamples = Set.empty
                    HoverSample = None
                    HoverReadout = None
                    ProbeArmed = false
                    ProbeReadout = None
                    PeekVis = false
                    PeekPose = false
                    LoopPending = None
                    Toast = None }
        | SetRenderingMode m ->
            { model with RenderingMode = m }
        | SetMatrixOrder o ->
            if model.MatrixOrder = o then model else { model with MatrixOrder = o }
        | ToggleGearPopover ->
            { model with GearPopoverOpen = not model.GearPopoverOpen }
        | ScanPinMsg msg ->
            let m = ScanPinUpdate.handleMsg env model msg
            // New pin → enter Pin in placement: the two panes are the only
            // picking surface, so arming the transaction moves focus there.
            let m =
                match msg with
                | BeginPinTransaction _ -> jumpFocus FocusPin m
                | _ -> m
            // Selection maintenance: a commit selects the newborn pin (it IS
            // the chosen pin from birth); deleting the selected pin clears it.
            let m =
                match msg with
                | CommitPin ->
                    let born =
                        m.ScanPins.Pins |> HashMap.toSeq
                        |> Seq.tryPick (fun (id, _) ->
                            if HashMap.containsKey id model.ScanPins.Pins then None else Some id)
                    (match born with
                     | Some id -> { m with Sel = { m.Sel with Pin = Some id; Point = None } }
                     | None -> m)
                | DeletePin id when model.Sel.Pin = Some id ->
                    { m with Sel = { m.Sel with Pin = None; Point = None } }
                | _ -> m
            // Pin set / geometry changes re-scope the in-cell inspection.
            let m =
                match msg with
                | CommitPin | SetInnerRadius _ | EditPointAt _ | DeletePin _ -> invalidateCellError m
                | _ -> m
            // ANY committed-pin edit invalidates its pair's solve: the pair's
            // edge (and every edge hanging beneath it — the subtree would
            // strand) drops and the poses recompose. The pair is read from the
            // PRE-edit model so a delete still resolves it.
            let editedPair =
                match msg with
                | SetInnerRadius(id, _) | EditPointAt(id, _, _) | DeletePin id ->
                    HashMap.tryFind id model.ScanPins.Pins |> Option.map (fun p -> p.Pair)
                | _ -> None
            match editedPair with
            | Some (a, b) ->
                match RegGraph.pairEdge a b m.RegGraph with
                | Some e ->
                    bumpPairSolve ()
                    showToast env "Pair unregistered — a pin changed"
                        (invalidateCellError
                            (invalidateRings
                                (ModelTransforms.recomposePoses
                                    { m with RegGraph = RegGraph.removeEdgeCascading e.Child m.RegGraph })))
                | None -> m
            | None -> m
        | SolvePair(a, b) ->
            let key = PairCell.key a b
            let pins =
                model.ScanPins.Pins |> HashMap.toList |> List.map snd
                |> List.filter (fun p -> p.Pair = key)
            if List.length pins < 3 then
                showToast env "Need ≥3 pins on this pair to solve" model
            else
                let g = model.RegGraph
                // Orientation: a re-solve keeps the existing edge's REF/MOV;
                // a fresh edge points the un-treed mesh (MOV) at the treed one.
                let orient =
                    match RegGraph.pairEdge a b g with
                    | Some e -> Choice1Of2 (e.Child, e.Parent)
                    | None ->
                        match RegGraph.inTree g a, RegGraph.inTree g b with
                        | true, false -> Choice1Of2 (b, a)
                        | false, true -> Choice1Of2 (a, b)
                        | true, true ->
                            // Redundant pair: allowed — the landing closes a
                            // TRANSIENT loop for the resolution modal. REF/MOV
                            // from the tree (nearer root = REF).
                            let r, m = MatrixNav.pairRefMov g a b
                            Choice1Of2 (m, r)
                        | false, false -> Choice2Of2 "neither mesh reaches the root yet — register a root-connected pair first"
                match orient with
                | Choice2Of2 why -> showToast env (sprintf "Cannot solve — %s" why) model
                | Choice1Of2 (child, parent) ->
                    bumpPairSolve ()
                    let gen = pairSolveGen
                    // Pairs at the AS-LOADED baselines: the edge transform maps
                    // child-baseline points onto parent-baseline points, pose-
                    // independent — ancestor registration composes on top (P1).
                    let toBase mesh (local : V3d) =
                        (ModelTransforms.loadWorld model mesh).Forward.TransformPos local
                    let pairsArr =
                        pins
                        |> List.map (fun p ->
                            let ptOf mesh = if mesh = fst key then p.PointA else p.PointB
                            toBase parent (ptOf parent), toBase child (ptOf child), 1.0)
                        |> Array.ofList
                    task {
                        try
                            let! (world, residuals) =
                                Query.lsqPairs ApiConfig.apiBase.Value child pairsArr |> Async.StartAsTask
                            env.Emit [PairSolved(gen, child, parent, world, residuals)]
                        with ex ->
                            env.Emit [ShowToast (sprintf "Solve failed: %s" ex.Message)]
                    } |> ignore
                    showToast env "Solving pair…" model
        | PairSolved(gen, child, parent, world, residuals) ->
            if gen <> pairSolveGen then model    // an edit/abort invalidated this solve mid-flight
            else
                let t = Trafo3d(world, world.Inverse)
                let q = RegGraph.solveQuality residuals
                let g = model.RegGraph
                let commit g2 =
                    showToast env (sprintf "Pair registered — quality %.2f" q)
                        (invalidateCellError
                            (invalidateRings (ModelTransforms.recomposePoses { model with RegGraph = g2 })))
                // A re-solve of THIS pair's edge replaces it in place; child
                // keying an edge to a DIFFERENT parent is the redundant case.
                let existingSamePair =
                    match Map.tryFind child g.Edges with
                    | Some e -> e.Parent = parent
                    | None -> false
                if existingSamePair then commit (RegGraph.withEdge child t q g)
                else
                    match RegGraph.tryAddEdge child parent t q g with
                    | EdgeAdded g2 -> commit g2
                    | EdgeRejected why -> showToast env (sprintf "Solve not applied — %s" why) model
                    | EdgeClosesLoop (cycle, residual) ->
                        // Transient: the committed graph stays the prior tree —
                        // the forced-resolution modal owns the next step. The
                        // displacement is read at the MOV mesh's centroid (the
                        // practical "how far do the paths disagree at the data").
                        let probePt =
                            Map.tryFind child model.DatasetCentroids |> Option.defaultValue V3d.Zero
                        let weakest =
                            (Some (cycle |> List.minBy (fun e -> e.Quality)), q)
                            |> fun (minTree, qNew) ->
                                match minTree with
                                | Some e when e.Quality <= qNew -> Some e.Child
                                | _ -> None    // the new edge itself is the weakest
                        { model with
                            LoopPending = Some {
                                Mov = child; Ref = parent
                                Transform = t; Quality = q
                                CycleEdges = cycle
                                ResidualRotDeg = RegGraph.residualRotationDeg residual
                                ResidualTransM = RegGraph.residualAt residual probePt
                                Selected = weakest } }
        | SetNearCut v ->
            { model with NearCutFrac = clamp 0.0 1.25 v }
        // Fly the main 3D to a metric-world point, keeping orientation.
        | FlyToPoint(world, radius) ->
            let scale = DatasetScale.active model.ActiveDataset model.DatasetScales
            let centreR = ScanPin.renderCentre model.CommonCentroid scale world
            env.Emit [CameraMessage (OrbitMessage.SetTargetCenter(AnimationKind.Tanh, centreR))
                      CameraMessage (OrbitMessage.SetTargetRadius(max 0.2 (radius * scale)))]
            model
        | ZoomToPin id ->
            (match HashMap.tryFind id model.ScanPins.Pins with
             | Some p ->
                let centre = ScanPin.centreWorldWith (ModelTransforms.displayedWorld model p.AnchorMesh) p
                env.Emit [FlyToPoint(centre, max 0.5 (p.InnerRadius * 4.0))]
             | None -> ())
            model
        | FlyToSensor mesh ->
            // The dataset-load framing, per mesh: the sensor (panorama centre,
            // mesh-origin fallback), framed to the mesh's own bounds.
            let center = ModelTransforms.panoCenterRender model mesh
            let radius =
                match Map.tryFind mesh model.MeshBounds with
                | Some b when not b.IsInvalid ->
                    max 1.0 (b.Size.Length * DatasetScale.forMesh model.DatasetScales mesh * 0.6)
                | _ -> model.Camera.radius
            env.Emit [CameraMessage (OrbitMessage.SetTargetCenter(AnimationKind.Tanh, center))
                      CameraMessage (OrbitMessage.SetTargetRadius radius)]
            model

    // Lazy pairwise-overlap sweep: every unordered mesh pair missing from the
    // cache gets one baseline-pose sufficiency query; results land as ONE batch.
    // Single-flight per generation — a dataset switch bumps it, so the sweep of
    // a previous dataset can never land.
    let private ensurePairOverlaps (env : Env<Message>) (model : Model) : Model =
        let names = model.MeshNames |> IndexList.toList
        let missing =
            [ for i in 0 .. names.Length - 2 do
                for j in i + 1 .. names.Length - 1 do
                    let k = PairCell.key names.[i] names.[j]
                    if not (Map.containsKey k model.PairOverlaps) then yield k ]
        if List.isEmpty missing || pairOverlapReqGen = pairOverlapGen then model
        else
            pairOverlapReqGen <- pairOverlapGen
            let gen = pairOverlapGen
            let jobs =
                missing |> List.map (fun (a, b) ->
                    let tA = (ModelTransforms.loadWorld model a).Forward
                    let tB = (ModelTransforms.loadWorld model b).Forward
                    async {
                        try
                            let! ok = Query.pairOverlap ApiConfig.apiBase.Value a tA b tB
                            return Some (a, b, ok)
                        with _ -> return None
                    })
            task {
                try
                    let! results = jobs |> Async.Parallel |> Async.StartAsTask
                    let landed = results |> Array.choose id
                    if landed.Length > 0 then env.Emit [PairOverlapComputed(gen, landed)]
                with _ -> ()
            } |> ignore
            model

    // In-cell pairwise error: one batch for the cell's pins at the CURRENT
    // poses (+ the same pins at the pair edge's BEFORE poses when registered —
    // the diagram's diff outline). Samples land MOV-relative-to-REF. Lazy,
    // single-flight, gen-guarded.
    let private ensureCellError (env : Env<Message>) (model : Model) : Model =
        match model.Focus, model.Sel.Pair with
        | (FocusSetup | FocusMatrix), _ | _, None -> model
        | (FocusPair | FocusPin), Some (a, b) ->
            let key = PairCell.key a b
            let pins =
                model.ScanPins.Pins |> HashMap.toList |> List.map snd
                |> List.filter (fun p -> p.Pair = key)
                |> List.sortBy (fun p -> p.CreatedAt, p.ShortName)
            if List.isEmpty pins || model.CellError.IsSome || cellErrorReqGen = cellErrorGen then model
            else
                cellErrorReqGen <- cellErrorGen
                let gen = cellErrorGen
                let ka, kb = key
                let _refMesh, movMesh = MatrixNav.pairRefMov model.RegGraph ka kb
                // The measure is meshB-relative-to-meshA; flip when MOV is meshA.
                let flip = movMesh = ka
                let orient (r : Query.PairPinError) =
                    if flip then { r with Median = -r.Median; Samples = r.Samples |> Array.map (~-) } else r
                let ids = pins |> List.map (fun p -> p.Id) |> Array.ofList
                let roisAt (world : string -> Trafo3d) =
                    pins |> List.map (fun p ->
                        let (ScanPinId.ScanPinId g) = p.Id
                        g.ToString "N", (world p.AnchorMesh).Forward.TransformPos p.CentreLocal, p.InnerRadius)
                let edge = RegGraph.pairEdge ka kb model.RegGraph
                let tNow (m : string) = (ModelTransforms.displayedWorld model m).Forward
                let roisNow = roisAt (fun m -> ModelTransforms.displayedWorld model m)
                let beforeReq =
                    edge |> Option.map (fun e ->
                        let w (m : string) = ModelTransforms.edgeWorld e.Child EdgeBefore model m
                        (w ka).Forward, (w kb).Forward, roisAt w)
                task {
                    try
                        let! after = Query.pairError ApiConfig.apiBase.Value ka (tNow ka) kb (tNow kb) roisNow |> Async.StartAsTask
                        let after = Array.map orient after |> Array.zip ids
                        let! before =
                            match beforeReq with
                            | Some (ta, tb, rois) ->
                                task {
                                    let! r = Query.pairError ApiConfig.apiBase.Value ka ta kb tb rois |> Async.StartAsTask
                                    return Some (Array.map orient r |> Array.zip ids)
                                }
                            | None -> task { return None }
                        env.Emit [CellErrorComputed(gen, after, before)]
                    with _ -> ()
                } |> ignore
                model

    // The in-cell false-colour buffer: MOV's per-vertex signed distance vs REF
    // at the displayed poses — never the reference against itself.
    let private ensureCellDist (env : Env<Message>) (model : Model) : Model =
        match model.Focus, model.Sel.Pair with
        | (FocusSetup | FocusMatrix), _ | _, None -> model
        | (FocusPair | FocusPin), Some (a, b) ->
            if not model.CellMapOn || model.CellDist.IsSome || cellDistReqGen = cellErrorGen then model
            else
                cellDistReqGen <- cellErrorGen
                let gen = cellErrorGen
                let refMesh, movMesh = MatrixNav.pairRefMov model.RegGraph a b
                let tOf (m : string) = (ModelTransforms.displayedWorld model m).Forward
                task {
                    try
                        let! d = Query.regionDistance ApiConfig.apiBase.Value movMesh refMesh (tOf movMesh) (tOf refMesh) |> Async.StartAsTask
                        env.Emit [CellDistComputed(gen, d)]
                    with _ -> ()
                } |> ignore
                model

    let update (env : Env<Message>) (model : Model) (msg : Message) =
        updateCore env model msg
        |> normalizeFocus
        |> ScanPinUpdate.ensureRings env
        |> ensureCellError env
        |> ensureCellDist env
        |> ensurePairOverlaps env

