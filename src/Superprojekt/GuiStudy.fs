namespace Superprojekt

open Aardvark.Base
open FSharp.Data.Adaptive
open Aardvark.Dom

// Study-mode chrome: full-screen pages (joining / invalid link / screened)
// and the study bar that replaces the normal top bar.
module GuiStudy =

    open Primitives

    let private sessionVal (model : AdaptiveModel) =
        model.Study |> AVal.map (function
            | Some (StudyActive s) -> Some s
            | _ -> None)

    // ── full-screen pages ──────────────────────────────────────────────

    let studyPages (model : AdaptiveModel) =
        let page =
            model.Study |> AVal.map (function
                | Some StudyJoining ->
                    Some ("Connecting to the study…",
                          "One moment — your session is being prepared.")
                | Some (StudyFailed message) ->
                    Some ("This study link is not available",
                          sprintf "%s. If you believe this is an error, please contact the study team." message)
                | Some StudyScreened ->
                    Some ("Thank you for your interest!",
                          "Unfortunately the tutorial checks did not work out this time, so the study ends here. Thank you for giving it a try — you may close this window.")
                | _ -> None)
        div {
            Class "study-page"
            showWhen (page |> AVal.map Option.isSome)
            div {
                Class "study-page-card"
                div { Class "study-page-title"; page |> AVal.map (fun p -> p |> Option.map fst |> Option.defaultValue "") }
                div { Class "study-page-body"; page |> AVal.map (fun p -> p |> Option.map snd |> Option.defaultValue "") }
            }
        }

    // ── study bar ──────────────────────────────────────────────────────

    let studyBar (env : Env<Message>) (model : AdaptiveModel) =
        let session = sessionVal model
        let active = session |> AVal.map Option.isSome
        let isDemo =
            session |> AVal.map (fun s -> s |> Option.map (fun x -> x.Demo) |> Option.defaultValue false)
        let placementActive =
            model.ScanPins.Placement |> AVal.map (function
                | AnchorPlacement -> true
                | _ -> false)
        div {
            Class "study-bar"
            showWhen active
            // progress dots: one per phase, current highlighted
            div {
                Class "study-dots"
                session
                |> AVal.map (fun s ->
                    match s with
                    | Some s ->
                        s.Config.Phases
                        |> List.mapi (fun i ph -> i, ph.Title, compare i s.Runtime.PhaseIx)
                        |> IndexList.ofList
                    | None -> IndexList.empty)
                |> AList.ofAVal
                |> AList.map (fun (_, title, cmp) ->
                    span {
                        Class (match cmp with
                               | -1 -> "study-dot study-dot-done"
                               | 0 -> "study-dot study-dot-current"
                               | _ -> "study-dot")
                        Attribute("title", title)
                    })
            }
            div {
                Class "study-bar-title"
                session |> AVal.map (fun s ->
                    s |> Option.bind Study.currentPhase
                      |> Option.map (fun ph -> ph.Title)
                      |> Option.defaultValue "")
            }
            // the goal line is always visible (§5)
            div {
                Class "study-bar-goal"
                session |> AVal.map (fun s ->
                    s |> Option.bind Study.currentPhase
                      |> Option.map (fun ph -> ph.GoalLine)
                      |> Option.defaultValue "")
            }
            // tool strip: only the features the current phase allows
            div {
                Class "study-tools"
                button {
                    Class "tb-btn study-tool"
                    showWhen (StudyGate.featureOn model "meshPanel")
                    Attribute("title", "Toggle the layer panel")
                    Dom.OnClick(fun _ -> env.Emit [ToggleMenu])
                    "☰ Layers"
                }
                button {
                    Class "tb-btn study-tool"
                    showWhen (StudyGate.featureOn model "pinPlace")
                    placementActive |> AVal.map (fun on -> if on then Some (Class "tb-btn-active") else None)
                    Attribute("title", "Place a pin — click on a surface (Esc cancels)")
                    Dom.OnClick(fun _ ->
                        if AVal.force placementActive then env.Emit [ScanPinMsg CancelPlacement]
                        else env.Emit [ScanPinMsg EnterAnchorPlacement])
                    "○ Pin"
                }
            }
            div {
                Class "study-bar-right"
                span {
                    Class "study-demo-badge"
                    showWhen isDemo
                    session |> AVal.map (fun s ->
                        s |> Option.map (fun x -> sprintf "DEMO · %s" (StudyCondition.tag x.Condition))
                          |> Option.defaultValue "")
                }
                button {
                    Class "study-exit-btn"
                    showWhen isDemo
                    Attribute("title", "Leave the study preview and return to the full app")
                    Dom.OnClick(fun _ -> env.Emit [StudyMsg StudyExitDemo])
                    "Exit study"
                }
                button {
                    Class "study-help-btn"
                    Attribute("title", "Show the current step's instructions again")
                    Dom.OnClick(fun _ -> env.Emit [StudyMsg StudyReopenOverlay])
                    "?"
                }
                button {
                    Class "study-next-btn"
                    session |> AVal.map (fun s ->
                        let satisfied = s |> Option.map (fun x -> x.Runtime.StepSatisfied) |> Option.defaultValue false
                        if satisfied then None else Some (Attribute("disabled", "disabled")))
                    session |> AVal.map (fun s ->
                        let satisfied = s |> Option.map (fun x -> x.Runtime.StepSatisfied) |> Option.defaultValue false
                        Some (Attribute("title",
                            if satisfied then "Continue to the next step"
                            else "Complete the current step first")))
                    Dom.OnClick(fun _ -> env.Emit [StudyMsg StudyNext])
                    "Next →"
                }
            }
        }

    // ── instruction overlay / guided tooltip ───────────────────────────

    // Minimal body rendering: blank-line separated paragraphs (the config
    // copy is placeholder English, edited by the researcher later).
    let private bodyParagraphs (body : string) =
        body.Replace("\r\n", "\n").Split([| "\n\n" |], System.StringSplitOptions.RemoveEmptyEntries)
        |> Array.map (fun p -> p.Trim())
        |> Array.filter (fun p -> p.Length > 0)
        |> Array.toList

    let instructionOverlay (env : Env<Message>) (model : AdaptiveModel) =
        let session = sessionVal model
        let stepInfo =
            session |> AVal.map (fun s ->
                match s with
                | Some s when s.Runtime.OverlayOpen ->
                    Study.currentStep s |> Option.map (fun st -> s, st)
                | _ -> None)
        // guidedAction with anchor → non-blocking tooltip card; else modal
        let tooltip =
            stepInfo |> AVal.map (function
                | Some (_, st) when st.Kind = KGuidedAction && st.Anchor.IsSome -> Some st
                | _ -> None)
        let modal =
            stepInfo |> AVal.map (function
                | Some (s, st) when not (st.Kind = KGuidedAction && st.Anchor.IsSome) -> Some (s, st)
                | _ -> None)
        let satisfied =
            session |> AVal.map (fun s ->
                s |> Option.map (fun x -> x.Runtime.StepSatisfied) |> Option.defaultValue false)
        let paragraphs (f : aval<string>) =
            f |> AVal.map (bodyParagraphs >> IndexList.ofList) |> AList.ofAVal
            |> AList.map (fun p -> div { Class "study-para"; p })
        div {
            // dim-background modal
            div {
                Class "study-overlay"
                showWhen (modal |> AVal.map Option.isSome)
                div {
                    Class "study-overlay-card"
                    div {
                        Class "study-overlay-body"
                        paragraphs (modal |> AVal.map (fun m -> m |> Option.map (fun (_, st) -> st.Body) |> Option.defaultValue ""))
                    }
                    // completion code on the final step (§9 P6)
                    div {
                        Class "study-code-box"
                        showWhen (modal |> AVal.map (fun m ->
                            match m with
                            | Some (s, _) -> s.Runtime.CompletionCode.IsSome
                            | None -> false))
                        span { Class "study-code-label"; "Your completion code" }
                        span {
                            Class "study-code"
                            modal |> AVal.map (fun m ->
                                match m with
                                | Some (s, _) -> s.Runtime.CompletionCode |> Option.defaultValue ""
                                | None -> "")
                        }
                    }
                    div {
                        Class "study-overlay-footer"
                        button {
                            Class "study-exit-btn"
                            Dom.OnClick(fun _ -> env.Emit [StudyMsg StudyCloseOverlay])
                            "Got it"
                        }
                        button {
                            Class "study-next-btn"
                            satisfied |> AVal.map (fun ok ->
                                if ok then None else Some (Attribute("disabled", "disabled")))
                            Dom.OnClick(fun _ -> env.Emit [StudyMsg StudyNext])
                            "Continue →"
                        }
                    }
                }
            }
            // anchored tooltip (non-blocking, live checkmark per §5)
            div {
                tooltip |> AVal.map (fun t ->
                    let anchor = t |> Option.bind (fun st -> st.Anchor) |> Option.defaultValue "viewport"
                    Some (Class (sprintf "study-tip study-tip-%s%s" anchor (if t.IsSome then "" else " hidden"))))
                div {
                    Class "study-tip-check"
                    satisfied |> AVal.map (fun ok -> if ok then Some (Class "study-tip-check-ok") else None)
                    satisfied |> AVal.map (fun ok -> if ok then "✓ done — press Next" else "○ not done yet")
                }
                div {
                    Class "study-tip-body"
                    paragraphs (tooltip |> AVal.map (fun t -> t |> Option.map (fun st -> st.Body) |> Option.defaultValue ""))
                }
                button {
                    Class "study-tip-close"
                    Attribute("title", "Hide (the ? button brings it back)")
                    Dom.OnClick(fun _ -> env.Emit [StudyMsg StudyCloseOverlay])
                    "✕"
                }
            }
        }
