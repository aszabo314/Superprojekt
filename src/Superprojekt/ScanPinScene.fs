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

    // Unit disk in the XY plane for the elevation-cursor slicing plane.
    let private diskPos, diskIdx =
        let segs = 64
        let pos = Array.init (segs + 1) (fun i ->
            if i = 0 then V3f.Zero
            else
                let a = float (i - 1) / float segs * Constant.PiTimesTwo
                V3f(float32 (cos a), float32 (sin a), 0.0f))
        let idx = ResizeArray<int>(segs * 3)
        for i in 1 .. segs do
            idx.Add 0; idx.Add i; idx.Add (if i = segs then 1 else i + 1)
        pos, idx.ToArray()

    let private diskPosBuf = AVal.constant (ArrayBuffer diskPos :> IBuffer)
    let private diskIdxBuf = AVal.constant (ArrayBuffer diskIdx :> IBuffer)
    let private diskIdxCnt = AVal.constant diskIdx.Length

    // Accent colour for the 2D-3D elevation cursor (#0891b2) — deliberately
    // distinct from every entry of the categorical mesh palette.
    let private cursorPlaneFill = V4f(0.031f, 0.569f, 0.698f, 0.28f)
    let private cursorPlaneRim  = V4d(0.031, 0.569, 0.698, 0.85)

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

        // Pin centres and radii are stored in metric world-space; the scene
        // graph works in render-space (post centroid translate, post dataset
        // scale). Project every pin coordinate before using it as a trafo.
        let renderCentreOpt =
            (model.CommonCentroid, datasetScale) ||> AVal.map2 (fun cc s ->
                fun (w : V3d) -> ScanPin.renderCentre cc s w)
        let renderLength =
            datasetScale |> AVal.map (fun s -> ScanPin.renderLength s)

        let pinDots =
            pinIdSet |> ASet.map (fun id ->
                let pinVal = pinsVal |> AVal.map (fun pins -> HashMap.tryFind id pins)
                let phaseVal = pinVal |> AVal.map (Option.map (fun p -> p.Phase))
                let centreVal =
                    (pinVal, renderCentreOpt) ||> AVal.map2 (fun po f ->
                        po |> Option.map (fun p -> f p.Centre))
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

        // Pin influence visuals: a thin equator ring (⊥ the pin's probe axis,
        // radius = InnerRadius) plus the sphere–surface contact rings per
        // visible mesh, all in the pin's categorical colour (host-mesh palette
        // colour — the same one on the card's colour bar). Unselected pins
        // draw at α 0.6 / 1.5 px, the selected pin at α 1.0 / 2.5 px. Normal
        // depth testing on purpose: foreground geometry occludes the curves,
        // which is the spatial cue. The old filled translucent shells are gone;
        // the white falloff-radius outline only shows while the radius sliders
        // are live (AdjustingPin) so the falloff slider still has feedback.
        let pinRings =
            pinIdSet |> ASet.collect (fun id ->
                let pinVal = pinsVal |> AVal.map (fun pins -> HashMap.tryFind id pins)
                let isSelected = selectedId |> AVal.map (fun sel -> sel = Some id)
                let isAdjusting =
                    model.ScanPins.Placement |> AVal.map (fun pl -> pl = AdjustingPin id)
                let ringData =
                    pinVal |> AVal.map (fun po ->
                        po |> Option.map (fun p ->
                            let colour =
                                match p.HostMeshName |> Option.bind (fun h -> Map.tryFind h p.DatasetColors) with
                                | Some c -> V3d(float c.R / 255.0, float c.G / 255.0, float c.B / 255.0)
                                | None -> V3d(0.102, 0.337, 0.859)
                            let rings = match p.ContactRings with RingsReady m -> m | _ -> Map.empty
                            p.Centre, p.InnerRadius, ScanPin.axis p, colour, rings))
                let segs =
                    AVal.custom (fun t ->
                        match ringData.GetValue t with
                        | None -> [||]
                        | Some (centre, radius, axis, colour, rings) ->
                            let sel = isSelected.GetValue t
                            let cc = model.CommonCentroid.GetValue t
                            let scale = datasetScale.GetValue t
                            let vis = model.MeshVisible.GetValue t
                            let col = V4d(colour, (if sel then 1.0 else 0.6))
                            let width = if sel then 2.5 else 1.5
                            let out = ResizeArray<V3d * V3d * V4d * float>()
                            let cR = ScanPin.renderCentre cc scale centre
                            let rR = ScanPin.renderLength scale radius
                            let nN = if axis.Length > 1e-9 then axis.Normalized else V3d.OOI
                            let u = (if abs nN.Z < 0.9 then Vec.cross nN V3d.OOI else Vec.cross nN V3d.IOO).Normalized
                            let v = Vec.cross nN u
                            let segsN = 64
                            for i in 0 .. segsN - 1 do
                                let a0 = float i / float segsN * Constant.PiTimesTwo
                                let a1 = float (i + 1) / float segsN * Constant.PiTimesTwo
                                out.Add(cR + (u * cos a0 + v * sin a0) * rR,
                                        cR + (u * cos a1 + v * sin a1) * rR, col, width)
                            for KeyValue(mesh, meshRings) in rings do
                                if Map.tryFind mesh vis |> Option.defaultValue true then
                                    for ring in meshRings do
                                        if ring.Length >= 2 then
                                            let rp = ring |> Array.map (ScanPin.renderCentre cc scale)
                                            for i in 0 .. rp.Length - 2 do
                                                out.Add(rp.[i], rp.[i + 1], col, width)
                            out.ToArray())
                let falloffSegs =
                    AVal.custom (fun t ->
                        if not (isAdjusting.GetValue t) then [||]
                        else
                            match pinsVal.GetValue t |> HashMap.tryFind id with
                            | Some p ->
                                let cc = model.CommonCentroid.GetValue t
                                let scale = datasetScale.GetValue t
                                PinGeometry.buildSphereOutline
                                    (ScanPin.renderCentre cc scale p.Centre)
                                    (ScanPin.renderLength scale p.FalloffRadius)
                                    (V4d(1.0, 1.0, 1.0, 0.55)) 1.0
                            | None -> [||])
                ASet.ofList [
                    sg {
                        Sg.Active notFullscreen
                        Sg.View view
                        Sg.Proj proj
                        Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
                        Sg.BlendMode (AVal.constant BlendMode.Blend)
                        Sg.NoEvents
                        Lines.render segs
                    }
                    sg {
                        Sg.Active notFullscreen
                        Sg.View view
                        Sg.Proj proj
                        Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
                        Sg.BlendMode (AVal.constant BlendMode.Blend)
                        Sg.NoEvents
                        Lines.render falloffSegs
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
                                let centreRender = ScanPin.renderCentre cc scale p.Centre
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
                    Sg.BlendMode (AVal.constant BlendMode.Blend)
                    Lines.render outlineSegs
                }
            ]

        // Chart-hover elevation cursor: a translucent disk orthogonal to the
        // pin's probe axis (NOT world-up — they only coincide for
        // heightfields) at the hovered signed distance. Alt extends it to
        // scene-wide bounds. Gated on the cursor pin's card being open.
        let cursorPlane =
            let effectiveId =
                (model.ScanPins.Placement, selectedId) ||> AVal.map2 (fun pl sel ->
                    match pl with
                    | AdjustingPin id -> Some id
                    | _ -> sel)
            let planeParams =
                AVal.custom (fun t ->
                    match model.ChartCursor.GetValue t with
                    | None -> None
                    | Some cur ->
                        if effectiveId.GetValue t <> Some cur.PinId then None
                        else
                            match HashMap.tryFind cur.PinId (pinsVal.GetValue t) with
                            | Some pin ->
                                match pin.Probe with
                                | ProbeReady r ->
                                    let cc = model.CommonCentroid.GetValue t
                                    let scale = datasetScale.GetValue t
                                    let centre = ScanPin.renderCentre cc scale (pin.Centre + r.Normal * cur.Distance)
                                    let radiusWorld =
                                        if cur.Extended then
                                            let sb = model.SceneBounds.GetValue t
                                            if sb.IsInvalid then pin.InnerRadius * 50.0
                                            else sb.Size.Length * 0.75
                                        else pin.InnerRadius
                                    Some (centre, r.Normal, ScanPin.renderLength scale radiusWorld)
                                | _ -> None
                            | None -> None)
            let active =
                (notFullscreen, planeParams) ||> AVal.map2 (fun nf p -> nf && Option.isSome p)
            let trafo =
                planeParams |> AVal.map (function
                    | Some (c, n, r) -> Trafo3d.Scale r * Trafo3d.RotateInto(V3d.OOI, n) * Trafo3d.Translation c
                    | None -> Trafo3d.Scale 0.0)
            let rimSegs =
                planeParams |> AVal.map (function
                    | Some (c, n, r) ->
                        let nN = n.Normalized
                        let u = (if abs nN.Z < 0.9 then Vec.cross nN V3d.OOI else Vec.cross nN V3d.IOO).Normalized
                        let v = Vec.cross nN u
                        let segs = 64
                        Array.init segs (fun i ->
                            let a0 = float i / float segs * Constant.PiTimesTwo
                            let a1 = float (i + 1) / float segs * Constant.PiTimesTwo
                            (c + (u * cos a0 + v * sin a0) * r,
                             c + (u * cos a1 + v * sin a1) * r,
                             cursorPlaneRim, 1.5))
                    | None -> [||])
            ASet.ofList [
                sg {
                    Sg.Active active
                    Sg.View view
                    Sg.Proj proj
                    Sg.Trafo trafo
                    Sg.Shader { DefaultSurfaces.trafo; Shader.flatColor }
                    Sg.Uniform("FlatColor", AVal.constant cursorPlaneFill)
                    Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
                    Sg.BlendMode (AVal.constant BlendMode.Blend)
                    Sg.NoEvents
                    Sg.VertexAttributes(
                        HashMap.ofList [ string DefaultSemantic.Positions, BufferView(diskPosBuf, typeof<V3f>) ])
                    Sg.Index(BufferView(diskIdxBuf, typeof<int>))
                    Sg.Render diskIdxCnt
                }
                sg {
                    Sg.Active active
                    Sg.View view
                    Sg.Proj proj
                    Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
                    Sg.BlendMode (AVal.constant BlendMode.Blend)
                    Sg.NoEvents
                    Lines.render rimSegs
                }
            ]

        ASet.unionMany (ASet.ofList [pinDots; pinRings; pinLines; pinPatchRings; ghostPreview; cursorPlane])
