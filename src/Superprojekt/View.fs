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
        let hoverCoord      = cval<V3d option> None
        let viewportSize    = cval (V2i(1, 1))
        let placementHover  = cval<V3d option> None
        // Hard-prohibit state at the placement hover: Some false = < 2
        // meshes in range (ghost fades, tooltip shows, click refuses); None =
        // unknown / no hover.
        let placementValid  = cval<bool option> None
        let cursorScreen    = cval<V2d option> None
        // Throttle/generation guard for the placement-hover ghost raycast (server
        // round-trip per move would flood; stale results must not overwrite).
        let mutable placeHoverMs  = 0.0
        let mutable placeHoverGen = 0

        let fullscreenActive = spaceHeld :> aval<bool>

        // Mesh isolation for the main view — every other mesh ghosts while set.
        let wheelIsolation =
            AVal.custom (fun t ->
                // The armed correspondence editor isolates its target mesh (solid; the
                // rest drop to ghost) so the GPU pick lands on it alone.
                match model.CorrArm.GetValue t with
                | Some (_, mesh) -> Some mesh
                | None ->
                    match model.Selection.Hovered.GetValue t with
                    | Some (HoverMesh m) -> Some m
                    | Some (HoverPoint (_, m)) -> Some m
                    | _ -> None)

        // Section/cutaway clipping was removed; the mesh shader keeps generic
        // clip-plane support but is fed a constant no-clip.
        let clipUniforms : aval<int * V4f * V4f> = AVal.constant (0, V4f.Zero, V4f.Zero)

        // Shown = clickable: the raycast candidate set mirrors what renders solid or
        // could be revealed (the solo overlay), evaluated at event time.
        let shownNow () =
            let solo = AVal.force model.MeshSolo
            fun (name : string) -> MeshVisibility.shown solo name

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
                        Frustum.perspective 90.0 1.0 5000.0 (float s.X / float s.Y) |> Frustum.projTrafo)

                // Cursor → nearest mesh surface via server raycast, ghost-agnostic
                // (the server just intersects geometry, ignoring the GPU ghost).
                // Bbox-culls shown+loaded meshes, raycasts the survivors in
                // parallel, returns the mesh name + render-space hit nearest the
                // camera + the hit triangle (for the exact-point probe).
                let raycastNearestNamed () : Async<(string * V3d * int) option> =
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
                                                Vec.dot (rp - ray.Origin) ray.Direction, name, rp, h.triangleId)
                                        })
                                    |> Async.Parallel
                                return
                                    hits |> Array.choose id |> Array.sortBy (fun (d, _, _, _) -> d)
                                    |> Array.tryHead |> Option.map (fun (_, n, rp, tri) -> n, rp, tri)
                            }

                let raycastNearest () : Async<V3d option> =
                    async {
                        let! hit = raycastNearestNamed ()
                        return hit |> Option.map (fun (_, rp, _) -> rp)
                    }

                // Exact-point probe (Inspect): the hit triangle's nearest corner
                // vertex indexes the mesh's per-vertex difference field — the
                // stored value AT that surface point, no server round trip.
                let probeValueAt (mesh : string) (renderPos : V3d) (triId : int) : float option =
                    match Map.tryFind mesh (AVal.force model.FocusDist) with
                    | None -> None
                    | Some arr ->
                        let lm = MeshView.loadMeshAsync (fun () -> ()) mesh
                        match lm.mesh.Value with
                        | Some md when triId >= 0 && triId * 3 + 2 < md.indices.Length ->
                            let cc = AVal.force model.CommonCentroid
                            let scale = DatasetScale.forMesh (AVal.force model.DatasetScales) mesh
                            let dispWorld = RigidTransform.renderToWorld scale cc (AVal.force (MeshView.displayedMeshT model mesh))
                            let own = dispWorld.Backward.TransformPos (ScanPin.worldCentre cc scale renderPos)
                            let local = V3f (own - md.centroid)
                            let mutable best = -1
                            let mutable bestD = System.Single.MaxValue
                            for k in 0 .. 2 do
                                let vi = md.indices.[triId * 3 + k]
                                if vi >= 0 && vi < md.positions.Length then
                                    let d = (md.positions.[vi] - local).LengthSquared
                                    if d < bestD then
                                        bestD <- d
                                        best <- vi
                            if best >= 0 && best < arr.Length && abs arr.[best] < 1e20f
                            then Some (float arr.[best])
                            else None
                        | _ -> None

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

                // Solid GPU pixel pick first, then fall through a ghost to the
                // nearest raycast surface. Used by pin placement and by
                // double-tap-to-recenter, so both work on ghosted meshes too.
                let resolvePick (frontmost : V3d option) : Async<V3d option> =
                    match frontmost with
                    | Some _ -> async.Return frontmost
                    | None -> raycastNearest ()

                // Hard-prohibit: a pin may only be placed where ≥ 2 meshes
                // have surface within the placement radius (the pin sphere).
                // Closest-point fan-out at each mesh's displayed pose (rigid, so
                // the returned distance is metric).
                let countOverlap (world : V3d) : Async<int> =
                    let names = AVal.force model.MeshNames.Content |> IndexList.toList
                    let cc = AVal.force model.CommonCentroid
                    let scales = AVal.force model.DatasetScales
                    let radius = max 0.01 (AVal.force model.QuickPinRadius)
                    let r2 = radius * radius
                    async {
                        let! hits =
                            names
                            |> List.map (fun n -> async {
                                try
                                    let scale = DatasetScale.forMesh scales n
                                    let dispWorld = RigidTransform.renderToWorld scale cc (AVal.force (MeshView.displayedMeshT model n))
                                    let own = dispWorld.Backward.TransformPos world
                                    let! r = Query.closestPoint ApiConfig.apiBase.Value n 0 own
                                    return
                                        match r with
                                        | Some h -> if float h.distanceSquared <= r2 then 1 else 0
                                        | None -> 0
                                with _ -> return 0 })
                            |> Async.Parallel
                        return Array.sum hits
                    }

                Sg.View view
                Sg.Proj proj

                Sg.Pass RenderPass.passZero

                Dom.OnPointerMove(fun e ->
                    let cursorPx = V2d(float e.OffsetPosition.X, float e.OffsetPosition.Y)
                    if cursorScreen.Value <> Some cursorPx then
                        transact (fun () -> cursorScreen.Value <- Some cursorPx)
                )

                // Clear hoverCoord on leave, else the top-bar readout keeps a
                // stale last-on-canvas coordinate over the HTML overlays.
                Dom.OnMouseLeave(fun _ ->
                    if hoverCoord.Value.IsSome then
                        transact (fun () -> hoverCoord.Value <- None)
                )

                Dom.OnMouseWheel(fun e ->
                    let delta = V2d(e.DeltaX, e.DeltaY) / 120.0
                    env.Emit [CameraMessage (OrbitMessage.Wheel delta)]
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
                            env.Emit [CameraMessage (OrbitMessage.SetTargetCenter(AnimationKind.Tanh, renderPos))]
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
                                | _ -> async.Return frontmost
                            match placement, resolved with
                            | AnchorPlacement, Some renderPos ->
                                // Hard-prohibit: verify overlap at the actual click
                                // point (authoritative — not the hover cache).
                                let worldPos = worldFromRender model renderPos
                                let! n = countOverlap worldPos
                                if n >= 2 then env.Emit [ScanPinMsg (PlaceAnchor worldPos)]
                                else env.Emit [ShowToast "No overlapping meshes here — placement needs ≥2 scans in range"]
                            | AnchorPlacement, None -> ()
                            | _, Some renderPos ->
                                // Mesh-surface clicks do NOT select — mesh selection
                                // and visibility live in the 2D GUI (roster, matrix,
                                // tiles). The click feeds the coordinate readout and,
                                // in Inspect, probes the exact point's error value.
                                let worldPos = worldFromRender model renderPos
                                transact (fun () -> hoverCoord.Value <- Some worldPos)
                                if AVal.force model.WorkflowStep = Inspect then
                                    let! named = raycastNearestNamed ()
                                    match named with
                                    | Some (mesh, rp, tri) ->
                                        match probeValueAt mesh rp tri with
                                        | Some v ->
                                            env.Emit [SetPointProbe (Some (mesh, worldFromRender model rp, v))]
                                        | None -> env.Emit [SetPointProbe None]
                                    | None -> ()
                            | _, None ->
                                // Depth 1.0 can also be a ghosted surface — raycast to
                                // distinguish a genuine background miss (clears the
                                // selection) from a click on ghosted terrain (nothing).
                                let! hit = raycastNearest ()
                                if hit.IsNone then env.Emit [SetSelection SelNone; SetPointProbe None]
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
                                // Overlap validity at the hover: drives the ghost fade
                                // + the cursor-side prohibit tooltip.
                                match hit with
                                | Some renderPos when gen = placeHoverGen ->
                                    let! n = countOverlap (worldFromRender model renderPos)
                                    if gen = placeHoverGen && placementValid.Value <> Some (n >= 2) then
                                        transact (fun () -> placementValid.Value <- Some (n >= 2))
                                | _ ->
                                    if gen = placeHoverGen && placementValid.Value.IsSome then
                                        transact (fun () -> placementValid.Value <- None)
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

                SceneGraph.build env info view proj fullscreenActive (placementHover :> aval<_>) (placementValid :> aval<_>) (viewportSize :> aval<V2i>) clipUniforms wheelIsolation model
            }

            Dom.OnKeyDown(fun e ->
                match e.Key with
                | " " ->
                    transact (fun () -> spaceHeld.Value <- true)
                | "i" | "I" ->
                    // Hold-I = registration peek (the reducer gates on a solve existing).
                    env.Emit [SetRegPeek true]
                | "Escape" ->
                    // Global cancel: disarm a placement, clear the brush, the
                    // point probe and the selection — clearing the selection also
                    // disarms the edit-point editor (arming forces a cell
                    // selection, so an armed editor always has one to clear).
                    // All no-ops when idle.
                    env.Emit [ScanPinMsg CancelPlacement; SetBrushedSamples []
                              SetPointProbe None; SetSelection SelNone]
                | _ -> ()
            )
            Dom.OnKeyUp(fun e ->
                match e.Key with
                | " "     -> transact (fun () -> spaceHeld.Value <- false)
                | "i" | "I" -> env.Emit [SetRegPeek false]
                | _ -> ()
            )

            GuiTopBar.topBar env model (hoverCoord :> aval<V3d option>)
            GuiRail.rail env model
            GuiFocus.panel env model
            GuiOverlays.toast model
            GuiOverlays.placementTooltip model (cursorScreen :> aval<_>) (placementValid :> aval<_>)
            GuiOverlays.corrFlash model (viewportSize :> aval<V2i>)
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
