namespace Superprojekt

open Aardvark.Base
open FSharp.Data.Adaptive
open Aardvark.Dom

// Bottom dock: full-width 2D dock (SVG/HTML, not a WebGL control), always
// mounted. Mode-contextual content (mesh roster / correspondence manager / pin
// distribution), cross-faded; the container never moves.
module GuiInspector =

    open Primitives

    // Pin distribution canvas (Task 4): per moving mesh, on a shared signed-distance
    // axis, jittered raw probe samples (the "rain") + a median/IQR box, with the
    // ±LoD₉₅ neutral band shaded. No KDE. Driven by the data-dist JSON attribute.
    let private distJs = [
        "  function ph(t){ var p=document.createElement('div'); p.className='ins-ph'; p.textContent=t; el.appendChild(p); }"
        "  if(!d || !d.rows){ ph(d&&d.pending?d.pending:'select a pin'); return; }"
        "  if(d.rows.length===0){ ph('no moving meshes probed'); return; }"
        "  var W=el.clientWidth||320, H=el.clientHeight||150; var dpr=window.devicePixelRatio||1;"
        "  var cv=document.createElement('canvas'); cv.width=Math.round(W*dpr); cv.height=Math.round(H*dpr);"
        "  cv.style.width=W+'px'; cv.style.height=H+'px'; cv.className='ins-dist-cv';"
        "  var g=cv.getContext('2d'); g.setTransform(dpr,0,0,dpr,0,0);"
        "  g.fillStyle='#ffffff'; g.fillRect(0,0,W,H);"
        "  var padL=10,padR=12,padT=24,padB=16; var lo=d.lo,hi=d.hi; var span=Math.max(1e-6,hi-lo);"
        "  function X(v){ return padL+(v-lo)/span*(W-padL-padR); }"
        "  g.fillStyle='#475569'; g.font='11px SF Mono,Monaco,monospace';"
        "  g.fillText(d.state+'  ·  signed distance (mm)  ·  0 = reference median',8,14);"
        "  var n=d.rows.length; var laneH=(H-padT-padB)/n;"
        "  g.strokeStyle='#cbd5e1'; g.lineWidth=1; g.beginPath(); g.moveTo(X(0),padT-2); g.lineTo(X(0),H-padB+2); g.stroke();"
        "  g.fillStyle='#94a3b8'; g.font='9px SF Mono,Monaco,monospace'; g.textAlign='center';"
        "  [lo,(lo+hi)/2,hi].forEach(function(v){ g.fillText(v.toFixed(0),X(v),H-4); });"
        "  g.textAlign='left';"
        "  d.rows.forEach(function(r,i){ var y0=padT+i*laneH, yc=y0+laneH*0.55;"
        "    if(r.lod>0){ g.fillStyle='rgba(148,163,184,0.16)'; g.fillRect(X(-r.lod),y0+2,Math.max(1,X(r.lod)-X(-r.lod)),laneH-4); }"
        "    g.globalAlpha=0.30; g.fillStyle=r.color;"
        "    for(var k=0;k<r.s.length;k++){ var x=X(r.s[k]); var yy=y0+laneH*0.32+Math.random()*(laneH*0.5); g.beginPath(); g.arc(x,yy,1.4,0,6.2832); g.fill(); }"
        "    g.globalAlpha=1;"
        "    var bx0=X(r.q1),bx1=X(r.q3); var bh=Math.min(13,laneH*0.42);"
        "    g.strokeStyle=r.color; g.lineWidth=1.4; g.strokeRect(bx0,yc-bh/2,Math.max(1,bx1-bx0),bh);"
        "    g.beginPath(); g.moveTo(X(r.median),yc-bh/2); g.lineTo(X(r.median),yc+bh/2); g.lineWidth=2.2; g.stroke();"
        "    g.fillStyle='#334155'; g.font='10px SF Mono,Monaco,monospace';"
        "    g.fillText(r.name+'   med '+r.median.toFixed(0)+'mm  IQR '+(r.q3-r.q1).toFixed(0)+'  n='+r.n, padL, y0+11); });"
        "  el.appendChild(cv);"
    ]

    // 8-way unicode arrow for a heading in degrees (0 = +X/east, 90 = +Y/north).
    let private dirArrow (deg : float) =
        let d = ((deg % 360.0) + 360.0) % 360.0
        [| "→"; "↗"; "↑"; "↖"; "←"; "↙"; "↓"; "↘" |].[int (System.Math.Round(d / 45.0)) % 8]

    let dock (env : Env<Message>) (model : AdaptiveModel) =
        let selected  = model.Selection.SelectedPin
        let pinsVal   = model.ScanPins.Pins |> AMap.toAVal
        let effId     = selected
        let effPin    = (effId, pinsVal) ||> AVal.map2 (fun id pins -> id |> Option.bind (fun i -> HashMap.tryFind i pins))
        let hasPin    = effPin |> AVal.map Option.isSome
        let orderVal  = model.MeshOrder.Content
        let refMeshA  = model.Registration |> AVal.map (fun r -> r.ReferenceMesh)
        let corrA     = effPin |> AVal.map (Option.bind ScanPin.correspondence)
        let emit (m : Message) = env.Emit [m]

        let visibleMovingA =
            AVal.custom (fun t ->
                let names = model.MeshNames.Content.GetValue t |> IndexList.toList
                let vis = model.MeshVisible.GetValue t
                let rf = (model.Registration.GetValue t).ReferenceMesh
                names |> List.filter (fun n -> Some n <> rf && (Map.tryFind n vis |> Option.defaultValue true)))

        // k/n counts in-ROI meshes only: n = in-ROI moving meshes, k = those with
        // a placed marker; out-of-ROI meshes are excluded entirely.
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

        let rosterRow (name : string) =
            let isVis   = model.MeshVisible |> AVal.map (fun m -> Map.tryFind name m |> Option.defaultValue true)
            let isRef   = refMeshA |> AVal.map ((=) (Some name))
            let colorVal = model.MeshOrder |> AMap.tryFind name |> AVal.map (Option.defaultValue 0 >> meshColor)
            let sensor  = model.MeshSensorTypes |> AVal.map (fun m -> Map.tryFind name m |> Option.defaultValue UnknownSensor)
            let loaded  = MeshView.loadMeshAsync (fun _ -> ()) name
            let triCount = loaded.fvc |> AVal.map (fun c -> max 0 (c / 3))
            let overlap =
                AVal.custom (fun t ->
                    match (model.Registration.GetValue t).ReferenceMesh with
                    | Some rf when rf <> name ->
                        let mb = model.MeshBounds.GetValue t
                        match Map.tryFind name mb, Map.tryFind rf mb with
                        | Some a, Some b -> a.Intersects b
                        | _ -> false
                    | _ -> true)
            let sensorTxt = function
                | RoverStereo -> "Rover" | Satellite -> "Sat" | Photogrammetry -> "Photo"
                | LiDAR -> "LiDAR" | UnknownSensor -> "—"
            let focused = model.Selection.FocusedMesh |> AVal.map ((=) (Some name))
            div {
                Class "ros-row"
                classWhen "ros-row-active" focused
                Dom.OnClick(fun _ -> emit (SetFocusedMesh (Some name)))
                Dom.OnPointerMove(fun _ -> emit (SetHovered (Some (HoverMesh name))))
                Dom.OnMouseLeave(fun _ -> emit (SetHovered None))
                span { Class "ins-sw"; colorVal |> AVal.map (fun c -> Some (Style [Css.Background (c4bToRgbCss c)])) }
                span { Class "ros-name"; orderVal |> AVal.map (fun o -> numbered o name) }
                span { Class "ros-cell"; isRef |> AVal.map (fun r -> if r then "★ ref" else "moving") }
                span { Class "ros-cell"; sensor |> AVal.map sensorTxt }
                span { Class "ros-cell"; triCount |> AVal.map (fun n -> sprintf "%d tris" n) }
                span {
                    Class "ros-cell"
                    showWhenNot isRef
                    overlap |> AVal.map (fun o -> if o then "overlaps ✓" else "no overlap")
                }
                button {
                    Class "mb"
                    classWhen "mb-on" isVis
                    Attribute("title", "Visible")
                    Dom.OnClick(fun _ -> emit (SetVisible(name, not (AVal.force isVis))))
                    isVis |> AVal.map (fun v -> if v then "●" else "○")
                }
            }
        let roster =
            div {
                Class "ins-roster"
                div {
                    Class "ros-head"
                    span { Class "ins-sw" }
                    span { Class "ros-name"; "mesh" }
                    span { Class "ros-cell"; "role" }
                    span { Class "ros-cell"; "sensor" }
                    span { Class "ros-cell"; "size" }
                    span { Class "ros-cell"; "vs ref" }
                    span { Class "mb" }
                }
                div { Class "ros-rows"; model.MeshNames |> AList.map rosterRow }
            }

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
                    (match effId.GetValue t with
                     | Some id -> model.Selection.Hovered.GetValue t = Some (HoverPoint (id, mesh))
                     | None -> false)
                    || model.Selection.SelectedPoint.GetValue t = Some mesh)
            let colorVal = model.MeshOrder |> AMap.tryFind mesh |> AVal.map (Option.defaultValue 0 >> meshColor)
            let hoverEmit (on : bool) =
                match AVal.force effId with
                | Some id -> emit (SetHovered (if on then Some (HoverPoint (id, mesh)) else None))
                | None -> emit (SetHovered None)
            let picking3D =
                (model.Corr3DPick, effId) ||> AVal.map2 (fun c id ->
                    match c, id with
                    | Some (pid, m), Some sid -> pid = sid && m = mesh
                    | _ -> false)
            div {
                Class "ins-mgr-row"
                showWhen show
                classWhenNot "ins-mgr-out" inRoi
                classWhen "ins-mgr-active" active
                Dom.OnPointerMove(fun _ -> hoverEmit true)
                Dom.OnMouseLeave(fun _ -> hoverEmit false)
                span { Class "ins-sw"; colorVal |> AVal.map (fun c -> Some (Style [Css.Background (c4bToRgbCss c)])) }
                span { Class "ins-mgr-name"; orderVal |> AVal.map (fun o -> numbered o mesh) }
                span { stateCls |> AVal.map Some; Class "ins-mgr-state"; stateGlyph }
                span { Class "ins-mgr-res"; resOrSpread }
                button {
                    Class "mb ins-mgr-act"
                    showWhen inRoi
                    Attribute("title", "Re-seed this mesh")
                    Dom.OnClick(fun _ -> match AVal.force effId with Some id -> emit (ReseedMesh(id, mesh)) | None -> ())
                    "⟳"
                }
                button {
                    Class "mb ins-mgr-act"
                    showWhen inRoi
                    Attribute("title", "Focus camera + edit in the focus panel")
                    Dom.OnClick(fun _ -> emit (SetSelectedPoint (Some mesh)); emit (SetFocusedMesh (Some mesh)))
                    "⌖"
                }
                button {
                    Class "mb ins-mgr-act"
                    showWhen inRoi
                    classWhen "mb-on" picking3D
                    Attribute("title", "Set this point in the 3D view (isolates the mesh; click the surface)")
                    Dom.OnClick(fun _ -> match AVal.force effId with Some id -> emit (StartCorr3DPick(id, mesh)) | None -> ())
                    picking3D |> AVal.map (fun on -> if on then "⊙" else "⊕")
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
        let nameVal   = effPin |> AVal.map (Option.map (fun p -> p.Name) >> Option.defaultValue "")
        let radiusVal = effPin |> AVal.map (Option.map (fun p -> p.InnerRadius) >> Option.defaultValue 0.5)
        let manager =
            div {
                Class "ins-mgr"
                div {
                    Class "ins-mgr-head"
                    input {
                        Class "ins-name"
                        Attribute("type", "text"); Attribute("title", "pin name")
                        nameVal |> AVal.map (fun n -> Some (Attribute("value", n)))
                        Dom.OnChange(fun e ->
                            match AVal.force effId with Some id -> emit (RenamePin(id, e.Value)) | None -> ())
                    }
                    inlineLogSlider "r" 0.01 10000.0 (sprintf "%.2f m") radiusVal (fun v ->
                        emit (ScanPinMsg (SetInnerRadius v)))
                }
                refRow
                div { Class "ins-mgr-rows"; model.MeshNames |> AList.map managerRow }
                div {
                    Class "ins-mgr-foot"
                    span { Class "ins-kn"; kn |> AVal.map (fun (k, n) -> sprintf "k/n %d/%d" k n) }
                    button {
                        Class "rail-btn rail-btn-primary ins-solve"
                        Dom.OnClick(fun _ -> emit SolveCoarse)
                        "Solve"
                    }
                }
            }

        // Inspect dock: a Difference|Displacement channel toggle (drives the focus
        // tiles), the pin distribution panel (Task 4), and the shift readout
        // (Task 5, displacement only). Containers are fixed; only content swaps.
        let channelA = model.InspectChannel
        let isDisplacement = channelA |> AVal.map ((=) ChDisplacement)

        // Shift readout (displacement): the focused mesh's centroid displacement
        // load→solved, split vertical (datum) / horizontal (lateral) + rotation
        // angle, derived client-side from its SolvedTransform.
        let shiftData =
            AVal.custom (fun t ->
                match model.Selection.FocusedMesh.GetValue t with
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
                        Some (numbered (orderVal.GetValue t) m, total, vertical, horizontal, heading, ang))
        let hasShift = shiftData |> AVal.map Option.isSome
        let shiftBody = (isDisplacement, hasShift) ||> AVal.map2 (&&)
        let shiftEmpty = (isDisplacement, hasShift) ||> AVal.map2 (fun d h -> d && not h)
        let shiftFmt f = shiftData |> AVal.map (function Some x -> f x | None -> "—")
        let shiftRow (k : string) (v : aval<string>) =
            div { Class "ins-shift-row"; span { Class "ins-shift-k"; k }; span { Class "ins-shift-v"; v } }

        // Per moving mesh: re-centred raw probe samples + median/IQR + ±LoD₉₅, on a
        // shared mm axis. The probe reflects the current RegView pose (it refetches
        // on toggle), so `state` labels which side is shown.
        let distData =
            AVal.custom (fun t ->
                let inv = System.Globalization.CultureInfo.InvariantCulture
                let g (v : float) =
                    if System.Double.IsNaN v || System.Double.IsInfinity v then "0" else v.ToString("0.###", inv)
                let order = orderVal.GetValue t
                match effPin.GetValue t with
                | None -> "{}"
                | Some p ->
                    match p.Probe with
                    | ProbeRunning | ProbeNone -> "{\"pending\":\"probing…\"}"
                    | ProbeError _ -> "{\"pending\":\"probe unavailable\"}"
                    | ProbeReady r ->
                        let stateLbl = match model.RegView.GetValue t with RegBefore -> "Before" | RegAfter -> "After"
                        let stdOf m =
                            r.Distributions |> Array.tryFind (fun d -> d.MeshName = m)
                            |> Option.map (fun d -> d.Std) |> Option.defaultValue 0.0
                        let refStd = stdOf r.ReferenceMesh
                        let moving = r.Distributions |> Array.filter (fun d -> d.MeshName <> r.ReferenceMesh)
                        if moving.Length = 0 then "{\"rows\":[]}"
                        else
                            let pooled = moving |> Array.collect (fun d -> d.Samples) |> Array.map (fun x -> x * 1000.0)
                            let lo, hi =
                                if pooled.Length = 0 then -10.0, 10.0
                                else
                                    let s = Array.sort pooled
                                    let q pp =
                                        let h = pp * float (s.Length - 1)
                                        let i = int h
                                        if i >= s.Length - 1 then s.[s.Length - 1]
                                        else s.[i] + (h - float i) * (s.[i + 1] - s.[i])
                                    q 0.01, q 0.99
                            let lo, hi = min lo 0.0, max hi 0.0
                            let pad = max 1.0 (hi - lo) * 0.08
                            let lo, hi = lo - pad, hi + pad
                            let rowJson (d : ProbeDistribution) =
                                let col = match HashMap.tryFind d.MeshName order with Some i -> c4bToHex (meshColor i) | None -> "#1a56db"
                                let lod = 1.96 * sqrt (refStd * refStd + d.Std * d.Std) * 1000.0
                                let stride = if d.Samples.Length > 300 then d.Samples.Length / 300 else 1
                                let sj =
                                    [ 0 .. stride .. d.Samples.Length - 1 ]
                                    |> List.map (fun i -> g (d.Samples.[i] * 1000.0)) |> String.concat ","
                                sprintf "{\"name\":\"%s\",\"color\":\"%s\",\"median\":%s,\"q1\":%s,\"q3\":%s,\"lod\":%s,\"n\":%d,\"s\":[%s]}"
                                    (numbered order d.MeshName) col (g (d.Median * 1000.0)) (g (d.Q1 * 1000.0)) (g (d.Q3 * 1000.0)) (g lod) d.Count sj
                            let rows = moving |> Array.map rowJson |> String.concat ","
                            sprintf "{\"state\":\"%s\",\"lo\":%s,\"hi\":%s,\"rows\":[%s]}" stateLbl (g lo) (g hi) rows)

        let inspectDock =
            div {
                Class "ins-inspect"
                div {
                    Class "ins-insp-head"
                    span { Class "ins-insp-label"; "Focus channel" }
                    compactButtonBar [
                        "Difference",   (channelA |> AVal.map ((=) ChDifference)),   (fun () -> emit (SetInspectChannel ChDifference))
                        "Displacement", (channelA |> AVal.map ((=) ChDisplacement)), (fun () -> emit (SetInspectChannel ChDisplacement))
                    ]
                }
                div {
                    Class "ins-insp-body"
                    div {
                        Class "ins-dist"
                        distData |> AVal.map (fun j -> Some (Attribute("data-dist", j)))
                        observedRender "data-dist" "{}" distJs
                    }
                    // Always mounted at a fixed width so the channel toggle never
                    // reflows the distribution panel; only the inner content swaps.
                    div {
                        Class "ins-shift"
                        div { Class "ins-stub-note"; showWhenNot isDisplacement; "Shift readout shows in the Displacement channel." }
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

        // Container-invariant cross-fade between the three modes.
        let stepA = model.WorkflowStep
        let modeOn (pred : WorkflowStep -> bool) =
            classWhen "ins-mode-on" (stepA |> AVal.map pred)
        div {
            Class "pin-inspector"
            div {
                Class "ins-header"
                span { Class "ins-mode-label"; stepA |> AVal.map WorkflowStep.mode }
            }
            div {
                Class "ins-modes"
                div { Class "ins-mode"; modeOn ((=) Overview); roster }
                div {
                    Class "ins-mode"
                    modeOn ((=) Correspondence)
                    div { Class "ins-empty"; showWhenNot hasPin; span { "◌ select a pin" } }
                    div { Class "ins-mgr-wrap"; showWhen hasPin; manager }
                }
                div { Class "ins-mode"; modeOn ((=) Inspect); inspectDock }
            }
        }
