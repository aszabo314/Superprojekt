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

    // Mesh names are easy to confuse (long, similar prefixes), so every list
    // / chart prefixes the mesh's stable order number (1-based, matches the
    // palette colour index in the mesh panel).
    let numbered (order : HashMap<string, int>) (name : string) =
        match HashMap.tryFind name order with
        | Some i -> sprintf "%d  %s" (i + 1) (shortName name)
        | None -> shortName name

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
        "  var SMALL_N = 20;"
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
        "  rows.forEach(function(r){"
        "    (r.kde || []).forEach(function(p){ if(p[0] >= y0 && p[0] <= y1 && p[1] > maxDen) maxDen = p[1]; });"
        "    (r.kde2 || []).forEach(function(p){ if(p[0] >= y0 && p[0] <= y1 && p[1] > maxDen) maxDen = p[1]; });"
        "  });"
        "  function desat(hex){"
        "    var n = parseInt(hex.slice(1), 16);"
        "    var r0 = (n >> 16) & 255, g0 = (n >> 8) & 255, b0 = n & 255;"
        "    var m = (r0 + g0 + b0) / 3, f = 0.65;"
        "    return 'rgb(' + Math.round(r0 + (m - r0) * f) + ',' + Math.round(g0 + (m - g0) * f) + ',' + Math.round(b0 + (m - b0) * f) + ')';"
        "  }"
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
        "      var refstd = d.refstd || 0;"
        "      var lod = 1.96 * Math.sqrt(refstd*refstd + (r.std||0)*(r.std||0));"
        "      if(lod > 0){ var bl=Math.max(y0,-lod), bh=Math.min(y1,lod); if(bh>bl){ var bnd=document.createElementNS(ns,'rect'); bnd.setAttribute('x',x0+1); bnd.setAttribute('y',sy(bh)); bnd.setAttribute('width',colW-2); bnd.setAttribute('height',Math.max(0,sy(bl)-sy(bh))); bnd.setAttribute('fill','#94a3b8'); bnd.setAttribute('fill-opacity','0.16'); svg.appendChild(bnd); } }"
        "      var hw = colW * 0.42;"
        "      var split = !!(r.kde2 && r.kde2.length > 1);"
        "      function halfArea(kdeRaw, sign, colour){"
        "        var kd = (kdeRaw || []).filter(function(p){ return p[0] >= y0 && p[0] <= y1; });"
        "        if(kd.length < 2 || maxDen <= 0) return;"
        "        var path = '';"
        "        kd.forEach(function(p, k){"
        "          path += (k === 0 ? 'M' : 'L') + (cx + sign * p[1] / maxDen * hw).toFixed(1) + ',' + sy(p[0]).toFixed(1);"
        "        });"
        "        path += 'L' + cx + ',' + sy(kd[kd.length - 1][0]).toFixed(1);"
        "        path += 'L' + cx + ',' + sy(kd[0][0]).toFixed(1) + 'Z';"
        "        var area = document.createElementNS(ns,'path');"
        "        area.setAttribute('d', path);"
        "        area.setAttribute('fill', colour); area.setAttribute('fill-opacity','0.4');"
        "        area.setAttribute('stroke', colour); area.setAttribute('stroke-width','1');"
        "        svg.appendChild(area);"
        "      }"
        "      if(split){"
        "        var dcol = desat(r.color);"
        "        halfArea(r.kde, -1, dcol);"
        "        halfArea(r.kde2, 1, r.color);"
        "        if(r.median >= y0 && r.median <= y1) ln(cx - colW * 0.3, sy(r.median), cx, sy(r.median), dcol, '1.5');"
        "        if(r.median2 >= y0 && r.median2 <= y1) ln(cx, sy(r.median2), cx + colW * 0.3, sy(r.median2), r.color, '1.5');"
        "        var qa1 = Math.max(r.q1, y0), qb1 = Math.min(r.q3, y1);"
        "        if(qb1 > qa1) ln(cx - 2, sy(qa1), cx - 2, sy(qb1), dcol, '2', null, '0.9');"
        "        var qa2 = Math.max(r.q12, y0), qb2 = Math.min(r.q32, y1);"
        "        if(qb2 > qa2) ln(cx + 2, sy(qa2), cx + 2, sy(qb2), r.color, '2', null, '0.9');"
        "        var ym1 = sy(Math.max(y0, Math.min(y1, r.median)));"
        "        var ym2 = sy(Math.max(y0, Math.min(y1, r.median2)));"
        "        if(Math.abs(ym2 - ym1) > 0.5){"
        "          ln(cx, ym1, cx, ym2, '#0f172a', '1');"
        "          var adir = ym2 > ym1 ? 1 : -1;"
        "          ln(cx, ym2, cx - 3, ym2 - adir * 4, '#0f172a', '1');"
        "          ln(cx, ym2, cx + 3, ym2 - adir * 4, '#0f172a', '1');"
        "        }"
        "        var dvv = r.median2 - r.median;"
        "        txt(cx + 5, (ym1 + ym2) / 2 + 3, 'Δ' + (dvv >= 0 ? '+' : '') + dvv.toFixed(3), 'start', '#0f172a', '8');"
        "      } else {"
        "        var kde = (r.kde || []).filter(function(p){ return p[0] >= y0 && p[0] <= y1; });"
        "        if(r.count > 0 && r.count < SMALL_N){"
        "          (r.samples || []).forEach(function(sv, si){"
        "            if(sv < y0 || sv > y1) return;"
        "            var jx = cx + (((si * 7) % 11) / 11 - 0.5) * hw * 1.1;"
        "            var dot = document.createElementNS(ns,'circle');"
        "            dot.setAttribute('cx', jx.toFixed(1)); dot.setAttribute('cy', sy(sv).toFixed(1));"
        "            dot.setAttribute('r','1.7'); dot.setAttribute('fill', r.color); dot.setAttribute('fill-opacity','0.75');"
        "            svg.appendChild(dot);"
        "          });"
        "        } else if(kde.length > 1 && maxDen > 0){"
        "          var path = '';"
        "          kde.forEach(function(p, k){"
        "            path += (k === 0 ? 'M' : 'L') + (cx + p[1] / maxDen * hw).toFixed(1) + ',' + sy(p[0]).toFixed(1);"
        "          });"
        "          for(var k = kde.length - 1; k >= 0; k--){"
        "            path += 'L' + (cx - kde[k][1] / maxDen * hw).toFixed(1) + ',' + sy(kde[k][0]).toFixed(1);"
        "          }"
        "          path += 'Z';"
        "          var area = document.createElementNS(ns,'path');"
        "          area.setAttribute('d', path);"
        "          area.setAttribute('fill', r.color); area.setAttribute('fill-opacity','0.4');"
        "          area.setAttribute('stroke', r.color); area.setAttribute('stroke-width','1');"
        "          svg.appendChild(area);"
        "        }"
        "        var sig = Math.abs(r.median) >= lod;"
        "        if(r.median >= y0 && r.median <= y1){"
        "          ln(cx - colW * 0.3, sy(r.median), cx + colW * 0.3, sy(r.median), sig ? r.color : '#94a3b8', sig ? '1.5' : '1', sig ? null : '2,2');"
        "          if(!sig) txt(cx + colW * 0.3 + 1, sy(r.median) + 3, 'n.s.', 'start', '#94a3b8', '7');"
        "        }"
        "        var qa = Math.max(r.q1, y0), qb = Math.min(r.q3, y1);"
        "        if(qb > qa) ln(cx, sy(qa), cx, sy(qb), r.color, '2.5', null, '0.9');"
        "      }"
        "    }"
        "    if(!mini) txt(cx, H - 4, grey ? '–' : ((r.far ? 'far · ' : '') + 'n=' + r.count), 'middle', grey ? '#94a3b8' : (r.far ? '#b45309' : '#475569'), '8');"
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
        // A3 range brush (only when the 3D map is on): drag a y-interval on the
        // plot to highlight the contributing surface band in 3D.
        "    var brushRect = document.createElementNS(ns,'rect');"
        "    brushRect.setAttribute('x', axisW); brushRect.setAttribute('width', svgW - axisW);"
        "    brushRect.setAttribute('fill', accent); brushRect.setAttribute('fill-opacity','0.16');"
        "    brushRect.setAttribute('stroke', accent); brushRect.setAttribute('stroke-opacity','0.6'); brushRect.setAttribute('stroke-width','1');"
        "    brushRect.style.display = 'none'; svg.appendChild(brushRect);"
        "    var brushing = false, brushY0 = 0, brushMoved = false;"
        "    svg.addEventListener('pointerdown', function(ev){"
        "      if(!d.brushon) return;"
        "      var rc = svg.getBoundingClientRect();"
        "      var x = ev.clientX - rc.left, y = ev.clientY - rc.top;"
        "      if(y >= plotY0 && y <= plotY1 && x >= axisW && !ev.altKey && !ev.shiftKey){"
        "        brushing = true; brushMoved = false; brushY0 = y;"
        "        try { svg.setPointerCapture(ev.pointerId); } catch(e) {}"
        "      }"
        "    });"
        "    svg.addEventListener('pointerenter', function(){ el._hovering = true; });"
        "    svg.addEventListener('pointermove', function(ev){"
        "      var rc = svg.getBoundingClientRect();"
        "      var x = ev.clientX - rc.left, y = ev.clientY - rc.top;"
        "      if(brushing){"
        "        var yy = Math.max(plotY0, Math.min(plotY1, y));"
        "        var lo = Math.min(brushY0, yy), hi = Math.max(brushY0, yy);"
        "        if(Math.abs(yy - brushY0) > 3) brushMoved = true;"
        "        brushRect.setAttribute('y', lo); brushRect.setAttribute('height', hi - lo); brushRect.style.display = '';"
        "        setCursor(null); hoverRect.style.display = 'none';"
        "        queue('brush|' + fromY(hi).toFixed(4) + '|' + fromY(lo).toFixed(4));"
        "        return;"
        "      }"
        "      el._hovering = true;"
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
        "    svg.addEventListener('pointerup', function(ev){"
        "      if(brushing){"
        "        brushing = false;"
        "        try { svg.releasePointerCapture(ev.pointerId); } catch(e) {}"
        "        if(!brushMoved){ brushRect.style.display = 'none'; send('brushclear'); }"
        "      }"
        "    });"
        "    svg.addEventListener('pointerleave', function(){"
        "      el._hovering = false; hoverRect.style.display = 'none';"
        "      applyCursorAttr(); queue('out');"
        "    });"
        "    svg.addEventListener('click', function(ev){"
        "      if(brushMoved){ brushMoved = false; return; }"
        "      var rc = svg.getBoundingClientRect();"
        "      var x = ev.clientX - rc.left, y = ev.clientY - rc.top;"
        "      var ci = colAt(x);"
        "      if(y >= plotY0 && y <= plotY1 && ci >= 0){"
        "        lastSent = '';"
        "        if(ev.altKey) send('lock|' + fromY(y).toFixed(4));"
        "        else if(ev.shiftKey) send('apick|' + fromY(y).toFixed(4) + '|' + rows[ci].id);"
        "        else send('click|' + rows[ci].id);"
        "      }"
        "    });"
        "    if(!el._docClick){"
        "      el._docClick = function(ev){"
        "        if(!document.contains(el)){ document.removeEventListener('click', el._docClick); el._docClick = null; return; }"
        // Clear the sticky column only when the click lands outside the whole
        // probe section — so the chart's own header controls (3D map, slice)
        // don't count as 'click outside' and wipe the soloed column.
        "        var box = el.closest('.pc-probe') || el;"
        "        if(!box.contains(ev.target)) send('clickout');"
        "      };"
        "      document.addEventListener('click', el._docClick);"
        "    }"
        "  }"
        "  el.appendChild(svg);"
    ]

    // Three-source stacked bar for a data-srcs = [d,a,c] attribute.
    let probeBarJs = [
        "  if(!d || d.length < 3) return;"
        "  var labels = ['Dataset error (sensor / reconstruction)','Algorithm residual (registration, correlated across the mesh)','Local conditioning (geometric observability of the marker)'];"
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

    // preview: the probe under the effective preview transforms while a
    // registration solve is pending — rows become paired half-violins
    // (committed left, preview right) with a median-shift arrow.
    let probeRidgeJson (mini : bool) (brushOn : bool) (sticky : string option) (colors : Map<string, C4b>) (order : HashMap<string, int>) (preview : ProbeResult option) (r : ProbeResult) =
        // y-range is always auto; columns are always sorted by significance.
        let win =
            match preview with
            | Some p -> Range1d(min r.XAuto.Min p.XAuto.Min, max r.XAuto.Max p.XAuto.Max)
            | None -> r.XAuto
        let colorHex name =
            match Map.tryFind name colors with
            | Some c -> c4bToHex c
            | None -> "#1a56db"
        let rows =
            r.Distributions |> Array.sortBy (fun d -> (if d.Count = 0 then 1 else 0), abs d.Median)
        let appendKde (sb : System.Text.StringBuilder) (kde : (float * float)[]) =
            kde |> Array.iteri (fun j (x, y) ->
                if j > 0 then sb.Append(',') |> ignore
                sb.Append(sprintf "[%.4g,%.4g]" x y) |> ignore)
        let appendSamples (sb : System.Text.StringBuilder) (s : float[]) =
            s |> Array.iteri (fun j x ->
                if j > 0 then sb.Append(',') |> ignore
                sb.Append(sprintf "%.4g" x) |> ignore)
        // σ_ref = the reference mesh's roughness (std of its re-centred
        // distances); feeds the per-mesh detection-limit band lod95 =
        // 1.96·√(σ_ref² + σ_mesh²).
        let refStd =
            r.Distributions |> Array.tryFind (fun d -> d.MeshName = r.ReferenceMesh)
            |> Option.map (fun d -> d.Std) |> Option.defaultValue 0.0
        let sb = System.Text.StringBuilder()
        sb.Append(sprintf "{\"status\":\"ready\",\"mini\":%b,\"brushon\":%b,\"ymin\":%.5g,\"ymax\":%.5g,\"refstd\":%.5g,\"sticky\":\"%s\",\"rows\":["
                    mini brushOn win.Min win.Max refStd (sticky |> Option.defaultValue "")) |> ignore
        // F5: a surface caught only because the 20 m probe cylinder is long —
        // its samples sit far down the axis from the pin centre — is flagged
        // non-local (axial offset from centre = RefOffset + median), so it is
        // not read as real local disagreement.
        let halfLen = r.Length * 0.5
        rows |> Array.iteri (fun i d ->
            if i > 0 then sb.Append(',') |> ignore
            let far =
                d.MeshName <> r.ReferenceMesh && halfLen > 1e-6
                && abs (d.Median + r.RefOffset) > 0.6 * halfLen
            sb.Append(sprintf "{\"id\":\"%s\",\"name\":\"%s\",\"color\":\"%s\",\"count\":%d,\"median\":%.5g,\"q1\":%.5g,\"q3\":%.5g,\"std\":%.5g,\"far\":%b,\"samples\":["
                        d.MeshName (numbered order d.MeshName) (colorHex d.MeshName) d.Count d.Median d.Q1 d.Q3 d.Std far) |> ignore
            appendSamples sb d.Samples
            sb.Append("],\"kde\":[") |> ignore
            appendKde sb d.Kde
            sb.Append("]") |> ignore
            match preview |> Option.bind (fun p -> p.Distributions |> Array.tryFind (fun pd -> pd.MeshName = d.MeshName)) with
            | Some pd when pd.Count > 0 ->
                sb.Append(sprintf ",\"count2\":%d,\"median2\":%.5g,\"q12\":%.5g,\"q32\":%.5g,\"kde2\":["
                            pd.Count pd.Median pd.Q1 pd.Q3) |> ignore
                appendKde sb pd.Kde
                sb.Append("]") |> ignore
            | _ -> ()
            sb.Append("}") |> ignore)
        sb.Append("]}") |> ignore
        sb.ToString()

    let probeStateJson (mini : bool) (brushOn : bool) (sticky : string option) (colors : Map<string, C4b>) (order : HashMap<string, int>) (preview : ProbeResult option) (probe : ProbeState) =
        match probe with
        | ProbeReady r -> probeRidgeJson mini brushOn sticky colors order preview r
        | ProbeError e -> sprintf "{\"status\":\"error\",\"reason\":\"%s\"}" (e.Replace("\\", "/").Replace("\"", "'"))
        | ProbeNone | ProbeRunning -> "{\"status\":\"running\"}"

    let private parseInvariant (s : string) =
        match System.Double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture) with
        | true, v -> Some v
        | _ -> None

    let pinCardBody (env : Env<Message>) (model : AdaptiveModel) (selectedPin : aval<ScanPin option>) (hoverWorld : aval<V3d option>) (patchHover : cval<PatchHover option>) =
        let isPoint = AVal.constant true
        let showOnly = Primitives.showWhen

        let readoutText = selectedPin |> AVal.map (function
            | Some p -> sprintf "(%.2f, %.2f, %.2f) m · R %.2f m" p.Centre.X p.Centre.Y p.Centre.Z p.InnerRadius
            | None -> "—")
        div {
            Class "pin-card-body"

            div {
                Class "pin-card-section pin-card-point"
                showOnly isPoint
                div {
                    Class "pc-readout"
                    div {
                        Class "pc-readout-row"
                        span { Class "pc-val"; readoutText }
                    }
                }
                // M3C2 probe: ridgeline, x-range / lock-order controls,
                // three-source stacked bar.
                let probe =
                    selectedPin |> AVal.map (function
                        | Some p -> Some p.Probe
                        | None -> None)
                let probeResult =
                    probe |> AVal.map (function
                        | Some (ProbeReady r) -> Some r
                        | _ -> None)
                let previewActive = model.PendingReg |> AVal.map PendingRegistration.isPreview
                // §5 NUM condition: violin chart replaced by an RMS table,
                // split-violin preview and three-source bar hidden.
                let violinOn = StudyGate.featureOn model "violinChart"
                let barOn = StudyGate.featureOn model "threeSourceBar"
                let meshOrderMap = model.MeshOrder.Content
                let previewSplit =
                    (previewActive, StudyGate.featureOn model "splitViolinPreview") ||> AVal.map2 (&&)
                let probeJson =
                    let selOrder =
                        (selectedPin, meshOrderMap, model.SurfaceDistOn) |||> AVal.map3 (fun po ord sd -> po, ord, sd)
                    (selOrder, model.ChartStickyMesh, previewSplit) |||> AVal.map3 (fun (po, order, brushOn) sticky pv ->
                        match po with
                        | Some pin ->
                            // Split violin while a solve preview is pending and
                            // the preview probe is in.
                            let preview =
                                if pv then
                                    match pin.ProbePreview with
                                    | ProbeReady r -> Some r
                                    | _ -> None
                                else None
                            probeStateJson false brushOn sticky pin.DatasetColors order preview pin.Probe
                        | None -> "{\"status\":\"none\"}")
                // 3D → chart: the elevation cursor line at the 3D hover
                // point's signed distance along the probe axis, shown only
                // while the hover point sits inside the probe cylinder.
                // Under a pending preview the preview-pose probe wins, so the
                // linking matches the on-screen geometry.
                let cursor3d =
                    (hoverWorld, selectedPin, previewActive) |||> AVal.map3 (fun hw po pv ->
                        match hw, po with
                        | Some q, Some pin ->
                            match ScanPin.effectiveProbe pv pin with
                            | ProbeReady r ->
                                let v = q - pin.Centre
                                let dAx = Vec.dot v r.Normal
                                let radial = (v - r.Normal * dAx).Length
                                if radial <= pin.InnerRadius && abs dAx <= r.Length * 0.5
                                then sprintf "{\"d\":%.4f}" (dAx - r.RefOffset)
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
                    // Shift+click on mesh M's column at signed distance d:
                    // anchors[M] = refAnchor + d·probeAxis (ViolinAxial,
                    // accepted). Only with correspondence enabled, never on
                    // the reference column.
                    | "apick" when parts.Length >= 3 ->
                        // Anchors are committed-pose world points — picking
                        // one against previewed geometry would get
                        // double-transformed on commit, so block like the
                        // other pickers.
                        if AVal.force previewActive then
                            env.Emit [ShowToast "Correspondence-marker picking is disabled while a solve preview is pending"]
                        else
                            match AVal.force selectedPin, parseInvariant parts.[1] with
                            | Some pin, Some dv when parts.[2] <> "" ->
                                match ScanPin.correspondence pin with
                                | Some c when c.Enabled
                                              && (AVal.force model.Registration).ReferenceMesh <> Some parts.[2] ->
                                    let refA = c.RefAnchor |> Option.defaultValue pin.Centre
                                    let axis =
                                        match pin.Probe with
                                        | ProbeReady r -> r.Normal
                                        | _ -> ScanPin.axis pin
                                    env.Emit [SetAnchor(pin.Id, parts.[2], refA + axis * dv, AnchorViolinAxial)]
                                | _ -> ()
                            | _ -> ()
                    | "clickout" ->
                        env.Emit [ClearChartSticky]
                    // A3 range brush: a y-interval on the violin → highlight the
                    // contributing band on the soloed mesh's surface in 3D.
                    | "brush" when parts.Length >= 3 ->
                        match parseInvariant parts.[1], parseInvariant parts.[2] with
                        | Some lo, Some hi -> env.Emit [SetSurfaceDistBrush (Some (lo, hi))]
                        | _ -> ()
                    | "brushclear" ->
                        env.Emit [SetSurfaceDistBrush None]
                    // Alt-click the plot → lock the iso-plane (SectionCap)
                    // into ClipPlanes so it survives orbiting.
                    | "lock" when parts.Length >= 2 ->
                        match parseInvariant parts.[1] with
                        | Some dv -> env.Emit [LockIsoPlane dv]
                        | None -> ()
                    | _ -> ()
                let sources =
                    probeResult |> AVal.map (Option.map (fun r -> r.Sources))
                // B1: a plain-language significance verdict per moving mesh,
                // read against the detection limit (not eyeballed off the band).
                let lodVerdict =
                    (probeResult, meshOrderMap) ||> AVal.map2 (fun rr order ->
                        match rr with
                        | Some r ->
                            let refStd =
                                r.Distributions |> Array.tryFind (fun d -> d.MeshName = r.ReferenceMesh)
                                |> Option.map (fun d -> d.Std) |> Option.defaultValue 0.0
                            r.Distributions
                            |> Array.filter (fun d -> d.Count > 0 && d.MeshName <> r.ReferenceMesh)
                            |> Array.map (fun d ->
                                let lod = 1.96 * sqrt (refStd*refStd + d.Std*d.Std)
                                let isSig = abs d.Median >= lod
                                let txt =
                                    if isSig then sprintf "%s  %+.2f m — significant" (numbered order d.MeshName) d.Median
                                    else sprintf "%s  within noise (n.s.)" (numbered order d.MeshName)
                                txt, isSig)
                            |> IndexList.ofArray
                        | None -> IndexList.empty)
                div {
                    Class "pc-probe"
                    div {
                        Class "pc-probe-head"
                        span { Class "pc-section-title"; "Distance probe" }
                        // Iso-plane sectioning: clip above the hovered plane;
                        // Alt-click the chart locks it (survives orbit).
                        button {
                            Class "tb-gear-btn"
                            showOnly violinOn
                            model.ClipAboveIso |> AVal.map (fun on -> if on then Some (Class "btn-active") else None)
                            Attribute("title", "Slice: while hovering the chart, clip the meshes above the iso-plane. Alt-click the chart to lock it.")
                            Dom.OnClick(fun _ -> env.Emit [ToggleClipAboveIso])
                            "⊟ slice"
                        }
                        // A2: paint the soloed mesh's signed distance in 3D.
                        button {
                            Class "tb-gear-btn"
                            showOnly violinOn
                            model.SurfaceDistOn |> AVal.map (fun on -> if on then Some (Class "btn-active") else None)
                            Attribute("title", "Paint signed distance on the surface in 3D — click a violin column to pick the mesh (per-mesh diverging map, 0 = reference, near-zero = neutral)")
                            Dom.OnClick(fun _ -> env.Emit [ToggleSurfaceDistance])
                            "⬢ 3D map"
                        }
                    }
                    input {
                        Class "pc-ridge-bus"
                        Attribute("type", "text")
                        Dom.OnInput(fun e -> onChartEvent e.Value)
                    }
                    // Channel legend folded into a hover tooltip on the chart
                    // (no always-on descriptive text line).
                    div {
                        Class "pc-ridge"
                        showOnly violinOn
                        Attribute("title", "y = signed distance along the reference's local surface normal (0 = reference). Width = precision / roughness (shared density scale). Median tick = bias. Grey band = ±LoD95 detection limit; a median inside it is not significant (n.s.). Two lobes = two surfaces, not noise.")
                        probeJson |> AVal.map (fun j -> Some (Attribute("data-ridge", j)))
                        cursor3d |> AVal.map (fun j -> Some (Attribute("data-cursor", j)))
                        Primitives.observedRender "data-ridge" "{}" ridgelineJs
                    }
                    // B1: explicit band label + per-mesh verdict.
                    div {
                        Class "pc-lod-legend"
                        showOnly violinOn
                        span { Class "pc-lod-swatch" }
                        span { "±LoD₉₅ detection limit — a median inside the band is not significant" }
                    }
                    div {
                        Class "pc-verdict"
                        showOnly violinOn
                        lodVerdict |> AList.ofAVal |> AList.map (fun (txt, isSig) ->
                            div { Class (if isSig then "pc-verdict-sig" else "pc-verdict-ns"); txt })
                    }
                    // B3: keep the two readings distinct — significance vs residual.
                    div {
                        Class "pc-verdict-cap"
                        showOnly violinOn
                        "Band = change significance. Alignment quality is the RMS residual in the Registration panel."
                    }
                    // NUM replacement: per-mesh signed-distance numbers from
                    // the same probe, plus registration RMS before/after.
                    div {
                        Class "pc-rms-table"
                        Primitives.showWhenNot violinOn
                        div {
                            Class "pc-rms-head"
                            span { Class "pc-rms-cell pc-rms-mesh"; "mesh" }
                            span { Class "pc-rms-cell"; "median" }
                            span { Class "pc-rms-cell"; "IQR" }
                            span { Class "pc-rms-cell"; "n" }
                        }
                        probeResult
                        |> AVal.map (fun r ->
                            match r with
                            | Some r ->
                                r.Distributions
                                |> Array.map (fun d -> d.MeshName, d.Median, d.Q3 - d.Q1, d.Count)
                                |> IndexList.ofArray
                            | None -> IndexList.empty)
                        |> AList.ofAVal
                        |> AList.map (fun (name, median, iqr, count) ->
                            div {
                                Class "pc-rms-row"
                                span { Class "pc-rms-cell pc-rms-mesh"; meshOrderMap |> AVal.map (fun o -> numbered o name) }
                                span { Class "pc-rms-cell"; sprintf "%+.3f m" median }
                                span { Class "pc-rms-cell"; sprintf "%.3f m" iqr }
                                span { Class "pc-rms-cell"; string count }
                            })
                        div {
                            Class "pc-rms-reg"
                            (model.PendingReg, model.RegistrationLog, meshOrderMap) |||> AVal.map3 (fun pending log order ->
                                let rows =
                                    match pending with
                                    | Some pr when not (Map.isEmpty pr.Results) ->
                                        pr.Results |> Map.toList
                                        |> List.map (fun (m, r) -> m, r.RmsBefore, r.RmsAfter, "pending")
                                    | _ ->
                                        match log with
                                        | step :: _ ->
                                            step.Outputs |> Map.toList
                                            |> List.map (fun (m, o) -> m, o.RmsBefore, o.RmsAfter, "committed")
                                        | [] -> []
                                match rows with
                                | [] -> "no registration solve yet"
                                | rows ->
                                    rows
                                    |> List.map (fun (m, b, a, tag) ->
                                        sprintf "%s: RMS %.3f → %.3f m (%s)" (numbered order m) b a tag)
                                    |> String.concat " · ")
                        }
                    }
                    div {
                        Class "pc-probe-caption"
                        (probeResult, meshOrderMap) ||> AVal.map2 (fun rr order ->
                            match rr with
                            | Some r -> sprintf "ref %s" (numbered order r.ReferenceMesh)
                            | None -> "")
                    }
                    div {
                        Class "pc-bar"
                        showOnly barOn
                        sources |> AVal.map (function
                            | Some s -> Some (Attribute("data-srcs", sprintf "[%.6g,%.6g,%.6g]" s.DatasetError s.AlgorithmResid s.LocalConditioning))
                            | None -> Some (Attribute("data-srcs", "[]")))
                        Primitives.observedRender "data-srcs" "[]" probeBarJs
                    }
                    div {
                        Class "pc-bar-legend"
                        showOnly barOn
                        span { Class "pc-legend-item pc-bar-dataset"; "Dataset" }
                        span { Class "pc-legend-item pc-bar-algorithm"; "Algorithm" }
                        span { Class "pc-legend-item pc-bar-conditioning"; "Conditioning" }
                    }
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
                        span {
                            Class "pc-section-title"
                            // glossary kept as hover help, not an always-on line
                            Attribute("title", "A correspondence is one real spot in the world, marked on each mesh by a correspondence marker point (one per mesh). Making a pin a registration pin gathers those markers and feeds them to the solve.")
                            "Correspondence"
                        }
                    }
                    // The one-click toggle: registration pin ⟺ has a correspondence.
                    Primitives.compactToggle "Make this a registration pin" corrEnabled (fun () ->
                        emitForPinTop ToggleCorrespondence)
                    div {
                        Class "pc-corr-body"
                        showOnly corrEnabled
                        div {
                            Class "pc-corr-hint"
                            "Registration needs ≥3 registration pins, each with a marker on every moving mesh."
                        }
                        div {
                            Class "pc-corr-ref"
                            (corr, selectedPin) ||> AVal.map2 (fun cOpt po ->
                                match cOpt, po with
                                | Some c, Some pin when c.RefAnchor.IsSome && c.RefDistance > 2.0 * pin.InnerRadius ->
                                    Some (Class "pc-corr-ref-warn")
                                | _ -> None)
                            span {
                                (corr, selectedPin) ||> AVal.map2 (fun cOpt po ->
                                    match cOpt, po with
                                    | Some c, Some pin ->
                                        match c.RefAnchor with
                                        | Some _ when c.RefDistance > 2.0 * pin.InnerRadius ->
                                            sprintf "⚠ reference marker %.2f m off the pin (> 2× radius)" c.RefDistance
                                        | Some _ when c.RefDistance > 0.0 ->
                                            sprintf "reference marker projected, Δ %.3f m" c.RefDistance
                                        | Some _ -> "reference marker = pin centre"
                                        | None -> "no reference marker yet — designate a ★ reference mesh"
                                    | _ -> "")
                            }
                            // F10: the reference marker is editable too — pick it
                            // on the reference mesh in 3D. F8: re-click cancels.
                            let refPickActive =
                                (model.AnchorPick, selectedPin, refMeshOpt) |||> AVal.map3 (fun ap sp rm ->
                                    match ap, sp, rm with
                                    | Some a, Some p, Some refMesh -> a.PinId = p.Id && a.Mesh = refMesh
                                    | _ -> false)
                            button {
                                Class "mb"
                                Primitives.showWhen (refMeshOpt |> AVal.map Option.isSome)
                                refPickActive |> AVal.map (fun on -> if on then Some (Class "btn-active") else None)
                                Attribute("title", "Pick / move the reference marker in 3D — click the reference mesh (click again or Esc to cancel)")
                                Dom.OnClick(fun _ ->
                                    match AVal.force selectedPin, AVal.force refMeshOpt with
                                    | Some p, Some refMesh ->
                                        if AVal.force refPickActive then env.Emit [CancelAnchorPick]
                                        else env.Emit [StartAnchorPick(p.Id, refMesh)]
                                    | _ -> ())
                                "⊕"
                            }
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
                                    // Hovering the whole row highlights this mesh's
                                    // correspondence marker in 3D (thick + bright).
                                    Dom.OnMouseEnter(fun _ ->
                                        match AVal.force selectedPin with
                                        | Some p -> env.Emit [SetCorrMarkerHover (Some (p.Id, mesh))]
                                        | None -> ())
                                    Dom.OnMouseLeave(fun _ -> env.Emit [SetCorrMarkerHover None])
                                    span { Class "pc-corr-mesh"; meshOrderMap |> AVal.map (fun o -> numbered o mesh) }
                                    span {
                                        Class "pc-corr-acc"
                                        anchor |> AVal.map (function
                                            | Some _ -> Some (Class "pc-corr-acc-on")
                                            | None -> None)
                                        anchor |> AVal.map (function
                                            | Some a -> sprintf "✓ %s" (AnchorSource.label a.Source)
                                            | None -> "—")
                                    }
                                    span {
                                        Class "pc-corr-res"
                                        residual |> AVal.map (function
                                            | Some r -> sprintf "%.3f m" r
                                            | None -> "")
                                    }
                                    let pickActive =
                                        (model.AnchorPick, selectedPin) ||> AVal.map2 (fun ap sp ->
                                            match ap, sp with
                                            | Some a, Some p -> a.PinId = p.Id && a.Mesh = mesh
                                            | _ -> false)
                                    button {
                                        // F8: re-click cancels the live pick (toggle).
                                        Class "mb"
                                        pickActive |> AVal.map (fun on -> if on then Some (Class "btn-active") else None)
                                        Attribute("title", "Pick this correspondence marker in 3D — one click on this mesh (click again or Esc to cancel)")
                                        Dom.OnClick(fun _ ->
                                            match AVal.force selectedPin with
                                            | Some p ->
                                                if AVal.force pickActive then env.Emit [CancelAnchorPick]
                                                else env.Emit [StartAnchorPick(p.Id, mesh)]
                                            | None -> ())
                                        "⊕"
                                    }
                                })
                        }
                        // F14: surface the patch picker — the occlusion-free way
                        // to fix markers on overlapping meshes.
                        div {
                            Class "pc-corr-pick-hint"
                            "Overlap hiding a marker? Pick it in 2D patches — nothing overlaps there."
                        }
                        div {
                            Class "pc-corr-actions"
                            button {
                                Class "tb-gear-btn pc-pick-patches"
                                Attribute("title", "Pick correspondence markers in co-oriented surface patches")
                                Dom.OnClick(fun _ -> emitForPinTop OpenPatchPicker)
                                "▦ Pick in patches"
                            }
                            // Marker↔reference rulers (distance / residual labels).
                            button {
                                Class "tb-gear-btn"
                                model.RulerActive |> AVal.map (fun on -> if on then Some (Class "btn-active") else None)
                                Attribute("title", "Rulers: label each correspondence marker↔reference distance (the pair gap; shrinks to the residual after a solve)")
                                Dom.OnClick(fun _ -> env.Emit [ToggleRuler])
                                "📏 Rulers"
                            }
                        }
                        // Patch small-multiples picker: one orthographic
                        // footprint per visible mesh in the shared reference
                        // frame; clicking sets that mesh's anchor.
                        let pickerOpen =
                            (model.PatchPicker, selectedPin) ||> AVal.map2 (fun pp po ->
                                match pp, po with
                                | Some p, Some pin -> p.PinId = pin.Id
                                | _ -> false)
                        let pickerShaded =
                            model.PatchPicker |> AVal.map (function
                                | Some p -> p.Shaded
                                | None -> false)
                        let pickerJson =
                            let pickOrder = (model.PatchPicker, meshOrderMap) ||> AVal.map2 (fun pp ord -> pp, ord)
                            (pickOrder, selectedPin, model.Registration) |||> AVal.map3 (fun (pp, order) po reg ->
                                match pp, po with
                                | Some p, Some pin when p.PinId = pin.Id ->
                                    if p.Running then "{\"status\":\"running\"}"
                                    elif List.isEmpty p.Entries then "{\"status\":\"none\"}"
                                    else
                                        let colorHex name =
                                            match Map.tryFind name pin.DatasetColors with
                                            | Some c -> c4bToHex c
                                            | None -> "#1a56db"
                                        let sb = System.Text.StringBuilder()
                                        sb.Append(sprintf "{\"status\":\"ready\",\"r\":%.4g,\"shaded\":%b,\"entries\":["
                                                    p.Radius p.Shaded) |> ignore
                                        p.Entries |> List.iteri (fun i e ->
                                            if i > 0 then sb.Append(',') |> ignore
                                            let isRef = reg.ReferenceMesh = Some e.Mesh
                                            sb.Append(sprintf "{\"id\":\"%s\",\"mesh\":\"%s\",\"color\":\"%s\",\"ref\":%b,\"atlas\":\"%s\",\"cross\":[%.5g,%.5g],\"tris\":["
                                                        e.Mesh (numbered order e.Mesh) (colorHex e.Mesh) isRef e.AtlasUrl
                                                        e.Crosshair.X e.Crosshair.Y) |> ignore
                                            e.Triangles |> Array.iteri (fun j t ->
                                                if j > 0 then sb.Append(',') |> ignore
                                                sb.Append(t) |> ignore)
                                            sb.Append("],\"pts\":[") |> ignore
                                            e.Points |> Array.iteri (fun j (uv, h, atlasUv) ->
                                                if j > 0 then sb.Append(',') |> ignore
                                                sb.Append(sprintf "[%.5g,%.5g,%.5g,%.5g,%.5g]"
                                                            uv.X uv.Y h atlasUv.X atlasUv.Y) |> ignore)
                                            sb.Append("]}") |> ignore)
                                        sb.Append("]}") |> ignore
                                        sb.ToString()
                                | _ -> "{\"status\":\"none\"}")
                        // Bus protocol from the cell JS:
                        //   pk|mesh|u|v|h            click pick (h = barycentric height on the hit triangle)
                        //   hv|mesh|cx|cy|z[|u|v|h]  hovered cell + its pan/zoom viewport, optional live cursor
                        //   out                      pointer left the cell
                        // pk goes through the reducer; hv/out only touch the
                        // view-local cval (no reducer churn on pointer moves).
                        let setPatchHover (next : PatchHover option) =
                            if patchHover.Value <> next then
                                transact (fun () -> patchHover.Value <- next)
                        let onPatchEvent (v : string) =
                            let parts = v.Split('|')
                            if parts.Length = 0 then () else
                            match parts.[0] with
                            | "pk" when parts.Length >= 5 ->
                                match parseInvariant parts.[2], parseInvariant parts.[3], parseInvariant parts.[4] with
                                | Some u, Some vv, Some h -> env.Emit [PatchPickerClick(parts.[1], u, vv, h)]
                                | _ -> ()
                            | "hv" when parts.Length >= 5 ->
                                match parseInvariant parts.[2], parseInvariant parts.[3], parseInvariant parts.[4] with
                                | Some cx, Some cy, Some z ->
                                    let point =
                                        if parts.Length >= 8 then
                                            match parseInvariant parts.[5], parseInvariant parts.[6], parseInvariant parts.[7] with
                                            | Some u, Some vv, Some h -> Some (V2d(u, vv), h)
                                            | _ -> None
                                        else None
                                    setPatchHover (Some { Mesh = parts.[1]; Centre = V2d(cx, cy); Zoom = z; Point = point })
                                | _ -> ()
                            | "out" -> setPatchHover None
                            | _ -> ()
                        div {
                            Class "pc-patchpicker"
                            showOnly pickerOpen
                            div {
                                Class "pc-probe-head"
                                span { Class "pc-section-title"; "Patch picker" }
                                button {
                                    Class "mb"
                                    Attribute("title", "Toggle textured / shaded-height rendering")
                                    Dom.OnClick(fun _ -> env.Emit [TogglePatchShaded])
                                    pickerShaded |> AVal.map (fun s -> if s then "height" else "texture")
                                }
                                button {
                                    Class "mb"
                                    Attribute("title", "Close patch picker")
                                    Dom.OnClick(fun _ -> env.Emit [ClosePatchPicker])
                                    "✕"
                                }
                            }
                            input {
                                Class "pc-patch-bus"
                                Attribute("type", "text")
                                Dom.OnInput(fun e -> onPatchEvent e.Value)
                            }
                            div {
                                Class "pc-patch-grid"
                                pickerJson |> AVal.map (fun j -> Some (Attribute("data-patches", j)))
                                Primitives.observedRender "data-patches" "{}" [
                                    // Canvas small-multiples: textured/shaded triangles, restricted
                                    // pan/zoom per cell, triangle hit-test picking and 2D↔3D hover
                                    // linking. Per-mesh viewport state survives re-renders on
                                    // el.__ppv; two stacked canvases per cell (base = surface,
                                    // overlay = cursor/vertex marks) so pointer moves never redraw
                                    // the triangles.
                                    "  function placeholder(t){ var p = document.createElement('div'); p.className = 'pin-card-empty'; p.textContent = t; el.appendChild(p); }"
                                    "  if(!d.status || d.status === 'none'){ return; }"
                                    "  if(d.status === 'running'){ placeholder('Sampling patches…'); return; }"
                                    "  var entries = d.entries || [];"
                                    "  if(entries.length === 0){ placeholder('No patches.'); return; }"
                                    "  var hmin = Infinity, hmax = -Infinity;"
                                    "  entries.forEach(function(e){ e.pts.forEach(function(p){ if(p[2] < hmin) hmin = p[2]; if(p[2] > hmax) hmax = p[2]; }); });"
                                    "  if(!(hmax > hmin)){ hmin = -0.5; hmax = 0.5; }"
                                    // F17: perceptually-uniform sequential ramp (viridis)
                                    // instead of the old magenta->blue gradient.
                                    "  var VIR = [[68,1,84],[59,82,139],[33,145,140],[94,201,98],[253,231,37]];"
                                    "  function hcol(h){"
                                    "    var t = Math.max(0, Math.min(1, (h - hmin) / (hmax - hmin)));"
                                    "    var x = t * 4, i = Math.min(3, Math.floor(x)), f = x - i;"
                                    "    var a = VIR[i], b = VIR[i+1];"
                                    "    return 'rgb(' + Math.round(a[0]+(b[0]-a[0])*f) + ',' + Math.round(a[1]+(b[1]-a[1])*f) + ',' + Math.round(a[2]+(b[2]-a[2])*f) + ')';"
                                    "  }"
                                    "  var send = function(s){"
                                    "    var pr = el.closest('.pc-patchpicker');"
                                    "    var b = pr ? pr.querySelector('.pc-patch-bus') : null;"
                                    "    if(b){ b.value = s; b.dispatchEvent(new Event('input', {bubbles:true})); }"
                                    "  };"
                                    "  var lastHv = '', hvQueued = null, hvRaf = 0;"
                                    "  var sendHv = function(s){"
                                    "    hvQueued = s;"
                                    "    if(!hvRaf){ hvRaf = requestAnimationFrame(function(){"
                                    "      hvRaf = 0;"
                                    "      if(hvQueued !== null && hvQueued !== lastHv){ lastHv = hvQueued; send(hvQueued); }"
                                    "    }); }"
                                    "  };"
                                    "  var views = el.__ppv = el.__ppv || {};"
                                    "  var cells = [];"
                                    "  var ghost = null;"
                                    "  var ACC = '#0891b2';"
                                    "  entries.forEach(function(e){"
                                    // F18: first view fits the populated footprint, not the
                                    // full box — zoom so the farthest sampled vertex reaches
                                    // the circle edge.
                                    "    var st = views[e.id];"
                                    "    if(!st){ var pr = 0; (e.pts||[]).forEach(function(p){ var l = Math.hypot(p[0], p[1]); if(l > pr) pr = l; }); var fz = pr > 1e-6 ? d.r / pr : 1; fz = Math.max(1, Math.min(12, fz)); st = views[e.id] = {cx:0, cy:0, z:fz}; }"
                                    "    var wrap = document.createElement('div');"
                                    "    wrap.className = 'pc-patch-cell' + (e.ref ? ' pc-patch-cell-ref' : '');"
                                    "    var head = document.createElement('div');"
                                    "    head.className = 'pc-patch-head';"
                                    "    var sw = document.createElement('span');"
                                    "    sw.className = 'pc-patch-swatch'; sw.style.background = e.color;"
                                    "    head.appendChild(sw);"
                                    "    var nm = document.createElement('span');"
                                    "    nm.textContent = e.mesh + (e.ref ? ' ★' : '');"
                                    "    head.appendChild(nm);"
                                    "    var zl = document.createElement('span');"
                                    "    zl.className = 'pc-patch-zoom';"
                                    "    zl.title = 'reset zoom';"
                                    "    head.appendChild(zl);"
                                    "    wrap.appendChild(head);"
                                    "    var size = 124, pad = 6, maxR = size / 2 - pad, c0 = size / 2;"
                                    "    var dpr = window.devicePixelRatio || 1;"
                                    "    var box = document.createElement('div');"
                                    "    box.className = 'pc-patch-box';"
                                    "    box.style.width = size + 'px'; box.style.height = size + 'px';"
                                    "    function mkCanvas(){"
                                    "      var cv = document.createElement('canvas');"
                                    "      cv.width = Math.round(size * dpr); cv.height = Math.round(size * dpr);"
                                    "      cv.className = 'pc-patch-canvas';"
                                    "      cv.style.width = size + 'px'; cv.style.height = size + 'px';"
                                    "      box.appendChild(cv);"
                                    "      var g = cv.getContext('2d');"
                                    "      g.setTransform(dpr, 0, 0, dpr, 0, 0);"
                                    "      return g;"
                                    "    }"
                                    "    var gb = mkCanvas(), gt = mkCanvas();"
                                    "    wrap.appendChild(box);"
                                    "    el.appendChild(wrap);"
                                    "    wrap.title = 'scroll = zoom, drag = pan, click the zoom label to reset' + (e.ref ? '' : ', click = set marker');"
                                    "    var order = [];"
                                    "    var tr3 = e.tris || [];"
                                    "    for(var i = 0; i + 2 < tr3.length; i += 3){ order.push([tr3[i], tr3[i+1], tr3[i+2]]); }"
                                    "    order.sort(function(a, b){"
                                    "      return (e.pts[a[0]][2] + e.pts[a[1]][2] + e.pts[a[2]][2]) - (e.pts[b[0]][2] + e.pts[b[1]][2] + e.pts[b[2]][2]);"
                                    "    });"
                                    // F15: an atlas that fails to load (CORS / decode / 0-size)
                                    // must fall back to shaded height, never a black cell.
                                    "    var img = null;"
                                    "    if(e.atlas){ var im = new Image(); im.onload = function(){ if(im.width > 0 && im.height > 0) img = im; requestDraw(); }; im.onerror = function(){ img = null; requestDraw(); }; im.src = e.atlas; }"
                                    "    function k(){ return maxR / d.r * st.z; }"
                                    "    function sx(u){ return c0 + (u - st.cx) * k(); }"
                                    "    function sy(v){ return c0 - (v - st.cy) * k(); }"
                                    "    function toData(px, py){ return [(px - c0) / k() + st.cx, st.cy - (py - c0) / k()]; }"
                                    "    function clampView(){"
                                    "      if(st.z < 1) st.z = 1; if(st.z > 12) st.z = 12;"
                                    "      var m = d.r * (1 - 1 / st.z);"
                                    "      var l = Math.hypot(st.cx, st.cy);"
                                    "      if(l > m){ var f = l > 0 ? m / l : 0; st.cx *= f; st.cy *= f; }"
                                    "    }"
                                    "    clampView();"
                                    "    function flatTri(x0, y0, x1, y1, x2, y2, col){"
                                    "      gb.beginPath(); gb.moveTo(x0, y0); gb.lineTo(x1, y1); gb.lineTo(x2, y2); gb.closePath();"
                                    "      gb.fillStyle = col; gb.fill();"
                                    "      gb.strokeStyle = col; gb.lineWidth = 0.6; gb.stroke();"
                                    "    }"
                                    "    function drawBase(){"
                                    "      gb.clearRect(0, 0, size, size);"
                                    // F16: clip the surface to the pin footprint circle and hatch
                                    // the uncovered area, so partial overlap reads as 'no coverage
                                    // here', not 'not drawn'.
                                    "      gb.save();"
                                    "      gb.beginPath(); gb.arc(sx(0), sy(0), d.r * k(), 0, 6.2832); gb.clip();"
                                    "      gb.fillStyle = '#f1f5f9'; gb.fillRect(0, 0, size, size);"
                                    "      gb.strokeStyle = '#e2e8f0'; gb.lineWidth = 1;"
                                    "      for(var hx = -size; hx < size * 2; hx += 8){ gb.beginPath(); gb.moveTo(hx, 0); gb.lineTo(hx - size, size); gb.stroke(); }"
                                    "      var shaded = d.shaded || !img;"
                                    "      order.forEach(function(tr){"
                                    "        var p0 = e.pts[tr[0]], p1 = e.pts[tr[1]], p2 = e.pts[tr[2]];"
                                    "        var x0 = sx(p0[0]), y0 = sy(p0[1]), x1 = sx(p1[0]), y1 = sy(p1[1]), x2 = sx(p2[0]), y2 = sy(p2[1]);"
                                    "        if(Math.max(x0, x1, x2) < 0 || Math.max(y0, y1, y2) < 0 || Math.min(x0, x1, x2) > size || Math.min(y0, y1, y2) > size) return;"
                                    "        if(shaded){ flatTri(x0, y0, x1, y1, x2, y2, hcol((p0[2] + p1[2] + p2[2]) / 3)); return; }"
                                    "        var W = img.width, H = img.height;"
                                    "        var u0 = p0[3] * W, v0 = (1 - p0[4]) * H, u1 = p1[3] * W, v1 = (1 - p1[4]) * H, u2 = p2[3] * W, v2 = (1 - p2[4]) * H;"
                                    "        var du = Math.max(Math.abs(p0[3] - p1[3]), Math.abs(p0[3] - p2[3]), Math.abs(p0[4] - p1[4]), Math.abs(p0[4] - p2[4]));"
                                    "        var den = (u1 - u0) * (v2 - v0) - (u2 - u0) * (v1 - v0);"
                                    "        if(du > 0.25 || Math.abs(den) < 1e-6){ flatTri(x0, y0, x1, y1, x2, y2, hcol((p0[2] + p1[2] + p2[2]) / 3)); return; }"
                                    "        var gx = (x0 + x1 + x2) / 3, gy = (y0 + y1 + y2) / 3, s = 1.025;"
                                    "        var a = ((x1 - x0) * (v2 - v0) - (x2 - x0) * (v1 - v0)) / den;"
                                    "        var b = ((x2 - x0) * (u1 - u0) - (x1 - x0) * (u2 - u0)) / den;"
                                    "        var c = ((y1 - y0) * (v2 - v0) - (y2 - y0) * (v1 - v0)) / den;"
                                    "        var f = ((y2 - y0) * (u1 - u0) - (y1 - y0) * (u2 - u0)) / den;"
                                    "        gb.save();"
                                    "        gb.beginPath();"
                                    "        gb.moveTo(gx + (x0 - gx) * s, gy + (y0 - gy) * s);"
                                    "        gb.lineTo(gx + (x1 - gx) * s, gy + (y1 - gy) * s);"
                                    "        gb.lineTo(gx + (x2 - gx) * s, gy + (y2 - gy) * s);"
                                    "        gb.closePath(); gb.clip();"
                                    "        gb.transform(a, c, b, f, x0 - a * u0 - b * v0, y0 - c * u0 - f * v0);"
                                    "        gb.drawImage(img, 0, 0);"
                                    "        gb.restore();"
                                    "      });"
                                    "      if(order.length === 0){"
                                    "        e.pts.forEach(function(p){"
                                    "          var x = sx(p[0]), y = sy(p[1]);"
                                    "          if(x < -2 || x > size + 2 || y < -2 || y > size + 2) return;"
                                    "          gb.beginPath(); gb.arc(x, y, 1.7, 0, 6.2832); gb.fillStyle = hcol(p[2]); gb.fill();"
                                    "        });"
                                    "      }"
                                    "      gb.restore();"
                                    "      gb.beginPath(); gb.arc(sx(0), sy(0), d.r * k(), 0, 6.2832);"
                                    "      gb.strokeStyle = e.ref ? '#b45309' : '#cbd5e1'; gb.lineWidth = e.ref ? 2 : 1; gb.stroke();"
                                    "      var chx = sx(e.cross[0]), chy = sy(e.cross[1]);"
                                    "      gb.strokeStyle = '#0f172a'; gb.lineWidth = 1.2;"
                                    "      gb.beginPath(); gb.moveTo(chx - 6, chy); gb.lineTo(chx + 6, chy); gb.moveTo(chx, chy - 6); gb.lineTo(chx, chy + 6); gb.stroke();"
                                    "      zl.textContent = st.z > 1.001 ? st.z.toFixed(1) + '×' : '';"
                                    "    }"
                                    "    var hovered = false, cursor = null, panning = null;"
                                    "    function hitTri(u, v){"
                                    "      for(var i = order.length - 1; i >= 0; i--){"
                                    "        var tr = order[i];"
                                    "        var p0 = e.pts[tr[0]], p1 = e.pts[tr[1]], p2 = e.pts[tr[2]];"
                                    "        var d1 = (u - p1[0]) * (p0[1] - p1[1]) - (p0[0] - p1[0]) * (v - p1[1]);"
                                    "        var d2 = (u - p2[0]) * (p1[1] - p2[1]) - (p1[0] - p2[0]) * (v - p2[1]);"
                                    "        var d3 = (u - p0[0]) * (p2[1] - p0[1]) - (p2[0] - p0[0]) * (v - p0[1]);"
                                    "        if(((d1 < 0) || (d2 < 0) || (d3 < 0)) && ((d1 > 0) || (d2 > 0) || (d3 > 0))) continue;"
                                    "        var den = (p1[1] - p2[1]) * (p0[0] - p2[0]) + (p2[0] - p1[0]) * (p0[1] - p2[1]);"
                                    "        if(Math.abs(den) < 1e-12) continue;"
                                    "        var w0 = ((p1[1] - p2[1]) * (u - p2[0]) + (p2[0] - p1[0]) * (v - p2[1])) / den;"
                                    "        var w1 = ((p2[1] - p0[1]) * (u - p2[0]) + (p0[0] - p2[0]) * (v - p2[1])) / den;"
                                    "        return p0[2] * w0 + p1[2] * w1 + p2[2] * (1 - w0 - w1);"
                                    "      }"
                                    "      return null;"
                                    "    }"
                                    "    function drawTop(){"
                                    "      gt.clearRect(0, 0, size, size);"
                                    "      if(ghost && ghost.mesh !== e.id){"
                                    "        var gx2 = sx(ghost.u), gy2 = sy(ghost.v);"
                                    "        gt.strokeStyle = 'rgba(8,145,178,0.4)'; gt.lineWidth = 1;"
                                    "        gt.beginPath(); gt.moveTo(gx2 - 5, gy2); gt.lineTo(gx2 + 5, gy2); gt.moveTo(gx2, gy2 - 5); gt.lineTo(gx2, gy2 + 5); gt.stroke();"
                                    "      }"
                                    "      if(!hovered) return;"
                                    "      gt.fillStyle = 'rgba(15,23,42,0.35)';"
                                    "      e.pts.forEach(function(p){"
                                    "        var x = sx(p[0]), y = sy(p[1]);"
                                    "        if(x >= 0 && x <= size && y >= 0 && y <= size) gt.fillRect(x - 0.7, y - 0.7, 1.4, 1.4);"
                                    "      });"
                                    "      if(cursor){"
                                    "        var x = sx(cursor[0]), y = sy(cursor[1]);"
                                    "        gt.strokeStyle = ACC; gt.lineWidth = 1.4;"
                                    "        gt.beginPath(); gt.moveTo(x - 7, y); gt.lineTo(x + 7, y); gt.moveTo(x, y - 7); gt.lineTo(x, y + 7); gt.stroke();"
                                    "        gt.beginPath(); gt.arc(x, y, 3, 0, 6.2832); gt.stroke();"
                                    "        gt.fillStyle = ACC; gt.font = '10px sans-serif';"
                                    "        gt.fillText('Δh ' + (cursor[2] >= 0 ? '+' : '') + cursor[2].toFixed(3) + ' m', 6, size - 6);"
                                    "      }"
                                    "    }"
                                    "    var dirty = false;"
                                    "    function requestDraw(){"
                                    "      if(!dirty){ dirty = true; requestAnimationFrame(function(){ dirty = false; drawBase(); drawTop(); }); }"
                                    "    }"
                                    "    function viewStr(){ return e.id + '|' + st.cx.toFixed(5) + '|' + st.cy.toFixed(5) + '|' + st.z.toFixed(3); }"
                                    "    function hvSend(){"
                                    "      if(!hovered) return;"
                                    "      if(cursor) sendHv('hv|' + viewStr() + '|' + cursor[0].toFixed(5) + '|' + cursor[1].toFixed(5) + '|' + cursor[2].toFixed(5));"
                                    "      else sendHv('hv|' + viewStr());"
                                    "    }"
                                    "    function setGhost(){"
                                    "      ghost = (hovered && cursor) ? {mesh: e.id, u: cursor[0], v: cursor[1]} : null;"
                                    "      cells.forEach(function(c){ if(c.id !== e.id) c.top(); });"
                                    "    }"
                                    "    var ev = gt.canvas;"
                                    "    if(!e.ref) ev.style.cursor = 'crosshair';"
                                    "    ev.addEventListener('pointerenter', function(){ hovered = true; hvSend(); drawTop(); });"
                                    "    ev.addEventListener('pointerleave', function(){"
                                    "      hovered = false; cursor = null; hvQueued = null; lastHv = '';"
                                    "      send('out'); setGhost(); drawTop();"
                                    "    });"
                                    "    ev.addEventListener('pointermove', function(evt){"
                                    "      var rc = ev.getBoundingClientRect();"
                                    "      if(panning){"
                                    "        var dx = evt.clientX - panning.x, dy = evt.clientY - panning.y;"
                                    "        if(panning.moved || Math.abs(dx) + Math.abs(dy) > 3){"
                                    "          panning.moved = true;"
                                    "          ev.style.cursor = 'grabbing';"
                                    "          st.cx -= dx / k(); st.cy += dy / k();"
                                    "          panning.x = evt.clientX; panning.y = evt.clientY;"
                                    "          clampView(); requestDraw(); hvSend();"
                                    "        }"
                                    "        return;"
                                    "      }"
                                    "      var uv = toData(evt.clientX - rc.left, evt.clientY - rc.top);"
                                    "      var h = hitTri(uv[0], uv[1]);"
                                    "      cursor = h === null ? null : [uv[0], uv[1], h];"
                                    "      hvSend(); setGhost(); drawTop();"
                                    "    });"
                                    "    ev.addEventListener('pointerdown', function(evt){"
                                    "      if(evt.button !== 0) return;"
                                    "      panning = {x: evt.clientX, y: evt.clientY, moved: false};"
                                    "      ev.setPointerCapture(evt.pointerId);"
                                    "    });"
                                    "    ev.addEventListener('pointerup', function(evt){"
                                    "      var wasPan = panning && panning.moved;"
                                    "      panning = null;"
                                    "      ev.style.cursor = e.ref ? '' : 'crosshair';"
                                    "      try{ ev.releasePointerCapture(evt.pointerId); }catch(err){}"
                                    "      if(wasPan || e.ref) return;"
                                    "      var rc = ev.getBoundingClientRect();"
                                    "      var uv = toData(evt.clientX - rc.left, evt.clientY - rc.top);"
                                    "      var h = hitTri(uv[0], uv[1]);"
                                    "      if(h !== null) send('pk|' + e.id + '|' + uv[0].toFixed(5) + '|' + uv[1].toFixed(5) + '|' + h.toFixed(5));"
                                    "    });"
                                    "    ev.addEventListener('wheel', function(evt){"
                                    "      evt.preventDefault();"
                                    "      var rc = ev.getBoundingClientRect();"
                                    "      var px = evt.clientX - rc.left, py = evt.clientY - rc.top;"
                                    "      var before = toData(px, py);"
                                    "      st.z = Math.max(1, Math.min(12, st.z * Math.exp(-evt.deltaY * 0.002)));"
                                    "      st.cx = before[0] - (px - c0) / k(); st.cy = before[1] + (py - c0) / k();"
                                    "      clampView(); requestDraw();"
                                    "      var uv = toData(px, py);"
                                    "      var h = hitTri(uv[0], uv[1]);"
                                    "      cursor = h === null ? null : [uv[0], uv[1], h];"
                                    "      hvSend(); setGhost();"
                                    "    }, {passive: false});"
                                    // Reset lives on the zoom label, NOT on dblclick — a
                                    // double-click on a pickable cell would fire the anchor
                                    // pick twice before the reset.
                                    "    zl.addEventListener('click', function(){"
                                    "      st.cx = 0; st.cy = 0; st.z = 1;"
                                    "      clampView(); requestDraw(); hvSend();"
                                    "    });"
                                    "    cells.push({id: e.id, top: drawTop});"
                                    "    drawBase(); drawTop();"
                                    "  });"
                                ]
                            }
                        }
                    }
                }
            }

        }
