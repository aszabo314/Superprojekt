namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open FSharp.Data.Adaptive
open Aardvark.Dom

module GuiPins =

    let private coreSampleTrafo = PinGeometry.coreSampleTrafo

    let shortName (name : string) =
        let mesh =
            let s = name.IndexOf('/')
            if s >= 0 then name.[s + 1 ..] else name
        if mesh.Length > 8 && mesh.[8] = '_' then
            let date = mesh.[..7]
            let si = mesh.LastIndexOf("_seg")
            if si > 0 then date + "_" + mesh.[si + 1 ..] else date
        else mesh

    let private c4bToHex (c : C4b) =
        sprintf "#%02x%02x%02x" c.R c.G c.B

    let private encodeDiagramJson (pin : ScanPin) (svgW : float) (svgH : float) (pad : float) =
        let results = pin.CutResults |> Map.toList
        if results.IsEmpty then "{\"paths\":[],\"legend\":[]}"
        else
            let allPts = results |> List.collect (fun (_, cr) -> cr.Polylines |> List.collect id)
            let xs = allPts |> List.map (fun (p : V2d) -> p.X)
            let ys = allPts |> List.map (fun (p : V2d) -> p.Y)
            let xMin = xs |> List.min
            let xMax = xs |> List.max
            let yMin = ys |> List.min
            let yMax = ys |> List.max
            let xRange = if xMax - xMin < 1e-9 then 1.0 else xMax - xMin
            let yRange = if yMax - yMin < 1e-9 then 1.0 else yMax - yMin
            let toSvgX x = pad + (x - xMin) / xRange * (svgW - 2.0 * pad)
            let toSvgY y = (svgH - pad) - (y - yMin) / yRange * (svgH - 2.0 * pad)
            let paths =
                results |> List.collect (fun (name, cr) ->
                    let color = pin.DatasetColors |> Map.tryFind name |> Option.defaultValue (C4b(100uy,100uy,100uy)) |> c4bToHex
                    cr.Polylines |> List.map (fun pts ->
                        let d = pts |> List.mapi (fun i (p : V2d) ->
                            let cmd = if i = 0 then "M" else "L"
                            sprintf "%s%.1f,%.1f" cmd (toSvgX p.X) (toSvgY p.Y)) |> String.concat " "
                        sprintf "{\"d\":\"%s\",\"c\":\"%s\"}" (d.Replace("\"","\\\"")) color))
            let legend =
                results |> List.map (fun (name, _) ->
                    let color = pin.DatasetColors |> Map.tryFind name |> Option.defaultValue (C4b(100uy,100uy,100uy)) |> c4bToHex
                    sprintf "{\"n\":\"%s\",\"c\":\"%s\"}" (shortName name) color)
            sprintf "{\"paths\":[%s],\"legend\":[%s],\"xMin\":%.4g,\"xMax\":%.4g,\"yMin\":%.4g,\"yMax\":%.4g}"
                (paths |> String.concat ",") (legend |> String.concat ",") xMin xMax yMin yMax

    let pinsTabPanel (env : Env<Message>) (model : AdaptiveModel) =
        let sp = model.ScanPins
        let isPlacing = (sp.PlacingMode, sp.ActivePlacement) ||> AVal.map2 (fun pm ap -> pm.IsSome || ap.IsSome)

        div {
            Class "tab-panel"
            Attribute("id", "hud-panel4")

            h3 { "ScanPins" }

            div {
                Class "btn-row"
                button {
                    isPlacing |> AVal.map (fun on ->
                        if on then Some (Class "btn-active") else None)
                    Dom.OnClick(fun _ ->
                        let placing = AVal.force isPlacing
                        if placing then env.Emit [ScanPinMsg CancelPlacement]
                        else env.Emit [ScanPinMsg (StartPlacement FootprintMode.Circle)]
                    )
                    isPlacing |> AVal.map (fun on ->
                        if on then "Cancel" else "Place pin")
                }
            }
            label {
                Style [Display "flex"; AlignItems "center"; MarginTop "4px"; FontSize "13px"]
                input {
                    Attribute("type", "checkbox")
                    model.PinAxisVertical |> AVal.map (fun v ->
                        if v then Some (Attribute("checked", "checked")) else None)
                    Dom.OnChange(fun _ -> env.Emit [TogglePinAxisVertical])
                }
                "Place along Z axis"
            }
            p {
                sp.PlacingMode |> AVal.map (fun pm ->
                    if pm.IsSome then "Tap on the 3D view to place anchor." else "")
            }

            let activePin =
                (sp.ActivePlacement, sp.Pins |> AMap.toAVal) ||> AVal.map2 (fun id pins ->
                    id |> Option.bind (fun i -> HashMap.tryFind i pins))
            div {
                activePin |> AVal.map (fun p ->
                    if p.IsNone then Some (Style [Display "none"]) else None)

                div {
                    "Radius  "
                    input {
                        Attribute("type", "range")
                        Attribute("min", "0.1"); Attribute("max", "20"); Attribute("step", "0.1")
                        Style [Width "100%"]
                        activePin |> AVal.map (fun p ->
                            match p with
                            | Some pin ->
                                match pin.Prism.Footprint.Vertices with
                                | v :: _ -> Some (Attribute("value", sprintf "%.1f" v.Length))
                                | _ -> Some (Attribute("value", "1"))
                            | None -> None)
                        Dom.OnInput(fun e ->
                            match System.Double.TryParse(e.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture) with
                            | true, v -> env.Emit [ScanPinMsg (SetFootprintRadius v)]
                            | _ -> ())
                    }
                }
                div {
                    Class "btn-row"
                    Style [MarginTop "6px"]
                    button {
                        Class "btn-active"
                        Dom.OnClick(fun _ -> env.Emit [ScanPinMsg CommitPin])
                        "Commit"
                    }
                    button {
                        Dom.OnClick(fun _ -> env.Emit [ScanPinMsg CancelPlacement])
                        "Discard"
                    }
                }
            }

            h3 { "Pins" }
            let pinIdList = sp.Pins |> AMap.toASet |> ASet.map fst |> ASet.sortBy id
            let guiPinsVal = sp.Pins |> AMap.toAVal
            pinIdList |> AList.map (fun id ->
                let pinVal = guiPinsVal |> AVal.map (fun pins -> HashMap.tryFind id pins)
                let isSelected = sp.SelectedPin |> AVal.map (fun sel -> sel = Some id)
                div {
                    Style [Padding "4px 0"; BorderBottom "1px solid #e2e8f0"]
                    isSelected |> AVal.map (fun s ->
                        if s then Some (Style [Background "#fff3cd"]) else None)
                    div {
                        Dom.OnClick(fun _ ->
                            let sel = AVal.force sp.SelectedPin
                            if sel = Some id then env.Emit [ScanPinMsg (SelectPin None)]
                            else env.Emit [ScanPinMsg (SelectPin (Some id))])
                        Style [Css.Cursor "pointer"; FontFamily "monospace"; FontSize "11px"]
                        pinVal |> AVal.map (fun pinOpt ->
                            match pinOpt with
                            | Some pin ->
                                let p = pin.Prism.AnchorPoint
                                let phase = if pin.Phase = PinPhase.Placement then " [placing]" else ""
                                sprintf "(%.1f, %.1f, %.1f)%s" p.X p.Y p.Z phase
                            | None -> "(removed)")
                    }
                    div {
                        Class "btn-row"
                        Style [MarginTop "2px"]
                        button {
                            Style [FontSize "11px"; Padding "1px 6px"]
                            Dom.OnClick(fun _ -> env.Emit [ScanPinMsg (FocusPin id)])
                            "Focus"
                        }
                        button {
                            Style [FontSize "11px"; Padding "1px 6px"]
                            Dom.OnClick(fun _ -> env.Emit [ScanPinMsg (DeletePin id)])
                            "Delete"
                        }
                    }
                }
            )
        }

    let pinDiagram (env : Env<Message>) (model : AdaptiveModel) (viewTrafo : aval<Trafo3d>) (vpSize : aval<V2i>) =
        let allPinsVal = model.ScanPins.Pins |> AMap.toAVal
        let selectedPin =
            (model.ScanPins.SelectedPin, allPinsVal) ||> AVal.map2 (fun sel pins ->
                sel |> Option.bind (fun id -> HashMap.tryFind id pins))

        let selectedPrism    = selectedPin |> AVal.map (Option.map (fun p -> p.Prism))
        let selectedCutPlane = selectedPin |> AVal.map (Option.map (fun p -> p.CutPlane))
        let selectedGridEval = selectedPin |> AVal.map (Option.bind (fun p -> p.GridEval))

        let svgW, svgH = 280.0, 180.0
        let pad = 30.0

        let screenPos =
            (selectedPrism, viewTrafo, vpSize) |||> AVal.map3 (fun prismOpt (vt : Trafo3d) sz ->
                match prismOpt with
                | None -> None
                | Some prism ->
                    let pt = prism.AnchorPoint
                    let aspect = float sz.X / max 1.0 (float sz.Y)
                    let proj = Frustum.perspective 90.0 1.0 5000.0 aspect |> Frustum.projTrafo
                    let m = proj.Forward * vt.Forward
                    let h = m * V4d(pt, 1.0)
                    if h.W < 0.1 then None
                    else
                        let ndc = h.XYZ / h.W
                        if abs ndc.X > 1.5 || abs ndc.Y > 1.5 then None
                        else
                            let px = (ndc.X * 0.5 + 0.5) * float sz.X
                            let py = (1.0 - (ndc.Y * 0.5 + 0.5)) * float sz.Y
                            Some (V2d(px, py))
            )

        div {
            Class "pin-diagram"
            (selectedPrism, screenPos) ||> AVal.map2 (fun p pos ->
                match p, pos with
                | None, _ | _, None ->
                    Some (Style [Css.Visibility "hidden"; Left "-9999px"; Top "-9999px"])
                | _, Some p ->
                    Some (Style [
                        Css.Visibility "visible"
                        Left (sprintf "%.0fpx" (p.X + 20.0))
                        Top (sprintf "%.0fpx" (p.Y - 120.0))
                    ]))

            h3 { "Profile" }

            div {
                Attribute("id", "pin-diagram-root")
                selectedPin |> AVal.map (fun pinOpt ->
                    let json =
                        match pinOpt with
                        | Some pin -> encodeDiagramJson pin svgW svgH pad
                        | None -> "{\"paths\":[],\"legend\":[]}"
                    Some (Attribute("data-diagram", json)))
                OnBoot [
                    "var last = '';"
                    "function render() {"
                    "  var el = document.getElementById('pin-diagram-root');"
                    "  if(!el) return;"
                    "  var raw = el.getAttribute('data-diagram') || '{}';"
                    "  if(raw === last) return;"
                    "  last = raw;"
                    "  el.innerHTML = '';"
                    "  try { var data = JSON.parse(raw); } catch(e) { return; }"
                    "  if(!data.paths || data.paths.length===0) return;"
                    sprintf "  var ns = 'http://www.w3.org/2000/svg';"
                    sprintf "  var svg = document.createElementNS(ns, 'svg');"
                    sprintf "  svg.setAttribute('width', '%.0f');" svgW
                    sprintf "  svg.setAttribute('height', '%.0f');" svgH
                    sprintf "  svg.setAttribute('viewBox', '0 0 %.0f %.0f');" svgW svgH
                    sprintf "  svg.style.background = '#fafbfc';"
                    sprintf "  svg.style.border = '1px solid #e2e8f0';"
                    sprintf "  svg.style.borderRadius = '4px';"
                    sprintf "  svg.style.display = 'block';"
                    "  el.appendChild(svg);"
                    sprintf "  var ax = document.createElementNS(ns, 'line');"
                    sprintf "  ax.setAttribute('x1','%.0f'); ax.setAttribute('y1','%.0f');" pad (svgH - pad)
                    sprintf "  ax.setAttribute('x2','%.0f'); ax.setAttribute('y2','%.0f');" (svgW - pad) (svgH - pad)
                    "  ax.setAttribute('stroke','#94a3b8'); ax.setAttribute('stroke-width','1');"
                    "  svg.appendChild(ax);"
                    sprintf "  var ay = document.createElementNS(ns, 'line');"
                    sprintf "  ay.setAttribute('x1','%.0f'); ay.setAttribute('y1','%.0f');" pad pad
                    sprintf "  ay.setAttribute('x2','%.0f'); ay.setAttribute('y2','%.0f');" pad (svgH - pad)
                    "  ay.setAttribute('stroke','#94a3b8'); ay.setAttribute('stroke-width','1');"
                    "  svg.appendChild(ay);"
                    "  function addText(x,y,txt,anchor) {"
                    "    var t = document.createElementNS(ns, 'text');"
                    "    t.setAttribute('x',x); t.setAttribute('y',y);"
                    "    t.setAttribute('font-size','9'); t.setAttribute('fill','#64748b');"
                    "    t.setAttribute('font-family','system-ui,sans-serif');"
                    "    if(anchor) t.setAttribute('text-anchor',anchor);"
                    "    t.textContent = txt; svg.appendChild(t);"
                    "  }"
                    "  if(data.xMin!==undefined) {"
                    sprintf "    addText(%.0f, %.0f, data.xMin.toFixed(1), 'start');" pad (svgH - pad + 12.0)
                    sprintf "    addText(%.0f, %.0f, data.xMax.toFixed(1), 'end');" (svgW - pad) (svgH - pad + 12.0)
                    sprintf "    addText(%.0f, %.0f, data.yMax.toFixed(1), 'end');" (pad - 4.0) (pad + 3.0)
                    sprintf "    addText(%.0f, %.0f, data.yMin.toFixed(1), 'end');" (pad - 4.0) (svgH - pad + 3.0)
                    "  }"
                    "  var tip = document.createElementNS(ns, 'text');"
                    "  tip.setAttribute('x','5'); tip.setAttribute('y','12');"
                    "  tip.setAttribute('font-size','10'); tip.setAttribute('fill','#0f172a');"
                    "  tip.setAttribute('font-family','system-ui,sans-serif');"
                    "  tip.setAttribute('font-weight','600');"
                    "  tip.style.pointerEvents = 'none';"
                    "  svg.appendChild(tip);"
                    "  var allPaths = [];"
                    "  data.paths.forEach(function(item,idx) {"
                    "    var p = document.createElementNS(ns, 'path');"
                    "    p.setAttribute('d', item.d);"
                    "    p.setAttribute('stroke', item.c);"
                    "    p.setAttribute('stroke-width', '2');"
                    "    p.setAttribute('fill', 'none');"
                    "    p.style.cursor = 'pointer';"
                    "    p.setAttribute('stroke-opacity', '0.85');"
                    "    allPaths.push(p);"
                    "    var leg = data.legend && data.legend[idx] ? data.legend[idx] : null;"
                    "    p.addEventListener('mouseenter', function() {"
                    "      allPaths.forEach(function(q) { q.setAttribute('stroke-opacity', q===p?'1':'0.2'); q.setAttribute('stroke-width', q===p?'3':'1.5'); });"
                    "      if(leg) tip.textContent = leg.n;"
                    "    });"
                    "    p.addEventListener('mouseleave', function() {"
                    "      allPaths.forEach(function(q) { q.setAttribute('stroke-opacity','0.85'); q.setAttribute('stroke-width','2'); });"
                    "      tip.textContent = '';"
                    "    });"
                    "    svg.appendChild(p);"
                    "  });"
                    "}"
                    "setInterval(render, 200);"
                ]
            }

            h3 { "Stratigraphy" }
            div {
                Class "strat-wrapper"
                StratigraphyView.render selectedPin
            }
            div {
                Class "btn-row"
                Style [MarginTop "4px"]
                button {
                    selectedPin |> AVal.map (fun po ->
                        match po with
                        | Some p when p.StratigraphyDisplay = Undistorted -> Some (Class "btn-active")
                        | _ -> None)
                    Dom.OnClick(fun _ ->
                        match AVal.force selectedPin with
                        | Some p -> env.Emit [ScanPinMsg (SetStratigraphyDisplay(p.Id, Undistorted))]
                        | None -> ())
                    "Undistorted"
                }
                button {
                    selectedPin |> AVal.map (fun po ->
                        match po with
                        | Some p when p.StratigraphyDisplay = Normalized -> Some (Class "btn-active")
                        | _ -> None)
                    Dom.OnClick(fun _ ->
                        match AVal.force selectedPin with
                        | Some p -> env.Emit [ScanPinMsg (SetStratigraphyDisplay(p.Id, Normalized))]
                        | None -> ())
                    "Normalized"
                }
            }

            div {
                Class "btn-row"
                Style [MarginTop "4px"]
                button {
                    selectedPin |> AVal.map (fun po ->
                        match po with
                        | Some p when p.GhostClip = GhostClipOn -> Some (Class "btn-active")
                        | _ -> None)
                    Dom.OnClick(fun _ ->
                        match AVal.force selectedPin with
                        | Some p ->
                            let next = if p.GhostClip = GhostClipOn then GhostClipOff else GhostClipOn
                            env.Emit [ScanPinMsg (SetGhostClip(p.Id, next))]
                        | None -> ())
                    "Ghost Clip Cylinder"
                }
                button {
                    selectedPin |> AVal.map (fun po ->
                        match po with
                        | Some p when p.ExtractedLines.ShowCutPlaneLines -> Some (Class "btn-active")
                        | _ -> None)
                    Dom.OnClick(fun _ ->
                        match AVal.force selectedPin with
                        | Some p -> env.Emit [ScanPinMsg (SetShowCutPlaneLines(p.Id, not p.ExtractedLines.ShowCutPlaneLines))]
                        | None -> ())
                    "Cut Lines"
                }
                button {
                    selectedPin |> AVal.map (fun po ->
                        match po with
                        | Some p when p.ExtractedLines.ShowCylinderEdgeLines -> Some (Class "btn-active")
                        | _ -> None)
                    Dom.OnClick(fun _ ->
                        match AVal.force selectedPin with
                        | Some p -> env.Emit [ScanPinMsg (SetShowCylinderEdgeLines(p.Id, not p.ExtractedLines.ShowCylinderEdgeLines))]
                        | None -> ())
                    "Edge Lines"
                }
            }

            div {
                Class "btn-row"
                Style [MarginTop "4px"]
                button {
                    selectedPin |> AVal.map (fun po ->
                        match po with
                        | Some p when p.Explosion.Enabled -> Some (Class "btn-active")
                        | _ -> None)
                    Dom.OnClick(fun _ ->
                        match AVal.force selectedPin with
                        | Some p -> env.Emit [ScanPinMsg (SetExplosionEnabled(p.Id, not p.Explosion.Enabled))]
                        | None -> ())
                    "Explode"
                }
                input {
                    Attribute("type", "range")
                    Attribute("min", "0"); Attribute("max", "3"); Attribute("step", "0.05")
                    Style [Width "100%"]
                    selectedPin |> AVal.map (fun po ->
                        match po with
                        | Some p -> Some (Attribute("value", sprintf "%.2f" p.Explosion.ExpansionFactor))
                        | None -> None)
                    Dom.OnInput(fun e ->
                        match AVal.force selectedPin with
                        | Some p ->
                            match System.Double.TryParse(e.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture) with
                            | true, v -> env.Emit [ScanPinMsg (SetExplosionFactor(p.Id, v))]
                            | _ -> ()
                        | None -> ())
                }
            }

            h3 { "Core Sample" }

            div {
                Class "btn-row"
                button {
                    model.CoreSampleViewMode |> AVal.map (fun m ->
                        if m = SideView then Some (Class "btn-active") else None)
                    Dom.OnClick(fun _ ->
                        env.Emit [SetCoreSampleViewMode SideView; ScanPinMsg (SetCutPlaneDistance 0.0)])
                    "Side"
                }
                button {
                    model.CoreSampleViewMode |> AVal.map (fun m ->
                        if m = TopView then Some (Class "btn-active") else None)
                    Dom.OnClick(fun _ ->
                        let rot = AVal.force model.CoreSampleRotation
                        let angleDeg = (rot * Constant.DegreesPerRadian + 360.0) % 360.0
                        env.Emit [SetCoreSampleViewMode TopView; ScanPinMsg (SetCutPlaneAngle angleDeg)])
                    "Top"
                }
            }

            div {
                Class "pin-mini-wrapper"

                renderControl {
                    RenderControl.Samples 1
                    Class "pin-mini-view"

                    let! size = RenderControl.ViewportSize

                    let lastPos = cval V2i.Zero
                    let dragging = cval false

                    let clickDist (py : int) =
                        let prismOpt = AVal.force selectedPrism
                        let extFwd, extBack = match prismOpt with Some prism -> prism.ExtentForward, prism.ExtentBackward | None -> 5.0, 5.0
                        let halfH = (extFwd + extBack) / 2.0
                        let centerZ = (extBack - extFwd) / 2.0
                        let sz = AVal.force size
                        let ndcY = 1.0 - 2.0 * float py / float sz.Y
                        -(centerZ - ndcY * halfH)

                    let clickAngle (px : int) (py : int) =
                        let sz = AVal.force size
                        let dx = float px - float sz.X / 2.0
                        let dy = float sz.Y / 2.0 - float py
                        (atan2 dx dy * Constant.DegreesPerRadian + 360.0) % 360.0

                    Dom.OnPointerDown((fun e ->
                        if e.Button = Button.Left then
                            transact (fun () ->
                                lastPos.Value <- e.OffsetPosition
                                dragging.Value <- true)
                            let mode = AVal.force model.CoreSampleViewMode
                            match mode with
                            | SideView -> env.Emit [ScanPinMsg (SetCutPlaneDistance (clickDist e.OffsetPosition.Y))]
                            | TopView  -> env.Emit [ScanPinMsg (SetCutPlaneAngle (clickAngle e.OffsetPosition.X e.OffsetPosition.Y))]
                    ), pointerCapture = true)

                    Dom.OnPointerUp((fun _ ->
                        transact (fun () -> dragging.Value <- false)
                    ), pointerCapture = true)

                    Dom.OnPointerMove(fun e ->
                        if AVal.force dragging then
                            let prev = AVal.force lastPos
                            let delta = e.OffsetPosition - prev
                            transact (fun () -> lastPos.Value <- e.OffsetPosition)
                            let mode = AVal.force model.CoreSampleViewMode
                            match mode with
                            | SideView ->
                                let rot = AVal.force model.CoreSampleRotation
                                env.Emit [SetCoreSampleRotation (rot + float delta.X * -0.01)]
                                env.Emit [ScanPinMsg (SetCutPlaneDistance (clickDist e.OffsetPosition.Y))]
                            | TopView ->
                                env.Emit [ScanPinMsg (SetCutPlaneAngle (clickAngle e.OffsetPosition.X e.OffsetPosition.Y))]
                    )

                    Dom.OnContextMenu(ignore, preventDefault = true)

                    let coreTrafo = selectedPrism |> AVal.map (fun prismOpt ->
                        match prismOpt with
                        | Some prism -> coreSampleTrafo prism
                        | None -> Trafo3d.Identity)

                    let coreRadius = selectedPrism |> AVal.map (fun prismOpt ->
                        match prismOpt with
                        | Some prism -> match prism.Footprint.Vertices with v :: _ -> v.Length | _ -> 1.0
                        | None -> 1e10)

                    let coreExtents = selectedPrism |> AVal.map (fun prismOpt ->
                        match prismOpt with
                        | Some prism -> prism.ExtentForward, prism.ExtentBackward
                        | None -> 5.0, 5.0)

                    let miniView =
                        (model.CoreSampleViewMode, model.CoreSampleRotation, coreExtents) |||> AVal.map3 (fun mode rot (extFwd, extBack) ->
                            let dist = 100.0
                            let centerZ = (extBack - extFwd) / 2.0
                            match mode with
                            | SideView ->
                                let dir = V3d(cos rot, sin rot, 0.0)
                                let r = Vec.cross V3d.OOI dir |> Vec.normalize
                                let eye = dir * dist + V3d(0.0, 0.0, centerZ)
                                CameraView(-V3d.OOI, eye, -dir, -V3d.OOI, r) |> CameraView.viewTrafo
                            | TopView ->
                                let eye = V3d(0.0, 0.0, dist)
                                CameraView(V3d.OOI, eye, -V3d.OOI, V3d.IOO, V3d.OIO) |> CameraView.viewTrafo
                        )

                    let camDist = 100.0

                    let miniProj =
                        (model.CoreSampleViewMode, coreRadius, coreExtents) |||> AVal.map3 (fun mode r (extFwd, extBack) ->
                            let halfH = (extFwd + extBack) / 2.0
                            match mode with
                            | TopView ->
                                Frustum.ortho (Box3d(V3d(-r, -r, camDist - halfH - 0.5), V3d(r, r, camDist + halfH + 0.5))) |> Frustum.projTrafo
                            | SideView ->
                                Frustum.ortho (Box3d(V3d(-r, -halfH, camDist - r - 0.5), V3d(r, halfH, camDist + r + 0.5))) |> Frustum.projTrafo
                        )

                    Sg.View miniView
                    Sg.Proj miniProj

                    let depthOn = model.DepthShadeOn |> AVal.map (fun b -> if b then 1 else 0)
                    let isoOn =
                        (model.IsolinesOn, model.CoreSampleViewMode) ||> AVal.map2 (fun on mode ->
                            if on && mode <> SideView then 1 else 0)
                    let wireVisible =
                        model.CoreSampleViewMode |> AVal.map (fun m -> m <> SideView)

                    let meshIndices =
                        model.MeshNames |> AList.toAVal |> AVal.map (fun names ->
                            names |> Seq.mapi (fun i a -> a, i) |> Map.ofSeq)

                    model.MeshNames |> AList.toASet |> ASet.map (fun name ->
                        let loaded = MeshView.loadMeshAsync ignore name
                        let dataset = name.Split('/', 2).[0]
                        let scale = model.DatasetScales |> AVal.map (fun m -> Map.tryFind dataset m |> Option.defaultValue 1.0)
                        let meshTrafo =
                            (model.CommonCentroid, loaded.centroid, scale) |||> AVal.map3 (fun common mesh s ->
                                Trafo3d.Translation(mesh - common) * Trafo3d.Scale(s))
                        let trafo = (coreTrafo, meshTrafo) ||> AVal.map2 (fun core mt -> mt*core)
                        let meshIdx = meshIndices |> AVal.map (fun m -> Map.tryFind name m |> Option.defaultValue 0)
                        sg {
                            Sg.Trafo trafo
                            Sg.Shader {
                                DefaultSurfaces.trafo
                                DefaultSurfaces.diffuseTexture
                                Shader.headlight
                                BlitShader.coreClip
                                Shader.depthShade
                                Shader.isolines
                            }
                            Sg.Uniform("DiffuseColorTexture", loaded.tex)
                            Sg.Uniform("CoreRadius", coreRadius)
                            Sg.Uniform("MeshIndex", meshIdx)
                            Sg.Uniform("ColorMode", model.ColorMode |> AVal.map (fun b -> if b then 1 else 0))
                            Sg.Uniform("DepthShadeOn", depthOn)
                            Sg.Uniform("IsolinesOn", isoOn)
                            Sg.Uniform("IsolineSpacing", AVal.constant 1.0)
                            Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
                            Sg.NoEvents
                            Sg.VertexAttributes(
                                HashMap.ofList [
                                    string DefaultSemantic.Positions, BufferView(loaded.pos, typeof<V3f>)
                                    string DefaultSemantic.DiffuseColorCoordinates, BufferView(loaded.tc, typeof<V2f>)
                                    string DefaultSemantic.Normals,   BufferView(loaded.nrm, typeof<V3f>)
                                ])
                            Sg.Active(loaded.fvc |> AVal.map (fun c -> c > 3))
                            Sg.Index(BufferView(loaded.idx, typeof<int>))
                            Sg.Render loaded.fvc
                        }
                    )

                    // Summary meshes (average, Q1, Q3) from grid-eval
                    let summaryMeshes =
                        (selectedPrism, selectedGridEval) ||> AVal.map2 (fun prismOpt geOpt ->
                            match prismOpt, geOpt with
                            | Some prism, Some ge ->
                                let t = coreSampleTrafo prism
                                let buildMesh (heights : float[]) =
                                    let pos, nrm, idx = PinGeometry.buildHeightfieldMesh ge.GridOrigin ge.CellSize ge.Resolution heights
                                    let pos = pos |> Array.map (fun p -> V3f(t.Forward.TransformPos(V3d p)))
                                    let nrm = nrm |> Array.map (fun n -> V3f(t.Forward.TransformDir(V3d n) |> Vec.normalize))
                                    pos, nrm, idx
                                let avg = buildMesh (ge.Cells |> Array.map (fun c -> c.Average))
                                let q1  = buildMesh (ge.Cells |> Array.map (fun c -> c.Q1))
                                let q3  = buildMesh (ge.Cells |> Array.map (fun c -> c.Q3))
                                Some (avg, q1, q3)
                            | _ -> None)

                    let summaryEntries = [
                        (fun (avg, _, _) -> avg), C4f(0.65f, 0.65f, 0.65f, 1.0f)
                        (fun (_, _, q3) -> q3),   C4f(0.95f, 0.55f, 0.15f, 0.45f)
                        (fun (_, q1, _) -> q1),   C4f(0.2f, 0.45f, 0.9f, 0.45f)
                    ]
                    for (selector, color) in summaryEntries do
                        let meshData = summaryMeshes |> AVal.map (fun opt ->
                            match opt with
                            | Some data -> selector data
                            | None -> [||], [||], [||])
                        let sPos = meshData |> AVal.map (fun (p, _, _) -> ArrayBuffer p :> IBuffer)
                        let sNrm = meshData |> AVal.map (fun (_, n, _) -> ArrayBuffer n :> IBuffer)
                        let sIdx = meshData |> AVal.map (fun (_, _, i) -> ArrayBuffer i :> IBuffer)
                        let sFvc = meshData |> AVal.map (fun (_, _, i) -> i.Length)
                        let active = summaryMeshes |> AVal.map Option.isSome
                        sg {
                            Sg.Active active
                            Sg.Shader {
                                DefaultSurfaces.trafo
                                DefaultSurfaces.constantColor color
                                BlitShader.coreClip
                                Shader.depthShade
                                Shader.isolines
                            }
                            Sg.Uniform("CoreRadius", coreRadius)
                            Sg.Uniform("DepthShadeOn", depthOn)
                            Sg.Uniform("IsolinesOn", isoOn)
                            Sg.Uniform("IsolineSpacing", AVal.constant 1.0)
                            Sg.BlendMode (AVal.constant BlendMode.Blend)
                            Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
                            Sg.NoEvents
                            Sg.VertexAttributes(
                                HashMap.ofList [
                                    string DefaultSemantic.Positions, BufferView(sPos, typeof<V3f>)
                                    string DefaultSemantic.Normals,   BufferView(sNrm, typeof<V3f>)
                                ])
                            Sg.Index(BufferView(sIdx, typeof<int>))
                            Sg.Render sFvc
                        }

                    let wireData = selectedPrism |> AVal.map (fun prismOpt ->
                        match prismOpt with
                        | Some prism ->
                            let pos, idx = Revolver.buildPrismWireframe prism 0.05
                            let t = coreSampleTrafo prism
                            let pos = pos |> Array.map (fun p -> V3f(t.Forward.TransformPos(V3d p)))
                            pos, idx
                        | None -> [||], [||])
                    let wirePos = wireData |> AVal.map (fun (p, _) -> ArrayBuffer p :> IBuffer)
                    let wireIdx = wireData |> AVal.map (fun (_, i) -> ArrayBuffer i :> IBuffer)
                    let wireFvc = wireData |> AVal.map (fun (_, i) -> i.Length)
                    sg {
                        Sg.Active wireVisible
                        Sg.Shader { DefaultSurfaces.trafo; Shader.flatColor }
                        Sg.Uniform("FlatColor", AVal.constant (V4d(1.0, 0.85, 0.0, 0.7)))
                        Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
                        Sg.NoEvents
                        Sg.VertexAttributes(
                            HashMap.ofList [ string DefaultSemantic.Positions, BufferView(wirePos, typeof<V3f>) ])
                        Sg.Index(BufferView(wireIdx, typeof<int>))
                        Sg.Render wireFvc
                    }

                    let planeData = (selectedPrism, selectedCutPlane) ||> AVal.map2 (fun prismOpt cpOpt ->
                        match prismOpt, cpOpt with
                        | Some prism, Some cutPlane ->
                            let quad, _ = PinGeometry.buildCutPlaneQuad prism cutPlane
                            let t = coreSampleTrafo prism
                            let quad = quad |> Array.map (fun p -> V3f(t.Forward.TransformPos(V3d p)))
                            let n =
                                let v0 = V3d quad.[0]
                                let v1 = V3d quad.[1]
                                let v2 = V3d quad.[2]
                                Vec.cross (v1 - v0) (v2 - v0) |> Vec.normalize
                            let thick = 0.15
                            let top = quad |> Array.map (fun p -> V3f(V3d p + n * thick))
                            let bot = quad |> Array.map (fun p -> V3f(V3d p - n * thick))
                            let pos = Array.append top bot
                            let idx = [| 0;1;2; 0;2;3; 4;6;5; 4;7;6
                                         0;4;5; 0;5;1; 2;6;7; 2;7;3
                                         0;3;7; 0;7;4; 1;5;6; 1;6;2 |]
                            pos, idx
                        | _ -> [||], [||])
                    let planePos = planeData |> AVal.map (fun (p, _) -> ArrayBuffer p :> IBuffer)
                    let planeIdx = planeData |> AVal.map (fun (_, i) -> ArrayBuffer i :> IBuffer)
                    let planeFvc = planeData |> AVal.map (fun (_, i) -> i.Length)
                    sg {
                        Sg.Shader { DefaultSurfaces.trafo; Shader.flatColor }
                        Sg.Uniform("FlatColor", AVal.constant (V4d(1.0, 0.9, 0.3, 0.45)))
                        Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
                        Sg.BlendMode (AVal.constant BlendMode.Blend)
                        Sg.NoEvents
                        Sg.VertexAttributes(
                            HashMap.ofList [ string DefaultSemantic.Positions, BufferView(planePos, typeof<V3f>) ])
                        Sg.Index(BufferView(planeIdx, typeof<int>))
                        Sg.Render planeFvc
                    }
                }

                // Cut plane indicator overlay
                div {
                    Class "pin-cut-indicator"
                    (selectedPrism, selectedCutPlane, model.CoreSampleViewMode) |||> AVal.map3 (fun prismOpt cpOpt mode ->
                        match prismOpt, cpOpt with
                        | Some prism, Some cutPlane ->
                            let extFwd = prism.ExtentForward
                            let extBack = prism.ExtentBackward
                            let halfH = (extFwd + extBack) / 2.0
                            let centerZ = (extBack - extFwd) / 2.0
                            match cutPlane, mode with
                            | CutPlaneMode.AcrossAxis dist, SideView ->
                                let ndcY = (dist + centerZ) / halfH
                                let pct = (1.0 - ndcY) / 2.0 * 100.0
                                Some (Attribute("data-ind", sprintf "top:%.2f%%" pct))
                            | CutPlaneMode.AlongAxis angleDeg, TopView ->
                                Some (Attribute("data-ind", sprintf "top:calc(50%% - 1px);transform-origin:center;transform:rotate(%.1fdeg)" (90.0 - angleDeg)))
                            | _ -> Some (Attribute("data-ind", "display:none"))
                        | _ -> Some (Attribute("data-ind", "display:none"))
                    )
                    OnBoot [
                        "var el = __THIS__;"
                        "function apply() { el.style.cssText = el.getAttribute('data-ind') || 'display:none'; }"
                        "apply();"
                        "new MutationObserver(function() { apply(); }).observe(el, {attributes:true, attributeFilter:['data-ind']});"
                    ]
                }
            }

            // Dataset ranking section (merged from PinDemo prototype).
            // Sources stats from the selected pin's GridEval; ordering is global UI state.
            div {
                Class "rank-section"
                h3 { "Datasets" }
                div {
                    Class "rank-controls"
                    label {
                        "Top K "
                        input {
                            Attribute("type", "number")
                            Attribute("min", "1")
                            Style [Width "48px"]
                            RankingState.topK |> AVal.map (fun k -> Some (Attribute("value", string k)))
                            Dom.OnInput(fun e ->
                                match System.Int32.TryParse(e.Value) with
                                | true, k when k >= 1 -> transact (fun () -> RankingState.topK.Value <- k)
                                | _ -> ())
                        }
                    }
                    label {
                        input {
                            Attribute("type", "checkbox")
                            RankingState.rankFadeOn |> AVal.map (fun on ->
                                if on then Some (Attribute("checked", "checked")) else None)
                            Dom.OnChange(fun _ ->
                                transact (fun () -> RankingState.rankFadeOn.Value <- not RankingState.rankFadeOn.Value))
                        }
                        "Rank fade"
                    }
                }
                div {
                    Class "rank-list"
                    selectedGridEval |> AVal.map (fun geOpt ->
                        let names =
                            match geOpt with
                            | Some ge -> ge.DatasetStats |> Array.map (fun s -> s.MeshName) |> Array.toList
                            | None -> []
                        RankingState.ensureDatasets names
                        Some (Attribute("data-names", System.String.Join("|", names))))
                    RankingState.datasetOrder
                    |> AVal.map IndexList.ofList
                    |> AList.ofAVal
                    |> AList.map (fun name ->
                        let statsOpt =
                            selectedGridEval |> AVal.map (fun geOpt ->
                                match geOpt with
                                | Some ge -> ge.DatasetStats |> Array.tryFind (fun s -> s.MeshName = name)
                                | None -> None)
                        let isHidden = RankingState.datasetHidden |> ASet.contains name
                        let rank = RankingState.rankOf name
                        let inK = RankingState.inTopK name
                        div {
                            (isHidden, inK) ||> AVal.map2 (fun h k ->
                                let cls =
                                    if h then "rank-row hidden"
                                    elif not k then "rank-row out-of-topk"
                                    else "rank-row"
                                Some (Class cls))
                            div {
                                Class "rank-badge"
                                rank |> AVal.map (fun r ->
                                    match r with
                                    | Some i -> sprintf "#%d" (i + 1)
                                    | None -> "—")
                            }
                            div {
                                Class "rank-name"
                                shortName name
                            }
                            div {
                                Class "rank-variance"
                                statsOpt |> AVal.map (fun so ->
                                    match so with
                                    | Some s -> sprintf "σ=%.1f" (sqrt s.ZVariance)
                                    | None -> "—")
                            }
                            div {
                                Class "rank-buttons"
                                button {
                                    Class "rank-move"
                                    Dom.OnClick(fun _ -> RankingState.move name -1)
                                    "▲"
                                }
                                button {
                                    Class "rank-move"
                                    Dom.OnClick(fun _ -> RankingState.move name 1)
                                    "▼"
                                }
                                button {
                                    Class "rank-toggle"
                                    Dom.OnClick(fun _ -> RankingState.toggleHidden name)
                                    isHidden |> AVal.map (fun h -> if h then "Show" else "Hide")
                                }
                            }
                        }
                    )
                }
            }

            div {
                Class "effect-toggles"
                label {
                    input {
                        Attribute("type", "checkbox")
                        model.DepthShadeOn |> AVal.map (fun v ->
                            if v then Some (Attribute("checked", "checked")) else None)
                        Dom.OnChange(fun _ -> env.Emit [ToggleDepthShade])
                    }
                    "Depth"
                }
                label {
                    input {
                        Attribute("type", "checkbox")
                        model.IsolinesOn |> AVal.map (fun v ->
                            if v then Some (Attribute("checked", "checked")) else None)
                        Dom.OnChange(fun _ -> env.Emit [ToggleIsolines])
                    }
                    "Isolines"
                }
                label {
                    input {
                        Attribute("type", "checkbox")
                        model.ColorMode |> AVal.map (fun v ->
                            if v then Some (Attribute("checked", "checked")) else None)
                        Dom.OnChange(fun _ -> env.Emit [ToggleColorMode])
                    }
                    "Color"
                }
            }

        }
