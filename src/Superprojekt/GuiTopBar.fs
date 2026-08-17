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
                inlineSlider "▤ Near cut" 0.0 1.25 0.01
                    (fun v -> if v <= 0.005 then "off" else sprintf "%.2f" v)
                    model.NearCutFrac (fun v -> env.Emit [SetNearCut v])
            }
            div {
                Class "tb-cut"
                Attribute("title", "Far cut: hide everything beyond the plane at this fraction of the distance to the orbit centre, a thick line marks the intersection; right end = off")
                inlineSlider "▤ Far cut" 0.05 2.5 0.01
                    (fun v -> if v >= 2.495 then "off" else sprintf "%.2f" v)
                    model.FarCutFrac (fun v -> env.Emit [SetFarCut v])
            }

            // Isolate pins: a view/render mode, so it lives with the other
            // render controls, not among the inspection instruments. The flag
            // stays per workflow level (LevelFlags) — the button reads and
            // toggles the CURRENT level's.
            button {
                Class "tb-btn"
                classWhen "tb-btn-active" ((model.Focus, model.AnchorGhostMode) ||> AVal.map2 LevelFlags.get)
                Attribute("title", "Isolate pins: show only the pin patches; off shows the full textured meshes. Remembered per workflow level.")
                Dom.OnClick(fun _ -> env.Emit [ToggleAnchorGhostMode])
                "◍ Isolate pins"
            }

            // Spring-loaded peek buttons: press-and-hold twins of the V/B keys
            // (the reducer re-guards, releases always land). Pointer capture
            // keeps the release landing even when the cursor slides off the
            // button mid-hold. DISABLED when a peek couldn't land — hidden
            // buttons are undiscoverable chrome; the ONE omission is V at
            // Matrix, where a REF/MOV flip has no meaning at all. Mirrors the
            // reducer's guards: V = the pair loaded + a pair-mesh isolate lock
            // (the peek swaps it), B = the pair loaded + registered, or the
            // whole graph at Matrix once an edge exists.
            let pairLoaded =
                AVal.custom (fun t ->
                    (match model.Focus.GetValue t with FocusPair | FocusPin -> true | FocusMatrix -> false) &&
                    (match (model.Sel.GetValue t).Pair with
                     | Some (a, b) ->
                        let loaded = model.MeshesLoaded.Content.GetValue t
                        HashSet.contains a loaded && HashSet.contains b loaded
                     | None -> false))
            let atMatrix = model.Focus |> AVal.map (fun f -> f = FocusMatrix)
            // Mirrors the reducer's guard: ANY effective isolation on a pair
            // mesh arms the flip — the committed lock or a transient (tile /
            // ◎-side hover, armed A/B pick).
            let canVis =
                AVal.custom (fun t ->
                    pairLoaded.GetValue t &&
                    (let eff, _ =
                        MeshVisibility.effectiveNarrowing (model.PinFocusHover.GetValue t)
                            (model.ArmedPick.GetValue t) (model.TileIsolateHover.GetValue t)
                            (model.TileIsolate.GetValue t) ((model.Sel.GetValue t).Point)
                     match eff, (model.Sel.GetValue t).Pair with
                     | Some m, Some (a, b) -> m = a || m = b
                     | _ -> false))
            let canPose =
                AVal.custom (fun t ->
                    let g = model.RegGraph.GetValue t
                    match model.Focus.GetValue t with
                    | FocusMatrix ->
                        let loaded = model.MeshesLoaded.Content.GetValue t
                        RegGraph.hasEdges g &&
                        g.Edges |> Map.forall (fun child e ->
                            HashSet.contains child loaded && HashSet.contains e.Parent loaded)
                    | FocusPair | FocusPin ->
                        pairLoaded.GetValue t &&
                        (match (model.Sel.GetValue t).Pair with
                         | Some (a, b) -> (RegGraph.pairEdge a b g).IsSome
                         | None -> false))
            let peekBtn (label : string) (title : string) (offHint : string)
                        (showA : aval<bool>) (canA : aval<bool>) (heldA : aval<bool>) (set : bool -> unit) =
                button {
                    Class "tb-btn-tiny tb-peek"
                    Primitives.showWhen showA
                    classWhen "tb-btn-active" heldA
                    canA |> AVal.map (fun ok ->
                        if ok then Some (Attribute("title", title))
                        else Some (Attribute("title", title + offHint)))
                    canA |> AVal.map (fun ok ->
                        if ok then None else Some (Attribute("disabled", "disabled")))
                    Dom.OnPointerDown((fun _ -> set true), pointerCapture = true)
                    Dom.OnPointerUp((fun _ -> set false), pointerCapture = true)
                    label
                }
            div {
                Class "tb-peeks"
                span { Class "lp-sublabel"; "Peek" }
                peekBtn "◌ V" "Hold: flip the isolation to the pair's other mesh — same spot, other epoch (same as holding V)"
                    " — available at the Pair/Pin level while a pair mesh is isolated (lock or hover)"
                    (atMatrix |> AVal.map not) canVis model.PeekVis (fun h -> env.Emit [SetPeekVis h])
                peekBtn "↺ B" "Hold: the registered meshes snap to their as-loaded poses — did registration help? (same as holding B); at Matrix the whole graph blinks at once"
                    " — available once the pair (at Matrix: the graph) is registered"
                    (AVal.constant true) canPose model.PeekPose (fun h -> env.Emit [SetPeekPose h])
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
                // The hidden mesh menu: reference-root designation + per-mesh
                // render toggles — out of the workflow rail by design.
                div {
                    Class "tb-gear-wrap"
                    button {
                        Class "tb-btn-tiny"
                        classWhen "tb-btn-active" model.MeshMenuOpen
                        Attribute("title", "Meshes: reference root + per-mesh rendering")
                        Dom.OnClick(fun _ -> env.Emit [ToggleMeshMenu])
                        "▦"
                    }
                    div {
                        Class "tb-gear-popover tb-mesh-popover"
                        showWhen model.MeshMenuOpen
                        div {
                            Class "tb-gear-row"
                            span { Class "lp-sublabel"; "Reference root · rendering" }
                        }
                        model.MeshNames |> AList.map (fun name ->
                            let idxVal = model.MeshOrder |> AMap.tryFind name |> AVal.map (Option.defaultValue 0)
                            let isRoot = model.RegGraph |> AVal.map (fun g -> g.Root = Some name)
                            let hm = model.MeshHeatmap |> AVal.map (fun m -> Map.tryFind name m |> Option.defaultValue HeatOff)
                            div {
                                Class "tb-gear-row tb-mesh-setup-row"
                                idxVal |> AVal.map (fun i -> Some (Attribute("title", sprintf "mesh %d" (i + 1))))
                                span { Class "pmx-sw"; (idxVal, isRoot) ||> AVal.map2 (fun i r -> Some (Style [Css.Background (c4bToRgbCss (meshColorRoot r i))])) }
                                span { Class "pmx-num"; idxVal |> AVal.map (fun i -> string (i + 1)) }
                                button {
                                    Class "tb-gear-btn tb-ref-btn"
                                    classWhen "setup-ref-on" isRoot
                                    Attribute("title", "Designate as the reference root. Re-rooting inside the registered tree keeps the registration; a mesh outside it clears the graph.")
                                    Dom.OnClick(fun _ -> env.Emit [SetRegRoot name])
                                    isRoot |> AVal.map (fun r -> if r then "★ Reference" else "☆ Set reference")
                                }
                                div {
                                    Class "rail-mesh-modes"
                                    Attribute("title", "Error visualization for this mesh: Textured · Distance · Shape · Incidence")
                                    compactButtonBar [
                                        "Tex",  (hm |> AVal.map ((=) HeatOff)),       (fun () -> env.Emit [SetMeshHeatmap(name, HeatOff)])
                                        "Dst",  (hm |> AVal.map ((=) HeatRange)),     (fun () -> env.Emit [SetMeshHeatmap(name, HeatRange)])
                                        "Shp",  (hm |> AVal.map ((=) HeatShape)),     (fun () -> env.Emit [SetMeshHeatmap(name, HeatShape)])
                                        "Inc",  (hm |> AVal.map ((=) HeatIncidence)), (fun () -> env.Emit [SetMeshHeatmap(name, HeatIncidence)])
                                    ]
                                }
                            })
                        let anyShapeOn =
                            model.MeshHeatmap |> AVal.map (Map.exists (fun _ h -> h = HeatShape))
                        div {
                            Class "tb-gear-row"
                            showWhen anyShapeOn
                            inlineSlider "Shape ≥" 0.0 1.0 0.01 (sprintf "%.2f") model.ShapeThreshold (fun v ->
                                env.Emit [SetShapeThreshold v])
                        }
                    }
                }
                div {
                    Class "tb-gear-wrap"
                    // Data-state checkpoints (browser localStorage): the view
                    // owns the storage IO — the reducer only ever receives the
                    // refreshed name list and a LOADED checkpoint's parsed
                    // data (ApplyCheckpoint; a dataset switch rides in front
                    // when the checkpoint belongs to another dataset).
                    let ckRefresh () =
                        let names =
                            try
                                (JSRuntime.Instance.Invoke<string>("spCkList") : string).Split('\n')
                                |> Array.filter (fun s -> s <> "")
                                |> Array.toList
                            with _ -> []
                        env.Emit [SetCheckpoints names]
                    let ckSave (name : string) =
                        match AVal.force model.ActiveDataset with
                        | None -> env.Emit [ShowToast "No dataset loaded — nothing to save"]
                        | Some ds ->
                            let pins =
                                model.ScanPins.Pins.Content |> AVal.force |> HashMap.toList
                                |> List.map snd
                                |> List.sortBy (fun p -> p.CreatedAt, p.ShortName)
                            let json = CheckpointStore.serialize ds (AVal.force model.RegGraph) pins
                            let ok = try JSRuntime.Instance.Invoke<bool>("spCkSave", name, json) with _ -> false
                            env.Emit [ShowToast (if ok then sprintf "Checkpoint '%s' saved" name
                                                 else "Checkpoint could not be stored")]
                            ckRefresh ()
                    let ckLoad (name : string) =
                        let json = try JSRuntime.Instance.Invoke<string>("spCkLoad", name) with _ -> ""
                        match CheckpointStore.tryDeserialize json with
                        | None -> env.Emit [ShowToast (sprintf "Checkpoint '%s' could not be read" name)]
                        | Some (ds, g, pins) ->
                            if AVal.force model.ActiveDataset <> Some ds then
                                env.Emit [SetActiveDataset ds]
                                ServerActions.loadDataset env ds
                            env.Emit [ApplyCheckpoint(name, ds, g, pins)]
                    let ckDelete (name : string) =
                        (try JSRuntime.Instance.Invoke<bool>("spCkDel", name) |> ignore with _ -> ())
                        env.Emit [ShowToast (sprintf "Checkpoint '%s' deleted" name)]
                        ckRefresh ()
                    button {
                        Class "tb-btn-tiny"
                        classWhen "tb-btn-active" model.GearPopoverOpen
                        Attribute("title", "Debug & settings")
                        Dom.OnClick(fun _ -> ckRefresh (); env.Emit [ToggleGearPopover])
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
                        // ── Checkpoints: the DATA state of one registration
                        // scenario (dataset + graph + pins) in localStorage.
                        div {
                            Class "tb-gear-row"
                            span { Class "lp-sublabel"; "Checkpoints" }
                            input {
                                Class "tb-ck-input"
                                Attribute("placeholder", "checkpoint name")
                                Dom.OnInput(fun e -> env.Emit [SetCheckpointName e.Value])
                            }
                            button {
                                Class "tb-gear-btn"
                                Attribute("title", "Save the current data state (dataset, registration graph, pins) under this name — an existing name is overwritten")
                                Dom.OnClick(fun _ ->
                                    let n = (AVal.force model.CheckpointName).Trim()
                                    if n = "" then env.Emit [ShowToast "Name the checkpoint first"]
                                    else ckSave n)
                                "Save"
                            }
                        }
                        div {
                            Class "tb-ck-rows"
                            let ckRows =
                                model.Checkpoints
                                |> AVal.map IndexList.ofList |> AList.ofAVal
                                |> AList.map (fun name ->
                                    div {
                                        Class "tb-gear-row tb-ck-row"
                                        span { Class "tb-ck-name"; Attribute("title", name); name }
                                        button {
                                            Class "tb-gear-btn"
                                            Attribute("title", "Load this checkpoint — replaces the current registration + pins (switches the dataset first when needed)")
                                            Dom.OnClick(fun _ -> ckLoad name)
                                            "Load"
                                        }
                                        button {
                                            Class "tb-gear-btn"
                                            Attribute("title", "Overwrite this checkpoint with the CURRENT data state")
                                            Dom.OnClick(fun _ -> ckSave name)
                                            "⟳"
                                        }
                                        button {
                                            Class "tb-gear-btn"
                                            Attribute("title", "Delete this checkpoint")
                                            Dom.OnClick(fun _ -> ckDelete name)
                                            "✕"
                                        }
                                    })
                            ckRows
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
                        gearSlider "Marker reveal radius (m)" 0.05 2.0 0.05 (sprintf "%.2f m") model.RevealRadius SetRevealRadius
                        gearSlider "Marker line weight" 0.5 3.0 0.1 (sprintf "%.1f×") model.MarkerWeight SetMarkerWeight
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
                        // ── Camera readout (sensor-centre reconstruction):
                        // live eye + orbit centre in absolute world (the
                        // *centroid.txt frame); a REGISTERED mesh adds the eye
                        // un-posed into its own file frame, where the two
                        // differ. Values gate on the popover flag, so a closed
                        // menu costs nothing per camera move.
                        let fmtP (p : V3d) = sprintf "%.4f %.4f %.4f" p.X p.Y p.Z
                        let scaleA = (model.ActiveDataset, model.DatasetScales) ||> AVal.map2 DatasetScale.active
                        let eyeWorldA =
                            (model.Camera.view, model.CommonCentroid, scaleA) |||> AVal.map3 (fun v common scale ->
                                ScanPin.worldCentre common scale v.Location)
                        let centreWorldA =
                            (model.Camera.center, model.CommonCentroid, scaleA) |||> AVal.map3 (fun c common scale ->
                                ScanPin.worldCentre common scale c)
                        let live (a : aval<V3d>) =
                            model.GearPopoverOpen |> AVal.bind (fun o ->
                                if o then a |> AVal.map fmtP else AVal.constant "")
                        let copyBtn (get : unit -> string) =
                            button {
                                Class "tb-gear-btn tb-cam-copy"
                                Attribute("title", "Copy these coordinates")
                                Dom.OnClick(fun _ -> (try JSRuntime.Instance.Invoke<bool>("spCopy", get ()) |> ignore with _ -> ()))
                                "⧉"
                            }
                        div {
                            Class "tb-gear-row"
                            span {
                                Class "lp-sublabel"
                                Attribute("title", "Live camera position in absolute world coordinates — the frame the *centroid.txt values live in. Park the eye where the scanner stood (Sensor ▾ jumps first-person; fully zoomed in, the eye sits ON the orbit centre) and copy.")
                                "Camera readout (world)"
                            }
                        }
                        div {
                            Class "tb-gear-row tb-cam-row"
                            span { Class "tb-cam-lab"; "Eye" }
                            span { Class "tb-gear-val tb-cam-val"; live eyeWorldA }
                            copyBtn (fun () -> fmtP (AVal.force eyeWorldA))
                        }
                        div {
                            Class "tb-gear-row tb-cam-row"
                            span { Class "tb-cam-lab"; "Orbit centre" }
                            span { Class "tb-gear-val tb-cam-val"; live centreWorldA }
                            copyBtn (fun () -> fmtP (AVal.force centreWorldA))
                        }
                        div {
                            model.MeshNames |> AList.map (fun name ->
                                // Stable per-row aval; reading the stable outer
                                // eyeWorldA via GetValue t is the sanctioned form.
                                let inFrameA =
                                    AVal.custom (fun t ->
                                        (MeshView.displayedWorldAt model t name).Backward.TransformPos (eyeWorldA.GetValue t))
                                let registered = model.ComposedPoses |> AVal.map (Map.containsKey name)
                                let lab =
                                    (model.MeshOrder |> AMap.tryFind name, model.MeshNames.Content) ||> AVal.map2 (fun o ns ->
                                        sprintf "Eye in %d %s" ((Option.defaultValue 0 o) + 1) (Primitives.friendlyName (IndexList.toList ns) name))
                                div {
                                    Class "tb-gear-row tb-cam-row"
                                    showWhen registered
                                    span { Class "tb-cam-lab"; lab }
                                    span { Class "tb-gear-val tb-cam-val"; live inFrameA }
                                    copyBtn (fun () -> fmtP (AVal.force inFrameA))
                                })
                        }
                    }
                }
                // The reaching-behaviour session log — the workshop's primary
                // data, tucked into a top-right button beside the debug menu.
                // The popover shows a tail; export downloads the whole log.
                div {
                    Class "tb-gear-wrap"
                    button {
                        Class "tb-btn-tiny"
                        classWhen "tb-btn-active" model.ReachLogOpen
                        Attribute("title", "Session log — every navigation action with the surface it came from")
                        Dom.OnClick(fun _ -> env.Emit [ToggleReachLog])
                        "≣"
                    }
                    div {
                        Class "tb-gear-popover tb-log-popover"
                        showWhen model.ReachLogOpen
                        div {
                            Class "tb-gear-row log-head"
                            span {
                                Class "lp-sublabel"
                                // Bind-gated: ReachLog is never trimmed, so the
                                // O(length) count must not run while closed.
                                model.ReachLogOpen |> AVal.bind (fun o ->
                                    if o then model.ReachLog |> AVal.map (fun l -> sprintf "Session log (%d)" (List.length l))
                                    else AVal.constant "Session log")
                            }
                            button {
                                Class "tb-gear-btn log-export"
                                Attribute("title", "Download the full session log as JSON")
                                Dom.OnClick(fun _ ->
                                    let esc (s : string) = s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                                    let json =
                                        AVal.force model.ReachLog
                                        |> List.rev
                                        |> List.map (fun ev ->
                                            sprintf "  {\"t\":\"%s\",\"source\":\"%s\",\"action\":\"%s\",\"subject\":\"%s\"}"
                                                (ev.At.ToString("o")) (esc ev.Source) (esc ev.Action) (esc ev.Subject))
                                        |> String.concat ",\n"
                                    try JSRuntime.Instance.Invoke<bool>("spDownloadText", "reach-log.json", "[\n" + json + "\n]") |> ignore
                                    with _ -> ())
                                "⤓ export"
                            }
                        }
                        let rows =
                            // Bind-gated: a CLOSED popover must cost zero per
                            // logged action (showWhen only hides via CSS — the
                            // 40-row rebuild would still run on every append).
                            model.ReachLogOpen
                            |> AVal.bind (fun o -> if o then model.ReachLog else AVal.constant [])
                            |> AVal.map (fun log ->
                                log |> List.truncate 40 |> List.map (fun ev ->
                                    div {
                                        Class "log-row"
                                        span { Class "log-time"; ev.At.ToLocalTime().ToString("HH:mm:ss") }
                                        span { Class "log-src"; ev.Source }
                                        span {
                                            Class "log-act"
                                            Attribute("title", ev.Action + (if ev.Subject = "" then "" else " — " + ev.Subject))
                                            ev.Action + (if ev.Subject = "" then "" else " · " + ev.Subject)
                                        }
                                    })
                                |> IndexList.ofList)
                            |> AList.ofAVal
                        div { Class "log-rows"; rows }
                    }
                }
            }
        }
