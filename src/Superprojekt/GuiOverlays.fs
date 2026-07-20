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

    // Cursor label of the Alt-wheel layer cycling. Gated on Alt actually HELD:
    // ActivePickingLayer persists after the cycle (it keeps steering pick
    // priority), so an always-on label would trail a stale mesh name around the
    // cursor over meshes it doesn't describe.
    let meshWheelLabel (model : AdaptiveModel) (cursorScreen : aval<V2d option>) (altHeld : aval<bool>) =
        let meshOrderMap = model.MeshOrder.Content
        let visible =
            (model.ActivePickingLayer, cursorScreen, altHeld) |||> AVal.map3 (fun layer cOpt alt ->
                layer.IsSome && cOpt.IsSome && alt)
        div {
            Class "mesh-wheel-label"
            Primitives.showWhen visible
            cursorScreen |> AVal.map (Option.map (fun pos ->
                Style [
                    Left (sprintf "%.0fpx" (pos.X + 14.0))
                    Top  (sprintf "%.0fpx" (pos.Y - 10.0))
                ]))
            (model.ActivePickingLayer, meshOrderMap) ||> AVal.map2 (fun layer order ->
                match layer with
                | Some name -> Primitives.numbered order name
                | None -> "")
        }

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

    // Stretch-mode ordinates — per DOT OF INTEREST, a vertical line
    // from the dot to the reference (its signed sample value), projected through
    // the SAME slice view/proj the 3D uses (so the exaggeration is inherited),
    // drawn as HTML strips so they are natively hoverable: the tooltip carries
    // the TRUE value (mm/cm), never the stretched pixel distance. Ordinates
    // exist ONLY in stretch mode — true scale has none.
    let sliceOrdinates (model : AdaptiveModel) (viewportSize : aval<V2i>) =
        let camA = MeshView.sliceCamera model
        let stretchA = ScanPinScene.sliceStretchFactor model
        let rankedA = ScanPinScene.sliceRankedBrush model
        let datasetScaleA =
            (model.ActiveDataset, model.DatasetScales) ||> AVal.map2 DatasetScale.active
        let ordsJson =
            AVal.custom (fun t ->
                if not (model.SliceStretch.GetValue t) then "[]"
                else
                    match camA.GetValue t, rankedA.GetValue t with
                    | Some s, Some ranked when ranked.Length > 0 ->
                        let n = stretchA.GetValue t
                        let vp = viewportSize.GetValue t
                        let aspect = float vp.X / float (max 1 vp.Y)
                        let viewT = CameraView.lookAt s.Eye s.Target s.Up |> CameraView.viewTrafo
                        let hw, hh = MeshView.sliceOrthoHalfSizes s aspect n
                        let projT = MeshView.orthoProjTrafo hw hh s.Near s.Far
                        let fwd = (viewT * projT).Forward
                        let scale = datasetScaleA.GetValue t
                        let toPx (p : V3d) =
                            let h = fwd * V4d(p, 1.0)
                            let ndc = h.XYZ / h.W
                            V2d((ndc.X * 0.5 + 0.5) * float vp.X, (0.5 - ndc.Y * 0.5) * float vp.Y)
                        let items =
                            ranked
                            |> Array.truncate ScanPinScene.maxDotsOfInterest
                            |> Array.choose (fun (_, p, vMm, _) ->
                                // The ordinate drops along the pin axis (= the M3C2
                                // measurement direction and the screen vertical).
                                let pRef = p - s.Up * ScanPin.renderLength scale (vMm / 1000.0)
                                let a = toPx p
                                let b = toPx pRef
                                if a.X < -50.0 || a.X > float vp.X + 50.0 then None
                                else
                                    let label =
                                        if abs vMm >= 100.0 then sprintf "%+.1f cm" (vMm / 10.0)
                                        else sprintf "%+.1f mm" vMm
                                    Some (sprintf "{\"x\":%.1f,\"y1\":%.1f,\"y2\":%.1f,\"v\":\"%s\"}" a.X a.Y b.Y label))
                        "[" + String.concat "," items + "]"
                    | _ -> "[]")
        div {
            Class "slice-ords"
            ordsJson |> AVal.map (fun j -> Some (Attribute("data-ords", j)))
            Primitives.observedRender "data-ords" "[]" [
                "  d.forEach(function(o){"
                "    var top = Math.min(o.y1, o.y2), h = Math.max(6, Math.abs(o.y2 - o.y1));"
                "    var strip = document.createElement('div'); strip.className='slice-ord';"
                "    strip.style.left=(o.x-4)+'px'; strip.style.top=top+'px'; strip.style.height=h+'px';"
                "    var line = document.createElement('div'); line.className='slice-ord-line'; strip.appendChild(line);"
                "    var tip = document.createElement('div'); tip.className='slice-ord-tip'; tip.textContent=o.v; strip.appendChild(tip);"
                "    el.appendChild(strip);"
                "  });"
            ]
        }

    // Slice-mode badges. Gold = the slice-mode accent (the badges only — the
    // focus angle indicator is white, transient layer).
    let sliceBadges (model : AdaptiveModel) =
        let stretchA = ScanPinScene.sliceStretchFactor model
        div {
            Class "slice-badges"
            Primitives.showWhen model.SliceMode
            div { Class "slice-badge"; "ortho slice view" }
            div {
                Class "slice-badge"
                Primitives.showWhen model.SliceStretch
                stretchA |> AVal.map (fun n ->
                    if n >= 100.0 then sprintf "vertical axis stretched ×%.0f" n
                    else sprintf "vertical axis stretched ×%.1f" n)
            }
        }

    // Slice-mode axes — a vertical ruler left of the view and a
    // horizontal one below it, both measuring METRIC distance from the pin
    // centre (the projection centre; the vertical ruler ticks in TRUE metres,
    // so its px spacing widens with the stretch factor). Recomputed from the
    // live ortho frame, so the rulers stay valid whenever the projection
    // changes (zoom-locked, but stretch/viewport/pin changes all re-tick).
    let sliceAxes (model : AdaptiveModel) (viewportSize : aval<V2i>) =
        let camA = MeshView.sliceCamera model
        let stretchA = ScanPinScene.sliceStretchFactor model
        let datasetScaleA =
            (model.ActiveDataset, model.DatasetScales) ||> AVal.map2 DatasetScale.active
        let nice125 (raw : float) =
            let raw = max 1e-6 raw
            let mag = 10.0 ** floor (log10 raw)
            let n = raw / mag
            (if n < 1.5 then 1.0 elif n < 3.5 then 2.0 elif n < 7.5 then 5.0 else 10.0) * mag
        let label (m : float) =
            if abs m < 1e-9 then "0"
            else (if m < 0.0 then "−" else "") + formatMeters (abs m)
        let axesJson =
            AVal.custom (fun t ->
                match camA.GetValue t with
                | None -> "null"
                | Some s ->
                    let vp = viewportSize.GetValue t
                    let w, h = float vp.X, float vp.Y
                    let n = stretchA.GetValue t
                    let scale = datasetScaleA.GetValue t
                    // px per metre straight from the shared ortho half-extents,
                    // so the rulers track the stretch AND the horizontal tighten.
                    let hw, hh = MeshView.sliceOrthoHalfSizes s (w / max 1.0 h) n
                    let pxPerMh = w * scale / (2.0 * hw)
                    let pxPerMv = h * scale / (2.0 * hh)
                    let cx, cy = w * 0.5, h * 0.5
                    let ticks (pxPerM : float) (extentPx : float) (centre : float) (flip : bool) =
                        let halfM = extentPx * 0.5 / max 1e-9 pxPerM
                        let step = nice125 (halfM / 4.0)
                        [ for k in int (ceil (-halfM / step)) .. int (floor (halfM / step)) do
                            let m = float k * step
                            let p = centre + (if flip then -m else m) * pxPerM
                            yield sprintf "{\"p\":%.1f,\"l\":\"%s\",\"z\":%d}" p (label m) (if k = 0 then 1 else 0) ]
                        |> String.concat ","
                    sprintf "{\"w\":%.0f,\"h\":%.0f,\"ht\":[%s],\"vt\":[%s]}"
                        w h (ticks pxPerMh w cx false) (ticks pxPerMv h cy true))
        div {
            Class "slice-axes"
            Primitives.showWhen model.SliceMode
            axesJson |> AVal.map (fun j -> Some (Attribute("data-axes", j)))
            Primitives.observedRender "data-axes" "null" [
                "  if(!d) return;"
                "  var svg = document.createElementNS(ns,'svg');"
                "  svg.setAttribute('width', d.w); svg.setAttribute('height', d.h);"
                "  svg.setAttribute('viewBox', '0 0 ' + d.w + ' ' + d.h);"
                "  var AX='#475569', GRID='rgba(71,85,105,0.10)';"
                // Vertical ruler right of the rail; horizontal ruler above the
                // control's bottom edge (= the dock top).
                "  var vx = 268, hy = d.h - 30;"
                "  function ln(x1,y1,x2,y2,st,sw){ var l=document.createElementNS(ns,'line');"
                "    l.setAttribute('x1',x1); l.setAttribute('y1',y1); l.setAttribute('x2',x2); l.setAttribute('y2',y2);"
                "    l.setAttribute('stroke',st); l.setAttribute('stroke-width',sw); svg.appendChild(l); }"
                "  function tx(x,y,s,anchor,bold){ var e=document.createElementNS(ns,'text');"
                "    e.setAttribute('x',x); e.setAttribute('y',y); e.setAttribute('fill',AX);"
                "    e.setAttribute('font-size','10'); e.setAttribute('font-family','SF Mono, Monaco, monospace');"
                "    e.setAttribute('text-anchor',anchor); if(bold) e.setAttribute('font-weight','700');"
                "    e.textContent=s; svg.appendChild(e); }"
                "  ln(vx, 8, vx, hy, AX, 1.2);"
                "  ln(vx, hy, d.w - 10, hy, AX, 1.2);"
                "  (d.ht||[]).forEach(function(tk){ if(tk.p < vx + 14 || tk.p > d.w - 14) return;"
                "    ln(tk.p, hy, tk.p, hy + (tk.z ? 8 : 5), AX, tk.z ? 1.6 : 1);"
                "    ln(tk.p, 8, tk.p, hy, GRID, 1);"
                "    tx(tk.p, hy + 18, tk.l, 'middle', tk.z); });"
                "  (d.vt||[]).forEach(function(tk){ if(tk.p < 16 || tk.p > hy - 8) return;"
                "    ln(vx - (tk.z ? 8 : 5), tk.p, vx, tk.p, AX, tk.z ? 1.6 : 1);"
                "    ln(vx, tk.p, d.w - 10, tk.p, GRID, 1);"
                "    tx(vx - 10, tk.p + 3, tk.l, 'end', tk.z); });"
                "  el.appendChild(svg);"
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
    // gradient with nice-step ticks and the exact range ends — it follows the
    // selection (ensemble/pin → variance σ, mesh/cell → that pair's
    // difference or the displacement channel) and a live brush (the brushed
    // dots' shared signed range). All maps read on the unified pin-derived scale.
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
                    elif not (Set.isEmpty (model.BrushedSamples.GetValue t))
                         && not (model.SliceMode.GetValue t) then
                        // Live brush: the maps stand down but the
                        // 3D dots carry the signed sample values on the shared
                        // diverging scale — the legend describes THEM. Probe
                        // samples are M3C2 regardless of the surface sub-mode.
                        "Difference (M3C2) · brushed", lo, hi, Primitives.Diff.colorSignedV3 lo hi
                    elif soloName.IsNone then
                        let m = max 1e-6 (max (abs lo) hi)
                        "Variance σ", 0.0, m,
                        (fun (v : float) ->
                            let tt = clamp 0.0 1.0 (v / m)
                            V3d(0.945, 0.961, 0.976) * (1.0 - tt) + V3d(0.725, 0.110, 0.110) * tt)
                    else
                        // Title names the compared meshes by display number
                        // (isolated moving mesh vs the reference); a cell
                        // selection appends its pin identity.
                        let order = orderContent.GetValue t
                        let numOf m = (HashMap.tryFind m order |> Option.defaultValue 0) + 1
                        let pair =
                            match soloName, model.ReferenceMesh.GetValue t with
                            | Some s, Some r -> sprintf " %d vs %d" (numOf s) (numOf r)
                            | _ -> ""
                        let pinSuffix =
                            match model.Selection.Active.GetValue t with
                            | SelCell (p, _) ->
                                match HashMap.tryFind p (pinsVal.GetValue t) with
                                | Some pin -> sprintf " · %s" pin.ShortName
                                | None -> ""
                            | _ -> ""
                        let sub = if model.ExtrinsicZDiff.GetValue t then "Δz" else "M3C2"
                        sprintf "Difference%s (%s)%s" pair sub pinSuffix, lo, hi, Primitives.Diff.colorSignedV3 lo hi
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
        // suppresses the maps — the value-coloured brushed dots' scale. The one
        // scale-less state left is slice mode + brush (neutral dots, values in
        // the ordinates/charts) — there it hides.
        let brushedInSlice =
            (model.BrushedSamples, model.SliceMode) ||> AVal.map2 (fun b s ->
                not (Set.isEmpty b) && s)
        div {
            Class "color-legend"
            Primitives.showWhen
                ((model.WorkflowStep, anyRangeOn, brushedInSlice) |||> AVal.map3 (fun s r bs ->
                    (s = Inspect && not bs) || r))
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
