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

    // Small flat-colour icosphere (point-marker fill; the Pin panes reuse it).
    let sphereShell
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

        // Mesh identity colour of a marker (the palette index via MeshOrder).
        let meshColAt (t : AdaptiveToken) (mesh : string) =
            let i = HashMap.tryFind mesh (model.MeshOrder.Content.GetValue t) |> Option.defaultValue 0
            let c = Primitives.meshColor i
            V3d(float c.R / 255.0, float c.G / 255.0, float c.B / 255.0)

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

        // Visible pin-centre marker: a small, faint neutral wire-box jack on top
        // (so the invisible pick proxy can't occlude it), sized by the
        // screen-constant flag height (view-dependent by design — recomputes per
        // camera move; a handful of pins keeps it cheap), NEVER rotated. The
        // draft's placed centre wears the same jack.
        let pinMarkerLines =
            let segs =
                AVal.custom (fun t ->
                    let pins = pinsVal.GetValue t
                    let cc = model.CommonCentroid.GetValue t
                    let scale = datasetScale.GetValue t
                    let eye = (view.GetValue t).Backward.TransformPos V3d.Zero
                    let fs = model.FlagScale.GetValue t
                    let out = ResizeArray<V3d * V3d * V4d * float>()
                    let armDim = if (model.ArmedPick.GetValue t).IsSome then 0.15 else 1.0
                    let addJack (cR : V3d) (dim : float) =
                        let col = V4d(0.45, 0.48, 0.53, 0.4 * dim)
                        let w = 1.0
                        let h = ScanPin.flagHeightRender scale fs (Vec.length (eye - cR))
                        let l, thin = h * 0.10, h * 0.02
                        addBoxOutline out cR l thin thin col w
                        addBoxOutline out cR thin l thin col w
                        addBoxOutline out cR thin thin l col w
                    for (_, p) in HashMap.toSeq pins do
                        if pinShownAt t p.Pair then
                            addJack (ScanPin.renderCentre cc scale (pinCentreWorldAt t p))
                                    (armDim * anchorHoverDimAt t p.AnchorMesh)
                    (match model.ScanPins.Placement.GetValue t with
                     | PlacementActive d when pinShownAt t d.Pair ->
                        match d.Area with
                        | Some (m, local) ->
                            addJack (ScanPin.renderCentre cc scale ((dispWorldAt t m).Forward.TransformPos local))
                                    (armDim * anchorHoverDimAt t m)
                        | None -> ()
                     | _ -> ())
                    out.ToArray())
            linesNodeTop flagsActive segs

        // Pin influence figure, ONE builder for committed pins and the draft
        // (a draft is a pin with parts missing — placed parts render final):
        // a thin duplex equator ring (⊥ display axis) + the anchorage cue +
        // sphere–surface contact rings per pair mesh — the sphere∩surface
        // intersection figures render PURE WHITE (a deliberate user choice
        // over the duplex convention). Fades while a pick is armed (marks must
        // not hide the pick spot). Normal depth testing on purpose — occlusion
        // is the spatial cue.
        let addAreaFigure (t : AdaptiveToken) (out : ResizeArray<V3d * V3d * V4d * float>)
                          (anchorMesh : string) (centreLocal : V3d) (radius : float)
                          (rings : Map<string, V3d[][]>) =
            let cc = model.CommonCentroid.GetValue t
            let scale = datasetScale.GetValue t
            let dim =
                (if (model.ArmedPick.GetValue t).IsSome then 0.15 else 1.0)
                * anchorHoverDimAt t anchorMesh
            let a = 0.65 * dim
            let coreW = 1.4
            let centre = (dispWorldAt t anchorMesh).Forward.TransformPos centreLocal
            let cR = ScanPin.renderCentre cc scale centre
            let rR = ScanPin.renderLength scale radius
            let axis = match upNormalA.GetValue t with Some u -> u | None -> V3d.OOI
            let nN, u, v = basisFromNormal axis
            duplex (fun c w -> addRing out cR u v rR c w 64) a coreW
            // Anchorage cue while the anchor mesh is isolated (or its
            // isolation previewed) — the tiles' dashed second ring, in 3D.
            (match isoCueMeshAt t with
             | Some m when m = anchorMesh ->
                addDashedRing out cR u v (rR * 1.08) (V4d(1.0, 1.0, 1.0, 0.85 * dim)) 1.5 64
             | _ -> ())
            // 1 m direction indicator along the display axis — thin
            // + semitransparent (orientation, not geometry).
            let axisCol = V4d(Primitives.pinInkV3d, 0.35 * dim)
            out.Add(cR, cR + nN * ScanPin.renderLength scale 1.0, axisCol, 1.0)
            let ringWhite = V4d(1.0, 1.0, 1.0, 0.85 * dim)
            for KeyValue(_mesh, meshRings) in rings do
                for ring in meshRings do
                    if ring.Length >= 2 then
                        let rp = ring |> Array.map (ScanPin.renderCentre cc scale)
                        for i in 0 .. rp.Length - 2 do
                            out.Add(rp.[i], rp.[i + 1], ringWhite, 1.6)

        let pinRings =
            pinIdSet |> ASet.collect (fun id ->
                let pinVal = pinsVal |> AVal.map (fun pins -> HashMap.tryFind id pins)
                // Per-field projections (adaptive-perf rule): a ring cache landing
                // on this pin must not rebuild the ring geometry.
                let geoVal = pinVal |> AVal.map (Option.map (fun p -> p.AnchorMesh, p.CentreLocal, p.InnerRadius, p.Pair))
                let ringsVal = pinVal |> AVal.map (Option.map (fun p -> match p.ContactRings with RingsReady m -> m | _ -> Map.empty))
                let segs =
                    AVal.custom (fun t ->
                        match geoVal.GetValue t, ringsVal.GetValue t with
                        | Some (anchorMesh, centreLocal, radius, pair), Some rings when pinShownAt t pair ->
                            let out = ResizeArray<V3d * V3d * V4d * float>()
                            addAreaFigure t out anchorMesh centreLocal radius rings
                            out.ToArray()
                        | _ -> [||])
                ASet.ofList [ linesNode flagsActive segs ])

        // The draft's area: the SAME figure, live from the moment the centre
        // lands (its contact rings arrive via the shared postlude).
        let draftAreaNode =
            let segs =
                AVal.custom (fun t ->
                    match model.ScanPins.Placement.GetValue t with
                    | PlacementActive d when pinShownAt t d.Pair ->
                        match d.Area with
                        | Some (m, local) ->
                            let out = ResizeArray<V3d * V3d * V4d * float>()
                            let rings = match d.Rings with RingsReady r -> r | _ -> Map.empty
                            addAreaFigure t out m local d.Radius rings
                            out.ToArray()
                        | None -> [||]
                    | _ -> [||])
            linesNode flagsActive segs

        // Correspondence LOCATOR: a camera-aligned, screen-constant crosshair
        // whose centre IS the pick point — no 3D body, nothing occluded, an
        // open centre so the point itself stays bare. Mesh-identity colour
        // over an ink under-stroke. NEVER hides (it is the locator) — but a
        // point whose mesh isn't solid (isolation/preview) MUTES to the fade
        // level instead of floating at full strength; the pair scope and the
        // global armed fade apply on top. One segs pass for committed pins
        // AND the draft's placed points (view-dependent by design —
        // recomputes per camera move; a handful of pins keeps it cheap).
        let crosshairNode =
            let segs =
                AVal.custom (fun t ->
                    let pins = pinsVal.GetValue t
                    let cc = model.CommonCentroid.GetValue t
                    let s = datasetScale.GetValue t
                    let vb = (view.GetValue t).Backward
                    let eye = vb.TransformPos V3d.Zero
                    let right = vb.TransformDir V3d.IOO
                    let up = vb.TransformDir V3d.OIO
                    let armDim = if (model.ArmedPick.GetValue t).IsSome then 0.15 else 1.0
                    let out = ResizeArray<V3d * V3d * V4d * float>()
                    let addCrosshair (cR : V3d) (col : V3d) (dim : float) =
                        let h = 0.025 * Vec.length (eye - cR)
                        let ink = V4d(Primitives.pinInkV3d, 0.9 * dim)
                        let core = V4d(col, 0.95 * dim)
                        for d in [| right; -right; up; -up |] do
                            let p0 = cR + d * (0.3 * h)
                            let p1 = cR + d * h
                            out.Add(p0, p1, ink, 3.4)
                            out.Add(p0, p1, core, 1.7)
                    let pt (mesh : string) (local : V3d) =
                        let vis = match markerAlphaAt t mesh with Some f -> f | None -> 0.15
                        addCrosshair
                            (ScanPin.renderCentre cc s ((dispWorldAt t mesh).Forward.TransformPos local))
                            (meshColAt t mesh) (armDim * vis)
                    for (_, p) in HashMap.toSeq pins do
                        if pinShownAt t p.Pair then
                            pt (fst p.Pair) p.PointA
                            pt (snd p.Pair) p.PointB
                    (match model.ScanPins.Placement.GetValue t with
                     | PlacementActive d when pinShownAt t d.Pair ->
                        d.PointA |> Option.iter (pt (fst d.Pair))
                        d.PointB |> Option.iter (pt (snd d.Pair))
                     | _ -> ())
                    out.ToArray())
            linesNodeTop notFullscreen segs

        // The locator's INTERSECTION REVEAL: the local geometry of the
        // point's own mesh (concentric contact rings + vertical relief cuts,
        // mesh-local from the server) — white fading to transparent with
        // metric distance from the point. Follows the mesh-solid visibility
        // rule (markerAlphaAt) and normal depth testing; the crosshair takes
        // the same factor but mutes instead of hiding.
        let revealSegs (t : AdaptiveToken) (out : ResizeArray<V3d * V3d * V4d * float>)
                       (mesh : string) (localPt : V3d) (lines : V3d[][]) =
            match markerAlphaAt t mesh with
            | None -> ()
            | Some f ->
                let cc = model.CommonCentroid.GetValue t
                let s = datasetScale.GetValue t
                let dim = (if (model.ArmedPick.GetValue t).IsSome then 0.15 else 1.0) * f
                let tw = dispWorldAt t mesh
                let rMax = max 0.01 (model.RevealRadius.GetValue t)
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
                            out.Add(
                                ScanPin.renderCentre cc s (tw.Forward.TransformPos a),
                                ScanPin.renderCentre cc s (tw.Forward.TransformPos b),
                                V4d(1.0, 1.0, 1.0, 0.9 * fade * dim), 1.4)

        let pointReveals =
            pinIdSet |> ASet.collect (fun id ->
                let pinVal = pinsVal |> AVal.map (fun pins -> HashMap.tryFind id pins)
                let rvVal =
                    pinVal |> AVal.map (Option.map (fun p ->
                        p.Pair, p.PointA, p.PointB,
                        (match p.RevealA with RevealReady l -> l | _ -> [||]),
                        (match p.RevealB with RevealReady l -> l | _ -> [||])))
                let segs =
                    AVal.custom (fun t ->
                        match rvVal.GetValue t with
                        | Some (pair, pa, pb, la, lb) when pinShownAt t pair ->
                            let out = ResizeArray<V3d * V3d * V4d * float>()
                            revealSegs t out (fst pair) pa la
                            revealSegs t out (snd pair) pb lb
                            out.ToArray()
                        | _ -> [||])
                ASet.ofList [ linesNode notFullscreen segs ])

        let draftReveal =
            let segs =
                AVal.custom (fun t ->
                    match model.ScanPins.Placement.GetValue t with
                    | PlacementActive d when pinShownAt t d.Pair ->
                        let out = ResizeArray<V3d * V3d * V4d * float>()
                        (match d.PointA, d.RevealA with
                         | Some p, RevealReady l -> revealSegs t out (fst d.Pair) p l
                         | _ -> ())
                        (match d.PointB, d.RevealB with
                         | Some p, RevealReady l -> revealSegs t out (snd d.Pair) p l
                         | _ -> ())
                        out.ToArray()
                    | _ -> [||])
            linesNode notFullscreen segs

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
                    let out = ResizeArray<V3d * V4d>()
                    let mutable gid = 0
                    for b in blocks do
                        let vis = markerAlphaAt t b.Mov
                        let r = b.Err
                        for i in 0 .. r.Samples.Length - 1 do
                            match vis with
                            | Some v when Set.contains gid brush && i < r.Positions.Length ->
                                let c = Primitives.Diff.colorSignedV3 lo hi r.Samples.[i]
                                out.Add(ScanPin.renderCentre cc s r.Positions.[i], V4d(c, v))
                            | _ -> ()
                            gid <- gid + 1
                    out.ToArray())
        let brushedSampleNode =
            sg {
                Sg.Active notFullscreen
                Sg.View view
                Sg.Proj proj
                Sg.DepthTest (AVal.constant DepthTest.None)
                Sg.BlendMode (AVal.constant BlendMode.Blend)
                Sg.NoEvents
                Discs.render view (discRadii |> AVal.map fst) (discRadii |> AVal.map snd) brushedDots
            }

        // The 3D-hovered dot's mark: a duplex ring around it (the diagram
        // cross-highlights the same gid). ONE glyph — the sanctioned
        // camera-dependent rebuild.
        let hoverRingNode =
            let segs =
                AVal.custom (fun t ->
                    match model.HoverSample.GetValue t with
                    | Some hov ->
                        let cc = model.CommonCentroid.GetValue t
                        let s = datasetScale.GetValue t
                        let vb = (view.GetValue t).Backward
                        let eye = vb.TransformPos V3d.Zero
                        let right = vb.TransformDir V3d.IOO
                        let up = vb.TransformDir V3d.OIO
                        let minR, maxR = discRadii.GetValue t
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
                        match found with
                        | Some (c, vis) ->
                            let out = ResizeArray<V3d * V3d * V4d * float>()
                            let rad = 2.2 * clamp minR maxR (float Discs.screenFrac * Vec.length (eye - c))
                            duplex (fun col w -> addRing out c right up rad col w 32) (0.95 * vis) 1.4
                            out.ToArray()
                        | None -> [||]
                    | None -> [||])
            linesNodeTop notFullscreen segs

        // The armed pick's cursor preview: what is ABOUT to be placed, at the
        // hovered surface point — single-stroke pure white (the uncommitted
        // convention). The same model state renders in the Pin tiles, so the
        // preview is synchronized across every view.
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
                            // The radius the landing will commit: the draft's
                            // during placement, else the selected pin's (a
                            // centre re-pick keeps it).
                            let r =
                                match model.ScanPins.Placement.GetValue t with
                                | PlacementActive d -> d.Radius
                                | PlacementIdle ->
                                    match (model.Sel.GetValue t).Pin
                                          |> Option.bind (fun id -> HashMap.tryFind id (pinsVal.GetValue t)) with
                                    | Some p -> p.InnerRadius
                                    | None -> model.QuickPinRadius.GetValue t
                            let rR = ScanPin.renderLength s r
                            for seg in PinGeometry.buildSphereOutline cR rR (V4d(1.0, 1.0, 1.0, 0.7)) 1.4 do
                                out.Add seg
                            addCross out cR (rR * 0.15) white 1.6
                         | ArmPoint _ ->
                            addWireSphere out cR 0.06 white 1.6 20
                            addCross out cR 0.075 white 1.6
                         | ArmProbe ->
                            addCross out cR 0.075 white 1.6)
                        out.ToArray()
                    | _ -> [||])
            linesNodeTop notFullscreen segs

        // Pin flag pole (far view): a neutral pole + top ring along the display
        // axis per committed pin, screen-constant size (ScanPin.flagHeightRender:
        // fixed screen fraction, world-clamped, gear-scaled — hence the view
        // dependency).
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
                        if pinShownAt t p.Pair then
                            let col = neutral
                            let w   = 2.5
                            let aN, u, v = basisFromNormal (ScanPin.axisWith up p)
                            let c   = ScanPin.renderCentre cc scale (pinCentreWorldAt t p)
                            let h   = ScanPin.flagHeightRender scale fs (Vec.length (eye - c))
                            let top = c + aN * h
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

        ASet.unionMany (ASet.ofList [pinDots; ASet.ofList [pinMarkerLines]; pinRings; pointReveals; ASet.ofList [draftAreaNode; draftReveal; crosshairNode; brushedSampleNode; hoverRingNode; armPreviewMarks]; pinFlags; pinLabels])
