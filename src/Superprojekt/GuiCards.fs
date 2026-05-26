namespace Superprojekt

open Aardvark.Base
open FSharp.Data.Adaptive
open Aardvark.Dom

module GuiCards =

    open Primitives

    let private cardDragHandle (title : string) (pos : aval<V2d>) (dragState : cval<(V2d * V2d) option>) (onCommit : V2d -> unit) =
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
                    onCommit p
                | None -> ()
            ), pointerCapture = true)
            title
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
                cardDragHandle "Explore" pos dragState (fun p -> env.Emit [SetExploreCardPos p])
                button {
                    Class "card-btn-close"
                    Attribute("title", "Close (disable explore mode)")
                    Dom.OnClick(fun _ -> env.Emit [ExploreMsg (SetExploreEnabled false)])
                    "×"
                }
            }
            div {
                Class "card-body explore-card-body"
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
                        "Blended",      mix |> AVal.map (fun m -> m = Blended),
                            (fun () -> env.Emit [ExploreMsg (SetMixMode Blended)])
                        "Side-by-side", mix |> AVal.map (fun m -> m = SideBySide),
                            (fun () -> env.Emit [ExploreMsg (SetMixMode SideBySide)])
                        "Alternating",  mix |> AVal.map (fun m -> m = Alternating),
                            (fun () -> env.Emit [ExploreMsg (SetMixMode Alternating)])
                    ]
                }
            }
        }

    let lassoCard (env : Env<Message>) (model : AdaptiveModel) =
        let dragState : cval<(V2d * V2d) option> = cval None
        let defaultPos = V2d(340.0, 44.0)
        let pos =
            (model.LassoCardPos, dragState :> aval<_>)
            ||> AVal.map2 (fun saved drag ->
                match drag with
                | Some (p, _) -> p
                | None -> saved |> Option.defaultValue defaultPos)
        let drawing   = model.LassoDrawing |> AVal.map Option.isSome
        let committed = model.LassoVolume  |> AVal.map Option.isSome
        let visible   = (drawing, committed) ||> AVal.map2 (||)
        div {
            Class "card lasso-card"
            (visible, pos) ||> AVal.map2 (fun on p ->
                if not on then Some (Style [Display "none"])
                else Some (Style [
                    Left (sprintf "%.0fpx" p.X)
                    Top (sprintf "%.0fpx" p.Y)
                ]))
            div {
                Class "card-titlebar"
                cardDragHandle "Lasso" pos dragState (fun p -> env.Emit [SetLassoCardPos p])
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
                    Class "lp-sublabel-hint"
                    (drawing, committed) ||> AVal.map2 (fun d c ->
                        match d, c with
                        | true, _ -> "Click to add vertex · double-click to commit · Esc to cancel"
                        | _, true -> "Lasso committed. Camera-anchored cone."
                        | _ -> "")
                }
                div {
                    Class "lp-clip-actions"
                    button {
                        Class "mb"
                        drawing |> AVal.map (fun on ->
                            if on then Some (Style [Display "none"]) else None)
                        Attribute("title", "Discard current lasso and start drawing a new one")
                        Dom.OnClick(fun _ -> env.Emit [LassoClear; LassoBegin])
                        "Redraw"
                    }
                    button {
                        Class "mb"
                        drawing |> AVal.map (fun on ->
                            if on then None else Some (Style [Display "none"]))
                        Attribute("title", "Cancel drawing")
                        Dom.OnClick(fun _ -> env.Emit [LassoCancel])
                        "Cancel"
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
            }
        }

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
                cardDragHandle "Registration" pos dragState (fun _ -> ())
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

    let registrationToggleButton (openCval : cval<bool>) =
        button {
            Class "tb-btn"
            Attribute("title", "Registration solver")
            Dom.OnClick(fun _ ->
                transact (fun () -> openCval.Value <- not openCval.Value))
            "⚙ Registration"
        }
