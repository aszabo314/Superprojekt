namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open FSharp.Data.Adaptive
open Aardvark.Dom

module Cards =

    let shortName (name : string) =
        let mesh =
            let s = name.IndexOf('/')
            if s >= 0 then name.[s + 1 ..] else name
        if mesh.Length > 8 && mesh.[8] = '_' then
            let date = mesh.[..7]
            let si = mesh.LastIndexOf("_seg")
            if si > 0 then date + "_" + mesh.[si + 1 ..] else date
        else mesh

    let c4bToHex (c : C4b) =
        sprintf "#%02x%02x%02x" c.R c.G c.B

    let parseFloat (s : string) =
        match System.Double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture) with
        | true, v -> Some v
        | _ -> None

    let checkedIf (v : aval<bool>) =
        v |> AVal.map (fun on -> if on then Some (Attribute("checked", "checked")) else None)

    /// Nice-number tick generator (same ladder as the scale bar).
    let niceTicks (lo : float) (hi : float) (targetCount : int) : float[] * float =
        let range = hi - lo
        if range <= 1e-12 || targetCount < 1 then [||], 1.0
        else
            let rough = range / float targetCount
            let mag = 10.0 ** floor (log10 rough)
            let norm = rough / mag
            let nice =
                if norm < 1.5 then 1.0
                elif norm < 3.0 then 2.0
                elif norm < 7.0 then 5.0
                else 10.0
            let step = nice * mag
            let start = ceil (lo / step) * step
            let ticks = ResizeArray<float>()
            let mutable v = start
            while v <= hi + 0.5 * step do
                if v >= lo - 1e-9 && v <= hi + 1e-9 then ticks.Add v
                v <- v + step
            ticks.ToArray(), step

    /// Every Nth tick is "major" (gets a label). Pick N so we label ≤ ~6 ticks.
    let private majorEvery (count : int) =
        if count <= 6 then 1
        elif count <= 12 then 2
        else 5

    let private fmtTick (v : float) (step : float) =
        if step >= 1.0 then sprintf "%g" (round v)
        elif step >= 0.1 then sprintf "%.1f" v
        elif step >= 0.01 then sprintf "%.2f" v
        else sprintf "%.3f" v

    let encodeDiagramJson (pin : ScanPin) (svgW : float) (svgH : float) (pad : float) =
        let results = pin.CutResults |> Map.toList
        if results.IsEmpty then "{\"paths\":[]}"
        else
            let allPts = results |> List.collect (fun (_, cr) -> cr.Polylines |> List.collect id)
            let xs = allPts |> List.map (fun (p : V2d) -> p.X)
            let ys = allPts |> List.map (fun (p : V2d) -> p.Y)
            let xMin = xs |> List.min
            let xMax = xs |> List.max
            let yMin = ys |> List.min
            let yMax = ys |> List.max
            let xRange = if xMax - xMin < 1e-9 then 1.0 else xMax - xMin
            let yRange = if yMax - yMin < 1e-9 then 1.0 else yMax - yMin
            let plotW = svgW - 2.0 * pad
            let plotH = svgH - 2.0 * pad
            // Aspect handling: compute per-axis scale (px / world unit).
            let scaleX, scaleY, originX, originY =
                match pin.CutAspect with
                | CutAspectOneToOne ->
                    let s = min (plotW / xRange) (plotH / yRange)
                    let drawW = s * xRange
                    let drawH = s * yRange
                    let ox = pad + (plotW - drawW) * 0.5
                    let oy = (svgH - pad) - (plotH - drawH) * 0.5
                    s, s, ox, oy
                | CutAspectFit ->
                    plotW / xRange, plotH / yRange, pad, svgH - pad
            let toSvgX x = originX + (x - xMin) * scaleX
            let toSvgY y = originY - (y - yMin) * scaleY
            let paths =
                results |> List.collect (fun (name, cr) ->
                    let color = pin.DatasetColors |> Map.tryFind name |> Option.defaultValue (C4b(100uy,100uy,100uy)) |> c4bToHex
                    cr.Polylines |> List.map (fun pts ->
                        let d = pts |> List.mapi (fun i (p : V2d) ->
                            let cmd = if i = 0 then "M" else "L"
                            sprintf "%s%.1f,%.1f" cmd (toSvgX p.X) (toSvgY p.Y)) |> String.concat " "
                        sprintf "{\"n\":\"%s\",\"d\":\"%s\",\"c\":\"%s\"}" (shortName name) (d.Replace("\"","\\\"")) color))
            // Axis ticks.
            let xTicks, xStep = niceTicks xMin xMax 6
            let yTicks, yStep = niceTicks yMin yMax 4
            let xMajor = majorEvery xTicks.Length
            let yMajor = majorEvery yTicks.Length
            let encodeTick (values : float[]) (major : int) (step : float) (toPx : float -> float) (labelOffset : float -> float -> string) =
                values |> Array.mapi (fun i v ->
                    let isMaj = i % major = 0
                    let lbl = if isMaj then fmtTick v step else ""
                    sprintf "{\"p\":%.1f,\"m\":%b,\"l\":\"%s\"}" (toPx v) isMaj lbl)
                |> String.concat ","
            let xTicksJson = encodeTick xTicks xMajor xStep toSvgX (fun _ _ -> "")
            let yTicksJson = encodeTick yTicks yMajor yStep toSvgY (fun _ _ -> "")
            // Hover marker if present and snap target resolves.
            let hoverJson =
                match pin.CutLineHover with
                | Some hv ->
                    let px = toSvgX hv.DiagramPos.X
                    let py = toSvgY hv.DiagramPos.Y
                    let col =
                        pin.DatasetColors |> Map.tryFind hv.MeshName
                        |> Option.defaultValue (C4b(100uy,100uy,100uy)) |> c4bToHex
                    sprintf "{\"x\":%.1f,\"y\":%.1f,\"c\":\"%s\",\"n\":\"%s\",\"u\":%.2f,\"v\":%.2f}"
                        px py col (shortName hv.MeshName) hv.CutDistance hv.Elevation
                | None -> "null"
            // Plot rectangle for JS hit testing.
            sprintf "{\"paths\":[%s],\"xt\":[%s],\"yt\":[%s],\"xMin\":%.6g,\"xMax\":%.6g,\"yMin\":%.6g,\"yMax\":%.6g,\"ox\":%.2f,\"oy\":%.2f,\"sx\":%.6g,\"sy\":%.6g,\"pw\":%.1f,\"ph\":%.1f,\"pad\":%.1f,\"w\":%.1f,\"h\":%.1f,\"hover\":%s}"
                (paths |> String.concat ",") xTicksJson yTicksJson
                xMin xMax yMin yMax originX originY scaleX scaleY plotW plotH pad svgW svgH hoverJson

    let private projectToScreen (anchor : V3d) (viewTrafo : Trafo3d) (vpSize : V2i) =
        let aspect = float vpSize.X / max 1.0 (float vpSize.Y)
        let proj = Frustum.perspective 90.0 1.0 5000.0 aspect |> Frustum.projTrafo
        let m = proj.Forward * viewTrafo.Forward
        let h = m * V4d(anchor, 1.0)
        if h.W < 0.1 then None
        else
            let ndc = h.XYZ / h.W
            if abs ndc.X > 2.0 || abs ndc.Y > 2.0 then None
            else
                let px = (ndc.X * 0.5 + 0.5) * float vpSize.X
                let py = (1.0 - (ndc.Y * 0.5 + 0.5)) * float vpSize.Y
                Some (V2d(px, py))

    let private clampToViewport (pos : V2d) (size : V2d) (vp : V2d) =
        let x = max 0.0 (min pos.X (vp.X - size.X))
        let y = max 0.0 (min pos.Y (vp.Y - size.Y))
        V2d(x, y)

    let private computeCardPos
        (card : Card)
        (viewTrafo : Trafo3d)
        (vpSize : V2i)
        : V2d option =
        match card.Attachment with
        | CardDragging(pos, _) -> Some pos
        | CardDetached pos -> Some pos
        | CardAttached ->
            match card.Anchor with
            | AnchorToWorldPoint anchor ->
                match projectToScreen anchor viewTrafo vpSize with
                | Some screenPt ->
                    let pos = V2d(screenPt.X + card.Size.X * 0.4, screenPt.Y - card.Size.Y * 0.5 - 40.0)
                    Some (clampToViewport pos card.Size (V2d vpSize))
                | None -> None

    let private diagramToWorld (pin : ScanPin) (diag : V2d) : V3d =
        let axis = pin.Prism.AxisDirection |> Vec.normalize
        let right, fwd = PinGeometry.axisFrame axis
        let anchor = pin.Prism.AnchorPoint
        match pin.CutPlane with
        | CutPlaneMode.AlongAxis angleDeg ->
            let a = angleDeg * Constant.RadiansPerDegree
            let dir = right * cos a + fwd * sin a
            anchor + dir * diag.X + axis * diag.Y
        | CutPlaneMode.AcrossAxis dist ->
            anchor + axis * dist + right * diag.X + fwd * diag.Y

    let private computeCutSnap (pin : ScanPin) (svgW : float) (svgH : float) (pad : float) (mousePx : V2d) : CutLineHover option =
        let results = pin.CutResults |> Map.toList
        if results.IsEmpty then None
        else
            let allPts = results |> List.collect (fun (_, cr) -> cr.Polylines |> List.collect id)
            if List.isEmpty allPts then None
            else
            let xs = allPts |> List.map (fun (p : V2d) -> p.X)
            let ys = allPts |> List.map (fun (p : V2d) -> p.Y)
            let xMin = xs |> List.min
            let xMax = xs |> List.max
            let yMin = ys |> List.min
            let yMax = ys |> List.max
            let xRange = if xMax - xMin < 1e-9 then 1.0 else xMax - xMin
            let yRange = if yMax - yMin < 1e-9 then 1.0 else yMax - yMin
            let plotW = svgW - 2.0 * pad
            let plotH = svgH - 2.0 * pad
            let scaleX, scaleY, originX, originY =
                match pin.CutAspect with
                | CutAspectOneToOne ->
                    let s = min (plotW / xRange) (plotH / yRange)
                    let drawW = s * xRange
                    let drawH = s * yRange
                    let ox = pad + (plotW - drawW) * 0.5
                    let oy = (svgH - pad) - (plotH - drawH) * 0.5
                    s, s, ox, oy
                | CutAspectFit ->
                    plotW / xRange, plotH / yRange, pad, svgH - pad
            let toPx (p : V2d) = V2d(originX + (p.X - xMin) * scaleX, originY - (p.Y - yMin) * scaleY)
            let thresholdSq = 100.0
            let mutable best : (float * string * V2d) option = None
            for (name, cr) in results do
                for poly in cr.Polylines do
                    let arr = poly |> List.toArray
                    for i in 0 .. arr.Length - 2 do
                        let a = toPx arr.[i]
                        let b = toPx arr.[i + 1]
                        let ab = b - a
                        let len2 = Vec.dot ab ab
                        let t =
                            if len2 < 1e-9 then 0.0
                            else clamp 0.0 1.0 (Vec.dot (mousePx - a) ab / len2)
                        let proj = a + ab * t
                        let d2 = Vec.lengthSquared (mousePx - proj)
                        if d2 <= thresholdSq then
                            let diag = arr.[i] + (arr.[i + 1] - arr.[i]) * t
                            match best with
                            | None -> best <- Some (d2, name, diag)
                            | Some (bd, _, _) when d2 < bd -> best <- Some (d2, name, diag)
                            | _ -> ()
            match best with
            | Some (_, name, diag) ->
                Some {
                    MeshName = name
                    DiagramPos = diag
                    WorldPos = diagramToWorld pin diag
                    CutDistance = diag.X
                    Elevation = diag.Y
                }
            | None -> None

    let private diagramSvg (env : Env<Message>) (selectedPin : aval<ScanPin option>) =
        let svgW, svgH = 280.0, 130.0
        let pad = 28.0
        div {
            Class "card-cut-diagram"
            selectedPin |> AVal.map (fun po ->
                let json =
                    match po with
                    | Some pin -> encodeDiagramJson pin svgW svgH pad
                    | None -> "{\"paths\":[]}"
                Some (Attribute("data-diagram", json)))

            Dom.OnPointerMove(fun e ->
                match AVal.force selectedPin with
                | Some pin when not pin.CutResults.IsEmpty ->
                    let mpx = V2d(float e.OffsetPosition.X, float e.OffsetPosition.Y)
                    let hv = computeCutSnap pin svgW svgH pad mpx
                    if hv <> pin.CutLineHover then
                        env.Emit [ScanPinMsg (SetCutLineHover(pin.Id, hv))]
                | _ -> ())

            Dom.OnMouseLeave(fun _ ->
                match AVal.force selectedPin with
                | Some pin when pin.CutLineHover.IsSome ->
                    env.Emit [ScanPinMsg (SetCutLineHover(pin.Id, None))]
                | _ -> ())

            OnBoot [
                "var el = __THIS__;"
                "var last = '';"
                "var ns = 'http://www.w3.org/2000/svg';"
                "function render() {"
                "  var raw = el.getAttribute('data-diagram') || '{}';"
                "  if(raw === last) return;"
                "  last = raw;"
                "  try { var data = JSON.parse(raw); } catch(e) { var data = {}; }"
                sprintf "  var W=%.0f, H=%.0f, pad=%.0f;" svgW svgH pad
                "  el.innerHTML = '';"
                "  var svg = document.createElementNS(ns, 'svg');"
                "  svg.setAttribute('width', W);"
                "  svg.setAttribute('height', H);"
                "  svg.setAttribute('viewBox', '0 0 ' + W + ' ' + H);"
                "  svg.style.background = '#fafbfc'; svg.style.display = 'block';"
                "  el.appendChild(svg);"
                "  if(!data.paths || data.paths.length === 0) {"
                "    var msg = document.createElementNS(ns, 'text');"
                "    msg.setAttribute('x', W*0.5); msg.setAttribute('y', H*0.5);"
                "    msg.setAttribute('text-anchor','middle'); msg.setAttribute('fill','#94a3b8');"
                "    msg.setAttribute('font-size','10');"
                "    msg.textContent = 'Awaiting cut\u2026';"
                "    svg.appendChild(msg); return;"
                "  }"
                "  var ax = document.createElementNS(ns, 'line');"
                "  ax.setAttribute('x1', pad); ax.setAttribute('y1', H-pad);"
                "  ax.setAttribute('x2', W-pad); ax.setAttribute('y2', H-pad);"
                "  ax.setAttribute('stroke','#94a3b8'); ax.setAttribute('stroke-width','1');"
                "  svg.appendChild(ax);"
                "  var ay = document.createElementNS(ns, 'line');"
                "  ay.setAttribute('x1', pad); ay.setAttribute('y1', pad);"
                "  ay.setAttribute('x2', pad); ay.setAttribute('y2', H-pad);"
                "  ay.setAttribute('stroke','#94a3b8'); ay.setAttribute('stroke-width','1');"
                "  svg.appendChild(ay);"
                "  (data.xt || []).forEach(function(t) {"
                "    var tk = document.createElementNS(ns, 'line');"
                "    tk.setAttribute('x1', t.p); tk.setAttribute('y1', H-pad);"
                "    tk.setAttribute('x2', t.p); tk.setAttribute('y2', H-pad-3);"
                "    tk.setAttribute('stroke','#64748b'); tk.setAttribute('stroke-width','1');"
                "    svg.appendChild(tk);"
                "    if(t.m && t.l) {"
                "      var lb = document.createElementNS(ns, 'text');"
                "      lb.setAttribute('x', t.p); lb.setAttribute('y', H-pad+10);"
                "      lb.setAttribute('text-anchor','middle'); lb.setAttribute('fill','#475569');"
                "      lb.setAttribute('font-size','9'); lb.setAttribute('font-family','SF Mono, Monaco, monospace');"
                "      lb.textContent = t.l; svg.appendChild(lb);"
                "    }"
                "  });"
                "  (data.yt || []).forEach(function(t) {"
                "    var tk = document.createElementNS(ns, 'line');"
                "    tk.setAttribute('x1', pad); tk.setAttribute('y1', t.p);"
                "    tk.setAttribute('x2', pad+3); tk.setAttribute('y2', t.p);"
                "    tk.setAttribute('stroke','#64748b'); tk.setAttribute('stroke-width','1');"
                "    svg.appendChild(tk);"
                "    if(t.m && t.l) {"
                "      var lb = document.createElementNS(ns, 'text');"
                "      lb.setAttribute('x', pad-4); lb.setAttribute('y', t.p+3);"
                "      lb.setAttribute('text-anchor','end'); lb.setAttribute('fill','#475569');"
                "      lb.setAttribute('font-size','9'); lb.setAttribute('font-family','SF Mono, Monaco, monospace');"
                "      lb.textContent = t.l; svg.appendChild(lb);"
                "    }"
                "  });"
                "  var xu = document.createElementNS(ns, 'text');"
                "  xu.setAttribute('x', W-pad+2); xu.setAttribute('y', H-pad+10);"
                "  xu.setAttribute('fill','#64748b'); xu.setAttribute('font-size','9');"
                "  xu.setAttribute('font-family','SF Mono, Monaco, monospace');"
                "  xu.textContent='[m]'; svg.appendChild(xu);"
                "  var yu = document.createElementNS(ns, 'text');"
                "  yu.setAttribute('x', pad-4); yu.setAttribute('y', pad-4);"
                "  yu.setAttribute('text-anchor','end'); yu.setAttribute('fill','#64748b');"
                "  yu.setAttribute('font-size','9'); yu.setAttribute('font-family','SF Mono, Monaco, monospace');"
                "  yu.textContent='[m]'; svg.appendChild(yu);"
                "  data.paths.forEach(function(item) {"
                "    var p = document.createElementNS(ns, 'path');"
                "    p.setAttribute('d', item.d);"
                "    p.setAttribute('stroke', item.c);"
                "    p.setAttribute('stroke-width', '2');"
                "    p.setAttribute('fill', 'none');"
                "    svg.appendChild(p);"
                "  });"
                "  if(data.hover) {"
                "    var h = data.hover;"
                "    var cr = document.createElementNS(ns, 'line');"
                "    cr.setAttribute('x1', h.x-8); cr.setAttribute('y1', h.y);"
                "    cr.setAttribute('x2', h.x+8); cr.setAttribute('y2', h.y);"
                "    cr.setAttribute('stroke', '#0f172a'); cr.setAttribute('stroke-width','1');"
                "    svg.appendChild(cr);"
                "    var cv = document.createElementNS(ns, 'line');"
                "    cv.setAttribute('x1', h.x); cv.setAttribute('y1', h.y-8);"
                "    cv.setAttribute('x2', h.x); cv.setAttribute('y2', h.y+8);"
                "    cv.setAttribute('stroke', '#0f172a'); cv.setAttribute('stroke-width','1');"
                "    svg.appendChild(cv);"
                "    var dot = document.createElementNS(ns, 'circle');"
                "    dot.setAttribute('cx', h.x); dot.setAttribute('cy', h.y);"
                "    dot.setAttribute('r', 4); dot.setAttribute('fill', h.c);"
                "    dot.setAttribute('stroke','#ffffff'); dot.setAttribute('stroke-width','1');"
                "    svg.appendChild(dot);"
                "    var tipY = (h.y < 20) ? h.y+16 : h.y-8;"
                "    var tipX = Math.max(pad+2, Math.min(W-pad-2, h.x+10));"
                "    var anchor = (h.x > W-pad-40) ? 'end' : 'start';"
                "    var tx = document.createElementNS(ns, 'text');"
                "    tx.setAttribute('x', tipX); tx.setAttribute('y', tipY);"
                "    tx.setAttribute('text-anchor', anchor); tx.setAttribute('fill','#0f172a');"
                "    tx.setAttribute('font-size','9'); tx.setAttribute('font-family','SF Mono, Monaco, monospace');"
                "    tx.textContent = h.n + ': ' + h.u.toFixed(1) + 'm, ' + h.v.toFixed(1) + 'm';"
                "    svg.appendChild(tx);"
                "  }"
                "}"
                "render();"
                "new MutationObserver(function(){render();}).observe(el, {attributes:true,attributeFilter:['data-diagram']});"
            ]
        }

    let private pinCardBody (env : Env<Message>) (model : AdaptiveModel) (selectedPin : aval<ScanPin option>) =
        div {
            Class "pin-card-body"

            diagramSvg env selectedPin

            div {
                Class "pin-card-strat"
                let isPlacing = (model.ScanPins.PlacingMode, model.ScanPins.ActivePlacement) ||> AVal.map2 (fun pm ap -> pm.IsSome || ap.IsSome)
                StratigraphyView.render env isPlacing selectedPin
                div {
                    Class "strat-hover-tip"
                    selectedPin |> AVal.map (fun po ->
                        match po |> Option.bind (fun p ->
                            p.Stratigraphy |> Option.bind (fun data ->
                                p.BetweenSpaceHover |> Option.bind (fun h ->
                                    if h.ColumnIdx < 0 || h.ColumnIdx >= data.Columns.Length then None
                                    else
                                        Stratigraphy.tryBracket data.Columns.[h.ColumnIdx].Events h.HoverZ
                                        |> Option.map (fun (zLo, zHi, lo, up) -> h, zLo, zHi, lo, up)))) with
                        | Some (h, zLo, zHi, lo, up) ->
                            let gap = zHi - zLo
                            let pinTag = if h.Pinned then " · pinned" else ""
                            sprintf "%s \u2194 %s · %.2fm%s" (shortName lo) (shortName up) gap pinTag
                        | None -> "")
                }
            }

            div {
                Class "pin-card-inline-controls"

                let oneToOne = selectedPin |> AVal.map (fun po -> po |> Option.map (fun p -> p.CutAspect = CutAspectOneToOne) |> Option.defaultValue false)
                div {
                    Attribute("title", "Cut aspect: 1:1 (slopes match 3D) / Fit")
                    Primitives.compactButtonBar [
                        "1:1",
                        oneToOne,
                        (fun () ->
                            match AVal.force selectedPin with
                            | Some p -> env.Emit [ScanPinMsg (SetCutAspect(p.Id, CutAspectOneToOne))]
                            | None -> ())
                        "Fit",
                        (oneToOne |> AVal.map not),
                        (fun () ->
                            match AVal.force selectedPin with
                            | Some p -> env.Emit [ScanPinMsg (SetCutAspect(p.Id, CutAspectFit))]
                            | None -> ())
                    ]
                }

                let normalized = selectedPin |> AVal.map (fun po -> po |> Option.map (fun p -> p.StratigraphyDisplay = Normalized) |> Option.defaultValue false)
                div {
                    Attribute("title", "Profile: Flat / Normalized")
                    Primitives.compactButtonBar [
                        "Flat",
                        (normalized |> AVal.map not),
                        (fun () ->
                            match AVal.force selectedPin with
                            | Some p -> env.Emit [ScanPinMsg (SetStratigraphyDisplay(p.Id, Undistorted))]
                            | None -> ())
                        "Norm",
                        normalized,
                        (fun () ->
                            match AVal.force selectedPin with
                            | Some p -> env.Emit [ScanPinMsg (SetStratigraphyDisplay(p.Id, Normalized))]
                            | None -> ())
                    ]
                }

                div {
                    Attribute("title", "Between-space highlighting")
                    Primitives.compactToggle "Gap" model.ScanPins.BetweenSpaceEnabled (fun () ->
                        env.Emit [ScanPinMsg ToggleBetweenSpaceEnabled])
                }

                let isolate = selectedPin |> AVal.map (fun po ->
                    match po with Some p -> p.GhostClip = GhostClipOn | None -> false)
                div {
                    Attribute("title", "Isolate: ghost-clip meshes outside the pin cylinder")
                    Primitives.compactToggle "Solo" isolate (fun () ->
                        match AVal.force selectedPin with
                        | Some p ->
                            let next = if p.GhostClip = GhostClipOn then GhostClipOff else GhostClipOn
                            env.Emit [ScanPinMsg (SetGhostClip(p.Id, next))]
                        | None -> ())
                }

                let ghostCut = selectedPin |> AVal.map (fun po ->
                    match po with Some p -> p.GhostClipCutPlane | None -> false)
                div {
                    Class "ct-gated"
                    isolate |> AVal.map (fun g -> if g then None else Some (Class "ct-disabled"))
                    Attribute("title", "Also clip in front of the cut plane")
                    Primitives.compactToggle "Cut" ghostCut (fun () ->
                        match AVal.force selectedPin with
                        | Some p when p.GhostClip = GhostClipOn ->
                            env.Emit [ScanPinMsg (SetGhostClipCutPlane(p.Id, not p.GhostClipCutPlane))]
                        | _ -> ())
                }
            }
        }

    let renderCards (env : Env<Message>) (model : AdaptiveModel) (viewTrafo : aval<Trafo3d>) (vpSize : aval<V2i>) =
        let allPinsVal = model.ScanPins.Pins |> AMap.toAVal
        let selectedPin =
            (model.ScanPins.SelectedPin, model.ScanPins.ActivePlacement, allPinsVal)
            |||> AVal.map3 (fun sel act pins ->
                let id = act |> Option.orElse sel
                id |> Option.bind (fun id -> HashMap.tryFind id pins))

        let cardsSnapshot = model.CardSystem.Cards |> AMap.toAVal

        let dragState = cval<(CardId * V2d * V2d) option> None

        let collapsedSet = cval (HashSet.empty<CardId>)

        let cardPositions =
            (cardsSnapshot, viewTrafo, vpSize)
            |||> AVal.map3 (fun cards vt sz ->
                let dict = System.Collections.Generic.Dictionary<CardId, V2d>()
                for (id, card) in HashMap.toSeq cards do
                    if card.Visible then
                        match computeCardPos card vt sz with
                        | Some pos -> dict.[id] <- pos
                        | None -> ()
                dict)

        let effectivePositions =
            (cardPositions, dragState :> aval<_>)
            ||> AVal.map2 (fun baseDict drag ->
                match drag with
                | None -> baseDict
                | Some (dragId, dragPos, _) ->
                    let dict = System.Collections.Generic.Dictionary<CardId, V2d>(baseDict)
                    dict.[dragId] <- dragPos
                    dict)

        div {
            Class "card-overlay"

            cardsSnapshot
            |> AVal.map (fun cards ->
                cards |> HashMap.toSeq
                |> Seq.filter (fun (_, c) -> match c.Content with StratigraphyDiagram _ -> true | _ -> false)
                |> Seq.sortBy (fun (_, c) -> c.ZOrder)
                |> Seq.map fst
                |> IndexList.ofSeq)
            |> AList.ofAVal
            |> AList.map (fun cardId ->
                let cardVal = cardsSnapshot |> AVal.map (fun cards -> HashMap.tryFind cardId cards)
                let effectivePos = effectivePositions |> AVal.map (fun dict ->
                    match dict.TryGetValue(cardId) with
                    | true, pos -> Some pos
                    | _ -> None)

                let isCollapsed =
                    (collapsedSet :> aval<_>) |> AVal.map (fun s -> HashSet.contains cardId s)

                div {
                    Class "card pin-card"
                    (cardVal, effectivePos) ||> AVal.map2 (fun cOpt pOpt ->
                        match cOpt, pOpt with
                        | Some card, Some pos when card.Visible ->
                            Some (Style [
                                Left (sprintf "%.0fpx" pos.X)
                                Top (sprintf "%.0fpx" pos.Y)
                                Width (sprintf "%.0fpx" card.Size.X)
                                Css.Visibility "visible"
                            ])
                        | _ ->
                            Some (Style [Display "none"]))

                    div {
                        Class "card-titlebar"

                        let isDetached = cardVal |> AVal.map (fun cOpt ->
                            match cOpt with
                            | Some c -> match c.Attachment with CardDetached _ -> true | _ -> false
                            | None -> false)

                        div {
                            Class "card-drag-handle"
                            Dom.OnPointerDown((fun e ->
                                if e.Button = Button.Left then
                                    let cardPos =
                                        match AVal.force effectivePos with
                                        | Some p -> p
                                        | None -> V2d.Zero
                                    let grabOffset = V2d(float e.ClientPosition.X, float e.ClientPosition.Y) - cardPos
                                    transact (fun () -> dragState.Value <- Some (cardId, cardPos, grabOffset))
                            ), pointerCapture = true)
                            Dom.OnPointerMove(fun e ->
                                match dragState.GetValue() with
                                | Some (id, _, offset) when id = cardId ->
                                    let newPos = V2d(float e.ClientPosition.X, float e.ClientPosition.Y) - offset
                                    transact (fun () -> dragState.Value <- Some (id, newPos, offset))
                                | _ -> ())
                            Dom.OnPointerUp((fun _ ->
                                match dragState.GetValue() with
                                | Some (id, pos, _) when id = cardId ->
                                    transact (fun () -> dragState.Value <- None)
                                    env.Emit [CardMsg (BringToFront id); CardMsg (FinishDrag(id, pos))]
                                | _ -> ()
                            ), pointerCapture = true)

                            selectedPin |> AVal.map (fun po ->
                                match po with
                                | Some pin ->
                                    let p = pin.Prism.AnchorPoint
                                    sprintf "Pin  (%.1f, %.1f, %.1f)" p.X p.Y p.Z
                                | None -> "Pin")
                        }

                        button {
                            Class "card-btn-reattach"
                            Attribute("title", "Reattach to pin")
                            isDetached |> AVal.map (fun d -> if d then None else Some (Style [Display "none"]))
                            Dom.OnClick(fun _ -> env.Emit [CardMsg (RedockCard cardId)])
                            "\U0001F4CC"
                        }
                        button {
                            Class "card-btn-collapse"
                            Attribute("title", "Collapse")
                            Dom.OnClick(fun _ ->
                                transact (fun () ->
                                    let s = collapsedSet.Value
                                    if HashSet.contains cardId s then collapsedSet.Value <- HashSet.remove cardId s
                                    else collapsedSet.Value <- HashSet.add cardId s))
                            isCollapsed |> AVal.map (fun c -> if c then "+" else "\u2013")
                        }
                        button {
                            Class "card-btn-close"
                            Attribute("title", "Deselect pin")
                            Dom.OnClick(fun _ -> env.Emit [ScanPinMsg (SelectPin None)])
                            "\u00d7"
                        }
                    }

                    div {
                        Class "card-body"
                        isCollapsed |> AVal.map (fun c ->
                            if c then Some (Style [Display "none"]) else None)
                        pinCardBody env model selectedPin
                    }
                }
            )
        }
