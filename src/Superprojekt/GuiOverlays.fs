namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open FSharp.Data.Adaptive
open Aardvark.Dom

module GuiOverlays =

    let meshWheelLabel (model : AdaptiveModel) (cursorScreen : aval<V2d option>) =
        let meshOrderMap = model.MeshOrder.Content
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
            (model.ActivePickingLayer, meshOrderMap) ||> AVal.map2 (fun layer order ->
                match layer with
                | Some name -> Primitives.numbered order name
                | None -> "")
        }

    // Show-overlays hold (§T8): a 2D name tag per pin, floating at its flag-pole tip
    // (ScanPin.flagTopRender projected to CSS px every frame). DOM, not Sg.Text, so
    // the tags keep a constant readable size; overlap is accepted. Same 90° horizontal
    // fov as the main projection — only the NDC x/y matter here, near/far don't.
    let pinFlagLabels (model : AdaptiveModel) (viewportSize : aval<V2i>) =
        let pinsVal = model.ScanPins.Pins |> AMap.toAVal
        let labelsJson =
            AVal.custom (fun t ->
                if not (model.ShowOverlaysHeld.GetValue t) then "[]"
                else
                    let pins  = pinsVal.GetValue t
                    let cc    = model.CommonCentroid.GetValue t
                    let scale = DatasetScale.active (model.ActiveDataset.GetValue t) (model.DatasetScales.GetValue t)
                    let vp    = viewportSize.GetValue t
                    let viewT = model.Camera.view.GetValue t |> CameraView.viewTrafo
                    let projT =
                        Frustum.perspective 90.0 1.0 5000.0 (float vp.X / float (max 1 vp.Y))
                        |> Frustum.projTrafo
                    let vpFwd = (viewT * projT).Forward
                    let items =
                        HashMap.toList pins |> List.choose (fun (_, p) ->
                            let h = vpFwd * V4d(ScanPin.flagTopRender cc scale p, 1.0)
                            if h.W <= 1e-6 then None
                            else
                                let ndc = h.XYZ / h.W
                                if abs ndc.X > 1.1 || abs ndc.Y > 1.1 then None
                                else
                                    Some (sprintf "{\"x\":%.1f,\"y\":%.1f,\"n\":\"%s %s\",\"c\":\"%s\"}"
                                            ((ndc.X * 0.5 + 0.5) * float vp.X)
                                            ((0.5 - ndc.Y * 0.5) * float vp.Y)
                                            p.Glyph p.ShortName (Primitives.c4bToHex p.PinColor)))
                    "[" + String.concat "," items + "]")
        div {
            Class "pin-flag-labels"
            Primitives.showWhen model.ShowOverlaysHeld
            labelsJson |> AVal.map (fun json -> Some (Attribute("data-labels", json)))
            Primitives.observedRender "data-labels" "[]" [
                "  d.forEach(function(p){"
                "    var s = document.createElement('div');"
                "    s.className = 'pfl';"
                "    s.textContent = p.n;"
                "    s.style.left = p.x + 'px';"
                "    s.style.top = p.y + 'px';"
                "    s.style.borderColor = p.c;"
                "    s.style.color = p.c;"
                "    el.appendChild(s);"
                "  });"
            ]
        }

    // Transient feedback for blocked/failed actions (auto-clears).
    let toast (model : AdaptiveModel) =
        div {
            Class "app-toast"
            Primitives.showWhen (model.Toast |> AVal.map Option.isSome)
            model.Toast |> AVal.map (Option.defaultValue "")
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
                let scale = DatasetScale.active (model.ActiveDataset.GetValue t) (model.DatasetScales.GetValue t)
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

    // Colormap legend (Inspect only, bottom centre): the active false-colour map's
    // gradient with nice-step ticks and the exact range ends. The three Inspect maps
    // all read on the unified pin-derived scale (§C): the ensemble variance σ
    // [0, max(|lo|,hi)], the soloed mesh's signed difference [lo, hi], and the
    // displacement magnitude [0, global max |load→solved|].
    let colorLegend (model : AdaptiveModel) =
        let rangeA = MeshView.inspectRange model
        let dispA = MeshView.displacementRange model
        let fmt (span : float) (v : float) =
            if span < 0.095 then sprintf "%.0f mm" (v * 1000.0)
            elif span < 0.95 then sprintf "%.0f cm" (v * 100.0)
            else sprintf "%.2f m" v
        let legendJson =
            AVal.custom (fun t ->
                let (lo, hi) = rangeA.GetValue t
                let solo = (model.MeshSolo.GetValue t).IsSome
                let title, vLo, vHi, colorAt =
                    if not solo then
                        let m = max 1e-6 (max (abs lo) hi)
                        "Disagreement σ", 0.0, m,
                        (fun (v : float) ->
                            let tt = clamp 0.0 1.0 (v / m)
                            V3d(0.945, 0.961, 0.976) * (1.0 - tt) + V3d(0.725, 0.110, 0.110) * tt)
                    else
                        match model.InspectChannel.GetValue t with
                        | ChDisplacement ->
                            let m = max 1e-6 (dispA.GetValue t)
                            "Displacement", 0.0, m,
                            (fun v ->
                                let tt = clamp 0.0 1.0 (v / m)
                                V3d(0.93, 0.94, 0.98) * (1.0 - tt) + V3d(0.118, 0.227, 0.541) * tt)
                        | ChDifference ->
                            let title = if model.ExtrinsicZDiff.GetValue t then "Difference (Δz)" else "Difference (M3C2)"
                            title, lo, hi, Primitives.Diff.colorSignedV3 lo hi
                let span = vHi - vLo
                let hexAt (v : float) =
                    let c = colorAt v
                    let b (x : float) = byte (clamp 0.0 255.0 (x * 255.0))
                    Primitives.c4bToHex (C4b(b c.X, b c.Y, b c.Z))
                let stops =
                    [ for i in 0 .. 23 -> sprintf "\"%s\"" (hexAt (vLo + span * float i / 23.0)) ]
                    |> String.concat ","
                // Nice-step ticks; ends carry the exact range values, so ticks that
                // would collide with them (outer 12%) are dropped.
                let step = niceRoundDistance (span / 4.0)
                let ticks =
                    if step <= 0.0 || span <= 0.0 then []
                    else
                        [ for k in int (ceil (vLo / step)) .. int (floor (vHi / step)) do
                            let v = float k * step
                            let p = (v - vLo) / span
                            if p > 0.12 && p < 0.88 then
                                yield sprintf "{\"p\":%.4f,\"l\":\"%s\"}" p (fmt span v) ]
                    |> String.concat ","
                sprintf "{\"title\":\"%s\",\"min\":\"%s\",\"max\":\"%s\",\"stops\":[%s],\"ticks\":[%s]}"
                    title (fmt span vLo) (fmt span vHi) stops ticks)
        div {
            Class "color-legend"
            Primitives.showWhen (model.WorkflowStep |> AVal.map ((=) Inspect))
            legendJson |> AVal.map (fun json -> Some (Attribute("data-legend", json)))
            Primitives.observedRender "data-legend" "{}" [
                "  if(!d.stops) return;"
                "  var W = 240, BH = 12, H = 44, PAD = 6;"
                "  var svg = document.createElementNS(ns, 'svg');"
                "  svg.setAttribute('width', W); svg.setAttribute('height', H);"
                "  svg.setAttribute('viewBox', '0 0 ' + W + ' ' + H);"
                "  var bw = W - 2 * PAD, n = d.stops.length;"
                "  var gid = 'clg' + Math.floor(Math.random() * 1e9);"
                "  var defs = document.createElementNS(ns, 'defs');"
                "  var gr = document.createElementNS(ns, 'linearGradient');"
                "  gr.setAttribute('id', gid);"
                "  d.stops.forEach(function(c, i){"
                "    var st = document.createElementNS(ns, 'stop');"
                "    st.setAttribute('offset', (100 * i / (n - 1)) + '%');"
                "    st.setAttribute('stop-color', c);"
                "    gr.appendChild(st);"
                "  });"
                "  defs.appendChild(gr); svg.appendChild(defs);"
                "  var bar = document.createElementNS(ns, 'rect');"
                "  bar.setAttribute('x', PAD); bar.setAttribute('y', 2);"
                "  bar.setAttribute('width', bw); bar.setAttribute('height', BH);"
                "  bar.setAttribute('fill', 'url(#' + gid + ')');"
                "  bar.setAttribute('stroke', '#94a3b8'); bar.setAttribute('stroke-width', '0.5');"
                "  svg.appendChild(bar);"
                "  function txt(x, anchor, s, y){"
                "    var tx = document.createElementNS(ns, 'text');"
                "    tx.setAttribute('x', x); tx.setAttribute('y', y || 28);"
                "    tx.setAttribute('fill', '#0f172a'); tx.setAttribute('font-size', '9');"
                "    tx.setAttribute('font-family', 'SF Mono, Monaco, monospace');"
                "    tx.setAttribute('text-anchor', anchor); tx.textContent = s;"
                "    svg.appendChild(tx);"
                "  }"
                "  (d.ticks || []).forEach(function(tk){"
                "    var x = PAD + tk.p * bw;"
                "    var ln = document.createElementNS(ns, 'line');"
                "    ln.setAttribute('x1', x); ln.setAttribute('y1', 2);"
                "    ln.setAttribute('x2', x); ln.setAttribute('y2', BH + 6);"
                "    ln.setAttribute('stroke', '#475569'); ln.setAttribute('stroke-width', '1');"
                "    svg.appendChild(ln);"
                "    txt(x, 'middle', tk.l);"
                "  });"
                "  txt(PAD, 'start', d.min, 28);"
                "  txt(W - PAD, 'end', d.max, 28);"
                "  el.innerHTML = '';"
                "  var tt = document.createElement('div'); tt.className = 'cl-title';"
                "  tt.textContent = d.title; el.appendChild(tt);"
                "  el.appendChild(svg);"
            ]
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
            Primitives.observedRender "data-axes" "[]" [
                "  var W = 60, H = 60, L = 22, cx = W/2, cy = H/2;"
                "  var svg = document.createElementNS(ns, 'svg');"
                "  svg.setAttribute('width', W); svg.setAttribute('height', H);"
                "  svg.setAttribute('viewBox', '0 0 ' + W + ' ' + H);"
                "  d.sort(function(a,b){return a.z - b.z;});"
                "  d.forEach(function(a){"
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
            ]
        }
