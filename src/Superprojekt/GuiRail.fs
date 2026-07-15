namespace Superprojekt

open Aardvark.Base
open Aardworx.WebAssembly
open FSharp.Data.Adaptive
open Aardvark.Dom
open Microsoft.JSInterop

// ── The slice cell (ScanPin v11 §A) ─────────────────────────────────────────
// ONE cross-section diagram style for every matrix cell: neutral grey ground,
// faint context slices of the cell's own mesh, the reference profile thickened
// by ±LoD₉₅ as a grey band, the mesh's centre profile as a black line with a
// white halo, and the pin-colour centre ring (identity + "centred on a point").
// Every cell shares ONE horizontal window and ONE vertical extent (global
// scales — a parallel gap reads as datum offset, a wedge as tilt, divergence as
// change); profiles leaving the frame are clipped by the SVG viewport and
// marked with an edge arrow. Rendered as a data-slice JSON attribute + one
// OnBoot SVG renderer, so cells stay stable divs that only re-attribute.
module SliceDiagram =

    open Primitives

    type Content = {
        Window   : float        // full horizontal extent (m)
        VExtent  : float        // vertical half-extent (m) about Anchor
        Anchor   : float        // chart v of the reference profile at the centre
        Lod      : float        // reference-band half-width (m)
        RefLines : V2d[][]      // reference centre-plane polylines
        Main     : V2d[][]      // this mesh's centre-plane polylines
        Context  : V2d[][][]    // this mesh's off-centre planes
        PinColor : C4b
    }

    // JSON payload for the boot renderer; (w, h) = the cell's CSS px size.
    let json (w : float) (h : float) (c : Content) : string =
        let halfW = max 1e-6 (c.Window * 0.5)
        let vext = max 1e-6 c.VExtent
        let x (u : float) = (u / (2.0 * halfW) + 0.5) * w
        let y (v : float) = h * 0.5 - (v - c.Anchor) / vext * (h * 0.5)
        let poly (d : System.Text.StringBuilder) (line : V2d[]) =
            for i in 0 .. line.Length - 1 do
                d.Append(if i = 0 then "M" else "L") |> ignore
                d.Append(sprintf "%.1f %.1f" (x line.[i].X) (y line.[i].Y)) |> ignore
        let band =
            c.RefLines |> Array.choose (fun line ->
                if line.Length < 2 then None
                else
                    let d = System.Text.StringBuilder()
                    for i in 0 .. line.Length - 1 do
                        d.Append(if i = 0 then "M" else "L") |> ignore
                        d.Append(sprintf "%.1f %.1f" (x line.[i].X) (y (line.[i].Y + c.Lod))) |> ignore
                    for i in line.Length - 1 .. -1 .. 0 do
                        d.Append(sprintf "L%.1f %.1f" (x line.[i].X) (y (line.[i].Y - c.Lod))) |> ignore
                    d.Append "Z" |> ignore
                    Some (d.ToString()))
        let ctx =
            c.Context |> Array.choose (fun plane ->
                let lines = plane |> Array.filter (fun l -> l.Length >= 2)
                if lines.Length = 0 then None
                else
                    let d = System.Text.StringBuilder()
                    for line in lines do poly d line
                    Some (d.ToString()))
        let mainLines = c.Main |> Array.filter (fun l -> l.Length >= 2)
        let main =
            if mainLines.Length = 0 then ""
            else
                let d = System.Text.StringBuilder()
                for line in mainLines do poly d line
                d.ToString()
        // Off-frame main line → a small arrow at the frame edge (worst exceedance).
        let mutable topX = nan
        let mutable topE = 0.0
        let mutable botX = nan
        let mutable botE = 0.0
        for line in mainLines do
            for p in line do
                if abs p.X <= halfW then
                    let dv = p.Y - c.Anchor
                    if dv > vext && dv - vext > topE then
                        topE <- dv - vext; topX <- x p.X
                    elif dv < -vext && -vext - dv > botE then
                        botE <- -vext - dv; botX <- x p.X
        let arr =
            [ if not (System.Double.IsNaN topX) then sprintf "{\"x\":%.1f,\"t\":1}" (clamp 4.0 (w - 4.0) topX)
              if not (System.Double.IsNaN botX) then sprintf "{\"x\":%.1f,\"t\":0}" (clamp 4.0 (w - 4.0) botX) ]
        sprintf "{\"w\":%.0f,\"h\":%.0f,\"band\":[%s],\"ctx\":[%s],\"main\":\"%s\",\"ring\":\"%s\",\"rx\":%.1f,\"ry\":%.1f,\"arr\":[%s]}"
            w h
            (band |> Array.map (sprintf "\"%s\"") |> String.concat ",")
            (ctx |> Array.map (sprintf "\"%s\"") |> String.concat ",")
            main (c4bToHex c.PinColor)
            (x 0.0) (clamp 3.0 (h - 3.0) (y 0.0))
            (String.concat "," arr)

    // One shared boot for every cell: rebuilds the SVG when data-slice mutates.
    let boot () =
        observedRender "data-slice" "null" [
            "if(!d) return;"
            "var svg = document.createElementNS(ns,'svg');"
            "svg.setAttribute('width', d.w); svg.setAttribute('height', d.h);"
            "svg.setAttribute('viewBox', '0 0 ' + d.w + ' ' + d.h);"
            "function P(dd, stroke, sw, op, fill){"
            "  var p = document.createElementNS(ns,'path');"
            "  p.setAttribute('d', dd); p.setAttribute('fill', fill||'none');"
            "  if(stroke){ p.setAttribute('stroke', stroke); p.setAttribute('stroke-width', sw); }"
            "  if(op) p.setAttribute('opacity', op);"
            "  p.setAttribute('stroke-linejoin','round'); p.setAttribute('stroke-linecap','round');"
            "  svg.appendChild(p);"
            "}"
            "(d.band||[]).forEach(function(b){ P(b, null, 0, null, '#c3cdd9'); });"
            "(d.ctx||[]).forEach(function(cx){ P(cx, '#0f172a', 1, '0.16'); });"
            "if(d.main){ P(d.main, '#ffffff', 3, '0.9'); P(d.main, '#111111', 1.4); }"
            "(d.arr||[]).forEach(function(a){"
            "  var yt = a.t ? 1.5 : d.h - 1.5, yb = a.t ? 6.5 : d.h - 6.5;"
            "  P('M' + (a.x-3) + ' ' + yb + 'L' + (a.x+3) + ' ' + yb + 'L' + a.x + ' ' + yt + 'Z', null, 0, null, '#0f172a');"
            "});"
            "if(d.ring){"
            "  var ci = document.createElementNS(ns,'circle');"
            "  ci.setAttribute('cx', d.rx); ci.setAttribute('cy', d.ry); ci.setAttribute('r', 3);"
            "  ci.setAttribute('fill','none'); ci.setAttribute('stroke', d.ring); ci.setAttribute('stroke-width', 1.5);"
            "  svg.appendChild(ci);"
            "  var dt = document.createElementNS(ns,'circle');"
            "  dt.setAttribute('cx', d.rx); dt.setAttribute('cy', d.ry); dt.setAttribute('r', 1.1);"
            "  dt.setAttribute('fill', '#0f172a');"
            "  svg.appendChild(dt);"
            "}"
            "el.appendChild(svg);"
        ]

// Left rail: three modes (Overview · Correspondence · Inspect), one expanded at
// a time; the container never moves, only the active mode's detail changes.
// Pure view — every control dispatches an existing message and never issues
// server queries itself.
module GuiRail =

    open Primitives

    type private Pill = PillReady | PillWarn | PillBlock | PillInfo

    // Cell state in the pin×mesh matrix: a slice diagram (§A, a SliceDiagram
    // data-slice payload), a faint out-of-ROI emptiness glyph (no marks — the
    // hatch background alone reads as "empty"), or a pending placeholder.
    type private CellInfo = CellPending | CellEmpty | CellVal of string

    // Slice-cell CSS px size (landscape; .mx-cell / .mx-colhead match).
    let private cellW, cellH = 34.0, 26.0

    // Reference centre-plane polylines of one pin's slice + the diagram anchor
    // (reference v at u≈0) + the reference relief (max |v − anchor| within the
    // window) — the §A vertical frame is symmetric about the anchor.
    let private refProfile (refName : string) (s : PinSlice) (halfW : float) =
        let ci = ScanPin.sliceCentreIndex s
        let lines =
            s.Meshes |> Array.tryFind (fun m -> m.MeshName = refName)
            |> Option.map (fun m -> if ci < m.Planes.Length then m.Planes.[ci] else [||])
            |> Option.defaultValue [||]
        let mutable anchor = 0.0
        let mutable bestU = infinity
        for line in lines do
            for p in line do
                if abs p.X < bestU then bestU <- abs p.X; anchor <- p.Y
        let mutable relief = 0.0
        for line in lines do
            for p in line do
                if abs p.X <= halfW then relief <- max relief (abs (p.Y - anchor))
        lines, anchor, relief

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
        // The trailing controls: ★ = the ONE reference picker (the focus tiles only
        // display it), then the per-mesh intrinsic error-visualization switches
        // (Textured / Distance / Shape / Incidence) — respected in both the 3D and
        // the 2D focus views.
        let meshRow (name : string) =
            let isVis  = model.MeshVisible |> AVal.map (fun m -> Map.tryFind name m |> Option.defaultValue true)
            let idxVal = model.MeshOrder |> AMap.tryFind name |> AVal.map (Option.defaultValue 0)
            let colorVal = idxVal |> AVal.map meshColor
            let hovered = model.Selection.Hovered |> AVal.map (function Some (HoverMesh m) -> m = name | _ -> false)
            let focused = model.Selection.Active |> AVal.map (fun s -> Selection.mesh s = Some name)
            let isRef  = refMesh |> AVal.map ((=) (Some name))
            let hm = model.MeshHeatmap |> AVal.map (fun m -> Map.tryFind name m |> Option.defaultValue HeatOff)
            let swatch = span { Class "mesh-swatch"; colorVal |> AVal.map (fun c -> Some (Style [Css.Background (hex c)])) }
            let num    = span { Class "mesh-num"; idxVal |> AVal.map (fun i -> string (i + 1)) }
            let nameSpan =
                span {
                    Class "rail-mesh-name"
                    Dom.OnClick(fun _ -> env.Emit [SetSelection (SelMesh name)])
                    Dom.OnDoubleClick(fun _ -> env.Emit [SetSelection (SelMesh name); ZoomToMesh name])
                    model.MeshNames.Content |> AVal.map (fun ns -> friendlyName (IndexList.toList ns) name)
                }
            let refBtn =
                button {
                    Class "mb mb-ref"
                    classWhen "mb-on" isRef
                    Attribute("title", "Reference mesh — all error is relative to it")
                    Dom.OnClick(fun _ ->
                        let cur = AVal.force isRef
                        env.Emit [SetReferenceMesh (if cur then None else Some name)])
                    isRef |> AVal.map (fun r -> if r then "★" else "☆")
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
                AList.ofList ([ swatch; num; nameSpan; refBtn; modeBar ])
            }
        // Shp quality cutoff (§B3): triangles below it render transparent (3D +
        // focus). Only offered while some mesh shows the Shp heatmap.
        let anyShapeOn =
            model.MeshHeatmap |> AVal.map (Map.exists (fun _ h -> h = HeatShape))
        let overviewBody =
            div {
                Class "rail-mesh-list"
                model.MeshNames |> AList.map meshRow
                div {
                    Class "rail-shape-cut"
                    showWhen anyShapeOn
                    inlineSlider "Shape ≥" 0.0 1.0 0.01 (sprintf "%.2f") model.ShapeThreshold (fun v ->
                        env.Emit [SetShapeThreshold v])
                }
            }

        // ── Pin × mesh matrix (§B) — the navigation backbone in Correspondence +
        // Inspect. Rows = pins (pin-colour name chip · ≤5 per-mesh cells);
        // each cell = the (pin, mesh) cross-section slice diagram (§A): reference
        // ±LoD₉₅ band vs the mesh's profile along the pin's section line,
        // before/after aware (SetRegView swaps the pose-baked slice pair, the reg
        // peek selects the other cache), out-of-ROI → a faint emptiness glyph.
        // Cell SINGLE click = cell selection (the locate: solo + focus framing, no
        // main camera). Clicking the cell of the ACTIVE locate toggles it off:
        // BackOutLocate restores the pre-locate camera / solo / visibility and the
        // selection clears. ClickGate-deferred because of that toggle — a
        // double-click must not toggle twice on the way to its zoom.
        // §A global scales, shared by EVERY slice cell so the diagrams compare:
        // ONE horizontal window (N × coarsest mesh spacing; pin-diameter fallback
        // until spacings land) and ONE vertical half-extent — a robust percentile
        // of (reference relief within the window + |pair median offset|) over all
        // ready (pin, moving-mesh) cells. Committed pose only, so the After/peek
        // flip moves the lines without rescaling the frames.
        let pinsVal = model.ScanPins.Pins |> AMap.toAVal
        let windowVal = (model.SliceNSamples, model.MeshSpacing) ||> AVal.map2 ScanPin.sliceWindow
        let vertExtentVal =
            AVal.custom (fun t ->
                let pins = pinsVal.GetValue t
                let pct = model.SliceVertPercentile.GetValue t
                let win = windowVal.GetValue t
                let es = ResizeArray<float>()
                for (_, p) in HashMap.toSeq pins do
                    match p.Probe, p.Slice with
                    | ProbeReady r, SliceReady s ->
                        let halfW = (win |> Option.defaultValue (p.InnerRadius * 2.0)) * 0.5
                        let _, _, relief = refProfile r.ReferenceMesh s halfW
                        let corr = ScanPin.correspondence p
                        let inRoiOf m = match corr with Some c -> Map.tryFind m c.InRoi |> Option.defaultValue true | None -> true
                        for d in r.Distributions do
                            if d.MeshName <> r.ReferenceMesh && d.Count > 0 && inRoiOf d.MeshName then
                                es.Add(relief + abs d.Median)
                    | _ -> ()
                if es.Count = 0 then 0.05
                else
                    es.Sort()
                    let i = clamp 0 (es.Count - 1) (int (pct * float (es.Count - 1) + 0.5))
                    max 0.005 es.[i])
        let selectCell (id : ScanPinId) (mesh : string) =
            let isLocated =
                (AVal.force model.LocateBackup).IsSome
                && AVal.force model.Selection.Active = SelCell(id, mesh)
            if isLocated then env.Emit [BackOutLocate; SetSelection SelNone]
            else env.Emit [SetSelection (SelCell(id, mesh))]
        // Cell DOUBLE click = cell selection + the MAIN-3D zoom onto the
        // correspondence (the focus hard-zoom follows the selection by itself).
        // Ends in "located + zoomed" regardless of what the leading clicks toggled.
        let zoomCell (id : ScanPinId) (mesh : string) =
            env.Emit (SetSelection (SelCell(id, mesh)) :: FocusScene.cellZoom model id mesh)
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
                    let colFocused = model.Selection.Active |> AVal.map (fun s -> Selection.mesh s = Some name)
                    let colRef = refMesh |> AVal.map ((=) (Some name))
                    div {
                        Class "mx-colhead"
                        // Selected column: the header fills with the MESH accent (§T4) —
                        // the cross itself emerges from dimming, not added ink.
                        classWhen "mx-colhead-sel" colFocused
                        (colFocused, idxVal) ||> AVal.map2 (fun f i ->
                            if f then Some (Style [Css.Background (Primitives.c4bToRgbaCss (meshColor i) 0.4)]) else None)
                        classWhen "mx-col-ref" colRef
                        Attribute("title", name)
                        Dom.OnClick(fun _ -> env.Emit [SetSelection (SelMesh name)])
                        Dom.OnDoubleClick(fun _ -> env.Emit [SetSelection (SelMesh name); ZoomToMesh name])
                        Dom.OnPointerMove(fun _ -> env.Emit [SetHovered (Some (HoverMesh name))])
                        Dom.OnMouseLeave(fun _ -> env.Emit [SetHovered None])
                        span { Class "mx-colsw"; idxVal |> AVal.map (fun i -> Some (Style [Css.Background (hex (meshColor i))])) }
                        span { Class "mx-colnum"; idxVal |> AVal.map (fun i -> string (i + 1)) }
                    })
            }
        let matrixRow (id : ScanPinId) (shortNm : string) (pinColor : C4b) =
            let selected = model.Selection.Active |> AVal.map (fun s -> Selection.pin s = Some id)
            let pinHover = model.Selection.Hovered |> AVal.map (function Some (HoverPin i) -> i = id | _ -> false)
            // The slice-cell inputs of this row, projected to just what the cells
            // read so ring/placement churn doesn't touch them; cells are stable
            // divs that only re-attribute their data-slice payload.
            let rowData =
                model.ScanPins.Pins |> AMap.tryFind id
                |> AVal.map (Option.map (fun p ->
                    p.Probe, p.Slice, p.SliceOther, p.Correspondence, p.InnerRadius, p.PinColor))
            let cell (mesh : string) =
                // §A cell payload: reference band ± LoD₉₅ (from the committed probe,
                // same pair statistic the residual fill used) + this mesh's centre
                // profile and context slices from the peek-selected slice cache.
                let info =
                    AVal.custom (fun t ->
                        match rowData.GetValue t with
                        | Some (ProbeReady r, slice, sliceOther, corr, innerRadius, pinCol) ->
                            let chosen =
                                match (if model.RegPeekHeld.GetValue t then sliceOther else slice), slice with
                                | SliceReady s, _ | _, SliceReady s -> Some s
                                | _ -> None
                            match chosen with
                            | Some s ->
                                let inRoi = match corr with Some c -> Map.tryFind mesh c.InRoi |> Option.defaultValue true | None -> true
                                let dist = r.Distributions |> Array.tryFind (fun d -> d.MeshName = mesh)
                                let count = dist |> Option.map (fun d -> d.Count) |> Option.defaultValue 0
                                let ci = ScanPin.sliceCentreIndex s
                                let sm = s.Meshes |> Array.tryFind (fun m -> m.MeshName = mesh)
                                let mainLines =
                                    sm |> Option.map (fun m -> if ci < m.Planes.Length then m.Planes.[ci] else [||])
                                    |> Option.defaultValue [||]
                                if not inRoi || count <= 0 || mainLines |> Array.forall (fun l -> l.Length < 2) then
                                    CellEmpty
                                else
                                    let win = windowVal.GetValue t |> Option.defaultValue (innerRadius * 2.0)
                                    let refLines, anchor, _ = refProfile r.ReferenceMesh s (win * 0.5)
                                    let refStd =
                                        r.Distributions |> Array.tryFind (fun d -> d.MeshName = r.ReferenceMesh)
                                        |> Option.map (fun d -> d.Std) |> Option.defaultValue 0.0
                                    let std = dist |> Option.map (fun d -> d.Std) |> Option.defaultValue 0.0
                                    let ctx =
                                        sm |> Option.map (fun m -> m.Planes |> Array.mapi (fun k pl -> if k = ci then [||] else pl))
                                        |> Option.defaultValue [||]
                                    CellVal (SliceDiagram.json cellW cellH {
                                        Window   = win
                                        VExtent  = vertExtentVal.GetValue t
                                        Anchor   = anchor
                                        Lod      = 1.96 * sqrt (refStd * refStd + std * std)
                                        RefLines = refLines
                                        Main     = mainLines
                                        Context  = ctx
                                        PinColor = pinCol })
                            | None -> CellPending
                        | _ -> CellPending)
                let active =
                    (selected, model.Selection.Hovered) ||> AVal.map2 (fun sel hov ->
                        (sel && (match hov with Some (HoverPoint(i,m)) -> i = id && m = mesh | _ -> false)))
                let cellSel = model.Selection.Active |> AVal.map ((=) (SelCell(id, mesh)))
                let cellRef = refMesh |> AVal.map ((=) (Some mesh))
                // Selection cross by DE-emphasis (§T4): with a selection active,
                // every cell outside the selected row/column dims — the cross
                // emerges by contrast, adding no strokes over the diagrams.
                let dimmed =
                    model.Selection.Active |> AVal.map (fun s ->
                        match s with
                        | SelNone -> false
                        | SelMesh m -> m <> mesh
                        | SelPin p -> p <> id
                        | SelCell (p, m) -> p <> id && m <> mesh)
                div {
                    Class "mx-cell"
                    info |> AVal.map (fun i -> Some (Attribute("data-slice", match i with CellVal j -> j | _ -> "null")))
                    info |> AVal.map (function CellEmpty -> Some (Class "mx-cell-empty") | CellPending -> Some (Class "mx-cell-pending") | CellVal _ -> None)
                    SliceDiagram.boot ()
                    classWhen "mx-cell-dim" dimmed
                    classWhen "mx-cell-sel" cellSel
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
                classWhen "mx-row-hover" pinHover
                div {
                    Class "mx-rowhead"
                    // Selected row: the header fills with the PIN accent (§T4).
                    classWhen "mx-rowhead-sel" selected
                    selected |> AVal.map (fun s ->
                        if s then Some (Style [Css.Background (Primitives.c4bToRgbaCss pinColor 0.4)]) else None)
                    // Pin linking: click = select (the reducer applies the Inspect
                    // isolation swap, the focus frames the pin by itself);
                    // double-click adds the MAIN-3D zoom.
                    Dom.OnClick(fun _ -> env.Emit [SetSelection (SelPin id)])
                    Dom.OnDoubleClick(fun _ -> env.Emit [SetSelection (SelPin id); ZoomToPin id])
                    Dom.OnPointerMove(fun _ -> env.Emit [SetHovered (Some (HoverPin id))])
                    Dom.OnMouseLeave(fun _ -> env.Emit [SetHovered None])
                    span { Class "mx-pinname"; Style [Css.Background (hex pinColor)]; shortNm }
                    button {
                        Class "mb mx-del"
                        Attribute("title", "Delete pin")
                        // Native confirmation dialog before the destructive delete.
                        Dom.OnClick(fun _ ->
                            let ok = try JSRuntime.Instance.Invoke<bool>("confirm", sprintf "Delete pin %s? This cannot be undone." shortNm) with _ -> false
                            if ok then env.Emit [ScanPinMsg (DeletePin id)])
                        "✕"
                    }
                }
                model.MeshNames |> AList.map cell
            }
        // Stable-identity row list, sorted by creation order; project to identity
        // only (name/colour/created) so probe/ring updates don't re-key a row.
        let matrixRows () =
            model.ScanPins.Pins
            |> AMap.map (fun _ p -> p.ShortName, p.PinColor, p.CreatedAt)
            |> AMap.toASet
            |> ASet.sortBy (fun (ScanPinId.ScanPinId g, (_, _, created)) -> created, g)
            |> AList.map (fun (id, (shortNm, pinColor, _)) -> matrixRow id shortNm pinColor)
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
