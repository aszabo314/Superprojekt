namespace Superprojekt

open Aardvark.Base
open Aardvark.Dom.Utilities.OrbitController
open Aardvark.Rendering
open FSharp.Data.Adaptive
open Aardvark.Dom
open Adaptify
open Superprojekt

module View =

    let private rayBoxT (ray : Ray3d) (box : Box3d) : float option =
        let inv = V3d(1.0 / ray.Direction.X, 1.0 / ray.Direction.Y, 1.0 / ray.Direction.Z)
        let tx1 = (box.Min.X - ray.Origin.X) * inv.X
        let tx2 = (box.Max.X - ray.Origin.X) * inv.X
        let ty1 = (box.Min.Y - ray.Origin.Y) * inv.Y
        let ty2 = (box.Max.Y - ray.Origin.Y) * inv.Y
        let tz1 = (box.Min.Z - ray.Origin.Z) * inv.Z
        let tz2 = (box.Max.Z - ray.Origin.Z) * inv.Z
        let tmin = max (max (min tx1 tx2) (min ty1 ty2)) (min tz1 tz2)
        let tmax = min (min (max tx1 tx2) (max ty1 ty2)) (max tz1 tz2)
        if tmax < 0.0 || tmin > tmax then None
        else Some (max tmin 0.0)

    let private pickRay (cursorPx : V2d) (vpSize : V2i) (viewTrafo : Trafo3d) (projTrafo : Trafo3d) =
        let ndc = V2d(2.0 * cursorPx.X / float vpSize.X - 1.0,
                      1.0 - 2.0 * cursorPx.Y / float vpSize.Y)
        let vp = viewTrafo * projTrafo
        let p0 = vp.Backward.TransformPosProj(V3d(ndc, -1.0))
        let p1 = vp.Backward.TransformPosProj(V3d(ndc, 1.0))
        Ray3d(p0, (p1 - p0) |> Vec.normalize)

    let private nowMs () = float System.DateTime.UtcNow.Ticks / 10000.0

    // Cursor ray ∩ the render-space Z=0 plane. Render Z=0 ⟺ world Z = CommonCentroid.Z,
    // so this is a horizontal plane at the dataset's mean elevation — the readout
    // fallback when the ray misses every mesh.
    let private rayPlaneZ0 (ray : Ray3d) : V3d option =
        if abs ray.Direction.Z < 1e-6 then None
        else
            let t = -ray.Origin.Z / ray.Direction.Z
            if t > 0.0 then Some (ray.Origin + t * ray.Direction) else None

    let private worldFromRender (model : AdaptiveModel) (renderPos : V3d) =
        let scale = DatasetScale.active (AVal.force model.ActiveDataset) (AVal.force model.DatasetScales)
        ScanPin.worldCentre (AVal.force model.CommonCentroid) scale renderPos

    let private renderBox (worldBox : Box3d) (cc : V3d) (scale : float) =
        let lo = ScanPin.renderCentre cc scale worldBox.Min
        let hi = ScanPin.renderCentre cc scale worldBox.Max
        Box3d(V3d(min lo.X hi.X, min lo.Y hi.Y, min lo.Z hi.Z),
              V3d(max lo.X hi.X, max lo.Y hi.Y, max lo.Z hi.Z))

    let view (env : Env<Message>) (model : AdaptiveModel) =

        ServerActions.init env

        let spaceHeld       = cval false
        let altHeld         = cval false
        let hoverCoord      = cval<V3d option> None
        let viewportSize    = cval (V2i(1, 1))
        let placementHover  = cval<V3d option> None
        let cursorScreen    = cval<V2d option> None
        // Throttle/generation guard for the placement-hover ghost raycast (server
        // round-trip per move would flood; stale results must not overwrite).
        let mutable placeHoverMs  = 0.0
        let mutable placeHoverGen = 0

        let fullscreenActive = spaceHeld :> aval<bool>

        // Mesh isolation for the main view (hover = peek): Alt-held layer
        // isolation (wheel-cycled), else the hovered mesh from the shared
        // Selection. Ghosts the rest while held.
        let wheelIsolation =
            AVal.custom (fun t ->
                // The armed correspondence editor isolates its target mesh (solid; the
                // rest drop to ghost) so the GPU pick lands on it alone.
                match model.CorrArm.GetValue t with
                | Some (_, mesh) -> Some mesh
                | None ->
                    if altHeld.GetValue t then model.ActivePickingLayer.GetValue t
                    else
                        match model.Selection.Hovered.GetValue t with
                        | Some (HoverMesh m) -> Some m
                        | Some (HoverPoint (_, m)) -> Some m
                        | _ -> None)

        // Section/cutaway clipping was removed; the mesh shader keeps generic
        // clip-plane support but is fed a constant no-clip.
        let clipUniforms : aval<int * V4f * V4f> = AVal.constant (0, V4f.Zero, V4f.Zero)

        // Shown = clickable: the raycast candidate set mirrors what renders solid or
        // could be revealed (per-mesh toggles + the solo overlay), evaluated at event time.
        let shownNow () =
            let solo = AVal.force model.MeshSolo
            let vis = AVal.force model.MeshVisible
            fun (name : string) -> MeshVisibility.shown solo vis name

        body {
            OnBoot [
                "const l = document.getElementById('loader');"
                "if(l) l.remove();"
                "document.body.classList.add('loaded');"
            ]

            renderControl {
                RenderControl.Samples 1
                Class "render-control"

                let pickModeOn =
                    model.ScanPins.Placement |> AVal.map (function
                        | PlacementIdle -> false
                        | _ -> true)
                pickModeOn |> AVal.map (fun pick ->
                    if pick then Some (Dom.Style [Css.Cursor "crosshair"]) else None)

                let! info = RenderControl.Info
                let! size = RenderControl.ViewportSize
                let! client = RenderControl.ClientSize

                // CSS-pixel size for anything mixing with DOM coords (cursor,
                // overlay placement). ViewportSize is framebuffer px (CSS ×
                // devicePixelRatio) → pushes cards off-screen on hi-dpi; use
                // ClientSize, falling back to framebuffer until the first DOM
                // event (ClientSize is V2i.II until then).
                let overlaySize =
                    (client, size) ||> AVal.map2 (fun c v ->
                        if c.X > 1 && c.Y > 1 then c else v)

                OrbitController.getAttributes (Env.map CameraMessage env)

                RenderControl.OnRendered(fun _ ->
                    let s = AVal.force overlaySize
                    let fb = AVal.force size
                    // Share the device-pixel-ratio with the focus panel (its secondary
                    // control can't bind ClientSize). overlaySize is CSS px, size is fb px.
                    let d = if s.X > 0 then float fb.X / float s.X else 1.0
                    if viewportSize.Value <> s || FocusScene.dpr.Value <> d then
                        transact (fun () ->
                            viewportSize.Value <- s
                            FocusScene.dpr.Value <- d)
                    env.Emit [CameraMessage OrbitMessage.Rendered]
                )

                let view = model.Camera.view |> AVal.map CameraView.viewTrafo
                let proj =
                    size |> AVal.map (fun s ->
                        Frustum.perspective 90.0 1.0 5000.0 (float s.X / float s.Y) |> Frustum.projTrafo
                    )

                // With ActivePickingLayer set, prefer that layer's surface over
                // the frontmost — but only if the cursor ray hits the layer's
                // bbox; else fall back to the GPU frontmost pick. Async because
                // the layer-specific raycast goes through the server.
                let resolveLayerPick (frontmost : V3d option) : Async<V3d option> =
                    let activeLayer = AVal.force model.ActivePickingLayer
                    match activeLayer, cursorScreen.Value with
                    | None, _ -> async.Return frontmost
                    | Some _, None -> async.Return frontmost
                    | Some layer, Some cursorPx ->
                        let bounds = AVal.force model.MeshBounds
                        match Map.tryFind layer bounds with
                        | None -> async.Return frontmost
                        | Some worldBox ->
                            let cc = AVal.force model.CommonCentroid
                            let scale = DatasetScale.forMesh (AVal.force model.DatasetScales) layer
                            let vpSize = AVal.force overlaySize
                            let v = AVal.force view
                            let p = AVal.force proj
                            let ray = pickRay cursorPx vpSize v p
                            match rayBoxT ray (renderBox worldBox cc scale) with
                            | None -> async.Return frontmost
                            | Some _ ->
                                async {
                                    // Un-apply the layer's displayed (before/after) pose so the
                                    // server raycast meets its load-pose geometry, then map the hit
                                    // back through the same pose. Render → metric world → server
                                    // frame, one step each (same convention as the focus pick).
                                    let dispWorld = RigidTransform.renderToWorld scale cc (AVal.force (MeshView.displayedMeshT model layer))
                                    let originW = ScanPin.worldCentre cc scale ray.Origin
                                    let serverOrigin = dispWorld.Backward.TransformPos originW
                                    let serverDir = (dispWorld.Backward.TransformDir ray.Direction).Normalized
                                    let! hit = Query.rayHit ApiConfig.apiBase.Value layer 0 serverOrigin serverDir
                                    match hit with
                                    | Some h -> return Some (ScanPin.renderCentre cc scale (dispWorld.Forward.TransformPos h.point))
                                    | None -> return frontmost
                                }

                // Cursor → nearest mesh surface via server raycast, ghost-agnostic
                // (the server just intersects geometry, ignoring the GPU ghost).
                // Bbox-culls visible+loaded meshes, raycasts the survivors in
                // parallel, returns the render-space hit nearest the camera — the
                // first surface the ray crosses wins, mesh and coordinate together.
                let raycastNearest () : Async<V3d option> =
                    match cursorScreen.Value with
                    | None -> async.Return None
                    | Some cursorPx ->
                        let cc = AVal.force model.CommonCentroid
                        let scales = AVal.force model.DatasetScales
                        let shown = shownNow ()
                        let bounds = AVal.force model.MeshBounds
                        let ray = pickRay cursorPx (AVal.force overlaySize) (AVal.force view) (AVal.force proj)
                        let candidates =
                            bounds |> Map.toSeq
                            |> Seq.choose (fun (name, world) ->
                                if shown name then
                                    let scale = DatasetScale.forMesh scales name
                                    if (rayBoxT ray (renderBox world cc scale)).IsSome
                                    then Some (name, scale) else None
                                else None)
                            |> Array.ofSeq
                        if candidates.Length = 0 then async.Return None
                        else
                            async {
                                let! hits =
                                    candidates
                                    |> Array.map (fun (name, scale) ->
                                        async {
                                            let dispWorld = RigidTransform.renderToWorld scale cc (AVal.force (MeshView.displayedMeshT model name))
                                            let serverOrigin = dispWorld.Backward.TransformPos (ScanPin.worldCentre cc scale ray.Origin)
                                            let serverDir = (dispWorld.Backward.TransformDir ray.Direction).Normalized
                                            let! hit = Query.rayHit ApiConfig.apiBase.Value name 0 serverOrigin serverDir
                                            return hit |> Option.map (fun h ->
                                                let rp = ScanPin.renderCentre cc scale (dispWorld.Forward.TransformPos h.point)
                                                Vec.dot (rp - ray.Origin) ray.Direction, rp)
                                        })
                                    |> Async.Parallel
                                return
                                    hits |> Array.choose id |> Array.sortBy fst
                                    |> Array.tryHead |> Option.map snd
                            }

                // Like raycastNearest, but keeps the mesh NAME of the nearest hit —
                // used to focus (§B) / solo (§C) the clicked mesh in 3D. Bbox-culls
                // visible+loaded meshes, raycasts the survivors, takes the nearest.
                let raycastNearestNamed () : Async<(string * V3d) option> =
                    match cursorScreen.Value with
                    | None -> async.Return None
                    | Some cursorPx ->
                        let cc = AVal.force model.CommonCentroid
                        let scales = AVal.force model.DatasetScales
                        let shown = shownNow ()
                        let bounds = AVal.force model.MeshBounds
                        let ray = pickRay cursorPx (AVal.force overlaySize) (AVal.force view) (AVal.force proj)
                        let candidates =
                            bounds |> Map.toSeq
                            |> Seq.choose (fun (name, world) ->
                                if shown name then
                                    let scale = DatasetScale.forMesh scales name
                                    if (rayBoxT ray (renderBox world cc scale)).IsSome
                                    then Some (name, scale) else None
                                else None)
                            |> Array.ofSeq
                        if candidates.Length = 0 then async.Return None
                        else
                            async {
                                let! hits =
                                    candidates
                                    |> Array.map (fun (name, scale) ->
                                        async {
                                            let dispWorld = RigidTransform.renderToWorld scale cc (AVal.force (MeshView.displayedMeshT model name))
                                            let serverOrigin = dispWorld.Backward.TransformPos (ScanPin.worldCentre cc scale ray.Origin)
                                            let serverDir = (dispWorld.Backward.TransformDir ray.Direction).Normalized
                                            let! hit = Query.rayHit ApiConfig.apiBase.Value name 0 serverOrigin serverDir
                                            return hit |> Option.map (fun h ->
                                                let rp = ScanPin.renderCentre cc scale (dispWorld.Forward.TransformPos h.point)
                                                Vec.dot (rp - ray.Origin) ray.Direction, name, rp)
                                        })
                                    |> Async.Parallel
                                return
                                    hits |> Array.choose id |> Array.sortBy (fun (d, _, _) -> d)
                                    |> Array.tryHead |> Option.map (fun (_, n, rp) -> n, rp)
                            }

                // Cursor → a SPECIFIC mesh's surface via server raycast (render-space
                // hit). Used by the 3D correspondence pick, which isolates one mesh and
                // must land on it alone (ignoring whatever else the ray crosses).
                let raycastMesh (name : string) : Async<V3d option> =
                    match cursorScreen.Value with
                    | None -> async.Return None
                    | Some cursorPx ->
                        let cc = AVal.force model.CommonCentroid
                        let scale = DatasetScale.forMesh (AVal.force model.DatasetScales) name
                        let ray = pickRay cursorPx (AVal.force overlaySize) (AVal.force view) (AVal.force proj)
                        async {
                            let dispWorld = RigidTransform.renderToWorld scale cc (AVal.force (MeshView.displayedMeshT model name))
                            let serverOrigin = dispWorld.Backward.TransformPos (ScanPin.worldCentre cc scale ray.Origin)
                            let serverDir = (dispWorld.Backward.TransformDir ray.Direction).Normalized
                            let! hit = Query.rayHit ApiConfig.apiBase.Value name 0 serverOrigin serverDir
                            return hit |> Option.map (fun h -> ScanPin.renderCentre cc scale (dispWorld.Forward.TransformPos h.point))
                        }

                // Solid pixel pick (GPU / active layer) first, then fall through a
                // ghost to the nearest raycast surface. Used by pin placement and by
                // double-tap-to-recenter, so both work on ghosted meshes too.
                let resolvePick (frontmost : V3d option) : Async<V3d option> =
                    async {
                        let! r = resolveLayerPick frontmost
                        match r with
                        | Some _ -> return r
                        | None -> return! raycastNearest ()
                    }

                Sg.View view
                Sg.Proj proj

                Sg.Pass RenderPass.passZero

                Dom.OnPointerMove(fun e ->
                    let cursorPx = V2d(float e.OffsetPosition.X, float e.OffsetPosition.Y)
                    if cursorScreen.Value <> Some cursorPx then
                        transact (fun () -> cursorScreen.Value <- Some cursorPx)
                    // self-heal a missed Alt keyup (focus lost while held)
                    if altHeld.Value <> e.Alt then
                        transact (fun () -> altHeld.Value <- e.Alt)
                )

                // Clear hoverCoord on leave, else it keeps its last on-canvas
                // value over an HTML overlay and freezes a stale 3D→chart line.
                Dom.OnMouseLeave(fun _ ->
                    if hoverCoord.Value.IsSome then
                        transact (fun () -> hoverCoord.Value <- None)
                )

                Dom.OnMouseWheel(fun e ->
                    let delta = V2d(e.DeltaX, e.DeltaY) / 120.0
                    if not e.Alt then
                        env.Emit [CameraMessage (OrbitMessage.Wheel(false, delta))]
                    else
                        // Option/Alt + wheel = cycle the isolated layer. Prefer
                        // meshes stacked under the cursor; with fewer than two
                        // there, fall back to all visible meshes in panel order
                        // (under-cursor-only made the wheel feel dead).
                        if altHeld.Value <> true then
                            transact (fun () -> altHeld.Value <- true)
                        let cursorPx = V2d(float e.OffsetPosition.X, float e.OffsetPosition.Y)
                        let vpSize = AVal.force overlaySize
                        let v = AVal.force view
                        let p = AVal.force proj
                        let ray = pickRay cursorPx vpSize v p
                        let bounds = AVal.force model.MeshBounds
                        let cc = AVal.force model.CommonCentroid
                        let scales = AVal.force model.DatasetScales
                        let isVisible = shownNow ()
                        let hits =
                            bounds |> Map.toSeq
                            |> Seq.choose (fun (name, world) ->
                                if isVisible name then
                                    let scale = DatasetScale.forMesh scales name
                                    rayBoxT ray (renderBox world cc scale) |> Option.map (fun t -> t, name)
                                else None)
                            |> Seq.sortBy fst
                            |> Seq.map snd
                            |> Array.ofSeq
                        let candidates =
                            if hits.Length >= 2 then hits
                            else
                                AVal.force model.MeshNames.Content |> IndexList.toArray |> Array.filter isVisible
                        // While a one-shot anchor pick is live the cycle
                        // retargets it, skipping the reference (anchors never
                        // land on the reference).
                        if candidates.Length > 0 then
                            let cur = AVal.force model.ActivePickingLayer
                            let n = candidates.Length
                            let dir = if e.DeltaY > 0.0 then 1 else -1
                            let next =
                                match cur with
                                | None -> candidates.[if dir > 0 then 0 else n - 1]
                                | Some c ->
                                    match Array.tryFindIndex ((=) c) candidates with
                                    | Some i -> candidates.[((i + dir) % n + n) % n]
                                    | None -> candidates.[if dir > 0 then 0 else n - 1]
                            env.Emit [SetActivePickingLayer (Some next)]
                )

                Sg.OnDoubleTap(fun e ->
                    let frontmost =
                        if e.Location.Depth < 0.9999 then Some e.WorldPosition else None
                    async {
                        // Solid surface first, then fall through a ghost via raycast,
                        // so double-tap-to-recenter works on ghosted meshes too.
                        let! resolved = resolvePick frontmost
                        match resolved with
                        | Some renderPos ->
                            env.Emit [CameraMessage (OrbitMessage.SetTargetCenter(true, AnimationKind.Tanh, renderPos))]
                        | None -> ()
                    } |> Async.Start
                    false
                )

                Sg.OnTap(fun e ->
                    let frontmost =
                        if e.Location.Depth < 0.9999 then Some e.WorldPosition else None
                    match AVal.force model.CorrArm with
                    | Some (pinId, mesh) ->
                        // Commit the picked surface point as this mesh's anchor (the
                        // reducer stores it mesh-local and disarms the editor). GPU pick
                        // on the isolated solid first, else a single-mesh raycast.
                        async {
                            let! resolved =
                                match frontmost with
                                | Some _ -> async.Return frontmost
                                | None -> raycastMesh mesh
                            match resolved with
                            | Some renderPos ->
                                env.Emit [PickCorrespondenceAt(pinId, mesh, worldFromRender model renderPos)]
                            | None -> ()
                        } |> Async.Start
                        true
                    | None ->
                        let placement = AVal.force model.ScanPins.Placement
                        async {
                            let! resolved =
                                match placement with
                                // During placement the terrain is ghosted AND the
                                // translucent preview sphere writes depth in front of the
                                // real surface, so the GPU pixel pick (`frontmost`) lands
                                // ~QuickPinRadius toward the camera. Ignore it and resolve
                                // on the server raycast, which intersects only real
                                // geometry (the same surface the flashlight previews).
                                | AnchorPlacement -> resolvePick None
                                | _ -> resolveLayerPick frontmost
                            match placement, resolved with
                            | AnchorPlacement, Some renderPos ->
                                let worldPos = worldFromRender model renderPos
                                env.Emit [ScanPinMsg (PlaceAnchor worldPos)]
                            | AnchorPlacement, None -> ()
                            | _, Some renderPos ->
                                let worldPos = worldFromRender model renderPos
                                transact (fun () -> hoverCoord.Value <- Some worldPos)
                                // Clicking a mesh in 3D selects it (read/write parity
                                // §B); the reducer applies the Inspect auto-solo (§C).
                                // Select only — no main camera; double-tap recenters.
                                let! named = raycastNearestNamed ()
                                match named with
                                | Some (mesh, _) -> env.Emit [SetSelection (SelMesh mesh)]
                                | None -> ()
                            | _, None ->
                                // A background miss clears the selection.
                                env.Emit [SetSelection SelNone]
                        } |> Async.Start
                        true
                )

                Sg.OnPointerMove(fun e ->
                    let placementWanted =
                        match AVal.force model.ScanPins.Placement with
                        | AnchorPlacement -> true
                        | _ -> false
                    let pick =
                        if e.Location.Depth < 0.9999 then Some e.WorldPosition else None
                    // Readout fallback: when the cursor ray misses every mesh, drop it
                    // onto the render Z=0 plane (dataset mean elevation) so the top-bar
                    // coordinate keeps reading over open ground / off-mesh.
                    let nextHover =
                        // While placing, `pick` hits the translucent preview sphere (not
                        // the surface), so read the coordinate off the raycast-driven
                        // placement preview instead — consistent with where the pin lands.
                        if placementWanted then
                            placementHover.Value |> Option.map (worldFromRender model)
                        else
                            match pick with
                            | Some renderPos -> Some (worldFromRender model renderPos)
                            | None ->
                                match cursorScreen.Value with
                                | Some cursorPx ->
                                    pickRay cursorPx (AVal.force overlaySize) (AVal.force view) (AVal.force proj)
                                    |> rayPlaneZ0
                                    |> Option.map (worldFromRender model)
                                | None -> None
                    if hoverCoord.Value <> nextHover then
                        transact (fun () -> hoverCoord.Value <- nextHover)
                    // Placement preview (flashlight): drive it purely from the server
                    // raycast — the GPU pixel pick is unusable here (ghosted terrain +
                    // the preview sphere occluding the surface), and the raycast is the
                    // same surface the click commits. Throttled + generation-guarded;
                    // the last preview is held between hits (None on a true miss).
                    if placementWanted then
                        let now = nowMs ()
                        if now - placeHoverMs > 60.0 then
                            placeHoverMs <- now
                            placeHoverGen <- placeHoverGen + 1
                            let gen = placeHoverGen
                            async {
                                let! hit = resolvePick None
                                if gen = placeHoverGen && placementHover.Value <> hit then
                                    transact (fun () -> placementHover.Value <- hit)
                            } |> Async.Start
                    // Armed correspondence editor (3D side): the target mesh is
                    // isolated solid, so the GPU pick lands on it; over a ghost/
                    // background fall back to a single-mesh raycast. Throttled → bounded
                    // CorrPreview message rate.
                    match AVal.force model.CorrArm with
                    | Some (_, mesh) ->
                        let now = nowMs ()
                        if now - placeHoverMs > 60.0 then
                            placeHoverMs <- now
                            placeHoverGen <- placeHoverGen + 1
                            let gen = placeHoverGen
                            match pick with
                            | Some renderPos ->
                                env.Emit [CorrPreviewComputed (Some (worldFromRender model renderPos))]
                            | None ->
                                async {
                                    let! hit = raycastMesh mesh
                                    if gen = placeHoverGen then
                                        env.Emit [CorrPreviewComputed (hit |> Option.map (worldFromRender model))]
                                } |> Async.Start
                    | None -> ()
                    true
                )

                SceneGraph.build env info view proj fullscreenActive (placementHover :> aval<_>) clipUniforms wheelIsolation model
            }

            Dom.OnKeyDown(fun e ->
                match e.Key with
                | "Alt" ->
                    if not altHeld.Value then transact (fun () -> altHeld.Value <- true)
                | " " ->
                    transact (fun () -> spaceHeld.Value <- true)
                | "i" | "I" ->
                    // Hold-I = registration peek (same gate as the top-bar button:
                    // only once a solve exists).
                    if not (Map.isEmpty (AVal.force model.SolvedTransforms)) then
                        env.Emit [SetRegPeek true]
                | "o" | "O" ->
                    // Hold-O = show-overlays (white-out except pins).
                    env.Emit [SetShowOverlays true]
                | "Escape" ->
                    env.Emit [ScanPinMsg CancelPlacement]
                | _ -> ()
            )
            Dom.OnKeyUp(fun e ->
                match e.Key with
                | " "     -> transact (fun () -> spaceHeld.Value <- false)
                | "Alt"   -> transact (fun () -> altHeld.Value <- false)
                | "i" | "I" -> env.Emit [SetRegPeek false]
                | "o" | "O" -> env.Emit [SetShowOverlays false]
                | _ -> ()
            )

            GuiTopBar.topBar env model (hoverCoord :> aval<V3d option>)
            GuiRail.rail env model (viewportSize :> aval<V2i>)
            GuiFocus.panel env model
            GuiOverlays.toast model
            GuiOverlays.pinFlagLabels model (viewportSize :> aval<V2i>)
            GuiOverlays.meshWheelLabel model (cursorScreen :> aval<_>) (altHeld :> aval<bool>)
            GuiOverlays.scaleBar model (viewportSize :> aval<V2i>)
            GuiOverlays.colorLegend model
            GuiOverlays.orientationIndicator model
            GuiInspector.dock env model
        }

module App =
    let app =
        {
            initial   = Model.initial
            update    = Update.update
            view      = View.view
            unpersist = Unpersist.instance
        }
