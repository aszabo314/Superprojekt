namespace Superprojekt

open Aardvark.Base
open FSharp.Data.Adaptive
open Aardvark.Dom

// Bottom dock: full-width 2D dock (SVG/HTML, not a WebGL control), always
// mounted. Mode-contextual content (mesh roster / correspondence manager / pin
// distribution), cross-faded; the container never moves.
module GuiInspector =

    open Primitives

    // Pin distribution canvas (§T6): per moving-mesh lane, the ROI sample "rain" of
    // every pin coloured by pin, on a shared signed-distance axis with the ±LoD₉₅
    // band + an axis scale and an LoD legend. A hovered pin's samples are bright; the
    // rest dim (the chart side of the bidirectional pin↔surface brushing).
    let private distJs = [
        "  function ph(t){ var p=document.createElement('div'); p.className='ins-ph'; p.textContent=t; el.appendChild(p); }"
        "  if(!d || !d.rows){ ph(d&&d.pending?d.pending:'place pins to see ROI distributions'); return; }"
        "  if(d.rows.length===0){ ph('no moving meshes probed'); return; }"
        "  var W=el.clientWidth||320, H=el.clientHeight||150; var dpr=window.devicePixelRatio||1;"
        "  var cv=document.createElement('canvas'); cv.width=Math.round(W*dpr); cv.height=Math.round(H*dpr);"
        "  cv.style.width=W+'px'; cv.style.height=H+'px'; cv.className='ins-dist-cv';"
        "  var g=cv.getContext('2d'); g.setTransform(dpr,0,0,dpr,0,0);"
        "  g.fillStyle='#ffffff'; g.fillRect(0,0,W,H);"
        "  var padL=10,padR=12,padT=26,padB=18; var lo=d.lo,hi=d.hi; var span=Math.max(1e-6,hi-lo);"
        "  function X(v){ return padL+(v-lo)/span*(W-padL-padR); }"
        "  g.fillStyle='#475569'; g.font='11px SF Mono,Monaco,monospace'; g.textAlign='left';"
        "  g.fillText(d.state+'  ·  signed distance to reference (mm)  ·  0 = reference median',8,13);"
        "  g.fillStyle='#94a3b8'; g.font='9px SF Mono,Monaco,monospace';"
        "  g.fillText('░ ±LoD₉₅ (within = indistinguishable)', W-200, 13);"
        "  var n=d.rows.length; var laneH=(H-padT-padB)/n;"
        "  g.strokeStyle='#cbd5e1'; g.lineWidth=1; g.beginPath(); g.moveTo(X(0),padT-2); g.lineTo(X(0),H-padB+2); g.stroke();"
        "  g.fillStyle='#94a3b8'; g.font='9px SF Mono,Monaco,monospace'; g.textAlign='center';"
        "  [lo,(lo+hi)/2,hi].forEach(function(v){ g.fillText(v.toFixed(0),X(v),H-5); });"
        "  g.textAlign='center'; g.fillText('mm', X(hi)+4, H-5);"
        "  g.textAlign='left';"
        "  d.rows.forEach(function(r,i){ var y0=padT+i*laneH;"
        "    if(r.lod>0){ g.fillStyle='rgba(148,163,184,0.18)'; g.fillRect(X(-r.lod),y0+2,Math.max(1,X(r.lod)-X(-r.lod)),laneH-4); }"
        "    r.pins.forEach(function(pn){ g.globalAlpha = pn.hl? 0.55 : 0.10; g.fillStyle=pn.color;"
        "      for(var k=0;k<pn.s.length;k++){ var x=X(pn.s[k]); var yy=y0+laneH*0.30+Math.random()*(laneH*0.5); g.beginPath(); g.arc(x,yy,1.5,0,6.2832); g.fill(); }"
        "      g.globalAlpha = pn.hl? 1 : 0.25; g.strokeStyle=pn.color; g.lineWidth=pn.hl?2.0:1.0;"
        "      g.beginPath(); g.moveTo(X(pn.med),y0+laneH*0.22); g.lineTo(X(pn.med),y0+laneH*0.82); g.stroke(); });"
        "    g.globalAlpha=1; g.fillStyle='#334155'; g.font='10px SF Mono,Monaco,monospace';"
        "    g.fillText(r.name, padL, y0+11); });"
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

        // Overview dock (T3): the focus tiles are the mesh browser now, so the dock is
        // a compact summary of the focused mesh (colour · number · role · sensor),
        // not a second mesh list.
        let sensorTxt = function
            | RoverStereo -> "Rover" | Satellite -> "Sat" | Photogrammetry -> "Photo"
            | LiDAR -> "LiDAR" | UnknownSensor -> "—"
        let focusedSummary =
            AVal.custom (fun t ->
                match model.Selection.FocusedMesh.GetValue t with
                | None -> None
                | Some name ->
                    let order = orderVal.GetValue t
                    let isRef = (model.Registration.GetValue t).ReferenceMesh = Some name
                    let idx = HashMap.tryFind name order |> Option.defaultValue 0
                    let sensor = Map.tryFind name (model.MeshSensorTypes.GetValue t) |> Option.defaultValue UnknownSensor
                    Some (numbered order name, isRef, sensorTxt sensor, meshColor idx))
        let overviewCard =
            div {
                Class "ins-ovw"
                div { Class "ins-ovw-empty"; showWhen (focusedSummary |> AVal.map Option.isNone); "Select a mesh tile to focus it." }
                div {
                    Class "ins-ovw-card"
                    showWhen (focusedSummary |> AVal.map Option.isSome)
                    span {
                        Class "ins-sw"
                        focusedSummary |> AVal.map (function Some (_, _, _, c) -> Some (Style [Css.Background (c4bToRgbCss c)]) | None -> None)
                    }
                    span { Class "ins-ovw-name"; focusedSummary |> AVal.map (function Some (n, _, _, _) -> n | None -> "") }
                    span { Class "ins-ovw-role"; focusedSummary |> AVal.map (function Some (_, r, _, _) -> (if r then "★ reference" else "moving") | None -> "") }
                    span { Class "ins-ovw-sensor"; focusedSummary |> AVal.map (function Some (_, _, s, _) -> s | None -> "") }
                }
            }

        // The matrix (left rail) is now the per-(pin,mesh) browser (§B); the
        // Correspondence dock reduces to pin meta: identity chip · name · radius ·
        // k/n · Solve. The per-mesh list, ref row, and dual pick buttons are gone.
        let nameVal   = effPin |> AVal.map (Option.map (fun p -> p.Name) >> Option.defaultValue "")
        let radiusVal = effPin |> AVal.map (Option.map (fun p -> p.InnerRadius) >> Option.defaultValue 0.5)
        let pinIdentChip =
            div {
                Class "ins-pinident"
                span {
                    Class "ins-pinident-sw"
                    effPin |> AVal.map (function Some p -> Some (Style [Css.Background (c4bToRgbCss p.PinColor)]) | None -> None)
                }
                span { Class "ins-pinident-gn"; effPin |> AVal.map (function Some p -> sprintf "%s %s" p.Glyph p.ShortName | None -> "") }
            }
        let manager =
            div {
                Class "ins-mgr"
                div {
                    Class "ins-mgr-head"
                    pinIdentChip
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

        // Distribution (§T6): per moving mesh lane, the ROI samples of EVERY pin,
        // coloured by pin (§A) on the shared signed-distance axis with the ±LoD₉₅
        // band. Hovering a pin (anywhere — legend, matrix row, 3D) highlights its
        // samples (the others dim) — the chart side of the bidirectional brushing.
        let distData =
            AVal.custom (fun t ->
                let inv = System.Globalization.CultureInfo.InvariantCulture
                let g (v : float) =
                    if System.Double.IsNaN v || System.Double.IsInfinity v then "0" else v.ToString("0.###", inv)
                let order = orderVal.GetValue t
                let pins = pinsVal.GetValue t
                let rf = (model.Registration.GetValue t).ReferenceMesh
                let vis = model.MeshVisible.GetValue t
                let names = model.MeshNames.Content.GetValue t |> IndexList.toList
                let moving = names |> List.filter (fun n -> Some n <> rf && (Map.tryFind n vis |> Option.defaultValue true))
                let hov = match model.Selection.Hovered.GetValue t with Some (HoverPin id) -> Some id | _ -> None
                let readyPins =
                    pins |> HashMap.toList
                    |> List.choose (fun (id, p) -> match p.Probe with ProbeReady r -> Some (id, p, r) | _ -> None)
                if List.isEmpty readyPins then "{\"pending\":\"probing pins…\"}"
                elif List.isEmpty moving then "{\"rows\":[]}"
                else
                    let stateLbl = match model.RegView.GetValue t with RegBefore -> "Before" | RegAfter -> "After"
                    let pooled = ResizeArray<float>()
                    for (_, _, r) in readyPins do
                        for d in r.Distributions do
                            if d.MeshName <> r.ReferenceMesh then for s in d.Samples do pooled.Add (s * 1000.0)
                    let lo, hi =
                        if pooled.Count = 0 then -10.0, 10.0
                        else
                            let s = pooled.ToArray() in Array.sortInPlace s
                            let q pp =
                                let h = pp * float (s.Length - 1)
                                let i = int h
                                if i >= s.Length - 1 then s.[s.Length - 1] else s.[i] + (h - float i) * (s.[i + 1] - s.[i])
                            q 0.01, q 0.99
                    let lo, hi = min lo 0.0, max hi 0.0
                    let pad = max 1.0 (hi - lo) * 0.08
                    let lo, hi = lo - pad, hi + pad
                    let refStdOf (r : ProbeResult) =
                        r.Distributions |> Array.tryFind (fun d -> d.MeshName = r.ReferenceMesh)
                        |> Option.map (fun d -> d.Std) |> Option.defaultValue 0.0
                    let rowJson mesh =
                        let groups =
                            readyPins |> List.choose (fun (id, p, r) ->
                                match r.Distributions |> Array.tryFind (fun d -> d.MeshName = mesh) with
                                | Some d when d.Count > 0 ->
                                    let rs = refStdOf r
                                    let lod = 1.96 * sqrt (rs * rs + d.Std * d.Std) * 1000.0
                                    let stride = if d.Samples.Length > 120 then d.Samples.Length / 120 else 1
                                    let sj = [ 0 .. stride .. d.Samples.Length - 1 ] |> List.map (fun k -> g (d.Samples.[k] * 1000.0)) |> String.concat ","
                                    let hl = match hov with Some h -> (if h = id then 1 else 0) | None -> 1
                                    Some (lod, sprintf "{\"color\":\"%s\",\"name\":\"%s %s\",\"lod\":%s,\"hl\":%d,\"med\":%s,\"s\":[%s]}"
                                                    (c4bToHex p.PinColor) p.Glyph p.ShortName (g lod) hl (g (d.Median * 1000.0)) sj)
                                | _ -> None)
                        if List.isEmpty groups then None
                        else
                            let avgLod = groups |> List.averageBy fst
                            Some (sprintf "{\"name\":\"%s\",\"lod\":%s,\"pins\":[%s]}" (numbered order mesh) (g avgLod) (groups |> List.map snd |> String.concat ","))
                    let rows = moving |> List.choose rowJson |> String.concat ","
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
                    // Difference sub-mode (M3C2 ↔ Δz) — only meaningful in the
                    // Difference channel. Moved here from the rail (rail = matrix now).
                    div {
                        Class "ins-insp-sub"
                        showWhen (channelA |> AVal.map ((=) ChDifference))
                        span { Class "ins-insp-label"; "Δ" }
                        compactButtonBar [
                            "M3C2", (model.ExtrinsicZDiff |> AVal.map not),  (fun () -> if AVal.force model.ExtrinsicZDiff then emit ToggleExtrinsicZDiff)
                            "Δz",   (model.ExtrinsicZDiff :> aval<bool>),    (fun () -> if not (AVal.force model.ExtrinsicZDiff) then emit ToggleExtrinsicZDiff)
                        ]
                    }
                    // Intrinsic per-fragment channel (camera incidence / range / shape).
                    div {
                        Class "ins-insp-sub"
                        span { Class "ins-insp-label"; "Intrinsic" }
                        compactButtonBar [
                            "Off",       (model.HeatmapMode |> AVal.map (fun m -> m = HeatOff)),        (fun () -> emit (SetHeatmapMode HeatOff))
                            "Incidence", (model.HeatmapMode |> AVal.map (fun m -> m = HeatIncidence)),  (fun () -> emit (SetHeatmapMode HeatIncidence))
                            "Range",     (model.HeatmapMode |> AVal.map (fun m -> m = HeatRange)),      (fun () -> emit (SetHeatmapMode HeatRange))
                            "Shape",     (model.HeatmapMode |> AVal.map (fun m -> m = HeatShape)),      (fun () -> emit (SetHeatmapMode HeatShape))
                        ]
                    }
                }
                div {
                    Class "ins-insp-body"
                    div {
                        Class "ins-dist-col"
                        // Pin legend — hovering a chip brushes that pin (highlights its
                        // samples in the chart AND its surface cells in 3D). The chart
                        // canvas is display-only, so this is the chart→3D brush handle.
                        div {
                            Class "ins-dist-legend"
                            model.ScanPins.Pins
                            |> AMap.map (fun _ p -> p.Glyph, p.ShortName, p.PinColor, p.CreatedAt)
                            |> AMap.toASet
                            |> ASet.sortBy (fun (ScanPinId.ScanPinId gg, (_, _, _, c)) -> c, gg)
                            |> AList.map (fun (id, (glyph, sn, col, _)) ->
                                let hovered = model.Selection.Hovered |> AVal.map (function Some (HoverPin i) -> i = id | _ -> false)
                                div {
                                    Class "ins-dist-leg"
                                    classWhen "ins-dist-leg-on" hovered
                                    Dom.OnPointerMove(fun _ -> emit (SetHovered (Some (HoverPin id))))
                                    Dom.OnMouseLeave(fun _ -> emit (SetHovered None))
                                    span { Class "ins-dist-leg-sw"; Style [Css.Background (c4bToRgbCss col)] }
                                    span { sprintf "%s %s" glyph sn }
                                })
                        }
                        div {
                            Class "ins-dist"
                            distData |> AVal.map (fun j -> Some (Attribute("data-dist", j)))
                            observedRender "data-dist" "{}" distJs
                        }
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
                div { Class "ins-mode"; modeOn ((=) Overview); overviewCard }
                div {
                    Class "ins-mode"
                    modeOn ((=) Correspondence)
                    div { Class "ins-empty"; showWhenNot hasPin; span { "◌ select a pin" } }
                    div { Class "ins-mgr-wrap"; showWhen hasPin; manager }
                }
                div { Class "ins-mode"; modeOn ((=) Inspect); inspectDock }
            }
        }
