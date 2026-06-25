namespace Superprojekt

open Aardvark.Base
open FSharp.Data.Adaptive
open Aardvark.Dom

// Bottom-dock pin inspector (Pin-Inspector spec §B). A full-width 2D dock
// (SVG/HTML, NOT a WebGL control) docked below the 3D viewport, always mounted,
// reading only the selected pin. Four panels left→right: B1 identity, B2
// raincloud (per moving mesh on a shared signed-distance axis), B3
// correspondence readout (numbers only), B4 three intrinsic-quality bars.
// Hard rule: no prose — only numbers, units, marks, glyphs, terse identifiers.
module GuiInspector =

    open Primitives

    // Red→green quality ramp (matches the §6 heatmaps); v ∈ [0,1].
    let private ramp (v : float) =
        let v = max 0.0 (min 1.0 v)
        let lerp (a : float) (b : float) = int (a + (b - a) * v)
        sprintf "rgb(%d,%d,%d)" (lerp 220.0 22.0) (lerp 38.0 163.0) (lerp 38.0 74.0)

    // Raincloud JSON: one row per visible moving mesh on a shared, zero-centred
    // signed-distance axis. after = effective (preview-or-committed) probe;
    // before = committed-probe median tick.
    let private rainJson
            (order : HashMap<string,int>) (moving : string list) (active : string option)
            (before : ProbeResult option) (after : ProbeResult option) : string =
        match after with
        | None -> "{\"status\":\"running\"}"
        | Some a ->
            if List.isEmpty moving then "{\"status\":\"none\"}" else
            let inv = System.Globalization.CultureInfo.InvariantCulture
            let g (v : float) = if System.Double.IsNaN v || System.Double.IsInfinity v then "0" else v.ToString("0.#####", inv)
            let refStd, refN =
                a.Distributions |> Array.tryFind (fun d -> d.MeshName = a.ReferenceMesh)
                |> Option.map (fun d -> d.Std, d.Count) |> Option.defaultValue (0.0, 0)
            let afterOf m = a.Distributions |> Array.tryFind (fun d -> d.MeshName = m)
            let beforeOf m = before |> Option.bind (fun b -> b.Distributions |> Array.tryFind (fun d -> d.MeshName = m))
            // Robust symmetric domain from the pooled before+after samples.
            let pool = ResizeArray<float>()
            for m in moving do
                match afterOf m with Some d -> pool.AddRange d.Samples | None -> ()
                match beforeOf m with Some d when d.Count > 0 -> pool.Add d.Median | _ -> ()
            let dom =
                if pool.Count = 0 then 0.1
                else
                    let s = pool.ToArray() in System.Array.Sort s
                    let q p = s.[max 0 (min (s.Length - 1) (int (p * float (s.Length - 1))))]
                    max 0.05 (max (abs (q 0.01)) (abs (q 0.99)))
            let activeMesh = active |> Option.filter (fun m -> List.contains m moving) |> Option.orElse (List.tryHead moving)
            let topN = List.length moving
            let sb = System.Text.StringBuilder()
            sb.Append(sprintf "{\"status\":\"ready\",\"lo\":%s,\"hi\":%s,\"rows\":[" (g -dom) (g dom)) |> ignore
            moving |> List.iteri (fun i m ->
                if i > 0 then sb.Append ',' |> ignore
                let col = match HashMap.tryFind m order with Some i -> c4bToHex (meshColor i) | None -> "#1a56db"
                let af = afterOf m
                let cnt = af |> Option.map (fun d -> d.Count) |> Option.defaultValue 0
                let std = af |> Option.map (fun d -> d.Std) |> Option.defaultValue 0.0
                let lod = 1.96 * sqrt (refStd * refStd / float (max 1 refN) + std * std / float (max 1 cnt))
                let med = af |> Option.map (fun d -> d.Median) |> Option.defaultValue 0.0
                let q1  = af |> Option.map (fun d -> d.Q1) |> Option.defaultValue 0.0
                let q3  = af |> Option.map (fun d -> d.Q3) |> Option.defaultValue 0.0
                let bm  = beforeOf m
                sb.Append(sprintf "{\"mesh\":\"%s\",\"name\":\"%s\",\"color\":\"%s\",\"active\":%b,\"count\":%d,\"median\":%s,\"q1\":%s,\"q3\":%s,\"lod\":%s,\"hasBefore\":%b,\"before\":%s,\"samples\":["
                            m (numbered order m) col (activeMesh = Some m) cnt (g med) (g q1) (g q3) (g lod)
                            (Option.isSome bm) (g (bm |> Option.map (fun d -> d.Median) |> Option.defaultValue 0.0))) |> ignore
                match af with
                | Some d -> d.Samples |> Array.iteri (fun j s -> (if j > 0 then sb.Append ',' |> ignore); sb.Append(g s) |> ignore)
                | None -> ()
                sb.Append "],\"kde\":[" |> ignore
                match af with
                | Some d when cnt >= 20 ->
                    d.Kde |> Array.iteri (fun j (x, y) -> (if j > 0 then sb.Append ',' |> ignore); sb.Append(sprintf "[%s,%s]" (g x) (g y)) |> ignore)
                | _ -> ()
                sb.Append "]}" |> ignore)
            ignore topN
            sb.Append "]}" |> ignore
            sb.ToString()

    let private rainJs = [
        "  function ph(t){ var p=document.createElement('div'); p.className='ins-ph'; p.textContent=t; el.appendChild(p); }"
        "  if(!d||d.status==='none'){ ph('—'); return; }"
        "  if(d.status==='running'){ ph('⋯'); return; }"
        "  var rows=d.rows||[]; if(rows.length===0){ ph('—'); return; }"
        "  var lo=d.lo,hi=d.hi; if(!(hi>lo)){ lo=-0.1; hi=0.1; }"
        "  var W=el.clientWidth||320, H=el.clientHeight||170;"
        "  var labelW=66,padR=10,axisH=14;"
        "  var x0=labelW,x1=W-padR,pw=Math.max(10,x1-x0),n=rows.length,rh=(H-axisH)/n;"
        "  function X(v){ return x0+(v-lo)/(hi-lo)*pw; }"
        "  var svg=document.createElementNS(ns,'svg'); svg.setAttribute('width',W); svg.setAttribute('height',H); svg.setAttribute('viewBox','0 0 '+W+' '+H);"
        "  function E(tag,a){ var e=document.createElementNS(ns,tag); for(var k in a) e.setAttribute(k,a[k]); return e; }"
        "  function ln(xa,ya,xb,yb,c,w,op,dash){ var l=E('line',{x1:xa,y1:ya,x2:xb,y2:yb,stroke:c,'stroke-width':w}); if(op)l.setAttribute('stroke-opacity',op); if(dash)l.setAttribute('stroke-dasharray',dash); svg.appendChild(l); return l; }"
        "  function tx(x,y,s,c,sz,an){ var t=E('text',{x:x,y:y,fill:c,'font-size':sz||9,'font-family':'SF Mono,Monaco,monospace','text-anchor':an||'start'}); t.textContent=s; svg.appendChild(t); return t; }"
        "  var zx=X(0); ln(zx,0,zx,H-axisH,'#94a3b8',1,'0.8');"
        "  var span=hi-lo,raw=span/4,p=Math.pow(10,Math.floor(Math.log(raw)/Math.LN10)),m=raw/p,step=(m>=5?5:m>=2?2:1)*p,dec=Math.max(0,-Math.floor(Math.log(step)/Math.LN10+1e-9));"
        "  for(var tv=Math.ceil(lo/step)*step; tv<=hi+step*0.001; tv+=step){ var xx=X(tv); ln(xx,H-axisH,xx,H-axisH+3,'#94a3b8',1); tx(xx,H-3,tv.toFixed(dec),'#64748b',8,'middle'); }"
        "  tx(x1,H-3,'m','#64748b',8,'end');"
        "  rows.forEach(function(r,i){ var yTop=i*rh, yc=yTop+rh*0.62, grey=r.count===0;"
        "    if(r.active) svg.appendChild(E('rect',{x:0,y:yTop,width:W,height:rh,fill:'#0891b2','fill-opacity':0.06}));"
        "    tx(4,yc-1,r.name,grey?'#94a3b8':'#0f172a',9);"
        "    var hit=E('rect',{x:0,y:yTop,width:W,height:rh,fill:'transparent'}); hit.style.cursor='pointer';"
        "    hit.addEventListener('click',function(){ var b=el.closest('.pin-inspector'); b=b?b.querySelector('.ins-rain-bus'):null; if(b){ b.value='row|'+r.mesh; b.dispatchEvent(new Event('input',{bubbles:true})); } });"
        "    svg.appendChild(hit);"
        "    ln(x0,yc,x1,yc,grey?'#e2e8f0':'#cbd5e1',1);"
        "    if(grey) return;"
        "    if(r.lod>0){ var bl=X(Math.max(lo,-r.lod)),bh=X(Math.min(hi,r.lod)); svg.appendChild(E('rect',{x:bl,y:yTop+2,width:Math.max(0,bh-bl),height:rh-4,fill:'#94a3b8','fill-opacity':0.14})); }"
        "    if(r.count>=20 && r.kde && r.kde.length>1){ var md=0; r.kde.forEach(function(q){ if(q[0]>=lo&&q[0]<=hi&&q[1]>md)md=q[1]; }); if(md>0){ var hg=rh*0.42,pa='',st=false; r.kde.forEach(function(q){ if(q[0]<lo||q[0]>hi)return; var xx=X(q[0]),yy=yc-q[1]/md*hg; pa+=(st?'L':'M')+xx.toFixed(1)+','+yy.toFixed(1); st=true; }); pa+='L'+X(Math.min(hi,r.kde[r.kde.length-1][0])).toFixed(1)+','+yc+'L'+X(Math.max(lo,r.kde[0][0])).toFixed(1)+','+yc+'Z'; svg.appendChild(E('path',{d:pa,fill:r.color,'fill-opacity':0.35,stroke:r.color,'stroke-width':1})); } }"
        "    var jh=Math.max(3,rh*0.32);"
        "    (r.samples||[]).forEach(function(sv,si){ if(sv<lo||sv>hi)return; var xx=X(sv), jy=yc-2-(((si*7)%Math.floor(jh))); svg.appendChild(E('circle',{cx:xx.toFixed(1),cy:jy.toFixed(1),r:1.1,fill:r.color,'fill-opacity':0.5})); });"
        "    var qa=X(Math.max(lo,r.q1)),qb=X(Math.min(hi,r.q3)); if(qb>qa) svg.appendChild(E('rect',{x:qa,y:yc-3,width:qb-qa,height:6,fill:'none',stroke:r.color,'stroke-width':1.2}));"
        "    var mx=X(Math.max(lo,Math.min(hi,r.median))); ln(mx,yc-5,mx,yc+5,r.color,2);"
        "    if(r.hasBefore){ var bx=X(Math.max(lo,Math.min(hi,r.before))); ln(bx,yc+4,bx,yc+10,'#94a3b8',1.5); }"
        "    tx(x1-1,yTop+rh*0.30,'n='+r.count,'#64748b',8,'end');"
        "  });"
        "  el.appendChild(svg);"
    ]

    let private pinKey (ScanPinId.ScanPinId g : ScanPinId) = g.ToString()

    let dock (env : Env<Message>) (model : AdaptiveModel) =
        let placement = model.ScanPins.Placement
        let selected  = model.ScanPins.SelectedPin
        let pinsVal   = model.ScanPins.Pins |> AMap.toAVal
        let effId     = ScanPinModel.effectivePinIdA placement selected
        let effPin    = (effId, pinsVal) ||> AVal.map2 (fun id pins -> id |> Option.bind (fun i -> HashMap.tryFind i pins))
        let hasPin    = effPin |> AVal.map Option.isSome
        let orderVal  = model.MeshOrder.Content
        let refMeshA  = model.Registration |> AVal.map (fun r -> r.ReferenceMesh)
        let meshNamesA = model.MeshNames |> AList.toAVal
        let previewOn = model.PendingReg |> AVal.map PendingRegistration.isPreview
        let corrA     = effPin |> AVal.map (Option.bind ScanPin.correspondence)

        let visibleMovingA =
            AVal.custom (fun t ->
                let names = meshNamesA.GetValue t |> IndexList.toList
                let vis = model.MeshVisible.GetValue t
                let rf = (model.Registration.GetValue t).ReferenceMesh
                names |> List.filter (fun n -> Some n <> rf && (Map.tryFind n vis |> Option.defaultValue true)))

        let beforeA = effPin |> AVal.map (Option.bind (fun p -> match p.Probe with ProbeReady r -> Some r | _ -> None))
        let afterA  = (effPin, previewOn) ||> AVal.map2 (fun po pv ->
                        po |> Option.bind (fun p -> match ScanPin.effectiveProbe pv p with ProbeReady r -> Some r | _ -> None))

        // ── B1 · identity ──────────────────────────────────────────────────
        let nameVal   = effPin |> AVal.map (Option.map (fun p -> p.Name) >> Option.defaultValue "")
        let radiusVal = effPin |> AVal.map (Option.map (fun p -> p.InnerRadius) >> Option.defaultValue 5.0)
        let corrEnabled = corrA |> AVal.map (function Some c -> c.Enabled | None -> false)
        // k/n counts in-ROI meshes only (§C): n = in-ROI moving meshes, k = those
        // with a placed marker; out-of-ROI meshes are excluded entirely.
        let inRoiOf (c : Correspondence option) (m : string) =
            match c with Some cc -> Map.tryFind m cc.InRoi |> Option.defaultValue true | None -> true
        let kn =
            AVal.custom (fun t ->
                let moving = visibleMovingA.GetValue t
                let c = corrA.GetValue t
                let inRoiMoving = moving |> List.filter (inRoiOf c)
                let k =
                    match c with
                    | Some cc -> inRoiMoving |> List.filter (fun m -> Map.containsKey m cc.Anchors) |> List.length
                    | None -> 0
                k, List.length inRoiMoving)
        let emitPin (mk : ScanPinId -> Message) = match AVal.force effId with Some id -> env.Emit [mk id] | None -> ()

        let identity =
            div {
                Class "ins-b1"
                input {
                    Class "ins-name"
                    Attribute("type", "text")
                    Attribute("title", "name")
                    nameVal |> AVal.map (fun n -> Some (Attribute("value", n)))
                    Dom.OnChange(fun e ->
                        match AVal.force effId with
                        | Some id -> env.Emit [RenamePin(id, e.Value)]
                        | None -> ())
                }
                inlineLogSlider "r" 0.01 10000.0 (sprintf "%.2f m") radiusVal (fun v ->
                    env.Emit [ScanPinMsg (SetInnerRadius v)])
                div {
                    Class "ins-b1-row"
                    span { Class "ins-kn"; kn |> AVal.map (fun (k, n) -> sprintf "k/n %d/%d" k n) }
                    button {
                        Class "mb"
                        corrEnabled |> AVal.map (fun e -> if e then Some (Class "mb-on") else None)
                        Attribute("title", "correspondence (promote / demote)")
                        Dom.OnClick(fun _ -> emitPin ToggleCorrespondence)
                        "⚲"
                    }
                    button {
                        Class "mb ins-del"
                        Attribute("title", "delete pin")
                        Dom.OnClick(fun _ -> emitPin (fun id -> ScanPinMsg (DeletePin id)))
                        "✕"
                    }
                }
            }

        // ── B2 · raincloud ─────────────────────────────────────────────────
        let rainData =
            AVal.custom (fun t ->
                rainJson (orderVal.GetValue t) (visibleMovingA.GetValue t)
                    (model.InspectorMesh.GetValue t) (beforeA.GetValue t) (afterA.GetValue t))
        let raincloud =
            div {
                Class "ins-b2"
                input {
                    Class "ins-rain-bus"
                    Attribute("type", "text")
                    Dom.OnInput(fun e ->
                        let parts = e.Value.Split('|')
                        if parts.Length = 2 && parts.[0] = "row" then
                            env.Emit [SetInspectorMesh (Some parts.[1])])
                }
                div {
                    Class "ins-rain"
                    rainData |> AVal.map (fun j -> Some (Attribute("data-rain", j)))
                    observedRender "data-rain" "{}" rainJs
                }
            }

        // ── B3 · correspondence readout (numbers only) ─────────────────────
        let b3 =
            div {
                Class "ins-b3"
                model.MeshNames |> AList.map (fun mesh ->
                    let isMoving = refMeshA |> AVal.map (fun r -> r <> Some mesh)
                    let isVis = model.MeshVisible |> AVal.map (fun m -> Map.tryFind mesh m |> Option.defaultValue true)
                    let show = (isMoving, isVis) ||> AVal.map2 (&&)
                    let placed = corrA |> AVal.map (Option.bind (fun c -> Map.tryFind mesh c.Anchors) >> Option.isSome)
                    let residual = corrA |> AVal.map (Option.bind (fun c -> Map.tryFind mesh c.Residuals))
                    let active = model.InspectorMesh |> AVal.map ((=) (Some mesh))
                    let colorVal = model.MeshOrder |> AMap.tryFind mesh |> AVal.map (Option.defaultValue 0 >> meshColor)
                    div {
                        Class "ins-b3-row"
                        showWhen show
                        active |> AVal.map (fun a -> if a then Some (Class "ins-b3-active") else None)
                        Dom.OnClick(fun _ -> env.Emit [SetInspectorMesh (Some mesh)])
                        span { Class "ins-sw"; colorVal |> AVal.map (fun c -> Some (Style [Css.Background (c4bToRgbCss c)])) }
                        span { Class "ins-b3-name"; orderVal |> AVal.map (fun o -> numbered o mesh) }
                        span {
                            Class "ins-b3-ok"
                            placed |> AVal.map (fun p -> Some (Class (if p then "ins-ok" else "ins-no")))
                            placed |> AVal.map (fun p -> if p then "✓" else "✗")
                        }
                        span {
                            Class "ins-b3-res"
                            residual |> AVal.map (function Some r -> sprintf "%.0f mm" (r * 1000.0) | None -> "—")
                        }
                    })
            }

        // ── B4 · intrinsic bars (active moving mesh) ───────────────────────
        let activeIntrinsics =
            AVal.custom (fun t ->
                let moving = visibleMovingA.GetValue t
                let act = model.InspectorMesh.GetValue t |> Option.filter (fun m -> List.contains m moving) |> Option.orElse (List.tryHead moving)
                match act, afterA.GetValue t with
                | Some m, Some r ->
                    r.Distributions |> Array.tryFind (fun d -> d.MeshName = m)
                    |> Option.map (fun d -> if d.Intrinsics.Length >= 3 then d.Intrinsics else [| 0.0; 0.0; 0.0 |])
                | _ -> None)
        let bar (letter : string) (idx : int) =
            div {
                Class "ins-bar-row"
                span { Class "ins-bar-id"; letter }
                div {
                    Class "ins-bar-track"
                    div {
                        Class "ins-bar-fill"
                        activeIntrinsics |> AVal.map (fun io ->
                            let v = io |> Option.map (fun a -> a.[idx]) |> Option.defaultValue 0.0
                            Some (Style [Width (sprintf "%.0f%%" (max 0.0 (min 1.0 v) * 100.0)); Css.Background (ramp v)]))
                    }
                }
                span {
                    Class "ins-bar-val"
                    activeIntrinsics |> AVal.map (function
                        | Some a -> sprintf "%.2f" a.[idx]
                        | None -> "—")
                }
            }
        let b4 =
            div {
                Class "ins-b4"
                bar "I" 0
                bar "R" 1
                bar "S" 2
            }

        // ── §F correspondence manager (Correspondences step) ──────────────
        let emit (m : Message) = env.Emit [m]
        let hoverEmit (m : string option) =
            match AVal.force effId with
            | Some id -> emit (SetCorrRowHover (m |> Option.map (fun mesh -> pinKey id, mesh)))
            | None -> emit (SetCorrRowHover None)
        let managerRow (mesh : string) =
            let isMoving = refMeshA |> AVal.map (fun r -> r <> Some mesh)
            let isVis = model.MeshVisible |> AVal.map (fun mp -> Map.tryFind mesh mp |> Option.defaultValue true)
            let show = (isMoving, isVis) ||> AVal.map2 (&&)
            let inRoi = corrA |> AVal.map (fun c -> inRoiOf c mesh)
            let placed = corrA |> AVal.map (Option.bind (fun c -> Map.tryFind mesh c.Anchors) >> Option.isSome)
            let stateGlyph = (inRoi, placed) ||> AVal.map2 (fun roi p -> if not roi then "⊘" elif p then "✓" else "○")
            let stateCls   = (inRoi, placed) ||> AVal.map2 (fun roi p -> Class (if not roi then "ins-st-out" elif p then "ins-st-ok" else "ins-st-mid"))
            let resOrSpread =
                corrA |> AVal.map (function
                    | Some cc ->
                        match Map.tryFind mesh cc.Residuals with
                        | Some r -> sprintf "%.0f mm" (r * 1000.0)
                        | None ->
                            match Map.tryFind mesh cc.Anchors, cc.RefAnchor with
                            | Some a, Some ra -> sprintf "%.0f mm" ((a.Point - ra).Length * 1000.0)
                            | _ -> "—"
                    | None -> "—")
            let active =
                AVal.custom (fun t ->
                    (match effId.GetValue t with Some id -> model.CorrRowHover.GetValue t = Some (pinKey id, mesh) | None -> false)
                    || model.InspectorMesh.GetValue t = Some mesh)
            let colorVal = model.MeshOrder |> AMap.tryFind mesh |> AVal.map (Option.defaultValue 0 >> meshColor)
            div {
                Class "ins-mgr-row"
                showWhen show
                inRoi |> AVal.map (fun roi -> if roi then None else Some (Class "ins-mgr-out"))
                active |> AVal.map (fun a -> if a then Some (Class "ins-mgr-active") else None)
                Dom.OnPointerMove(fun _ -> hoverEmit (Some mesh))
                Dom.OnMouseLeave(fun _ -> hoverEmit None)
                span { Class "ins-sw"; colorVal |> AVal.map (fun c -> Some (Style [Css.Background (c4bToRgbCss c)])) }
                span { Class "ins-mgr-name"; orderVal |> AVal.map (fun o -> numbered o mesh) }
                span { stateCls |> AVal.map Some; Class "ins-mgr-state"; stateGlyph }
                span { Class "ins-mgr-res"; resOrSpread }
                button {
                    Class "mb ins-mgr-act"
                    inRoi |> AVal.map (fun roi -> if roi then None else Some (Class "hidden"))
                    Attribute("title", "Re-seed this mesh")
                    Dom.OnClick(fun _ -> match AVal.force effId with Some id -> emit (ReseedMesh(id, mesh)) | None -> ())
                    "⟳"
                }
                button {
                    Class "mb ins-mgr-act"
                    inRoi |> AVal.map (fun roi -> if roi then None else Some (Class "hidden"))
                    Attribute("title", "Edit in the focus panel")
                    Dom.OnClick(fun _ -> emit (SetFocusMesh (Some mesh)))
                    "✎"
                }
            }
        let refColorA =
            AVal.custom (fun t ->
                match refMeshA.GetValue t with
                | Some r -> (match HashMap.tryFind r (orderVal.GetValue t) with Some i -> meshColor i | None -> meshColor 0)
                | None -> meshColor 0)
        let refRow =
            div {
                Class "ins-mgr-row ins-mgr-refrow"
                span { Class "ins-sw"; refColorA |> AVal.map (fun c -> Some (Style [Css.Background (c4bToRgbCss c)])) }
                span { Class "ins-mgr-name"
                       (refMeshA, orderVal) ||> AVal.map2 (fun r o -> match r with Some m -> numbered o m | None -> "— no reference") }
                span { Class "ins-mgr-state ins-st-ref"; "★" }
                span { Class "ins-mgr-res"; corrA |> AVal.map (function Some c when c.RefAnchor.IsSome -> "ref" | _ -> "…") }
            }
        let manager =
            div {
                Class "ins-mgr"
                refRow
                div { Class "ins-mgr-rows"; model.MeshNames |> AList.map managerRow }
                div {
                    Class "ins-mgr-foot"
                    span { Class "ins-kn"; kn |> AVal.map (fun (k, n) -> sprintf "k/n %d/%d" k n) }
                    button {
                        Class "rail-btn rail-btn-primary ins-solve"
                        previewOn |> AVal.map (fun p -> if p then Some (Attribute("disabled", "disabled")) else None)
                        Dom.OnClick(fun _ -> emit SolveCoarse)
                        "Solve coarse"
                    }
                }
            }

        // ── light step modes ──────────────────────────────────────────────
        let referenceMode =
            div {
                Class "ins-light"
                div {
                    Class "ins-light-row"
                    span { Class "ins-light-k"; "Reference" }
                    span { (refMeshA, orderVal) ||> AVal.map2 (fun r o -> match r with Some m -> numbered o m | None -> "— pick a ★ in the rail") }
                }
                div {
                    Class "ins-light-row"
                    span { Class "ins-light-k"; "Meshes" }
                    span { model.MeshNames.Content |> AVal.map (fun ns -> sprintf "%d loaded" (IndexList.count ns)) }
                }
            }
        let fineMode =
            div {
                Class "ins-light"
                div {
                    Class "ins-light-row"
                    span { Class "ins-light-k"; "RMS" }
                    span {
                        model.LastSolve |> AVal.map (fun ls ->
                            if Map.isEmpty ls then "no solve yet"
                            else
                                let bs = ls |> Map.toSeq |> Seq.map (fun (_, e) -> e.RmsBefore) |> Seq.toArray
                                let aft = ls |> Map.toSeq |> Seq.map (fun (_, e) -> e.RmsAfter) |> Seq.toArray
                                sprintf "%.0f → %.0f mm" (Array.average bs * 1000.0) (Array.average aft * 1000.0))
                    }
                }
                button {
                    Class "rail-btn ins-solve"
                    previewOn |> AVal.map (fun p -> if p then Some (Attribute("disabled", "disabled")) else None)
                    Dom.OnClick(fun _ -> emit RunRegistration)
                    "Run / re-run ICP"
                }
            }
        let commitMode =
            let pend = model.PendingReg |> AVal.map (function Some pr -> Map.count pr.Results | None -> 0)
            div {
                Class "ins-light"
                div {
                    Class "ins-light-row"
                    pend |> AVal.map (fun n -> if n > 0 then sprintf "Previewing %d mesh(es) — committed vs new pose" n else "Nothing pending — run a solve first")
                }
                div {
                    Class "ins-commit-row"
                    showWhen previewOn
                    button {
                        Class "rail-btn rail-btn-primary"
                        Dom.OnClick(fun _ -> emit CommitRegistration)
                        "Commit"
                    }
                    button {
                        Class "rail-btn"
                        Dom.OnClick(fun _ -> emit DiscardRegistration)
                        "Discard"
                    }
                }
            }

        // ── step-contextual assembly (cross-faded; v3 §A) ─────────────────
        let stepA = model.WorkflowStep
        let modeOn (pred : WorkflowStep -> bool) =
            stepA |> AVal.map (fun s -> if pred s then Some (Class "ins-mode-on") else None)
        let errorInspector =
            div {
                div { Class "ins-empty"; showWhenNot hasPin; span { "◌" } }
                div { Class "ins-body"; showWhen hasPin; identity; raincloud; b3; b4 }
            }
        div {
            Class "pin-inspector"
            div {
                Class "ins-header"
                span { Class "ins-mode-label"; stepA |> AVal.map WorkflowStep.mode }
            }
            div {
                Class "ins-modes"
                div { Class "ins-mode"; modeOn ((=) StepReference); referenceMode }
                div { Class "ins-mode"; modeOn (fun s -> s = StepManualMove || s = StepInspect); errorInspector }
                div {
                    Class "ins-mode"
                    modeOn ((=) StepCorrespondences)
                    div { Class "ins-empty"; showWhenNot hasPin; span { "◌ select a pin" } }
                    div { Class "ins-mgr-wrap"; showWhen hasPin; manager }
                }
                div { Class "ins-mode"; modeOn ((=) StepFine); fineMode }
                div { Class "ins-mode"; modeOn ((=) StepCommit); commitMode }
            }
        }
