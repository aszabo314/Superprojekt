namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open FSharp.Data.Adaptive
open Aardvark.Dom

module GuiPins =

    let private coreSampleTrafo (prism : SelectionPrism) =
        let axis = prism.AxisDirection |> Vec.normalize
        let up = if abs axis.Z > 0.9 then V3d.OIO else V3d.OOI
        let right = Vec.cross axis up |> Vec.normalize
        let fwd = Vec.cross right axis |> Vec.normalize
        let rotFwd = M44d(right.X, right.Y, right.Z, 0.0,
                          fwd.X,   fwd.Y,   fwd.Z,   0.0,
                          axis.X,  axis.Y,  axis.Z,  0.0,
                          0.0,     0.0,     0.0,     1.0)
        Trafo3d.Translation(-prism.AnchorPoint) * Trafo3d(rotFwd, rotFwd.Transposed)

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

                h3 { "Cut plane" }
                div {
                    Class "btn-row"
                    button {
                        activePin |> AVal.map (fun p ->
                            match p with
                            | Some pin -> match pin.CutPlane with CutPlaneMode.AlongAxis _ -> Some (Class "btn-active") | _ -> None
                            | None -> None)
                        Dom.OnClick(fun _ -> env.Emit [ScanPinMsg (SetCutPlaneAngle 0.0)])
                        "Along axis"
                    }
                    button {
                        activePin |> AVal.map (fun p ->
                            match p with
                            | Some pin -> match pin.CutPlane with CutPlaneMode.AcrossAxis _ -> Some (Class "btn-active") | _ -> None
                            | None -> None)
                        Dom.OnClick(fun _ -> env.Emit [ScanPinMsg (SetCutPlaneDistance 0.0)])
                        "Across axis"
                    }
                }
                div {
                    activePin |> AVal.map (fun p ->
                        match p with
                        | Some pin -> match pin.CutPlane with CutPlaneMode.AlongAxis _ -> None | _ -> Some (Style [Display "none"])
                        | None -> Some (Style [Display "none"]))
                    "Angle  "
                    input {
                        Attribute("type", "range")
                        Attribute("min", "0"); Attribute("max", "360"); Attribute("step", "1")
                        Style [Width "100%"]
                        activePin |> AVal.map (fun p ->
                            match p with
                            | Some pin ->
                                match pin.CutPlane with
                                | CutPlaneMode.AlongAxis a -> Some (Attribute("value", sprintf "%.0f" a))
                                | _ -> Some (Attribute("value", "0"))
                            | None -> None)
                        Dom.OnInput(fun e ->
                            match System.Double.TryParse(e.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture) with
                            | true, v -> env.Emit [ScanPinMsg (SetCutPlaneAngle v)]
                            | _ -> ())
                    }
                }
                div {
                    activePin |> AVal.map (fun p ->
                        match p with
                        | Some pin -> match pin.CutPlane with CutPlaneMode.AcrossAxis _ -> None | _ -> Some (Style [Display "none"])
                        | None -> Some (Style [Display "none"]))
                    "Distance  "
                    input {
                        Attribute("type", "range")
                        Attribute("min", "-100"); Attribute("max", "100"); Attribute("step", "0.5")
                        Style [Width "100%"]
                        activePin |> AVal.map (fun p ->
                            match p with
                            | Some pin ->
                                match pin.CutPlane with
                                | CutPlaneMode.AcrossAxis d -> Some (Attribute("value", sprintf "%.1f" d))
                                | _ -> Some (Attribute("value", "0"))
                            | None -> None)
                        Dom.OnInput(fun e ->
                            match System.Double.TryParse(e.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture) with
                            | true, v -> env.Emit [ScanPinMsg (SetCutPlaneDistance v)]
                            | _ -> ())
                    }
                }
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
            sp.Pins |> AMap.toASet |> ASet.sortBy (fun (_, p) -> p.Id) |> AList.map (fun (id, pin) ->
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
                        let p = pin.Prism.AnchorPoint
                        let phase = if pin.Phase = PinPhase.Placement then " [placing]" else ""
                        sprintf "(%.1f, %.1f, %.1f)%s" p.X p.Y p.Z phase
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
        let selectedPin =
            (model.ScanPins.SelectedPin, model.ScanPins.Pins |> AMap.toAVal) ||> AVal.map2 (fun sel pins ->
                sel |> Option.bind (fun id -> HashMap.tryFind id pins))

        let svgW, svgH = 280.0, 180.0
        let pad = 30.0

        let screenPos =
            (selectedPin, viewTrafo, vpSize) |||> AVal.map3 (fun pinOpt (vt : Trafo3d) sz ->
                match pinOpt with
                | None -> None
                | Some pin ->
                    let pt = pin.Prism.AnchorPoint
                    let aspect = float sz.X / max 1.0 (float sz.Y)
                    let proj = Frustum.perspective 90.0 0.5 1000.0 aspect |> Frustum.projTrafo
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
            selectedPin |> AVal.map (fun p ->
                if p.IsNone then Some (Style [Display "none"]) else None)
            screenPos |> AVal.map (fun pos ->
                match pos with
                | Some p ->
                    Some (Style [
                        Left (sprintf "%.0fpx" (p.X + 20.0))
                        Top (sprintf "%.0fpx" (p.Y - 120.0))
                    ])
                | None -> None)

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
                    "var el = __THIS__;"
                    "function render() {"
                    "  el.innerHTML = '';"
                    "  try { var data = JSON.parse(el.getAttribute('data-diagram') || '{}'); } catch(e) { return; }"
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
                    "  if(data.legend && data.legend.length > 0) {"
                    "    var leg = document.createElement('div');"
                    "    leg.style.marginTop = '6px';"
                    "    leg.style.fontSize = '11px';"
                    "    data.legend.forEach(function(item) {"
                    "      var row = document.createElement('div');"
                    "      row.style.display = 'flex';"
                    "      row.style.alignItems = 'center';"
                    "      row.style.gap = '4px';"
                    "      row.style.marginBottom = '2px';"
                    "      var sw = document.createElement('div');"
                    "      sw.style.width = '14px';"
                    "      sw.style.height = '3px';"
                    "      sw.style.background = item.c;"
                    "      sw.style.flexShrink = '0';"
                    "      row.appendChild(sw);"
                    "      row.appendChild(document.createTextNode(item.n));"
                    "      leg.appendChild(row);"
                    "    });"
                    "    el.appendChild(leg);"
                    "  }"
                    "}"
                    "render();"
                    "var obs = new MutationObserver(function(m) {"
                    "  for(var i=0;i<m.length;i++) if(m[i].attributeName==='data-diagram') { render(); break; }"
                    "});"
                    "obs.observe(el, {attributes:true});"
                ]
            }

            h3 { "Core Sample" }

            renderControl {
                RenderControl.Samples 1
                Class "pin-mini-view"

                OrbitController.getAttributes (Env.map PinViewCameraMessage env)

                let! size = RenderControl.ViewportSize

                RenderControl.OnRendered(fun _ ->
                    env.Emit [PinViewCameraMessage OrbitMessage.Rendered]
                )

                let miniView = model.PinViewCamera.view |> AVal.map CameraView.viewTrafo
                let miniProj =
                    size |> AVal.map (fun s ->
                        Frustum.perspective 60.0 0.5 5000.0 (float s.X / float s.Y) |> Frustum.projTrafo)

                Sg.View miniView
                Sg.Proj miniProj

                let coreTrafo = selectedPin |> AVal.map (fun pinOpt ->
                    match pinOpt with
                    | Some pin -> coreSampleTrafo pin.Prism
                    | None -> Trafo3d.Identity)

                let coreRadius = selectedPin |> AVal.map (fun pinOpt ->
                    match pinOpt with
                    | Some pin -> match pin.Prism.Footprint.Vertices with v :: _ -> v.Length | _ -> 1.0
                    | None -> 1e10)

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
                            BlitShader.coreClip
                        }
                        Sg.Uniform("DiffuseColorTexture", loaded.tex)
                        Sg.Uniform("CoreRadius", coreRadius)
                        Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
                        Sg.NoEvents
                        Sg.VertexAttributes(
                            HashMap.ofList [
                                string DefaultSemantic.Positions, BufferView(loaded.pos, typeof<V3f>)
                                string DefaultSemantic.DiffuseColorCoordinates, BufferView(loaded.tc, typeof<V2f>)
                            ])
                        Sg.Active(loaded.fvc |> AVal.map (fun c -> c > 3))
                        Sg.Index(BufferView(loaded.idx, typeof<int>))
                        Sg.Render loaded.fvc
                    }
                )

                let wireData = selectedPin |> AVal.map (fun pinOpt ->
                    match pinOpt with
                    | Some pin ->
                        let pos, idx = Revolver.buildPrismWireframe pin.Prism 0.05
                        let t = coreSampleTrafo pin.Prism
                        let pos = pos |> Array.map (fun p -> V3f(t.Forward.TransformPos(V3d p)))
                        pos, idx
                    | None -> [||], [||])
                let wirePos = wireData |> AVal.map (fun (p, _) -> ArrayBuffer p :> IBuffer)
                let wireIdx = wireData |> AVal.map (fun (_, i) -> ArrayBuffer i :> IBuffer)
                let wireFvc = wireData |> AVal.map (fun (_, i) -> i.Length)
                sg {
                    Sg.Shader { DefaultSurfaces.trafo; Shader.flatColor }
                    Sg.Uniform("FlatColor", AVal.constant (V4d(1.0, 0.85, 0.0, 0.7)))
                    Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
                    Sg.NoEvents
                    Sg.VertexAttributes(
                        HashMap.ofList [ string DefaultSemantic.Positions, BufferView(wirePos, typeof<V3f>) ])
                    Sg.Index(BufferView(wireIdx, typeof<int>))
                    Sg.Render wireFvc
                }

                let planeData = selectedPin |> AVal.map (fun pinOpt ->
                    match pinOpt with
                    | Some pin ->
                        let pos, idx = Revolver.buildCutPlaneQuad pin.Prism pin.CutPlane
                        let t = coreSampleTrafo pin.Prism
                        let pos = pos |> Array.map (fun p -> V3f(t.Forward.TransformPos(V3d p)))
                        pos, idx
                    | None -> [||], [||])
                let planePos = planeData |> AVal.map (fun (p, _) -> ArrayBuffer p :> IBuffer)
                let planeIdx = planeData |> AVal.map (fun (_, i) -> ArrayBuffer i :> IBuffer)
                let planeFvc = planeData |> AVal.map (fun (_, i) -> i.Length)
                sg {
                    Sg.Shader { DefaultSurfaces.trafo; Shader.flatColor }
                    Sg.Uniform("FlatColor", AVal.constant (V4d(1.0, 0.9, 0.3, 0.25)))
                    Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
                    Sg.BlendMode BlendMode.Blend
                    Sg.NoEvents
                    Sg.VertexAttributes(
                        HashMap.ofList [ string DefaultSemantic.Positions, BufferView(planePos, typeof<V3f>) ])
                    Sg.Index(BufferView(planeIdx, typeof<int>))
                    Sg.Render planeFvc
                }
            }
        }
