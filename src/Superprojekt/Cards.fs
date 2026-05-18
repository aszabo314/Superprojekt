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

    /// Nice-number tick generator (same ladder as the scale bar). Kept as a
    /// generic utility for V6 payload cards (§D.7).
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

    // V6 §D.7.1 — Point payload card. Three sections per spec:
    // (1) numeric readout of (centre, radius, σ); (2) error-provenance
    // stacked bar (placeholder bars until Phase 7 supplies real data);
    // (3) editable reliability-weight slider.
    let private pinCardBody (env : Env<Message>) (model : AdaptiveModel) (selectedPin : aval<ScanPin option>) =
        let payloadKind =
            selectedPin |> AVal.map (function
                | Some p -> Some (PayloadType.kind p.Payload)
                | None -> None)
        let isPoint = payloadKind |> AVal.map ((=) (Some PointKind))
        let isLine  = payloadKind |> AVal.map ((=) (Some LineKind))
        let isPatch = payloadKind |> AVal.map ((=) (Some PatchKind))
        let showOnly (v : aval<bool>) =
            v |> AVal.map (fun on -> if on then None else Some (Style [Display "none"]))

        let centreText = selectedPin |> AVal.map (function
            | Some p -> sprintf "(%.2f, %.2f, %.2f)" p.Centre.X p.Centre.Y p.Centre.Z
            | None -> "—")
        let radiusText = selectedPin |> AVal.map (function
            | Some p -> sprintf "%.2f m" p.Radius
            | None -> "—")
        let sigmaText = selectedPin |> AVal.map (function
            | Some p -> sprintf "%.2f m" p.Sigma
            | None -> "—")
        let reliability = selectedPin |> AVal.map (function
            | Some p ->
                match p.Payload with
                | Point pp -> pp.ReliabilityWeight
                | _ -> 1.0
            | None -> 1.0)
        let onReliabilityChange v =
            match AVal.force selectedPin with
            | Some p -> env.Emit [ScanPinMsg (SetReliabilityWeight(p.Id, v))]
            | None -> ()

        div {
            Class "pin-card-body"

            // Point payload section.
            div {
                Class "pin-card-section pin-card-point"
                showOnly isPoint
                div {
                    Class "pc-readout"
                    div {
                        Class "pc-readout-row"
                        span { Class "pc-key"; "Centre" }
                        span { Class "pc-val"; centreText }
                    }
                    div {
                        Class "pc-readout-row"
                        span { Class "pc-key"; "Radius" }
                        span { Class "pc-val"; radiusText }
                    }
                    div {
                        Class "pc-readout-row"
                        span { Class "pc-key"; "σ" }
                        span { Class "pc-val"; sigmaText }
                    }
                }
                // §D.9 error-provenance stacked bar — real per-pin values.
                let provenance =
                    (selectedPin, model.MeshSensorTypes, model.MeshDatasetErrors,
                     model.MeshAlgorithmResidual, model.CommonCentroid, model.DatasetScales,
                     model.ActiveDataset, model.ScanPins.Pins |> AMap.toAVal)
                    |> fun (a, b, c, d, e, f, g, h) ->
                        AVal.custom (fun tok ->
                            let pinOpt = a.GetValue tok
                            let sensors = b.GetValue tok
                            let overrides = c.GetValue tok
                            let algo = d.GetValue tok
                            let cc = e.GetValue tok
                            let scales = f.GetValue tok
                            let ds = g.GetValue tok
                            let pins = h.GetValue tok
                            match pinOpt with
                            | None -> (0.0, 0.0, 0.0)
                            | Some pin ->
                                match pin.HostMeshName with
                                | None -> (Provenance.defaultDatasetError UnknownSensor, 0.0, 1e6)
                                | Some host ->
                                    let scale = ds |> Option.bind (fun d -> Map.tryFind d scales) |> Option.defaultValue 1.0
                                    let worldP = pin.Centre / scale + cc
                                    let anchors =
                                        pins |> HashMap.toSeq
                                        |> Seq.choose (fun (_, p) ->
                                            if p.Phase = PinPhase.Committed then
                                                Some (p.Centre / scale + cc, p.Sigma / scale)
                                            else None)
                                        |> Array.ofSeq
                                    Provenance.sourcesAt host overrides sensors algo worldP anchors)
                let provText =
                    provenance |> AVal.map (fun (d, a, c) ->
                        sprintf "D %.3fm • A %.3fm • C %.0f" d a c)
                div {
                    Class "pc-provenance"
                    div { Class "pc-section-title"; "Error provenance" }
                    div {
                        Class "pc-bar"
                        provenance |> AVal.map (fun (d, a, c) ->
                            // Normalise to percentages so each bar segment
                            // shows the relative contribution. Conditioning
                            // scaled to metres-equivalent for stacking.
                            let cM = c * 0.01
                            let total = max 1e-6 (d + a + cM)
                            let pd = d / total * 100.0
                            let pa = a / total * 100.0
                            let pc = cM / total * 100.0
                            Some (Attribute("data-prov", sprintf "[%.1f,%.1f,%.1f]" pd pa pc)))
                        OnBoot [
                            "(function(){"
                            "var el = __THIS__;"
                            "var last = '';"
                            "function render(){"
                            "  var raw = el.getAttribute('data-prov') || '[]';"
                            "  if(raw === last) return; last = raw;"
                            "  try { var arr = JSON.parse(raw); } catch(e) { return; }"
                            "  el.innerHTML = '';"
                            "  if(!arr || arr.length < 3) return;"
                            "  var colours = ['#60a5fa','#f59e0b','#a78bfa'];"
                            "  arr.forEach(function(p, i){"
                            "    var d = document.createElement('div');"
                            "    d.style.width = p + '%';"
                            "    d.style.background = colours[i];"
                            "    d.style.height = '100%';"
                            "    el.appendChild(d);"
                            "  });"
                            "}"
                            "render();"
                            "new MutationObserver(render).observe(el,{attributes:true,attributeFilter:['data-prov']});"
                            "})();"
                        ]
                    }
                    div {
                        Class "pc-bar-legend"
                        span { Class "pc-legend-item pc-bar-dataset"; "Dataset" }
                        span { Class "pc-legend-item pc-bar-algorithm"; "Algorithm" }
                        span { Class "pc-legend-item pc-bar-conditioning"; "Conditioning" }
                    }
                    div { Class "pc-provenance-readout"; provText }
                }
                div {
                    Class "pc-reliability"
                    Primitives.inlineSlider
                        "Reliability"
                        0.0 1.0 0.01
                        (sprintf "%.2f")
                        reliability
                        onReliabilityChange
                }
            }

            // §D.7.2 — Line payload card: arc-length × elevation (or curvature)
            // plot. Renders the host polyline plus every cross-mesh trace
            // recorded in CrossMeshTraces, each in its mesh palette colour.
            let lineStateJson =
                selectedPin |> AVal.map (function
                    | Some pin ->
                        match pin.Payload with
                        | Line lp ->
                            let modeLabel =
                                match lp.Mode with
                                | ElevationIsoline _ -> "Elevation"
                                | CurvatureRidge -> "Ridge dihedral"
                            let traces = ResizeArray<string * V3d[] * float[] * string * bool>()
                            let palette = pin.DatasetColors
                            let colorHex (name : string) =
                                match Map.tryFind name palette with
                                | Some c -> c4bToHex c
                                | None -> "#1a56db"
                            let host = pin.HostMeshName |> Option.defaultValue ""
                            traces.Add(host, lp.Points, lp.ScalarVals, colorHex host, true)
                            for kv in lp.CrossMeshTraces do
                                let mesh = kv.Key
                                let pts, sc = kv.Value
                                traces.Add(mesh, pts, sc, colorHex mesh, false)
                            // Drop empty entries early so the OnBoot JS only
                            // worries about valid polylines.
                            let traces =
                                traces |> Seq.filter (fun (_, pts, _, _, _) -> pts.Length >= 2)
                                       |> Array.ofSeq
                            if traces.Length = 0 then "{}"
                            else
                                let sb = System.Text.StringBuilder()
                                sb.Append("{\"mode\":\"") |> ignore
                                sb.Append(modeLabel) |> ignore
                                sb.Append("\",\"traces\":[") |> ignore
                                for ti in 0 .. traces.Length - 1 do
                                    if ti > 0 then sb.Append(',') |> ignore
                                    let (mesh, pts, scalars, color, isHost) = traces.[ti]
                                    let n = pts.Length
                                    let arc = Array.zeroCreate<float> n
                                    for i in 1 .. n - 1 do
                                        arc.[i] <- arc.[i - 1] + (pts.[i] - pts.[i - 1]).Length
                                    sb.Append("{\"mesh\":\"") |> ignore
                                    sb.Append(shortName mesh) |> ignore
                                    sb.Append("\",\"color\":\"") |> ignore
                                    sb.Append(color) |> ignore
                                    sb.Append("\",\"host\":") |> ignore
                                    sb.Append(if isHost then "true" else "false") |> ignore
                                    sb.Append(",\"pts\":[") |> ignore
                                    for i in 0 .. n - 1 do
                                        if i > 0 then sb.Append(',') |> ignore
                                        let s = if i < scalars.Length then scalars.[i] else 0.0
                                        sb.Append(sprintf "[%.3f,%.3f]" arc.[i] s) |> ignore
                                    sb.Append("]}") |> ignore
                                sb.Append("]}") |> ignore
                                sb.ToString()
                        | _ -> "{}"
                    | None -> "{}")
            div {
                Class "pin-card-section pin-card-line"
                showOnly isLine
                lineStateJson |> AVal.map (fun j -> Some (Attribute("data-line", j)))
                OnBoot [
                    "(function(){"
                    "var el = __THIS__;"
                    "var last = '';"
                    "var ns = 'http://www.w3.org/2000/svg';"
                    "function render(){"
                    "  var raw = el.getAttribute('data-line') || '{}';"
                    "  if(raw === last) return; last = raw;"
                    "  try { var d = JSON.parse(raw); } catch(e) { return; }"
                    "  el.innerHTML = '';"
                    "  if(!d.traces || d.traces.length === 0){"
                    "    var p = document.createElement('div');"
                    "    p.className = 'pin-card-empty';"
                    "    p.textContent = 'Tracing…';"
                    "    el.appendChild(p);"
                    "    return;"
                    "  }"
                    "  var w = 280, h = 130, padL = 38, padR = 6, padT = 6, padB = 38;"
                    "  var iw = w - padL - padR, ih = h - padT - padB;"
                    "  var xMax = 0, yMin = Infinity, yMax = -Infinity;"
                    "  d.traces.forEach(function(t){"
                    "    t.pts.forEach(function(p){"
                    "      if(p[0] > xMax) xMax = p[0];"
                    "      if(p[1] < yMin) yMin = p[1];"
                    "      if(p[1] > yMax) yMax = p[1];"
                    "    });"
                    "  });"
                    "  if(yMax - yMin < 0.001){ var c = (yMax + yMin)/2; yMin = c - 0.5; yMax = c + 0.5; }"
                    "  if(xMax < 0.001) xMax = 1.0;"
                    "  var sx = function(v){ return padL + v / xMax * iw; };"
                    "  var sy = function(v){ return padT + ih - (v - yMin)/(yMax - yMin) * ih; };"
                    "  var svg = document.createElementNS(ns,'svg');"
                    "  svg.setAttribute('class','line-plot');"
                    "  svg.setAttribute('width', w); svg.setAttribute('height', h);"
                    "  svg.setAttribute('viewBox', '0 0 ' + w + ' ' + h);"
                    "  var frame = document.createElementNS(ns,'rect');"
                    "  frame.setAttribute('x', padL); frame.setAttribute('y', padT);"
                    "  frame.setAttribute('width', iw); frame.setAttribute('height', ih);"
                    "  frame.setAttribute('fill','#f8fafc'); frame.setAttribute('stroke','#cbd5e1');"
                    "  frame.setAttribute('stroke-width','1');"
                    "  svg.appendChild(frame);"
                    "  d.traces.forEach(function(tr){"
                    "    var pl = document.createElementNS(ns,'polyline');"
                    "    pl.setAttribute('points', tr.pts.map(function(p){return sx(p[0])+','+sy(p[1]);}).join(' '));"
                    "    pl.setAttribute('stroke', tr.color);"
                    "    pl.setAttribute('stroke-width', tr.host ? '1.8' : '1.2');"
                    "    pl.setAttribute('stroke-opacity', tr.host ? '1.0' : '0.85');"
                    "    pl.setAttribute('fill','none');"
                    "    svg.appendChild(pl);"
                    "  });"
                    "  function txt(x,y,s,anchor,color){"
                    "    var t = document.createElementNS(ns,'text');"
                    "    t.setAttribute('x', x); t.setAttribute('y', y);"
                    "    t.setAttribute('text-anchor', anchor || 'middle');"
                    "    t.setAttribute('font-family','SF Mono, Monaco, monospace');"
                    "    t.setAttribute('font-size','9');"
                    "    t.setAttribute('fill', color || '#475569');"
                    "    t.textContent = s; return t;"
                    "  }"
                    "  svg.appendChild(txt(padL - 4, padT + 8, yMax.toFixed(1)));"
                    "  svg.appendChild(txt(padL - 4, padT + ih - 1, yMin.toFixed(1), 'end'));"
                    "  svg.appendChild(txt(padL, padT + ih + 10, '0m', 'start'));"
                    "  svg.appendChild(txt(w - padR, padT + ih + 10, xMax.toFixed(1) + 'm', 'end'));"
                    "  svg.appendChild(txt((padL + w - padR)/2, padT + ih + 10, d.mode, 'middle'));"
                    "  // Legend: mesh names with their colours, two-per-row max."
                    "  var lyBase = padT + ih + 22;"
                    "  d.traces.forEach(function(tr, i){"
                    "    var col = i % 2;"
                    "    var row = (i / 2) | 0;"
                    "    var lx = padL + col * (iw / 2);"
                    "    var ly = lyBase + row * 10;"
                    "    var sw = document.createElementNS(ns,'rect');"
                    "    sw.setAttribute('x', lx); sw.setAttribute('y', ly - 5);"
                    "    sw.setAttribute('width','7'); sw.setAttribute('height','5');"
                    "    sw.setAttribute('fill', tr.color); svg.appendChild(sw);"
                    "    svg.appendChild(txt(lx + 10, ly, tr.mesh + (tr.host ? ' ★' : ''), 'start', tr.color));"
                    "  });"
                    "  el.appendChild(svg);"
                    "}"
                    "render();"
                    "new MutationObserver(render).observe(el,{attributes:true,attributeFilter:['data-line']});"
                    "})();"
                ]
            }

            // §D.7.3 — Patch payload card: azimuthal-equidistant unwrap.
            // Each projected point is drawn as a small filled dot whose
            // colour encodes its world-space Z. A compass rose marks
            // CompassNorth (the in-tangent-plane direction toward world +Y).
            let patchStateJson =
                selectedPin |> AVal.map (function
                    | Some pin ->
                        match pin.Payload with
                        | Patch pp ->
                            let pts = pp.ProjectedPoints
                            if pts.Length = 0 then
                                sprintf "{\"r\":%.3f,\"empty\":true}" pp.Radius
                            else
                                let mutable zMin = System.Double.MaxValue
                                let mutable zMax = System.Double.MinValue
                                for (_, w) in pts do
                                    if w.Z < zMin then zMin <- w.Z
                                    if w.Z > zMax then zMax <- w.Z
                                if zMax - zMin < 1e-6 then
                                    let m = (zMin + zMax) * 0.5
                                    zMin <- m - 0.5; zMax <- m + 0.5
                                let sb = System.Text.StringBuilder()
                                sb.Append("{\"r\":") |> ignore
                                sb.Append(sprintf "%.3f" pp.Radius) |> ignore
                                sb.Append(",\"zMin\":") |> ignore
                                sb.Append(sprintf "%.3f" zMin) |> ignore
                                sb.Append(",\"zMax\":") |> ignore
                                sb.Append(sprintf "%.3f" zMax) |> ignore
                                sb.Append(",\"north\":[") |> ignore
                                sb.Append(sprintf "%.3f,%.3f" pp.CompassNorth.X pp.CompassNorth.Y) |> ignore
                                sb.Append("],\"mesh\":\"") |> ignore
                                sb.Append(shortName pp.SourceMeshName) |> ignore
                                sb.Append("\",\"color\":\"") |> ignore
                                let colorHex =
                                    match Map.tryFind pp.SourceMeshName pin.DatasetColors with
                                    | Some c -> c4bToHex c
                                    | None -> "#1a56db"
                                sb.Append(colorHex) |> ignore
                                sb.Append("\",\"pts\":[") |> ignore
                                for i in 0 .. pts.Length - 1 do
                                    if i > 0 then sb.Append(',') |> ignore
                                    let (p2, w) = pts.[i]
                                    sb.Append(sprintf "[%.3f,%.3f,%.3f]" p2.X p2.Y w.Z) |> ignore
                                sb.Append("]}") |> ignore
                                sb.ToString()
                        | _ -> "{}"
                    | None -> "{}")
            div {
                Class "pin-card-section pin-card-patch"
                showOnly isPatch
                patchStateJson |> AVal.map (fun j -> Some (Attribute("data-patch", j)))
                OnBoot [
                    "(function(){"
                    "var el = __THIS__;"
                    "var last = '';"
                    "var ns = 'http://www.w3.org/2000/svg';"
                    "function render(){"
                    "  var raw = el.getAttribute('data-patch') || '{}';"
                    "  if(raw === last) return; last = raw;"
                    "  try { var d = JSON.parse(raw); } catch(e) { return; }"
                    "  el.innerHTML = '';"
                    "  if(d.empty || !d.pts || d.pts.length < 3){"
                    "    var p = document.createElement('div');"
                    "    p.className = 'pin-card-empty';"
                    "    p.textContent = 'Computing patch projection…';"
                    "    el.appendChild(p);"
                    "    return;"
                    "  }"
                    "  var size = 220, pad = 14;"
                    "  var cx = size/2, cy = size/2;"
                    "  var maxR = (size/2) - pad;"
                    "  var sx = function(px){ return cx + px / d.r * maxR; };"
                    "  var sy = function(py){ return cy - py / d.r * maxR; };"
                    "  var svg = document.createElementNS(ns,'svg');"
                    "  svg.setAttribute('class','patch-plot');"
                    "  svg.setAttribute('width', size); svg.setAttribute('height', size);"
                    "  svg.setAttribute('viewBox','0 0 '+size+' '+size);"
                    "  var ring = document.createElementNS(ns,'circle');"
                    "  ring.setAttribute('cx', cx); ring.setAttribute('cy', cy);"
                    "  ring.setAttribute('r', maxR);"
                    "  ring.setAttribute('fill','#f8fafc');"
                    "  ring.setAttribute('stroke', d.color);"
                    "  ring.setAttribute('stroke-width','2');"
                    "  svg.appendChild(ring);"
                    "  function colour(z){"
                    "    var t = (z - d.zMin) / (d.zMax - d.zMin);"
                    "    t = Math.max(0, Math.min(1, t));"
                    "    var r = Math.round(255 * t);"
                    "    var b = Math.round(255 * (1 - t));"
                    "    return 'rgb(' + r + ',' + (60 + Math.round(120*t)) + ',' + b + ')';"
                    "  }"
                    "  d.pts.forEach(function(p){"
                    "    var c = document.createElementNS(ns,'circle');"
                    "    c.setAttribute('cx', sx(p[0])); c.setAttribute('cy', sy(p[1]));"
                    "    c.setAttribute('r','1.6');"
                    "    c.setAttribute('fill', colour(p[2]));"
                    "    c.setAttribute('opacity','0.85');"
                    "    svg.appendChild(c);"
                    "  });"
                    "  // Compass rose: arrow from centre toward CompassNorth."
                    "  var nx = sx(d.north[0] * d.r * 0.9);"
                    "  var ny = sy(d.north[1] * d.r * 0.9);"
                    "  var arrow = document.createElementNS(ns,'line');"
                    "  arrow.setAttribute('x1', cx); arrow.setAttribute('y1', cy);"
                    "  arrow.setAttribute('x2', nx); arrow.setAttribute('y2', ny);"
                    "  arrow.setAttribute('stroke','#0f172a');"
                    "  arrow.setAttribute('stroke-width','1.5');"
                    "  svg.appendChild(arrow);"
                    "  var nLabel = document.createElementNS(ns,'text');"
                    "  nLabel.setAttribute('x', nx); nLabel.setAttribute('y', ny - 2);"
                    "  nLabel.setAttribute('text-anchor','middle');"
                    "  nLabel.setAttribute('font-family','SF Mono, Monaco, monospace');"
                    "  nLabel.setAttribute('font-size','10');"
                    "  nLabel.setAttribute('font-weight','bold');"
                    "  nLabel.setAttribute('fill','#0f172a');"
                    "  nLabel.textContent = 'N';"
                    "  svg.appendChild(nLabel);"
                    "  el.appendChild(svg);"
                    "  var caption = document.createElement('div');"
                    "  caption.className = 'pin-card-caption';"
                    "  caption.textContent = d.mesh + ' • r=' + d.r.toFixed(1) + 'm • ' + d.pts.length + ' pts';"
                    "  el.appendChild(caption);"
                    "}"
                    "render();"
                    "new MutationObserver(render).observe(el,{attributes:true,attributeFilter:['data-patch']});"
                    "})();"
                ]
            }
        }

    let renderCards (env : Env<Message>) (model : AdaptiveModel) (viewTrafo : aval<Trafo3d>) (vpSize : aval<V2i>) =
        let allPinsVal = model.ScanPins.Pins |> AMap.toAVal
        let activePlacementId =
            model.ScanPins.Placement |> AVal.map (function
                | AdjustingPin id -> Some id
                | _ -> None)
        let selectedPin =
            (model.ScanPins.SelectedPin, activePlacementId, allPinsVal)
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
                |> Seq.filter (fun (_, c) -> match c.Content with PinCard _ -> true)
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

                    // §D.12 — coloured frame: a thin strip at the top of
                    // the card carries the host mesh's palette colour so
                    // that the card visibly links to the 3D anchor + 3D
                    // patch ring (same colour in both views).
                    div {
                        Class "pin-card-color-bar"
                        selectedPin |> AVal.map (fun po ->
                            match po with
                            | Some p ->
                                let bg =
                                    match p.HostMeshName with
                                    | Some host ->
                                        match Map.tryFind host p.DatasetColors with
                                        | Some c -> sprintf "rgb(%d,%d,%d)" (int c.R) (int c.G) (int c.B)
                                        | None -> "#1a56db"
                                    | None -> "#1a56db"
                                Some (Style [Css.Background bg])
                            | None -> Some (Style [Display "none"]))
                    }

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
                                    let p = pin.Centre
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
                            isCollapsed |> AVal.map (fun c -> if c then "+" else "–")
                        }
                        button {
                            Class "card-btn-close"
                            Attribute("title", "Deselect pin")
                            Dom.OnClick(fun _ -> env.Emit [ScanPinMsg (SelectPin None)])
                            "×"
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
