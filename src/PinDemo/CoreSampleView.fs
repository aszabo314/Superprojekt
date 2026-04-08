namespace PinDemo

open Aardvark.Base
open Aardvark.Rendering
open FSharp.Data.Adaptive
open Aardvark.Dom
open Superprojekt

module CoreSampleView =

    let private prism = PanelState.pin.Prism

    let private coreTrafo = PinGeometry.coreSampleTrafo prism

    let private coreRadius =
        match prism.Footprint.Vertices with
        | v :: _ -> v.Length
        | _ -> 1.0

    let private extents = prism.ExtentForward, prism.ExtentBackward

    let private dataZMin = PanelState.datasets |> Array.map (fun d -> d.Stats.ZMin) |> Array.min
    let private dataZMax = PanelState.datasets |> Array.map (fun d -> d.Stats.ZMax) |> Array.max

    let private transformNormals (nrm : V3f[]) =
        let m = coreTrafo.Forward
        nrm |> Array.map (fun n -> V3f(m.TransformDir(V3d n) |> Vec.normalize))

    let private meshGeometry =
        PanelState.datasets
        |> Array.mapi (fun i d ->
            let pos, nrm, idx = PinGeometry.buildHeightfieldMesh d.Grid.GridOrigin d.Grid.CellSize d.Grid.Resolution d.Grid.Heights
            let pos = pos |> Array.map (fun p -> V3f(coreTrafo.Forward.TransformPos(V3d p)))
            let nrm = transformNormals nrm
            let color = d.Color.ToC4f().ToV4f()
            d.MeshName, i, pos, nrm, idx, color)

    let private buildSummaryMesh (grid : GridSampledSurface) =
        let pos, nrm, idx = PinGeometry.buildHeightfieldMesh grid.GridOrigin grid.CellSize grid.Resolution grid.Heights
        let pos = pos |> Array.map (fun p -> V3f(coreTrafo.Forward.TransformPos(V3d p)))
        let nrm = transformNormals nrm
        pos, nrm, idx

    let private avgMesh = buildSummaryMesh DummyData.averageGrid
    let private q1Mesh  = buildSummaryMesh DummyData.q1Grid
    let private q3Mesh  = buildSummaryMesh DummyData.q3Grid

    let private wireGeometry =
        let pos, idx = PinGeometry.buildPrismWireframe prism 0.05
        let pos = pos |> Array.map (fun p -> V3f(coreTrafo.Forward.TransformPos(V3d p)))
        pos, idx

    let render () =
        renderControl {
            RenderControl.Samples 1
            Class "pin-mini-view"

            let! size = RenderControl.ViewportSize

            let lastPos  = cval V2i.Zero
            let dragging = cval false

            let clickDist (py : int) =
                let extFwd, extBack = extents
                let zoom = AVal.force PanelState.coreZoom
                let halfH = (extFwd + extBack) / 2.0 / zoom
                let centerZ = (extBack - extFwd) / 2.0
                let sz = AVal.force size
                let ndcY = 1.0 - 2.0 * float py / float sz.Y
                -(centerZ - ndcY * halfH)

            let clickAngle (px : int) (py : int) =
                let sz = AVal.force size
                let dx = float px - float sz.X / 2.0
                let dy = float sz.Y / 2.0 - float py
                (atan2 dx dy * Constant.DegreesPerRadian + 360.0) % 360.0

            Dom.OnPointerDown((fun e ->
                if e.Button = Button.Left then
                    transact (fun () ->
                        lastPos.Value <- e.OffsetPosition
                        dragging.Value <- true)
                    let mode = AVal.force PanelState.coreViewMode
                    match mode with
                    | SideView ->
                        transact (fun () ->
                            PanelState.cutMode.Value <- CutPlaneMode.AcrossAxis (clickDist e.OffsetPosition.Y))
                    | TopView ->
                        transact (fun () ->
                            PanelState.cutMode.Value <- CutPlaneMode.AlongAxis (clickAngle e.OffsetPosition.X e.OffsetPosition.Y))
            ), pointerCapture = true)

            Dom.OnPointerUp((fun _ ->
                transact (fun () -> dragging.Value <- false)
            ), pointerCapture = true)

            Dom.OnPointerMove(fun e ->
                if AVal.force dragging then
                    let prev = AVal.force lastPos
                    let delta = e.OffsetPosition - prev
                    transact (fun () -> lastPos.Value <- e.OffsetPosition)
                    let mode = AVal.force PanelState.coreViewMode
                    match mode with
                    | SideView ->
                        let rot = AVal.force PanelState.coreRotation
                        transact (fun () ->
                            PanelState.coreRotation.Value <- rot + float delta.X * -0.01
                            PanelState.cutMode.Value <- CutPlaneMode.AcrossAxis (clickDist e.OffsetPosition.Y))
                    | TopView ->
                        transact (fun () ->
                            PanelState.cutMode.Value <- CutPlaneMode.AlongAxis (clickAngle e.OffsetPosition.X e.OffsetPosition.Y)))

            Dom.OnContextMenu(ignore, preventDefault = true)

            let extFwd, extBack = extents
            let centerZ = (extBack - extFwd) / 2.0

            let viewT =
                (PanelState.coreViewMode, PanelState.coreRotation) ||> AVal.map2 (fun mode rot ->
                    let dist = 100.0
                    match mode with
                    | SideView ->
                        let dir = V3d(cos rot, sin rot, 0.0)
                        let r = Vec.cross V3d.OOI dir |> Vec.normalize
                        let eye = dir * dist + V3d(0.0, 0.0, centerZ)
                        CameraView(-V3d.OOI, eye, -dir, -V3d.OOI, r) |> CameraView.viewTrafo
                    | TopView ->
                        let eye = V3d(0.0, 0.0, dist)
                        CameraView(V3d.OOI, eye, -V3d.OOI, V3d.IOO, V3d.OIO) |> CameraView.viewTrafo)

            let camDist = 100.0

            let projT =
                (PanelState.coreViewMode, PanelState.coreZoom) ||> AVal.map2 (fun mode zoom ->
                    let r = coreRadius / zoom
                    let halfH = (extFwd + extBack) / 2.0 / zoom
                    let nearP, farP =
                        match mode with
                        | SideView ->
                            camDist - coreRadius - 0.5, camDist + coreRadius + 0.5
                        | TopView ->
                            camDist + dataZMin - 0.5, camDist + dataZMax + 0.5
                    match mode with
                    | TopView ->
                        Frustum.ortho (Box3d(V3d(-r, -r, nearP), V3d(r, r, farP)))
                        |> Frustum.projTrafo
                    | SideView ->
                        Frustum.ortho (Box3d(V3d(-r, -halfH, nearP), V3d(r, halfH, farP)))
                        |> Frustum.projTrafo)

            Sg.View viewT
            Sg.Proj projT

            let depthOn = PanelState.depthShadeOn |> AVal.map (fun b -> if b then 1 else 0)
            let isoOn =
                (PanelState.isolinesOn, PanelState.coreViewMode) ||> AVal.map2 (fun on mode ->
                    if on && mode <> SideView then 1 else 0)
            let wireVisible =
                PanelState.coreViewMode |> AVal.map (fun m -> m <> SideView)

            let isFiltered =
                PanelState.aggregationMode |> AVal.map (fun m -> m = Difference)

            // Per-dataset heightfield meshes (cylindrically clipped) — only in Difference mode
            for (name, meshIdx, pos, nrm, idx, color) in meshGeometry do
                let inK = PanelState.inTopK name
                let visible =
                    (isFiltered, inK) ||> AVal.map2 (fun filt k -> filt && k)
                let opacity = PanelState.opacityOf name
                sg {
                    Sg.Active visible
                    Sg.Shader {
                        DefaultSurfaces.trafo
                        DefaultSurfaces.constantColor (C4f color)
                        Shader.headlight
                        BlitShader.coreClip
                        Shader.depthShade
                        Shader.isolines
                        Shader.applyOpacity
                    }
                    Sg.Uniform("CoreRadius", AVal.constant coreRadius)
                    Sg.Uniform("MeshIndex", AVal.constant meshIdx)
                    Sg.Uniform("ColorMode", PanelState.colorMode |> AVal.map (fun b -> if b then 1 else 0))
                    Sg.Uniform("DepthShadeOn", depthOn)
                    Sg.Uniform("IsolinesOn", isoOn)
                    Sg.Uniform("IsolineSpacing", AVal.constant 1.0)
                    Sg.Uniform("Opacity", opacity)
                    Sg.BlendMode (AVal.constant BlendMode.Blend)
                    Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
                    Sg.NoEvents
                    Sg.VertexAttributes(
                        HashMap.ofList [
                            string DefaultSemantic.Positions, BufferView(AVal.constant (ArrayBuffer pos :> IBuffer), typeof<V3f>)
                            string DefaultSemantic.Normals,   BufferView(AVal.constant (ArrayBuffer nrm :> IBuffer), typeof<V3f>)
                        ])
                    Sg.Index(BufferView(AVal.constant (ArrayBuffer idx :> IBuffer), typeof<int>))
                    Sg.Render (AVal.constant idx.Length)
                }

            let isSummary = isFiltered |> AVal.map not

            // Summary meshes — shown when aggregation is Avg/Q1/Q3
            let summaryMeshes = [
                avgMesh,  C4f(0.65f, 0.65f, 0.65f, 1.0f),    isSummary
                q3Mesh,   C4f(0.95f, 0.55f, 0.15f, 0.45f),   PanelState.aggregationMode |> AVal.map (fun m -> m = Q3 || m = Average)
                q1Mesh,   C4f(0.2f, 0.45f, 0.9f, 0.45f),     PanelState.aggregationMode |> AVal.map (fun m -> m = Q1 || m = Average)
            ]
            for ((pos, nrm, idx), color, active) in summaryMeshes do
                sg {
                    Sg.Active active
                    Sg.Shader {
                        DefaultSurfaces.trafo
                        DefaultSurfaces.constantColor color
                        BlitShader.coreClip
                        Shader.depthShade
                        Shader.isolines
                    }
                    Sg.Uniform("CoreRadius", AVal.constant coreRadius)
                    Sg.Uniform("MeshIndex", AVal.constant 0)
                    Sg.Uniform("DepthShadeOn", depthOn)
                    Sg.Uniform("IsolinesOn", isoOn)
                    Sg.Uniform("IsolineSpacing", AVal.constant 1.0)
                    Sg.BlendMode (AVal.constant BlendMode.Blend)
                    Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
                    Sg.NoEvents
                    Sg.VertexAttributes(
                        HashMap.ofList [
                            string DefaultSemantic.Positions, BufferView(AVal.constant (ArrayBuffer pos :> IBuffer), typeof<V3f>)
                            string DefaultSemantic.Normals,   BufferView(AVal.constant (ArrayBuffer nrm :> IBuffer), typeof<V3f>)
                        ])
                    Sg.Index(BufferView(AVal.constant (ArrayBuffer idx :> IBuffer), typeof<int>))
                    Sg.Render (AVal.constant idx.Length)
                }

            // Prism wireframe (hidden in side view to keep core sample clean)
            let wirePos, wireIdx = wireGeometry
            sg {
                Sg.Active wireVisible
                Sg.Shader { DefaultSurfaces.trafo; Shader.flatColor }
                Sg.Uniform("FlatColor", AVal.constant (V4d(1.0, 0.85, 0.0, 1.0)))
                Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
                Sg.NoEvents
                Sg.VertexAttributes(
                    HashMap.ofList [
                        string DefaultSemantic.Positions, BufferView(AVal.constant (ArrayBuffer wirePos :> IBuffer), typeof<V3f>)
                    ])
                Sg.Index(BufferView(AVal.constant (ArrayBuffer wireIdx :> IBuffer), typeof<int>))
                Sg.Render (AVal.constant wireIdx.Length)
            }

            // Cut plane slab (thin box so it's visible edge-on in side view)
            let planeData =
                PanelState.cutMode |> AVal.map (fun cut ->
                    let quad, _ = PinGeometry.buildCutPlaneQuad prism cut
                    let quad = quad |> Array.map (fun p -> V3f(coreTrafo.Forward.TransformPos(V3d p)))
                    let n =
                        let v0 = V3d quad.[0]
                        let v1 = V3d quad.[1]
                        let v2 = V3d quad.[2]
                        Vec.cross (v1 - v0) (v2 - v0) |> Vec.normalize
                    let t = 0.15
                    let top = quad |> Array.map (fun p -> V3f(V3d p + n * t))
                    let bot = quad |> Array.map (fun p -> V3f(V3d p - n * t))
                    let pos = Array.append top bot
                    let idx = [| 0;1;2; 0;2;3; 4;6;5; 4;7;6
                                 0;4;5; 0;5;1; 2;6;7; 2;7;3
                                 0;3;7; 0;7;4; 1;5;6; 1;6;2 |]
                    pos, idx)
            sg {
                Sg.Shader { DefaultSurfaces.trafo; Shader.flatColor }
                Sg.Uniform("FlatColor", AVal.constant (V4d(1.0, 0.9, 0.3, 0.45)))
                Sg.BlendMode (AVal.constant BlendMode.Blend)
                Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
                Sg.NoEvents
                Sg.VertexAttributes(
                    HashMap.ofList [
                        string DefaultSemantic.Positions, BufferView(planeData |> AVal.map (fun (p, _) -> ArrayBuffer p :> IBuffer), typeof<V3f>)
                    ])
                Sg.Index(BufferView(planeData |> AVal.map (fun (_, i) -> ArrayBuffer i :> IBuffer), typeof<int>))
                Sg.Render (planeData |> AVal.map (fun (_, i) -> i.Length))
            }
        }
