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

    // Coordinate-cross centre block. Standard depth test (occluded by opaque
    // meshes in front), no depth write, alpha-blended.
    let private axisBox (color : V4d) (trafo : Trafo3d) =
        sg {
            Sg.Trafo (AVal.constant trafo)
            Sg.Shader { DefaultSurfaces.trafo; Shader.flatColor }
            Sg.Uniform("FlatColor", AVal.constant (V4f color))
            Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
            Sg.DepthMask (AVal.constant false)
            Sg.BlendMode (AVal.constant BlendMode.Blend)
            Sg.NoEvents
            Sg.VertexAttributes(
                HashMap.ofList [ string DefaultSemantic.Positions, BufferView(AVal.constant (ArrayBuffer boxPos :> IBuffer), typeof<V3f>) ]
            )
            Sg.Index(BufferView(AVal.constant (ArrayBuffer boxIdx :> IBuffer), typeof<int>))
            Sg.Render (AVal.constant boxIdx.Length)
        }

    // Origin cross + tick segments + axis-tip and integer-metre labels.
    // All depth-test against opaque mesh depth (LessOrEqual) but do not write
    // depth — so they integrate with the scene without occluding anything.
    let private originIndicator (view : aval<Trafo3d>) (proj : aval<Trafo3d>) (active : aval<bool>) =
        let axisLength = 3.0
        let tickSpacing = 0.25
        let tickLen = 0.12

        let xColor = V4d(0.82, 0.15, 0.10, 1.0)
        let yColor = V4d(0.10, 0.72, 0.10, 1.0)
        let zColor = V4d(0.15, 0.35, 0.90, 1.0)

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
                 axisBox (V4d(0.88, 0.88, 0.88, 1.0)) (Trafo3d.Scale 0.08) }
            sg { Sg.Active active; Sg.View view; Sg.Proj proj
                 Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
                 Sg.DepthMask (AVal.constant false)
                 Sg.BlendMode (AVal.constant BlendMode.Blend)
                 Lines.render allLineSegs }
        ]

    let private originLabels (view : aval<Trafo3d>) (proj : aval<Trafo3d>) (active : aval<bool>) =
        let axisLength = 3.0
        let tickSpacing = 0.25
        let tickLen = 0.12
        let labelSize = 0.15

        let xColor = V4d(0.82, 0.15, 0.10, 1.0)
        let yColor = V4d(0.10, 0.72, 0.10, 1.0)
        let zColor = V4d(0.15, 0.35, 0.90, 1.0)

        let toC4b (c : V4d) = C4b(byte(c.X*255.0), byte(c.Y*255.0), byte(c.Z*255.0))
        let darken (c : V4d) = toC4b (V4d(c.X * 0.55, c.Y * 0.55, c.Z * 0.55, 1.0))

        let textTrafoX = Trafo3d.RotationX(Constant.PiHalf)
        let textTrafoY = Trafo3d.RotationX(Constant.PiHalf) * Trafo3d.RotationZ(Constant.PiHalf)
        let textTrafoZ = Trafo3d.RotationX(Constant.PiHalf)

        let labelNodes (color : V4d) (dir : V3d) (perpA : V3d) (textRot : Trafo3d) =
            let n = int (axisLength / tickSpacing)
            let textColor = darken color
            [ for i in 1 .. n do
                if i % 4 = 0 then
                    let dist = float i * tickSpacing
                    let center = dir * dist
                    let labelPos = center + perpA * (tickLen * 0.5 + labelSize * 1.2)
                    let trafo = Trafo3d.Scale(labelSize) * textRot * Trafo3d.Translation(labelPos)
                    yield sg {
                        Sg.Active active; Sg.View view; Sg.Proj proj
                        Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
                        Sg.DepthMask (AVal.constant false)
                        Sg.Trafo (AVal.constant trafo)
                        Sg.Text(sprintf "%.0f" dist, color = AVal.constant textColor, align = TextAlignment.Center)
                    } ]

        let tipOffset = axisLength + labelSize * 1.5
        let tipNodes =
            [ sg { Sg.Active active; Sg.View view; Sg.Proj proj
                   Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
                   Sg.DepthMask (AVal.constant false)
                   Sg.Trafo (AVal.constant (Trafo3d.Scale(labelSize * 1.5) * textTrafoX * Trafo3d.Translation(V3d.IOO * tipOffset)))
                   Sg.Text("X", color = AVal.constant (darken xColor), align = TextAlignment.Center) }
              sg { Sg.Active active; Sg.View view; Sg.Proj proj
                   Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
                   Sg.DepthMask (AVal.constant false)
                   Sg.Trafo (AVal.constant (Trafo3d.Scale(labelSize * 1.5) * textTrafoY * Trafo3d.Translation(V3d.OIO * tipOffset)))
                   Sg.Text("Y", color = AVal.constant (darken yColor), align = TextAlignment.Center) }
              sg { Sg.Active active; Sg.View view; Sg.Proj proj
                   Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
                   Sg.DepthMask (AVal.constant false)
                   Sg.Trafo (AVal.constant (Trafo3d.Scale(labelSize * 1.5) * textTrafoZ * Trafo3d.Translation(V3d.OOI * tipOffset)))
                   Sg.Text("Z", color = AVal.constant (darken zColor), align = TextAlignment.Center) } ]

        ASet.ofList (
            tipNodes
            @ labelNodes xColor V3d.IOO V3d.OOI textTrafoX
            @ labelNodes yColor V3d.OIO V3d.IOO textTrafoY
            @ labelNodes zColor V3d.OOI V3d.IOO textTrafoZ)

    // 100×100 unit floor grid in the render-space XY plane (z=0). Grey lines
    // at α≈0.5, every 10th line slightly darker as a major tick. Depth-test
    // against opaque mesh fragments, no depth write.
    let private groundGridSegments =
        let extent     = 50.0
        let minorStep  = 1.0
        let majorStep  = 10.0
        let minorColor = V4d(0.55, 0.55, 0.55, 0.5)
        let majorColor = V4d(0.30, 0.30, 0.30, 0.5)
        let minorWidth = 1.0
        let majorWidth = 1.4
        let segs = ResizeArray<V3d * V3d * V4d * float>()
        let n = int (extent / minorStep)
        let majorEvery = int (majorStep / minorStep)
        for i in -n .. n do
            let t = float i * minorStep
            let isMajor = (i % majorEvery = 0)
            let color, width =
                if isMajor then majorColor, majorWidth
                else            minorColor, minorWidth
            segs.Add(V3d(-extent, t, 0.0), V3d(extent, t, 0.0), color, width)
            segs.Add(V3d(t, -extent, 0.0), V3d(t, extent, 0.0), color, width)
        AVal.constant (segs.ToArray())

    let private groundGrid (view : aval<Trafo3d>) (proj : aval<Trafo3d>) (active : aval<bool>) =
        ASet.single (
            sg {
                Sg.Active active
                Sg.View view
                Sg.Proj proj
                Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
                Sg.DepthMask (AVal.constant false)
                Sg.BlendMode (AVal.constant BlendMode.Blend)
                Lines.render groundGridSegments
            })

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

        // Single forward render pass into the main framebuffer:
        //   • Meshes: shader writes (rgb, α) and α-gated gl_FragDepth — opaque
        //     fragments (α ≥ 0.99) write their natural depth, transparent
        //     fragments write 1.0 (far) so they're occluded by any opaque
        //     pixel in the scene but still blend over the clear colour.
        //   • Pin geometry, ground grid, coordinate cross + labels: standard
        //     depth test (LessOrEqual against the opaque mesh depth), no
        //     depth write, alpha-blended.
        //
        // BlendMode.Blend is the default for the renderControl; per-node Sg.BlendMode
        // overrides are set where straight α blending matters (lines, axisBox).

        let meshScene  = MeshView.buildScene loadFinished model
        let pinScene   = ScanPinScene.build env view proj fullscreenActive placementHover model

        let notFullscreen = AVal.map not fullscreenActive
        let cross         = originIndicator view proj notFullscreen
        let labels        = originLabels    view proj notFullscreen
        let grid          = groundGrid      view proj notFullscreen

        let viewportUni =
            sg {
                Sg.View view
                Sg.Proj proj
                Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
                Sg.BlendMode (AVal.constant BlendMode.Blend)
                Sg.Uniform("ViewportSize", info.ViewportSize)
                ASet.unionMany (ASet.ofList [ meshScene; pinScene; grid; cross; labels ])
            }

        ASet.single viewportUni
