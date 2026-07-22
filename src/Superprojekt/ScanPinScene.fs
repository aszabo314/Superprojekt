namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open Aardworx.WebAssembly
open FSharp.Data.Adaptive
open Aardvark.Dom
open Superprojekt.LineGlyphs

module ScanPinScene =

    let private spherePos, sphereIdx = PinGeometry.buildIcosphere 2

    let private spherePosBuf = AVal.constant (ArrayBuffer spherePos :> IBuffer)
    let private sphereIdxBuf = AVal.constant (ArrayBuffer sphereIdx :> IBuffer)
    let private sphereIdxCnt = AVal.constant sphereIdx.Length

    // Placement hover preview shell.
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

    // Canonical per-sample list for distribution brushing — the single source
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
            let rf = model.ReferenceMesh.GetValue t
            let moving = names |> List.filter (fun n -> Some n <> rf)
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

    // Brushed sample base data: (renderPos, valueMm) per brushed dot;
    // meshFilter restricts to one mesh (the focus views).
    let private brushedBase (model : AdaptiveModel) (meshFilter : string option) : aval<(V3d * float)[]> =
        let canonA = brushSamples model
        let datasetScale =
            (model.ActiveDataset, model.DatasetScales) ||> AVal.map2 DatasetScale.active
        AVal.custom (fun t ->
            let brushed = model.BrushedSamples.GetValue t
            if Set.isEmpty brushed then [||]
            else
                let cc = model.CommonCentroid.GetValue t
                let scale = datasetScale.GetValue t
                let canon = canonA.GetValue t
                brushed |> Seq.choose (fun gid ->
                    if gid >= 0 && gid < canon.Length then
                        let (_, mesh, pos, vMm) = canon.[gid]
                        if meshFilter |> Option.forall ((=) mesh) then
                            Some (ScanPin.renderCentre cc scale pos, vMm)
                        else None
                    else None)
                |> Array.ofSeq)

    // One brushed-sample glyph: a screen-aligned circle with a cross through
    // it, as line segments; right/upv are the HALF-SIZE axis vectors.
    let private addGlyph (out : ResizeArray<V3d * V3d * V4d * float>)
                         (c : V3d) (right : V3d) (upv : V3d) (col : V4d) (w : float) =
        let n = 20
        let mutable prev = c + right
        for i in 1 .. n do
            let a = float i / float n * Constant.PiTimesTwo
            let p = c + right * cos a + upv * sin a
            out.Add(prev, p, col, w)
            prev <- p
        out.Add(c - right, c + right, col, w)
        out.Add(c - upv, c + upv, col, w)

    let private glyphInk (a : float) = V4d(0.22, 0.25, 0.30, a)

    // Main-3D brushed glyphs: CONSTANT SCREEN SIZE (BrushDotPx CSS px,
    // perspective per dot from its eye distance; view-dependent by design, like
    // the pin flags). The glyphs carry the shared difference viz — each dot's
    // stroke = its sample value through the ONE diverging map/range, over a
    // dark under-stroke so the near-white zero end stays readable on the plain
    // Inspect surface.
    let brushedDotSegments (model : AdaptiveModel) (viewportCss : aval<V2i>) (view : aval<Trafo3d>) =
        let baseA = brushedBase model None
        let rangeA = MeshView.inspectRange model
        AVal.custom (fun t ->
            let dots = baseA.GetValue t
            if dots.Length = 0 then [||]
            else
                let vt = view.GetValue t
                let right = vt.Backward.TransformDir V3d.IOO |> Vec.normalize
                let up = vt.Backward.TransformDir V3d.OIO |> Vec.normalize
                let eye = vt.Backward.TransformPos V3d.Zero
                let pxHalf = model.BrushDotPx.GetValue t * 0.5
                let vpY = float (max 1 (viewportCss.GetValue t).Y)
                let (lo, hi) = rangeA.GetValue t
                let out = ResizeArray<V3d * V3d * V4d * float>()
                for (p, vMm) in dots do
                    // Perspective (90° vertical fov ⇒ tan(fov/2) = 1): per dot.
                    let r = pxHalf * 2.0 * (Vec.distance p eye) / vpY
                    let rx, uy = right * r, up * r
                    addGlyph out p rx uy (V4d(0.13, 0.15, 0.18, 0.9)) 3.2
                    addGlyph out p rx uy (V4d(Primitives.Diff.colorSignedV3 lo hi (vMm * 0.001), 1.0)) 1.6
                out.ToArray())

    // Focus-view brushed glyphs: the same circle+cross mark, XY-aligned (exact
    // in the top-down views), at a fixed render size — the focus cameras keep
    // their own zoom conventions, so no px constancy is attempted.
    let brushedDotSegmentsFocus (model : AdaptiveModel) (name : string) =
        let baseA = brushedBase model (Some name)
        baseA |> AVal.map (fun dots ->
            let out = ResizeArray<V3d * V3d * V4d * float>()
            let r = 0.045
            for (p, _) in dots do
                addGlyph out p (V3d.IOO * r) (V3d.OIO * r) (glyphInk 1.0) 1.5
            out.ToArray())

    let build
            (env : Env<Message>)
            (view : aval<Trafo3d>) (proj : aval<Trafo3d>)
            (fullscreenActive : aval<bool>)
            (placementHover : aval<V3d option>)
            (placementValid : aval<bool option>)
            (viewportCss : aval<V2i>)
            (model : AdaptiveModel) =

        let datasetScale =
            (model.ActiveDataset, model.DatasetScales) ||> AVal.map2 DatasetScale.active

        let notFullscreen = AVal.map not fullscreenActive
        // The constellation renders only in the Correspondence workflow
        // (matching the focus panel's overlay).
        let inCorrespondence = model.WorkflowStep |> AVal.map ((=) Correspondence)
        let constellationActive =
            (notFullscreen, inCorrespondence) ||> AVal.map2 (&&)
        // Shared chrome for every line overlay.
        // LessOrEqual = occluded by foreground geometry (the spatial cue);
        // None = on top (constellation depth bias, selection circle).
        let linesNodeDT (depth : DepthTest) (active : aval<bool>) segs =
            sg {
                Sg.Active active
                Sg.View view
                Sg.Proj proj
                Sg.DepthTest (AVal.constant depth)
                Sg.BlendMode (AVal.constant BlendMode.Blend)
                Sg.NoEvents
                Lines.render segs
            }
        let linesNode = linesNodeDT DepthTest.LessOrEqual
        let linesNodeTop = linesNodeDT DepthTest.None
        let selectedId = model.Selection.Active |> AVal.map Selection.pin
        let pinIdSet = model.ScanPins.Pins |> AMap.toASet |> ASet.map fst
        let pinsVal = model.ScanPins.Pins |> AMap.toAVal
        // Project-wide up-normal (terrain-like data): the shared flag/ring axis;
        // None → per-pin probe axis (ScanPin.axisWith).
        let upNormalA = MeshView.projectUpNormal model
        let flagsActive = notFullscreen
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
                    // taps can't toggle twice; double-tap = select + 3D zoom.
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
        // the invisible pick proxy can't occlude it). The flag's base cross: sized
        // by the screen-constant flag height (view-dependent by design — recomputes
        // per camera move; a handful of pins keeps it cheap), but NEVER rotated
        // (axis-aligned, unlike the name).
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
                    let eye = (view.GetValue t).Backward.TransformPos V3d.Zero
                    let fs = model.FlagScale.GetValue t
                    let out = ResizeArray<V3d * V3d * V4d * float>()
                    for (id, centre) in HashMap.toSeq centres do
                        let isSel = sel = Some id
                        let hovered = hov = Some (HoverPin id)
                        let col =
                            if isSel || hovered then V4d(0.25, 0.28, 0.33, 0.85)
                            else V4d(0.45, 0.48, 0.53, 0.4)
                        let w = if isSel || hovered then 1.6 else 1.0
                        let cR = ScanPin.renderCentre cc scale centre
                        let h = ScanPin.flagHeightRender scale fs (Vec.length (eye - cR))
                        let l, thin = h * 0.10, h * 0.02
                        addBoxOutline out cR l thin thin col w
                        addBoxOutline out cR thin l thin col w
                        addBoxOutline out cR thin thin l col w
                    out.ToArray())
            linesNodeTop flagsActive segs

        // Pin influence visuals: a thin equator ring (⊥ probe axis, radius =
        // InnerRadius) + sphere–surface contact rings per visible mesh, in the
        // shared pin ink. Normal depth testing on purpose — occlusion is the
        // spatial cue.
        let pinRings =
            pinIdSet |> ASet.collect (fun id ->
                let pinVal = pinsVal |> AVal.map (fun pins -> HashMap.tryFind id pins)
                let isSelected = selectedId |> AVal.map (fun sel -> sel = Some id)
                // Per-field projections (adaptive-perf rule): a probe/slice cache
                // landing on this pin must not rebuild the ring geometry.
                let centreVal = pinVal |> AVal.map (Option.map (fun p -> p.Centre))
                let radiusVal = pinVal |> AVal.map (Option.map (fun p -> p.InnerRadius))
                let axisVal   = (pinVal, upNormalA) ||> AVal.map2 (fun po up -> po |> Option.map (ScanPin.axisWith up))
                let ringsVal  = pinVal |> AVal.map (Option.map (fun p -> match p.ContactRings with RingsReady m -> m | _ -> Map.empty))
                let segs =
                    AVal.custom (fun t ->
                        match centreVal.GetValue t, radiusVal.GetValue t, axisVal.GetValue t, ringsVal.GetValue t with
                        | Some centre, Some radius, Some axis, Some rings ->
                            let sel = isSelected.GetValue t
                            let hovered = model.Selection.Hovered.GetValue t = Some (HoverPin id)
                            let cc = model.CommonCentroid.GetValue t
                            let scale = datasetScale.GetValue t
                            // Shown-set gating (solo overlay): rings on a
                            // ghosted-away mesh would float without their surface.
                            let solo = model.MeshSolo.GetValue t
                            let a = if hovered || sel then 1.0 else 0.65
                            let coreW = if hovered then 3.0 elif sel then 2.2 else 1.4
                            let out = ResizeArray<V3d * V3d * V4d * float>()
                            let cR = ScanPin.renderCentre cc scale centre
                            let rR = ScanPin.renderLength scale radius
                            let nN, u, v = basisFromNormal axis
                            duplex (fun c w -> addRing out cR u v rR c w 64) a coreW
                            // 1 m direction indicator along the pin axis — thin
                            // + semitransparent (orientation, not geometry).
                            // Points up until the probe's PCA normal lands.
                            let axisCol = V4d(Primitives.pinInkV3d, (if sel then 0.5 else 0.35))
                            out.Add(cR, cR + nN * ScanPin.renderLength scale 1.0, axisCol, 1.0)
                            duplex (fun c w ->
                                for KeyValue(mesh, meshRings) in rings do
                                    if MeshVisibility.shown solo mesh then
                                        for ring in meshRings do
                                            if ring.Length >= 2 then
                                                let rp = ring |> Array.map (ScanPin.renderCentre cc scale)
                                                for i in 0 .. rp.Length - 2 do
                                                    out.Add(rp.[i], rp.[i + 1], c, w)) a coreW
                            out.ToArray()
                        | _ -> [||])
                ASet.ofList [ linesNode flagsActive segs ])

        // Correspondence constellation lines: per pin, a small wire-sphere + cross
        // glyph at every mesh's marker — the reference's RefAnchor drawn exactly like
        // a moving-mesh marker — plus a thin line from each moving glyph to the
        // reference point. All markers carry the shared pin ink (which mesh a marker
        // sits on is the hover-linked matrix cell's job). Fixed render size.
        // Project to the correspondence only — depending on the whole pin map
        // would rebuild the constellation buffer on any pin field change.
        let pinCorr = model.ScanPins.Pins |> AMap.map (fun _ p -> ScanPin.correspondence p) |> AMap.toAVal
        let constellation =
            let segs =
                AVal.custom (fun t ->
                    let pins = pinCorr.GetValue t
                    let cc = model.CommonCentroid.GetValue t
                    let scale = datasetScale.GetValue t
                    let sel = selectedId.GetValue t
                    let hov = model.Selection.Hovered.GetValue t
                    let names = model.MeshNames.Content.GetValue t |> IndexList.toList
                    let rf = model.ReferenceMesh.GetValue t
                    // Shown-set gating (solo overlay), so a locate shows
                    // only the located mesh's markers.
                    let solo = model.MeshSolo.GetValue t
                    let moving =
                        names |> List.filter (fun n ->
                            Some n <> rf && MeshVisibility.shown solo n)
                    let out = ResizeArray<V3d * V3d * V4d * float>()
                    for (id, c) in HashMap.toSeq pins do
                        match c.RefAnchor with
                        | Some ra ->
                                let isSel = sel = Some id
                                let pinHover = hov = Some (HoverPin id)
                                let emph = isSel || pinHover
                                let raR = ScanPin.renderCentre cc scale ra
                                (match rf with
                                 | Some rn when MeshVisibility.shown solo rn ->
                                    let refHover = hov = Some (HoverPoint (id, rn))
                                    let a = if refHover || emph then 1.0 else 0.55
                                    let gw = if refHover || isSel then 2.0 else 1.3
                                    duplex (fun c w ->
                                        addWireSphere out raR 0.055 c w 16
                                        addCross out raR 0.07 c w) a gw
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
                                        let rowHover = hov = Some (HoverPoint (id, mesh))
                                        let a = if rowHover || emph then 1.0 else 0.55
                                        let mw = if rowHover || isSel then 2.0 else 1.3
                                        let wR = ScanPin.renderCentre cc scale w
                                        duplex (fun c w ->
                                            addWireSphere out wR 0.055 c w 16
                                            addCross out wR 0.07 c w) a mw
                                        out.Add(wR, raR, V4d(Primitives.pinInkV3d, (if emph then 0.9 else 0.3)), (if isSel then 1.5 else 1.0))
                                    | None -> ()
                        | None -> ()
                    out.ToArray())
            ASet.ofList [ linesNodeTop constellationActive segs ]

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
            // WHITE: the uncommitted-transient layer — the tap commits it
            // into the committed pin geometry, exactly like the correspondence
            // ghost. Hard-prohibit: with < 2 meshes in range at the
            // hover the indicator goes very transparent (placement is refused).
            let ghostFade =
                placementValid |> AVal.map (fun v -> if v = Some false then 0.2 else 1.0)
            let outlineSegs =
                (placementHover, previewR, ghostFade) |||> AVal.map3 (fun hOpt r fade ->
                    match hOpt with
                    | Some c -> PinGeometry.buildSphereOutline c r (V4d(1.0, 1.0, 1.0, 0.9 * fade)) 1.5
                    | None -> [||])
            let shellCol = ghostFade |> AVal.map (fun fade -> V4d(1.0, 1.0, 1.0, 0.22 * fade))
            ASet.ofList [
                sphereShell view proj active trafo shellCol
                linesNode active outlineSegs
            ]

        // Pin flag pole (far view): a neutral pole + top ring along the probe axis
        // per committed pin, screen-constant size (ScanPin.flagHeightRender: fixed
        // screen fraction, world-clamped, gear-scaled — hence the view dependency).
        let pinFlags =
            let neutral = V4d(0.52, 0.55, 0.60, 0.75)
            let segs =
                AVal.custom (fun t ->
                    let pins  = pinsVal.GetValue t
                    let cc    = model.CommonCentroid.GetValue t
                    let scale = datasetScale.GetValue t
                    let eye = (view.GetValue t).Backward.TransformPos V3d.Zero
                    let fs = model.FlagScale.GetValue t
                    let up = upNormalA.GetValue t
                    let out   = ResizeArray<V3d * V3d * V4d * float>()
                    for (_, p) in HashMap.toSeq pins do
                        let col = neutral
                        let w   = 2.5
                        let aN, u, v = basisFromNormal (ScanPin.axisWith up p)
                        let c   = ScanPin.renderCentre cc scale p.Centre
                        let h   = ScanPin.flagHeightRender scale fs (Vec.length (eye - c))
                        let top = ScanPin.flagTopRender cc scale h aN p
                        out.Add(c, top, col, w)
                        addRing out top u v (h * 0.16) col w 24
                    out.ToArray())
            ASet.ofList [ linesNode flagsActive segs ]

        // Pin identity flag name: the pin's ShortName floating above the flag top —
        // a WHITE core over four dark offset copies (poor-man's text outline), so
        // the name reads on light and dark texture alike. Sized by the
        // screen-constant flag height and billboarded about Z so the text always
        // faces the camera — the flag's only rotating element (the base cross
        // stays axis-aligned). Always-on-top (DepthTest.None); the dark copies sit
        // in passOne, the white core in passTwo (within-pass order is arbitrary).
        // Identity is immutable → snapshot once per id (no atlas rebuild on
        // probe/ring updates); only the trafo is adaptive (uniform update, no
        // rebuild).
        let pinFlagFrame = model.ScanPins.Pins |> AMap.map (fun _ p -> p.Centre, ScanPin.axis p) |> AMap.toAVal
        let pinLabels =
            pinIdSet |> ASet.map (fun id ->
                let labelsActive = flagsActive
                let p0 = HashMap.tryFind id (AVal.force pinsVal)
                let trafoVal =
                    AVal.custom (fun t ->
                        let cc = model.CommonCentroid.GetValue t
                        let scale = datasetScale.GetValue t
                        match HashMap.tryFind id (pinFlagFrame.GetValue t) with
                        | Some (centre, pinAxis) ->
                            let axis = match upNormalA.GetValue t with Some u -> u | None -> pinAxis
                            let aN = if axis.Length > 1e-9 then axis.Normalized else V3d.OOI
                            let cR = ScanPin.renderCentre cc scale centre
                            let eye = (view.GetValue t).Backward.TransformPos V3d.Zero
                            let h = ScanPin.flagHeightRender scale (model.FlagScale.GetValue t) (Vec.length (eye - cR))
                            let pos = cR + aN * (h * 1.25)
                            let d = eye - pos
                            let yaw = if d.X * d.X + d.Y * d.Y < 1e-12 then 0.0 else atan2 d.X (-d.Y)
                            Trafo3d.Scale (h * 0.30) * Trafo3d.RotationX Constant.PiHalf
                            * Trafo3d.RotationZ yaw * Trafo3d.Translation pos
                        | None -> Trafo3d.Scale 0.0)
                match p0 with
                | Some pin ->
                    // Offsets in unscaled text units (pre-scale translation), so
                    // the halo thickness tracks the label size.
                    let copy (dx : float) (dy : float) (color : C4b) pass =
                        sg {
                            Sg.Pass pass
                            Sg.DepthTest (AVal.constant DepthTest.None)
                            Sg.Trafo (trafoVal |> AVal.map (fun tr -> Trafo3d.Translation(V3d(dx, dy, 0.0)) * tr))
                            Sg.Text(pin.ShortName, color = AVal.constant color, align = TextAlignment.Center)
                        }
                    let d = 0.05
                    sg {
                        Sg.Active labelsActive
                        Sg.View view
                        Sg.Proj proj
                        Sg.NoEvents
                        copy -d -d Primitives.pinInk RenderPass.passOne
                        copy  d -d Primitives.pinInk RenderPass.passOne
                        copy -d  d Primitives.pinInk RenderPass.passOne
                        copy  d  d Primitives.pinInk RenderPass.passOne
                        copy 0.0 0.0 C4b.White RenderPass.passTwo
                    }
                | None -> sg { Sg.NoEvents })

        // Live correspondence-pick preview: a WHITE wire sphere + cross at the
        // hovered surface point while set-correspondence mode aims it (metric
        // world → render). White = "not committed yet" — the click turns it into
        // the pin-coloured marker. A move > 10 cm (world) from the current anchor
        // adds a white arrow old → new (thin shaft + line-triangle tip, facing the
        // eye). On top so it reads against the surface.
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
                        let white = V4d(1.0, 1.0, 1.0, 0.95)
                        let out = ResizeArray<V3d * V3d * V4d * float>()
                        addWireSphere out wR 0.06 white 1.8 20
                        addCross out wR 0.075 white 1.8
                        (match model.CorrArm.GetValue t with
                         | Some (pid, mesh) ->
                            let orig =
                                HashMap.tryFind pid (pinsVal.GetValue t)
                                |> Option.map ScanPin.correspondence
                                |> Option.bind (Correspondence.anchorOwn (model.ReferenceMesh.GetValue t = Some mesh) mesh)
                                |> Option.map (fun own -> (dispWorldAt t mesh).Forward.TransformPos own)
                            match orig with
                            | Some ow when Vec.distance ow w > 0.1 ->
                                let eye = (view.GetValue t).Backward.TransformPos V3d.Zero
                                addArrow out (ScanPin.renderCentre cc s ow) wR eye white 1.8
                            | _ -> ()
                         | None -> ())
                        out.ToArray()
                    | None -> [||])
            linesNodeTop active segs

        // Selection circle: a dashed WHITE ring slightly larger than the selected
        // pin's influence radius, lifted to the median contact-ring height
        // (ScanPin.selectionCircleCentre) — the bright, uncoloured "this one"
        // marker (the other pins go greyscale). On top; main-3D twin of the
        // focus panel's circle.
        let selectionCircle =
            let segs =
                AVal.custom (fun t ->
                    match selectedId.GetValue t with
                    | Some id ->
                        match HashMap.tryFind id (pinsVal.GetValue t) with
                        | Some p ->
                            let cc = model.CommonCentroid.GetValue t
                            let s = datasetScale.GetValue t
                            let cR = ScanPin.renderCentre cc s (ScanPin.selectionCircleCentre p)
                            let rR = ScanPin.renderLength s (ScanPin.selectionCircleRadius p)
                            let out = ResizeArray<V3d * V3d * V4d * float>()
                            addDashedRing out cR V3d.IOO V3d.OIO rR (V4d(1.0, 1.0, 1.0, 0.95)) 2.2 72
                            out.ToArray()
                        | None -> [||]
                    | None -> [||])
            linesNodeTop notFullscreen segs

        // Brushed individual samples: screen-aligned circle+cross glyphs at the
        // brushed samples' surface positions, looked up by gid in the SAME
        // canonical array the charts label with — so a chart range-brush lands
        // on the exact 3D surface cells.
        let brushedDots =
            linesNodeTop notFullscreen (brushedDotSegments model viewportCss view)

        // Exact-point probe marker: a high-contrast wire-sphere + cross at the
        // probed surface point (the value reads in the dock charts' amber line).
        let pointProbeMark =
            let segs =
                AVal.custom (fun t ->
                    match model.PointProbe.GetValue t with
                    | Some (_, w, _) ->
                        let cc = model.CommonCentroid.GetValue t
                        let s = datasetScale.GetValue t
                        let pR = ScanPin.renderCentre cc s w
                        let out = ResizeArray<V3d * V3d * V4d * float>()
                        duplex (fun c wd ->
                            addWireSphere out pR 0.05 c wd 16
                            addCross out pR 0.065 c wd) 1.0 2.0
                        out.ToArray()
                    | None -> [||])
            linesNodeTop notFullscreen segs

        ASet.unionMany (ASet.ofList [pinDots; ASet.ofList [pinMarkerLines]; pinRings; pinFlags; pinLabels; ghostPreview; constellation; ASet.ofList [corrPreview]; ASet.ofList [selectionCircle]; ASet.ofList [brushedDots]; ASet.ofList [pointProbeMark]])
