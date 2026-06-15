namespace Superprojekt

open Aardvark.Base
open Aardworx.WebAssembly
open FSharp.Data.Adaptive
open Aardvark.Dom

module GuiTopBar =

    open Primitives

    let topBar (env : Env<Message>) (model : AdaptiveModel) (hoverCoord : aval<V3d option>) =
        // §6 guards: these actions are blocked while a registration preview
        // is pending (the reducer also rejects them; this is the affordance).
        let previewOn = model.PendingReg |> AVal.map PendingRegistration.isPreview
        let previewDisabled =
            previewOn |> AVal.map (fun p ->
                if p then Some (Attribute("disabled", "disabled")) else None)
        div {
            Class "top-bar"
            button {
                Class "tb-burger"
                Attribute("title", "Toggle left panel")
                Dom.OnClick(fun _ -> env.Emit [ToggleMenu])
                div { Class "burger-line" }
                div { Class "burger-line" }
                div { Class "burger-line" }
            }

            let datasetOpen = cval false
            div {
                Class "tb-dataset"
                button {
                    Class "tb-dataset-btn"
                    Dom.OnClick(fun _ -> transact (fun () -> datasetOpen.Value <- not datasetOpen.Value))
                    model.ActiveDataset |> AVal.map (fun a ->
                        let name = a |> Option.defaultValue "Dataset"
                        sprintf "%s ▾" name)
                }
                div {
                    Class "tb-dataset-menu"
                    showWhen (datasetOpen :> aval<_>)
                    model.Datasets |> AVal.map IndexList.ofList |> AList.ofAVal |> AList.map (fun dataset ->
                        let isActive = model.ActiveDataset |> AVal.map (fun a -> a = Some dataset)
                        button {
                            Class "tb-dataset-item"
                            isActive |> AVal.map (fun on -> if on then Some (Class "active") else None)
                            previewDisabled
                            previewOn |> AVal.map (fun p ->
                                if p then Some (Attribute("title", "Dataset switch is blocked while previewing a registration result"))
                                else None)
                            Dom.OnClick(fun _ ->
                                if not (AVal.force previewOn) then
                                    transact (fun () -> datasetOpen.Value <- false)
                                    env.Emit [SetActiveDataset dataset]
                                    ServerActions.loadDataset env dataset)
                            dataset
                        })
                }
            }

            let lassoActive =
                (model.LassoDrawing, model.LassoVolume)
                ||> AVal.map2 (fun d v -> d.IsSome || v.IsSome)
            button {
                Class "tb-btn"
                lassoActive |> AVal.map (fun on -> if on then Some (Class "tb-btn-active") else None)
                Attribute("title", "Lasso: start drawing a clip polygon on the viewport")
                Dom.OnClick(fun _ ->
                    if AVal.force lassoActive then env.Emit [LassoClear]
                    else env.Emit [LassoBegin])
                "◌ Lasso"
            }

            let placementActive =
                model.ScanPins.Placement |> AVal.map (function
                    | AnchorPlacement -> true
                    | _ -> false)
            button {
                Class "tb-btn"
                placementActive |> AVal.map (fun on -> if on then Some (Class "tb-btn-active") else None)
                previewDisabled
                previewOn |> AVal.map (fun p ->
                    Some (Attribute("title",
                        if p then "Pin placement is blocked while previewing a registration result"
                        else "Place anchor — click on a surface (Esc cancels)")))
                Dom.OnClick(fun _ ->
                    let active = AVal.force placementActive
                    if active then env.Emit [ScanPinMsg CancelPlacement]
                    else env.Emit [LassoCancel; ScanPinMsg EnterAnchorPlacement])
                "○ Pin"
            }

            button {
                Class "tb-btn"
                model.FusionMode |> AVal.map (fun on -> if on then Some (Class "tb-btn-active") else None)
                previewDisabled
                previewOn |> AVal.map (fun p ->
                    Some (Attribute("title",
                        if p then "Fusion is blocked while previewing a registration result"
                        else "Fusion mesh: per-pixel best mesh from the registered ensemble")))
                Dom.OnClick(fun _ -> env.Emit [ToggleFusionMode])
                "◈ Fusion"
            }

            button {
                Class "tb-btn"
                model.PanoramaOpen |> AVal.map (fun on -> if on then Some (Class "tb-btn-active") else None)
                Attribute("title", "Panorama: cylindrical view from a synthetic viewpoint in the scene")
                Dom.OnClick(fun _ -> env.Emit [TogglePanorama])
                "▦ Pano"
            }

            button {
                Class "tb-btn"
                model.WorkflowPanelOpen |> AVal.map (fun on -> if on then Some (Class "tb-btn-active") else None)
                Attribute("title", "Registration workflow: readiness, anchors, status and error stats in one panel")
                Dom.OnClick(fun _ -> env.Emit [ToggleWorkflowPanel])
                "⚲ Workflow"
            }

            button {
                Class "tb-btn tb-btn-icon"
                Attribute("title", "Reset camera")
                Dom.OnClick(fun _ -> env.Emit [ResetCamera])
                "⟲"
            }

            // Spring-loaded reference peek: while held, ghost every mesh except
            // the reference (★). Transient importance override — never mutates
            // the eye toggles. Disabled until a reference is designated.
            button {
                Class "tb-btn"
                model.ReferencePeekHeld |> AVal.map (fun on -> if on then Some (Class "tb-btn-active") else None)
                model.Registration |> AVal.map (fun r ->
                    if r.ReferenceMesh.IsNone then Some (Attribute("disabled", "disabled")) else None)
                Attribute("title", "Peek reference: hold to show only the reference mesh (hotkey: R)")
                Dom.OnPointerDown((fun _ -> env.Emit [SetReferencePeek true]), pointerCapture = true)
                Dom.OnPointerUp((fun _ -> env.Emit [SetReferencePeek false]), pointerCapture = true)
                "👁 Peek"
            }

            div {
                Class "tb-right"
                span {
                    Class "tb-coord"
                    hoverCoord |> AVal.map (fun c ->
                        match c with
                        | Some p -> sprintf "⌖ %.1f, %.1f, %.1f" p.X p.Y p.Z
                        | None   -> "⌖ —")
                }
                div {
                    Class "tb-gear-wrap"
                    button {
                        Class "tb-btn-tiny"
                        model.GearPopoverOpen |> AVal.map (fun on -> if on then Some (Class "tb-btn-active") else None)
                        Attribute("title", "Debug & settings")
                        Dom.OnClick(fun _ -> env.Emit [ToggleGearPopover])
                        "⚙"
                    }
                    div {
                        Class "tb-gear-popover"
                        showWhen model.GearPopoverOpen
                        div {
                            Class "tb-gear-row"
                            span { Class "lp-sublabel"; "Retarget" }
                            div {
                                Class "tb-gear-btn-row"
                                button {
                                    Class "tb-gear-btn"
                                    previewOn |> AVal.map (fun p ->
                                        Some (Attribute("title",
                                            if p then "Retarget is blocked while previewing a registration result"
                                            else "Project all pins onto the active picking layer (hold Option/Alt and scroll to pick the target mesh first)")))
                                    (model.ActivePickingLayer, previewOn) ||> AVal.map2 (fun l p ->
                                        if l.IsNone || p then Some (Attribute("disabled", "disabled")) else None)
                                    Dom.OnClick(fun _ ->
                                        match AVal.force model.ActivePickingLayer with
                                        | Some target -> env.Emit [StartRetarget target]
                                        | None -> ())
                                    "→ Project pins to active layer"
                                }
                            }
                        }
                        div {
                            Class "tb-gear-row"
                            span { Class "lp-sublabel"; "Workspace" }
                            div {
                                Class "tb-gear-btn-row"
                                button {
                                    Class "tb-gear-btn"
                                    Attribute("title", "Save workspace as JSON")
                                    Dom.OnClick(fun _ -> env.Emit [SaveWorkspace])
                                    "💾 Save"
                                }
                                button {
                                    Class "tb-gear-btn"
                                    Attribute("title", "Load workspace from JSON file")
                                    Dom.OnClick(fun _ ->
                                        task {
                                            try
                                                let rt = Aardworx.WebAssembly.JSRuntime.Instance :> Microsoft.JSInterop.IJSRuntime
                                                let! json = rt.InvokeAsync<string>("SuperWorkspaceLoad", [||]).AsTask()
                                                if not (isNull json) && json.Length > 0 then
                                                    env.Emit [LoadWorkspaceJson json]
                                            with _ -> ()
                                        } |> ignore)
                                    "📂 Load"
                                }
                            }
                        }
                        div {
                            Class "tb-gear-row"
                            showWhen (model.StudiesAvailable |> AVal.map (List.isEmpty >> not))
                            span { Class "lp-sublabel"; "Preview study mode" }
                            let demoCond = cval CondFull
                            div {
                                Class "tb-gear-btn-row"
                                button {
                                    Class "tb-gear-btn"
                                    Attribute("title", "Condition for the preview session (FULL = all charts, NUM = numbers only)")
                                    Dom.OnClick(fun _ -> transact (fun () ->
                                        demoCond.Value <- (match demoCond.Value with CondFull -> CondNum | CondNum -> CondFull)))
                                    (demoCond :> aval<_>) |> AVal.map (fun c -> sprintf "Condition: %s ⇄" (StudyCondition.tag c))
                                }
                                model.StudiesAvailable |> AVal.map IndexList.ofList |> AList.ofAVal |> AList.map (fun studyId ->
                                    button {
                                        Class "tb-gear-btn"
                                        Attribute("title", "Enter this study as a demo session (telemetry flagged demo, exit any time)")
                                        Dom.OnClick(fun _ -> env.Emit [StudyMsg (StudyStartDemo(studyId, demoCond.Value))])
                                        sprintf "▶ %s" studyId
                                    })
                            }
                        }
                        div {
                            Class "tb-gear-row"
                            inlineSlider "Camera speed" 0.05 2.0 0.01 (sprintf "%.2f") model.Camera.speed (fun v ->
                                env.Emit [CameraMessage (OrbitMessage.SetSpeed v)])
                        }
                        div {
                            Class "tb-gear-row"
                            compactToggle "Ghost silhouette" model.GhostSilhouette (fun () ->
                                env.Emit [ToggleGhostSilhouette])
                            inlineSlider "Ghost opacity" 0.0 1.0 0.01 (sprintf "%.2f") model.GhostOpacity (fun v ->
                                env.Emit [SetGhostOpacity v])
                        }
                        div {
                            Class "tb-gear-row"
                            // Isolate pins: ghost everything outside the pins' falloff regions.
                            // Auto-suspended while placing an anchor (terrain stays visible);
                            // the toggle reflects the temporary hold and is inert during it.
                            let placing =
                                model.ScanPins.Placement |> AVal.map (function AnchorPlacement -> true | _ -> false)
                            let isoEffective =
                                (model.AnchorGhostMode, placing) ||> AVal.map2 (fun on p -> on && not p)
                            compactToggle "Isolate pins" isoEffective (fun () ->
                                if not (AVal.force placing) then env.Emit [ToggleAnchorGhostMode])
                        }
                        div {
                            Class "tb-gear-row"
                            inlineSlider "Shading strength" 0.0 1.0 0.01 (sprintf "%.2f") model.ShadingStrength (fun v ->
                                env.Emit [SetShadingStrength v])
                        }
                        div {
                            Class "tb-gear-row"
                            inlineSlider "Slope threshold (°)" 1.0 89.0 1.0 (sprintf "%.0f°") model.SlopeThresholdDeg (fun v ->
                                env.Emit [SetSlopeThresholdDeg v])
                        }
                        div {
                            Class "tb-gear-row"
                            span { Class "lp-sublabel"; "Dataset" }
                            span {
                                Class "tb-gear-val"
                                (model.ActiveDataset, model.SceneBounds, model.CommonCentroid)
                                |||> AVal.map3 (fun ds bb cc ->
                                    let name = ds |> Option.defaultValue "(none)"
                                    if bb.IsInvalid then sprintf "%s — (bounds pending)" name
                                    else
                                        sprintf "%s   bounds %.1f–%.1f × %.1f–%.1f × %.1f–%.1f   centroid (%.1f,%.1f,%.1f)"
                                            name bb.Min.X bb.Max.X bb.Min.Y bb.Max.Y bb.Min.Z bb.Max.Z
                                            cc.X cc.Y cc.Z)
                            }
                        }
                        div {
                            Class "tb-gear-mesh-info"
                            model.MeshNames |> AList.map (fun name ->
                                let centroid = model.DatasetCentroids |> AVal.map (fun m -> Map.tryFind name m |> Option.defaultValue V3d.Zero)
                                div {
                                    Class "tb-gear-mesh-row"
                                    span { Class "tb-gear-mesh-name"; Cards.shortName name }
                                    span {
                                        Class "tb-gear-mesh-coord"
                                        centroid |> AVal.map (fun c ->
                                            sprintf "centroid (%.1f, %.1f, %.1f)" c.X c.Y c.Z)
                                    }
                                })
                        }
                        div {
                            Class "tb-gear-log"
                            model.DebugLog |> AList.map (fun line -> div { Class "tb-gear-log-line"; line })
                        }
                    }
                }
            }
        }
