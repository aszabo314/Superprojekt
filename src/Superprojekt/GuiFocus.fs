namespace Superprojekt

open Aardvark.Base
open FSharp.Data.Adaptive
open Aardvark.Dom

// Right focus panel: canvas-only (the main 3D viewport is the only WebGL
// control). A large single (focused mesh, pan + zoom, draggable correspondence
// handles) over a small-multiples strip, one tile per visible mesh. The Pano /
// Top projection toggle drives both. Context follows the mode: pick (shaded
// relief) vs compare (Inspect, active error channel).
module GuiFocus =

    open Primitives

    // Inspect focus-tile server channel + canvas colour kind for a moving mesh.
    // Difference = signed M3C2 (1) / vertical Δz (2), diverging; displacement =
    // arrow glyphs (channel 6, Task 5). Pick context + the reference cell are Shade.
    let private compareChannel (model : AdaptiveModel) : int * string =
        match AVal.force model.InspectChannel with
        | ChDifference   -> (if AVal.force model.ExtrinsicZDiff then 2 else 1), "diverging"
        | ChDisplacement -> 6, "disp"

    // Shared cell renderer (used by the multiples strip and the large single).
    let private cellDrawJs = [
        "  function c01(x){ return x<0?0:(x>1?1:x); }"
        "  function lerp(a,b,t){ return 'rgb('+Math.round(a[0]+(b[0]-a[0])*t)+','+Math.round(a[1]+(b[1]-a[1])*t)+','+Math.round(a[2]+(b[2]-a[2])*t)+')'; }"
        "  function colShade(s){ var g=Math.round(40+200*c01(s)); return 'rgb('+g+','+g+','+g+')'; }"
        "  function colQuality(s){ return lerp([220,38,38],[22,163,74],c01(s)); }"
        "  function colVar(s,hi){ return lerp([241,245,249],[185,28,28],c01(s/Math.max(1e-6,hi))); }"
        "  function colDiv(s,hi,lod){ if(Math.abs(s)<lod) return '#f1f5f9'; var tt=Math.max(-1,Math.min(1,s/Math.max(1e-6,hi))); return tt>=0?lerp([241,245,249],[220,38,38],tt):lerp([241,245,249],[37,99,235],-tt); }"
        "  function colOf(cell,s){ if(Math.abs(s)>=1e20) return '#e2e8f0'; if(cell.kind==='shade') return colShade(s); if(cell.kind==='quality') return colQuality(s); if(cell.kind==='variance') return colVar(s,cell.hi); return colDiv(s,cell.hi,cell.lod||0); }"
        // Displacement glyphs: load→solved arrows, sequential blue by |d|/magHi.
        "  function drawArrows(g,cell,X,Y){"
        "    if(!cell.arrows) return; var mh=cell.magHi||1e-6;"
        "    for(var i=0;i<cell.arrows.length;i++){ var ar=cell.arrows[i];"
        "      var bx=X(ar[0]),by=Y(ar[1]),tx=X(ar[2]),ty=Y(ar[3]); var tc=Math.max(0,Math.min(1,ar[4]/mh));"
        "      var col=lerp([191,219,254],[7,42,107],tc); g.strokeStyle=col; g.fillStyle=col; g.lineWidth=1.2;"
        "      g.beginPath(); g.moveTo(bx,by); g.lineTo(tx,ty); g.stroke();"
        "      var an=Math.atan2(ty-by,tx-bx),hl=4;"
        "      g.beginPath(); g.moveTo(tx,ty); g.lineTo(tx-hl*Math.cos(an-0.5),ty-hl*Math.sin(an-0.5)); g.lineTo(tx-hl*Math.cos(an+0.5),ty-hl*Math.sin(an+0.5)); g.closePath(); g.fill(); }"
        "  }"
        "  function drawCell(g,cell,X,Y){"
        "    var v2=cell.v2, tr=cell.tris, s=cell.s; var disp=cell.kind==='disp';"
        "    for(var i=0;i+2<tr.length;i+=3){ var a=tr[i],b=tr[i+1],c=tr[i+2]; var col;"
        "      if(disp){ col='#ffffff'; } else { var sa=s[a],sb=s[b],sc=s[c]; var nd=(Math.abs(sa)>=1e20||Math.abs(sb)>=1e20||Math.abs(sc)>=1e20); var sm=nd?1e30:(sa+sb+sc)/3; col=colOf(cell,sm); }"
        "      var x0=X(v2[2*a]),y0=Y(v2[2*a+1]),x1=X(v2[2*b]),y1=Y(v2[2*b+1]),x2=X(v2[2*c]),y2=Y(v2[2*c+1]);"
        "      g.beginPath(); g.moveTo(x0,y0); g.lineTo(x1,y1); g.lineTo(x2,y2); g.closePath();"
        "      g.fillStyle=col; g.fill(); g.strokeStyle=disp?'#e2e8f0':col; g.lineWidth=0.5; g.stroke(); }"
        "    if(disp){ drawArrows(g,cell,X,Y); }"
        "    if(cell.cross){ var cx=X(cell.cross[0]),cy=Y(cell.cross[1]); g.strokeStyle='#0f172a'; g.lineWidth=1.6; g.beginPath(); g.moveTo(cx-6,cy); g.lineTo(cx+6,cy); g.moveTo(cx,cy-6); g.lineTo(cx,cy+6); g.stroke(); }"
        "    if(cell.handle){ var hx=X(cell.handle[0]),hy=Y(cell.handle[1]),hr=cell.hover?6:4; g.beginPath(); g.arc(hx,hy,hr,0,6.2832); g.fillStyle=cell.color; g.fill(); g.strokeStyle=cell.hover?'#0891b2':'#fff'; g.lineWidth=cell.hover?2.4:1.4; g.stroke(); }"
        "  }"
    ]

    let private multiplesJs =
        cellDrawJs @ [
        "  function ph(t){ var p=document.createElement('div'); p.className='fm-ph'; p.textContent=t; el.appendChild(p); }"
        "  if(!d || !d.cells || d.cells.length===0){ ph('—'); return; }"
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
        "    drawCell(g,cell,X,Y);"
        "    var lab=document.createElement('div'); lab.className='fm-label';"
        "    lab.innerHTML='<span class=\"fm-sw\" style=\"background:'+cell.color+'\"></span>'+cell.name;"
        "    box.appendChild(cv); box.appendChild(lab);"
        "    box.title='click → focus this mesh';"
        "    box.addEventListener('click',function(){ var bus=el.closest('.focus-panel'); bus=bus?bus.querySelector('.fm-bus'):null; if(bus){ bus.value='cell|'+cell.mesh; bus.dispatchEvent(new Event('input',{bubbles:true})); } });"
        "    el.appendChild(box);"
        "  });"
        ]

    // Large single: pan/zoom state is kept JS-local on el.__fsv (not the reducer).
    let private singleJs =
        cellDrawJs @ [
        "  function ph(t){ var p=document.createElement('div'); p.className='fm-ph fm-ph-big'; p.textContent=t; el.appendChild(p); }"
        "  if(!d || !d.cell){ ph('select a mesh'); return; }"
        "  var cell=d.cell; var editing=!!d.editing;"
        "  var W=el.clientWidth||320, H=el.clientHeight||220; var dpr=window.devicePixelRatio||1;"
        "  var cv=document.createElement('canvas'); cv.width=Math.round(W*dpr); cv.height=Math.round(H*dpr);"
        "  cv.style.width=W+'px'; cv.style.height=H+'px'; cv.className='fs-canvas-el';"
        "  var g=cv.getContext('2d');"
        "  var st=el.__fsv||(el.__fsv={z:1,tx:0,ty:0,key:''});"
        "  if(st.key!==cell.mesh){ st.z=1; st.tx=0; st.ty=0; st.key=cell.mesh; }"
        "  var bb=cell.box; var bw=Math.max(1e-6,bb[2]-bb[0]), bh=Math.max(1e-6,bb[3]-bb[1]);"
        "  var pad=12, k0=Math.min((W-2*pad)/bw,(H-2*pad)/bh);"
        "  function k(){ return k0*st.z; }"
        "  function ox(){ return (W-bw*k())/2 + st.tx; } function oy(){ return (H-bh*k())/2 + st.ty; }"
        "  function X(u){ return ox()+(u-bb[0])*k(); } function Y(v){ return H-(oy()+(v-bb[1])*k()); }"
        "  function invX(px){ return bb[0]+(px-ox())/k(); } function invY(py){ return bb[1]+((H-py)-oy())/k(); }"
        "  function draw(){ g.setTransform(dpr,0,0,dpr,0,0); g.fillStyle='#f8fafc'; g.fillRect(0,0,W,H); drawCell(g,cell,X,Y);"
        "    if(cell.kind==='disp' && cell.magHi){ var rl=Math.min(k()*cell.magHi,W*0.4); if(rl<8)rl=8; var rx=12,ry=H-14; g.strokeStyle='#334155'; g.fillStyle='#334155'; g.lineWidth=1.5; g.beginPath(); g.moveTo(rx,ry); g.lineTo(rx+rl,ry); g.stroke(); g.beginPath(); g.moveTo(rx+rl,ry); g.lineTo(rx+rl-5,ry-3); g.lineTo(rx+rl-5,ry+3); g.closePath(); g.fill(); g.font='10px SF Mono,Monaco,monospace'; g.fillText('↔ '+(cell.magHi*1000).toFixed(0)+' mm',rx,ry-4); }"
        "    g.fillStyle='#475569'; g.font='11px SF Mono,Monaco,monospace'; g.fillText(cell.name+(editing?'  ✎ click to place':'')+(d.zlabel||''),8,16); }"
        "  draw();"
        "  function near(px,py){ if(!cell.handle) return false; var dx=px-X(cell.handle[0]),dy=py-Y(cell.handle[1]); return dx*dx+dy*dy<100; }"
        "  var mode=null,sx=0,sy=0,otx=0,oty=0;"
        "  cv.addEventListener('wheel',function(e){ e.preventDefault(); var r=cv.getBoundingClientRect(); var px=e.clientX-r.left,py=e.clientY-r.top; var u=invX(px),v=invY(py); var f=e.deltaY<0?1.15:1/1.15; st.z=Math.max(0.2,Math.min(40,st.z*f)); st.tx=px-(W/2)-(u-bb[0])*k()+bw*k()/2; st.ty=(H/2)-(H-py)-(v-bb[1])*k()+bh*k()/2; draw(); },{passive:false});"
        "  cv.addEventListener('pointerdown',function(e){ var r=cv.getBoundingClientRect(); sx=e.clientX-r.left; sy=e.clientY-r.top; otx=st.tx; oty=st.ty; cv.setPointerCapture(e.pointerId); if(editing && e.button===0 && !e.shiftKey){ mode='pick'; } else { mode='pan'; } });"
        "  cv.addEventListener('pointermove',function(e){ if(!mode)return; var r=cv.getBoundingClientRect(); var px=e.clientX-r.left,py=e.clientY-r.top; if(mode==='pan'){ st.tx=otx+(px-sx); st.ty=oty-(py-sy); draw(); } else if(mode==='pick'){ cell.handle=[invX(px),invY(py)]; cell.hover=true; draw(); } });"
        "  cv.addEventListener('pointerup',function(e){ if(mode==='pick'){ var r=cv.getBoundingClientRect(); var px=e.clientX-r.left,py=e.clientY-r.top; var u=invX(px),v=invY(py); var bus=el.closest('.focus-panel'); bus=bus?bus.querySelector('.fs-bus'):null; if(bus){ bus.value='pick|'+u+'|'+v; bus.dispatchEvent(new Event('input',{bubbles:true})); } } mode=null; });"
        "  el.appendChild(cv);"
        ]

    let panel (env : Env<Message>) (model : AdaptiveModel) =
        let refMeshA = model.Registration |> AVal.map (fun r -> r.ReferenceMesh)
        let compareContext = model.WorkflowStep |> AVal.map ((=) Inspect)
        let corrStep = model.WorkflowStep |> AVal.map ((=) Correspondence)
        // Displacement forces the Oblique projection, so the Pano/Top selector is
        // replaced by a fixed chip in that channel.
        let displacementActive =
            (model.WorkflowStep, model.InspectChannel) ||> AVal.map2 (fun s c -> s = Inspect && c = ChDisplacement)

        // A hard solo in the main view falls back to its restore set so every
        // cell stays present.
        let visibleMeshes =
            AVal.custom (fun t ->
                let names = model.MeshNames.Content.GetValue t |> IndexList.toList
                let vis =
                    match model.MeshSolo.GetValue t with
                    | Solo(_, restore) -> restore
                    | NoSolo -> model.MeshVisible.GetValue t
                names |> List.filter (fun n -> Map.tryFind n vis |> Option.defaultValue true))

        // Effective focus mesh (defaults to the first visible when unset/invalid);
        // shownMesh swaps to the reference while peek-reference is held.
        let focusMesh =
            (model.Selection.FocusedMesh, visibleMeshes) ||> AVal.map2 (fun fm vis ->
                match fm with
                | Some m when List.contains m vis -> Some m
                | _ -> List.tryHead vis)
        let shownMesh =
            (focusMesh, model.FocusPeekReference, refMeshA)
            |||> AVal.map3 (fun fm pk rf -> if pk then rf else fm)

        let effCorrA =
            (ScanPinModel.effectivePinIdA model.ScanPins.Placement model.Selection.SelectedPin,
             model.ScanPins.Pins |> AMap.toAVal)
            ||> AVal.map2 (fun id pins -> id |> Option.bind (fun i -> HashMap.tryFind i pins) |> Option.bind ScanPin.correspondence)

        let dispRender (t : AdaptiveToken) (mesh : string) =
            match model.RegView.GetValue t, Map.tryFind mesh (model.SolvedTransforms.GetValue t) with
            | RegAfter, Some s -> s
            | _ -> Map.tryFind mesh (model.LoadTransforms.GetValue t) |> Option.defaultValue Trafo3d.Identity

        let g (inv : System.Globalization.CultureInfo) (v : float) =
            if System.Double.IsNaN v || System.Double.IsInfinity v then "0" else v.ToString("0.######", inv)

        // One JSON cell per mesh, projected in the cell's own server frame
        // (own-origin pano / world ortho).
        let cellJson (t : AdaptiveToken) (inv : System.Globalization.CultureInfo)
                     (cmp : bool) (editing : bool) (sharedHi : float) (lod : float) (movingKind : string) (m : string) =
            let maps = model.FocusMaps.GetValue t
            match Map.tryFind m maps with
            | None -> None
            | Some p ->
                let g = g inv
                let order = model.MeshOrder.Content.GetValue t
                let rf = (model.Registration.GetValue t).ReferenceMesh
                let proj = model.FocusProjection.GetValue t
                let cc = model.CommonCentroid.GetValue t
                let corr = effCorrA.GetValue t
                let refW = corr |> Option.bind (fun c -> if c.Enabled then c.RefAnchor else None)
                let hoverMesh = model.Selection.Hovered.GetValue t |> function Some (HoverPoint (_, hm)) -> Some hm | _ -> None
                let eyeOf mesh =
                    let s = DatasetScale.forMesh (model.DatasetScales.GetValue t) mesh
                    let centroid = Map.tryFind mesh (model.DatasetCentroids.GetValue t) |> Option.defaultValue V3d.Zero
                    (RigidTransform.renderToWorld s cc (dispRender t mesh)).Forward.TransformPos centroid
                let projPt (mesh : string) (w : V3d) =
                    let halfPi = System.Math.PI * 0.5
                    match proj with
                    | ProjTop  -> w.X, w.Y
                    | ProjOblique -> (w.X - w.Y) * 0.86602540378, w.Z + (w.X + w.Y) * 0.5
                    | ProjPano ->
                        let d = w - eyeOf mesh
                        let hyp = sqrt (d.X * d.X + d.Y * d.Y)
                        (if hyp < 1e-9 && abs d.Z < 1e-9 then 0.0 else atan2 d.Y d.X) / System.Math.PI,
                        atan2 d.Z (max 1e-9 hyp) / halfPi
                let markerOf mesh =
                    corr |> Option.bind (fun c ->
                        if c.Enabled && (Map.tryFind mesh c.InRoi |> Option.defaultValue true)
                        then Map.tryFind mesh c.Anchors |> Option.map (fun a ->
                                (RigidTransform.renderToWorld (DatasetScale.forMesh (model.DatasetScales.GetValue t) mesh) cc (dispRender t mesh)).Forward.TransformPos a.Point)
                        else None)
                let isRef = Some m = rf
                let kind = if not cmp || isRef then "shade" else movingKind
                let lo, hi = if kind = "shade" then 0.0, 1.0 else 0.0, sharedHi
                let mutable x0 = infinity
                let mutable y0 = infinity
                let mutable x1 = -infinity
                let mutable y1 = -infinity
                let nv = p.Verts2d.Length / 2
                for kk in 0 .. nv - 1 do
                    let u = p.Verts2d.[2*kk]
                    let v = p.Verts2d.[2*kk+1]
                    if u < x0 then x0 <- u
                    if u > x1 then x1 <- u
                    if v < y0 then y0 <- v
                    if v > y1 then y1 <- v
                let col = match HashMap.tryFind m order with Some i -> c4bToHex (meshColor i) | None -> "#1a56db"
                let sb = System.Text.StringBuilder()
                sb.Append(sprintf "{\"mesh\":\"%s\",\"name\":\"%s\",\"color\":\"%s\",\"active\":%b,\"kind\":\"%s\",\"lo\":%s,\"hi\":%s,\"lod\":%s,\"box\":[%s,%s,%s,%s],\"v2\":["
                            m (numbered order m) col (focusMesh.GetValue t = Some m) kind (g lo) (g hi) (g lod)
                            (g x0) (g y0) (g x1) (g y1)) |> ignore
                p.Verts2d |> Array.iteri (fun j v -> (if j > 0 then sb.Append ',' |> ignore); sb.Append(g v) |> ignore)
                sb.Append "],\"tris\":[" |> ignore
                p.Tris |> Array.iteri (fun j v -> (if j > 0 then sb.Append ',' |> ignore); sb.Append(string v) |> ignore)
                sb.Append "],\"s\":[" |> ignore
                p.Scalar |> Array.iteri (fun j v -> (if j > 0 then sb.Append ',' |> ignore); sb.Append(g v) |> ignore)
                sb.Append "]" |> ignore
                // Displacement arrows (base→tip 2D, magnitude) + shared magnitude
                // scale; only the displacement kind carries them.
                if kind = "disp" && p.DispMag.Length > 0 then
                    sb.Append(sprintf ",\"magHi\":%s,\"arrows\":[" (g sharedHi)) |> ignore
                    for i in 0 .. p.DispMag.Length - 1 do
                        if i > 0 then sb.Append ',' |> ignore
                        sb.Append(sprintf "[%s,%s,%s,%s,%s]"
                                    (g p.DispBase.[2*i]) (g p.DispBase.[2*i+1])
                                    (g p.DispTip.[2*i])  (g p.DispTip.[2*i+1]) (g p.DispMag.[i])) |> ignore
                    sb.Append "]" |> ignore
                let pt2 (uv : float * float) = let u, v = uv in sprintf "[%s,%s]" (g u) (g v)
                let handleJ = if editing && not isRef then (match markerOf m with Some w -> pt2 (projPt m w) | None -> "null") else "null"
                let crossJ  = if editing && not isRef then (match refW with Some w -> pt2 (projPt m w) | None -> "null") else "null"
                sb.Append(sprintf ",\"handle\":%s,\"cross\":%s,\"hover\":%b}" handleJ crossJ (hoverMesh = Some m)) |> ignore
                Some (sb.ToString())

        let sharedHiOf (t : AdaptiveToken) (vis : string list) (rf : string option) =
            let maps = model.FocusMaps.GetValue t
            let movingHi =
                vis |> List.choose (fun m ->
                    if Some m = rf then None
                    else Map.tryFind m maps |> Option.map (fun p -> max (abs p.Lo) (abs p.Hi)))
            if List.isEmpty movingHi then 1.0 else List.max movingHi |> max 1e-3
        // Shared displacement magnitude scale — only the solved moving meshes carry
        // a magnitude (server Hi = max |displacement|).
        let magHiOf (t : AdaptiveToken) (vis : string list) =
            let maps = model.FocusMaps.GetValue t
            let solved = model.SolvedTransforms.GetValue t
            let his =
                vis |> List.choose (fun m ->
                    if Map.containsKey m solved then Map.tryFind m maps |> Option.map (fun p -> abs p.Hi) else None)
            if List.isEmpty his then 1e-3 else List.max his |> max 1e-3
        let lodOf (t : AdaptiveToken) =
            let pinsNow = (model.ScanPins.Pins |> AMap.toAVal).GetValue t
            match (model.Selection.SelectedPin.GetValue t) |> Option.bind (fun id -> HashMap.tryFind id pinsNow) with
            | Some p ->
                match p.Probe with
                | ProbeReady r ->
                    let stdOf m = r.Distributions |> Array.tryFind (fun d -> d.MeshName = m) |> Option.map (fun d -> d.Std) |> Option.defaultValue 0.0
                    let refStd = stdOf r.ReferenceMesh
                    let mStd = focusMesh.GetValue t |> Option.map stdOf |> Option.defaultValue 0.0
                    1.96 * sqrt (refStd * refStd + mStd * mStd)
                | _ -> 0.0
            | None -> 0.0

        let multiplesData =
            AVal.custom (fun t ->
                let inv = System.Globalization.CultureInfo.InvariantCulture
                let g = g inv
                let vis = visibleMeshes.GetValue t
                let rf = (model.Registration.GetValue t).ReferenceMesh
                let cmp = compareContext.GetValue t
                let editing = corrStep.GetValue t
                let movingKind = let _, k = compareChannel model in k
                let sharedHi = if movingKind = "disp" then magHiOf t vis else sharedHiOf t vis rf
                let lod = lodOf t
                let cells = vis |> List.choose (cellJson t inv cmp editing sharedHi lod movingKind)
                let maps = model.FocusMaps.GetValue t
                let union =
                    let mutable x0 = infinity
                    let mutable y0 = infinity
                    let mutable x1 = -infinity
                    let mutable y1 = -infinity
                    for m in vis do
                        match Map.tryFind m maps with
                        | Some p ->
                            let nv = p.Verts2d.Length / 2
                            for kk in 0 .. nv - 1 do
                                let u = p.Verts2d.[2*kk]
                                let v = p.Verts2d.[2*kk+1]
                                if u < x0 then x0 <- u
                                if u > x1 then x1 <- u
                                if v < y0 then y0 <- v
                                if v > y1 then y1 <- v
                        | None -> ()
                    if x0 <= x1 then sprintf "[%s,%s,%s,%s]" (g x0) (g y0) (g x1) (g y1) else "null"
                if List.isEmpty cells then "{\"cells\":[]}"
                else sprintf "{\"shared\":%b,\"box\":%s,\"cells\":[%s]}" cmp union (cells |> String.concat ","))

        let singleData =
            AVal.custom (fun t ->
                let inv = System.Globalization.CultureInfo.InvariantCulture
                match shownMesh.GetValue t with
                | None -> "{}"
                | Some m ->
                    let vis = visibleMeshes.GetValue t
                    let rf = (model.Registration.GetValue t).ReferenceMesh
                    let cmp = compareContext.GetValue t
                    let editing = corrStep.GetValue t && Some m <> rf && not (model.FocusPeekReference.GetValue t)
                    let movingKind = let _, k = compareChannel model in k
                    let sharedHi = if movingKind = "disp" then magHiOf t vis else sharedHiOf t vis rf
                    let lod = lodOf t
                    match cellJson t inv cmp editing sharedHi lod movingKind m with
                    | Some c -> sprintf "{\"editing\":%b,\"proj\":%d,\"cell\":%s}" editing (FocusProjection.toInt (model.FocusProjection.GetValue t)) c
                    | None -> "{}")

        // Surface pick: invert the 2D frame coord to a world ray, then server
        // raycast. The ray is handed to the server in the mesh's own untransformed
        // frame; the resulting anchor is stored mesh-local.
        let pickAt (u : float) (v : float) =
            match AVal.force focusMesh, AVal.force refMeshA with
            | Some mesh, Some refM when mesh <> refM && not (AVal.force model.FocusPeekReference) ->
                match ScanPinModel.effectivePinIdA model.ScanPins.Placement model.Selection.SelectedPin |> AVal.force with
                | None -> ()
                | Some pinId ->
                    let scale = DatasetScale.forMesh (AVal.force model.DatasetScales) mesh
                    let cc = AVal.force model.CommonCentroid
                    let disp =
                        match AVal.force model.RegView, Map.tryFind mesh (AVal.force model.SolvedTransforms) with
                        | RegAfter, Some s -> s
                        | _ -> Map.tryFind mesh (AVal.force model.LoadTransforms) |> Option.defaultValue Trafo3d.Identity
                    let dw = RigidTransform.renderToWorld scale cc disp
                    let originW, dirW =
                        match AVal.force model.FocusProjection with
                        // Oblique never carries a correspondence pick (Inspect-only),
                        // but the match must be total; treat like the Top drop.
                        | ProjTop | ProjOblique -> V3d(u, v, 1.0e7), V3d(0.0, 0.0, -1.0)
                        | ProjPano ->
                            let centroid = Map.tryFind mesh (AVal.force model.DatasetCentroids) |> Option.defaultValue V3d.Zero
                            let eye = dw.Forward.TransformPos centroid
                            let phi = u * System.Math.PI
                            let theta = v * System.Math.PI * 0.5
                            eye, (V3d(cos theta * cos phi, cos theta * sin phi, sin theta)).Normalized
                    let ownO = dw.Backward.TransformPos originW
                    let ownD = (dw.Backward.TransformDir dirW).Normalized
                    async {
                        let! hit = Query.rayHit ApiConfig.apiBase.Value mesh 0 ownO ownD
                        match hit with
                        | Some h -> env.Emit [PickCorrespondenceAt(pinId, mesh, dw.Forward.TransformPos h.point)]
                        | None -> ()
                    } |> Async.Start
            | _ -> ()

        let projBtn (p : FocusProjection) =
            button {
                Class "focus-proj-btn"
                model.FocusProjection |> AVal.map (fun a -> if a = p then Some (Class "btn-active") else None)
                Dom.OnClick(fun _ -> env.Emit [SetFocusProjection p])
                FocusProjection.label p
            }

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
                    showWhenNot displacementActive
                    projBtn ProjPano; projBtn ProjTop
                }
                span { Class "focus-proj-fixed"; showWhen displacementActive; "Oblique" }
                peekBtn
            }
            div {
                Class "focus-single"
                singleData |> AVal.map (fun j -> Some (Attribute("data-single", j)))
                observedRender "data-single" "{}" singleJs
            }
            input {
                Class "fm-bus"
                Attribute("type", "text")
                Dom.OnInput(fun e ->
                    let parts = e.Value.Split('|')
                    if parts.Length = 2 && parts.[0] = "cell" then env.Emit [SetFocusedMesh (Some parts.[1])])
            }
            input {
                Class "fs-bus"
                Attribute("type", "text")
                Dom.OnInput(fun e ->
                    let parts = e.Value.Split('|')
                    if parts.Length = 3 && parts.[0] = "pick" then
                        let pf (s : string) = match System.Double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture) with true, v -> Some v | _ -> None
                        match pf parts.[1], pf parts.[2] with
                        | Some u, Some v -> pickAt u v
                        | _ -> ())
            }
            div {
                Class "focus-multiples"
                multiplesData |> AVal.map (fun j -> Some (Attribute("data-focus", j)))
                observedRender "data-focus" "{}" multiplesJs
            }
        }
