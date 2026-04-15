namespace Superprojekt

open Aardvark.Base
open FSharp.Data.Adaptive
open Aardvark.Dom

module Gui =

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
                            Class "input-sm"
                            (model.ActiveDataset, model.DatasetScales) ||> AVal.map2 (fun ds scales ->
                                let s = ds |> Option.bind (fun d -> Map.tryFind d scales) |> Option.defaultValue 1.0
                                Some (Attribute("value", sprintf "%.4g" s))
                            )
                            model.ActiveDataset |> AVal.map (fun ds ->
                                if ds.IsNone then Some (Attribute("disabled", "disabled")) else None
                            )
                            Dom.OnInput(fun e ->
                                match Cards.parseFloat e.Value, AVal.force model.ActiveDataset with
                                | Some v, Some dataset -> env.Emit [SetDatasetScale(dataset, v)]
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
                                    Cards.checkedIf isVis
                                    Dom.OnClick(fun _ ->
                                        let current = AVal.force isVis
                                        env.Emit [SetVisible(name, not current)]
                                    )
                                }
                                " " + Cards.shortName name
                            }
                        }
                    )

                    div {
                        label {
                            input {
                                Attribute("type", "checkbox")
                                Cards.checkedIf model.GhostSilhouette
                                Dom.OnClick(fun _ -> env.Emit [ToggleGhostSilhouette])
                            }
                            " Ghost silhouette"
                        }
                        div {
                            model.GhostSilhouette |> AVal.map (fun on ->
                                if on then None else Some (Style [Display "none"]))
                            "Opacity  "
                            input {
                                Attribute("type", "range")
                                Attribute("min", "0.01"); Attribute("max", "1.0"); Attribute("step", "0.01")
                                Class "range-full"
                                model.GhostOpacity |> AVal.map (fun v ->
                                    Some (Attribute("value", sprintf "%.2f" v)))
                                Dom.OnInput(fun e ->
                                    Cards.parseFloat e.Value |> Option.iter (fun v -> env.Emit [SetGhostOpacity v]))
                            }
                        }
                    }

                    div {
                        label {
                            input {
                                Attribute("type", "checkbox")
                                Cards.checkedIf model.ColorMode
                                Dom.OnClick(fun _ -> env.Emit [ToggleColorMode])
                            }
                            " Color (false-color shading)"
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
                                Cards.checkedIf model.DifferenceRendering
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
                                Class "input-sm"
                                current |> AVal.map (fun v -> Some (Attribute("value", sprintf "%.4g" v)))
                                Dom.OnInput(fun e ->
                                    Cards.parseFloat e.Value |> Option.iter (fun v -> env.Emit [msg v]))
                            }
                        }
                    numInput "Min depth" model.MinDifferenceDepth SetMinDifferenceDepth
                    numInput "Max depth" model.MaxDifferenceDepth SetMaxDifferenceDepth

                    h3 { "Explore mode" }
                    p { "Heatmap: steep faces with inter-mesh disagreement" }
                    let exploreEnabled  = model.Explore |> AVal.map (fun e -> e.Enabled)
                    let steepnessThresh = model.Explore |> AVal.map (fun e -> e.SteepnessThreshold)
                    let varianceThresh  = model.Explore |> AVal.map (fun e -> e.VarianceThreshold)
                    div {
                        label {
                            input {
                                Attribute("type", "checkbox")
                                Cards.checkedIf exploreEnabled
                                Dom.OnClick(fun _ ->
                                    let cur = AVal.force exploreEnabled
                                    env.Emit [ExploreMsg (SetExploreEnabled (not cur))])
                            }
                            " Enabled"
                        }
                    }
                    div {
                        "Face steepness filter  "
                        input {
                            Attribute("type", "range")
                            Attribute("min", "0.0"); Attribute("max", "1.0"); Attribute("step", "0.01")
                            Class "range-full"
                            steepnessThresh |> AVal.map (fun v -> Some (Attribute("value", sprintf "%.2f" v)))
                            Dom.OnInput(fun e ->
                                Cards.parseFloat e.Value |> Option.iter (fun v -> env.Emit [ExploreMsg (SetSteepnessThreshold v)]))
                        }
                        steepnessThresh |> AVal.map (fun v -> sprintf "%.2f" v)
                    }
                    div {
                        "Change sensitivity  "
                        input {
                            Attribute("type", "range")
                            Attribute("min", "0.00001"); Attribute("max", "0.1"); Attribute("step", "0.00001")
                            Class "range-full"
                            varianceThresh |> AVal.map (fun v -> Some (Attribute("value", sprintf "%.5f" v)))
                            Dom.OnInput(fun e ->
                                Cards.parseFloat e.Value |> Option.iter (fun v -> env.Emit [ExploreMsg (SetVarianceThreshold v)]))
                        }
                        varianceThresh |> AVal.map (fun v -> sprintf "%.5f" v)
                    }

                    h3 { "Reference axis" }
                    p { "Defines 'steep' for explore mode and pin placement axis" }
                    div {
                        Class "btn-row"
                        button {
                            model.ReferenceAxis |> AVal.map (fun m ->
                                if m = AlongWorldZ then Some (Class "btn-active") else None)
                            Dom.OnClick(fun _ -> env.Emit [ExploreMsg (SetReferenceAxisMode AlongWorldZ)])
                            "World Z"
                        }
                        button {
                            model.ReferenceAxis |> AVal.map (fun m ->
                                if m = AlongCameraView then Some (Class "btn-active") else None)
                            Dom.OnClick(fun _ -> env.Emit [ExploreMsg (SetReferenceAxisMode AlongCameraView)])
                            "Camera direction"
                        }
                    }

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
                            order |> AVal.map (fun o -> sprintf "%d  %s" (o + 1) (Cards.shortName name))
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
                                Cards.checkedIf model.ClipActive
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
                                Class "range-full"
                                Dom.OnInput(fun e ->
                                    Cards.parseFloat e.Value |> Option.iter (fun v -> env.Emit [SetClipBox(setValue (AVal.force model.ClipBox) v)]))
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
                GuiPins.pinsTabPanel env model
            }
        }

    let fullscreenInfo (model : AdaptiveModel) =
        div {
            Class "fullscreen-info"
            model.FullscreenOn |> AVal.map (fun on ->
                if not on then Some (Style [Display "none"]) else None
            )
            model.ActiveDataset |> AVal.map (fun ds ->
                match ds with
                | Some d -> div { Class "fullscreen-info-title"; d }
                | None   -> div { []  }
            )
            model.MeshNames |> AList.map (fun name ->
                let order = model.MeshOrder |> AMap.tryFind name |> AVal.map (Option.defaultValue 0)
                div {
                    order |> AVal.map (fun o -> sprintf "%d  %s" (o + 1) (Cards.shortName name))
                }
            )
        }

    let coordinateDisplay (coord : aval<V3d option>) =
        div {
            Class "coord-display"
            coord |> AVal.map (fun c ->
                match c with
                | None   -> ""
                | Some p -> sprintf "X: %.2f   Y: %.2f   Z: %.2f" p.X p.Y p.Z
            )
        }

    let debugLogToggle (visible : cval<bool>) =
        button {
            Class "debug-toggle"
            Dom.OnClick(fun _ -> transact (fun () -> visible.Value <- not visible.Value))
            (visible :> aval<bool>) |> AVal.map (fun v -> if v then "▼ log" else "▲ log")
        }

    let debugLog (visible : aval<bool>) (model : AdaptiveModel) =
        div {
            Class "debug-log"
            visible |> AVal.map (fun v -> if not v then Some (Style [Display "none"]) else None)
            model.DebugLog |> AList.map (fun line -> div { line })
        }
