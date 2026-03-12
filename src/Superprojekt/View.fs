namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open FSharp.Data.Adaptive
open Aardvark.Dom
open Adaptify
open Superprojekt

module View =

    let view (env : Env<Message>) (model : AdaptiveModel) =
        body {
            OnBoot [
                "const l = document.getElementById('loader');"
                "if(l) l.remove();"
            ]

            renderControl {
                RenderControl.Samples 1

                model.DraggingPoint |> AVal.map (fun v ->
                    if Option.isSome v then Some (Style [Css.Cursor "crosshair"])
                    else None
                )

                let! size = RenderControl.ViewportSize
                Style [
                    Width "100%"
                    Height "100%"
                    Background "linear-gradient(to top, #051937, #314264, #5d6f95, #8ba1c9, #bbd5ff)"
                ]

                OrbitController.getAttributes (Env.map CameraMessage env)

                RenderControl.OnRendered(fun _ ->
                    env.Emit [CameraMessage OrbitMessage.Rendered]
                )

                let proj =
                    size |> AVal.map (fun s ->
                        Frustum.perspective 90.0 0.1 100.0 (float s.X / float s.Y)
                    )

                Sg.View(model.Camera.view |> AVal.map CameraView.viewTrafo)
                Sg.Proj(proj |> AVal.map Frustum.projTrafo)

                Sg.OnPointerLeave(fun _ ->
                    env.Emit [Hover None]
                )

                Sg.OnDoubleTap(fun e ->
                    env.Emit [CameraMessage (OrbitMessage.SetTargetCenter(true, AnimationKind.Tanh, e.WorldPosition))]
                    false
                )

                Sg.OnTap(fun e ->
                    if e.Button = Button.Right then
                        env.Emit [Click e.Position]
                        false
                    else
                        true
                )

                Sg.OnLongPress(fun e ->
                    env.Emit [Click e.Position]
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

                // green teapot at origin
                sg {
                    Sg.Shader {
                        DefaultSurfaces.trafo
                        DefaultSurfaces.simpleLighting
                        Shader.withViewPos
                    }
                    Primitives.Teapot(C4b.Green)
                }

                // yellow octahedron hovering above the teapot
                sg {
                    Sg.Translate(0.0, 0.0, 1.0)
                    Primitives.Octahedron(C4b.Yellow)
                }

                // red hover sphere
                sg {
                    Sg.Active(model.DraggingPoint |> AVal.map Option.isNone)
                    Sg.Active(model.Hover |> AVal.map Option.isSome)
                    let pos = model.Hover |> AVal.map (function Some p -> p | None -> V3d.Zero)
                    Sg.NoEvents
                    Primitives.Sphere(pos, 0.1, C4b.Red)
                }

                // drag ghost + placed points
                sg {
                    // yellow ghost sphere while dragging
                    sg {
                        Sg.NoEvents
                        let pos = model.DraggingPoint |> AVal.map (function Some (_, p) -> Some p | _ -> None)
                        Sg.Active(pos |> AVal.map Option.isSome)
                        Primitives.Sphere(pos |> AVal.map (Option.defaultValue V3d.Zero), 0.1, C4b.Yellow)
                    }

                    // interactive green placed spheres
                    model.Points |> AList.mapi (fun idx pos ->
                        sg {
                            Sg.Active(model.DraggingPoint |> AVal.map (function Some (i, _) -> i <> idx | None -> true))
                            let mutable down = false
                            Sg.Cursor(model.DraggingPoint |> AVal.map (function
                                | Some _ -> None
                                | None   -> Some "pointer"
                            ))
                            Sg.OnTap(true, fun e ->
                                if e.Button = Button.Right then
                                    env.Emit [Delete idx]
                                    false
                                else
                                    true
                            )
                            Sg.OnPointerDown(fun e ->
                                if e.Button = Button.Left then
                                    down <- true
                                    e.Context.SetPointerCapture(e.Target, e.PointerId)
                                    env.Emit [StartDrag idx]
                                    false
                                else
                                    true
                            )
                            Sg.OnPointerMove(fun e ->
                                if down then
                                    env.Emit [Message.Update(idx, e.WorldPosition)]
                                    false
                                else
                                    true
                            )
                            Sg.OnPointerUp(fun e ->
                                if e.Button = Button.Left && down then
                                    down <- false
                                    env.Emit [Message.Update(idx, e.WorldPosition); StopDrag]
                                    e.Context.ReleasePointerCapture(e.Target, e.PointerId)
                                    false
                                else
                                    true
                            )
                            Primitives.Sphere(pos, 0.1, C4b.Green)
                        }
                    )
                }

                // blue stacked boxes (counter-driven)
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

            // HUD overlay
            div {
                Style [
                    Position "fixed"
                    Top "10px"
                    Left "10px"
                ]
                h1 {
                    "Counter: "
                    model.Value |> AVal.map string
                }
                button {
                    "+"
                    Dom.OnClick(fun _ -> env.Emit [Increment])
                }
                button {
                    "-"
                    Dom.OnClick(fun _ -> env.Emit [Decrement])
                }
                button {
                    "Clear Points"
                    Dom.OnClick(fun _ -> env.Emit [Clear])
                }
                h2 {
                    model.Hover |> AVal.map (function
                        | Some p -> "Hover: " + p.ToString("0.00")
                        | None   -> "Hover: none"
                    )
                }
                ul {
                    li { "right-click to place spheres in the scene" }
                    li { "left-click and drag to move spheres" }
                    li { "right-click on a sphere to delete it" }
                    li { "double-click to focus the camera" }
                }
            }
        }

module App =    
    let app =
        {
            initial   = Model.initial
            update    = Update.update
            view      = View.view
            unpersist = Unpersist.instance
        }
