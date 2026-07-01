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
        let flyTo (target : FlyToTarget) =
            let s = AVal.force viewportSize
            env.Emit [FlyTo(target, float s.X / float (max 1 s.Y))]

        let stepStatus (step : WorkflowStep) : aval<Pill * string> =
            AVal.custom (fun t ->
                let hasRef = (model.Registration.GetValue t).ReferenceMesh |> Option.isSome
                let solved = not (Map.isEmpty (model.SolvedTransforms.GetValue t))
                match step with
                | Overview ->
                    if hasRef then PillReady, "reference set"
                    else PillBlock, "pick a reference ★"
                | Correspondence ->
                    if not hasRef then PillBlock, "needs a reference"
                    elif solved then PillReady, "aligned"
                    else PillInfo, "place ≥3 correspondences, then solve"
                | Inspect ->
                    if hasRef then PillInfo, "error layers"
                    else PillBlock, "needs a reference")

        let modeHeader (step : WorkflowStep) =
            let active = curStep |> AVal.map ((=) step)
            let status = stepStatus step
            button {
                Class "rail-step"
                classWhen "rail-step-active" active
                Dom.OnClick(fun _ -> env.Emit [SetWorkflowStep step])
                span { Class "rail-step-no"; string (WorkflowStep.index step + 1) }
                span { Class "rail-step-title"; WorkflowStep.title step }
                span {
                    status |> AVal.map (fun (p, _) -> Some (Class (pillClass p)))
                    status |> AVal.map (fun (p, _) ->
                        match p with PillReady -> "✔" | PillWarn -> "⚠" | PillBlock -> "✖" | PillInfo -> "•")
                }
            }

        let modeBody (step : WorkflowStep) (body : DomNode) =
            div {
                Class "rail-body"
                showWhen (curStep |> AVal.map ((=) step))
                body
            }

        // Overview rail roster (§B): one row per mesh. click = focus, hover = peek;
        // the trailing controls (★ ref, vis, solo, sensor, frame) are the Overview
        // setup affordances. The interactive per-mesh controls also live on the focus
        // tile strip (T3); this roster is the rail's "columns + per-mesh info" view.
        let meshRow (name : string) =
            let isVis  = model.MeshVisible |> AVal.map (fun m -> Map.tryFind name m |> Option.defaultValue true)
            let isSolo = model.MeshSolo |> AVal.map (function Solo(n, _) -> n = name | _ -> false)
            let isRef  = refMesh |> AVal.map ((=) (Some name))
            let idxVal = model.MeshOrder |> AMap.tryFind name |> AVal.map (Option.defaultValue 0)
            let colorVal = idxVal |> AVal.map meshColor
            let hovered = model.Selection.Hovered |> AVal.map (function Some (HoverMesh m) -> m = name | _ -> false)
            let focused = model.Selection.FocusedMesh |> AVal.map ((=) (Some name))
            let swatch = span { Class "mesh-swatch"; colorVal |> AVal.map (fun c -> Some (Style [Css.Background (hex c)])) }
            let num    = span { Class "mesh-num"; idxVal |> AVal.map (fun i -> string (i + 1)) }
            let nameSpan =
                span {
                    Class "rail-mesh-name"
                    Dom.OnClick(fun _ -> env.Emit [SetFocusedMesh (Some name)])
                    model.MeshNames.Content |> AVal.map (fun ns -> friendlyName (IndexList.toList ns) name)
                }
            let refBtn =
                button {
                    Class "mb mb-ref"
                    classWhen "mb-on" isRef
                    Attribute("title", "Reference mesh — all error is relative to it")
                    Dom.OnClick(fun _ ->
                        let cur = AVal.force refMesh
                        env.Emit [SetReferenceMesh (if cur = Some name then None else Some name)])
                    isRef |> AVal.map (fun r -> if r then "★" else "☆")
                }
            let visBtn =
                button {
                    Class "mb"
                    classWhen "mb-on" isVis
                    Attribute("title", "Visible")
                    Dom.OnClick(fun _ -> env.Emit [SetVisible(name, not (AVal.force isVis))])
                    isVis |> AVal.map (fun v -> if v then "●" else "○")
                }
            let soloBtn =
                button {
                    Class "mb"
                    classWhen "mb-on" isSolo
                    Attribute("title", "Isolate this mesh (hide the others); click again to restore")
                    Dom.OnClick(fun _ -> env.Emit [ToggleMeshSolo name])
                    "◐"
                }
            let frameBtn =
                button {
                    Class "mb"
                    Attribute("title", "Frame this mesh")
                    Dom.OnClick(fun _ ->
                        match Map.tryFind name (AVal.force model.MeshBounds) with
                        | Some b -> flyTo (FlyToBounds b)
                        | None -> ())
                    "⌖"
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
                AList.ofList ([ swatch; num; nameSpan; refBtn; visBtn; soloBtn; frameBtn ])
            }
        let overviewBody =
            div { Class "rail-mesh-list"; model.MeshNames |> AList.map meshRow }

        // ── Pin × mesh matrix (§B) — the navigation backbone in Correspondence +
        // Inspect. Rows = pins (glyph · name · pin-colour swatch · ≤5 per-mesh cells);
        // each cell = the (pin, mesh) ROI-median signed distance to the reference,
        // painted on the linear-diverging difference map (§C), before/after aware
        // (the probe refetches on RegView), out-of-ROI → a faint emptiness glyph.
        let pinsVal = model.ScanPins.Pins |> AMap.toAVal
        // (pin, mesh) selection + tight camera sync: locate when a marker exists,
        // else select + focus + fly to the pin centre. Clicking the ALREADY-selected
        // cell toggles it off: clears the selection + isolation (BackOutLocate restores
        // the pre-locate camera / solo / visibility) so the same click un-isolates.
        let selectCell (id : ScanPinId) (mesh : string) =
            let isSelected =
                AVal.force model.Selection.SelectedPin = Some id
                && AVal.force model.Selection.FocusedMesh = Some mesh
            if isSelected then
                env.Emit [BackOutLocate; SetFocusedMesh None; ScanPinMsg (SelectPin None)]
                FocusScene.resetCam (Some mesh)
            else
                let corr = HashMap.tryFind id (AVal.force pinsVal) |> Option.bind ScanPin.correspondence
                match corr |> Option.bind (fun c -> Map.tryFind mesh c.Anchors) with
                | Some a ->
                    let cc = AVal.force model.CommonCentroid
                    let s  = DatasetScale.forMesh (AVal.force model.DatasetScales) mesh
                    let world = (RigidTransform.renderToWorld s cc (AVal.force (MeshView.displayedMeshT model mesh))).Forward.TransformPos a.Point
                    env.Emit [FrameCorrespondence(id, mesh)]
                    FocusScene.focusOnWorld model mesh world 4.0
                | None ->
                    // No marker yet: select the pin + focus the mesh; the reducers fly
                    // both cameras (§T9).
                    env.Emit [ScanPinMsg (SelectPin (Some id)); SetFocusedMesh (Some mesh)]
        let matrixHead () =
            div {
                Class "mx-head"
                div { Class "mx-corner"; "pin \\ mesh" }
                model.MeshNames |> AList.map (fun name ->
                    // Columns show only mesh colour + number (T3); per-mesh controls
                    // and the ★ reference live on the focus tile strip. Clicking a column
                    // focuses the mesh (identical to clicking its focus tile); the column
                    // highlights when that mesh is focused (the reverse link).
                    let idxVal = model.MeshOrder |> AMap.tryFind name |> AVal.map (Option.defaultValue 0)
                    let colFocused = model.Selection.FocusedMesh |> AVal.map ((=) (Some name))
                    div {
                        Class "mx-colhead"
                        classWhen "mx-col-sel" colFocused
                        Attribute("title", name)
                        Dom.OnClick(fun _ -> env.Emit [SetFocusedMesh (Some name)])
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
                div {
                    Class "mx-cell"
                    info |> AVal.map (function CellVal c -> Some (Style [Css.Background (hex c)]) | _ -> None)
                    info |> AVal.map (function CellEmpty -> Some (Class "mx-cell-empty") | CellPending -> Some (Class "mx-cell-pending") | CellVal _ -> None)
                    classWhen "mx-cell-active" active
                    Attribute("title", mesh)
                    Dom.OnClick(fun _ -> selectCell id mesh)
                    Dom.OnPointerMove(fun _ -> env.Emit [SetHovered (Some (HoverPoint(id, mesh)))])
                    Dom.OnMouseLeave(fun _ -> env.Emit [SetHovered None])
                }
            div {
                Class "mx-row"
                classWhen "mx-row-sel" selected
                classWhen "mx-row-hover" pinHover
                div {
                    Class "mx-rowhead"
                    Dom.OnClick(fun _ -> env.Emit [ScanPinMsg (SelectPin (Some id))])
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

        // The reconstruction-readiness hints moved to the top bar (next to before/after);
        // the individual mesh/pin hints were removed (superseded by the matrix).
        let corrBody =
            div {
                Class "rail-step-controls"
                div {
                    Class "rail-pins-head"
                    span { Class "rail-section-title"; "Pins" }
                    button {
                        Class "rail-btn rail-pin-add"
                        classWhen "rail-btn-active" placing
                        Attribute("title", "Place a pin — tap on the reference surface")
                        Dom.OnClick(fun _ ->
                            if AVal.force placing then env.Emit [ScanPinMsg CancelPlacement]
                            else env.Emit [ScanPinMsg EnterAnchorPlacement])
                        placing |> AVal.map (fun p -> if p then "○ placing… (Esc)" else "○ Place pin")
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
