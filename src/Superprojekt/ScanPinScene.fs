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
        let selectedId = model.Selection.SelectedPin
        let pinIdSet = model.ScanPins.Pins |> AMap.toASet |> ASet.map fst
        let pinsVal = model.ScanPins.Pins |> AMap.toAVal
        let placementActive =
            model.ScanPins.Placement |> AVal.map (function AnchorPlacement -> true | _ -> false)

        // Displayed (before/after) world transform of a mesh at the given token —
        // anchors are mesh-local, so their world follows this.
        let dispWorldAt (t : AdaptiveToken) (mesh : string) =
            let scale = DatasetScale.forMesh (model.DatasetScales.GetValue t) mesh
            let cc = model.CommonCentroid.GetValue t
            let disp =
                match model.RegView.GetValue t, Map.tryFind mesh (model.SolvedTransforms.GetValue t) with
                | RegAfter, Some s -> s
                | _ -> Map.tryFind mesh (model.LoadTransforms.GetValue t) |> Option.defaultValue Trafo3d.Identity
            RigidTransform.renderToWorld scale cc disp

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
                    Sg.OnTap(fun _ ->
                        match AVal.force placementActive with
                        | true -> true
                        | false ->
                            let sel = AVal.force selectedId
                            if sel = Some id then env.Emit [ScanPinMsg (SelectPin None)]
                            else env.Emit [ScanPinMsg (SelectPin (Some id))]
                            false)
                    Sg.VertexAttributes(
                        HashMap.ofList [ string DefaultSemantic.Positions, BufferView(spherePosBuf, typeof<V3f>) ])
                    Sg.Index(BufferView(sphereIdxBuf, typeof<int>))
                    Sg.Render sphereIdxCnt
                }
            )

        // Visible pin-centre marker: a small wire-box jack on top (so the invisible
        // pick proxy can't occlude it). Yellow when selected, brighter red on hover,
        // else red. Fixed render size — independent of pin radius.
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
                            if isSel then V4d(1.0, 0.85, 0.0, 1.0)
                            elif hovered then V4d(1.0, 0.55, 0.45, 1.0)
                            else V4d(0.95, 0.35, 0.35, 0.9)
                        let w = if isSel || hovered then 2.0 else 1.2
                        let cR = ScanPin.renderCentre cc scale centre
                        addBoxOutline out cR 0.07  0.014 0.014 col w
                        addBoxOutline out cR 0.014 0.07  0.014 col w
                        addBoxOutline out cR 0.014 0.014 0.07  col w
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
                            // Pin-row hover lights the rings up thick + bright
                            // (UI→3D linking via the shared Selection record).
                            let hovered = model.Selection.Hovered.GetValue t = Some (HoverPin id)
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

        // Correspondence constellation (selected pin emphasized): per moving mesh a
        // small sphere glyph at its correspondence point + the reference as a larger
        // haloed glyph, with lines from each to the reference; out-of-ROI omitted.
        // Glyphs render on top (depth bias), pickable for brushing, and follow the
        // displayed pose.
        let refGlyphCol = V4d(0.706, 0.325, 0.035, 1.0)   // amber #b45309

        // Correspondence constellation lines: per pin, a small wire-sphere + cross
        // glyph at each moving-mesh marker and a larger one at the reference point,
        // plus a thin line from each moving glyph to the reference. Fixed render size
        // (independent of pin radius). Selection / hover brighten; out-of-ROI meshes
        // omitted. Rendered on top (depth bias) so the markers read against surfaces.
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
                    let moving = names |> List.filter (fun n -> Some n <> rf && (Map.tryFind n vis |> Option.defaultValue true))
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
                                let gw = if emph then 2.0 else 1.4
                                let refCol = if emph then refGlyphCol else V4d(refGlyphCol.XYZ, 0.4)
                                addWireSphere out raR 0.07 refCol gw 20
                                addCross out raR 0.09 refCol gw
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

        // Pin glyph (far view): a pole + head per committed pin. Head colour =
        // verdict (green if every moving mesh's |median| ≤ LoD₉₅, red if any is
        // significant; grey when no probe yet). Pole height grows with magnitude
        // (max |median offset| across moving meshes).
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

        ASet.unionMany (ASet.ofList [pinDots; ASet.ofList [pinMarkerLines]; pinRings; pinGlyphs; ghostPreview; constellation; ASet.ofList [corrPreview]])
