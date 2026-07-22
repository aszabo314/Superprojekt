namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open FSharp.Data.Adaptive
open Aardvark.Dom

module GuiOverlays =

    let private formatMeters (m : float) =
        if m >= 1000.0 then sprintf "%g km" (m / 1000.0)
        elif m >= 1.0 then sprintf "%g m" m
        else sprintf "%g cm" (m * 100.0)

    // Cursor-side hard-prohibit tooltip — shown while a placement is armed and
    // the hovered spot has < 2 meshes in range (placement is refused).
    let placementTooltip (model : AdaptiveModel) (cursorScreen : aval<V2d option>) (placementValid : aval<bool option>) =
        let placing =
            model.ScanPins.Placement |> AVal.map (function AnchorPlacement -> true | _ -> false)
        let visible =
            (placing, placementValid, cursorScreen) |||> AVal.map3 (fun p v c ->
                p && v = Some false && c.IsSome)
        div {
            Class "placement-tooltip"
            Primitives.showWhen visible
            cursorScreen |> AVal.map (Option.map (fun pos ->
                Style [
                    Left (sprintf "%.0fpx" (pos.X + 14.0))
                    Top  (sprintf "%.0fpx" (pos.Y + 18.0))
                ]))
            "no overlapping meshes here"
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


    // Confirmation flash of a committed correspondence pick: a short expanding-
    // ring CSS animation at the commit point, projected into the main 3D view
    // (same 90° frustum as View.fs). The generation in the payload restarts the
    // animation on back-to-back picks; ClearCorrFlash empties the layer.
    let corrFlash (model : AdaptiveModel) (viewportSize : aval<V2i>) =
        let flashJson =
            AVal.custom (fun t ->
                match model.CorrFlash.GetValue t with
                | None -> "null"
                | Some (w, gen) ->
                    let vp = viewportSize.GetValue t
                    let cc = model.CommonCentroid.GetValue t
                    let scale = DatasetScale.active (model.ActiveDataset.GetValue t) (model.DatasetScales.GetValue t)
                    let p = ScanPin.renderCentre cc scale w
                    let viewT = CameraView.viewTrafo (model.Camera.view.GetValue t)
                    let aspect = float vp.X / float (max 1 vp.Y)
                    let projT = Frustum.perspective 90.0 1.0 5000.0 aspect |> Frustum.projTrafo
                    let h = (viewT * projT).Forward * V4d(p, 1.0)
                    if h.W <= 1e-9 then "null"
                    else
                        let ndc = h.XYZ / h.W
                        sprintf "{\"x\":%.1f,\"y\":%.1f,\"g\":%d}"
                            ((ndc.X * 0.5 + 0.5) * float vp.X) ((0.5 - ndc.Y * 0.5) * float vp.Y) gen)
        div {
            Class "corr-flash-layer"
            flashJson |> AVal.map (fun j -> Some (Attribute("data-flash", j)))
            Primitives.observedRender "data-flash" "null" [
                "  if(!d) return;"
                "  var ring = document.createElement('div'); ring.className='corr-flash';"
                "  ring.style.left=d.x+'px'; ring.style.top=d.y+'px';"
                "  el.appendChild(ring);"
            ]
        }

    // Transient feedback for blocked/failed actions (auto-clears).
    let toast (model : AdaptiveModel) =
        div {
            Class "app-toast"
            Primitives.showWhen (model.Toast |> AVal.map Option.isSome)
            model.Toast |> AVal.map (Option.defaultValue "")
        }

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

    // Colormap legend (Inspect only, bottom centre): the ACTIVE false-colour map's
    // gradient with nice-step ticks and the exact range ends — the difference map
    // vs the reference (ensemble or the isolated pair) and a live brush (the
    // brushed dots' shared signed range). All maps read on the unified pin-derived
    // scale.
    let colorLegend (model : AdaptiveModel) =
        let rangeA = MeshView.inspectRange model
        let orderContent = model.MeshOrder.Content
        let pinsVal = model.ScanPins.Pins |> AMap.toAVal
        let fmt (span : float) (v : float) =
            if span < 0.095 then sprintf "%.0f mm" (v * 1000.0)
            elif span < 0.95 then sprintf "%.0f cm" (v * 100.0)
            elif span < 10.0 then sprintf "%.2f m" v
            else sprintf "%.0f m" v
        let heatRangeMaxA = MeshView.rangeMaxWorld model
        // Outside Inspect the legend serves the Range heatmap: shown while any
        // shown mesh has Dst active, on the ONE all-mesh scale.
        let anyRangeOn =
            (model.MeshHeatmap, model.MeshSolo) ||> AVal.map2 (fun hm solo ->
                hm |> Map.exists (fun m h -> h = HeatRange && MeshVisibility.shown solo m))
        let legendJson =
            AVal.custom (fun t ->
                let (lo, hi) = rangeA.GetValue t
                let soloName = model.MeshSolo.GetValue t
                let title, vLo, vHi, colorAt =
                    if model.WorkflowStep.GetValue t <> Inspect then
                        let m = max 1e-6 (heatRangeMaxA.GetValue t)
                        "Range", 0.0, m,
                        (fun (v : float) ->
                            let tt = clamp 0.0 1.0 (v / m)
                            V3d(0.13, 0.40, 0.85) * (1.0 - tt) + V3d(0.86, 0.20, 0.15) * tt)
                    elif not (Set.isEmpty (model.BrushedSamples.GetValue t)) then
                        // Live brush: the maps stand down but the 3D dots carry the
                        // signed sample values on the shared diverging scale — the
                        // legend describes THEM.
                        "Difference · brushed", lo, hi, Primitives.Diff.colorSignedV3 lo hi
                    else
                        // Title names the compared meshes by display number: the
                        // isolated moving mesh vs the reference, or — in the
                        // ensemble — every moving mesh vs the reference; a cell
                        // selection appends its pin identity.
                        let order = orderContent.GetValue t
                        let numOf m = (HashMap.tryFind m order |> Option.defaultValue 0) + 1
                        let pair =
                            match soloName, model.ReferenceMesh.GetValue t with
                            | Some s, Some r -> sprintf " %d vs %d" (numOf s) (numOf r)
                            | None, Some r -> sprintf " vs %d" (numOf r)
                            | _ -> ""
                        let pinSuffix =
                            match model.Selection.Active.GetValue t with
                            | SelCell (p, _) ->
                                match HashMap.tryFind p (pinsVal.GetValue t) with
                                | Some pin -> sprintf " · %s" pin.ShortName
                                | None -> ""
                            | _ -> ""
                        sprintf "Difference%s%s" pair pinSuffix, lo, hi, Primitives.Diff.colorSignedV3 lo hi
                let span = vHi - vLo
                let hexAt (v : float) =
                    let c = colorAt v
                    let b (x : float) = byte (clamp 0.0 255.0 (x * 255.0))
                    Primitives.c4bToHex (C4b(b c.X, b c.Y, b c.Z))
                let stops =
                    [ for i in 0 .. 23 -> sprintf "\"%s\"" (hexAt (vLo + span * float i / 23.0)) ]
                    |> String.concat ","
                // Nice-step ticks; ends carry the exact range values, so ticks that
                // would collide with them (outer 12%) are dropped. The zero tick is
                // flagged — its label renders one line lower so it can never overlap
                // an edge label on an asymmetric range.
                let step = niceRoundDistance (span / 4.0)
                let ticks =
                    if step <= 0.0 || span <= 0.0 then []
                    else
                        [ for k in int (ceil (vLo / step)) .. int (floor (vHi / step)) do
                            let v = float k * step
                            let p = (v - vLo) / span
                            if p > 0.12 && p < 0.88 then
                                yield sprintf "{\"p\":%.4f,\"l\":\"%s\",\"z\":%d}" p (fmt span v) (if k = 0 then 1 else 0) ]
                    |> String.concat ","
                sprintf "{\"title\":\"%s\",\"min\":\"%s\",\"max\":\"%s\",\"stops\":[%s],\"ticks\":[%s]}"
                    title (fmt span vLo) (fmt span vHi) stops ticks)
        // In Inspect the legend shows the active surface map, or — while a brush
        // suppresses the maps — the value-coloured brushed dots' scale.
        div {
            Class "color-legend"
            Primitives.showWhen
                ((model.WorkflowStep, anyRangeOn) ||> AVal.map2 (fun s r ->
                    s = Inspect || r))
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
                "    txt(x, 'middle', tk.l, tk.z ? 37 : 28);"
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
