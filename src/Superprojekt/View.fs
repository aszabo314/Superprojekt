namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open FSharp.Data.Adaptive
open Aardvark.Dom
open Adaptify
open Superprojekt


module View =

    let view (env : Env<Message>) (model : AdaptiveModel) =

        let _init =
            task {
                try
                    let! cs = MeshData.fetchCentroids MeshView.apiBase.Value
                    env.Emit [CentroidsLoaded cs]
                with e ->
                    Log.error "centroids fetch failed: %A" e
                try
                    let! bboxes = MeshData.fetchBboxes MeshView.apiBase.Value
                    env.Emit [ClipBoundsLoaded bboxes]
                with e ->
                    Log.error "bboxes fetch failed: %A" e
            }

        let cursorPosition = cval None
        let shiftHeld      = cval false
        let spaceHeld      = cval false

        let revolverActive   = AVal.map2 (||) (shiftHeld :> aval<_>) model.RevolverOn
        let fullscreenActive = AVal.map2 (||) (spaceHeld :> aval<_>) model.FullscreenOn
        let revolverBase =
            AVal.custom (fun t ->
                if (shiftHeld :> aval<_>).GetValue(t) then (cursorPosition :> aval<_>).GetValue(t)
                elif (model.RevolverOn :> aval<_>).GetValue(t) then Some ((model.RevolverCenter :> aval<_>).GetValue(t))
                else None
            )

        body {
            OnBoot [
                "const l = document.getElementById('loader');"
                "if(l) l.remove();"
                "document.body.classList.add('loaded');"
            ]

            Dom.OnMouseWheel((fun e ->
                if e.Shift || spaceHeld |> AVal.force then
                    env.Emit [ CycleMeshOrder (sign e.DeltaY) ]
                    false
                else
                    true
            ), true)

            renderControl {
                RenderControl.Samples 1
                Class "render-control"

                let! info = RenderControl.Info
                let! size = RenderControl.ViewportSize

                OrbitController.getAttributes (Env.map CameraMessage env)

                
                
                let mutable initial = true
                RenderControl.OnRendered(fun _ ->
                    if initial then
                        
                        initial <- false
                    env.Emit [CameraMessage OrbitMessage.Rendered]
                )

                let view = model.Camera.view |> AVal.map CameraView.viewTrafo
                let proj =
                    size |> AVal.map (fun s ->
                        Frustum.perspective 90.0 0.5 1000.0 (float s.X / float s.Y) |> Frustum.projTrafo
                    )

                Sg.View view
                Sg.Proj proj

                Sg.OnDoubleTap(fun e ->
                    env.Emit [CameraMessage (OrbitMessage.SetTargetCenter(true, AnimationKind.Tanh, e.WorldPosition))]
                    false
                )

                Sg.OnTap(fun e ->
                    if e.Ctrl && e.Button = Button.Left then
                        env.Emit [ClearFilteredMesh]
                        Interactions.triggerFilter env model e.Position
                        false
                    else true
                )

                Sg.OnLongPress(fun e ->
                    env.Emit [ClearFilteredMesh]
                    Interactions.triggerFilter env model e.Position
                    false
                )

                Sg.Shader {
                    DefaultSurfaces.trafo
                    DefaultSurfaces.simpleLighting
                    Shader.nothing
                }

                sg {
                    Sg.Scale 10.0
                    Primitives.Quad(Quad3d(V3d(-1, -1, 0), V3d(2, 0, 0), V3d(0.0, 2.0, 0.0)), C4b.SandyBrown)
                }

                sg {
                    Sg.Shader {
                        DefaultSurfaces.trafo
                        DefaultSurfaces.simpleLighting
                        Shader.withViewPos
                    }
                    Primitives.Teapot(C4b.Green)
                }

                Dom.OnPointerMove(fun e ->
                    transact (fun () ->
                        let b = e.ClientRect
                        let tc = (V2d e.ClientPosition - b.Min) / b.Size
                        cursorPosition.Value <- Some (V2d(2.0 * tc.X - 1.0, 1.0 - 2.0 * tc.Y))
                        shiftHeld.Value <- e.Shift
                    )
                )

                Dom.OnPointerDown(fun e ->
                    if AVal.force model.RevolverOn && not (AVal.force shiftHeld) then
                        let b = e.ClientRect
                        let tc = (V2d e.ClientPosition - b.Min) / b.Size
                        let ndc = V2d(2.0 * tc.X - 1.0, 1.0 - 2.0 * tc.Y)
                        env.Emit [SetRevolverCenter ndc]
                )
                

                Revolver.build env info view proj revolverBase revolverActive fullscreenActive model
            }

            Dom.OnKeyDown(fun e ->
                match e.Key with
                | "Shift" -> transact (fun () -> shiftHeld.Value <- true)
                | " "     -> transact (fun () -> spaceHeld.Value <- true)
                | _ -> ()
            )
            Dom.OnKeyUp(fun e ->
                match e.Key with
                | "Shift" -> transact (fun () -> shiftHeld.Value <- false)
                | " "     -> transact (fun () -> spaceHeld.Value <- false)
                | _ -> ()
            )

            Gui.burgerButton env
            Gui.hudTabs env model
            Gui.debugLog model
        }


module App =
    let app =
        {
            initial   = Model.initial
            update    = Update.update
            view      = View.view
            unpersist = Unpersist.instance
        }
