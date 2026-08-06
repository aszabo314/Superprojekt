namespace Superprojekt

open Aardvark.Base
open Aardworx.WebAssembly
open Microsoft.JSInterop
open FSharp.Data.Adaptive
open Aardvark.Dom
open Superprojekt
open UpdateHelpers

module Update =

    // The ONE focus-jump path: transient lenses (tile isolate, matrix hover),
    // the spring-loaded peeks and the armed pick are level-scoped and die on
    // any jump; leaving Pin mid-placement (Esc or a rail jump) aborts the
    // transaction — full rollback.
    // The brush addresses ONE scope's gid stream — the graph's at Matrix, the
    // selected pair's inside the workspace — so crossing between them would
    // reinterpret the same gids against a different stream. Pair⇄Pin keeps the
    // brush (one stream, the Pin level only narrows the diagram).
    let private clearBrushAcross (from_ : FocusLevel) (f : FocusLevel) (model : Model) =
        if (from_ = FocusMatrix) = (f = FocusMatrix) then model
        else { model with BrushedSamples = Set.empty; HoverSample = None; HoverReadout = None }

    let private jumpFocus (f : FocusLevel) (model : Model) =
        let sp =
            if model.Focus = FocusPin && f <> FocusPin
            then { model.ScanPins with Placement = PlacementIdle }
            else model.ScanPins
        { clearBrushAcross model.Focus f model with
            Focus = f; ScanPins = sp
            // Point focus rides the tile isolate (ONE state) — both reset on a
            // jump; the pair/pin memory itself survives.
            Sel = { model.Sel with Point = None }
            TileIsolate = None; TileIsolateHover = None; MatrixHoverPair = None
            PeekVis = false; PeekPose = false
            ArmedPick = None; ArmPreview = None; PinFocusHover = None
            TilePinHover = None; NewPinHover = false
            PinRadiusEditOpen = false; PinExitPending = None }

    // The exit-guard's threshold: a draft only deserves a confirm-delete once
    // its centre is placed — a centreless draft exits silently.
    let private placingWithCentre (model : Model) =
        match model.ScanPins.Placement with
        | PlacementActive d -> d.Area.IsSome
        | PlacementIdle -> false

    // The VIS peek's scope: the pair workspace (Pair AND Pin) with both pair
    // meshes GPU-resident and no blocking modal. It has no meaning at Matrix —
    // there is no REF/MOV pair to flip between at graph scope.
    let private peekPairLoaded (model : Model) =
        model.LoopPending.IsNone &&
        (match model.Focus, model.Sel.Pair with
         | (FocusPair | FocusPin), Some (a, b) ->
            HashSet.contains a model.MeshesLoaded && HashSet.contains b model.MeshesLoaded
         | _ -> false)

    // The POSE peek's scope: the pair workspace's registered pair, or — at
    // Matrix — the WHOLE graph at once (≥1 edge; as-loaded vs as-loaded blinks
    // nothing). Every blinking mesh must be GPU-resident: the swap is a trafo
    // uniform, so both states are resident by construction, but a mesh still
    // loading would pop in mid-blink.
    let private peekPoseOk (model : Model) =
        model.LoopPending.IsNone &&
        (match model.Focus with
         | FocusMatrix ->
            RegGraph.hasEdges model.RegGraph &&
            model.RegGraph.Edges |> Map.forall (fun child e ->
                HashSet.contains child model.MeshesLoaded && HashSet.contains e.Parent model.MeshesLoaded)
         | FocusPair | FocusPin ->
            peekPairLoaded model &&
            (match model.Sel.Pair with
             | Some (a, b) -> (RegGraph.pairEdge a b model.RegGraph).IsSome
             | None -> false))

    // The (child, parent) pairs of every edge STRICTLY BELOW `top` in the
    // tree, parent-first — the pin-edit cascade drops them alongside top's
    // own edge, and the re-solve queue wants them in launchable order.
    let private subtreeDependents (g : RegGraph) (top : string) : (string * string) list =
        let rec collect queue acc =
            match queue with
            | [] -> List.rev acc
            | m :: rest ->
                let kids =
                    g.Edges |> Map.toList
                    |> List.choose (fun (c, e) -> if e.Parent = m then Some c else None)
                collect (rest @ kids) ((kids |> List.map (fun c -> c, m) |> List.rev) @ acc)
        collect [top] []

    // Drain the re-solve cascade: launch the FIRST pending dependent that can
    // still solve (its parent back in the tree, the child free, ≥3 pins) —
    // that solve's own commit re-enters here for the rest. An entry that can
    // no longer solve is flagged with a toast and skipped, never silent.
    let rec private continuePendingResolves (env : Env<Message>) (model : Model) : Model =
        match model.PendingResolves with
        | [] -> model
        | (child, parent) :: rest ->
            let model = { model with PendingResolves = rest }
            let key = PairCell.key child parent
            let pinCount =
                model.ScanPins.Pins |> HashMap.toList
                |> List.filter (fun (_, p) -> p.Pair = key) |> List.length
            if RegGraph.pairEdge child parent model.RegGraph |> Option.isSome then
                continuePendingResolves env model
            elif pinCount >= 3 && RegGraph.inTree model.RegGraph parent
                 && not (RegGraph.inTree model.RegGraph child) then
                env.Emit [SolvePair(child, parent)]
                model
            else
                let numOf m = (HashMap.tryFind m model.MeshOrder |> Option.defaultValue 0) + 1
                let why =
                    if pinCount < 3 then sprintf "only %d pin%s left" pinCount (if pinCount = 1 then "" else "s")
                    else "its reference is no longer in the tree"
                showToast env
                    (sprintf "Pair %d ↔ %d could not be re-solved (%s) — left unregistered"
                        (numOf child) (numOf parent) why)
                    (continuePendingResolves env model)

    // A step may retract what the current focus level depends on (pin deleted,
    // placement aborted, selection cleared) — demote to the nearest enabled
    // ancestor. Runs after every reducer step. A demotion out of Pin takes the
    // pin-level transients with it (this path bypasses jumpFocus).
    let private normalizeFocus (model : Model) =
        let placing = match model.ScanPins.Placement with PlacementActive _ -> true | PlacementIdle -> false
        let rec fix f = if FocusLevel.enabled model.Sel placing f then f else fix (FocusLevel.parent f)
        let f = fix model.Focus
        if f = model.Focus then model
        else
            { clearBrushAcross model.Focus f model with
                Focus = f; ArmedPick = None; ArmPreview = None; PinFocusHover = None }

    // ── Tile refocus (the camera rule: the MAIN 3D never moves on a GUI
    // action without an explicit prompt — the ortho tiles are EXEMPT and
    // re-frame to the current subject). frameTiles writes both pair meshes'
    // shared 2D cameras: one metric-world centre, one wanted half-width.
    let private frameTiles (meshes : string list) (centreWorld : V3d) (halfWidthMetric : float) (model : Model) =
        let scale = DatasetScale.active model.ActiveDataset model.DatasetScales
        let centreR = ScanPin.renderCentre model.CommonCentroid scale centreWorld
        // TileCam.Radius drives the ortho half-width via tan 30° — inverted
        // here so the frame lands at the wanted width.
        let radius =
            clamp 0.05 100000.0
                (ScanPin.renderLength scale halfWidthMetric / tan (30.0 * System.Math.PI / 180.0))
        { model with
            TileCams =
                meshes |> List.fold (fun tc m ->
                    Map.add m { Centre = centreR; Radius = radius } tc) model.TileCams }

    // Tight on the pin: the selected committed pin, else the draft's centre.
    let private framePinTiles (model : Model) =
        match model.Sel.Pair with
        | None -> model
        | Some (a, b) ->
            let subject =
                match model.Sel.Pin |> Option.bind (fun id -> HashMap.tryFind id model.ScanPins.Pins) with
                | Some p ->
                    Some (ScanPin.centreWorldWith (ModelTransforms.displayedWorld model p.AnchorMesh) p,
                          max 0.5 (p.InnerRadius * 3.0))
                | None ->
                    match model.ScanPins.Placement with
                    | PlacementActive d ->
                        d.Area |> Option.map (fun (m, local) ->
                            (ModelTransforms.displayedWorld model m).Forward.TransformPos local,
                            max 0.5 (d.Radius * 3.0))
                    | PlacementIdle -> None
            match subject with
            | Some (c, hw) -> frameTiles [a; b] c hw model
            | None -> model

    // The pair's overlap area (as-loaded world bbox intersection in XY; the
    // union when they don't meet) — the new-transaction framing.
    let private frameOverlapTiles (a : string) (b : string) (model : Model) =
        match Map.tryFind a model.MeshBounds, Map.tryFind b model.MeshBounds with
        | Some ba, Some bb when not ba.IsInvalid && not bb.IsInvalid ->
            let lo = V3d(max ba.Min.X bb.Min.X, max ba.Min.Y bb.Min.Y, min ba.Min.Z bb.Min.Z)
            let hi = V3d(min ba.Max.X bb.Max.X, min ba.Max.Y bb.Max.Y, max ba.Max.Z bb.Max.Z)
            let box = if lo.X < hi.X && lo.Y < hi.Y then Box3d(lo, hi) else ba.ExtendedBy bb
            frameTiles [a; b] box.Center (max 1.0 (0.6 * max box.Size.X box.Size.Y)) model
        | _ -> model

    let private updateCore (env : Env<Message>) (model : Model) (msg : Message) =
        match msg with
        | CameraMessage msg ->
            // While a pick is armed the LEFT button IS the pick — swallow left
            // rotate-begins so the main 3D holds still under a picking click
            // (pan/middle/wheel/touch stay live).
            let swallow =
                model.ArmedPick.IsSome &&
                (match msg with
                 | OrbitMessage.PointerDown(_, b, false, _) -> b = model.Camera.rotateButton
                 | _ -> false)
            if swallow then model
            else { model with Camera = OrbitController.update (Env.map CameraMessage env) model.Camera msg }
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
            let lf = model.AnchorGhostMode
            { model with AnchorGhostMode = LevelFlags.set model.Focus (not (LevelFlags.get model.Focus lf)) lf }
        | SetQuickPinRadius v ->
            { model with QuickPinRadius = max 0.01 v }
        | SetFlagScale v ->
            { model with FlagScale = clamp 0.1 10.0 v }
        | SetRevealRadius v ->
            let v = clamp 0.01 10.0 v
            if v = model.RevealRadius then model
            else
                { model with
                    RevealRadius = v
                    ScanPins = ScanPinModel.invalidateReveals model.ScanPins }
        | SetRegRoot mesh when model.RegGraph.Root = Some mesh ->
            model    // idempotent: re-designating the same root must not touch the graph
        | SetRegRoot mesh ->
            let g = model.RegGraph
            // A root change clears the whole per-level selection — every
            // descendant level loses its subject (and any in-flight pin work).
            let model =
                { model with
                    Sel = FocusSelection.empty
                    ScanPins = { model.ScanPins with Placement = PlacementIdle }
                    // Parent relations shift under a re-root — the queued
                    // dependents' orientations would be stale.
                    PendingResolves = [] }
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
        | SetTileIsolateHover h ->
            if model.TileIsolateHover = h then model else { model with TileIsolateHover = h }
        | SetMatrixHoverPair hp ->
            if model.MatrixHoverPair = hp then model else { model with MatrixHoverPair = hp }
        | ToggleTileIsolate mesh ->
            let iso = if model.TileIsolate = Some mesh then None else Some mesh
            // At the Pin level the isolate and the point focus are ONE state
            // (the focus buttons write both) — a tile click keeps them in step.
            let sel =
                match model.Focus, model.Sel.Pair with
                | FocusPin, Some (a, b) when mesh = a || mesh = b ->
                    { model.Sel with Point = iso }
                | _ -> model.Sel
            { model with TileIsolate = iso; Sel = sel }
        | SetFocus f ->
            let placing = match model.ScanPins.Placement with PlacementActive _ -> true | PlacementIdle -> false
            if f = model.Focus || not (FocusLevel.enabled model.Sel placing f) then model
            // The exit-guard: leaving Pin with an incomplete pin parks the
            // jump behind the confirm-delete popup instead of silently
            // rolling back — but ONLY once the centre exists; a centreless
            // draft is worthless and exits silently (the jump rolls it back).
            // Esc's FocusAscend goes through the same gate.
            elif model.Focus = FocusPin && placingWithCentre model then { model with PinExitPending = Some f }
            else jumpFocus f model
        | FocusAscend ->
            if model.Focus = FocusMatrix then model
            elif model.Focus = FocusPin && placingWithCentre model then { model with PinExitPending = Some FocusPair }
            else jumpFocus (FocusLevel.parent model.Focus) model
        | ConfirmPinExit ->
            (match model.PinExitPending with
             // The jump itself rolls the draft back (Pin → elsewhere).
             | Some f -> jumpFocus f model
             | None -> model)
        | CancelPinExit ->
            if model.PinExitPending.IsNone then model else { model with PinExitPending = None }
        | SelectPair(a, b) ->
            let key = PairCell.key a b
            if model.Sel.Pair = Some key then
                // The remembered pair: the selection (incl. its pin memory)
                // stands — re-entering restores the last workspace state.
                jumpFocus FocusPair model
            // Pre-warning: both meshes already connected THROUGH the tree with
            // no direct edge — a solve here can only add a redundant edge (a
            // loop). Park the entry behind the blocking confirm.
            elif RegGraph.pairEdge a b model.RegGraph |> Option.isNone
                 && MatrixNav.hopDepth model.RegGraph a |> Option.isSome
                 && MatrixNav.hopDepth model.RegGraph b |> Option.isSome then
                { model with PairConnectWarn = Some key }
            else
                // A NEW pair cascade-clears its descendants and every in-cell
                // cache; a placement bound to the old pair rolls back. The Pin
                // panes keep their meshes' shared 2D tile cameras.
                invalidateCellError
                    { jumpFocus FocusPair model with
                        Sel = { Pair = Some key; Pin = None; Point = None }
                        ScanPins = { model.ScanPins with Placement = PlacementIdle } }
        | ConfirmPairConnectWarn ->
            (match model.PairConnectWarn with
             | Some (a, b) ->
                invalidateCellError
                    { jumpFocus FocusPair { model with PairConnectWarn = None } with
                        Sel = { Pair = Some (PairCell.key a b); Pin = None; Point = None }
                        ScanPins = { model.ScanPins with Placement = PlacementIdle } }
             | None -> model)
        | CancelPairConnectWarn ->
            if model.PairConnectWarn.IsNone then model else { model with PairConnectWarn = None }
        | AssessGlobalQuality ->
            // Leaving Pin with a centred draft goes through the exit-guard
            // like every jump — the ribbon stays for a retry after resolving.
            if model.Focus = FocusPin && placingWithCentre model then
                { model with PinExitPending = Some FocusMatrix }
            else
                let m = if model.Focus = FocusMatrix then model else jumpFocus FocusMatrix model
                { m with
                    InspectOpen = LevelFlags.set FocusMatrix true m.InspectOpen
                    CellMapOn = true }
        | LogReach(source, action, subject) ->
            logReach source action subject model
        | ToggleReachLog ->
            { model with ReachLogOpen = not model.ReachLogOpen }
        | SetCheckpointName n ->
            if model.CheckpointName = n then model else { model with CheckpointName = n }
        | SetCheckpoints names ->
            if model.Checkpoints = names then model else { model with Checkpoints = names }
        | ApplyCheckpoint(name, ds, g, pins) ->
            // The view pre-switches the dataset (a SetActiveDataset rides in
            // front of this message when needed), so a mismatch here means
            // that switch was rejected — never apply cross-dataset data.
            if model.ActiveDataset <> Some ds then
                showToast env "Checkpoint belongs to another dataset — not applied" model
            else
                bumpPairSolve ()
                let model =
                    { jumpFocus FocusMatrix model with
                        Sel = FocusSelection.empty
                        ScanPins =
                            { Pins = pins |> List.map (fun p -> p.Id, p) |> HashMap.ofList
                              Placement = PlacementIdle }
                        LoopPending = None
                        PendingResolves = [] }
                showToast env (sprintf "Checkpoint '%s' loaded" name)
                    (invalidateCellError (ModelTransforms.recomposePoses { model with RegGraph = g }))
        | SelectPin id ->
            let valid =
                match HashMap.tryFind id model.ScanPins.Pins with
                | Some p -> model.Sel.Pair = Some p.Pair
                | None -> false
            if not valid || model.Sel.Pin = Some id then model
            else
                framePinTiles
                    { model with
                        Sel = { model.Sel with Pin = Some id; Point = None }
                        PinRadiusEditOpen = false }
        | SetPinFocusHover h ->
            if model.PinFocusHover = h then model else { model with PinFocusHover = h }
        | SetTilePinHover h ->
            if model.TilePinHover = h then model else { model with TilePinHover = h }
        | SetNewPinHover h ->
            if model.NewPinHover = h then model else { model with NewPinHover = h }
        | ToggleRadiusEdit ->
            { model with PinRadiusEditOpen = not model.PinRadiusEditOpen }
        | ToggleArmPick target ->
            let placing = match model.ScanPins.Placement with PlacementActive _ -> true | PlacementIdle -> false
            let valid =
                match target, model.Sel.Pair with
                // Centre: places the draft's area marker, or re-anchors the
                // selected committed pin (the panel's centre edit).
                | ArmCentre, Some _ -> model.Focus = FocusPin && (placing || model.Sel.Pin.IsSome)
                | ArmPoint m, Some (a, b) ->
                    model.Focus = FocusPin && (m = a || m = b) && (placing || model.Sel.Pin.IsSome)
                | _, None -> false
            if not valid then model
            elif model.ArmedPick = Some target then
                { model with ArmedPick = None; ArmPreview = None }
            else
                // Arming enters the scrimmed quasi-mode: the top-bar menus
                // close (an open one would float dead over the scrim).
                { model with
                    ArmedPick = Some target; ArmPreview = None
                    GearPopoverOpen = false; MeshMenuOpen = false; SensorMenuOpen = false
                    ReachLogOpen = false }
        | SetArmPreview p ->
            if model.ArmedPick.IsNone then
                if model.ArmPreview.IsSome then { model with ArmPreview = None } else model
            elif model.ArmPreview = p then model
            else { model with ArmPreview = p }

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
        | GraphErrorComputed(gen, after, before) ->
            if gen <> cellErrorGen then model
            else { model with GraphError = Some after; GraphErrorBefore = Some before }
        | GraphDistComputed(gen, after, before) ->
            if gen <> cellErrorGen then model
            else { model with GraphDist = Map.ofArray after; GraphDistBefore = Map.ofArray before }
        | SetBrushedSamples ids ->
            // Cap the brushed set so a runaway brush can't flood the 3D marker
            // node — generous enough to span every pin of the WIDEST scope (the
            // graph: every edge's pins at ≤300 samples each), so a wide brush
            // never silently drops whole pins. The dots cost one static buffer
            // billboarded in the vertex stage, so the cap is about sanity, not
            // frame time.
            let st = ids |> List.truncate 12000 |> Set.ofList
            if model.BrushedSamples = st then model
            else { model with BrushedSamples = st; HoverSample = None; HoverReadout = None }
        | SetHoverSample gid ->
            if model.HoverSample = gid then model
            else { model with HoverSample = gid; HoverReadout = None }
        | HoverReadoutComputed(gen, gid, v) ->
            if gen <> cellErrorGen || model.HoverSample <> Some gid then model
            else { model with HoverReadout = Some (gid, v) }
        | ToggleCellMap ->
            { model with CellMapOn = not model.CellMapOn }
        | SetPeekVis held ->
            // Pair-workspace scope (Pair AND Pin) + both pair meshes
            // GPU-resident + ANY effective isolation on a pair mesh — the
            // committed lock or a transient (tile / ◎-side hover, armed A/B
            // pick): the peek swaps whatever isolate is in effect to the
            // pair's other mesh while held; without one there is nothing to
            // swap. Releases always land.
            let isoOk =
                let eff, _ =
                    MeshVisibility.effectiveNarrowing model.PinFocusHover model.ArmedPick
                        model.TileIsolateHover model.TileIsolate model.Sel.Point
                match eff, model.Sel.Pair with
                | Some m, Some (a, b) -> m = a || m = b
                | _ -> false
            if model.PeekVis = held || (held && not (peekPairLoaded model && isoOk)) then model
            else { model with PeekVis = held }
        | SetPeekPose held ->
            if model.PeekPose = held || (held && not (peekPoseOk model)) then model
            else { model with PeekPose = held }
        | SelectLoopEdge sel ->
            (match model.LoopPending with
             | Some lp -> { model with LoopPending = Some { lp with Selected = sel } }
             | None -> model)
        | HoverLoopChoice h ->
            (match model.LoopPending with
             | Some lp when lp.Hover <> h -> { model with LoopPending = Some { lp with Hover = h } }
             | _ -> model)
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
                // the centroids are in): its sensor position, framed to its own
                // bounds rather than the whole scene. One-shot per dataset load.
                let center, radius =
                    match m.RegGraph.Root |> Option.bind (fun r -> Map.tryFind r perMesh |> Option.map (fun b -> r, b)) with
                    | Some (r, b) ->
                        let scale = DatasetScale.forMesh m.DatasetScales r
                        ModelTransforms.sensorRender m r, max 1.0 (b.Size.Length * scale * 0.6)
                    | None ->
                        ModelTransforms.firstSensorRender m, max 1.0 (padded.Size.Length * 0.6)
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
                    TileIsolate = None
                    TileIsolateHover = None
                    MatrixHoverPair = None
                    RegGraph = RegGraph.empty
                    ComposedPoses = Map.empty
                    PairOverlaps = Map.empty
                    Focus = FocusMatrix
                    Sel = FocusSelection.empty
                    TileCams = Map.empty
                    CellError = None
                    CellErrorBefore = None
                    CellDist = None
                    GraphError = None
                    GraphErrorBefore = None
                    GraphDist = Map.empty
                    GraphDistBefore = Map.empty
                    BrushedSamples = Set.empty
                    HoverSample = None
                    HoverReadout = None
                    ArmedPick = None
                    ArmPreview = None
                    PinFocusHover = None
                    TilePinHover = None
                    NewPinHover = false
                    PinRadiusEditOpen = false
                    PeekVis = false
                    PeekPose = false
                    LoopPending = None
                    PinExitPending = None
                    PairConnectWarn = None
                    PendingResolves = []
                    Toast = None }
        | SetRenderingMode m ->
            { model with RenderingMode = m }
        | ToggleGearPopover ->
            { model with GearPopoverOpen = not model.GearPopoverOpen }
        | ToggleMeshMenu ->
            { model with MeshMenuOpen = not model.MeshMenuOpen }
        | ToggleSensorMenu ->
            { model with SensorMenuOpen = not model.SensorMenuOpen }
        | ToggleInspectPanel ->
            let lf = model.InspectOpen
            { model with InspectOpen = LevelFlags.set model.Focus (not (LevelFlags.get model.Focus lf)) lf }
        | ScanPinMsg msg ->
            let m = ScanPinUpdate.handleMsg env model msg
            // New pin → enter Pin in placement with the CENTRE pick pre-armed
            // (the natural first pick), the tiles framed on the pair's overlap
            // area, and the pin selection cleared — the DRAFT is the subject
            // (the newborn re-selects on completion).
            let m =
                match msg with
                | BeginPinTransaction (a, b) ->
                    frameOverlapTiles a b
                        { jumpFocus FocusPin m with
                            ArmedPick = Some ArmCentre
                            Sel = { m.Sel with Pin = None; Point = None } }
                | _ -> m
            // A landed pick exits its arm (the spec'd disarm path); the tiles
            // re-frame on placement/edit — tight on the pin.
            let m =
                match msg with
                | DraftAreaAt _ | DraftPointAt _ | EditPointAt _ | EditCentreAt _ ->
                    { m with ArmedPick = None; ArmPreview = None }
                | _ -> m
            // Guided placement: while the draft lives, every landed part arms
            // the next missing one (centre → point A → point B — free order
            // still converges), so ○ New pin walks all three steps without a
            // manual re-arm; the last landing minted the pin (placement idle),
            // which ends the chain. The placement banner names the armed step.
            let m =
                match msg with
                | DraftAreaAt _ | DraftPointAt _ ->
                    (match m.ScanPins.Placement with
                     | PlacementActive d ->
                        let next =
                            if d.Area.IsNone then Some ArmCentre
                            elif d.PointA.IsNone then Some (ArmPoint (fst d.Pair))
                            elif d.PointB.IsNone then Some (ArmPoint (snd d.Pair))
                            else None
                        { m with ArmedPick = next }
                     | PlacementIdle -> m)
                | _ -> m
            let m =
                match msg with
                | DraftAreaAt _ | SetInnerRadius _ | EditPointAt _ | EditCentreAt _ -> framePinTiles m
                | _ -> m
            // Selection maintenance: a draft pick that COMPLETED the pin is
            // its birth (implicit completion) — the newborn is selected from
            // birth and re-scopes the inspection; deleting the selected pin
            // clears it.
            let born =
                match msg with
                | DraftAreaAt _ | DraftPointAt _ ->
                    m.ScanPins.Pins |> HashMap.toSeq
                    |> Seq.tryPick (fun (id, _) ->
                        if HashMap.containsKey id model.ScanPins.Pins then None else Some id)
                | _ -> None
            let m =
                match born with
                | Some id ->
                    invalidateCellError
                        (framePinTiles { m with Sel = { m.Sel with Pin = Some id; Point = None } })
                | None -> m
            let m =
                match msg with
                | DeletePin id when model.Sel.Pin = Some id ->
                    { m with Sel = { m.Sel with Pin = None; Point = None } }
                | _ -> m
            // Pin geometry changes re-scope the in-cell inspection.
            let m =
                match msg with
                | SetInnerRadius _ | EditPointAt _ | EditCentreAt _ | DeletePin _ -> invalidateCellError m
                | _ -> m
            // ANY committed-pin edit invalidates its pair's solve: the pair's
            // edge (and every edge hanging beneath it — the subtree would
            // strand) drops and the poses recompose. The pair is read from the
            // PRE-edit model so a delete still resolves it.
            let editedPair =
                match msg with
                | SetInnerRadius(id, _) | EditPointAt(id, _, _) | EditCentreAt(id, _, _) | DeletePin id ->
                    HashMap.tryFind id model.ScanPins.Pins |> Option.map (fun p -> p.Pair)
                | _ -> None
            match editedPair with
            | Some (a, b) ->
                match RegGraph.pairEdge a b m.RegGraph with
                | Some e ->
                    bumpPairSolve ()
                    // The cascade's collateral — every edge BELOW the dropped
                    // one — queues for the automatic re-solve after THIS pair
                    // solves again (its own edge re-solves by the user's click).
                    let dependents = subtreeDependents m.RegGraph e.Child
                    let toastMsg =
                        if List.isEmpty dependents then "Pair unregistered — a pin changed"
                        else
                            sprintf "Pair unregistered — a pin changed (%d dependent pair%s will re-solve with it)"
                                (List.length dependents) (if List.length dependents = 1 then "" else "s")
                    showToast env toastMsg
                        (invalidateCellError
                            (invalidateRings
                                (ModelTransforms.recomposePoses
                                    { m with
                                        RegGraph = RegGraph.removeEdgeCascading e.Child m.RegGraph
                                        PendingResolves =
                                            (m.PendingResolves @ dependents) |> List.distinctBy fst })))
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
                    // The re-solve cascade drains AFTER the registered toast so
                    // a degradation notice stays visible over it.
                    continuePendingResolves env
                        (showToast env (sprintf "Pair registered — quality %.2f" q)
                            (invalidateCellError
                                (invalidateRings (ModelTransforms.recomposePoses { model with RegGraph = g2 }))))
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
                                Selected = weakest
                                Hover = None } }
        | SetNearCut v ->
            { model with NearCutFrac = clamp 0.0 1.25 v }
        | SetFarCut v ->
            // 0 would cut everything — the off position is the RIGHT end.
            { model with FarCutFrac = clamp 0.05 2.5 v }
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
            // A sensor-VIEWPOINT jump, not an overview framing: a close orbit
            // around where the scanner actually stood — the sensor rides the
            // mesh's displayed pose (server frame == metric world at load).
            let world = (ModelTransforms.displayedWorld model mesh).Forward.TransformPos
                            (ModelTransforms.sensorWorld model mesh)
            env.Emit [FlyToPoint(world, 10.0)]
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
        | FocusMatrix, _ | _, None -> model
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
        | FocusMatrix, _ | _, None -> model
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

    // The graph-scope error stream (Matrix): one pin batch per established
    // edge, measured child-relative-to-parent (pass the PARENT first — the
    // endpoint returns meshB relative to meshA, so the parent-relative
    // orientation needs no flip), fanned out in parallel. Async.Parallel keeps
    // the request order, so the canonical edge×pin gid stream is deterministic.
    // Both states are fetched together — AFTER at the composed poses (the
    // residual), BEFORE with both endpoints at their as-loaded baselines (the
    // raw pre-registration disagreement) — and land in one message, so the
    // Matrix pose peek only ever swaps two complete streams.
    let private ensureGraphError (env : Env<Message>) (model : Model) : Model =
        let edges = model.RegGraph.Edges |> Map.toList |> List.sortBy fst
        if model.Focus <> FocusMatrix || List.isEmpty edges
           || model.GraphError.IsSome || graphErrorReqGen = cellErrorGen then model
        else
            graphErrorReqGen <- cellErrorGen
            let gen = cellErrorGen
            let roisAt (world : string -> Trafo3d) (pins : ScanPin list) =
                pins |> List.map (fun p ->
                    let (ScanPinId.ScanPinId g) = p.Id
                    g.ToString "N", (world p.AnchorMesh).Forward.TransformPos p.CentreLocal, p.InnerRadius)
            let reqs =
                edges |> List.choose (fun (child, e) ->
                    let pins =
                        model.ScanPins.Pins |> HashMap.toList |> List.map snd
                        |> List.filter (fun p -> p.Pair = PairCell.key child e.Parent)
                        |> List.sortBy (fun p -> p.CreatedAt, p.ShortName)
                    if List.isEmpty pins then None
                    else Some (child, e.Parent, pins |> List.map (fun p -> p.Id), pins))
            if List.isEmpty reqs then model
            else
                // A pin's ROI centre rides its anchor mesh, so the before state
                // has to re-place it at the baseline too — the sphere must sit
                // on the surface it measures.
                let batch (world : string -> Trafo3d) =
                    reqs
                    |> List.map (fun (child, parent, ids, pins) ->
                        async {
                            try
                                let! r =
                                    Query.pairError ApiConfig.apiBase.Value
                                        parent (world parent).Forward child (world child).Forward (roisAt world pins)
                                return
                                    Seq.zip ids r
                                    |> Seq.map (fun (id, e) -> { Mov = child; Ref = parent; Pin = id; Err = e })
                                    |> Seq.toArray
                            with _ -> return [||]
                        })
                    |> Async.Parallel
                task {
                    try
                        let afterT = batch (ModelTransforms.displayedWorld model) |> Async.StartAsTask
                        let beforeT = batch (ModelTransforms.loadWorld model) |> Async.StartAsTask
                        let! after = afterT
                        let! before = beforeT
                        env.Emit [GraphErrorComputed(gen, Array.concat after, Array.concat before)]
                    with _ -> ()
                } |> ignore
                model

    // The graph-scope false-colour buffers (Matrix): every established edge's
    // CHILD against its PARENT — the union of the per-edge moving-side maps,
    // fanned out in parallel (independent queries; one sequential sweep would
    // stall the whole map on the slowest edge). Both states again, so the pose
    // peek repaints from resident buffers. Unregistered meshes are simply
    // absent — nothing fabricates error for a mesh that has no parent.
    let private ensureGraphDist (env : Env<Message>) (model : Model) : Model =
        let edges = model.RegGraph.Edges |> Map.toList
        if model.Focus <> FocusMatrix || not model.CellMapOn || List.isEmpty edges
           || not (Map.isEmpty model.GraphDist) || graphDistReqGen = cellErrorGen then model
        else
            graphDistReqGen <- cellErrorGen
            let gen = cellErrorGen
            let sweep (world : string -> Trafo3d) =
                edges
                |> List.map (fun (child, e) ->
                    async {
                        try
                            let! d =
                                Query.regionDistance ApiConfig.apiBase.Value child e.Parent
                                    (world child).Forward (world e.Parent).Forward
                            return Some (child, d)
                        with _ -> return None
                    })
                |> Async.Parallel
            task {
                try
                    let afterT = sweep (ModelTransforms.displayedWorld model) |> Async.StartAsTask
                    let beforeT = sweep (ModelTransforms.loadWorld model) |> Async.StartAsTask
                    let! after = afterT
                    let! before = beforeT
                    let landed = after |> Array.choose id
                    if landed.Length > 0 then
                        env.Emit [GraphDistComputed(gen, landed, before |> Array.choose id)]
                with _ -> ()
            } |> ignore
            model

    // The spanned event is a TRANSITION, not a state read — logged for the
    // reaching record; the visible mark (the tree's finished ribbon) is
    // purely DERIVED from the spanned state, so disconnecting clears it with
    // zero bookkeeping. Runs after every reducer step against the pre-step
    // model.
    let private trackSpanned (model0 : Model) (model : Model) =
        let names (m : Model) = m.MeshNames |> IndexList.toList
        let was = Workflow.spanned (names model0) model0.RegGraph
        let now = Workflow.spanned (names model) model.RegGraph
        if now && not was then logReach "event" "spanned" "" model
        elif was && not now then logReach "event" "unspanned" "" model
        else model

    let update (env : Env<Message>) (model : Model) (msg : Message) =
        updateCore env model msg
        |> trackSpanned model
        |> normalizeFocus
        |> ScanPinUpdate.ensureRings env
        |> ensureCellError env
        |> ensureCellDist env
        |> ensureGraphError env
        |> ensureGraphDist env
        |> ensurePairOverlaps env

