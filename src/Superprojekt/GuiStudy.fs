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

    // ── task pane: question + questionnaire widgets (§7) ───────────────

    let private parseFloatInv (s : string) =
        match System.Double.TryParse(s, System.Globalization.NumberStyles.Float,
                                     System.Globalization.CultureInfo.InvariantCulture) with
        | true, v -> Some v
        | _ -> None

    // n-point scale as a row of small buttons (1 … n), tracking an aval.
    let private scaleRow (selected : aval<int option>) (points : int) (onPick : int -> unit) =
        div {
            Class "study-scale"
            AList.ofList [ 1 .. points ]
            |> AList.map (fun i ->
                button {
                    Class "study-scale-btn"
                    selected |> AVal.map (fun s -> if s = Some i then Some (Class "study-scale-on") else None)
                    Dom.OnClick(fun _ -> onPick i)
                    string i
                })
        }

    let private confidenceRow (env : Env<Message>) (qid : string) (draft : aval<AnswerDraft>) =
        div {
            Class "study-confidence"
            span { Class "study-q-sub"; "How confident are you? (1 = guessing, 7 = certain)" }
            scaleRow (draft |> AVal.map (fun d -> d.Confidence)) 7 (fun i ->
                env.Emit [StudyMsg (StudySetConfidence(qid, i))])
        }

    let private questionWidget (env : Env<Message>) (session : aval<StudySession option>) =
        // Key the widget subtree on the (config-static) question alone — the
        // session changes on every runtime update and rebuilding the subtree
        // would reset input focus mid-typing.
        let stepQ =
            session |> AVal.map (fun s ->
                s |> Option.bind (fun s ->
                    Study.currentStep s |> Option.bind (Study.effectiveQuestion s.Config)))
        let draftOf (qid : string) =
            session |> AVal.map (fun s ->
                s |> Option.bind (fun s -> Map.tryFind qid s.Runtime.AnswersDraft)
                  |> Option.defaultValue AnswerDraft.empty)
        stepQ
        |> AVal.map (fun q ->
            match q with
            | None -> IndexList.empty
            | Some q -> IndexList.single q)
        |> AList.ofAVal
        |> AList.map (fun q ->
            let draft = draftOf q.Id
            div {
                Class "study-q"
                match q.Kind with
                | SingleChoice options ->
                    div {
                        Class "study-choices"
                        AList.ofList (List.ofArray options |> List.mapi (fun i o -> i, o))
                        |> AList.map (fun (i, option_) ->
                            button {
                                Class "study-choice"
                                draft |> AVal.map (fun d ->
                                    if d.Value = Some (AChoice i) then Some (Class "study-choice-on") else None)
                                Dom.OnClick(fun _ -> env.Emit [StudyMsg (StudySetChoice(q.Id, i))])
                                option_
                            })
                    }
                | SceneClick ->
                    let armed =
                        session |> AVal.map (fun s ->
                            s |> Option.map (fun x -> x.Runtime.SceneClickArm = Some q.Id)
                              |> Option.defaultValue false)
                    div {
                        Class "study-sceneclick"
                        button {
                            Class "study-mark-btn"
                            armed |> AVal.map (fun a -> if a then Some (Class "tb-btn-active") else None)
                            Dom.OnClick(fun _ ->
                                if AVal.force armed then env.Emit [StudyMsg StudyCancelSceneClick]
                                else env.Emit [StudyMsg (StudyArmSceneClick q.Id)])
                            armed |> AVal.map (fun a -> if a then "Click on the surface… (Esc cancels)" else "⚑ Mark in scene")
                        }
                        span {
                            Class "study-q-sub"
                            draft |> AVal.map (fun d ->
                                match d.Value with
                                | Some (APoint p) -> sprintf "marked at (%.1f, %.1f, %.1f) — click again to replace" p.X p.Y p.Z
                                | _ -> "no point marked yet")
                        }
                    }
                | NumericQ unit_ ->
                    div {
                        Class "study-numeric"
                        input {
                            Class "study-num-input"
                            Attribute("type", "number")
                            Attribute("step", "any")
                            draft |> AVal.map (fun d ->
                                match d.Value with
                                | Some (ANumber v) -> Some (Attribute("value", sprintf "%g" v))
                                | _ -> None)
                            Dom.OnInput(fun e ->
                                parseFloatInv e.Value
                                |> Option.iter (fun v -> env.Emit [StudyMsg (StudySetNumber(q.Id, v))]))
                        }
                        span { Class "study-unit"; unit_ }
                    }
                | FreeTextQ minLen ->
                    div {
                        Class "study-freetext"
                        textarea {
                            Class "study-text-input"
                            Attribute("rows", "4")
                            Dom.OnInput(fun e -> env.Emit [StudyMsg (StudySetText(q.Id, e.Value))])
                        }
                        span {
                            Class "study-q-sub"
                            if minLen > 0 then
                                draft |> AVal.map (fun d ->
                                    let len =
                                        match d.Value with
                                        | Some (AText t) -> t.Trim().Length
                                        | _ -> 0
                                    if len >= minLen then "✓"
                                    else sprintf "%d more characters needed" (minLen - len))
                            else AVal.constant ""
                        }
                    }
                | LikertGrid (items, points) ->
                    div {
                        Class "study-grid"
                        AList.ofList (List.ofArray items |> List.mapi (fun i it -> i, it))
                        |> AList.map (fun (i, item) ->
                            let cur =
                                draft |> AVal.map (fun d ->
                                    match d.Value with
                                    | Some (AGrid g) -> Map.tryFind i g
                                    | _ -> None)
                            div {
                                Class "study-grid-row"
                                div { Class "study-grid-item"; item }
                                if points > 20 then
                                    // Raw-TLX style 0–100 slider
                                    div {
                                        Class "study-grid-slider"
                                        input {
                                            Attribute("type", "range")
                                            Attribute("min", "0")
                                            Attribute("max", "100")
                                            Attribute("step", "1")
                                            cur |> AVal.map (fun c ->
                                                Some (Attribute("value", c |> Option.map (sprintf "%.0f") |> Option.defaultValue "50")))
                                            Dom.OnInput(fun e ->
                                                parseFloatInv e.Value
                                                |> Option.iter (fun v -> env.Emit [StudyMsg (StudySetGridItem(q.Id, i, v))]))
                                        }
                                        span {
                                            Class "study-grid-val"
                                            cur |> AVal.map (function Some v -> sprintf "%.0f" v | None -> "—")
                                        }
                                    }
                                else
                                    scaleRow (cur |> AVal.map (Option.map int)) points (fun v ->
                                        env.Emit [StudyMsg (StudySetGridItem(q.Id, i, float v))])
                            })
                    }
                if q.Confidence then
                    confidenceRow env q.Id draft
                // tutorial gold feedback (the one correctness echo, §4)
                div {
                    Class "study-gold"
                    session |> AVal.map (fun so ->
                        match so with
                        | Some s when Study.isTutorialPhase s.Config s.Runtime.PhaseIx && q.Gold ->
                            match Map.tryFind q.Id s.Runtime.GoldStatus with
                            | Some true -> Some (Class "study-gold-ok")
                            | Some false -> Some (Class "study-gold-bad")
                            | None -> Some (Class "hidden")
                        | _ -> Some (Class "hidden"))
                    session |> AVal.map (fun so ->
                        match so with
                        | Some s ->
                            match Map.tryFind q.Id s.Runtime.GoldStatus with
                            | Some true -> "✓ correct — press Next"
                            | Some false -> "✗ not quite — have another look and try again"
                            | None -> ""
                        | None -> "")
                }
            })

    let taskPane (env : Env<Message>) (model : AdaptiveModel) =
        let session = sessionVal model
        let current =
            session |> AVal.map (fun s ->
                s |> Option.bind (fun s ->
                    Study.currentStep s |> Option.map (fun st -> s, st)))
        let showPane =
            current |> AVal.map (fun c ->
                match c with
                | Some (_, st) -> (match st.Kind with KQuestion | KQuestionnaire _ -> true | _ -> false)
                | None -> false)
        div {
            Class "study-task-pane"
            showWhen showPane
            div {
                Class "study-task-head"
                current |> AVal.map (fun c ->
                    match c with
                    | Some (_, st) ->
                        match st.Kind with
                        | KQuestionnaire _ -> "Questionnaire"
                        | _ -> "Question"
                    | None -> "")
            }
            div {
                Class "study-task-body"
                current |> AVal.map (fun c ->
                    c |> Option.map (fun (_, st) -> st.Body) |> Option.defaultValue "")
            }
            questionWidget env session
        }
