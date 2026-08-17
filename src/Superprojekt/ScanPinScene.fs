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

    // Pin-scope isolation (Isolate pins ON at the Pin level): every pin but
    // the in-scope one drops to near-transparent — the 3D marks AND the tile
    // glyphs read this ONE rule. The subject = the selected pin (an active
    // draft is always the subject: placement clears the pin selection); same
    // armed-centre suspension as the mesh-side anchor ghost.
    let pinScopeIsoOn (model : AdaptiveModel) (t : AdaptiveToken) =
        model.Focus.GetValue t = FocusPin
        && LevelFlags.get FocusPin (model.AnchorGhostMode.GetValue t)
        && model.ArmedPick.GetValue t <> Some ArmCentre
    let pinScopeDim (model : AdaptiveModel) (t : AdaptiveToken) (id : ScanPinId) =
        if pinScopeIsoOn model t && (model.Sel.GetValue t).Pin <> Some id then 0.08 else 1.0

    // ── The committed-pin glyphs, module-level: the ortho tiles render the
    // SAME marks as the main 3D through these, so the two views cannot drift.

    // The correspondence locator's triplex arms (white rim / ink / identity
    // core), open-centred; `h` = the outer radius in render units — the
    // caller supplies the view's screen-constant conversion.
    let addCrosshairGlyph (out : ResizeArray<V3d * V3d * V4d * float>)
                          (cR : V3d) (right : V3d) (up : V3d) (h : float)
                          (col : V3d) (dim : float) =
        let rim = V4d(1.0, 1.0, 1.0, 0.85 * dim)
        let ink = V4d(Primitives.pinInkV3d, 0.9 * dim)
        let core = V4d(col, 0.95 * dim)
        for d in [| right; -right; up; -up |] do
            let p0 = cR + d * (0.3 * h)
            let p1 = cR + d * h
            out.Add(p0, p1, rim, 5.4)
            out.Add(p0, p1, ink, 3.4)
            out.Add(p0, p1, core, 1.7)

    // The main view's variant as GlyphLines CAM segments (centre + UNIT
    // camera-plane offsets — the vertex stage applies the screen-constant h,
    // so the buffer never rebuilds on a camera move).
    let addCrosshairGlyphC (out : ResizeArray<V3d * V2d * V2d * V4d * float>)
                           (cR : V3d) (col : V3d) (dim : float) =
        let rim = V4d(1.0, 1.0, 1.0, 0.85 * dim)
        let ink = V4d(Primitives.pinInkV3d, 0.9 * dim)
        let core = V4d(col, 0.95 * dim)
        for d in [| V2d.IO; -V2d.IO; V2d.OI; -V2d.OI |] do
            out.Add(cR, d * 0.3, d * 1.0, rim, 5.4)
            out.Add(cR, d * 0.3, d * 1.0, ink, 3.4)
            out.Add(cR, d * 0.3, d * 1.0, core, 1.7)

    // The area figure's thin duplex equator ring.
    let addAreaRing (out : ResizeArray<V3d * V3d * V4d * float>)
                    (cR : V3d) (u : V3d) (v : V3d) (rR : float) (dim : float) =
        duplex (fun c w -> addRing out cR u v rR c w 64) (0.65 * dim) 1.4

    // The sphere∩surface contact rings — PURE WHITE single-stroke (the
    // committed exception to duplex); ring points are metric world.
    let addContactRings (out : ResizeArray<V3d * V3d * V4d * float>)
                        (toRender : V3d -> V3d) (rings : Map<string, V3d[][]>) (dim : float) =
        let ringWhite = V4d(1.0, 1.0, 1.0, 0.85 * dim)
        for KeyValue(_mesh, meshRings) in rings do
            for ring in meshRings do
                if ring.Length >= 2 then
                    let rp = ring |> Array.map toRender
                    for i in 0 .. rp.Length - 2 do
                        out.Add(rp.[i], rp.[i + 1], ringWhite, 1.6)

    // The loud-highlight mark: a bold dashed double ring (ink under white)
    // at ×1.18 of the area ring.
    let addHighlightRing (out : ResizeArray<V3d * V3d * V4d * float>)
                         (cR : V3d) (u : V3d) (v : V3d) (rR : float) =
        addDashedRing out cR u v (rR * 1.18) (V4d(Primitives.pinInkV3d, 0.9)) 5.0 72
        addDashedRing out cR u v (rR * 1.18) (V4d(1.0, 1.0, 1.0, 0.95)) 3.0 72

    // The intersection reveal's white distance fade (`lines` in the point's
    // mesh's own frame; `toRender` bakes pose + dataset transform).
    let addRevealLines (out : ResizeArray<V3d * V3d * V4d * float>)
                       (toRender : V3d -> V3d) (localPt : V3d) (rMax : float)
                       (dim : float) (lines : V3d[][]) =
        for line in lines do
            for i in 0 .. line.Length - 2 do
                let a = line.[i]
                let b = line.[i + 1]
                let dMid = ((a + b) * 0.5 - localPt).Length
                // Outermost ring keeps ~0.2 alpha; the cuts' slight
                // overshoot past rMax runs out linearly.
                let fade =
                    if dMid <= rMax then 1.0 - 0.8 * ((dMid / rMax) ** 1.5)
                    else max 0.0 (0.2 * (1.0 - (dMid - rMax) / (0.15 * rMax)))
                if fade > 0.01 then
                    out.Add(toRender a, toRender b, V4d(1.0, 1.0, 1.0, 0.9 * fade * dim), 1.4)

    // The loud-highlight subject (pin-scope isolation OFF): the hovered pin
    // row's pin, else the focused pin inside the pair scopes.
    let highlightPin (model : AdaptiveModel) (t : AdaptiveToken) : ScanPinId option =
        if pinScopeIsoOn model t then None
        else
            match model.TilePinHover.GetValue t with
            | Some id -> Some id
            | None ->
                match model.Focus.GetValue t with
                | FocusPair | FocusPin -> (model.Sel.GetValue t).Pin
                | FocusMatrix -> None

    let build
            (env : Env<Message>)
            (view : aval<Trafo3d>) (proj : aval<Trafo3d>)
            (fullscreenActive : aval<bool>)
            (model : AdaptiveModel) =

        let datasetScale =
            (model.ActiveDataset, model.DatasetScales) ||> AVal.map2 DatasetScale.active

        let notFullscreen = AVal.map not fullscreenActive
        // Shared chrome for every line overlay.
        // LessOrEqual = occluded by foreground geometry (the spatial cue);
        // None = on top.
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
        // Dim-uniform variant: the node's hover/scope fade rides MarkDim, so a
        // hover transient costs one uniform update instead of re-tessellating
        // and re-uploading the node's whole buffer.
        let linesNodeDim (depth : DepthTest) (active : aval<bool>) (dim : aval<float32>) segs =
            sg {
                Sg.Active active
                Sg.View view
                Sg.Proj proj
                Sg.DepthTest (AVal.constant depth)
                Sg.BlendMode (AVal.constant BlendMode.Blend)
                Sg.NoEvents
                Lines.renderWith dim segs
            }
        let pinIdSet = model.ScanPins.Pins |> AMap.toASet |> ASet.map fst
        let pinsVal = model.ScanPins.Pins |> AMap.toAVal
        // Project-wide up-normal (terrain-like data): the shared flag/ring axis;
        // world-up fallback (ScanPin.axisWith).
        let upNormalA = MeshView.projectUpNormal model
        let flagsActive = notFullscreen
        let placementActive =
            model.ScanPins.Placement |> AVal.map (function PlacementActive _ -> true | _ -> false)

        // Displayed world transform of a mesh at the given token — pin geometry
        // is mesh-local, so its world rides these poses.
        let dispWorldAt (t : AdaptiveToken) (mesh : string) =
            MeshView.displayedWorldAt model t mesh

        // The pin's metric-world centre: it RIDES its placement mesh.
        let pinCentreWorldAt (t : AdaptiveToken) (p : ScanPin) =
            (dispWorldAt t p.AnchorMesh).Forward.TransformPos p.CentreLocal

        // Focus scoping: survey levels show every pin; Pair/Pin scope shows the
        // selected pair's pins only.
        let pinShownAt (t : AdaptiveToken) (pair : string * string) =
            MeshVisibility.pinShown (model.Focus.GetValue t) ((model.Sel.GetValue t).Pair) pair

        // Mesh identity colour of a marker (the palette index via MeshOrder;
        // the root reads reference gold).
        let meshColAt (t : AdaptiveToken) (mesh : string) =
            let i = HashMap.tryFind mesh (model.MeshOrder.Content.GetValue t) |> Option.defaultValue 0
            let isRoot = (model.RegGraph.GetValue t).Root = Some mesh
            Primitives.c4bToV3d (Primitives.meshColorRoot isRoot i)

        // The vis peek's isolate swap, mirrored from the shown-rule contexts.
        let peekSwapAt (t : AdaptiveToken) (pair : (string * string) option)
                       (iso : string option) (pf : string option) =
            match iso, pair with
            | Some m, Some (a, b) when model.PeekVis.GetValue t && (m = a || m = b) ->
                let other = if m = a then b else a
                Some other, (pf |> Option.map (fun x -> if x = m then other else x))
            | _ -> iso, pf

        // The brush/error-map default isolates folded into the committed lock
        // the same way the shown rule does it (an explicit lock wins).
        let isoLockAt (t : AdaptiveToken) =
            MeshView.committedIsoLockAt model t

        // A correspondence point's mesh-bound marks (the intersection reveal)
        // follow their MESH's solid visibility: solid under the EFFECTIVE
        // narrowing (hover previews replace the lock — a previewed-solid mesh
        // shows its marks) = full; solid only under the COMMITTED state
        // (lock / Sel.Point, peek-swapped) = faded 0.15 (the preview dims it,
        // never pops it away); solid under neither = HIDDEN (it would float
        // in the air). The armed transient is excluded — the global armed
        // fade already covers it. None = hidden, Some f = alpha factor.
        let markerAlphaAt (t : AdaptiveToken) (mesh : string) : float option =
            let focus = model.Focus.GetValue t
            let sel = model.Sel.GetValue t
            let gs = MeshView.graphMapScopeAt model t
            let isoC, pfC = peekSwapAt t sel.Pair (isoLockAt t) sel.Point
            let committed = MeshVisibility.shown focus sel.Pair isoC None gs pfC mesh
            let isoF, pfF =
                MeshVisibility.effectiveNarrowing (model.PinFocusHover.GetValue t) None
                    (model.TileIsolateHover.GetValue t) (isoLockAt t) sel.Point
            let isoF, pfF = peekSwapAt t sel.Pair isoF pfF
            let hp = model.MatrixHoverPair.GetValue t
            if MeshVisibility.shown focus sel.Pair isoF hp gs pfF mesh then Some 1.0
            elif committed then Some 0.15
            else None

        // The mesh whose isolation is in effect or being previewed (the ONE
        // effective narrowing, peek-swapped; iso wins over the point) —
        // drives the anchorage cue (dashed second ring) and the ◎-hover pin
        // fade.
        let isoCueMeshAt (t : AdaptiveToken) =
            let sel = model.Sel.GetValue t
            let iso, pf =
                MeshVisibility.effectiveNarrowing (model.PinFocusHover.GetValue t) None
                    (model.TileIsolateHover.GetValue t) (isoLockAt t) sel.Point
            let iso, pf = peekSwapAt t sel.Pair iso pf
            match iso with Some _ -> iso | None -> pf

        // ◎-side hover: pin marks of pins NOT anchored to the hovered mesh
        // fade (the anchored ones are the isolation's own pins).
        let anchorHoverDimAt (t : AdaptiveToken) (anchorMesh : string) =
            match model.PinFocusHover.GetValue t with
            | Some (HoverSide hm) -> if anchorMesh = hm then 1.0 else 0.15
            | _ -> 1.0

        let pinScopeDimAt (t : AdaptiveToken) (id : ScanPinId) = pinScopeDim model t id

        // Pin centre pick proxies: small invisible spheres carrying the
        // double-tap zoom. Alpha 0 → invisible in colour but still present in
        // the depth/id pick pass.
        let pinDots =
            pinIdSet |> ASet.map (fun id ->
                let pinVal = pinsVal |> AVal.map (fun pins -> HashMap.tryFind id pins)
                let trafo =
                    AVal.custom (fun t ->
                        match pinVal.GetValue t with
                        | Some p when pinShownAt t p.Pair ->
                            let cc = model.CommonCentroid.GetValue t
                            let s = datasetScale.GetValue t
                            let cR = ScanPin.renderCentre cc s (pinCentreWorldAt t p)
                            Trafo3d.Scale 0.07 * Trafo3d.Translation cR
                        | _ -> Trafo3d.Scale 0.0)
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
                    // Double-tap = 3D zoom onto the pin (a pure camera action).
                    Sg.OnDoubleTap(fun _ ->
                        match AVal.force placementActive with
                        | true -> true
                        | false ->
                            env.Emit [ZoomToPin id]
                            false)
                    Sg.VertexAttributes(
                        HashMap.ofList [ string DefaultSemantic.Positions, BufferView(spherePosBuf, typeof<V3f>) ])
                    Sg.Index(BufferView(sphereIdxBuf, typeof<int>))
                    Sg.Render sphereIdxCnt
                }
            )

        // The flag-height screen-constant sizing as GlyphLines uniforms:
        // h = clamp(0.1·ds, 20·ds, 0.10·eyeDist) × FlagScale — exactly
        // ScanPin.flagHeightRender with FlagScale factored out (the metric
        // clamp bounds scale with it too).
        let flagFrac = AVal.constant 0.10f
        let flagMinR = datasetScale |> AVal.map (fun ds -> float32 (0.1 * ds))
        let flagMaxR = datasetScale |> AVal.map (fun ds -> float32 (20.0 * ds))
        let flagScaleA = model.FlagScale |> AVal.map float32

        // Visible pin-centre marker: a small, faint neutral wire-box jack on top
        // (so the invisible pick proxy can't occlude it), NEVER rotated. UNIT
        // offsets — the vertex stage applies the screen-constant flag height,
        // so a camera move never re-tessellates (hover fades stay vertex-baked:
        // hover is rare next to camera frames and this buffer is small). The
        // draft's placed centre wears the same jack.
        let pinMarkerLines =
            let segs =
                AVal.custom (fun t ->
                    let pins = pinsVal.GetValue t
                    let cc = model.CommonCentroid.GetValue t
                    let scale = datasetScale.GetValue t
                    let out = ResizeArray<V3d * V3d * V3d * V4d * float>()
                    let armDim = if (model.ArmedPick.GetValue t).IsSome then 0.15 else 1.0
                    let addJack (cR : V3d) (dim : float) =
                        let col = V4d(0.45, 0.48, 0.53, 0.4 * dim)
                        let w = 1.0
                        GlyphLines.addGlyphBox out cR 0.10 0.02 0.02 col w
                        GlyphLines.addGlyphBox out cR 0.02 0.10 0.02 col w
                        GlyphLines.addGlyphBox out cR 0.02 0.02 0.10 col w
                    for (id, p) in HashMap.toSeq pins do
                        if pinShownAt t p.Pair then
                            addJack (ScanPin.renderCentre cc scale (pinCentreWorldAt t p))
                                    (armDim * anchorHoverDimAt t p.AnchorMesh * pinScopeDimAt t id)
                    (match model.ScanPins.Placement.GetValue t with
                     | PlacementActive d when pinShownAt t d.Pair ->
                        match d.Area with
                        | Some (m, local) ->
                            addJack (ScanPin.renderCentre cc scale ((dispWorldAt t m).Forward.TransformPos local))
                                    (armDim * anchorHoverDimAt t m)
                        | None -> ()
                     | _ -> ())
                    out.ToArray())
            sg {
                Sg.Active flagsActive
                Sg.View view
                Sg.Proj proj
                Sg.DepthTest (AVal.constant DepthTest.None)
                Sg.BlendMode (AVal.constant BlendMode.Blend)
                Sg.NoEvents
                GlyphLines.renderWorld flagFrac flagMinR flagMaxR flagScaleA segs
            }

        // Pin influence figure, ONE builder for committed pins and the draft
        // (a draft is a pin with parts missing — placed parts render final):
        // a thin duplex equator ring (⊥ display axis) + the anchorage cue +
        // sphere–surface contact rings per pair mesh — the sphere∩surface
        // intersection figures render PURE WHITE (a deliberate user choice
        // over the duplex convention). Fades while a pick is armed (marks must
        // not hide the pick spot). Normal depth testing on purpose — occlusion
        // is the spatial cue.
        // Geometry ONLY (dim = 1.0 baked): the armed/hover/scope fades ride the
        // node's MarkDim uniform, and the anchorage cue is its own Sg.Active-
        // gated node — so hover transients never re-tessellate the figure.
        let addAreaFigure (t : AdaptiveToken) (out : ResizeArray<V3d * V3d * V4d * float>)
                          (anchorMesh : string) (centreLocal : V3d) (radius : float)
                          (rings : Map<string, V3d[][]>) =
            let cc = model.CommonCentroid.GetValue t
            let scale = datasetScale.GetValue t
            let centre = (dispWorldAt t anchorMesh).Forward.TransformPos centreLocal
            let cR = ScanPin.renderCentre cc scale centre
            let rR = ScanPin.renderLength scale radius
            let axis = match upNormalA.GetValue t with Some u -> u | None -> V3d.OOI
            let nN, u, v = basisFromNormal axis
            addAreaRing out cR u v rR 1.0
            // 1 m direction indicator along the display axis — thin
            // + semitransparent (orientation, not geometry).
            let axisCol = V4d(Primitives.pinInkV3d, 0.35)
            out.Add(cR, cR + nN * ScanPin.renderLength scale 1.0, axisCol, 1.0)
            addContactRings out (ScanPin.renderCentre cc scale) rings 1.0

        // The anchorage cue (the tiles' dashed second ring, in 3D) — geometry
        // from the pin's figure inputs alone; the isolation state gates the
        // NODE, not the tessellation.
        let cueSegsOf (t : AdaptiveToken) (anchorMesh : string) (centreLocal : V3d) (radius : float) =
            let cc = model.CommonCentroid.GetValue t
            let scale = datasetScale.GetValue t
            let centre = (dispWorldAt t anchorMesh).Forward.TransformPos centreLocal
            let cR = ScanPin.renderCentre cc scale centre
            let rR = ScanPin.renderLength scale radius
            let axis = match upNormalA.GetValue t with Some u -> u | None -> V3d.OOI
            let _, u, v = basisFromNormal axis
            let out = ResizeArray<V3d * V3d * V4d * float>()
            addDashedRing out cR u v (rR * 1.08) (V4d(1.0, 1.0, 1.0, 0.85)) 1.5 64
            out.ToArray()

        let pinRings =
            pinIdSet |> ASet.collect (fun id ->
                let pinVal = pinsVal |> AVal.map (fun pins -> HashMap.tryFind id pins)
                // Per-field projections (adaptive-perf rule): a ring cache landing
                // on this pin must not rebuild the ring geometry.
                let geoVal = pinVal |> AVal.map (Option.map (fun p -> p.AnchorMesh, p.CentreLocal, p.InnerRadius, p.Pair))
                let ringsVal = pinVal |> AVal.map (Option.map (fun p -> match p.ContactRings with RingsReady m -> m | _ -> Map.empty))
                let shownA =
                    AVal.custom (fun t ->
                        match geoVal.GetValue t with
                        | Some (_, _, _, pair) -> pinShownAt t pair
                        | None -> false)
                let activeA = (flagsActive, shownA) ||> AVal.map2 (&&)
                // Hover/scope fades ride the MarkDim uniform (one uniform update
                // per hover transient, never a buffer rebuild).
                let dimA =
                    AVal.custom (fun t ->
                        match geoVal.GetValue t with
                        | Some (anchorMesh, _, _, _) ->
                            float32 (
                                (if (model.ArmedPick.GetValue t).IsSome then 0.15 else 1.0)
                                * anchorHoverDimAt t anchorMesh
                                * pinScopeDimAt t id)
                        | None -> 1.0f)
                let segs =
                    AVal.custom (fun t ->
                        match geoVal.GetValue t, ringsVal.GetValue t with
                        | Some (anchorMesh, centreLocal, radius, _), Some rings ->
                            let out = ResizeArray<V3d * V3d * V4d * float>()
                            addAreaFigure t out anchorMesh centreLocal radius rings
                            out.ToArray()
                        | _ -> [||])
                let cueActive =
                    AVal.custom (fun t ->
                        flagsActive.GetValue t
                        && (match geoVal.GetValue t with
                            | Some (anchorMesh, _, _, pair) ->
                                pinShownAt t pair && isoCueMeshAt t = Some anchorMesh
                            | None -> false))
                let cueSegs =
                    AVal.custom (fun t ->
                        match geoVal.GetValue t with
                        | Some (anchorMesh, centreLocal, radius, _) ->
                            cueSegsOf t anchorMesh centreLocal radius
                        | None -> [||])
                ASet.ofList [
                    linesNodeDim DepthTest.LessOrEqual activeA dimA segs
                    linesNodeDim DepthTest.LessOrEqual cueActive dimA cueSegs
                ])

        // The draft's area: the SAME figure, live from the moment the centre
        // lands (its contact rings arrive via the shared postlude).
        let draftAreaNode =
            let draftGeo =
                model.ScanPins.Placement |> AVal.map (function
                    | PlacementActive d ->
                        d.Area |> Option.map (fun (m, local) ->
                            m, local, d.Radius, d.Pair,
                            (match d.Rings with RingsReady r -> r | _ -> Map.empty))
                    | PlacementIdle -> None)
            let shownA =
                AVal.custom (fun t ->
                    flagsActive.GetValue t
                    && (match draftGeo.GetValue t with
                        | Some (_, _, _, pair, _) -> pinShownAt t pair
                        | None -> false))
            let dimA =
                AVal.custom (fun t ->
                    match draftGeo.GetValue t with
                    | Some (m, _, _, _, _) ->
                        float32 (
                            (if (model.ArmedPick.GetValue t).IsSome then 0.15 else 1.0)
                            * anchorHoverDimAt t m)
                    | None -> 1.0f)
            let segs =
                AVal.custom (fun t ->
                    match draftGeo.GetValue t with
                    | Some (m, local, radius, _, rings) ->
                        let out = ResizeArray<V3d * V3d * V4d * float>()
                        addAreaFigure t out m local radius rings
                        out.ToArray()
                    | None -> [||])
            let cueActive =
                AVal.custom (fun t ->
                    shownA.GetValue t
                    && (match draftGeo.GetValue t with
                        | Some (m, _, _, _, _) -> isoCueMeshAt t = Some m
                        | None -> false))
            let cueSegs =
                AVal.custom (fun t ->
                    match draftGeo.GetValue t with
                    | Some (m, local, radius, _, _) -> cueSegsOf t m local radius
                    | None -> [||])
            [ linesNodeDim DepthTest.LessOrEqual shownA dimA segs
              linesNodeDim DepthTest.LessOrEqual cueActive dimA cueSegs ]

        // Correspondence LOCATOR: a camera-aligned, screen-constant crosshair
        // whose centre IS the pick point — no 3D body, nothing occluded, an
        // open centre so the point itself stays bare. Triplex arms: a white
        // rim under an ink under-stroke under the mesh-identity core — the
        // ink separates the colour on terrain, the rim keeps the glyph
        // readable where the ink vanishes (the dark void background). NEVER
        // hides (it is the locator) — but a point whose mesh isn't solid
        // (isolation/preview) MUTES to the fade level instead of floating at
        // full strength; the pair scope and the global armed fade apply on
        // top. One segs pass for committed pins AND the draft's placed
        // points; the screen-constant sizing and camera alignment run in the
        // GlyphLines vertex stage, so a camera move never rebuilds the buffer.
        let crosshairNode =
            let segs =
                AVal.custom (fun t ->
                    let pins = pinsVal.GetValue t
                    let cc = model.CommonCentroid.GetValue t
                    let s = datasetScale.GetValue t
                    let armDim = if (model.ArmedPick.GetValue t).IsSome then 0.15 else 1.0
                    let out = ResizeArray<V3d * V2d * V2d * V4d * float>()
                    let pt (scopeDim : float) (mesh : string) (local : V3d) =
                        let vis = match markerAlphaAt t mesh with Some f -> f | None -> 0.15
                        addCrosshairGlyphC out
                            (ScanPin.renderCentre cc s ((dispWorldAt t mesh).Forward.TransformPos local))
                            (meshColAt t mesh) (armDim * vis * scopeDim)
                    for (id, p) in HashMap.toSeq pins do
                        if pinShownAt t p.Pair then
                            let sd = pinScopeDimAt t id
                            pt sd (fst p.Pair) p.PointA
                            pt sd (snd p.Pair) p.PointB
                    (match model.ScanPins.Placement.GetValue t with
                     | PlacementActive d when pinShownAt t d.Pair ->
                        d.PointA |> Option.iter (pt 1.0 (fst d.Pair))
                        d.PointB |> Option.iter (pt 1.0 (snd d.Pair))
                     | _ -> ())
                    out.ToArray())
            sg {
                Sg.Active notFullscreen
                Sg.View view
                Sg.Proj proj
                Sg.DepthTest (AVal.constant DepthTest.None)
                Sg.BlendMode (AVal.constant BlendMode.Blend)
                Sg.NoEvents
                GlyphLines.renderCam view
                    (AVal.constant 0.025f) (AVal.constant 0.0f) (AVal.constant 1.0e30f)
                    (AVal.constant 1.0f) segs
            }

        // The locator's INTERSECTION REVEAL: the local geometry of the
        // point's own mesh (concentric contact rings + vertical relief cuts,
        // mesh-local from the server) — white fading to transparent with
        // metric distance from the point. Follows the mesh-solid visibility
        // rule (markerAlphaAt) and normal depth testing; the crosshair takes
        // the same factor but mutes instead of hiding.
        // Geometry ONLY (dim = 1.0 baked; the per-segment distance fade stays
        // in the vertices — it depends on the point alone): the mesh-solid
        // visibility factor rides the node's MarkDim uniform, so a hover
        // transient never re-tessellates the reveal — the heaviest per-pin
        // line geometry in the scene.
        let revealSegs (t : AdaptiveToken) (out : ResizeArray<V3d * V3d * V4d * float>)
                       (mesh : string) (localPt : V3d) (lines : V3d[][]) =
            let cc = model.CommonCentroid.GetValue t
            let s = datasetScale.GetValue t
            let tw = dispWorldAt t mesh
            let rMax = max 0.01 (model.RevealRadius.GetValue t)
            addRevealLines out (fun p -> ScanPin.renderCentre cc s (tw.Forward.TransformPos p))
                localPt rMax 1.0 lines

        // The mesh-solid alpha of one reveal side (markerAlphaAt semantics):
        // hidden = 0 (the node's Active gate drops it), faded = 0.15, full = 1;
        // × the global armed fade × the pin-scope dim.
        let revealDim (t : AdaptiveToken) (mesh : string) (scopeDim : float) =
            match markerAlphaAt t mesh with
            | None -> 0.0f
            | Some f ->
                float32 (
                    (if (model.ArmedPick.GetValue t).IsSome then 0.15 else 1.0)
                    * f * scopeDim)

        let pointReveals =
            pinIdSet |> ASet.collect (fun id ->
                let pinVal = pinsVal |> AVal.map (fun pins -> HashMap.tryFind id pins)
                // One node per reveal SIDE — each side follows its OWN mesh's
                // solid visibility.
                let sideNode (side : int) =
                    let rv =
                        pinVal |> AVal.map (Option.map (fun p ->
                            p.Pair,
                            (if side = 0 then p.PointA else p.PointB),
                            (match (if side = 0 then p.RevealA else p.RevealB) with
                             | RevealReady l -> l | _ -> [||])))
                    let meshOf (pair : string * string) = if side = 0 then fst pair else snd pair
                    let dimA =
                        AVal.custom (fun t ->
                            match rv.GetValue t with
                            | Some (pair, _, _) -> revealDim t (meshOf pair) (pinScopeDimAt t id)
                            | None -> 0.0f)
                    let activeA =
                        AVal.custom (fun t ->
                            notFullscreen.GetValue t
                            && (match rv.GetValue t with
                                | Some (pair, _, lines) -> lines.Length > 0 && pinShownAt t pair
                                | None -> false)
                            && dimA.GetValue t > 0.0f)
                    let segs =
                        AVal.custom (fun t ->
                            match rv.GetValue t with
                            | Some (pair, pt, lines) when lines.Length > 0 ->
                                let out = ResizeArray<V3d * V3d * V4d * float>()
                                revealSegs t out (meshOf pair) pt lines
                                out.ToArray()
                            | _ -> [||])
                    linesNodeDim DepthTest.LessOrEqual activeA dimA segs
                ASet.ofList [ sideNode 0; sideNode 1 ])

        let draftReveal =
            let sideNode (side : int) =
                let rv =
                    model.ScanPins.Placement |> AVal.map (function
                        | PlacementActive d ->
                            let pt = if side = 0 then d.PointA else d.PointB
                            let lines =
                                match (if side = 0 then d.RevealA else d.RevealB) with
                                | RevealReady l -> l | _ -> [||]
                            (match pt with
                             | Some p when lines.Length > 0 -> Some (d.Pair, p, lines)
                             | _ -> None)
                        | PlacementIdle -> None)
                let meshOf (pair : string * string) = if side = 0 then fst pair else snd pair
                let dimA =
                    AVal.custom (fun t ->
                        match rv.GetValue t with
                        | Some (pair, _, _) -> revealDim t (meshOf pair) 1.0
                        | None -> 0.0f)
                let activeA =
                    AVal.custom (fun t ->
                        notFullscreen.GetValue t
                        && (match rv.GetValue t with
                            | Some (pair, _, _) -> pinShownAt t pair
                            | None -> false)
                        && dimA.GetValue t > 0.0f)
                let segs =
                    AVal.custom (fun t ->
                        match rv.GetValue t with
                        | Some (pair, pt, lines) ->
                            let out = ResizeArray<V3d * V3d * V4d * float>()
                            revealSegs t out (meshOf pair) pt lines
                            out.ToArray()
                        | None -> [||])
                linesNodeDim DepthTest.LessOrEqual activeA dimA segs
            [ sideNode 0; sideNode 1 ]

        // Loud highlight of the focused/hovered pin (isolation OFF): a BOLD
        // dashed second ring around the ground ring, no depth test (reads
        // through terrain), plus a white box around the flag label — both
        // white-over-ink so they carry on any background. One pin at a time,
        // view-dependent by design (a single glyph set per camera move).
        let highlightNode =
            let segs =
                AVal.custom (fun t ->
                    match highlightPin model t with
                    | Some id ->
                        match HashMap.tryFind id (pinsVal.GetValue t) with
                        | Some p when pinShownAt t p.Pair ->
                            let cc = model.CommonCentroid.GetValue t
                            let scale = datasetScale.GetValue t
                            let cR = ScanPin.renderCentre cc scale (pinCentreWorldAt t p)
                            let rR = ScanPin.renderLength scale p.InnerRadius
                            let axis = match upNormalA.GetValue t with Some u -> u | None -> V3d.OOI
                            let aN, u, v = basisFromNormal axis
                            let out = ResizeArray<V3d * V3d * V4d * float>()
                            addHighlightRing out cR u v rR
                            // The label box, in the label's own billboard frame
                            // (mirrors the pinLabels placement math).
                            let eye = (view.GetValue t).Backward.TransformPos V3d.Zero
                            let h = ScanPin.flagHeightRender scale (model.FlagScale.GetValue t) (Vec.length (eye - cR))
                            let pos = cR + aN * (h * 1.25)
                            let d = eye - pos
                            let yaw = if d.X * d.X + d.Y * d.Y < 1e-12 then 0.0 else atan2 d.X (-d.Y)
                            let s = h * 0.30
                            let dx = V3d(cos yaw, sin yaw, 0.0)
                            let corner (x : float) (y : float) = pos + dx * (x * s) + V3d.OOI * (y * s)
                            let cs = [| corner -0.95 -0.30; corner 0.95 -0.30; corner 0.95 1.05; corner -0.95 1.05 |]
                            for i in 0 .. 3 do
                                out.Add(cs.[i], cs.[(i + 1) % 4], V4d(Primitives.pinInkV3d, 0.9), 4.0)
                            for i in 0 .. 3 do
                                out.Add(cs.[i], cs.[(i + 1) % 4], V4d(1.0, 1.0, 1.0, 0.95), 2.2)
                            out.ToArray()
                        | _ -> [||]
                    | None -> [||])
            linesNodeTop notFullscreen segs

        // Brushed diagram samples in 3D: flat camera-facing DISCS — a pure
        // locator + colour channel, no 3D relief to occlude the surface they
        // measure. Screen-constant radius inside a metric clamp, so zooming in
        // reveals the true coordinate instead of inflating the mark.
        // gid-addressed into the canonical inspect stream (MeshView.
        // inspectBlocksAt — the pair's pins, or every edge's pins at Matrix);
        // ≤12000 (reducer cap), hence the strict buffer discipline — the
        // geometry depends on the brush ALONE (the hovered dot rides a separate
        // node, so a hover never re-uploads the dot buffers, and the camera
        // only moves uniforms).
        let discRadii =
            datasetScale |> AVal.map (fun s ->
                ScanPin.renderLength s 0.005, ScanPin.renderLength s 0.5)
        // Each block's samples sit on ITS OWN moving mesh, so its dots follow
        // that mesh's solid visibility (the A10 marker rule): full, faded, or
        // gone with the mesh — no dot floats over absent geometry. At graph
        // scope every edge contributes its own owner.
        // GEOMETRY depends on the brush + data alone; the per-mesh visibility
        // fade rides the DiscAlpha slot-uniform (slot = display index — the
        // OutlineMask convention), so a hover transient moves 32 floats
        // instead of re-tessellating up to 12000 discs.
        let brushedDots =
            AVal.custom (fun t ->
                let brush = model.BrushedSamples.GetValue t
                let blocks = MeshView.inspectBlocksAt model t
                if Set.isEmpty brush || blocks.Length = 0 then [||]
                else
                    let cc = model.CommonCentroid.GetValue t
                    let s = datasetScale.GetValue t
                    // The dots ARE samples of the surface field: same ramp,
                    // same range as the false-colour map and the legend.
                    let lo, hi = MeshView.inspectRangeAt model t
                    let order = model.MeshOrder.Content.GetValue t
                    let out = ResizeArray<V3d * V4d * int>()
                    let mutable gid = 0
                    for b in blocks do
                        let slot = HashMap.tryFind b.Mov order |> Option.defaultValue 0
                        let r = b.Err
                        for i in 0 .. r.Samples.Length - 1 do
                            if Set.contains gid brush && i < r.Positions.Length then
                                let c = Primitives.Diff.colorSignedV3 lo hi r.Samples.[i]
                                out.Add(ScanPin.renderCentre cc s r.Positions.[i], V4d(c, 1.0), slot)
                            gid <- gid + 1
                    out.ToArray())
        let dotAlphas =
            AVal.custom (fun t ->
                let order = model.MeshOrder.Content.GetValue t
                let a = Array.create 32 1.0f
                for (mesh, idx) in HashMap.toSeq order do
                    if idx >= 0 && idx < 32 then
                        a.[idx] <- match markerAlphaAt t mesh with Some f -> float32 f | None -> 0.0f
                a)
        let brushedSampleNode =
            sg {
                Sg.Active notFullscreen
                Sg.View view
                Sg.Proj proj
                Sg.DepthTest (AVal.constant DepthTest.None)
                Sg.BlendMode (AVal.constant BlendMode.Blend)
                Sg.NoEvents
                Discs.render view (discRadii |> AVal.map fst) (discRadii |> AVal.map snd) dotAlphas brushedDots
            }

        // The 3D-hovered dot's mark: a duplex ring around it (the diagram
        // cross-highlights the same gid). ONE glyph — the sanctioned
        // camera-dependent rebuild.
        let hoverRingNode =
            // The gid lookup walks the whole inspect stream — split from the
            // view-dependent ring so it runs per HOVER change, not per camera
            // frame while a dot is hovered.
            let hoverPosA =
                AVal.custom (fun t ->
                    match model.HoverSample.GetValue t with
                    | Some hov ->
                        let cc = model.CommonCentroid.GetValue t
                        let s = datasetScale.GetValue t
                        let mutable found = None
                        let mutable gid = 0
                        for b in MeshView.inspectBlocksAt model t do
                            let r = b.Err
                            for i in 0 .. r.Samples.Length - 1 do
                                if gid = hov && i < r.Positions.Length then
                                    found <-
                                        markerAlphaAt t b.Mov
                                        |> Option.map (fun vis -> ScanPin.renderCentre cc s r.Positions.[i], vis)
                                gid <- gid + 1
                        found
                    | None -> None)
            let segs =
                AVal.custom (fun t ->
                    match hoverPosA.GetValue t with
                    | Some (c, vis) ->
                        let vb = (view.GetValue t).Backward
                        let eye = vb.TransformPos V3d.Zero
                        let right = vb.TransformDir V3d.IOO
                        let up = vb.TransformDir V3d.OIO
                        let minR, maxR = discRadii.GetValue t
                        let out = ResizeArray<V3d * V3d * V4d * float>()
                        let rad = 2.2 * clamp minR maxR (float Discs.screenFrac * Vec.length (eye - c))
                        duplex (fun col w -> addRing out c right up rad col w 32) (0.95 * vis) 1.4
                        out.ToArray()
                    | None -> [||])
            linesNodeTop notFullscreen segs

        // The armed pick's cursor preview: what is ABOUT to be placed, at the
        // hovered surface point — single-stroke pure white (the uncommitted
        // convention), DEPTH-COMPOSED with the meshes (linesNode) so the far
        // side of the wire sphere reads behind terrain; the centre itself
        // always sits on the frontmost solid surface (the GPU pick), so the
        // marker never fully hides. The shader's ArmSphere band paints the
        // live sphere∩surface intersection alongside. The same model state
        // renders in the Pin tiles, so the preview is synchronized across
        // every view.
        let armPreviewMarks =
            let segs =
                AVal.custom (fun t ->
                    match model.ArmedPick.GetValue t, model.ArmPreview.GetValue t with
                    | Some target, Some world ->
                        let cc = model.CommonCentroid.GetValue t
                        let s = datasetScale.GetValue t
                        let cR = ScanPin.renderCentre cc s world
                        let white = V4d(1.0, 1.0, 1.0, 0.9)
                        let out = ResizeArray<V3d * V3d * V4d * float>()
                        (match target with
                         | ArmCentre ->
                            let r = MeshView.armCommitRadiusAt model t
                            let rR = ScanPin.renderLength s r
                            for seg in PinGeometry.buildSphereOutline cR rR (V4d(1.0, 1.0, 1.0, 0.7)) 1.4 do
                                out.Add seg
                            addCross out cR (rR * 0.15) white 1.6
                         | ArmPoint _ ->
                            addWireSphere out cR 0.06 white 1.6 20
                            addCross out cR 0.075 white 1.6)
                        out.ToArray()
                    | _ -> [||])
            linesNode notFullscreen segs

        // Pin flag pole (far view): a neutral pole + top ring along the display
        // axis per committed pin. UNIT offsets (pole 0→axis, ring at the top,
        // per-pin axes baked in) — the screen-constant flag height runs in the
        // GlyphLines vertex stage, so a camera move never rebuilds the buffer.
        let pinFlags =
            let neutral = V4d(0.52, 0.55, 0.60, 0.75)
            let segs =
                AVal.custom (fun t ->
                    let pins  = pinsVal.GetValue t
                    let cc    = model.CommonCentroid.GetValue t
                    let scale = datasetScale.GetValue t
                    let up = upNormalA.GetValue t
                    let out   = ResizeArray<V3d * V3d * V3d * V4d * float>()
                    for (_, p) in HashMap.toSeq pins do
                        if pinShownAt t p.Pair then
                            let col = neutral
                            let w   = 2.5
                            let aN, u, v = basisFromNormal (ScanPin.axisWith up p)
                            let c   = ScanPin.renderCentre cc scale (pinCentreWorldAt t p)
                            out.Add(c, V3d.Zero, aN, col, w)
                            for i in 0 .. 23 do
                                let a0 = float i / 24.0 * Constant.PiTimesTwo
                                let a1 = float (i + 1) / 24.0 * Constant.PiTimesTwo
                                out.Add(c, aN + (u * cos a0 + v * sin a0) * 0.16,
                                           aN + (u * cos a1 + v * sin a1) * 0.16, col, w)
                    out.ToArray())
            let node =
                sg {
                    Sg.Active flagsActive
                    Sg.View view
                    Sg.Proj proj
                    Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
                    Sg.BlendMode (AVal.constant BlendMode.Blend)
                    Sg.NoEvents
                    GlyphLines.renderWorld flagFrac flagMinR flagMaxR flagScaleA segs
                }
            ASet.ofList [ node ]

        // Pin identity flag name: the pin's ShortName floating above the flag top —
        // a WHITE core over four dark offset copies (poor-man's text outline), so
        // the name reads on light and dark texture alike. Sized by the
        // screen-constant flag height and billboarded about Z so the text always
        // faces the camera — the flag's only rotating element (the base cross
        // stays axis-aligned). Always-on-top (DepthTest.None); the dark copies sit
        // in passOne, the white core in passTwo (within-pass order is arbitrary).
        // Identity is immutable → snapshot once per id (no atlas rebuild on
        // ring updates); only the trafo is adaptive (uniform update, no rebuild).
        let pinLabels =
            pinIdSet |> ASet.map (fun id ->
                let labelsActive = flagsActive
                let p0 = HashMap.tryFind id (AVal.force pinsVal)
                let geoVal = pinsVal |> AVal.map (fun pins ->
                    HashMap.tryFind id pins |> Option.map (fun p -> p.AnchorMesh, p.CentreLocal, p.Pair))
                let trafoVal =
                    AVal.custom (fun t ->
                        let cc = model.CommonCentroid.GetValue t
                        let scale = datasetScale.GetValue t
                        match geoVal.GetValue t with
                        | Some (anchorMesh, centreLocal, pair) when pinShownAt t pair ->
                            let axis = match upNormalA.GetValue t with Some u -> u | None -> V3d.OOI
                            let aN = if axis.Length > 1e-9 then axis.Normalized else V3d.OOI
                            let centre = (dispWorldAt t anchorMesh).Forward.TransformPos centreLocal
                            let cR = ScanPin.renderCentre cc scale centre
                            let eye = (view.GetValue t).Backward.TransformPos V3d.Zero
                            let h = ScanPin.flagHeightRender scale (model.FlagScale.GetValue t) (Vec.length (eye - cR))
                            let pos = cR + aN * (h * 1.25)
                            let d = eye - pos
                            let yaw = if d.X * d.X + d.Y * d.Y < 1e-12 then 0.0 else atan2 d.X (-d.Y)
                            Trafo3d.Scale (h * 0.30) * Trafo3d.RotationX Constant.PiHalf
                            * Trafo3d.RotationZ yaw * Trafo3d.Translation pos
                        | _ -> Trafo3d.Scale 0.0)
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

        ASet.unionMany (ASet.ofList [pinDots; ASet.ofList [pinMarkerLines]; pinRings; pointReveals; ASet.ofList (draftAreaNode @ draftReveal @ [highlightNode; crosshairNode; brushedSampleNode; hoverRingNode; armPreviewMarks]); pinFlags; pinLabels])
