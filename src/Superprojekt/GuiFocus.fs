namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open FSharp.Data.Adaptive
open Aardvark.Dom

// Right focus panel (spec v3): a persistent large single (the one secondary
// WebGL control) stacked over a canvas small-multiples strip, both driven by one
// projection selector. Context follows the workflow step — pick (step 2,
// textured + surface-click correspondence) vs compare (step 4, active §6 channel,
// reference-origin panorama). Layout never changes between steps; only content
// does. The multiples are pure 2D canvas (no extra WebGL control).
module GuiFocus =

    open Primitives

    // The compare-context server channel (spec §F int) the multiples + single use
    // for a moving mesh, plus the canvas colour kind. Pick → Shade; the reference
    // cell is always Shade (shaded-relief). Mirrors FocusView's channel choice.
    // Returns (channelInt, kind).
    let private compareChannel (model : AdaptiveModel) : int * string =
        if AVal.force model.SurfaceDistOn then
            (if AVal.force model.ExtrinsicZDiff then 2 else 1), "diverging"
        elif AVal.force model.VarianceOn then 1, "diverging"
        else
            match AVal.force model.HeatmapMode with
            | HeatIncidence -> 3, "quality"
            | HeatRange     -> 4, "quality"
            | HeatShape     -> 5, "quality"
            | HeatOff       -> 1, "diverging"

    let private pickRay (cursorPx : V2d) (vp : V2i) (view : Trafo3d) (proj : Trafo3d) =
        let ndc = V2d(2.0 * cursorPx.X / float vp.X - 1.0, 1.0 - 2.0 * cursorPx.Y / float vp.Y)
        let vpm = view * proj
        let p0 = vpm.Backward.TransformPosProj(V3d(ndc, -1.0))
        let p1 = vpm.Backward.TransformPosProj(V3d(ndc, 1.0))
        Ray3d(p0, (p1 - p0) |> Vec.normalize)

    // Project a render-space point to the large-single panel as (x%, y%), using
    // the same projection FocusProject.vertex draws with. None when off-frame.
    let private projectToFocus (renderP : V3d) (cam : FocusView.FocusCam) : (float * float) option =
        let ndc =
            if cam.Pano = 1 then
                let d = renderP - V3d cam.EyeRender
                let hyp = sqrt (d.X * d.X + d.Y * d.Y)
                let u = (if hyp < 1e-9 && abs d.Z < 1e-9 then 0.0 else atan2 d.Y d.X) / System.Math.PI
                let v = atan2 d.Z (max 1e-9 hyp) / (System.Math.PI * 0.5)
                Some (u, v)
            else
                let c = (cam.View * cam.Proj).Forward * V4d(renderP, 1.0)
                if c.W <= 1e-9 then None else Some (c.X / c.W, c.Y / c.W)
        ndc |> Option.bind (fun (u, v) ->
            if u < -1.05 || u > 1.05 || v < -1.05 || v > 1.05 then None
            else Some ((u + 1.0) * 50.0, (1.0 - v) * 50.0))

    // ── canvas small-multiples renderer ───────────────────────────────────
    let private multiplesJs = [
        "  function ph(t){ var p=document.createElement('div'); p.className='fm-ph'; p.textContent=t; el.appendChild(p); }"
        "  if(!d || !d.cells || d.cells.length===0){ ph('—'); return; }"
        "  function c01(x){ return x<0?0:(x>1?1:x); }"
        "  function lerp(a,b,t){ return 'rgb('+Math.round(a[0]+(b[0]-a[0])*t)+','+Math.round(a[1]+(b[1]-a[1])*t)+','+Math.round(a[2]+(b[2]-a[2])*t)+')'; }"
        "  function colShade(s){ var g=Math.round(40+200*c01(s)); return 'rgb('+g+','+g+','+g+')'; }"
        "  function colQuality(s){ return lerp([220,38,38],[22,163,74],c01(s)); }"
        "  function colVar(s,hi){ return lerp([241,245,249],[185,28,28],c01(s/Math.max(1e-6,hi))); }"
        "  function colDiv(s,hi,lod){ if(Math.abs(s)<lod) return '#f1f5f9'; var tt=Math.max(-1,Math.min(1,s/Math.max(1e-6,hi))); return tt>=0?lerp([241,245,249],[220,38,38],tt):lerp([241,245,249],[37,99,235],-tt); }"
        "  function colOf(cell,s){ if(Math.abs(s)>=1e20) return '#e2e8f0'; if(cell.kind==='shade') return colShade(s); if(cell.kind==='quality') return colQuality(s); if(cell.kind==='variance') return colVar(s,cell.hi); return colDiv(s,cell.hi,cell.lod||0); }"
        "  var W=el.clientWidth||300; var cells=d.cells; var n=cells.length;"
        "  var cols=Math.max(1,Math.min(n,Math.floor(W/96))); var cw=Math.floor(W/cols)-6; if(cw<70)cw=70; var ch=Math.round(cw*0.78);"
        "  var dpr=window.devicePixelRatio||1;"
        "  cells.forEach(function(cell){"
        "    var box=document.createElement('div'); box.className='fm-cell'+(cell.active?' fm-active':'');"
        "    box.style.width=cw+'px';"
        "    var cv=document.createElement('canvas'); cv.width=Math.round(cw*dpr); cv.height=Math.round(ch*dpr);"
        "    cv.style.width=cw+'px'; cv.style.height=ch+'px'; cv.className='fm-canvas';"
        "    var g=cv.getContext('2d'); g.setTransform(dpr,0,0,dpr,0,0);"
        "    g.fillStyle='#f8fafc'; g.fillRect(0,0,cw,ch);"
        "    var bb=d.box && d.shared ? d.box : cell.box;"
        "    var bw=Math.max(1e-6,bb[2]-bb[0]), bh=Math.max(1e-6,bb[3]-bb[1]);"
        "    var pad=4; var k=Math.min((cw-2*pad)/bw,(ch-2*pad)/bh); var ox=(cw-bw*k)/2, oy=(ch-bh*k)/2;"
        "    function X(u){ return ox+(u-bb[0])*k; } function Y(v){ return ch-(oy+(v-bb[1])*k); }"
        "    var v2=cell.v2, tr=cell.tris, s=cell.s;"
        "    for(var i=0;i+2<tr.length;i+=3){"
        "      var a=tr[i],b=tr[i+1],c=tr[i+2];"
        "      var sa=s[a],sb=s[b],sc=s[c];"
        "      var nd=(Math.abs(sa)>=1e20||Math.abs(sb)>=1e20||Math.abs(sc)>=1e20);"
        "      var sm=nd?1e30:(sa+sb+sc)/3; var col=colOf(cell,sm);"
        "      var x0=X(v2[2*a]),y0=Y(v2[2*a+1]),x1=X(v2[2*b]),y1=Y(v2[2*b+1]),x2=X(v2[2*c]),y2=Y(v2[2*c+1]);"
        "      g.beginPath(); g.moveTo(x0,y0); g.lineTo(x1,y1); g.lineTo(x2,y2); g.closePath();"
        "      g.fillStyle=col; g.fill(); g.strokeStyle=col; g.lineWidth=0.5; g.stroke();"
        "    }"
        // §E reference crosshair (opaque) + correspondence handle (mesh colour).
        "    if(cell.cross){ var cx=X(cell.cross[0]),cy=Y(cell.cross[1]); g.strokeStyle='#0f172a'; g.lineWidth=1.4; g.beginPath(); g.moveTo(cx-5,cy); g.lineTo(cx+5,cy); g.moveTo(cx,cy-5); g.lineTo(cx,cy+5); g.stroke(); }"
        "    if(cell.handle){ var hx=X(cell.handle[0]),hy=Y(cell.handle[1]),hr=cell.hover?5.2:3.4; g.beginPath(); g.arc(hx,hy,hr,0,6.2832); g.fillStyle=cell.color; g.fill(); g.strokeStyle=cell.hover?'#0891b2':'#fff'; g.lineWidth=cell.hover?2:1.2; g.stroke(); }"
        "    var lab=document.createElement('div'); lab.className='fm-label';"
        "    lab.innerHTML='<span class=\"fm-sw\" style=\"background:'+cell.color+'\"></span>'+cell.name;"
        "    box.appendChild(cv); box.appendChild(lab);"
        "    box.title='click → focus this mesh';"
        "    box.addEventListener('click',function(){ var bus=el.closest('.focus-panel'); bus=bus?bus.querySelector('.fm-bus'):null; if(bus){ bus.value='cell|'+cell.mesh; bus.dispatchEvent(new Event('input',{bubbles:true})); } });"
        "    el.appendChild(box);"
        "  });"
    ]

    let panel (env : Env<Message>) (model : AdaptiveModel) =
        let refMeshA = model.Registration |> AVal.map (fun r -> r.ReferenceMesh)
        let compareContext = model.WorkflowStep |> AVal.map ((=) StepInspect)
        // Manual move (step 2) = translate-drag; Correspondences (step 3) = edit
        // correspondence handles by surface-click / drag. Both are pick context.
        let manualMoveStep = model.WorkflowStep |> AVal.map ((=) StepManualMove)
        let corrStep = model.WorkflowStep |> AVal.map ((=) StepCorrespondences)
        // Latest rc client size (CSS px) for NDC math in the surface-pick / drag.
        let focusClientSizeCval = cval (V2i(1, 1))
        let focusClientSize = focusClientSizeCval :> aval<V2i>

        // Visible meshes (moving + reference) → multiples + focus default. A hard
        // solo in the main view falls back to its restore set so every cell stays.
        let visibleMeshes =
            AVal.custom (fun t ->
                let names = model.MeshNames.Content.GetValue t |> IndexList.toList
                let vis =
                    match model.MeshSolo.GetValue t with
                    | Solo(_, restore) -> restore
                    | NoSolo -> model.MeshVisible.GetValue t
                names |> List.filter (fun n -> Map.tryFind n vis |> Option.defaultValue true))

        // Effective focus mesh (defaults to the first visible when unset/invalid).
        let focusMesh =
            (model.FocusMesh, visibleMeshes) ||> AVal.map2 (fun fm vis ->
                match fm with
                | Some m when List.contains m vis -> Some m
                | _ -> List.tryHead vis)

        let camFor (name : string) = FocusView.cam model name compareContext
        let camA = focusMesh |> AVal.bind (function Some n -> camFor n | None -> AVal.constant { FocusView.View = Trafo3d.Identity; FocusView.Proj = Trafo3d.Identity; FocusView.EyeRender = V3f.Zero; FocusView.RangeRender = 1.0f; FocusView.Pano = 0 })
        // Peek-reference (§E): hold → render the reference geometry through the
        // focused mesh's own camera (juxtaposition in the same frame).
        let renderedMesh =
            (focusMesh, model.FocusPeekReference, corrStep, refMeshA)
            |> fun (fm, pk, cs, rf) ->
                AVal.custom (fun t ->
                    if pk.GetValue t && cs.GetValue t then rf.GetValue t else fm.GetValue t)

        // ── §E focus-2D handle + reference crosshair (Correspondences) ────────
        let pinsValF = model.ScanPins.Pins |> AMap.toAVal
        let effCorrA =
            (ScanPinModel.effectivePinIdA model.ScanPins.Placement model.ScanPins.SelectedPin, pinsValF)
            ||> AVal.map2 (fun id pins -> id |> Option.bind (fun i -> HashMap.tryFind i pins) |> Option.bind ScanPin.correspondence)
        let datasetScaleA = (model.ActiveDataset, model.DatasetScales) ||> AVal.map2 DatasetScale.active
        let toScreen (worldA : aval<V3d option>) =
            AVal.custom (fun t ->
                match worldA.GetValue t with
                | Some w ->
                    let cc = model.CommonCentroid.GetValue t
                    let s = datasetScaleA.GetValue t
                    projectToFocus (ScanPin.renderCentre cc s w) (camA.GetValue t)
                | None -> None)
        let handleWorld =
            (focusMesh, effCorrA) ||> AVal.map2 (fun fm c ->
                match fm, c with
                | Some m, Some cc when cc.Enabled && (Map.tryFind m cc.InRoi |> Option.defaultValue true) ->
                    Map.tryFind m cc.Anchors |> Option.map (fun a -> a.Point)
                | _ -> None)
        let refWorld = effCorrA |> AVal.map (Option.bind (fun c -> if c.Enabled then c.RefAnchor else None))
        let handleScreen = toScreen handleWorld
        let crossScreen  = toScreen refWorld
        let handleColorA =
            (focusMesh, model.MeshOrder.Content) ||> AVal.map2 (fun fm o ->
                match fm with Some m -> (match HashMap.tryFind m o with Some i -> c4bToHex (meshColor i) | None -> "#1a56db") | None -> "#1a56db")
        let posStyle = function
            | Some (x, y) -> Some (Style [Css.Left (sprintf "%.2f%%" x); Css.Top (sprintf "%.2f%%" y)])
            | None -> Some (Class "hidden")
        let handleOverlay =
            div {
                Class "focus-handles"
                corrStep |> AVal.map (fun on -> if on then None else Some (Class "hidden"))
                div { Class "fh-cross"; crossScreen |> AVal.map posStyle }
                div {
                    Class "fh-handle"
                    handleScreen |> AVal.map posStyle
                    handleColorA |> AVal.map (fun c -> Some (Style [Css.Background c]))
                    (focusMesh, model.CorrRowHover) ||> AVal.map2 (fun fm h ->
                        match fm, h with Some m, Some (_, hm) when m = hm -> Some (Class "fh-hot") | _ -> None)
                }
            }

        // Drag (ortho translate, step 2) + click (surface-pick correspondence).
        let downPos : cval<V2d option> = cval None
        let lastPos : cval<V2d option> = cval None
        let dragged : cval<bool> = cval false

        // Screen right/up axes (render space) for the three ortho projections.
        let screenAxes = function
            | ProjTop   -> V3d.IOO, V3d.OIO
            | ProjFront -> V3d.IOO, V3d.OOI
            | ProjSide  -> V3d.OIO, V3d.OOI
            | ProjPano  -> V3d.Zero, V3d.Zero

        // Resolve a focus-panel click to a 3D surface point on the focused mesh
        // via the server raycast, then set/update the focused mesh's
        // correspondence for the selected pin (spec §D step 2).
        let pickAt (clickPx : V2d) =
            match AVal.force focusMesh, AVal.force refMeshA with
            | Some mesh, Some refMesh when mesh <> refMesh ->
                match ScanPinModel.effectivePinIdA model.ScanPins.Placement model.ScanPins.SelectedPin |> AVal.force with
                | None -> ()
                | Some pinId ->
                    let cam = AVal.force camA
                    let loaded = MeshView.loadMeshAsync (fun _ -> ()) mesh
                    let ft = AVal.force (FocusView.fullTrafo model loaded mesh)
                    let centroid = AVal.force loaded.centroid
                    let scale = DatasetScale.forMesh (AVal.force model.DatasetScales) mesh
                    let cc = AVal.force model.CommonCentroid
                    let renderRay =
                        match AVal.force model.FocusProjection with
                        | ProjPano ->
                            // invert the cylindrical projection: NDC (u,v) → (φ,θ).
                            let cs = AVal.force focusClientSize
                            let ndc = V2d(2.0 * clickPx.X / float (max 1 cs.X) - 1.0, 1.0 - 2.0 * clickPx.Y / float (max 1 cs.Y))
                            let phi = ndc.X * System.Math.PI
                            let theta = ndc.Y * System.Math.PI * 0.5
                            let dir = V3d(cos theta * cos phi, cos theta * sin phi, sin theta)
                            Ray3d(V3d cam.EyeRender, dir.Normalized)
                        | _ ->
                            let cs = AVal.force focusClientSize
                            pickRay clickPx cs cam.View cam.Proj
                    let ownOrigin = ft.Backward.TransformPos renderRay.Origin + centroid
                    let ownDir = (ft.Backward.TransformDir renderRay.Direction).Normalized
                    let renderT = AVal.force (MeshView.effectiveMeshT model mesh)
                    let w = RigidTransform.renderToWorld scale cc renderT
                    async {
                        let! hit = Query.rayHit ApiConfig.apiBase.Value mesh 0 ownOrigin ownDir
                        match hit with
                        | Some h -> env.Emit [PickCorrespondenceAt(pinId, mesh, w.Forward.TransformPos h.point)]
                        | None -> ()
                    } |> Async.Start
            | _ -> ()

        let rc =
            renderControl {
                RenderControl.Samples 1
                Class "focus-rc"
                let! client = RenderControl.ClientSize

                Sg.View (camA |> AVal.map (fun c -> c.View))
                Sg.Proj (camA |> AVal.map (fun c -> c.Proj))
                Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
                Sg.BlendMode (AVal.constant BlendMode.Blend)

                RenderControl.OnRendered(fun _ ->
                    let s = AVal.force client
                    if focusClientSizeCval.Value <> s && s.X > 1 && s.Y > 1 then
                        transact (fun () -> focusClientSizeCval.Value <- s))

                Dom.OnPointerDown((fun e ->
                    if e.Button = Button.Left then
                        let p = V2d(float e.OffsetPosition.X, float e.OffsetPosition.Y)
                        transact (fun () -> downPos.Value <- Some p; lastPos.Value <- Some p; dragged.Value <- false)),
                    pointerCapture = true)
                Dom.OnPointerUp((fun e ->
                    let wasDragged = dragged.Value
                    if downPos.Value.IsSome then
                        transact (fun () -> downPos.Value <- None; lastPos.Value <- None; dragged.Value <- false)
                    // Correspondences: a click or a press-drag-release places the
                    // focused mesh's marker at the release point (§E editor).
                    ignore wasDragged
                    if AVal.force corrStep then
                        pickAt (V2d(float e.OffsetPosition.X, float e.OffsetPosition.Y))),
                    pointerCapture = true)
                Dom.OnPointerMove(fun e ->
                    match lastPos.Value with
                    | Some prev ->
                        let cur = V2d(float e.OffsetPosition.X, float e.OffsetPosition.Y)
                        let d = cur - prev
                        // Ortho translate-align only (step 2, focused moving mesh).
                        let proj = AVal.force model.FocusProjection
                        let inStep2 = AVal.force manualMoveStep
                        if inStep2 && proj <> ProjPano && d.Length > 0.5 then
                            transact (fun () -> lastPos.Value <- Some cur; dragged.Value <- true)
                            let cam = AVal.force camA
                            let cs = AVal.force focusClientSize
                            let h = max 1.0 (float cs.Y)
                            // ortho half-extent from the proj trafo (1 / proj.M11).
                            let half = 1.0 / (abs cam.Proj.Forward.M11)
                            let u = 2.0 * half / h
                            let right, upv = screenAxes proj
                            let delta = right * (d.X * u) - upv * (d.Y * u)
                            if delta.Length > 1e-9 then env.Emit [TranslateAlignMesh delta]
                        elif d.Length > 0.5 then
                            transact (fun () -> lastPos.Value <- Some cur)
                    | None -> ())

                FocusView.buildSingle model renderedMesh focusMesh compareContext camFor
            }

        let projBtn (p : FocusProjection) =
            button {
                Class "focus-proj-btn"
                model.FocusProjection |> AVal.map (fun a -> if a = p then Some (Class "btn-active") else None)
                Dom.OnClick(fun _ -> env.Emit [SetFocusProjection p])
                FocusProjection.label p
            }

        // ── multiples JSON (one cell per visible mesh) ─────────────────────
        let multiplesData =
            AVal.custom (fun t ->
                let inv = System.Globalization.CultureInfo.InvariantCulture
                let g (v : float) = if System.Double.IsNaN v || System.Double.IsInfinity v then "0" else v.ToString("0.######", inv)
                let vis = visibleMeshes.GetValue t
                let maps = model.FocusMaps.GetValue t
                let order = model.MeshOrder.Content.GetValue t
                let rf = (model.Registration.GetValue t).ReferenceMesh
                let fm = focusMesh.GetValue t
                let cmp = compareContext.GetValue t
                // §E correspondence handles + reference crosshair per cell, in the
                // cell's own server frame (own-origin pano / world ortho).
                let editing = corrStep.GetValue t
                let hoverMesh = model.CorrRowHover.GetValue t |> Option.map snd
                let proj = model.FocusProjection.GetValue t
                let cc = model.CommonCentroid.GetValue t
                let corr = effCorrA.GetValue t
                let refW = corr |> Option.bind (fun c -> if c.Enabled then c.RefAnchor else None)
                let eyeOf m =
                    let s = DatasetScale.forMesh (model.DatasetScales.GetValue t) m
                    let crt = Map.tryFind m (model.MeshTransforms.GetValue t) |> Option.defaultValue Trafo3d.Identity
                    let centroid = Map.tryFind m (model.DatasetCentroids.GetValue t) |> Option.defaultValue V3d.Zero
                    (RigidTransform.renderToWorld s cc crt).Forward.TransformPos centroid
                let projPt (m : string) (w : V3d) =
                    let halfPi = System.Math.PI * 0.5
                    match proj with
                    | ProjTop   -> w.X, w.Y
                    | ProjFront -> w.X, w.Z
                    | ProjSide  -> w.Y, w.Z
                    | ProjPano  ->
                        let d = w - eyeOf m
                        let hyp = sqrt (d.X * d.X + d.Y * d.Y)
                        (if hyp < 1e-9 && abs d.Z < 1e-9 then 0.0 else atan2 d.Y d.X) / System.Math.PI,
                        atan2 d.Z (max 1e-9 hyp) / halfPi
                let markerOf m =
                    corr |> Option.bind (fun c ->
                        if c.Enabled && (Map.tryFind m c.InRoi |> Option.defaultValue true)
                        then Map.tryFind m c.Anchors |> Option.map (fun a -> a.Point) else None)
                // shared symmetric/sequential domain from the moving cells.
                let movingHi =
                    vis |> List.choose (fun m ->
                        if Some m = rf then None
                        else Map.tryFind m maps |> Option.map (fun p -> max (abs p.Lo) (abs p.Hi)))
                let sharedHi = if List.isEmpty movingHi then 1.0 else List.max movingHi |> max 1e-3
                // Detection-limit neutral band (extrinsic): the selected pin's
                // reference σ pooled with the focus mesh σ (1.96·√(σ_ref²+σ_M²)).
                let pinsNow = AVal.force (model.ScanPins.Pins |> AMap.toAVal)
                let lod =
                    match (model.ScanPins.SelectedPin.GetValue t) |> Option.bind (fun id -> HashMap.tryFind id pinsNow) with
                    | Some p ->
                        match p.Probe with
                        | ProbeReady r ->
                            let stdOf m = r.Distributions |> Array.tryFind (fun d -> d.MeshName = m) |> Option.map (fun d -> d.Std) |> Option.defaultValue 0.0
                            let refStd = stdOf r.ReferenceMesh
                            let mStd = fm |> Option.map stdOf |> Option.defaultValue 0.0
                            1.96 * sqrt (refStd * refStd + mStd * mStd)
                        | _ -> 0.0
                    | None -> 0.0
                let movingKind = let _, k = compareChannel model in k
                let cells =
                    vis |> List.choose (fun m ->
                        match Map.tryFind m maps with
                        | None -> None
                        | Some p ->
                            let isRef = Some m = rf
                            let kind = if not cmp || isRef then "shade" else movingKind
                            let lo, hi = if kind = "shade" then 0.0, 1.0 else 0.0, sharedHi
                            // per-cell bbox of verts2d
                            let mutable x0 = infinity
                            let mutable y0 = infinity
                            let mutable x1 = -infinity
                            let mutable y1 = -infinity
                            let nv = p.Verts2d.Length / 2
                            for k in 0 .. nv - 1 do
                                let u = p.Verts2d.[2*k]
                                let v = p.Verts2d.[2*k+1]
                                if u < x0 then x0 <- u
                                if u > x1 then x1 <- u
                                if v < y0 then y0 <- v
                                if v > y1 then y1 <- v
                            let col = match HashMap.tryFind m order with Some i -> c4bToHex (meshColor i) | None -> "#1a56db"
                            let sb = System.Text.StringBuilder()
                            sb.Append(sprintf "{\"mesh\":\"%s\",\"name\":\"%s\",\"color\":\"%s\",\"active\":%b,\"kind\":\"%s\",\"lo\":%s,\"hi\":%s,\"lod\":%s,\"box\":[%s,%s,%s,%s],\"v2\":["
                                        m (numbered order m) col (fm = Some m) kind (g lo) (g hi) (g lod)
                                        (g x0) (g y0) (g x1) (g y1)) |> ignore
                            p.Verts2d |> Array.iteri (fun j v -> (if j > 0 then sb.Append ',' |> ignore); sb.Append(g v) |> ignore)
                            sb.Append "],\"tris\":[" |> ignore
                            p.Tris |> Array.iteri (fun j v -> (if j > 0 then sb.Append ',' |> ignore); sb.Append(string v) |> ignore)
                            sb.Append "],\"s\":[" |> ignore
                            p.Scalar |> Array.iteri (fun j v -> (if j > 0 then sb.Append ',' |> ignore); sb.Append(g v) |> ignore)
                            sb.Append "]" |> ignore
                            // §E handle (this mesh's marker) + reference crosshair,
                            // projected into the cell's own frame; only while editing.
                            let pt2 (uv : float * float) = let u, v = uv in sprintf "[%s,%s]" (g u) (g v)
                            let handleJ =
                                if editing && not isRef then
                                    match markerOf m with Some w -> pt2 (projPt m w) | None -> "null"
                                else "null"
                            let crossJ =
                                if editing && not isRef then
                                    match refW with Some w -> pt2 (projPt m w) | None -> "null"
                                else "null"
                            sb.Append(sprintf ",\"handle\":%s,\"cross\":%s,\"hover\":%b}" handleJ crossJ (hoverMesh = Some m)) |> ignore
                            Some (m, sb.ToString()))
                // shared frame bbox (compare): union of all cell boxes so a feature
                // lands at the same screen location across cells.
                let union =
                    let mutable x0 = infinity
                    let mutable y0 = infinity
                    let mutable x1 = -infinity
                    let mutable y1 = -infinity
                    for m in vis do
                        match Map.tryFind m maps with
                        | Some p ->
                            let nv = p.Verts2d.Length / 2
                            for k in 0 .. nv - 1 do
                                let u = p.Verts2d.[2*k]
                                let v = p.Verts2d.[2*k+1]
                                if u < x0 then x0 <- u
                                if u > x1 then x1 <- u
                                if v < y0 then y0 <- v
                                if v > y1 then y1 <- v
                        | None -> ()
                    if x0 <= x1 then sprintf "[%s,%s,%s,%s]" (g x0) (g y0) (g x1) (g y1) else "null"
                if List.isEmpty cells then "{\"cells\":[]}"
                else sprintf "{\"shared\":%b,\"box\":%s,\"cells\":[%s]}" cmp union (cells |> List.map snd |> String.concat ","))

        // Peek-reference modifier (§E): press-and-hold flips the large single to
        // the reference mesh, textured, in the same frame (juxtaposition).
        let peekBtn =
            button {
                Class "focus-peek"
                corrStep |> AVal.map (fun on -> if on then None else Some (Class "hidden"))
                Attribute("title", "Hold to peek the reference mesh in this frame")
                Dom.OnPointerDown((fun _ -> env.Emit [SetFocusPeekReference true]), pointerCapture = true)
                Dom.OnPointerUp((fun _ -> env.Emit [SetFocusPeekReference false]), pointerCapture = true)
                "⇄ ref"
            }

        div {
            Class "focus-panel"
            div {
                Class "focus-head"
                span { Class "focus-title"; "Focus" }
                div {
                    Class "focus-proj"
                    projBtn ProjPano; projBtn ProjTop; projBtn ProjFront; projBtn ProjSide
                }
                peekBtn
            }
            div { Class "focus-single"; rc; handleOverlay }
            input {
                Class "fm-bus"
                Attribute("type", "text")
                Dom.OnInput(fun e ->
                    let parts = e.Value.Split('|')
                    if parts.Length = 2 && parts.[0] = "cell" then env.Emit [SetFocusMesh (Some parts.[1])])
            }
            div {
                Class "focus-multiples"
                multiplesData |> AVal.map (fun j -> Some (Attribute("data-focus", j)))
                observedRender "data-focus" "{}" multiplesJs
            }
        }
