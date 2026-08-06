namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open Aardworx.WebAssembly
open FSharp.Data.Adaptive
open Aardvark.Dom
open Superprojekt.LineGlyphs

// The secondary 2D views: ONE persistent right-edge tile strip (a top-down
// ORTHOGRAPHIC thumbnail per mesh, shared per-mesh 2D cameras), present at
// every focus level — Matrix shows all meshes, Pair/Pin the selected pair's
// two. Tiles do VISIBILITY (unarmed click = isolate/de-isolate, hover =
// preview); picking is ARM-DRIVEN — the armed target attributes the mesh (an
// ArmPoint pick raycasts its own mesh alone regardless of the view — that is
// what keeps co-located pairs pickable), so an armed click in a tile is just
// another pick surface. Picks resolve through server raycasts — Sg pointer
// events do not fire reliably in secondary render controls.
module GuiPanes =

    open Primitives

    let private pickRay (cursorPx : V2d) (vpSize : V2i) (viewTrafo : Trafo3d) (projTrafo : Trafo3d) =
        let ndc = V2d(2.0 * cursorPx.X / float vpSize.X - 1.0,
                      1.0 - 2.0 * cursorPx.Y / float vpSize.Y)
        let vp = viewTrafo * projTrafo
        let p0 = vp.Backward.TransformPosProj(V3d(ndc, -1.0))
        let p1 = vp.Backward.TransformPosProj(V3d(ndc, 1.0))
        Ray3d(p0, (p1 - p0) |> Vec.normalize)

    let private nowMs () = float System.DateTime.UtcNow.Ticks / 10000.0

    // ── The armed pick, shared by EVERY view (main 3D and any tile; ray in
    // RENDER space): the arm target picks the raycast candidates — ArmPoint
    // its own mesh alone, ArmCentre both pair meshes (nearest hit lands).
    // Returns (mesh, own-frame local, metric world).
    let private armedResolve (model : AdaptiveModel) (target : ArmTarget) (ray : Ray3d)
        : Async<(string * V3d * V3d) option> =
        match (AVal.force model.Sel).Pair with
        | None -> async.Return None
        | Some (a, b) ->
            let candidates = match target with ArmPoint m -> [m] | ArmCentre -> [a; b]
            async {
                let! hits =
                    candidates
                    |> List.map (fun name -> async {
                        let cc = AVal.force model.CommonCentroid
                        let scale = DatasetScale.forMesh (AVal.force model.DatasetScales) name
                        let dispWorld =
                            RigidTransform.renderToWorld scale cc (AVal.force (MeshView.displayedMeshT model name))
                        let serverOrigin = dispWorld.Backward.TransformPos (ScanPin.worldCentre cc scale ray.Origin)
                        let serverDir = (dispWorld.Backward.TransformDir ray.Direction).Normalized
                        let! hit = Query.rayHit ApiConfig.apiBase.Value name 0 serverOrigin serverDir
                        return hit |> Option.map (fun h ->
                            let world = dispWorld.Forward.TransformPos h.point
                            let rp = ScanPin.renderCentre cc scale world
                            Vec.dot (rp - ray.Origin) ray.Direction, name, h.point, world)
                    })
                    |> Async.Parallel
                return
                    hits |> Array.choose id |> Array.sortBy (fun (d, _, _, _) -> d)
                    |> Array.tryHead |> Option.map (fun (_, n, local, world) -> n, local, world)
            }

    // A landed armed pick: route into the draft (centre/point) or a committed
    // pin's point re-pick; the reducer disarms on landing. A CENTRE landing
    // first validates the ROI against the OTHER pair mesh (adaptive radius:
    // grow until ≥20 of its vertices fall inside, capped at ×4 — past the cap
    // the location is refused and the arm stays lit for another try).
    let armedPick (env : Env<Message>) (model : AdaptiveModel) (ray : Ray3d) =
        match AVal.force model.ArmedPick with
        | None -> ()
        | Some target ->
            async {
                let! hit = armedResolve model target ray
                match hit with
                | Some (mesh, local, world) ->
                    let sel = AVal.force model.Sel
                    let placement = AVal.force model.ScanPins.Placement
                    let msgs =
                        match placement with
                        | PlacementActive _ ->
                            match target with
                            | ArmCentre -> [ScanPinMsg (DraftAreaAt(mesh, local))]
                            | ArmPoint m -> [ScanPinMsg (DraftPointAt(m, local))]
                        | PlacementIdle ->
                            match target, sel.Pin with
                            | ArmPoint m, Some id -> [ScanPinMsg (EditPointAt(id, m, local))]
                            | ArmCentre, Some id -> [ScanPinMsg (EditCentreAt(id, mesh, local))]
                            | _ -> []
                    if not (List.isEmpty msgs) then
                        match target, sel.Pair with
                        | ArmCentre, Some (a, b) ->
                            let other = if mesh = a then b else a
                            // The radius this landing will commit: the draft's,
                            // else the selected pin's.
                            let curRadius =
                                match placement with
                                | PlacementActive d -> d.Radius
                                | PlacementIdle ->
                                    sel.Pin
                                    |> Option.bind (fun id -> HashMap.tryFind id (AVal.force model.ScanPins.Pins.Content))
                                    |> Option.map (fun p -> p.InnerRadius)
                                    |> Option.defaultValue (AVal.force model.QuickPinRadius)
                            let cc = AVal.force model.CommonCentroid
                            let scale = DatasetScale.forMesh (AVal.force model.DatasetScales) other
                            let otherT =
                                (RigidTransform.renderToWorld scale cc (AVal.force (MeshView.displayedMeshT model other))).Forward
                            let otherNum =
                                (AVal.force (model.MeshOrder |> AMap.tryFind other) |> Option.defaultValue 0) + 1
                            let! ok, r = Query.roiFit ApiConfig.apiBase.Value other otherT world curRadius 20 4.0
                            if not ok then
                                env.Emit [ShowToast (sprintf "No centre here — mesh %d has no surface within reach (the region would have to grow past ×4)." otherNum)]
                            elif r > curRadius * 1.0001 then
                                // Radius BEFORE centre: the centre landing may
                                // complete the draft (landDraft mints on it).
                                let radiusMsg =
                                    match placement, sel.Pin with
                                    | PlacementActive _, _ -> ScanPinMsg (SetDraftRadius r)
                                    | PlacementIdle, Some id -> ScanPinMsg (SetInnerRadius(id, r))
                                    | PlacementIdle, None -> ScanPinMsg (SetDraftRadius r)
                                env.Emit (radiusMsg :: msgs)
                                env.Emit [ShowToast (sprintf "Region grew to %.2f m to include mesh %d." r otherNum)]
                            else
                                env.Emit msgs
                        | _ -> env.Emit msgs
                | None -> ()
            } |> Async.Start

    // The armed hover preview (server raycast, throttled; the reducer drops
    // stale landings once disarmed).
    let mutable private armHoverMs = 0.0
    let armedHover (env : Env<Message>) (model : AdaptiveModel) (ray : Ray3d) =
        match AVal.force model.ArmedPick with
        | None -> ()
        | Some target ->
            let now = nowMs ()
            if now - armHoverMs > 70.0 then
                armHoverMs <- now
                async {
                    let! hit = armedResolve model target ray
                    if (AVal.force model.ArmedPick).IsSome then
                        env.Emit [SetArmPreview (hit |> Option.map (fun (_, _, w) -> w))]
                } |> Async.Start

    // ── The ONE 2D top-down camera (keyed per mesh — a mesh keeps its view
    // across levels): the stored pan/zoom, else the default bounds framing
    // (sky = +Y — a look-down view cannot use +Z). The default frames the
    // REFERENCE ROOT's bounds, so unpanned tiles all show the same area
    // (comparable small multiples); own bounds only without a root.
    let private tileCamOf (model : AdaptiveModel) (name : string) : aval<TileCam> =
        AVal.custom (fun t ->
            // Pin-row hover: every tile preview-frames the hovered pin — the
            // exact framing a row click (SelectPin → frameTiles) makes
            // persistent, so click = "keep what you see".
            let hovered =
                match model.TilePinHover.GetValue t with
                | Some id ->
                    match HashMap.tryFind id (model.ScanPins.Pins.Content.GetValue t) with
                    | Some p ->
                        let cc = model.CommonCentroid.GetValue t
                        let scale = DatasetScale.active (model.ActiveDataset.GetValue t) (model.DatasetScales.GetValue t)
                        let world = (MeshView.displayedWorldAt model t p.AnchorMesh).Forward.TransformPos p.CentreLocal
                        let radius =
                            clamp 0.05 100000.0
                                (ScanPin.renderLength scale (max 0.5 (p.InnerRadius * 3.0)) / tan (30.0 * System.Math.PI / 180.0))
                        Some { Centre = ScanPin.renderCentre cc scale world; Radius = radius }
                    | None -> None
                | None -> None
            match hovered with
            | Some c -> c
            | None ->
            match Map.tryFind name (model.TileCams.GetValue t) with
            | Some c -> c
            | None ->
                let cc = model.CommonCentroid.GetValue t
                let bounds = model.MeshBounds.GetValue t
                let subject =
                    match (model.RegGraph.GetValue t).Root with
                    | Some r when (match Map.tryFind r bounds with Some b -> not b.IsInvalid | None -> false) -> r
                    | _ -> name
                let scale = DatasetScale.forMesh (model.DatasetScales.GetValue t) subject
                match Map.tryFind subject bounds with
                | Some b when not b.IsInvalid ->
                    { Centre = ScanPin.renderCentre cc scale b.Center
                      Radius = max 1.0 (b.Size.Length * scale * 0.55) }
                | _ -> { Centre = V3d.Zero; Radius = 5.0 })

    // The view is ORTHOGRAPHIC top-down: Radius drives the ortho half-width
    // (tan 30° keeps the framing the earlier 60°-fov perspective had, so
    // stored cameras keep their meaning), and the eye rides high enough above
    // the centre plane that no terrain near-clips at any zoom.
    let private halfWidthOf (cam : TileCam) =
        cam.Radius * tan (30.0 * System.Math.PI / 180.0)

    // Render units per CSS pixel at the centre plane.
    let private unitsPerPx (cam : TileCam) (w : int) =
        2.0 * halfWidthOf cam / float (max 1 w)

    // Scene Z extent in render units — the eye-height margin that keeps the
    // whole terrain inside the ortho volume.
    let private zExtent (model : AdaptiveModel) (t : AdaptiveToken) =
        let b = model.SceneBounds.GetValue t
        let s = DatasetScale.active (model.ActiveDataset.GetValue t) (model.DatasetScales.GetValue t)
        if b.IsInvalid then 100.0 else max 10.0 (b.Size.Z * s)

    let private cam2dView (model : AdaptiveModel) (camA : aval<TileCam>) =
        AVal.custom (fun t ->
            let c = camA.GetValue t
            let d = c.Radius + zExtent model t
            CameraView.lookAt (c.Centre + V3d.OOI * d) c.Centre V3d.OIO |> CameraView.viewTrafo)

    let private cam2dProj (model : AdaptiveModel) (camA : aval<TileCam>) (size : aval<V2i>) =
        AVal.custom (fun t ->
            let c = camA.GetValue t
            let s = size.GetValue t
            let aspect = float s.X / float (max 1 s.Y)
            let halfW = halfWidthOf c
            let halfH = halfW / aspect
            let zext = zExtent model t
            let fr : Frustum =
                { left = -halfW; right = halfW; bottom = -halfH; top = halfH
                  near = 0.1; far = c.Radius + 2.0 * zext + 10.0; isOrtho = true }
            Frustum.projTrafo fr)

    // The controller attributes: drag pans in the XY plane (anchored at the
    // drag start — no incremental drift; screen right = +X, screen down = −Y),
    // wheel zooms TO the cursor (the point under it stays put: its offset from
    // the centre scales with the radius). A drag-free pointer-up (≤ 4 px) is a
    // click → `onPick`; a drag-free move → `onHover Some`, leave → `onHover
    // None` (the pin tiles' armed pick/preview; the survey tiles pass None).
    let private cam2dAtts (env : Env<Message>) (name : string)
                          (camA : aval<TileCam>) (clientSize : aval<V2i>)
                          (onPick : (V2d -> unit) option)
                          (onHover : (V2d option -> unit) option) =
        let mutable drag : (V2d * TileCam * bool) option = None
        att {
            Dom.OnPointerDown((fun e ->
                drag <- Some (V2d(float e.OffsetPosition.X, float e.OffsetPosition.Y), AVal.force camA, false)),
                pointerCapture = true)
            Dom.OnPointerUp((fun e ->
                (match drag, onPick with
                 | Some (p0, _, moved), Some pick ->
                    let p = V2d(float e.OffsetPosition.X, float e.OffsetPosition.Y)
                    if not moved && Vec.distance p0 p < 4.0 then pick p
                 | _ -> ())
                drag <- None),
                pointerCapture = true)
            Dom.OnPointerMove(fun e ->
                match drag with
                | Some (p0, cam0, moved) ->
                    let p = V2d(float e.OffsetPosition.X, float e.OffsetPosition.Y)
                    let d = p - p0
                    // Jitter under the click threshold stays a click.
                    if moved || d.Length >= 4.0 then
                        drag <- Some (p0, cam0, true)
                        let u = unitsPerPx cam0 (AVal.force clientSize).X
                        env.Emit [SetTileCam(name, { cam0 with Centre = cam0.Centre - V3d(d.X * u, -d.Y * u, 0.0) })]
                | None ->
                    match onHover with
                    | Some h -> h (Some (V2d(float e.OffsetPosition.X, float e.OffsetPosition.Y)))
                    | None -> ())
            Dom.OnMouseLeave(fun _ ->
                match onHover with
                | Some h -> h None
                | None -> ())
            Dom.OnMouseWheel(fun e ->
                let cam = AVal.force camA
                let s = AVal.force clientSize
                let u = unitsPerPx cam s.X
                let k = 1.1 ** (e.DeltaY / 120.0)
                let off = V3d((float e.OffsetPosition.X - float s.X * 0.5) * u,
                              -(float e.OffsetPosition.Y - float s.Y * 0.5) * u, 0.0)
                env.Emit [SetTileCam(name, { Centre = cam.Centre + off - off * k; Radius = cam.Radius * k })])
        }

    // One strip tile: a top-down thumbnail of the mesh (displayed pose, live
    // survey heatmap, the shared 2D pan/zoom camera), identity chip, the gold
    // root outline on top, and the pair's pin marks while this mesh sits in
    // the selected pair. The tile's OWN interaction is visibility: unarmed
    // click = isolate/de-isolate, hover = isolate preview. While a pick is
    // armed the click lands the pick instead — the ARM TARGET attributes the
    // mesh, the tile is just another view surface.
    let private meshTile (env : Env<Message>) (model : AdaptiveModel) (name : string) =
        // Strip scope: Matrix = every mesh, Pair/Pin = the selected pair only.
        let shownA =
            (model.Focus, model.Sel) ||> AVal.map2 (fun f s ->
                match f with
                | FocusMatrix -> true
                | FocusPair | FocusPin ->
                    match s.Pair with
                    | Some (a, b) -> name = a || name = b
                    | None -> false)
        let idxVal = model.MeshOrder |> AMap.tryFind name |> AVal.map (Option.defaultValue 0)
        let isRoot = model.RegGraph |> AVal.map (fun g -> g.Root = Some name)
        let isolated = model.TileIsolate |> AVal.map ((=) (Some name))
        let datasetScale =
            (model.ActiveDataset, model.DatasetScales) ||> AVal.map2 DatasetScale.active
        let pinsVal = model.ScanPins.Pins |> AMap.toAVal
        // The other pair mesh while this mesh sits in the selected pair —
        // scopes the pin marks and feeds the armed-placement overlap gate.
        let otherA =
            model.Sel |> AVal.map (fun s ->
                match s.Pair with
                | Some (a, b) when name = a -> Some b
                | Some (a, b) when name = b -> Some a
                | _ -> None)
        let marksOn =
            (shownA, otherA) ||> AVal.map2 (fun sh o -> sh && o.IsSome)
        // Own-frame local of ANY mesh → render position (the pair tiles share
        // render space — a mark rides its own mesh's pose whichever tile draws it).
        let renderOn (t : AdaptiveToken) (mesh : string) (local : V3d) =
            let cc = model.CommonCentroid.GetValue t
            let s = datasetScale.GetValue t
            ScanPin.renderCentre cc s ((MeshView.displayedWorldAt model t mesh).Forward.TransformPos local)
        let renderOf (t : AdaptiveToken) (local : V3d) = renderOn t name local

        div {
            Class "mesh-tile"
            classWhen "tile-off" (shownA |> AVal.map not)
            classWhen "tile-iso" isolated
            classWhen "tile-armed" (model.ArmedPick |> AVal.map Option.isSome)
            // Wheel over a tile ZOOMS — the strip's overflow scroll must not
            // also fire; framework wheel listeners are passive, so the
            // default needs its own non-passive preventDefault.
            OnBoot ["__THIS__.addEventListener('wheel',function(e){e.preventDefault();},{passive:false});"]
            div {
                Class "pane-chip"
                idxVal |> AVal.map (fun i -> Some (Attribute("title", sprintf "mesh %d" (i + 1))))
                span { Class "pmx-sw"; (idxVal, isRoot) ||> AVal.map2 (fun i r -> Some (Style [Css.Background (c4bToRgbCss (meshColorRoot r i))])) }
                span { Class "pmx-num"; idxVal |> AVal.map (fun i -> string (i + 1)) }
                span { Class "pmx-root-star"; isRoot |> AVal.map (fun r -> if r then "★" else "") }
            }
            renderControl {
                RenderControl.Samples 1
                Class "tile-rc"

                let! info = RenderControl.Info
                let! size = RenderControl.ViewportSize
                let! client = RenderControl.ClientSize
                // CSS-pixel size for cursor→ray math (ViewportSize is
                // framebuffer px; ClientSize is V2i.II until the first DOM event).
                let clientSize =
                    (client, size) ||> AVal.map2 (fun c v ->
                        if c.X > 1 && c.Y > 1 then c else v)

                let camA = tileCamOf model name
                let view = cam2dView model camA
                let proj = cam2dProj model camA size

                let rayOf (cursorPx : V2d) =
                    pickRay cursorPx (AVal.force clientSize) (AVal.force view) (AVal.force proj)
                // The tile's click: an armed pick captures it (any view is a
                // pick surface); unarmed it toggles the isolation.
                let pick (cursorPx : V2d) =
                    if (AVal.force model.ArmedPick).IsSome then armedPick env model (rayOf cursorPx)
                    else env.Emit [ToggleTileIsolate name]
                let hover (p : V2d option) =
                    if (AVal.force model.ArmedPick).IsSome then
                        match p with
                        | Some cursorPx -> armedHover env model (rayOf cursorPx)
                        | None -> env.Emit [SetArmPreview None]
                    else
                        env.Emit [SetTileIsolateHover (p |> Option.map (fun _ -> name))]

                cam2dAtts env name camA clientSize (Some pick) (Some hover)

                Sg.View view
                Sg.Proj proj
                Sg.Pass RenderPass.passZero
                Sg.Uniform("ViewportSize", size)

                // Tile-camera coverage MRT for the armed-placement
                // isolate-overlap gate — rendered only while the gate reads it.
                let covActive =
                    (marksOn, model.ScanPins.Placement) ||> AVal.map2 (fun m pl ->
                        m && (match pl with PlacementActive _ -> true | PlacementIdle -> false))
                let cov0, cov1, _ = OutlineView.coverageOffscreen info model covActive view proj
                MeshView.buildPaneScene model name shownA (otherA, cov0, cov1) size

                // The gold reference outline: the ROOT mesh's footprint from
                // this tile's camera, on top of everything.
                let rc0, rc1, rtexel = OutlineView.rootCoverageOffscreen info model shownA view proj
                OutlineView.buildRootOutline shownA (model.OutlineWidthPx |> AVal.map float32) rc0 rc1 rtexel

                // ── Pin marks: the SAME committed glyphs as the main 3D
                // (the ScanPinScene module builders — the two views must not
                // drift): every pair pin's area figure (duplex equator ring +
                // white contact rings; the anchor tile adds the dashed
                // anchorage ring) + its own point's reveal, the highlight
                // ring, the draft and the armed cursor preview.
                let upA = MeshView.projectUpNormal model
                let armDimA = model.ArmedPick |> AVal.map (fun a -> if a.IsSome then 0.15 else 1.0)
                let segs =
                    AVal.custom (fun t ->
                        let sel = model.Sel.GetValue t
                        match sel.Pair with
                        | Some (pa, pb) when name = pa || name = pb ->
                            let pair = (pa, pb)
                            let pins = pinsVal.GetValue t
                            // While a pick is armed the placed marks fade to
                            // near-invisible so they can't hide the pick spot;
                            // only the armed preview stays full.
                            let armDim = armDimA.GetValue t
                            let cc = model.CommonCentroid.GetValue t
                            let scale = datasetScale.GetValue t
                            let toRender = ScanPin.renderCentre cc scale
                            let axis = match upA.GetValue t with Some u -> u | None -> V3d.OOI
                            let _, u, v = basisFromNormal axis
                            let rMax = max 0.01 (model.RevealRadius.GetValue t)
                            let out = ResizeArray<V3d * V3d * V4d * float>()
                            // The area figure in BOTH pair tiles (the centre
                            // rides the ANCHOR mesh's pose); the anchor tile
                            // alone adds the dashed anchorage ring.
                            let areaFigure (dim : float) (anchorMesh : string)
                                           (centreLocal : V3d) (radius : float) rings =
                                let cR = renderOn t anchorMesh centreLocal
                                let rR = ScanPin.renderLength scale radius
                                ScanPinScene.addAreaRing out cR u v rR dim
                                ScanPinScene.addContactRings out toRender rings dim
                                if anchorMesh = name then
                                    addDashedRing out cR u v (rR * 1.08) (V4d(1.0, 1.0, 1.0, 0.85 * dim)) 1.5 64
                            // The tile's OWN point's intersection reveal (the
                            // other side's relief belongs to the other tile).
                            let reveal (dim : float) (localPt : V3d) (rv : RevealState) =
                                match rv with
                                | RevealReady lines ->
                                    ScanPinScene.addRevealLines out (renderOf t) localPt rMax dim lines
                                | _ -> ()
                            for (id, p) in HashMap.toSeq pins do
                                if p.Pair = pair then
                                    let dim = armDim * ScanPinScene.pinScopeDim model t id
                                    let rings = match p.ContactRings with RingsReady m -> m | _ -> Map.empty
                                    areaFigure dim p.AnchorMesh p.CentreLocal p.InnerRadius rings
                                    reveal dim (if name = fst pair then p.PointA else p.PointB)
                                               (if name = fst pair then p.RevealA else p.RevealB)
                            (match ScanPinScene.highlightPin model t with
                             | Some id ->
                                (match HashMap.tryFind id pins with
                                 | Some p when p.Pair = pair ->
                                    ScanPinScene.addHighlightRing out
                                        (renderOn t p.AnchorMesh p.CentreLocal) u v
                                        (ScanPin.renderLength scale p.InnerRadius)
                                 | _ -> ())
                             | None -> ())
                            (match model.ScanPins.Placement.GetValue t with
                             | PlacementActive d when name = fst d.Pair || name = snd d.Pair ->
                                (match d.Area with
                                 | Some (m, local) ->
                                    let rings = match d.Rings with RingsReady r -> r | _ -> Map.empty
                                    areaFigure armDim m local d.Radius rings
                                 | None -> ())
                                (match (if name = fst d.Pair then d.PointA else d.PointB) with
                                 | Some local -> reveal armDim local (if name = fst d.Pair then d.RevealA else d.RevealB)
                                 | None -> ())
                             | _ -> ())
                            // The armed pick's cursor preview (metric world —
                            // no mesh pose re-apply), synchronized with the
                            // main 3D: the same model state draws everywhere.
                            (match model.ArmedPick.GetValue t, model.ArmPreview.GetValue t with
                             | Some target, Some world ->
                                let cR = toRender world
                                let white = V4d(1.0, 1.0, 1.0, 0.9)
                                (match target with
                                 | ArmCentre ->
                                    let r = MeshView.armCommitRadiusAt model t
                                    let rR = ScanPin.renderLength scale r
                                    for seg in PinGeometry.buildSphereOutline cR rR (V4d(1.0, 1.0, 1.0, 0.7)) 1.4 do
                                        out.Add seg
                                    addCross out cR (rR * 0.15) white 1.6
                                 | ArmPoint _ ->
                                    addWireSphere out cR 0.06 white 1.6 20
                                    addCross out cR 0.075 white 1.6)
                             | _ -> ())
                            out.ToArray()
                        | _ -> [||])
                sg {
                    Sg.Active marksOn
                    Sg.Pass RenderPass.passOne
                    Sg.DepthTest (AVal.constant DepthTest.None)
                    Sg.BlendMode (AVal.constant BlendMode.Blend)
                    Sg.NoEvents
                    Lines.render segs
                }

                // Correspondence crosshairs — the 3D's screen-constant
                // triplex locator; 0.025 × the camera radius is the ortho
                // analogue of the eye distance. Reads the radius alone, so a
                // pan never rebuilds the glyphs; passTwo puts them
                // deterministically over the area/reveal lines.
                let camRadius = camA |> AVal.map (fun c -> c.Radius)
                let meshColV3 = (idxVal, isRoot) ||> AVal.map2 (fun i r -> c4bToV3d (meshColorRoot r i))
                let crossSegs =
                    AVal.custom (fun t ->
                        let sel = model.Sel.GetValue t
                        match sel.Pair with
                        | Some (pa, pb) when name = pa || name = pb ->
                            let pair = (pa, pb)
                            let pins = pinsVal.GetValue t
                            let armDim = armDimA.GetValue t
                            let h = 0.025 * camRadius.GetValue t
                            let col = meshColV3.GetValue t
                            let out = ResizeArray<V3d * V3d * V4d * float>()
                            let pt (dim : float) (local : V3d) =
                                ScanPinScene.addCrosshairGlyph out (renderOf t local) V3d.IOO V3d.OIO h col dim
                            for (id, p) in HashMap.toSeq pins do
                                if p.Pair = pair then
                                    pt (armDim * ScanPinScene.pinScopeDim model t id)
                                       (if name = fst pair then p.PointA else p.PointB)
                            (match model.ScanPins.Placement.GetValue t with
                             | PlacementActive d when name = fst d.Pair || name = snd d.Pair ->
                                (if name = fst d.Pair then d.PointA else d.PointB)
                                |> Option.iter (pt armDim)
                             | _ -> ())
                            out.ToArray()
                        | _ -> [||])
                sg {
                    Sg.Active marksOn
                    Sg.Pass RenderPass.passTwo
                    Sg.DepthTest (AVal.constant DepthTest.None)
                    Sg.BlendMode (AVal.constant BlendMode.Blend)
                    Sg.NoEvents
                    Lines.render crossSegs
                }
            }
        }

    // The strip's left-edge width-resize handle — pure DOM (layout chrome,
    // not model state); the render controls track their client size, so the
    // tiles reflow for free.
    let private stripResizeHandle (title : string) =
        div {
            Class "tiles-handle"
            Attribute("title", title)
            OnBoot [
                "(function(){"
                "var h=__THIS__; var p=h.parentElement;"
                "h.addEventListener('pointerdown',function(e){"
                "  e.preventDefault(); h.setPointerCapture(e.pointerId);"
                "  function mv(ev){ var w=Math.max(160,Math.min(600,window.innerWidth-ev.clientX)); p.style.width=w+'px'; }"
                "  function up(){ h.removeEventListener('pointermove',mv); h.removeEventListener('pointerup',up); }"
                "  h.addEventListener('pointermove',mv); h.addEventListener('pointerup',up); });"
                "})();"
            ]
        }

    // The tile strip: one top-down tile per mesh down the right edge, mounted
    // ONCE per dataset and present at EVERY level — Matrix shows all meshes
    // (default-framed on the reference root, comparable small multiples),
    // Pair/Pin narrow to the selected pair's two. Off-scope tiles hide via
    // visibility + absolute positioning — never display:none (a collapsed
    // render control loses its viewport) — and their scenes are
    // Sg.Active-gated, so hidden tiles cost ~nothing per frame.
    let tileStrip (env : Env<Message>) (model : AdaptiveModel) =
        div {
            Class "mesh-tiles"
            stripResizeHandle "Drag to resize the tile strip"
            model.MeshNames |> AList.map (meshTile env model)
        }
