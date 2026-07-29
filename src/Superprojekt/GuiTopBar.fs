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
            div {
                Class "tb-cut"
                Attribute("title", "Near cut: slice the scene in place — the plane sits at this fraction of the distance to the orbit centre, a thick line marks the intersection; 0 = off")
                inlineSlider "▤ Cut" 0.0 1.25 0.01
                    (fun v -> if v <= 0.005 then "off" else sprintf "%.2f" v)
                    model.NearCutFrac (fun v -> env.Emit [SetNearCut v])
            }

            // Spring-loaded peek buttons: press-and-hold twins of the V/B keys
            // (Pair scope; the reducer re-guards, releases always land).
            // Pointer capture keeps the release landing even when the cursor
            // slides off the button mid-hold.
            let canPeek =
                AVal.custom (fun t ->
                    model.Focus.GetValue t = FocusPair &&
                    (match (model.Sel.GetValue t).Pair with
                     | Some (a, b) ->
                        let loaded = model.MeshesLoaded.Content.GetValue t
                        HashSet.contains a loaded && HashSet.contains b loaded
                     | None -> false))
            let peekBtn (label : string) (title : string) (heldA : aval<bool>) (set : bool -> unit) =
                button {
                    Class "tb-btn-tiny tb-peek"
                    classWhen "tb-btn-active" heldA
                    Attribute("title", title)
                    Dom.OnPointerDown((fun _ -> set true), pointerCapture = true)
                    Dom.OnPointerUp((fun _ -> set false), pointerCapture = true)
                    label
                }
            div {
                Class "tb-peeks"
                showWhen canPeek
                span { Class "lp-sublabel"; "Peek" }
                peekBtn "◌ V" "Hold: the moving mesh blinks off — is this the same rock? (same as holding V)"
                    model.PeekVis (fun h -> env.Emit [SetPeekVis h])
                peekBtn "↺ B" "Hold: the moving mesh snaps to its as-loaded pose — did registration help? (same as holding B)"
                    model.PeekPose (fun h -> env.Emit [SetPeekPose h])
            }

            div {
                Class "tb-right"
                div {
                    Class "tb-coord"
                    Attribute("title", "Cursor world coordinate (drops to the mean-elevation XY plane when off-mesh)")
                    let fmt (p : V3d) = sprintf "%.1f  %.1f  %.1f" p.X p.Y p.Z
                    span {
                        Class "tb-coord-w"
                        hoverCoord |> AVal.map (function Some p -> "world " + fmt p | None -> "world  –")
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
                    let gearSlider label lo hi step fmt v (msg : float -> Message) =
                        div {
                            Class "tb-gear-row"
                            inlineSlider label lo hi step fmt v (fun x -> env.Emit [msg x])
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
                        gearSlider "Outline edge threshold" 0.0001 0.01 0.0001 (sprintf "%.4f") model.OutlineThreshold SetOutlineThreshold
                        gearSlider "Outline thickness (px)" 1.0 8.0 0.5 (sprintf "%.1f px") model.OutlineWidthPx SetOutlineWidth
                        gearSlider "Isolines over Z range" 4.0 2000.0 1.0 (sprintf "%.0f") model.IsolineBands SetIsolineBands
                        gearSlider "Isoline opacity" 0.0 1.0 0.01 (sprintf "%.2f") model.IsolineOpacity SetIsolineOpacity
                        gearSlider "Camera speed" 0.05 2.0 0.01 (sprintf "%.2f") model.Camera.speed (fun v -> CameraMessage (OrbitMessage.SetSpeed v))
                        div {
                            Class "tb-gear-row"
                            compactToggle "Ghost silhouette" model.GhostSilhouette (fun () ->
                                env.Emit [ToggleGhostSilhouette])
                            inlineSlider "Ghost opacity" 0.0 1.0 0.01 (sprintf "%.2f") model.GhostOpacity (fun v ->
                                env.Emit [SetGhostOpacity v])
                        }
                        gearSlider "Shading strength" 0.0 1.0 0.01 (sprintf "%.2f") model.ShadingStrength SetShadingStrength
                        gearSlider "Slope threshold (°)" 1.0 89.0 1.0 (sprintf "%.0f°") model.SlopeThresholdDeg SetSlopeThresholdDeg
                        div {
                            Class "tb-gear-row"
                            numberInput "Quick-pin radius (m)" 0.01 50.0 0.005 (sprintf "%.3f") model.QuickPinRadius (fun v ->
                                env.Emit [SetQuickPinRadius v])
                        }
                        gearSlider "Pin flag scale" 0.2 5.0 0.1 (sprintf "%.1f×") model.FlagScale SetFlagScale
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
                    }
                }
            }
        }
