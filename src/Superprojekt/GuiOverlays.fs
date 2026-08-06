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

    // The guided-placement alert: while the pin transaction has a pick armed,
    // one prominent top-centre banner names the current step (the reducer
    // chains centre → point A → point B by re-arming after each landing).
    // Purely derived — no step state exists; a committed pin's edits show no
    // banner (the transaction is over).
    let placementBanner (model : AdaptiveModel) =
        let step =
            AVal.custom (fun t ->
                match model.ScanPins.Placement.GetValue t with
                | PlacementIdle -> None
                | PlacementActive d ->
                    let stepNo =
                        1 + (if d.Area.IsSome then 1 else 0)
                          + (if d.PointA.IsSome then 1 else 0)
                          + (if d.PointB.IsSome then 1 else 0)
                    match model.ArmedPick.GetValue t with
                    | Some ArmCentre ->
                        Some (sprintf "Step %d of 3 — place the pin centre" stepNo,
                              "click the highlighted overlap area in any view")
                    | Some (ArmPoint m) ->
                        let num =
                            model.MeshOrder.Content.GetValue t
                            |> HashMap.tryFind m |> Option.defaultValue 0 |> (+) 1
                        Some (sprintf "Step %d of 3 — place the correspondence point on mesh %d" stepNo num,
                              "click the same terrain feature on this mesh (it renders alone while armed)")
                    | _ -> None)
        div {
            Class "place-banner"
            Primitives.showWhen (step |> AVal.map Option.isSome)
            span { Class "pb-title"; step |> AVal.map (function Some (tt, _) -> tt | None -> "") }
            span { Class "pb-hint"; step |> AVal.map (function Some (_, h) -> h | None -> "") }
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
                    let dists =
                        match MeshView.graphSideAt model t with
                        | EdgeBefore -> model.GraphDistBefore.GetValue t
                        | EdgeAfter -> model.GraphDist.GetValue t
                    (model.CellMapOn.GetValue t && not (Map.isEmpty dists))
                    || not (Set.isEmpty (model.BrushedSamples.GetValue t)))
        // The 3D-hovered dot's value, connecting "this value" to the scale: the
        // exact number the tooltip shows, or the dot's own sample value until
        // that fetch lands.
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
                // While the difference map paints, pointing at a mesh (tile/
                // tree hover or the isolate lock) CROPS the displayed range to
                // that mesh's own error extent (5th–95th pct of its resident
                // per-vertex buffer). The colour MAPPING stays the shared
                // scale — the bar shows the segment of the ramp the mesh
                // actually uses, labelled with its extent; un-pointing
                // restores the full scope range.
                let title, vLo, vHi =
                    if not (diffOn.GetValue t) then title, vLo, vHi
                    else
                        let target =
                            match model.TileIsolateHover.GetValue t with
                            | Some m -> Some m
                            | None -> model.TileIsolate.GetValue t
                        let distsOf (m : string) =
                            match model.Focus.GetValue t with
                            | FocusMatrix ->
                                let dm =
                                    match MeshView.graphSideAt model t with
                                    | EdgeBefore -> model.GraphDistBefore.GetValue t
                                    | EdgeAfter -> model.GraphDist.GetValue t
                                Map.tryFind m dm
                            | FocusPair | FocusPin ->
                                match (model.Sel.GetValue t).Pair with
                                | Some (a, b) ->
                                    let _, movM = MatrixNav.pairRefMov (model.RegGraph.GetValue t) a b
                                    if m = movM then model.CellDist.GetValue t else None
                                | None -> None
                        match target |> Option.bind (fun m -> distsOf m |> Option.map (fun d -> m, d)) with
                        | Some (m, arr) ->
                            let valid =
                                arr |> Array.choose (fun v ->
                                    if abs v < 1e20f then Some (float v) else None)
                            if valid.Length < 8 then title, vLo, vHi
                            else
                                Array.sortInPlace valid
                                let q p = valid.[min (valid.Length - 1) (int (p * float valid.Length))]
                                let cl = max vLo (q 0.05)
                                let ch = min vHi (q 0.95)
                                if ch - cl < 1e-6 then title, vLo, vHi
                                else
                                    let order = model.MeshOrder.Content.GetValue t
                                    let num = (HashMap.tryFind m order |> Option.defaultValue 0) + 1
                                    sprintf "%s · mesh %d" title num, cl, ch
                        | None -> title, vLo, vHi
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

    // The FORCED loop-resolution modal (P9): blocking, self-announcing — the
    // whole interaction for a transient loop. It NAMES the two meshes the
    // redundant edge connects (number + swatch), defines the residual and the
    // per-edge quality numbers, embeds the registration tree, and previews
    // every choice on it (hover-else-selection: the to-be-removed edge reads
    // red dashed, the new edge green dashed while kept). The user removes
    // exactly ONE edge; Confirm commits, Cancel/Esc discards the redundant
    // edge and the prior tree stands.
    let loopModal (env : Env<Message>) (model : AdaptiveModel) =
        let pending = model.LoopPending
        let esc (s : string) = s.Replace("\\", "\\\\").Replace("\"", "\\\"")
        let residualLine =
            pending |> AVal.map (function
                | Some lp ->
                    let trans =
                        if lp.ResidualTransM < 1.0 then sprintf "%.1f cm" (lp.ResidualTransM * 100.0)
                        else sprintf "%.2f m" lp.ResidualTransM
                    sprintf "Loop residual: the two paths disagree by %.1f° and %s." lp.ResidualRotDeg trans
                | None -> "")
        // The specific meshes the redundant edge connects — number + swatch.
        let meshChips =
            (pending, model.MeshOrder.Content, model.RegGraph) |||> AVal.map3 (fun lp order g ->
                match lp with
                | None -> IndexList.empty
                | Some lp ->
                    let chip (m : string) =
                        let i = HashMap.tryFind m order |> Option.defaultValue 0
                        let col = Primitives.c4bToRgbCss (Primitives.meshColorRoot (g.Root = Some m) i)
                        div {
                            Class "cw-chip"
                            span { Class "pmx-sw"; Style [Css.Background col] }
                            span { Class "pmx-num"; string (i + 1) }
                        }
                    IndexList.ofList [
                        chip lp.Mov
                        span { Class "cw-link"; "↔" }
                        chip lp.Ref
                    ])
            |> AList.ofAVal
        // The embedded tree + per-choice preview: hover-else-selection marks
        // the edge a confirm would REMOVE; the new edge renders dashed —
        // green while kept, red when it is the one to discard.
        let treeData =
            AVal.custom (fun t ->
                match model.LoopPending.GetValue t with
                | None -> "{}"
                | Some lp ->
                    let g = model.RegGraph.GetValue t
                    let names = IndexList.toList (model.MeshNames.Content.GetValue t)
                    let order = model.MeshOrder.Content.GetValue t
                    let choice = lp.Hover |> Option.defaultValue lp.Selected
                    let nodes =
                        names |> List.map (fun n ->
                            let i = HashMap.tryFind n order |> Option.defaultValue 0
                            let d = match MatrixNav.hopDepth g n with Some d -> d | None -> -1
                            sprintf "{\"id\":\"%s\",\"num\":%d,\"c\":\"%s\",\"d\":%d,\"root\":%b}"
                                (esc n) (i + 1)
                                (Primitives.c4bToHex (Primitives.meshColorRoot (g.Root = Some n) i))
                                d (g.Root = Some n))
                    let edges =
                        g.Edges |> Map.toList |> List.map (fun (c, e) ->
                            sprintf "{\"c\":\"%s\",\"p\":\"%s\",\"drop\":%b}"
                                (esc c) (esc e.Parent) (choice = Some c))
                    sprintf "{\"nodes\":[%s],\"edges\":[%s],\"nMov\":\"%s\",\"nRef\":\"%s\",\"dropNew\":%b}"
                        (String.concat "," nodes) (String.concat "," edges)
                        (esc lp.Mov) (esc lp.Ref) choice.IsNone)
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
                            // Hovering a choice previews its effect on the
                            // embedded tree above.
                            Dom.OnMouseEnter(fun _ -> env.Emit [HoverLoopChoice (Some sel)])
                            Dom.OnMouseLeave(fun _ -> env.Emit [HoverLoopChoice None])
                            span { Class "lm-edge"; label }
                            span {
                                Class "lm-q"
                                Attribute("title", "Solve quality of this edge = 1 / (1 + rms / 5 cm) over its pin residuals: 1.00 is a perfect fit, 0.50 ≈ 5 cm rms. Removing the lowest-quality edge keeps the best-fitting paths.")
                                sprintf "quality %.2f" q
                            }
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
                div { Class "lm-title"; "Two paths now connect" }
                div { Class "lm-meshes"; meshChips }
                div {
                    Class "lm-residual"
                    Attribute("title", "How far the loop is from closing: going around it one way vs the other differs by this rotation and by this displacement at the moving mesh's data.")
                    residualLine
                }
                div {
                    Class "loop-tree"
                    treeData |> AVal.map (fun j -> Some (Attribute("data-looptree", j)))
                    Primitives.observedRender "data-looptree" "{}" [
                        "  if(!d.nodes || !d.nodes.length){ return; }"
                        "  var NR=10, rowH=40, colW=34, padX=12, padY=12;"
                        "  var byId={}; d.nodes.forEach(function(n){ byId[n.id]=n; });"
                        "  var kids={}; d.edges.forEach(function(e){ (kids[e.p]=kids[e.p]||[]).push(e.c); });"
                        "  Object.keys(kids).forEach(function(k){ kids[k].sort(function(a,b){ return byId[a].num-byId[b].num; }); });"
                        "  var X={}, cnt=0;"
                        "  var root=null; d.nodes.forEach(function(n){ if(n.root) root=n; });"
                        "  function lay(id){ var ks=kids[id]||[];"
                        "    if(!ks.length){ X[id]=cnt++; return; }"
                        "    ks.forEach(lay); X[id]=(X[ks[0]]+X[ks[ks.length-1]])/2; }"
                        "  if(root) lay(root.id);"
                        "  var isl=d.nodes.filter(function(n){ return n.d<0; });"
                        "  isl.forEach(function(n,i){ n._ix=i; });"
                        "  var maxD=0; d.nodes.forEach(function(n){ if(n.d>maxD) maxD=n.d; });"
                        "  var cols=Math.max(cnt, isl.length, 1);"
                        "  var W=padX*2+NR*2+(cols-1)*colW;"
                        "  var islY=padY+NR+(maxD+1)*rowH+14;"
                        "  var H=(isl.length? islY : padY+NR+maxD*rowH)+NR+padY;"
                        "  var svg=document.createElementNS(ns,'svg');"
                        "  svg.setAttribute('width',W); svg.setAttribute('height',H);"
                        "  svg.setAttribute('viewBox','0 0 '+W+' '+H); svg.style.display='block'; svg.style.margin='0 auto';"
                        "  function px(id){ var n=byId[id]; return padX+NR+(n.d<0 ? n._ix : (X[id]||0))*colW; }"
                        "  function py(id){ var n=byId[id]; return n.d<0 ? islY : padY+NR+n.d*rowH; }"
                        "  d.edges.forEach(function(e){"
                        "    var ln=document.createElementNS(ns,'line');"
                        "    ln.setAttribute('x1',px(e.p)); ln.setAttribute('y1',py(e.p)+NR); ln.setAttribute('x2',px(e.c)); ln.setAttribute('y2',py(e.c)-NR);"
                        "    if(e.drop){ ln.setAttribute('stroke','#dc2626'); ln.setAttribute('stroke-width',3); ln.setAttribute('stroke-dasharray','4 3'); }"
                        "    else { ln.setAttribute('stroke','#64748b'); ln.setAttribute('stroke-width',1.5); }"
                        "    svg.appendChild(ln);"
                        "  });"
                        "  if(d.nMov && byId[d.nMov] && byId[d.nRef]){"
                        "    var nv=document.createElementNS(ns,'line');"
                        "    nv.setAttribute('x1',px(d.nMov)); nv.setAttribute('y1',py(d.nMov)); nv.setAttribute('x2',px(d.nRef)); nv.setAttribute('y2',py(d.nRef));"
                        "    nv.setAttribute('stroke', d.dropNew?'#dc2626':'#15803d'); nv.setAttribute('stroke-width',2.5); nv.setAttribute('stroke-dasharray','5 4');"
                        "    nv.setAttribute('opacity','0.9');"
                        "    svg.appendChild(nv);"
                        "  }"
                        "  d.nodes.forEach(function(n){"
                        "    var cx=px(n.id), cy=py(n.id);"
                        "    if(n.root){ var gr=document.createElementNS(ns,'circle');"
                        "      gr.setAttribute('cx',cx); gr.setAttribute('cy',cy); gr.setAttribute('r',NR+3);"
                        "      gr.setAttribute('fill','none'); gr.style.stroke='var(--ref-gold)'; gr.setAttribute('stroke-width',2);"
                        "      svg.appendChild(gr); }"
                        "    var c=document.createElementNS(ns,'circle');"
                        "    c.setAttribute('cx',cx); c.setAttribute('cy',cy); c.setAttribute('r',NR); c.setAttribute('fill','#ffffff');"
                        "    if(n.d<0){ c.setAttribute('stroke','#94a3b8'); c.setAttribute('stroke-dasharray','3 2.5'); c.setAttribute('stroke-width',1.5); }"
                        "    else { c.setAttribute('stroke',n.c); c.setAttribute('stroke-width',2.5); }"
                        "    svg.appendChild(c);"
                        "    var tx=document.createElementNS(ns,'text');"
                        "    tx.setAttribute('x',cx); tx.setAttribute('y',cy+3.5); tx.setAttribute('text-anchor','middle');"
                        "    tx.setAttribute('font-size','10'); tx.setAttribute('font-weight','700');"
                        "    tx.setAttribute('fill','#0f172a'); tx.setAttribute('font-family','Inter,sans-serif');"
                        "    tx.textContent=n.num;"
                        "    svg.appendChild(tx);"
                        "  });"
                        "  el.appendChild(svg);"
                    ]
                }
                div { Class "lm-treehint"; "green dashed = the new connection · red dashed = removed by the highlighted choice" }
                div { Class "lm-hint"; "Remove one edge on the loop to keep a single registration path (the weakest link is pre-selected; hover a choice to preview it above):" }
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

    // The already-connected pre-warning: front-half of the loop flow — opening
    // a cell whose meshes the tree already connects (indirectly) warns BEFORE
    // any placement; proceeding still leads to the loop-resolution modal on
    // solve. Esc cancels.
    let pairConnectModal (env : Env<Message>) (model : AdaptiveModel) =
        let bodyLine =
            (model.PairConnectWarn, model.MeshOrder.Content) ||> AVal.map2 (fun w order ->
                match w with
                | Some (a, b) ->
                    let numOf m = (HashMap.tryFind m order |> Option.defaultValue 0) + 1
                    sprintf "Mesh %d and mesh %d are already registered to each other through the tree. Registering them directly adds a second, redundant connection — a loop you will then have to resolve." (numOf a) (numOf b)
                | None -> "")
        div {
            Class "modal-scrim"
            Primitives.showWhen (model.PairConnectWarn |> AVal.map Option.isSome)
            div {
                Class "loop-modal"
                div { Class "lm-title"; "These meshes are already connected" }
                div { Class "lm-hint"; bodyLine }
                div {
                    Class "lm-buttons"
                    button {
                        Class "rail-btn lm-cancel"
                        Attribute("title", "Stay at the matrix (Esc)")
                        Dom.OnClick(fun _ -> env.Emit [CancelPairConnectWarn])
                        "Cancel"
                    }
                    button {
                        Class "rail-btn lm-confirm"
                        Attribute("title", "Open the pair anyway — a solve here will raise the loop-resolution dialog")
                        Dom.OnClick(fun _ -> env.Emit [ConfirmPairConnectWarn])
                        "Proceed anyway"
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
