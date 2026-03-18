namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open FSharp.Data.Adaptive
open Aardvark.Dom
open Adaptify
open Superprojekt


module BlitShader =
    open FShade
    type Fragment =
        {
            [<Color>] c : V4d
            [<Depth>] d : float
        }
    
    let colorSam =
        sampler2d {
            texture uniform?ColorTexture
            filter Filter.MinMagPoint
            addressU WrapMode.Wrap
            addressV WrapMode.Wrap
        }
    let depthSam =
        sampler2d {
            texture uniform?DepthTexture
            filter Filter.MinMagPoint
            addressU WrapMode.Wrap
            addressV WrapMode.Wrap
        }
    
    let read (v : Effects.Vertex) =
        fragment {
            return  {
                c = colorSam.SampleLevel(v.tc, 0.0)
                d = depthSam.SampleLevel(v.tc, 0.0).X
            }
            
        }
        
    type UniformScope with
        member x.TextureOffset : V2d = x?TextureOffset
        member x.TextureScale : V2d = x?TextureScale
        
    let readColor (v : Effects.Vertex) =
        fragment {
            
            let ndc = 2.0 * v.tc - V2d.II
            if Vec.lengthSquared ndc > 1.0 then discard()
            
            let a = V4d.IIOI
            let b = colorSam.SampleLevel(uniform.TextureOffset + uniform.TextureScale * v.tc, 0.0)
            return lerp  a b 0.5
        }
    

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

        body {
            OnBoot [
                "const l = document.getElementById('loader');"
                "if(l) l.remove();"
                "document.body.classList.add('loaded');"
            ]

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

                let meshTextures =
                    
                    let signature =
                        info.Runtime.CreateFramebufferSignature [
                            DefaultSemantic.Colors, TextureFormat.Rgba8
                            DefaultSemantic.DepthStencil,  TextureFormat.Depth24Stencil8
                        ]
                    
                    model.MeshNames |> AList.toASet |> ASet.mapToAMap (fun name ->
                        let mesh =
                            sg {
                                Sg.View view
                                Sg.Proj proj
                                Sg.Uniform("ViewportSize", info.ViewportSize)
                                MeshView.render name (AVal.constant true) model.CommonCentroid
                            }
                            
                        let objs = mesh.GetRenderObjects(TraversalState.empty info.Runtime)
                        let task = info.Runtime.CompileRender(signature, objs)
                            
                        let color, depth = 
                            task |> RenderTask.renderToColorAndDepthWithClear info.ViewportSize (clear { color C4f.Zero; depth 1.0 })
                            
                        
                        color, depth
                    )
                
                
                meshTextures |> AMap.toASet |> ASet.map (fun (name, (color, depth)) ->
                    let active =
                        model.MeshVisible
                        |> AVal.map (fun m -> Map.tryFind name m |> Option.defaultValue true)
 
                    sg {
                        Sg.Active active
                        Sg.Shader {
                            BlitShader.read
                        }
                        Sg.Uniform("ColorTexture", color)
                        Sg.Uniform("DepthTexture", depth)
                        Primitives.FullscreenQuad
                    }    
                )
                
                
                let cursorPosition = cval None
                Dom.OnPointerMove(fun e ->
                    transact (fun () ->
                        if e.Shift then
                            let b = e.ClientRect
                            let tc = (V2d e.ClientPosition - b.Min) / b.Size
                            let ndc = V2d(2.0 * tc.X - 1.0, 1.0 - 2.0 * tc.Y)
                            env.Emit [LogDebug (sprintf "pointer: %A ndc" ndc)]
                            cursorPosition.Value <- Some ndc
                        else
                            cursorPosition.Value <- None
                    )
                )
                
                let overlay =
                    sg {
                        Sg.NoEvents
                        let pixelSize = 200
                        let dataSet = "Hess-201803"
                        let t =
                            (cursorPosition, info.ViewportSize) ||> AVal.map2 (fun ndc size ->
                                match ndc with
                                | Some ndc ->
                                    let scale = float pixelSize / V2d size
                                    Trafo3d.Scale(scale.X, scale.Y, 1.0) *
                                    Trafo3d.Translation(ndc.X, ndc.Y, 0.0)
                                | None ->
                                    Trafo3d.Scale(0.0)
                            )
                        
                        let texture =
                            meshTextures
                            |> AMap.tryFind dataSet
                            |> AdaptiveResource.bind (function Some (c,_) -> c |> AdaptiveResource.map (fun t -> t :> ITexture) :> aval<_> | None -> DefaultTextures.checkerboard)
                        
                        let textureOffset =
                            (cursorPosition, info.ViewportSize) ||> AVal.map2 (fun ndc size ->
                                match ndc with
                                | Some ndc ->
                                    let tc = (ndc + V2d.II) * 0.5
                                    tc - 0.5 * V2d(pixelSize,pixelSize) / V2d(size)
                                | None ->
                                    V2d.Zero
                            )
                            
                        let textureScale =
                            info.ViewportSize |> AVal.map (fun s -> V2d(pixelSize) / V2d(s))
                        
                        Sg.Uniform("TextureOffset", textureOffset)
                        Sg.Uniform("TextureScale", textureScale)
                        
                        Sg.View Trafo3d.Identity
                        Sg.Proj Trafo3d.Identity
                        Sg.Uniform("ColorTexture", texture)
                        Sg.Trafo t
                        Sg.Shader {
                            DefaultSurfaces.trafo
                            BlitShader.readColor
                        }
                        Primitives.FullscreenQuad
                        
                    }
                
                overlay
                //
                // // all loaded meshes + filter overlay per mesh
                // model.MeshNames |> AList.map (fun name ->
                //     let active =
                //         model.MeshVisible
                //         |> AVal.map (fun m -> Map.tryFind name m |> Option.defaultValue true)
                //         
                //     Sg.Delay(fun state ->
                //         
                //         let mesh = MeshView.render name active model.CommonCentroid
                //         
                //         let objs = mesh.GetRenderObjects(state)
                //         
                //         
                //     )
                //     // sg {
                //     //     
                //     //
                //     //     sg {
                //     //         Sg.Translate(0.0, 0.0, 1.0)
                //     //         let index = AMap.tryFind name model.Filtered |> AVal.map (function Some idx -> idx | None -> [||])
                //     //         let buffer = index |> AVal.map (fun v -> ArrayBuffer(v) :> IBuffer)
                //     //         let mesh   = MeshView.loadMeshAsync name
                //     //         let filterMesh = { mesh with idx = buffer; fvc = index |> AVal.map Array.length }
                //     //         MeshView.renderMesh filterMesh active model.CommonCentroid
                //     //     }
                //     // }
                // ) |> AList.toASet

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
