namespace Superprojekt

open Aardvark.Base
open Aardworx.WebAssembly
open FSharp.Data.Adaptive
open Aardvark.Dom

module GuiTopBar =

    open Primitives

    let topBar (env : Env<Message>) (model : AdaptiveModel) (hoverCoord : aval<V3d option>) =
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
                    (datasetOpen :> aval<_>) |> AVal.map (fun o ->
                        if o then None else Some (Style [Display "none"]))
                    model.Datasets |> AVal.map IndexList.ofList |> AList.ofAVal |> AList.map (fun dataset ->
                        let isActive = model.ActiveDataset |> AVal.map (fun a -> a = Some dataset)
                        button {
                            Class "tb-dataset-item"
                            isActive |> AVal.map (fun on -> if on then Some (Class "active") else None)
                            Dom.OnClick(fun _ ->
                                transact (fun () -> datasetOpen.Value <- false)
                                env.Emit [SetActiveDataset dataset]
                                ServerActions.loadDataset env dataset)
                            dataset
                        })
                }
            }

            let exploreEnabled = model.Explore |> AVal.map (fun e -> e.Enabled)
            button {
                Class "tb-btn"
                exploreEnabled |> AVal.map (fun on -> if on then Some (Class "tb-btn-active") else None)
                Attribute("title", "Toggle explore heatmap")
                Dom.OnClick(fun _ ->
                    let cur = AVal.force exploreEnabled
                    env.Emit [ExploreMsg (SetExploreEnabled (not cur))])
                "◉ Explore"
            }

            let placementActive =
                model.ScanPins.Placement |> AVal.map (function
                    | AnchorPlacement -> true
                    | _ -> false)
            button {
                Class "tb-btn"
                placementActive |> AVal.map (fun on -> if on then Some (Class "tb-btn-active") else None)
                Attribute("title", "Place anchor — click on a surface (Esc cancels)")
                Dom.OnClick(fun _ ->
                    let active = AVal.force placementActive
                    if active then env.Emit [ScanPinMsg CancelPlacement]
                    else env.Emit [LassoCancel; ScanPinMsg EnterAnchorPlacement])
                "○ Pin"
            }

            button {
                Class "tb-btn"
                model.FusionMode |> AVal.map (fun on -> if on then Some (Class "tb-btn-active") else None)
                Attribute("title", "Fusion mesh: per-pixel best mesh from the registered ensemble")
                Dom.OnClick(fun _ -> env.Emit [ToggleFusionMode])
                "◈ Fusion"
            }

            button {
                Class "tb-btn tb-btn-icon"
                Attribute("title", "Reset camera")
                Dom.OnClick(fun _ -> env.Emit [ResetCamera])
                "⟲"
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
                        model.GearPopoverOpen |> AVal.map (fun o -> if o then None else Some (Style [Display "none"]))
                        div {
                            Class "tb-gear-row"
                            span { Class "lp-sublabel"; "Reference axis" }
                            compactButtonBar [
                                "World Z",
                                    (model.ReferenceAxis |> AVal.map (fun m -> m = AlongWorldZ)),
                                    (fun () -> env.Emit [ExploreMsg (SetReferenceAxisMode AlongWorldZ)])
                                "Camera",
                                    (model.ReferenceAxis |> AVal.map (fun m -> m = AlongCameraView)),
                                    (fun () -> env.Emit [ExploreMsg (SetReferenceAxisMode AlongCameraView)])
                            ]
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
                            compactToggle "Anchor-blob ghost" model.AnchorGhostMode (fun () ->
                                env.Emit [ToggleAnchorGhostMode])
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
