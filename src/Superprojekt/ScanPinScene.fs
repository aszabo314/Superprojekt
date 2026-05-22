namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open Aardworx.WebAssembly
open FSharp.Data.Adaptive
open Aardvark.Dom

module ScanPinScene =

    let private spherePos, sphereIdx = PinGeometry.buildIcosphere 2

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
        addBox 0.18 0.025 0.025
        addBox 0.025 0.18 0.025
        addBox 0.025 0.025 0.18
        pos.ToArray(), idx.ToArray()

    let private spherePosBuf = AVal.constant (ArrayBuffer spherePos :> IBuffer)
    let private sphereIdxBuf = AVal.constant (ArrayBuffer sphereIdx :> IBuffer)
    let private sphereIdxCnt = AVal.constant sphereIdx.Length
    let private markerPosBuf = AVal.constant (ArrayBuffer pinMarkerPos :> IBuffer)
    let private markerIdxBuf = AVal.constant (ArrayBuffer pinMarkerIdx :> IBuffer)
    let private markerIdxCnt = AVal.constant pinMarkerIdx.Length

    let private sphereShell
            (view : aval<Trafo3d>) (proj : aval<Trafo3d>)
            (active : aval<bool>) (trafo : aval<Trafo3d>) (color : aval<V4d>) =
        sg {
            Sg.Active active
            Sg.View view
            Sg.Proj proj
            Sg.Trafo trafo
            Sg.Shader { DefaultSurfaces.trafo; Shader.flatColor }
            Sg.Uniform("FlatColor", color |> AVal.map V4f)
            Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
            Sg.DepthMask (AVal.constant false)
            Sg.BlendMode (AVal.constant BlendMode.Blend)
            Sg.NoEvents
            Sg.VertexAttributes(
                HashMap.ofList [ string DefaultSemantic.Positions, BufferView(spherePosBuf, typeof<V3f>) ])
            Sg.Index(BufferView(sphereIdxBuf, typeof<int>))
            Sg.Render sphereIdxCnt
        }

    let build
            (env : Env<Message>)
            (view : aval<Trafo3d>) (proj : aval<Trafo3d>)
            (fullscreenActive : aval<bool>)
            (placementHover : aval<V3d option>)
            (model : AdaptiveModel) =

        let datasetScale =
            (model.ActiveDataset, model.DatasetScales) ||> AVal.map2 (fun ds map ->
                match ds with
                | Some d -> Map.tryFind d map |> Option.defaultValue 1.0
                | None -> 1.0)

        let notFullscreen = AVal.map not fullscreenActive
        let selectedId = model.ScanPins.SelectedPin
        let pinIdSet = model.ScanPins.Pins |> AMap.toASet |> ASet.map fst
        let pinsVal = model.ScanPins.Pins |> AMap.toAVal
        let placementActive =
            model.ScanPins.Placement |> AVal.map (function AnchorPlacement -> true | _ -> false)

        let pinDots =
            pinIdSet |> ASet.map (fun id ->
                let pinVal = pinsVal |> AVal.map (fun pins -> HashMap.tryFind id pins)
                let phaseVal = pinVal |> AVal.map (Option.map (fun p -> p.Phase))
                let centreVal = pinVal |> AVal.map (Option.map (fun p -> p.Centre))
                let color =
                    (selectedId, phaseVal) ||> AVal.map2 (fun sel phaseOpt ->
                        match phaseOpt with
                        | Some phase ->
                            if sel = Some id then V4d(1.0, 0.9, 0.0, 1.0)
                            elif phase = PinPhase.Placement then V4d(0.2, 1.0, 0.3, 1.0)
                            else V4d(1.0, 0.3, 0.3, 1.0)
                        | None -> V4d(0.0, 0.0, 0.0, 0.0))
                let trafo =
                    centreVal |> AVal.map (function
                        | Some c -> Trafo3d.Translation c
                        | None -> Trafo3d.Scale 0.0)
                sg {
                    Sg.Active notFullscreen
                    Sg.View view
                    Sg.Proj proj
                    Sg.Trafo trafo
                    Sg.Shader { DefaultSurfaces.trafo; Shader.flatColor }
                    Sg.Uniform("FlatColor", color |> AVal.map V4f)
                    Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
                    Sg.DepthMask (AVal.constant false)
                    Sg.BlendMode (AVal.constant BlendMode.Blend)
                    Sg.OnTap(fun _ ->
                        match AVal.force placementActive with
                        | true -> true
                        | false ->
                            let sel = AVal.force selectedId
                            if sel = Some id then env.Emit [ScanPinMsg (SelectPin None)]
                            else env.Emit [ScanPinMsg (SelectPin (Some id))]
                            false)
                    Sg.OnDoubleTap(fun _ ->
                        env.Emit [ScanPinMsg (FocusPin id)]
                        false)
                    Sg.VertexAttributes(
                        HashMap.ofList [ string DefaultSemantic.Positions, BufferView(markerPosBuf, typeof<V3f>) ])
                    Sg.Index(BufferView(markerIdxBuf, typeof<int>))
                    Sg.Render markerIdxCnt
                }
            )

        let pinSpheres =
            pinIdSet |> ASet.collect (fun id ->
                let pinVal = pinsVal |> AVal.map (fun pins -> HashMap.tryFind id pins)
                let isSelected = selectedId |> AVal.map (fun sel -> sel = Some id)
                let active = (notFullscreen, isSelected) ||> AVal.map2 (&&)
                let centreVal = pinVal |> AVal.map (Option.map (fun p -> p.Centre))
                let radiusVal = pinVal |> AVal.map (Option.map (fun p -> p.Radius) >> Option.defaultValue 0.0)
                let sigmaVal  = pinVal |> AVal.map (Option.map (fun p -> p.Sigma)  >> Option.defaultValue 0.0)
                let phaseVal  = pinVal |> AVal.map (Option.map (fun p -> p.Phase))
                let outerTrafo =
                    (centreVal, radiusVal) ||> AVal.map2 (fun co r ->
                        match co with
                        | Some c -> Trafo3d.Scale r * Trafo3d.Translation c
                        | None -> Trafo3d.Scale 0.0)
                let innerTrafo =
                    (centreVal, sigmaVal) ||> AVal.map2 (fun co s ->
                        match co with
                        | Some c -> Trafo3d.Scale s * Trafo3d.Translation c
                        | None -> Trafo3d.Scale 0.0)
                let baseColor =
                    (isSelected, phaseVal) ||> AVal.map2 (fun sel phaseOpt ->
                        if sel then V3d(1.0, 0.9, 0.0)
                        else
                            match phaseOpt with
                            | Some PinPhase.Placement -> V3d(0.2, 1.0, 0.3)
                            | Some PinPhase.Committed -> V3d(1.0, 0.5, 0.5)
                            | None -> V3d.Zero)
                let outerColor = baseColor |> AVal.map (fun c -> V4d(c.X, c.Y, c.Z, 0.10))
                let innerColor = baseColor |> AVal.map (fun c -> V4d(c.X, c.Y, c.Z, 0.30))
                let outlineSegs =
                    (centreVal, radiusVal, isSelected) |||> AVal.map3 (fun co r sel ->
                        if sel then
                            match co with
                            | Some c -> PinGeometry.buildSphereOutline c r (V4d(1.0, 1.0, 1.0, 0.55)) 1.0
                            | None -> [||]
                        else [||])
                ASet.ofList [
                    sphereShell view proj active outerTrafo outerColor
                    sphereShell view proj active innerTrafo innerColor
                    sg {
                        Sg.Active active
                        Sg.View view
                        Sg.Proj proj
                        Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
                        Sg.DepthMask (AVal.constant false)
                        Sg.BlendMode (AVal.constant BlendMode.Blend)
                        Lines.render outlineSegs
                    }
                ])

        let inline colorForMesh (mesh : string) (palette : Map<string, C4b>) (selected : bool) (isHost : bool) =
            if selected && isHost then V4d(1.0, 0.9, 0.0, 0.98)
            else
                match Map.tryFind mesh palette with
                | Some c -> V4d(float c.R / 255.0, float c.G / 255.0, float c.B / 255.0, 0.95)
                | None -> V4d(0.1, 0.34, 0.86, 0.95)

        let pinLines =
            pinIdSet |> ASet.map (fun id ->
                let pinVal = pinsVal |> AVal.map (fun pins -> HashMap.tryFind id pins)
                let isSelected = selectedId |> AVal.map (fun sel -> sel = Some id)
                let active = (notFullscreen, isSelected) ||> AVal.map2 (&&)
                let traces =
                    pinVal |> AVal.map (fun po ->
                        match po with
                        | Some p ->
                            match p.Payload, p.HostMeshName with
                            | Line lp, hostOpt ->
                                let host = hostOpt |> Option.defaultValue ""
                                let palette = p.DatasetColors
                                let pairs = ResizeArray<string * V3d[] * bool>()
                                pairs.Add(host, lp.Points, true)
                                for kv in lp.CrossMeshTraces do
                                    pairs.Add(kv.Key, fst kv.Value, false)
                                palette, pairs.ToArray()
                            | _ -> Map.empty, [||]
                        | None -> Map.empty, [||])
                let ccScale =
                    (model.CommonCentroid, datasetScale) ||> AVal.map2 (fun cc s -> cc, s)
                let segs =
                    (traces, isSelected, ccScale) |||> AVal.map3 (fun (palette, lines) sel (cc, scale) ->
                        let out = ResizeArray<V3d * V3d * V4d * float>()
                        for (mesh, pts, isHost) in lines do
                            if pts.Length >= 2 then
                                let color = colorForMesh mesh palette sel isHost
                                let rps = pts |> Array.map (fun p -> (p - cc) * scale)
                                for i in 0 .. rps.Length - 2 do
                                    out.Add(rps.[i], rps.[i + 1], color, 2.0)
                        out.ToArray())
                sg {
                    Sg.Active active
                    Sg.View view
                    Sg.Proj proj
                    Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
                    Sg.DepthMask (AVal.constant false)
                    Sg.BlendMode (AVal.constant BlendMode.Blend)
                    Lines.render segs
                })

        let pinPatchRings =
            pinIdSet |> ASet.map (fun id ->
                let pinVal = pinsVal |> AVal.map (fun pins -> HashMap.tryFind id pins)
                let isSelected = selectedId |> AVal.map (fun sel -> sel = Some id)
                let active = (notFullscreen, isSelected) ||> AVal.map2 (&&)
                let segs =
                    (pinVal, model.CommonCentroid, datasetScale)
                    |||> AVal.map3 (fun po cc scale ->
                        match po with
                        | Some p ->
                            match p.Payload with
                            | Patch pp ->
                                let centreRender = p.Centre
                                let radiusRender = pp.Radius
                                let color =
                                    match p.HostMeshName with
                                    | Some host ->
                                        match Map.tryFind host p.DatasetColors with
                                        | Some c -> V4d(float c.R / 255.0, float c.G / 255.0, float c.B / 255.0, 0.95)
                                        | None -> V4d(0.1, 0.34, 0.86, 0.95)
                                    | None -> V4d(0.1, 0.34, 0.86, 0.95)
                                PinGeometry.buildPatchFootprint
                                    centreRender radiusRender
                                    pp.RefDirWorld pp.NormalWorld
                                    color 1.5
                            | _ -> [||]
                        | None -> [||])
                sg {
                    Sg.Active active
                    Sg.View view
                    Sg.Proj proj
                    Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
                    Sg.DepthMask (AVal.constant false)
                    Sg.BlendMode (AVal.constant BlendMode.Blend)
                    Lines.render segs
                })

        let ghostPreview =
            let defaultR =
                model.SceneBounds |> AVal.map (fun b ->
                    if b.IsInvalid then 1.0
                    else max 0.1 (b.Size.Length * 0.05))
            let active =
                (notFullscreen, placementActive, placementHover) |||> AVal.map3 (fun nf pa hOpt ->
                    nf && pa && hOpt.IsSome)
            let trafo =
                (placementHover, defaultR) ||> AVal.map2 (fun hOpt r ->
                    match hOpt with
                    | Some c -> Trafo3d.Scale r * Trafo3d.Translation c
                    | None -> Trafo3d.Scale 0.0)
            let outlineSegs =
                (placementHover, defaultR) ||> AVal.map2 (fun hOpt r ->
                    match hOpt with
                    | Some c -> PinGeometry.buildSphereOutline c r (V4d(0.1, 0.34, 0.86, 0.85)) 1.5
                    | None -> [||])
            ASet.ofList [
                sphereShell view proj active trafo (AVal.constant (V4d(0.1, 0.34, 0.86, 0.18)))
                sg {
                    Sg.Active active
                    Sg.View view
                    Sg.Proj proj
                    Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
                    Sg.DepthMask (AVal.constant false)
                    Sg.BlendMode (AVal.constant BlendMode.Blend)
                    Lines.render outlineSegs
                }
            ]

        ASet.unionMany (ASet.ofList [pinDots; pinSpheres; pinLines; pinPatchRings; ghostPreview])
