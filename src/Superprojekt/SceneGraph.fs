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
    // the post-OIT overlay (passOne) instead of WBOIT MRT outputs. Renders as
    // always-on-top (DepthTest.None): the unified WBOIT pipeline has no main-FB
    // depth attachment for the widget to test against.
    let private axisBoxForward (color : V4d) (trafo : Trafo3d) =
        sg {
            Sg.Trafo (AVal.constant trafo)
            Sg.Shader { DefaultSurfaces.trafo; Shader.flatColor }
            Sg.Uniform("FlatColor", AVal.constant (V4f color))
            Sg.DepthTest (AVal.constant DepthTest.None)
            Sg.NoEvents
            Sg.VertexAttributes(
                HashMap.ofList [ string DefaultSemantic.Positions, BufferView(AVal.constant (ArrayBuffer boxPos :> IBuffer), typeof<V3f>) ]
            )
            Sg.Index(BufferView(AVal.constant (ArrayBuffer boxIdx :> IBuffer), typeof<int>))
            Sg.Render (AVal.constant boxIdx.Length)
        }

    // Origin indicator: 3 axes + tick marks. Rendered as a forward overlay on
    // RenderPass.passOne (post-OIT compose). Always-on-top now that the unified
    // WBOIT pipeline no longer maintains a main-FB depth attachment — typical
    // behaviour for a world-origin gizmo in CAD-style tools.
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
                 Sg.DepthTest (AVal.constant DepthTest.None)
                 axisBoxForward (V4d(0.88, 0.88, 0.88, 1.0)) (Trafo3d.Scale 0.08) }
            sg { Sg.Active active; Sg.View view; Sg.Proj proj
                 Sg.Pass RenderPass.passOne
                 Sg.DepthTest (AVal.constant DepthTest.None)
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
                        Sg.DepthTest (AVal.constant DepthTest.None)
                        Sg.Trafo (AVal.constant trafo)
                        Sg.Text(sprintf "%.0f" dist, color = AVal.constant textColor, align = TextAlignment.Center)
                    } ]

        let tipOffset = axisLength + labelSize * 1.5
        let tipNodes =
            [ sg { Sg.Active active; Sg.View view; Sg.Proj proj
                   Sg.Pass RenderPass.passOne
                   Sg.DepthTest (AVal.constant DepthTest.None)
                   Sg.Trafo (AVal.constant (Trafo3d.Scale(labelSize * 1.5) * textTrafoX * Trafo3d.Translation(V3d.IOO * tipOffset)))
                   Sg.Text("X", color = AVal.constant (darken xColor), align = TextAlignment.Center) }
              sg { Sg.Active active; Sg.View view; Sg.Proj proj
                   Sg.Pass RenderPass.passOne
                   Sg.DepthTest (AVal.constant DepthTest.None)
                   Sg.Trafo (AVal.constant (Trafo3d.Scale(labelSize * 1.5) * textTrafoY * Trafo3d.Translation(V3d.OIO * tipOffset)))
                   Sg.Text("Y", color = AVal.constant (darken yColor), align = TextAlignment.Center) }
              sg { Sg.Active active; Sg.View view; Sg.Proj proj
                   Sg.Pass RenderPass.passOne
                   Sg.DepthTest (AVal.constant DepthTest.None)
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

        // ---- Render pipeline (hybrid Forward + WBOIT, two offscreen passes):
        //   1. Forward-opaque pass (DepthMask=ON, LessOrEqual):
        //        meshScene with Sg.Uniform IsForwardPass = true. The shader
        //        discards α<τ and writes the colour to the ForwardColor
        //        attachment; α=1 surfaces compete via depth-test, so the
        //        front-most α=1 mesh wins per-pixel — no WBOIT colour bleed
        //        inside the lasso/blob interior.
        //   2. WBOIT translucent pass (DepthMask=OFF, LessOrEqual against the
        //      depth buffer pass 1 just wrote):
        //        meshScene + pinScene with IsForwardPass = false. The shader
        //        discards α≥τ and emits to Accum/Revealage. Translucent
        //        fragments behind the forward-opaque depth are rejected
        //        (occluded ghosts disappear — accepted trade-off); fragments
        //        in front of or beside opaques accumulate via WBOIT.
        //   3. Compose pass: samples ForwardColor + Accum + Revealage, applies
        //      "WBOIT over Forward" and writes colour into the main FB. Does
        //      not write depth — passOne overlays draw with DepthTest.None.
        //   4. Picking that used to read main-FB depth (`e.Location.Depth`) is
        //      driven by explicit CPU/server raycasts in View.fs.

        let oitSize = info.ViewportSize

        let forwardColorTex = info.Runtime.CreateTexture2D(oitSize, TextureFormat.Rgba8,           1, 1)
        let accumTex        = info.Runtime.CreateTexture2D(oitSize, TextureFormat.Rgba16f,         1, 1)
        let revealageTex    = info.Runtime.CreateTexture2D(oitSize, TextureFormat.Rgba8,           1, 1)
        let depthTex        = info.Runtime.CreateTexture2D(oitSize, TextureFormat.Depth24Stencil8, 1, 1)

        let oitSig =
            info.Runtime.CreateFramebufferSignature [
                OIT.ForwardColorSemantic,     TextureFormat.Rgba8
                OIT.AccumSemantic,            TextureFormat.Rgba16f
                OIT.RevealageSemantic,        TextureFormat.Rgba8
                DefaultSemantic.DepthStencil, TextureFormat.Depth24Stencil8
            ]

        let oitFbo =
            (forwardColorTex, accumTex, revealageTex)
            |||> AdaptiveResource.bind3 (fun (fw : IBackendTexture) (a : IBackendTexture) (r : IBackendTexture) ->
                depthTex |> AdaptiveResource.bind (fun (d : IBackendTexture) ->
                    AVal.constant (
                        info.Runtime.CreateFramebuffer(
                            oitSig,
                            [
                                OIT.ForwardColorSemantic,     fw.[TextureAspect.Color, 0, 0]        :> IFramebufferOutput
                                OIT.AccumSemantic,            a.[TextureAspect.Color, 0, 0]         :> IFramebufferOutput
                                OIT.RevealageSemantic,        r.[TextureAspect.Color, 0, 0]         :> IFramebufferOutput
                                DefaultSemantic.DepthStencil, d.[TextureAspect.DepthStencil, 0, 0]  :> IFramebufferOutput
                            ]))))

        let meshScene = MeshView.buildScene loadFinished model
        let pinScene  = ScanPinScene.build env view proj fullscreenActive placementHover model

        // Shared per-attachment blend modes. ForwardColor uses straight alpha
        // blend so a fragment with src=(0,0,0,0) leaves the dst untouched —
        // the WBOIT pass never clobbers the forward result, and vice-versa.
        let accumBlend     = AVal.constant BlendMode.Add
        let revealageBlend = AVal.constant OIT.revealageBlendMode
        let forwardBlend   = AVal.constant BlendMode.Blend

        // Pass 1: forward-opaque (α ≥ τ writes ForwardColor; α < τ discards).
        let forwardOpaqueScene =
            sg {
                Sg.View view
                Sg.Proj proj
                Sg.DepthMask (AVal.constant true)
                Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
                Sg.BlendMode(OIT.ForwardColorSemantic, forwardBlend)
                Sg.BlendMode(OIT.AccumSemantic,        accumBlend)
                Sg.BlendMode(OIT.RevealageSemantic,    revealageBlend)
                Sg.Uniform("IsForwardPass", AVal.constant true)
                Sg.Uniform("ViewportSize", info.ViewportSize)
                meshScene
            }

        // Pass 2: WBOIT (α < τ writes Accum/Revealage; α ≥ τ discards). Pins +
        // line widgets only emit WBOIT (they use OIT.weightedBlend, ignoring
        // IsForwardPass) so they live here.
        let wbOitContent = ASet.unionMany (ASet.ofList [ meshScene; pinScene ])
        let wbOitScene =
            sg {
                Sg.View view
                Sg.Proj proj
                Sg.DepthMask (AVal.constant false)
                Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
                Sg.BlendMode(OIT.ForwardColorSemantic, forwardBlend)
                Sg.BlendMode(OIT.AccumSemantic,        accumBlend)
                Sg.BlendMode(OIT.RevealageSemantic,    revealageBlend)
                Sg.Uniform("IsForwardPass", AVal.constant false)
                Sg.Uniform("ViewportSize", info.ViewportSize)
                wbOitContent
            }

        let forwardTask =
            info.Runtime.CompileRender(oitSig, forwardOpaqueScene.GetRenderObjects(TraversalState.empty info.Runtime))
        let wbOitTask =
            info.Runtime.CompileRender(oitSig, wbOitScene.GetRenderObjects(TraversalState.empty info.Runtime))
        let oitClear =
            info.Runtime.CompileClear(
                oitSig,
                clear {
                    colors [
                        OIT.ForwardColorSemantic, C4f.Zero
                        OIT.AccumSemantic,        C4f.Zero
                        OIT.RevealageSemantic,    C4f.White
                    ]
                    depth 1.0
                    stencil 0
                })

        // Drive the offscreen tasks via an AVal.custom that returns the resolved
        // ForwardColor + Accum + Revealage textures.
        let oitOutputs =
            (forwardColorTex, accumTex, revealageTex)
            |||> AdaptiveResource.bind3 (fun (fw : IBackendTexture) (a : IBackendTexture) (r : IBackendTexture) ->
                oitFbo |> AVal.bind (fun oFbo ->
                    AVal.custom (fun tok ->
                        oitClear.Run    (tok, RenderToken.Empty, oFbo)
                        forwardTask.Run (tok, RenderToken.Empty, oFbo)
                        wbOitTask.Run   (tok, RenderToken.Empty, oFbo)
                        fw, a, r)))
        let forwardOut   = oitOutputs |> AVal.map (fun (fw,_,_) -> fw) |> AdaptiveResource.map (fun t -> t :> ITexture)
        let accumOut     = oitOutputs |> AVal.map (fun (_,a,_)  -> a)  |> AdaptiveResource.map (fun t -> t :> ITexture)
        let revealageOut = oitOutputs |> AVal.map (fun (_,_,r)  -> r)  |> AdaptiveResource.map (fun t -> t :> ITexture)

        // Compose pass — a fullscreen quad samples all three attachments and
        // applies "WBOIT over Forward" into the main framebuffer.
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
                Sg.Uniform("ForwardColorTexture", forwardOut)
                Sg.Uniform("AccumTexture",        accumOut)
                Sg.Uniform("RevealageTexture",    revealageOut)
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

        // Coordinate cross + labels: forward overlay on passOne. Compose writes
        // color only (no depth) into the main FB, so the widget renders as
        // always-on-top — set DepthTest.None below.
        let indicatorNodes = originIndicator view proj (AVal.map not fullscreenActive)
        let labelNodes     = originLabels    view proj (AVal.map not fullscreenActive)

        ASet.unionMany (ASet.ofList [ ASet.single composeNode; indicatorNodes; labelNodes ])
