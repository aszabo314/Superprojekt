namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open Aardworx.WebAssembly
open FSharp.Data.Adaptive
open Aardvark.Dom

module Revolver =

    let private boxPos =
        [|  V3f(-0.5f, -0.5f, -0.5f); V3f( 0.5f, -0.5f, -0.5f); V3f( 0.5f,  0.5f, -0.5f); V3f(-0.5f,  0.5f, -0.5f)
            V3f(-0.5f, -0.5f,  0.5f); V3f( 0.5f, -0.5f,  0.5f); V3f( 0.5f,  0.5f,  0.5f); V3f(-0.5f,  0.5f,  0.5f) |]
    let private boxIdx =
        [| 0;1;2; 0;2;3;  5;4;7; 5;7;6;  4;0;3; 4;3;7;  1;5;6; 1;6;2;  0;4;5; 0;5;1;  3;2;6; 3;6;7 |]

    let private axisBox (color : V4d) (trafo : Trafo3d) =
        sg {
            Sg.Trafo (AVal.constant trafo)
            Sg.Shader { DefaultSurfaces.trafo; Shader.flatColor }
            Sg.Uniform("FlatColor", AVal.constant color)
            Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
            Sg.NoEvents
            Sg.VertexAttributes(
                HashMap.ofList [ string DefaultSemantic.Positions, BufferView(AVal.constant (ArrayBuffer boxPos :> IBuffer), typeof<V3f>) ]
            )
            Sg.Index(BufferView(AVal.constant (ArrayBuffer boxIdx :> IBuffer), typeof<int>))
            Sg.Render (AVal.constant boxIdx.Length)
        }

    // Axis indicator at render-space origin (= common centroid).
    // +Y = North (green, dominant), +X = East (red), +Z = Up (blue). Sizes in render-space units.
    let private originIndicator (view : aval<Trafo3d>) (proj : aval<Trafo3d>) (active : aval<bool>) =
        let box color trafo =
            sg { Sg.Active active; Sg.View view; Sg.Proj proj; axisBox color trafo }
        ASet.ofList [
            box (V4d(0.88, 0.88, 0.88, 1.0)) (Trafo3d.Scale 1.5)
            box (V4d(0.1,  0.72, 0.1,  1.0)) (Trafo3d.Scale(1.0, 5.0, 1.0) * Trafo3d.Translation(0.0, 2.5, 0.0))  // N shaft
            box (V4d(0.1,  0.72, 0.1,  1.0)) (Trafo3d.Scale(2.2, 2.0, 2.2) * Trafo3d.Translation(0.0, 6.0, 0.0))  // N tip
            box (V4d(0.82, 0.15, 0.1,  1.0)) (Trafo3d.Scale(3.0, 0.75, 0.75) * Trafo3d.Translation(1.5, 0.0, 0.0)) // E
            box (V4d(0.15, 0.35, 0.9,  1.0)) (Trafo3d.Scale(0.75, 0.75, 3.0) * Trafo3d.Translation(0.0, 0.0, 1.5)) // U
        ]

    let buildPrismWireframe = PinGeometry.buildPrismWireframe
    let buildCutPlaneQuad = PinGeometry.buildCutPlaneQuad

    let private disk
            (revolverActive    : aval<bool>)
            (revolverBase      : aval<option<V2d>>)
            (colorArrTex       : aval<ITexture>)
            (viewportSize      : aval<V2i>)
            (sliceIndex        : aval<int>)
            (renderPositionNdc : aval<option<V2d>>) =
        let pixelSize = 200
        sg {
            Sg.Active revolverActive
            Sg.NoEvents
            let t =
                (renderPositionNdc, viewportSize) ||> AVal.map2 (fun ndc size ->
                    match ndc with
                    | Some ndc ->
                        let scale = float pixelSize / V2d size
                        Trafo3d.Scale(scale.X, scale.Y, 1.0) * Trafo3d.Translation(ndc.X, ndc.Y, 0.0)
                    | None ->
                        Trafo3d.Scale(0.0)
                )
            let textureOffset =
                (revolverBase, viewportSize) ||> AVal.map2 (fun ndc size ->
                    match ndc with
                    | Some ndc ->
                        let tc = (ndc + V2d.II) * 0.5
                        tc - 0.5 * V2d(pixelSize, pixelSize) / V2d size
                    | None -> V2d.Zero
                )
            let textureScale = viewportSize |> AVal.map (fun s -> V2d pixelSize / V2d s)
            Sg.Uniform("TextureOffset", textureOffset)
            Sg.Uniform("TextureScale",  textureScale)
            Sg.Uniform("SliceIndex",    sliceIndex)
            Sg.View Trafo3d.Identity
            Sg.Proj Trafo3d.Identity
            Sg.Uniform("ColorTexture",  colorArrTex)
            Sg.Trafo t
            Sg.Shader { DefaultSurfaces.trafo; BlitShader.readArraySliceColor }
            Primitives.FullscreenQuad
        }

    let build
        (env : Env<Message>)
        (info : Aardvark.Dom.RenderControlInfo)
        (view : aval<Trafo3d>)
        (proj : aval<Trafo3d>)
        (revolverBase     : aval<option<V2d>>)
        (revolverActive   : aval<bool>)
        (fullscreenActive : aval<bool>)
        (model : AdaptiveModel) =
        
        let loadFinished (name : string) =
            env.Emit [ LoadFinished name ]
        
        let cnt, colors, depths, meshIndices = MeshView.buildMeshTextures info loadFinished view proj model
        let colorArrTex = colors |> AdaptiveResource.map (fun t -> t :> ITexture)
        let depthArrTex = depths |> AdaptiveResource.map (fun t -> t :> ITexture)

        let sliceOf name =
            meshIndices |> AVal.map (fun m -> 2 * (Map.tryFind name m |> Option.defaultValue 0))

        let clipMin = AVal.map2 (fun (b : Box3d) cc -> b.Min - cc) model.ClipBox model.CommonCentroid
        let clipMax = AVal.map2 (fun (b : Box3d) cc -> b.Max - cc) model.ClipBox model.CommonCentroid

        let meshVisibilityMask =
            (model.MeshVisible, meshIndices) ||> AVal.map2 (fun vis indices ->
                indices |> Map.fold (fun mask name i ->
                    if Map.tryFind name vis |> Option.defaultValue true then mask ||| (1 <<< i) else mask
                ) 0
            )

        let composite =
            sg {
                Sg.Active (AVal.map not fullscreenActive)
                MeshView.composeMeshTextures cnt colors depths model.DifferenceRendering model.MinDifferenceDepth model.MaxDifferenceDepth clipMin clipMax model.GhostSilhouette meshVisibilityMask
            }

        let fullscreenNodes =
            model.MeshNames |> AList.map (fun name ->
                let order = model.MeshOrder |> AMap.tryFind name |> AVal.map (Option.defaultValue 0)
                let trafo =
                    order |> AVal.map (fun o ->
                        if o = 0 then
                            Trafo3d.Translation(V3d(0.0, 0.0, 0.1))
                        else
                            let oi = float o - 1.0
                            Trafo3d.Scale(V3d(0.1, 0.1, 1.0))
                                * Trafo3d.Translation(V3d(0.9, 0.9, 0.0))
                                * Trafo3d.Translation(V3d(0.0, -oi * 0.2, 0.0))
                    )
                sg {
                    Sg.Active fullscreenActive
                    Sg.Shader { DefaultSurfaces.trafo; BlitShader.readArraySlice }
                    Sg.Trafo trafo
                    Sg.View Trafo3d.Identity
                    Sg.Proj Trafo3d.Identity
                    Sg.Uniform("ColorTexture", colorArrTex)
                    Sg.Uniform("DepthTexture", depthArrTex)
                    Sg.Uniform("SliceIndex",   sliceOf name)
                    Primitives.FullscreenQuad
                }
            ) |> AList.toASet

        let diskNodes =
            model.MeshNames |> AList.map (fun name ->
                let order = model.MeshOrder |> AMap.tryFind name |> AVal.map (Option.defaultValue 0)
                let renderPos =
                    (revolverBase, order, info.ViewportSize) |||> AVal.map3 (fun p o size ->
                        match p with
                        | Some p -> Some (p + 2.0 * float o * (V2d(0.0, 200.0) / V2d size))
                        | None   -> None
                    )
                disk revolverActive revolverBase colorArrTex info.ViewportSize (sliceOf name) renderPos
            ) |> AList.toASet

        let indicatorNodes = originIndicator view proj (AVal.map not fullscreenActive)

        let pinIdSet = model.ScanPins.Pins |> AMap.toASet |> ASet.map fst
        let pinsVal = model.ScanPins.Pins |> AMap.toAVal

        let pinDots =
            let notFullscreen = AVal.map not fullscreenActive
            let selectedId = model.ScanPins.SelectedPin
            pinIdSet |> ASet.map (fun id ->
                let pinVal = pinsVal |> AVal.map (fun pins -> HashMap.tryFind id pins)
                let color =
                    (selectedId, pinVal) ||> AVal.map2 (fun sel pinOpt ->
                        match pinOpt with
                        | Some pin ->
                            if sel = Some id then V4d(1.0, 0.9, 0.0, 1.0)
                            elif pin.Phase = PinPhase.Placement then V4d(0.2, 1.0, 0.3, 1.0)
                            else V4d(1.0, 0.3, 0.3, 1.0)
                        | None -> V4d(0.0, 0.0, 0.0, 0.0))
                let trafo = pinVal |> AVal.map (fun pinOpt ->
                    match pinOpt with
                    | Some pin -> Trafo3d.Scale(0.5) * Trafo3d.Translation(pin.Prism.AnchorPoint)
                    | None -> Trafo3d.Scale(0.0))
                sg {
                    Sg.Active notFullscreen
                    Sg.View view
                    Sg.Proj proj
                    Sg.Trafo trafo
                    Sg.Shader { DefaultSurfaces.trafo; Shader.flatColor }
                    Sg.Uniform("FlatColor", color)
                    Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
                    Sg.OnTap(fun _ ->
                        let sel = AVal.force selectedId
                        if sel = Some id then env.Emit [ScanPinMsg (SelectPin None)]
                        else env.Emit [ScanPinMsg (SelectPin (Some id))]
                        false)
                    Sg.OnDoubleTap(fun _ ->
                        env.Emit [ScanPinMsg (FocusPin id)]
                        false)
                    Sg.VertexAttributes(
                        HashMap.ofList [ string DefaultSemantic.Positions, BufferView(AVal.constant (ArrayBuffer boxPos :> IBuffer), typeof<V3f>) ]
                    )
                    Sg.Index(BufferView(AVal.constant (ArrayBuffer boxIdx :> IBuffer), typeof<int>))
                    Sg.Render (AVal.constant boxIdx.Length)
                }
            )

        let pinPrisms =
            let notFullscreen = AVal.map not fullscreenActive
            let selectedId = model.ScanPins.SelectedPin
            pinIdSet |> ASet.collect (fun id ->
                let pinVal = pinsVal |> AVal.map (fun pins -> HashMap.tryFind id pins)
                let isSelected = selectedId |> AVal.map (fun sel -> sel = Some id)
                let wireColor =
                    (selectedId, pinVal) ||> AVal.map2 (fun sel pinOpt ->
                        match pinOpt with
                        | Some pin ->
                            if sel = Some id then V4d(1.0, 0.85, 0.0, 0.7)
                            elif pin.Phase = PinPhase.Placement then V4d(0.2, 1.0, 0.3, 0.5)
                            else V4d(0.6, 0.6, 0.6, 0.35)
                        | None -> V4d(0.0, 0.0, 0.0, 0.0))
                let wireData = pinVal |> AVal.map (fun pinOpt ->
                    match pinOpt with
                    | Some pin -> buildPrismWireframe pin.Prism 0.05
                    | None -> [||], [||])
                let planeData = pinVal |> AVal.map (fun pinOpt ->
                    match pinOpt with
                    | Some pin -> buildCutPlaneQuad pin.Prism pin.CutPlane
                    | None -> [||], [||])
                let wirePos = wireData |> AVal.map fst
                let wireIdx = wireData |> AVal.map snd
                let planePos = planeData |> AVal.map fst
                let planeIdx = planeData |> AVal.map snd
                ASet.ofList [
                    sg {
                        Sg.Active notFullscreen
                        Sg.View view
                        Sg.Proj proj
                        Sg.Shader { DefaultSurfaces.trafo; Shader.flatColor }
                        Sg.Uniform("FlatColor", wireColor)
                        Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
                        Sg.NoEvents
                        Sg.VertexAttributes(
                            HashMap.ofList [ string DefaultSemantic.Positions, BufferView(wirePos |> AVal.map (fun p -> ArrayBuffer p :> IBuffer), typeof<V3f>) ])
                        Sg.Index(BufferView(wireIdx |> AVal.map (fun i -> ArrayBuffer i :> IBuffer), typeof<int>))
                        Sg.Render(wireIdx |> AVal.map Array.length)
                    }
                    sg {
                        Sg.Active (AVal.map2 (&&) notFullscreen isSelected)
                        Sg.View view
                        Sg.Proj proj
                        Sg.Shader { DefaultSurfaces.trafo; Shader.flatColor }
                        Sg.Uniform("FlatColor", AVal.constant (V4d(1.0, 0.9, 0.3, 0.25)))
                        Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
                        Sg.NoEvents
                        Sg.VertexAttributes(
                            HashMap.ofList [ string DefaultSemantic.Positions, BufferView(planePos |> AVal.map (fun p -> ArrayBuffer p :> IBuffer), typeof<V3f>) ])
                        Sg.Index(BufferView(planeIdx |> AVal.map (fun i -> ArrayBuffer i :> IBuffer), typeof<int>))
                        Sg.Render(planeIdx |> AVal.map Array.length)
                    }
                ]
            )

        // V3 Phase 2.11: extracted lines (cut-plane intersection lines and
        // cylinder edge curves), per-pin toggleable, vertex-colored ribbons drawn
        // without depth test so they sit on top of the geometry.
        let extractedLines =
            let notFullscreen = AVal.map not fullscreenActive
            let toV4f (c : C4b) =
                let f = c.ToC4f()
                V4f(f.R, f.G, f.B, 1.0f)

            let meshNamesVal = model.MeshNames |> AList.toAVal |> AVal.map IndexList.toArray
            pinIdSet |> ASet.collect (fun id ->
                let pinVal = pinsVal |> AVal.map (fun pins -> HashMap.tryFind id pins)
                let pinAndNames = (pinVal, meshNamesVal) ||> AVal.map2 (fun po n -> po, n)

                let cutGeo =
                    pinAndNames |> AVal.map (fun (pinOpt, names) ->
                        match pinOpt with
                        | Some pin when pin.ExtractedLines.ShowCutPlaneLines && not (Map.isEmpty pin.CutResults) ->
                            let prism = pin.Prism
                            let axis = prism.AxisDirection |> Vec.normalize
                            let up = if abs axis.Z > 0.9 then V3d.OIO else V3d.OOI
                            let right = Vec.cross axis up |> Vec.normalize
                            let fwd = Vec.cross right axis |> Vec.normalize
                            let planePoint, axisU, axisV, planeNormal =
                                match pin.CutPlane with
                                | CutPlaneMode.AlongAxis angleDeg ->
                                    let a = angleDeg * Constant.RadiansPerDegree
                                    let dir = right * cos a + fwd * sin a
                                    let normal = Vec.cross dir axis |> Vec.normalize
                                    prism.AnchorPoint, dir, axis, normal
                                | CutPlaneMode.AcrossAxis dist ->
                                    prism.AnchorPoint + axis * dist, right, fwd, axis
                            let explosion = Stratigraphy.explosionOffsets pin names
                            let positions = ResizeArray<V3f>()
                            let colors = ResizeArray<V4f>()
                            let indices = ResizeArray<int>()
                            let thickness = 0.06
                            for KeyValue(name, cr) in pin.CutResults do
                                let color = pin.DatasetColors |> Map.tryFind name |> Option.defaultValue (C4b(120uy,120uy,120uy)) |> toV4f
                                let off = Map.tryFind name explosion |> Option.defaultValue V3d.Zero
                                for poly in cr.Polylines do
                                    let pts3d =
                                        poly |> List.map (fun (p : V2d) -> planePoint + axisU * p.X + axisV * p.Y + off) |> Array.ofList
                                    PinGeometry.appendPolylineRibbon positions colors indices pts3d color planeNormal thickness
                            positions.ToArray(), colors.ToArray(), indices.ToArray()
                        | _ -> [||], [||], [||])

                let edgeGeo =
                    pinAndNames |> AVal.map (fun (pinOpt, names) ->
                        match pinOpt with
                        | Some pin when pin.ExtractedLines.ShowCylinderEdgeLines ->
                            match pin.Stratigraphy with
                            | Some data when data.Columns.Length >= 2 ->
                                let prism = pin.Prism
                                let axis = prism.AxisDirection |> Vec.normalize
                                let up = if abs axis.Z > 0.9 then V3d.OIO else V3d.OOI
                                let right = Vec.cross axis up |> Vec.normalize
                                let fwd = Vec.cross right axis |> Vec.normalize
                                let radius = match prism.Footprint.Vertices with v :: _ -> v.Length | _ -> 1.0
                                let explosion = Stratigraphy.explosionOffsets pin names
                                let toPoint (angle : float) (z : float) (off : V3d) =
                                    prism.AnchorPoint + (right * cos angle + fwd * sin angle) * radius + axis * z + off
                                let datasets =
                                    data.Columns
                                    |> Array.collect (fun c -> c.Events |> List.map snd |> List.toArray)
                                    |> Array.distinct
                                let positions = ResizeArray<V3f>()
                                let colors = ResizeArray<V4f>()
                                let indices = ResizeArray<int>()
                                let thickness = 0.05
                                for ds in datasets do
                                    let color = pin.DatasetColors |> Map.tryFind ds |> Option.defaultValue (C4b(120uy,120uy,120uy)) |> toV4f
                                    let off = Map.tryFind ds explosion |> Option.defaultValue V3d.Zero
                                    let perColumn =
                                        data.Columns |> Array.map (fun col ->
                                            col.Events
                                            |> List.filter (fun (_, n) -> n = ds)
                                            |> List.map fst
                                            |> List.sort)
                                    let maxLanes = perColumn |> Array.map List.length |> Array.fold max 0
                                    for lane in 0 .. maxLanes - 1 do
                                        let mutable accum = ResizeArray<V3d>()
                                        let flush () =
                                            if accum.Count >= 2 then
                                                PinGeometry.appendPolylineRibbon positions colors indices (accum.ToArray()) color axis thickness
                                            accum <- ResizeArray<V3d>()
                                        for ci in 0 .. data.Columns.Length - 1 do
                                            let zs = perColumn.[ci]
                                            if lane < zs.Length then
                                                accum.Add(toPoint data.Columns.[ci].Angle zs.[lane] off)
                                            else
                                                flush ()
                                        // Wrap-around: connect back to column 0 if both ends have this lane.
                                        if accum.Count > 0 then
                                            let zs0 = perColumn.[0]
                                            if lane < zs0.Length then
                                                accum.Add(toPoint data.Columns.[0].Angle zs0.[lane] off)
                                            flush ()
                                positions.ToArray(), colors.ToArray(), indices.ToArray()
                            | _ -> [||], [||], [||]
                        | _ -> [||], [||], [||])

                let cutPos = cutGeo |> AVal.map (fun (p,_,_) -> ArrayBuffer p :> IBuffer)
                let cutCol = cutGeo |> AVal.map (fun (_,c,_) -> ArrayBuffer c :> IBuffer)
                let cutIdx = cutGeo |> AVal.map (fun (_,_,i) -> ArrayBuffer i :> IBuffer)
                let cutCnt = cutGeo |> AVal.map (fun (_,_,i) -> i.Length)
                let edgePos = edgeGeo |> AVal.map (fun (p,_,_) -> ArrayBuffer p :> IBuffer)
                let edgeCol = edgeGeo |> AVal.map (fun (_,c,_) -> ArrayBuffer c :> IBuffer)
                let edgeIdx = edgeGeo |> AVal.map (fun (_,_,i) -> ArrayBuffer i :> IBuffer)
                let edgeCnt = edgeGeo |> AVal.map (fun (_,_,i) -> i.Length)

                ASet.ofList [
                    sg {
                        Sg.Active notFullscreen
                        Sg.View view
                        Sg.Proj proj
                        Sg.Shader { DefaultSurfaces.trafo; Shader.vertexColor }
                        Sg.DepthTest (AVal.constant DepthTest.None)
                        Sg.NoEvents
                        Sg.VertexAttributes(
                            HashMap.ofList [
                                string DefaultSemantic.Positions, BufferView(cutPos, typeof<V3f>)
                                string DefaultSemantic.Colors,    BufferView(cutCol, typeof<V4f>) ])
                        Sg.Index(BufferView(cutIdx, typeof<int>))
                        Sg.Render cutCnt
                    }
                    sg {
                        Sg.Active notFullscreen
                        Sg.View view
                        Sg.Proj proj
                        Sg.Shader { DefaultSurfaces.trafo; Shader.vertexColor }
                        Sg.DepthTest (AVal.constant DepthTest.None)
                        Sg.NoEvents
                        Sg.VertexAttributes(
                            HashMap.ofList [
                                string DefaultSemantic.Positions, BufferView(edgePos, typeof<V3f>)
                                string DefaultSemantic.Colors,    BufferView(edgeCol, typeof<V4f>) ])
                        Sg.Index(BufferView(edgeIdx, typeof<int>))
                        Sg.Render edgeCnt
                    }
                ])

        // V3 Phase 2.8/2.9: in-scene cut-plane controls.
        // 2.8: AcrossAxis rail+handle along the prism axis, dragged to set distance.
        // 2.9: AlongAxis translucent cap discs on each end, clicked to set angle.
        // Both visible only for the currently edited / selected pin and only for
        // the matching CutPlaneMode. Rendered without depth test.
        let cutPlaneSlider =
            let notFullscreen = AVal.map not fullscreenActive
            let selectedId = model.ScanPins.SelectedPin
            let activeId = model.ScanPins.ActivePlacement
            let editedPin =
                (selectedId, activeId, pinsVal) |||> AVal.map3 (fun sel act pins ->
                    let id = act |> Option.orElse sel
                    id |> Option.bind (fun id -> HashMap.tryFind id pins))

            let dragInfo : cval<option<V3d * V3d * float * float>> = cval None
            // For AlongAxis cap drag: stores the cap plane (point on plane + axis normal).
            let discDrag : cval<option<V3d * V3d>> = cval None

            let geometry =
                editedPin |> AVal.map (fun pinOpt ->
                    match pinOpt with
                    | Some pin ->
                        match pin.CutPlane with
                        | CutPlaneMode.AcrossAxis dist ->
                            let axis = pin.Prism.AxisDirection |> Vec.normalize
                            let r = match pin.Prism.Footprint.Vertices with v :: _ -> v.Length | _ -> 1.0
                            let railThick = max 0.04 (r * 0.06)
                            let handleSize = max 0.18 (r * 0.22)
                            let a0 = pin.Prism.AnchorPoint - axis * pin.Prism.ExtentBackward
                            let a1 = pin.Prism.AnchorPoint + axis * pin.Prism.ExtentForward
                            let railP, railI = PinGeometry.buildLineTube a0 a1 railThick
                            let handleC = pin.Prism.AnchorPoint + axis * dist
                            let handleP, handleI = PinGeometry.buildHandleBox handleC axis handleSize
                            railP, railI, handleP, handleI, true
                        | _ -> [||], [||], [||], [||], false
                    | None -> [||], [||], [||], [||], false)

            let railPos = geometry |> AVal.map (fun (p,_,_,_,_) -> ArrayBuffer p :> IBuffer)
            let railIdx = geometry |> AVal.map (fun (_,i,_,_,_) -> ArrayBuffer i :> IBuffer)
            let railCnt = geometry |> AVal.map (fun (_,i,_,_,_) -> i.Length)
            let handlePos = geometry |> AVal.map (fun (_,_,p,_,_) -> ArrayBuffer p :> IBuffer)
            let handleIdx = geometry |> AVal.map (fun (_,_,_,i,_) -> ArrayBuffer i :> IBuffer)
            let handleCnt = geometry |> AVal.map (fun (_,_,_,i,_) -> i.Length)
            let acrossActive = (notFullscreen, geometry) ||> AVal.map2 (fun nf (_,_,_,_,act) -> nf && act)

            let alongGeometry =
                editedPin |> AVal.map (fun pinOpt ->
                    match pinOpt with
                    | Some pin ->
                        match pin.CutPlane with
                        | CutPlaneMode.AlongAxis angleDeg ->
                            let axis = pin.Prism.AxisDirection |> Vec.normalize
                            let r = match pin.Prism.Footprint.Vertices with v :: _ -> v.Length | _ -> 1.0
                            let up = if abs axis.Z > 0.9 then V3d.OIO else V3d.OOI
                            let right = Vec.cross axis up |> Vec.normalize
                            let fwd = Vec.cross right axis |> Vec.normalize
                            let topC = pin.Prism.AnchorPoint + axis * pin.Prism.ExtentForward
                            let botC = pin.Prism.AnchorPoint - axis * pin.Prism.ExtentBackward
                            let topP, topI = PinGeometry.buildDisc topC axis r 64
                            let botP, botI = PinGeometry.buildDisc botC axis r 64
                            let a = angleDeg * Constant.RadiansPerDegree
                            let dirR = right * cos a + fwd * sin a
                            let lineThick = max 0.04 (r * 0.05)
                            let topLineP, topLineI = PinGeometry.buildLineTube (topC - dirR * r) (topC + dirR * r) lineThick
                            let botLineP, botLineI = PinGeometry.buildLineTube (botC - dirR * r) (botC + dirR * r) lineThick
                            topP, topI, botP, botI, topLineP, topLineI, botLineP, botLineI, true
                        | _ -> [||], [||], [||], [||], [||], [||], [||], [||], false
                    | None -> [||], [||], [||], [||], [||], [||], [||], [||], false)

            let topDiscPos = alongGeometry |> AVal.map (fun (p,_,_,_,_,_,_,_,_) -> ArrayBuffer p :> IBuffer)
            let topDiscIdx = alongGeometry |> AVal.map (fun (_,i,_,_,_,_,_,_,_) -> ArrayBuffer i :> IBuffer)
            let topDiscCnt = alongGeometry |> AVal.map (fun (_,i,_,_,_,_,_,_,_) -> i.Length)
            let botDiscPos = alongGeometry |> AVal.map (fun (_,_,p,_,_,_,_,_,_) -> ArrayBuffer p :> IBuffer)
            let botDiscIdx = alongGeometry |> AVal.map (fun (_,_,_,i,_,_,_,_,_) -> ArrayBuffer i :> IBuffer)
            let botDiscCnt = alongGeometry |> AVal.map (fun (_,_,_,i,_,_,_,_,_) -> i.Length)
            let topLinePos = alongGeometry |> AVal.map (fun (_,_,_,_,p,_,_,_,_) -> ArrayBuffer p :> IBuffer)
            let topLineIdx = alongGeometry |> AVal.map (fun (_,_,_,_,_,i,_,_,_) -> ArrayBuffer i :> IBuffer)
            let topLineCnt = alongGeometry |> AVal.map (fun (_,_,_,_,_,i,_,_,_) -> i.Length)
            let botLinePos = alongGeometry |> AVal.map (fun (_,_,_,_,_,_,p,_,_) -> ArrayBuffer p :> IBuffer)
            let botLineIdx = alongGeometry |> AVal.map (fun (_,_,_,_,_,_,_,i,_) -> ArrayBuffer i :> IBuffer)
            let botLineCnt = alongGeometry |> AVal.map (fun (_,_,_,_,_,_,_,i,_) -> i.Length)
            let alongActive = (notFullscreen, alongGeometry) ||> AVal.map2 (fun nf (_,_,_,_,_,_,_,_,act) -> nf && act)

            let emitAngleFromHit (hit : V3d) =
                match AVal.force editedPin with
                | Some pin ->
                    let axis = pin.Prism.AxisDirection |> Vec.normalize
                    let up = if abs axis.Z > 0.9 then V3d.OIO else V3d.OOI
                    let right = Vec.cross axis up |> Vec.normalize
                    let fwd = Vec.cross right axis |> Vec.normalize
                    let v = hit - pin.Prism.AnchorPoint
                    let lx = Vec.dot v right
                    let ly = Vec.dot v fwd
                    let ang = atan2 ly lx * Constant.DegreesPerRadian
                    env.Emit [ScanPinMsg (SetCutPlaneAngle ang)]
                | None -> ()

            let pickRayOf (e : ScenePointerEvent) =
                let vp = e.ViewProjTrafo
                let pixel = e.Pixel
                let s = e.ViewportSize
                let ndc = V2d(2.0 * float pixel.X / float s.X - 1.0, 1.0 - 2.0 * float pixel.Y / float s.Y)
                let p0 = vp.Backward.TransformPosProj(V3d(ndc, -1.0))
                let p1 = vp.Backward.TransformPosProj(V3d(ndc, 1.0))
                Ray3d(p0, (p1 - p0) |> Vec.normalize)

            let intersectPlane (ray : Ray3d) (planePt : V3d) (planeN : V3d) =
                let denom = Vec.dot ray.Direction planeN
                if abs denom < 1e-10 then None
                else
                    let t = Vec.dot (planePt - ray.Origin) planeN / denom
                    if t < 0.0 then None
                    else Some (ray.Origin + ray.Direction * t)

            let updateAngleFromPointer (e : ScenePointerEvent) =
                match AVal.force discDrag with
                | Some (planePt, planeN) ->
                    match intersectPlane (pickRayOf e) planePt planeN with
                    | Some hit -> emitAngleFromHit hit
                    | None -> ()
                | None -> ()

            let updateFromPointer (e : ScenePointerEvent) =
                match AVal.force dragInfo with
                | Some (anchor, axis, extBack, extFwd) ->
                    let ray = pickRayOf e
                    let railRay = Ray3d(anchor, axis)
                    let closest = ray.GetClosestPointOn(railRay)
                    let t = railRay.GetTOfProjectedPoint(closest)
                    let dist = clamp -extBack extFwd t
                    env.Emit [ScanPinMsg (SetCutPlaneDistance dist)]
                | None -> ()

            ASet.ofList [
                sg {
                    Sg.Active acrossActive
                    Sg.View view
                    Sg.Proj proj
                    Sg.Shader { DefaultSurfaces.trafo; Shader.flatColor }
                    Sg.Uniform("FlatColor", AVal.constant (V4d(0.1, 0.34, 0.86, 1.0)))
                    Sg.DepthTest (AVal.constant DepthTest.None)
                    Sg.NoEvents
                    Sg.VertexAttributes(
                        HashMap.ofList [ string DefaultSemantic.Positions, BufferView(railPos, typeof<V3f>) ])
                    Sg.Index(BufferView(railIdx, typeof<int>))
                    Sg.Render railCnt
                }
                sg {
                    Sg.Active acrossActive
                    Sg.View view
                    Sg.Proj proj
                    Sg.Shader { DefaultSurfaces.trafo; Shader.flatColor }
                    Sg.Uniform("FlatColor", AVal.constant (V4d(1.0, 1.0, 1.0, 1.0)))
                    Sg.DepthTest (AVal.constant DepthTest.None)
                    Sg.OnPointerDown(true, fun e ->
                        match AVal.force editedPin with
                        | Some pin ->
                            match pin.CutPlane with
                            | CutPlaneMode.AcrossAxis _ ->
                                let axis = pin.Prism.AxisDirection |> Vec.normalize
                                transact (fun () ->
                                    dragInfo.Value <- Some (pin.Prism.AnchorPoint, axis, pin.Prism.ExtentBackward, pin.Prism.ExtentForward))
                                updateFromPointer e
                                false
                            | _ -> true
                        | None -> true)
                    Sg.OnPointerMove(fun e ->
                        if (AVal.force dragInfo).IsSome then updateFromPointer e)
                    Sg.OnPointerUp(true, fun _ ->
                        if (AVal.force dragInfo).IsSome then
                            transact (fun () -> dragInfo.Value <- None))
                    Sg.VertexAttributes(
                        HashMap.ofList [ string DefaultSemantic.Positions, BufferView(handlePos, typeof<V3f>) ])
                    Sg.Index(BufferView(handleIdx, typeof<int>))
                    Sg.Render handleCnt
                }
                // AlongAxis: top cap disc (pickable).
                sg {
                    Sg.Active alongActive
                    Sg.View view
                    Sg.Proj proj
                    Sg.Shader { DefaultSurfaces.trafo; Shader.flatColor }
                    Sg.Uniform("FlatColor", AVal.constant (V4d(0.1, 0.34, 0.86, 0.18)))
                    Sg.DepthTest (AVal.constant DepthTest.None)
                    Sg.OnPointerDown(true, fun e ->
                        match AVal.force editedPin with
                        | Some pin ->
                            match pin.CutPlane with
                            | CutPlaneMode.AlongAxis _ ->
                                let axis = pin.Prism.AxisDirection |> Vec.normalize
                                let topC = pin.Prism.AnchorPoint + axis * pin.Prism.ExtentForward
                                transact (fun () -> discDrag.Value <- Some (topC, axis))
                                emitAngleFromHit e.WorldPosition
                                false
                            | _ -> true
                        | None -> true)
                    Sg.OnPointerMove(fun e ->
                        if (AVal.force discDrag).IsSome then updateAngleFromPointer e)
                    Sg.OnPointerUp(true, fun _ ->
                        if (AVal.force discDrag).IsSome then
                            transact (fun () -> discDrag.Value <- None))
                    Sg.VertexAttributes(
                        HashMap.ofList [ string DefaultSemantic.Positions, BufferView(topDiscPos, typeof<V3f>) ])
                    Sg.Index(BufferView(topDiscIdx, typeof<int>))
                    Sg.Render topDiscCnt
                }
                // AlongAxis: bottom cap disc (pickable).
                sg {
                    Sg.Active alongActive
                    Sg.View view
                    Sg.Proj proj
                    Sg.Shader { DefaultSurfaces.trafo; Shader.flatColor }
                    Sg.Uniform("FlatColor", AVal.constant (V4d(0.1, 0.34, 0.86, 0.18)))
                    Sg.DepthTest (AVal.constant DepthTest.None)
                    Sg.OnPointerDown(true, fun e ->
                        match AVal.force editedPin with
                        | Some pin ->
                            match pin.CutPlane with
                            | CutPlaneMode.AlongAxis _ ->
                                let axis = pin.Prism.AxisDirection |> Vec.normalize
                                let botC = pin.Prism.AnchorPoint - axis * pin.Prism.ExtentBackward
                                transact (fun () -> discDrag.Value <- Some (botC, axis))
                                emitAngleFromHit e.WorldPosition
                                false
                            | _ -> true
                        | None -> true)
                    Sg.OnPointerMove(fun e ->
                        if (AVal.force discDrag).IsSome then updateAngleFromPointer e)
                    Sg.OnPointerUp(true, fun _ ->
                        if (AVal.force discDrag).IsSome then
                            transact (fun () -> discDrag.Value <- None))
                    Sg.VertexAttributes(
                        HashMap.ofList [ string DefaultSemantic.Positions, BufferView(botDiscPos, typeof<V3f>) ])
                    Sg.Index(BufferView(botDiscIdx, typeof<int>))
                    Sg.Render botDiscCnt
                }
                // AlongAxis: top diameter line (current angle).
                sg {
                    Sg.Active alongActive
                    Sg.View view
                    Sg.Proj proj
                    Sg.Shader { DefaultSurfaces.trafo; Shader.flatColor }
                    Sg.Uniform("FlatColor", AVal.constant (V4d(1.0, 1.0, 1.0, 1.0)))
                    Sg.DepthTest (AVal.constant DepthTest.None)
                    Sg.NoEvents
                    Sg.VertexAttributes(
                        HashMap.ofList [ string DefaultSemantic.Positions, BufferView(topLinePos, typeof<V3f>) ])
                    Sg.Index(BufferView(topLineIdx, typeof<int>))
                    Sg.Render topLineCnt
                }
                // AlongAxis: bottom diameter line (current angle).
                sg {
                    Sg.Active alongActive
                    Sg.View view
                    Sg.Proj proj
                    Sg.Shader { DefaultSurfaces.trafo; Shader.flatColor }
                    Sg.Uniform("FlatColor", AVal.constant (V4d(1.0, 1.0, 1.0, 1.0)))
                    Sg.DepthTest (AVal.constant DepthTest.None)
                    Sg.NoEvents
                    Sg.VertexAttributes(
                        HashMap.ofList [ string DefaultSemantic.Positions, BufferView(botLinePos, typeof<V3f>) ])
                    Sg.Index(BufferView(botLineIdx, typeof<int>))
                    Sg.Render botLineCnt
                }
            ]

        ASet.unionMany (ASet.ofList [ASet.single composite; fullscreenNodes; diskNodes; indicatorNodes; pinDots; pinPrisms; extractedLines; cutPlaneSlider])
