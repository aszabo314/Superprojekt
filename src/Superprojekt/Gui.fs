namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open FSharp.Data.Adaptive
open Aardvark.Dom

module Gui =

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

            let exploreEnabled = model.Explore |> AVal.map (fun e -> e.Enabled)

            button {
                Class "tb-btn"
                exploreEnabled |> AVal.map (fun on -> if on then Some (Class "tb-btn-active") else None)
                Attribute("title", "Toggle explore heatmap")
                Dom.OnClick(fun _ ->
                    let cur = AVal.force exploreEnabled
                    env.Emit [ExploreMsg (SetExploreEnabled (not cur))])
                "\u25C9 Explore"
            }

            let placingMode =
                model.ScanPins.Placement |> AVal.map (function
                    | PlacementIdle -> None
                    | ProfilePlacement _ -> Some ProfileMode
                    | PlanPlacement _ -> Some PlanMode
                    | AutoPlacement _ -> Some AutoMode
                    | AdjustingPin(_, m) -> Some m)
            let exploreOn = model.Explore |> AVal.map (fun e -> e.Enabled)

            let modeButton (mode : PlacementMode) (label : string) (tooltip : string) (enabled : aval<bool>) =
                button {
                    (placingMode, enabled) ||> AVal.map2 (fun cur en ->
                        let baseCls = "tb-seg-btn"
                        match cur with
                        | Some m when m = mode -> Class (baseCls + " tb-seg-btn-active")
                        | _ when not en -> Class (baseCls + " tb-seg-btn-disabled")
                        | _ -> Class baseCls)
                    Attribute("title", tooltip)
                    Dom.OnClick(fun _ ->
                        if not (AVal.force enabled) then ()
                        else
                            match AVal.force placingMode with
                            | Some m when m = mode -> env.Emit [ScanPinMsg CancelPlacement]
                            | _ -> env.Emit [ScanPinMsg (SelectPlacementMode mode)])
                    label
                }

            div {
                Class "tb-seg-group"
                Attribute("role", "group")
                Attribute("title", "ScanPin placement mode")
                modeButton ProfileMode "Profile" "Vertical cut — two clicks on a surface" (AVal.constant true)
                modeButton PlanMode    "Plan"    "Horizontal cut — click-drag on a surface" (AVal.constant true)
                modeButton AutoMode    "Auto"    "From explore hot-spot (enable explore mode first)" exploreOn
            }

            button {
                Class "tb-btn tb-btn-icon"
                Attribute("title", "Reset camera")
                Dom.OnClick(fun _ -> env.Emit [ResetCamera])
                "\u27F2"
            }

            div {
                Class "tb-right"
                span {
                    Class "tb-coord"
                    hoverCoord |> AVal.map (fun c ->
                        match c with
                        | Some p -> sprintf "\u2316 %.1f, %.1f, %.1f" p.X p.Y p.Z
                        | None   -> "\u2316 \u2014")
                }

                div {
                    Class "tb-gear-wrap"
                    button {
                        Class "tb-btn-tiny"
                        model.GearPopoverOpen |> AVal.map (fun on -> if on then Some (Class "tb-btn-active") else None)
                        Attribute("title", "Debug & settings")
                        Dom.OnClick(fun _ -> env.Emit [ToggleGearPopover])
                        "\u2699"
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
                            span { Class "lp-sublabel"; "Dataset" }
                            span {
                                Class "tb-gear-val"
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
        let colorVal =
            model.MeshOrder |> AMap.tryFind name |> AVal.map (Option.defaultValue 0 >> meshColor)
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
                    button {
                        Class "mb"
                        model.RevolverOn |> AVal.map (fun on -> if on then Some (Class "mb-on") else None)
                        Attribute("title", "Toggle revolver overlay")
                        Dom.OnClick(fun _ -> env.Emit [ToggleRevolver])
                        "\u229E"
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

    let placementFlyout (env : Env<Message>) (model : AdaptiveModel) =
        let sp = model.ScanPins
        let activePlacementId =
            sp.Placement |> AVal.map (function
                | AdjustingPin(id, _) -> Some id
                | _ -> None)
        let activePin =
            activePlacementId |> AVal.bind (function
                | Some i -> sp.Pins |> AMap.tryFind i
                | None -> AVal.constant None)
        let adjusting =
            sp.Placement |> AVal.map (function AdjustingPin _ -> true | _ -> false)
        let placementHint =
            sp.Placement |> AVal.map (function
                | ProfilePlacement ProfileWaitingForFirstPoint -> "Click the first point on a surface."
                | ProfilePlacement (ProfileWaitingForSecondPoint _) -> "Click the second point to set cut direction."
                | PlanPlacement PlanWaitingForDrag -> "Click and drag to lasso a circular area."
                | PlanPlacement (PlanDragging _) -> "Release to place the pin."
                | AutoPlacement _ -> "Click a hot spot to place the pin."
                | _ -> "")
        let flyoutClass =
            (adjusting, model.MenuOpen) ||> AVal.map2 (fun adj open_ ->
                if not adj then "placement-flyout hidden"
                elif open_ then "placement-flyout pf-left-open"
                else "placement-flyout pf-left-closed")
        div {
            flyoutClass |> AVal.map (fun c -> Some (Class c))
            div { Class "lp-section-title"; "Placing Pin" }
            p {
                Class "lp-hint"
                placementHint
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

            let editingMode =
                sp.Placement |> AVal.map (function
                    | AdjustingPin(_, m) -> Some m
                    | _ -> None)
            div {
                Class "lp-sub"
                editingMode |> AVal.map (function
                    | Some AutoMode | None -> None
                    | Some _ -> Some (Style [Display "none"]))
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
            let ghostCut = activePin |> AVal.map (fun p ->
                match p with
                | Some pin -> pin.GhostClipCutPlane
                | None -> false)
            div {
                Class "lp-ghost-row"
                compactToggle "Solo" ghost (fun () ->
                    match AVal.force activePin with
                    | Some p ->
                        let next = if p.GhostClip = GhostClipOn then GhostClipOff else GhostClipOn
                        env.Emit [ScanPinMsg (SetGhostClip(p.Id, next))]
                    | None -> ())
                div {
                    Class "ct-gated"
                    ghost |> AVal.map (fun g -> if g then None else Some (Class "ct-disabled"))
                    Attribute("title", "Clip in front of cut plane")
                    compactToggle "+ Cut" ghostCut (fun () ->
                        match AVal.force activePin with
                        | Some p when p.GhostClip = GhostClipOn ->
                            env.Emit [ScanPinMsg (SetGhostClipCutPlane(p.Id, not p.GhostClipCutPlane))]
                        | _ -> ())
                }
            }

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
                            let bLo = bounds |> AVal.map (fun b -> if b.IsInvalid then -100.0 else getLo b)
                            let bHi = bounds |> AVal.map (fun b -> if b.IsInvalid then  100.0 else getHi b)
                            let step = (bLo, bHi) ||> AVal.map2 (fun lo hi -> max 0.01 ((hi - lo) / 100.0))
                            inlineRangeSliderA label bLo bHi step
                                None lo hi (fun a b ->
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

    let private niceRoundDistance (d : float) =
        if d <= 0.0 || System.Double.IsNaN d || System.Double.IsInfinity d then 1.0
        else
            let steps = [| 0.1; 0.2; 0.5; 1.0; 2.0; 5.0; 10.0; 20.0; 50.0; 100.0; 200.0; 500.0; 1000.0 |]
            let mag = 10.0 ** floor (log10 d)
            let norm = d / mag
            let picked =
                if norm < 0.15 then 0.1
                elif norm < 0.35 then 0.2
                elif norm < 0.75 then 0.5
                elif norm < 1.5 then 1.0
                elif norm < 3.5 then 2.0
                elif norm < 7.5 then 5.0
                else 10.0
            let v = picked * mag
            steps |> Array.minBy (fun s -> abs (log (s / v)))

    let private formatMeters (m : float) =
        if m >= 1000.0 then sprintf "%g km" (m / 1000.0)
        elif m >= 1.0 then sprintf "%g m" m
        else sprintf "%g cm" (m * 100.0)

    let scaleBar (model : AdaptiveModel) (viewportSize : aval<V2i>) =
        let targetPx = 100.0
        let barInfo =
            AVal.custom (fun t ->
                let radius = model.Camera.radius.GetValue t
                let vp = viewportSize.GetValue t
                let ds = model.ActiveDataset.GetValue t
                let scales = model.DatasetScales.GetValue t
                let scale =
                    match ds with
                    | Some d -> Map.tryFind d scales |> Option.defaultValue 1.0
                    | None -> 1.0
                let h = max 1 vp.Y
                let verticalFov = 90.0 * Constant.RadiansPerDegree
                let renderPerPixel = 2.0 * tan (verticalFov * 0.5) * radius / float h
                let realAt100 = targetPx * renderPerPixel / scale
                let nice = niceRoundDistance realAt100
                let px = nice * scale / renderPerPixel
                let px = if System.Double.IsNaN px || System.Double.IsInfinity px then targetPx else max 10.0 (min 400.0 px)
                px, formatMeters nice)
        let barPx = barInfo |> AVal.map fst
        let barLabel = barInfo |> AVal.map snd
        div {
            Class "scale-bar"
            div {
                Class "sb-bar"
                barPx |> AVal.map (fun px -> Some (Style [Width (sprintf "%.0fpx" px)]))
                span { Class "sb-cap sb-cap-l" }
                span { Class "sb-line" }
                span { Class "sb-cap sb-cap-r" }
            }
            div {
                Class "sb-label"
                barLabel
            }
        }

    let orientationIndicator (model : AdaptiveModel) =
        let axisJson =
            model.Camera.view |> AVal.map (fun cv ->
                let vt = CameraView.viewTrafo cv
                let tr (v : V3d) = vt.Forward.TransformDir v
                let x = tr V3d.IOO
                let y = tr V3d.OIO
                let z = tr V3d.OOI
                let fmt (v : V3d) (name : string) (color : string) =
                    sprintf "{\"x\":%f,\"y\":%f,\"z\":%f,\"n\":\"%s\",\"c\":\"%s\"}" v.X v.Y v.Z name color
                sprintf "[%s,%s,%s]"
                    (fmt x "X" "#dc2626")
                    (fmt y "Y" "#16a34a")
                    (fmt z "Z" "#2563eb"))
        div {
            Class "orient-indicator"
            axisJson |> AVal.map (fun json -> Some (Attribute("data-axes", json)))
            OnBoot [
                "(function(){"
                "var el = __THIS__;"
                "var last = '';"
                "var ns = 'http://www.w3.org/2000/svg';"
                "var W = 60, H = 60, L = 22, cx = W/2, cy = H/2;"
                "function render() {"
                "  var raw = el.getAttribute('data-axes') || '[]';"
                "  if(raw === last) return; last = raw;"
                "  try { var arr = JSON.parse(raw); } catch(e) { return; }"
                "  el.innerHTML = '';"
                "  var svg = document.createElementNS(ns, 'svg');"
                "  svg.setAttribute('width', W); svg.setAttribute('height', H);"
                "  svg.setAttribute('viewBox', '0 0 ' + W + ' ' + H);"
                "  arr.sort(function(a,b){return a.z - b.z;});"
                "  arr.forEach(function(a){"
                "    var ex = cx + a.x * L, ey = cy - a.y * L;"
                "    var ln = document.createElementNS(ns, 'line');"
                "    ln.setAttribute('x1', cx); ln.setAttribute('y1', cy);"
                "    ln.setAttribute('x2', ex); ln.setAttribute('y2', ey);"
                "    ln.setAttribute('stroke', a.c); ln.setAttribute('stroke-width','2');"
                "    ln.setAttribute('stroke-linecap','round');"
                "    svg.appendChild(ln);"
                "    if(a.z > -0.2){"
                "      var tx = document.createElementNS(ns, 'text');"
                "      tx.setAttribute('x', cx + a.x * (L + 6));"
                "      tx.setAttribute('y', cy - a.y * (L + 6) + 3);"
                "      tx.setAttribute('fill', a.c);"
                "      tx.setAttribute('font-size','9');"
                "      tx.setAttribute('font-family','monospace');"
                "      tx.setAttribute('text-anchor','middle');"
                "      tx.textContent = a.n;"
                "      svg.appendChild(tx);"
                "    }"
                "  });"
                "  el.appendChild(svg);"
                "}"
                "render();"
                "new MutationObserver(render).observe(el, {attributes:true,attributeFilter:['data-axes']});"
                "})();"
            ]
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

    let exploreCard (env : Env<Message>) (model : AdaptiveModel) =
        let dragState : cval<(V2d * V2d) option> = cval None
        let defaultPos = V2d(200.0, 44.0)
        let pos =
            (model.ExploreCardPos, dragState :> aval<_>)
            ||> AVal.map2 (fun saved drag ->
                match drag with
                | Some (p, _) -> p
                | None -> saved |> Option.defaultValue defaultPos)
        let visible = model.Explore |> AVal.map (fun e -> e.Enabled)
        div {
            Class "card explore-card"
            (visible, pos) ||> AVal.map2 (fun on p ->
                if not on then Some (Style [Display "none"])
                else Some (Style [
                    Left (sprintf "%.0fpx" p.X)
                    Top (sprintf "%.0fpx" p.Y)
                ]))

            div {
                Class "card-titlebar"
                div {
                    Class "card-drag-handle"
                    Dom.OnPointerDown((fun e ->
                        if e.Button = Button.Left then
                            let cardPos = AVal.force pos
                            let grab = V2d(float e.ClientPosition.X, float e.ClientPosition.Y) - cardPos
                            transact (fun () -> dragState.Value <- Some (cardPos, grab))
                    ), pointerCapture = true)
                    Dom.OnPointerMove(fun e ->
                        match dragState.GetValue() with
                        | Some (_, grab) ->
                            let p = V2d(float e.ClientPosition.X, float e.ClientPosition.Y) - grab
                            transact (fun () -> dragState.Value <- Some (p, grab))
                        | None -> ())
                    Dom.OnPointerUp((fun _ ->
                        match dragState.GetValue() with
                        | Some (p, _) ->
                            transact (fun () -> dragState.Value <- None)
                            env.Emit [SetExploreCardPos p]
                        | None -> ()
                    ), pointerCapture = true)
                    "Explore"
                }
                button {
                    Class "card-btn-close"
                    Attribute("title", "Close (disable explore mode)")
                    Dom.OnClick(fun _ -> env.Emit [ExploreMsg (SetExploreEnabled false)])
                    "×"
                }
            }

            div {
                Class "card-body explore-card-body"
                let steep = model.Explore |> AVal.map (fun e -> e.SteepnessThreshold)
                let disag = model.Explore |> AVal.map (fun e -> e.DisagreementThreshold)
                inlineSlider "Steepness" 0.0 1.0 0.01 (sprintf "%.2f") steep (fun v ->
                    env.Emit [ExploreMsg (SetSteepnessThreshold v)])
                inlineLogSlider "Sensitivity" 0.001 10.0 (fun v ->
                    if v < 0.1 then sprintf "%.0f mm" (v * 1000.0)
                    else sprintf "%.2f m" v) disag (fun v ->
                    env.Emit [ExploreMsg (SetDisagreementThreshold v)])
            }
        }

