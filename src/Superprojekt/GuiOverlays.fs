namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open FSharp.Data.Adaptive
open Aardvark.Dom

module GuiOverlays =

    // Cursor label of the Alt-wheel layer cycling. Gated on Alt actually HELD:
    // ActivePickingLayer persists after the cycle (it keeps steering pick
    // priority), so an always-on label would trail a stale mesh name around the
    // cursor over meshes it doesn't describe (§B6).
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

    // Show-overlays hold: a 2D name tag per pin floating at its flag-pole tip
    // (ScanPin.flagTopRender projected to CSS px every frame), extended with the
    // pin's precomputed vertical cross-section as a small profile chart. Two
    // attributes drive one JS renderer: data-labels re-projects the tag positions
    // every frame (cheap), data-charts carries the SVG chart content and changes
    // only when a slice cache / displayed pose / mesh visibility changes — so
    // orbiting while the hold is down moves the pills without rebuilding chart DOM.
    // Same 90° horizontal fov as the main projection — only NDC x/y matter here.
    let pinFlagLabels (model : AdaptiveModel) (viewportSize : aval<V2i>) =
        let pinsVal = model.ScanPins.Pins |> AMap.toAVal
        let idStr (id : ScanPinId) = let (ScanPinId.ScanPinId g) = id in g.ToString "N"
        let labelsJson =
            AVal.custom (fun t ->
                if not (model.ShowOverlaysHeld.GetValue t) then "[]"
                else
                    let pins  = pinsVal.GetValue t
                    let cc    = model.CommonCentroid.GetValue t
                    let scale = DatasetScale.active (model.ActiveDataset.GetValue t) (model.DatasetScales.GetValue t)
                    let vp    = viewportSize.GetValue t
                    let cam   = model.Camera.view.GetValue t
                    let viewT = CameraView.viewTrafo cam
                    let fs    = model.FlagScale.GetValue t
                    let projT =
                        Frustum.perspective 90.0 1.0 5000.0 (float vp.X / float (max 1 vp.Y))
                        |> Frustum.projTrafo
                    let vpFwd = (viewT * projT).Forward
                    let items =
                        HashMap.toList pins |> List.choose (fun (id, p) ->
                            let cR = ScanPin.renderCentre cc scale p.Centre
                            let fh = ScanPin.flagHeightRender scale fs (Vec.length (cam.Location - cR))
                            let h = vpFwd * V4d(ScanPin.flagTopRender cc scale fh p, 1.0)
                            if h.W <= 1e-6 then None
                            else
                                let ndc = h.XYZ / h.W
                                if abs ndc.X > 1.1 || abs ndc.Y > 1.1 then None
                                else
                                    Some (sprintf "{\"id\":\"%s\",\"x\":%.1f,\"y\":%.1f,\"n\":\"%s\",\"c\":\"%s\"}"
                                            (idStr id)
                                            ((ndc.X * 0.5 + 0.5) * float vp.X)
                                            ((0.5 - ndc.Y * 0.5) * float vp.Y)
                                            p.ShortName (Primitives.c4bToHex p.PinColor)))
                    "[" + String.concat "," items + "]")
        // Chart geometry (CSS px). The x axis spans the slice window (±Extent about
        // the pin centre); the y axis auto-fits the v-range of BOTH pose caches so
        // the peek flips emphasis without rescaling (vertical exaggeration is the
        // point — surface separations are far smaller than the window width).
        let cw, ch = 170.0, 92.0
        let padX, padT, padB = 5.0, 4.0, 13.0
        let chartsJson =
            AVal.custom (fun t ->
                // Hidden overlay ⇒ no work (labelsJson has the same early-out) —
                // slice/pose churn must not rebuild an invisible chart's JSON.
                if not (model.ShowOverlaysHeld.GetValue t) then "{}" else
                let pins  = pinsVal.GetValue t
                let peek  = model.RegPeekHeld.GetValue t
                let solo  = model.MeshSolo.GetValue t
                let order = model.MeshOrder.Content.GetValue t
                let rf    = model.ReferenceMesh.GetValue t
                let sb = System.Text.StringBuilder()
                sb.Append '{' |> ignore
                let mutable firstPin = true
                for (id, p) in HashMap.toList pins do
                    let chosen, other =
                        if peek then p.SliceOther, p.Slice else p.Slice, p.SliceOther
                    match chosen with
                    | SliceReady s when s.Extent > 1e-9 ->
                        // One v-range across both poses, all meshes, all planes.
                        let mutable lo = infinity
                        let mutable hi = -infinity
                        let scan (sl : PinSlice) =
                            for m in sl.Meshes do
                                for pl in m.Planes do
                                    for line in pl do
                                        for q in line do
                                            if q.Y < lo then lo <- q.Y
                                            if q.Y > hi then hi <- q.Y
                        scan s
                        (match other with SliceReady o -> scan o | _ -> ())
                        if hi >= lo then
                            let span0 = hi - lo
                            let mid = (lo + hi) * 0.5
                            let span = max span0 0.01
                            let lo = mid - span * 0.54
                            let hi = mid + span * 0.54
                            let e = s.Extent
                            let x (u : float) = padX + (u + e) / (2.0 * e) * (cw - 2.0 * padX)
                            let y (v : float) = ch - padB - (v - lo) / (hi - lo) * (ch - padT - padB)
                            let ci = ScanPin.sliceCentreIndex s
                            let laneOrder (name : string) =
                                HashMap.tryFind name order |> Option.defaultValue System.Int32.MaxValue
                            let meshes =
                                s.Meshes
                                |> Array.filter (fun m -> MeshVisibility.shown solo m.MeshName)
                                |> Array.sortBy (fun m -> laneOrder m.MeshName)
                            let colorOf (name : string) =
                                match Map.tryFind name p.DatasetColors with
                                | Some c4 -> Primitives.c4bToHex c4
                                // Mesh identity stays in the mesh palette family (§B1).
                                | None -> Primitives.c4bToHex (Primitives.meshColor (HashMap.tryFind name order |> Option.defaultValue 0))
                            // Draw order: outermost planes first, the centre slice last.
                            let planeOrder =
                                Array.init s.Offsets.Length (fun k -> k)
                                |> Array.sortByDescending (fun k -> abs s.Offsets.[k])
                            let paths = ResizeArray<string>()
                            for k in planeOrder do
                                let o, sw =
                                    if k = ci then 1.0, 1.6
                                    else max 0.08 (0.34 - 0.34 * (abs s.Offsets.[k] / e)), 1.0
                                for m in meshes do
                                    if k < m.Planes.Length && m.Planes.[k].Length > 0 then
                                        let d = System.Text.StringBuilder()
                                        for line in m.Planes.[k] do
                                            for i in 0 .. line.Length - 1 do
                                                d.Append(if i = 0 then "M" else "L") |> ignore
                                                d.Append(sprintf "%.1f %.1f" (x line.[i].X) (y line.[i].Y)) |> ignore
                                        paths.Add (sprintf "{\"d\":\"%s\",\"c\":\"%s\",\"o\":%.2f,\"sw\":%.1f}"
                                                    (d.ToString()) (colorOf m.MeshName) o sw)
                            // Correspondence markers, projected onto the centre slice
                            // (the out-of-plane offset is dropped deliberately).
                            let dots = ResizeArray<string>()
                            let c = p.Correspondence
                            for m in meshes do
                                let world =
                                    if Some m.MeshName = rf then c.RefAnchor
                                    else
                                        let inRoi = Map.tryFind m.MeshName c.InRoi |> Option.defaultValue true
                                        match Map.tryFind m.MeshName c.Anchors with
                                        | Some a when inRoi ->
                                            Some ((MeshView.displayedWorldPeekAt model t m.MeshName).Forward.TransformPos a.Point)
                                        | _ -> None
                                match world with
                                | Some w ->
                                    let uv = ScanPin.sliceUV p.Centre s.UDir w
                                    let dx = min (cw - padX) (max padX (x uv.X))
                                    let dy = min (ch - padB) (max padT (y uv.Y))
                                    dots.Add (sprintf "{\"x\":%.1f,\"y\":%.1f,\"c\":\"%s\"}" dx dy (colorOf m.MeshName))
                                | None -> ()
                            let grid = ResizeArray<string>()
                            if lo < 0.0 && hi > 0.0 then
                                grid.Add (sprintf "{\"x1\":%.1f,\"y1\":%.1f,\"x2\":%.1f,\"y2\":%.1f}" padX (y 0.0) (cw - padX) (y 0.0))
                            grid.Add (sprintf "{\"x1\":%.1f,\"y1\":%.1f,\"x2\":%.1f,\"y2\":%.1f}" (x 0.0) padT (x 0.0) (ch - padB))
                            if not firstPin then sb.Append ',' |> ignore
                            firstPin <- false
                            sb.Append(sprintf "\"%s\":{\"w\":%.0f,\"h\":%.0f,\"paths\":[%s],\"dots\":[%s],\"grid\":[%s],\"dl\":\"⌀ %s\",\"dr\":\"Δ %s\"}"
                                        (idStr id) cw ch
                                        (String.concat "," paths)
                                        (String.concat "," dots)
                                        (String.concat "," grid)
                                        (formatMeters (2.0 * e)) (formatMeters span0)) |> ignore
                    | _ -> ()
                sb.Append '}' |> ignore
                sb.ToString())
        div {
            Class "pin-flag-labels"
            Primitives.showWhen model.ShowOverlaysHeld
            labelsJson |> AVal.map (fun json -> Some (Attribute("data-labels", json)))
            chartsJson |> AVal.map (fun json -> Some (Attribute("data-charts", json)))
            OnBoot [
                "(function(){"
                "var el = __THIS__;"
                "var ns = 'http://www.w3.org/2000/svg';"
                "var lastL = '', lastC = '';"
                "var charts = {}, pills = {};"
                "function buildChart(id){"
                "  var pill = pills[id]; if(!pill) return;"
                "  var host = pill.querySelector('.pfl-chart');"
                "  host.innerHTML = '';"
                "  var ch = charts[id];"
                "  if(!ch){ pill.classList.add('pfl-nochart'); return; }"
                "  pill.classList.remove('pfl-nochart');"
                "  var svg = document.createElementNS(ns, 'svg');"
                "  svg.setAttribute('width', ch.w); svg.setAttribute('height', ch.h);"
                "  svg.setAttribute('viewBox', '0 0 ' + ch.w + ' ' + ch.h);"
                "  (ch.grid||[]).forEach(function(g){"
                "    var ln = document.createElementNS(ns, 'line');"
                "    ln.setAttribute('x1', g.x1); ln.setAttribute('y1', g.y1);"
                "    ln.setAttribute('x2', g.x2); ln.setAttribute('y2', g.y2);"
                "    ln.setAttribute('stroke', '#dbe2ea'); ln.setAttribute('stroke-width', '1');"
                "    svg.appendChild(ln);"
                "  });"
                "  (ch.paths||[]).forEach(function(p){"
                "    var pa = document.createElementNS(ns, 'path');"
                "    pa.setAttribute('d', p.d); pa.setAttribute('fill', 'none');"
                "    pa.setAttribute('stroke', p.c); pa.setAttribute('stroke-opacity', p.o);"
                "    pa.setAttribute('stroke-width', p.sw); pa.setAttribute('stroke-linejoin', 'round');"
                "    svg.appendChild(pa);"
                "  });"
                "  (ch.dots||[]).forEach(function(dt){"
                "    var ci = document.createElementNS(ns, 'circle');"
                "    ci.setAttribute('cx', dt.x); ci.setAttribute('cy', dt.y); ci.setAttribute('r', '3');"
                "    ci.setAttribute('fill', dt.c); ci.setAttribute('stroke', '#ffffff'); ci.setAttribute('stroke-width', '1.2');"
                "    svg.appendChild(ci);"
                "  });"
                "  function lab(x, anchor, s){"
                "    var tx = document.createElementNS(ns, 'text');"
                "    tx.setAttribute('x', x); tx.setAttribute('y', ch.h - 3);"
                "    tx.setAttribute('fill', '#64748b'); tx.setAttribute('font-size', '8');"
                "    tx.setAttribute('font-family', 'SF Mono, Monaco, monospace');"
                "    tx.setAttribute('text-anchor', anchor); tx.textContent = s;"
                "    svg.appendChild(tx);"
                "  }"
                "  if(ch.dl) lab(3, 'start', ch.dl);"
                "  if(ch.dr) lab(ch.w - 3, 'end', ch.dr);"
                "  host.appendChild(svg);"
                "}"
                "function render(){"
                "  var rawL = el.getAttribute('data-labels') || '[]';"
                "  var rawC = el.getAttribute('data-charts') || '{}';"
                "  var chartsChanged = rawC !== lastC;"
                "  if(rawL === lastL && !chartsChanged) return;"
                "  lastL = rawL; lastC = rawC;"
                "  var items; try { items = JSON.parse(rawL); } catch(e) { return; }"
                "  if(chartsChanged){ try { charts = JSON.parse(rawC); } catch(e) { charts = {}; } }"
                "  var seen = {};"
                "  items.forEach(function(p){"
                "    seen[p.id] = true;"
                "    var pill = pills[p.id];"
                "    if(!pill){"
                "      pill = document.createElement('div');"
                "      pill.className = 'pfl';"
                "      var nm = document.createElement('div'); nm.className = 'pfl-name';"
                "      var chd = document.createElement('div'); chd.className = 'pfl-chart';"
                "      pill.appendChild(nm); pill.appendChild(chd);"
                "      el.appendChild(pill);"
                "      pills[p.id] = pill;"
                "      pill._n = '';"
                "      buildChart(p.id);"
                "    } else if(chartsChanged){ buildChart(p.id); }"
                "    if(pill._n !== p.n){"
                "      pill._n = p.n;"
                "      pill.querySelector('.pfl-name').textContent = p.n;"
                "      pill.style.borderColor = p.c; pill.style.color = p.c;"
                "    }"
                "    pill.style.left = p.x + 'px'; pill.style.top = p.y + 'px';"
                "  });"
                "  Object.keys(pills).forEach(function(id){"
                "    if(!seen[id]){ el.removeChild(pills[id]); delete pills[id]; }"
                "  });"
                "}"
                "render();"
                "new MutationObserver(render).observe(el,{attributes:true,attributeFilter:['data-labels','data-charts']});"
                "})();"
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
    // gradient with nice-step ticks and the exact range ends — it follows the
    // selection (§A5: ensemble/pin → variance σ, mesh/cell → that pair's
    // difference or the displacement channel) and a live brush (§A4: the brushed
    // dots' shared signed range). All maps read on the unified pin-derived scale (§C).
    let colorLegend (model : AdaptiveModel) =
        let rangeA = MeshView.inspectRange model
        let dispA = MeshView.displacementRange model
        let orderContent = model.MeshOrder.Content
        let pinsVal = model.ScanPins.Pins |> AMap.toAVal
        let fmt (span : float) (v : float) =
            if span < 0.095 then sprintf "%.0f mm" (v * 1000.0)
            elif span < 0.95 then sprintf "%.0f cm" (v * 100.0)
            elif span < 10.0 then sprintf "%.2f m" v
            else sprintf "%.0f m" v
        let heatRangeMaxA = MeshView.rangeMaxWorld model
        // Outside Inspect the legend serves the Range heatmap (§B3): shown while any
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
                        // Brushing = sole focus: the maps stand down, the dots paint
                        // on the shared signed range.
                        "Brushed samples", lo, hi, Primitives.Diff.colorSignedV3 lo hi
                    elif soloName.IsNone then
                        let m = max 1e-6 (max (abs lo) hi)
                        "Variance σ", 0.0, m,
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
                            // Title names the compared meshes by display number
                            // (isolated moving mesh vs the reference); a cell
                            // selection appends its pin identity (§A5: "that pair").
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
        div {
            Class "color-legend"
            Primitives.showWhen
                ((model.WorkflowStep, anyRangeOn) ||> AVal.map2 (fun s r -> s = Inspect || r))
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
