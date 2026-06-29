namespace Superprojekt

open Aardvark.Base
open FSharp.Data.Adaptive
open Aardvark.Dom

// Right focus panel: a large WebGL single (focused mesh, textured, pan/zoom + GPU
// correspondence pick) over a small-multiples strip of textured thumbnails, both
// from FocusScene. The Pano / Top projection toggle drives the single. Head = the
// projection toggle, ⊕ set point, ⟲ reset, ⇄ peek-reference.
module GuiFocus =

    let panel (env : Env<Message>) (model : AdaptiveModel) =
        let refMeshA = model.Registration |> AVal.map (fun r -> r.ReferenceMesh)
        let corrStep = model.WorkflowStep |> AVal.map ((=) Correspondence)

        // A hard solo in the main view falls back to its restore set so the focus
        // still resolves a mesh.
        let visibleMeshes =
            AVal.custom (fun t ->
                let names = model.MeshNames.Content.GetValue t |> IndexList.toList
                let vis =
                    match model.MeshSolo.GetValue t with
                    | Solo(_, restore) -> restore
                    | NoSolo -> model.MeshVisible.GetValue t
                names |> List.filter (fun n -> Map.tryFind n vis |> Option.defaultValue true))
        let focusMesh =
            (model.Selection.FocusedMesh, visibleMeshes) ||> AVal.map2 (fun fm vis ->
                match fm with
                | Some m when List.contains m vis -> Some m
                | _ -> List.tryHead vis)

        // Set-correspondence is offered only with a selected pin + a non-reference
        // focused mesh (reference present), and not while peeking the reference.
        let setAvailable =
            AVal.custom (fun t ->
                corrStep.GetValue t
                && not (model.FocusPeekReference.GetValue t)
                && (model.Selection.SelectedPin.GetValue t).IsSome
                && (match focusMesh.GetValue t, refMeshA.GetValue t with
                    | Some m, Some rf -> m <> rf
                    | _ -> false))

        let projBtn (p : FocusProjection) =
            button {
                Class "focus-proj-btn"
                model.FocusProjection |> AVal.map (fun a -> if a = p then Some (Class "btn-active") else None)
                Dom.OnClick(fun _ -> env.Emit [SetFocusProjection p])
                FocusProjection.label p
            }

        let resetBtn =
            button {
                Class "focus-reset"
                Attribute("title", "Reset pan / zoom")
                Dom.OnClick(fun _ -> FocusScene.resetCam (AVal.force focusMesh))
                "⟲ reset"
            }

        let peekBtn =
            button {
                Class "focus-peek"
                corrStep |> AVal.map (fun on -> if on then None else Some (Class "hidden"))
                Attribute("title", "Hold to peek the reference mesh in this frame")
                Dom.OnPointerDown((fun _ -> env.Emit [SetFocusPeekReference true]), pointerCapture = true)
                Dom.OnPointerUp((fun _ -> env.Emit [SetFocusPeekReference false]), pointerCapture = true)
                "⇄ ref"
            }

        // Set-correspondence toggle: while on, a click in the single places the
        // correspondence at the cursor's surface point.
        let setBtn =
            button {
                Class "focus-set"
                setAvailable |> AVal.map (fun a -> if a then None else Some (Class "hidden"))
                model.CorrSetMode |> AVal.map (fun on -> if on then Some (Class "btn-active") else None)
                Attribute("title", "Set correspondence: click the surface to place")
                Dom.OnClick(fun _ -> env.Emit [ToggleCorrSetMode])
                model.CorrSetMode |> AVal.map (fun on -> if on then "⊙ aiming…" else "⊕ set point")
            }

        div {
            Class "focus-panel"
            div {
                Class "focus-head"
                span { Class "focus-title"; "Focus" }
                div { Class "focus-proj"; projBtn ProjPano; projBtn ProjTop }
                div {
                    Class "focus-head-right"
                    setBtn
                    resetBtn
                    peekBtn
                }
            }
            div { Class "focus-single"; FocusScene.single env model }
            div { Class "focus-multiples"; FocusScene.multiples env model }
        }
