namespace Superprojekt

open Aardvark.Base
open Aardworx.WebAssembly
open FSharp.Data.Adaptive
open Aardvark.Dom
open Microsoft.JSInterop

// Left rail: three modes (Overview · Correspondence · Inspect), one expanded at
// a time; the container never moves, only the active mode's detail changes.
// Pure view — every control dispatches an existing message and never issues
// server queries itself.
module GuiRail =

    open Primitives

    type private Pill = PillReady | PillWarn | PillBlock | PillInfo

    // Cell state in the pin×mesh matrix: a coloured difference swatch, a faint
    // out-of-ROI emptiness glyph, or a pending (probe still running) placeholder.
    type private CellInfo = CellPending | CellEmpty | CellVal of C4b

    let private pillClass = function
        | PillReady -> "rail-pill rail-pill-ready"
        | PillWarn  -> "rail-pill rail-pill-warn"
        | PillBlock -> "rail-pill rail-pill-block"
        | PillInfo  -> "rail-pill rail-pill-info"

    let private hex = Primitives.c4bToRgbCss

    let rail (env : Env<Message>) (model : AdaptiveModel) (viewportSize : aval<V2i>) =
        let refMesh   = model.Registration |> AVal.map (fun r -> r.ReferenceMesh)
        let curStep   = model.WorkflowStep
        ignore viewportSize

        // Status pill on the Register header only — Overview/Inspect titles stay bare.
        let corrStatus : aval<Pill * string> =
            AVal.custom (fun t ->
                let hasRef = (model.Registration.GetValue t).ReferenceMesh |> Option.isSome
                let solved = not (Map.isEmpty (model.SolvedTransforms.GetValue t))
                if not hasRef then PillBlock, "needs a reference"
                elif solved then PillReady, "aligned"
                else PillInfo, "place ≥3 correspondences, then solve")

        let modeHeader (step : WorkflowStep) =
            let active = curStep |> AVal.map ((=) step)
            let children =
                [ span { Class "rail-step-no"; string (WorkflowStep.index step + 1) }
                  span { Class "rail-step-title"; WorkflowStep.title step } ]
                @ (if step = Correspondence then
                       [ span {
                             corrStatus |> AVal.map (fun (p, _) -> Some (Class (pillClass p)))
                             corrStatus |> AVal.map (fun (p, _) ->
                                 match p with PillReady -> "✔" | PillWarn -> "⚠" | PillBlock -> "✖" | PillInfo -> "•")
                         } ]
                   else [])
            button {
                Class "rail-step"
                classWhen "rail-step-active" active
                Dom.OnClick(fun _ -> env.Emit [SetWorkflowStep step])
                AList.ofList children
            }

        let modeBody (step : WorkflowStep) (body : DomNode) =
            div {
                Class "rail-body"
                showWhen (curStep |> AVal.map ((=) step))
                body
            }

        // Overview rail roster (§B): one row per mesh. click name = focus,
        // double-click = zoom, hover = peek.
        // The trailing controls are the per-mesh intrinsic error-visualization switches
        // (Textured / Distance / Shape / Incidence) — respected in both the 3D and the
        // 2D focus views. The old ★ ref / vis / ◐ isolate controls were duplicates of the
        // focus tile strip (T3) and were removed from here.
        let meshRow (name : string) =
            let isVis  = model.MeshVisible |> AVal.map (fun m -> Map.tryFind name m |> Option.defaultValue true)
            let idxVal = model.MeshOrder |> AMap.tryFind name |> AVal.map (Option.defaultValue 0)
            let colorVal = idxVal |> AVal.map meshColor
            let hovered = model.Selection.Hovered |> AVal.map (function Some (HoverMesh m) -> m = name | _ -> false)
            let focused = model.Selection.FocusedMesh |> AVal.map ((=) (Some name))
            let hm = model.MeshHeatmap |> AVal.map (fun m -> Map.tryFind name m |> Option.defaultValue HeatOff)
            let swatch = span { Class "mesh-swatch"; colorVal |> AVal.map (fun c -> Some (Style [Css.Background (hex c)])) }
            let num    = span { Class "mesh-num"; idxVal |> AVal.map (fun i -> string (i + 1)) }
            let nameSpan =
                span {
                    Class "rail-mesh-name"
                    Dom.OnClick(fun _ -> env.Emit [SetFocusedMesh (Some name)])
                    Dom.OnDoubleClick(fun _ ->
                        env.Emit [SetFocusedMesh (Some name); ZoomToMesh name]
                        FocusScene.resetCam (Some name))
                    model.MeshNames.Content |> AVal.map (fun ns -> friendlyName (IndexList.toList ns) name)
                }
            let modeBar =
                div {
                    Class "rail-mesh-modes"
                    Attribute("title", "Error visualization for this mesh (3D + focus): Textured · Distance · Shape · Incidence")
                    compactButtonBar [
                        "Tex",  (hm |> AVal.map ((=) HeatOff)),       (fun () -> env.Emit [SetMeshHeatmap(name, HeatOff)])
                        "Dst",  (hm |> AVal.map ((=) HeatRange)),     (fun () -> env.Emit [SetMeshHeatmap(name, HeatRange)])
                        "Shp",  (hm |> AVal.map ((=) HeatShape)),     (fun () -> env.Emit [SetMeshHeatmap(name, HeatShape)])
                        "Inc",  (hm |> AVal.map ((=) HeatIncidence)), (fun () -> env.Emit [SetMeshHeatmap(name, HeatIncidence)])
                    ]
                }
            div {
                Class "rail-mesh-row"
                classWhenNot "rail-row-dim" isVis
                classWhen "rail-row-hover" hovered
                classWhen "rail-mesh-sel" focused
                Attribute("title", name)
                // hover = peek-isolate this mesh via the shared Selection.
                Dom.OnPointerMove(fun _ -> env.Emit [SetHovered (Some (HoverMesh name))])
                Dom.OnMouseLeave(fun _ -> env.Emit [SetHovered None])
                AList.ofList ([ swatch; num; nameSpan; modeBar ])
            }
        let overviewBody =
            div { Class "rail-mesh-list"; model.MeshNames |> AList.map meshRow }

        // ── Pin × mesh matrix (§B) — the navigation backbone in Correspondence +
        // Inspect. Rows = pins (glyph · name · pin-colour swatch · ≤5 per-mesh cells);
        // each cell = the (pin, mesh) ROI-median signed distance to the reference,
        // painted on the linear-diverging difference map (§C), before/after aware
        // (the probe refetches on RegView), out-of-ROI → a faint emptiness glyph.
        let pinsVal = model.ScanPins.Pins |> AMap.toAVal
        // The reference's marker is its RefAnchor — a ref-column cell behaves like
        // any other cell.
        let cellAnchorOwn (id : ScanPinId) (mesh : string) =
            let pin = HashMap.tryFind id (AVal.force pinsVal)
            let isRef = (AVal.force model.Registration).ReferenceMesh = Some mesh
            let anchorOwn =
                pin |> Option.bind ScanPin.correspondence |> Option.bind (fun c ->
                    if isRef then c.RefAnchor
                    else Map.tryFind mesh c.Anchors |> Option.map (fun a -> a.Point))
            pin, anchorOwn
        // Cell SINGLE click = selection/visibility only (no camera): locate when a
        // marker exists, else select + focus. Clicking the cell of the ACTIVE locate
        // toggles it off: BackOutLocate restores the pre-locate camera / solo /
        // visibility and the selection clears. ClickGate-deferred because of that
        // toggle — a double-click must not toggle twice on the way to its zoom.
        let selectCell (id : ScanPinId) (mesh : string) =
            let isLocated =
                (AVal.force model.LocateBackup).IsSome
                && AVal.force model.Selection.SelectedPin = Some id
                && AVal.force model.Selection.FocusedMesh = Some mesh
            if isLocated then
                env.Emit [BackOutLocate; SetFocusedMesh None; ScanPinMsg (SelectPin None)]
                FocusScene.resetCam (Some mesh)
            else
                match cellAnchorOwn id mesh with
                | _, Some _ -> env.Emit [FrameCorrespondence(id, mesh)]
                | _, None -> env.Emit [ScanPinMsg (SelectPin (Some id)); SetFocusedMesh (Some mesh)]
        // Cell DOUBLE click = zoom both viewports onto the correspondence. Ends in
        // "located + zoomed" regardless of what the leading clicks toggled:
        // FrameCorrespondence is (re-)issued (idempotent when already located; also
        // forces ProjTop, which the 2D zoom maths needs).
        let zoomCell (id : ScanPinId) (mesh : string) =
            match cellAnchorOwn id mesh with
            | pin, Some own ->
                env.Emit [FrameCorrespondence(id, mesh)]
                let cc = AVal.force model.CommonCentroid
                let s  = DatasetScale.forMesh (AVal.force model.DatasetScales) mesh
                let world = (RigidTransform.renderToWorld s cc (AVal.force (MeshView.displayedMeshT model mesh))).Forward.TransformPos own
                let r = pin |> Option.map (fun p -> p.InnerRadius) |> Option.defaultValue 0.5
                let tight = max 0.5 (r * 4.0)
                env.Emit [FlyToPoint(world, tight)]
                FocusScene.zoomOnWorldRadius model mesh world tight
            | _, None ->
                // No marker to zoom onto: select + frame the mesh instead.
                env.Emit [ScanPinMsg (SelectPin (Some id)); SetFocusedMesh (Some mesh); ZoomToMesh mesh]
                FocusScene.resetCam (Some mesh)
        let matrixHead () =
            div {
                Class "mx-head"
                div { Class "mx-corner" }
                model.MeshNames |> AList.map (fun name ->
                    // Columns show only mesh colour + number (T3); per-mesh controls
                    // and the ★ reference live on the focus tile strip. Clicking a column
                    // focuses the mesh (identical to clicking its focus tile); the column
                    // highlights when that mesh is focused (the reverse link).
                    let idxVal = model.MeshOrder |> AMap.tryFind name |> AVal.map (Option.defaultValue 0)
                    let colFocused = model.Selection.FocusedMesh |> AVal.map ((=) (Some name))
                    let colRef = refMesh |> AVal.map ((=) (Some name))
                    div {
                        Class "mx-colhead"
                        classWhen "mx-col-sel" colFocused
                        classWhen "mx-col-ref" colRef
                        Attribute("title", name)
                        Dom.OnClick(fun _ -> env.Emit [SetFocusedMesh (Some name)])
                        Dom.OnDoubleClick(fun _ ->
                            env.Emit [SetFocusedMesh (Some name); ZoomToMesh name]
                            FocusScene.resetCam (Some name))
                        Dom.OnPointerMove(fun _ -> env.Emit [SetHovered (Some (HoverMesh name))])
                        Dom.OnMouseLeave(fun _ -> env.Emit [SetHovered None])
                        span { Class "mx-colsw"; idxVal |> AVal.map (fun i -> Some (Style [Css.Background (hex (meshColor i))])) }
                        span { Class "mx-colnum"; idxVal |> AVal.map (fun i -> string (i + 1)) }
                    })
            }
        let matrixRow (id : ScanPinId) (glyph : string) (shortNm : string) (pinColor : C4b) =
            let selected = model.Selection.SelectedPin |> AVal.map ((=) (Some id))
            let pinHover = model.Selection.Hovered |> AVal.map (function Some (HoverPin i) -> i = id | _ -> false)
            // Per-(pin,mesh) difference data, derived from the pin's probe (medians
            // re-centred so 0 = reference median) + ROI membership. Recomputes when
            // the probe lands; cells are stable divs that only recolour.
            let rowData =
                model.ScanPins.Pins |> AMap.tryFind id
                |> AVal.map (fun po ->
                    match po with
                    | Some p ->
                        match p.Probe with
                        | ProbeReady r ->
                            let corr = ScanPin.correspondence p
                            let inRoiOf m = match corr with Some c -> Map.tryFind m c.InRoi |> Option.defaultValue true | None -> true
                            let refStd =
                                r.Distributions |> Array.tryFind (fun d -> d.MeshName = r.ReferenceMesh)
                                |> Option.map (fun d -> d.Std) |> Option.defaultValue 0.0
                            let cells =
                                r.Distributions |> Array.choose (fun d ->
                                    if d.Count <= 0 then None
                                    else
                                        let lod = 1.96 * sqrt (refStd * refStd + d.Std * d.Std)
                                        Some (d.MeshName, (d.Median, lod, inRoiOf d.MeshName)))
                                |> Map.ofArray
                            let movers = r.Distributions |> Array.filter (fun d -> d.MeshName <> r.ReferenceMesh && d.Count > 0)
                            let range = if movers.Length = 0 then 1.0e-3 else max 1.0e-3 (movers |> Array.map (fun d -> abs d.Median) |> Array.max)
                            Some (cells, range)
                        | _ -> None
                    | None -> None)
            let cell (mesh : string) =
                let info =
                    rowData |> AVal.map (function
                        | None -> CellPending
                        | Some (cells, range) ->
                            match Map.tryFind mesh cells with
                            | Some (median, lod, inRoi) -> if inRoi then CellVal (Primitives.Diff.color lod range median) else CellEmpty
                            | None -> CellEmpty)
                let active =
                    (selected, model.Selection.Hovered) ||> AVal.map2 (fun sel hov ->
                        (sel && (match hov with Some (HoverPoint(i,m)) -> i = id && m = mesh | _ -> false)))
                let cellRef = refMesh |> AVal.map ((=) (Some mesh))
                div {
                    Class "mx-cell"
                    info |> AVal.map (function CellVal c -> Some (Style [Css.Background (hex c)]) | _ -> None)
                    info |> AVal.map (function CellEmpty -> Some (Class "mx-cell-empty") | CellPending -> Some (Class "mx-cell-pending") | CellVal _ -> None)
                    classWhen "mx-cell-active" active
                    classWhen "mx-cell-ref" cellRef
                    Attribute("title", mesh)
                    Dom.OnClick(fun _ -> ClickGate.single "mx-cell" (fun () -> selectCell id mesh))
                    Dom.OnDoubleClick(fun _ -> ClickGate.double "mx-cell" (fun () -> zoomCell id mesh))
                    Dom.OnPointerMove(fun _ -> env.Emit [SetHovered (Some (HoverPoint(id, mesh)))])
                    Dom.OnMouseLeave(fun _ -> env.Emit [SetHovered None])
                }
            div {
                Class "mx-row"
                classWhen "mx-row-sel" selected
                classWhen "mx-row-hover" pinHover
                div {
                    Class "mx-rowhead"
                    // Pin linking: click = select (the reducer applies the Inspect
                    // isolation swap); double-click = zoom — 3D tight to the pin,
                    // focus panel keeps its mesh and zooms onto the pin too.
                    Dom.OnClick(fun _ -> env.Emit [ScanPinMsg (SelectPin (Some id))])
                    Dom.OnDoubleClick(fun _ ->
                        env.Emit [ScanPinMsg (SelectPin (Some id)); ZoomToPin id]
                        match HashMap.tryFind id (AVal.force pinsVal) with
                        | Some p -> FocusScene.zoomOnPin model p.Centre p.InnerRadius
                        | None -> ())
                    Dom.OnPointerMove(fun _ -> env.Emit [SetHovered (Some (HoverPin id))])
                    Dom.OnMouseLeave(fun _ -> env.Emit [SetHovered None])
                    span { Class "mx-glyph"; Style [Css.Background (hex pinColor)]; glyph }
                    span { Class "mx-name"; shortNm }
                    button {
                        Class "mb mx-del"
                        Attribute("title", "Delete pin")
                        // Native confirmation dialog before the destructive delete.
                        Dom.OnClick(fun _ ->
                            let ok = try JSRuntime.Instance.Invoke<bool>("confirm", sprintf "Delete pin %s %s? This cannot be undone." glyph shortNm) with _ -> false
                            if ok then env.Emit [ScanPinMsg (DeletePin id)])
                        "✕"
                    }
                }
                model.MeshNames |> AList.map cell
            }
        // Stable-identity row list, sorted by creation order; project to identity
        // only (glyph/name/colour/created) so probe/ring updates don't re-key a row.
        let matrixRows () =
            model.ScanPins.Pins
            |> AMap.map (fun _ p -> p.Glyph, p.ShortName, p.PinColor, p.CreatedAt)
            |> AMap.toASet
            |> ASet.sortBy (fun (ScanPinId.ScanPinId g, (_, _, _, created)) -> created, g)
            |> AList.map (fun (id, (glyph, shortNm, pinColor, _)) -> matrixRow id glyph shortNm pinColor)
        // Built fresh per call: the matrix is mounted in BOTH the Correspondence and
        // Inspect bodies (both always in the DOM), so they must be distinct nodes.
        let matrixView () =
            div {
                Class "mx"
                matrixHead ()
                div { Class "mx-rows"; matrixRows () }
            }
        let placing =
            model.ScanPins.Placement |> AVal.map (function AnchorPlacement -> true | _ -> false)

        // The global reconstruction-readiness hint (moved here from the top bar) sits
        // next to the Solve button, which is greyed until a mesh is solvable (≥3 in-ROI
        // markers with a reference). The per-mesh/per-pin hints stay in the matrix.
        // The Ready diagnostic gets the display numbers of the solvable meshes
        // appended ("Ready to align 1,3") — a view concern (numbering = MeshOrder),
        // so it patches the engine text here rather than inside Readiness.compute.
        let readiness =
            (ReadinessView.input model, model.MeshOrder.Content) ||> AVal.map2 (fun input order ->
                Readiness.compute input
                |> List.map (fun d ->
                    if d.Severity = Ready then
                        let nums =
                            Readiness.pairCounts input
                            |> List.choose (fun (m, n) ->
                                if n >= 3 then Some ((HashMap.tryFind m order |> Option.defaultValue 0) + 1) else None)
                            |> List.sort
                        if List.isEmpty nums then d
                        else { d with Text = sprintf "%s %s" d.Text (nums |> List.map string |> String.concat ",") }
                    else d))
        let canSolve  = readiness |> AVal.map (List.exists (fun d -> d.Severity = Ready))
        let sevClass  = function Blocker -> "block" | Warning -> "warn" | Ready -> "ready" | Info -> "info"
        let sevIcon   = function Blocker -> "✖" | Warning -> "⚠" | Ready -> "✔" | Info -> "•"
        let corrBody =
            div {
                Class "rail-step-controls"
                div {
                    Class "rail-solve-row"
                    button {
                        Class "rail-btn rail-btn-primary rail-solve"
                        canSolve |> AVal.map (fun ok -> if ok then None else Some (Attribute("disabled", "disabled")))
                        Attribute("title", "Align the moving meshes to the reference from the placed correspondences")
                        Dom.OnClick(fun _ -> if AVal.force canSolve then env.Emit [SolveCoarse])
                        "Solve"
                    }
                    div {
                        Class "rail-readiness"
                        readiness |> AVal.map IndexList.ofList |> AList.ofAVal |> AList.map (fun d ->
                            span {
                                Class (sprintf "tb-ready-pill tb-ready-%s" (sevClass d.Severity))
                                span { Class "tb-ready-ic"; sevIcon d.Severity }
                                span { Class "tb-ready-tx"; d.Text }
                            })
                    }
                }
                div {
                    Class "rail-pins-head"
                    button {
                        Class "rail-btn rail-pin-add"
                        classWhen "rail-btn-active" placing
                        Attribute("title", "Place a pin — tap on the reference surface")
                        Dom.OnClick(fun _ ->
                            if AVal.force placing then env.Emit [ScanPinMsg CancelPlacement]
                            else env.Emit [ScanPinMsg EnterAnchorPlacement])
                        placing |> AVal.map (fun p -> if p then "○ placing… (Esc)" else "○ New pin")
                    }
                }
                div { Class "rail-matrix-wrap"; matrixView () }
            }

        // Inspect rail = the same pin×mesh matrix (identical metric to Correspondence,
        // §B). The Difference / Intrinsic channel controls moved to the Inspect dock.
        let inspectBody =
            div {
                Class "rail-step-controls"
                div { Class "rail-matrix-wrap"; matrixView () }
            }

        div {
            Class "workflow-rail"
            div {
                Class "rail-steps"
                modeHeader Overview
                modeBody Overview overviewBody
                modeHeader Correspondence
                modeBody Correspondence corrBody
                modeHeader Inspect
                modeBody Inspect inspectBody
            }
        }
