namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open FSharp.Data.Adaptive
open Aardvark.Dom
open Adaptify
open Superprojekt


module View =

    let view (env : Env<Message>) (model : AdaptiveModel) =

        //Interactions.startHoverQuery env model

        let _init =
            task {
                try
                    let! cs = MeshData.fetchCentroids MeshView.apiBase.Value
                    env.Emit [CentroidsLoaded cs]
                with e ->
                    Log.error "centroids fetch failed: %A" e
            }

        let cursorPosition = cval None
        let shiftHeld      = cval false
        let spaceHeld      = cval false

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

                Sg.OnPointerMove(fun e ->
                    env.Emit [SetCurrentHoverPosition (Some e.WorldPosition)]
                )
                Sg.OnFocusLeave(fun _ ->
                    env.Emit [SetCurrentHoverPosition None]
                )
                Sg.OnPointerLeave(fun _ ->
                    env.Emit [SetCurrentHoverPosition None; Hover None]
                )

                RenderControl.OnRendered(fun _ ->
                    env.Emit [CameraMessage OrbitMessage.Rendered]
                )

                let view =
                    model.Camera.view |> AVal.map CameraView.viewTrafo
                
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
                    //env.Emit [LogDebug (sprintf "LongPress pos=%s world=%s" (e.Position.ToString("0.00")) (e.WorldPosition.ToString("0.00")))]
                    env.Emit [ClearFilteredMesh]
                    Interactions.triggerFilter env model e.Position
                    false
                )

                Sg.OnPointerMove(fun p ->
                    env.Emit [Hover (Some p.Position)]
                )

                Sg.Shader {
                    DefaultSurfaces.trafo
                    DefaultSurfaces.simpleLighting
                    Shader.nothing
                }

                // floor plane
                sg {
                    Sg.Scale 10.0
                    Primitives.Quad(Quad3d(V3d(-1, -1, 0), V3d(2, 0, 0), V3d(0.0, 2.0, 0.0)), C4b.SandyBrown)
                }

                Dom.OnPointerMove(fun e ->
                    transact (fun () ->
                        let b = e.ClientRect
                        let tc = (V2d e.ClientPosition - b.Min) / b.Size
                        cursorPosition.Value <- Some (V2d(2.0 * tc.X - 1.0, 1.0 - 2.0 * tc.Y))
                        shiftHeld.Value <- e.Shift
                    )
                )

                Revolver.build info view proj cursorPosition spaceHeld shiftHeld model
                
                // green teapot
                sg {
                    Sg.Shader {
                        DefaultSurfaces.trafo
                        DefaultSurfaces.simpleLighting
                        Shader.withViewPos
                    }
                    Primitives.Teapot(C4b.Green)
                }

                // yellow octahedron
                sg {
                    Sg.Translate(0.0, 0.0, 1.0)
                    Primitives.Octahedron(C4b.Yellow)
                }

                // red hover sphere
                sg {
                    Sg.Active(model.Hover |> AVal.map Option.isSome)
                    let pos = model.Hover |> AVal.map (function Some p -> p | None -> V3d.Zero)
                    Sg.NoEvents
                    Primitives.Sphere(pos, 0.1, C4b.Red)
                }

                // blue stacked boxes
                sg {
                    Sg.NoEvents
                    Sg.Translate(1.0, 0.0, 0.0)
                    ASet.range (AVal.constant 0) model.Value
                    |> ASet.map (fun i ->
                        sg {
                            Sg.Translate(0.0, 0.0, float i * 0.4)
                            Primitives.Box(Box3d.FromCenterAndSize(V3d.Zero, V3d.III * 0.1), C4b.Blue)
                        }
                    )
                }
            }

            Dom.OnKeyDown(fun e ->
                if e.Key = "Shift" then transact (fun () -> shiftHeld.Value <- true)
            )
            Dom.OnKeyUp(fun e ->
                if e.Key = "Shift" then transact (fun () -> shiftHeld.Value <- false)
            )
            Dom.OnKeyDown(fun e ->
                if e.Key = " " then transact (fun () -> spaceHeld.Value <- true)
            )
            Dom.OnKeyUp(fun e ->
                if e.Key = " " then transact (fun () -> spaceHeld.Value <- false)
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
