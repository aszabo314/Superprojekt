namespace PinDemo

open Aardvark.Base
open Superprojekt

module SvgDiagram =

    let private c4bToHex (c : C4b) =
        sprintf "#%02x%02x%02x" c.R c.G c.B

    let private shortName (name : string) =
        let mesh =
            let s = name.IndexOf('/')
            if s >= 0 then name.[s + 1 ..] else name
        mesh

    let svgWidth, svgHeight, padding = 280.0, 180.0, 30.0

    /// Encode a ScanPin's CutResults polylines as a JSON blob consumed by the
    /// browser-side renderer in `bootJs`. Mirrors GuiPins.encodeDiagramJson.
    let encodeDiagramJson (pin : ScanPin) =
        let svgW, svgH, pad = svgWidth, svgHeight, padding
        let results = pin.CutResults |> Map.toList
        if results.IsEmpty then "{\"paths\":[],\"legend\":[]}"
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
            let toSvgX x = pad + (x - xMin) / xRange * (svgW - 2.0 * pad)
            let toSvgY y = (svgH - pad) - (y - yMin) / yRange * (svgH - 2.0 * pad)
            let paths =
                results |> List.collect (fun (name, cr) ->
                    let color =
                        pin.DatasetColors
                        |> Map.tryFind name
                        |> Option.defaultValue (C4b(100uy,100uy,100uy))
                        |> c4bToHex
                    cr.Polylines |> List.map (fun pts ->
                        let d =
                            pts
                            |> List.mapi (fun i (p : V2d) ->
                                let cmd = if i = 0 then "M" else "L"
                                sprintf "%s%.1f,%.1f" cmd (toSvgX p.X) (toSvgY p.Y))
                            |> String.concat " "
                        sprintf "{\"d\":\"%s\",\"c\":\"%s\"}" (d.Replace("\"","\\\"")) color))
            let legend =
                results |> List.map (fun (name, _) ->
                    let color =
                        pin.DatasetColors
                        |> Map.tryFind name
                        |> Option.defaultValue (C4b(100uy,100uy,100uy))
                        |> c4bToHex
                    sprintf "{\"n\":\"%s\",\"c\":\"%s\"}" (shortName name) color)
            sprintf "{\"paths\":[%s],\"legend\":[%s],\"xMin\":%.4g,\"xMax\":%.4g,\"yMin\":%.4g,\"yMax\":%.4g}"
                (paths |> String.concat ",") (legend |> String.concat ",") xMin xMax yMin yMax

    /// Boot JavaScript that reads `data-diagram` from the host element and renders an SVG.
    let bootJs : string list =
        let svgW, svgH, pad = svgWidth, svgHeight, padding
        [
            "var last = '';"
            "function render() {"
            "  var el = document.getElementById('pin-diagram-root');"
            "  if(!el) return;"
            "  var raw = el.getAttribute('data-diagram') || '{}';"
            "  if(raw === last) return;"
            "  last = raw;"
            "  el.innerHTML = '';"
            "  try { var data = JSON.parse(raw); } catch(e) { return; }"
            "  if(!data.paths || data.paths.length===0) return;"
            "  var ns = 'http://www.w3.org/2000/svg';"
            "  var svg = document.createElementNS(ns, 'svg');"
            sprintf "  svg.setAttribute('width', '%.0f');" svgW
            sprintf "  svg.setAttribute('height', '%.0f');" svgH
            sprintf "  svg.setAttribute('viewBox', '0 0 %.0f %.0f');" svgW svgH
            "  svg.style.background = '#fafbfc';"
            "  svg.style.border = '1px solid #e2e8f0';"
            "  svg.style.borderRadius = '4px';"
            "  svg.style.display = 'block';"
            "  el.appendChild(svg);"
            "  var ax = document.createElementNS(ns, 'line');"
            sprintf "  ax.setAttribute('x1','%.0f'); ax.setAttribute('y1','%.0f');" pad (svgH - pad)
            sprintf "  ax.setAttribute('x2','%.0f'); ax.setAttribute('y2','%.0f');" (svgW - pad) (svgH - pad)
            "  ax.setAttribute('stroke','#94a3b8'); ax.setAttribute('stroke-width','1');"
            "  svg.appendChild(ax);"
            "  var ay = document.createElementNS(ns, 'line');"
            sprintf "  ay.setAttribute('x1','%.0f'); ay.setAttribute('y1','%.0f');" pad pad
            sprintf "  ay.setAttribute('x2','%.0f'); ay.setAttribute('y2','%.0f');" pad (svgH - pad)
            "  ay.setAttribute('stroke','#94a3b8'); ay.setAttribute('stroke-width','1');"
            "  svg.appendChild(ay);"
            "  function addText(x,y,txt,anchor) {"
            "    var t = document.createElementNS(ns, 'text');"
            "    t.setAttribute('x',x); t.setAttribute('y',y);"
            "    t.setAttribute('font-size','9'); t.setAttribute('fill','#64748b');"
            "    t.setAttribute('font-family','system-ui,sans-serif');"
            "    if(anchor) t.setAttribute('text-anchor',anchor);"
            "    t.textContent = txt; svg.appendChild(t);"
            "  }"
            "  if(data.xMin!==undefined) {"
            sprintf "    addText(%.0f, %.0f, data.xMin.toFixed(1), 'start');" pad (svgH - pad + 12.0)
            sprintf "    addText(%.0f, %.0f, data.xMax.toFixed(1), 'end');" (svgW - pad) (svgH - pad + 12.0)
            sprintf "    addText(%.0f, %.0f, data.yMax.toFixed(1), 'end');" (pad - 4.0) (pad + 3.0)
            sprintf "    addText(%.0f, %.0f, data.yMin.toFixed(1), 'end');" (pad - 4.0) (svgH - pad + 3.0)
            "  }"
            "  var tip = document.createElementNS(ns, 'text');"
            "  tip.setAttribute('x','5'); tip.setAttribute('y','12');"
            "  tip.setAttribute('font-size','10'); tip.setAttribute('fill','#0f172a');"
            "  tip.setAttribute('font-family','system-ui,sans-serif');"
            "  tip.setAttribute('font-weight','600');"
            "  tip.style.pointerEvents = 'none';"
            "  svg.appendChild(tip);"
            "  var allPaths = [];"
            "  data.paths.forEach(function(item,idx) {"
            "    var p = document.createElementNS(ns, 'path');"
            "    p.setAttribute('d', item.d);"
            "    p.setAttribute('stroke', item.c);"
            "    p.setAttribute('stroke-width', '2');"
            "    p.setAttribute('fill', 'none');"
            "    p.style.cursor = 'pointer';"
            "    p.setAttribute('stroke-opacity', '0.85');"
            "    allPaths.push(p);"
            "    var leg = data.legend && data.legend[idx] ? data.legend[idx] : null;"
            "    p.addEventListener('mouseenter', function() {"
            "      allPaths.forEach(function(q) { q.setAttribute('stroke-opacity', q===p?'1':'0.2'); q.setAttribute('stroke-width', q===p?'3':'1.5'); });"
            "      if(leg) tip.textContent = leg.n;"
            "    });"
            "    p.addEventListener('mouseleave', function() {"
            "      allPaths.forEach(function(q) { q.setAttribute('stroke-opacity','0.85'); q.setAttribute('stroke-width','2'); });"
            "      tip.textContent = '';"
            "    });"
            "    svg.appendChild(p);"
            "  });"
            "}"
            "setInterval(render, 200);"
        ]
