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

    // Unicode sparkline of an ICP convergence series (print-appropriate, no
    // extra JS / GPU resources).
    let private spark (xs : float[]) =
        if xs.Length < 2 then ""
        else
            let xs = if xs.Length > 24 then xs.[xs.Length - 24 ..] else xs
            let mn = Array.min xs
            let mx = Array.max xs
            let blocks = [| '▁'; '▂'; '▃'; '▄'; '▅'; '▆'; '▇'; '█' |]
            xs
            |> Array.map (fun v ->
                let t = if mx - mn < 1e-12 then 0.0 else (v - mn) / (mx - mn)
                blocks.[min 7 (int (t * 7.999))])
            |> System.String

    let registrationCard (env : Env<Message>) (model : AdaptiveModel) =
        let dragState : cval<(V2d * V2d) option> = cval None
        let committedPos = cval (V2d(200.0, 180.0))
        let pos = Cards.cardPos (committedPos :> aval<_>) dragState
        let fineWarnDismissed = cval false

        let pinsVal = model.ScanPins.Pins |> AMap.toAVal
        let refMeshOpt = model.Registration |> AVal.map (fun r -> r.ReferenceMesh)
        let running = model.Registration |> AVal.map (fun r -> r.Running)
        let mode = model.Registration |> AVal.map (fun r -> r.Mode)

        // Shared readiness engine (workflow panel §2): the card renders its
        // readiness line / badge / pin rows from the same input + diagnostics
        // the panel uses — single source of truth.
        let readiness = ReadinessView.input model

        let canSolveCoarse =
            readiness |> AVal.map (fun i ->
                (Readiness.compute i).Coarse |> List.exists (fun d -> d.Severity = Severity.Ready))
        let coarseTooltip =
            (readiness, running) ||> AVal.map2 (fun i busy ->
                if busy then "Solving…"
                elif i.ReferenceMesh.IsNone then "Designate a reference mesh (★) first"
                elif not (Readiness.pairCounts i |> List.exists (fun (_, n) -> n >= 3)) then
                    "Needs ≥3 accepted anchor pairs on at least one visible moving mesh"
                else "Solve landmark alignment for every visible moving mesh with ≥3 accepted pairs")

        let pendingResults =
            model.PendingReg |> AVal.map (function
                | Some pr -> pr.Results |> Map.toList |> IndexList.ofList
                | None -> IndexList.empty)
            |> AList.ofAVal
        let isPreview = model.PendingReg |> AVal.map PendingRegistration.isPreview
        let logList =
            model.RegistrationLog
            |> AVal.map (fun log -> log |> List.mapi (fun i s -> i, s) |> IndexList.ofList)
            |> AList.ofAVal

        div {
            Class "card registration-card"
            Cards.cardStyle model.RegistrationCardOpen pos
            div {
                Class "card-titlebar"
                Cards.cardDragHandle (AVal.constant "Registration") pos dragState (fun p ->
                    transact (fun () -> committedPos.Value <- p))
                button {
                    Class "card-btn-close"
                    Attribute("title", "Close")
                    Dom.OnClick(fun _ -> env.Emit [SetRegistrationCardOpen false])
                    "×"
                }
            }
            div {
                Class "card-body registration-card-body"

                // 1 · Reference (mirror of the mesh panel's ★ toggle).
                div {
                    Class "lp-sublabel"
                    refMeshOpt |> AVal.map (function
                        | Some r -> sprintf "★ Reference: %s" (Cards.shortName r)
                        | None -> "★ Reference: — none —")
                }
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

                // 2 · Stage 1: coarse landmark solve.
                div { Class "lp-sublabel"; "Stage 1 · Coarse (landmarks)" }
                div {
                    Class "reg-readiness"
                    div {
                        Class "reg-readiness-line"
                        readiness |> AVal.map (fun i ->
                            let pairCounts = Readiness.pairCounts i
                            let pairsTxt =
                                if List.isEmpty pairCounts then "no visible moving meshes"
                                else
                                    pairCounts
                                    |> List.map (fun (m, n) -> sprintf "%s:%d" (Cards.shortName m) n)
                                    |> String.concat "  "
                            sprintf "%d enabled pins · pairs %s" (List.length i.EnabledPins) pairsTxt)
                    }
                    div {
                        Class "reg-cond-badge"
                        readiness |> AVal.map (fun i ->
                            let ratio = Readiness.lambdaRatioOf i
                            if List.length i.EnabledPins < 3 then Some (Class "reg-cond-na")
                            elif ratio < 1e-3 then Some (Class "reg-cond-bad")
                            elif ratio < 0.05 then Some (Class "reg-cond-warn")
                            else Some (Class "reg-cond-ok"))
                        readiness |> AVal.map (fun i ->
                            let ratio = Readiness.lambdaRatioOf i
                            if List.length i.EnabledPins < 3 then "conditioning: n/a"
                            elif ratio < 1e-3 then sprintf "conditioning: collinear (λ2/λ1 %.1e)" ratio
                            elif ratio < 0.05 then sprintf "conditioning: weak (λ2/λ1 %.3f)" ratio
                            else sprintf "conditioning: ok (λ2/λ1 %.2f)" ratio)
                    }
                    div {
                        Class "reg-pin-list"
                        (readiness |> AVal.map (fun i ->
                            i.EnabledPins |> List.map (fun p -> p.Id, p.Label, p.AcceptedTotal) |> IndexList.ofList)
                         |> AList.ofAVal)
                        |> AList.map (fun (id, label, accepted) ->
                            div {
                                Class "reg-pin-row"
                                span {
                                    Class "reg-pin-label"
                                    sprintf "%s · %d accepted" label accepted
                                }
                                button {
                                    Class "mb"
                                    Attribute("title", "Exclude this pin from the correspondence solve")
                                    Dom.OnClick(fun _ -> env.Emit [ToggleCorrespondence id])
                                    "⊘"
                                }
                            })
                    }
                }
                div {
                    Class "lp-commit-row"
                    Primitives.showWhen (StudyGate.featureOn model "coarseSolve")
                    button {
                        Class "lp-commit"
                        (canSolveCoarse, running) ||> AVal.map2 (fun ok busy ->
                            if ok && not busy then None else Some (Attribute("disabled", "disabled")))
                        coarseTooltip |> AVal.map (fun tt -> Some (Attribute("title", tt)))
                        Dom.OnClick(fun _ -> env.Emit [SolveCoarse])
                        running |> AVal.map (fun r -> if r then "⏳ Solving…" else "▶ Solve coarse")
                    }
                }

                // 3 · Stage 2: fine ICP (existing math, unchanged).
                div {
                Primitives.showWhen (StudyGate.featureOn model "fineSolve")
                div { Class "lp-sublabel"; "Stage 2 · Fine (ICP)" }
                compactButtonBar [
                    "Traditional ICP",
                        mode |> AVal.map (fun m -> m = TraditionalIcp),
                        (fun () -> env.Emit [SetRegistrationMode TraditionalIcp])
                    "Region-restricted",
                        mode |> AVal.map (fun m -> m = RegionRestrictedIcp),
                        (fun () -> env.Emit [SetRegistrationMode RegionRestrictedIcp])
                ]
                div {
                    Class "reg-fine-warn"
                    let noCoarse =
                        model.RegistrationLog |> AVal.map (fun log ->
                            not (log |> List.exists (fun s -> s.Stage = StageCoarse)))
                    showWhen ((noCoarse, fineWarnDismissed :> aval<_>) ||> AVal.map2 (fun nc d -> nc && not d))
                    span { "No committed coarse step yet — ICP may settle in a local minimum." }
                    button {
                        Class "mb"
                        Attribute("title", "Dismiss")
                        Dom.OnClick(fun _ -> transact (fun () -> fineWarnDismissed.Value <- true))
                        "✕"
                    }
                }
                div {
                    Class "lp-commit-row"
                    let canRun =
                        (refMeshOpt, running) ||> AVal.map2 (fun r busy -> r.IsSome && not busy)
                    button {
                        Class "lp-commit"
                        canRun |> AVal.map (fun ok ->
                            if ok then None else Some (Attribute("disabled", "disabled")))
                        (refMeshOpt, running) ||> AVal.map2 (fun r busy ->
                            let tt =
                                if busy then "Solving…"
                                elif r.IsNone then "Designate a reference mesh (★) first"
                                else "Solve ICP for every visible mesh against the reference (starts from committed transforms)"
                            Some (Attribute("title", tt)))
                        Dom.OnClick(fun _ -> env.Emit [RunRegistration])
                        running |> AVal.map (fun r -> if r then "⏳ Running…" else "▶ Solve fine")
                    }
                }
                }

                // 4 · Pending result (uncommitted preview).
                div {
                    Class "reg-pending"
                    showWhen isPreview
                    div { Class "lp-sublabel"; "Pending result — previewing" }
                    div {
                        Class "reg-pending-table"
                        pendingResults |> AList.map (fun (mesh, r) ->
                            let dPct =
                                if abs r.RmsBefore < 1e-12 then 0.0
                                else (r.RmsAfter - r.RmsBefore) / r.RmsBefore * 100.0
                            div {
                                Class "reg-pending-row"
                                span { Class "reg-pending-mesh"; Cards.shortName mesh }
                                span {
                                    Class "reg-pending-rms"
                                    sprintf "%.3f → %.3f m (%+.1f%%)" r.RmsBefore r.RmsAfter dPct
                                }
                                span { Class "reg-spark"; spark r.Convergence }
                                span {
                                    Class (if r.Collinear then "reg-collinear-badge" else "reg-collinear-badge hidden")
                                    Attribute("title", "Anchor pairs are nearly collinear — rotation poorly constrained")
                                    "⚠ collinear"
                                }
                            })
                    }
                    div {
                        Class "reg-unsolved"
                        model.PendingReg |> AVal.map (function
                            | Some pr when not (List.isEmpty pr.Unsolved) ->
                                sprintf "unsolved: %s"
                                    (pr.Unsolved |> List.map Cards.shortName |> String.concat ", ")
                            | _ -> "")
                    }
                    div {
                        Class "lp-commit-row"
                        button {
                            Class "lp-commit"
                            Primitives.showWhen (StudyGate.featureOn model "commit")
                            Attribute("title", "Apply the previewed transforms and append a history step")
                            Dom.OnClick(fun _ -> env.Emit [CommitRegistration])
                            "✓ Commit"
                        }
                        button {
                            Class "lp-discard"
                            Attribute("title", "Drop the preview; nothing changes")
                            Dom.OnClick(fun _ -> env.Emit [DiscardRegistration])
                            "✕ Discard"
                        }
                    }
                }

                // 5 · History (newest first, only the newest step rolls back).
                div {
                    Class "reg-history"
                    div { Class "lp-sublabel"; "History" }
                    div {
                        Class "reg-history-list"
                        logList |> AList.map (fun (idx, step) ->
                            let rms =
                                let outs = step.Outputs |> Map.toList |> List.map snd
                                if List.isEmpty outs then "—"
                                else
                                    let b = outs |> List.averageBy (fun o -> o.RmsBefore)
                                    let a = outs |> List.averageBy (fun o -> o.RmsAfter)
                                    sprintf "%.3f → %.3f m" b a
                            let stage = match step.Stage with StageCoarse -> "coarse" | StageFine -> "fine"
                            div {
                                Class "reg-history-row"
                                span {
                                    Class "reg-history-label"
                                    sprintf "#%d %s %s · RMS %s" step.Step stage step.Mode rms
                                }
                                button {
                                    Class "mb"
                                    Primitives.showWhen (StudyGate.featureOn model "rollback")
                                    if idx <> 0 then Attribute("disabled", "disabled")
                                    Attribute("title",
                                        (if idx = 0 then "Roll this step back"
                                         else "Only the newest step can be rolled back"))
                                    Dom.OnClick(fun _ -> env.Emit [RollbackRegStep])
                                    "↩"
                                }
                            })
                    }
                    div {
                        Class "lp-commit-row"
                        button {
                            Class "lp-discard"
                            Primitives.showWhen (StudyGate.featureOn model "rollback")
                            Attribute("title", "Roll back every registration step (identity transforms, empty history)")
                            Dom.OnClick(fun _ -> env.Emit [ResetRegistration])
                            "↺ Reset"
                        }
                        // Study mode only (§9 P4): declare the current
                        // committed state the final answer.
                        button {
                            Class "lp-commit"
                            Primitives.showWhen (StudyGate.studyActive model)
                            Attribute("title", "Post the current committed transforms as your final result")
                            Dom.OnClick(fun _ -> env.Emit [StudyMsg StudySetAsFinal])
                            "★ Set as final"
                        }
                    }
                }
            }
        }

    // The nested renderControl (expensive cube capture) is mounted only while
    // the panel is open, via an alist gated on PanoramaOpen.
    let panoramaCard (env : Env<Message>) (model : AdaptiveModel) =
        let dragState : cval<(V2d * V2d) option> = cval None
        let committedPos = cval (V2d(360.0, 80.0))
        let pos = Cards.cardPos (committedPos :> aval<_>) dragState
        let mode = model.PanoramaMode

        // Pose must match PanoramaView's fallback so markers and click-to-place
        // agree with what is rendered.
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
                // Click-to-place: ray through the cylindrical pose, nearest
                // server-side hit becomes the anchor.
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
                                let names = MeshView.visibleMeshNames model
                                if not (List.isEmpty names) then
                                    async {
                                        let! hits = Query.rayHitMany ApiConfig.apiBase.Value names (fun _ -> eyeW, dirW)
                                        let best =
                                            hits |> Array.choose id
                                            |> Array.sortBy (fun (_, h) -> h.t)
                                            |> Array.tryHead
                                        match best with
                                        | Some (name, h) ->
                                            env.Emit [SetActivePickingLayer (Some name); ScanPinMsg (PlaceAnchor h.point)]
                                        | None -> ()
                                    } |> Async.Start
                        | _ -> ())
                PanoramaView.build info model
            }
        let rcMount =
            model.PanoramaOpen
            |> AVal.map (fun o -> if o then IndexList.single rc else IndexList.empty)
            |> AList.ofAVal

        // Committed pins projected into cylindrical space as overlay markers.
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
                    showWhen (mode |> AVal.map ((=) PanoBlend))
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

    let registrationToggleButton (env : Env<Message>) (model : AdaptiveModel) =
        button {
            Class "tb-btn"
            Attribute("title", "Registration solver")
            Dom.OnClick(fun _ ->
                env.Emit [SetRegistrationCardOpen (not (AVal.force model.RegistrationCardOpen))])
            "⚙ Registration"
        }

    // Anchor auto-seed review modal (clone of the retarget review): one row
    // per (pin × mesh) seeded anchor with the projection distance; rows with
    // Δ > 2× falloff or no projection are flagged. Apply marks accepted.
    let anchorReviewCard (env : Env<Message>) (model : AdaptiveModel) =
        let pinsVal = model.ScanPins.Pins |> AMap.toAVal
        let candidatesAList =
            (model.AnchorReview, model.AnchorReviewFilter) ||> AVal.map2 (fun review filt ->
                match review with
                | AnchorReviewing cs ->
                    match filt with
                    | Some mesh -> cs |> Array.filter (fun c -> c.Mesh = mesh) |> IndexList.ofArray
                    | None -> IndexList.ofArray cs
                | _ -> IndexList.empty)
            |> AList.ofAVal
        let title =
            (model.AnchorReview, model.AnchorReviewFilter) ||> AVal.map2 (fun review filt ->
                match review with
                | AnchorReviewSeeding -> "Seeding correspondence anchors…"
                | AnchorReviewing cs ->
                    match filt with
                    | Some mesh ->
                        let n = cs |> Array.filter (fun c -> c.Mesh = mesh) |> Array.length
                        sprintf "Anchor review — %s (%d anchors)" (Cards.shortName mesh) n
                    | None -> sprintf "Anchor review (%d anchors)" cs.Length
                | _ -> "Anchor review")
        let progressNote =
            model.AnchorReview |> AVal.map (function
                | AnchorReviewSeeding -> "Projecting reference anchors onto the other meshes…"
                | _ -> "")
        div {
            Class "card retarget-card anchor-review-card"
            showWhen (model.AnchorReview |> AVal.map (function AnchorReviewIdle -> false | _ -> true))
            div {
                Class "card-titlebar"
                span { Class "card-title"; title }
                button {
                    Class "card-btn-close"
                    Attribute("title", "Close (anchors stay unaccepted)")
                    Dom.OnClick(fun _ -> env.Emit [CancelAnchorReview])
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
                        let pinLabel =
                            pinsVal |> AVal.map (fun pins ->
                                match HashMap.tryFind c.PinId pins with
                                | Some p -> sprintf "(%.1f, %.1f, %.1f)" p.Centre.X p.Centre.Y p.Centre.Z
                                | None -> "(removed)")
                        let baseClass =
                            "retarget-row" +
                            (if highRisk then " retarget-row-risk" else "") +
                            (match c.Decision with
                             | AnchorAccept -> " retarget-row-accepted"
                             | AnchorReject -> " retarget-row-rejected"
                             | _ -> "")
                        div {
                            Class baseClass
                            span { Class "retarget-pin"; pinLabel }
                            span { Class "retarget-pin"; Cards.shortName c.Mesh }
                            span { Class "retarget-dist"; distLabel }
                            div {
                                Class "retarget-actions"
                                button {
                                    Class "retarget-btn-accept"
                                    Attribute("title", "Accept this anchor")
                                    Dom.OnClick(fun _ -> env.Emit [SetAnchorDecision(c.PinId, c.Mesh, AnchorAccept)])
                                    "✓"
                                }
                                button {
                                    Class "retarget-btn-reject"
                                    Attribute("title", "Reject (stays unaccepted; pick later in patches / 3D / violin)")
                                    Dom.OnClick(fun _ -> env.Emit [SetAnchorDecision(c.PinId, c.Mesh, AnchorReject)])
                                    "✕"
                                }
                            }
                        })
                }
                div {
                    Class "retarget-commit-row"
                    showWhen (model.AnchorReview |> AVal.map (function AnchorReviewing _ -> true | _ -> false))
                    button {
                        Class "lp-commit"
                        Attribute("title", "Mark accepted anchors as usable for the coarse solve")
                        Dom.OnClick(fun _ -> env.Emit [ApplyAnchorReview])
                        "Apply"
                    }
                    button {
                        Class "lp-discard"
                        Dom.OnClick(fun _ -> env.Emit [CancelAnchorReview])
                        "Cancel"
                    }
                }
            }
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
            showWhen (model.Retarget |> AVal.map (function RetargetIdle -> false | _ -> true))
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
                    showWhen (model.Retarget |> AVal.map (function RetargetReviewing _ -> true | _ -> false))
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
