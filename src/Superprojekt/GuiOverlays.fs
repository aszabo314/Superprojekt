namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open FSharp.Data.Adaptive
open Aardvark.Dom

module GuiOverlays =

    let persistenceBridge (env : Env<Message>) =
        div {
            Class "persistence-bridge"
            Style [Display "none"]
            input {
                Attribute("type", "file")
                Attribute("id", "ws-file-picker")
                Attribute("accept", ".json,.scanpin.json,application/json")
            }
            input {
                Attribute("type", "text")
                Attribute("id", "ws-load-sink")
                Dom.OnInput(fun e ->
                    if not (System.String.IsNullOrEmpty e.Value) then
                        env.Emit [LoadWorkspace e.Value])
            }
            OnBoot [
                "(function(){"
                "var fp = document.getElementById('ws-file-picker');"
                "var sink = document.getElementById('ws-load-sink');"
                "if (!fp || !sink) return;"
                "fp.addEventListener('change', function(){"
                "  var f = fp.files && fp.files[0]; if (!f) return;"
                "  var r = new FileReader();"
                "  r.onload = function(){"
                "    sink.value = r.result;"
                "    sink.dispatchEvent(new Event('input', {bubbles: true}));"
                "    fp.value = '';"
                "  };"
                "  r.readAsText(f);"
                "});"
                "})();"
            ]
        }

    let meshWheelLabel (model : AdaptiveModel) (cursorScreen : aval<V2d option>) =
        div {
            Class "mesh-wheel-label"
            (model.ActivePickingLayer, cursorScreen) ||> AVal.map2 (fun layer cOpt ->
                match layer, cOpt with
                | Some _, Some pos ->
                    Some (Style [
                        Left (sprintf "%.0fpx" (pos.X + 14.0))
                        Top  (sprintf "%.0fpx" (pos.Y - 10.0))
                    ])
                | _ -> Some (Style [Display "none"]))
            model.ActivePickingLayer |> AVal.map (function
                | Some name -> Cards.shortName name
                | None -> "")
        }

    let lassoOverlay (env : Env<Message>) (model : AdaptiveModel) (cursorScreen : aval<V2d option>) =
        let stateJson =
            (model.LassoDrawing, cursorScreen, model.LassoVolume) |||> AVal.map3 (fun drawing cursor committed ->
                let drawingArr =
                    match drawing with
                    | Some d -> d.Vertices
                    | None -> [||]
                let cursorArr =
                    match cursor with
                    | Some c -> [| c |]
                    | None -> [||]
                let committedArr =
                    match committed with
                    | Some v -> v.ScreenPolygon
                    | None -> [||]
                let fmtArr (a : V2d[]) =
                    a |> Array.map (fun p -> sprintf "[%.1f,%.1f]" p.X p.Y) |> String.concat ","
                sprintf "{\"d\":[%s],\"c\":[%s],\"k\":[%s]}"
                    (fmtArr drawingArr) (fmtArr cursorArr) (fmtArr committedArr))
        div {
            Class "lasso-overlay"
            stateJson |> AVal.map (fun j -> Some (Attribute("data-lasso", j)))
            OnBoot [
                "(function(){"
                "var el = __THIS__;"
                "var last = '';"
                "var ns = 'http://www.w3.org/2000/svg';"
                "function poly(points, attrs){"
                "  var p = document.createElementNS(ns, 'polyline');"
                "  p.setAttribute('points', points.map(function(pt){return pt[0]+','+pt[1];}).join(' '));"
                "  for(var k in attrs) p.setAttribute(k, attrs[k]);"
                "  return p;"
                "}"
                "function render(){"
                "  var raw = el.getAttribute('data-lasso') || '{}';"
                "  if(raw === last) return; last = raw;"
                "  try { var d = JSON.parse(raw); } catch(e) { return; }"
                "  el.innerHTML = '';"
                "  var svg = document.createElementNS(ns, 'svg');"
                "  svg.setAttribute('class','lasso-svg');"
                "  var rect = el.getBoundingClientRect();"
                "  svg.setAttribute('width', rect.width);"
                "  svg.setAttribute('height', rect.height);"
                "  el.appendChild(svg);"
                "  if(d.k && d.k.length >= 3){"
                "    var k = d.k.slice(); k.push(k[0]);"
                "    svg.appendChild(poly(k, {stroke:'#1a56db','stroke-width':'1.5','stroke-dasharray':'4,3',fill:'rgba(26,86,219,0.04)'}));"
                "  }"
                "  if(d.d && d.d.length > 0){"
                "    if(d.d.length >= 2)"
                "      svg.appendChild(poly(d.d, {stroke:'#0f172a','stroke-width':'1.5',fill:'none'}));"
                "    d.d.forEach(function(pt){"
                "      var c = document.createElementNS(ns, 'circle');"
                "      c.setAttribute('cx', pt[0]); c.setAttribute('cy', pt[1]);"
                "      c.setAttribute('r','3'); c.setAttribute('fill','#0f172a');"
                "      svg.appendChild(c);"
                "    });"
                "    if(d.c && d.c.length > 0){"
                "      var last = d.d[d.d.length-1];"
                "      var line = document.createElementNS(ns, 'line');"
                "      line.setAttribute('x1', last[0]); line.setAttribute('y1', last[1]);"
                "      line.setAttribute('x2', d.c[0][0]); line.setAttribute('y2', d.c[0][1]);"
                "      line.setAttribute('stroke','#0f172a'); line.setAttribute('stroke-width','1');"
                "      line.setAttribute('stroke-dasharray','4,4');"
                "      svg.appendChild(line);"
                "    }"
                "  }"
                "}"
                "render();"
                "new MutationObserver(render).observe(el,{attributes:true,attributeFilter:['data-lasso']});"
                "})();"
            ]
        }

    let private niceRoundDistance (d : float) =
        if d <= 0.0 || System.Double.IsNaN d || System.Double.IsInfinity d then 1.0
        else
            let steps = [| 0.1; 0.2; 0.5; 1.0; 2.0; 5.0; 10.0; 20.0; 50.0; 100.0; 200.0; 500.0; 1000.0 |]
            let mag = 10.0 ** floor (log10 d)
            let norm = d / mag
            let picked =
                if norm < 0.15 then 0.1
                elif norm < 0.35 then 0.2
                elif norm < 0.75 then 0.5
                elif norm < 1.5 then 1.0
                elif norm < 3.5 then 2.0
                elif norm < 7.5 then 5.0
                else 10.0
            let v = picked * mag
            steps |> Array.minBy (fun s -> abs (log (s / v)))

    let private formatMeters (m : float) =
        if m >= 1000.0 then sprintf "%g km" (m / 1000.0)
        elif m >= 1.0 then sprintf "%g m" m
        else sprintf "%g cm" (m * 100.0)

    let scaleBar (model : AdaptiveModel) (viewportSize : aval<V2i>) =
        let targetPx = 100.0
        let barInfo =
            AVal.custom (fun t ->
                let radius = model.Camera.radius.GetValue t
                let vp = viewportSize.GetValue t
                let ds = model.ActiveDataset.GetValue t
                let scales = model.DatasetScales.GetValue t
                let scale =
                    match ds with
                    | Some d -> Map.tryFind d scales |> Option.defaultValue 1.0
                    | None -> 1.0
                let h = max 1 vp.Y
                let verticalFov = 90.0 * Constant.RadiansPerDegree
                let renderPerPixel = 2.0 * tan (verticalFov * 0.5) * radius / float h
                let realAt100 = targetPx * renderPerPixel / scale
                let nice = niceRoundDistance realAt100
                let px = nice * scale / renderPerPixel
                let px = if System.Double.IsNaN px || System.Double.IsInfinity px then targetPx else max 10.0 (min 400.0 px)
                px, formatMeters nice)
        let barPx = barInfo |> AVal.map fst
        let barLabel = barInfo |> AVal.map snd
        div {
            Class "scale-bar"
            div {
                Class "sb-bar"
                barPx |> AVal.map (fun px -> Some (Style [Width (sprintf "%.0fpx" px)]))
                span { Class "sb-cap sb-cap-l" }
                span { Class "sb-line" }
                span { Class "sb-cap sb-cap-r" }
            }
            div { Class "sb-label"; barLabel }
        }

    let orientationIndicator (model : AdaptiveModel) =
        let axisJson =
            model.Camera.view |> AVal.map (fun cv ->
                let vt = CameraView.viewTrafo cv
                let tr (v : V3d) = vt.Forward.TransformDir v
                let x = tr V3d.IOO
                let y = tr V3d.OIO
                let z = tr V3d.OOI
                let fmt (v : V3d) (name : string) (color : string) =
                    sprintf "{\"x\":%f,\"y\":%f,\"z\":%f,\"n\":\"%s\",\"c\":\"%s\"}" v.X v.Y v.Z name color
                sprintf "[%s,%s,%s]"
                    (fmt x "X" "#dc2626")
                    (fmt y "Y" "#16a34a")
                    (fmt z "Z" "#2563eb"))
        div {
            Class "orient-indicator"
            axisJson |> AVal.map (fun json -> Some (Attribute("data-axes", json)))
            OnBoot [
                "(function(){"
                "var el = __THIS__;"
                "var last = '';"
                "var ns = 'http://www.w3.org/2000/svg';"
                "var W = 60, H = 60, L = 22, cx = W/2, cy = H/2;"
                "function render() {"
                "  var raw = el.getAttribute('data-axes') || '[]';"
                "  if(raw === last) return; last = raw;"
                "  try { var arr = JSON.parse(raw); } catch(e) { return; }"
                "  el.innerHTML = '';"
                "  var svg = document.createElementNS(ns, 'svg');"
                "  svg.setAttribute('width', W); svg.setAttribute('height', H);"
                "  svg.setAttribute('viewBox', '0 0 ' + W + ' ' + H);"
                "  arr.sort(function(a,b){return a.z - b.z;});"
                "  arr.forEach(function(a){"
                "    var ex = cx + a.x * L, ey = cy - a.y * L;"
                "    var ln = document.createElementNS(ns, 'line');"
                "    ln.setAttribute('x1', cx); ln.setAttribute('y1', cy);"
                "    ln.setAttribute('x2', ex); ln.setAttribute('y2', ey);"
                "    ln.setAttribute('stroke', a.c); ln.setAttribute('stroke-width','2');"
                "    ln.setAttribute('stroke-linecap','round');"
                "    svg.appendChild(ln);"
                "    if(a.z > -0.2){"
                "      var tx = document.createElementNS(ns, 'text');"
                "      tx.setAttribute('x', cx + a.x * (L + 6));"
                "      tx.setAttribute('y', cy - a.y * (L + 6) + 3);"
                "      tx.setAttribute('fill', a.c);"
                "      tx.setAttribute('font-size','9');"
                "      tx.setAttribute('font-family','monospace');"
                "      tx.setAttribute('text-anchor','middle');"
                "      tx.textContent = a.n;"
                "      svg.appendChild(tx);"
                "    }"
                "  });"
                "  el.appendChild(svg);"
                "}"
                "render();"
                "new MutationObserver(render).observe(el, {attributes:true,attributeFilter:['data-axes']});"
                "})();"
            ]
        }

    let fullscreenInfo (model : AdaptiveModel) =
        div {
            Class "fullscreen-info"
            model.FullscreenOn |> AVal.map (fun on ->
                if not on then Some (Style [Display "none"]) else None)
            model.ActiveDataset |> AVal.map (fun ds ->
                match ds with
                | Some d -> div { Class "fullscreen-info-title"; d }
                | None   -> div { []  })
            model.MeshNames |> AList.map (fun name ->
                let order = model.MeshOrder |> AMap.tryFind name |> AVal.map (Option.defaultValue 0)
                div {
                    order |> AVal.map (fun o -> sprintf "%d  %s" (o + 1) (Cards.shortName name))
                })
        }
