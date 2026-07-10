namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open Aardworx.WebAssembly
open FSharp.Data.Adaptive
open Aardvark.Dom

module ScanPinScene =

    let private spherePos, sphereIdx = PinGeometry.buildIcosphere 2

    let private spherePosBuf = AVal.constant (ArrayBuffer spherePos :> IBuffer)
    let private sphereIdxBuf = AVal.constant (ArrayBuffer sphereIdx :> IBuffer)
    let private sphereIdxCnt = AVal.constant sphereIdx.Length

    // Translucent icosphere shell (placement hover preview).
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

    // Wire sphere (three axis-aligned great circles) of radius r at c.
    let private addWireSphere (out : ResizeArray<V3d * V3d * V4d * float>)
                              (c : V3d) (r : float) (col : V4d) (width : float) (segs : int) =
        addRing out c V3d.IOO V3d.OIO r col width segs
        addRing out c V3d.IOO V3d.OOI r col width segs
        addRing out c V3d.OIO V3d.OOI r col width segs

    // Small 3-axis cross (half-length r) marking an exact point at c.
    let private addCross (out : ResizeArray<V3d * V3d * V4d * float>)
                         (c : V3d) (r : float) (col : V4d) (width : float) =
        out.Add(c - V3d.IOO * r, c + V3d.IOO * r, col, width)
        out.Add(c - V3d.OIO * r, c + V3d.OIO * r, col, width)
        out.Add(c - V3d.OOI * r, c + V3d.OOI * r, col, width)

    // 12 edges of an axis-aligned box (half-extents hx,hy,hz) at c.
    let private addBoxOutline (out : ResizeArray<V3d * V3d * V4d * float>)
                              (c : V3d) (hx : float) (hy : float) (hz : float) (col : V4d) (width : float) =
        let v = [|
            V3d(-hx, -hy, -hz); V3d( hx, -hy, -hz); V3d( hx, hy, -hz); V3d(-hx, hy, -hz)
            V3d(-hx, -hy,  hz); V3d( hx, -hy,  hz); V3d( hx, hy,  hz); V3d(-hx, hy,  hz) |]
        let e = [| 0,1; 1,2; 2,3; 3,0; 4,5; 5,6; 6,7; 7,4; 0,4; 1,5; 2,6; 3,7 |]
        for (a, b) in e do out.Add(c + v.[a], c + v.[b], col, width)

    // Canonical per-sample list for distribution brushing (§T6) — the single source
    // of truth shared by the chart (gid labelling), the 3D brushed markers, and the
    // 3D→chart spatial query. Order is fixed (moving meshes by MeshNames order × ready
    // pins by (CreatedAt, guid) × sample, strided to ≤ brushMaxPerCell): the array
    // index IS the sample's global id (gid). Returns (pin, mesh, world-pos, value-mm).
    let private brushMaxPerCell = 100
    let brushSamples (model : AdaptiveModel) : aval<(ScanPinId * string * V3d * float)[]> =
        let pinsVal  = model.ScanPins.Pins |> AMap.toAVal
        AVal.custom (fun t ->
            let pins = pinsVal.GetValue t
            let names = model.MeshNames.Content.GetValue t |> IndexList.toList
            let vis = model.MeshVisible.GetValue t
            let rf = (model.Registration.GetValue t).ReferenceMesh
            let moving = names |> List.filter (fun n -> Some n <> rf && (Map.tryFind n vis |> Option.defaultValue true))
            let ready =
                pins |> HashMap.toList
                |> List.choose (fun (id, p) -> match p.Probe with ProbeReady r -> Some (id, r) | _ -> None)
                |> List.sortBy (fun (ScanPinId.ScanPinId g, _) ->
                    (match HashMap.tryFind (ScanPinId.ScanPinId g) pins with Some p -> p.CreatedAt | None -> System.DateTime.MinValue), g)
            let out = ResizeArray<ScanPinId * string * V3d * float>()
            for mesh in moving do
                for (id, r) in ready do
                    match r.Distributions |> Array.tryFind (fun d -> d.MeshName = mesh) with
                    | Some d when d.Count > 0 ->
                        let n = min d.Samples.Length d.Positions.Length
                        let stride = if n > brushMaxPerCell then n / brushMaxPerCell else 1
                        let mutable i = 0
                        while i < n do
                            out.Add(id, mesh, d.Positions.[i], d.Samples.[i] * 1000.0)
                            i <- i + stride
                    | _ -> ()
            out.ToArray())

    // Brushed-dot geometry (§A4): a small solid icosphere per brushed sample,
    // coloured by value on the SHARED inspect range (the legend's scale while a
    // brush is active). meshFilter restricts to one mesh — the focus views pass
    // their own mesh; the main 3D passes None. Positions come from the same
    // canonical gid array the chart labels with.
    let brushedDotGeometry (model : AdaptiveModel) (meshFilter : string option) =
        let canonA = brushSamples model
        let spherePos, sphereIdx = PinGeometry.buildIcosphere 1
        let rangeA = MeshView.inspectRange model
        let datasetScale =
            (model.ActiveDataset, model.DatasetScales) ||> AVal.map2 DatasetScale.active
        AVal.custom (fun t ->
            let brushed = model.BrushedSamples.GetValue t
            if Set.isEmpty brushed then [||], [||], [||]
            else
                let canon = canonA.GetValue t
                let (lo, hi) = rangeA.GetValue t
                let cc = model.CommonCentroid.GetValue t
                let scale = datasetScale.GetValue t
                let dots =
                    brushed |> Seq.choose (fun gid ->
                        if gid >= 0 && gid < canon.Length then
                            let (_, mesh, pos, vMm) = canon.[gid]
                            if meshFilter |> Option.forall ((=) mesh) then
                                Some (ScanPin.renderCentre cc scale pos,
                                      Primitives.Diff.colorSignedV3 lo hi (vMm / 1000.0))
                            else None
                        else None)
                    |> Array.ofSeq
                let r = 0.03
                let nv = spherePos.Length
                let posOut = Array.zeroCreate<V3f> (dots.Length * nv)
                let colOut = Array.zeroCreate<V4f> (dots.Length * nv)
                let idxOut = Array.zeroCreate<int> (dots.Length * sphereIdx.Length)
                for di in 0 .. dots.Length - 1 do
                    let (c, col) = dots.[di]
                    let cf = V3f c
                    let colF = V4f(float32 col.X, float32 col.Y, float32 col.Z, 1.0f)
                    let vb = di * nv
                    for vi in 0 .. nv - 1 do
                        posOut.[vb + vi] <- cf + spherePos.[vi] * float32 r
                        colOut.[vb + vi] <- colF
                    let ib = di * sphereIdx.Length
                    for ii in 0 .. sphereIdx.Length - 1 do
                        idxOut.[ib + ii] <- vb + sphereIdx.[ii]
                posOut, colOut, idxOut)

    let build
            (env : Env<Message>)
            (view : aval<Trafo3d>) (proj : aval<Trafo3d>)
            (fullscreenActive : aval<bool>)
            (placementHover : aval<V3d option>)
            (model : AdaptiveModel) =

        let datasetScale =
            (model.ActiveDataset, model.DatasetScales) ||> AVal.map2 DatasetScale.active

        let notFullscreen = AVal.map not fullscreenActive
        // Correspondence point markers (the constellation) render only in the
        // Correspondence workflow — Overview/Inspect stay clean (matches the focus
        // panel's overlay, which is already gated the same way).
        let inCorrespondence = model.WorkflowStep |> AVal.map ((=) Correspondence)
        let constellationActive = (notFullscreen, inCorrespondence) ||> AVal.map2 (&&)
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
        // Same, but depth-test off → renders on top of surfaces (constellation
        // depth bias; ghost-isolation clears occluders when reading it).
        let linesNodeTop (active : aval<bool>) segs =
            sg {
                Sg.Active active
                Sg.View view
                Sg.Proj proj
                Sg.DepthTest (AVal.constant DepthTest.None)
                Sg.BlendMode (AVal.constant BlendMode.Blend)
                Sg.NoEvents
                Lines.render segs
            }
        let selectedId = model.Selection.Active |> AVal.map Selection.pin
        let pinIdSet = model.ScanPins.Pins |> AMap.toASet |> ASet.map fst
        let pinsVal = model.ScanPins.Pins |> AMap.toAVal
        let placementActive =
            model.ScanPins.Placement |> AVal.map (function AnchorPlacement -> true | _ -> false)

        // Displayed (before/after, peek-aware) world transform of a mesh at the
        // given token — anchors are mesh-local, so their world follows this.
        let dispWorldAt (t : AdaptiveToken) (mesh : string) =
            MeshView.displayedWorldPeekAt model t mesh

        // Pin centres/radii are metric world-space; the scene graph is render-
        // space (post centroid translate + dataset scale). Project before use.
        let renderCentreOpt =
            (model.CommonCentroid, datasetScale) ||> AVal.map2 (fun cc s ->
                fun (w : V3d) -> ScanPin.renderCentre cc s w)
        let renderLength =
            datasetScale |> AVal.map (fun s -> ScanPin.renderLength s)

        // Pin centre pick proxies: small invisible spheres carrying the select tap
        // (the visible marker is the wire-box jack in pinMarkerLines). Alpha 0 →
        // invisible in colour but still present in the depth/id pick pass.
        let pinDots =
            pinIdSet |> ASet.map (fun id ->
                let pinVal = pinsVal |> AVal.map (fun pins -> HashMap.tryFind id pins)
                let centreVal =
                    (pinVal, renderCentreOpt) ||> AVal.map2 (fun po f ->
                        po |> Option.map (fun p -> f p.Centre))
                let trafo =
                    centreVal |> AVal.map (function
                        | Some c -> Trafo3d.Scale 0.07 * Trafo3d.Translation c
                        | None -> Trafo3d.Scale 0.0)
                sg {
                    Sg.Active notFullscreen
                    Sg.View view
                    Sg.Proj proj
                    Sg.Trafo trafo
                    Sg.Shader { DefaultSurfaces.trafo; Shader.flatColor }
                    Sg.Uniform("FlatColor", AVal.constant (V4f(0.0, 0.0, 0.0, 0.0)))
                    // On top (None): an invisible proxy still writes depth, so a
                    // LessOrEqual marker would self-occlude behind it.
                    Sg.DepthTest (AVal.constant DepthTest.None)
                    Sg.BlendMode (AVal.constant BlendMode.Blend)
                    // Tap toggles selection — ClickGate-deferred so a double-tap's two
                    // taps can't toggle twice; double-tap = select + 3D zoom (the 2D
                    // focus zoom is out of reach here: FocusScene compiles later — the
                    // matrix pin row is the fully linked path).
                    Sg.OnTap(fun _ ->
                        match AVal.force placementActive with
                        | true -> true
                        | false ->
                            ClickGate.single "pin-dot" (fun () ->
                                if AVal.force selectedId = Some id then env.Emit [SetSelection SelNone]
                                else env.Emit [SetSelection (SelPin id)])
                            false)
                    Sg.OnDoubleTap(fun _ ->
                        match AVal.force placementActive with
                        | true -> true
                        | false ->
                            ClickGate.double "pin-dot" (fun () ->
                                env.Emit [SetSelection (SelPin id); ZoomToPin id])
                            false)
                    Sg.VertexAttributes(
                        HashMap.ofList [ string DefaultSemantic.Positions, BufferView(spherePosBuf, typeof<V3f>) ])
                    Sg.Index(BufferView(sphereIdxBuf, typeof<int>))
                    Sg.Render sphereIdxCnt
                }
            )

        // Visible pin-centre marker: a small, faint neutral wire-box jack on top (so
        // the invisible pick proxy can't occlude it); slightly darker when selected
        // or hovered. Fixed render size — independent of pin radius.
        // Project to centres only — depending on the whole pin map would rebuild the
        // marker buffer on any pin field change (probe/ring result, rename, …).
        let pinCentres = model.ScanPins.Pins |> AMap.map (fun _ p -> p.Centre) |> AMap.toAVal
        let pinMarkerLines =
            let segs =
                AVal.custom (fun t ->
                    let centres = pinCentres.GetValue t
                    let cc = model.CommonCentroid.GetValue t
                    let scale = datasetScale.GetValue t
                    let sel = selectedId.GetValue t
                    let hov = model.Selection.Hovered.GetValue t
                    let out = ResizeArray<V3d * V3d * V4d * float>()
                    for (id, centre) in HashMap.toSeq centres do
                        let isSel = sel = Some id
                        let hovered = hov = Some (HoverPin id)
                        let col =
                            if isSel || hovered then V4d(0.25, 0.28, 0.33, 0.85)
                            else V4d(0.45, 0.48, 0.53, 0.4)
                        let w = if isSel || hovered then 1.6 else 1.0
                        let cR = ScanPin.renderCentre cc scale centre
                        addBoxOutline out cR 0.035 0.007 0.007 col w
                        addBoxOutline out cR 0.007 0.035 0.007 col w
                        addBoxOutline out cR 0.007 0.007 0.035 col w
                    out.ToArray())
            linesNodeTop notFullscreen segs

        // Pin influence visuals: a thin equator ring (⊥ probe axis, radius =
        // InnerRadius) + sphere–surface contact rings per visible mesh, in the
        // pin's categorical colour. Normal depth testing on purpose — occlusion
        // is the spatial cue.
        let contactRingsOn = AVal.constant true
        let pinRings =
            pinIdSet |> ASet.collect (fun id ->
                let pinVal = pinsVal |> AVal.map (fun pins -> HashMap.tryFind id pins)
                let isSelected = selectedId |> AVal.map (fun sel -> sel = Some id)
                let ringData =
                    pinVal |> AVal.map (fun po ->
                        po |> Option.map (fun p ->
                            let colour = Primitives.c4bToV3d p.PinColor
                            let rings = match p.ContactRings with RingsReady m -> m | _ -> Map.empty
                            p.Centre, p.InnerRadius, ScanPin.axis p, colour, rings))
                let segs =
                    AVal.custom (fun t ->
                        match ringData.GetValue t with
                        | None -> [||]
                        | Some (centre, radius, axis, colour, rings) ->
                            let sel = isSelected.GetValue t
                            // Pin-row hover lights the rings up thick + bright
                            // (UI→3D linking via the shared Selection record).
                            let hovered = model.Selection.Hovered.GetValue t = Some (HoverPin id)
                            let cc = model.CommonCentroid.GetValue t
                            let scale = datasetScale.GetValue t
                            // Shown-set gating (toggles + solo overlay): rings on a
                            // ghosted-away mesh would float without their surface.
                            let solo = model.MeshSolo.GetValue t
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
                                   && MeshVisibility.shown solo vis mesh then
                                    for ring in meshRings do
                                        if ring.Length >= 2 then
                                            let rp = ring |> Array.map (ScanPin.renderCentre cc scale)
                                            for i in 0 .. rp.Length - 2 do
                                                out.Add(rp.[i], rp.[i + 1], col, width)
                            out.ToArray())
                ASet.ofList [ linesNode notFullscreen segs ])

        // Correspondence constellation lines: per pin, a small wire-sphere + cross
        // glyph at every mesh's marker — the reference's RefAnchor drawn exactly like
        // a moving-mesh marker (same glyph, its mesh colour) — plus a thin line from
        // each moving glyph to the reference point. Fixed render size (independent of
        // pin radius). Selection / hover brighten; out-of-ROI meshes omitted.
        // Rendered on top (depth bias) so the markers read against surfaces.
        // Project to (correspondence, dataset colours) only — depending on the whole
        // pin map would rebuild the constellation buffer on any pin field change.
        let pinCorr = model.ScanPins.Pins |> AMap.map (fun _ p -> ScanPin.correspondence p, p.DatasetColors) |> AMap.toAVal
        let constLines =
            let segs =
                AVal.custom (fun t ->
                    let pins = pinCorr.GetValue t
                    let cc = model.CommonCentroid.GetValue t
                    let scale = datasetScale.GetValue t
                    let sel = selectedId.GetValue t
                    let hov = model.Selection.Hovered.GetValue t
                    let names = model.MeshNames.Content.GetValue t |> IndexList.toList
                    let vis = model.MeshVisible.GetValue t
                    let rf = (model.Registration.GetValue t).ReferenceMesh
                    // Shown-set gating (toggles + solo overlay), so a locate shows
                    // only the located mesh's markers.
                    let solo = model.MeshSolo.GetValue t
                    let moving =
                        names |> List.filter (fun n ->
                            Some n <> rf && MeshVisibility.shown solo vis n)
                    let out = ResizeArray<V3d * V3d * V4d * float>()
                    for (id, (corr, datasetColors)) in HashMap.toSeq pins do
                        match corr with
                        | Some c ->
                            match c.RefAnchor with
                            | Some ra ->
                                let isSel = sel = Some id
                                let pinHover = hov = Some (HoverPin id)
                                let emph = isSel || pinHover
                                let raR = ScanPin.renderCentre cc scale ra
                                (match rf with
                                 | Some rn when MeshVisibility.shown solo vis rn ->
                                    let baseCol =
                                        match Map.tryFind rn datasetColors with
                                        | Some c4 -> Primitives.c4bToV3d c4
                                        | None -> V3d(0.102, 0.337, 0.859)
                                    let refHover = hov = Some (HoverPoint (id, rn))
                                    let col =
                                        if refHover then V4d(baseCol * 0.4 + V3d.III * 0.6, 1.0)
                                        elif emph then V4d(baseCol, 1.0)
                                        else V4d(baseCol, 0.4)
                                    let gw = if refHover || isSel then 2.0 else 1.4
                                    addWireSphere out raR 0.055 col gw 16
                                    addCross out raR 0.07 col gw
                                 | _ -> ())
                                for mesh in moving do
                                    // Inline marker resolution: read dispWorldAt with THIS
                                    // aval's token so the displayed (before/after) transform
                                    // stays a tracked dependency. Building a transient
                                    // `markerWorldOf` aval here and forcing it dropped that
                                    // edge (constellation could stop following the toggle).
                                    let marker =
                                        let inRoi = Map.tryFind mesh c.InRoi |> Option.defaultValue true
                                        match Map.tryFind mesh c.Anchors with
                                        | Some a when inRoi -> Some ((dispWorldAt t mesh).Forward.TransformPos a.Point)
                                        | _ -> None
                                    match marker with
                                    | Some w ->
                                        let baseCol =
                                            match Map.tryFind mesh datasetColors with
                                            | Some cc4 -> Primitives.c4bToV3d cc4
                                            | None -> V3d(0.102, 0.337, 0.859)
                                        let rowHover = hov = Some (HoverPoint (id, mesh))
                                        let col =
                                            if rowHover then V4d(baseCol * 0.4 + V3d.III * 0.6, 1.0)
                                            elif emph then V4d(baseCol, 1.0)
                                            else V4d(baseCol, 0.4)
                                        let mw = if rowHover || isSel then 2.0 else 1.4
                                        let wR = ScanPin.renderCentre cc scale w
                                        addWireSphere out wR 0.055 col mw 16
                                        addCross out wR 0.07 col mw
                                        out.Add(wR, raR, V4d(col.XYZ, (if emph then 0.9 else 0.3)), (if isSel then 1.5 else 1.0))
                                    | None -> ()
                            | None -> ()
                        | _ -> ()
                    out.ToArray())
            ASet.ofList [ linesNodeTop constellationActive segs ]

        let constellation = constLines

        let ghostPreview =
            // Preview radius = the radius a click would place (QuickPinRadius,
            // metric) in render space, so the hover sphere matches the real pin.
            let previewR =
                (model.QuickPinRadius, datasetScale) ||> AVal.map2 (fun r s ->
                    max 1e-4 (ScanPin.renderLength s r))
            let active =
                (notFullscreen, placementActive, placementHover) |||> AVal.map3 (fun nf pa hOpt ->
                    nf && pa && hOpt.IsSome)
            let trafo =
                (placementHover, previewR) ||> AVal.map2 (fun hOpt r ->
                    match hOpt with
                    | Some c -> Trafo3d.Scale r * Trafo3d.Translation c
                    | None -> Trafo3d.Scale 0.0)
            let outlineSegs =
                (placementHover, previewR) ||> AVal.map2 (fun hOpt r ->
                    match hOpt with
                    | Some c -> PinGeometry.buildSphereOutline c r (V4d(0.1, 0.34, 0.86, 0.85)) 1.5
                    | None -> [||])
            ASet.ofList [
                sphereShell view proj active trafo (AVal.constant (V4d(0.1, 0.34, 0.86, 0.18)))
                linesNode active outlineSegs
            ]

        // Pin pole (far view): a neutral flag along the probe axis per committed pin;
        // pole height grows with magnitude (ScanPin.flagMagnitude). While the
        // show-overlays hold is down it takes the pin colour and gets much thicker,
        // matching the white-out-except-pins read.
        let pinGlyphs =
            let neutral = V4d(0.52, 0.55, 0.60, 0.75)
            let segs =
                AVal.custom (fun t ->
                    let pins  = pinsVal.GetValue t
                    let cc    = model.CommonCentroid.GetValue t
                    let scale = datasetScale.GetValue t
                    let overlays = model.ShowOverlaysHeld.GetValue t
                    let out   = ResizeArray<V3d * V3d * V4d * float>()
                    for (_, p) in HashMap.toSeq pins do
                        let col = if overlays then V4d(Primitives.c4bToV3d p.PinColor, 1.0) else neutral
                        let w   = if overlays then 7.0 else 2.5
                        let _, u, v = basisFromNormal (ScanPin.axis p)
                        let c   = ScanPin.renderCentre cc scale p.Centre
                        let top = ScanPin.flagTopRender cc scale p
                        let hr  = ScanPin.renderLength scale (p.InnerRadius * 0.5)
                        out.Add(c, top, col, w)
                        addRing out top u v hr col w 24
                    out.ToArray())
            ASet.ofList [ linesNode notFullscreen segs ]

        // Centre-slice intersection lines (show-overlays hold): the cached vertical
        // cross-section's centre plane per pin, one polyline set per shown mesh in
        // its dataset colour — the 3D locator for the label profile charts. Slice
        // data is pose-baked (Slice = committed, SliceOther = opposite), so the reg
        // peek just selects the other cache. Drawn on top: the lines lie exactly ON
        // the whited-out surfaces, where a coplanar depth test would stitch.
        let pinSliceData =
            model.ScanPins.Pins
            |> AMap.map (fun _ p -> p.Centre, p.Slice, p.SliceOther, p.DatasetColors)
            |> AMap.toAVal
        let slicesActive = (notFullscreen, model.ShowOverlaysHeld) ||> AVal.map2 (&&)
        let pinSliceLines =
            let segs =
                AVal.custom (fun t ->
                    if not (model.ShowOverlaysHeld.GetValue t) then [||]
                    else
                        let pins  = pinSliceData.GetValue t
                        let cc    = model.CommonCentroid.GetValue t
                        let scale = datasetScale.GetValue t
                        let peek  = model.RegPeekHeld.GetValue t
                        let solo  = model.MeshSolo.GetValue t
                        let vis   = model.MeshVisible.GetValue t
                        let out = ResizeArray<V3d * V3d * V4d * float>()
                        for (_, (centre, slice, sliceOther, colors)) in HashMap.toSeq pins do
                            let chosen =
                                match (if peek then sliceOther else slice) with
                                | SliceReady s -> Some s
                                | _ -> None
                            match chosen with
                            | Some s ->
                                let ci = ScanPin.sliceCentreIndex s
                                let w = s.Offsets.[ci]
                                for sm in s.Meshes do
                                    if MeshVisibility.shown solo vis sm.MeshName && ci < sm.Planes.Length then
                                        let col =
                                            match Map.tryFind sm.MeshName colors with
                                            | Some c4 -> V4d(Primitives.c4bToV3d c4, 0.95)
                                            | None -> V4d(0.102, 0.337, 0.859, 0.95)
                                        for line in sm.Planes.[ci] do
                                            for i in 0 .. line.Length - 2 do
                                                let a = ScanPin.sliceToWorld centre w line.[i]
                                                let b = ScanPin.sliceToWorld centre w line.[i + 1]
                                                out.Add(ScanPin.renderCentre cc scale a,
                                                        ScanPin.renderCentre cc scale b, col, 2.0)
                            | None -> ()
                        out.ToArray())
            ASet.ofList [ linesNodeTop slicesActive segs ]

        // Pin identity flag (§A): the pin's ShortName as a text label floating above
        // the pin centre, in the pin's PinColor. Always-on-top (passOne, DepthTest.None)
        // so it reads against the terrain. Identity is immutable → snapshot once per id
        // (no atlas rebuild on probe/ring updates); only the position is adaptive.
        // Hidden while the show-overlays hold is down — the 2D flag-tip name tags
        // (GuiOverlays.pinFlagLabels) take over there.
        let pinCentreRadius = model.ScanPins.Pins |> AMap.map (fun _ p -> p.Centre, p.InnerRadius) |> AMap.toAVal
        let labelsActive =
            (notFullscreen, model.ShowOverlaysHeld) ||> AVal.map2 (fun nf ov -> nf && not ov)
        let pinLabels =
            pinIdSet |> ASet.map (fun id ->
                let p0 = HashMap.tryFind id (AVal.force pinsVal)
                let labelSize = 0.2
                let topVal =
                    AVal.custom (fun t ->
                        let cc = model.CommonCentroid.GetValue t
                        let scale = datasetScale.GetValue t
                        match HashMap.tryFind id (pinCentreRadius.GetValue t) with
                        | Some (centre, radius) ->
                            let cR = ScanPin.renderCentre cc scale centre
                            cR + V3d.OOI * (ScanPin.renderLength scale (radius * 1.5) + 0.55)
                        | None -> V3d(0.0, 0.0, -1.0e6))
                match p0 with
                | Some pin ->
                    sg {
                        Sg.Active labelsActive
                        Sg.View view
                        Sg.Proj proj
                        Sg.Pass RenderPass.passOne
                        Sg.DepthTest (AVal.constant DepthTest.None)
                        Sg.NoEvents
                        Sg.Trafo (topVal |> AVal.map (fun top ->
                            Trafo3d.Scale labelSize * Trafo3d.RotationX(Constant.PiHalf) * Trafo3d.Translation top))
                        Sg.Text(pin.ShortName, color = AVal.constant pin.PinColor, align = TextAlignment.Center)
                    }
                | None -> sg { Sg.NoEvents })

        // Live correspondence-pick preview: a cyan wire sphere + cross at the hovered
        // surface point while set-correspondence mode aims it (metric world → render).
        // On top so it reads against the surface. Fixed render size.
        let corrPreview =
            let active =
                (notFullscreen, model.CorrPreview) ||> AVal.map2 (fun nf c -> nf && Option.isSome c)
            let segs =
                AVal.custom (fun t ->
                    match model.CorrPreview.GetValue t with
                    | Some w ->
                        let cc = model.CommonCentroid.GetValue t
                        let s = datasetScale.GetValue t
                        let wR = ScanPin.renderCentre cc s w
                        let col = V4d(0.0, 0.78, 0.84, 0.95)
                        let out = ResizeArray<V3d * V3d * V4d * float>()
                        addWireSphere out wR 0.06 col 1.8 20
                        addCross out wR 0.075 col 1.8
                        out.ToArray()
                    | None -> [||])
            linesNodeTop active segs

        // Brushed individual samples (§T6/§A4): small solid dots at the brushed
        // samples' surface positions, looked up by gid in the SAME canonical array
        // the chart labels with — so a chart range-brush lands on the exact 3D
        // surface cells. Driven by Model.BrushedSamples (chart drag ONLY — no hover
        // reveal). While a brush is active the surface maps stand down and these
        // dots are the only value carriers (shared inspect range = the legend).
        let brushedDots =
            sg {
                Sg.Active notFullscreen
                Sg.View view
                Sg.Proj proj
                Sg.DepthTest (AVal.constant DepthTest.None)
                Sg.BlendMode (AVal.constant BlendMode.Blend)
                Sg.NoEvents
                Dots.render (brushedDotGeometry model None)
            }

        ASet.unionMany (ASet.ofList [pinDots; ASet.ofList [pinMarkerLines]; pinRings; pinGlyphs; pinSliceLines; pinLabels; ghostPreview; constellation; ASet.ofList [corrPreview]; ASet.ofList [brushedDots]])
