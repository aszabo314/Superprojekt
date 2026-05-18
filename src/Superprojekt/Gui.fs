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

            let placementActive =
                model.ScanPins.Placement |> AVal.map (function
                    | AnchorPlacement -> true
                    | _ -> false)

            button {
                Class "tb-btn"
                placementActive |> AVal.map (fun on -> if on then Some (Class "tb-btn-active") else None)
                Attribute("title", "Place anchor \u2014 click on a surface (Esc cancels)")
                Dom.OnClick(fun _ ->
                    let active = AVal.force placementActive
                    if active then env.Emit [ScanPinMsg CancelPlacement]
                    // Mutual-exclusion: kicking off pin placement clears any
                    // in-progress lasso (\u00A7D.3 / \u00A7D.6 share the cursor).
                    else env.Emit [LassoCancel; ScanPinMsg EnterAnchorPlacement])
                "\u25CB Pin"
            }

            // V6 \u00A7D.10 \u2014 Fusion mode toggle. When on, the composition pass
            // picks per-pixel the lowest-total-error mesh from the visible
            // set instead of the front-most one.
            button {
                Class "tb-btn"
                model.FusionMode |> AVal.map (fun on -> if on then Some (Class "tb-btn-active") else None)
                Attribute("title", "Fusion mesh: per-pixel best mesh from the registered ensemble (V6 \u00A7D.10)")
                Dom.OnClick(fun _ -> env.Emit [ToggleFusionMode])
                "\u25C8 Fusion"
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
            // V6 §D.2 — ghost detail selector, hidden when silhouette is off.
            div {
                Class "lp-sub lp-ghost-detail"
                model.GhostSilhouette |> AVal.map (fun on ->
                    if on then None else Some (Style [Display "none"]))
                let detail = model.GhostDetail
                compactButtonBar [
                    "Outline",
                        detail |> AVal.map (fun d -> d = OutlineOnly),
                        (fun () -> env.Emit [SetGhostDetail OutlineOnly])
                    "+ Curvature",
                        detail |> AVal.map (fun d -> d = PlusCurvature),
                        (fun () -> env.Emit [SetGhostDetail PlusCurvature])
                    "+ Terrain",
                        detail |> AVal.map (fun d -> d = PlusTerrainFeatures),
                        (fun () -> env.Emit [SetGhostDetail PlusTerrainFeatures])
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
        let adjusting =
            sp.Placement |> AVal.map (function AdjustingPin _ -> true | _ -> false)
        let flyoutClass =
            (adjusting, model.MenuOpen) ||> AVal.map2 (fun adj open_ ->
                if not adj then "placement-flyout hidden"
                elif open_ then "placement-flyout pf-left-open"
                else "placement-flyout pf-left-closed")
        div {
            flyoutClass |> AVal.map (fun c -> Some (Class c))
            div { Class "lp-section-title"; "Adjust Anchor" }

            let radius = activePin |> AVal.map (Option.map (fun p -> p.Radius) >> Option.defaultValue 1.0)
            let sigma = activePin |> AVal.map (Option.map (fun p -> p.Sigma) >> Option.defaultValue 0.5)
            // SetAnchorSigma clamps to \u2264 Radius in the handler (\u00a7D.6.4).
            inlineSlider "Radius" 0.05 50.0 0.05 (sprintf "%.2fm") radius (fun v ->
                env.Emit [ScanPinMsg (SetAnchorRadius v)])
            inlineSlider "\u03c3 (sigma)" 0.01 50.0 0.01 (sprintf "%.2fm") sigma (fun v ->
                env.Emit [ScanPinMsg (SetAnchorSigma v)])

            // V6 \u00a7D.6.4 \u2014 payload-type selector. Switching destroys the
            // current payload and instantiates the new kind with defaults.
            let payloadKind =
                activePin |> AVal.map (function
                    | Some p -> PayloadType.kind p.Payload
                    | None -> PointKind)
            let pinId = activePlacementId
            div { Class "lp-sublabel"; "Payload" }
            compactButtonBar [
                "Point",
                    payloadKind |> AVal.map ((=) PointKind),
                    (fun () ->
                        match AVal.force pinId with
                        | Some id -> env.Emit [ScanPinMsg (ChangePayloadType(id, PointKind))]
                        | None -> ())
                "Line",
                    payloadKind |> AVal.map ((=) LineKind),
                    (fun () ->
                        match AVal.force pinId with
                        | Some id -> env.Emit [ScanPinMsg (ChangePayloadType(id, LineKind))]
                        | None -> ())
                "Patch",
                    payloadKind |> AVal.map ((=) PatchKind),
                    (fun () ->
                        match AVal.force pinId with
                        | Some id -> env.Emit [ScanPinMsg (ChangePayloadType(id, PatchKind))]
                        | None -> ())
            ]

            // \u00a7D.7.1 \u2014 Reliability weight slider, shown only when the
            // active pin currently has a Point payload.
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
                isPoint |> AVal.map (fun v ->
                    if v then None else Some (Style [Display "none"]))
                inlineSlider "Reliability" 0.0 1.0 0.01 (sprintf "%.2f") reliability (fun v ->
                    match AVal.force pinId with
                    | Some id -> env.Emit [ScanPinMsg (SetReliabilityWeight(id, v))]
                    | None -> ())
            }

            // §D.7.2 — Line-payload sub-mode toggle + elevation slider.
            // Visible only when the active pin currently has a Line payload.
            // CurvatureRidge is greyed out in 4b (lands in 4c).
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
                isLine |> AVal.map (fun v ->
                    if v then None else Some (Style [Display "none"]))
                div { Class "lp-sublabel"; "Line mode" }
                compactButtonBar [
                    "Elevation",
                        isIsoline,
                        (fun () ->
                            match AVal.force pinId, AVal.force centreZ with
                            | Some id, z -> env.Emit [ScanPinMsg (SetLineMode(id, ElevationIsoline z))]
                            | _ -> ())
                    "Ridge",
                        isIsoline |> AVal.map not,
                        (fun () ->
                            match AVal.force pinId with
                            | Some id -> env.Emit [ScanPinMsg (SetLineMode(id, CurvatureRidge))]
                            | None -> ())
                ]
                div {
                    Class "lp-isoline-row"
                    isIsoline |> AVal.map (fun v ->
                        if v then None else Some (Style [Display "none"]))
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
                                    let a = p.Centre
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

                // V6 §D.9 — per-mesh dataset-error overrides + sensor
                // selection. Defaults come from Provenance.defaultDatasetError;
                // the override slider clears back to the default with the
                // ↺ button.
                collapsibleSection "Error metadata" false (
                    div {
                        Class "lp-err-meta"
                        model.MeshNames |> AList.map (fun name ->
                            let sensors = model.MeshSensorTypes
                            let overrides = model.MeshDatasetErrors
                            let sensor =
                                sensors |> AVal.map (fun m ->
                                    Map.tryFind name m |> Option.defaultValue UnknownSensor)
                            let userValue =
                                overrides |> AVal.map (fun m -> Map.tryFind name m)
                            let displayed =
                                (sensor, userValue) ||> AVal.map2 (fun s ov ->
                                    ov |> Option.defaultValue (Provenance.defaultDatasetError s))
                            div {
                                Class "lp-err-mesh-row"
                                div { Class "lp-err-mesh-name"; Cards.shortName name }
                                compactButtonBar [
                                    "Rover",
                                        sensor |> AVal.map ((=) RoverStereo),
                                        (fun () -> env.Emit [SetMeshSensorType(name, RoverStereo)])
                                    "Sat",
                                        sensor |> AVal.map ((=) Satellite),
                                        (fun () -> env.Emit [SetMeshSensorType(name, Satellite)])
                                    "Photo",
                                        sensor |> AVal.map ((=) Photogrammetry),
                                        (fun () -> env.Emit [SetMeshSensorType(name, Photogrammetry)])
                                    "LiDAR",
                                        sensor |> AVal.map ((=) LiDAR),
                                        (fun () -> env.Emit [SetMeshSensorType(name, LiDAR)])
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

                // V6 §D.9 — global error-provenance heatmap toggle.
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
                        div { Class "lp-sublabel-hint"; "Red = dataset, green = algorithm, blue = conditioning." }
                    })

                // V6 §D.3 lasso. Sits alongside the rectangular box clip;
                // both can be active simultaneously and the per-fragment
                // discard enforces their intersection.
                collapsibleSection "Lasso" false (
                    div {
                        Class "lp-clip-body"
                        let drawing = model.LassoDrawing |> AVal.map Option.isSome
                        let committed = model.LassoVolume |> AVal.map Option.isSome
                        div {
                            Class "lp-clip-actions"
                            button {
                                Class "mb"
                                drawing |> AVal.map (fun on ->
                                    if on then Some (Class "mb-on") else None)
                                Attribute("title", "Click vertices on the viewport; double-click to commit; Esc cancels.")
                                Dom.OnClick(fun _ ->
                                    if AVal.force drawing then env.Emit [LassoCancel]
                                    else env.Emit [LassoBegin])
                                drawing |> AVal.map (fun on ->
                                    if on then "Drawing…" else "Draw Lasso")
                            }
                            button {
                                Class "mb"
                                committed |> AVal.map (fun c ->
                                    if c then None else Some (Style [Display "none"]))
                                Attribute("title", "Clear committed lasso")
                                Dom.OnClick(fun _ -> env.Emit [LassoClear])
                                "Clear"
                            }
                        }
                        div {
                            Class "lp-sublabel-hint"
                            (drawing, committed) ||> AVal.map2 (fun d c ->
                                match d, c with
                                | true, _  -> "Click to add vertex · double-click to commit · Esc to cancel"
                                | _, true  -> "Lasso committed. Camera-anchored cone."
                                | _ -> "")
                        }
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

    /// V6 §D.1 — small floating label next to the cursor indicating which
    /// mesh is currently the active picking layer. Hidden until the user
    /// has cycled at least once and the cursor is over the viewport.
    let meshWheelLabel (model : AdaptiveModel) (cursorScreen : aval<V2d option>) =
        div {
            Class "mesh-wheel-label"
            (model.ActivePickingLayer, cursorScreen) ||> AVal.map2 (fun layer cOpt ->
                match layer, cOpt with
                | Some _, Some pos ->
                    Some (Style [
                        Left (sprintf "%.0fpx" (pos.X + 14.0))
                        Top  (sprintf "%.0fpx" (pos.Y - 10.0))
                    ])
                | _ -> Some (Style [Display "none"]))
            model.ActivePickingLayer |> AVal.map (function
                | Some name -> Cards.shortName name
                | None -> "")
        }

    /// V6 §D.3 — in-progress lasso overlay (SVG polyline rendered above the
    /// renderControl). Shows the committed-so-far polygon plus a dashed
    /// preview segment from the last vertex to the cursor.
    let lassoOverlay (env : Env<Message>) (model : AdaptiveModel) (cursorScreen : aval<V2d option>) =
        let stateJson =
            (model.LassoDrawing, cursorScreen, model.LassoVolume) |||> AVal.map3 (fun drawing cursor committed ->
                let drawingArr =
                    match drawing with
                    | Some d -> d.Vertices
                    | None -> [||]
                let cursorArr =
                    match cursor with
                    | Some c -> [| c |]
                    | None -> [||]
                let committedArr =
                    match committed with
                    | Some v -> v.ScreenPolygon
                    | None -> [||]
                let fmtArr (a : V2d[]) =
                    a |> Array.map (fun p -> sprintf "[%.1f,%.1f]" p.X p.Y) |> String.concat ","
                sprintf "{\"d\":[%s],\"c\":[%s],\"k\":[%s]}"
                    (fmtArr drawingArr) (fmtArr cursorArr) (fmtArr committedArr))
        div {
            Class "lasso-overlay"
            stateJson |> AVal.map (fun j -> Some (Attribute("data-lasso", j)))
            OnBoot [
                "(function(){"
                "var el = __THIS__;"
                "var last = '';"
                "var ns = 'http://www.w3.org/2000/svg';"
                "function poly(points, attrs){"
                "  var p = document.createElementNS(ns, 'polyline');"
                "  p.setAttribute('points', points.map(function(pt){return pt[0]+','+pt[1];}).join(' '));"
                "  for(var k in attrs) p.setAttribute(k, attrs[k]);"
                "  return p;"
                "}"
                "function render(){"
                "  var raw = el.getAttribute('data-lasso') || '{}';"
                "  if(raw === last) return; last = raw;"
                "  try { var d = JSON.parse(raw); } catch(e) { return; }"
                "  el.innerHTML = '';"
                "  var svg = document.createElementNS(ns, 'svg');"
                "  svg.setAttribute('class','lasso-svg');"
                "  var rect = el.getBoundingClientRect();"
                "  svg.setAttribute('width', rect.width);"
                "  svg.setAttribute('height', rect.height);"
                "  el.appendChild(svg);"
                "  if(d.k && d.k.length >= 3){"
                "    var k = d.k.slice(); k.push(k[0]);"
                "    svg.appendChild(poly(k, {stroke:'#1a56db','stroke-width':'1.5','stroke-dasharray':'4,3',fill:'rgba(26,86,219,0.04)'}));"
                "  }"
                "  if(d.d && d.d.length > 0){"
                "    if(d.d.length >= 2)"
                "      svg.appendChild(poly(d.d, {stroke:'#0f172a','stroke-width':'1.5',fill:'none'}));"
                "    d.d.forEach(function(pt){"
                "      var c = document.createElementNS(ns, 'circle');"
                "      c.setAttribute('cx', pt[0]); c.setAttribute('cy', pt[1]);"
                "      c.setAttribute('r','3'); c.setAttribute('fill','#0f172a');"
                "      svg.appendChild(c);"
                "    });"
                "    if(d.c && d.c.length > 0){"
                "      var last = d.d[d.d.length-1];"
                "      var line = document.createElementNS(ns, 'line');"
                "      line.setAttribute('x1', last[0]); line.setAttribute('y1', last[1]);"
                "      line.setAttribute('x2', d.c[0][0]); line.setAttribute('y2', d.c[0][1]);"
                "      line.setAttribute('stroke','#0f172a'); line.setAttribute('stroke-width','1');"
                "      line.setAttribute('stroke-dasharray','4,4');"
                "      svg.appendChild(line);"
                "    }"
                "  }"
                "}"
                "render();"
                "new MutationObserver(render).observe(el,{attributes:true,attributeFilter:['data-lasso']});"
                "})();"
            ]
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

                // V6 §D.4 — two independently-toggled signals.
                let fcEnabled = model.Explore |> AVal.map (fun e -> e.FeatureConfidence.Enabled)
                let fcThresh  = model.Explore |> AVal.map (fun e -> e.FeatureConfidence.Threshold)
                let dgEnabled = model.Explore |> AVal.map (fun e -> e.Disagreement.Enabled)
                let dgThresh  = model.Explore |> AVal.map (fun e -> e.Disagreement.Threshold)
                let bothOn    = (fcEnabled, dgEnabled) ||> AVal.map2 (&&)
                let mix       = model.Explore |> AVal.map (fun e -> e.MixMode)

                div {
                    Class "explore-signal-row"
                    compactToggle "Feature confidence" fcEnabled (fun () ->
                        let on = AVal.force fcEnabled
                        env.Emit [ExploreMsg (SetSignalEnabled(FeatureConfidenceSignal, not on))])
                    div {
                        Class "explore-signal-controls"
                        fcEnabled |> AVal.map (fun on ->
                            if on then None else Some (Style [Display "none"]))
                        inlineSlider "Sensitivity" 0.0 1.0 0.01 (sprintf "%.2f") fcThresh (fun v ->
                            env.Emit [ExploreMsg (SetSignalThreshold(FeatureConfidenceSignal, v))])
                    }
                }

                div {
                    Class "explore-signal-row"
                    compactToggle "Disagreement" dgEnabled (fun () ->
                        let on = AVal.force dgEnabled
                        env.Emit [ExploreMsg (SetSignalEnabled(DisagreementSignal, not on))])
                    div {
                        Class "explore-signal-controls"
                        dgEnabled |> AVal.map (fun on ->
                            if on then None else Some (Style [Display "none"]))
                        inlineLogSlider "Sensitivity" 0.001 10.0 (fun v ->
                            if v < 0.1 then sprintf "%.0f mm" (v * 1000.0)
                            else sprintf "%.2f m" v) dgThresh (fun v ->
                            env.Emit [ExploreMsg (SetSignalThreshold(DisagreementSignal, v))])
                    }
                }

                div {
                    Class "explore-mix-row"
                    bothOn |> AVal.map (fun on ->
                        if on then None else Some (Style [Display "none"]))
                    span { Class "lp-sublabel"; "Mix" }
                    compactButtonBar [
                        "Blended",
                            mix |> AVal.map (fun m -> m = Blended),
                            (fun () -> env.Emit [ExploreMsg (SetMixMode Blended)])
                        "Side-by-side",
                            mix |> AVal.map (fun m -> m = SideBySide),
                            (fun () -> env.Emit [ExploreMsg (SetMixMode SideBySide)])
                        "Alternating",
                            mix |> AVal.map (fun m -> m = Alternating),
                            (fun () -> env.Emit [ExploreMsg (SetMixMode Alternating)])
                    ]
                }
            }
        }

    /// V6 §D.8 — Registration solver panel. Floating draggable card.
    /// `openCval` is the shared open/close state (toggled by the
    /// top-bar button). Solve modes: Traditional ICP (full mesh),
    /// Region-restricted ICP (anchor-Gaussian weights), Point-pair +
    /// refinement (greyed for now — needs correspondence-linking UI).
    let registrationCard (env : Env<Message>) (model : AdaptiveModel) (openCval : cval<bool>) =
        let dragState : cval<(V2d * V2d) option> = cval None
        let defaultPos = V2d(200.0, 280.0)
        let pos =
            dragState :> aval<_> |> AVal.map (function
                | Some (p, _) -> p
                | None -> defaultPos)
        div {
            Class "card registration-card"
            (openCval :> aval<_>, pos) ||> AVal.map2 (fun open_ p ->
                if not open_ then Some (Style [Display "none"])
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
                        | Some (p, _) -> transact (fun () -> dragState.Value <- Some (p, V2d.Zero))
                        | None -> ()
                    ), pointerCapture = true)
                    "Registration"
                }
                button {
                    Class "card-btn-close"
                    Attribute("title", "Close")
                    Dom.OnClick(fun _ -> transact (fun () -> openCval.Value <- false))
                    "×"
                }
            }

            div {
                Class "card-body registration-card-body"

                let mode       = model.Registration |> AVal.map (fun r -> r.Mode)
                let refMeshOpt = model.Registration |> AVal.map (fun r -> r.ReferenceMesh)
                let running    = model.Registration |> AVal.map (fun r -> r.Running)
                let conv       = model.Registration |> AVal.map (fun r -> r.ConvergenceLog)
                let resi       = model.Registration |> AVal.map (fun r -> r.LastResiduals)

                div { Class "lp-sublabel"; "Solve mode" }
                compactButtonBar [
                    "Traditional ICP",
                        mode |> AVal.map (fun m -> m = TraditionalIcp),
                        (fun () -> env.Emit [SetRegistrationMode TraditionalIcp])
                    "Region-restricted",
                        mode |> AVal.map (fun m -> m = RegionRestrictedIcp),
                        (fun () -> env.Emit [SetRegistrationMode RegionRestrictedIcp])
                    "Point-pair",
                        mode |> AVal.map (fun m -> m = PointPairPlusRefinement),
                        // Greyed (no-op) until anchor correspondence linking lands.
                        (fun () -> ())
                ]

                div { Class "lp-sublabel"; "Reference mesh" }
                div {
                    Class "lp-mesh-list"
                    model.MeshNames |> AList.map (fun n ->
                        let isRef = refMeshOpt |> AVal.map ((=) (Some n))
                        button {
                            Class "lp-mesh-btn"
                            isRef |> AVal.map (fun on ->
                                if on then Some (Class "cbb-btn-active") else None)
                            Dom.OnClick(fun _ ->
                                let cur = AVal.force refMeshOpt
                                env.Emit [SetReferenceMesh (if cur = Some n then None else Some n)])
                            Cards.shortName n
                        })
                }

                div {
                    Class "lp-commit-row"
                    button {
                        Class "lp-commit"
                        Dom.OnClick(fun _ -> env.Emit [RunRegistration])
                        running |> AVal.map (fun r ->
                            if r then Some (Attribute("disabled", "disabled")) else None)
                        running |> AVal.map (fun r -> if r then "Solving…" else "▶ Run")
                    }
                    button {
                        Class "lp-discard"
                        Attribute("title", "Reset all mesh transforms to identity")
                        Dom.OnClick(fun _ -> env.Emit [ResetMeshTransforms])
                        "↺ Reset"
                    }
                }

                // Final residuals — RMS readout + histogram bar chart.
                div { Class "lp-sublabel"; "Residuals" }
                div {
                    Class "reg-residual-stats"
                    resi |> AVal.map (fun (r : float[]) ->
                        if r.Length = 0 then "No solve yet"
                        else
                            let n = r.Length
                            let mean = (r |> Array.sum) / float n
                            let var = (r |> Array.sumBy (fun x -> (x - mean) ** 2.0)) / float n
                            let rms = sqrt ((r |> Array.sumBy (fun x -> x * x)) / float n)
                            sprintf "n=%d • mean %.3fm • RMS %.3fm • σ %.3f" n mean rms (sqrt var))
                }
                div {
                    Class "reg-residual-histogram"
                    resi |> AVal.map (fun (r : float[]) ->
                        if r.Length < 2 then Some (Attribute("data-hist", "{}"))
                        else
                            let bins = 20
                            let lo = Array.min r
                            let hi = Array.max r
                            let span = max 1e-6 (hi - lo)
                            let counts = Array.zeroCreate<int> bins
                            for v in r do
                                let bi = min (bins - 1) (max 0 (int ((v - lo) / span * float bins)))
                                counts.[bi] <- counts.[bi] + 1
                            let maxCount = Array.max counts |> max 1
                            let bars =
                                counts
                                |> Array.mapi (fun i c -> sprintf "[%d,%d]" i c)
                                |> String.concat ","
                            Some (Attribute("data-hist", sprintf "{\"max\":%d,\"bins\":%d,\"lo\":%.4f,\"hi\":%.4f,\"counts\":[%s]}" maxCount bins lo hi bars)))
                    OnBoot [
                        "(function(){"
                        "var el = __THIS__;"
                        "var last = '';"
                        "function render(){"
                        "  var raw = el.getAttribute('data-hist') || '{}';"
                        "  if(raw === last) return; last = raw;"
                        "  try { var d = JSON.parse(raw); } catch(e) { return; }"
                        "  el.innerHTML = '';"
                        "  if(!d.counts || d.counts.length === 0){ el.textContent = '—'; return; }"
                        "  var w = 240, h = 50;"
                        "  var ns = 'http://www.w3.org/2000/svg';"
                        "  var svg = document.createElementNS(ns,'svg');"
                        "  svg.setAttribute('width', w); svg.setAttribute('height', h);"
                        "  var bw = w / d.bins;"
                        "  d.counts.forEach(function(b){"
                        "    var r = document.createElementNS(ns,'rect');"
                        "    var bh = (b[1] / d.max) * (h - 8);"
                        "    r.setAttribute('x', b[0] * bw);"
                        "    r.setAttribute('y', h - bh);"
                        "    r.setAttribute('width', bw - 1);"
                        "    r.setAttribute('height', bh);"
                        "    r.setAttribute('fill', '#1a56db');"
                        "    svg.appendChild(r);"
                        "  });"
                        "  el.appendChild(svg);"
                        "}"
                        "render();"
                        "new MutationObserver(render).observe(el,{attributes:true,attributeFilter:['data-hist']});"
                        "})();"
                    ]
                }

                // Convergence log — one line per iteration.
                div { Class "lp-sublabel"; "Convergence" }
                div {
                    Class "reg-convergence-log"
                    conv |> AVal.map (fun (iters : RegistrationIteration[]) ->
                        if iters.Length = 0 then "—"
                        else
                            iters
                            |> Array.map (fun it -> sprintf "  iter %2d  RMS %.4fm" it.Iter it.Rms)
                            |> String.concat "\n")
                }
            }
        }

    /// V6 §D.8 — small top-bar toggle that opens the registration card.
    let registrationToggleButton (openCval : cval<bool>) =
        button {
            Class "tb-btn"
            Attribute("title", "Registration solver (V6 §D.8)")
            Dom.OnClick(fun _ ->
                transact (fun () -> openCval.Value <- not openCval.Value))
            "⚙ Registration"
        }

