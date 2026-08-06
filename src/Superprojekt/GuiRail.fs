namespace Superprojekt

open Aardvark.Base
open Aardworx.WebAssembly
open FSharp.Data.Adaptive
open Aardvark.Dom
open Microsoft.JSInterop

// Left panel: the registration navigator — the focus rail (the mesh×mesh pair
// matrix and its narrowing Pair/Pin scopes) and the cell-workspace (per-pair
// toolkit + error inspection). Pure view — every control dispatches an
// existing message and never issues server queries itself.
module GuiRail =

    open Primitives

    let private rgb = Primitives.c4bToRgbCss

    // ── The ONE in-cell error diagram: uniform-grey histogram (48 bins) of
    // the scope's samples, with the before/after diff (fill = current state,
    // DASHED near-black step outline = the before total) and a real legend
    // (payload legF/legL name the fill and the outline). Full furniture
    // always; an empty payload renders a centred placeholder. Brushing is
    // BIN-QUANTIZED: the drag snaps to whole bins, a drag-free click selects
    // the single bin under the cursor (an empty bin clears), hover outlines
    // the bin; gids flow as csv through the hidden bridge input.
    // data-brushed echoes the model back; data-hover (the 3D-hovered gid)
    // draws the amber cross-highlight; data-hilite (the hovered pin row's
    // name) repaints that pin's stack slice amber.
    let private chartJs = [
        "(function(){"
        "var el=__THIS__;"
        "function findBridge(){ return (el.parentElement&&el.parentElement.querySelector('.cw-brush-bridge'))||document.querySelector('.cw-brush-bridge'); }"
        "el._dots=[]; el._hb=-1; var dragging=false, range=null, anchorV=0;"
        "function emit(){ var b=findBridge(); if(!b) return; var ids=[];"
        "  if(range){ for(var i=0;i<el._dots.length;i++){ var dt=el._dots[i]; if(dt.v>=range[0]&&dt.v<=range[1]) ids.push(dt.gid); } }"
        "  b.value=ids.join(','); b.dispatchEvent(new Event('input',{bubbles:true})); }"
        "function niceStep(raw){ var mag=Math.pow(10,Math.floor(Math.log(raw)/Math.LN10)); var n=raw/mag; return (n<1.5?1:n<3.5?2:n<7.5?5:10)*mag; }"
        "function render(){"
        "  var raw=el.getAttribute('data-chart')||'{}'; var d; try{d=JSON.parse(raw);}catch(e){return;}"
        "  var braw=el.getAttribute('data-brushed')||''; var bset=new Set(braw.length?braw.split(',').map(Number):[]);"
        "  var hraw=el.getAttribute('data-hover')||''; var hgid=hraw.length?parseInt(hraw):-1;"
        "  var hl=el.getAttribute('data-hilite')||'';"
        "  el.innerHTML=''; el._dots=[];"
        "  var W=el.clientWidth||280, H=el.clientHeight||150; var dpr=window.devicePixelRatio||1;"
        "  var cv=document.createElement('canvas'); cv.width=Math.round(W*dpr); cv.height=Math.round(H*dpr);"
        "  cv.style.width=W+'px'; cv.style.height=H+'px';"
        "  var g=cv.getContext('2d'); g.setTransform(dpr,0,0,dpr,0,0);"
        "  g.fillStyle='#ffffff'; g.fillRect(0,0,W,H);"
        "  var padL=30,padR=8,padT=18,padB=22;"
        "  var lo=(d.lo!=null?d.lo:-10), hi=(d.hi!=null?d.hi:10); var span=Math.max(1e-6,hi-lo);"
        "  function X(v){ return padL+(v-lo)/span*(W-padL-padR); }"
        "  el._XV=function(x){ return lo+(x-padL)/Math.max(1,W-padL-padR)*span; };"
        "  var axY=H-padB, axT=padT;"
        "  var B=d.bins||48; var bw=span/B;"
        // Bin geometry for the quantized brush + hover (handlers live outside).
        "  el._bin={lo:lo,hi:hi,bw:bw,B:B};"
        "  el._inPlot=function(x,y){ return x>=padL&&x<=W-padR&&y>=axT&&y<=axY; };"
        "  g.fillStyle='#0f172a'; g.font='700 11px Inter,\\'Segoe UI\\',sans-serif'; g.textAlign='left';"
        "  g.fillText(d.title||'',4,12);"
        "  g.strokeStyle='#94a3b8'; g.lineWidth=1;"
        "  g.beginPath(); g.moveTo(padL,axY+0.5); g.lineTo(W-padR,axY+0.5); g.stroke();"
        "  g.beginPath(); g.moveTo(padL+0.5,axT); g.lineTo(padL+0.5,axY); g.stroke();"
        "  var step=niceStep(span/5); var dec=step>=1?0:(step>=0.1?1:2);"
        "  g.font='9px SF Mono,Monaco,monospace'; g.textAlign='center';"
        "  for(var v=Math.ceil(lo/step)*step; v<=hi+1e-9; v+=step){ var x=X(v);"
        "    g.strokeStyle='#eef2f6'; g.beginPath(); g.moveTo(x,axT); g.lineTo(x,axY); g.stroke();"
        "    g.strokeStyle='#94a3b8'; g.beginPath(); g.moveTo(x,axY); g.lineTo(x,axY+4); g.stroke();"
        "    g.fillStyle='#64748b'; g.fillText(v.toFixed(dec),x,axY+13); }"
        "  if(lo<=0&&hi>=0){ g.strokeStyle='#cbd5e1'; g.lineWidth=1.2; g.beginPath(); g.moveTo(X(0),axT); g.lineTo(X(0),axY); g.stroke(); g.lineWidth=1; }"
        "  g.fillStyle='#64748b'; g.font='9px Inter,sans-serif'; g.textAlign='right';"
        "  g.fillText('signed error (mm)',W-padR,H-3);"
        "  var S=d.series||[];"
        "  if(S.length===0){ g.fillStyle='#94a3b8'; g.font='12px Inter,sans-serif'; g.textAlign='center';"
        "    g.fillText(d.ph||'\\u2014',(padL+W-padR)/2,(axT+axY)/2+4); el.appendChild(cv); return; }"
        "  function histC(sn,k){ var a=sn[k]; return (a&&a.length===B)?a:null; }"
        // One count→height scale over BOTH sides; y = counts.
        "  var maxC=1; ['h','hb'].forEach(function(k){ for(var b=0;b<B;b++){ var s=0; S.forEach(function(sn){ var a=histC(sn,k); if(a) s+=a[b]; }); if(s>maxC)maxC=s; } });"
        "  var k=(axY-axT-4)/maxC;"
        "  if(d.lod>0){ g.fillStyle='rgba(148,163,184,0.18)'; g.fillRect(X(-d.lod),axT,Math.max(1,X(d.lod)-X(-d.lod)),axY-axT); }"
        // Filled stack: the CURRENT side; the hovered pin row's slice (hilite
        // by series name) lights amber AND restacks to the BOTTOM, so the
        // highlighted portion always reads from the shared baseline instead
        // of floating mid-stack.
        "  var prev=new Array(B).fill(0);"
        "  var stackOrd=hl?S.filter(function(sn){return sn.name===hl;}).concat(S.filter(function(sn){return sn.name!==hl;})):S;"
        "  stackOrd.forEach(function(sn){ var a=histC(sn,'h'); if(!a) return;"
        "    g.globalAlpha=0.85; g.fillStyle=(hl&&sn.name===hl)?'#d97706':sn.color;"
        "    for(var b=0;b<B;b++){ var c=a[b]; if(c>0){ var x0=X(lo+b*bw); var wd=Math.max(1,X(lo+(b+1)*bw)-x0);"
        "      g.fillRect(x0,axY-(prev[b]+c)*k,wd,c*k); } prev[b]+=c; } });"
        // BEFORE: dashed near-black step outline of the total (shape only).
        "  var anyO=false; var tot=new Array(B).fill(0);"
        "  S.forEach(function(sn){ var a=histC(sn,'hb'); if(!a) return; anyO=true; for(var b=0;b<B;b++) tot[b]+=a[b]; });"
        "  if(anyO){ g.globalAlpha=0.9; g.strokeStyle='#0f172a'; g.lineWidth=1; g.setLineDash([4,3]);"
        "    g.beginPath(); g.moveTo(X(lo),axY);"
        "    for(var b=0;b<B;b++){ var yT=axY-tot[b]*k; g.lineTo(X(lo+b*bw),yT); g.lineTo(X(lo+(b+1)*bw),yT); }"
        "    g.lineTo(X(hi),axY); g.stroke(); g.setLineDash([]); }"
        // The REAL legend: a fill swatch + a dashed-line sample, named by the
        // payload (legF/legL) — the title is not a legend.
        "  g.globalAlpha=1; g.font='9px Inter,sans-serif'; g.textAlign='left';"
        "  var lx=padL+4, ly=axT+8;"
        "  if(d.legF){ g.fillStyle='rgba(120,120,120,0.85)'; g.fillRect(lx,ly-6,8,7);"
        "    g.fillStyle='#475569'; g.fillText(d.legF,lx+11,ly); lx+=11+g.measureText(d.legF).width+12; }"
        "  if(d.legL&&anyO){ g.strokeStyle='#0f172a'; g.lineWidth=1; g.setLineDash([4,3]);"
        "    g.beginPath(); g.moveTo(lx,ly-3); g.lineTo(lx+14,ly-3); g.stroke(); g.setLineDash([]);"
        "    g.fillStyle='#475569'; g.fillText(d.legL,lx+17,ly); }"
        "  S.forEach(function(sn){ if(sn.med==null) return; g.globalAlpha=0.9; g.strokeStyle=(hl&&sn.name===hl)?'#d97706':sn.color; g.lineWidth=1.6;"
        "    g.beginPath(); g.moveTo(X(sn.med),axY); g.lineTo(X(sn.med),axY-9); g.stroke(); g.lineWidth=1; });"
        // Conceptual samples: never painted, but they ARE the brush targets.
        "  g.globalAlpha=1;"
        "  S.forEach(function(sn){ if(sn.g) for(var q=0;q<sn.g.length;q++) el._dots.push({gid:sn.g[q],v:sn.s[q]}); });"
        // Hovered bin: a thin outline column (the single-bin select preview).
        "  if(el._hb>=0&&el._hb<B&&!dragging){ var hbx=X(lo+el._hb*bw);"
        "    g.fillStyle='rgba(8,145,178,0.07)'; g.fillRect(hbx,axT,Math.max(1,X(lo+(el._hb+1)*bw)-hbx),axY-axT);"
        "    g.strokeStyle='#0891b2'; g.lineWidth=1; g.strokeRect(hbx+0.5,axT+0.5,Math.max(1,X(lo+(el._hb+1)*bw)-hbx)-1,axY-axT-1); }"
        // 3D-hovered sample: amber cross-highlight at its value.
        "  if(hgid>=0){ for(var q2=0;q2<el._dots.length;q2++){ if(el._dots[q2].gid===hgid){"
        "    var hx=X(Math.max(lo,Math.min(hi,el._dots[q2].v)));"
        "    g.strokeStyle='#d97706'; g.lineWidth=1.8; g.beginPath(); g.moveTo(hx,axT); g.lineTo(hx,axY); g.stroke(); g.lineWidth=1; break; } } }"
        // Brush band: the local drag range, else reconstructed from the echo.
        "  var dispRange=range;"
        "  if(!dispRange&&bset.size){ var mn=1/0,mx=-1/0; for(var q3=0;q3<el._dots.length;q3++){ var dd=el._dots[q3]; if(bset.has(dd.gid)){ if(dd.v<mn)mn=dd.v; if(dd.v>mx)mx=dd.v; } } if(mx>=mn) dispRange=[mn,mx]; }"
        "  if(dispRange){ var x0b=X(dispRange[0]), x1b=X(dispRange[1]);"
        "    g.fillStyle='rgba(8,145,178,0.10)'; g.fillRect(x0b,axT,Math.max(1,x1b-x0b),axY-axT);"
        "    g.strokeStyle='#0891b2'; g.lineWidth=1.2; g.beginPath(); g.moveTo(x0b,axT); g.lineTo(x0b,axY); g.moveTo(x1b,axT); g.lineTo(x1b,axY); g.stroke(); }"
        "  el.appendChild(cv);"
        "}"
        "function cursorV(e){ var r=el.getBoundingClientRect(); return el._XV?el._XV(e.clientX-r.left):0; }"
        "function binOf(v){ var bn=el._bin; if(!bn) return -1; return Math.max(0,Math.min(bn.B-1,Math.floor((v-bn.lo)/bn.bw))); }"
        // Bin-quantized brush: the drag snaps to whole bins; a drag-free click
        // is a SINGLE-BIN select (an empty bin yields no gids = clear).
        "function snapRange(vA,vB){ var bn=el._bin; if(!bn) return [Math.min(vA,vB),Math.max(vA,vB)];"
        "  var b0=binOf(vA), b1=binOf(vB); var mn=Math.min(b0,b1), mx=Math.max(b0,b1);"
        "  return [bn.lo+mn*bn.bw, bn.lo+(mx+1)*bn.bw]; }"
        "el.addEventListener('pointerdown',function(e){ dragging=true; anchorV=cursorV(e); range=null; el.setPointerCapture(e.pointerId); });"
        "el.addEventListener('pointermove',function(e){ var v=cursorV(e);"
        "  if(dragging){ range=snapRange(anchorV,v); render(); }"
        "  else { var r=el.getBoundingClientRect(); var nb=(el._inPlot&&el._inPlot(e.clientX-r.left,e.clientY-r.top))?binOf(v):-1;"
        "    if(nb!==el._hb){ el._hb=nb; render(); } } });"
        "el.addEventListener('pointerleave',function(){ if(el._hb!==-1){ el._hb=-1; render(); } });"
        "el.addEventListener('pointerup',function(e){ if(!dragging) return; dragging=false;"
        "  range=snapRange(anchorV,cursorV(e)); emit(); render(); });"
        "render();"
        "new MutationObserver(render).observe(el,{attributes:true,attributeFilter:['data-chart','data-brushed','data-hover','data-hilite']});"
        // Re-render on size changes — including display:none → shown (the
        // floating panel hides wholesale), where boot painted at width 0.
        "new ResizeObserver(function(){ render(); }).observe(el);"
        "})();"
    ]

    // ── The inspection toolbox's body, keyed to ONE pair: the diagram, the
    // false-colour map toggle, the isolate-pins view mode and the hover
    // readout. The Pin level narrows the diagram to the selected pin — gids
    // stay CANONICAL (indices into the full CellError sample concatenation),
    // so the brush keeps addressing the same 3D samples at either level.
    let private inspectBody (env : Env<Message>) (model : AdaptiveModel) (a : string) (b : string) =
        let chartData =
            AVal.custom (fun t ->
                let inv = System.Globalization.CultureInfo.InvariantCulture
                let gf (v : float) =
                    if System.Double.IsNaN v || System.Double.IsInfinity v then "0" else v.ToString("0.###", inv)
                let order = model.MeshOrder.Content.GetValue t
                let numOfN m = (HashMap.tryFind m order |> Option.defaultValue 0) + 1
                let refM, movM = MatrixNav.pairRefMov (model.RegGraph.GetValue t) a b
                let pins = model.ScanPins.Pins.Content.GetValue t
                let onlyPin =
                    match model.Focus.GetValue t, (model.Sel.GetValue t).Pin with
                    | FocusPin, Some id -> Some id
                    | _ -> None
                // The pose peek flips the visible distribution to the edge-
                // before state (both resident — zero refetch); gids/values stay
                // the CANONICAL now-stream, so a brush held across the peek
                // highlights the corresponding region, matrix-style.
                let before0 = model.CellErrorBefore.GetValue t
                let peeked = model.PeekPose.GetValue t && before0.IsSome
                let title0 =
                    match onlyPin |> Option.bind (fun id -> HashMap.tryFind id pins) with
                    | Some p -> sprintf "Pin %s — mesh %d error vs %d" p.ShortName (numOfN movM) (numOfN refM)
                    | None -> sprintf "Mesh %d error vs %d — across pins" (numOfN movM) (numOfN refM)
                let title = if peeked then title0 + " — as loaded" else title0
                match model.CellError.GetValue t with
                | None ->
                    sprintf "{\"title\":\"%s\",\"ph\":\"place pins to measure\",\"lo\":-10,\"hi\":10,\"bins\":48,\"series\":[]}" title
                | Some cells ->
                    let before = before0
                    // The x-range stays the FULL cell's (the shared-scale rule),
                    // so a pin-narrowed diagram is comparable across pins.
                    let allSamples =
                        seq {
                            for (_, r) in cells do yield! r.Samples
                            match before with
                            | Some bs -> for (_, r) in bs do yield! r.Samples
                            | None -> ()
                        }
                    let lo0, hi0 = ErrorRange.ofSamples allSamples
                    let lo, hi = lo0 * 1000.0, hi0 * 1000.0
                    let pad = max 1.0 (hi - lo) * 0.08
                    let lo, hi = lo - pad, hi + pad
                    let bins = 48
                    let binW = (hi - lo) / float bins
                    let histOf (samples : float[]) =
                        let c : int[] = Array.zeroCreate bins
                        for v in samples do
                            let idx = max 0 (min (bins - 1) (int ((v * 1000.0 - lo) / binW)))
                            c.[idx] <- c.[idx] + 1
                        c
                    let beforeOf pid =
                        before |> Option.bind (fun bs ->
                            bs |> Array.tryFind (fun (bid, _) -> bid = pid) |> Option.map snd)
                    let lods =
                        if peeked then
                            (before |> Option.defaultValue [||])
                            |> Array.choose (fun (_, r) -> if r.Count > 0 then Some r.LodHalfWidth else None)
                        else cells |> Array.choose (fun (_, r) -> if r.Count > 0 then Some r.LodHalfWidth else None)
                    let lod = if lods.Length = 0 then 0.0 else (Array.average lods) * 1000.0
                    let mutable gid = 0
                    let series =
                        cells |> Array.map (fun (pid, r) ->
                            let g0 = gid
                            gid <- gid + r.Samples.Length
                            match onlyPin with
                            | Some id when id <> pid -> None
                            | _ ->
                                let name =
                                    match HashMap.tryFind pid pins with
                                    | Some p -> p.ShortName
                                    | None -> "?"
                                let hb = beforeOf pid |> Option.map (fun br -> histOf br.Samples)
                                // The VISIBLE distribution (fill + median): the
                                // peeked state's when held, else the current.
                                let fill =
                                    if peeked then
                                        beforeOf pid |> Option.map (fun br -> br.Samples) |> Option.defaultValue [||]
                                    else r.Samples
                                let medOf (vs : float[]) =
                                    if vs.Length = 0 then "null"
                                    else gf ((Array.sort vs).[vs.Length / 2] * 1000.0)
                                let med = if peeked then medOf fill elif r.Count > 0 then gf (r.Median * 1000.0) else "null"
                                let gids = Array.init r.Samples.Length (fun k -> g0 + k)
                                let hj = histOf fill |> Array.map string |> String.concat ","
                                let hbj = hb |> Option.map (fun c -> c |> Array.map string |> String.concat ",") |> Option.defaultValue ""
                                Some (
                                    sprintf "{\"name\":\"%s\",\"color\":\"#787878\",\"med\":%s,\"g\":[%s],\"s\":[%s],\"h\":[%s],\"hb\":[%s]}"
                                        name med
                                        (gids |> Array.map string |> String.concat ",")
                                        (r.Samples |> Array.map (fun v -> gf (v * 1000.0)) |> String.concat ",")
                                        hj hbj))
                        |> Array.choose id
                        |> String.concat ","
                    let legF = if peeked then "as loaded" else "error now"
                    sprintf "{\"title\":\"%s\",\"lo\":%s,\"hi\":%s,\"bins\":%d,\"lod\":%s,\"legF\":\"%s\",\"legL\":\"before registration\",\"series\":[%s]}"
                        title (gf lo) (gf hi) bins (gf lod) legF series)
        let brushedData = model.BrushedSamples |> AVal.map (fun s -> s |> Seq.map string |> String.concat ",")
        let hoverData = model.HoverSample |> AVal.map (function Some g -> string g | None -> "")
        // The hovered pin row's name — its stack slice lights amber (F4↔F5).
        let hiliteData =
            (model.TilePinHover, model.ScanPins.Pins |> AMap.toAVal) ||> AVal.map2 (fun h pins ->
                match h |> Option.bind (fun id -> HashMap.tryFind id pins) with
                | Some p -> p.ShortName
                | None -> "")
        div {
            Class "cw-inspect"
            div {
                Class "cw-readout"
                span {
                    Class "cw-readout-hover"
                    model.HoverReadout |> AVal.map (function
                        | Some (_, v) -> sprintf "sample %+.1f mm" (v * 1000.0)
                        | None -> "")
                }
            }
            // The chart's peer row: the SPATIAL view toggle (the error map)
            // beside the always-on distribution — complementary views, both
            // live at once — plus the brush clear (the ex-probe slot).
            div {
                Class "cw-chart-tools"
                div {
                    Class "rail-isolate"
                    Attribute("title", "False-colour error map in 3D: paints the MOV mesh's signed distance vs the reference on the same scale as this histogram (the reference is never error-coloured). At the Pin level the map narrows to the pin's area. The map and the histogram stay live together.")
                    compactToggle "Error map (3D)" model.CellMapOn (fun () -> env.Emit [ToggleCellMap])
                }
                button {
                    Class "rail-btn cw-clearbrush"
                    model.BrushedSamples |> AVal.map (fun s ->
                        if Set.isEmpty s then Some (Attribute("disabled", "disabled")) else None)
                    Attribute("title", "Clear the histogram brush selection (and its 3D dots)")
                    Dom.OnClick(fun _ -> env.Emit [SetBrushedSamples []])
                    "⊗ Clear brush"
                }
            }
            div {
                Class "cw-chart"
                chartData |> AVal.map (fun j -> Some (Attribute("data-chart", j)))
                brushedData |> AVal.map (fun bd -> Some (Attribute("data-brushed", bd)))
                hoverData |> AVal.map (fun h -> Some (Attribute("data-hover", h)))
                hiliteData |> AVal.map (fun h -> Some (Attribute("data-hilite", h)))
                OnBoot chartJs
            }
            // The JS→Elm brush bridge (hidden; the chart dispatches input).
            input {
                Class "cw-brush-bridge"
                Dom.OnInput(fun e ->
                    let ids =
                        (e.Value : string).Split(',')
                        |> Array.choose (fun sSeg ->
                            match System.Int32.TryParse sSeg with
                            | true, v -> Some v
                            | _ -> None)
                        |> Array.toList
                    env.Emit [SetBrushedSamples ids])
            }
        }

    // ── The inspection toolbox's GRAPH body (Matrix scope): the same
    // instruments resolved against the WHOLE registration tree instead of one
    // edge — every error here is parent-relative (a child measured against the
    // neighbour one hop toward the root), so only established edges
    // contribute and an edgeless graph is legitimately empty.
    let private graphBody (env : Env<Message>) (model : AdaptiveModel) =
        // ONE pooled monochrome series: every established edge's samples
        // concatenated into a single distribution (pooled SAMPLES, not bin-wise
        // added counts) in the PEEKED state, plus ONE pooled before-outline
        // (the same union) — a single step line stays readable where N
        // per-edge ghosts would not, and colouring by edge would put a second
        // key on the diagram. The brush is what identifies a source: its dots
        // resolve to their own meshes in 3D. The pose peek swaps the whole
        // distribution (as-loaded ⇄ residual) on the FIXED axis below, so the
        // mass visibly collapses toward zero instead of rescaling to look
        // identical.
        let chartData =
            AVal.custom (fun t ->
                let inv = System.Globalization.CultureInfo.InvariantCulture
                let gf (v : float) =
                    if System.Double.IsNaN v || System.Double.IsInfinity v then "0" else v.ToString("0.###", inv)
                let peeked = MeshView.graphSideAt model t = EdgeBefore
                let title =
                    if peeked then "Graph error vs parents — as loaded"
                    else "Graph error vs parents — all registered edges"
                let blocks = MeshView.inspectBlocksAt model t
                let samples = blocks |> Array.collect (fun b -> b.Err.Samples)
                if samples.Length = 0 then
                    sprintf "{\"title\":\"%s\",\"ph\":\"register pairs to measure\",\"lo\":-10,\"hi\":10,\"bins\":48,\"series\":[]}" title
                else
                    // Axis + binning from the SHARED (before-state) range, so
                    // both states are read against one ruler.
                    let lo0, hi0 = MeshView.inspectRangeAt model t
                    let lo, hi = lo0 * 1000.0, hi0 * 1000.0
                    let pad = max 1.0 (hi - lo) * 0.08
                    let lo, hi = lo - pad, hi + pad
                    let bins = 48
                    let binW = (hi - lo) / float bins
                    let histOf (vs : float[]) =
                        let c : int[] = Array.zeroCreate bins
                        for v in vs do
                            let idx = max 0 (min (bins - 1) (int ((v * 1000.0 - lo) / binW)))
                            c.[idx] <- c.[idx] + 1
                        c
                    let med =
                        let s = Array.sort samples
                        gf (s.[s.Length / 2] * 1000.0)
                    let lods = blocks |> Array.choose (fun b -> if b.Err.Count > 0 then Some b.Err.LodHalfWidth else None)
                    let lod = if lods.Length = 0 then 0.0 else (Array.average lods) * 1000.0
                    // The pooled BEFORE outline (the same union of every edge's
                    // samples the before-state cache holds), on the shared fixed
                    // axis — the one step line stays readable where N per-edge
                    // ghosts would not. While the pose peek shows the before
                    // state, fill and line coincide by construction.
                    let beforeSamples =
                        model.GraphErrorBefore.GetValue t
                        |> Option.defaultValue [||]
                        |> Array.collect (fun b -> b.Err.Samples)
                    let hbj =
                        if beforeSamples.Length = 0 then ""
                        else histOf beforeSamples |> Array.map string |> String.concat ","
                    let series =
                        sprintf "{\"name\":\"graph\",\"color\":\"%s\",\"med\":%s,\"g\":[%s],\"s\":[%s],\"h\":[%s],\"hb\":[%s]}"
                            (c4bToHex (C4b(120uy, 120uy, 120uy))) med
                            (Array.init samples.Length string |> String.concat ",")
                            (samples |> Array.map (fun v -> gf (v * 1000.0)) |> String.concat ",")
                            (histOf samples |> Array.map string |> String.concat ",")
                            hbj
                    let legF = if peeked then "as loaded" else "error vs parents"
                    sprintf "{\"title\":\"%s\",\"lo\":%s,\"hi\":%s,\"bins\":%d,\"lod\":%s,\"legF\":\"%s\",\"legL\":\"before registration\",\"series\":[%s]}"
                        title (gf lo) (gf hi) bins (gf lod) legF series)
        let brushedData = model.BrushedSamples |> AVal.map (fun s -> s |> Seq.map string |> String.concat ",")
        let hoverData = model.HoverSample |> AVal.map (function Some g -> string g | None -> "")
        div {
            Class "cw-inspect"
            div {
                Class "cw-readout"
                span {
                    Class "cw-readout-hover"
                    model.HoverReadout |> AVal.map (function
                        | Some (_, v) -> sprintf "sample %+.1f mm" (v * 1000.0)
                        | None -> "")
                }
            }
            // The chart's peer row: the SPATIAL view toggle beside the
            // always-on distribution (both live at once) plus the brush clear.
            div {
                Class "cw-chart-tools"
                div {
                    Class "rail-isolate"
                    Attribute("title", "False-colour error map in 3D: paints every registered mesh with its own parent-relative error at once, on the same scale as this histogram. The reference root and unregistered meshes stay excluded outlines. The map and the histogram stay live together.")
                    compactToggle "Error map (3D)" model.CellMapOn (fun () -> env.Emit [ToggleCellMap])
                }
                button {
                    Class "rail-btn cw-clearbrush"
                    model.BrushedSamples |> AVal.map (fun s ->
                        if Set.isEmpty s then Some (Attribute("disabled", "disabled")) else None)
                    Attribute("title", "Clear the histogram brush selection (and its 3D dots)")
                    Dom.OnClick(fun _ -> env.Emit [SetBrushedSamples []])
                    "⊗ Clear brush"
                }
            }
            div {
                Class "cw-chart"
                chartData |> AVal.map (fun j -> Some (Attribute("data-chart", j)))
                brushedData |> AVal.map (fun bd -> Some (Attribute("data-brushed", bd)))
                hoverData |> AVal.map (fun h -> Some (Attribute("data-hover", h)))
                OnBoot chartJs
            }
            // The JS→Elm brush bridge (hidden; the chart dispatches input).
            input {
                Class "cw-brush-bridge"
                Dom.OnInput(fun e ->
                    let ids =
                        (e.Value : string).Split(',')
                        |> Array.choose (fun sSeg ->
                            match System.Int32.TryParse sSeg with
                            | true, v -> Some v
                            | _ -> None)
                        |> Array.toList
                    env.Emit [SetBrushedSamples ids])
            }
        }

    // ── The docked inspection toolbox: top-left below the navigator, present
    // at EVERY level — Matrix reads the whole graph, Pair one edge, Pin the
    // selected pin — collapsible to its thin header edge (the header IS the
    // top-left toggle).
    let inspectPanel (env : Env<Message>) (model : AdaptiveModel) =
        let visible =
            (model.Focus, model.Sel) ||> AVal.map2 (fun f s ->
                f = FocusMatrix || s.Pair.IsSome)
        let content =
            (model.Focus, model.Sel) ||> AVal.map2 (fun f s ->
                match f, s.Pair with
                | FocusMatrix, _ -> IndexList.ofList [ graphBody env model ]
                | _, Some (a, b) -> IndexList.ofList [ inspectBody env model a b ]
                | _, None -> IndexList.empty)
            |> AList.ofAVal
        // The collapse flag is per level (Matrix defaults collapsed, the pair
        // workspace open) — the header toggles the CURRENT level's flag.
        let openHere = (model.Focus, model.InspectOpen) ||> AVal.map2 LevelFlags.get
        div {
            Class "inspect-dock"
            Primitives.showWhen visible
            div {
                Class "inspect-dock-head"
                Attribute("title", "Inspection toolbox — click to collapse/expand (remembered per workflow level)")
                Dom.OnClick(fun _ -> env.Emit [ToggleInspectPanel])
                span {
                    Class "inspect-dock-caret"
                    openHere |> AVal.map (fun o -> if o then "▾" else "▸")
                }
                span { Class "lp-sublabel"; "Inspect" }
            }
            div {
                Class "inspect-dock-body"
                Primitives.showWhen openHere
                content
            }
        }

    // ── The rooted registration tree: the matrix's co-equal PEER navigator —
    // root at top, edges = the established registrations, depth = hops from
    // the root (the provenance-path length), tidy-tree x layout (leaves in
    // mesh order, parents centred over their children). Disconnected meshes
    // float as a dashed island row below. Deliberately ROUGH: a static SVG
    // re-render per state change — no animation, no pan/zoom. Clicks go
    // through the hidden bridge input (observedRender rebuilds the SVG
    // wholesale, so handlers cannot live on Aardvark-managed nodes).
    let private treePanel (env : Env<Message>) (model : AdaptiveModel) =
        let esc (s : string) = s.Replace("\\", "\\\\").Replace("\"", "\\\"")
        let treeData =
            AVal.custom (fun t ->
                let g = model.RegGraph.GetValue t
                let names = IndexList.toList (model.MeshNames.Content.GetValue t)
                let order = model.MeshOrder.Content.GetValue t
                let selPair = (model.Sel.GetValue t).Pair
                // The matrix cell hover mirrors into the tree: a registered
                // pair lights its edge, an unregistered one draws a dashed
                // preview of the edge a solve would insert.
                let hov = model.MatrixHoverPair.GetValue t
                let hovEdge =
                    hov |> Option.bind (fun (x, y) ->
                        g.Edges |> Map.toList
                        |> List.tryPick (fun (c, e) ->
                            if PairCell.key c e.Parent = PairCell.key x y then Some c else None))
                let hovPreview =
                    match hov, hovEdge with
                    | Some (x, y), None -> sprintf ",\"hovP\":[\"%s\",\"%s\"]" (esc x) (esc y)
                    | _ -> ""
                let nodes =
                    names |> List.map (fun n ->
                        let i = HashMap.tryFind n order |> Option.defaultValue 0
                        let d = match MatrixNav.hopDepth g n with Some d -> d | None -> -1
                        sprintf "{\"id\":\"%s\",\"num\":%d,\"c\":\"%s\",\"d\":%d,\"root\":%b}"
                            (esc n) (i + 1)
                            (c4bToHex (meshColorRoot (g.Root = Some n) i)) d (g.Root = Some n))
                let edges =
                    g.Edges |> Map.toList |> List.map (fun (c, e) ->
                        sprintf "{\"c\":\"%s\",\"p\":\"%s\",\"sel\":%b,\"hov\":%b}"
                            (esc c) (esc e.Parent) (selPair = Some (PairCell.key c e.Parent))
                            (hovEdge = Some c))
                sprintf "{\"nodes\":[%s],\"edges\":[%s]%s}"
                    (String.concat "," nodes) (String.concat "," edges) hovPreview)
        // The finished ribbon: purely DERIVED from the spanned state, so
        // disconnecting clears it by itself. The one entry point into the
        // global instruments lives here (the old centre notice is gone).
        let spannedA =
            (model.MeshNames.Content, model.RegGraph) ||> AVal.map2 (fun ns g ->
                Workflow.spanned (IndexList.toList ns) g)
        div {
            Class "tree-nav"
            div {
                Class "tree-ribbon"
                showWhen spannedA
                span { Class "tree-ribbon-text"; "✓ all meshes connected" }
                button {
                    Class "rail-btn tree-ribbon-btn"
                    Attribute("title", "Open the global inspection instruments: the graph error map, the pooled histogram and the pose peek at the Matrix level")
                    Dom.OnClick(fun _ -> env.Emit [LogReach("tree", "assess-global", ""); AssessGlobalQuality])
                    "Assess global quality →"
                }
            }
            div {
                Class "tree-canvas"
                // Stale-hover guard: an SVG rebuild under the cursor swallows
                // the JS mouseleave — the container's leave always fires.
                Dom.OnMouseLeave(fun _ -> env.Emit [SetTileIsolateHover None; SetMatrixHoverPair None])
                treeData |> AVal.map (fun j -> Some (Attribute("data-tree", j)))
                observedRender "data-tree" "{}" [
                    "  if(!d.nodes || !d.nodes.length){ return; }"
                    "  var NR=11, rowH=46, colW=40, padX=16, padY=16;"
                    "  var byId={}; d.nodes.forEach(function(n){ byId[n.id]=n; });"
                    "  var kids={}; d.edges.forEach(function(e){ (kids[e.p]=kids[e.p]||[]).push(e.c); });"
                    "  Object.keys(kids).forEach(function(k){ kids[k].sort(function(a,b){ return byId[a].num-byId[b].num; }); });"
                    // Tidy layout: leaves take slots in order, a parent centres
                    // over its first and last child.
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
                    "  var islY=padY+NR+(maxD+1)*rowH+18;"
                    "  var H=(isl.length? islY : padY+NR+maxD*rowH)+NR+padY;"
                    "  var svg=document.createElementNS(ns,'svg');"
                    "  svg.setAttribute('width',W); svg.setAttribute('height',H);"
                    "  svg.setAttribute('viewBox','0 0 '+W+' '+H); svg.style.display='block';"
                    "  function px(id){ var n=byId[id]; return padX+NR+(n.d<0 ? n._ix : (X[id]||0))*colW; }"
                    "  function py(id){ var n=byId[id]; return n.d<0 ? islY : padY+NR+n.d*rowH; }"
                    "  function pk(kind,id){ var b=el.parentElement.querySelector('.tree-bridge'); if(!b) return;"
                    "    el._seq=(el._seq||0)+1; b.value=kind+'|'+id+'|'+el._seq;"
                    "    b.dispatchEvent(new Event('input',{bubbles:true})); }"
                    "  d.edges.forEach(function(e){"
                    "    var x1=px(e.p), y1=py(e.p)+NR, x2=px(e.c), y2=py(e.c)-NR;"
                    "    var ln=document.createElementNS(ns,'line');"
                    "    ln.setAttribute('x1',x1); ln.setAttribute('y1',y1); ln.setAttribute('x2',x2); ln.setAttribute('y2',y2);"
                    "    ln.setAttribute('stroke', (e.sel||e.hov)?'#1a56db':'#64748b'); ln.setAttribute('stroke-width', (e.sel||e.hov)?3:1.5);"
                    "    svg.appendChild(ln);"
                    "    var ht=document.createElementNS(ns,'line');"
                    "    ht.setAttribute('x1',x1); ht.setAttribute('y1',y1); ht.setAttribute('x2',x2); ht.setAttribute('y2',y2);"
                    "    ht.setAttribute('stroke','rgba(0,0,0,0)'); ht.setAttribute('stroke-width',12); ht.style.cursor='pointer';"
                    "    var tt=document.createElementNS(ns,'title');"
                    "    tt.textContent='mesh '+byId[e.c].num+' \\u2192 mesh '+byId[e.p].num+' \\u2014 click to open this pair';"
                    "    ht.appendChild(tt);"
                    "    ht.addEventListener('click',function(){ pk('e',e.c); });"
                    "    ht.addEventListener('mouseenter',function(){ ln.setAttribute('stroke','#1a56db'); ln.setAttribute('stroke-width',3); pk('he',e.c); });"
                    "    ht.addEventListener('mouseleave',function(){ ln.setAttribute('stroke', (e.sel||e.hov)?'#1a56db':'#64748b'); ln.setAttribute('stroke-width', (e.sel||e.hov)?3:1.5); pk('hx',''); });"
                    "    svg.appendChild(ht);"
                    "  });"
                    // Hovered UNREGISTERED pair: a dashed preview of the edge a
                    // solve would insert (drawn under the nodes, inert).
                    "  if(d.hovP && byId[d.hovP[0]] && byId[d.hovP[1]]){"
                    "    var pv=document.createElementNS(ns,'line');"
                    "    pv.setAttribute('x1',px(d.hovP[0])); pv.setAttribute('y1',py(d.hovP[0]));"
                    "    pv.setAttribute('x2',px(d.hovP[1])); pv.setAttribute('y2',py(d.hovP[1]));"
                    "    pv.setAttribute('stroke','#1a56db'); pv.setAttribute('stroke-width',2);"
                    "    pv.setAttribute('stroke-dasharray','5 4'); pv.setAttribute('opacity','0.75');"
                    "    pv.setAttribute('pointer-events','none');"
                    "    svg.appendChild(pv);"
                    "  }"
                    "  if(isl.length){"
                    "    var sep=document.createElementNS(ns,'line');"
                    "    sep.setAttribute('x1',4); sep.setAttribute('y1',islY-NR-12); sep.setAttribute('x2',W-4); sep.setAttribute('y2',islY-NR-12);"
                    "    sep.setAttribute('stroke','#cbd5e1'); sep.setAttribute('stroke-dasharray','4 3');"
                    "    svg.appendChild(sep);"
                    "    var lb=document.createElementNS(ns,'text');"
                    "    lb.setAttribute('x',4); lb.setAttribute('y',islY-NR-16); lb.setAttribute('fill','#94a3b8'); lb.setAttribute('font-size','9');"
                    "    lb.textContent='not connected yet';"
                    "    svg.appendChild(lb);"
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
                    "    tx.setAttribute('fill', n.d<0?'#64748b':'#0f172a'); tx.setAttribute('font-family','Inter,sans-serif');"
                    "    tx.textContent=n.num;"
                    "    svg.appendChild(tx);"
                    "    var ht=document.createElementNS(ns,'circle');"
                    "    ht.setAttribute('cx',cx); ht.setAttribute('cy',cy); ht.setAttribute('r',NR+6); ht.setAttribute('fill','rgba(0,0,0,0)');"
                    "    var tt=document.createElementNS(ns,'title');"
                    "    tt.textContent='mesh '+n.num+(n.root?' \\u2014 the reference root':'')+(n.d<0?' \\u2014 not connected yet':'')+' \\u2014 hover previews it in 3D';"
                    "    ht.appendChild(tt);"
                    "    ht.addEventListener('mouseenter',function(){"
                    "      var hr=document.createElementNS(ns,'circle');"
                    "      hr.setAttribute('cx',cx); hr.setAttribute('cy',cy); hr.setAttribute('r',NR+5);"
                    "      hr.setAttribute('fill','none'); hr.setAttribute('stroke','#1a56db'); hr.setAttribute('stroke-width',2.5);"
                    "      hr.setAttribute('pointer-events','none');"
                    "      svg.insertBefore(hr,ht); ht._hr=hr; pk('hn',n.id); });"
                    "    ht.addEventListener('mouseleave',function(){ if(ht._hr){ ht._hr.remove(); ht._hr=null; } pk('hx',''); });"
                    "    svg.appendChild(ht);"
                    "  });"
                    "  el.appendChild(svg);"
                ]
            }
            // The JS→Elm bridge: "e|child|seq" (click) opens the edge's pair
            // through the existing cell-selection/descend path; "hn|mesh|seq" /
            // "he|child|seq" / "hx||seq" are the HOVER previews — a node hover
            // rides the tile-hover isolation preview, an edge hover the matrix
            // cell-hover overlap preview, so the tree previews exactly what the
            // tiles and the matrix already preview.
            input {
                Class "tree-bridge"
                Dom.OnInput(fun e ->
                    match (e.Value : string).Split('|') with
                    | [| "e"; child; _ |] ->
                        (match Map.tryFind child (AVal.force model.RegGraph).Edges with
                         | Some edge ->
                            env.Emit [LogReach("tree", "open-pair", child + " | " + edge.Parent)
                                      SelectPair(child, edge.Parent)]
                         | None -> ())
                    | [| "hn"; mesh; _ |] ->
                        env.Emit [SetTileIsolateHover (Some mesh)]
                    | [| "he"; child; _ |] ->
                        (match Map.tryFind child (AVal.force model.RegGraph).Edges with
                         | Some edge -> env.Emit [SetMatrixHoverPair (Some (child, edge.Parent))]
                         | None -> ())
                    | [| "hx"; _; _ |] ->
                        env.Emit [SetTileIsolateHover None; SetMatrixHoverPair None]
                    | _ -> ())
            }
        }

    let rail (env : Env<Message>) (model : AdaptiveModel) =

        // ── Mesh × mesh navigator: rows/cols = meshes in sensor (acquisition)
        // order, UPPER TRIANGLE only (registration is symmetric — no lower
        // half; the diagonal is cosmetic placeholders, root designation lives
        // in the top-bar mesh menu). Cell (A,B) IS the pair's registration
        // edge. Emphasis ramp: impossible fades into the background (a hole) <
        // possible = an outlined empty vessel < registered = filled, fill
        // strength = the edge's ONE quality scalar (achromatic ink — the colour
        // families stay free for gradients and mesh identity).
        let orderedNames = model.MeshNames.Content |> AVal.map IndexList.toList
        let pairCellView (a : string) (b : string) =
            let st =
                (model.PairOverlaps, model.RegGraph) ||> AVal.map2 (fun po g -> PairCell.state po g a b)
            let isSel = model.Sel |> AVal.map (fun s -> s.Pair = Some (PairCell.key a b))
            // Lights from the cell's own hover AND from a tree-edge hover —
            // both feed the one MatrixHoverPair transient (key order varies).
            let isHov =
                model.MatrixHoverPair |> AVal.map (function
                    | Some (x, y) -> PairCell.key x y = PairCell.key a b
                    | None -> false)
            // Tree-redundant: the tree already connects both meshes with no
            // direct edge — registering here can only add a loop, so the cell
            // recedes to a faint borderless hint and the tree-completing
            // cells stand out.
            let isRedundant =
                (st, model.RegGraph) ||> AVal.map2 (fun s g ->
                    match s with
                    | PairPossible ->
                        MatrixNav.hopDepth g a |> Option.isSome
                        && MatrixNav.hopDepth g b |> Option.isSome
                    | _ -> false)
            let title =
                (st, model.MeshOrder.Content, isRedundant) |||> AVal.map3 (fun s order red ->
                    let num m = (HashMap.tryFind m order |> Option.defaultValue 0) + 1
                    let pair = sprintf "%d × %d" (num a) (num b)
                    match s with
                    | PairImpossible -> sprintf "%s — insufficient overlap" pair
                    | PairPossible when red -> sprintf "%s — already connected through the tree (a direct link would only add a loop)" pair
                    | PairPossible -> sprintf "%s — can be registered (not yet)" pair
                    | PairRegistered q -> sprintf "%s — registered, quality %.2f" pair q)
            div {
                Class "pmx-cell"
                classWhen "pmx-sel" isSel
                classWhen "pmx-cellhover" isHov
                classWhen "pmx-redundant" isRedundant
                st |> AVal.map (function
                    | PairImpossible -> Some (Class "pmx-imp")
                    | PairPossible -> Some (Class "pmx-pos")
                    | PairRegistered _ -> Some (Class "pmx-reg"))
                st |> AVal.map (function
                    | PairRegistered q ->
                        Some (Style [Css.Background (sprintf "rgba(15, 23, 42, %.3f)" (0.30 + 0.65 * clamp 0.0 1.0 q))])
                    | _ -> None)
                title |> AVal.map (fun tt -> Some (Attribute("title", tt)))
                // Hover = the 3D overlap preview (every real cell — hovering an
                // impossible one shows exactly why: almost nothing lights up).
                Dom.OnMouseEnter(fun _ -> env.Emit [SetMatrixHoverPair (Some (a, b))])
                Dom.OnMouseLeave(fun _ -> env.Emit [SetMatrixHoverPair None])
                // A Possible/Registered cell IS the pair — clicking selects it
                // and enters its Pair level (impossible cells are inert holes).
                Dom.OnClick(fun _ ->
                    match AVal.force st with
                    | PairPossible | PairRegistered _ ->
                        env.Emit [LogReach("matrix", "open-pair", a + " | " + b); SelectPair(a, b)]
                    | PairImpossible -> ())
            }
        let numSwatch (name : string) =
            let idxVal = model.MeshOrder |> AMap.tryFind name |> AVal.map (Option.defaultValue 0)
            let rootVal = model.RegGraph |> AVal.map (fun g -> g.Root = Some name)
            AList.ofList [
                span { Class "pmx-sw"; (idxVal, rootVal) ||> AVal.map2 (fun i r -> Some (Style [Css.Background (rgb (meshColorRoot r i))])) }
                span { Class "pmx-num"; idxVal |> AVal.map (fun i -> string (i + 1)) }
            ]
        // Gold marks the reference root in the matrix heads — the tree reads
        // from the matrix without a trip back to Setup.
        let headRoot (name : string) =
            model.RegGraph |> AVal.map (fun g -> g.Root = Some name)
        // The hovered mesh subject (a tree node or a strip tile) marks its
        // matrix row + column heads — the same transient the 3D isolation
        // preview rides.
        let headHover (name : string) =
            model.TileIsolateHover |> AVal.map ((=) (Some name))
        let headTitle (name : string) =
            (headRoot name, model.MeshOrder |> AMap.tryFind name) ||> AVal.map2 (fun r o ->
                Some (Attribute("title",
                        sprintf "mesh %d%s" ((Option.defaultValue 0 o) + 1)
                            (if r then " — the reference root ★" else ""))))
        // Rebuilt wholesale on an order change — a ≤ palette-sized grid that
        // changes rarely (the sanctioned simple AList form).
        let pairMatrixView () =
            // Cosmetic diagonal: a mesh has no pair with itself — the cell is an
            // inert disabled placeholder that only anchors the matrix shape.
            let diagCell () =
                div { Class "pmx-cell pmx-diag"; Attribute("title", "a mesh has no pair with itself") }
            let rowsA =
                orderedNames |> AVal.map (fun ns ->
                    let arr = List.toArray ns
                    let n = arr.Length
                    if n < 2 then IndexList.empty
                    else
                        IndexList.ofList [
                            yield div {
                                Class "pmx-row"
                                div { Class "pmx-rowhead pmx-corner" }
                                AList.ofList [
                                    for j in 0 .. n - 1 ->
                                        div {
                                            Class "pmx-colhead"
                                            classWhen "pmx-head-root" (headRoot arr.[j])
                                            classWhen "pmx-head-hover" (headHover arr.[j])
                                            headTitle arr.[j]
                                            numSwatch arr.[j]
                                        } ]
                            }
                            for i in 0 .. n - 1 do
                                yield div {
                                    Class "pmx-row"
                                    div {
                                        Class "pmx-rowhead"
                                        classWhen "pmx-head-root" (headRoot arr.[i])
                                        classWhen "pmx-head-hover" (headHover arr.[i])
                                        headTitle arr.[i]
                                        numSwatch arr.[i]
                                    }
                                    AList.ofList [
                                        for j in 0 .. n - 1 ->
                                            if j < i then div { Class "pmx-cell pmx-void" }
                                            elif j = i then diagCell ()
                                            else pairCellView arr.[i] arr.[j]
                                    ]
                                }
                        ])
            div {
                Class "pmx"
                rowsA |> AList.ofAVal
            }

        // ── The home stage: TWO co-equal navigators over the one registration
        // state — the pair matrix and the rooted tree, side by side with
        // visual parity (equal flex, identical chrome; neither dominant, so
        // reaching behaviour is not a layout artifact).
        let homeStage () =
            div {
                Class "home-stage"
                div {
                    Class "home-nav"
                    div { Class "home-nav-head"; span { Class "lp-sublabel"; "Pair matrix" } }
                    div { Class "home-nav-body"; pairMatrixView () }
                }
                div {
                    Class "home-nav"
                    div { Class "home-nav-head"; span { Class "lp-sublabel"; "Registration tree" } }
                    div { Class "home-nav-body"; treePanel env model }
                }
            }

        // ── The focus rail: three stops, strictly narrowing scope. Enablement
        // is selection-derived; the reducer re-guards every jump.
        let railLevels =
            let placing =
                model.ScanPins.Placement |> AVal.map (function PlacementActive _ -> true | PlacementIdle -> false)
            let stop (label : string) (title : string) (level : FocusLevel) =
                let active = model.Focus |> AVal.map ((=) level)
                let enabled = (model.Sel, placing) ||> AVal.map2 (fun sel pl -> FocusLevel.enabled sel pl level)
                button {
                    Class "rail-stop"
                    classWhen "rail-btn-active" active
                    enabled |> AVal.map (fun e -> if e then None else Some (Attribute("disabled", "disabled")))
                    Attribute("title", title)
                    Dom.OnClick(fun _ -> env.Emit [LogReach("rail", "jump", label); SetFocus level])
                    label
                }
            div {
                Class "rail-levels"
                stop "Matrix" "Pick the next pair, read connectivity" FocusMatrix
                stop "Pair"   "Work one pair: pins, solve, inspection (choose a pair in the matrix)" FocusPair
                stop "Pin"    "Configure one scanpin (choose or place a pin in the pair)" FocusPin
            }

        // ── Cell-workspace: scoped to ONE pair. The A↔B header pins the pair
        // identity for the whole stay; tools below operate on this pair as a
        // toolkit, never as global modes (the remaining tools land P6–P8).
        let cellWorkspace (a : string) (b : string) =
            let meshChip (name : string) =
                let idxVal = model.MeshOrder |> AMap.tryFind name |> AVal.map (Option.defaultValue 0)
                let rootVal = model.RegGraph |> AVal.map (fun g -> g.Root = Some name)
                div {
                    Class "cw-chip"
                    idxVal |> AVal.map (fun i -> Some (Attribute("title", sprintf "mesh %d" (i + 1))))
                    span { Class "pmx-sw"; (idxVal, rootVal) ||> AVal.map2 (fun i r -> Some (Style [Css.Background (rgb (meshColorRoot r i))])) }
                    span { Class "pmx-num"; idxVal |> AVal.map (fun i -> string (i + 1)) }
                }
            let pairState =
                (model.PairOverlaps, model.RegGraph) ||> AVal.map2 (fun po g ->
                    match PairCell.state po g a b with
                    | PairRegistered q -> sprintf "registered · quality %.2f" q
                    | PairPossible -> "not registered yet"
                    | PairImpossible -> "insufficient overlap")
            let pairKey = PairCell.key a b
            // The pair's pins, canonical order — rebuilt on pin add/delete only
            // (identity projection; the sanctioned simple AList form at this size).
            let pairPins =
                model.ScanPins.Pins |> AMap.toAVal |> AVal.map (fun pins ->
                    pins |> HashMap.toList |> List.map snd
                    |> List.filter (fun p -> p.Pair = pairKey)
                    |> List.sortBy (fun p -> p.CreatedAt, p.ShortName))
            let pinCount = pairPins |> AVal.map List.length

            // ── Committed-pin rows: the row BODY is inert — hovering
            // highlights the pin in 3D (the loud highlight + the tile
            // preview-framing ride TilePinHover) and its histogram portion;
            // the three buttons are the only actions: fly-to, delete, open at
            // the Pin level. Radius editing lives in the Pin panel; point
            // re-picks at the Pin level via the armed pick.
            let pinRow (p : ScanPin) =
                let isSel = model.Sel |> AVal.map (fun s -> s.Pin = Some p.Id)
                div {
                    Class "cw-pin-row"
                    classWhen "cw-pin-sel" isSel
                    Dom.OnMouseEnter(fun _ -> env.Emit [SetTilePinHover (Some p.Id)])
                    Dom.OnMouseLeave(fun _ -> env.Emit [SetTilePinHover None])
                    span { Class "cw-pin-name"; p.ShortName }
                    button {
                        Class "mb cw-pin-btn"
                        Attribute("title", "Fly the camera to this pin")
                        Dom.OnClick(fun _ -> env.Emit [ZoomToPin p.Id])
                        "⌖"
                    }
                    button {
                        Class "mb cw-del"
                        Attribute("title", "Delete pin")
                        Dom.OnClick(fun _ ->
                            let ok = try JSRuntime.Instance.Invoke<bool>("confirm", sprintf "Delete pin %s? This cannot be undone." p.ShortName) with _ -> false
                            if ok then env.Emit [ScanPinMsg (DeletePin p.Id)])
                        "✕"
                    }
                    button {
                        Class "mb cw-pin-btn cw-goto"
                        Attribute("title", "Open this pin at the Pin level")
                        Dom.OnClick(fun _ ->
                            env.Emit [LogReach("pair", "open-pin", p.ShortName); SelectPin p.Id; SetFocus FocusPin])
                        "▸"
                    }
                }
            let pinList =
                let rows =
                    pairPins
                    |> AVal.map (fun ps -> IndexList.ofList (ps |> List.map pinRow))
                    |> AList.ofAVal
                div {
                    Class "cw-pins"
                    rows
                }

            // The panel READS as its sequence, top to bottom: [+ New pin] →
            // the pin list → the "N remaining" workflow line → the linked
            // Solve→Finish two-step (the lit step is the next one to take).
            let registered =
                model.RegGraph |> AVal.map (fun g -> (RegGraph.pairEdge a b g).IsSome)
            let remainingLine =
                (pinCount, registered) ||> AVal.map2 (fun n r ->
                    if n < 3 then
                        sprintf "%d more pin%s needed to solve" (3 - n) (if 3 - n = 1 then "" else "s")
                    elif r then sprintf "%d pins placed — solved" n
                    else sprintf "%d pins placed — ready to solve" n)
            let solveLit  = (pinCount, registered) ||> AVal.map2 (fun n r -> n >= 3 && not r)
            div {
                Class "cw"
                div {
                    Class "cw-head"
                    meshChip a
                    span { Class "cw-link"; "↔" }
                    meshChip b
                }
                div { Class "cw-state"; pairState }
                button {
                    Class "rail-btn cw-newpin"
                    Attribute("title", "Place a pin on this pair: a guided three-step placement — the centre, then one correspondence point per mesh; each landed click arms the next step and the pin exists once all three are placed. Only the highlighted overlap region is a valid pin location")
                    // Hover lights the pair's overlap-region gate (only the
                    // overlap is a valid pin location); the click's focus
                    // jump wipes the hover, and the pre-armed centre pick
                    // carries the gate seamlessly.
                    Dom.OnMouseEnter(fun _ -> env.Emit [SetNewPinHover true])
                    Dom.OnMouseLeave(fun _ -> env.Emit [SetNewPinHover false])
                    Dom.OnClick(fun _ -> env.Emit [ScanPinMsg (BeginPinTransaction pairKey)])
                    "＋ New pin"
                }
                pinList
                div { Class "cw-remaining"; remainingLine }
                div {
                    Class "cw-steps"
                    button {
                        Class "rail-btn cw-solve"
                        classWhen "cw-step-lit" solveLit
                        pinCount |> AVal.map (fun n -> if n >= 3 then None else Some (Attribute("disabled", "disabled")))
                        Attribute("title", "Solve this pair's edge from its pins (needs ≥3)")
                        Dom.OnClick(fun _ ->
                            if AVal.force pinCount >= 3 then env.Emit [SolvePair(a, b)])
                        "⌖ Solve"
                    }
                    span { Class "cw-step-arrow"; "→" }
                    button {
                        Class "rail-btn cw-finish-btn"
                        classWhen "cw-step-lit" registered
                        registered |> AVal.map (fun r ->
                            if r then None else Some (Attribute("disabled", "disabled")))
                        Attribute("title", "Done with this pair — back to the matrix (enabled once the pair has been solved)")
                        Dom.OnClick(fun _ ->
                            env.Emit [LogReach("pair", "finish", a + " | " + b); SetFocus FocusMatrix])
                        "✓ Finish pair"
                    }
                }
            }

        // ── Pin level: configure ONE scanpin through the Edit panel — every
        // verb is arm-driven (placement and committed re-picks are the same
        // arms) and an armed pick lands in ANY view. View steering lives in
        // the tiles (a tile click isolates), not in panel buttons.
        let pinLevelView (a : string) (b : string) =
            let placement = model.ScanPins.Placement
            let placing = placement |> AVal.map (function PlacementActive _ -> true | PlacementIdle -> false)
            let selPin =
                (model.Sel, model.ScanPins.Pins |> AMap.toAVal) ||> AVal.map2 (fun s pins ->
                    s.Pin |> Option.bind (fun id -> HashMap.tryFind id pins))
            let chip (name : string) =
                let idxVal = model.MeshOrder |> AMap.tryFind name |> AVal.map (Option.defaultValue 0)
                let rootVal = model.RegGraph |> AVal.map (fun g -> g.Root = Some name)
                AList.ofList [
                    span { Class "pmx-sw"; (idxVal, rootVal) ||> AVal.map2 (fun i r -> Some (Style [Css.Background (rgb (meshColorRoot r i))])) }
                    span { Class "pmx-num"; idxVal |> AVal.map (fun i -> string (i + 1)) }
                ]
            // Arm buttons: the ONE way picking engages. While armed the left
            // button picks (never orbits) in every view; a landed pick, Esc or
            // a re-click disarms. Arming a correspondence pick isolates its
            // mesh (hover previews). The SAME arms serve placement (the draft
            // part lands) and a committed pin (the pick replaces/re-anchors).
            let armBtn (target : ArmTarget) (hover : PinHover) (title : string)
                       (withChip : string option) (label : string) =
                button {
                    Class "rail-btn pin-arm-btn"
                    // arm-lit = the armed quasi-mode's cancel affordance,
                    // raised above the scrim.
                    classWhen "rail-btn-active arm-lit" (model.ArmedPick |> AVal.map ((=) (Some target)))
                    Attribute("title", title)
                    Dom.OnMouseEnter(fun _ -> env.Emit [SetPinFocusHover (Some hover)])
                    Dom.OnMouseLeave(fun _ -> env.Emit [SetPinFocusHover None])
                    Dom.OnClick(fun _ -> env.Emit [ToggleArmPick target])
                    let chipList = match withChip with Some m -> chip m | None -> AList.empty
                    chipList
                    label
                }
            let hasPin = selPin |> AVal.map Option.isSome
            // ── The Edit panel: one correspondence pick per mesh, then the
            // pin's centre + radius. Radius stays hidden until its edit is
            // clicked.
            let controlPanel =
                div {
                    Class "pin-panel"
                    div { Class "pin-panel-head"; "Edit" }
                    armBtn (ArmPoint a) (HoverSide a)
                        "Arm the correspondence pick on this mesh — it renders alone while armed; click any view (a re-pick replaces the point and unregisters the pair)"
                        (Some a) "✚ point"
                    armBtn (ArmPoint b) (HoverSide b)
                        "Arm the correspondence pick on this mesh — it renders alone while armed; click any view (a re-pick replaces the point and unregisters the pair)"
                        (Some b) "✚ point"
                    armBtn ArmCentre HoverBoth
                        "Arm the centre pick: click any view — during placement it drops the area marker, on a committed pin it moves the centre (the hit mesh anchors the pin; unregisters the pair)"
                        None "◯ Centre"
                    button {
                        Class "rail-btn pin-arm-btn"
                        classWhen "rail-btn-active" model.PinRadiusEditOpen
                        (hasPin, placing) ||> AVal.map2 (fun p pl ->
                            if p || pl then None else Some (Attribute("disabled", "disabled")))
                        Attribute("title", "Edit the pin radius (reveals the slider; the radius scopes error analysis)")
                        Dom.OnClick(fun _ -> env.Emit [ToggleRadiusEdit])
                        "⌀ Radius"
                    }
                }
            // The SAME radius edit serves the draft and a committed pin.
            let radiusRow =
                div {
                    Class "cw-tools"
                    showWhen ((model.PinRadiusEditOpen, (hasPin, placing) ||> AVal.map2 (||))
                              ||> AVal.map2 (&&))
                    inlineLogSlider "r" 0.01 100.0 (sprintf "%.2f m")
                        ((placement, selPin) ||> AVal.map2 (fun pl p ->
                            match pl with
                            | PlacementActive d -> d.Radius
                            | PlacementIdle -> match p with Some p -> p.InnerRadius | None -> 0.5))
                        (fun v ->
                            match AVal.force placement with
                            | PlacementActive _ -> env.Emit [ScanPinMsg (SetDraftRadius v)]
                            | PlacementIdle ->
                                match (AVal.force model.Sel).Pin with
                                | Some id -> env.Emit [ScanPinMsg (SetInnerRadius(id, v))]
                                | None -> ())
                }
            // Placement progress cue (draft only — a committed pin needs none).
            let draftCue =
                let cue =
                    placement |> AVal.map (function
                        | PlacementActive d ->
                            sprintf "centre %s · %d of 2 points — the pin exists once all three are placed"
                                (if d.Area.IsSome then "✓" else "·") (PinDraft.pointCount d)
                        | PlacementIdle -> "")
                div {
                    Class "cw-draft"
                    showWhen placing
                    span { Class "cw-cue"; cue }
                }
            div {
                Class "cw pin-level"
                div {
                    Class "cw-head"
                    span {
                        Class "cw-pin-name"
                        (placing, selPin) ||> AVal.map2 (fun pl p ->
                            if pl then "New pin"
                            else match p with Some p -> sprintf "Pin %s" p.ShortName | None -> "—")
                    }
                }
                div {
                    Class "cw-state"
                    (placing, selPin) ||> AVal.map2 (fun pl p ->
                        if pl then "guided placement: each landed click arms the next step"
                        else
                            match p with
                            | Some _ -> "arm an edit to change geometry · tile clicks steer the view"
                            | None -> "")
                }
                controlPanel
                radiusRow
                draftCue
                // The workflow exits: Cancel aborts an unfinished placement
                // (the navigation guard still asks once a centre exists),
                // Finish leaves a complete pin — both land back at Pair
                // through the ONE navigation path.
                let bothPoints =
                    placement |> AVal.map (function
                        | PlacementActive d -> d.PointA.IsSome && d.PointB.IsSome
                        | PlacementIdle -> false)
                let cancelDisabled =
                    (placing, bothPoints) ||> AVal.map2 (fun pl bp ->
                        if pl && not bp then None else Some (Attribute("disabled", "disabled")))
                div {
                    Class "cw-finish"
                    button {
                        Class "rail-btn pin-cancel-btn"
                        cancelDisabled
                        Attribute("title", "Abort this pin placement and return to the pair (a placed centre asks for one confirmation)")
                        Dom.OnClick(fun _ -> env.Emit [LogReach("pin", "cancel", ""); SetFocus FocusPair])
                        "✕ Cancel"
                    }
                    button {
                        Class "cw-finish-btn"
                        placing |> AVal.map (fun pl ->
                            if pl then Some (Attribute("disabled", "disabled")) else None)
                        Attribute("title", "Done with this pin — back to the pair (enabled once the pin is complete)")
                        Dom.OnClick(fun _ -> env.Emit [LogReach("pin", "finish", ""); SetFocus FocusPair])
                        "✓ Finish pin"
                    }
                }
            }

        div {
            Class "workflow-rail"
            railLevels
            div {
                Class "rail-body"
                // The level switch — rebuilt on a focus/pair change (rare; the
                // pair workspace is freshly keyed to its pair).
                let selPairA = model.Sel |> AVal.map (fun s -> s.Pair)
                let levelNode =
                    (model.Focus, selPairA) ||> AVal.map2 (fun focus selPair ->
                        let node =
                            match focus, selPair with
                            | FocusMatrix, _ -> homeStage ()
                            | FocusPair, Some (a, b) -> cellWorkspace a b
                            | FocusPin, Some (a, b) -> pinLevelView a b
                            // Unreachable — the reducer keeps Focus enabled.
                            | (FocusPair | FocusPin), None -> homeStage ()
                        IndexList.ofList [ node ])
                    |> AList.ofAVal
                levelNode
            }
        }
