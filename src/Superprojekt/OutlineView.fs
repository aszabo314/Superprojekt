namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open Aardworx.WebAssembly
open FSharp.Data.Adaptive
open Aardvark.Dom
open FShade

// §10 per-mesh image-space outlines. Offscreen G-buffer pass (world normal +
// depth → target0, palette colour + mask → target1) then a fullscreen
// edge-detect composite that paints each mesh's outline in its palette colour,
// including the near-plane cut (mask boundary). Gated on OutlineMode — when off
// the composite is inactive and the offscreen task never runs (lazy), so it can
// never regress the main forward pass. Replaces the opacity-ghost as the body
// identity cue when enabled. The at-most-two-WebGL-controls rule is unaffected:
// this is an extra offscreen render target on the main control, not a 3rd one.
module OutlineView =

    let private quadPos =
        [| V3f(-1.0f, -1.0f, 0.0f); V3f(1.0f, -1.0f, 0.0f)
           V3f( 1.0f,  1.0f, 0.0f); V3f(-1.0f, 1.0f, 0.0f) |]
    let private quadIdx = [| 0; 1; 2; 0; 2; 3 |]

    let private outline1 = Sym.ofString "Outline1"

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

        let node = MeshView.buildOutlineNode model view proj
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
                Sg.Active model.OutlineMode
                Sg.DepthTest (AVal.constant DepthTest.None)
                Sg.BlendMode (AVal.constant BlendMode.Blend)
                Sg.Shader {
                    OutlineEdge.vertex
                    OutlineEdge.fragment
                }
                Sg.Uniform("GNormal", gNormal)
                Sg.Uniform("GColor", gColor)
                Sg.Uniform("OutlineTexel", texel)
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
