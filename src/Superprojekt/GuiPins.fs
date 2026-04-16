namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open FSharp.Data.Adaptive
open Aardvark.Dom

module GuiPins =

    let shortName = Cards.shortName
    let private encodeDiagramJson = Cards.encodeDiagramJson

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
                        Class "range-full"
                        activePin |> AVal.map (fun p ->
                            match p with
                            | Some pin ->
                                match pin.Prism.Footprint.Vertices with
                                | v :: _ -> Some (Attribute("value", sprintf "%.1f" v.Length))
                                | _ -> Some (Attribute("value", "1"))
                            | None -> None)
                        Dom.OnInput(fun e ->
                            Cards.parseFloat e.Value |> Option.iter (fun v -> env.Emit [ScanPinMsg (SetFootprintRadius v)]))
                    }
                }
                div {
                    "Length  "
                    input {
                        Attribute("type", "range")
                        Attribute("min", "0.5"); Attribute("max", "100"); Attribute("step", "0.5")
                        Class "range-full"
                        activePin |> AVal.map (fun p ->
                            match p with
                            | Some pin -> Some (Attribute("value", sprintf "%.1f" pin.Prism.ExtentForward))
                            | None -> None)
                        Dom.OnInput(fun e ->
                            Cards.parseFloat e.Value |> Option.iter (fun v -> env.Emit [ScanPinMsg (SetPinLength v)]))
                    }
                }
                div {
                    Class "btn-row mt-6"
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
            let guiPinsVal = sp.Pins |> AMap.toAVal
            let pinIdList =
                guiPinsVal
                |> AVal.map (fun pins -> pins |> HashMap.toSeq |> Seq.map fst |> Seq.sort |> IndexList.ofSeq)
                |> AList.ofAVal
            pinIdList |> AList.map (fun id ->
                let pinVal = guiPinsVal |> AVal.map (fun pins -> HashMap.tryFind id pins)
                let isSelected = sp.SelectedPin |> AVal.map (fun sel -> sel = Some id)
                div {
                    Class "pin-item"
                    isSelected |> AVal.map (fun s ->
                        if s then Some (Class "pin-item-selected") else None)
                    div {
                        Dom.OnClick(fun _ ->
                            let sel = AVal.force sp.SelectedPin
                            if sel = Some id then env.Emit [ScanPinMsg (SelectPin None)]
                            else env.Emit [ScanPinMsg (SelectPin (Some id))])
                        Class "pin-item-label"
                        pinVal |> AVal.map (fun pinOpt ->
                            match pinOpt with
                            | Some pin ->
                                let p = pin.Prism.AnchorPoint
                                let phase = if pin.Phase = PinPhase.Placement then " [placing]" else ""
                                sprintf "(%.1f, %.1f, %.1f)%s" p.X p.Y p.Z phase
                            | None -> "(removed)")
                    }
                    div {
                        Class "btn-row mt-4"
                        button {
                            Class "btn-sm"
                            Dom.OnClick(fun _ -> env.Emit [ScanPinMsg (FocusPin id)])
                            "Focus"
                        }
                        button {
                            Class "btn-sm"
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
            (model.ScanPins.SelectedPin, model.ScanPins.ActivePlacement, allPinsVal)
            |||> AVal.map3 (fun sel act pins ->
                let id = act |> Option.orElse sel
                id |> Option.bind (fun id -> HashMap.tryFind id pins))

        let selectedPrism    = selectedPin |> AVal.map (Option.map (fun p -> p.Prism))

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
                let isPlacing = (model.ScanPins.PlacingMode, model.ScanPins.ActivePlacement) ||> AVal.map2 (fun pm ap -> pm.IsSome || ap.IsSome)
                StratigraphyView.render env isPlacing selectedPin
            }
            div {
                Class "btn-row mt-4"
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
                Class "btn-row mt-4"
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
                Class "btn-row mt-4"
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
                    Class "range-full"
                    selectedPin |> AVal.map (fun po ->
                        match po with
                        | Some p -> Some (Attribute("value", sprintf "%.2f" p.Explosion.ExpansionFactor))
                        | None -> None)
                    Dom.OnInput(fun e ->
                        match AVal.force selectedPin, Cards.parseFloat e.Value with
                        | Some p, Some v -> env.Emit [ScanPinMsg (SetExplosionFactor(p.Id, v))]
                        | _ -> ())
                }
            }

            div {
                Class "effect-toggles"
                label {
                    input {
                        Attribute("type", "checkbox")
                        Cards.checkedIf model.DepthShadeOn
                        Dom.OnChange(fun _ -> env.Emit [ToggleDepthShade])
                    }
                    "Depth"
                }
                label {
                    input {
                        Attribute("type", "checkbox")
                        Cards.checkedIf model.IsolinesOn
                        Dom.OnChange(fun _ -> env.Emit [ToggleIsolines])
                    }
                    "Isolines"
                }
                label {
                    input {
                        Attribute("type", "checkbox")
                        Cards.checkedIf model.ColorMode
                        Dom.OnChange(fun _ -> env.Emit [ToggleColorMode])
                    }
                    "Color"
                }
            }

        }
