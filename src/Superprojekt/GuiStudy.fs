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
        div {
            Class "study-bar"
            showWhen active
            div {
                Class "study-bar-title"
                session |> AVal.map (fun s ->
                    s |> Option.map (fun s ->
                        Study.currentPhase s
                        |> Option.map (fun ph -> sprintf "%s — %s" s.Config.Title ph.Title)
                        |> Option.defaultValue s.Config.Title)
                      |> Option.defaultValue "")
            }
            div {
                Class "study-bar-goal"
                session |> AVal.map (fun s ->
                    s |> Option.bind Study.currentPhase
                      |> Option.map (fun ph -> ph.GoalLine)
                      |> Option.defaultValue "")
            }
            div {
                Class "study-bar-right"
                span {
                    Class "study-demo-badge"
                    showWhen (session |> AVal.map (fun s -> s |> Option.map (fun x -> x.Demo) |> Option.defaultValue false))
                    session |> AVal.map (fun s ->
                        s |> Option.map (fun x -> sprintf "DEMO · %s" (StudyCondition.tag x.Condition))
                          |> Option.defaultValue "")
                }
                button {
                    Class "study-exit-btn"
                    showWhen (session |> AVal.map (fun s -> s |> Option.map (fun x -> x.Demo) |> Option.defaultValue false))
                    Attribute("title", "Leave the study preview and return to the full app")
                    Dom.OnClick(fun _ -> env.Emit [StudyMsg StudyExitDemo])
                    "Exit study"
                }
            }
        }
