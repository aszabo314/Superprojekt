namespace Superprojekt

open Aardvark.Base
open FSharp.Data.Adaptive
open Aardvark.Dom

module GuiPanels =

    open Primitives

    let private meshRow (env : Env<Message>) (model : AdaptiveModel) (name : string) =
        let isVis = model.MeshVisible |> AVal.map (fun m -> Map.tryFind name m |> Option.defaultValue true)
        let isSolo = model.MeshSolo |> AVal.map (fun s ->
            match s with Solo(n, _) -> n = name | _ -> false)
        let colorVal =
            model.MeshOrder |> AMap.tryFind name |> AVal.map (Option.defaultValue 0 >> meshColor)
        div {
            Class "mesh-row"
            span {
                Class "mesh-swatch"
                colorVal |> AVal.map (fun c ->
                    Some (Style [Css.Background (sprintf "rgb(%d,%d,%d)" (int c.R) (int c.G) (int c.B))]))
            }
            span { Class "mesh-name"; Cards.shortName name }
            button {
                Class "mb"
                isVis |> AVal.map (fun v -> if v then Some (Class "mb-on") else None)
                Attribute("title", "Visible")
                Dom.OnClick(fun _ ->
                    let cur = AVal.force isVis
                    env.Emit [SetVisible(name, not cur)])
                isVis |> AVal.map (fun v -> if v then "●" else "○")
            }
            button {
                Class "mb"
                isSolo |> AVal.map (fun s -> if s then Some (Class "mb-on") else None)
                Attribute("title", "Solo (isolate)")
                Dom.OnClick(fun _ -> env.Emit [ToggleMeshSolo name])
                "◐"
            }
            button {
                Class "mb"
                Attribute("title", "Focus camera on this mesh")
                Dom.OnClick(fun _ -> env.Emit [JumpToMesh name])
                "⌖"
            }
        }

    let private meshSection (env : Env<Message>) (model : AdaptiveModel) =
        div {
            Class "lp-section"
            div {
                Class "lp-section-head"
                span { Class "lp-section-title"; "Meshes" }
                div {
                    Class "lp-section-actions"
                    button {
                        Class "mb"; Attribute("title", "Show all")
                        Dom.OnClick(fun _ -> env.Emit [ShowAllMeshes])
                        "All"
                    }
                    button {
                        Class "mb"; Attribute("title", "Hide all")
                        Dom.OnClick(fun _ -> env.Emit [HideAllMeshes])
                        "None"
                    }
                }
            }
            div {
                Class "mesh-list"
                model.MeshNames |> AList.map (fun name -> meshRow env model name)
            }
            div {
                Class "lp-sub"
                span { Class "lp-sublabel"; "Rendering" }
                let rm = model.RenderingMode
                compactButtonBar [
                    "Textured", (rm |> AVal.map (fun m -> m = Textured)), (fun () -> env.Emit [SetRenderingMode Textured])
                    "Shaded",   (rm |> AVal.map (fun m -> m = Shaded)),   (fun () -> env.Emit [SetRenderingMode Shaded])
                    "Slope",    (rm |> AVal.map (fun m -> m = SlopeColor)),   (fun () -> env.Emit [SetRenderingMode SlopeColor])
                ]
            }
        }

    let placementFlyout (env : Env<Message>) (model : AdaptiveModel) =
        let sp = model.ScanPins
        let activePlacementId =
            sp.Placement |> AVal.map (function
                | AdjustingPin id -> Some id
                | _ -> None)
        let activePin =
            activePlacementId |> AVal.bind (function
                | Some i -> sp.Pins |> AMap.tryFind i
                | None -> AVal.constant None)
        let adjusting = sp.Placement |> AVal.map (function AdjustingPin _ -> true | _ -> false)
        let flyoutClass =
            (adjusting, model.MenuOpen) ||> AVal.map2 (fun adj open_ ->
                if not adj then "placement-flyout hidden"
                elif open_ then "placement-flyout pf-left-open"
                else "placement-flyout pf-left-closed")
        div {
            flyoutClass |> AVal.map (fun c -> Some (Class c))
            div { Class "lp-section-title"; "Adjust Anchor" }
            let innerR =
                activePin |> AVal.map (Option.map (fun p -> p.InnerRadius) >> Option.defaultValue 1.0)
            let falloffDelta =
                activePin |> AVal.map (Option.map (fun p -> max 0.01 (p.FalloffRadius - p.InnerRadius)) >> Option.defaultValue 3.0)
            inlineLogSlider "Inner radius" 0.01 10000.0 (sprintf "%.2f m") innerR (fun v ->
                env.Emit [ScanPinMsg (SetInnerRadius v)])
            inlineLogSlider "Falloff +" 0.01 10000.0 (sprintf "+%.2f m") falloffDelta (fun v ->
                env.Emit [ScanPinMsg (SetFalloffDelta v)])

            let payloadKind =
                activePin |> AVal.map (function
                    | Some p -> PayloadType.kind p.Payload
                    | None -> PointKind)
            let pinId = activePlacementId
            let emitForId (mk : ScanPinId -> ScanPinMessage) =
                fun () ->
                    match AVal.force pinId with
                    | Some id -> env.Emit [ScanPinMsg (mk id)]
                    | None -> ()
            div { Class "lp-sublabel"; "Payload" }
            compactButtonBar [
                "Point", payloadKind |> AVal.map ((=) PointKind),
                    emitForId (fun id -> ChangePayloadType(id, PointKind))
                "Line",  payloadKind |> AVal.map ((=) LineKind),
                    emitForId (fun id -> ChangePayloadType(id, LineKind))
                "Patch", payloadKind |> AVal.map ((=) PatchKind),
                    emitForId (fun id -> ChangePayloadType(id, PatchKind))
            ]

            let isPoint = payloadKind |> AVal.map ((=) PointKind)
            let reliability =
                activePin |> AVal.map (fun po ->
                    match po with
                    | Some p ->
                        match p.Payload with
                        | Point pp -> pp.ReliabilityWeight
                        | _ -> 1.0
                    | None -> 1.0)
            div {
                Class "lp-reliability-row"
                showWhen isPoint
                inlineSlider "Reliability" 0.0 1.0 0.01 (sprintf "%.2f") reliability (fun v ->
                    match AVal.force pinId with
                    | Some id -> env.Emit [ScanPinMsg (SetReliabilityWeight(id, v))]
                    | None -> ())
            }

            let isLine = payloadKind |> AVal.map ((=) LineKind)
            let isIsoline =
                activePin |> AVal.map (fun po ->
                    match po with
                    | Some p ->
                        match p.Payload with
                        | Line { Mode = ElevationIsoline _ } -> true
                        | _ -> false
                    | None -> false)
            let isolineElev =
                activePin |> AVal.map (fun po ->
                    match po with
                    | Some p ->
                        match p.Payload with
                        | Line { Mode = ElevationIsoline e } -> e
                        | _ -> p.Centre.Z
                    | None -> 0.0)
            let centreZ =
                activePin |> AVal.map (function
                    | Some p -> p.Centre.Z
                    | None -> 0.0)
            div {
                Class "lp-line-controls"
                showWhen isLine
                div { Class "lp-sublabel"; "Line mode" }
                compactButtonBar [
                    "Elevation", isIsoline,
                        (fun () ->
                            match AVal.force pinId, AVal.force centreZ with
                            | Some id, z -> env.Emit [ScanPinMsg (SetLineMode(id, ElevationIsoline z))]
                            | _ -> ())
                    "Ridge", isIsoline |> AVal.map not,
                        emitForId (fun id -> SetLineMode(id, CurvatureRidge))
                ]
                div {
                    Class "lp-isoline-row"
                    showWhen isIsoline
                    inlineSlider "Elevation" -10000.0 10000.0 0.1 (sprintf "%.1fm") isolineElev (fun v ->
                        match AVal.force pinId with
                        | Some id -> env.Emit [ScanPinMsg (SetLineMode(id, ElevationIsoline v))]
                        | None -> ())
                }
            }

            div {
                Class "lp-commit-row"
                button {
                    Class "lp-commit"
                    Dom.OnClick(fun _ -> env.Emit [ScanPinMsg CommitPin])
                    "✓ Commit"
                }
                button {
                    Class "lp-discard"
                    Dom.OnClick(fun _ -> env.Emit [ScanPinMsg CancelPlacement])
                    "✕ Discard"
                }
            }
        }

    let private pinSection (env : Env<Message>) (model : AdaptiveModel) =
        let sp = model.ScanPins
        let pinsVal = sp.Pins |> AMap.toAVal
        let pinIdList =
            pinsVal |> AVal.map (fun pins ->
                pins |> HashMap.toSeq |> Seq.map fst |> Seq.sort |> IndexList.ofSeq)
            |> AList.ofAVal
        div {
            Class "lp-section"
            div { Class "lp-section-head"; span { Class "lp-section-title"; "Pins" } }
            div {
                Class "pin-list"
                pinIdList |> AList.map (fun id ->
                    let pinVal = pinsVal |> AVal.map (fun pins -> HashMap.tryFind id pins)
                    let isSelected = sp.SelectedPin |> AVal.map (fun s -> s = Some id)
                    div {
                        Class "pin-row"
                        isSelected |> AVal.map (fun s -> if s then Some (Class "pin-row-selected") else None)
                        span {
                            Class "pin-status"
                            pinVal |> AVal.map (fun po ->
                                match po with
                                | Some p when p.Phase = PinPhase.Placement -> "○"
                                | Some _ -> "●"
                                | None -> "")
                        }
                        span {
                            Class "pin-label"
                            Dom.OnClick(fun _ ->
                                let sel = AVal.force sp.SelectedPin
                                if sel = Some id then env.Emit [ScanPinMsg (SelectPin None)]
                                else env.Emit [ScanPinMsg (SelectPin (Some id))])
                            pinVal |> AVal.map (fun po ->
                                match po with
                                | Some p ->
                                    let a = p.Centre
                                    sprintf "(%.1f, %.1f, %.1f)" a.X a.Y a.Z
                                | None -> "(removed)")
                        }
                        button {
                            Class "mb"; Attribute("title", "Focus")
                            Dom.OnClick(fun _ ->
                                env.Emit [ScanPinMsg (SelectPin (Some id)); ScanPinMsg (FocusPin id)])
                            "⌖"
                        }
                        button {
                            Class "mb"; Attribute("title", "Edit")
                            showWhen (pinVal |> AVal.map (function Some p -> p.Phase = PinPhase.Committed | None -> false))
                            Dom.OnClick(fun _ -> env.Emit [EditPin id])
                            "✎"
                        }
                        button {
                            Class "mb"; Attribute("title", "Delete")
                            Dom.OnClick(fun _ -> env.Emit [ScanPinMsg (DeletePin id)])
                            "✕"
                        }
                    })
            }
        }

    let private visTechSection (env : Env<Message>) (model : AdaptiveModel) =
        collapsibleSection "Visualization" false (
            div {
                Class "lp-vis-body"

                collapsibleSection "Error metadata" false (
                    div {
                        Class "lp-err-meta"
                        model.MeshNames |> AList.map (fun name ->
                            let sensor =
                                model.MeshSensorTypes |> AVal.map (fun m ->
                                    Map.tryFind name m |> Option.defaultValue UnknownSensor)
                            let userValue =
                                model.MeshDatasetErrors |> AVal.map (fun m -> Map.tryFind name m)
                            let displayed =
                                (sensor, userValue) ||> AVal.map2 (fun s ov ->
                                    ov |> Option.defaultValue (Provenance.defaultDatasetError s))
                            let sensorBtn (label : string) (sensorType : SensorType) =
                                label, sensor |> AVal.map ((=) sensorType),
                                    (fun () -> env.Emit [SetMeshSensorType(name, sensorType)])
                            div {
                                Class "lp-err-mesh-row"
                                div { Class "lp-err-mesh-name"; Cards.shortName name }
                                compactButtonBar [
                                    sensorBtn "Rover" RoverStereo
                                    sensorBtn "Sat"   Satellite
                                    sensorBtn "Photo" Photogrammetry
                                    sensorBtn "LiDAR" LiDAR
                                ]
                                div {
                                    Class "lp-err-override"
                                    inlineLogSlider "Override" 0.0001 10.0 (fun v ->
                                        if v < 0.01 then sprintf "%.1fmm" (v * 1000.0)
                                        else sprintf "%.3fm" v) displayed (fun v ->
                                        env.Emit [SetMeshDatasetError(name, Some v)])
                                    button {
                                        Class "mb"
                                        Attribute("title", "Revert to sensor default")
                                        Dom.OnClick(fun _ ->
                                            env.Emit [SetMeshDatasetError(name, None)])
                                        "↺"
                                    }
                                }
                            })
                    })

                collapsibleSection "Error provenance" false (
                    div {
                        Class "lp-prov-body"
                        compactToggle "Show heatmap" model.ProvenanceHeatmap (fun () ->
                            env.Emit [ToggleProvenanceHeatmap])
                        compactToggle "Falloff zones only" model.FalloffZoneOnly (fun () ->
                            env.Emit [ToggleFalloffZoneOnly])
                        inlineLogSlider "Threshold" 0.0001 10.0 (fun v ->
                            if v < 0.01 then sprintf "%.1fmm" (v * 1000.0)
                            else sprintf "%.2fm" v) model.ProvenanceThreshold (fun v ->
                            env.Emit [SetProvenanceThreshold v])
                        div { Class "lp-sublabel-hint"; "Blue = dataset, orange = algorithm, purple = conditioning." }
                    })

            })

    let leftPanel (env : Env<Message>) (model : AdaptiveModel) =
        div {
            Class "left-panel"
            model.MenuOpen |> AVal.map (fun o -> if o then Some (Class "open") else None)
            div {
                Class "lp-normal"
                meshSection env model
                pinSection env model
                visTechSection env model
            }
        }
