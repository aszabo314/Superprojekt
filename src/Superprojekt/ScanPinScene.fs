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
            (patchHover : aval<PatchHover option>)
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
        // Study gating: the sphere–surface contact rings are their own
        // feature; the equator ring stays (it is the pin's footprint cue).
        let contactRingsOn = model.Study |> AVal.map (fun s -> Study.featureVisible s "contactRings")
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
                            // 1 m (world) direction indicator along the pin
                            // axis — thin + semitransparent so it reads as
                            // orientation, not geometry. Points up until the
                            // probe's PCA normal lands, like the equator ring.
                            let axisCol = V4d(colour, (if sel then 0.5 else 0.35))
                            out.Add(cR, cR + nN * ScanPin.renderLength scale 1.0, axisCol, 1.0)
                            for KeyValue(mesh, meshRings) in rings do
                                if contactRingsOn.GetValue t
                                   && (Map.tryFind mesh vis |> Option.defaultValue true) then
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

        // Correspondence visuals (always, not only during preview): accepted
        // anchors as small wireframe tetrahedra in the mesh palette colour
        // plus a thin line to the pin's reference anchor. Both follow the
        // effective preview transforms while a solve preview is pending.
        let anchorGlyphs =
            let tetra =
                let s = 1.0 / sqrt 3.0
                [| V3d(s, s, s); V3d(s, -s, -s); V3d(-s, s, -s); V3d(-s, -s, s) |]
            let tetraEdges = [| 0, 1; 0, 2; 0, 3; 1, 2; 1, 3; 2, 3 |]
            let segs =
                AVal.custom (fun t ->
                    let pins = pinsVal.GetValue t
                    let pending = model.PendingReg.GetValue t
                    let transforms = model.MeshTransforms.GetValue t
                    let scales = model.DatasetScales.GetValue t
                    let cc = model.CommonCentroid.GetValue t
                    let sel = selectedId.GetValue t
                    let scaleActive = datasetScale.GetValue t
                    let deltaCache = System.Collections.Generic.Dictionary<string, Trafo3d option>()
                    let worldDeltaOf (mesh : string) =
                        match deltaCache.TryGetValue mesh with
                        | true, v -> v
                        | _ ->
                            let v =
                                match PendingRegistration.delta mesh pending with
                                | Some d ->
                                    let scale = DatasetScale.forMesh scales mesh
                                    let c = Map.tryFind mesh transforms |> Option.defaultValue Trafo3d.Identity
                                    let wb = RigidTransform.renderToWorld scale cc c
                                    let wa = RigidTransform.renderToWorld scale cc (RegLog.effective c d)
                                    Some (wb.Inverse * wa)
                                | None -> None
                            deltaCache.[mesh] <- v
                            v
                    let out = ResizeArray<V3d * V3d * V4d * float>()
                    for (_, pin) in HashMap.toSeq pins do
                        match ScanPin.correspondence pin with
                        | Some corr when corr.Enabled ->
                            let isSel = sel = Some pin.Id
                            let alpha = if isSel then 1.0 else 0.6
                            let width = if isSel then 1.5 else 1.0
                            let refR =
                                corr.RefAnchor |> Option.map (ScanPin.renderCentre cc scaleActive)
                            let glyphR =
                                ScanPin.renderLength scaleActive (max 0.05 (pin.InnerRadius * 0.12))
                            for KeyValue(mesh, a) in corr.Anchors do
                                if a.Accepted then
                                    let pWorld =
                                        match worldDeltaOf mesh with
                                        | Some d -> d.Forward.TransformPos a.Point
                                        | None -> a.Point
                                    let p = ScanPin.renderCentre cc scaleActive pWorld
                                    let colour =
                                        match Map.tryFind mesh pin.DatasetColors with
                                        | Some c -> V4d(float c.R / 255.0, float c.G / 255.0, float c.B / 255.0, alpha)
                                        | None -> V4d(0.102, 0.337, 0.859, alpha)
                                    for (i, j) in tetraEdges do
                                        out.Add(p + tetra.[i] * glyphR, p + tetra.[j] * glyphR, colour, width)
                                    match refR with
                                    | Some r -> out.Add(p, r, colour, width)
                                    | None -> ()
                        | _ -> ()
                    out.ToArray())
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
            ]

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
                                let pv = PendingRegistration.isPreview (model.PendingReg.GetValue t)
                                match ScanPin.effectiveProbe pv pin with
                                | ProbeReady r ->
                                    let cc = model.CommonCentroid.GetValue t
                                    let scale = datasetScale.GetValue t
                                    let centre = ScanPin.renderCentre cc scale (pin.Centre + r.Normal * (cur.Distance + r.RefOffset))
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

        // Locked iso-plane gizmo: a slate ring in the plane plus a short
        // normal stub, drawn for every committed ClipPlane so the persistent
        // section plane stays legible while orbiting.
        let clipGizmos =
            let col = V4d(0.27, 0.31, 0.39, 0.85)
            let segs =
                AVal.custom (fun t ->
                    let planes = model.ClipPlanes.GetValue t
                    if List.isEmpty planes then [||]
                    else
                        let cc = model.CommonCentroid.GetValue t
                        let scale = datasetScale.GetValue t
                        let sb = model.SceneBounds.GetValue t
                        let rWorld = if sb.IsInvalid then 5.0 else sb.Size.Length * 0.3
                        let out = ResizeArray<V3d * V3d * V4d * float>()
                        for p in planes do
                            let c = ScanPin.renderCentre cc scale p.Origin
                            let r = ScanPin.renderLength scale rWorld
                            let nN = if p.Normal.Length > 1e-9 then p.Normal.Normalized else V3d.OOI
                            let u = (if abs nN.Z < 0.9 then Vec.cross nN V3d.OOI else Vec.cross nN V3d.IOO).Normalized
                            let v = Vec.cross nN u
                            let segsN = 72
                            for i in 0 .. segsN - 1 do
                                let a0 = float i / float segsN * Constant.PiTimesTwo
                                let a1 = float (i + 1) / float segsN * Constant.PiTimesTwo
                                out.Add(c + (u * cos a0 + v * sin a0) * r, c + (u * cos a1 + v * sin a1) * r, col, 1.5)
                            out.Add(c, c + nN * (r * 0.12), col, 1.5)
                        out.ToArray())
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
            ]

        // Review candidate anchors (WP16): during AnchorReviewing, draw each
        // candidate as a hollow tetra glyph in a pending colour (amber
        // undecided / green accept / red reject) with a connector to its pin's
        // reference anchor — visible before Apply, so the review modal's
        // decisions can be judged in 3D (Mode B cutaway is auto-active).
        let reviewCandidates =
            let tetra =
                let s = 1.0 / sqrt 3.0
                [| V3d(s, s, s); V3d(s, -s, -s); V3d(-s, s, -s); V3d(-s, -s, s) |]
            let tetraEdges = [| 0, 1; 0, 2; 0, 3; 1, 2; 1, 3; 2, 3 |]
            let segs =
                AVal.custom (fun t ->
                    match model.AnchorReview.GetValue t with
                    | AnchorReviewing cands ->
                        let cc = model.CommonCentroid.GetValue t
                        let scale = datasetScale.GetValue t
                        let pins = pinsVal.GetValue t
                        let out = ResizeArray<V3d * V3d * V4d * float>()
                        for c in cands do
                            if System.Double.IsFinite c.ProjectionDistance then
                                let col =
                                    match c.Decision with
                                    | AnchorAccept    -> V4d(0.13, 0.70, 0.36, 0.95)
                                    | AnchorReject    -> V4d(0.86, 0.15, 0.15, 0.75)
                                    | AnchorUndecided -> V4d(0.85, 0.47, 0.02, 0.95)
                                let p = ScanPin.renderCentre cc scale c.Point
                                let glyphR = ScanPin.renderLength scale (max 0.05 (c.FalloffRadius * 0.06))
                                for (i, j) in tetraEdges do
                                    out.Add(p + tetra.[i] * glyphR, p + tetra.[j] * glyphR, col, 1.2)
                                match HashMap.tryFind c.PinId pins
                                      |> Option.bind ScanPin.correspondence
                                      |> Option.bind (fun cr -> cr.RefAnchor) with
                                | Some ra -> out.Add(p, ScanPin.renderCentre cc scale ra, col, 1.0)
                                | None -> ()
                        out.ToArray()
                    | _ -> [||])
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
            ]

        // Patch-picker 2D→3D linking (patchHover is a view-local cval set by
        // the cell JS — pointer moves never touch the reducer): while a patch
        // cell is hovered, every sampled vertex inside the cell's current
        // pan/zoom viewport gets a short tick along the shared frame normal,
        // and the live cursor gets a jack glyph at the exact surface point —
        // both in the chart-linking accent. The frame is orthonormal, so
        // world = centre + u·refDir + v·left + h·normal reconstructs vertex
        // positions exactly. Ticks are memoized (entries compared by
        // reference) and return the same array while unchanged, so a plain
        // pointer move only rebuilds the 3-segment marker.
        let patchLink =
            let accentTick = V4d(0.031, 0.569, 0.698, 0.45)
            let accentMark = V4d(0.031, 0.569, 0.698, 0.95)
            let tickCache :
                (PatchPickerEntry list option * (string * V2d * float * V3d * float) * (V3d * V3d * V4d * float)[]) ref =
                ref (None, ("", V2d.Zero, 0.0, V3d.Zero, 0.0), Array.empty)
            let tickSegs =
                AVal.custom (fun t ->
                    let reset () =
                        tickCache.Value <- None, ("", V2d.Zero, 0.0, V3d.Zero, 0.0), Array.empty
                        Array.empty
                    match patchHover.GetValue t with
                    | None -> reset ()
                    | Some hov ->
                        match model.PatchPicker.GetValue t with
                        | Some pp when not pp.Running ->
                            match pp.Entries |> List.tryFind (fun e -> e.Mesh = hov.Mesh) with
                            | Some entry ->
                                let cc = model.CommonCentroid.GetValue t
                                let scale = datasetScale.GetValue t
                                let key = (hov.Mesh, hov.Centre, hov.Zoom, cc, scale)
                                let lastEntries, lastKey, cached = tickCache.Value
                                let entriesSame =
                                    match lastEntries with
                                    | Some le -> System.Object.ReferenceEquals(le, pp.Entries)
                                    | None -> false
                                if entriesSame && lastKey = key then cached
                                else
                                    let left = Vec.cross pp.Normal pp.RefDir
                                    let s = pp.Radius / max 1.0 hov.Zoom
                                    let tickLen = ScanPin.renderLength scale (pp.Radius * 0.05)
                                    let out = ResizeArray<V3d * V3d * V4d * float>(entry.Points.Length)
                                    for (uv, h, _) in entry.Points do
                                        if abs (uv.X - hov.Centre.X) <= s && abs (uv.Y - hov.Centre.Y) <= s then
                                            let wp = entry.Centre + pp.RefDir * uv.X + left * uv.Y + pp.Normal * h
                                            let p = ScanPin.renderCentre cc scale wp
                                            out.Add(p, p + pp.Normal * tickLen, accentTick, 1.0)
                                    let arr = out.ToArray()
                                    tickCache.Value <- Some pp.Entries, key, arr
                                    arr
                            | None -> reset ()
                        | _ -> reset ())
            let markerSegs =
                AVal.custom (fun t ->
                    match patchHover.GetValue t with
                    | Some hov ->
                        match hov.Point with
                        | Some (uv, h) ->
                            match model.PatchPicker.GetValue t with
                            | Some pp when not pp.Running ->
                                match pp.Entries |> List.tryFind (fun e -> e.Mesh = hov.Mesh) with
                                | Some entry ->
                                    let cc = model.CommonCentroid.GetValue t
                                    let scale = datasetScale.GetValue t
                                    let left = Vec.cross pp.Normal pp.RefDir
                                    let wp = entry.Centre + pp.RefDir * uv.X + left * uv.Y + pp.Normal * h
                                    let p = ScanPin.renderCentre cc scale wp
                                    let l = ScanPin.renderLength scale (pp.Radius * 0.1)
                                    [| p - pp.RefDir * l, p + pp.RefDir * l, accentMark, 1.8
                                       p - left * l, p + left * l, accentMark, 1.8
                                       p, p + pp.Normal * (l * 1.6), accentMark, 1.8 |]
                                | None -> Array.empty
                            | _ -> Array.empty
                        | None -> Array.empty
                    | None -> Array.empty)
            let node segs =
                sg {
                    Sg.Active notFullscreen
                    Sg.View view
                    Sg.Proj proj
                    Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
                    Sg.BlendMode (AVal.constant BlendMode.Blend)
                    Sg.NoEvents
                    Lines.render segs
                }
            ASet.ofList [node tickSegs; node markerSegs]

        // Study flag markers (§7 sceneClick / §9 P5): config-planted flags of
        // the current phase in amber, the participant's marks in the linking
        // accent. One Lines node, pole + diamond head sized in world metres
        // and converted at the boundary like every other pin visual. The
        // model.Study leaf changes on every study reducer step (answer
        // drafts, predicate counts), so the result is memoized on the actual
        // inputs and the same array reference is returned while they are
        // unchanged — the equality cut keeps the line buffer untouched.
        let studyFlags =
            let flagCache : ((string * int * Map<string, V3d> * V3d * float * Box3d) option * (V3d * V3d * V4d * float)[]) ref =
                ref (None, Array.empty)
            let segs =
                AVal.custom (fun t ->
                    match model.Study.GetValue t with
                    | Some (StudyActive s) ->
                        let cc = model.CommonCentroid.GetValue t
                        let scale = datasetScale.GetValue t
                        let sb = model.SceneBounds.GetValue t
                        let key = Some (s.SessionId, s.Runtime.PhaseIx, s.Runtime.Flags, cc, scale, sb)
                        match flagCache.Value with
                        | lastKey, cached when lastKey = key -> cached
                        | _ ->
                            let hWorld = if sb.IsInvalid then 1.0 else sb.Size.Length * 0.02
                            let h = ScanPin.renderLength scale hWorld
                            let questions =
                                match Study.currentPhase s with
                                | Some ph -> ph.Steps |> List.choose (fun st -> Study.effectiveQuestion s.Config st)
                                | None -> []
                            let out = ResizeArray<V3d * V3d * V4d * float>()
                            let flag (world : V3d) (colour : V4d) =
                                let p = ScanPin.renderCentre cc scale world
                                let top = p + V3d.OOI * h
                                let r = h * 0.18
                                out.Add(p, top, colour, 2.0)
                                for (a, b) in [ V3d.IOO, V3d.OIO; V3d.OIO, -V3d.IOO; -V3d.IOO, -V3d.OIO; -V3d.OIO, V3d.IOO ] do
                                    out.Add(top + a * r, top + b * r, colour, 2.0)
                            for q in questions do
                                match q.FlagPoint with
                                | Some fp -> flag fp (V4d(0.85, 0.47, 0.02, 0.95))
                                | None -> ()
                                match Map.tryFind q.Id s.Runtime.Flags with
                                | Some mark -> flag mark (V4d(0.03, 0.57, 0.7, 0.95))
                                | None -> ()
                            let result = out.ToArray()
                            flagCache.Value <- key, result
                            result
                    | _ ->
                        flagCache.Value <- None, Array.empty
                        Array.empty)
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
            ]

        ASet.unionMany (ASet.ofList [pinDots; pinRings; ghostPreview; cursorPlane; clipGizmos; anchorGlyphs; reviewCandidates; patchLink; studyFlags])
