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
        // camera move; a handful of pins keeps it cheap), NEVER rotated.
        let pinMarkerLines =
            let segs =
                AVal.custom (fun t ->
                    let pins = pinsVal.GetValue t
                    let cc = model.CommonCentroid.GetValue t
                    let scale = datasetScale.GetValue t
                    let eye = (view.GetValue t).Backward.TransformPos V3d.Zero
                    let fs = model.FlagScale.GetValue t
                    let out = ResizeArray<V3d * V3d * V4d * float>()
                    for (_, p) in HashMap.toSeq pins do
                        if pinShownAt t p.Pair then
                            let col = V4d(0.45, 0.48, 0.53, 0.4)
                            let w = 1.0
                            let cR = ScanPin.renderCentre cc scale (pinCentreWorldAt t p)
                            let h = ScanPin.flagHeightRender scale fs (Vec.length (eye - cR))
                            let l, thin = h * 0.10, h * 0.02
                            addBoxOutline out cR l thin thin col w
                            addBoxOutline out cR thin l thin col w
                            addBoxOutline out cR thin thin l col w
                    out.ToArray())
            linesNodeTop flagsActive segs

        // Pin influence visuals: a thin equator ring (⊥ display axis, radius =
        // InnerRadius) + sphere–surface contact rings per pair mesh, in the
        // shared pin ink. Normal depth testing on purpose — occlusion is the
        // spatial cue.
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
                            let cc = model.CommonCentroid.GetValue t
                            let scale = datasetScale.GetValue t
                            let a = 0.65
                            let coreW = 1.4
                            let out = ResizeArray<V3d * V3d * V4d * float>()
                            let centre = (dispWorldAt t anchorMesh).Forward.TransformPos centreLocal
                            let cR = ScanPin.renderCentre cc scale centre
                            let rR = ScanPin.renderLength scale radius
                            let axis = match upNormalA.GetValue t with Some u -> u | None -> V3d.OOI
                            let nN, u, v = basisFromNormal axis
                            duplex (fun c w -> addRing out cR u v rR c w 64) a coreW
                            // 1 m direction indicator along the display axis — thin
                            // + semitransparent (orientation, not geometry).
                            let axisCol = V4d(Primitives.pinInkV3d, 0.35)
                            out.Add(cR, cR + nN * ScanPin.renderLength scale 1.0, axisCol, 1.0)
                            duplex (fun c w ->
                                for KeyValue(_mesh, meshRings) in rings do
                                    for ring in meshRings do
                                        if ring.Length >= 2 then
                                            let rp = ring |> Array.map (ScanPin.renderCentre cc scale)
                                            for i in 0 .. rp.Length - 2 do
                                                out.Add(rp.[i], rp.[i + 1], c, w)) a coreW
                            out.ToArray()
                        | _ -> [||])
                ASet.ofList [ linesNode flagsActive segs ])

        // Committed correspondence markers: per pin one marker on EACH pair
        // mesh — MESH-COLOURED FILL + WHITE OUTLINE (unmistakable pick
        // attribution + contrast on grey). Fixed render size; the fill is an
        // opaque mini icosphere, the outline a white wire sphere around it.
        let pointMarkers =
            pinIdSet |> ASet.collect (fun id ->
                let pinVal = pinsVal |> AVal.map (fun pins -> HashMap.tryFind id pins)
                let ptVal = pinVal |> AVal.map (Option.map (fun p -> p.Pair, p.PointA, p.PointB))
                let markerOn (side : int) =
                    // side 0 = fst Pair, 1 = snd Pair
                    let world =
                        AVal.custom (fun t ->
                            match ptVal.GetValue t with
                            | Some (pair, pa, pb) when pinShownAt t pair ->
                                let mesh = if side = 0 then fst pair else snd pair
                                let local = if side = 0 then pa else pb
                                let cc = model.CommonCentroid.GetValue t
                                let s = datasetScale.GetValue t
                                let w = (dispWorldAt t mesh).Forward.TransformPos local
                                Some (ScanPin.renderCentre cc s w, meshColAt t mesh)
                            | _ -> None)
                    let trafo =
                        world |> AVal.map (function
                            | Some (c, _) -> Trafo3d.Scale 0.05 * Trafo3d.Translation c
                            | None -> Trafo3d.Scale 0.0)
                    let fill =
                        world |> AVal.map (function
                            | Some (_, col) -> V4d(col, 1.0)
                            | None -> V4d.Zero)
                    let outline =
                        world |> AVal.map (function
                            | Some (c, _) ->
                                let out = ResizeArray<V3d * V3d * V4d * float>()
                                addWireSphere out c 0.065 (V4d(1.0, 1.0, 1.0, 0.95)) 1.6 16
                                out.ToArray()
                            | None -> [||])
                    [ sphereShell view proj notFullscreen trafo fill
                      linesNodeTop notFullscreen outline ]
                ASet.ofList (markerOn 0 @ markerOn 1))

        // Brushed diagram samples in 3D: transient WHITE glyphs (ink under-
        // stroke for readability) at the sample world positions, gid-addressed
        // into the canonical CellError concatenation; the 3D-hovered one turns
        // amber — the diagram cross-highlights the same gid. ≤200 (reducer cap).
        let brushedSampleNode =
            let segs =
                AVal.custom (fun t ->
                    let brush = model.BrushedSamples.GetValue t
                    if Set.isEmpty brush then [||]
                    else
                        match model.CellError.GetValue t with
                        | None -> [||]
                        | Some cells ->
                            let cc = model.CommonCentroid.GetValue t
                            let s = datasetScale.GetValue t
                            let hov = model.HoverSample.GetValue t
                            let out = ResizeArray<V3d * V3d * V4d * float>()
                            let mutable gid = 0
                            for (_, r) in cells do
                                for i in 0 .. r.Samples.Length - 1 do
                                    if Set.contains gid brush && i < r.Positions.Length then
                                        let pR = ScanPin.renderCentre cc s r.Positions.[i]
                                        let isHov = hov = Some gid
                                        let col =
                                            if isHov then V4d(0.85, 0.46, 0.02, 1.0)
                                            else V4d(1.0, 1.0, 1.0, 0.95)
                                        let sz = if isHov then 0.055 else 0.04
                                        let ink = V4d(Primitives.pinInkV3d, 0.9)
                                        addWireSphere out pR sz ink 3.0 12
                                        addCross out pR (sz * 1.3) ink 3.0
                                        addWireSphere out pR sz col 1.4 12
                                        addCross out pR (sz * 1.3) col 1.4
                                    gid <- gid + 1
                            out.ToArray())
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

        ASet.unionMany (ASet.ofList [pinDots; ASet.ofList [pinMarkerLines]; pinRings; pointMarkers; ASet.ofList [brushedSampleNode]; pinFlags; pinLabels])
