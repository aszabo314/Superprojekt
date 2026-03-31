namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open FSharp.Data.Adaptive
open Aardvark.Dom

module Gui =

    let private shortName (name : string) =
        let mesh =
            let s = name.IndexOf('/')
            if s >= 0 then name.[s + 1 ..] else name
        if mesh.Length > 8 && mesh.[8] = '_' then
            let date = mesh.[..7]
            let si = mesh.LastIndexOf("_seg")
            if si > 0 then date + "_" + mesh.[si + 1 ..] else date
        else mesh

    let burgerButton (env : Env<Message>) =
        button {
            Attribute("id", "burger-btn")
            Class "burger-btn"
            Dom.OnClick(fun _ -> env.Emit [ToggleMenu])
            div { Class "burger-line" }
            div { Class "burger-line" }
            div { Class "burger-line" }
        }

    let hudTabs (env : Env<Message>) (model : AdaptiveModel) =
        div {
            Class "tabs"
            model.MenuOpen |> AVal.map (fun o ->
                if o then Some (Class "tabs-open") else None
            )

            div {
                Class "tab-labels"
                label {
                    input {
                        Attribute("type",    "radio")
                        Attribute("name",    "hud-tabs")
                        Attribute("id",      "hud-tab1")
                        Attribute("checked", "checked")
                    }
                    "Scene"
                }
                label {
                    input {
                        Attribute("type", "radio")
                        Attribute("name", "hud-tabs")
                        Attribute("id",   "hud-tab2")
                    }
                    "Overlay"
                }
                label {
                    input {
                        Attribute("type", "radio")
                        Attribute("name", "hud-tabs")
                        Attribute("id",   "hud-tab3")
                    }
                    "Clip"
                }
                label {
                    input {
                        Attribute("type", "radio")
                        Attribute("name", "hud-tabs")
                        Attribute("id",   "hud-tab4")
                    }
                    "Pins"
                }
            }

            div {
                Class "tab-panels"

                div {
                    Class "tab-panel"
                    Attribute("id", "hud-panel1")

                    h3 { "Dataset" }
                    div {
                        Class "btn-row"
                        model.Datasets |> AVal.map IndexList.ofList |> AList.ofAVal |> AList.map (fun dataset ->
                            let isActive = model.ActiveDataset |> AVal.map (fun a -> a = Some dataset)
                            let tooltip =
                                (model.DatasetCentroids, model.DatasetScales) ||> AVal.map2 (fun centroids scales ->
                                    let cStr =
                                        match Map.tryFind dataset centroids with
                                        | Some v -> sprintf "(%.0f, %.0f, %.0f)" v.X v.Y v.Z
                                        | None   -> "not loaded"
                                    let s = Map.tryFind dataset scales |> Option.defaultValue 1.0
                                    sprintf "centroid: %s\nscale: %.3g" cStr s
                                )
                            button {
                                isActive |> AVal.map (fun on -> if on then Some (Class "btn-active") else None)
                                tooltip |> AVal.map (fun t -> Some (Attribute("title", t)))
                                Dom.OnClick(fun _ ->
                                    env.Emit [SetActiveDataset dataset]
                                    ServerActions.loadDataset env dataset
                                )
                                dataset
                            }
                        )
                    }

                    div {
                        "Scale  "
                        input {
                            Attribute("type", "number")
                            Attribute("step", "0.1")
                            Style [Width "80px"]
                            (model.ActiveDataset, model.DatasetScales) ||> AVal.map2 (fun ds scales ->
                                let s = ds |> Option.bind (fun d -> Map.tryFind d scales) |> Option.defaultValue 1.0
                                Some (Attribute("value", sprintf "%.4g" s))
                            )
                            model.ActiveDataset |> AVal.map (fun ds ->
                                if ds.IsNone then Some (Attribute("disabled", "disabled")) else None
                            )
                            Dom.OnInput(fun e ->
                                match System.Double.TryParse(e.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture) with
                                | true, v ->
                                    match AVal.force model.ActiveDataset with
                                    | Some dataset -> env.Emit [SetDatasetScale(dataset, v)]
                                    | None -> ()
                                | _ -> ()
                            )
                        }
                    }

                    h3 { "Meshes" }
                    model.MeshNames |> AList.map (fun name ->
                        let isVis =
                            model.MeshVisible
                            |> AVal.map (fun m -> Map.tryFind name m |> Option.defaultValue true)
                        div {
                            label {
                                input {
                                    Attribute("type", "checkbox")
                                    isVis |> AVal.map (fun v ->
                                        if v then Some (Attribute("checked", "checked")) else None
                                    )
                                    Dom.OnClick(fun _ ->
                                        let current = AVal.force isVis
                                        env.Emit [SetVisible(name, not current)]
                                    )
                                }
                                " " + shortName name
                            }
                        }
                    )

                    div {
                        label {
                            input {
                                Attribute("type", "checkbox")
                                model.GhostSilhouette |> AVal.map (fun on ->
                                    if on then Some (Attribute("checked", "checked")) else None
                                )
                                Dom.OnClick(fun _ -> env.Emit [ToggleGhostSilhouette])
                            }
                            " Ghost silhouette"
                        }
                    }

                    button { "Clear Filter"; Dom.OnClick(fun _ -> env.Emit [ClearFilteredMesh]) }

                    ul {
                        li { "ctrl+click or long-press to filter" }
                        li { "double-click to focus camera" }
                        li { "shift+scroll to cycle mesh order" }
                    }
                }

                div {
                    Class "tab-panel"
                    Attribute("id", "hud-panel2")

                    h3 { "Revolver" }
                    p { "Magnifier disks per mesh (also: hold Shift)" }
                    button {
                        model.RevolverOn |> AVal.map (fun on ->
                            if on then Some (Class "btn-active") else None
                        )
                        Dom.OnClick(fun _ -> env.Emit [ToggleRevolver])
                        model.RevolverOn |> AVal.map (fun on ->
                            if on then "Revolver: ON" else "Revolver: OFF"
                        )
                    }
                    p {
                        model.RevolverOn |> AVal.map (fun on ->
                            if on then "Tap on the 3D view to reposition the magnifier." else ""
                        )
                    }

                    h3 { "Fullscreen overlay" }
                    p { "Show each mesh in a tile (also: hold Space)" }
                    button {
                        model.FullscreenOn |> AVal.map (fun on ->
                            if on then Some (Class "btn-active") else None
                        )
                        Dom.OnClick(fun _ -> env.Emit [ToggleFullscreen])
                        model.FullscreenOn |> AVal.map (fun on ->
                            if on then "Fullscreen: ON" else "Fullscreen: OFF"
                        )
                    }

                    h3 { "Difference rendering" }
                    div {
                        label {
                            input {
                                Attribute("type", "checkbox")
                                model.DifferenceRendering |> AVal.map (fun on ->
                                    if on then Some (Attribute("checked", "checked")) else None
                                )
                                Dom.OnClick(fun _ -> env.Emit [ToggleDifferenceRendering])
                            }
                            " Show mesh difference"
                        }
                    }
                    let numInput (label : string) (current : aval<float>) (msg : float -> Message) =
                        div {
                            label + "  "
                            input {
                                Attribute("type", "number")
                                Attribute("step", "any")
                                Style [Width "80px"]
                                current |> AVal.map (fun v -> Some (Attribute("value", sprintf "%.4g" v)))
                                Dom.OnInput(fun e ->
                                    match System.Double.TryParse(e.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture) with
                                    | true, v -> env.Emit [msg v]
                                    | _ -> ()
                                )
                            }
                        }
                    numInput "Min depth" model.MinDifferenceDepth SetMinDifferenceDepth
                    numInput "Max depth" model.MaxDifferenceDepth SetMaxDifferenceDepth

                    h3 { "Mesh order" }
                    p { "Determines stacking order of revolver and fullscreen tiles." }
                    div {
                        Class "btn-row"
                        button { "◀ Prev"; Dom.OnClick(fun _ -> env.Emit [CycleMeshOrder -1]) }
                        button { "Next ▶"; Dom.OnClick(fun _ -> env.Emit [CycleMeshOrder  1]) }
                    }
                    model.MeshNames |> AList.map (fun name ->
                        let order = model.MeshOrder |> AMap.tryFind name |> AVal.map (Option.defaultValue 0)
                        div {
                            order |> AVal.map (fun o -> sprintf "%d  %s" (o + 1) (shortName name))
                        }
                    )
                }

                div {
                    Class "tab-panel"
                    Attribute("id", "hud-panel3")

                    h3 { "Workspace Clip" }

                    div {
                        label {
                            input {
                                Attribute("type", "checkbox")
                                model.ClipActive |> AVal.map (fun on ->
                                    if on then Some (Attribute("checked", "checked")) else None
                                )
                                Dom.OnClick(fun _ -> env.Emit [ToggleClip])
                            }
                            " Enable clipping"
                        }
                    }

                    let slider (getValue : Box3d -> float)
                               (setValue : Box3d -> float -> Box3d)
                               (bMin : Box3d -> float) (bMax : Box3d -> float) =
                        div {
                            input {
                                Attribute("type", "range")
                                Attribute("step", "any")
                                model.ClipBounds |> AVal.map (fun b ->
                                    if b.IsInvalid then Some (Attribute("disabled", "disabled")) else None)
                                model.ClipBounds |> AVal.map (fun b ->
                                    if b.IsInvalid then None else Some (Attribute("min", sprintf "%.6g" (bMin b))))
                                model.ClipBounds |> AVal.map (fun b ->
                                    if b.IsInvalid then None else Some (Attribute("max", sprintf "%.6g" (bMax b))))
                                model.ClipBox |> AVal.map (fun b ->
                                    Some (Attribute("value", sprintf "%.6g" (getValue b))))
                                Style [Width "100%"]
                                Dom.OnInput(fun e ->
                                    match System.Double.TryParse(e.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture) with
                                    | true, v -> env.Emit [SetClipBox(setValue (AVal.force model.ClipBox) v)]
                                    | _ -> ()
                                )
                            }
                            model.ClipBox |> AVal.map (fun b -> sprintf "%.2f" (getValue b))
                        }

                    p { "X min" }
                    slider (fun b -> b.Min.X)
                           (fun box v -> Box3d(V3d(min v box.Max.X, box.Min.Y, box.Min.Z), box.Max))
                           (fun b -> b.Min.X) (fun b -> b.Max.X)
                    p { "X max" }
                    slider (fun b -> b.Max.X)
                           (fun box v -> Box3d(box.Min, V3d(max v box.Min.X, box.Max.Y, box.Max.Z)))
                           (fun b -> b.Min.X) (fun b -> b.Max.X)

                    p { "Y min" }
                    slider (fun b -> b.Min.Y)
                           (fun box v -> Box3d(V3d(box.Min.X, min v box.Max.Y, box.Min.Z), box.Max))
                           (fun b -> b.Min.Y) (fun b -> b.Max.Y)
                    p { "Y max" }
                    slider (fun b -> b.Max.Y)
                           (fun box v -> Box3d(box.Min, V3d(box.Max.X, max v box.Min.Y, box.Max.Z)))
                           (fun b -> b.Min.Y) (fun b -> b.Max.Y)

                    p { "Z min" }
                    slider (fun b -> b.Min.Z)
                           (fun box v -> Box3d(V3d(box.Min.X, box.Min.Y, min v box.Max.Z), box.Max))
                           (fun b -> b.Min.Z) (fun b -> b.Max.Z)
                    p { "Z max" }
                    slider (fun b -> b.Max.Z)
                           (fun box v -> Box3d(box.Min, V3d(box.Max.X, box.Max.Y, max v box.Min.Z)))
                           (fun b -> b.Min.Z) (fun b -> b.Max.Z)

                    button { "Reset to bounds"; Dom.OnClick(fun _ -> env.Emit [ResetClip]) }
                }

                div {
                    Class "tab-panel"
                    Attribute("id", "hud-panel4")

                    h3 { "ScanPins" }

                    // placement controls
                    let sp = model.ScanPins
                    let isPlacing = (sp.PlacingMode, sp.ActivePlacement) ||> AVal.map2 (fun pm ap -> pm.IsSome || ap.IsSome)

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

                    // active pin controls (cut plane, commit)
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

                    // pin list
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
            }
        }

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

    let pinDiagram (model : AdaptiveModel) (viewTrafo : aval<Trafo3d>) (vpSize : aval<V2i>) =
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
                    // svg canvas
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
                    // axes
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
                    // axis labels
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
                    // tooltip
                    "  var tip = document.createElementNS(ns, 'text');"
                    "  tip.setAttribute('x','5'); tip.setAttribute('y','12');"
                    "  tip.setAttribute('font-size','10'); tip.setAttribute('fill','#0f172a');"
                    "  tip.setAttribute('font-family','system-ui,sans-serif');"
                    "  tip.setAttribute('font-weight','600');"
                    "  tip.style.pointerEvents = 'none';"
                    "  svg.appendChild(tip);"
                    // paths with hover
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
                    // legend
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
        }

    let fullscreenInfo (model : AdaptiveModel) =
        div {
            model.FullscreenOn |> AVal.map (fun on ->
                if not on then Some (Style [Display "none"]) else None
            )
            Style [
                Position "fixed"; Top "10px"; Left "10px"
                Background "rgba(255,255,255,0.88)"; Padding "6px 10px"
                BorderRadius "4px"; FontSize "12px"; PointerEvents "none"
                ZIndex 100; BorderLeft "3px solid #1a56db"
            ]
            model.ActiveDataset |> AVal.map (fun ds ->
                match ds with
                | Some d -> div { Style [FontWeight "bold"; MarginBottom "4px"]; d }
                | None   -> div { []  }
            )
            model.MeshNames |> AList.map (fun name ->
                let order = model.MeshOrder |> AMap.tryFind name |> AVal.map (Option.defaultValue 0)
                div {
                    order |> AVal.map (fun o -> sprintf "%d  %s" (o + 1) (shortName name))
                }
            )
        }

    let coordinateDisplay (coord : aval<V3d option>) =
        div {
            Style [
                Position "fixed"; Bottom "8px"; Left "50%"
                StyleProperty("transform", "translateX(-50%)")
                Background "rgba(255,255,255,0.88)"; Padding "3px 12px"
                BorderRadius "4px"; FontSize "12px"; FontFamily "monospace"
                PointerEvents "none"; ZIndex 100
            ]
            coord |> AVal.map (fun c ->
                match c with
                | None   -> ""
                | Some p -> sprintf "X: %.2f   Y: %.2f   Z: %.2f" p.X p.Y p.Z
            )
        }

    let debugLogToggle (visible : cval<bool>) =
        button {
            Style [
                Position "fixed"; Bottom "0"; Left "0"
                Background "rgba(0,0,0,0.7)"; Color "#0f0"
                Border "none"; FontFamily "monospace"; FontSize "11px"
                Padding "2px 8px"; Css.Cursor "pointer"; ZIndex 10000
            ]
            Dom.OnClick(fun _ -> transact (fun () -> visible.Value <- not visible.Value))
            (visible :> aval<bool>) |> AVal.map (fun v -> if v then "▼ log" else "▲ log")
        }

    let debugLog (visible : aval<bool>) (model : AdaptiveModel) =
        div {
            visible |> AVal.map (fun v -> if not v then Some (Style [Display "none"]) else None)
            Style [
                Position "fixed"; Bottom "0"; Left "30px"; Right "0"
                MaxHeight "30vh"; OverflowY "auto"
                Background "rgba(0,0,0,0.8)"; Color "#0f0"
                FontFamily "monospace"; FontSize "11px"
                Padding "4px 8px"; PointerEvents "none"
                ZIndex 9999; StyleProperty("white-space", "pre-wrap")
            ]
            model.DebugLog |> AList.map (fun line -> div { line })
        }
