namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open FSharp.Data.Adaptive
open Aardvark.Dom

module CardsPin =

    let shortName (name : string) =
        let mesh =
            let s = name.IndexOf('/')
            if s >= 0 then name.[s + 1 ..] else name
        if mesh.Length > 8 && mesh.[8] = '_' then
            let date = mesh.[..7]
            let si = mesh.LastIndexOf("_seg")
            if si > 0 then date + "_" + mesh.[si + 1 ..] else date
        else mesh

    // Prefix the mesh's stable 1-based order number (matches the panel palette index) — names are easy to confuse.
    let numbered (order : HashMap<string, int>) (name : string) =
        match HashMap.tryFind name order with
        | Some i -> sprintf "%d  %s" (i + 1) (shortName name)
        | None -> shortName name

    let c4bToHex (c : C4b) =
        sprintf "#%02x%02x%02x" c.R c.G c.B

    // preview = probe under effective preview transforms while a solve is pending — rows become paired half-violins (committed left, preview right) with a median-shift arrow.
    let probeRidgeJson (mini : bool) (brushOn : bool) (sticky : string option) (colors : Map<string, C4b>) (order : HashMap<string, int>) (preview : ProbeResult option) (r : ProbeResult) =
        // y-range always auto; columns always sorted by significance.
        let win =
            match preview with
            | Some p -> Range1d(min r.XAuto.Min p.XAuto.Min, max r.XAuto.Max p.XAuto.Max)
            | None -> r.XAuto
        let colorHex name =
            match Map.tryFind name colors with
            | Some c -> c4bToHex c
            | None -> "#1a56db"
        let rows =
            r.Distributions |> Array.sortBy (fun d -> (if d.Count = 0 then 1 else 0), abs d.Median)
        let appendKde (sb : System.Text.StringBuilder) (kde : (float * float)[]) =
            kde |> Array.iteri (fun j (x, y) ->
                if j > 0 then sb.Append(',') |> ignore
                sb.Append(sprintf "[%.4g,%.4g]" x y) |> ignore)
        let appendSamples (sb : System.Text.StringBuilder) (s : float[]) =
            s |> Array.iteri (fun j x ->
                if j > 0 then sb.Append(',') |> ignore
                sb.Append(sprintf "%.4g" x) |> ignore)
        // σ_ref = reference roughness (std of re-centred distances); feeds the per-mesh band lod95 = 1.96·√(σ_ref² + σ_mesh²).
        let refStd =
            r.Distributions |> Array.tryFind (fun d -> d.MeshName = r.ReferenceMesh)
            |> Option.map (fun d -> d.Std) |> Option.defaultValue 0.0
        let sb = System.Text.StringBuilder()
        sb.Append(sprintf "{\"status\":\"ready\",\"mini\":%b,\"brushon\":%b,\"ymin\":%.5g,\"ymax\":%.5g,\"refstd\":%.5g,\"sticky\":\"%s\",\"rows\":["
                    mini brushOn win.Min win.Max refStd (sticky |> Option.defaultValue "")) |> ignore
        // F5: flag a surface caught only by the long 20 m cylinder as non-local (axial offset = RefOffset + median), not real local disagreement.
        let halfLen = r.Length * 0.5
        rows |> Array.iteri (fun i d ->
            if i > 0 then sb.Append(',') |> ignore
            let far =
                d.MeshName <> r.ReferenceMesh && halfLen > 1e-6
                && abs (d.Median + r.RefOffset) > 0.6 * halfLen
            sb.Append(sprintf "{\"id\":\"%s\",\"name\":\"%s\",\"color\":\"%s\",\"count\":%d,\"median\":%.5g,\"q1\":%.5g,\"q3\":%.5g,\"std\":%.5g,\"far\":%b,\"samples\":["
                        d.MeshName (numbered order d.MeshName) (colorHex d.MeshName) d.Count d.Median d.Q1 d.Q3 d.Std far) |> ignore
            appendSamples sb d.Samples
            sb.Append("],\"kde\":[") |> ignore
            appendKde sb d.Kde
            sb.Append("]") |> ignore
            match preview |> Option.bind (fun p -> p.Distributions |> Array.tryFind (fun pd -> pd.MeshName = d.MeshName)) with
            | Some pd when pd.Count > 0 ->
                sb.Append(sprintf ",\"count2\":%d,\"median2\":%.5g,\"q12\":%.5g,\"q32\":%.5g,\"kde2\":["
                            pd.Count pd.Median pd.Q1 pd.Q3) |> ignore
                appendKde sb pd.Kde
                sb.Append("]") |> ignore
            | _ -> ()
            sb.Append("}") |> ignore)
        sb.Append("]}") |> ignore
        sb.ToString()

    let probeStateJson (mini : bool) (brushOn : bool) (sticky : string option) (colors : Map<string, C4b>) (order : HashMap<string, int>) (preview : ProbeResult option) (probe : ProbeState) =
        match probe with
        | ProbeReady r -> probeRidgeJson mini brushOn sticky colors order preview r
        | ProbeError e -> sprintf "{\"status\":\"error\",\"reason\":\"%s\"}" (e.Replace("\\", "/").Replace("\"", "'"))
        | ProbeNone | ProbeRunning -> "{\"status\":\"running\"}"

    let private parseInvariant (s : string) =
        match System.Double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture) with
        | true, v -> Some v
        | _ -> None

    // ── Correspondence detail view (orthographic, SVG) ────────────────────
    // World geometry + per-marker symbolic patch → JSON; the SVG renderer (JS
    // below) only projects / pans / zooms. Positions are emitted RELATIVE to the
    // frame centroid (the look-at) so JSON stays compact + precise despite the
    // huge UTM origin; directions (strike/down/north) are absolute unit vectors.
    let private buildDetailJson
            (pin : ScanPin) (c : Correspondence) (order : HashMap<string,int>)
            (grids : Map<string, ElevGridState>) (trans : Map<string, Trafo3d>)
            (pending : PendingRegistration option) (refMesh : string option)
            (cc : V3d) (scales : Map<string, float>) : string =
        let committedRender mesh = Map.tryFind mesh trans |> Option.defaultValue Trafo3d.Identity
        let effRender mesh =
            let cm = committedRender mesh
            match PendingRegistration.delta mesh pending with
            | Some d -> RegLog.effective cm d
            | None -> cm
        let toWorld mesh (r : Trafo3d) = RigidTransform.renderToWorld (DatasetScale.forMesh scales mesh) cc r
        let committedWorld mesh = toWorld mesh (committedRender mesh)
        let effWorld mesh = toWorld mesh (effRender mesh)
        // own-frame point (committed pose) → current pose (committed ∘ pending)
        let curWorld mesh (p : V3d) =
            let own = (committedWorld mesh).Backward.TransformPos p
            (effWorld mesh).Forward.TransformPos own
        match c.RefAnchor, refMesh with
        | Some ra, Some rm ->
            let refWorld = curWorld rm ra
            let entries =
                let movers =
                    c.Anchors |> Map.toList
                    |> List.filter (fun (m, _) -> m <> rm)
                    |> List.map (fun (m, a) -> m, curWorld m a.Point, false)
                (rm, refWorld, true) :: movers
            let worlds = entries |> List.map (fun (_, w, _) -> w) |> Array.ofList
            let centroid = (worlds |> Array.fold (+) V3d.Zero) / float (max 1 worlds.Length)
            let az = DetailViewMath.sideAzimuth worlds
            let north = V3d.OIO
            let inv = System.Globalization.CultureInfo.InvariantCulture
            let g (v : float) = if System.Double.IsNaN v || System.Double.IsInfinity v then "0" else v.ToString("0.######", inv)
            let rel (v : V3d) = let r = v - centroid in sprintf "%s,%s,%s" (g r.X) (g r.Y) (g r.Z)
            let dir (v : V3d) = sprintf "%s,%s,%s" (g v.X) (g v.Y) (g v.Z)
            let sb = System.Text.StringBuilder()
            sb.Append(sprintf "{\"status\":\"ready\",\"az\":%s,\"north\":[%s],\"markers\":[" (g az) (dir north)) |> ignore
            entries |> List.iteri (fun i (mesh, world, isRef) ->
                if i > 0 then sb.Append(',') |> ignore
                let euclid, vert, horiz, azv = DetailViewMath.markerMetrics refWorld world (Some north)
                let colorHex = match Map.tryFind mesh pin.DatasetColors with Some col -> c4bToHex col | None -> "#1a56db"
                sb.Append(sprintf "{\"key\":\"%s\",\"name\":\"%s\",\"color\":\"%s\",\"ref\":%b,\"world\":[%s],\"euclid\":%s,\"vert\":%s,\"horiz\":%s,\"az\":%s"
                            mesh (numbered order mesh) colorHex isRef (rel world)
                            (g euclid) (g vert) (g horiz)
                            (if System.Double.IsNaN azv then "null" else g (azv * 180.0 / System.Math.PI))) |> ignore
                match Map.tryFind mesh grids with
                | Some (GridReady grid) ->
                    let patch = DetailViewMath.symbolicPatch grid (effWorld mesh)
                    sb.Append(sprintf ",\"dip\":%s,\"strike\":[%s],\"down\":[%s],\"zmin\":%s,\"zmax\":%s,\"contours\":["
                                (g (patch.DipRad * 180.0 / System.Math.PI)) (dir patch.StrikeDir) (dir patch.DownSlope)
                                (g (patch.ZMin - centroid.Z)) (g (patch.ZMax - centroid.Z))) |> ignore
                    patch.Contours |> Array.iteri (fun j s ->
                        if j > 0 then sb.Append(',') |> ignore
                        sb.Append(sprintf "[%s,%s]" (rel s.A) (rel s.B)) |> ignore)
                    let polys (ps : V3d[][]) =
                        ps |> Array.iteri (fun j poly ->
                            if j > 0 then sb.Append(',') |> ignore
                            sb.Append('[') |> ignore
                            poly |> Array.iteri (fun k p -> (if k > 0 then sb.Append(',') |> ignore); sb.Append(rel p) |> ignore)
                            sb.Append(']') |> ignore)
                    sb.Append("],\"ridges\":[") |> ignore
                    polys patch.Ridges
                    sb.Append("],\"valleys\":[") |> ignore
                    polys patch.Valleys
                    sb.Append("]") |> ignore
                | _ -> ()
                sb.Append("}") |> ignore)
            sb.Append("]}") |> ignore
            sb.ToString()
        | _ -> "{\"status\":\"noref\"}"

    // SVG renderer: builds toolbar + ortho viewport + values table inside the
    // host; camera (view / azimuth / pan / zoom) is JS-local on el.__dv so
    // pan/zoom never touch the reducer. Row/glyph hover posts to .pc-detail-bus.
    let private detailJs = [
        "  function ph(t){ var p=document.createElement('div'); p.className='pin-card-empty'; p.textContent=t; el.appendChild(p); }"
        "  if(!d || d.status !== 'ready'){ ph(d && d.status==='noref' ? 'Designate a ★ reference mesh to measure against.' : 'No correspondence markers yet.'); return; }"
        "  var markers = d.markers || [];"
        "  if(markers.length === 0){ ph('No correspondence markers yet.'); return; }"
        "  function send(s){ var r=el.closest('.pc-detail'); var b=r?r.querySelector('.pc-detail-bus'):null; if(b){ b.value=s; b.dispatchEvent(new Event('input',{bubbles:true})); } }"
        "  function cross(a,b){ return [a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0]]; }"
        "  function dot(a,b){ return a[0]*b[0]+a[1]*b[1]+a[2]*b[2]; }"
        "  function norm(a){ var l=Math.hypot(a[0],a[1],a[2])||1; return [a[0]/l,a[1]/l,a[2]/l]; }"
        "  function niceStep(raw){ if(!(raw>0)) return 1; var p=Math.floor(Math.log10(raw)), b=Math.pow(10,p), c=[1,2,5,10]; for(var i=0;i<4;i++){ if(c[i]*b>=raw-1e-12) return c[i]*b; } return 10*b; }"
        "  function hx(c){ return [parseInt(c.substr(1,2),16),parseInt(c.substr(3,2),16),parseInt(c.substr(5,2),16)]; }"
        "  function shadeCol(rgb,z,zmin,zmax,wt){ var t=(zmax>zmin)?(z-zmin)/(zmax-zmin):0.5; t=Math.max(0,Math.min(1,t)); var s=(0.55+0.7*t)*(wt||1); function cl(v){return Math.max(0,Math.min(255,Math.round(v*s)));} return 'rgb('+cl(rgb[0])+','+cl(rgb[1])+','+cl(rgb[2])+')'; }"
        // camera state persists across re-renders; markers-changed clears userAdjusted
        "  var sig = markers.map(function(m){ return m.key+':'+m.world.map(function(v){return v.toFixed(2);}).join(','); }).join('|');"
        "  var st = el.__dv = el.__dv || {view:'side', az:d.az, el:0.0, panX:0, panY:0, zoom:2.0, userAdjusted:false, hover:null, sig:''};"
        "  if(st.sig !== sig){ st.sig = sig; st.userAdjusted = false; st.az = d.az; }"
        // DOM scaffold
        "  var tb=document.createElement('div'); tb.className='pc-detail-tb';"
        "  function refreshTB(){ var bs=tb.querySelectorAll('.pc-detail-vbtn'); ['side','top','free'].forEach(function(v,i){ if(bs[i]) bs[i].className='pc-detail-vbtn'+(st.view===v?' btn-active':''); }); }"
        "  ['side','top','free'].forEach(function(v){ var bb=document.createElement('button'); bb.className='pc-detail-vbtn'+(st.view===v?' btn-active':''); bb.textContent=v.charAt(0).toUpperCase()+v.slice(1); bb.onclick=function(){ st.view=v; if(!st.userAdjusted) autofit(); refreshTB(); draw(); }; tb.appendChild(bb); });"
        "  var rb=document.createElement('button'); rb.className='pc-detail-vbtn pc-detail-reset'; rb.textContent='Reset'; rb.onclick=function(){ st.userAdjusted=false; st.az=d.az; st.el=0; autofit(); refreshTB(); draw(); }; tb.appendChild(rb);"
        "  el.appendChild(tb);"
        "  var box=document.createElement('div'); box.className='pc-detail-box'; el.appendChild(box);"
        "  var svg=document.createElementNS(ns,'svg'); svg.setAttribute('class','pc-detail-svg'); box.appendChild(svg);"
        "  var tbl=document.createElement('div'); tbl.className='pc-detail-table'; el.appendChild(tbl);"
        // camera math
        "  var Wd=300, Hd=220, b=null, ppm=1;"
        "  function basis(){ var up=[0,0,1];"
        "    if(st.view==='top'){ var vd=[0,0,-1]; var r=norm(cross(vd,[0,1,0])); return {r:r,u:norm(cross(r,vd))}; }"
        "    if(st.view==='free'){ var ce=Math.cos(st.el),se=Math.sin(st.el); var vd=[ce*Math.cos(st.az),ce*Math.sin(st.az),se]; var r=norm(cross(vd,up)); return {r:r,u:norm(cross(r,vd))}; }"
        "    var vd=[Math.cos(st.az),Math.sin(st.az),0]; var r=norm(cross(vd,up)); return {r:r,u:norm(cross(r,vd))}; }"
        "  function setupGeom(){ b=basis(); Wd=svg.clientWidth||300; Hd=svg.clientHeight||220; ppm=(Hd*0.5)/st.zoom; }"
        "  function proj(P){ var sx=dot(P,b.r), sy=dot(P,b.u); return [Wd*0.5+(sx-st.panX)*ppm, Hd*0.5-(sy-st.panY)*ppm]; }"
        "  function projDir(V){ return [dot(V,b.r), -dot(V,b.u)]; }"
        "  function autofit(){ b=basis(); var W2=svg.clientWidth||300, H2=svg.clientHeight||220; var mnx=1e9,mxx=-1e9,mny=1e9,mxy=-1e9;"
        "    function acc(P){ var sx=dot(P,b.r), sy=dot(P,b.u); if(sx<mnx)mnx=sx; if(sx>mxx)mxx=sx; if(sy<mny)mny=sy; if(sy>mxy)mxy=sy; }"
        "    markers.forEach(function(m){ acc(m.world); (m.contours||[]).forEach(function(s){ acc([s[0],s[1],s[2]]); acc([s[3],s[4],s[5]]); }); });"
        "    if(mxx<mnx){ mnx=-2;mxx=2;mny=-2;mxy=2; }"
        "    st.panX=(mnx+mxx)/2; st.panY=(mny+mxy)/2; var hw=(mxx-mnx)/2||1, hh=(mxy-mny)/2||1, asp=W2/H2;"
        "    var z=Math.max(hh, hw/asp)/0.8; st.zoom=Math.max(0.25,Math.min(50, z>0?z:2)); }"
        // svg primitives
        "  function mk(tag,a){ var e=document.createElementNS(ns,tag); for(var k in a){ e.setAttribute(k,a[k]); } return e; }"
        "  function ln(x1,y1,x2,y2,col,w,extra){ var a={x1:x1,y1:y1,x2:x2,y2:y2,stroke:col,'stroke-width':w,'stroke-linecap':'round'}; if(extra) for(var k in extra) a[k]=extra[k]; svg.appendChild(mk('line',a)); }"
        "  function txt(x,y,s,col,size,anchor){ var t0=mk('text',{x:x,y:y,'font-size':size,'text-anchor':anchor||'middle',fill:'none',stroke:'#fff','stroke-width':3,'stroke-linejoin':'round'}); t0.textContent=s; svg.appendChild(t0); var t=mk('text',{x:x,y:y,'font-size':size,'text-anchor':anchor||'middle',fill:col}); t.textContent=s; svg.appendChild(t); }"
        "  function drawPoly(flat,rgb,zmin,zmax,w,wt){ for(var i=0;i+5<flat.length;i+=3){ var a=proj([flat[i],flat[i+1],flat[i+2]]); var bb=proj([flat[i+3],flat[i+4],flat[i+5]]); var zc=(flat[i+2]+flat[i+5])/2; ln(a[0],a[1],bb[0],bb[1], shadeCol(rgb,zc,zmin,zmax,wt), w); } }"
        "  function draw(){ setupGeom(); while(svg.firstChild) svg.removeChild(svg.firstChild);"
        "    var ref=markers.filter(function(m){return m.ref;})[0]; var rp=ref?proj(ref.world):[Wd/2,Hd/2];"
        // symbolic lines (under glyphs)
        "    markers.forEach(function(m){ var rgb=hx(m.color), zmin=m.zmin, zmax=m.zmax;"
        "      (m.contours||[]).forEach(function(s){ var a=proj([s[0],s[1],s[2]]), bb=proj([s[3],s[4],s[5]]); var zc=(s[2]+s[5])/2; ln(a[0],a[1],bb[0],bb[1], shadeCol(rgb,zc,zmin,zmax,1), 1.25); });"
        "      (m.valleys||[]).forEach(function(p){ drawPoly(p,rgb,zmin,zmax,2.0,0.85); });"
        "      (m.ridges||[]).forEach(function(p){ drawPoly(p,rgb,zmin,zmax,2.0,1.15); }); });"
        // measurement lines + labels (not in Free)
        "    if(st.view!=='free'){ markers.forEach(function(m){ if(m.ref) return; var p=proj(m.world); ln(rp[0],rp[1],p[0],p[1],'rgba(15,23,42,0.45)',1,{'stroke-dasharray':'3,3'}); txt((rp[0]+p[0])/2,(rp[1]+p[1])/2-2, m.euclid.toFixed(3)+' m','#0f172a',10); }); }"
        // glyphs + strike/dip
        "    markers.forEach(function(m){ var p=proj(m.world); var hovd=(st.hover===m.key); var col=m.color;"
        "      if(m.ref){ svg.appendChild(mk('circle',{cx:p[0],cy:p[1],r:6,fill:'none',stroke:col,'stroke-width':hovd?2.6:1.8})); ln(p[0]-7,p[1],p[0]+7,p[1],col,hovd?2:1.4); ln(p[0],p[1]-7,p[0],p[1]+7,col,hovd?2:1.4); }"
        "      else { svg.appendChild(mk('circle',{cx:p[0],cy:p[1],r:hovd?6:4.5,fill:col,stroke:'#fff','stroke-width':1.6})); }"
        "      if(m.strike){ var sd=projDir(m.strike); var sl=Math.hypot(sd[0],sd[1])||1; var ux=sd[0]/sl, uy=sd[1]/sl; ln(p[0]-ux*10,p[1]-uy*10,p[0]+ux*10,p[1]+uy*10,col,2); if(m.down){ var dd=projDir(m.down); var dl=Math.hypot(dd[0],dd[1]); if(dl>1e-6){ ln(p[0],p[1],p[0]+dd[0]/dl*7,p[1]+dd[1]/dl*7,col,2); if(m.dip!=null) txt(p[0]+dd[0]/dl*16,p[1]+dd[1]/dl*16, m.dip.toFixed(0)+'°', col, 9); } } } });"
        // callouts (right-edge vertical fan, leader lines)
        "    var mv=markers.filter(function(m){return !m.ref;}); var bw=110, bh=Math.min(32,(Hd-10)/Math.max(1,mv.length)-2), bx=Wd-bw-3;"
        "    mv.forEach(function(m,i){ var by=5+i*(bh+3); var p=proj(m.world); ln(p[0],p[1],bx,by+bh/2,'rgba(100,116,139,0.6)',1);"
        "      svg.appendChild(mk('rect',{x:bx,y:by,width:bw,height:bh,rx:3,fill:'rgba(255,255,255,0.93)',stroke:m.color,'stroke-width':(st.hover===m.key)?2:1}));"
        "      var nm=m.name.split('  ')[0]; txt(bx+5,by+13, nm+'  Δ'+m.euclid.toFixed(3)+'m','#0f172a',10,'start');"
        "      var dipS=(m.dip!=null)?('dip '+m.dip.toFixed(1)+'°'):''; var ext=(st.hover===m.key)?('Z'+(m.vert>=0?'+':'')+m.vert.toFixed(3)+' H'+m.horiz.toFixed(3)+(dipS?'  '+dipS:'')):dipS; if(ext) txt(bx+5,by+bh-5, ext,'#475569',9,'start'); });"
        // rulers + scale bar (not Free)
        "    if(st.view!=='free'){ var stepM=niceStep(64/ppm); var bp=stepM*ppm, x0=10, y0=Hd-9; ln(x0,y0,x0+bp,y0,'#0f172a',2); ln(x0,y0-3,x0,y0+3,'#0f172a',1); ln(x0+bp,y0-3,x0+bp,y0+3,'#0f172a',1); txt(x0+bp/2,y0-5,(stepM<1?stepM.toFixed(2):stepM.toFixed(0))+' m','#0f172a',9);"
        "      if(st.view==='side' && ref){ var rx=22, stepE=niceStep(48/ppm), refY=proj(ref.world)[1]; ln(rx,8,rx,Hd-18,'#94a3b8',1); for(var kk=-8;kk<=8;kk++){ var val=kk*stepE, yy=refY-val*ppm; if(yy<8||yy>Hd-18) continue; ln(rx-3,yy,rx+3,yy,'#94a3b8',1); txt(rx+16,yy+3,(val>0?'+':'')+(stepE<1?val.toFixed(2):val.toFixed(0)),'#64748b',8,'start'); } } }"
        // compass (Top)
        "    if(st.view==='top' && d.north){ var nd=projDir(d.north); var nl=Math.hypot(nd[0],nd[1])||1; var cx=Wd-22, cy=22; svg.appendChild(mk('circle',{cx:cx,cy:cy,r:13,fill:'rgba(255,255,255,0.85)',stroke:'#cbd5e1','stroke-width':1})); var nx=nd[0]/nl*11, ny=nd[1]/nl*11; ln(cx,cy,cx+nx,cy+ny,'#b45309',2); txt(cx+nx*1.5,cy+ny*1.5+3,'N','#b45309',9); }"
        "  }"
        // table
        "  function cell(parent,t,cls){ var s=document.createElement('span'); if(cls) s.className=cls; s.textContent=t; parent.appendChild(s); return s; }"
        "  function buildTable(){ tbl.innerHTML='';"
        "    var head=document.createElement('div'); head.className='pc-detail-trow pc-detail-thead'; ['Mesh','Euclid','Z','Horiz','Az','Dip'].forEach(function(h){ cell(head,h); }); tbl.appendChild(head);"
        "    markers.forEach(function(m){ var row=document.createElement('div'); row.className='pc-detail-trow'+(m.ref?' pc-detail-ref':'');"
        "      var ms=document.createElement('span'); ms.className='pc-detail-mesh'; var sw=document.createElement('i'); sw.style.background=m.color; ms.appendChild(sw); ms.appendChild(document.createTextNode(m.name+(m.ref?' ★':''))); row.appendChild(ms);"
        "      cell(row, m.ref?'0':m.euclid.toFixed(3)); cell(row, m.ref?'0':((m.vert>=0?'+':'')+m.vert.toFixed(3))); cell(row, m.ref?'0':m.horiz.toFixed(3)); cell(row, (m.ref||m.az==null)?'—':(m.az.toFixed(1)+'°')); cell(row, (m.dip==null)?'—':(m.dip.toFixed(1)+'°'));"
        "      row.addEventListener('mouseenter',function(){ st.hover=m.key; send('hov|'+m.key); draw(); }); row.addEventListener('mouseleave',function(){ st.hover=null; send('out'); draw(); }); tbl.appendChild(row); }); }"
        // pan / zoom / rotate
        "  var pan=null;"
        "  svg.addEventListener('pointerdown',function(e){ if(e.button!==0) return; pan={x:e.clientX,y:e.clientY}; svg.setPointerCapture(e.pointerId); svg.style.cursor='grabbing'; });"
        "  svg.addEventListener('pointermove',function(e){ if(!pan) return; var dx=e.clientX-pan.x, dy=e.clientY-pan.y; pan.x=e.clientX; pan.y=e.clientY; if(st.view==='free'){ st.az-=dx*0.01; st.el=Math.max(-1.4,Math.min(1.4,st.el+dy*0.01)); } else { setupGeom(); st.panX-=dx/ppm; st.panY+=dy/ppm; } st.userAdjusted=true; draw(); });"
        "  function endPan(e){ pan=null; svg.style.cursor=''; if(e&&e.pointerId!=null){ try{svg.releasePointerCapture(e.pointerId);}catch(err){} } }"
        "  svg.addEventListener('pointerup',endPan); svg.addEventListener('pointerleave',function(){ if(pan) endPan(); });"
        "  svg.addEventListener('wheel',function(e){ e.preventDefault(); setupGeom(); st.zoom=Math.max(0.25,Math.min(50, st.zoom*Math.exp(e.deltaY*0.001))); st.userAdjusted=true; draw(); }, {passive:false});"
        "  buildTable();"
        "  if(!st.userAdjusted) autofit();"
        "  draw();"
        // redraw once after layout settles (clientWidth is 0 before first layout)
        "  requestAnimationFrame(function(){ if(svg.clientWidth>0){ if(!st.userAdjusted) autofit(); draw(); } });"
    ]

    let private detailSection (env : Env<Message>) (model : AdaptiveModel) (selectedPin : aval<ScanPin option>) =
        let corr = selectedPin |> AVal.map (Option.bind ScanPin.correspondence)
        let detailVisible =
            corr |> AVal.map (function
                | Some c when c.Enabled && (not (Map.isEmpty c.Anchors) || c.RefAnchor.IsSome) -> true
                | _ -> false)
        let detailJson =
            AVal.custom (fun t ->
                match selectedPin.GetValue t with
                | Some pin ->
                    match pin.Correspondence with
                    | Some c when c.Enabled && (not (Map.isEmpty c.Anchors) || c.RefAnchor.IsSome) ->
                        buildDetailJson pin c
                            (model.MeshOrder.Content.GetValue t)
                            (model.DetailGrids.GetValue t)
                            (model.MeshTransforms.GetValue t)
                            (model.PendingReg.GetValue t)
                            ((model.Registration.GetValue t).ReferenceMesh)
                            (model.CommonCentroid.GetValue t)
                            (model.DatasetScales.GetValue t)
                    | _ -> "{\"status\":\"none\"}"
                | None -> "{\"status\":\"none\"}")
        let onDetailEvent (v : string) =
            let parts = v.Split('|')
            match parts.[0] with
            | "hov" when parts.Length >= 2 ->
                match AVal.force selectedPin with
                | Some pin -> env.Emit [SetCorrMarkerHover (Some (pin.Id, parts.[1])); SetChartHoverMesh (Some parts.[1])]
                | None -> ()
            | "out" -> env.Emit [SetCorrMarkerHover None; SetChartHoverMesh None]
            | _ -> ()
        div {
            Class "pc-detail"
            Primitives.showWhen detailVisible
            div {
                Class "pc-probe-head"
                span {
                    Class "pc-section-title"
                    Attribute("title", "To-scale orthographic view of this correspondence's markers, with a symbolic surface (contours + ridge/valley lines) per mesh and labelled offsets to the reference marker.")
                    "Correspondence detail"
                }
            }
            input {
                Class "pc-detail-bus"
                Attribute("type", "text")
                Dom.OnInput(fun e -> onDetailEvent e.Value)
            }
            div {
                Class "pc-detail-host"
                detailJson |> AVal.map (fun j -> Some (Attribute("data-detail", j)))
                Primitives.observedRender "data-detail" "{}" detailJs
            }
        }

    let pinCardBody (env : Env<Message>) (model : AdaptiveModel) (selectedPin : aval<ScanPin option>) (hoverWorld : aval<V3d option>) (patchHover : cval<PatchHover option>) =
        let isPoint = AVal.constant true
        let showOnly = Primitives.showWhen

        let readoutText = selectedPin |> AVal.map (function
            | Some p -> sprintf "(%.2f, %.2f, %.2f) m · R %.2f m" p.Centre.X p.Centre.Y p.Centre.Z p.InnerRadius
            | None -> "—")
        div {
            Class "pin-card-body"

            div {
                Class "pin-card-section pin-card-point"
                showOnly isPoint
                div {
                    Class "pc-readout"
                    div {
                        Class "pc-readout-row"
                        span { Class "pc-val"; readoutText }
                    }
                }
                // M3C2 probe: ridgeline, x-range / lock-order controls, three-source bar.
                let probe =
                    selectedPin |> AVal.map (function
                        | Some p -> Some p.Probe
                        | None -> None)
                let probeResult =
                    probe |> AVal.map (function
                        | Some (ProbeReady r) -> Some r
                        | _ -> None)
                let previewActive = model.PendingReg |> AVal.map PendingRegistration.isPreview
                let violinOn = AVal.constant true
                let meshOrderMap = model.MeshOrder.Content
                let previewSplit = previewActive
                let probeJson =
                    let selOrder =
                        (selectedPin, meshOrderMap, model.SurfaceDistOn) |||> AVal.map3 (fun po ord sd -> po, ord, sd)
                    (selOrder, model.ChartStickyMesh, previewSplit) |||> AVal.map3 (fun (po, order, brushOn) sticky pv ->
                        match po with
                        | Some pin ->
                            // Split violin while a solve preview is pending and the preview probe is in.
                            let preview =
                                if pv then
                                    match pin.ProbePreview with
                                    | ProbeReady r -> Some r
                                    | _ -> None
                                else None
                            probeStateJson false brushOn sticky pin.DatasetColors order preview pin.Probe
                        | None -> "{\"status\":\"none\"}")
                // 3D → chart: cursor line at the 3D hover point's signed distance along the probe axis, only while inside the cylinder. Under a pending preview the preview-pose probe wins (linking matches on-screen geometry).
                let cursor3d =
                    (hoverWorld, selectedPin, previewActive) |||> AVal.map3 (fun hw po pv ->
                        match hw, po with
                        | Some q, Some pin ->
                            match ScanPin.effectiveProbe pv pin with
                            | ProbeReady r ->
                                let v = q - pin.Centre
                                let dAx = Vec.dot v r.Normal
                                let radial = (v - r.Normal * dAx).Length
                                if radial <= pin.InnerRadius && abs dAx <= r.Length * 0.5
                                then sprintf "{\"d\":%.4f}" (dAx - r.RefOffset)
                                else "{}"
                            | _ -> "{}"
                        | _ -> "{}")
                // Chart → model: chart JS posts pointer interactions to a hidden input (synthetic 'input' events).
                let onChartEvent (v : string) =
                    let parts = v.Split('|')
                    match parts.[0] with
                    | "mv" when parts.Length >= 4 ->
                        match AVal.force selectedPin, parseInvariant parts.[1] with
                        | Some pin, Some dv ->
                            let cursor = { PinId = pin.Id; Distance = dv; Extended = parts.[2] = "1" }
                            let mesh = if parts.[3] = "" then None else Some parts.[3]
                            env.Emit [SetChartCursor (Some cursor); SetChartHoverMesh mesh]
                        | _ -> ()
                    | "out" ->
                        env.Emit [SetChartCursor None; SetChartHoverMesh None]
                    | "click" when parts.Length >= 2 ->
                        env.Emit [ChartColumnClick parts.[1]]
                    // Shift+click mesh M's column at distance d: anchors[M] = refAnchor + d·probeAxis (ViolinAxial). Correspondence-enabled only, never the reference column.
                    | "apick" when parts.Length >= 3 ->
                        // Anchors are committed-pose world points; picking against previewed geometry double-transforms on commit, so block like the other pickers.
                        if AVal.force previewActive then
                            env.Emit [ShowToast "Correspondence-marker picking is disabled while a solve preview is pending"]
                        else
                            match AVal.force selectedPin, parseInvariant parts.[1] with
                            | Some pin, Some dv when parts.[2] <> "" ->
                                match ScanPin.correspondence pin with
                                | Some c when c.Enabled
                                              && (AVal.force model.Registration).ReferenceMesh <> Some parts.[2] ->
                                    let refA = c.RefAnchor |> Option.defaultValue pin.Centre
                                    let axis =
                                        match pin.Probe with
                                        | ProbeReady r -> r.Normal
                                        | _ -> ScanPin.axis pin
                                    env.Emit [SetAnchor(pin.Id, parts.[2], refA + axis * dv, AnchorViolinAxial)]
                                | _ -> ()
                            | _ -> ()
                    | "clickout" ->
                        env.Emit [ClearChartSticky]
                    // A3 range brush: y-interval → highlight that band on the soloed mesh's surface in 3D.
                    | "brush" when parts.Length >= 3 ->
                        match parseInvariant parts.[1], parseInvariant parts.[2] with
                        | Some lo, Some hi -> env.Emit [SetSurfaceDistBrush (Some (lo, hi))]
                        | _ -> ()
                    | "brushclear" ->
                        env.Emit [SetSurfaceDistBrush None]
                    | _ -> ()
                // B1: plain-language significance verdict per moving mesh, read against the detection limit.
                let lodVerdict =
                    (probeResult, meshOrderMap) ||> AVal.map2 (fun rr order ->
                        match rr with
                        | Some r ->
                            let refStd =
                                r.Distributions |> Array.tryFind (fun d -> d.MeshName = r.ReferenceMesh)
                                |> Option.map (fun d -> d.Std) |> Option.defaultValue 0.0
                            r.Distributions
                            |> Array.filter (fun d -> d.Count > 0 && d.MeshName <> r.ReferenceMesh)
                            |> Array.map (fun d ->
                                let lod = 1.96 * sqrt (refStd*refStd + d.Std*d.Std)
                                let isSig = abs d.Median >= lod
                                let txt =
                                    if isSig then sprintf "%s  %+.2f m — significant" (numbered order d.MeshName) d.Median
                                    else sprintf "%s  within noise (n.s.)" (numbered order d.MeshName)
                                txt, isSig)
                            |> IndexList.ofArray
                        | None -> IndexList.empty)
                div {
                    Class "pc-probe"
                    div {
                        Class "pc-probe-head"
                        span { Class "pc-section-title"; "Distance probe" }
                        // A2: paint the soloed mesh's signed distance in 3D.
                        button {
                            Class "tb-gear-btn"
                            showOnly violinOn
                            model.SurfaceDistOn |> AVal.map (fun on -> if on then Some (Class "btn-active") else None)
                            Attribute("title", "Paint signed distance on the surface in 3D — click a violin column to pick the mesh (per-mesh diverging map, 0 = reference, near-zero = neutral)")
                            Dom.OnClick(fun _ -> env.Emit [ToggleSurfaceDistance])
                            "⬢ 3D map"
                        }
                    }
                    input {
                        Class "pc-ridge-bus"
                        Attribute("type", "text")
                        Dom.OnInput(fun e -> onChartEvent e.Value)
                    }
                    // Channel legend folded into the chart's hover tooltip (no always-on text line).
                    div {
                        Class "pc-ridge"
                        showOnly violinOn
                        Attribute("title", "y = signed distance along the reference's local surface normal (0 = reference). Width = precision / roughness (shared density scale). Median tick = bias. Grey band = ±LoD95 detection limit; a median inside it is not significant (n.s.). Two lobes = two surfaces, not noise.")
                        probeJson |> AVal.map (fun j -> Some (Attribute("data-ridge", j)))
                        cursor3d |> AVal.map (fun j -> Some (Attribute("data-cursor", j)))
                        Primitives.observedRender "data-ridge" "{}" CardCharts.ridgelineJs
                    }
                    // B1: explicit band label + per-mesh verdict.
                    div {
                        Class "pc-lod-legend"
                        showOnly violinOn
                        span { Class "pc-lod-swatch" }
                        span { "±LoD₉₅ detection limit — a median inside the band is not significant" }
                    }
                    div {
                        Class "pc-verdict"
                        showOnly violinOn
                        lodVerdict |> AList.ofAVal |> AList.map (fun (txt, isSig) ->
                            div { Class (if isSig then "pc-verdict-sig" else "pc-verdict-ns"); txt })
                    }
                    // B3: keep the two readings distinct — significance vs residual.
                    div {
                        Class "pc-verdict-cap"
                        showOnly violinOn
                        "Band = change significance. Alignment quality is the RMS residual in the Registration panel."
                    }
                    // NUM replacement: per-mesh signed-distance numbers + registration RMS before/after.
                    div {
                        Class "pc-rms-table"
                        Primitives.showWhenNot violinOn
                        div {
                            Class "pc-rms-head"
                            span { Class "pc-rms-cell pc-rms-mesh"; "mesh" }
                            span { Class "pc-rms-cell"; "median" }
                            span { Class "pc-rms-cell"; "IQR" }
                            span { Class "pc-rms-cell"; "n" }
                        }
                        probeResult
                        |> AVal.map (fun r ->
                            match r with
                            | Some r ->
                                r.Distributions
                                |> Array.map (fun d -> d.MeshName, d.Median, d.Q3 - d.Q1, d.Count)
                                |> IndexList.ofArray
                            | None -> IndexList.empty)
                        |> AList.ofAVal
                        |> AList.map (fun (name, median, iqr, count) ->
                            div {
                                Class "pc-rms-row"
                                span { Class "pc-rms-cell pc-rms-mesh"; meshOrderMap |> AVal.map (fun o -> numbered o name) }
                                span { Class "pc-rms-cell"; sprintf "%+.3f m" median }
                                span { Class "pc-rms-cell"; sprintf "%.3f m" iqr }
                                span { Class "pc-rms-cell"; string count }
                            })
                        div {
                            Class "pc-rms-reg"
                            (model.PendingReg, model.LastSolve, meshOrderMap) |||> AVal.map3 (fun pending lastSolve order ->
                                let rows =
                                    match pending with
                                    | Some pr when not (Map.isEmpty pr.Results) ->
                                        pr.Results |> Map.toList
                                        |> List.map (fun (m, r) -> m, r.RmsBefore, r.RmsAfter, "pending")
                                    | _ ->
                                        lastSolve |> Map.toList
                                        |> List.map (fun (m, e) -> m, e.RmsBefore, e.RmsAfter, "committed")
                                match rows with
                                | [] -> "no registration solve yet"
                                | rows ->
                                    rows
                                    |> List.map (fun (m, b, a, tag) ->
                                        sprintf "%s: RMS %.3f → %.3f m (%s)" (numbered order m) b a tag)
                                    |> String.concat " · ")
                        }
                    }
                    div {
                        Class "pc-probe-caption"
                        (probeResult, meshOrderMap) ||> AVal.map2 (fun rr order ->
                            match rr with
                            | Some r -> sprintf "ref %s" (numbered order r.ReferenceMesh)
                            | None -> "")
                    }
                }
                // Ensemble-registration correspondence: per-mesh anchor status, last coarse-solve residuals, fallback picks.
                let corr =
                    selectedPin |> AVal.map (fun po -> po |> Option.bind ScanPin.correspondence)
                let corrEnabled = corr |> AVal.map (function Some c -> c.Enabled | None -> false)
                let refMeshOpt = model.Registration |> AVal.map (fun r -> r.ReferenceMesh)
                let emitForPinTop (mk : ScanPinId -> Message) =
                    match AVal.force selectedPin with
                    | Some p -> env.Emit [mk p.Id]
                    | None -> ()
                div {
                    Class "pc-corr"
                    div {
                        Class "pc-probe-head"
                        span {
                            Class "pc-section-title"
                            Attribute("title", "A correspondence is one real spot in the world, marked on each mesh by a correspondence marker point (one per mesh). Making a pin a registration pin gathers those markers and feeds them to the solve.")
                            "Correspondence"
                        }
                    }
                    // The one-click toggle: registration pin ⟺ has a correspondence.
                    Primitives.compactToggle "Make this a registration pin" corrEnabled (fun () ->
                        emitForPinTop ToggleCorrespondence)
                    div {
                        Class "pc-corr-body"
                        showOnly corrEnabled
                        div {
                            Class "pc-corr-hint"
                            "Registration needs ≥3 registration pins, each with a marker on every moving mesh."
                        }
                        div {
                            Class "pc-corr-ref"
                            (corr, selectedPin) ||> AVal.map2 (fun cOpt po ->
                                match cOpt, po with
                                | Some c, Some pin when c.RefAnchor.IsSome && c.RefDistance > 2.0 * pin.InnerRadius ->
                                    Some (Class "pc-corr-ref-warn")
                                | _ -> None)
                            span {
                                (corr, selectedPin) ||> AVal.map2 (fun cOpt po ->
                                    match cOpt, po with
                                    | Some c, Some pin ->
                                        match c.RefAnchor with
                                        | Some _ when c.RefDistance > 2.0 * pin.InnerRadius ->
                                            sprintf "⚠ reference marker %.2f m off the pin (> 2× radius)" c.RefDistance
                                        | Some _ when c.RefDistance > 0.0 ->
                                            sprintf "reference marker projected, Δ %.3f m" c.RefDistance
                                        | Some _ -> "reference marker = pin centre"
                                        | None -> "no reference marker yet — designate a ★ reference mesh"
                                    | _ -> "")
                            }
                            // F10: reference marker is editable — pick on the reference mesh in 3D. F8: re-click cancels.
                            let refPickActive =
                                (model.AnchorPick, selectedPin, refMeshOpt) |||> AVal.map3 (fun ap sp rm ->
                                    match ap, sp, rm with
                                    | Some a, Some p, Some refMesh -> a.PinId = p.Id && a.Mesh = refMesh
                                    | _ -> false)
                            button {
                                Class "mb"
                                Primitives.showWhen (refMeshOpt |> AVal.map Option.isSome)
                                refPickActive |> AVal.map (fun on -> if on then Some (Class "btn-active") else None)
                                Attribute("title", "Pick / move the reference marker in 3D — click the reference mesh (click again or Esc to cancel)")
                                Dom.OnClick(fun _ ->
                                    match AVal.force selectedPin, AVal.force refMeshOpt with
                                    | Some p, Some refMesh ->
                                        if AVal.force refPickActive then env.Emit [CancelAnchorPick]
                                        else env.Emit [StartAnchorPick(p.Id, refMesh)]
                                    | _ -> ())
                                "⊕"
                            }
                        }
                        div {
                            Class "pc-corr-rows"
                            model.MeshNames |> AList.map (fun mesh ->
                                let isMoving = refMeshOpt |> AVal.map (fun r -> r <> Some mesh)
                                let anchor =
                                    corr |> AVal.map (Option.bind (fun c -> Map.tryFind mesh c.Anchors))
                                let residual =
                                    corr |> AVal.map (Option.bind (fun c -> Map.tryFind mesh c.Residuals))
                                div {
                                    Class "pc-corr-row"
                                    Primitives.showWhen isMoving
                                    // Row hover highlights this mesh's correspondence marker in 3D (thick + bright).
                                    Dom.OnMouseEnter(fun _ ->
                                        match AVal.force selectedPin with
                                        | Some p -> env.Emit [SetCorrMarkerHover (Some (p.Id, mesh))]
                                        | None -> ())
                                    Dom.OnMouseLeave(fun _ -> env.Emit [SetCorrMarkerHover None])
                                    span { Class "pc-corr-mesh"; meshOrderMap |> AVal.map (fun o -> numbered o mesh) }
                                    span {
                                        Class "pc-corr-acc"
                                        anchor |> AVal.map (function
                                            | Some _ -> Some (Class "pc-corr-acc-on")
                                            | None -> None)
                                        anchor |> AVal.map (function
                                            | Some a -> sprintf "✓ %s" (AnchorSource.label a.Source)
                                            | None -> "—")
                                    }
                                    span {
                                        Class "pc-corr-res"
                                        residual |> AVal.map (function
                                            | Some r -> sprintf "%.3f m" r
                                            | None -> "")
                                    }
                                    let pickActive =
                                        (model.AnchorPick, selectedPin) ||> AVal.map2 (fun ap sp ->
                                            match ap, sp with
                                            | Some a, Some p -> a.PinId = p.Id && a.Mesh = mesh
                                            | _ -> false)
                                    button {
                                        // F8: re-click cancels the live pick (toggle).
                                        Class "mb"
                                        pickActive |> AVal.map (fun on -> if on then Some (Class "btn-active") else None)
                                        Attribute("title", "Pick this correspondence marker in 3D — one click on this mesh (click again or Esc to cancel)")
                                        Dom.OnClick(fun _ ->
                                            match AVal.force selectedPin with
                                            | Some p ->
                                                if AVal.force pickActive then env.Emit [CancelAnchorPick]
                                                else env.Emit [StartAnchorPick(p.Id, mesh)]
                                            | None -> ())
                                        "⊕"
                                    }
                                })
                        }
                        // F14: surface the patch picker — occlusion-free marker fixing on overlapping meshes.
                        div {
                            Class "pc-corr-pick-hint"
                            "Overlap hiding a marker? Pick it in 2D patches — nothing overlaps there."
                        }
                        div {
                            Class "pc-corr-actions"
                            button {
                                Class "tb-gear-btn pc-pick-patches"
                                Attribute("title", "Pick correspondence markers in co-oriented surface patches")
                                Dom.OnClick(fun _ -> emitForPinTop OpenPatchPicker)
                                "▦ Pick in patches"
                            }
                        }
                        // Patch small-multiples picker: one orthographic footprint per visible mesh in the shared reference frame; click sets that mesh's anchor.
                        let pickerOpen =
                            (model.PatchPicker, selectedPin) ||> AVal.map2 (fun pp po ->
                                match pp, po with
                                | Some p, Some pin -> p.PinId = pin.Id
                                | _ -> false)
                        let pickerShaded =
                            model.PatchPicker |> AVal.map (function
                                | Some p -> p.Shaded
                                | None -> false)
                        let pickerJson =
                            let pickOrder = (model.PatchPicker, meshOrderMap) ||> AVal.map2 (fun pp ord -> pp, ord)
                            (pickOrder, selectedPin, model.Registration) |||> AVal.map3 (fun (pp, order) po reg ->
                                match pp, po with
                                | Some p, Some pin when p.PinId = pin.Id ->
                                    if p.Running then "{\"status\":\"running\"}"
                                    elif List.isEmpty p.Entries then "{\"status\":\"none\"}"
                                    else
                                        let colorHex name =
                                            match Map.tryFind name pin.DatasetColors with
                                            | Some c -> c4bToHex c
                                            | None -> "#1a56db"
                                        let sb = System.Text.StringBuilder()
                                        sb.Append(sprintf "{\"status\":\"ready\",\"r\":%.4g,\"shaded\":%b,\"entries\":["
                                                    p.Radius p.Shaded) |> ignore
                                        p.Entries |> List.iteri (fun i e ->
                                            if i > 0 then sb.Append(',') |> ignore
                                            let isRef = reg.ReferenceMesh = Some e.Mesh
                                            sb.Append(sprintf "{\"id\":\"%s\",\"mesh\":\"%s\",\"color\":\"%s\",\"ref\":%b,\"atlas\":\"%s\",\"cross\":[%.5g,%.5g],\"tris\":["
                                                        e.Mesh (numbered order e.Mesh) (colorHex e.Mesh) isRef e.AtlasUrl
                                                        e.Crosshair.X e.Crosshair.Y) |> ignore
                                            e.Triangles |> Array.iteri (fun j t ->
                                                if j > 0 then sb.Append(',') |> ignore
                                                sb.Append(t) |> ignore)
                                            sb.Append("],\"pts\":[") |> ignore
                                            e.Points |> Array.iteri (fun j (uv, h, atlasUv) ->
                                                if j > 0 then sb.Append(',') |> ignore
                                                sb.Append(sprintf "[%.5g,%.5g,%.5g,%.5g,%.5g]"
                                                            uv.X uv.Y h atlasUv.X atlasUv.Y) |> ignore)
                                            sb.Append("]}") |> ignore)
                                        sb.Append("]}") |> ignore
                                        sb.ToString()
                                | _ -> "{\"status\":\"none\"}")
                        // Cell-JS bus protocol: pk|mesh|u|v|h (click pick, h = barycentric height), hv|mesh|cx|cy|z[|u|v|h] (hovered cell + pan/zoom viewport + optional cursor), out.
                        // pk → reducer; hv/out only touch the view-local cval (no reducer churn on pointer moves).
                        let setPatchHover (next : PatchHover option) =
                            if patchHover.Value <> next then
                                transact (fun () -> patchHover.Value <- next)
                        let onPatchEvent (v : string) =
                            let parts = v.Split('|')
                            if parts.Length = 0 then () else
                            match parts.[0] with
                            | "pk" when parts.Length >= 5 ->
                                match parseInvariant parts.[2], parseInvariant parts.[3], parseInvariant parts.[4] with
                                | Some u, Some vv, Some h -> env.Emit [PatchPickerClick(parts.[1], u, vv, h)]
                                | _ -> ()
                            | "hv" when parts.Length >= 5 ->
                                match parseInvariant parts.[2], parseInvariant parts.[3], parseInvariant parts.[4] with
                                | Some cx, Some cy, Some z ->
                                    let point =
                                        if parts.Length >= 8 then
                                            match parseInvariant parts.[5], parseInvariant parts.[6], parseInvariant parts.[7] with
                                            | Some u, Some vv, Some h -> Some (V2d(u, vv), h)
                                            | _ -> None
                                        else None
                                    setPatchHover (Some { Mesh = parts.[1]; Centre = V2d(cx, cy); Zoom = z; Point = point })
                                | _ -> ()
                            | "out" -> setPatchHover None
                            | _ -> ()
                        div {
                            Class "pc-patchpicker"
                            showOnly pickerOpen
                            div {
                                Class "pc-probe-head"
                                span { Class "pc-section-title"; "Patch picker" }
                                button {
                                    Class "mb"
                                    Attribute("title", "Toggle textured / shaded-height rendering")
                                    Dom.OnClick(fun _ -> env.Emit [TogglePatchShaded])
                                    pickerShaded |> AVal.map (fun s -> if s then "height" else "texture")
                                }
                                button {
                                    Class "mb"
                                    Attribute("title", "Close patch picker")
                                    Dom.OnClick(fun _ -> env.Emit [ClosePatchPicker])
                                    "✕"
                                }
                            }
                            input {
                                Class "pc-patch-bus"
                                Attribute("type", "text")
                                Dom.OnInput(fun e -> onPatchEvent e.Value)
                            }
                            div {
                                Class "pc-patch-grid"
                                pickerJson |> AVal.map (fun j -> Some (Attribute("data-patches", j)))
                                Primitives.observedRender "data-patches" "{}" [
                                    // Canvas small-multiples: textured/shaded triangles, per-cell pan/zoom, triangle hit-test picking, 2D↔3D hover linking.
                                    // Per-mesh viewport state survives re-renders on el.__ppv; two stacked canvases per cell (base = surface, overlay = cursor/marks) so pointer moves never redraw triangles.
                                    "  function placeholder(t){ var p = document.createElement('div'); p.className = 'pin-card-empty'; p.textContent = t; el.appendChild(p); }"
                                    "  if(!d.status || d.status === 'none'){ return; }"
                                    "  if(d.status === 'running'){ placeholder('Sampling patches…'); return; }"
                                    "  var entries = d.entries || [];"
                                    "  if(entries.length === 0){ placeholder('No patches.'); return; }"
                                    "  var hmin = Infinity, hmax = -Infinity;"
                                    "  entries.forEach(function(e){ e.pts.forEach(function(p){ if(p[2] < hmin) hmin = p[2]; if(p[2] > hmax) hmax = p[2]; }); });"
                                    "  if(!(hmax > hmin)){ hmin = -0.5; hmax = 0.5; }"
                                    // F17: viridis (perceptually-uniform) height ramp.
                                    "  var VIR = [[68,1,84],[59,82,139],[33,145,140],[94,201,98],[253,231,37]];"
                                    "  function hcol(h){"
                                    "    var t = Math.max(0, Math.min(1, (h - hmin) / (hmax - hmin)));"
                                    "    var x = t * 4, i = Math.min(3, Math.floor(x)), f = x - i;"
                                    "    var a = VIR[i], b = VIR[i+1];"
                                    "    return 'rgb(' + Math.round(a[0]+(b[0]-a[0])*f) + ',' + Math.round(a[1]+(b[1]-a[1])*f) + ',' + Math.round(a[2]+(b[2]-a[2])*f) + ')';"
                                    "  }"
                                    "  var send = function(s){"
                                    "    var pr = el.closest('.pc-patchpicker');"
                                    "    var b = pr ? pr.querySelector('.pc-patch-bus') : null;"
                                    "    if(b){ b.value = s; b.dispatchEvent(new Event('input', {bubbles:true})); }"
                                    "  };"
                                    "  var lastHv = '', hvQueued = null, hvRaf = 0;"
                                    "  var sendHv = function(s){"
                                    "    hvQueued = s;"
                                    "    if(!hvRaf){ hvRaf = requestAnimationFrame(function(){"
                                    "      hvRaf = 0;"
                                    "      if(hvQueued !== null && hvQueued !== lastHv){ lastHv = hvQueued; send(hvQueued); }"
                                    "    }); }"
                                    "  };"
                                    "  var views = el.__ppv = el.__ppv || {};"
                                    "  var cells = [];"
                                    "  var ghost = null;"
                                    "  var ACC = '#0891b2';"
                                    "  entries.forEach(function(e){"
                                    // F18: first view fits the populated footprint (farthest sampled vertex reaches the circle edge), not the full box.
                                    "    var st = views[e.id];"
                                    "    if(!st){ var pr = 0; (e.pts||[]).forEach(function(p){ var l = Math.hypot(p[0], p[1]); if(l > pr) pr = l; }); var fz = pr > 1e-6 ? d.r / pr : 1; fz = Math.max(1, Math.min(12, fz)); st = views[e.id] = {cx:0, cy:0, z:fz}; }"
                                    "    var wrap = document.createElement('div');"
                                    "    wrap.className = 'pc-patch-cell' + (e.ref ? ' pc-patch-cell-ref' : '');"
                                    "    var head = document.createElement('div');"
                                    "    head.className = 'pc-patch-head';"
                                    "    var sw = document.createElement('span');"
                                    "    sw.className = 'pc-patch-swatch'; sw.style.background = e.color;"
                                    "    head.appendChild(sw);"
                                    "    var nm = document.createElement('span');"
                                    "    nm.textContent = e.mesh + (e.ref ? ' ★' : '');"
                                    "    head.appendChild(nm);"
                                    "    var zl = document.createElement('span');"
                                    "    zl.className = 'pc-patch-zoom';"
                                    "    zl.title = 'reset zoom';"
                                    "    head.appendChild(zl);"
                                    "    wrap.appendChild(head);"
                                    "    var size = 124, pad = 6, maxR = size / 2 - pad, c0 = size / 2;"
                                    "    var dpr = window.devicePixelRatio || 1;"
                                    "    var box = document.createElement('div');"
                                    "    box.className = 'pc-patch-box';"
                                    "    box.style.width = size + 'px'; box.style.height = size + 'px';"
                                    "    function mkCanvas(){"
                                    "      var cv = document.createElement('canvas');"
                                    "      cv.width = Math.round(size * dpr); cv.height = Math.round(size * dpr);"
                                    "      cv.className = 'pc-patch-canvas';"
                                    "      cv.style.width = size + 'px'; cv.style.height = size + 'px';"
                                    "      box.appendChild(cv);"
                                    "      var g = cv.getContext('2d');"
                                    "      g.setTransform(dpr, 0, 0, dpr, 0, 0);"
                                    "      return g;"
                                    "    }"
                                    "    var gb = mkCanvas(), gt = mkCanvas();"
                                    "    wrap.appendChild(box);"
                                    "    el.appendChild(wrap);"
                                    "    wrap.title = 'scroll = zoom, drag = pan, click the zoom label to reset' + (e.ref ? '' : ', click = set marker');"
                                    "    var order = [];"
                                    "    var tr3 = e.tris || [];"
                                    "    for(var i = 0; i + 2 < tr3.length; i += 3){ order.push([tr3[i], tr3[i+1], tr3[i+2]]); }"
                                    "    order.sort(function(a, b){"
                                    "      return (e.pts[a[0]][2] + e.pts[a[1]][2] + e.pts[a[2]][2]) - (e.pts[b[0]][2] + e.pts[b[1]][2] + e.pts[b[2]][2]);"
                                    "    });"
                                    // F15: an atlas that fails to load (CORS / decode / 0-size) falls back to shaded height, never a black cell.
                                    "    var img = null;"
                                    "    if(e.atlas){ var im = new Image(); im.onload = function(){ if(im.width > 0 && im.height > 0) img = im; requestDraw(); }; im.onerror = function(){ img = null; requestDraw(); }; im.src = e.atlas; }"
                                    "    function k(){ return maxR / d.r * st.z; }"
                                    "    function sx(u){ return c0 + (u - st.cx) * k(); }"
                                    "    function sy(v){ return c0 - (v - st.cy) * k(); }"
                                    "    function toData(px, py){ return [(px - c0) / k() + st.cx, st.cy - (py - c0) / k()]; }"
                                    "    function clampView(){"
                                    "      if(st.z < 1) st.z = 1; if(st.z > 12) st.z = 12;"
                                    "      var m = d.r * (1 - 1 / st.z);"
                                    "      var l = Math.hypot(st.cx, st.cy);"
                                    "      if(l > m){ var f = l > 0 ? m / l : 0; st.cx *= f; st.cy *= f; }"
                                    "    }"
                                    "    clampView();"
                                    "    function flatTri(x0, y0, x1, y1, x2, y2, col){"
                                    "      gb.beginPath(); gb.moveTo(x0, y0); gb.lineTo(x1, y1); gb.lineTo(x2, y2); gb.closePath();"
                                    "      gb.fillStyle = col; gb.fill();"
                                    "      gb.strokeStyle = col; gb.lineWidth = 0.6; gb.stroke();"
                                    "    }"
                                    "    function drawBase(){"
                                    "      gb.clearRect(0, 0, size, size);"
                                    // F16: clip to the footprint circle + hatch the uncovered area, so partial overlap reads as 'no coverage', not 'not drawn'.
                                    "      gb.save();"
                                    "      gb.beginPath(); gb.arc(sx(0), sy(0), d.r * k(), 0, 6.2832); gb.clip();"
                                    "      gb.fillStyle = '#f1f5f9'; gb.fillRect(0, 0, size, size);"
                                    "      gb.strokeStyle = '#e2e8f0'; gb.lineWidth = 1;"
                                    "      for(var hx = -size; hx < size * 2; hx += 8){ gb.beginPath(); gb.moveTo(hx, 0); gb.lineTo(hx - size, size); gb.stroke(); }"
                                    "      var shaded = d.shaded || !img;"
                                    "      order.forEach(function(tr){"
                                    "        var p0 = e.pts[tr[0]], p1 = e.pts[tr[1]], p2 = e.pts[tr[2]];"
                                    "        var x0 = sx(p0[0]), y0 = sy(p0[1]), x1 = sx(p1[0]), y1 = sy(p1[1]), x2 = sx(p2[0]), y2 = sy(p2[1]);"
                                    "        if(Math.max(x0, x1, x2) < 0 || Math.max(y0, y1, y2) < 0 || Math.min(x0, x1, x2) > size || Math.min(y0, y1, y2) > size) return;"
                                    "        if(shaded){ flatTri(x0, y0, x1, y1, x2, y2, hcol((p0[2] + p1[2] + p2[2]) / 3)); return; }"
                                    "        var W = img.width, H = img.height;"
                                    "        var u0 = p0[3] * W, v0 = (1 - p0[4]) * H, u1 = p1[3] * W, v1 = (1 - p1[4]) * H, u2 = p2[3] * W, v2 = (1 - p2[4]) * H;"
                                    "        var du = Math.max(Math.abs(p0[3] - p1[3]), Math.abs(p0[3] - p2[3]), Math.abs(p0[4] - p1[4]), Math.abs(p0[4] - p2[4]));"
                                    "        var den = (u1 - u0) * (v2 - v0) - (u2 - u0) * (v1 - v0);"
                                    "        if(du > 0.25 || Math.abs(den) < 1e-6){ flatTri(x0, y0, x1, y1, x2, y2, hcol((p0[2] + p1[2] + p2[2]) / 3)); return; }"
                                    "        var gx = (x0 + x1 + x2) / 3, gy = (y0 + y1 + y2) / 3, s = 1.025;"
                                    "        var a = ((x1 - x0) * (v2 - v0) - (x2 - x0) * (v1 - v0)) / den;"
                                    "        var b = ((x2 - x0) * (u1 - u0) - (x1 - x0) * (u2 - u0)) / den;"
                                    "        var c = ((y1 - y0) * (v2 - v0) - (y2 - y0) * (v1 - v0)) / den;"
                                    "        var f = ((y2 - y0) * (u1 - u0) - (y1 - y0) * (u2 - u0)) / den;"
                                    "        gb.save();"
                                    "        gb.beginPath();"
                                    "        gb.moveTo(gx + (x0 - gx) * s, gy + (y0 - gy) * s);"
                                    "        gb.lineTo(gx + (x1 - gx) * s, gy + (y1 - gy) * s);"
                                    "        gb.lineTo(gx + (x2 - gx) * s, gy + (y2 - gy) * s);"
                                    "        gb.closePath(); gb.clip();"
                                    "        gb.transform(a, c, b, f, x0 - a * u0 - b * v0, y0 - c * u0 - f * v0);"
                                    "        gb.drawImage(img, 0, 0);"
                                    "        gb.restore();"
                                    "      });"
                                    "      if(order.length === 0){"
                                    "        e.pts.forEach(function(p){"
                                    "          var x = sx(p[0]), y = sy(p[1]);"
                                    "          if(x < -2 || x > size + 2 || y < -2 || y > size + 2) return;"
                                    "          gb.beginPath(); gb.arc(x, y, 1.7, 0, 6.2832); gb.fillStyle = hcol(p[2]); gb.fill();"
                                    "        });"
                                    "      }"
                                    "      gb.restore();"
                                    "      gb.beginPath(); gb.arc(sx(0), sy(0), d.r * k(), 0, 6.2832);"
                                    "      gb.strokeStyle = e.ref ? '#b45309' : '#cbd5e1'; gb.lineWidth = e.ref ? 2 : 1; gb.stroke();"
                                    "      var chx = sx(e.cross[0]), chy = sy(e.cross[1]);"
                                    "      gb.strokeStyle = '#0f172a'; gb.lineWidth = 1.2;"
                                    "      gb.beginPath(); gb.moveTo(chx - 6, chy); gb.lineTo(chx + 6, chy); gb.moveTo(chx, chy - 6); gb.lineTo(chx, chy + 6); gb.stroke();"
                                    "      zl.textContent = st.z > 1.001 ? st.z.toFixed(1) + '×' : '';"
                                    "    }"
                                    "    var hovered = false, cursor = null, panning = null;"
                                    "    function hitTri(u, v){"
                                    "      for(var i = order.length - 1; i >= 0; i--){"
                                    "        var tr = order[i];"
                                    "        var p0 = e.pts[tr[0]], p1 = e.pts[tr[1]], p2 = e.pts[tr[2]];"
                                    "        var d1 = (u - p1[0]) * (p0[1] - p1[1]) - (p0[0] - p1[0]) * (v - p1[1]);"
                                    "        var d2 = (u - p2[0]) * (p1[1] - p2[1]) - (p1[0] - p2[0]) * (v - p2[1]);"
                                    "        var d3 = (u - p0[0]) * (p2[1] - p0[1]) - (p2[0] - p0[0]) * (v - p0[1]);"
                                    "        if(((d1 < 0) || (d2 < 0) || (d3 < 0)) && ((d1 > 0) || (d2 > 0) || (d3 > 0))) continue;"
                                    "        var den = (p1[1] - p2[1]) * (p0[0] - p2[0]) + (p2[0] - p1[0]) * (p0[1] - p2[1]);"
                                    "        if(Math.abs(den) < 1e-12) continue;"
                                    "        var w0 = ((p1[1] - p2[1]) * (u - p2[0]) + (p2[0] - p1[0]) * (v - p2[1])) / den;"
                                    "        var w1 = ((p2[1] - p0[1]) * (u - p2[0]) + (p0[0] - p2[0]) * (v - p2[1])) / den;"
                                    "        return p0[2] * w0 + p1[2] * w1 + p2[2] * (1 - w0 - w1);"
                                    "      }"
                                    "      return null;"
                                    "    }"
                                    "    function drawTop(){"
                                    "      gt.clearRect(0, 0, size, size);"
                                    "      if(ghost && ghost.mesh !== e.id){"
                                    "        var gx2 = sx(ghost.u), gy2 = sy(ghost.v);"
                                    "        gt.strokeStyle = 'rgba(8,145,178,0.4)'; gt.lineWidth = 1;"
                                    "        gt.beginPath(); gt.moveTo(gx2 - 5, gy2); gt.lineTo(gx2 + 5, gy2); gt.moveTo(gx2, gy2 - 5); gt.lineTo(gx2, gy2 + 5); gt.stroke();"
                                    "      }"
                                    "      if(!hovered) return;"
                                    "      gt.fillStyle = 'rgba(15,23,42,0.35)';"
                                    "      e.pts.forEach(function(p){"
                                    "        var x = sx(p[0]), y = sy(p[1]);"
                                    "        if(x >= 0 && x <= size && y >= 0 && y <= size) gt.fillRect(x - 0.7, y - 0.7, 1.4, 1.4);"
                                    "      });"
                                    "      if(cursor){"
                                    "        var x = sx(cursor[0]), y = sy(cursor[1]);"
                                    "        gt.strokeStyle = ACC; gt.lineWidth = 1.4;"
                                    "        gt.beginPath(); gt.moveTo(x - 7, y); gt.lineTo(x + 7, y); gt.moveTo(x, y - 7); gt.lineTo(x, y + 7); gt.stroke();"
                                    "        gt.beginPath(); gt.arc(x, y, 3, 0, 6.2832); gt.stroke();"
                                    "        gt.fillStyle = ACC; gt.font = '10px sans-serif';"
                                    "        gt.fillText('Δh ' + (cursor[2] >= 0 ? '+' : '') + cursor[2].toFixed(3) + ' m', 6, size - 6);"
                                    "      }"
                                    "    }"
                                    "    var dirty = false;"
                                    "    function requestDraw(){"
                                    "      if(!dirty){ dirty = true; requestAnimationFrame(function(){ dirty = false; drawBase(); drawTop(); }); }"
                                    "    }"
                                    "    function viewStr(){ return e.id + '|' + st.cx.toFixed(5) + '|' + st.cy.toFixed(5) + '|' + st.z.toFixed(3); }"
                                    "    function hvSend(){"
                                    "      if(!hovered) return;"
                                    "      if(cursor) sendHv('hv|' + viewStr() + '|' + cursor[0].toFixed(5) + '|' + cursor[1].toFixed(5) + '|' + cursor[2].toFixed(5));"
                                    "      else sendHv('hv|' + viewStr());"
                                    "    }"
                                    "    function setGhost(){"
                                    "      ghost = (hovered && cursor) ? {mesh: e.id, u: cursor[0], v: cursor[1]} : null;"
                                    "      cells.forEach(function(c){ if(c.id !== e.id) c.top(); });"
                                    "    }"
                                    "    var ev = gt.canvas;"
                                    "    if(!e.ref) ev.style.cursor = 'crosshair';"
                                    "    ev.addEventListener('pointerenter', function(){ hovered = true; hvSend(); drawTop(); });"
                                    "    ev.addEventListener('pointerleave', function(){"
                                    "      hovered = false; cursor = null; hvQueued = null; lastHv = '';"
                                    "      send('out'); setGhost(); drawTop();"
                                    "    });"
                                    "    ev.addEventListener('pointermove', function(evt){"
                                    "      var rc = ev.getBoundingClientRect();"
                                    "      if(panning){"
                                    "        var dx = evt.clientX - panning.x, dy = evt.clientY - panning.y;"
                                    "        if(panning.moved || Math.abs(dx) + Math.abs(dy) > 3){"
                                    "          panning.moved = true;"
                                    "          ev.style.cursor = 'grabbing';"
                                    "          st.cx -= dx / k(); st.cy += dy / k();"
                                    "          panning.x = evt.clientX; panning.y = evt.clientY;"
                                    "          clampView(); requestDraw(); hvSend();"
                                    "        }"
                                    "        return;"
                                    "      }"
                                    "      var uv = toData(evt.clientX - rc.left, evt.clientY - rc.top);"
                                    "      var h = hitTri(uv[0], uv[1]);"
                                    "      cursor = h === null ? null : [uv[0], uv[1], h];"
                                    "      hvSend(); setGhost(); drawTop();"
                                    "    });"
                                    "    ev.addEventListener('pointerdown', function(evt){"
                                    "      if(evt.button !== 0) return;"
                                    "      panning = {x: evt.clientX, y: evt.clientY, moved: false};"
                                    "      ev.setPointerCapture(evt.pointerId);"
                                    "    });"
                                    "    ev.addEventListener('pointerup', function(evt){"
                                    "      var wasPan = panning && panning.moved;"
                                    "      panning = null;"
                                    "      ev.style.cursor = e.ref ? '' : 'crosshair';"
                                    "      try{ ev.releasePointerCapture(evt.pointerId); }catch(err){}"
                                    "      if(wasPan || e.ref) return;"
                                    "      var rc = ev.getBoundingClientRect();"
                                    "      var uv = toData(evt.clientX - rc.left, evt.clientY - rc.top);"
                                    "      var h = hitTri(uv[0], uv[1]);"
                                    "      if(h !== null) send('pk|' + e.id + '|' + uv[0].toFixed(5) + '|' + uv[1].toFixed(5) + '|' + h.toFixed(5));"
                                    "    });"
                                    "    ev.addEventListener('wheel', function(evt){"
                                    "      evt.preventDefault();"
                                    "      var rc = ev.getBoundingClientRect();"
                                    "      var px = evt.clientX - rc.left, py = evt.clientY - rc.top;"
                                    "      var before = toData(px, py);"
                                    "      st.z = Math.max(1, Math.min(12, st.z * Math.exp(-evt.deltaY * 0.002)));"
                                    "      st.cx = before[0] - (px - c0) / k(); st.cy = before[1] + (py - c0) / k();"
                                    "      clampView(); requestDraw();"
                                    "      var uv = toData(px, py);"
                                    "      var h = hitTri(uv[0], uv[1]);"
                                    "      cursor = h === null ? null : [uv[0], uv[1], h];"
                                    "      hvSend(); setGhost();"
                                    "    }, {passive: false});"
                                    // Reset lives on the zoom label, NOT dblclick — dblclick on a pickable cell would fire the anchor pick twice first.
                                    "    zl.addEventListener('click', function(){"
                                    "      st.cx = 0; st.cy = 0; st.z = 1;"
                                    "      clampView(); requestDraw(); hvSend();"
                                    "    });"
                                    "    cells.push({id: e.id, top: drawTop});"
                                    "    drawBase(); drawTop();"
                                    "  });"
                                ]
                            }
                        }
                    }
                }
                detailSection env model selectedPin
            }

        }

