namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open Aardworx.WebAssembly
open FSharp.Data.Adaptive
open Aardvark.Dom
open FShade

// Per-mesh image-space outlines. Offscreen G-buffer pass (world-Z band parity +
// depth → target0, palette colour + coverage mask → target1) then a fullscreen
// edge-detect composite painting each mesh's silhouette/cliff outline + world-Z
// isolines in its palette colour. Always on — the offscreen task runs every frame.
module OutlineView =

    let private quadPos =
        [| V3f(-1.0f, -1.0f, 0.0f); V3f(1.0f, -1.0f, 0.0f)
           V3f( 1.0f,  1.0f, 0.0f); V3f(-1.0f, 1.0f, 0.0f) |]
    let private quadIdx = [| 0; 1; 2; 0; 2; 3 |]

    let private outline1 = Sym.ofString "Outline1"

    // Offscreen G-buffer + fullscreen edge-detect composite for an arbitrary outline
    // node (already carrying its own View/Proj/Trafo). Shared by the main-view all-mesh
    // outline and the focus single's reference-mesh silhouette overlay. The composite is
    // DepthTest.None → it draws on top of whatever preceded it in the framebuffer.
    let buildFromNode
        (info : Aardvark.Dom.RenderControlInfo)
        (thresholdA : aval<float32>)
        (node : ISceneNode) : aset<ISceneNode> =

        let runtime = info.Runtime
        let size = info.ViewportSize

        let signature =
            runtime.CreateFramebufferSignature([
                DefaultSemantic.Colors,       TextureFormat.Rgba8
                outline1,                     TextureFormat.Rgba8
                DefaultSemantic.DepthStencil, TextureFormat.Depth24Stencil8
            ])

        let normalAtt =
            runtime.CreateTextureAttachment(runtime.CreateTexture2D(size, TextureFormat.Rgba8))
        let colorAtt =
            runtime.CreateTextureAttachment(runtime.CreateTexture2D(size, TextureFormat.Rgba8))
        let depthAtt =
            runtime.CreateTextureAttachment(runtime.CreateTexture2D(size, TextureFormat.Depth24Stencil8))

        let fbo =
            runtime.CreateFramebuffer(signature, Map.ofList [
                DefaultSemantic.Colors,       normalAtt
                outline1,                     colorAtt
                DefaultSemantic.DepthStencil, depthAtt
            ])

        let renderObjects, _ = node.GetObjects(TraversalState.empty runtime)

        let task = runtime.CompileRender(signature, renderObjects)
        let clr = clear { color C4f.Black; depth 1.0 }
        let output = task |> RenderTask.renderToWithClear fbo clr
        let gNormal = output.GetOutputTexture DefaultSemantic.Colors
        let gColor  = output.GetOutputTexture outline1

        let texel =
            size |> AVal.map (fun (s : V2i) ->
                V2f(1.0f / float32 (max 1 s.X), 1.0f / float32 (max 1 s.Y)))

        let composite =
            sg {
                // NoEvents is load-bearing: pickable nodes write (id, gl_FragCoord.z)
                // into the pick attachment with blending OFF — a fullscreen quad at
                // NDC z=0 would stamp depth 0.5 over every pick pixel (its screen
                // alpha is irrelevant there), breaking every GPU pick in the view.
                Sg.NoEvents
                Sg.DepthTest (AVal.constant DepthTest.None)
                Sg.BlendMode (AVal.constant BlendMode.Blend)
                Sg.Shader {
                    OutlineEdge.vertex
                    OutlineEdge.fragment
                }
                Sg.Uniform("GNormal", gNormal)
                Sg.Uniform("GColor", gColor)
                Sg.Uniform("OutlineTexel", texel)
                Sg.Uniform("OutlineThreshold", thresholdA)
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

    let build
        (info : Aardvark.Dom.RenderControlInfo)
        (model : AdaptiveModel)
        (view : aval<Trafo3d>)
        (proj : aval<Trafo3d>) : aset<ISceneNode> =
        buildFromNode info (model.OutlineThreshold |> AVal.map float32) (MeshView.buildOutlineNode model view proj)
