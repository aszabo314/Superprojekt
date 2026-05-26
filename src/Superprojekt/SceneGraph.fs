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

    // Coordinate-cross centre block. Always-on-top: depth test disabled and
    // drawn in passOne after the opaque mesh pass, so it overlays everything.
    // Alpha-blended.
    let private axisBox (color : V4d) (trafo : Trafo3d) =
        sg {
            Sg.Pass RenderPass.passOne
            Sg.Trafo (AVal.constant trafo)
            Sg.Shader { DefaultSurfaces.trafo; Shader.flatColor }
            Sg.Uniform("FlatColor", AVal.constant (V4f color))
            Sg.DepthTest (AVal.constant DepthTest.None)
            Sg.BlendMode (AVal.constant BlendMode.Blend)
            Sg.NoEvents
            Sg.VertexAttributes(
                HashMap.ofList [ string DefaultSemantic.Positions, BufferView(AVal.constant (ArrayBuffer boxPos :> IBuffer), typeof<V3f>) ]
            )
            Sg.Index(BufferView(AVal.constant (ArrayBuffer boxIdx :> IBuffer), typeof<int>))
            Sg.Render (AVal.constant boxIdx.Length)
        }

    // Origin cross + tick segments + axis-tip and integer-metre labels.
    // Always-on-top: depth test disabled, drawn in passOne after meshes.
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
                 Sg.Pass RenderPass.passOne
                 Sg.DepthTest (AVal.constant DepthTest.None)
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

        // Single forward render pass into the main framebuffer:
        //   • Meshes: shader writes (rgb, α) and α-gated gl_FragDepth — opaque
        //     fragments (α ≥ 0.99) write their natural depth, transparent
        //     fragments write 1.0 (far). All passZero geometry shares one
        //     depth buffer.
        //   • Pin geometry: depth-tested against the mesh depth so it fades
        //     behind opaque surfaces; alpha-blended.
        //   • Coordinate cross + labels: passOne with DepthTest.None — always
        //     on top.
        //
        // Sg.DepthMask is intentionally NEVER set anywhere in the scene graph:
        // it is buggy in this Aardvark/WebGL build and silently breaks the
        // depth pipeline when used. Every node therefore writes depth using
        // whatever its shader produces; the visible ordering is steered via
        // Sg.DepthTest + Sg.Pass alone. This violates the textbook
        // "translucent should not write depth" rule but is the only
        // combination that actually renders correctly here.

        let meshScene  = MeshView.buildScene loadFinished model
        let pinScene   = ScanPinScene.build env view proj fullscreenActive placementHover model

        let notFullscreen = AVal.map not fullscreenActive
        let cross         = originIndicator view proj notFullscreen
        let labels        = originLabels    view proj notFullscreen

        let viewportUni =
            sg {
                Sg.View view
                Sg.Proj proj
                Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
                Sg.BlendMode (AVal.constant BlendMode.Blend)
                Sg.Uniform("ViewportSize", info.ViewportSize)
                meshScene
                cross
                pinScene
                labels
            }

        ASet.single viewportUni
