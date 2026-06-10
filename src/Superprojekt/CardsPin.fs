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

    // Ridgeline chart for a data-ridge JSON attribute. d.mini
    // renders the compressed hover-probe variant: colour squares
    // instead of labels, no count badges, no click-to-expand detail.
    let ridgelineJs = [
        "  function placeholder(t){ var p = document.createElement('div'); p.className = 'pin-card-empty'; p.textContent = t; el.appendChild(p); }"
        "  if(!d.status || d.status === 'none'){ placeholder('No probe for this payload.'); return; }"
        "  if(d.status === 'running'){ placeholder('Probing…'); return; }"
        "  if(d.status === 'error'){ placeholder('Probe failed: ' + (d.reason || '')); return; }"
        "  var mini = !!d.mini;"
        "  var rows = d.rows || [];"
        "  var n = rows.length;"
        "  if(n === 0){ placeholder('No meshes.'); return; }"
        "  var w = mini ? 240 : 300;"
        "  var labelW = mini ? 16 : 80;"
        "  var badgeW = mini ? 6 : 46;"
        "  var maxH = mini ? 150 : 400;"
        "  var rowH = Math.min(30, Math.max(13, (maxH - 60) / n));"
        "  var padT = 6, padB = mini ? 14 : 26;"
        "  var h = Math.round(rowH * n + padT + padB);"
        "  var iw = w - labelW - badgeW;"
        "  var x0 = d.xmin, x1 = d.xmax;"
        "  if(!(x1 > x0)){ x0 = -0.1; x1 = 0.1; }"
        "  var sx = function(v){ return labelW + (v - x0) / (x1 - x0) * iw; };"
        "  var maxY = 0;"
        "  rows.forEach(function(r){ (r.kde || []).forEach(function(p){ if(p[0] >= x0 && p[0] <= x1 && p[1] > maxY) maxY = p[1]; }); });"
        "  var svg = document.createElementNS(ns,'svg');"
        "  svg.setAttribute('class','ridge-plot');"
        "  svg.setAttribute('width', w); svg.setAttribute('height', h);"
        "  svg.setAttribute('viewBox', '0 0 ' + w + ' ' + h);"
        "  var frame = document.createElementNS(ns,'rect');"
        "  frame.setAttribute('x', labelW); frame.setAttribute('y', padT);"
        "  frame.setAttribute('width', iw); frame.setAttribute('height', h - padT - padB);"
        "  frame.setAttribute('fill','#f8fafc'); frame.setAttribute('stroke','#cbd5e1'); frame.setAttribute('stroke-width','1');"
        "  svg.appendChild(frame);"
        "  function ln(xa,ya,xb,yb,stroke,sw,dash,op){"
        "    var l = document.createElementNS(ns,'line');"
        "    l.setAttribute('x1',xa); l.setAttribute('y1',ya); l.setAttribute('x2',xb); l.setAttribute('y2',yb);"
        "    l.setAttribute('stroke',stroke); l.setAttribute('stroke-width',sw);"
        "    if(dash) l.setAttribute('stroke-dasharray',dash);"
        "    if(op) l.setAttribute('stroke-opacity',op);"
        "    svg.appendChild(l);"
        "  }"
        "  function txt(x,y,s,anchor,fill,size){"
        "    var t = document.createElementNS(ns,'text');"
        "    t.setAttribute('x',x); t.setAttribute('y',y);"
        "    t.setAttribute('text-anchor', anchor || 'middle');"
        "    t.setAttribute('font-family','SF Mono, Monaco, monospace');"
        "    t.setAttribute('font-size', size || '9');"
        "    t.setAttribute('fill', fill || '#475569');"
        "    t.textContent = s; svg.appendChild(t);"
        "  }"
        "  if(0 >= x0 && 0 <= x1) ln(sx(0), padT, sx(0), h - padB, '#64748b', '1', '3,3', '0.7');"
        "  var rawStep = (x1 - x0) / 4;"
        "  var pow = Math.pow(10, Math.floor(Math.log(rawStep) / Math.LN10));"
        "  var ms = rawStep / pow;"
        "  var step = (ms >= 5 ? 5 : ms >= 2 ? 2 : 1) * pow;"
        "  var dec = Math.max(0, -Math.floor(Math.log(step) / Math.LN10 + 1e-9));"
        "  for(var tx = Math.ceil(x0 / step) * step; tx <= x1 + step * 0.001; tx += step){"
        "    ln(sx(tx), h - padB, sx(tx), h - padB + 3, '#94a3b8', '1');"
        "    if(!mini) txt(sx(tx), h - padB + 12, tx.toFixed(dec), 'middle');"
        "  }"
        "  if(!mini) txt(labelW + iw / 2, h - 2, 'signed distance (m)', 'middle', '#64748b');"
        "  var detail = null;"
        "  rows.forEach(function(r, i){"
        "    var by = padT + (i + 1) * rowH;"
        "    var ch = rowH * 0.82;"
        "    var grey = r.count === 0;"
        "    if(mini){"
        "      var swr = document.createElementNS(ns,'rect');"
        "      swr.setAttribute('x', 3); swr.setAttribute('y', by - 7);"
        "      swr.setAttribute('width', 7); swr.setAttribute('height', 7);"
        "      swr.setAttribute('fill', grey ? '#cbd5e1' : r.color);"
        "      svg.appendChild(swr);"
        "    } else {"
        "      var nm = r.name.length > 11 ? r.name.slice(0, 10) + '…' : r.name;"
        "      txt(labelW - 5, by - rowH * 0.3, nm, 'end', grey ? '#94a3b8' : '#0f172a');"
        "      txt(w - 3, by - rowH * 0.3, grey ? '–' : 'n=' + r.count, 'end', grey ? '#94a3b8' : '#475569', '8');"
        "    }"
        "    if(!grey){"
        "      var kde = (r.kde || []).filter(function(p){ return p[0] >= x0 && p[0] <= x1; });"
        "      if(kde.length > 1 && maxY > 0){"
        "        var path = 'M' + sx(kde[0][0]).toFixed(1) + ',' + by.toFixed(1);"
        "        kde.forEach(function(p){ path += 'L' + sx(p[0]).toFixed(1) + ',' + (by - p[1] / maxY * ch).toFixed(1); });"
        "        path += 'L' + sx(kde[kde.length - 1][0]).toFixed(1) + ',' + by.toFixed(1) + 'Z';"
        "        var area = document.createElementNS(ns,'path');"
        "        area.setAttribute('d', path);"
        "        area.setAttribute('fill', r.color); area.setAttribute('fill-opacity','0.4');"
        "        area.setAttribute('stroke', r.color); area.setAttribute('stroke-width','1');"
        "        svg.appendChild(area);"
        "      }"
        "      if(r.median >= x0 && r.median <= x1) ln(sx(r.median), by, sx(r.median), by - ch, r.color, '1.5');"
        "      var qa = Math.max(r.q1, x0), qb = Math.min(r.q3, x1);"
        "      if(qb > qa) ln(sx(qa), by, sx(qb), by, r.color, '2.5', null, '0.9');"
        "    }"
        "    if(!mini){"
        "      var hit = document.createElementNS(ns,'rect');"
        "      hit.setAttribute('x', 0); hit.setAttribute('y', by - rowH);"
        "      hit.setAttribute('width', w); hit.setAttribute('height', rowH);"
        "      hit.setAttribute('fill','transparent'); hit.style.cursor = 'pointer';"
        "      hit.addEventListener('click', function(){"
        "        if(!detail) return;"
        "        var iqr = r.q3 - r.q1;"
        "        var line = r.name + ' — offset ' + (r.median >= 0 ? '+' : '') + r.median.toFixed(3) + ' m · IQR ' + iqr.toFixed(3) + ' m · n=' + r.count;"
        "        detail.textContent = (detail.textContent === line) ? '' : line;"
        "      });"
        "      svg.appendChild(hit);"
        "    }"
        "  });"
        "  el.appendChild(svg);"
        "  if(!mini){"
        "    detail = document.createElement('div');"
        "    detail.className = 'ridge-detail';"
        "    el.appendChild(detail);"
        "  }"
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

    let probeRidgeJson (mini : bool) (lockOrder : bool) (xRange : ProbeXRange) (colors : Map<string, C4b>) (r : ProbeResult) =
        let win = ProbeXRange.window r xRange
        let colorHex name =
            match Map.tryFind name colors with
            | Some c -> c4bToHex c
            | None -> "#1a56db"
        let rows =
            if lockOrder then r.Distributions
            else r.Distributions |> Array.sortBy (fun d -> (if d.Count = 0 then 1 else 0), abs d.Median)
        let sb = System.Text.StringBuilder()
        sb.Append(sprintf "{\"status\":\"ready\",\"mini\":%b,\"xmin\":%.5g,\"xmax\":%.5g,\"rows\":[" mini win.Min win.Max) |> ignore
        rows |> Array.iteri (fun i d ->
            if i > 0 then sb.Append(',') |> ignore
            sb.Append(sprintf "{\"name\":\"%s\",\"color\":\"%s\",\"count\":%d,\"median\":%.5g,\"q1\":%.5g,\"q3\":%.5g,\"kde\":["
                        (shortName d.MeshName) (colorHex d.MeshName) d.Count d.Median d.Q1 d.Q3) |> ignore
            d.Kde |> Array.iteri (fun j (x, y) ->
                if j > 0 then sb.Append(',') |> ignore
                sb.Append(sprintf "[%.4g,%.4g]" x y) |> ignore)
            sb.Append("]}") |> ignore)
        sb.Append("]}") |> ignore
        sb.ToString()

    let probeStateJson (mini : bool) (lockOrder : bool) (xRange : ProbeXRange) (colors : Map<string, C4b>) (probe : ProbeState) =
        match probe with
        | ProbeReady r -> probeRidgeJson mini lockOrder xRange colors r
        | ProbeError e -> sprintf "{\"status\":\"error\",\"reason\":\"%s\"}" (e.Replace("\\", "/").Replace("\"", "'"))
        | ProbeNone | ProbeRunning -> "{\"status\":\"running\"}"

    let pinCardBody (env : Env<Message>) (model : AdaptiveModel) (selectedPin : aval<ScanPin option>) =
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
                    selectedPin |> AVal.map (function
                        | Some pin ->
                            match pin.Payload with
                            | Point _ -> probeStateJson false pin.ProbeLockOrder pin.ProbeXRange pin.DatasetColors pin.Probe
                            | _ -> "{\"status\":\"none\"}"
                        | None -> "{\"status\":\"none\"}")
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
                    div {
                        Class "pc-ridge"
                        probeJson |> AVal.map (fun j -> Some (Attribute("data-ridge", j)))
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
