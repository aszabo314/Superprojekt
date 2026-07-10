namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open Aardvark.Application
open Aardworx.WebAssembly
open FSharp.Data.Adaptive
open Aardvark.Dom

// WebGL focus panel. Each mesh is rendered full-res and textured in render space at
// its displayed pose (same frame as the main view). Top = strictly orthographic;
// 360° = a standard perspective camera fixed at the mesh's panorama centre (drag =
// look around, wheel = fov zoom). A tiny controller (no orbit) drives the single;
// the tiles are static thumbnails. Correspondence picking is Dom-driven (cursor →
// render ray → server raycast → 3D preview ghost on move, place on click); the
// `Sg.OnTap` GPU pick did not fire reliably in this 2nd render control.
module FocusScene =

    // Camera state of the single, per (mesh, projection): Top reads (pan, zoom) in
    // fit-relative units; the 360° view reuses the same pair as (azimuth/π,
    // elevation/(π/2)) + magnification, under its own key — the two states are not
    // interconvertible, so they never share. Kept PER MESH so switching
    // small-multiples restores each view's own state. Survives rebuilds.
    let private camStates = System.Collections.Generic.Dictionary<string, cval<V2d> * cval<float>>()
    let private camFor (key : string) =
        match camStates.TryGetValue key with
        | true, v -> v
        | _ ->
            let v = (cval V2d.Zero, cval 1.0)
            camStates.[key] <- v
            v
    let private panoKey (name : string) = name + "†pano"
    // Device-pixel-ratio (framebuffer ÷ CSS px). Set by the main view (where
    // RenderControl.ClientSize works); used to turn the focus cursor (CSS px) into
    // framebuffer-relative NDC. Binding ClientSize directly in this secondary
    // control left the single blank, so the dpr is shared instead.
    let dpr = cval 1.0
    // Drag/hover state shared across focus controls — safe only because one large
    // `single` exists at a time (the tiles are non-interactive). Would need per-control
    // state if two singles ever coexisted.
    let mutable private dragging = false
    let mutable private lastPx = V2i.Zero
    // Hover-preview throttle + generation guard (drops out-of-order raycast results).
    let private nowMs () = float System.DateTime.UtcNow.Ticks / 10000.0
    let mutable private hoverGen = 0
    let mutable private lastHoverMs = 0.0

    // GL orthographic projection (RH, -Z forward; near→-1, far→+1).
    let private orthoProj (hw : float) (hh : float) (near : float) (far : float) =
        let m =
            M44d(1.0 / hw, 0.0,      0.0,                 0.0,
                 0.0,      1.0 / hh, 0.0,                 0.0,
                 0.0,      0.0,      -2.0 / (far - near), -(far + near) / (far - near),
                 0.0,      0.0,      0.0,                 1.0)
        Trafo3d(m, m.Inverse)

    // local mesh → render space (the main view's meshTrafo): centroid-relative →
    // common-relative → dataset scale → displayed (before/after) pose.
    let private renderTrafoOf (model : AdaptiveModel) (name : string) (loaded : LoadedMesh) =
        let scale = model.DatasetScales |> AVal.map (fun m -> DatasetScale.forMesh m name)
        let baseT =
            (loaded.centroid, model.CommonCentroid, scale)
            |||> AVal.map3 (fun c common s -> Trafo3d.Translation(c - common) * Trafo3d.Scale s)
        (baseT, MeshView.displayedMeshT model name) ||> AVal.map2 (fun b t -> b * t), scale

    // Framing for both the single and the tiles: the panorama centre and the
    // half-extent that frames the mesh around it.
    //  • centreWorld  = stored PanoCenters[mesh] (absolute world) else the centroid
    //                   (= the mesh origin) — request: no entry ⇒ origin, as before.
    //  • centreRender = that carried through renderT (− centroid → mesh frame → render):
    //                   the 360° camera position AND the Top-view camera centre.
    //  • extent       = farthest mesh-bbox corner from the centre (render units), so a
    //                   centre off the geometry still frames it; ×0.98 zooms in a touch.
    let private framing (model : AdaptiveModel) (name : string) (loaded : LoadedMesh)
                        (renderT : aval<Trafo3d>) (scale : aval<float>) =
        let centreWorld =
            (model.PanoCenters, loaded.centroid) ||> AVal.map2 (fun centers c ->
                match Map.tryFind name centers with Some w -> w | None -> c)
        let centreRender =
            (renderT, centreWorld, loaded.centroid) |||> AVal.map3 (fun rt w c ->
                rt.Forward.TransformPos (w - c))
        let extent =
            AVal.custom (fun t ->
                let s = scale.GetValue t
                let w = centreWorld.GetValue t
                let r =
                    match Map.tryFind name (model.MeshBounds.GetValue t) with
                    | Some (b : Box3d) when not b.IsInvalid ->
                        let dx = max (abs (b.Min.X - w.X)) (abs (b.Max.X - w.X))
                        let dy = max (abs (b.Min.Y - w.Y)) (abs (b.Max.Y - w.Y))
                        let dz = max (abs (b.Min.Z - w.Z)) (abs (b.Max.Z - w.Z))
                        sqrt (dx * dx + dy * dy + dz * dz)
                    | _ -> loaded.localMaxR.GetValue t
                max 1e-4 (r * s * 0.98))
        centreWorld, centreRender, extent

    // Selection-derived base framing (§A2) of a mesh's Top canvas: the focus camera
    // FOLLOWS the selection. pin → the pin region (its centre, ROI ×4); cell → that
    // mesh's correspondence marker at the committed pose (hard-zoom, same radius
    // convention as the 3D FlyToPoint), falling back to the pin centre while no
    // marker exists; mesh / nothing → the whole-mesh fit. Returned as the
    // (pan, zoom) BASE the per-selection user offsets compose onto.
    let private selBaseFrame (model : AdaptiveModel) (name : string)
                             (scale : aval<float>) (panoEye : aval<V3d>) (fitExtent : aval<float>) =
        let pinsAval = model.ScanPins.Pins |> AMap.toAVal
        AVal.custom (fun t ->
            let sel = model.Selection.Active.GetValue t
            let target =
                match sel with
                | SelMesh _ | SelNone -> None
                | SelPin p | SelCell (p, _) ->
                    HashMap.tryFind p (pinsAval.GetValue t)
                    |> Option.map (fun pin ->
                        let centre =
                            match sel with
                            | SelCell _ ->
                                let anchorOwn =
                                    ScanPin.correspondence pin |> Option.bind (fun c ->
                                        if (model.Registration.GetValue t).ReferenceMesh = Some name then c.RefAnchor
                                        else Map.tryFind name c.Anchors |> Option.map (fun a -> a.Point))
                                match anchorOwn with
                                | Some own ->
                                    let disp =
                                        match model.RegView.GetValue t, Map.tryFind name (model.SolvedTransforms.GetValue t) with
                                        | RegAfter, Some s -> s
                                        | _ -> Map.tryFind name (model.LoadTransforms.GetValue t) |> Option.defaultValue Trafo3d.Identity
                                    let dw = RigidTransform.renderToWorld (scale.GetValue t) (model.CommonCentroid.GetValue t) disp
                                    dw.Forward.TransformPos own
                                | None -> pin.Centre
                            | _ -> pin.Centre
                        centre, max 0.5 (pin.InnerRadius * 4.0))
            match target with
            | None -> V2d.Zero, 1.0
            | Some (w, he) ->
                let cc = model.CommonCentroid.GetValue t
                let s = scale.GetValue t
                let rp = ScanPin.renderCentre cc s w
                let fc = panoEye.GetValue t
                let ext = max 1e-4 (fitExtent.GetValue t)
                let tgt = max 1e-4 (ScanPin.renderLength s he)
                V2d((rp.X - fc.X) / ext, (rp.Y - fc.Y) / ext), clamp 1.0 200.0 (ext / tgt))

    // Brushed sample dots on THIS mesh (§A4) — always-on-top; empty when no brush.
    let private brushedDotsNode (model : AdaptiveModel) (name : string) =
        sg {
            Sg.DepthTest (AVal.constant DepthTest.None)
            Sg.BlendMode (AVal.constant BlendMode.Blend)
            Sg.NoEvents
            Dots.render (ScanPinScene.brushedDotGeometry model (Some name))
        }

    // 360° view state, reusing the per-mesh (pan, zoom) pair: pan.X = azimuth/π,
    // pan.Y = elevation/(π/2) (clamped short of the poles so the Z-up lookAt never
    // degenerates), zoom = magnification. Fov is HORIZONTAL degrees (the
    // Frustum.perspective convention, same as the main view's 90°).
    let private panoFov (zoom : float) = clamp 4.0 120.0 (90.0 / max 1e-3 zoom)
    // NDC → view-direction scales: x = tan(fovH/2), y = x / aspect.
    let private panoHalfTans (zoom : float) (aspect : float) =
        let tx = tan (panoFov zoom * Constant.RadiansPerDegree * 0.5)
        tx, tx / aspect
    let private panoAngles (pan : V2d) =
        pan.X * Constant.Pi,
        clamp (-0.495 * Constant.Pi) (0.495 * Constant.Pi) (pan.Y * Constant.PiHalf)
    let private panoForward (pan : V2d) =
        let az, el = panoAngles pan
        V3d(cos el * cos az, cos el * sin az, sin el)
    // Rotate the 360° view by (Δazimuth, Δelevation) radians; azimuth wraps, elevation
    // clamps. Call inside a transact.
    let private panoRotate (pan : cval<V2d>) (dAz : float) (dEl : float) =
        let v = pan.Value + V2d(dAz / Constant.Pi, dEl / Constant.PiHalf)
        pan.Value <- V2d(((v.X + 1.0) % 2.0 + 2.0) % 2.0 - 1.0, clamp (-1.0) 1.0 v.Y)

    // Perspective camera fixed at the panorama eye — only the view direction (drag)
    // and fov (wheel) change; near/far scale with the framing extent.
    let private panoCam
            (size : aval<V2i>) (eye : aval<V3d>) (fitExtent : aval<float>)
            (pan : aval<V2d>) (zoomA : aval<float>) =
        let view =
            (eye, pan) ||> AVal.map2 (fun e p ->
                CameraView.lookAt e (e + panoForward p) V3d.OOI |> CameraView.viewTrafo)
        let proj =
            AVal.custom (fun t ->
                let s = size.GetValue t
                let ext = max 1e-4 (fitExtent.GetValue t)
                let aspect = float s.X / float (max 1 s.Y)
                Frustum.perspective (panoFov (zoomA.GetValue t)) (ext * 1e-3) (ext * 4.0) aspect
                |> Frustum.projTrafo)
        view, proj

    // Top-down ortho view + projection framing the displayed render centroid,
    // radius = localMaxR·scale, offset by (pan, zoom).
    let private orthoCam
            (size : aval<V2i>) (fitCenter : aval<V3d>) (fitExtent : aval<float>)
            (pan : aval<V2d>) (zoomA : aval<float>) =
        let view =
            AVal.custom (fun t ->
                let fc = fitCenter.GetValue t
                let ext = fitExtent.GetValue t
                let p = pan.GetValue t
                CameraView.lookAt (V3d(fc.X + p.X * ext, fc.Y + p.Y * ext, fc.Z + (ext + 1.0) * 5.0))
                                  (V3d(fc.X + p.X * ext, fc.Y + p.Y * ext, fc.Z)) (V3d(0.0, 1.0, 0.0))
                |> CameraView.viewTrafo)
        let proj =
            AVal.custom (fun t ->
                let s = size.GetValue t
                let ext = fitExtent.GetValue t
                let he = ext / max 1e-3 (zoomA.GetValue t)
                let aspect = float s.X / float (max 1 s.Y)
                orthoProj (he * aspect) he 0.01 ((ext + 1.0) * 12.0))
        view, proj

    let private vattrs (loaded : LoadedMesh) (scalarBuf : aval<IBuffer>) =
        HashMap.ofList [
            string DefaultSemantic.Positions,               BufferView(loaded.pos, typeof<V3f>)
            string DefaultSemantic.DiffuseColorCoordinates, BufferView(loaded.tc,  typeof<V2f>)
            "FocusScalar",                                  BufferView(scalarBuf, typeof<float32>)
        ]

    // Top-view overlay primitives (render space; XY plane, since Top looks down −Z).
    let private addRingXY (out : ResizeArray<V3d * V3d * V4d * float>)
                          (c : V3d) (r : float) (col : V4d) (w : float) (segs : int) =
        for i in 0 .. segs - 1 do
            let a0 = float i       / float segs * Constant.PiTimesTwo
            let a1 = float (i + 1) / float segs * Constant.PiTimesTwo
            out.Add(c + V3d(cos a0, sin a0, 0.0) * r, c + V3d(cos a1, sin a1, 0.0) * r, col, w)
    let private addCrossXY (out : ResizeArray<V3d * V3d * V4d * float>)
                           (c : V3d) (r : float) (col : V4d) (w : float) =
        out.Add(c - V3d.IOO * r, c + V3d.IOO * r, col, w)
        out.Add(c - V3d.OIO * r, c + V3d.OIO * r, col, w)

    // load/solved forward maps (mesh-local → render) at token t, sharing the base
    // render trafo (centroid-relative → common-relative → dataset scale → pose).
    let private loadSolvedForwards (model : AdaptiveModel) (name : string) (loaded : LoadedMesh) (sc : float) (t : AdaptiveToken) =
        let baseT = Trafo3d.Translation(loaded.centroid.GetValue t - model.CommonCentroid.GetValue t) * Trafo3d.Scale sc
        let fwd (m : Map<string, Trafo3d>) = (baseT * (Map.tryFind name m |> Option.defaultValue Trafo3d.Identity)).Forward
        fwd (model.LoadTransforms.GetValue t), fwd (model.SolvedTransforms.GetValue t)

    // Inspect colour overlay for a mesh: (FocusMode, per-vertex scalar buffer, hi).
    // 0 = texture; 1 = difference (FocusDist, diverging); 2 = displacement (per-vertex
    // |load→solved| computed here, sequential). Texture/no-data → a zero buffer of the
    // right length (the shader ignores it).
    let private focusOverlay (model : AdaptiveModel) (name : string) (loaded : LoadedMesh) (scale : aval<float>) =
        // Inspect comparison overlay (1 = difference, 2 = displacement) takes
        // precedence; otherwise the per-mesh intrinsic heatmap (4/5/6) mirrors the 3D
        // view. HeatOff / no comparison ⇒ 0 (texture).
        let modeA =
            AVal.custom (fun t ->
                let inspectMode =
                    if model.WorkflowStep.GetValue t <> Inspect then 0
                    // Brushing = sole focus (§A4): the error maps stand down while
                    // samples are brushed — only the brushed dots carry value.
                    elif not (Set.isEmpty (model.BrushedSamples.GetValue t)) then 0
                    else
                        let rf = (model.Registration.GetValue t).ReferenceMesh
                        if Some name = rf then 0
                        else
                            match model.InspectChannel.GetValue t with
                            | ChDifference ->
                                // Pose-baked pair — the reg peek selects the Other
                                // cache so the paint flips with the geometry.
                                let fd =
                                    if model.RegPeekHeld.GetValue t then model.FocusDistOther
                                    else model.FocusDist
                                if Map.containsKey name (fd.GetValue t) then 1 else 0
                            | ChDisplacement -> if Map.containsKey name (model.SolvedTransforms.GetValue t) then 2 else 0
                if inspectMode <> 0 then inspectMode
                else
                    match Map.tryFind name (model.MeshHeatmap.GetValue t) |> Option.defaultValue HeatOff with
                    | HeatOff -> 0 | HeatIncidence -> 4 | HeatRange -> 5 | HeatShape -> 6)
        let scalarData =
            AVal.custom (fun t ->
                let zero () =
                    loaded.pos.GetValue t |> ignore
                    let n = match loaded.mesh.Value with Some md -> md.positions.Length | None -> 3
                    ArrayBuffer (Array.zeroCreate<float32> n) :> IBuffer
                match modeA.GetValue t with
                | 1 ->
                    let fd =
                        if model.RegPeekHeld.GetValue t then model.FocusDistOther
                        else model.FocusDist
                    match Map.tryFind name (fd.GetValue t) with
                    | Some arr -> ArrayBuffer arr :> IBuffer
                    | None -> zero ()
                | 2 ->
                    loaded.pos.GetValue t |> ignore
                    match loaded.mesh.Value with
                    | Some md ->
                        let pos = md.positions
                        let sc = scale.GetValue t
                        let loadF, solvedF = loadSolvedForwards model name loaded sc t
                        let mag = Array.init pos.Length (fun i ->
                            let p = V3d pos.[i]
                            float32 ((solvedF.TransformPos p - loadF.TransformPos p).Length / sc))
                        ArrayBuffer mag :> IBuffer
                    | None -> zero ()
                // Intrinsic per-mesh heatmaps: per-vertex scalar pre-normalized to [0,1]
                // in the mesh's own (pose-independent) frame. Sensor = the pano centre in
                // mesh-local coords (no entry ⇒ the mesh origin), matching MeshView.
                | 4 | 5 | 6 as m ->
                    loaded.pos.GetValue t |> ignore
                    match loaded.mesh.Value with
                    | Some md ->
                        let pos = md.positions
                        let sensor =
                            match Map.tryFind name (model.PanoCenters.GetValue t) with
                            | Some w -> V3f (w - loaded.centroid.GetValue t)
                            | None -> V3f.Zero
                        let arr =
                            match m with
                            | 4 ->
                                let nrm = md.normals
                                Array.init pos.Length (fun i ->
                                    let toS = (sensor - pos.[i]).Normalized
                                    abs (Vec.dot (nrm.[i].Normalized) toS))
                            | 5 ->
                                let mutable mx = 1e-6f
                                for p in pos do
                                    let d = (p - sensor).Length
                                    if d > mx then mx <- d
                                pos |> Array.map (fun p -> (p - sensor).Length / mx)
                            | _ -> MeshView.shapeQuality pos md.indices
                        ArrayBuffer arr :> IBuffer
                    | None -> zero ()
                | _ -> zero ())
        // Map ends from the unified pin-derived range (§C) — same scale as the 3D
        // painters, so every tile and the single are directly comparable.
        let rangeA = MeshView.inspectRange model
        let dispA = MeshView.displacementRange model
        let hiA =
            (modeA, rangeA, dispA) |||> AVal.map3 (fun m (_, hi) disp ->
                match m with
                | 1 -> float32 hi
                | 2 -> float32 disp
                | _ -> 1.0f)
        let loNegA =
            (modeA, rangeA) ||> AVal.map2 (fun m (lo, _) ->
                if m = 1 then float32 (abs lo) else 1.0f)
        let isoA =
            (modeA, rangeA) ||> AVal.map2 (fun m (lo, hi) ->
                if m = 1 then float32 (Primitives.Diff.isoStep lo hi) else 0.0f)
        modeA, scalarData, hiA, loNegA, isoA

    // Large single: render-space, textured. Top = orthographic pan/zoom; 360° = a
    // perspective camera fixed at the panorama eye (drag = look around, wheel = fov
    // zoom). Picking is Dom-driven (the Sg pick didn't fire reliably in this 2nd
    // control): the cursor is inverted to a render-space ray, raycast on the server,
    // and the hit drives a live 3D preview ghost on move + the placement on click.
    let private focusSingle (env : Env<Message>) (model : AdaptiveModel) (name : string) (proj : FocusProjection) : DomNode =
        let loaded = MeshView.loadMeshAsync (fun () -> ()) name
        let renderT, scale = renderTrafoOf model name loaded
        let isPano = (proj = ProjPano)
        // panoEye = the 360° camera position AND the Top-view camera centre (the
        // mesh's panorama centre in render space); fitExtent frames the mesh around it.
        let _, panoEye, fitExtent = framing model name loaded renderT scale
        // User pan/zoom OFFSETS, keyed per (mesh, projection, selection target): a
        // fresh selection starts at its derived base framing (offset zero), and
        // re-selecting the same target restores the user's adjustments. The 360°
        // view keeps its single per-mesh key (no selection framing there).
        let camPair =
            if isPano then AVal.constant (camFor (panoKey name))
            else
                model.Selection.Active |> AVal.map (fun s ->
                    let k =
                        match s with
                        | SelPin p -> let (ScanPinId.ScanPinId g) = p in "|p" + g.ToString "N"
                        | SelCell (p, _) -> let (ScanPinId.ScanPinId g) = p in "|c" + g.ToString "N"
                        | SelMesh _ | SelNone -> ""
                    camFor (name + k))
        let panUser  = camPair |> AVal.bind (fun (p, _) -> p :> aval<V2d>)
        let zoomUser = camPair |> AVal.bind (fun (_, z) -> z :> aval<float>)
        // Effective camera = selection base ⊕ user offset (the pano base is inert).
        let baseFrame =
            if isPano then AVal.constant (V2d.Zero, 1.0)
            else selBaseFrame model name scale panoEye fitExtent
        let panEff  = (baseFrame, panUser)  ||> AVal.map2 (fun (pb, _) p -> pb + p)
        let zoomEff = (baseFrame, zoomUser) ||> AVal.map2 (fun (_, zb) z -> zb * z)
        let curPan ()  = fst (AVal.force camPair)
        let curZoom () = snd (AVal.force camPair)
        let modeA, scalarBuf, hiA, loNegA, isoA = focusOverlay model name loaded scale
        // Displacement single: white surface (mode 2 → 3) so the arrow glyphs read.
        let surfaceMode = modeA |> AVal.map (fun m -> if m = 2 then 3 else m)
        // Load→solved arrow glyphs (render space, exaggerated for visibility; colour =
        // true magnitude). Empty unless this solved mesh is in the displacement channel.
        let arrowSegs =
            AVal.custom (fun t ->
                let disp =
                    model.WorkflowStep.GetValue t = Inspect
                    && model.InspectChannel.GetValue t = ChDisplacement
                    && Set.isEmpty (model.BrushedSamples.GetValue t)
                match loaded.mesh.Value with
                | Some md when disp && Map.containsKey name (model.SolvedTransforms.GetValue t) ->
                    let pos = md.positions
                    let sc = scale.GetValue t
                    let loadF, solvedF = loadSolvedForwards model name loaded sc t
                    let n = pos.Length
                    let stride = max 1 (n / 250)
                    let mutable maxMag = 1e-9
                    let mutable i = 0
                    while i < n do
                        let p = V3d pos.[i]
                        let m = (solvedF.TransformPos p - loadF.TransformPos p).Length
                        if m > maxMag then maxMag <- m
                        i <- i + stride
                    let exag = 0.18 * fitExtent.GetValue t / maxMag
                    let hi = float (hiA.GetValue t)
                    let out = ResizeArray<V3d * V3d * V4d * float>()
                    i <- 0
                    while i < n do
                        let p = V3d pos.[i]
                        let b = loadF.TransformPos p
                        let s = solvedF.TransformPos p
                        let d = (s - b) * exag
                        let tip = b + d
                        // colour by true magnitude (world metres): light → dark blue.
                        let tc = min 1.0 ((s - b).Length / sc / max 1e-6 hi)
                        let col = V4d(0.576 + (0.118 - 0.576) * tc, 0.773 + (0.227 - 0.773) * tc, 0.992 + (0.541 - 0.992) * tc, 0.95)
                        out.Add(b, tip, col, 1.6)
                        let dl = sqrt (d.X * d.X + d.Y * d.Y)
                        if dl > 1e-9 then
                            let nx = d.X / dl
                            let ny = d.Y / dl
                            let hl = dl * 0.28
                            let ca = -0.866
                            let sa = 0.5
                            out.Add(tip, V3d(tip.X + hl * (nx * ca - ny * sa), tip.Y + hl * (nx * sa + ny * ca), tip.Z), col, 1.6)
                            out.Add(tip, V3d(tip.X + hl * (nx * ca + ny * sa), tip.Y + hl * (-nx * sa + ny * ca), tip.Z), col, 1.6)
                        i <- i + stride
                    out.ToArray()
                | _ -> [||])
        // Correspondence-mode overlay (Top only): each pin's bounding-sphere circle
        // (true InnerRadius footprint) + a screen-fixed always-on-top glyph at THIS
        // mesh's anchor for each pin. The ring layout (flat XY circles) and the
        // screen-fixed glyph sizing are Top-projection maths, so it stays Top-only.
        let pinsAval   = model.ScanPins.Pins |> AMap.toAVal
        let dispRenderT = MeshView.displayedMeshT model name
        let meshCol =
            model.MeshOrder |> AMap.tryFind name
            |> AVal.map (Option.defaultValue 0 >> Primitives.meshColor >> Primitives.c4bToV3d)
        let overlaySegs =
            AVal.custom (fun t ->
                if isPano || model.WorkflowStep.GetValue t <> Correspondence then [||]
                else
                    let pins = pinsAval.GetValue t
                    let cc = model.CommonCentroid.GetValue t
                    let s = scale.GetValue t
                    let sel = Selection.pin (model.Selection.Active.GetValue t)
                    let ext = fitExtent.GetValue t
                    let z = zoomEff.GetValue t
                    let gr = 0.05 * ext / max 1e-3 z   // screen-fixed glyph half-size
                    let baseCol = meshCol.GetValue t
                    let isRef = (model.Registration.GetValue t).ReferenceMesh = Some name
                    let dw = RigidTransform.renderToWorld s cc (dispRenderT.GetValue t)
                    let out = ResizeArray<V3d * V3d * V4d * float>()
                    for (id, p) in HashMap.toSeq pins do
                        let isSel = sel = Some id
                        let cR = ScanPin.renderCentre cc s p.Centre
                        let rR = ScanPin.renderLength s p.InnerRadius
                        // ROI circle in the pin's own colour (selection = weight/alpha).
                        let pinCol = Primitives.c4bToV3d p.PinColor
                        let ringCol = V4d(pinCol, if isSel then 0.95 else 0.6)
                        addRingXY out cR rR ringCol (if isSel then 2.2 else 1.3) 48
                        match ScanPin.correspondence p with
                        | Some c ->
                            // The reference's marker is its RefAnchor (own-frame like
                            // Anchors), drawn with the same glyph as any other mesh.
                            let anchorOwn =
                                if isRef then c.RefAnchor
                                else Map.tryFind name c.Anchors |> Option.map (fun a -> a.Point)
                            match anchorOwn with
                            | Some own ->
                                let aR = ScanPin.renderCentre cc s (dw.Forward.TransformPos own)
                                let gcol = V4d(baseCol, if isSel then 1.0 else 0.95)
                                let gw = if isSel then 2.5 else 1.8
                                addCrossXY out aR gr gcol gw
                                addRingXY out aR (gr * 0.6) gcol gw 24
                            | None -> ()
                        | None -> ()
                    // Live aim ghost (preview in both views, §T4): a cyan cross+ring at
                    // the hovered pick point while armed for THIS mesh.
                    match model.CorrArm.GetValue t with
                    | Some (_, m) when m = name ->
                        match model.CorrPreview.GetValue t with
                        | Some w ->
                            let pr = ScanPin.renderCentre cc s w
                            let col = V4d(0.0, 0.78, 0.84, 1.0)
                            addCrossXY out pr gr col 2.2
                            addRingXY out pr (gr * 0.6) col 2.2 24
                        | None -> ()
                    | _ -> ()
                    out.ToArray())
        // passOne + DepthTest.None: the surface writes depth in the default pass, so a
        // same-pass overlay was occluded (it rendered under the mesh); a later pass with
        // no depth test draws it always-on-top (same trick as the main-view cross).
        let overlay =
            sg {
                Sg.Pass RenderPass.passOne
                Sg.DepthTest (AVal.constant DepthTest.None)
                Sg.BlendMode (AVal.constant BlendMode.Blend)
                Sg.NoEvents
                Lines.render overlaySegs
            }
        renderControl {
            RenderControl.Samples 1
            Class "focus-rc"
            let! info = RenderControl.Info
            let! size = RenderControl.ViewportSize
            // Cursor coords are CSS px (DOM); ViewportSize is framebuffer px (CSS ×
            // devicePixelRatio). Divide out the shared dpr to get CSS-px size for the
            // cursor → NDC math (don't bind RenderControl.ClientSize here — it blanks
            // this secondary control).
            let overlaySize =
                (size, dpr :> aval<_>) ||> AVal.map2 (fun v d ->
                    let k = max 1e-3 d
                    V2i(max 1 (int (round (float v.X / k))), max 1 (int (round (float v.Y / k)))))
            // Cursor (px,py, CSS px) → metric-world surface point via a server raycast.
            // Two coordinate spaces only: render (where the cursor ray is built) and
            // metric world (cc/scale, the app's single world convention). The mesh's
            // displayed before/after pose lives in metric world as `dispWorld`
            // (= ModelTransforms.displayedWorld), whose Backward is exactly the mesh's
            // server frame — so render → metric world → server is one step each, the
            // hit comes back through dispWorld.Forward, and per-mesh centroid never
            // enters. overlaySize is CSS px (ViewportSize ÷ dpr) → dpr-correct picks.
            let worldRayHit (px : float) (py : float) : Async<V3d option> =
                let s = AVal.force overlaySize
                let w = float (max 1 s.X)
                let h = float (max 1 s.Y)
                let aspect = w / h
                let clipX = 2.0 * px / w - 1.0
                let clipY = 1.0 - 2.0 * py / h
                // fc = the pano centre (the 360° camera position; the ortho frame
                // centre for Top) — matches the render camera.
                let fc = AVal.force panoEye
                let ext = AVal.force fitExtent
                let z = AVal.force zoomEff
                let pan = AVal.force panEff
                let originR, dirR =
                    if isPano then
                        // Perspective unproject: forward + the NDC offsets along the
                        // camera's right/up, scaled by the half-fov tangents — exactly
                        // the panoCam frustum.
                        let fwd = panoForward pan
                        let right = (Vec.cross fwd V3d.OOI).Normalized
                        let up = Vec.cross right fwd
                        let tx, ty = panoHalfTans z aspect
                        fc, (fwd + right * (clipX * tx) + up * (clipY * ty)).Normalized
                    else
                        let halfE = ext / max 1e-3 z
                        V3d(fc.X + pan.X * ext + clipX * halfE * aspect,
                            fc.Y + pan.Y * ext + clipY * halfE,
                            fc.Z + (ext + 1.0) * 5.0), V3d(0.0, 0.0, -1.0)
                let sc = AVal.force scale
                let cc = AVal.force model.CommonCentroid
                let dispWorld = RigidTransform.renderToWorld sc cc (AVal.force (MeshView.displayedMeshT model name))
                let serverOrigin = dispWorld.Backward.TransformPos (ScanPin.worldCentre cc sc originR)
                let serverDir = (dispWorld.Backward.TransformDir dirR).Normalized
                async {
                    let! hit = Query.rayHit ApiConfig.apiBase.Value name 0 serverOrigin serverDir
                    return hit |> Option.map (fun hh -> dispWorld.Forward.TransformPos hh.point)
                }
            // Armed for THIS mesh (CorrArm = Some(pin, name))? Then move = live aim
            // ghost (throttled), click = place (the reducer disarms on a committed
            // pick). Otherwise: drag = pan.
            let armedHere () =
                match AVal.force model.CorrArm with
                | Some (pid, m) when m = name -> Some pid
                | _ -> None
            Dom.OnPointerDown(fun e ->
                let p = e.OffsetPosition
                lastPx <- p
                // Drag = camera (pan in Top, look-around in 360°): middle or Shift+left
                // always; plain left too when no correspondence edit is armed. Armed
                // plain left stays reserved for placing the point.
                match armedHere () with
                | Some pinId when e.Button = Button.Left && not e.Shift ->
                    async {
                        match! worldRayHit (float p.X) (float p.Y) with
                        | Some world -> env.Emit [PickCorrespondenceAt(pinId, name, world)]
                        | None -> ()
                    } |> Async.Start
                | _ ->
                    if e.Button = Button.Middle || e.Button = Button.Left then dragging <- true)
            Dom.OnPointerUp(fun _ -> dragging <- false)
            Dom.OnPointerMove(fun e ->
                let p = e.OffsetPosition
                if (armedHere ()).IsSome then
                    let now = nowMs ()
                    if now - lastHoverMs > 60.0 then
                        lastHoverMs <- now
                        hoverGen <- hoverGen + 1
                        let gen = hoverGen
                        async {
                            let! wld = worldRayHit (float p.X) (float p.Y)
                            if gen = hoverGen then env.Emit [CorrPreviewComputed wld]
                        } |> Async.Start
                elif dragging then
                    let s = AVal.force overlaySize
                    let w = float (max 1 s.X)
                    let h = float (max 1 s.Y)
                    if isPano then
                        // Grab-the-world look-around: the surface point under the cursor
                        // stays under it — per-axis atan offsets at the current fov, old
                        // vs new cursor position.
                        let tx, ty = panoHalfTans (curZoom ()).Value (w / h)
                        let thX (px : float) = atan ((2.0 * px / w - 1.0) * tx)
                        let thY (py : float) = atan ((1.0 - 2.0 * py / h) * ty)
                        let dAz = thX (float p.X) - thX (float lastPx.X)
                        let dEl = thY (float lastPx.Y) - thY (float p.Y)
                        transact (fun () -> panoRotate (curPan ()) dAz dEl)
                    else
                        let d = p - lastPx
                        let k = 2.0 / (h * max 1e-3 (AVal.force zoomEff))
                        let pc = curPan ()
                        transact (fun () ->
                            pc.Value <- pc.Value + V2d(-float d.X * k, float d.Y * k))
                lastPx <- p)
            Dom.OnMouseLeave(fun _ ->
                if (armedHere ()).IsSome then
                    hoverGen <- hoverGen + 1
                    env.Emit [CorrPreviewComputed None])
            // Mouse-anchored zoom: keep the point under the cursor fixed — Top shifts
            // the pan, 360° rotates so the view direction under the cursor keeps its
            // screen position across the fov change.
            Dom.OnMouseWheel(fun e ->
                let s = AVal.force overlaySize
                let w = float (max 1 s.X)
                let h = float (max 1 s.Y)
                let aspect = w / h
                let clipX = 2.0 * float lastPx.X / w - 1.0
                let clipY = 1.0 - 2.0 * float lastPx.Y / h
                let zc = curZoom ()
                let pc = curPan ()
                if isPano then
                    let z = zc.Value
                    let z' = clamp 0.05 200.0 (z * (1.1 ** (-e.DeltaY / 120.0)))
                    let txOld, tyOld = panoHalfTans z  aspect
                    let txNew, tyNew = panoHalfTans z' aspect
                    let dAz = atan (clipX * txNew) - atan (clipX * txOld)
                    let dEl = atan (clipY * tyOld) - atan (clipY * tyNew)
                    transact (fun () ->
                        zc.Value <- z'
                        panoRotate pc dAz dEl)
                else
                    // The clamp + anchor maths run on the EFFECTIVE zoom; only the
                    // user factor is stored (pan offsets add linearly regardless).
                    let zEff = AVal.force zoomEff
                    let zEff' = clamp 0.05 200.0 (zEff * (1.1 ** (-e.DeltaY / 120.0)))
                    transact (fun () ->
                        zc.Value <- zc.Value * (zEff' / max 1e-9 zEff)
                        pc.Value <- pc.Value + V2d(clipX * aspect * (1.0/zEff - 1.0/zEff'), clipY * (1.0/zEff - 1.0/zEff'))))
            let viewT, projT =
                if isPano then panoCam size panoEye fitExtent panEff zoomEff
                else orthoCam size panoEye fitExtent panEff zoomEff
            // Reference-mesh silhouette overlaid on the enlarged single when a non-reference
            // (moving) mesh is shown — the image-space outline reused from the 3D view, in
            // gold, drawn on top (Correspondence + Inspect; the single only exists there).
            // Top only: the outline pass is untried from an inside-the-mesh perspective
            // camera. Reference is never solved, so the outline is pose-stable while the
            // moving surface shifts under it.
            let refOutline =
                if isPano then ASet.empty
                else
                    let show =
                        model.Registration |> AVal.map (fun r ->
                            match r.ReferenceMesh with Some rf -> rf <> name | None -> false)
                    let node = MeshView.buildReferenceOutlineNode model viewT projT (V4f(0.831f, 0.631f, 0.024f, 1.0f)) show
                    OutlineView.buildFromNode info (model.OutlineThreshold |> AVal.map float32) node
            // One standard pipeline for both projections — the camera (viewT/projT)
            // carries the whole difference.
            let surface =
                sg {
                    Sg.Trafo renderT
                    Sg.Shader { DefaultSurfaces.trafo; DefaultSurfaces.diffuseTexture; FocusShaders.focusColor }
                    Sg.Uniform("DiffuseColorTexture", loaded.tex)
                    Sg.Uniform("FocusMode", surfaceMode)
                    Sg.Uniform("FocusHi",   hiA)
                    Sg.Uniform("FocusLoNeg", loNegA)
                    Sg.Uniform("FocusLod",  AVal.constant 0.0f)
                    Sg.Uniform("FocusIsoStep", isoA)
                    Sg.NoEvents
                    Sg.VertexAttributes(vattrs loaded scalarBuf)
                    Sg.Index(BufferView(loaded.idx, typeof<int>))
                    Sg.Render loaded.fvc
                }
            sg {
                Sg.View viewT
                Sg.Proj projT
                Sg.Uniform("ViewportSize", size)
                Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
                Sg.BlendMode (AVal.constant BlendMode.Blend)
                surface
                refOutline
                Lines.render arrowSegs
                overlay
                brushedDotsNode model name
            }
        }

    // One thumbnail tile per mesh — the mesh browser (§B/T3). The render area selects
    // (focus) and peek-isolates on hover (mirrors the Overview rail roster); a control
    // strip carries the per-mesh controls that live ONCE here: ★ reference toggle,
    // visibility, ◐ isolate. All meshes are tiled (hidden → dimmed) so a hidden mesh
    // can be re-enabled. Reference tile = a prominent ★ indicator (T10).
    let private focusTile (env : Env<Message>) (model : AdaptiveModel) (name : string) : DomNode =
        let loaded = MeshView.loadMeshAsync (fun () -> ()) name
        let renderT, scale = renderTrafoOf model name loaded
        // Centre the thumbnail on the same panorama centre as the single.
        let _, fitCenter, fitExtent = framing model name loaded renderT scale
        let modeA, scalarBuf, hiA, loNegA, isoA = focusOverlay model name loaded scale
        let rc =
            renderControl {
                RenderControl.Samples 1
                Class "focus-rc"
                let! size = RenderControl.ViewportSize
                // Tiles frame the selection too (§A2): a pin/cell zooms every tile
                // onto that region on ITS OWN mesh — a small-multiples comparison.
                let baseFrame = selBaseFrame model name scale fitCenter fitExtent
                let view, proj = orthoCam size fitCenter fitExtent (baseFrame |> AVal.map fst) (baseFrame |> AVal.map snd)
                Sg.View view
                Sg.Proj proj
                sg {
                    Sg.Trafo renderT
                    Sg.Shader { DefaultSurfaces.trafo; DefaultSurfaces.diffuseTexture; FocusShaders.focusColor }
                    Sg.Uniform("DiffuseColorTexture", loaded.tex)
                    Sg.Uniform("FocusMode", modeA)
                    Sg.Uniform("FocusHi",   hiA)
                    Sg.Uniform("FocusLoNeg", loNegA)
                    Sg.Uniform("FocusLod",  AVal.constant 0.0f)
                    Sg.Uniform("FocusIsoStep", isoA)
                    Sg.NoEvents
                    Sg.VertexAttributes(vattrs loaded scalarBuf)
                    Sg.Index(BufferView(loaded.idx, typeof<int>))
                    Sg.Render loaded.fvc
                }
                brushedDotsNode model name
            }
        let idxVal = model.MeshOrder |> AMap.tryFind name |> AVal.map (Option.defaultValue 0)
        let colorCss = idxVal |> AVal.map (fun i -> Primitives.c4bToRgbCss (Primitives.meshColor i))
        let active = model.Selection.Active |> AVal.map (fun s -> Selection.mesh s = Some name)
        let isVis  = model.MeshVisible |> AVal.map (fun m -> Map.tryFind name m |> Option.defaultValue true)
        let isRef  = model.Registration |> AVal.map (fun r -> r.ReferenceMesh = Some name)
        let isSolo = model.MeshSolo |> AVal.map ((=) (Some name))
        let anySolo = model.MeshSolo |> AVal.map Option.isSome
        let refBtn =
            button {
                Class "mb mb-ref"
                Primitives.classWhen "mb-on" isRef
                Attribute("title", "Reference mesh — all error is relative to it")
                Dom.OnClick(fun _ ->
                    let cur = AVal.force isRef
                    env.Emit [SetReferenceMesh (if cur then None else Some name)])
                isRef |> AVal.map (fun r -> if r then "★" else "☆")
            }
        // The visibility toggle is frozen while a mesh is isolated (isolation
        // overrides it; ending isolation resets every toggle to ON).
        let visBtn =
            button {
                Class "mb"
                Primitives.classWhen "mb-on" isVis
                anySolo |> AVal.map (fun s ->
                    if s then Some (Attribute("disabled", "disabled")) else None)
                anySolo |> AVal.map (fun s ->
                    Some (Attribute("title", if s then "Visibility is locked while a mesh is isolated" else "Visible")))
                Dom.OnClick(fun _ ->
                    if not (AVal.force anySolo) then
                        env.Emit [SetVisible(name, not (AVal.force isVis))])
                isVis |> AVal.map (fun v -> if v then "●" else "○")
            }
        let soloBtn =
            button {
                Class "mb"
                Primitives.classWhen "mb-on" isSolo
                Attribute("title", "Isolate this mesh (hide the others); click again to restore")
                Dom.OnClick(fun _ -> env.Emit [ToggleMeshSolo name])
                "◐"
            }
        div {
            Class "focus-tile"
            Primitives.classWhen "fm-active" active
            Primitives.classWhen "ft-ref" isRef
            Primitives.classWhenNot "ft-hidden" isVis
            // Render area = the selector (click → focus). Controls are a sibling strip
            // so clicking them never also focuses.
            div {
                Class "focus-tile-view"
                Attribute("title", "click → select · double-click → zoom · hover → isolate this mesh")
                Dom.OnClick(fun _ -> env.Emit [SetSelection (SelMesh name)])
                Dom.OnDoubleClick(fun _ -> env.Emit [SetSelection (SelMesh name); ZoomToMesh name])
                // hover = peek-isolate this mesh in the 3D view (mirrors the rail roster).
                Dom.OnPointerMove(fun _ -> env.Emit [SetHovered (Some (HoverMesh name))])
                Dom.OnMouseLeave(fun _ -> env.Emit [SetHovered None])
                rc
                div {
                    Class "fm-label"
                    span { Class "fm-sw"; colorCss |> AVal.map (fun c -> Some (Style [Css.Background c])) }
                    span { Class "ft-refstar"; Primitives.showWhen isRef; "★" }
                    model.MeshNames.Content |> AVal.map (fun ns -> Primitives.friendlyName (IndexList.toList ns) name)
                }
            }
            div {
                Class "focus-tile-ctrls"
                refBtn
                visBtn
                soloBtn
            }
        }

    // The large single, keyed by (mesh, effective projection) so a projection toggle
    // rebuilds. The displacement channel forces ortho (its arrowheads are laid out in
    // the Top view's XY screen plane), so 360° collapses to Top there.
    let single (env : Env<Message>) (model : AdaptiveModel) =
        AVal.custom (fun t ->
            // Resolve the focused mesh INLINE — building a transient AVal.custom here
            // (the old `focusMeshOf model`) and reading it dropped its dependency edge,
            // so this aval evaluated once (empty at startup) and never re-fired when the
            // meshes loaded → the single stayed blank.
            let chosen =
                let names = model.MeshNames.Content.GetValue t |> IndexList.toList
                let vis = model.MeshVisible.GetValue t
                let visible = names |> List.filter (fun n -> Map.tryFind n vis |> Option.defaultValue true)
                match Selection.mesh (model.Selection.Active.GetValue t) with
                | Some m when List.contains m visible -> Some m
                | _ -> List.tryHead visible
            match chosen with
            | None -> IndexList.empty
            | Some n ->
                let proj = model.FocusProjection.GetValue t
                let disp = model.WorkflowStep.GetValue t = Inspect && model.InspectChannel.GetValue t = ChDisplacement
                IndexList.single (n, (if disp && proj = ProjPano then ProjTop else proj)))
        |> AList.ofAVal
        |> AList.map (fun (n, proj) -> focusSingle env model n proj)

    // One tile per mesh — the mesh browser (T3). ALL meshes are listed (hidden ones
    // dimmed) so a hidden mesh can be re-enabled from its tile.
    let multiples (env : Env<Message>) (model : AdaptiveModel) =
        model.MeshNames |> AList.map (focusTile env model)
