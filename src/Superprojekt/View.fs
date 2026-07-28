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
        let cursorScreen    = cval<V2d option> None
        // Throttle/generation guard for the placement-hover ghost raycast (server
        // round-trip per move would flood; stale results must not overwrite).
        let mutable placeHoverMs  = 0.0
        let mutable placeHoverGen = 0
        // Sample-hover throttle (3D → diagram cross-highlight + exact readout).
        let mutable sampleHoverMs = 0.0

        let fullscreenActive = spaceHeld :> aval<bool>

        // Section/cutaway clipping was removed; the mesh shader keeps generic
        // clip-plane support but is fed a constant no-clip.
        let clipUniforms : aval<int * V4f * V4f> = AVal.constant (0, V4f.Zero, V4f.Zero)

        // Shown = clickable: the raycast candidate set mirrors what renders
        // solid (the nav scope + the Setup isolate + the matrix hover),
        // evaluated at event time.
        let shownNow () =
            let nav = AVal.force model.Nav
            let iso =
                match AVal.force model.SetupIsolateHover with
                | Some _ as h -> h
                | None -> AVal.force model.SetupIsolate
            let hp = AVal.force model.MatrixHoverPair
            fun (name : string) -> MeshVisibility.shown nav iso hp name

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
                    (model.ScanPins.Placement, model.ScanPins.Edit) ||> AVal.map2 (fun pl ed ->
                        (match pl with PlacementActive _ -> true | PlacementIdle -> false)
                        || (match ed with EditPoint _ -> true | EditIdle -> false))
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
                    if viewportSize.Value <> s then
                        transact (fun () -> viewportSize.Value <- s)
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
                // camera + the hit triangle.
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

                // Cursor → a SPECIFIC mesh's surface (render-space hit) — the
                // point-edit pick must land on its target mesh alone.
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

                // A picked render position → the hit mesh's OWN frame (through its
                // displayed pose — the surface the user clicked).
                let ownLocalOf (mesh : string) (renderPos : V3d) =
                    let cc = AVal.force model.CommonCentroid
                    let scale = DatasetScale.forMesh (AVal.force model.DatasetScales) mesh
                    let dispWorld = RigidTransform.renderToWorld scale cc (AVal.force (MeshView.displayedMeshT model mesh))
                    dispWorld.Backward.TransformPos (ScanPin.worldCentre cc scale renderPos)

                // Exact pairwise error at a metric-world point (the P0
                // exact-point endpoint), oriented MOV-relative-to-REF like every
                // stored sample; k runs on the landed value.
                let exactPairValueAt (world : V3d) (radius : float) (k : float -> unit) =
                    match AVal.force model.Nav with
                    | NavHome -> ()
                    | NavCell(a, b) ->
                        let g = AVal.force model.RegGraph
                        let ka, kb = PairCell.key a b
                        let _, mov = MatrixNav.pairRefMov g ka kb
                        let flip = mov = ka
                        let tOf (m : string) =
                            let cc = AVal.force model.CommonCentroid
                            let scale = DatasetScale.forMesh (AVal.force model.DatasetScales) m
                            (RigidTransform.renderToWorld scale cc (AVal.force (MeshView.displayedMeshT model m))).Forward
                        async {
                            let! v = Query.pairErrorAt ApiConfig.apiBase.Value ka (tOf ka) kb (tOf kb) world radius
                            match v with
                            | Some v -> k (if flip then -v else v)
                            | None -> ()
                        } |> Async.Start

                Sg.OnTap(fun _ ->
                    let placement = AVal.force model.ScanPins.Placement
                    let edit = AVal.force model.ScanPins.Edit
                    async {
                        match placement, edit with
                        | PlacementActive(tool, _), _ ->
                            // Transaction picks resolve on the server raycast of the
                            // pair's shown meshes — the GPU pixel pick is unusable
                            // under the ghosted/overlaid transaction rendering. The
                            // hit mesh attributes the pick (area anchor / point slot).
                            let! named = raycastNearestNamed ()
                            match named with
                            | Some (mesh, rp, _) ->
                                let local = ownLocalOf mesh rp
                                match tool with
                                | ToolArea -> env.Emit [ScanPinMsg (DraftAreaAt(mesh, local))]
                                | ToolPoint -> env.Emit [ScanPinMsg (DraftPointAt(mesh, local))]
                            | None -> ()
                        | PlacementIdle, EditPoint(id, mesh) ->
                            // The edit pick must land on the TARGET mesh alone.
                            let! hit = raycastMesh mesh
                            match hit with
                            | Some rp -> env.Emit [ScanPinMsg (EditPointAt(id, mesh, ownLocalOf mesh rp))]
                            | None -> ()
                        | PlacementIdle, EditIdle ->
                            // Armed point probe: pick any 3D point → exact value
                            // into the transient workspace readout.
                            if AVal.force model.ProbeArmed then
                                let! hit = raycastNearest ()
                                match hit with
                                | Some rp ->
                                    let world = worldFromRender model rp
                                    let gen = UpdateHelpers.cellErrorGen
                                    let radius = max 0.01 (AVal.force model.QuickPinRadius)
                                    exactPairValueAt world radius (fun v ->
                                        env.Emit [ProbeReadoutComputed(gen, world, v)])
                                | None -> ()
                    } |> Async.Start
                    true
                )

                Sg.OnPointerMove(fun e ->
                    // The flashlight ghost previews the AREA drop only.
                    let placementWanted =
                        match AVal.force model.ScanPins.Placement with
                        | PlacementActive(ToolArea, _) -> true
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
                    // 3D → diagram: hover the nearest brushed sample (screen
                    // space, ≤ 12 px) → cross-highlight + exact-value readout.
                    // Throttled; the exact value fetches only on a gid change.
                    let brush = AVal.force model.BrushedSamples
                    if not (Set.isEmpty brush) && not placementWanted then
                        let now = nowMs ()
                        if now - sampleHoverMs > 80.0 then
                            sampleHoverMs <- now
                            match AVal.force model.CellError, cursorScreen.Value with
                            | Some cells, Some cur ->
                                let cc = AVal.force model.CommonCentroid
                                let scale = DatasetScale.active (AVal.force model.ActiveDataset) (AVal.force model.DatasetScales)
                                let vp = (AVal.force view) * (AVal.force proj)
                                let sizePx = AVal.force overlaySize
                                let toScreen (w : V3d) =
                                    let ndc = vp.Forward.TransformPosProj (ScanPin.renderCentre cc scale w)
                                    V2d(0.5 * (ndc.X + 1.0) * float sizePx.X, 0.5 * (1.0 - ndc.Y) * float sizePx.Y)
                                let mutable best = -1
                                let mutable bestD = 12.0
                                let mutable bestPin = None
                                let mutable gid = 0
                                for (pid, r) in cells do
                                    for i in 0 .. r.Samples.Length - 1 do
                                        if Set.contains gid brush && i < r.Positions.Length then
                                            let d = Vec.distance (toScreen r.Positions.[i]) cur
                                            if d < bestD then
                                                bestD <- d
                                                best <- gid
                                                bestPin <- Some (pid, r.Positions.[i])
                                        gid <- gid + 1
                                let cur = AVal.force model.HoverSample
                                if best >= 0 then
                                    if cur <> Some best then
                                        env.Emit [SetHoverSample (Some best)]
                                        match bestPin with
                                        | Some (pid, pos) ->
                                            let radius =
                                                HashMap.tryFind pid (AVal.force (model.ScanPins.Pins |> AMap.toAVal))
                                                |> Option.map (fun p -> p.InnerRadius)
                                                |> Option.defaultValue (AVal.force model.QuickPinRadius)
                                            let gen = UpdateHelpers.cellErrorGen
                                            exactPairValueAt pos radius (fun v ->
                                                env.Emit [HoverReadoutComputed(gen, best, v)])
                                        | None -> ()
                                elif cur.IsSome then
                                    env.Emit [SetHoverSample None]
                            | _ -> ()
                    true
                )

                SceneGraph.build env info view proj fullscreenActive (placementHover :> aval<_>) clipUniforms model
            }

            Dom.OnKeyDown(fun e ->
                match e.Key with
                | " " ->
                    transact (fun () -> spaceHeld.Value <- true)
                // The two spring-loaded blink keys (cell scope; the reducer
                // refuses a press unless both pair meshes are resident). Key
                // repeat is absorbed by the reducer's idempotence guard.
                | "v" | "V" -> env.Emit [SetPeekVis true]
                | "b" | "B" -> env.Emit [SetPeekPose true]
                | "Escape" ->
                    // ONE Esc: the innermost in-progress action cancels first —
                    // the blocking loop modal (cancel = discard the redundant
                    // edge) > transaction abort (full rollback) > point-edit
                    // disarm > probe disarm > ascend one level (cell → matrix).
                    if (AVal.force model.LoopPending).IsSome then
                        env.Emit [CancelLoopResolution]
                    else
                        match AVal.force model.ScanPins.Placement, AVal.force model.ScanPins.Edit with
                        | PlacementActive _, _ -> env.Emit [ScanPinMsg AbortPinTransaction]
                        | _, EditPoint _ -> env.Emit [ScanPinMsg CancelPointEdit]
                        | _ when AVal.force model.ProbeArmed -> env.Emit [ToggleProbeArmed]
                        | _ -> env.Emit [NavAscend]
                | _ -> ()
            )
            Dom.OnKeyUp(fun e ->
                match e.Key with
                | " "     -> transact (fun () -> spaceHeld.Value <- false)
                | "v" | "V" -> env.Emit [SetPeekVis false]
                | "b" | "B" -> env.Emit [SetPeekPose false]
                | _ -> ()
            )

            GuiTopBar.topBar env model (hoverCoord :> aval<V3d option>)
            GuiRail.rail env model
            GuiOverlays.toast model
            GuiOverlays.scaleBar model (viewportSize :> aval<V2i>)
            GuiOverlays.colorLegend model
            GuiOverlays.orientationIndicator model
            GuiOverlays.loopModal env model
        }

module App =
    let app =
        {
            initial   = Model.initial
            update    = Update.update
            view      = View.view
            unpersist = Unpersist.instance
        }
