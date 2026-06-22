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
        let isRef = model.Registration |> AVal.map (fun r -> r.ReferenceMesh = Some name)
        let idxVal = model.MeshOrder |> AMap.tryFind name |> AVal.map (Option.defaultValue 0)
        let colorVal = idxVal |> AVal.map meshColor
        div {
            Class "mesh-row"
            span {
                Class "mesh-swatch"
                colorVal |> AVal.map (fun c ->
                    Some (Style [Css.Background (Primitives.c4bToRgbCss c)]))
            }
            span { Class "mesh-num"; idxVal |> AVal.map (fun i -> string (i + 1)) }
            span { Class "mesh-name"; Cards.shortName name }
            // Reference toggle, two-way bound to the registration card.
            button {
                Class "mb mb-ref"
                isRef |> AVal.map (fun r -> if r then Some (Class "mb-on") else None)
                Attribute("title", "All error metrics are relative to this mesh (no absolute ground truth).")
                Dom.OnClick(fun _ ->
                    let cur = AVal.force model.Registration
                    env.Emit [SetReferenceMesh (if cur.ReferenceMesh = Some name then None else Some name)])
                isRef |> AVal.map (fun r -> if r then "★" else "☆")
            }
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
            // Fine-tuning controls need pinEdit; placing + committing only
            // needs pinPlace.
            div {
            showWhen (StudyGate.featureOn model "pinEdit")
            let innerR =
                activePin |> AVal.map (Option.map (fun p -> p.InnerRadius) >> Option.defaultValue 1.0)
            inlineLogSlider "Inner radius" 0.01 10000.0 (sprintf "%.2f m") innerR (fun v ->
                env.Emit [ScanPinMsg (SetInnerRadius v)])

            let pinId = activePlacementId

            // Numeric reposition: set the pin centre live while adjusting.
            let centre =
                activePin |> AVal.map (Option.map (fun p -> p.Centre) >> Option.defaultValue V3d.Zero)
            let posInput (lbl : string) (get : V3d -> float) (upd : V3d -> float -> V3d) =
                div {
                    Class "pf-pos-field"
                    span { Class "pf-pos-lbl"; lbl }
                    input {
                        Class "pf-pos-input"
                        Attribute("type", "number")
                        Attribute("step", "0.1")
                        centre |> AVal.map (fun c -> Some (Attribute("value", sprintf "%.2f" (get c))))
                        Dom.OnChange(fun e ->
                            match System.Double.TryParse(e.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture) with
                            | true, v ->
                                match AVal.force pinId with
                                | Some id -> env.Emit [ScanPinMsg (RepositionPin(id, upd (AVal.force centre) v))]
                                | None -> ()
                            | _ -> ())
                    }
                }
            div { Class "lp-sublabel"; "Position (m)" }
            div {
                Class "pf-pos-fields"
                posInput "X" (fun c -> c.X) (fun c v -> V3d(v, c.Y, c.Z))
                posInput "Y" (fun c -> c.Y) (fun c v -> V3d(c.X, v, c.Z))
                posInput "Z" (fun c -> c.Z) (fun c v -> V3d(c.X, c.Y, v))
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
                                | Some p -> Some (Attribute("title", sprintf "(%.1f, %.1f, %.1f) m" p.Centre.X p.Centre.Y p.Centre.Z))
                                | None -> None)
                            pinVal |> AVal.map (fun po ->
                                match po with
                                | Some p -> p.Name
                                | None -> "(removed)")
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

                div {
                showWhen (StudyGate.featureOn model "errorMetadata")
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
                            let numName =
                                model.MeshOrder |> AMap.tryFind name |> AVal.map (fun o ->
                                    sprintf "%d  %s" ((Option.defaultValue 0 o) + 1) (Cards.shortName name))
                            div {
                                Class "lp-err-mesh-row"
                                div { Class "lp-err-mesh-name"; numName }
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
                }

                div {
                showWhen (StudyGate.featureOn model "heatmap")
                collapsibleSection "Error provenance" false (
                    div {
                        Class "lp-prov-body"
                        let mode = model.HeatmapMode
                        let previewOn = model.PendingReg |> AVal.map PendingRegistration.isPreview
                        compactButtonBar [
                            "Off",     mode |> AVal.map ((=) HeatOff),
                                (fun () -> env.Emit [SetHeatmapMode HeatOff])
                            "Sources", mode |> AVal.map ((=) HeatProvenance),
                                (fun () -> env.Emit [SetHeatmapMode HeatProvenance])
                            // Diff needs a pending solve preview to diff against.
                            "Diff",    mode |> AVal.map ((=) HeatDiff),
                                (fun () ->
                                    if AVal.force previewOn then env.Emit [SetHeatmapMode HeatDiff])
                        ]
                        div {
                            Class "lp-sublabel-hint"
                            showWhenNot previewOn
                            "Diff needs a pending registration preview."
                        }
                        inlineLogSlider "Threshold" 0.0001 10.0 (fun v ->
                            if v < 0.01 then sprintf "%.1fmm" (v * 1000.0)
                            else sprintf "%.2fm" v) model.ProvenanceThreshold (fun v ->
                            env.Emit [SetProvenanceThreshold v])
                        div { Class "lp-sublabel-hint"; "Blue = dataset, orange = algorithm, purple = conditioning." }
                    })
                }

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
