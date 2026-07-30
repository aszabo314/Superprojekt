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

    // Bottom-centre colour legend, two states: the in-cell DIFFERENCE map
    // (diverging, over the ONE pair range — shown while the cell map paints)
    // wins; else the Range heatmap while any mesh has Dst active.
    let colorLegend (model : AdaptiveModel) =
        let fmt (span : float) (v : float) =
            if span < 0.095 then sprintf "%.0f mm" (v * 1000.0)
            elif span < 0.95 then sprintf "%.0f cm" (v * 100.0)
            elif span < 10.0 then sprintf "%.2f m" v
            else sprintf "%.0f m" v
        let heatRangeMaxA = MeshView.rangeMaxWorld model
        let anyRangeOn =
            model.MeshHeatmap |> AVal.map (Map.exists (fun _ h -> h = HeatRange))
        // The ONE difference scale, shown while anything paints on it: the
        // surface map, or the brushed dots (which carry the same ramp — never a
        // second legend).
        let diffOn =
            AVal.custom (fun t ->
                match model.Focus.GetValue t with
                | FocusPair | FocusPin ->
                    (model.Sel.GetValue t).Pair.IsSome
                    && ((model.CellMapOn.GetValue t && (model.CellDist.GetValue t).IsSome)
                        || not (Set.isEmpty (model.BrushedSamples.GetValue t)))
                | FocusMatrix ->
                    (model.CellMapOn.GetValue t && not (Map.isEmpty (model.GraphDist.GetValue t)))
                    || not (Set.isEmpty (model.BrushedSamples.GetValue t)))
        // The 3D-hovered dot's value, connecting "this value" to the scale: the
        // exact probed number the tooltip shows, or the dot's own sample value
        // until that fetch lands.
        let hoveredValue =
            AVal.custom (fun t ->
                match model.HoverSample.GetValue t with
                | None -> None
                | Some gid ->
                    match model.HoverReadout.GetValue t with
                    | Some (g, v) when g = gid -> Some v
                    | _ ->
                        let rec find (i : int) (bs : InspectBlock list) =
                            match bs with
                            | [] -> None
                            | b :: rest ->
                                if i < b.Err.Samples.Length then Some b.Err.Samples.[i]
                                else find (i - b.Err.Samples.Length) rest
                        find gid (Array.toList (MeshView.inspectBlocksAt model t)))
        let legendJson =
            AVal.custom (fun t ->
                let title, vLo, vHi, colorAt =
                    if diffOn.GetValue t then
                        let lo, hi = MeshView.inspectRangeAt model t
                        let name =
                            match model.Focus.GetValue t, (model.Sel.GetValue t).Pair with
                            | FocusMatrix, _ -> "Difference vs parents"
                            | _, Some (a, b) ->
                                let order = model.MeshOrder.Content.GetValue t
                                let num m = (HashMap.tryFind m order |> Option.defaultValue 0) + 1
                                let refM, movM = MatrixNav.pairRefMov (model.RegGraph.GetValue t) a b
                                sprintf "Difference %d vs %d" (num movM) (num refM)
                            | _, None -> "Difference"
                        name, lo, hi, Primitives.Diff.colorSignedV3 lo hi
                    else
                        let m = max 1e-6 (heatRangeMaxA.GetValue t)
                        "Range", 0.0, m,
                        (fun (v : float) ->
                            let tt = clamp 0.0 1.0 (v / m)
                            V3d(0.13, 0.40, 0.85) * (1.0 - tt) + V3d(0.86, 0.20, 0.15) * tt)
                let span = vHi - vLo
                let hexAt (v : float) =
                    let c = colorAt v
                    let b (x : float) = byte (clamp 0.0 255.0 (x * 255.0))
                    Primitives.c4bToHex (C4b(b c.X, b c.Y, b c.Z))
                let stops =
                    [ for i in 0 .. 23 -> sprintf "\"%s\"" (hexAt (vLo + span * float i / 23.0)) ]
                    |> String.concat ","
                // Nice-step ticks; ends carry the exact range values, so ticks that
                // would collide with them (outer 12%) are dropped; the zero tick
                // renders one line lower so it never overlaps an edge label.
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
                // The hovered dot's mark on the bar: -1 = none (the diff scale
                // only — the Range heatmap carries no brushed samples).
                let hov =
                    if diffOn.GetValue t && span > 0.0 then
                        match hoveredValue.GetValue t with
                        | Some v -> clamp 0.0 1.0 ((v - vLo) / span)
                        | None -> -1.0
                    else -1.0
                sprintf "{\"title\":\"%s\",\"min\":\"%s\",\"max\":\"%s\",\"hov\":%.4f,\"stops\":[%s],\"ticks\":[%s]}"
                    title (fmt span vLo) (fmt span vHi) hov stops ticks)
        div {
            Class "color-legend"
            Primitives.showWhen ((anyRangeOn, diffOn) ||> AVal.map2 (||))
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
                // The 3D-hovered dot's value, in the diagram's own hover amber:
                // appended last, so it rides over the bar and every tick.
                "  if(d.hov >= 0){"
                "    var hx = PAD + d.hov * bw;"
                "    ['#ffffff','#d97706'].forEach(function(c, i){"
                "      var hl = document.createElementNS(ns, 'line');"
                "      hl.setAttribute('x1', hx); hl.setAttribute('y1', 0);"
                "      hl.setAttribute('x2', hx); hl.setAttribute('y2', BH + 5);"
                "      hl.setAttribute('stroke', c); hl.setAttribute('stroke-width', i ? 2 : 4);"
                "      svg.appendChild(hl);"
                "    });"
                "  }"
                "  el.innerHTML = '';"
                "  var tt = document.createElement('div'); tt.className = 'cl-title';"
                "  tt.textContent = d.title; el.appendChild(tt);"
                "  el.appendChild(svg);"
            ]
        }

    // The FORCED loop-resolution modal (P9): blocking, minimal, self-
    // announcing — the whole interaction for a transient loop (no 3D
    // choreography, no standing overlay). It states the residual, lists every
    // cycle edge with its single-scalar quality (weakest pre-selected), and
    // the user removes exactly ONE edge; Confirm commits, Cancel/Esc discards
    // the redundant edge and the prior tree stands.
    let loopModal (env : Env<Message>) (model : AdaptiveModel) =
        let pending = model.LoopPending
        let residualLine =
            pending |> AVal.map (function
                | Some lp ->
                    let trans =
                        if lp.ResidualTransM < 1.0 then sprintf "%.1f cm" (lp.ResidualTransM * 100.0)
                        else sprintf "%.2f m" lp.ResidualTransM
                    sprintf "These paths disagree by %.1f° and %s." lp.ResidualRotDeg trans
                | None -> "")
        let rows =
            (pending, model.MeshOrder.Content) ||> AVal.map2 (fun lp order ->
                match lp with
                | None -> IndexList.empty
                | Some lp ->
                    let numOf m = (HashMap.tryFind m order |> Option.defaultValue 0) + 1
                    let row (label : string) (q : float) (sel : string option) =
                        div {
                            Class "lm-row"
                            Primitives.classWhen "lm-row-sel"
                                (model.LoopPending |> AVal.map (function
                                    | Some p -> p.Selected = sel
                                    | None -> false))
                            Dom.OnClick(fun _ -> env.Emit [SelectLoopEdge sel])
                            span { Class "lm-edge"; label }
                            span { Class "lm-q"; sprintf "quality %.2f" q }
                        }
                    IndexList.ofList [
                        yield row (sprintf "new edge %d ↔ %d" (numOf lp.Mov) (numOf lp.Ref)) lp.Quality None
                        for e in lp.CycleEdges do
                            yield row (sprintf "%d → %d" (numOf e.Child) (numOf e.Parent)) e.Quality (Some e.Child)
                    ])
            |> AList.ofAVal
        div {
            Class "modal-scrim"
            Primitives.showWhen (pending |> AVal.map Option.isSome)
            div {
                Class "loop-modal"
                div { Class "lm-title"; "Two paths now connect these meshes" }
                div { Class "lm-residual"; residualLine }
                div { Class "lm-hint"; "Remove one edge on the loop to keep a single registration path (the weakest link is pre-selected):" }
                div { Class "lm-rows"; rows }
                div {
                    Class "lm-buttons"
                    button {
                        Class "rail-btn lm-cancel"
                        Attribute("title", "Discard the just-added edge; the prior tree stands (Esc)")
                        Dom.OnClick(fun _ -> env.Emit [CancelLoopResolution])
                        "Cancel"
                    }
                    button {
                        Class "rail-btn lm-confirm"
                        Attribute("title", "Remove the selected edge and recompose the poses")
                        Dom.OnClick(fun _ -> env.Emit [ConfirmLoopResolution])
                        "Remove edge ✓"
                    }
                }
            }
        }

    // The Pin exit-guard: blocking confirm-delete for leaving Pin with an
    // incomplete pin (supersedes the old silent rollback). Esc cancels.
    let pinExitModal (env : Env<Message>) (model : AdaptiveModel) =
        div {
            Class "modal-scrim"
            Primitives.showWhen (model.PinExitPending |> AVal.map Option.isSome)
            div {
                Class "loop-modal"
                div { Class "lm-title"; "Delete the incomplete pin?" }
                div {
                    Class "lm-hint"
                    "This pin does not have all its parts yet (centre + a point on each mesh). Leaving the Pin level now deletes it."
                }
                div {
                    Class "lm-buttons"
                    button {
                        Class "rail-btn lm-cancel"
                        Attribute("title", "Stay in the Pin level and keep placing (Esc)")
                        Dom.OnClick(fun _ -> env.Emit [CancelPinExit])
                        "Stay"
                    }
                    button {
                        Class "rail-btn lm-danger"
                        Attribute("title", "Delete the incomplete pin and leave")
                        Dom.OnClick(fun _ -> env.Emit [ConfirmPinExit])
                        "Delete & leave"
                    }
                }
            }
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
