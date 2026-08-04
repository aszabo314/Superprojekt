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
        // The clear reaches EVERY colour attachment, and C4f.Black has ALPHA 1 —
        // the coverage composites read the alpha channels as mesh channels 3/7,
        // so a black clear plants two phantom "covered" meshes on every
        // uncovered pixel. The depth-free coverage passes must clear to
        // transparent zero.
        let clr =
            if withDepth then clear { color C4f.Black; depth 1.0 }
            else clear { color (C4f(0.0f, 0.0f, 0.0f, 0.0f)) }
        let output = task |> RenderTask.renderToWithClear fbo clr
        let texel =
            size |> AVal.map (fun (s : V2i) ->
                V2f(1.0f / float32 (max 1 s.X), 1.0f / float32 (max 1 s.Y)))
        output.GetOutputTexture DefaultSemantic.Colors, output.GetOutputTexture target1, texel

    // The footprint coverage MRT, rendered ONCE per frame and shared: the
    // coverage-edge composite outlines it, and the main mesh pass samples it
    // for the matrix-hover overlap preview. `active` gates the pass (per-tile
    // MRTs render only while their overlap gate reads them).
    let coverageOffscreen
        (info : Aardvark.Dom.RenderControlInfo)
        (model : AdaptiveModel)
        (active : aval<bool>)
        (view : aval<Trafo3d>)
        (proj : aval<Trafo3d>) =
        let c0, c1, texel = renderOffscreen info coverage1 false (MeshView.buildCoverageNode model active view proj)
        c0 :> aval<IBackendTexture>, c1 :> aval<IBackendTexture>, texel

    // Per-tile ROOT footprint MRT (channel 0 only, from the tile's camera) —
    // the source of the strips' gold reference overlay.
    let rootCoverageOffscreen
        (info : Aardvark.Dom.RenderControlInfo)
        (model : AdaptiveModel)
        (active : aval<bool>)
        (view : aval<Trafo3d>)
        (proj : aval<Trafo3d>) =
        let c0, c1, texel =
            renderOffscreen info coverage1 false (MeshView.buildRootCoverageNode model active view proj)
        c0 :> aval<IBackendTexture>, c1 :> aval<IBackendTexture>, texel

    // The gold on-top composite of the root coverage: channel 0's covered↔
    // uncovered transition in the reference gold, DepthTest.None in passOne —
    // unobscured by whatever the tile renders beneath.
    let buildRootOutline
        (active : aval<bool>)
        (widthA : aval<float32>)
        (cov0 : aval<IBackendTexture>)
        (cov1 : aval<IBackendTexture>)
        (texel : aval<V2f>) : ISceneNode =
        let mask = AVal.constant (Array.init 32 (fun i -> if i = 0 then V4f.IOOO else V4f.Zero))
        let colors =
            AVal.constant (Array.init 8 (fun i ->
                if i = 0 then V4f(V3f Primitives.refGoldV3d, 1.0f) else V4f.Zero))
        sg {
            Sg.Active active
            // NoEvents is load-bearing — see the main composite below.
            Sg.NoEvents
            Sg.Pass RenderPass.passOne
            Sg.DepthTest (AVal.constant DepthTest.None)
            Sg.BlendMode (AVal.constant BlendMode.Blend)
            Sg.Shader {
                OutlineEdge.vertex
                OutlineCoverageEdge.fragment
            }
            Sg.Uniform("Coverage0", cov0)
            Sg.Uniform("Coverage1", cov1)
            Sg.Uniform("OutlineTexel", texel)
            Sg.Uniform("OutlineWidthPx", widthA)
            Sg.Uniform("OutlineMask", mask)
            Sg.Uniform("CoverageColors", colors)
            Sg.VertexAttributes quadAttrs
            Sg.Index quadIdxView
            Sg.Render (AVal.constant quadIdx.Length)
        }

    // Occlusion-free per-mesh footprint contours: an additive
    // coverage MRT (one channel per mesh, no depth) + a fullscreen composite that
    // outlines every channel's covered↔uncovered transition in that mesh's palette
    // colour — each mesh keeps its complete own contour even where the combined
    // (depth-tested) G-buffer sees only the union.
    let private buildCoverage
        (widthA : aval<float32>)
        (mask : aval<V4f[]>)
        (colors : aval<V4f[]>)
        (cov0 : aval<IBackendTexture>)
        (cov1 : aval<IBackendTexture>)
        (texel : aval<V2f>) : aset<ISceneNode> =

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
                Sg.Uniform("OutlineWidthPx", widthA)
                Sg.Uniform("OutlineMask", mask)
                Sg.Uniform("CoverageColors", colors)
                Sg.VertexAttributes quadAttrs
                Sg.Index quadIdxView
                Sg.Render (AVal.constant quadIdx.Length)
            }

        ASet.single composite

    // Offscreen G-buffer + fullscreen edge-detect composite for an arbitrary outline
    // node (already carrying its own View/Proj/Trafo). The composite is
    // DepthTest.None → it draws on top of whatever preceded it in the framebuffer.
    // `mask` = the per-mesh line gate, indexed by the G-buffer mesh id (MeshView.outlineMask).
    let buildFromNode
        (info : Aardvark.Dom.RenderControlInfo)
        (thresholdA : aval<float32>)
        (widthA : aval<float32>)
        (isoOpacityA : aval<float32>)
        (mask : aval<V4f[]>)
        (greyA : aval<float32>)
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
                Sg.Uniform("OutlineWidthPx", widthA)
                Sg.Uniform("IsolineOpacity", isoOpacityA)
                Sg.Uniform("OutlineMask", mask)
                Sg.Uniform("OutlineGrey", greyA)
                Sg.VertexAttributes quadAttrs
                Sg.Index quadIdxView
                Sg.Render (AVal.constant quadIdx.Length)
            }

        ASet.single composite

    let build
        (info : Aardvark.Dom.RenderControlInfo)
        (model : AdaptiveModel)
        (view : aval<Trafo3d>)
        (proj : aval<Trafo3d>)
        (cov0 : aval<IBackendTexture>, cov1 : aval<IBackendTexture>, covTexel : aval<V2f>) : aset<ISceneNode> =
        let mask = MeshView.outlineMask model
        let widthA = model.OutlineWidthPx |> AVal.map float32
        // Error-map isolation: silhouettes drop to luminance grey.
        let greyA =
            AVal.custom (fun t -> if MeshView.mapIsolationAt model t then 1.0f else 0.0f)
        let combined =
            buildFromNode info (model.OutlineThreshold |> AVal.map float32)
                widthA
                (model.IsolineOpacity |> AVal.map float32)
                mask greyA (MeshView.buildOutlineNode model view proj)
        let footprints =
            buildCoverage widthA (MeshView.footprintMask model) (MeshView.coverageColorsA model)
                cov0 cov1 covTexel
        ASet.union combined footprints
