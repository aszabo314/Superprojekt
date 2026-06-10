namespace Superprojekt

open Aardvark.Base
open FSharp.Data.Adaptive
open Aardvark.Dom

module GuiCards =

    open Primitives

    let lassoCard (env : Env<Message>) (model : AdaptiveModel) =
        let dragState : cval<(V2d * V2d) option> = cval None
        let committed = model.LassoCardPos |> AVal.map (Option.defaultValue (V2d(340.0, 44.0)))
        let pos = Cards.cardPos committed dragState
        let drawing   = model.LassoDrawing |> AVal.map Option.isSome
        let committed = model.LassoVolume  |> AVal.map Option.isSome
        let enabled   = model.LassoEnabled
        let visible   = (drawing, committed) ||> AVal.map2 (||)
        // Show only when this button's predicate is true.
        let showWhen (a : aval<bool>) =
            a |> AVal.map (fun on -> if on then None else Some (Style [Display "none"]))
        let showWhenNot (a : aval<bool>) =
            a |> AVal.map (fun on -> if on then Some (Style [Display "none"]) else None)
        div {
            Class "card lasso-card"
            Cards.cardStyle visible pos
            div {
                Class "card-titlebar"
                Cards.cardDragHandle (AVal.constant "Lasso") pos dragState (fun p -> env.Emit [SetLassoCardPos p])
                button {
                    Class "card-btn-close"
                    Attribute("title", "Clear lasso")
                    Dom.OnClick(fun _ -> env.Emit [LassoClear])
                    "×"
                }
            }
            div {
                Class "card-body lasso-card-body"
                div {
                    Class "lp-clip-actions"
                    // ◉/○ — toggle filter on/off without clearing the polygon.
                    button {
                        Class "mb"
                        showWhen committed
                        enabled |> AVal.map (fun on ->
                            if on then Some (Class "mb-on") else None)
                        Attribute("title", "Enable / disable lasso filter (keeps the polygon)")
                        Dom.OnClick(fun _ -> env.Emit [ToggleLassoEnabled])
                        enabled |> AVal.map (fun on -> if on then "◉" else "○")
                    }
                    // ✎ — clear and start a new lasso.
                    button {
                        Class "mb"
                        showWhenNot drawing
                        Attribute("title", "Redraw (clear + start new)")
                        Dom.OnClick(fun _ -> env.Emit [LassoClear; LassoBegin])
                        "✎"
                    }
                    // ⊘ — cancel an in-progress drawing.
                    button {
                        Class "mb"
                        showWhen drawing
                        Attribute("title", "Cancel drawing")
                        Dom.OnClick(fun _ -> env.Emit [LassoCancel])
                        "⊘"
                    }
                    // ✕ — clear committed lasso.
                    button {
                        Class "mb"
                        showWhen committed
                        Attribute("title", "Clear")
                        Dom.OnClick(fun _ -> env.Emit [LassoClear])
                        "✕"
                    }
                }
            }
        }

    let registrationCard (env : Env<Message>) (model : AdaptiveModel) (openCval : cval<bool>) =
        let dragState : cval<(V2d * V2d) option> = cval None
        let committedPos = cval (V2d(200.0, 280.0))
        let pos = Cards.cardPos (committedPos :> aval<_>) dragState
        div {
            Class "card registration-card"
            Cards.cardStyle (openCval :> aval<_>) pos
            div {
                Class "card-titlebar"
                Cards.cardDragHandle (AVal.constant "Registration") pos dragState (fun p ->
                    transact (fun () -> committedPos.Value <- p))
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
                div { Class "lp-sublabel"; "Solve mode" }
                compactButtonBar [
                    "Traditional ICP",
                        mode |> AVal.map (fun m -> m = TraditionalIcp),
                        (fun () -> env.Emit [SetRegistrationMode TraditionalIcp])
                    "Region-restricted",
                        mode |> AVal.map (fun m -> m = RegionRestrictedIcp),
                        (fun () -> env.Emit [SetRegistrationMode RegionRestrictedIcp])
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
                        Attribute("disabled", "disabled")
                        Attribute("title", "Registration solve is not available yet (TODO)")
                        "▶ Run (todo)"
                    }
                    button {
                        Class "lp-discard"
                        Attribute("title", "Reset all mesh transforms to identity")
                        Dom.OnClick(fun _ -> env.Emit [ResetMeshTransforms])
                        "↺ Reset"
                    }
                }
            }
        }

    // Panorama panel. The card chrome is always mounted (hidden when closed);
    // the nested renderControl — which drives the expensive cube capture — is
    // mounted only while open, via an alist gated on PanoramaOpen, so it
    // allocates and renders nothing when the panel is hidden.
    let panoramaCard (env : Env<Message>) (model : AdaptiveModel) =
        let dragState : cval<(V2d * V2d) option> = cval None
        let committedPos = cval (V2d(360.0, 80.0))
        let pos = Cards.cardPos (committedPos :> aval<_>) dragState
        let mode = model.PanoramaMode

        // Selected panorama pose (world eye + yaw), matching PanoramaView's
        // fallback so markers and click-to-place agree with what is rendered.
        let poseW =
            (model.Panoramas, model.SelectedPanorama, model.SceneBounds)
            |||> AVal.map3 (fun ps i sb ->
                match List.tryItem i ps with
                | Some p -> p.EyeWorld, p.Yaw
                | None ->
                    let c = if sb.IsValid then sb.Center + V3d(0.0, 0.0, 2.0) else V3d.Zero
                    c, 0.0)

        // vScale must match PanoReproject's PanoVScale uniform (1.0).
        let vScale = 1.0

        let rc =
            renderControl {
                RenderControl.Samples 1
                Class "pano-render"
                let! info = RenderControl.Info
                let! size = RenderControl.ViewportSize
                Sg.View (AVal.constant Trafo3d.Identity)
                Sg.Proj (AVal.constant Trafo3d.Identity)
                // Click-to-place: in anchor-placement mode, turn the click into a
                // world ray through the cylindrical pose and raycast every
                // visible mesh server-side; the nearest hit becomes the anchor
                // (and its mesh the active layer / host).
                Dom.OnPointerDown(fun e ->
                    if e.Button = Button.Left then
                        match AVal.force model.ScanPins.Placement with
                        | AnchorPlacement ->
                            let sz = AVal.force size
                            if sz.X > 0 && sz.Y > 0 then
                                let off = e.OffsetPosition
                                let ndcX = float off.X / float sz.X * 2.0 - 1.0
                                let ndcY = (1.0 - float off.Y / float sz.Y) * 2.0 - 1.0
                                let eyeW, yaw = AVal.force poseW
                                let phi = yaw + ndcX * Constant.Pi
                                let dirW = V3d(cos phi, sin phi, ndcY * vScale).Normalized
                                let visible = AVal.force model.MeshVisible
                                let names =
                                    model.MeshNames |> AList.toAVal |> AVal.force |> IndexList.toList
                                    |> List.filter (fun n -> Map.tryFind n visible |> Option.defaultValue true)
                                if not (List.isEmpty names) then
                                    async {
                                        let! hits =
                                            names
                                            |> List.map (fun name ->
                                                async {
                                                    let! h = Query.rayHit ApiConfig.apiBase.Value name 0 eyeW dirW
                                                    return h |> Option.map (fun hit -> hit.t, hit.point, name)
                                                })
                                            |> Async.Parallel
                                        let best =
                                            hits |> Array.choose id
                                            |> Array.sortBy (fun (t, _, _) -> t)
                                            |> Array.tryHead
                                        match best with
                                        | Some (_, pt, name) ->
                                            env.Emit [SetActivePickingLayer (Some name); ScanPinMsg (PlaceAnchor pt)]
                                        | None -> ()
                                    } |> Async.Start
                        | _ -> ())
                PanoramaView.build info model
            }
        let rcMount =
            model.PanoramaOpen
            |> AVal.map (fun o -> if o then IndexList.single rc else IndexList.empty)
            |> AList.ofAVal

        // Committed pins projected into cylindrical panorama space as overlay
        // markers (percentage-positioned, so resolution-independent). Only pins
        // inside the panel's FOV are emitted.
        let markers =
            (model.ScanPins.Pins |> AMap.toAVal, poseW) ||> AVal.map2 (fun pins (eyeW, yaw) ->
                pins
                |> HashMap.toList
                |> List.choose (fun (_, pn) ->
                    if pn.Phase <> PinPhase.Committed then None
                    else
                        let dir = pn.Centre - eyeW
                        let horiz = sqrt (dir.X * dir.X + dir.Y * dir.Y)
                        if dir.Length < 1e-6 || horiz < 1e-9 then None
                        else
                            let phi = atan2 dir.Y dir.X
                            let a = atan2 (sin (phi - yaw)) (cos (phi - yaw))
                            let ndcX = a / Constant.Pi
                            let ndcY = (dir.Z / horiz) / vScale
                            if abs ndcX <= 1.0 && abs ndcY <= 1.0 then
                                Some ((ndcX * 0.5 + 0.5) * 100.0, (0.5 - ndcY * 0.5) * 100.0)
                            else None)
                |> IndexList.ofList)
            |> AList.ofAVal

        div {
            Class "card panorama-card"
            Cards.cardStyle model.PanoramaOpen pos
            div {
                Class "card-titlebar"
                Cards.cardDragHandle (AVal.constant "Panorama") pos dragState (fun p ->
                    transact (fun () -> committedPos.Value <- p))
                button {
                    Class "card-btn-close"
                    Attribute("title", "Close")
                    Dom.OnClick(fun _ -> env.Emit [TogglePanorama])
                    "×"
                }
            }
            div {
                Class "card-body panorama-card-body"
                div {
                    Class "pano-modebar"
                    compactButtonBar [
                        "Photo",  mode |> AVal.map (fun m -> m = PanoPhoto),
                            (fun () -> env.Emit [SetPanoramaMode PanoPhoto])
                        "Render", mode |> AVal.map (fun m -> m = PanoRender),
                            (fun () -> env.Emit [SetPanoramaMode PanoRender])
                        "Blend",  mode |> AVal.map (fun m -> m = PanoBlend),
                            (fun () -> env.Emit [SetPanoramaMode PanoBlend])
                    ]
                }
                div {
                    Class "pano-blend-row"
                    mode |> AVal.map (fun m -> if m = PanoBlend then None else Some (Style [Display "none"]))
                    inlineSlider "Blend" 0.0 1.0 0.01 (sprintf "%.2f") model.PanoramaBlend (fun v ->
                        env.Emit [SetPanoramaBlend v])
                }
                div {
                    Class "pano-view-wrap"
                    rcMount
                    markers |> AList.map (fun (l, t) ->
                        div {
                            Class "pano-marker"
                            Style [ Left (sprintf "%.2f%%" l); Top (sprintf "%.2f%%" t) ]
                        })
                }
                div {
                    Class "pano-actions"
                    button {
                        Class "mb"
                        Attribute("title", "Fly the 3D camera to this viewpoint")
                        Dom.OnClick(fun _ -> env.Emit [FlyToPanorama (AVal.force model.SelectedPanorama)])
                        "✈ Fly to pose"
                    }
                }
            }
        }

    let registrationToggleButton (openCval : cval<bool>) =
        button {
            Class "tb-btn"
            Attribute("title", "Registration solver")
            Dom.OnClick(fun _ ->
                transact (fun () -> openCval.Value <- not openCval.Value))
            "⚙ Registration"
        }

    // Retarget review card. Shown while RetargetState is RetargetProjecting
    // (waiting on server) or RetargetReviewing (user picks accept/reject per
    // pin). Hidden on RetargetIdle.
    let retargetCard (env : Env<Message>) (model : AdaptiveModel) =
        let candidatesAList =
            model.Retarget
            |> AVal.map (function
                | RetargetReviewing cs -> IndexList.ofArray cs
                | _ -> IndexList.empty)
            |> AList.ofAVal
        let title =
            model.Retarget |> AVal.map (function
                | RetargetProjecting target ->
                    sprintf "Retarget to %s — projecting…" (Cards.shortName target)
                | RetargetReviewing cs ->
                    sprintf "Retarget review (%d pins)" cs.Length
                | _ -> "Retarget")
        let progressNote =
            model.Retarget |> AVal.map (function
                | RetargetProjecting _ -> "Projecting pins onto target mesh…"
                | _ -> "")
        div {
            Class "card retarget-card"
            model.Retarget |> AVal.map (function
                | RetargetIdle -> Some (Style [Display "none"])
                | _ -> None)
            div {
                Class "card-titlebar"
                span { Class "card-title"; title }
                button {
                    Class "card-btn-close"
                    Attribute("title", "Cancel retarget")
                    Dom.OnClick(fun _ -> env.Emit [CancelRetarget])
                    "×"
                }
            }
            div {
                Class "card-body retarget-card-body"
                div { Class "retarget-empty"; progressNote }
                div {
                    Class "retarget-list"
                    candidatesAList |> AList.map (fun c ->
                        let infinite = System.Double.IsInfinity c.ProjectionDistance
                        let highRisk = infinite || c.ProjectionDistance > 2.0 * c.FalloffRadius
                        let distLabel =
                            if infinite then "no projection"
                            else sprintf "Δ %.3fm" c.ProjectionDistance
                        let baseClass =
                            "retarget-row" +
                            (if highRisk then " retarget-row-risk" else "") +
                            (match c.Decision with
                             | RetargetAccept -> " retarget-row-accepted"
                             | RetargetReject -> " retarget-row-rejected"
                             | _ -> "")
                        div {
                            Class baseClass
                            span {
                                Class "retarget-pin"
                                c.OriginalHostMesh |> Option.defaultValue "—" |> Cards.shortName
                            }
                            span { Class "retarget-dist"; distLabel }
                            div {
                                Class "retarget-actions"
                                button {
                                    Class "retarget-btn-accept"
                                    Attribute("title", "Accept projection")
                                    Dom.OnClick(fun _ -> env.Emit [SetRetargetDecision(c.PinId, RetargetAccept)])
                                    "✓"
                                }
                                button {
                                    Class "retarget-btn-reject"
                                    Attribute("title", "Reject (keep current position)")
                                    Dom.OnClick(fun _ -> env.Emit [SetRetargetDecision(c.PinId, RetargetReject)])
                                    "✕"
                                }
                            }
                        })
                }
                div {
                    Class "retarget-commit-row"
                    model.Retarget |> AVal.map (function
                        | RetargetReviewing _ -> None
                        | _ -> Some (Style [Display "none"]))
                    button {
                        Class "lp-commit"
                        Attribute("title", "Apply accepted projections")
                        Dom.OnClick(fun _ -> env.Emit [CommitRetarget])
                        "Apply"
                    }
                    button {
                        Class "lp-discard"
                        Dom.OnClick(fun _ -> env.Emit [CancelRetarget])
                        "Cancel"
                    }
                }
            }
        }
