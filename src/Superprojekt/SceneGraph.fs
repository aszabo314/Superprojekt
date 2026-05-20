namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open Aardworx.WebAssembly
open FSharp.Data.Adaptive
open Aardvark.Dom
open FShade

module SceneGraph =

    let private boxPos =
        [|  V3f(-0.5f, -0.5f, -0.5f); V3f( 0.5f, -0.5f, -0.5f); V3f( 0.5f,  0.5f, -0.5f); V3f(-0.5f,  0.5f, -0.5f)
            V3f(-0.5f, -0.5f,  0.5f); V3f( 0.5f, -0.5f,  0.5f); V3f( 0.5f,  0.5f,  0.5f); V3f(-0.5f,  0.5f,  0.5f) |]
    let private boxIdx =
        [| 0;1;2; 0;2;3;  5;4;7; 5;7;6;  4;0;3; 4;3;7;  1;5;6; 1;6;2;  0;4;5; 0;5;1;  3;2;6; 3;6;7 |]

    let private axisBox (color : V4d) (trafo : Trafo3d) =
        sg {
            Sg.Trafo (AVal.constant trafo)
            Sg.Shader { DefaultSurfaces.trafo; Shader.flatColor; OIT.weightedBlend }
            Sg.Uniform("FlatColor", AVal.constant (V4f color))
            Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
            Sg.NoEvents
            Sg.VertexAttributes(
                HashMap.ofList [ string DefaultSemantic.Positions, BufferView(AVal.constant (ArrayBuffer boxPos :> IBuffer), typeof<V3f>) ]
            )
            Sg.Index(BufferView(AVal.constant (ArrayBuffer boxIdx :> IBuffer), typeof<int>))
            Sg.Render (AVal.constant boxIdx.Length)
        }

    // Origin indicator: 3 axes + tick marks. (Text labels are dropped — Sg.Text
    // uses its own internal shader that can't be composed with OIT.weightedBlend.
    // TODO: re-add labels via a post-OIT overlay pass if needed.)
    let private originIndicator (view : aval<Trafo3d>) (proj : aval<Trafo3d>) (active : aval<bool>) =
        let axisLength = 3.0
        let tickSpacing = 0.25
        let tickLen = 0.12

        let xColor = V4d(0.82, 0.15, 0.1, 1.0)
        let yColor = V4d(0.1, 0.72, 0.1, 1.0)
        let zColor = V4d(0.15, 0.35, 0.9, 1.0)

        let tickSegs (color : V4d) (dir : V3d) (perpA : V3d) =
            let n = int (axisLength / tickSpacing)
            let half = perpA * (tickLen * 0.5)
            [| for i in 1 .. n do
                let center = dir * (float i * tickSpacing)
                yield center - half, center + half, color, 1.5 |]

        let allLineSegs =
            AVal.constant (Array.concat [
                [| V3d.Zero, V3d.IOO * axisLength, xColor, 2.0
                   V3d.Zero, V3d.OIO * axisLength, yColor, 2.0
                   V3d.Zero, V3d.OOI * axisLength, zColor, 2.0 |]
                tickSegs xColor V3d.IOO V3d.OOI
                tickSegs yColor V3d.OIO V3d.IOO
                tickSegs zColor V3d.OOI V3d.IOO
            ])

        ASet.ofList [
            sg { Sg.Active active; Sg.View view; Sg.Proj proj; axisBox (V4d(0.88, 0.88, 0.88, 1.0)) (Trafo3d.Scale 0.08) }
            sg { Sg.Active active; Sg.View view; Sg.Proj proj; Lines.render allLineSegs }
        ]

    let build
        (env : Env<Message>)
        (info : Aardvark.Dom.RenderControlInfo)
        (view : aval<Trafo3d>)
        (proj : aval<Trafo3d>)
        (fullscreenActive : aval<bool>)
        (placementHover : aval<V3d option>)
        (model : AdaptiveModel) =

        let loadFinished (name : string) =
            env.Emit [ LoadFinished name ]

        // ---- Render pipeline:
        //   1. Depth pre-pass: only fully-opaque (enabled) meshes write depth into
        //      the shared depth attachment. The OIT pass then depth-tests against
        //      this without writing depth itself — that's the only way WBOIT can
        //      give correct occlusion. Without the pre-pass, two opaque fragments
        //      at the same pixel both contribute to Accum (back-most can still
        //      leak ~10% through the depth-based weight), producing the "missing
        //      depth occlusion" artifact.
        //   2. OIT pass: all visible 3D objects (incl. ghost meshes, pins, axes)
        //      emit weighted Accum + Revealage MRT outputs; depth-test against (1),
        //      depth-write OFF.
        //   3. Compose pass (a fullscreen quad as an ASet leaf): sample Accum +
        //      Revealage and alpha-blend (baseColor, density) over the screen.

        let oitSize = info.ViewportSize

        let accumTex     = info.Runtime.CreateTexture2D(oitSize, TextureFormat.Rgba16f, 1, 1)
        let revealageTex = info.Runtime.CreateTexture2D(oitSize, TextureFormat.Rgba8,   1, 1)
        let depthTex     = info.Runtime.CreateTexture2D(oitSize, TextureFormat.Depth24Stencil8, 1, 1)

        let prepassSig =
            info.Runtime.CreateFramebufferSignature [
                DefaultSemantic.DepthStencil, TextureFormat.Depth24Stencil8
            ]
        let oitSig =
            info.Runtime.CreateFramebufferSignature [
                OIT.AccumSemantic,            TextureFormat.Rgba16f
                OIT.RevealageSemantic,        TextureFormat.Rgba8
                DefaultSemantic.DepthStencil, TextureFormat.Depth24Stencil8
            ]

        let prepassFbo =
            depthTex |> AdaptiveResource.bind (fun (d : IBackendTexture) ->
                AVal.constant (
                    info.Runtime.CreateFramebuffer(
                        prepassSig,
                        [ DefaultSemantic.DepthStencil, d.[TextureAspect.DepthStencil, 0, 0] :> IFramebufferOutput ]
                    )))

        let oitFbo =
            (accumTex, revealageTex, depthTex) |||> AdaptiveResource.bind3 (fun (a : IBackendTexture) (r : IBackendTexture) (d : IBackendTexture) ->
                AVal.constant (
                    info.Runtime.CreateFramebuffer(
                        oitSig,
                        [
                            OIT.AccumSemantic,            a.[TextureAspect.Color, 0, 0] :> IFramebufferOutput
                            OIT.RevealageSemantic,        r.[TextureAspect.Color, 0, 0] :> IFramebufferOutput
                            DefaultSemantic.DepthStencil, d.[TextureAspect.DepthStencil, 0, 0] :> IFramebufferOutput
                        ])))

        // Pre-pass scene: only enabled meshes, trafo + depth-only fragment.
        let prepassScene =
            sg {
                Sg.View view
                Sg.Proj proj
                Sg.DepthMask (AVal.constant true)
                Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
                Sg.Uniform("ViewportSize", info.ViewportSize)
                MeshView.buildOpaqueDepthScene loadFinished model
            }
        let prepassTask =
            info.Runtime.CompileRender(prepassSig, prepassScene.GetRenderObjects(TraversalState.empty info.Runtime))
        let prepassClear =
            info.Runtime.CompileClear(prepassSig, clear { depth 1.0; stencil 0 })

        // OIT scene: every 3D leaf emits MRT to Accum + Revealage. Depth-write OFF
        // so opaque meshes don't pollute the depth buffer the pre-pass populated.
        let meshScene = MeshView.buildScene loadFinished model
        let indicatorNodes = originIndicator view proj (AVal.map not fullscreenActive)
        let pinScene = ScanPinScene.build env view proj fullscreenActive placementHover model

        let oitContent = ASet.unionMany (ASet.ofList [ meshScene; indicatorNodes; pinScene ])

        let oitScene =
            sg {
                Sg.View view
                Sg.Proj proj
                Sg.DepthMask (AVal.constant false)
                Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
                Sg.BlendMode(OIT.AccumSemantic,     AVal.constant BlendMode.Add)
                Sg.BlendMode(OIT.RevealageSemantic, AVal.constant OIT.revealageBlendMode)
                Sg.Uniform("ViewportSize", info.ViewportSize)
                oitContent
            }
        let oitTask =
            info.Runtime.CompileRender(oitSig, oitScene.GetRenderObjects(TraversalState.empty info.Runtime))
        let oitClear =
            info.Runtime.CompileClear(
                oitSig,
                clear {
                    colors [
                        OIT.AccumSemantic,     C4f.Zero
                        OIT.RevealageSemantic, C4f.White
                    ]
                })

        // Drive the offscreen tasks via an AVal.custom that returns the resolved
        // Accum + Revealage textures.
        let oitOutputs =
            (accumTex, revealageTex)
            ||> AdaptiveResource.bind2 (fun (a : IBackendTexture) (r : IBackendTexture) ->
                prepassFbo |> AVal.bind (fun pFbo ->
                    oitFbo |> AVal.bind (fun oFbo ->
                        AVal.custom (fun tok ->
                            prepassClear.Run(tok, RenderToken.Empty, pFbo)
                            prepassTask.Run (tok, RenderToken.Empty, pFbo)
                            oitClear.Run    (tok, RenderToken.Empty, oFbo)
                            oitTask.Run     (tok, RenderToken.Empty, oFbo)
                            a, r))))
        let accumOut     = oitOutputs |> AVal.map fst |> AdaptiveResource.map (fun t -> t :> ITexture)
        let revealageOut = oitOutputs |> AVal.map snd |> AdaptiveResource.map (fun t -> t :> ITexture)

        // Compose pass — a fullscreen quad written into the main framebuffer using
        // straight alpha blending of (baseColor, density) over the gradient.
        let quadPos =
            AVal.constant (ArrayBuffer [|
                V3f(-1.0f, -1.0f, 0.0f); V3f( 1.0f, -1.0f, 0.0f)
                V3f( 1.0f,  1.0f, 0.0f); V3f(-1.0f,  1.0f, 0.0f)
            |] :> IBuffer)
        let quadTc =
            AVal.constant (ArrayBuffer [|
                V2f(0.0f, 0.0f); V2f(1.0f, 0.0f)
                V2f(1.0f, 1.0f); V2f(0.0f, 1.0f)
            |] :> IBuffer)
        let quadIdx = AVal.constant (ArrayBuffer [| 0; 1; 2; 0; 2; 3 |] :> IBuffer)

        let composeNode =
            sg {
                Sg.Shader { DefaultSurfaces.trafo; OIT.compose }
                Sg.Uniform("AccumTexture",     accumOut)
                Sg.Uniform("RevealageTexture", revealageOut)
                Sg.Uniform("DepthTexture", depthTex)
                Sg.BlendMode (AVal.constant BlendMode.Blend)
                Sg.DepthTest (AVal.constant DepthTest.None)
                Sg.View Trafo3d.Identity
                Sg.Proj Trafo3d.Identity
                Sg.VertexAttributes(
                    HashMap.ofList [
                        string DefaultSemantic.Positions,               BufferView(quadPos, typeof<V3f>)
                        string DefaultSemantic.DiffuseColorCoordinates, BufferView(quadTc,  typeof<V2f>)
                    ]
                )
                Sg.Index(BufferView(quadIdx, typeof<int>))
                Sg.Render (AVal.constant 6)
            }

        ASet.single composeNode
