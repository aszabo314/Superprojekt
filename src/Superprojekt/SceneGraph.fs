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

    // Forward-shaded version of axisBox — emits a regular Color attachment for
    // the post-OIT overlay (passOne) instead of WBOIT MRT outputs.
    let private axisBoxForward (color : V4d) (trafo : Trafo3d) =
        sg {
            Sg.Trafo (AVal.constant trafo)
            Sg.Shader { DefaultSurfaces.trafo; Shader.flatColor }
            Sg.Uniform("FlatColor", AVal.constant (V4f color))
            Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
            Sg.NoEvents
            Sg.VertexAttributes(
                HashMap.ofList [ string DefaultSemantic.Positions, BufferView(AVal.constant (ArrayBuffer boxPos :> IBuffer), typeof<V3f>) ]
            )
            Sg.Index(BufferView(AVal.constant (ArrayBuffer boxIdx :> IBuffer), typeof<int>))
            Sg.Render (AVal.constant boxIdx.Length)
        }

    // Origin indicator: 3 axes + tick marks. Rendered as a forward overlay on
    // RenderPass.passOne (post-OIT compose). The OIT pass washes thin lines
    // out — WBOIT averages line color with any overlapping mesh fragment at
    // the same pixel, which muddies / hides the cross — so the coordinate
    // widget renders straight into the main framebuffer with depth-test
    // against the mesh depth that compose wrote.
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
            sg { Sg.Active active; Sg.View view; Sg.Proj proj
                 Sg.Pass RenderPass.passOne
                 axisBoxForward (V4d(0.88, 0.88, 0.88, 1.0)) (Trafo3d.Scale 0.08) }
            sg { Sg.Active active; Sg.View view; Sg.Proj proj
                 Sg.Pass RenderPass.passOne
                 Lines.renderForward allLineSegs }
        ]

    // Origin tick + tip text labels. Runs on RenderPass.passOne — i.e. AFTER the
    // OIT compose quad writes its color + (pre-pass) depth into the main FB —
    // so the labels depth-test against mesh depth just like in the old forward
    // pipeline. Sg.Text's internal shader is incompatible with OIT MRT, hence
    // this separate forward overlay.
    let private originLabels (view : aval<Trafo3d>) (proj : aval<Trafo3d>) (active : aval<bool>) =
        let axisLength = 3.0
        let tickSpacing = 0.25
        let tickLen = 0.12
        let labelSize = 0.15

        let xColor = V4d(0.82, 0.15, 0.1, 1.0)
        let yColor = V4d(0.1, 0.72, 0.1, 1.0)
        let zColor = V4d(0.15, 0.35, 0.9, 1.0)

        let toC4b (c : V4d) = C4b(byte(c.X*255.0), byte(c.Y*255.0), byte(c.Z*255.0))
        let darken (c : V4d) = toC4b (V4d(c.X * 0.55, c.Y * 0.55, c.Z * 0.55, 1.0))

        // Each axis' tick labels are written in a plane whose first basis is the
        // axis direction. The text is rotated so its baseline aligns with that
        // direction and faces outward along the chosen perpendicular.
        let textTrafoX = Trafo3d.RotationX(Constant.PiHalf)
        let textTrafoY = Trafo3d.RotationX(Constant.PiHalf) * Trafo3d.RotationZ(Constant.PiHalf)
        let textTrafoZ = Trafo3d.RotationX(Constant.PiHalf)

        let labelNodes (color : V4d) (dir : V3d) (perpA : V3d) (textRot : Trafo3d) =
            let n = int (axisLength / tickSpacing)
            let textColor = darken color
            [ for i in 1 .. n do
                // Every 4th tick → integer-metre labels (spacing 0.25 → label every 1.0).
                if i % 4 = 0 then
                    let dist = float i * tickSpacing
                    let center = dir * dist
                    let labelPos = center + perpA * (tickLen * 0.5 + labelSize * 1.2)
                    let trafo = Trafo3d.Scale(labelSize) * textRot * Trafo3d.Translation(labelPos)
                    yield sg {
                        Sg.Active active; Sg.View view; Sg.Proj proj
                        Sg.Pass RenderPass.passOne
                        Sg.Trafo (AVal.constant trafo)
                        Sg.Text(sprintf "%.0f" dist, color = AVal.constant textColor, align = TextAlignment.Center)
                    } ]

        let tipOffset = axisLength + labelSize * 1.5
        let tipNodes =
            [ sg { Sg.Active active; Sg.View view; Sg.Proj proj
                   Sg.Pass RenderPass.passOne
                   Sg.Trafo (AVal.constant (Trafo3d.Scale(labelSize * 1.5) * textTrafoX * Trafo3d.Translation(V3d.IOO * tipOffset)))
                   Sg.Text("X", color = AVal.constant (darken xColor), align = TextAlignment.Center) }
              sg { Sg.Active active; Sg.View view; Sg.Proj proj
                   Sg.Pass RenderPass.passOne
                   Sg.Trafo (AVal.constant (Trafo3d.Scale(labelSize * 1.5) * textTrafoY * Trafo3d.Translation(V3d.OIO * tipOffset)))
                   Sg.Text("Y", color = AVal.constant (darken yColor), align = TextAlignment.Center) }
              sg { Sg.Active active; Sg.View view; Sg.Proj proj
                   Sg.Pass RenderPass.passOne
                   Sg.Trafo (AVal.constant (Trafo3d.Scale(labelSize * 1.5) * textTrafoZ * Trafo3d.Translation(V3d.OOI * tipOffset)))
                   Sg.Text("Z", color = AVal.constant (darken zColor), align = TextAlignment.Center) } ]

        ASet.ofList (
            tipNodes
            @ labelNodes xColor V3d.IOO V3d.OOI textTrafoX
            @ labelNodes yColor V3d.OIO V3d.IOO textTrafoY
            @ labelNodes zColor V3d.OOI V3d.IOO textTrafoZ)

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
        // The coordinate-cross indicator is NOT in here — it renders on passOne
        // as a forward overlay (see `indicatorNodes` below). WBOIT thin-line
        // washout would otherwise make the cross hard / impossible to see.
        let meshScene = MeshView.buildScene loadFinished model
        let pinScene = ScanPinScene.build env view proj fullscreenActive placementHover model

        let oitContent = ASet.unionMany (ASet.ofList [ meshScene; pinScene ])

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

        // Coordinate cross + labels: forward overlay on passOne. Runs after
        // the compose quad writes its color + (OIT-prepass) depth into the main
        // framebuffer, so the widget depth-tests against the opaque-mesh depth
        // exactly like the old forward pipeline.
        let indicatorNodes = originIndicator view proj (AVal.map not fullscreenActive)
        let labelNodes     = originLabels    view proj (AVal.map not fullscreenActive)

        ASet.unionMany (ASet.ofList [ ASet.single composeNode; indicatorNodes; labelNodes ])
