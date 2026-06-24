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

    // Coordinate-cross centre block. Always-on-top: DepthTest.None, passOne
    // after the opaque mesh pass; alpha-blended.
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

    // Origin cross + tick segments. Always-on-top: DepthTest.None, passOne.
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

    // Subtle reference-mesh marker (★): its bbox edges in the accent colour,
    // depth-tested so it stays unobtrusive.
    let private referenceOutline (view : aval<Trafo3d>) (proj : aval<Trafo3d>) (active : aval<bool>) (model : AdaptiveModel) =
        let col = V4d(0.102, 0.337, 0.859, 0.5)
        let segs =
            AVal.custom (fun t ->
                match (model.Registration.GetValue t).ReferenceMesh with
                | None -> [||]
                | Some name ->
                    match Map.tryFind name (model.MeshBounds.GetValue t) with
                    | None -> [||]
                    | Some box ->
                        let cc = model.CommonCentroid.GetValue t
                        let scale = DatasetScale.forMesh (model.DatasetScales.GetValue t) name
                        let tr =
                            Map.tryFind name (model.MeshTransforms.GetValue t)
                            |> Option.defaultValue Trafo3d.Identity
                        let corner (ix : int) (iy : int) (iz : int) =
                            let w =
                                V3d((if ix = 0 then box.Min.X else box.Max.X),
                                    (if iy = 0 then box.Min.Y else box.Max.Y),
                                    (if iz = 0 then box.Min.Z else box.Max.Z))
                            tr.Forward.TransformPos ((w - cc) * scale)
                        let edges =
                            [|
                                (0,0,0),(1,0,0); (0,1,0),(1,1,0); (0,0,1),(1,0,1); (0,1,1),(1,1,1)
                                (0,0,0),(0,1,0); (1,0,0),(1,1,0); (0,0,1),(0,1,1); (1,0,1),(1,1,1)
                                (0,0,0),(0,0,1); (1,0,0),(1,0,1); (0,1,0),(0,1,1); (1,1,0),(1,1,1)
                            |]
                        edges |> Array.map (fun ((ax, ay, az), (bx, by, bz)) ->
                            corner ax ay az, corner bx by bz, col, 1.5))
        sg {
            Sg.Active active
            Sg.View view
            Sg.Proj proj
            Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
            Sg.BlendMode (AVal.constant BlendMode.Blend)
            Sg.NoEvents
            Lines.render segs
        }

    let build
        (env : Env<Message>)
        (info : Aardvark.Dom.RenderControlInfo)
        (view : aval<Trafo3d>)
        (proj : aval<Trafo3d>)
        (fullscreenActive : aval<bool>)
        (placementHover : aval<V3d option>)
        (patchHover : aval<PatchHover option>)
        (cursorHighlight : aval<CursorHighlight option>)
        (clipUniforms : aval<int * V4f * V4f>)
        (previewSwap : aval<bool>)
        (wheelIsolation : aval<string option>)
        (model : AdaptiveModel) =

        let loadFinished (name : string) =
            env.Emit [ LoadFinished name ]

        // Single forward pass into the main framebuffer:
        //   • Meshes: (rgb, α) + α-gated depth — opaque (α ≥ 0.99) write natural
        //     depth, the rest write 1.0 (far). All passZero shares one buffer.
        //   • Pin geometry: depth-tested against the mesh depth; alpha-blended.
        //   • Cross + labels: passOne, DepthTest.None — always on top.
        //
        // Sg.DepthMask is intentionally NEVER set: it is buggy in this
        // Aardvark/WebGL build and silently breaks the depth pipeline. Every
        // node writes depth from its shader; ordering is steered via
        // Sg.DepthTest + Sg.Pass alone. Violates the textbook "translucent
        // shouldn't write depth" rule but is the only combination that works.

        let meshScene  = MeshView.buildScene loadFinished cursorHighlight clipUniforms previewSwap wheelIsolation model
        let outlineScene = OutlineView.build info model view proj
        let pinScene   = ScanPinScene.build env view proj fullscreenActive placementHover patchHover model

        let notFullscreen = AVal.map not fullscreenActive
        let cross         = originIndicator view proj notFullscreen
        let labels        = originLabels    view proj notFullscreen
        let refOutline    = referenceOutline view proj notFullscreen model

        let viewportUni =
            sg {
                Sg.View view
                Sg.Proj proj
                Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
                Sg.BlendMode (AVal.constant BlendMode.Blend)
                Sg.Uniform("ViewportSize", info.ViewportSize)
                meshScene
                outlineScene
                cross
                pinScene
                refOutline
                labels
            }

        ASet.single viewportUni
