namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open FSharp.Data.Adaptive
open Aardvark.Dom
open Adaptify
open Superprojekt
open Aardworx.WebAssembly

module View =

    let render (name : string) (active : aval<bool>) (commonCentroid : aval<V3d>) =
        sg {
            Sg.Active active
            Sg.Shader {
                DefaultSurfaces.trafo
                DefaultSurfaces.diffuseTexture
            }
            let pos = cval (ArrayBuffer Array.empty<V3f> :> IBuffer)
            let tc  = cval (ArrayBuffer Array.empty<V2f>  :> IBuffer)
            let idx = cval (ArrayBuffer Array.empty<int>   :> IBuffer)
            let tex = cval<ITexture> (AVal.force DefaultTextures.checkerboard)
            let fvc = cval 0

            let _loader =
                task {
                    try
                        let! mesh = MeshData.fetch "https://kingpin.aardvarkians.com/api" name 0
                        let cc = AVal.force commonCentroid
                        // localPos is centroid-relative → add mesh.centroid for world pos → subtract common centroid
                        let rebased = mesh.positions |> Array.map (fun p -> V3f(mesh.centroid + V3d p - cc))
                        transact (fun () ->
                            pos.Value <- ArrayBuffer rebased
                            tc.Value  <- ArrayBuffer mesh.uvs
                            idx.Value <- ArrayBuffer mesh.indices
                            fvc.Value <- mesh.indices.Length
                        )
                        let! img = JSImage.load mesh.atlasUrl
                        transact (fun () -> tex.Value <- JSTexture(img, true))
                    with e ->
                        Log.error "failed to load mesh %s: %A" name e
                }

            Sg.Uniform("DiffuseColorTexture", tex)
            Sg.VertexAttributes(
                HashMap.ofList [
                    string DefaultSemantic.Positions,              BufferView(pos, typeof<V3f>)
                    string DefaultSemantic.DiffuseColorCoordinates, BufferView(tc,  typeof<V2f>)
                ]
            )
            Sg.Index(BufferView(idx, typeof<int>))
            Sg.Render fvc
        }


    let view (env : Env<Message>) (model : AdaptiveModel) =

        // fetch all centroids once on startup; populates MeshNames + CommonCentroid
        let _init =
            task {
                try
                    let! cs = MeshData.fetchCentroids "https://kingpin.aardvarkians.com/api"
                    env.Emit [CentroidsLoaded cs]
                with e ->
                    Log.error "centroids fetch failed: %A" e
            }

        body {
            OnBoot [
                "const l = document.getElementById('loader');"
                "if(l) l.remove();"
            ]

            renderControl {
                RenderControl.Samples 1
                Class "render-control"

                model.DraggingPoint |> AVal.map (fun v ->
                    if Option.isSome v then Some (Style [Css.Cursor "crosshair"])
                    else None
                )

                let! size = RenderControl.ViewportSize

                OrbitController.getAttributes (Env.map CameraMessage env)

                RenderControl.OnRendered(fun _ ->
                    env.Emit [CameraMessage OrbitMessage.Rendered]
                )

                let proj =
                    size |> AVal.map (fun s ->
                        Frustum.perspective 90.0 0.5 1000.0 (float s.X / float s.Y)
                    )

                Sg.View(model.Camera.view |> AVal.map CameraView.viewTrafo)
                Sg.Proj(proj |> AVal.map Frustum.projTrafo)

                Sg.OnPointerLeave(fun _ -> env.Emit [Hover None])

                Sg.OnDoubleTap(fun e ->
                    env.Emit [CameraMessage (OrbitMessage.SetTargetCenter(true, AnimationKind.Tanh, e.WorldPosition))]
                    false
                )

                Sg.OnTap(fun e ->
                    if e.Button = Button.Right then
                        env.Emit [Click e.Position]
                        false
                    else
                        // test: sphere query at tap position on all loaded meshes
                        let renderPos = e.Position                              // render-space (relative to commonCentroid)
                        let cc        = AVal.force model.CommonCentroid
                        let worldPos  = renderPos + cc                          // absolute world-space for server
                        let names     = AList.force model.MeshNames
                        task {
                            let mutable total = 0
                            for name in names do
                                let! tris =
                                    Query.sphereTriangles "https://kingpin.aardvarkians.com/api" name 0 worldPos 0.5
                                    |> Async.StartAsTask
                                total <- total + tris.Length
                            Log.line "sphere query @ (%.2f, %.2f, %.2f) r=0.5 → %d triangles" worldPos.X worldPos.Y worldPos.Z total
                        } |> ignore
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

                // all loaded meshes
                model.MeshNames |> AList.map (fun name ->
                    let active =
                        model.MeshVisible
                        |> AVal.map (fun m -> Map.tryFind name m |> Option.defaultValue true)
                    render name active model.CommonCentroid
                ) |> AList.toASet

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
                    sg {
                        Sg.NoEvents
                        let pos = model.DraggingPoint |> AVal.map (function Some (_, p) -> Some p | _ -> None)
                        Sg.Active(pos |> AVal.map Option.isSome)
                        Primitives.Sphere(pos |> AVal.map (Option.defaultValue V3d.Zero), 0.1, C4b.Yellow)
                    }

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
                                else true
                            )
                            Sg.OnPointerDown(fun e ->
                                if e.Button = Button.Left then
                                    down <- true
                                    e.Context.SetPointerCapture(e.Target, e.PointerId)
                                    env.Emit [StartDrag idx]
                                    false
                                else true
                            )
                            Sg.OnPointerMove(fun e ->
                                if down then
                                    env.Emit [Message.Update(idx, e.WorldPosition)]
                                    false
                                else true
                            )
                            Sg.OnPointerUp(fun e ->
                                if e.Button = Button.Left && down then
                                    down <- false
                                    env.Emit [Message.Update(idx, e.WorldPosition); StopDrag]
                                    e.Context.ReleasePointerCapture(e.Target, e.PointerId)
                                    false
                                else true
                            )
                            Primitives.Sphere(pos, 0.1, C4b.Green)
                        }
                    )
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

            // Burger menu toggle (mobile)
            button {
                Attribute("id", "burger-btn")
                Class "burger-btn"
                Dom.OnClick(fun _ ->
                    env.Emit [ToggleMenu]
                )
                div { Class "burger-line" }
                div { Class "burger-line" }
                div { Class "burger-line" }
            }

            // HUD overlay — pure-CSS tabs
            div {
                Class "tabs"
                model.MenuOpen |> AVal.map (fun o ->
                    if o then Some (Class "tabs-open")
                    else None
                )

                div {
                    Class "tab-labels"
                    label {
                        input {
                            Attribute("type",    "radio")
                            Attribute("name",    "hud-tabs")
                            Attribute("id",      "hud-tab1")
                            Attribute("checked", "checked")
                        }
                        "Scene"
                    }
                    label {
                        input {
                            Attribute("type", "radio")
                            Attribute("name", "hud-tabs")
                            Attribute("id",   "hud-tab2")
                        }
                        "Info"
                    }
                    label {
                        input {
                            Attribute("type", "radio")
                            Attribute("name", "hud-tabs")
                            Attribute("id",   "hud-tab3")
                        }
                        "Settings"
                    }
                }

                div {
                    Class "tab-panels"

                    // ── Tab 1: Scene ──────────────────────────────────
                    div {
                        Class "tab-panel"
                        Attribute("id", "hud-panel1")

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

                        // mesh toggles
                        h3 { "Meshes" }
                        model.MeshNames |> AList.map (fun name ->
                            let isVis =
                                model.MeshVisible
                                |> AVal.map (fun m -> Map.tryFind name m |> Option.defaultValue true)
                            div {
                                label {
                                    input {
                                        Attribute("type", "checkbox")
                                        isVis |> AVal.map (fun v ->
                                            if v then Some (Attribute("checked", "checked"))
                                            else None
                                        )
                                        Dom.OnClick(fun _ ->
                                            let current = AVal.force isVis
                                            env.Emit [SetVisible(name, not current)]
                                        )
                                    }
                                    " " + name
                                }
                            }
                        )

                        ul {
                            li { "right-click to place spheres" }
                            li { "left-click and drag to move" }
                            li { "right-click a sphere to delete" }
                            li { "double-click to focus camera" }
                        }
                    }

                    // ── Tab 2: Info ───────────────────────────────────
                    div {
                        Class "tab-panel"
                        Attribute("id", "hud-panel2")

                        h1 { "Info" }
                        p { "Aardvark.Dom WebAssembly scaffold." }
                        p { "Elm-style architecture: Model / Update / View." }
                        p { "Rendering via WebGL with an orbit camera." }
                    }

                    // ── Tab 3: Settings ───────────────────────────────
                    div {
                        Class "tab-panel"
                        Attribute("id", "hud-panel3")

                        h1 { "Settings" }
                        p { "Nothing to configure yet." }
                    }
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
