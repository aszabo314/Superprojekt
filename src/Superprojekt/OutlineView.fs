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
    let private quadAttrs =
        HashMap.ofList [
            string DefaultSemantic.Positions,
                BufferView(AVal.constant (ArrayBuffer quadPos :> IBuffer), typeof<V3f>)
        ]
    let private quadIdxView = BufferView(AVal.constant (ArrayBuffer quadIdx :> IBuffer), typeof<int>)

    let private outline1 = Sym.ofString "Outline1"
    let private coverage1 = Sym.ofString "Coverage1"

    // Offscreen scaffold shared by the G-buffer and coverage passes: render `node`
    // into a fresh two-colour-target FBO (+ optional depth), returning the two
    // output textures and the texel size for the edge composites.
    let private renderOffscreen
        (info : Aardvark.Dom.RenderControlInfo)
        (target1 : Symbol) (withDepth : bool)
        (node : ISceneNode) =
        let runtime = info.Runtime
        let size = info.ViewportSize
        let atts =
            [ yield DefaultSemantic.Colors, TextureFormat.Rgba8
              yield target1,                TextureFormat.Rgba8
              if withDepth then yield DefaultSemantic.DepthStencil, TextureFormat.Depth24Stencil8 ]
        let signature = runtime.CreateFramebufferSignature atts
        let fbo =
            runtime.CreateFramebuffer(signature,
                atts
                |> List.map (fun (sem, fmt) -> sem, runtime.CreateTextureAttachment(runtime.CreateTexture2D(size, fmt)))
                |> Map.ofList)
        let renderObjects, _ = node.GetObjects(TraversalState.empty runtime)
        let task = runtime.CompileRender(signature, renderObjects)
        let clr = if withDepth then clear { color C4f.Black; depth 1.0 } else clear { color C4f.Black }
        let output = task |> RenderTask.renderToWithClear fbo clr
        let texel =
            size |> AVal.map (fun (s : V2i) ->
                V2f(1.0f / float32 (max 1 s.X), 1.0f / float32 (max 1 s.Y)))
        output.GetOutputTexture DefaultSemantic.Colors, output.GetOutputTexture target1, texel

    // Occlusion-free per-mesh footprint contours (§outline per-mesh): an additive
    // coverage MRT (one channel per mesh, no depth) + a fullscreen composite that
    // outlines every channel's covered↔uncovered transition in that mesh's palette
    // colour — each mesh keeps its complete own contour even where the combined
    // (depth-tested) G-buffer sees only the union.
    let private buildCoverage
        (info : Aardvark.Dom.RenderControlInfo)
        (mask : aval<V4f[]>)
        (node : ISceneNode) : aset<ISceneNode> =

        let cov0, cov1, texel = renderOffscreen info coverage1 false node
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
                Sg.VertexAttributes quadAttrs
                Sg.Index quadIdxView
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
        (isoOpacityA : aval<float32>)
        (distFadeA : aval<float32>)
        (mask : aval<V4f[]>)
        (node : ISceneNode) : aset<ISceneNode> =

        let gNormal, gColor, texel = renderOffscreen info outline1 true node
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
                Sg.Uniform("IsolineOpacity", isoOpacityA)
                Sg.Uniform("OutlineDistFade", distFadeA)
                Sg.Uniform("OutlineMask", mask)
                Sg.VertexAttributes quadAttrs
                Sg.Index quadIdxView
                Sg.Render (AVal.constant quadIdx.Length)
            }

        ASet.single composite

    // Fused placement-suitability overlay (v12 §2): the shape-weighted coverage
    // MRT → one fullscreen composite (transparent / flat grey / mesh-colour
    // hatch, see SuitabilityComposite). Composite active only while a placement
    // is armed; drawn BEFORE the outline composites in SceneGraph, so isolines
    // and footprint contours stay readable on top of it.
    let buildSuitability
        (info : Aardvark.Dom.RenderControlInfo)
        (model : AdaptiveModel)
        (view : aval<Trafo3d>)
        (proj : aval<Trafo3d>) : aset<ISceneNode> =
        let suit0, suit1, _ = renderOffscreen info coverage1 false (MeshView.buildSuitabilityNode model view proj)
        let active =
            model.ScanPins.Placement |> AVal.map (function AnchorPlacement -> true | _ -> false)
        let composite =
            sg {
                // NoEvents is load-bearing — see the main composite above.
                Sg.NoEvents
                Sg.Active active
                Sg.DepthTest (AVal.constant DepthTest.None)
                Sg.BlendMode (AVal.constant BlendMode.Blend)
                Sg.Shader {
                    OutlineEdge.vertex
                    SuitabilityComposite.fragment
                }
                Sg.Uniform("Suit0", suit0)
                Sg.Uniform("Suit1", suit1)
                Sg.Uniform("CoverageColors", AVal.constant MeshView.coverageColors)
                Sg.VertexAttributes quadAttrs
                Sg.Index quadIdxView
                Sg.Render (AVal.constant quadIdx.Length)
            }
        ASet.single composite

    let build
        (info : Aardvark.Dom.RenderControlInfo)
        (model : AdaptiveModel)
        (view : aval<Trafo3d>)
        (proj : aval<Trafo3d>) : aset<ISceneNode> =
        let mask = MeshView.outlineMask model
        // Slice-mode line falloff (v12 §5 follow-up): the SAME small window as
        // the mesh surface fade (SliceCam.FadeDist, a few cm) — outlines and
        // isolines vanish with the fill. depth01 → distance is linear under the
        // slice ortho. 0 disables (perspective).
        let distFade =
            MeshView.sliceCamera model |> AVal.map (function
                | Some s -> float32 ((s.Far - s.Near) / max 1e-9 s.FadeDist)
                | None -> 0.0f)
        let combined =
            buildFromNode info (model.OutlineThreshold |> AVal.map float32)
                (model.IsolineOpacity |> AVal.map float32)
                distFade
                mask (MeshView.buildOutlineNode model view proj)
        let footprints = buildCoverage info mask (MeshView.buildCoverageNode model view proj)
        ASet.union combined footprints
