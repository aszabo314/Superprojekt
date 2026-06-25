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
        // Same, but depth-test off → renders on top of surfaces (constellation
        // §D depth bias; ghost-isolation clears occluders when reading it).
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

        // ── §D correspondence constellation (selected pin emphasized) ────────
        // Per moving mesh: a small filled sphere glyph at its correspondence
        // point (palette colour); the reference point a larger haloed glyph;
        // thin lines from each moving glyph to the reference glyph. Out-of-ROI
        // meshes are omitted. Glyphs render on top (depth bias) and are pickable
        // for §G brushing. Marker positions follow the effective preview pose.
        let pinKey (ScanPinId.ScanPinId g : ScanPinId) = g.ToString()
        let refGlyphCol = V4d(0.706, 0.325, 0.035, 1.0)   // amber #b45309

        // World marker for a (pin, mesh), following the preview delta; None when
        // out-of-ROI / no marker. Reused by glyph trafo + the connecting lines.
        let markerWorldOf (pinVal : aval<ScanPin option>) (mesh : string) =
            AVal.custom (fun t ->
                match pinVal.GetValue t |> Option.bind ScanPin.correspondence with
                | Some c when c.Enabled ->
                    let inRoi = Map.tryFind mesh c.InRoi |> Option.defaultValue true
                    match Map.tryFind mesh c.Anchors with
                    | Some a when inRoi ->
                        match PendingRegistration.delta mesh (model.PendingReg.GetValue t) with
                        | Some d ->
                            let scale = DatasetScale.forMesh (model.DatasetScales.GetValue t) mesh
                            let cc = model.CommonCentroid.GetValue t
                            let committed = Map.tryFind mesh (model.MeshTransforms.GetValue t) |> Option.defaultValue Trafo3d.Identity
                            Some ((RigidTransform.worldDeltaOf scale cc committed d).Forward.TransformPos a.Point)
                        | None -> Some a.Point
                    | _ -> None
                | _ -> None)

        // Stable (pin × moving-mesh) key set — changes only on enabled-pins /
        // mesh-list / reference / visibility, never on hover or preview.
        let corrPairs =
            AVal.custom (fun t ->
                let names = model.MeshNames.Content.GetValue t |> IndexList.toList
                let vis = model.MeshVisible.GetValue t
                let rf = (model.Registration.GetValue t).ReferenceMesh
                let moving = names |> List.filter (fun n -> Some n <> rf && (Map.tryFind n vis |> Option.defaultValue true))
                let enabled =
                    pinsVal.GetValue t |> HashMap.toSeq
                    |> Seq.choose (fun (id, p) -> match ScanPin.correspondence p with Some c when c.Enabled -> Some id | _ -> None)
                    |> List.ofSeq
                seq { for id in enabled do for m in moving -> (id, m) } |> HashSet.ofSeq)

        let glyphColour (pinVal : aval<ScanPin option>) (pinId : ScanPinId) (mesh : string) =
            AVal.custom (fun t ->
                let baseCol =
                    match pinVal.GetValue t |> Option.bind (fun p -> Map.tryFind mesh p.DatasetColors) with
                    | Some c -> Primitives.c4bToV3d c
                    | None -> V3d(0.102, 0.337, 0.859)
                let sel = selectedId.GetValue t = Some pinId
                let rowHover = model.CorrRowHover.GetValue t = Some (pinKey pinId, mesh)
                let pinHover = model.WorkflowPinHover.GetValue t = Some pinId
                if rowHover then V4d(baseCol * 0.4 + V3d.III * 0.6, 1.0)
                elif sel || pinHover then V4d(baseCol, 1.0)
                else V4d(baseCol, 0.4))

        let glyphSphere (pinId : ScanPinId) (mesh : string) =
            let pinVal = pinsVal |> AVal.map (HashMap.tryFind pinId)
            let world = markerWorldOf pinVal mesh
            let active = world |> AVal.map Option.isSome
            let trafo =
                (world, model.CommonCentroid, datasetScale, pinVal)
                |> fun (a, b, c, d) -> AVal.custom (fun t ->
                    match a.GetValue t with
                    | Some w ->
                        let cc = b.GetValue t
                        let s = c.GetValue t
                        let ir = d.GetValue t |> Option.map (fun p -> p.InnerRadius) |> Option.defaultValue 1.0
                        let r = max 0.08 (ScanPin.renderLength s (ir * 0.3))
                        let sel = selectedId.GetValue t = Some pinId
                        Trafo3d.Scale (if sel then r else r * 0.75) * Trafo3d.Translation (ScanPin.renderCentre cc s w)
                    | None -> Trafo3d.Scale 0.0)
            sg {
                Sg.Active (AVal.map2 (&&) notFullscreen active)
                Sg.View view
                Sg.Proj proj
                Sg.Trafo trafo
                Sg.Shader { DefaultSurfaces.trafo; Shader.flatColor }
                Sg.Uniform("FlatColor", glyphColour pinVal pinId mesh |> AVal.map V4f)
                Sg.DepthTest (AVal.constant DepthTest.None)
                Sg.BlendMode (AVal.constant BlendMode.Blend)
                Sg.OnPointerMove(fun _ -> env.Emit [SetCorrRowHover (Some (pinKey pinId, mesh))]; true)
                Sg.OnTap(fun _ -> env.Emit [SetFocusMesh (Some mesh)]; false)
                Sg.VertexAttributes(
                    HashMap.ofList [ string DefaultSemantic.Positions, BufferView(spherePosBuf, typeof<V3f>) ])
                Sg.Index(BufferView(sphereIdxBuf, typeof<int>))
                Sg.Render sphereIdxCnt
            }
        let movingGlyphs = corrPairs |> ASet.ofAVal |> ASet.map (fun (id, m) -> glyphSphere id m)

        // Reference glyph per enabled pin: a larger amber sphere at the reference
        // point (haloed by the ring in the lines below).
        let refGlyph (pinId : ScanPinId) =
            let pinVal = pinsVal |> AVal.map (HashMap.tryFind pinId)
            let raVal = pinVal |> AVal.map (Option.bind ScanPin.correspondence >> Option.bind (fun c -> if c.Enabled then c.RefAnchor else None))
            let active = raVal |> AVal.map Option.isSome
            let trafo =
                AVal.custom (fun t ->
                    match raVal.GetValue t with
                    | Some ra ->
                        let cc = model.CommonCentroid.GetValue t
                        let s = datasetScale.GetValue t
                        let ir = pinVal.GetValue t |> Option.map (fun p -> p.InnerRadius) |> Option.defaultValue 1.0
                        let r = max 0.12 (ScanPin.renderLength s (ir * 0.45))
                        Trafo3d.Scale r * Trafo3d.Translation (ScanPin.renderCentre cc s ra)
                    | None -> Trafo3d.Scale 0.0)
            let col =
                selectedId |> AVal.map (fun sel -> if sel = Some pinId then refGlyphCol else V4d(refGlyphCol.XYZ, 0.4))
            sg {
                Sg.Active (AVal.map2 (&&) notFullscreen active)
                Sg.View view
                Sg.Proj proj
                Sg.Trafo trafo
                Sg.Shader { DefaultSurfaces.trafo; Shader.flatColor }
                Sg.Uniform("FlatColor", col |> AVal.map V4f)
                Sg.DepthTest (AVal.constant DepthTest.None)
                Sg.BlendMode (AVal.constant BlendMode.Blend)
                Sg.NoEvents
                Sg.VertexAttributes(
                    HashMap.ofList [ string DefaultSemantic.Positions, BufferView(spherePosBuf, typeof<V3f>) ])
                Sg.Index(BufferView(sphereIdxBuf, typeof<int>))
                Sg.Render sphereIdxCnt
            }
        let enabledPinIds =
            pinsVal |> AVal.map (fun pins ->
                pins |> HashMap.toSeq
                |> Seq.choose (fun (id, p) -> match ScanPin.correspondence p with Some c when c.Enabled -> Some id | _ -> None)
                |> HashSet.ofSeq)
        let refGlyphs = enabledPinIds |> ASet.ofAVal |> ASet.map refGlyph

        // Connecting lines (moving glyph → reference glyph) + reference halo ring.
        let constLines =
            let segs =
                AVal.custom (fun t ->
                    let pins = pinsVal.GetValue t
                    let cc = model.CommonCentroid.GetValue t
                    let scale = datasetScale.GetValue t
                    let sel = selectedId.GetValue t
                    let names = model.MeshNames.Content.GetValue t |> IndexList.toList
                    let vis = model.MeshVisible.GetValue t
                    let rf = (model.Registration.GetValue t).ReferenceMesh
                    let moving = names |> List.filter (fun n -> Some n <> rf && (Map.tryFind n vis |> Option.defaultValue true))
                    let out = ResizeArray<V3d * V3d * V4d * float>()
                    for (id, p) in HashMap.toSeq pins do
                        match ScanPin.correspondence p with
                        | Some c when c.Enabled ->
                            match c.RefAnchor with
                            | Some ra ->
                                let isSel = sel = Some id
                                let raR = ScanPin.renderCentre cc scale ra
                                let baseAlpha = if isSel then 0.9 else 0.3
                                let width = if isSel then 1.5 else 1.0
                                let _, u, v = basisFromNormal (ScanPin.axis p)
                                let hr = max 0.08 (ScanPin.renderLength scale (p.InnerRadius * 0.5))
                                addRing out raR u v hr (V4d(refGlyphCol.XYZ, baseAlpha)) width 24
                                let pinVal = pins |> HashMap.tryFind id |> AVal.constant
                                for mesh in moving do
                                    match (markerWorldOf pinVal mesh).GetValue t with
                                    | Some w ->
                                        let baseCol =
                                            match Map.tryFind mesh p.DatasetColors with
                                            | Some cc4 -> Primitives.c4bToV3d cc4
                                            | None -> V3d(0.102, 0.337, 0.859)
                                        out.Add(ScanPin.renderCentre cc scale w, raR, V4d(baseCol, baseAlpha), width)
                                    | None -> ()
                            | None -> ()
                        | _ -> ()
                    out.ToArray())
            ASet.ofList [ linesNodeTop notFullscreen segs ]

        let constellation = ASet.unionMany (ASet.ofList [ constLines; movingGlyphs; refGlyphs ])

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

        ASet.unionMany (ASet.ofList [pinDots; pinRings; pinGlyphs; movementLayer; ghostPreview; constellation])
