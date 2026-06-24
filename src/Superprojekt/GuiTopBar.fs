namespace Superprojekt

open Aardvark.Base
open Aardworx.WebAssembly
open FSharp.Data.Adaptive
open Aardvark.Dom

module GuiTopBar =

    open Primitives

    let topBar (env : Env<Message>) (model : AdaptiveModel) (hoverCoord : aval<V3d option>) =
        // §6 guards: these actions are blocked while a preview is pending (the
        // reducer also rejects them; this is just the affordance).
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

            button {
                Class "tb-btn tb-btn-icon"
                Attribute("title", "Reset camera")
                Dom.OnClick(fun _ -> env.Emit [ResetCamera])
                "⟲"
            }

            // Spring-loaded reference peek: while held, ghost every mesh except
            // the reference (★). Transient — never mutates the eye toggles.
            // Pointer-leave/up both release so it can't stick.
            button {
                Class "tb-btn"
                model.ReferencePeekHeld |> AVal.map (fun on -> if on then Some (Class "tb-btn-active") else None)
                Attribute("title", "Peek reference: hold to show only the reference mesh (hotkey: R)")
                Dom.OnPointerDown((fun _ -> env.Emit [SetReferencePeek true]), pointerCapture = true)
                Dom.OnPointerUp((fun _ -> env.Emit [SetReferencePeek false]), pointerCapture = true)
                Dom.OnMouseLeave(fun _ -> env.Emit [SetReferencePeek false])
                "👁 Peek"
            }

            div {
                Class "tb-right"
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
                            span { Class "lp-sublabel"; "Dataset" }
                            div {
                                Class "tb-gear-btn-row"
                                model.Datasets |> AVal.map IndexList.ofList |> AList.ofAVal |> AList.map (fun dataset ->
                                    let isActive = model.ActiveDataset |> AVal.map (fun a -> a = Some dataset)
                                    button {
                                        Class "tb-gear-btn"
                                        isActive |> AVal.map (fun on -> if on then Some (Class "active") else None)
                                        previewDisabled
                                        Dom.OnClick(fun _ ->
                                            if not (AVal.force previewOn) then
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
                            compactToggle "Per-mesh outlines (image-space)" model.OutlineMode (fun () ->
                                env.Emit [ToggleOutlines])
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
                            // Isolate pins: ghost everything outside the pins'
                            // radius regions. Auto-suspended (and inert) while
                            // placing an anchor, so the terrain stays visible.
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
                                    model.MeshOrder |> AMap.tryFind name |> AVal.map (fun o ->
                                        sprintf "%d  %s" ((Option.defaultValue 0 o) + 1) (Cards.shortName name))
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
