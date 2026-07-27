namespace Superprojekt

open Aardvark.Base
open Aardworx.WebAssembly
open FSharp.Data.Adaptive
open Aardvark.Dom
open Microsoft.JSInterop

// Left panel: the registration navigator — matrix-home (survey/root Setup +
// the mesh×mesh pair matrix) and the cell-workspace (per-pair toolkit +
// error inspection). Pure view — every control dispatches an existing message
// and never issues server queries itself.
module GuiRail =

    open Primitives

    let private rgb = Primitives.c4bToRgbCss

    // ── The ONE in-cell error diagram: the MOV mesh's error across the pair's
    // pins, pin-source-STACKED (48-bin histogram, achromatic pin ramp), with
    // the per-edge before/after diff (fill = current, near-black step outline =
    // the edge-before total, registered pairs only). Full furniture always; an
    // empty payload renders a centred placeholder. Brushing = an x-range drag
    // over the conceptual samples (el._dots) → gid csv through the hidden
    // bridge input; a plain click clears. data-brushed echoes the model back;
    // data-hover (the 3D-hovered gid) draws the amber cross-highlight.
    let private chartJs = [
        "(function(){"
        "var el=__THIS__;"
        "function findBridge(){ return (el.parentElement&&el.parentElement.querySelector('.cw-brush-bridge'))||document.querySelector('.cw-brush-bridge'); }"
        "el._dots=[]; var dragging=false, range=null, anchorV=0;"
        "function emit(){ var b=findBridge(); if(!b) return; var ids=[];"
        "  if(range){ for(var i=0;i<el._dots.length;i++){ var dt=el._dots[i]; if(dt.v>=range[0]&&dt.v<=range[1]) ids.push(dt.gid); } }"
        "  b.value=ids.join(','); b.dispatchEvent(new Event('input',{bubbles:true})); }"
        "function niceStep(raw){ var mag=Math.pow(10,Math.floor(Math.log(raw)/Math.LN10)); var n=raw/mag; return (n<1.5?1:n<3.5?2:n<7.5?5:10)*mag; }"
        "function render(){"
        "  var raw=el.getAttribute('data-chart')||'{}'; var d; try{d=JSON.parse(raw);}catch(e){return;}"
        "  var braw=el.getAttribute('data-brushed')||''; var bset=new Set(braw.length?braw.split(',').map(Number):[]);"
        "  var hraw=el.getAttribute('data-hover')||''; var hgid=hraw.length?parseInt(hraw):-1;"
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
        "  var B=d.bins||48; var bw=span/B;"
        "  function histC(sn,k){ var a=sn[k]; return (a&&a.length===B)?a:null; }"
        // One count→height scale over BOTH sides; y = counts.
        "  var maxC=1; ['h','hb'].forEach(function(k){ for(var b=0;b<B;b++){ var s=0; S.forEach(function(sn){ var a=histC(sn,k); if(a) s+=a[b]; }); if(s>maxC)maxC=s; } });"
        "  var k=(axY-axT-4)/maxC;"
        "  if(d.lod>0){ g.fillStyle='rgba(148,163,184,0.18)'; g.fillRect(X(-d.lod),axT,Math.max(1,X(d.lod)-X(-d.lod)),axY-axT); }"
        // Filled stack: the CURRENT side, series bottom-up in payload order.
        "  var prev=new Array(B).fill(0);"
        "  S.forEach(function(sn){ var a=histC(sn,'h'); if(!a) return;"
        "    g.globalAlpha=0.85; g.fillStyle=sn.color;"
        "    for(var b=0;b<B;b++){ var c=a[b]; if(c>0){ var x0=X(lo+b*bw); var wd=Math.max(1,X(lo+(b+1)*bw)-x0);"
        "      g.fillRect(x0,axY-(prev[b]+c)*k,wd,c*k); } prev[b]+=c; } });"
        // Edge-BEFORE: near-black step outline of the total (shape only).
        "  var anyO=false; var tot=new Array(B).fill(0);"
        "  S.forEach(function(sn){ var a=histC(sn,'hb'); if(!a) return; anyO=true; for(var b=0;b<B;b++) tot[b]+=a[b]; });"
        "  if(anyO){ g.globalAlpha=0.9; g.strokeStyle='#0f172a'; g.lineWidth=1;"
        "    g.beginPath(); g.moveTo(X(lo),axY);"
        "    for(var b=0;b<B;b++){ var yT=axY-tot[b]*k; g.lineTo(X(lo+b*bw),yT); g.lineTo(X(lo+(b+1)*bw),yT); }"
        "    g.lineTo(X(hi),axY); g.stroke();"
        "    g.fillStyle='#64748b'; g.font='9px Inter,sans-serif'; g.textAlign='left';"
        "    g.fillText('fill now \\u00b7 line before',padL+3,axT+9); }"
        "  S.forEach(function(sn){ if(sn.med==null) return; g.globalAlpha=0.9; g.strokeStyle=sn.color; g.lineWidth=1.6;"
        "    g.beginPath(); g.moveTo(X(sn.med),axY); g.lineTo(X(sn.med),axY-9); g.stroke(); g.lineWidth=1; });"
        // Conceptual samples: never painted, but they ARE the brush targets.
        "  g.globalAlpha=1;"
        "  S.forEach(function(sn){ if(sn.g) for(var q=0;q<sn.g.length;q++) el._dots.push({gid:sn.g[q],v:sn.s[q]}); });"
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
        "el.addEventListener('pointerdown',function(e){ dragging=true; anchorV=cursorV(e); range=null; el.setPointerCapture(e.pointerId); });"
        "el.addEventListener('pointermove',function(e){ if(!dragging) return; var v=cursorV(e);"
        "  range=[Math.min(anchorV,v),Math.max(anchorV,v)]; render(); });"
        "el.addEventListener('pointerup',function(e){ if(!dragging) return; dragging=false;"
        "  var v=cursorV(e); if(Math.abs(v-anchorV)<1e-9) range=null; emit(); render(); });"
        "render();"
        "new MutationObserver(render).observe(el,{attributes:true,attributeFilter:['data-chart','data-brushed','data-hover']});"
        "})();"
    ]

    let rail (env : Env<Message>) (model : AdaptiveModel) =

        // ── Mesh × mesh navigator: rows/cols = meshes, UPPER TRIANGLE only
        // (registration is symmetric — no lower half; no diagonal, root
        // designation is the Setup step). Cell (A,B) IS the pair's registration
        // edge. Emphasis ramp: impossible fades into the background (a hole) <
        // possible = an outlined empty vessel < registered = filled, fill
        // strength = the edge's ONE quality scalar (achromatic ink — the colour
        // families stay free for gradients and mesh identity).
        // Ordering = the one scalability lever: sensor (canonical) / coverage
        // (XY footprint) / connectedness to the root. Contents never change
        // with the order — cells derive from the pair alone.
        let orderedNames =
            AVal.custom (fun t ->
                let names = model.MeshNames.Content.GetValue t |> IndexList.toList
                let canonical = model.MeshOrder.Content.GetValue t |> HashMap.toList |> Map.ofList
                let coverage =
                    model.MeshBounds.GetValue t
                    |> Map.map (fun _ (b : Box3d) -> if b.IsInvalid then 0.0 else b.Size.X * b.Size.Y)
                MatrixNav.orderMeshes (model.MatrixOrder.GetValue t) canonical coverage
                    (model.RegGraph.GetValue t) names)
        let pairCellView (a : string) (b : string) =
            let st =
                (model.PairOverlaps, model.RegGraph) ||> AVal.map2 (fun po g -> PairCell.state po g a b)
            let title =
                (st, model.MeshOrder.Content) ||> AVal.map2 (fun s order ->
                    let num m = (HashMap.tryFind m order |> Option.defaultValue 0) + 1
                    let pair = sprintf "%d × %d" (num a) (num b)
                    match s with
                    | PairImpossible -> sprintf "%s — insufficient overlap" pair
                    | PairPossible -> sprintf "%s — can be registered (not yet)" pair
                    | PairRegistered q -> sprintf "%s — registered, quality %.2f" pair q)
            div {
                Class "pmx-cell"
                st |> AVal.map (function
                    | PairImpossible -> Some (Class "pmx-imp")
                    | PairPossible -> Some (Class "pmx-pos")
                    | PairRegistered _ -> Some (Class "pmx-reg"))
                st |> AVal.map (function
                    | PairRegistered q ->
                        Some (Style [Css.Background (sprintf "rgba(15, 23, 42, %.3f)" (0.30 + 0.65 * clamp 0.0 1.0 q))])
                    | _ -> None)
                title |> AVal.map (fun tt -> Some (Attribute("title", tt)))
                // Descend: a Possible/Registered cell IS the pair — clicking it
                // enters that pair's workspace (impossible cells are inert holes).
                Dom.OnClick(fun _ ->
                    match AVal.force st with
                    | PairPossible | PairRegistered _ -> env.Emit [DescendPair(a, b)]
                    | PairImpossible -> ())
            }
        let numSwatch (name : string) =
            let idxVal = model.MeshOrder |> AMap.tryFind name |> AVal.map (Option.defaultValue 0)
            AList.ofList [
                span { Class "pmx-sw"; idxVal |> AVal.map (fun i -> Some (Style [Css.Background (rgb (meshColor i))])) }
                span { Class "pmx-num"; idxVal |> AVal.map (fun i -> string (i + 1)) }
            ]
        // Rebuilt wholesale on an order change — a ≤ palette-sized grid that
        // changes rarely (the sanctioned simple AList form).
        let pairMatrixView () =
            let orderBar =
                div {
                    Class "pmx-order"
                    Attribute("title", "Row/column order: sensor (acquisition), coverage (footprint), or connectedness to the root")
                    span { Class "pmx-order-label"; "Order" }
                    compactButtonBar [
                        "Sensor", (model.MatrixOrder |> AVal.map ((=) OrderSensor)),    (fun () -> env.Emit [SetMatrixOrder OrderSensor])
                        "Cover",  (model.MatrixOrder |> AVal.map ((=) OrderCoverage)),  (fun () -> env.Emit [SetMatrixOrder OrderCoverage])
                        "Root",   (model.MatrixOrder |> AVal.map ((=) OrderConnected)), (fun () -> env.Emit [SetMatrixOrder OrderConnected])
                    ]
                }
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
                                AList.ofList [ for j in 1 .. n - 1 -> div { Class "pmx-colhead"; Attribute("title", arr.[j]); numSwatch arr.[j] } ]
                            }
                            for i in 0 .. n - 2 do
                                yield div {
                                    Class "pmx-row"
                                    div { Class "pmx-rowhead"; Attribute("title", arr.[i]); numSwatch arr.[i] }
                                    AList.ofList [
                                        for j in 1 .. n - 1 ->
                                            if j <= i then div { Class "pmx-cell pmx-void" }
                                            else pairCellView arr.[i] arr.[j]
                                    ]
                                }
                        ])
            div {
                Class "pmx"
                orderBar
                rowsA |> AList.ofAVal
            }

        // ── Setup state: survey the meshes (identity, sensor fly-to, intrinsic
        // error visualization) + designate the reference-root as an explicit
        // separate step. Root designation lives HERE deliberately: not on the
        // matrix diagonal, not on a cell.
        let surveyRow (name : string) =
            let idxVal = model.MeshOrder |> AMap.tryFind name |> AVal.map (Option.defaultValue 0)
            let isRoot = model.RegGraph |> AVal.map (fun g -> g.Root = Some name)
            let hm = model.MeshHeatmap |> AVal.map (fun m -> Map.tryFind name m |> Option.defaultValue HeatOff)
            div {
                Class "pmx-root-row"
                classWhen "pmx-root-on" isRoot
                Attribute("title", name)
                span { Class "pmx-sw"; idxVal |> AVal.map (fun i -> Some (Style [Css.Background (rgb (meshColor i))])) }
                span { Class "pmx-num"; idxVal |> AVal.map (fun i -> string (i + 1)) }
                span {
                    Class "pmx-root-name"
                    Attribute("title", "Designate as the reference root. Re-rooting inside the registered tree keeps the registration; a mesh outside it clears the graph. Double-click = 3D zoom.")
                    Dom.OnClick(fun _ -> env.Emit [SetRegRoot name])
                    Dom.OnDoubleClick(fun _ -> env.Emit [ZoomToMesh name])
                    model.MeshNames.Content |> AVal.map (fun ns -> friendlyName (IndexList.toList ns) name)
                }
                span { Class "pmx-root-star"; isRoot |> AVal.map (fun r -> if r then "★" else "") }
                button {
                    Class "mb mb-cam"
                    Attribute("title", "Fly the 3D camera to this mesh's sensor viewpoint")
                    Dom.OnClick(fun _ -> env.Emit [FlyToSensor name])
                    "◎"
                }
                div {
                    Class "rail-mesh-modes"
                    Attribute("title", "Error visualization for this mesh: Textured · Distance · Shape · Incidence")
                    compactButtonBar [
                        "Tex",  (hm |> AVal.map ((=) HeatOff)),       (fun () -> env.Emit [SetMeshHeatmap(name, HeatOff)])
                        "Dst",  (hm |> AVal.map ((=) HeatRange)),     (fun () -> env.Emit [SetMeshHeatmap(name, HeatRange)])
                        "Shp",  (hm |> AVal.map ((=) HeatShape)),     (fun () -> env.Emit [SetMeshHeatmap(name, HeatShape)])
                        "Inc",  (hm |> AVal.map ((=) HeatIncidence)), (fun () -> env.Emit [SetMeshHeatmap(name, HeatIncidence)])
                    ]
                }
            }
        let anyShapeOn =
            model.MeshHeatmap |> AVal.map (Map.exists (fun _ h -> h = HeatShape))
        let rootOverview () =
            div {
                Class "pmx-root"
                div { Class "pmx-root-hint"; "Reference root ★ — every pose composes toward it" }
                model.MeshNames |> AList.map surveyRow
                div {
                    Class "rail-shape-cut"
                    showWhen anyShapeOn
                    inlineSlider "Shape ≥" 0.0 1.0 0.01 (sprintf "%.2f") model.ShapeThreshold (fun v ->
                        env.Emit [SetShapeThreshold v])
                }
            }

        // ── Matrix-home: ONE instrument, two states — Setup (survey + root)
        // and Pairs (the matrix).
        let matrixHomeView () =
            div {
                Class "pmx-home"
                div {
                    Class "pmx-tabs"
                    compactButtonBar [
                        "Setup", (model.MatrixHome |> AVal.map ((=) HomeOverview)), (fun () -> env.Emit [SetMatrixHome HomeOverview])
                        "Pairs", (model.MatrixHome |> AVal.map ((=) HomePairs)),    (fun () -> env.Emit [SetMatrixHome HomePairs])
                    ]
                }
                div { showWhen (model.MatrixHome |> AVal.map ((=) HomeOverview)); rootOverview () }
                div { showWhen (model.MatrixHome |> AVal.map ((=) HomePairs)); pairMatrixView () }
            }

        // ── Cell-workspace: scoped to ONE pair. The A↔B header pins the pair
        // identity for the whole stay; tools below operate on this pair as a
        // toolkit, never as global modes (the remaining tools land P6–P8).
        let cellWorkspace (a : string) (b : string) =
            let meshChip (name : string) =
                let idxVal = model.MeshOrder |> AMap.tryFind name |> AVal.map (Option.defaultValue 0)
                div {
                    Class "cw-chip"
                    Attribute("title", name)
                    span { Class "pmx-sw"; idxVal |> AVal.map (fun i -> Some (Style [Css.Background (rgb (meshColor i))])) }
                    span { Class "pmx-num"; idxVal |> AVal.map (fun i -> string (i + 1)) }
                    span {
                        Class "cw-chip-name"
                        model.MeshNames.Content |> AVal.map (fun ns -> friendlyName (IndexList.toList ns) name)
                    }
                }
            let pairState =
                (model.PairOverlaps, model.RegGraph) ||> AVal.map2 (fun po g ->
                    match PairCell.state po g a b with
                    | PairRegistered q -> sprintf "registered · quality %.2f" q
                    | PairPossible -> "not registered yet"
                    | PairImpossible -> "insufficient overlap")
            let pairKey = PairCell.key a b
            let placement = model.ScanPins.Placement
            let placing = placement |> AVal.map (function PlacementActive _ -> true | _ -> false)
            let idle = placing |> AVal.map not
            let numOf (mesh : string) =
                model.MeshOrder |> AMap.tryFind mesh |> AVal.map (fun i -> (Option.defaultValue 0 i) + 1)
            // The pair's pins, canonical order — rebuilt on pin add/delete only
            // (identity projection; the sanctioned simple AList form at this size).
            let pairPins =
                model.ScanPins.Pins |> AMap.toAVal |> AVal.map (fun pins ->
                    pins |> HashMap.toList |> List.map snd
                    |> List.filter (fun p -> p.Pair = pairKey)
                    |> List.sortBy (fun p -> p.CreatedAt, p.ShortName))
            let pinCount = pairPins |> AVal.map List.length

            // ── The placement transaction UI: self-announcing, free order —
            // Area/Points sub-tools re-armable any time, re-picking replaces,
            // the "N of 2" cue lives ONLY here, Esc/✕ aborts (full rollback),
            // ✓ commits (enabled only when complete → the pin is born atomic).
            let draftBar =
                let toolIs tool =
                    placement |> AVal.map (function
                        | PlacementActive(t, _) -> t = tool
                        | PlacementIdle -> false)
                let draft = placement |> AVal.map (function PlacementActive(_, d) -> Some d | _ -> None)
                let cue =
                    draft |> AVal.map (function
                        | Some d ->
                            sprintf "area %s · %d of 2 points"
                                (if d.Area.IsSome then "✓" else "·") (PinDraft.pointCount d)
                        | None -> "")
                let complete = draft |> AVal.map (function Some d -> PinDraft.complete d | None -> false)
                div {
                    Class "cw-draft"
                    showWhen placing
                    compactButtonBar [
                        "◯ Area",  toolIs ToolArea,  (fun () -> env.Emit [ScanPinMsg (SetDraftTool ToolArea)])
                        "✚ Points", toolIs ToolPoint, (fun () -> env.Emit [ScanPinMsg (SetDraftTool ToolPoint)])
                    ]
                    span { Class "cw-cue"; cue }
                    button {
                        Class "rail-btn cw-commit"
                        complete |> AVal.map (fun ok -> if ok then None else Some (Attribute("disabled", "disabled")))
                        Attribute("title", "Commit: the pin is born whole (area + both points)")
                        Dom.OnClick(fun _ -> env.Emit [ScanPinMsg CommitPin])
                        "✓ Commit"
                    }
                    button {
                        Class "rail-btn cw-abort"
                        Attribute("title", "Abort the placement — nothing persists (Esc)")
                        Dom.OnClick(fun _ -> env.Emit [ScanPinMsg AbortPinTransaction])
                        "✕"
                    }
                }

            // ── Committed-pin rows: radius, per-mesh point re-pick, delete.
            // Every edit invalidates the pair's solve (the reducer drops the edge).
            let pinRow (p : ScanPin) =
                let editArm (mesh : string) =
                    let armed =
                        model.ScanPins.Edit |> AVal.map (function
                            | EditPoint(id, m) -> id = p.Id && m = mesh
                            | EditIdle -> false)
                    button {
                        Class "mb cw-edit"
                        classWhen "mb-on" armed
                        numOf mesh |> AVal.map (fun n ->
                            Some (Attribute("title", sprintf "Re-pick this pin's point on mesh %d (click its surface; Esc cancels)" n)))
                        Dom.OnClick(fun _ ->
                            if AVal.force armed then env.Emit [ScanPinMsg CancelPointEdit]
                            else env.Emit [ScanPinMsg (BeginPointEdit(p.Id, mesh))])
                        numOf mesh |> AVal.map (fun n -> sprintf "·%d" n)
                    }
                div {
                    Class "cw-pin-row"
                    span { Class "cw-pin-name"; p.ShortName }
                    inlineLogSlider "r" 0.01 100.0 (sprintf "%.2f m")
                        (model.ScanPins.Pins |> AMap.tryFind p.Id
                         |> AVal.map (fun po -> po |> Option.map (fun q -> q.InnerRadius) |> Option.defaultValue p.InnerRadius))
                        (fun v -> env.Emit [ScanPinMsg (SetInnerRadius(p.Id, v))])
                    editArm (fst pairKey)
                    editArm (snd pairKey)
                    button {
                        Class "mb cw-del"
                        Attribute("title", "Delete pin")
                        Dom.OnClick(fun _ ->
                            let ok = try JSRuntime.Instance.Invoke<bool>("confirm", sprintf "Delete pin %s? This cannot be undone." p.ShortName) with _ -> false
                            if ok then env.Emit [ScanPinMsg (DeletePin p.Id)])
                        "✕"
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

            // ── In-cell error inspection: the ONE diagram (MOV across pins),
            // the false-colour map toggle, and the transient armed probe.
            let chartData =
                AVal.custom (fun t ->
                    let inv = System.Globalization.CultureInfo.InvariantCulture
                    let gf (v : float) =
                        if System.Double.IsNaN v || System.Double.IsInfinity v then "0" else v.ToString("0.###", inv)
                    let order = model.MeshOrder.Content.GetValue t
                    let numOfN m = (HashMap.tryFind m order |> Option.defaultValue 0) + 1
                    let refM, movM = MatrixNav.pairRefMov (model.RegGraph.GetValue t) a b
                    let title = sprintf "Mesh %d error vs %d — across pins" (numOfN movM) (numOfN refM)
                    match model.CellError.GetValue t with
                    | None ->
                        sprintf "{\"title\":\"%s\",\"ph\":\"place pins to measure\",\"lo\":-10,\"hi\":10,\"bins\":48,\"series\":[]}" title
                    | Some cells ->
                        let before = model.CellErrorBefore.GetValue t
                        let pins = model.ScanPins.Pins.Content.GetValue t
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
                        let greyOf (i : int) (n : int) =
                            let tt = if n <= 1 then 0.5 else float i / float (n - 1)
                            let v = 190 - int (tt * 140.0)
                            c4bToHex (C4b(byte v, byte v, byte v))
                        let lods = cells |> Array.choose (fun (_, r) -> if r.Count > 0 then Some r.LodHalfWidth else None)
                        let lod = if lods.Length = 0 then 0.0 else (Array.average lods) * 1000.0
                        let mutable gid = 0
                        let series =
                            cells |> Array.mapi (fun i (pid, r) ->
                                let name =
                                    match HashMap.tryFind pid pins with
                                    | Some p -> p.ShortName
                                    | None -> "?"
                                let hb =
                                    match before with
                                    | Some bs ->
                                        bs |> Array.tryFind (fun (bid, _) -> bid = pid)
                                        |> Option.map (fun (_, br) -> histOf br.Samples)
                                    | None -> None
                                let gids = Array.init r.Samples.Length (fun k -> gid + k)
                                gid <- gid + r.Samples.Length
                                let med = if r.Count > 0 then gf (r.Median * 1000.0) else "null"
                                let hj = histOf r.Samples |> Array.map string |> String.concat ","
                                let hbj = hb |> Option.map (fun c -> c |> Array.map string |> String.concat ",") |> Option.defaultValue ""
                                sprintf "{\"name\":\"%s\",\"color\":\"%s\",\"med\":%s,\"g\":[%s],\"s\":[%s],\"h\":[%s],\"hb\":[%s]}"
                                    name (greyOf i cells.Length) med
                                    (gids |> Array.map string |> String.concat ",")
                                    (r.Samples |> Array.map (fun v -> gf (v * 1000.0)) |> String.concat ",")
                                    hj hbj)
                            |> String.concat ","
                        sprintf "{\"title\":\"%s\",\"lo\":%s,\"hi\":%s,\"bins\":%d,\"lod\":%s,\"series\":[%s]}"
                            title (gf lo) (gf hi) bins (gf lod) series)
            let brushedData = model.BrushedSamples |> AVal.map (fun s -> s |> Seq.map string |> String.concat ",")
            let hoverData = model.HoverSample |> AVal.map (function Some g -> string g | None -> "")
            let inspectSection =
                div {
                    Class "cw-inspect"
                    div {
                        Class "cw-tools"
                        div {
                            Class "rail-isolate"
                            Attribute("title", "False-colour error map: paints the MOV mesh's signed distance vs the reference (the reference is never error-coloured)")
                            compactToggle "Error map" model.CellMapOn (fun () -> env.Emit [ToggleCellMap])
                        }
                        button {
                            Class "rail-btn cw-probe"
                            classWhen "rail-btn-active" model.ProbeArmed
                            Attribute("title", "Armed point probe: while armed, pick any 3D point for its exact error value. Fully transient — disarm wipes it (Esc).")
                            Dom.OnClick(fun _ -> env.Emit [ToggleProbeArmed])
                            "⊕ Probe"
                        }
                    }
                    div {
                        Class "cw-readout"
                        span {
                            Class "cw-readout-probe"
                            showWhen model.ProbeArmed
                            model.ProbeReadout |> AVal.map (function
                                | Some (_, v) -> sprintf "probe %+.1f mm" (v * 1000.0)
                                | None -> "probe: pick a 3D point")
                        }
                        span {
                            Class "cw-readout-hover"
                            model.HoverReadout |> AVal.map (function
                                | Some (_, v) -> sprintf "sample %+.1f mm" (v * 1000.0)
                                | None -> "")
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

            div {
                Class "cw"
                div {
                    Class "cw-head"
                    button {
                        Class "cw-back"
                        Attribute("title", "Back to the pair matrix (Esc)")
                        Dom.OnClick(fun _ -> env.Emit [NavAscend])
                        "‹"
                    }
                    meshChip a
                    span { Class "cw-link"; "↔" }
                    meshChip b
                }
                div { Class "cw-state"; pairState }
                // The per-pair toolkit — every tool operates on THIS pair only
                // (scoping flows through the nav visibility rule).
                div {
                    Class "cw-tools"
                    button {
                        Class "rail-btn rail-pin-add"
                        showWhen idle
                        Attribute("title", "Place a pin on this pair: drop the area marker + pick a point on each mesh, then commit (free order; Esc aborts)")
                        Dom.OnClick(fun _ -> env.Emit [ScanPinMsg (BeginPinTransaction pairKey)])
                        "○ New pin"
                    }
                    button {
                        Class "rail-btn cw-solve"
                        showWhen idle
                        pinCount |> AVal.map (fun n -> if n >= 3 then None else Some (Attribute("disabled", "disabled")))
                        Attribute("title", "Solve this pair's edge from its pins (needs ≥3)")
                        Dom.OnClick(fun _ ->
                            if AVal.force pinCount >= 3 then env.Emit [SolvePair(a, b)])
                        pinCount |> AVal.map (fun n -> sprintf "⌖ Solve (%d/3)" (min n 3))
                    }
                    div {
                        Class "rail-isolate"
                        showWhen idle
                        Attribute("title", "Isolate pins: show only the pin patches; unchecked shows the full textured meshes")
                        compactToggle "Isolate pins" model.AnchorGhostMode (fun () ->
                            env.Emit [ToggleAnchorGhostMode])
                    }
                }
                draftBar
                pinList
                inspectSection
            }

        div {
            Class "workflow-rail"
            div {
                Class "rail-body"
                // Home vs cell — rebuilt on a nav change (rare; the workspace is
                // freshly keyed to its pair).
                let levelNode =
                    model.Nav
                    |> AVal.map (fun nav ->
                        let node =
                            match nav with
                            | NavHome -> matrixHomeView ()
                            | NavCell (a, b) -> cellWorkspace a b
                        IndexList.ofList [ node ])
                    |> AList.ofAVal
                levelNode
            }
        }
