namespace Superprojekt

open Aardvark.Base
open Aardworx.WebAssembly
open FSharp.Data.Adaptive
open Aardvark.Dom

module GuiTopBar =

    open Primitives

    let topBar (env : Env<Message>) (model : AdaptiveModel) (hoverCoord : aval<V3d option>) =
        // Global before/after toggle: disabled until any SolvedTransform exists.
        let solved = model.SolvedTransforms |> AVal.map (Map.isEmpty >> not)
        div {
            Class "top-bar"
            // Spring-loaded overview (hotkey O): white-out the meshes so only the
            // pins carry colour (thick coloured flags + 2D name tags at their tips).
            button {
                Class "tb-btn"
                classWhen "tb-btn-active" model.ShowOverlaysHeld
                Attribute("title", "Plan: hold to white-out the meshes — only the pins stay coloured, with name tags (hotkey: O)")
                Dom.OnPointerDown((fun _ -> env.Emit [SetShowOverlays true]), pointerCapture = true)
                Dom.OnPointerUp((fun _ -> env.Emit [SetShowOverlays false]), pointerCapture = true)
                Dom.OnMouseLeave(fun _ -> env.Emit [SetShowOverlays false])
                "🗺 Plan"
            }

            div {
                Class "tb-regview"
                classWhenNot "tb-regview-off" solved
                Attribute("title", "Show meshes before or after registration")
                let btn (label : string) (v : RegView) =
                    button {
                        Class "tb-regview-btn"
                        classWhen "btn-active" ((model.RegView, solved) ||> AVal.map2 (fun cur s -> s && cur = v))
                        Dom.OnClick(fun _ -> if AVal.force solved then env.Emit [SetRegView v])
                        label
                    }
                btn "Before" RegBefore
                btn "After" RegAfter
                // Spring-loaded peek: hold to momentarily show the OTHER state.
                button {
                    Class "tb-regview-btn tb-regview-peek"
                    classWhen "btn-active" model.RegPeekHeld
                    Attribute("title", "Peek: hold to momentarily show the other registration state (hotkey: I)")
                    Dom.OnPointerDown((fun _ -> if AVal.force solved then env.Emit [SetRegPeek true]), pointerCapture = true)
                    Dom.OnPointerUp((fun _ -> env.Emit [SetRegPeek false]), pointerCapture = true)
                    Dom.OnMouseLeave(fun _ -> env.Emit [SetRegPeek false])
                    "Peek"
                }
            }

            // The reconstruction-readiness hint moved to the Correspondence rail body,
            // next to the Solve button (GuiRail).

            div {
                Class "tb-right"
                // Live cursor coordinate. World = metric world under the cursor
                // (XY-plane at mean elevation when off-mesh). When a mesh is focused,
                // also its point in that mesh's own frame (origin = its camera) =
                // world − centroid — exact at load pose, the relevant case here.
                div {
                    Class "tb-coord"
                    Attribute("title", "Cursor world coordinate (drops to the mean-elevation XY plane when off-mesh). Focused mesh → offset from that mesh's origin.")
                    let fmt (p : V3d) = sprintf "%.1f  %.1f  %.1f" p.X p.Y p.Z
                    span {
                        Class "tb-coord-w"
                        hoverCoord |> AVal.map (function Some p -> "world " + fmt p | None -> "world  –")
                    }
                    span {
                        Class "tb-coord-l"
                        let centsNames = (model.DatasetCentroids, model.MeshNames.Content) ||> AVal.map2 (fun c n -> c, IndexList.toList n)
                        (hoverCoord, model.Selection.Active |> AVal.map Selection.mesh, centsNames)
                        |||> AVal.map3 (fun hc fm (cents, names) ->
                            match hc, fm with
                            | Some p, Some name ->
                                match Map.tryFind name cents with
                                | Some c -> sprintf "    %s  %s" (Primitives.friendlyName names name) (fmt (p - c))
                                | None -> ""
                            | _ -> "")
                    }
                }
                div {
                    Class "tb-gear-wrap"
                    button {
                        Class "tb-btn-tiny"
                        classWhen "tb-btn-active" model.GearPopoverOpen
                        Attribute("title", "Debug & settings")
                        Dom.OnClick(fun _ -> env.Emit [ToggleGearPopover])
                        "⚙"
                    }
                    div {
                        Class "tb-gear-popover"
                        showWhen model.GearPopoverOpen
                        div {
                            Class "tb-gear-row"
                            span { Class "lp-sublabel"; "Dataset" }
                            div {
                                Class "tb-gear-btn-row"
                                model.Datasets |> AVal.map IndexList.ofList |> AList.ofAVal |> AList.map (fun dataset ->
                                    let isActive = model.ActiveDataset |> AVal.map (fun a -> a = Some dataset)
                                    button {
                                        Class "tb-gear-btn"
                                        classWhen "active" isActive
                                        Dom.OnClick(fun _ ->
                                            env.Emit [SetActiveDataset dataset]
                                            ServerActions.loadDataset env dataset)
                                        dataset
                                    })
                            }
                        }
                        div {
                            Class "tb-gear-row"
                            span { Class "lp-sublabel"; "Rendering" }
                            compactButtonBar [
                                "Textured", (model.RenderingMode |> AVal.map (fun m -> m = Textured)), (fun () -> env.Emit [SetRenderingMode Textured])
                                "Shaded",   (model.RenderingMode |> AVal.map (fun m -> m = Shaded)),    (fun () -> env.Emit [SetRenderingMode Shaded])
                                "Slope",    (model.RenderingMode |> AVal.map (fun m -> m = SlopeColor)),(fun () -> env.Emit [SetRenderingMode SlopeColor])
                            ]
                        }
                        div {
                            Class "tb-gear-row"
                            inlineSlider "Outline edge threshold" 0.0001 0.01 0.0001 (sprintf "%.4f") model.OutlineThreshold (fun v ->
                                env.Emit [SetOutlineThreshold v])
                        }
                        div {
                            Class "tb-gear-row"
                            inlineSlider "Isolines over Z range" 4.0 2000.0 1.0 (sprintf "%.0f") model.IsolineBands (fun v ->
                                env.Emit [SetIsolineBands v])
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
                            // Auto-suspended (and inert) while placing a pin, so
                            // the terrain stays visible.
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
                            numberInput "Quick-pin radius (m)" 0.01 50.0 0.005 (sprintf "%.3f") model.QuickPinRadius (fun v ->
                                env.Emit [SetQuickPinRadius v])
                        }
                        div {
                            Class "tb-gear-row"
                            inlineSlider "Pin flag scale" 0.2 5.0 0.1 (sprintf "%.1f×") model.FlagScale (fun v ->
                                env.Emit [SetFlagScale v])
                        }
                        // Slice-cell tunables (§A): one global window / context /
                        // vertical scale for every matrix slice diagram.
                        div {
                            Class "tb-gear-row"
                            inlineSlider "Slice window (× spacing)" 2.0 12.0 0.5 (sprintf "%.1f") model.SliceNSamples (fun v ->
                                env.Emit [SetSliceNSamples v])
                        }
                        div {
                            Class "tb-gear-row"
                            inlineSlider "Slice context (each side)" 0.0 4.0 1.0 (sprintf "%.0f") model.SliceContextCount (fun v ->
                                env.Emit [SetSliceContextCount v])
                        }
                        div {
                            Class "tb-gear-row"
                            inlineSlider "Slice context spacing (× window)" 0.02 0.5 0.01 (sprintf "%.2f") model.SliceContextSpacing (fun v ->
                                env.Emit [SetSliceContextSpacing v])
                        }
                        div {
                            Class "tb-gear-row"
                            inlineSlider "Slice vertical percentile" 0.5 1.0 0.01 (sprintf "%.2f") model.SliceVertPercentile (fun v ->
                                env.Emit [SetSliceVertPercentile v])
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
                                let numName =
                                    (model.MeshOrder |> AMap.tryFind name, model.MeshNames.Content) ||> AVal.map2 (fun o ns ->
                                        sprintf "%d  %s" ((Option.defaultValue 0 o) + 1) (Primitives.friendlyName (IndexList.toList ns) name))
                                div {
                                    Class "tb-gear-mesh-row"
                                    span { Class "tb-gear-mesh-name"; numName }
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
