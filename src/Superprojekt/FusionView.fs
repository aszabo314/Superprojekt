namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open Aardworx.WebAssembly
open FSharp.Data.Adaptive
open Aardvark.Dom
open FShade

// Fullscreen-quad composite: samples the offscreen fusion colour texture and
// writes it to the main framebuffer. The quad is supplied in NDC (clip space)
// so the vertex stage is a pure pass-through — no view/proj.
[<ReflectedDefinition>]
module FusionComposite =
    open FShade

    let private fusionColor =
        sampler2d {
            texture uniform?FusionColor
            filter Filter.MinMagLinear
            addressU WrapMode.Clamp
            addressV WrapMode.Clamp
        }

    type Vtx = {
        [<Position>]               pos : V4f
        [<Semantic("FusionTc")>]   tc  : V2f
    }

    let vertex (v : Vtx) =
        vertex {
            // pos is already clip-space NDC; derive [0,1] tex coords from it.
            return { v with tc = V2f(v.pos.X * 0.5f + 0.5f, v.pos.Y * 0.5f + 0.5f) }
        }

    let fragment (v : Vtx) =
        fragment {
            return fusionColor.Sample(v.tc)
        }

module FusionView =

    // Fullscreen quad in NDC (z = 0). Composite uses DepthTest.None so z is moot.
    let private quadPos =
        [| V3f(-1.0f, -1.0f, 0.0f); V3f(1.0f, -1.0f, 0.0f)
           V3f( 1.0f,  1.0f, 0.0f); V3f(-1.0f, 1.0f, 0.0f) |]
    let private quadIdx = [| 0; 1; 2; 0; 2; 3 |]

    // Build the offscreen fusion pass + the composite node that draws its
    // colour output into the main framebuffer. The offscreen render task is
    // lazy: it only runs when the composite node is Active (FusionMode on) and
    // its colour-texture uniform gets pulled during the main render.
    let build
        (info : Aardvark.Dom.RenderControlInfo)
        (model : AdaptiveModel)
        (view : aval<Trafo3d>)
        (proj : aval<Trafo3d>) : aset<ISceneNode> =

        let runtime = info.Runtime
        let size = info.ViewportSize

        let signature =
            runtime.CreateFramebufferSignature([
                DefaultSemantic.Colors,       TextureFormat.Rgba8
                DefaultSemantic.DepthStencil, TextureFormat.Depth24Stencil8
            ])

        let colorAtt =
            runtime.CreateTextureAttachment(runtime.CreateTexture2D(size, TextureFormat.Rgba8))
        let depthAtt =
            runtime.CreateTextureAttachment(runtime.CreateTexture2D(size, TextureFormat.Depth24Stencil8))

        let fbo =
            runtime.CreateFramebuffer(signature, Map.ofList [
                DefaultSemantic.Colors,       colorAtt
                DefaultSemantic.DepthStencil, depthAtt
            ])

        let fusionNode = MeshView.buildFusionNode model view proj
        let renderObjects, _ = fusionNode.GetObjects(TraversalState.empty runtime)

        let task = runtime.CompileRender(signature, renderObjects)
        let clr = clear { color C4f.Black; depth 1.0 }
        let output = task |> RenderTask.renderToWithClear fbo clr
        let colorOut = output.GetOutputTexture DefaultSemantic.Colors

        let composite =
            sg {
                Sg.Active model.FusionMode
                Sg.DepthTest (AVal.constant DepthTest.None)
                Sg.Shader {
                    FusionComposite.vertex
                    FusionComposite.fragment
                }
                Sg.Uniform("FusionColor", colorOut)
                Sg.VertexAttributes(
                    HashMap.ofList [
                        string DefaultSemantic.Positions,
                            BufferView(AVal.constant (ArrayBuffer quadPos :> IBuffer), typeof<V3f>)
                    ]
                )
                Sg.Index(BufferView(AVal.constant (ArrayBuffer quadIdx :> IBuffer), typeof<int>))
                Sg.Render (AVal.constant quadIdx.Length)
            }

        ASet.single composite
