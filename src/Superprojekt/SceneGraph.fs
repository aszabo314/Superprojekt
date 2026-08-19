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

    // Coordinate-cross centre block.
    let private axisBox (color : V4d) (trafo : aval<Trafo3d>) =
        sg {
            Sg.Pass RenderPass.passOne
            Sg.Trafo trafo
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

    // Origin-cross geometry constants, shared by the indicator and its labels.
    let private axisLength = 3.0
    let private tickSpacing = 0.25
    let private tickLen = 0.12
    let private xColor = V4d(0.82, 0.15, 0.10, 1.0)
    let private yColor = V4d(0.10, 0.72, 0.10, 1.0)
    let private zColor = V4d(0.15, 0.35, 0.90, 1.0)

    // Origin cross + tick segments, anchored at `center` (render space — the first
    // mesh's sensor position).
    let private originIndicator (view : aval<Trafo3d>) (proj : aval<Trafo3d>) (active : aval<bool>) (center : aval<V3d>) =
        let tickSegs (o : V3d) (color : V4d) (dir : V3d) (perpA : V3d) =
            let n = int (axisLength / tickSpacing)
            let half = perpA * (tickLen * 0.5)
            [| for i in 1 .. n do
                let c = o + dir * (float i * tickSpacing)
                yield c - half, c + half, color, 1.5 |]

        let allLineSegs =
            center |> AVal.map (fun o ->
                Array.concat [
                    [| o, o + V3d.IOO * axisLength, xColor, 2.0
                       o, o + V3d.OIO * axisLength, yColor, 2.0
                       o, o + V3d.OOI * axisLength, zColor, 2.0 |]
                    tickSegs o xColor V3d.IOO V3d.OOI
                    tickSegs o yColor V3d.OIO V3d.IOO
                    tickSegs o zColor V3d.OOI V3d.IOO
                ])

        ASet.ofList [
            sg { Sg.Active active; Sg.View view; Sg.Proj proj
                 axisBox (V4d(0.88, 0.88, 0.88, 1.0)) (center |> AVal.map (fun o -> Trafo3d.Scale 0.08 * Trafo3d.Translation o)) }
            sg { Sg.Active active; Sg.View view; Sg.Proj proj
                 Sg.Pass RenderPass.passOne
                 Sg.DepthTest (AVal.constant DepthTest.None)
                 Sg.BlendMode (AVal.constant BlendMode.Blend)
                 Lines.render allLineSegs }
        ]

    let private originLabels (view : aval<Trafo3d>) (proj : aval<Trafo3d>) (active : aval<bool>) (center : aval<V3d>) =
        let labelSize = 0.15
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
                    let labelPos = dir * dist + perpA * (tickLen * 0.5 + labelSize * 1.2)
                    let trafo = Trafo3d.Scale(labelSize) * textRot * Trafo3d.Translation(labelPos)
                    yield sg {
                        Sg.NoEvents
                        Sg.Active active; Sg.View view; Sg.Proj proj
                        Sg.Pass RenderPass.passOne
                        Sg.DepthTest (AVal.constant DepthTest.None)
                        Sg.Trafo (center |> AVal.map (fun o -> trafo * Trafo3d.Translation o))
                        Sg.Text(sprintf "%.0f" dist, color = AVal.constant textColor, align = TextAlignment.Center)
                    } ]

        let tipOffset = axisLength + labelSize * 1.5
        let tipTrafo (baseT : Trafo3d) = center |> AVal.map (fun o -> baseT * Trafo3d.Translation o)
        let tipNodes =
            [ sg { Sg.NoEvents
                   Sg.Active active; Sg.View view; Sg.Proj proj
                   Sg.Pass RenderPass.passOne
                   Sg.DepthTest (AVal.constant DepthTest.None)
                   Sg.Trafo (tipTrafo (Trafo3d.Scale(labelSize * 1.5) * textTrafoX * Trafo3d.Translation(V3d.IOO * tipOffset)))
                   Sg.Text("X", color = AVal.constant (darken xColor), align = TextAlignment.Center) }
              sg { Sg.NoEvents
                   Sg.Active active; Sg.View view; Sg.Proj proj
                   Sg.Pass RenderPass.passOne
                   Sg.DepthTest (AVal.constant DepthTest.None)
                   Sg.Trafo (tipTrafo (Trafo3d.Scale(labelSize * 1.5) * textTrafoY * Trafo3d.Translation(V3d.OIO * tipOffset)))
                   Sg.Text("Y", color = AVal.constant (darken yColor), align = TextAlignment.Center) }
              sg { Sg.NoEvents
                   Sg.Active active; Sg.View view; Sg.Proj proj
                   Sg.Pass RenderPass.passOne
                   Sg.DepthTest (AVal.constant DepthTest.None)
                   Sg.Trafo (tipTrafo (Trafo3d.Scale(labelSize * 1.5) * textTrafoZ * Trafo3d.Translation(V3d.OOI * tipOffset)))
                   Sg.Text("Z", color = AVal.constant (darken zColor), align = TextAlignment.Center) } ]

        ASet.ofList (
            tipNodes
            @ labelNodes xColor V3d.IOO V3d.OOI textTrafoX
            @ labelNodes yColor V3d.OIO V3d.IOO textTrafoY
            @ labelNodes zColor V3d.OOI V3d.IOO textTrafoZ)

    // Bbox edge outline of ONE mesh at its committed displayed pose — the gold
    // root marker's builder (the root is never a peek's MOV).
    let private bboxOutline (view : aval<Trafo3d>) (proj : aval<Trafo3d>) (active : aval<bool>)
                            (model : AdaptiveModel)
                            (nameAt : AdaptiveToken -> string option) (col : V4d) (width : float) =
        let segs =
            AVal.custom (fun t ->
                match nameAt t with
                | None -> [||]
                | Some name ->
                    match Map.tryFind name (model.MeshBounds.GetValue t) with
                    | None -> [||]
                    | Some box ->
                        let cc = model.CommonCentroid.GetValue t
                        let scale = DatasetScale.forMesh (model.DatasetScales.GetValue t) name
                        let tr =
                            match Map.tryFind name (model.ComposedPoses.GetValue t) with
                            | Some s -> s
                            | None -> Map.tryFind name (model.LoadTransforms.GetValue t) |> Option.defaultValue Trafo3d.Identity
                        let corner (ix : int) (iy : int) (iz : int) =
                            let w =
                                V3d((if ix = 0 then box.Min.X else box.Max.X),
                                    (if iy = 0 then box.Min.Y else box.Max.Y),
                                    (if iz = 0 then box.Min.Z else box.Max.Z))
                            tr.Forward.TransformPos (ScanPin.renderCentre cc scale w)
                        let edges =
                            [|
                                (0,0,0),(1,0,0); (0,1,0),(1,1,0); (0,0,1),(1,0,1); (0,1,1),(1,1,1)
                                (0,0,0),(0,1,0); (1,0,0),(1,1,0); (0,0,1),(0,1,1); (1,0,1),(1,1,1)
                                (0,0,0),(0,0,1); (1,0,0),(1,0,1); (0,1,0),(0,1,1); (1,1,0),(1,1,1)
                            |]
                        edges |> Array.map (fun ((ax, ay, az), (bx, by, bz)) ->
                            corner ax ay az, corner bx by bz, col, width))
        sg {
            Sg.Active active
            Sg.View view
            Sg.Proj proj
            Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
            Sg.BlendMode (AVal.constant BlendMode.Blend)
            Sg.NoEvents
            Lines.render segs
        }

    // Prominent reference-mesh marker: its bbox edges in gold (matching the
    // survey tile ★), thick + bright so the reference is unmistakable in 3D.
    let private referenceOutline view proj active (model : AdaptiveModel) =
        bboxOutline view proj active model
            (fun t -> (model.RegGraph.GetValue t).Root)
            (V4d(Primitives.refGoldV3d, 0.95)) 2.5

    let build
        (env : Env<Message>)
        (info : Aardvark.Dom.RenderControlInfo)
        (view : aval<Trafo3d>)
        (proj : aval<Trafo3d>)
        (fullscreenActive : aval<bool>)
        (clipUniforms : aval<int * V4f * V4f>)
        (model : AdaptiveModel) =

        let loadFinished (name : string) =
            env.Emit [ LoadFinished name ]

        // Sg.DepthMask is intentionally NEVER set: it is buggy in this
        // Aardvark/WebGL build and silently breaks the depth pipeline. Every node
        // writes depth from its shader; ordering is steered via Sg.DepthTest +
        // Sg.Pass alone. Cross + labels run in passOne (DepthTest.None) on top.

        let meshScene  = MeshView.buildScene loadFinished clipUniforms model
        // ONE coverage MRT render, shared by the footprint composite and the
        // mesh shader's matrix-hover overlap preview.
        let cov0, cov1, covTexel = OutlineView.coverageOffscreen info model (AVal.constant true) view proj
        let outlineScene = OutlineView.build info model view proj (cov0, cov1, covTexel)
        let ovOn, ovSelA0, ovSelA1, ovSelB0, ovSelB1 = MeshView.overlapPreviewUniforms model
        let pinScene   = ScanPinScene.build env view proj fullscreenActive model

        let notFullscreen = AVal.map not fullscreenActive
        // The cross + axis labels sit at the first mesh's sensor position (render
        // space): its origin = its centroid-file world coordinate; empty → origin.
        let crossCenter =
            AVal.custom (fun t ->
                match model.MeshNames.Content.GetValue t |> IndexList.toList with
                | first :: _ ->
                    let cc = model.CommonCentroid.GetValue t
                    let world = Map.tryFind first (model.DatasetCentroids.GetValue t) |> Option.defaultValue cc
                    ScanPin.renderCentre cc (DatasetScale.forMesh (model.DatasetScales.GetValue t) first) world
                | [] -> V3d.Zero)
        let crossActive   = notFullscreen
        let cross         = originIndicator view proj crossActive crossCenter
        let labels        = originLabels    view proj crossActive crossCenter
        let refOutline    = referenceOutline view proj notFullscreen model

        // Orbit-centre cue: a small ring+cross at the rotation centre, shown
        // whenever the camera is MOVING — an orbiting drag (easing target
        // leads the pose), a held pan, a zoom easing, or a fly-to animation —
        // and hidden the moment it is still. Screen-constant: eye distance =
        // the orbit radius, so size ∝ radius reads constant.
        let orbitCue =
            let active =
                AVal.custom (fun t ->
                    let cam = model.Camera
                    let drags = cam.dragStarts.GetValue t
                    let rotating =
                        (drags |> MapExt.toSeq
                         |> Seq.exists (fun (_, (_, b)) -> b = cam.rotateButton || b = Button.None))
                        && (abs (cam.targetPhi.GetValue t - cam.phi.GetValue t) > 1e-4
                            || abs (cam.targetTheta.GetValue t - cam.theta.GetValue t) > 1e-4)
                    let panning =
                        drags |> MapExt.toSeq
                        |> Seq.exists (fun (_, (_, b)) -> b = cam.panButton || b = Button.Button4)
                    let zooming =
                        let r = cam.radius.GetValue t
                        abs (cam.targetRadius.GetValue t - r) > 1e-3 * max 1e-6 r
                    let flying =
                        (cam.centerAnimation.GetValue t).IsSome
                        || (cam.locationAnimation.GetValue t).IsSome
                    rotating || panning || zooming || flying)
            let segs =
                AVal.custom (fun t ->
                    let c = model.Camera.center.GetValue t
                    let s = model.Camera.radius.GetValue t * 0.02
                    let out = ResizeArray<V3d * V3d * V4d * float>()
                    LineGlyphs.duplex (fun col w ->
                        LineGlyphs.addRingXY out c s col w 32
                        out.Add(c - V3d.IOO * (s * 1.5), c + V3d.IOO * (s * 1.5), col, w)
                        out.Add(c - V3d.OIO * (s * 1.5), c + V3d.OIO * (s * 1.5), col, w)
                        out.Add(c - V3d.OOI * (s * 1.5), c + V3d.OOI * (s * 1.5), col, w)) 0.9 1.6
                    out.ToArray())
            sg {
                Sg.Active ((notFullscreen, active) ||> AVal.map2 (&&))
                Sg.Pass RenderPass.passOne
                Sg.DepthTest (AVal.constant DepthTest.None)
                Sg.BlendMode (AVal.constant BlendMode.Blend)
                Sg.NoEvents
                Lines.render segs
            }

        let viewportUni =
            sg {
                Sg.View view
                Sg.Proj proj
                Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
                Sg.BlendMode (AVal.constant BlendMode.Blend)
                Sg.Uniform("ViewportSize", info.ViewportSize)
                Sg.Uniform("Coverage0", cov0)
                Sg.Uniform("Coverage1", cov1)
                Sg.Uniform("OverlapPreview", ovOn)
                Sg.Uniform("OverlapSelA0", ovSelA0)
                Sg.Uniform("OverlapSelA1", ovSelA1)
                Sg.Uniform("OverlapSelB0", ovSelB0)
                Sg.Uniform("OverlapSelB1", ovSelB1)
                meshScene
                outlineScene
                cross
                pinScene
                refOutline
                orbitCue
                labels
            }

        ASet.single viewportUni
