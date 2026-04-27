namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open Aardworx.WebAssembly
open FSharp.Data.Adaptive
open Aardvark.Dom

module ScanPinScene =

    let private boxPos =
        [|  V3f(-0.5f, -0.5f, -0.5f); V3f( 0.5f, -0.5f, -0.5f); V3f( 0.5f,  0.5f, -0.5f); V3f(-0.5f,  0.5f, -0.5f)
            V3f(-0.5f, -0.5f,  0.5f); V3f( 0.5f, -0.5f,  0.5f); V3f( 0.5f,  0.5f,  0.5f); V3f(-0.5f,  0.5f,  0.5f) |]
    let private boxIdx =
        [| 0;1;2; 0;2;3;  5;4;7; 5;7;6;  4;0;3; 4;3;7;  1;5;6; 1;6;2;  0;4;5; 0;5;1;  3;2;6; 3;6;7 |]

    let private pinMarkerPos, pinMarkerIdx =
        let pos = System.Collections.Generic.List<V3f>()
        let idx = System.Collections.Generic.List<int>()
        let addBox (hx : float) (hy : float) (hz : float) =
            let base0 = pos.Count
            pos.Add (V3f(-hx, -hy, -hz)); pos.Add (V3f( hx, -hy, -hz))
            pos.Add (V3f( hx,  hy, -hz)); pos.Add (V3f(-hx,  hy, -hz))
            pos.Add (V3f(-hx, -hy,  hz)); pos.Add (V3f( hx, -hy,  hz))
            pos.Add (V3f( hx,  hy,  hz)); pos.Add (V3f(-hx,  hy,  hz))
            let offs = [| 0;1;2; 0;2;3;  5;4;7; 5;7;6;  4;0;3; 4;3;7
                          1;5;6; 1;6;2;  0;4;5; 0;5;1;  3;2;6; 3;6;7 |]
            for o in offs do idx.Add(base0 + o)
        addBox 0.03 0.03 0.5
        addBox 0.18 0.025 0.025
        addBox 0.025 0.18 0.025
        pos.ToArray(), idx.ToArray()

    let build
            (env : Env<Message>)
            (view : aval<Trafo3d>) (proj : aval<Trafo3d>)
            (fullscreenActive : aval<bool>)
            (model : AdaptiveModel) =

        let notFullscreen = AVal.map not fullscreenActive
        let selectedId = model.ScanPins.SelectedPin
        let pinIdSet = model.ScanPins.Pins |> AMap.toASet |> ASet.map fst
        let pinsVal = model.ScanPins.Pins |> AMap.toAVal

        let pinDots =
            pinIdSet |> ASet.map (fun id ->
                let pinVal = pinsVal |> AVal.map (fun pins -> HashMap.tryFind id pins)
                let phaseVal = pinVal |> AVal.map (Option.map (fun p -> p.Phase))
                let anchorVal = pinVal |> AVal.map (Option.map (fun p -> p.Prism.AnchorPoint))
                let axisVal = pinVal |> AVal.map (Option.map (fun p -> p.Prism.AxisDirection))
                let color =
                    (selectedId, phaseVal) ||> AVal.map2 (fun sel phaseOpt ->
                        match phaseOpt with
                        | Some phase ->
                            if sel = Some id then V4d(1.0, 0.9, 0.0, 1.0)
                            elif phase = PinPhase.Placement then V4d(0.2, 1.0, 0.3, 1.0)
                            else V4d(1.0, 0.3, 0.3, 1.0)
                        | None -> V4d(0.0, 0.0, 0.0, 0.0))
                let trafo =
                    (anchorVal, axisVal) ||> AVal.map2 (fun aOpt xOpt ->
                        match aOpt, xOpt with
                        | Some a, Some axis ->
                            let axis = Vec.normalize axis
                            let right, fwd = PinGeometry.axisFrame axis
                            let rotM =
                                M44d(right.X, fwd.X, axis.X, 0.0,
                                     right.Y, fwd.Y, axis.Y, 0.0,
                                     right.Z, fwd.Z, axis.Z, 0.0,
                                     0.0,     0.0,   0.0,    1.0)
                            Trafo3d(rotM, rotM.Transposed) * Trafo3d.Translation(a)
                        | _ -> Trafo3d.Scale(0.0))
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
                        HashMap.ofList [ string DefaultSemantic.Positions, BufferView(AVal.constant (ArrayBuffer pinMarkerPos :> IBuffer), typeof<V3f>) ]
                    )
                    Sg.Index(BufferView(AVal.constant (ArrayBuffer pinMarkerIdx :> IBuffer), typeof<int>))
                    Sg.Render (AVal.constant pinMarkerIdx.Length)
                }
            )

        let pinPrisms =
            pinIdSet |> ASet.collect (fun id ->
                let pinVal = pinsVal |> AVal.map (fun pins -> HashMap.tryFind id pins)
                let isSelected = selectedId |> AVal.map (fun sel -> sel = Some id)
                let prismVal = pinVal |> AVal.map (Option.map (fun p -> p.Prism))
                let cutPlaneVal = pinVal |> AVal.map (Option.map (fun p -> p.CutPlane))
                let planeData =
                    (prismVal, cutPlaneVal) ||> AVal.map2 (fun po co ->
                        match po, co with
                        | Some prism, Some cp -> PinGeometry.buildCutPlaneRefined prism cp
                        | _ -> [||], [||], [||])
                let planePos = planeData |> AVal.map (fun (p,_,_) -> p)
                let planeCol = planeData |> AVal.map (fun (_,c,_) -> c)
                let planeIdx = planeData |> AVal.map (fun (_,_,i) -> i)
                let tickCounts =
                    prismVal |> AVal.map (fun po ->
                        match po with
                        | Some prism ->
                            let r = match prism.Footprint.Vertices with v :: _ -> v.Length | _ -> 1.0
                            let hw = r * 1.2
                            let hh = (prism.ExtentForward + prism.ExtentBackward) * 0.5
                            int (hw * 2.0), int (hh * 2.0)
                        | None -> 0, 0)
                let tickSet =
                    tickCounts |> AVal.map (fun (nU, nV) ->
                        seq {
                            for i in 0 .. nU do yield PinGeometry.EdgeU, i
                            for i in 0 .. nV do yield PinGeometry.EdgeV, i
                        })
                    |> ASet.ofAVal
                let isActiveAndSelected = AVal.map2 (&&) notFullscreen isSelected
                let labelNodes =
                    tickSet |> ASet.map (fun (edge, i) ->
                        let tickI = float i
                        let text =
                            prismVal |> AVal.map (fun po ->
                                match po with
                                | Some prism ->
                                    let r = match prism.Footprint.Vertices with v :: _ -> v.Length | _ -> 1.0
                                    let hw = r * 1.2
                                    let hh = (prism.ExtentForward + prism.ExtentBackward) * 0.5
                                    let localCoord =
                                        match edge with
                                        | PinGeometry.EdgeU -> tickI - hw
                                        | PinGeometry.EdgeV -> tickI - hh
                                    if abs localCoord < 0.005 then "0" else sprintf "%.2f" localCoord
                                | None -> "")
                        let trafo =
                            (prismVal, cutPlaneVal) ||> AVal.map2 (fun po co ->
                                match po, co with
                                | Some prism, Some cp ->
                                    let r = match prism.Footprint.Vertices with v :: _ -> v.Length | _ -> 1.0
                                    let extentU = r * 2.4
                                    let extentV = prism.ExtentForward + prism.ExtentBackward
                                    let unitT =
                                        match edge with
                                        | PinGeometry.EdgeU -> min 1.0 (tickI / max 1e-9 extentU)
                                        | PinGeometry.EdgeV -> min 1.0 (tickI / max 1e-9 extentV)
                                    PinGeometry.tickLabelWorldTrafo prism cp edge unitT
                                | _ -> Trafo3d.Identity)
                        sg {
                            Sg.Active isActiveAndSelected
                            Sg.View view
                            Sg.Proj proj
                            Sg.Pass RenderPass.passTwo
                            Sg.Trafo trafo
                            Sg.Text(text, color = AVal.constant (C4b(160uy, 160uy, 160uy)), align = TextAlignment.Center)
                        })
                let centroidTrafo =
                    (prismVal, cutPlaneVal) ||> AVal.map2 (fun po co ->
                        match po, co with
                        | Some prism, Some cp -> PinGeometry.centroidLabelTrafo prism cp
                        | _ -> Trafo3d.Identity)
                let centroidText =
                    model.CommonCentroid |> AVal.map (fun cc ->
                        sprintf "%.2f, %.2f, %.2f" cc.X cc.Y cc.Z)
                let centroidNode =
                    ASet.ofList [
                        sg {
                            Sg.Active isActiveAndSelected
                            Sg.View view
                            Sg.Proj proj
                            Sg.Pass RenderPass.passTwo
                            Sg.Trafo centroidTrafo
                            Sg.Text(centroidText, color = AVal.constant (C4b(130uy, 140uy, 160uy)), align = TextAlignment.Center)
                        }
                    ]
                ASet.union
                    (ASet.ofList [
                        sg {
                            Sg.Active isActiveAndSelected
                            Sg.View view
                            Sg.Proj proj
                            Sg.Pass RenderPass.passOne
                            Sg.Shader { DefaultSurfaces.trafo; Shader.vertexColor }
                            Sg.BlendMode BlendMode.Blend
                            Sg.DepthTest (AVal.constant DepthTest.None)
                            Sg.NoEvents
                            Sg.VertexAttributes(
                                HashMap.ofList [
                                    string DefaultSemantic.Positions, BufferView(planePos |> AVal.map (fun p -> ArrayBuffer p :> IBuffer), typeof<V3f>)
                                    string DefaultSemantic.Colors,    BufferView(planeCol |> AVal.map (fun c -> ArrayBuffer c :> IBuffer), typeof<V4f>)
                                ])
                            Sg.Index(BufferView(planeIdx |> AVal.map (fun i -> ArrayBuffer i :> IBuffer), typeof<int>))
                            Sg.Render(planeIdx |> AVal.map Array.length)
                        }
                    ])
                    (ASet.unionMany (ASet.ofList [labelNodes; centroidNode]))
            )

        let betweenSpaceBand =
            pinIdSet |> ASet.collect (fun id ->
                let pinVal = pinsVal |> AVal.map (fun pins -> HashMap.tryFind id pins)
                let isSelected = selectedId |> AVal.map (fun sel -> sel = Some id)
                let bsEnabled = model.ScanPins.BetweenSpaceEnabled
                let isActiveAndSelected = (notFullscreen, isSelected, bsEnabled) |||> AVal.map3 (fun nf sel bs -> nf && sel && bs)
                let prismVal = pinVal |> AVal.map (Option.map (fun p -> p.Prism))
                let stratVal = pinVal |> AVal.map (Option.bind (fun p -> p.Stratigraphy))
                let cacheVal = pinVal |> AVal.map (Option.bind (fun p -> p.BandCache))
                let hoverVal =
                    (pinVal, bsEnabled) ||> AVal.map2 (fun po enabled ->
                        if not enabled then None
                        else po |> Option.bind (fun p -> p.BetweenSpaceHover)
                                |> Option.map (fun h -> h.ColumnIdx, h.HoverZ))
                let prismCacheVal = (prismVal, cacheVal) ||> AVal.map2 (fun a b -> a, b)
                let geo =
                    (prismCacheVal, stratVal, hoverVal) |||> AVal.map3 (fun (prismO, cacheO) dataO hOpt ->
                        match prismO, dataO, hOpt with
                        | Some prism, Some data, Some (col, z) -> PinGeometry.buildBetweenSpaceSurfaces prism data cacheO col z
                        | _ -> ([||], [||]), ([||], [||]), ([||], [||]))
                let dummyP = [| V3f.Zero |]
                let safeP (a : V3f[]) = if a.Length = 0 then dummyP else a
                let upperPos = geo |> AVal.map (fun ((p, _), _, _) -> ArrayBuffer (safeP p) :> IBuffer)
                let upperIdx = geo |> AVal.map (fun ((_, i), _, _) -> ArrayBuffer i :> IBuffer)
                let upperCnt = geo |> AVal.map (fun ((_, i), _, _) -> i.Length)
                let lowerPos = geo |> AVal.map (fun (_, (p, _), _) -> ArrayBuffer (safeP p) :> IBuffer)
                let lowerIdx = geo |> AVal.map (fun (_, (_, i), _) -> ArrayBuffer i :> IBuffer)
                let lowerCnt = geo |> AVal.map (fun (_, (_, i), _) -> i.Length)
                let sidePos  = geo |> AVal.map (fun (_, _, (p, _)) -> ArrayBuffer (safeP p) :> IBuffer)
                let sideIdx  = geo |> AVal.map (fun (_, _, (_, i)) -> ArrayBuffer i :> IBuffer)
                let sideCnt  = geo |> AVal.map (fun (_, _, (_, i)) -> i.Length)
                let makeSurface (color : V4d) (positions : aval<IBuffer>) (idx : aval<IBuffer>) (cnt : aval<int>) =
                    sg {
                        Sg.Active isActiveAndSelected
                        Sg.View view
                        Sg.Proj proj
                        Sg.Pass RenderPass.passTwo
                        Sg.Shader { DefaultSurfaces.trafo; Shader.flatColor }
                        Sg.Uniform("FlatColor", AVal.constant color)
                        Sg.BlendMode BlendMode.Blend
                        Sg.DepthTest (AVal.constant DepthTest.None)
                        Sg.NoEvents
                        Sg.VertexAttributes(
                            HashMap.ofList [
                                string DefaultSemantic.Positions, BufferView(positions, typeof<V3f>)
                            ])
                        Sg.Index(BufferView(idx, typeof<int>))
                        Sg.Render cnt
                    }
                ASet.ofList [
                    makeSurface (V4d(1.0, 1.0, 0.9, 0.55)) upperPos upperIdx upperCnt
                    makeSurface (V4d(1.0, 0.95, 0.7, 0.55)) lowerPos lowerIdx lowerCnt
                    makeSurface (V4d(1.0, 0.97, 0.8, 0.40)) sidePos  sideIdx  sideCnt
                ]
            )

        let extractedLines =
            let toV4f (c : C4b) =
                let f = c.ToC4f()
                V4f(f.R, f.G, f.B, 1.0f)

            let meshNamesVal = model.MeshNames |> AList.toAVal |> AVal.map IndexList.toArray
            pinIdSet |> ASet.collect (fun id ->
                let pinVal = pinsVal |> AVal.map (fun pins -> HashMap.tryFind id pins)

                let cutResultsDep =
                    pinVal |> AVal.map (fun po ->
                        po |> Option.bind (fun p ->
                            if p.ExtractedLines.ShowCutPlaneLines && not (Map.isEmpty p.CutResults)
                            then Some (p.Prism, p.CutResultsPlane, p.CutResults, p.DatasetColors, p.Stratigraphy)
                            else None))
                let edgeDeps =
                    pinVal |> AVal.map (fun po ->
                        po |> Option.bind (fun p ->
                            if p.ExtractedLines.ShowCylinderEdgeLines
                            then Some (p.Prism, p.Stratigraphy, p.DatasetColors)
                            else None))

                let cutGeo =
                    (cutResultsDep, meshNamesVal) ||> AVal.map2 (fun depsOpt names ->
                        match depsOpt with
                        | Some(prism, cutPlane, cutResults, datasetColors, strat) ->
                            let axis = prism.AxisDirection |> Vec.normalize
                            let right, fwd = PinGeometry.axisFrame axis
                            let planePoint, axisU, axisV, planeNormal =
                                match cutPlane with
                                | CutPlaneMode.AlongAxis angleDeg ->
                                    let a = angleDeg * Constant.RadiansPerDegree
                                    let dir = right * cos a + fwd * sin a
                                    let normal = Vec.cross dir axis |> Vec.normalize
                                    prism.AnchorPoint, dir, axis, normal
                                | CutPlaneMode.AcrossAxis dist ->
                                    prism.AnchorPoint + axis * dist, right, fwd, axis
                            let positions = ResizeArray<V3f>()
                            let colors = ResizeArray<V4f>()
                            let indices = ResizeArray<int>()
                            let thickness = 0.06
                            for KeyValue(name, cr) in cutResults do
                                let color = datasetColors |> Map.tryFind name |> Option.defaultValue (C4b(120uy,120uy,120uy)) |> toV4f
                                for poly in cr.Polylines do
                                    let pts3d =
                                        poly |> List.map (fun (p : V2d) -> planePoint + axisU * p.X + axisV * p.Y) |> Array.ofList
                                    PinGeometry.appendPolylineRibbon positions colors indices pts3d color planeNormal thickness
                            positions.ToArray(), colors.ToArray(), indices.ToArray()
                        | _ -> [||], [||], [||])

                let edgeGeo =
                    (edgeDeps, meshNamesVal) ||> AVal.map2 (fun depsOpt names ->
                        match depsOpt with
                        | Some(prism, Some data, datasetColors) when data.Columns.Length >= 2 ->
                            let axis = prism.AxisDirection |> Vec.normalize
                            let right, fwd = PinGeometry.axisFrame axis
                            let radius = match prism.Footprint.Vertices with v :: _ -> v.Length | _ -> 1.0
                            let toPoint (angle : float) (z : float) =
                                prism.AnchorPoint + (right * cos angle + fwd * sin angle) * radius + axis * z
                            let datasets =
                                data.Columns
                                |> Array.collect (fun c -> c.Events |> List.map snd |> List.toArray)
                                |> Array.distinct
                            let positions = ResizeArray<V3f>()
                            let colors = ResizeArray<V4f>()
                            let indices = ResizeArray<int>()
                            let thickness = 0.05
                            for ds in datasets do
                                let color = datasetColors |> Map.tryFind ds |> Option.defaultValue (C4b(120uy,120uy,120uy)) |> toV4f
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
                                            accum.Add(toPoint data.Columns.[ci].Angle zs.[lane])
                                        else
                                            flush ()
                                    if accum.Count > 0 then
                                        let zs0 = perColumn.[0]
                                        if lane < zs0.Length then
                                            accum.Add(toPoint data.Columns.[0].Angle zs0.[lane])
                                        flush ()
                            positions.ToArray(), colors.ToArray(), indices.ToArray()
                        | _ -> [||], [||], [||])

                let dummyP = [| V3f.Zero |]
                let dummyC = [| V4f.Zero |]
                let safeP (a : V3f[]) = if a.Length = 0 then dummyP else a
                let safeC (a : V4f[]) = if a.Length = 0 then dummyC else a
                let cutPos = cutGeo |> AVal.map (fun (p,_,_) -> ArrayBuffer (safeP p) :> IBuffer)
                let cutCol = cutGeo |> AVal.map (fun (_,c,_) -> ArrayBuffer (safeC c) :> IBuffer)
                let cutIdx = cutGeo |> AVal.map (fun (_,_,i) -> ArrayBuffer i :> IBuffer)
                let cutCnt = cutGeo |> AVal.map (fun (_,_,i) -> i.Length)
                let edgePos = edgeGeo |> AVal.map (fun (p,_,_) -> ArrayBuffer (safeP p) :> IBuffer)
                let edgeCol = edgeGeo |> AVal.map (fun (_,c,_) -> ArrayBuffer (safeC c) :> IBuffer)
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

        let cutHoverMarker =
            pinIdSet |> ASet.collect (fun id ->
                let pinVal = pinsVal |> AVal.map (fun pins -> HashMap.tryFind id pins)
                let isSelected = selectedId |> AVal.map (fun sel -> sel = Some id)
                let hoverVal = pinVal |> AVal.map (Option.bind (fun p -> p.CutLineHover))
                let colorMapVal = pinVal |> AVal.map (Option.map (fun p -> p.DatasetColors) >> Option.defaultValue Map.empty)
                let active =
                    (notFullscreen, isSelected, hoverVal) |||> AVal.map3 (fun nf sel h -> nf && sel && h.IsSome)
                let trafo =
                    hoverVal |> AVal.map (function
                        | Some h -> Trafo3d.Scale(0.25) * Trafo3d.Translation(h.WorldPos)
                        | None -> Trafo3d.Scale(0.0))
                let color =
                    (hoverVal, colorMapVal) ||> AVal.map2 (fun hOpt colorMap ->
                        match hOpt with
                        | Some h ->
                            let c = colorMap |> Map.tryFind h.MeshName |> Option.defaultValue (C4b(100uy,100uy,100uy))
                            let f = c.ToC4f()
                            V4d(float f.R, float f.G, float f.B, 1.0)
                        | None -> V4d.Zero)
                ASet.ofList [
                    sg {
                        Sg.Active active
                        Sg.View view
                        Sg.Proj proj
                        Sg.Trafo trafo
                        Sg.Pass RenderPass.passTwo
                        Sg.Shader { DefaultSurfaces.trafo; Shader.flatColor }
                        Sg.Uniform("FlatColor", color)
                        Sg.DepthTest (AVal.constant DepthTest.None)
                        Sg.NoEvents
                        Sg.VertexAttributes(
                            HashMap.ofList [ string DefaultSemantic.Positions, BufferView(AVal.constant (ArrayBuffer boxPos :> IBuffer), typeof<V3f>) ])
                        Sg.Index(BufferView(AVal.constant (ArrayBuffer boxIdx :> IBuffer), typeof<int>))
                        Sg.Render (AVal.constant boxIdx.Length)
                    }
                ])

        let hullPicking =
            let activeId =
                model.ScanPins.Placement |> AVal.map (function
                    | AdjustingPin(id, _) -> Some id
                    | _ -> None)
            let editedPin =
                (selectedId, activeId, pinsVal) |||> AVal.map3 (fun sel act pins ->
                    let id = act |> Option.orElse sel
                    id |> Option.bind (fun id -> HashMap.tryFind id pins))

            let editedPrism = editedPin |> AVal.map (Option.map (fun p -> p.Prism))
            let editedCutPlane = editedPin |> AVal.map (Option.map (fun p -> p.CutPlane))

            let hullGeometry =
                editedPrism |> AVal.map (fun prismOpt ->
                    match prismOpt with
                    | Some prism ->
                        let p, i = PinGeometry.buildCylinderHull prism 64
                        p, i, true
                    | None -> [||], [||], false)

            let hullPos = hullGeometry |> AVal.map (fun (p,_,_) -> ArrayBuffer p :> IBuffer)
            let hullIdx = hullGeometry |> AVal.map (fun (_,i,_) -> ArrayBuffer i :> IBuffer)
            let hullCnt = hullGeometry |> AVal.map (fun (_,i,_) -> i.Length)
            let hullActive =
                (notFullscreen, hullGeometry) ||> AVal.map2 (fun nf (_,_,act) -> nf && act)

            let indicatorGeometry =
                (editedPrism, editedCutPlane) ||> AVal.map2 (fun prismOpt cpOpt ->
                    match prismOpt, cpOpt with
                    | Some prism, Some cp ->
                        let r = match prism.Footprint.Vertices with v :: _ -> v.Length | _ -> 1.0
                        let thick = max 0.03 (r * 0.04)
                        match cp with
                        | CutPlaneMode.AcrossAxis dist ->
                            let p, i = PinGeometry.buildHullRing prism dist thick 64
                            p, i, true
                        | CutPlaneMode.AlongAxis angleDeg ->
                            let p, i = PinGeometry.buildHullLine prism angleDeg thick
                            p, i, true
                    | _ -> [||], [||], false)

            let indPos = indicatorGeometry |> AVal.map (fun (p,_,_) -> ArrayBuffer p :> IBuffer)
            let indIdx = indicatorGeometry |> AVal.map (fun (_,i,_) -> ArrayBuffer i :> IBuffer)
            let indCnt = indicatorGeometry |> AVal.map (fun (_,i,_) -> i.Length)
            let indActive = (notFullscreen, indicatorGeometry) ||> AVal.map2 (fun nf (_,_,act) -> nf && act)

            ASet.ofList [
                sg {
                    Sg.Active hullActive
                    Sg.View view
                    Sg.Proj proj
                    Sg.Shader { DefaultSurfaces.trafo; Shader.flatColor }
                    Sg.Uniform("FlatColor", AVal.constant (V4d(1.0, 1.0, 1.0, 0.1)))
                    Sg.BlendMode (AVal.constant BlendMode.Blend)
                    Sg.DepthTest (AVal.constant DepthTest.None)
                    Sg.Pass RenderPass.passOne
                    Sg.NoEvents
                    Sg.VertexAttributes(
                        HashMap.ofList [ string DefaultSemantic.Positions, BufferView(hullPos, typeof<V3f>) ])
                    Sg.Index(BufferView(hullIdx, typeof<int>))
                    Sg.Render hullCnt
                }
                sg {
                    Sg.Active indActive
                    Sg.View view
                    Sg.Proj proj
                    Sg.Shader { DefaultSurfaces.trafo; Shader.flatColor }
                    Sg.Uniform("FlatColor", AVal.constant (V4d(1.0, 1.0, 1.0, 0.9)))
                    Sg.DepthTest (AVal.constant DepthTest.None)
                    Sg.Pass RenderPass.passOne
                    Sg.VertexAttributes(
                        HashMap.ofList [ string DefaultSemantic.Positions, BufferView(indPos, typeof<V3f>) ])
                    Sg.Index(BufferView(indIdx, typeof<int>))
                    Sg.Render indCnt
                }
            ]

        let placementPreview =
            let previewPrism =
                (model.ScanPins.Placement, model.ClipBounds) ||> AVal.map2 (fun placement bounds ->
                    match placement with
                    | ProfilePlacement (ProfileWaitingForSecondPoint(p1, Some p2)) ->
                        PinGeometry.placementPreviewPrism placement bounds
                        |> Option.map (fun p -> p, Some (p1, p2), None)
                    | PlanPlacement (PlanDragging _) ->
                        PinGeometry.placementPreviewPrism placement bounds
                        |> Option.map (fun p -> p, None, None)
                    | AutoPlacement (AutoHovering (Some preview)) ->
                        let prism = PinGeometry.autoPreviewPrism preview bounds
                        Some (prism, None, Some preview.CutPlaneMode)
                    | _ -> None)
            let hullGeom =
                previewPrism |> AVal.map (fun po ->
                    match po with
                    | Some (prism, _, _) ->
                        let p, i = PinGeometry.buildCylinderHull prism 64
                        p, i, true
                    | None -> [||], [||], false)
            let hullPos = hullGeom |> AVal.map (fun (p,_,_) -> ArrayBuffer p :> IBuffer)
            let hullIdx = hullGeom |> AVal.map (fun (_,i,_) -> ArrayBuffer i :> IBuffer)
            let hullCnt = hullGeom |> AVal.map (fun (_,i,_) -> i.Length)
            let hullActive = hullGeom |> AVal.map (fun (_,_,a) -> a)
            let lineGeom =
                previewPrism |> AVal.map (fun po ->
                    match po with
                    | Some (_, Some (p1, p2), _) ->
                        [| V3f p1; V3f p2 |], [| 0; 1 |], true
                    | _ -> [||], [||], false)
            let linePos = lineGeom |> AVal.map (fun (p,_,_) -> ArrayBuffer p :> IBuffer)
            let lineIdx = lineGeom |> AVal.map (fun (_,i,_) -> ArrayBuffer i :> IBuffer)
            let lineCnt = lineGeom |> AVal.map (fun (_,i,_) -> i.Length)
            let lineActive = lineGeom |> AVal.map (fun (_,_,a) -> a)
            let cutGeom =
                previewPrism |> AVal.map (fun po ->
                    match po with
                    | Some (prism, _, Some cutPlane) ->
                        let p, i = PinGeometry.buildCutPlaneQuad prism cutPlane
                        p, i, true
                    | _ -> [||], [||], false)
            let cutPos = cutGeom |> AVal.map (fun (p,_,_) -> ArrayBuffer p :> IBuffer)
            let cutIdx = cutGeom |> AVal.map (fun (_,i,_) -> ArrayBuffer i :> IBuffer)
            let cutCnt = cutGeom |> AVal.map (fun (_,i,_) -> i.Length)
            let cutActive = cutGeom |> AVal.map (fun (_,_,a) -> a)
            let axisGeom =
                previewPrism |> AVal.map (fun po ->
                    match po with
                    | Some (prism, _, Some _) ->
                        let axis = prism.AxisDirection |> Vec.normalize
                        let top = prism.AnchorPoint + axis * prism.ExtentForward
                        let bot = prism.AnchorPoint - axis * prism.ExtentBackward
                        [| V3f top; V3f bot |], [| 0; 1 |], true
                    | _ -> [||], [||], false)
            let axisPos = axisGeom |> AVal.map (fun (p,_,_) -> ArrayBuffer p :> IBuffer)
            let axisIdx = axisGeom |> AVal.map (fun (_,i,_) -> ArrayBuffer i :> IBuffer)
            let axisCnt = axisGeom |> AVal.map (fun (_,i,_) -> i.Length)
            let axisActive = axisGeom |> AVal.map (fun (_,_,a) -> a)
            ASet.ofList [
                sg {
                    Sg.Active hullActive
                    Sg.View view
                    Sg.Proj proj
                    Sg.Shader { DefaultSurfaces.trafo; Shader.flatColor }
                    Sg.Uniform("FlatColor", AVal.constant (V4d(0.1, 0.34, 0.86, 0.18)))
                    Sg.BlendMode (AVal.constant BlendMode.Blend)
                    Sg.DepthTest (AVal.constant DepthTest.None)
                    Sg.Pass RenderPass.passOne
                    Sg.NoEvents
                    Sg.VertexAttributes(
                        HashMap.ofList [ string DefaultSemantic.Positions, BufferView(hullPos, typeof<V3f>) ])
                    Sg.Index(BufferView(hullIdx, typeof<int>))
                    Sg.Render hullCnt
                }
                sg {
                    Sg.Active lineActive
                    Sg.View view
                    Sg.Proj proj
                    Sg.Shader { DefaultSurfaces.trafo; Shader.flatColor }
                    Sg.Uniform("FlatColor", AVal.constant (V4d(0.1, 0.34, 0.86, 0.95)))
                    Sg.DepthTest (AVal.constant DepthTest.None)
                    Sg.Pass RenderPass.passOne
                    Sg.Mode IndexedGeometryMode.LineList
                    Sg.NoEvents
                    Sg.VertexAttributes(
                        HashMap.ofList [ string DefaultSemantic.Positions, BufferView(linePos, typeof<V3f>) ])
                    Sg.Index(BufferView(lineIdx, typeof<int>))
                    Sg.Render lineCnt
                }
                sg {
                    Sg.Active cutActive
                    Sg.View view
                    Sg.Proj proj
                    Sg.Shader { DefaultSurfaces.trafo; Shader.flatColor }
                    Sg.Uniform("FlatColor", AVal.constant (V4d(0.1, 0.34, 0.86, 0.25)))
                    Sg.BlendMode (AVal.constant BlendMode.Blend)
                    Sg.DepthTest (AVal.constant DepthTest.None)
                    Sg.Pass RenderPass.passOne
                    Sg.NoEvents
                    Sg.VertexAttributes(
                        HashMap.ofList [ string DefaultSemantic.Positions, BufferView(cutPos, typeof<V3f>) ])
                    Sg.Index(BufferView(cutIdx, typeof<int>))
                    Sg.Render cutCnt
                }
                sg {
                    Sg.Active axisActive
                    Sg.View view
                    Sg.Proj proj
                    Sg.Shader { DefaultSurfaces.trafo; Shader.flatColor }
                    Sg.Uniform("FlatColor", AVal.constant (V4d(0.1, 0.34, 0.86, 0.7)))
                    Sg.DepthTest (AVal.constant DepthTest.None)
                    Sg.Pass RenderPass.passOne
                    Sg.Mode IndexedGeometryMode.LineList
                    Sg.NoEvents
                    Sg.VertexAttributes(
                        HashMap.ofList [ string DefaultSemantic.Positions, BufferView(axisPos, typeof<V3f>) ])
                    Sg.Index(BufferView(axisIdx, typeof<int>))
                    Sg.Render axisCnt
                }
            ]

        ASet.unionMany (ASet.ofList [pinDots; pinPrisms; betweenSpaceBand; extractedLines; cutHoverMarker; hullPicking; placementPreview])
