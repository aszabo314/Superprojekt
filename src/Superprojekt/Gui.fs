namespace Superprojekt

open Aardvark.Base
open FSharp.Data.Adaptive
open Aardvark.Dom

module Gui =

    open Primitives

    let private pinColor =
        [| C4b(228uy,26uy,28uy); C4b(55uy,126uy,184uy); C4b(77uy,175uy,74uy); C4b(152uy,78uy,163uy)
           C4b(255uy,127uy,0uy); C4b(255uy,255uy,51uy); C4b(166uy,86uy,40uy); C4b(247uy,129uy,191uy); C4b(153uy,153uy,153uy) |]

    let private meshColorFor (meshOrder : HashMap<string,int>) (name : string) =
        let idx = HashMap.tryFind name meshOrder |> Option.defaultValue 0
        pinColor.[idx % pinColor.Length]

    let topBar (env : Env<Message>) (model : AdaptiveModel) =
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
                        sprintf "%s \u25BE" name)
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

            let explorePopoverOpen = model.ExplorePopoverOpen
            let exploreEnabled = model.Explore |> AVal.map (fun e -> e.Enabled)

            div {
                Class "tb-explore-wrap"
                button {
                    Class "tb-btn"
                    exploreEnabled |> AVal.map (fun on -> if on then Some (Class "tb-btn-active") else None)
                    Attribute("title", "Toggle explore heatmap")
                    Dom.OnClick(fun _ ->
                        let cur = AVal.force exploreEnabled
                        env.Emit [ExploreMsg (SetExploreEnabled (not cur))]
                        if not cur then env.Emit [ToggleExplorePopover])
                    "\u25C9 Explore"
                }
                div {
                    Class "tb-explore-popover"
                    (exploreEnabled, explorePopoverOpen) ||> AVal.map2 (fun en op ->
                        if en && op then None else Some (Style [Display "none"]))
                    let steep = model.Explore |> AVal.map (fun e -> e.SteepnessThreshold)
                    let disag = model.Explore |> AVal.map (fun e -> e.DisagreementThreshold)
                    inlineSlider "Steepness" 0.0 1.0 0.01 (sprintf "%.2f") steep (fun v ->
                        env.Emit [ExploreMsg (SetSteepnessThreshold v)])
                    inlineLogSlider "Sensitivity" 0.001 10.0 (fun v ->
                        if v < 0.1 then sprintf "%.0f mm" (v * 1000.0)
                        else sprintf "%.2f m" v) disag (fun v ->
                        env.Emit [ExploreMsg (SetDisagreementThreshold v)])
                }
                button {
                    Class "tb-btn-tiny"
                    exploreEnabled |> AVal.map (fun on -> if on then None else Some (Style [Display "none"]))
                    Attribute("title", "Explore settings")
                    Dom.OnClick(fun _ -> env.Emit [ToggleExplorePopover])
                    "\u2699"
                }
            }

            let isPlacing =
                (model.ScanPins.PlacingMode, model.ScanPins.ActivePlacement) ||> AVal.map2 (fun pm ap -> pm.IsSome || ap.IsSome)

            button {
                Class "tb-btn"
                isPlacing |> AVal.map (fun on -> if on then Some (Class "tb-btn-active") else None)
                Attribute("title", "Place a ScanPin")
                Dom.OnClick(fun _ ->
                    let placing = AVal.force isPlacing
                    if placing then env.Emit [ScanPinMsg CancelPlacement]
                    else env.Emit [ScanPinMsg (StartPlacement FootprintMode.Circle)])
                isPlacing |> AVal.map (fun on -> if on then "\u2715 Cancel" else "+ Pin")
            }

            button {
                Class "tb-btn"
                model.RevolverOn |> AVal.map (fun on -> if on then Some (Class "tb-btn-active") else None)
                Attribute("title", "Toggle revolver overlay")
                Dom.OnClick(fun _ -> env.Emit [ToggleRevolver])
                "\u229E Revolver"
            }

            button {
                Class "tb-btn tb-btn-icon"
                Attribute("title", "Reset camera")
                Dom.OnClick(fun _ -> env.Emit [ResetCamera])
                "\u27F2"
            }
        }

    let revolverBar (env : Env<Message>) (model : AdaptiveModel) =
        div {
            Class "rev-bar"
            model.RevolverOn |> AVal.map (fun on ->
                if on then None else Some (Style [Display "none"]))
            button {
                Class "tb-btn-tiny"
                Attribute("title", "Previous mesh")
                Dom.OnClick(fun _ -> env.Emit [CycleMeshOrder -1])
                "\u25C0"
            }
            button {
                Class "tb-btn-tiny"
                Attribute("title", "Next mesh")
                Dom.OnClick(fun _ -> env.Emit [CycleMeshOrder 1])
                "\u25B6"
            }
            let size = model.RevolverSettings |> AVal.map (fun r -> r.CircleRadius)
            inlineSlider "Size" 20.0 400.0 1.0 (sprintf "%.0f") size (fun v ->
                env.Emit [SetRevolverRadius v])
        }

    let private meshRow (env : Env<Message>) (model : AdaptiveModel) (name : string) =
        let isVis = model.MeshVisible |> AVal.map (fun m -> Map.tryFind name m |> Option.defaultValue true)
        let isSolo = model.MeshSolo |> AVal.map (fun s ->
            match s with Solo(n, _) -> n = name | _ -> false)
        let colorVal = model.MeshOrder |> AMap.toAVal |> AVal.map (fun o -> meshColorFor o name)
        div {
            Class "mesh-row"
            span {
                Class "mesh-swatch"
                colorVal |> AVal.map (fun c ->
                    Some (Style [Css.Background (sprintf "rgb(%d,%d,%d)" (int c.R) (int c.G) (int c.B))]))
            }
            span {
                Class "mesh-name"
                Cards.shortName name
            }
            button {
                Class "mb"
                isVis |> AVal.map (fun v -> if v then Some (Class "mb-on") else None)
                Attribute("title", "Visible")
                Dom.OnClick(fun _ ->
                    let cur = AVal.force isVis
                    env.Emit [SetVisible(name, not cur)])
                isVis |> AVal.map (fun v -> if v then "\u25CF" else "\u25CB")
            }
            button {
                Class "mb"
                isSolo |> AVal.map (fun s -> if s then Some (Class "mb-on") else None)
                Attribute("title", "Solo (isolate)")
                Dom.OnClick(fun _ -> env.Emit [ToggleMeshSolo name])
                "\u25D0"
            }
            button {
                Class "mb"
                Attribute("title", "Focus camera on this mesh")
                Dom.OnClick(fun _ -> env.Emit [JumpToMesh name])
                "\u2316"
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
                    "White",    (rm |> AVal.map (fun m -> m = WhiteSurface)), (fun () -> env.Emit [SetRenderingMode WhiteSurface])
                ]
            }
            compactToggle "Ghost silhouette" model.GhostSilhouette (fun () ->
                env.Emit [ToggleGhostSilhouette])
        }

    let private placementPanel (env : Env<Message>) (model : AdaptiveModel) =
        let sp = model.ScanPins
        let activePin =
            (sp.ActivePlacement, sp.Pins |> AMap.toAVal) ||> AVal.map2 (fun id pins ->
                id |> Option.bind (fun i -> HashMap.tryFind i pins))
        div {
            Class "lp-placement"
            div { Class "lp-section-title"; "Placing Pin" }
            p {
                Class "lp-hint"
                sp.PlacingMode |> AVal.map (fun pm ->
                    if pm.IsSome then "Tap on a mesh to anchor the pin." else "")
            }

            let radius = activePin |> AVal.map (fun p ->
                match p with
                | Some pin ->
                    match pin.Prism.Footprint.Vertices with v :: _ -> v.Length | _ -> 1.0
                | None -> 1.0)
            inlineSlider "Radius" 0.1 50.0 0.1 (sprintf "%.1fm") radius (fun v ->
                env.Emit [ScanPinMsg (SetFootprintRadius v)])

            let length = activePin |> AVal.map (fun p ->
                match p with
                | Some pin -> pin.Prism.ExtentBackward
                | None -> 3.0)
            inlineSlider "Length" 0.5 100.0 0.5 (sprintf "%.1fm") length (fun v ->
                env.Emit [ScanPinMsg (SetPinLength v)])

            div {
                Class "lp-sub"
                span { Class "lp-sublabel"; "Cut Plane" }
                let mode = activePin |> AVal.map (fun p ->
                    match p with
                    | Some pin ->
                        match pin.CutPlane with
                        | CutPlaneMode.AlongAxis _ -> 0
                        | CutPlaneMode.AcrossAxis _ -> 1
                    | None -> 0)
                compactButtonBar [
                    "Vertical",   (mode |> AVal.map (fun m -> m = 0)),
                        (fun () ->
                            match AVal.force activePin with
                            | Some p ->
                                match p.CutPlane with
                                | CutPlaneMode.AlongAxis _ -> ()
                                | _ -> env.Emit [ScanPinMsg (SetCutPlaneMode (CutPlaneMode.AlongAxis 0.0))]
                            | None -> ())
                    "Horizontal", (mode |> AVal.map (fun m -> m = 1)),
                        (fun () ->
                            match AVal.force activePin with
                            | Some p ->
                                match p.CutPlane with
                                | CutPlaneMode.AcrossAxis _ -> ()
                                | _ ->
                                    let mid = (p.Prism.ExtentForward - p.Prism.ExtentBackward) * 0.5
                                    env.Emit [ScanPinMsg (SetCutPlaneMode (CutPlaneMode.AcrossAxis mid))]
                            | None -> ())
                ]
            }

            let ghost = activePin |> AVal.map (fun p ->
                match p with
                | Some pin -> pin.GhostClip = GhostClipOn
                | None -> false)
            compactToggle "Ghost clip" ghost (fun () ->
                match AVal.force activePin with
                | Some p ->
                    let next = if p.GhostClip = GhostClipOn then GhostClipOff else GhostClipOn
                    env.Emit [ScanPinMsg (SetGhostClip(p.Id, next))]
                | None -> ())

            div {
                Class "lp-commit-row"
                button {
                    Class "lp-commit"
                    Dom.OnClick(fun _ -> env.Emit [ScanPinMsg CommitPin])
                    "\u2713 Commit"
                }
                button {
                    Class "lp-discard"
                    Dom.OnClick(fun _ -> env.Emit [ScanPinMsg CancelPlacement])
                    "\u2715 Discard"
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
                                | Some p when p.Phase = PinPhase.Placement -> "\u25CB"
                                | Some _ -> "\u25CF"
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
                                    let a = p.Prism.AnchorPoint
                                    sprintf "(%.1f, %.1f, %.1f)" a.X a.Y a.Z
                                | None -> "(removed)")
                        }
                        button {
                            Class "mb"; Attribute("title", "Focus")
                            Dom.OnClick(fun _ ->
                                env.Emit [ScanPinMsg (SelectPin (Some id)); ScanPinMsg (FocusPin id)])
                            "\u2316"
                        }
                        button {
                            Class "mb"; Attribute("title", "Edit")
                            pinVal |> AVal.map (fun po ->
                                match po with
                                | Some p when p.Phase = PinPhase.Committed -> None
                                | _ -> Some (Style [Display "none"]))
                            Dom.OnClick(fun _ -> env.Emit [EditPin id])
                            "\u270E"
                        }
                        button {
                            Class "mb"; Attribute("title", "Delete")
                            Dom.OnClick(fun _ -> env.Emit [ScanPinMsg (DeletePin id)])
                            "\u2715"
                        }
                    })
            }
        }

    let private visTechSection (env : Env<Message>) (model : AdaptiveModel) =
        collapsibleSection "Visualization" false (
            div {
                Class "lp-vis-body"
                div {
                    Class "lp-diff-row"
                    compactToggle "Difference" model.DifferenceRendering (fun () ->
                        env.Emit [ToggleDifferenceRendering])
                    inlineRangeSlider
                        ""
                        0.0 20.0 0.1
                        (fun lo hi -> sprintf "%.1f\u2013%.1fm" lo hi)
                        model.MinDifferenceDepth model.MaxDifferenceDepth
                        (fun lo hi ->
                            env.Emit [SetMinDifferenceDepth lo; SetMaxDifferenceDepth hi])
                }

                collapsibleSection "Clipping Box" false (
                    div {
                        Class "lp-clip-body"
                        compactToggle "Enabled" model.ClipActive (fun () ->
                            env.Emit [ToggleClip])

                        let bounds = model.ClipBounds
                        let box = model.ClipBox
                        let axisSlider (label : string) (getLo : Box3d -> float) (getHi : Box3d -> float)
                                       (setLo : Box3d -> float -> Box3d) (setHi : Box3d -> float -> Box3d) =
                            let lo = box |> AVal.map getLo
                            let hi = box |> AVal.map getHi
                            let b = AVal.force bounds
                            let bLo = if b.IsInvalid then -100.0 else getLo b
                            let bHi = if b.IsInvalid then 100.0 else getHi b
                            let step = max 0.01 ((bHi - bLo) / 100.0)
                            inlineRangeSlider label bLo bHi step
                                (fun a b -> sprintf "%.1f\u2013%.1f" a b) lo hi (fun a b ->
                                let cur = AVal.force box
                                let cur = setLo cur a
                                let cur = setHi cur b
                                env.Emit [SetClipBox cur])

                        axisSlider "X"
                            (fun b -> b.Min.X) (fun b -> b.Max.X)
                            (fun b v -> Box3d(V3d(v, b.Min.Y, b.Min.Z), b.Max))
                            (fun b v -> Box3d(b.Min, V3d(v, b.Max.Y, b.Max.Z)))
                        axisSlider "Y"
                            (fun b -> b.Min.Y) (fun b -> b.Max.Y)
                            (fun b v -> Box3d(V3d(b.Min.X, v, b.Min.Z), b.Max))
                            (fun b v -> Box3d(b.Min, V3d(b.Max.X, v, b.Max.Z)))
                        axisSlider "Z"
                            (fun b -> b.Min.Z) (fun b -> b.Max.Z)
                            (fun b v -> Box3d(V3d(b.Min.X, b.Min.Y, v), b.Max))
                            (fun b v -> Box3d(b.Min, V3d(b.Max.X, b.Max.Y, v)))
                    })
            })

    let leftPanel (env : Env<Message>) (model : AdaptiveModel) =
        let placing = (model.ScanPins.PlacingMode, model.ScanPins.ActivePlacement) ||> AVal.map2 (fun pm ap -> pm.IsSome || ap.IsSome)
        div {
            Class "left-panel"
            model.MenuOpen |> AVal.map (fun o -> if o then Some (Class "open") else None)

            placing |> AVal.map (fun p ->
                if p then
                    placementPanel env model
                else
                    div {
                        Class "lp-normal"
                        meshSection env model
                        pinSection env model
                        visTechSection env model
                    })
        }

    let bottomBar (env : Env<Message>) (model : AdaptiveModel) =
        div {
            Class "bottom-bar"
            div {
                Class "bb-spacer"
            }
            button {
                Class "bb-debug-toggle"
                model.BottomBarExpanded |> AVal.map (fun e -> if e then Some (Class "active") else None)
                Dom.OnClick(fun _ -> env.Emit [ToggleBottomBar])
                model.BottomBarExpanded |> AVal.map (fun e -> if e then "\u25BE Debug" else "\u25B8 Debug")
            }
            div {
                Class "bb-debug-panel"
                model.BottomBarExpanded |> AVal.map (fun e -> if e then None else Some (Style [Display "none"]))

                div {
                    Class "bb-debug-row"
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
                    Class "bb-debug-row"
                    inlineSlider "Camera speed" 0.05 2.0 0.01 (sprintf "%.2f") model.Camera.speed (fun v ->
                        env.Emit [CameraMessage (OrbitMessage.SetSpeed v)])
                }

                div {
                    Class "bb-debug-row"
                    span { Class "lp-sublabel"; "Dataset" }
                    span {
                        Class "bb-debug-val"
                        (model.ActiveDataset, model.ClipBounds, model.CommonCentroid)
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
                    Class "bb-mesh-info"
                    model.MeshNames |> AList.map (fun name ->
                        let centroid = model.DatasetCentroids |> AVal.map (fun m -> Map.tryFind name m |> Option.defaultValue V3d.Zero)
                        div {
                            Class "bb-mesh-row"
                            span { Class "bb-mesh-name"; Cards.shortName name }
                            span {
                                Class "bb-mesh-coord"
                                centroid |> AVal.map (fun c ->
                                    sprintf "centroid (%.1f, %.1f, %.1f)" c.X c.Y c.Z)
                            }
                        })
                }

                div {
                    Class "bb-debug-log"
                    model.DebugLog |> AList.map (fun line -> div { Class "bb-log-line"; line })
                }
            }
        }

    let fullscreenInfo (model : AdaptiveModel) =
        div {
            Class "fullscreen-info"
            model.FullscreenOn |> AVal.map (fun on ->
                if not on then Some (Style [Display "none"]) else None)
            model.ActiveDataset |> AVal.map (fun ds ->
                match ds with
                | Some d -> div { Class "fullscreen-info-title"; d }
                | None   -> div { []  })
            model.MeshNames |> AList.map (fun name ->
                let order = model.MeshOrder |> AMap.tryFind name |> AVal.map (Option.defaultValue 0)
                div {
                    order |> AVal.map (fun o -> sprintf "%d  %s" (o + 1) (Cards.shortName name))
                })
        }

    let coordinateDisplay (coord : aval<V3d option>) =
        div {
            Class "coord-display"
            coord |> AVal.map (fun c ->
                match c with
                | None   -> ""
                | Some p -> sprintf "X: %.2f   Y: %.2f   Z: %.2f" p.X p.Y p.Z)
        }
