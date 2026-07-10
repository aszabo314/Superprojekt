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
    let private coverage1 = Sym.ofString "Coverage1"

    // Occlusion-free per-mesh footprint contours (§outline per-mesh): an additive
    // coverage MRT (one channel per mesh, no depth) + a fullscreen composite that
    // outlines every channel's covered↔uncovered transition in that mesh's palette
    // colour — each mesh keeps its complete own contour even where the combined
    // (depth-tested) G-buffer sees only the union.
    let private buildCoverage
        (info : Aardvark.Dom.RenderControlInfo)
        (mask : aval<V4f[]>)
        (node : ISceneNode) : aset<ISceneNode> =

        let runtime = info.Runtime
        let size = info.ViewportSize

        let signature =
            runtime.CreateFramebufferSignature([
                DefaultSemantic.Colors, TextureFormat.Rgba8
                coverage1,              TextureFormat.Rgba8
            ])
        let att0 = runtime.CreateTextureAttachment(runtime.CreateTexture2D(size, TextureFormat.Rgba8))
        let att1 = runtime.CreateTextureAttachment(runtime.CreateTexture2D(size, TextureFormat.Rgba8))
        let fbo =
            runtime.CreateFramebuffer(signature, Map.ofList [
                DefaultSemantic.Colors, att0
                coverage1,              att1
            ])

        let renderObjects, _ = node.GetObjects(TraversalState.empty runtime)
        let task = runtime.CompileRender(signature, renderObjects)
        let clr = clear { color C4f.Black }
        let output = task |> RenderTask.renderToWithClear fbo clr
        let cov0 = output.GetOutputTexture DefaultSemantic.Colors
        let cov1 = output.GetOutputTexture coverage1

        let texel =
            size |> AVal.map (fun (s : V2i) ->
                V2f(1.0f / float32 (max 1 s.X), 1.0f / float32 (max 1 s.Y)))

        let composite =
            sg {
                // NoEvents is load-bearing — see the main composite below.
                Sg.NoEvents
                Sg.DepthTest (AVal.constant DepthTest.None)
                Sg.BlendMode (AVal.constant BlendMode.Blend)
                Sg.Shader {
                    OutlineEdge.vertex
                    OutlineCoverageEdge.fragment
                }
                Sg.Uniform("Coverage0", cov0)
                Sg.Uniform("Coverage1", cov1)
                Sg.Uniform("OutlineTexel", texel)
                Sg.Uniform("OutlineMask", mask)
                Sg.Uniform("CoverageColors", AVal.constant MeshView.coverageColors)
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

    // A mask that gates nothing (every slot = full lines) — for callers without
    // per-mesh outline state (the focus single's reference overlay).
    let maskAllOn : aval<V4f[]> =
        AVal.constant (Array.create 32 (V4f(1.0f, 0.0f, 0.0f, 0.0f)))

    // Offscreen G-buffer + fullscreen edge-detect composite for an arbitrary outline
    // node (already carrying its own View/Proj/Trafo). Shared by the main-view all-mesh
    // outline and the focus single's reference-mesh silhouette overlay. The composite is
    // DepthTest.None → it draws on top of whatever preceded it in the framebuffer.
    // `mask` = the per-mesh line gate, indexed by the G-buffer mesh id (MeshView.outlineMask).
    let buildFromNode
        (info : Aardvark.Dom.RenderControlInfo)
        (thresholdA : aval<float32>)
        (mask : aval<V4f[]>)
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
                Sg.Uniform("OutlineMask", mask)
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
        let mask = MeshView.outlineMask model
        let combined =
            buildFromNode info (model.OutlineThreshold |> AVal.map float32)
                mask (MeshView.buildOutlineNode model view proj)
        let footprints = buildCoverage info mask (MeshView.buildCoverageNode model view proj)
        ASet.union combined footprints
