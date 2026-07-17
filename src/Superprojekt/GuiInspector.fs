namespace Superprojekt

open Aardvark.Base
open FSharp.Data.Adaptive
open Aardvark.Dom

// Bottom dock: full-width 2D dock (SVG/HTML, not a WebGL control), always
// mounted. Mode-contextual content (mesh roster / correspondence manager / pin
// distribution), cross-faded; the container never moves.
module GuiInspector =

    open Primitives

    // ONE standard labelled chart (v12 §3), instantiated twice (mesh chart +
    // pin chart): title, x axis (mm, nice-step ticks + label), y axis (counts),
    // inset legend, and a STACKED HISTOGRAM of the payload's series over the
    // shared signed-distance range — 48 crisp bins. Once a solve exists the
    // chart carries BOTH poses: the emphasized pose (committed view, flipped by
    // the spring-loaded Peek — visual only) is the filled stack, the other pose
    // a near-black step OUTLINE of the total (shape only); a pose key names
    // them. An empty payload renders the FULL furniture with a centred
    // placeholder (d.ph) — never a blank panel.
    // Brushing: an X-RANGE drag over the CONCEPTUAL samples (el._dots, from the
    // series' canonical g/s arrays of the COMMITTED pose — no dots painted);
    // selected gids go to the shared hidden input (the JS→Elm bridge) → the 3D
    // sample dots. A plain click clears. data-brushed echoes the model back (a
    // model-side clear drops the local band); with no local drag the band is
    // reconstructed from the echoed gids. Self-contained OnBoot (observes
    // data-dist + data-brushed + its own size); the bridge is resolved lazily
    // EACH emit (a boot capture would freeze null).
    let private chartJs = [
        "(function(){"
        "var el=__THIS__;"
        "function findBridge(){ return (el.parentElement&&el.parentElement.querySelector('.ins-brush-bridge'))||document.querySelector('.ins-brush-bridge'); }"
        "el._dots=[]; var dragging=false, range=null, anchorV=0, lastEmit=0;"
        "function emit(){ var b=findBridge(); if(!b) return; var ids=[];"
        "  if(range){ for(var i=0;i<el._dots.length;i++){ var dt=el._dots[i]; if(dt.v>=range[0]&&dt.v<=range[1]) ids.push(dt.gid); } }"
        "  b.value=ids.join(','); b.dispatchEvent(new Event('input',{bubbles:true})); }"
        "function niceStep(raw){ var mag=Math.pow(10,Math.floor(Math.log(raw)/Math.LN10)); var n=raw/mag; return (n<1.5?1:n<3.5?2:n<7.5?5:10)*mag; }"
        "function render(){"
        "  var raw=el.getAttribute('data-dist')||'{}'; var d; try{d=JSON.parse(raw);}catch(e){return;}"
        "  var braw=el.getAttribute('data-brushed')||''; var bset=new Set(braw.length?braw.split(',').map(Number):[]);"
        "  var fraw=el.getAttribute('data-focus')||''; var fset=new Set(fraw.length?fraw.split(',').map(Number):[]);"
        "  el.innerHTML=''; el._dots=[];"
        "  var W=el.clientWidth||300, H=el.clientHeight||150; var dpr=window.devicePixelRatio||1;"
        "  var cv=document.createElement('canvas'); cv.width=Math.round(W*dpr); cv.height=Math.round(H*dpr);"
        "  cv.style.width=W+'px'; cv.style.height=H+'px';"
        "  var g=cv.getContext('2d'); g.setTransform(dpr,0,0,dpr,0,0);"
        "  g.fillStyle='#ffffff'; g.fillRect(0,0,W,H);"
        // Furniture — ALWAYS drawn: title, axes frame, x/y ticks, axis label.
        "  var padL=30,padR=8,padT=20,padB=24;"
        "  var lo=(d.lo!=null?d.lo:-10), hi=(d.hi!=null?d.hi:10); var span=Math.max(1e-6,hi-lo);"
        "  function X(v){ return padL+(v-lo)/span*(W-padL-padR); }"
        "  el._XV=function(x){ return lo+(x-padL)/Math.max(1,W-padL-padR)*span; };"
        "  el._padL=padL; el._padR=padR;"
        "  var axY=H-padB, axT=padT;"
        "  g.fillStyle='#0f172a'; g.font='700 11px Inter,\\'Segoe UI\\',sans-serif'; g.textAlign='left';"
        "  g.fillText(d.title||'',4,13);"
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
        "  g.fillText(d.xlabel||'signed error vs reference (mm)',W-padR,H-4);"
        "  var S=d.series||[];"
        // Empty state: full furniture + centred placeholder, never a blank panel.
        "  if(S.length===0){ g.fillStyle='#94a3b8'; g.font='12px Inter,sans-serif'; g.textAlign='center';"
        "    g.fillText(d.ph||'\\u2014',(padL+W-padR)/2,(axT+axY)/2+4); el.appendChild(cv); return; }"
        "  var B=d.bins||48; var bw=span/B;"
        "  function poseC(sn,ps){ var a=ps==='a'?sn.ha:sn.hb; return (a&&a.length===B)?a:null; }"
        "  var anyB=false,anyA=false; S.forEach(function(sn){ if(poseC(sn,'b'))anyB=true; if(poseC(sn,'a'))anyA=true; });"
        // Emphasized pose = filled stack; the other pose (solved only) = outline.
        "  var fillP=d.reg?d.act:(anyB?'b':'a'); var outP=d.reg?(fillP==='a'?'b':'a'):null;"
        // One count→height scale over BOTH poses; y axis = counts, nice ticks.
        "  var maxC=1; ['b','a'].forEach(function(ps){ for(var b=0;b<B;b++){ var s=0; S.forEach(function(sn){ var a=poseC(sn,ps); if(a) s+=a[b]; }); if(s>maxC)maxC=s; } });"
        "  var k=(axY-axT-4)/maxC;"
        "  var cs=niceStep(Math.max(1,maxC/3)); g.font='9px SF Mono,Monaco,monospace'; g.textAlign='right';"
        "  for(var c=cs;c<=maxC;c+=cs){ var y=axY-c*k;"
        "    g.strokeStyle='#eef2f6'; g.beginPath(); g.moveTo(padL,y+0.5); g.lineTo(W-padR,y+0.5); g.stroke();"
        "    g.fillStyle='#64748b'; g.fillText(String(Math.round(c)),padL-3,y+3); }"
        "  if(d.lod>0){ g.fillStyle='rgba(148,163,184,0.18)'; g.fillRect(X(-d.lod),axT,Math.max(1,X(d.lod)-X(-d.lod)),axY-axT); }"
        // Filled stack: crisp per-bin rects, series bottom-up in payload order.
        "  var prev=new Array(B).fill(0);"
        "  S.forEach(function(sn){ var a=poseC(sn,fillP); if(!a) return;"
        "    g.globalAlpha=sn.hl?0.9:0.3; g.fillStyle=sn.color;"
        "    for(var b=0;b<B;b++){ var c=a[b]; if(c>0){ var x0=X(lo+b*bw); var wd=Math.max(1,X(lo+(b+1)*bw)-x0);"
        "      g.fillRect(x0,axY-(prev[b]+c)*k,wd,c*k); } prev[b]+=c; } });"
        // Other pose: near-black step outline of the TOTAL (shape only).
        "  if(outP){ var tot=new Array(B).fill(0); var anyO=false;"
        "    S.forEach(function(sn){ var a=poseC(sn,outP); if(!a) return; anyO=true; for(var b=0;b<B;b++) tot[b]+=a[b]; });"
        "    if(anyO){ g.globalAlpha=0.9; g.strokeStyle='#0f172a'; g.lineWidth=1;"
        "      g.beginPath(); g.moveTo(X(lo),axY);"
        "      for(var b=0;b<B;b++){ var yT=axY-tot[b]*k; g.lineTo(X(lo+b*bw),yT); g.lineTo(X(lo+(b+1)*bw),yT); }"
        "      g.lineTo(X(hi),axY); g.stroke(); } }"
        // Median ticks on the baseline.
        "  S.forEach(function(sn){ if(sn.med==null) return; g.globalAlpha=sn.hl?0.95:0.35; g.strokeStyle=sn.color; g.lineWidth=sn.hl?2:1;"
        "    g.beginPath(); g.moveTo(X(sn.med),axY); g.lineTo(X(sn.med),axY-9); g.stroke(); g.lineWidth=1; });"
        // The conceptual samples: never painted, but they ARE the brush targets.
        "  S.forEach(function(sn){ if(sn.g) for(var q=0;q<sn.g.length;q++) el._dots.push({gid:sn.g[q],v:sn.s[q]}); });"
        // Slice-mode dots of interest (v12 §6 bonus): amber markers on the
        // baseline that follow the cut plane as the slice camera moves.
        "  if(fset.size){ g.globalAlpha=1; g.fillStyle='#d97706'; g.strokeStyle='#ffffff'; g.lineWidth=1;"
        "    for(var q3=0;q3<el._dots.length;q3++){ var dd3=el._dots[q3]; if(fset.has(dd3.gid)){"
        "      g.beginPath(); g.arc(X(dd3.v),axY-5,3,0,6.2832); g.fill(); g.stroke(); } } }"
        // Inset legend (top-right): series swatch + name, capped; pose key below.
        "  g.globalAlpha=1; g.font='9px Inter,sans-serif';"
        "  var rows=[]; S.forEach(function(sn){ rows.push([sn.color,sn.name]); });"
        "  var maxE=Math.max(2,Math.floor((axY-axT-14)/11));"
        "  var over=rows.length>maxE; var shown=over?rows.slice(0,maxE-1):rows;"
        "  var lgh=(shown.length+(over?1:0))*11+(outP?12:0)+4;"
        "  var lgw=64; var lx=W-padR-lgw-2, ly=axT+2;"
        "  g.fillStyle='rgba(255,255,255,0.82)'; g.fillRect(lx-3,ly-2,lgw+5,lgh);"
        "  g.textAlign='left';"
        "  shown.forEach(function(r,i){ var y=ly+7+i*11;"
        "    g.fillStyle=r[0]; g.fillRect(lx,y-6,8,8);"
        "    g.fillStyle='#334155'; g.fillText(r[1],lx+11,y+1); });"
        "  if(over){ g.fillStyle='#64748b'; g.fillText('+'+(rows.length-shown.length)+' more',lx,ly+7+shown.length*11+1); }"
        "  if(outP){ g.fillStyle='#64748b'; g.fillText('fill '+(fillP==='b'?'Before':'After')+' \\u00b7 line '+(outP==='b'?'Before':'After'),lx,ly+7+(shown.length+(over?1:0))*11+2); }"
        // Selection band: local drag range, else reconstructed from the model echo.
        "  var dispRange=range;"
        "  if(!dispRange&&bset.size){ var mn=1/0,mx=-1/0; for(var q1=0;q1<el._dots.length;q1++){ var dd=el._dots[q1]; if(bset.has(dd.gid)){ if(dd.v<mn)mn=dd.v; if(dd.v>mx)mx=dd.v; } } if(mx>=mn) dispRange=[mn,mx]; }"
        "  if(dispRange){ var x0=X(dispRange[0]), x1=X(dispRange[1]);"
        "    g.fillStyle='rgba(8,145,178,0.10)'; g.fillRect(x0,axT,Math.max(1,x1-x0),axY-axT);"
        "    g.strokeStyle='#0891b2'; g.lineWidth=1.2; g.beginPath(); g.moveTo(x0,axT); g.lineTo(x0,axY); g.moveTo(x1,axT); g.lineTo(x1,axY); g.stroke();"
        "    var cnt=0; for(var q2=0;q2<el._dots.length;q2++){ if(el._dots[q2].v>=dispRange[0]&&el._dots[q2].v<=dispRange[1]) cnt++; }"
        "    g.font='10px SF Mono,Monaco,monospace'; g.lineWidth=3; g.strokeStyle='rgba(255,255,255,0.85)'; g.fillStyle='#0891b2';"
        "    var lab0=dispRange[0].toFixed(1), lab1=dispRange[1].toFixed(1)+' \\u00b7 n='+cnt;"
        "    g.textAlign='right'; g.strokeText(lab0,x0-3,axT+9); g.fillText(lab0,x0-3,axT+9);"
        "    g.textAlign='left';  g.strokeText(lab1,x1+3,axT+9); g.fillText(lab1,x1+3,axT+9); g.lineWidth=1; }"
        "  el.appendChild(cv);"
        "}"
        "function cursorV(e){ var r=el.getBoundingClientRect(); var W=el.clientWidth||300;"
        "  var x=Math.max(el._padL,Math.min(W-el._padR,e.clientX-r.left)); return el._XV(x); }"
        // render() after each move draws the band now; emit() (throttled) drives the
        // model + the 3D markers. A click (zero-width range) clears the brush.
        "el.addEventListener('pointerdown',function(e){ if(e.button!==0||!el._XV) return; dragging=true;"
        "  anchorV=cursorV(e); range=[anchorV,anchorV]; try{el.setPointerCapture(e.pointerId);}catch(_){} render(); e.preventDefault(); });"
        "el.addEventListener('pointermove',function(e){ if(!dragging||!el._XV) return; var v=cursorV(e);"
        "  range=[Math.min(anchorV,v),Math.max(anchorV,v)]; render();"
        "  var now=Date.now(); if(now-lastEmit>50){ lastEmit=now; emit(); } });"
        "el.addEventListener('pointerup',function(e){ if(!dragging) return; dragging=false;"
        "  try{el.releasePointerCapture(e.pointerId);}catch(_){}"
        "  if(range&&range[1]-range[0]<1e-9){ range=null; }"
        "  render(); emit(); });"
        "render();"
        // A model-side clear (empty data-brushed while not dragging) drops the local band.
        "new MutationObserver(function(muts){"
        "  if(!dragging&&range){ for(var q=0;q<muts.length;q++){ if(muts[q].attributeName==='data-brushed'&&!(el.getAttribute('data-brushed')||'').length){ range=null; break; } } }"
        "  render(); }).observe(el,{attributes:true,attributeFilter:['data-dist','data-brushed','data-focus']});"
        // The chart width follows dock drags and window resizes.
        "if(window.ResizeObserver){ new ResizeObserver(function(){ render(); }).observe(el); }"
        "})();" ]

    // 8-way unicode arrow for a heading in degrees (0 = +X/east, 90 = +Y/north).
    let private dirArrow (deg : float) =
        let d = ((deg % 360.0) + 360.0) % 360.0
        [| "→"; "↗"; "↑"; "↖"; "←"; "↙"; "↓"; "↘" |].[int (System.Math.Round(d / 45.0)) % 8]

    let dock (env : Env<Message>) (model : AdaptiveModel) =
        let selected  = model.Selection.Active |> AVal.map Selection.pin
        let pinsVal   = model.ScanPins.Pins |> AMap.toAVal
        let effPin    = (selected, pinsVal) ||> AVal.map2 (fun id pins -> id |> Option.bind (fun i -> HashMap.tryFind i pins))
        let hasPin    = effPin |> AVal.map Option.isSome
        let orderVal  = model.MeshOrder.Content
        let corrA     = effPin |> AVal.map (Option.map ScanPin.correspondence)
        let emit (m : Message) = env.Emit [m]

        // The matrix (left rail) is now the per-(pin,mesh) browser (§B); the
        // Register dock reduces to the selected pin: identity label (near-black
        // name, v12 §4) · radius · the per-mesh correspondence coordinate editor.
        let radiusVal = effPin |> AVal.map (Option.map (fun p -> p.InnerRadius) >> Option.defaultValue 0.5)
        let pinIdentChip =
            div {
                Class "ins-pinident"
                effPin |> AVal.map (function Some p -> p.ShortName | None -> "")
            }
        // The selected pin's correspondence point on a mesh, in metric world at the
        // committed pose (the reference row reads RefAnchor, movers their anchor);
        // PickCorrespondenceAt converts back with the same transform.
        let anchorWorldOf (mesh : string) =
            AVal.custom (fun t ->
                match corrA.GetValue t with
                | None -> None
                | Some c ->
                    let isRef = model.ReferenceMesh.GetValue t = Some mesh
                    Correspondence.anchorOwn isRef mesh c
                    |> Option.map (fun p -> (MeshView.displayedWorldCommittedAt model t mesh).Forward.TransformPos p))

        // One row per mesh: number · name · ★ · editable X/Y/Z of the correspondence
        // point. Edits route through PickCorrespondenceAt — same ROI clamp and
        // ref/mover handling as a surface pick. No point placed yet → disabled.
        let anchorRow (mesh : string) =
            let aw = anchorWorldOf mesh
            let idxVal = model.MeshOrder |> AMap.tryFind mesh |> AVal.map (Option.defaultValue 0)
            let isRef = model.ReferenceMesh |> AVal.map ((=) (Some mesh))
            let axisInput (axis : int) (labelText : string) =
                div {
                    Class "ins-anchor-axis"
                    span { Class "ins-anchor-axlabel"; labelText }
                    input {
                        Class "ins-anchor-num"
                        Attribute("type", "number")
                        Attribute("step", "0.01")
                        aw |> AVal.map (function
                            | Some w ->
                                let v = match axis with 0 -> w.X | 1 -> w.Y | _ -> w.Z
                                Some (Attribute("value", sprintf "%.3f" v))
                            | None -> Some (Attribute("value", "")))
                        // Read-only in the After view: correspondences are edited in
                        // the Before state only (After just shows the moved points).
                        (aw, model.RegView) ||> AVal.map2 (fun a rv ->
                            if a.IsNone || rv = RegAfter then Some (Attribute("disabled", "disabled")) else None)
                        Dom.OnChange(fun e ->
                            match parseFloat e.Value with
                            | None -> ()
                            | Some v ->
                                match AVal.force aw, AVal.force selected with
                                | Some w, Some pinId ->
                                    let w' =
                                        match axis with
                                        | 0 -> V3d(v, w.Y, w.Z)
                                        | 1 -> V3d(w.X, v, w.Z)
                                        | _ -> V3d(w.X, w.Y, v)
                                    emit (PickCorrespondenceAt(pinId, mesh, w'))
                                | _ -> ())
                    }
                }
            div {
                Class "ins-anchor-row"
                span { Class "mesh-num"; idxVal |> AVal.map (fun i -> string (i + 1)) }
                span {
                    Class "ins-anchor-name"
                    Attribute("title", mesh)
                    model.MeshNames.Content |> AVal.map (fun ns -> friendlyName (IndexList.toList ns) mesh)
                }
                span { Class "ins-anchor-ref"; isRef |> AVal.map (fun r -> if r then "★" else "") }
                axisInput 0 "X"
                axisInput 1 "Y"
                axisInput 2 "Z"
            }

        let manager =
            div {
                Class "ins-mgr"
                div {
                    Class "ins-mgr-head"
                    pinIdentChip
                    inlineLogSlider "r" 0.01 10000.0 (sprintf "%.2f m") radiusVal (fun v ->
                        emit (ScanPinMsg (SetInnerRadius v)))
                    span {
                        Class "ins-anchor-note"
                        showWhen (model.RegView |> AVal.map ((=) RegAfter))
                        "After pose — read-only; switch to Before to edit"
                    }
                }
                div { Class "ins-anchor-rows"; model.MeshNames |> AList.map anchorRow }
            }

        // Inspect dock: a Difference|Displacement channel toggle (drives the focus
        // tiles), the pin distribution panel (Task 4), and the shift readout
        // (Task 5). Containers are fixed; only content swaps.
        let channelA = model.InspectChannel

        // Shift readout: the focused mesh's centroid displacement
        // load→solved, split vertical (datum) / horizontal (lateral) + rotation
        // angle, derived client-side from its SolvedTransform.
        let shiftData =
            AVal.custom (fun t ->
                match Selection.mesh (model.Selection.Active.GetValue t) with
                | None -> None
                | Some m ->
                    match Map.tryFind m (model.SolvedTransforms.GetValue t) with
                    | None -> None
                    | Some sr ->
                        let scale = DatasetScale.forMesh (model.DatasetScales.GetValue t) m
                        let cc = model.CommonCentroid.GetValue t
                        let centroidW = Map.tryFind m (model.DatasetCentroids.GetValue t) |> Option.defaultValue cc
                        let sw = (RigidTransform.renderToWorld scale cc sr).Forward
                        let shift = sw.TransformPos centroidW - centroidW
                        let total = shift.Length
                        let vertical = shift.Z
                        let horizontal = sqrt (shift.X * shift.X + shift.Y * shift.Y)
                        let heading = atan2 shift.Y shift.X * 180.0 / System.Math.PI
                        let trace = sw.M00 + sw.M11 + sw.M22
                        let ang = acos (max -1.0 (min 1.0 ((trace - 1.0) / 2.0))) * 180.0 / System.Math.PI
                        let names = model.MeshNames.Content.GetValue t |> IndexList.toList
                        Some (numberedFriendly (orderVal.GetValue t) names m, total, vertical, horizontal, heading, ang))
        let shiftBody = shiftData |> AVal.map Option.isSome
        let shiftEmpty = shiftBody |> AVal.map not
        let shiftFmt f = shiftData |> AVal.map (function Some x -> f x | None -> "—")
        let shiftRow (k : string) (v : aval<string>) =
            div { Class "ins-shift-row"; span { Class "ins-shift-k"; k }; span { Class "ins-shift-v"; v } }

        // Two fixed charts (v12 §3), side by side, always both present:
        //   MESH chart = the selected mesh's error across its pins (= the
        //   selection's matrix COLUMN), series stacked in canonical pin order on
        //   an achromatic grey ramp (pins carry no identity colour — the legend
        //   names them);
        //   PIN chart = the selected pin's error across meshes (= the matrix
        //   ROW), series in the mesh palette.
        // A missing selection half renders full furniture with a placeholder.
        // Histogram counts (48 bins over the ONE shared range) are computed HERE
        // from the FULL probe sample sets — both Before/After halves once a solve
        // exists (Probe = committed pose, ProbeOther = the opposite) — while the
        // brushable conceptual samples stay the SAME canonical array as the 3D
        // side (gid = array index, committed pose only).
        let canonA = ScanPinScene.brushSamples model
        // Hover-FREE core: everything expensive (the all-sample quantile sort, the
        // 48-bin histograms, the JSON assembly) is computed here — for BOTH charts
        // in one pass — with `§P<guid>§` / `§M<mesh>§` sentinels in the hl slots;
        // the cheap hover substitution runs per chart below, so a hover crossing
        // never re-sorts the samples.
        let chartsCore =
            AVal.custom (fun t ->
                let inv = System.Globalization.CultureInfo.InvariantCulture
                let g (v : float) =
                    if System.Double.IsNaN v || System.Double.IsInfinity v then "0" else v.ToString("0.###", inv)
                let order = orderVal.GetValue t
                let pins = pinsVal.GetValue t
                let rf = model.ReferenceMesh.GetValue t
                let names = model.MeshNames.Content.GetValue t |> IndexList.toList
                let moving = names |> List.filter (fun n -> Some n <> rf)
                let sel = model.Selection.Active.GetValue t
                let regView = model.RegView.GetValue t
                let solved = not (Map.isEmpty (model.SolvedTransforms.GetValue t))
                let peek = model.RegPeekHeld.GetValue t
                // Canonical stack order (CreatedAt, guid) — same as brushSamples, so
                // pin layers stack identically everywhere.
                let readyPins =
                    pins |> HashMap.toList
                    |> List.choose (fun (id, p) -> match p.Probe with ProbeReady r -> Some (id, p, r) | _ -> None)
                    |> List.sortBy (fun (id, p, _) -> let (ScanPinId.ScanPinId gg) = id in p.CreatedAt, gg)
                let canon = canonA.GetValue t
                let friendly = numberedFriendly order names
                let selMeshRaw = Selection.mesh sel |> Option.filter (fun m -> List.contains m names)
                let selPinId = Selection.pin sel
                let meshTitle =
                    match selMeshRaw with
                    | Some m -> sprintf "Mesh %s — across pins" (friendly m)
                    | None -> "Mesh — across pins"
                let pinTitle =
                    match selPinId |> Option.bind (fun id -> HashMap.tryFind id pins) with
                    | Some p -> sprintf "Pin %s — across meshes" p.ShortName
                    | None -> "Pin — across meshes"
                let emptyJson (title : string) (ph : string) (lo : float) (hi : float) =
                    sprintf "{\"title\":\"%s\",\"ph\":\"%s\",\"lo\":%s,\"hi\":%s,\"bins\":48,\"series\":[]}"
                        title ph (g lo) (g hi)
                if List.isEmpty readyPins || List.isEmpty moving || canon.Length = 0 then
                    let pending = if List.isEmpty readyPins then "probing pins…" else "no moving meshes"
                    let ph (has : bool) (pick : string) = if has then pending else pick
                    (emptyJson meshTitle (ph selMeshRaw.IsSome "select a mesh") -10.0 10.0,
                     emptyJson pinTitle (ph selPinId.IsSome "select a pin") -10.0 10.0)
                else
                    // Emphasized pose: the committed view, flipped while Peek is held
                    // (the flip is purely visual — same contract as the 3D peek).
                    let actView = if peek then RegView.other regView else regView
                    let act = match actView with RegBefore -> "b" | RegAfter -> "a"
                    // Fixed halves: b = Before, a = After. Probe is the committed
                    // pose, ProbeOther the opposite — map accordingly.
                    let halves =
                        readyPins |> List.map (fun (id, p, r) ->
                            let other = match p.ProbeOther with ProbeReady o when solved -> Some o | _ -> None
                            match regView with
                            | RegBefore -> id, p, r, Some r, other
                            | RegAfter  -> id, p, r, other, Some r)
                    // Bucket the canonical samples by (mesh, pin) → (gid, valueMm), gid = index.
                    let byMesh = System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<ScanPinId, ResizeArray<int * float>>>()
                    canon |> Array.iteri (fun gid (id, mesh, _pos, valMm) ->
                        let md =
                            match byMesh.TryGetValue mesh with
                            | true, x -> x
                            | _ -> let x = System.Collections.Generic.Dictionary<ScanPinId, ResizeArray<int * float>>() in byMesh.[mesh] <- x; x
                        let lst =
                            match md.TryGetValue id with
                            | true, x -> x
                            | _ -> let x = ResizeArray<int * float>() in md.[id] <- x; x
                        lst.Add(gid, valMm))
                    // Shared x-range: 1–99% quantiles over the FULL sample sets of
                    // BOTH halves and ALL selections, so diagrams stay comparable.
                    let lo, hi =
                        let acc = ResizeArray<float>()
                        let add (ro : ProbeResult option) =
                            match ro with
                            | Some r ->
                                for d in r.Distributions do
                                    if List.contains d.MeshName moving then
                                        for v in d.Samples do acc.Add(v * 1000.0)
                            | None -> ()
                        for (_, _, _, b, a) in halves do add b; add a
                        let s = acc.ToArray()
                        if s.Length = 0 then -10.0, 10.0
                        else
                            Array.sortInPlace s
                            let q pp =
                                let h = pp * float (s.Length - 1)
                                let i = int h
                                if i >= s.Length - 1 then s.[s.Length - 1] else s.[i] + (h - float i) * (s.[i + 1] - s.[i])
                            q 0.01, q 0.99
                    let lo, hi = min lo 0.0, max hi 0.0
                    let pad = max 1.0 (hi - lo) * 0.08
                    let lo, hi = lo - pad, hi + pad
                    let bins = 48
                    let binW = (hi - lo) / float bins
                    let histFor (mesh : string) (ro : ProbeResult option) =
                        ro |> Option.bind (fun r ->
                            r.Distributions |> Array.tryFind (fun d -> d.MeshName = mesh && d.Count > 0)
                            |> Option.map (fun d ->
                                let c : int[] = Array.zeroCreate bins
                                for v in d.Samples do
                                    let idx = max 0 (min (bins - 1) (int ((v * 1000.0 - lo) / binW)))
                                    c.[idx] <- c.[idx] + 1
                                c))
                    let refStdOf (r : ProbeResult) =
                        r.Distributions |> Array.tryFind (fun d -> d.MeshName = r.ReferenceMesh)
                        |> Option.map (fun d -> d.Std) |> Option.defaultValue 0.0
                    let addHist a b =
                        match a, b with
                        | Some (x : int[]), Some y -> Some (Array.map2 (+) x y)
                        | Some x, None | None, Some x -> Some x
                        | None, None -> None
                    // One stacked layer aggregated over (pin × mesh) parts: summed
                    // per-pose histograms, pooled brush gids, mean LoD; a median tick
                    // only when the layer is a single (pin, mesh) cell.
                    let layer (color : C4b) (lname : string) (hl : string)
                              (parts : ((ScanPinId * ScanPin * ProbeResult * ProbeResult option * ProbeResult option) * string) list) =
                        let mutable hb = None
                        let mutable ha = None
                        let lods = ResizeArray<float>()
                        let meds = ResizeArray<float>()
                        let gids = ResizeArray<string>()
                        let svals = ResizeArray<string>()
                        for ((id, _, r, bR, aR), mesh) in parts do
                            hb <- addHist hb (histFor mesh bR)
                            ha <- addHist ha (histFor mesh aR)
                            (match byMesh.TryGetValue mesh with
                             | true, md ->
                                 match md.TryGetValue id with
                                 | true, lst -> for (gid, v) in lst do gids.Add(string gid); svals.Add(g v)
                                 | _ -> ()
                             | _ -> ())
                            match r.Distributions |> Array.tryFind (fun d -> d.MeshName = mesh && d.Count > 0) with
                            | Some d ->
                                let rs = refStdOf r
                                lods.Add(1.96 * sqrt (rs * rs + d.Std * d.Std) * 1000.0)
                                meds.Add(d.Median * 1000.0)
                            | None -> ()
                        if hb.IsNone && ha.IsNone && gids.Count = 0 then None
                        else
                            let med = if meds.Count = 1 then g meds.[0] else "null"
                            let hist (h : int[] option) = h |> Option.map (fun c -> c |> Array.map string |> String.concat ",") |> Option.defaultValue ""
                            let lod = if lods.Count = 0 then None else Some (Seq.average lods)
                            Some (lod, sprintf "{\"color\":\"%s\",\"name\":\"%s\",\"hl\":%s,\"med\":%s,\"g\":[%s],\"s\":[%s],\"hb\":[%s],\"ha\":[%s]}"
                                        (c4bToHex color) lname hl med
                                        (String.concat "," gids) (String.concat "," svals) (hist hb) (hist ha))
                    let hlPin (id : ScanPinId) =
                        let (ScanPinId.ScanPinId gg) = id in sprintf "§P%s§" (gg.ToString "N")
                    let hlMesh (m : string) = sprintf "§M%s§" m
                    let pinLabel (p : ScanPin) = p.ShortName
                    // Achromatic ramp for the mesh chart's pin series (light → dark
                    // by canonical order): distinguishable in the stack + legend
                    // without reintroducing pin identity colours.
                    let greyOf (i : int) (n : int) =
                        let tt = if n <= 1 then 0.5 else float i / float (n - 1)
                        let v = 190 - int (tt * 140.0)
                        C4b(byte v, byte v, byte v)
                    let chartJson (title : string) (ph : string) (layers : (float option * string) option list) =
                        let groups = layers |> List.choose id
                        if List.isEmpty groups then emptyJson title ph lo hi
                        else
                            let lods = groups |> List.choose fst
                            let avgLod = if List.isEmpty lods then 0.0 else List.average lods
                            sprintf "{\"title\":\"%s\",\"reg\":%s,\"act\":\"%s\",\"lo\":%s,\"hi\":%s,\"bins\":%d,\"lod\":%s,\"series\":[%s]}"
                                title (if solved then "1" else "0") act (g lo) (g hi) bins (g avgLod)
                                (groups |> List.map snd |> String.concat ",")
                    let meshJson =
                        match selMeshRaw with
                        | None -> emptyJson meshTitle "select a mesh" lo hi
                        | Some m when Some m = rf -> emptyJson meshTitle "reference mesh — no error vs itself" lo hi
                        | Some m ->
                            let n = List.length halves
                            halves |> List.mapi (fun i ((id, p, _, _, _) as h) ->
                                layer (greyOf i n) (pinLabel p) (hlPin id) [h, m])
                            |> chartJson meshTitle "no data"
                    let pinJson =
                        match selPinId |> Option.bind (fun pid -> halves |> List.tryFind (fun (id, _, _, _, _) -> id = pid)) with
                        | None -> emptyJson pinTitle "select a pin" lo hi
                        | Some ((_, p, _, _, _) as h) ->
                            moving |> List.map (fun m ->
                                let mi = HashMap.tryFind m order |> Option.defaultValue 0
                                layer (meshColor mi) (friendly m) (hlMesh m) [h, m])
                            |> chartJson (sprintf "Pin %s — across meshes" p.ShortName) "no data"
                    (meshJson, pinJson))
        // Substitute the hl sentinels for the current hover — a plain string scan,
        // so hovering matrix cells/rows never recomputes the stats above.
        let substHl (core : string) (hov : HoverTarget option) =
            if not (core.Contains '§') then core
            else
                let sb = System.Text.StringBuilder(core.Length)
                let mutable i = 0
                while i < core.Length do
                    let c = core.[i]
                    if c = '§' then
                        let e = core.IndexOf('§', i + 1)
                        let tok = core.Substring(i + 1, e - i - 1)
                        let hl =
                            if tok.[0] = 'P' then
                                match hov with
                                | Some (HoverPin (ScanPinId.ScanPinId gg))
                                | Some (HoverPoint (ScanPinId.ScanPinId gg, _)) ->
                                    if gg.ToString "N" = tok.Substring 1 then 1 else 0
                                | _ -> 1
                            else
                                match hov with
                                | Some (HoverMesh hm) | Some (HoverPoint (_, hm)) ->
                                    if hm = tok.Substring 1 then 1 else 0
                                | _ -> 1
                        sb.Append hl |> ignore
                        i <- e + 1
                    else
                        sb.Append c |> ignore
                        i <- i + 1
                sb.ToString()
        let meshChartData = (chartsCore |> AVal.map fst, model.Selection.Hovered) ||> AVal.map2 substHl
        let pinChartData  = (chartsCore |> AVal.map snd, model.Selection.Hovered) ||> AVal.map2 substHl
        // Comma-joined brushed gids → the canvas highlight (data-brushed).
        let brushedData = model.BrushedSamples |> AVal.map (fun s -> s |> Seq.map string |> String.concat ",")
        // Slice-mode dots of interest (v12 §6 bonus): their gids cross-highlight
        // in BOTH charts, following the cut plane as the slice camera moves.
        let focusData =
            ScanPinScene.sliceRankedBrush model |> AVal.map (function
                | Some ranked ->
                    ranked
                    |> Array.truncate ScanPinScene.maxDotsOfInterest
                    |> Array.map (fun (gid, _, _, _) -> string gid)
                    |> String.concat ","
                | None -> "")

        // One compact head row: the metric toggles only (channel + Δ sub-mode) —
        // they configure the view; selection is the matrix's job (§A3).
        let inspectDock =
            div {
                Class "ins-inspect"
                div {
                    Class "ins-insp-head"
                    compactButtonBar [
                        "Difference",   (channelA |> AVal.map ((=) ChDifference)),   (fun () -> emit (SetInspectChannel ChDifference))
                        "Displacement", (channelA |> AVal.map ((=) ChDisplacement)), (fun () -> emit (SetInspectChannel ChDisplacement))
                    ]
                    // Difference sub-mode (M3C2 ↔ Δz) — only meaningful in the
                    // Difference channel. The single-mesh intrinsic channels
                    // (incidence / range / shape) live in the Overview mesh list.
                    div {
                        Class "ins-insp-sub"
                        showWhen (channelA |> AVal.map ((=) ChDifference))
                        compactButtonBar [
                            "M3C2", (model.ExtrinsicZDiff |> AVal.map not),  (fun () -> if AVal.force model.ExtrinsicZDiff then emit ToggleExtrinsicZDiff)
                            "Δz",   (model.ExtrinsicZDiff :> aval<bool>),    (fun () -> if not (AVal.force model.ExtrinsicZDiff) then emit ToggleExtrinsicZDiff)
                        ]
                    }
                }
                div {
                    Class "ins-insp-body"
                    // The two fixed charts (v12 §3): mesh = matrix column, pin =
                    // matrix row — always both mounted, side by side, no reflow.
                    div {
                        Class "ins-charts"
                        div {
                            Class "ins-chart"
                            meshChartData |> AVal.map (fun j -> Some (Attribute("data-dist", j)))
                            brushedData |> AVal.map (fun b -> Some (Attribute("data-brushed", b)))
                            focusData |> AVal.map (fun f -> Some (Attribute("data-focus", f)))
                            OnBoot chartJs
                        }
                        div {
                            Class "ins-chart"
                            pinChartData |> AVal.map (fun j -> Some (Attribute("data-dist", j)))
                            brushedData |> AVal.map (fun b -> Some (Attribute("data-brushed", b)))
                            focusData |> AVal.map (fun f -> Some (Attribute("data-focus", f)))
                            OnBoot chartJs
                        }
                        // JS→Elm bridge shared by both charts: a chart writes brushed
                        // gids here + fires an input event; this forwards them to the
                        // model (SetBrushedSamples replaces the whole set).
                        input {
                            Class "ins-brush-bridge"
                            Attribute("type", "text")
                            Dom.OnInput(fun e ->
                                let ids =
                                    e.Value.Split(',')
                                    |> Array.choose (fun s -> match System.Int32.TryParse s with | true, v -> Some v | _ -> None)
                                    |> Array.toList
                                emit (SetBrushedSamples ids))
                        }
                    }
                    div {
                        Class "ins-shift"
                        div { Class "ins-stub-note"; showWhen shiftEmpty; "Focus a solved mesh to read its shift." }
                        div {
                            Class "ins-shift-body"; showWhen shiftBody
                            div { Class "ins-shift-head"; shiftFmt (fun (n, _, _, _, _, _) -> sprintf "Shift — %s" n) }
                            shiftRow "total"          (shiftFmt (fun (_, tot, _, _, hd, _) -> sprintf "%.3f m  %s" tot (dirArrow hd)))
                            shiftRow "vertical datum" (shiftFmt (fun (_, _, vr, _, _, _) -> sprintf "%+.3f m" vr))
                            shiftRow "horizontal"     (shiftFmt (fun (_, _, _, hz, _, _) -> sprintf "%.3f m" hz))
                            shiftRow "rotation"       (shiftFmt (fun (_, _, _, _, _, ang) -> sprintf "%.2f°" ang))
                        }
                    }
                }
            }

        // Vertical resize handle on the dock's top edge — same pure-JS pattern as
        // the focus panel's aspect-locked handle. Writes the --dock-h root var, the
        // single source the dock, the render control and the bottom-anchored
        // overlays all read, so everything follows one drag.
        let resizeHandle =
            div {
                Class "dock-resize"
                Attribute("title", "Drag to resize the detail dock")
                OnBoot [
                    "(function(){"
                    "var h=__THIS__; var dock=h.closest('.pin-inspector'); if(!dock) return;"
                    "var dragging=false, startY=0, startH=0;"
                    "function setH(v){ v=Math.max(120,Math.min(Math.round(window.innerHeight*0.6),v)); document.documentElement.style.setProperty('--dock-h', v+'px'); }"
                    "h.addEventListener('pointerdown',function(e){ dragging=true; startY=e.clientY; startH=dock.getBoundingClientRect().height; h.setPointerCapture(e.pointerId); e.preventDefault(); e.stopPropagation(); });"
                    "h.addEventListener('pointermove',function(e){ if(!dragging) return; setH(startH + (startY - e.clientY)); });"
                    "h.addEventListener('pointerup',function(e){ dragging=false; try{h.releasePointerCapture(e.pointerId);}catch(_){} });"
                    "})();" ]
            }

        // Container-invariant cross-fade between the three modes. (The old mode-label
        // header row is gone — the rail already names the mode; the dock height goes
        // to content.)
        let stepA = model.WorkflowStep
        let modeOn (pred : WorkflowStep -> bool) =
            classWhen "ins-mode-on" (stepA |> AVal.map pred)
        div {
            Class "pin-inspector"
            resizeHandle
            div {
                Class "ins-modes"
                // Overview dock: deliberately empty — the rail roster and the focus
                // tiles are the browsers.
                div { Class "ins-mode"; modeOn ((=) Overview) }
                div {
                    Class "ins-mode"
                    modeOn ((=) Correspondence)
                    div { Class "ins-mgr-wrap"; showWhen hasPin; manager }
                }
                div { Class "ins-mode"; modeOn ((=) Inspect); inspectDock }
            }
        }
