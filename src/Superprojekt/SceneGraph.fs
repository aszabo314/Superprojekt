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

    // Origin cross + tick segments, anchored at `center` (render space — the first
    // mesh's panorama centre). Always-on-top: DepthTest.None, passOne.
    let private originIndicator (view : aval<Trafo3d>) (proj : aval<Trafo3d>) (active : aval<bool>) (center : aval<V3d>) =
        let axisLength = 3.0
        let tickSpacing = 0.25
        let tickLen = 0.12

        let xColor = V4d(0.82, 0.15, 0.10, 1.0)
        let yColor = V4d(0.10, 0.72, 0.10, 1.0)
        let zColor = V4d(0.15, 0.35, 0.90, 1.0)

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

    // Prominent reference-mesh marker (§T10): its bbox edges in gold (matching the
    // focus reference tile ★), thick + bright so the reference is unmistakable in 3D.
    let private referenceOutline (view : aval<Trafo3d>) (proj : aval<Trafo3d>) (active : aval<bool>) (model : AdaptiveModel) =
        let col = V4d(0.831, 0.631, 0.024, 0.95)
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
                        let view =
                            match model.RegView.GetValue t, model.RegPeekHeld.GetValue t with
                            | RegBefore, true -> RegAfter
                            | RegAfter, true -> RegBefore
                            | v, false -> v
                        let tr =
                            match view, Map.tryFind name (model.SolvedTransforms.GetValue t) with
                            | RegAfter, Some s -> s
                            | _ -> Map.tryFind name (model.LoadTransforms.GetValue t) |> Option.defaultValue Trafo3d.Identity
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
                            corner ax ay az, corner bx by bz, col, 2.5))
        sg {
            Sg.Active active
            Sg.View view
            Sg.Proj proj
            Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
            Sg.BlendMode (AVal.constant BlendMode.Blend)
            Sg.NoEvents
            Lines.render segs
        }

    // The focused mesh's bbox edges in a cyan accent — the 3D "active" treatment
    // mirroring the rail row + focus tile (read parity §B). Depth-tested + subtle,
    // distinct from the reference (blue). Hidden when nothing is focused.
    let private focusedOutline (view : aval<Trafo3d>) (proj : aval<Trafo3d>) (active : aval<bool>) (model : AdaptiveModel) =
        let col = V4d(0.031, 0.569, 0.698, 0.7)   // cyan #0891b2
        let segs =
            AVal.custom (fun t ->
                match Selection.mesh (model.Selection.Active.GetValue t) with
                | None -> [||]
                | Some name ->
                    match Map.tryFind name (model.MeshBounds.GetValue t) with
                    | None -> [||]
                    | Some box ->
                        let cc = model.CommonCentroid.GetValue t
                        let scale = DatasetScale.forMesh (model.DatasetScales.GetValue t) name
                        let view =
                            match model.RegView.GetValue t, model.RegPeekHeld.GetValue t with
                            | RegBefore, true -> RegAfter
                            | RegAfter, true -> RegBefore
                            | v, false -> v
                        let tr =
                            match view, Map.tryFind name (model.SolvedTransforms.GetValue t) with
                            | RegAfter, Some s -> s
                            | _ -> Map.tryFind name (model.LoadTransforms.GetValue t) |> Option.defaultValue Trafo3d.Identity
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
        (clipUniforms : aval<int * V4f * V4f>)
        (wheelIsolation : aval<string option>)
        (model : AdaptiveModel) =

        let loadFinished (name : string) =
            env.Emit [ LoadFinished name ]

        // Sg.DepthMask is intentionally NEVER set: it is buggy in this
        // Aardvark/WebGL build and silently breaks the depth pipeline. Every node
        // writes depth from its shader; ordering is steered via Sg.DepthTest +
        // Sg.Pass alone. Cross + labels run in passOne (DepthTest.None) on top.

        let meshScene  = MeshView.buildScene loadFinished clipUniforms placementHover wheelIsolation model
        let outlineScene = OutlineView.build info model view proj
        let pinScene   = ScanPinScene.build env view proj fullscreenActive placementHover model

        let notFullscreen = AVal.map not fullscreenActive
        // The cross + axis labels sit at the first mesh's panorama centre (render
        // space): stored PanoCenters[first] else its centroid (= origin); empty → origin.
        let crossCenter =
            AVal.custom (fun t ->
                match model.MeshNames.Content.GetValue t |> IndexList.toList with
                | first :: _ ->
                    let cc = model.CommonCentroid.GetValue t
                    let world =
                        match Map.tryFind first (model.PanoCenters.GetValue t) with
                        | Some w -> w
                        | None -> Map.tryFind first (model.DatasetCentroids.GetValue t) |> Option.defaultValue cc
                    ScanPin.renderCentre cc (DatasetScale.forMesh (model.DatasetScales.GetValue t) first) world
                | [] -> V3d.Zero)
        let cross         = originIndicator view proj notFullscreen crossCenter
        let labels        = originLabels    view proj notFullscreen crossCenter
        let refOutline    = referenceOutline view proj notFullscreen model
        let focusOutline  = focusedOutline   view proj notFullscreen model

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
                focusOutline
                labels
            }

        ASet.single viewportUni
