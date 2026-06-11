namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open FSharp.Data.Adaptive
open Aardvark.Dom

module CardsPin =

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

    // Vertical violin chart for a data-ridge JSON attribute: signed distance
    // on the y axis (positive up, 0 = reference median), one column per mesh.
    // d.mini renders the compressed hover-probe variant (colour squares, no
    // badges, no interaction). The full variant is interactive:
    //   • pointer in the plot area → elevation cursor line + 'mv|d|alt|mesh'
    //     events to the .pc-ridge-bus input (picked up by Dom.OnInput)
    //   • column click → 'click|mesh' (sticky highlight, toggles)
    //   • document-level click outside the chart → 'clickout'
    //   • the data-cursor attribute ({"d":…}, driven from 3D hover) moves the
    //     same cursor line without re-rendering the chart.
    let ridgelineJs = [
        "  function placeholder(t){ var p = document.createElement('div'); p.className = 'pin-card-empty'; p.textContent = t; el.appendChild(p); }"
        "  if(!d.status || d.status === 'none'){ placeholder('No probe for this payload.'); return; }"
        "  if(d.status === 'running'){ placeholder('Probing…'); return; }"
        "  if(d.status === 'error'){ placeholder('Probe failed: ' + (d.reason || '')); return; }"
        "  var mini = !!d.mini;"
        "  var rows = d.rows || [];"
        "  var n = rows.length;"
        "  if(n === 0){ placeholder('No meshes.'); return; }"
        "  var accent = '#0891b2';"
        "  var w = mini ? 240 : Math.max(220, el.clientWidth || 296);"
        "  var axisW = mini ? 34 : 80;"
        "  var headerH = mini ? 16 : 24;"
        "  var badgeH = mini ? 10 : 16;"
        "  var H = mini ? 150 : 280;"
        "  var minCol = mini ? 24 : 40;"
        "  var colW = Math.max(minCol, (w - axisW) / n);"
        "  var svgW = Math.ceil(axisW + colW * n);"
        "  var y0 = d.ymin, y1 = d.ymax;"
        "  if(!(y1 > y0)){ y0 = -0.1; y1 = 0.1; }"
        "  var plotY0 = headerH, plotY1 = H - badgeH;"
        "  var ih = plotY1 - plotY0;"
        "  function sy(v){ return plotY0 + (y1 - v) / (y1 - y0) * ih; }"
        "  function fromY(py){ return y1 - (py - plotY0) / ih * (y1 - y0); }"
        "  var maxDen = 0;"
        "  rows.forEach(function(r){ (r.kde || []).forEach(function(p){ if(p[0] >= y0 && p[0] <= y1 && p[1] > maxDen) maxDen = p[1]; }); });"
        "  var svg = document.createElementNS(ns,'svg');"
        "  svg.setAttribute('class','ridge-plot');"
        "  svg.setAttribute('width', svgW); svg.setAttribute('height', H);"
        "  svg.setAttribute('viewBox', '0 0 ' + svgW + ' ' + H);"
        "  function ln(xa,ya,xb,yb,stroke,sw,dash,op){"
        "    var l = document.createElementNS(ns,'line');"
        "    l.setAttribute('x1',xa); l.setAttribute('y1',ya); l.setAttribute('x2',xb); l.setAttribute('y2',yb);"
        "    l.setAttribute('stroke',stroke); l.setAttribute('stroke-width',sw);"
        "    if(dash) l.setAttribute('stroke-dasharray',dash);"
        "    if(op) l.setAttribute('stroke-opacity',op);"
        "    svg.appendChild(l); return l;"
        "  }"
        "  function txt(x,y,s,anchor,fill,size){"
        "    var t = document.createElementNS(ns,'text');"
        "    t.setAttribute('x',x); t.setAttribute('y',y);"
        "    t.setAttribute('text-anchor', anchor || 'middle');"
        "    t.setAttribute('font-family','SF Mono, Monaco, monospace');"
        "    t.setAttribute('font-size', size || '9');"
        "    t.setAttribute('fill', fill || '#475569');"
        "    t.textContent = s; svg.appendChild(t); return t;"
        "  }"
        "  var frame = document.createElementNS(ns,'rect');"
        "  frame.setAttribute('x', axisW); frame.setAttribute('y', plotY0);"
        "  frame.setAttribute('width', svgW - axisW); frame.setAttribute('height', ih);"
        "  frame.setAttribute('fill','#f8fafc'); frame.setAttribute('stroke','#cbd5e1'); frame.setAttribute('stroke-width','1');"
        "  svg.appendChild(frame);"
        "  var hoverRect = document.createElementNS(ns,'rect');"
        "  hoverRect.setAttribute('y', plotY0); hoverRect.setAttribute('height', ih);"
        "  hoverRect.setAttribute('fill', accent); hoverRect.setAttribute('fill-opacity','0.08');"
        "  hoverRect.style.display = 'none';"
        "  svg.appendChild(hoverRect);"
        "  var rawStep = (y1 - y0) / 4;"
        "  var pow = Math.pow(10, Math.floor(Math.log(rawStep) / Math.LN10));"
        "  var ms = rawStep / pow;"
        "  var step = (ms >= 5 ? 5 : ms >= 2 ? 2 : 1) * pow;"
        "  var dec = Math.max(0, -Math.floor(Math.log(step) / Math.LN10 + 1e-9));"
        "  for(var tv = Math.ceil(y0 / step) * step; tv <= y1 + step * 0.001; tv += step){"
        "    ln(axisW - 3, sy(tv), axisW, sy(tv), '#94a3b8', '1');"
        "    txt(axisW - 5, sy(tv) + 3, tv.toFixed(dec), 'end', '#475569', mini ? '8' : '9');"
        "  }"
        "  if(0 >= y0 && 0 <= y1) ln(axisW, sy(0), svgW, sy(0), '#64748b', '1', '3,3', '0.7');"
        "  if(!mini){"
        "    var at = txt(10, plotY0 + ih / 2, 'offset (m)', 'middle', '#64748b');"
        "    at.setAttribute('transform', 'rotate(-90 10 ' + (plotY0 + ih / 2) + ')');"
        "  }"
        "  rows.forEach(function(r, i){"
        "    var x0 = axisW + i * colW, cx = x0 + colW / 2;"
        "    var grey = r.count === 0;"
        "    if(mini){"
        "      var swm = document.createElementNS(ns,'rect');"
        "      swm.setAttribute('x', cx - 2.5); swm.setAttribute('y', 4);"
        "      swm.setAttribute('width', 5); swm.setAttribute('height', 5);"
        "      swm.setAttribute('fill', grey ? '#cbd5e1' : r.color);"
        "      svg.appendChild(swm);"
        "    } else {"
        "      var maxChars = Math.max(3, Math.floor((colW - 16) / 5.4));"
        "      var nm = r.name.length > maxChars ? r.name.slice(0, maxChars - 1) + '…' : r.name;"
        "      var sx0 = cx - (nm.length * 5.4 + 11) / 2;"
        "      var swr = document.createElementNS(ns,'rect');"
        "      swr.setAttribute('x', sx0); swr.setAttribute('y', 8);"
        "      swr.setAttribute('width', 7); swr.setAttribute('height', 7);"
        "      swr.setAttribute('fill', grey ? '#cbd5e1' : r.color);"
        "      svg.appendChild(swr);"
        "      txt(sx0 + 11, 15, nm, 'start', grey ? '#94a3b8' : '#0f172a');"
        "    }"
        "    if(!grey){"
        "      var kde = (r.kde || []).filter(function(p){ return p[0] >= y0 && p[0] <= y1; });"
        "      if(kde.length > 1 && maxDen > 0){"
        "        var hw = colW * 0.42;"
        "        var path = '';"
        "        kde.forEach(function(p, k){"
        "          path += (k === 0 ? 'M' : 'L') + (cx + p[1] / maxDen * hw).toFixed(1) + ',' + sy(p[0]).toFixed(1);"
        "        });"
        "        for(var k = kde.length - 1; k >= 0; k--){"
        "          path += 'L' + (cx - kde[k][1] / maxDen * hw).toFixed(1) + ',' + sy(kde[k][0]).toFixed(1);"
        "        }"
        "        path += 'Z';"
        "        var area = document.createElementNS(ns,'path');"
        "        area.setAttribute('d', path);"
        "        area.setAttribute('fill', r.color); area.setAttribute('fill-opacity','0.4');"
        "        area.setAttribute('stroke', r.color); area.setAttribute('stroke-width','1');"
        "        svg.appendChild(area);"
        "      }"
        "      if(r.median >= y0 && r.median <= y1) ln(cx - colW * 0.3, sy(r.median), cx + colW * 0.3, sy(r.median), r.color, '1.5');"
        "      var qa = Math.max(r.q1, y0), qb = Math.min(r.q3, y1);"
        "      if(qb > qa) ln(cx, sy(qa), cx, sy(qb), r.color, '2.5', null, '0.9');"
        "    }"
        "    if(!mini) txt(cx, H - 4, grey ? '–' : 'n=' + r.count, 'middle', grey ? '#94a3b8' : '#475569', '8');"
        "    if(!mini && d.sticky && d.sticky === r.id){"
        "      var st = document.createElementNS(ns,'rect');"
        "      st.setAttribute('x', x0 + 1.5); st.setAttribute('y', plotY0 + 1.5);"
        "      st.setAttribute('width', colW - 3); st.setAttribute('height', ih - 3);"
        "      st.setAttribute('fill','none'); st.setAttribute('stroke','#0f172a'); st.setAttribute('stroke-width','2');"
        "      svg.appendChild(st);"
        "    }"
        "  });"
        "  var cursor = ln(axisW, plotY0, svgW, plotY0, accent, '1.5');"
        "  cursor.style.display = 'none';"
        "  var cursorLabel = txt(svgW - 3, plotY0, '', 'end', accent, '8');"
        "  cursorLabel.style.display = 'none';"
        "  function setCursor(dv){"
        "    if(dv === null || dv < y0 || dv > y1){ cursor.style.display = 'none'; cursorLabel.style.display = 'none'; return; }"
        "    var y = sy(dv);"
        "    cursor.setAttribute('y1', y); cursor.setAttribute('y2', y);"
        "    cursor.style.display = '';"
        "    cursorLabel.setAttribute('y', y - 3);"
        "    cursorLabel.textContent = (dv >= 0 ? '+' : '') + dv.toFixed(2) + ' m';"
        "    cursorLabel.style.display = '';"
        "  }"
        "  el._hovering = false;"
        "  function applyCursorAttr(){"
        "    if(el._hovering) return;"
        "    var raw = el.getAttribute('data-cursor') || '{}';"
        "    var c; try { c = JSON.parse(raw); } catch(e) { return; }"
        "    setCursor(typeof c.d === 'number' ? c.d : null);"
        "  }"
        "  el._applyCursorAttr = applyCursorAttr;"
        "  if(!el._cursorObs){"
        "    el._cursorObs = new MutationObserver(function(){ if(el._applyCursorAttr) el._applyCursorAttr(); });"
        "    el._cursorObs.observe(el, {attributes:true, attributeFilter:['data-cursor']});"
        "  }"
        "  applyCursorAttr();"
        "  if(!mini){"
        "    var send = function(s){"
        "      var pr = el.closest('.pc-probe');"
        "      var b = pr ? pr.querySelector('.pc-ridge-bus') : null;"
        "      if(b){ b.value = s; b.dispatchEvent(new Event('input', {bubbles:true})); }"
        "    };"
        "    var lastSent = '', pend = null, raf = 0;"
        "    var flush = function(){ raf = 0; if(pend !== null && pend !== lastSent){ lastSent = pend; send(pend); } };"
        "    var queue = function(s){ pend = s; if(!raf) raf = requestAnimationFrame(flush); };"
        "    function colAt(x){ var i = Math.floor((x - axisW) / colW); return (i >= 0 && i < n) ? i : -1; }"
        "    svg.addEventListener('pointerenter', function(){ el._hovering = true; });"
        "    svg.addEventListener('pointermove', function(ev){"
        "      el._hovering = true;"
        "      var rc = svg.getBoundingClientRect();"
        "      var x = ev.clientX - rc.left, y = ev.clientY - rc.top;"
        "      if(y < plotY0 || y > plotY1 || x < axisW){"
        "        setCursor(null); hoverRect.style.display = 'none'; queue('out'); return;"
        "      }"
        "      var dv = fromY(y);"
        "      var ci = colAt(x);"
        "      setCursor(dv);"
        "      if(ci >= 0){ hoverRect.setAttribute('x', axisW + ci * colW); hoverRect.setAttribute('width', colW); hoverRect.style.display = ''; }"
        "      else hoverRect.style.display = 'none';"
        "      queue('mv|' + dv.toFixed(4) + '|' + (ev.altKey ? '1' : '0') + '|' + (ci >= 0 ? rows[ci].id : ''));"
        "    });"
        "    svg.addEventListener('pointerleave', function(){"
        "      el._hovering = false; hoverRect.style.display = 'none';"
        "      applyCursorAttr(); queue('out');"
        "    });"
        "    svg.addEventListener('click', function(ev){"
        "      var rc = svg.getBoundingClientRect();"
        "      var x = ev.clientX - rc.left, y = ev.clientY - rc.top;"
        "      var ci = colAt(x);"
        "      if(y >= plotY0 && y <= plotY1 && ci >= 0){ lastSent = ''; send('click|' + rows[ci].id); }"
        "    });"
        "    if(!el._docClick){"
        "      el._docClick = function(ev){"
        "        if(!document.contains(el)){ document.removeEventListener('click', el._docClick); el._docClick = null; return; }"
        "        if(!el.contains(ev.target)) send('clickout');"
        "      };"
        "      document.addEventListener('click', el._docClick);"
        "    }"
        "  }"
        "  el.appendChild(svg);"
    ]

    // Three-source stacked bar for a data-srcs = [d,a,c] attribute.
    let probeBarJs = [
        "  if(!d || d.length < 3) return;"
        "  var labels = ['Dataset error','Algorithm residual','Local conditioning'];"
        "  var colours = ['#60a5fa','#f59e0b','#a78bfa'];"
        "  var total = d[0] + d[1] + d[2];"
        "  if(total <= 0) return;"
        "  d.forEach(function(v, i){"
        "    var s = document.createElement('div');"
        "    s.style.width = (v / total * 100) + '%';"
        "    s.style.background = colours[i];"
        "    s.style.height = '100%';"
        "    s.title = labels[i] + ': ' + v.toFixed(4) + ' m';"
        "    el.appendChild(s);"
        "  });"
    ]

    let probeRidgeJson (mini : bool) (lockOrder : bool) (xRange : ProbeXRange) (sticky : string option) (colors : Map<string, C4b>) (r : ProbeResult) =
        let win = ProbeXRange.window r xRange
        let colorHex name =
            match Map.tryFind name colors with
            | Some c -> c4bToHex c
            | None -> "#1a56db"
        let rows =
            if lockOrder then r.Distributions
            else r.Distributions |> Array.sortBy (fun d -> (if d.Count = 0 then 1 else 0), abs d.Median)
        let sb = System.Text.StringBuilder()
        sb.Append(sprintf "{\"status\":\"ready\",\"mini\":%b,\"ymin\":%.5g,\"ymax\":%.5g,\"sticky\":\"%s\",\"rows\":["
                    mini win.Min win.Max (sticky |> Option.defaultValue "")) |> ignore
        rows |> Array.iteri (fun i d ->
            if i > 0 then sb.Append(',') |> ignore
            sb.Append(sprintf "{\"id\":\"%s\",\"name\":\"%s\",\"color\":\"%s\",\"count\":%d,\"median\":%.5g,\"q1\":%.5g,\"q3\":%.5g,\"kde\":["
                        d.MeshName (shortName d.MeshName) (colorHex d.MeshName) d.Count d.Median d.Q1 d.Q3) |> ignore
            d.Kde |> Array.iteri (fun j (x, y) ->
                if j > 0 then sb.Append(',') |> ignore
                sb.Append(sprintf "[%.4g,%.4g]" x y) |> ignore)
            sb.Append("]}") |> ignore)
        sb.Append("]}") |> ignore
        sb.ToString()

    let probeStateJson (mini : bool) (lockOrder : bool) (xRange : ProbeXRange) (sticky : string option) (colors : Map<string, C4b>) (probe : ProbeState) =
        match probe with
        | ProbeReady r -> probeRidgeJson mini lockOrder xRange sticky colors r
        | ProbeError e -> sprintf "{\"status\":\"error\",\"reason\":\"%s\"}" (e.Replace("\\", "/").Replace("\"", "'"))
        | ProbeNone | ProbeRunning -> "{\"status\":\"running\"}"

    let private parseInvariant (s : string) =
        match System.Double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture) with
        | true, v -> Some v
        | _ -> None

    let pinCardBody (env : Env<Message>) (model : AdaptiveModel) (selectedPin : aval<ScanPin option>) (hoverWorld : aval<V3d option>) =
        let payloadKind =
            selectedPin |> AVal.map (function
                | Some p -> Some (PayloadType.kind p.Payload)
                | None -> None)
        let isPoint = payloadKind |> AVal.map ((=) (Some PointKind))
        let isLine  = payloadKind |> AVal.map ((=) (Some LineKind))
        let isPatch = payloadKind |> AVal.map ((=) (Some PatchKind))
        let showOnly = Primitives.showWhen

        let centreText = selectedPin |> AVal.map (function
            | Some p -> sprintf "(%.2f, %.2f, %.2f) m" p.Centre.X p.Centre.Y p.Centre.Z
            | None -> "—")
        let innerText = selectedPin |> AVal.map (function
            | Some p -> sprintf "%.2f m" p.InnerRadius
            | None -> "—")
        let falloffText = selectedPin |> AVal.map (function
            | Some p -> sprintf "%.2f m" p.FalloffRadius
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
                        span { Class "pc-key"; "Inner R" }
                        span { Class "pc-val"; innerText }
                    }
                    div {
                        Class "pc-readout-row"
                        span { Class "pc-key"; "Falloff R" }
                        span { Class "pc-val"; falloffText }
                    }
                }
                // M3C2 probe: planarity badge, ridgeline,
                // x-range / lock-order controls, three-source stacked bar.
                let probe =
                    selectedPin |> AVal.map (function
                        | Some p -> (match p.Payload with Point _ -> Some p.Probe | _ -> None)
                        | None -> None)
                let probeResult =
                    probe |> AVal.map (function
                        | Some (ProbeReady r) -> Some r
                        | _ -> None)
                let probeJson =
                    (selectedPin, model.ChartStickyMesh) ||> AVal.map2 (fun po sticky ->
                        match po with
                        | Some pin ->
                            match pin.Payload with
                            | Point _ -> probeStateJson false pin.ProbeLockOrder pin.ProbeXRange sticky pin.DatasetColors pin.Probe
                            | _ -> "{\"status\":\"none\"}"
                        | None -> "{\"status\":\"none\"}")
                // 3D → chart: the elevation cursor line at the 3D hover
                // point's signed distance along the probe axis, shown only
                // while the hover point sits inside the probe cylinder.
                let cursor3d =
                    (hoverWorld, selectedPin) ||> AVal.map2 (fun hw po ->
                        match hw, po with
                        | Some q, Some pin ->
                            match pin.Probe with
                            | ProbeReady r ->
                                let v = q - pin.Centre
                                let dAx = Vec.dot v r.Normal
                                let radial = (v - r.Normal * dAx).Length
                                if radial <= pin.InnerRadius && abs dAx <= r.Length * 0.5
                                then sprintf "{\"d\":%.4f}" dAx
                                else "{}"
                            | _ -> "{}"
                        | _ -> "{}")
                // Chart → model: the chart JS posts pointer interactions to
                // this hidden input (synthetic 'input' events).
                let onChartEvent (v : string) =
                    let parts = v.Split('|')
                    match parts.[0] with
                    | "mv" when parts.Length >= 4 ->
                        match AVal.force selectedPin, parseInvariant parts.[1] with
                        | Some pin, Some dv ->
                            let cursor = { PinId = pin.Id; Distance = dv; Extended = parts.[2] = "1" }
                            let mesh = if parts.[3] = "" then None else Some parts.[3]
                            env.Emit [SetChartCursor (Some cursor); SetChartHoverMesh mesh]
                        | _ -> ()
                    | "out" ->
                        env.Emit [SetChartCursor None; SetChartHoverMesh None]
                    | "click" when parts.Length >= 2 ->
                        env.Emit [ChartColumnClick parts.[1]]
                    | "clickout" ->
                        env.Emit [ClearChartSticky]
                    | _ -> ()
                let planarBadge =
                    probeResult |> AVal.map (Option.map (fun r -> r.Planar))
                let sources =
                    probeResult |> AVal.map (Option.map (fun r -> r.Sources))
                let srcText =
                    sources |> AVal.map (function
                        | Some s -> sprintf "Data: %.3f m | Algo: %.3f m | Cond: %.4f m" s.DatasetError s.AlgorithmResid s.LocalConditioning
                        | None -> "")
                let emitForPin (mk : ScanPinId -> ScanPinMessage) =
                    match AVal.force selectedPin with
                    | Some p -> env.Emit [ScanPinMsg (mk p.Id)]
                    | None -> ()
                div {
                    Class "pc-probe"
                    div {
                        Class "pc-probe-head"
                        span { Class "pc-section-title"; "Distance probe" }
                        span {
                            Class "pc-planarity"
                            planarBadge |> AVal.map (function
                                | Some true -> Some (Class "pc-planar-ok")
                                | Some false -> Some (Class "pc-planar-warn")
                                | None -> Some (Class "hidden"))
                            planarBadge |> AVal.map (function
                                | Some false -> "not planar"
                                | _ -> "planar")
                        }
                    }
                    input {
                        Class "pc-ridge-bus"
                        Attribute("type", "text")
                        Dom.OnInput(fun e -> onChartEvent e.Value)
                    }
                    div {
                        Class "pc-ridge"
                        probeJson |> AVal.map (fun j -> Some (Attribute("data-ridge", j)))
                        cursor3d |> AVal.map (fun j -> Some (Attribute("data-cursor", j)))
                        Primitives.observedRender "data-ridge" "{}" ridgelineJs
                    }
                    div {
                        Class "pc-probe-controls"
                        showOnly (probeResult |> AVal.map Option.isSome)
                        Primitives.compactButtonBar
                            (ProbeXRange.all |> List.map (fun xr ->
                                ProbeXRange.label xr,
                                (selectedPin |> AVal.map (function Some p -> p.ProbeXRange = xr | None -> false)),
                                (fun () -> emitForPin (fun id -> SetProbeXRange(id, xr)))))
                        Primitives.compactToggle "Lock order"
                            (selectedPin |> AVal.map (function Some p -> p.ProbeLockOrder | None -> false))
                            (fun () -> emitForPin ToggleProbeLockOrder)
                    }
                    div {
                        Class "pc-probe-caption"
                        probeResult |> AVal.map (function
                            | Some r -> sprintf "ref %s · cylinder L %.1f m" (shortName r.ReferenceMesh) r.Length
                            | None -> "")
                    }
                    div {
                        Class "pc-bar"
                        sources |> AVal.map (function
                            | Some s -> Some (Attribute("data-srcs", sprintf "[%.6g,%.6g,%.6g]" s.DatasetError s.AlgorithmResid s.LocalConditioning))
                            | None -> Some (Attribute("data-srcs", "[]")))
                        Primitives.observedRender "data-srcs" "[]" probeBarJs
                    }
                    div {
                        Class "pc-bar-legend"
                        span { Class "pc-legend-item pc-bar-dataset"; "Dataset" }
                        span { Class "pc-legend-item pc-bar-algorithm"; "Algorithm" }
                        span { Class "pc-legend-item pc-bar-conditioning"; "Conditioning" }
                    }
                    div { Class "pc-provenance-readout"; srcText }
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
                // Ensemble-registration correspondence: anchor status per
                // mesh, residuals of the last coarse solve, fallback picks.
                let corr =
                    selectedPin |> AVal.map (fun po -> po |> Option.bind ScanPin.correspondence)
                let corrEnabled = corr |> AVal.map (function Some c -> c.Enabled | None -> false)
                let refMeshOpt = model.Registration |> AVal.map (fun r -> r.ReferenceMesh)
                let emitForPinTop (mk : ScanPinId -> Message) =
                    match AVal.force selectedPin with
                    | Some p -> env.Emit [mk p.Id]
                    | None -> ()
                div {
                    Class "pc-corr"
                    div {
                        Class "pc-probe-head"
                        span { Class "pc-section-title"; "Correspondence" }
                    }
                    // The one-click exclude/include toggle (sets enabled).
                    Primitives.compactToggle "Use as registration landmark" corrEnabled (fun () ->
                        emitForPinTop ToggleCorrespondence)
                    div {
                        Class "pc-corr-body"
                        showOnly corrEnabled
                        div {
                            Class "pc-corr-ref"
                            (corr, selectedPin) ||> AVal.map2 (fun cOpt po ->
                                match cOpt, po with
                                | Some c, Some pin ->
                                    match c.RefAnchor with
                                    | Some _ when c.RefDistance > 2.0 * pin.FalloffRadius ->
                                        sprintf "⚠ reference anchor %.2f m off the pin (> 2× falloff)" c.RefDistance
                                    | Some _ when c.RefDistance > 0.0 ->
                                        sprintf "reference anchor projected, Δ %.3f m" c.RefDistance
                                    | Some _ -> "reference anchor = pin centre"
                                    | None -> "no reference anchor yet — designate a ★ reference mesh"
                                | _ -> "")
                            (corr, selectedPin) ||> AVal.map2 (fun cOpt po ->
                                match cOpt, po with
                                | Some c, Some pin when c.RefAnchor.IsSome && c.RefDistance > 2.0 * pin.FalloffRadius ->
                                    Some (Class "pc-corr-ref-warn")
                                | _ -> None)
                        }
                        div {
                            Class "pc-corr-rows"
                            model.MeshNames |> AList.map (fun mesh ->
                                let isMoving = refMeshOpt |> AVal.map (fun r -> r <> Some mesh)
                                let anchor =
                                    corr |> AVal.map (Option.bind (fun c -> Map.tryFind mesh c.Anchors))
                                let residual =
                                    corr |> AVal.map (Option.bind (fun c -> Map.tryFind mesh c.Residuals))
                                div {
                                    Class "pc-corr-row"
                                    Primitives.showWhen isMoving
                                    span { Class "pc-corr-mesh"; shortName mesh }
                                    span {
                                        Class "pc-corr-acc"
                                        anchor |> AVal.map (function
                                            | Some a when a.Accepted -> Some (Class "pc-corr-acc-on")
                                            | _ -> None)
                                        anchor |> AVal.map (function
                                            | Some a when a.Accepted -> sprintf "✓ %s" (AnchorSource.label a.Source)
                                            | Some a -> sprintf "○ %s" (AnchorSource.label a.Source)
                                            | None -> "—")
                                    }
                                    span {
                                        Class "pc-corr-res"
                                        residual |> AVal.map (function
                                            | Some r -> sprintf "%.3f m" r
                                            | None -> "")
                                    }
                                    button {
                                        Class "mb"
                                        Attribute("title", "Pick this anchor in 3D — one click on this mesh (Esc cancels)")
                                        Dom.OnClick(fun _ ->
                                            match AVal.force selectedPin with
                                            | Some p -> env.Emit [StartAnchorPick(p.Id, mesh)]
                                            | None -> ())
                                        "⊕"
                                    }
                                })
                        }
                        div {
                            Class "pc-corr-actions"
                            button {
                                Class "tb-gear-btn"
                                Attribute("title", "Pick anchors in co-oriented surface patches")
                                Dom.OnClick(fun _ -> emitForPinTop OpenPatchPicker)
                                "▦ Pick in patches"
                            }
                        }
                    }
                }
            }

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
                Primitives.observedRender "data-line" "{}" [
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
                ]
            }

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
                Primitives.observedRender "data-patch" "{}" [
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
                ]
            }
        }
