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

    // 2D-3D elevation-cursor accent (#0891b2) — distinct from the mesh palette.
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

    // Orthonormal (u, v) basis ⊥ a (possibly unnormalized/degenerate) normal,
    // plus the normalized normal itself.
    let private basisFromNormal (n : V3d) =
        let nN = if n.Length > 1e-9 then n.Normalized else V3d.OOI
        let u = (if abs nN.Z < 0.9 then Vec.cross nN V3d.OOI else Vec.cross nN V3d.IOO).Normalized
        nN, u, Vec.cross nN u

    // Append a closed ring of `segs` segments (centre c, radius r) in the (u,v) plane.
    let private addRing (out : ResizeArray<V3d * V3d * V4d * float>)
                        (c : V3d) (u : V3d) (v : V3d) (r : float) (col : V4d) (width : float) (segs : int) =
        for i in 0 .. segs - 1 do
            let a0 = float i / float segs * Constant.PiTimesTwo
            let a1 = float (i + 1) / float segs * Constant.PiTimesTwo
            out.Add(c + (u * cos a0 + v * sin a0) * r, c + (u * cos a1 + v * sin a1) * r, col, width)

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
        // Shared chrome for every line overlay: alpha-blended, occluded by
        // foreground geometry (the spatial cue), non-interactive.
        let linesNode (active : aval<bool>) segs =
            sg {
                Sg.Active active
                Sg.View view
                Sg.Proj proj
                Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
                Sg.BlendMode (AVal.constant BlendMode.Blend)
                Sg.NoEvents
                Lines.render segs
            }
        let selectedId = model.ScanPins.SelectedPin
        let pinIdSet = model.ScanPins.Pins |> AMap.toASet |> ASet.map fst
        let pinsVal = model.ScanPins.Pins |> AMap.toAVal
        let placementActive =
            model.ScanPins.Placement |> AVal.map (function AnchorPlacement -> true | _ -> false)

        // Pin centres/radii are metric world-space; the scene graph is render-
        // space (post centroid translate + dataset scale). Project before use.
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
                    Sg.VertexAttributes(
                        HashMap.ofList [ string DefaultSemantic.Positions, BufferView(markerPosBuf, typeof<V3f>) ])
                    Sg.Index(BufferView(markerIdxBuf, typeof<int>))
                    Sg.Render markerIdxCnt
                }
            )

        // Pin influence visuals: a thin equator ring (⊥ probe axis, radius =
        // InnerRadius) + sphere–surface contact rings per visible mesh, in the
        // pin's categorical colour. Unselected α 0.6 / 1.5 px, selected α 1.0 /
        // 2.5 px. Normal depth testing on purpose — occlusion is the spatial
        // cue.
        let contactRingsOn = AVal.constant true
        let pinRings =
            pinIdSet |> ASet.collect (fun id ->
                let pinVal = pinsVal |> AVal.map (fun pins -> HashMap.tryFind id pins)
                let isSelected = selectedId |> AVal.map (fun sel -> sel = Some id)
                let ringData =
                    pinVal |> AVal.map (fun po ->
                        po |> Option.map (fun p ->
                            let colour =
                                match p.HostMeshName |> Option.bind (fun h -> Map.tryFind h p.DatasetColors) with
                                | Some c -> Primitives.c4bToV3d c
                                | None -> V3d(0.102, 0.337, 0.859)
                            let rings = match p.ContactRings with RingsReady m -> m | _ -> Map.empty
                            p.Centre, p.InnerRadius, ScanPin.axis p, colour, rings))
                let segs =
                    AVal.custom (fun t ->
                        match ringData.GetValue t with
                        | None -> [||]
                        | Some (centre, radius, axis, colour, rings) ->
                            let sel = isSelected.GetValue t
                            // Workflow-card row hover lights the rings up thick
                            // + bright (UI→3D linking).
                            let hovered = model.WorkflowPinHover.GetValue t = Some id
                            let cc = model.CommonCentroid.GetValue t
                            let scale = datasetScale.GetValue t
                            let vis = model.MeshVisible.GetValue t
                            let col =
                                if hovered then V4d(colour * 0.45 + V3d.III * 0.55, 1.0)
                                else V4d(colour, (if sel then 1.0 else 0.6))
                            let width = if hovered then 4.0 elif sel then 2.5 else 1.5
                            let out = ResizeArray<V3d * V3d * V4d * float>()
                            let cR = ScanPin.renderCentre cc scale centre
                            let rR = ScanPin.renderLength scale radius
                            let nN, u, v = basisFromNormal axis
                            addRing out cR u v rR col width 64
                            // 1 m direction indicator along the pin axis — thin
                            // + semitransparent (orientation, not geometry).
                            // Points up until the probe's PCA normal lands.
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
                ASet.ofList [ linesNode notFullscreen segs ])

        // A4: during a 3D marker pick, draw the reference marker's normal as a
        // guide — the predicted correspondence is where it meets the target.
        let pickGuide =
            let segs =
                AVal.custom (fun t ->
                    match model.AnchorPick.GetValue t with
                    | Some ap ->
                        let pins = pinsVal.GetValue t
                        match HashMap.tryFind ap.PinId pins with
                        | Some pin ->
                            match ScanPin.correspondence pin |> Option.bind (fun c -> c.RefAnchor) with
                            | Some ra ->
                                let cc = model.CommonCentroid.GetValue t
                                let scale = datasetScale.GetValue t
                                let nN, u, v = basisFromNormal (ScanPin.axis pin)
                                let raR = ScanPin.renderCentre cc scale ra
                                let len = ScanPin.renderLength scale (max 0.5 (pin.InnerRadius * 4.0))
                                let cross = ScanPin.renderLength scale (max 0.1 (pin.InnerRadius * 0.25))
                                let col = V4d(0.031, 0.569, 0.698, 0.85)
                                let out = ResizeArray<V3d * V3d * V4d * float>()
                                out.Add(raR - nN * len, raR + nN * len, col, 1.5)
                                out.Add(raR - u * cross, raR + u * cross, col, 1.5)
                                out.Add(raR - v * cross, raR + v * cross, col, 1.5)
                                out.ToArray()
                            | None -> [||]
                        | None -> [||]
                    | None -> [||])
            ASet.ofList [ linesNode notFullscreen segs ]

        // A1: transient 3D body for the Ctrl+click hover probe — same vocabulary
        // as a placed pin (equator ring + short axis line) so a spot-check reads
        // as a region probe, not a tooltip. Cleared by the HoverProbe cascade.
        let hoverProbeBody =
            let segs =
                AVal.custom (fun t ->
                    match model.HoverProbe.GetValue t with
                    | Some h ->
                        match h.Probe with
                        | ProbeReady r ->
                            let cc = model.CommonCentroid.GetValue t
                            let scale = datasetScale.GetValue t
                            let cR = ScanPin.renderCentre cc scale h.Anchor
                            let rR = ScanPin.renderLength scale h.Radius
                            let nN, u, v = basisFromNormal r.Normal
                            let col = V4d(0.031, 0.569, 0.698, 0.95)
                            let out = ResizeArray<V3d * V3d * V4d * float>()
                            addRing out cR u v rR col 2.0 48
                            out.Add(cR - nN * rR, cR + nN * rR, V4d(0.031, 0.569, 0.698, 0.7), 1.5)
                            out.ToArray()
                        | _ -> [||]
                    | None -> [||])
            ASet.ofList [ linesNode notFullscreen segs ]

        // Correspondence visuals (always, not only during preview): markers as
        // small wireframe tetrahedra in the mesh palette colour + a thin line
        // to the reference anchor. Both follow the effective preview transforms.
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
                                    Some (RigidTransform.worldDeltaOf scale cc c d)
                                | None -> None
                            deltaCache.[mesh] <- v
                            v
                    let hover = model.CorrMarkerHover.GetValue t
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
                                    // Pin-card row hover lights this glyph + ref
                                    // line up thick + bright.
                                    let hovered = hover = Some (pin.Id, mesh)
                                    let pWorld =
                                        match worldDeltaOf mesh with
                                        | Some d -> d.Forward.TransformPos a.Point
                                        | None -> a.Point
                                    let p = ScanPin.renderCentre cc scaleActive pWorld
                                    let baseCol =
                                        match Map.tryFind mesh pin.DatasetColors with
                                        | Some c -> Primitives.c4bToV3d c
                                        | None -> V3d(0.102, 0.337, 0.859)
                                    let colour =
                                        if hovered then V4d(baseCol * 0.45 + V3d.III * 0.55, 1.0)
                                        else V4d(baseCol, alpha)
                                    let w = if hovered then 4.0 else width
                                    for (i, j) in tetraEdges do
                                        out.Add(p + tetra.[i] * glyphR, p + tetra.[j] * glyphR, colour, w)
                                    match refR with
                                    | Some r -> out.Add(p, r, colour, w)
                                    | None -> ()
                        | _ -> ()
                    out.ToArray())
            ASet.ofList [ linesNode notFullscreen segs ]

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
                linesNode active outlineSegs
            ]

        // Chart-hover elevation cursor: a translucent disk ⊥ the pin's probe
        // axis (NOT world-up — they coincide only for heightfields) at the
        // hovered distance. Alt extends to scene bounds. Gated on card open.
        let cursorPlane =
            let effectiveId =
                ScanPinModel.effectivePinIdA model.ScanPins.Placement selectedId
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
                                    // F4: reference surface (chart d = 0) is at
                                    // RefOffset along the axis — ruler runs from
                                    // there to the picked distance.
                                    let refCentre = ScanPin.renderCentre cc scale (pin.Centre + r.Normal * r.RefOffset)
                                    let radiusWorld =
                                        if cur.Extended then
                                            let sb = model.SceneBounds.GetValue t
                                            if sb.IsInvalid then pin.InnerRadius * 50.0
                                            else sb.Size.Length * 0.75
                                        else pin.InnerRadius
                                    Some (centre, refCentre, r.Normal,
                                          ScanPin.renderLength scale radiusWorld,
                                          ScanPin.renderLength scale pin.InnerRadius)
                                | _ -> None
                            | None -> None)
            let active =
                (notFullscreen, planeParams) ||> AVal.map2 (fun nf p -> nf && Option.isSome p)
            let trafo =
                planeParams |> AVal.map (function
                    | Some (c, _, n, r, _) -> Trafo3d.Scale r * Trafo3d.RotateInto(V3d.OOI, n) * Trafo3d.Translation c
                    | None -> Trafo3d.Scale 0.0)
            let rimSegs =
                planeParams |> AVal.map (function
                    | Some (c, refC, n, r, innerR) ->
                        let _, u, v = basisFromNormal n
                        let out = ResizeArray<V3d * V3d * V4d * float>()
                        addRing out c u v r cursorPlaneRim 1.5 64
                        // F4 ruler: reference → picked distance, with end ticks.
                        let tk = innerR * 0.3
                        out.Add(refC, c, cursorPlaneRim, 2.0)
                        out.Add(refC - u * tk, refC + u * tk, cursorPlaneRim, 2.0)
                        out.Add(refC - v * tk, refC + v * tk, cursorPlaneRim, 2.0)
                        out.ToArray()
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
                linesNode active rimSegs
            ]

        // Patch-picker 2D→3D linking (patchHover is a view-local cval set by the
        // cell JS — pointer moves never touch the reducer): while a cell is
        // hovered, sampled vertices inside its pan/zoom viewport get a tick
        // along the frame normal and the cursor gets a jack glyph, both in the
        // accent. Frame is orthonormal, so world = centre + u·refDir + v·left +
        // h·normal is exact. Ticks memoized (entries compared by reference) so a
        // plain pointer move only rebuilds the 3-segment marker.
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
            ASet.ofList [ linesNode notFullscreen tickSegs; linesNode notFullscreen markerSegs ]

        // §8 pin glyph (far/preattentive view): a pole + head per committed pin.
        // Head colour = verdict (green if every moving mesh's |median| ≤ LoD₉₅,
        // red if any is significant; grey when no probe yet). Pole height grows
        // with magnitude (max |median offset| across moving meshes). The near
        // (attentive) split-violin lives in the pin card / flyout.
        let pinGlyphs =
            let green = V4d(0.086, 0.639, 0.290, 1.0)   // #16a34a
            let red   = V4d(0.863, 0.149, 0.149, 1.0)   // #dc2626
            let grey  = V4d(0.60, 0.62, 0.66, 0.9)
            let segs =
                AVal.custom (fun t ->
                    let pins  = pinsVal.GetValue t
                    let cc    = model.CommonCentroid.GetValue t
                    let scale = datasetScale.GetValue t
                    let out   = ResizeArray<V3d * V3d * V4d * float>()
                    for (_, p) in HashMap.toSeq pins do
                        if p.Phase = PinPhase.Committed then
                            let verdict, magnitude =
                                match p.Probe with
                                | ProbeReady r ->
                                    let moving =
                                        r.Distributions
                                        |> Array.filter (fun d -> d.MeshName <> r.ReferenceMesh && d.Count > 0)
                                    if moving.Length = 0 then grey, 0.0
                                    else
                                        let refD = r.Distributions |> Array.tryFind (fun d -> d.MeshName = r.ReferenceMesh)
                                        let refStd = refD |> Option.map (fun d -> d.Std) |> Option.defaultValue 0.0
                                        let refN = refD |> Option.map (fun d -> float (max 1 d.Count)) |> Option.defaultValue 1.0
                                        let anySig =
                                            moving |> Array.exists (fun d ->
                                                let lod = 1.96 * sqrt (refStd*refStd/refN + d.Std*d.Std/float (max 1 d.Count))
                                                abs d.Median > lod)
                                        let mag = moving |> Array.map (fun d -> abs d.Median) |> Array.max
                                        (if anySig then red else green), mag
                                | _ -> grey, 0.0
                            let axisN, u, v = basisFromNormal (ScanPin.axis p)
                            let c   = ScanPin.renderCentre cc scale p.Centre
                            let h   = ScanPin.renderLength scale (p.InnerRadius * 1.5 + magnitude * 3.0)
                            let top = c + axisN * h
                            let hr  = ScanPin.renderLength scale (p.InnerRadius * 0.5)
                            out.Add(c, top, verdict, 2.5)
                            addRing out top u v hr verdict 2.5 24
                    out.ToArray())
            ASet.ofList [ linesNode notFullscreen segs ]

        // §7 movement layer (preview only): per committed pin ROI, show the
        // applied rigid motion of each moving mesh as before→after displacement
        // arrows (MovementGlyphs) or an original-faint / warped-accent lattice
        // (MovementGrid). World delta = the preview pose relative to committed.
        let movementLayer =
            let accent = V4d(0.031, 0.569, 0.698, 0.95)
            let faint  = V4d(0.45, 0.50, 0.55, 0.40)
            let segs =
                AVal.custom (fun t ->
                    let mode = model.MovementLayer.GetValue t
                    match model.PendingReg.GetValue t with
                    | Some pr when mode <> MovementOff && not (Map.isEmpty pr.Results) ->
                        let pins  = pinsVal.GetValue t
                        let cc    = model.CommonCentroid.GetValue t
                        let scale = datasetScale.GetValue t
                        let transforms = model.MeshTransforms.GetValue t
                        let scales = model.DatasetScales.GetValue t
                        let out = ResizeArray<V3d * V3d * V4d * float>()
                        let K = 2
                        let arrow (bR : V3d) (aR : V3d) =
                            out.Add(bR, aR, accent, 1.5)
                            let d = aR - bR
                            if d.Length > 1e-6 then
                                let dn = d.Normalized
                                let perp = (if abs dn.Z < 0.9 then Vec.cross dn V3d.OOI else Vec.cross dn V3d.IOO).Normalized
                                let hl = d.Length * 0.28
                                let hw = hl * 0.5
                                out.Add(aR, aR - dn * hl + perp * hw, accent, 1.5)
                                out.Add(aR, aR - dn * hl - perp * hw, accent, 1.5)
                        for (_, p) in HashMap.toSeq pins do
                            if p.Phase = PinPhase.Committed then
                                let axisN, u, v = basisFromNormal (ScanPin.axis p)
                                ignore axisN
                                let r = p.InnerRadius
                                for KeyValue(mesh, res) in pr.Results do
                                    let committed = Map.tryFind mesh transforms |> Option.defaultValue Trafo3d.Identity
                                    let sM = DatasetScale.forMesh scales mesh
                                    let wd = RigidTransform.worldDeltaOf sM cc committed res.Delta
                                    let pts =
                                        Array2D.init (2*K+1) (2*K+1) (fun i j ->
                                            let off = u * (float (i-K) / float K * r) + v * (float (j-K) / float K * r)
                                            let before = p.Centre + off
                                            let after  = wd.Forward.TransformPos before
                                            ScanPin.renderCentre cc scale before, ScanPin.renderCentre cc scale after)
                                    match mode with
                                    | MovementGlyphs ->
                                        for i in 0 .. 2*K do
                                            for j in 0 .. 2*K do
                                                let bR, aR = pts.[i, j]
                                                arrow bR aR
                                    | MovementGrid ->
                                        let lattice (sel : (V3d * V3d) -> V3d) col =
                                            for i in 0 .. 2*K do
                                                for j in 0 .. 2*K do
                                                    if i < 2*K then out.Add(sel pts.[i, j], sel pts.[i+1, j], col, 1.0)
                                                    if j < 2*K then out.Add(sel pts.[i, j], sel pts.[i, j+1], col, 1.0)
                                        lattice fst faint
                                        lattice snd accent
                                    | MovementOff -> ()
                        out.ToArray()
                    | _ -> [||])
            ASet.ofList [ linesNode notFullscreen segs ]

        ASet.unionMany (ASet.ofList [pinDots; pinRings; pinGlyphs; movementLayer; hoverProbeBody; pickGuide; ghostPreview; cursorPlane; anchorGlyphs; patchLink])
