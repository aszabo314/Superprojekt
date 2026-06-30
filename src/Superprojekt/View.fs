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

        let fullscreenActive = spaceHeld :> aval<bool>

        // Mesh isolation for the main view (hover = peek): Alt-held layer
        // isolation (wheel-cycled), else the hovered mesh from the shared
        // Selection. Ghosts the rest while held.
        let wheelIsolation =
            AVal.custom (fun t ->
                if altHeld.GetValue t then model.ActivePickingLayer.GetValue t
                else
                    match model.Selection.Hovered.GetValue t with
                    | Some (HoverMesh m) -> Some m
                    | Some (HoverPoint (_, m)) -> Some m
                    | _ -> None)

        // Section/cutaway clipping was removed; the mesh shader keeps generic
        // clip-plane support but is fed a constant no-clip.
        let clipUniforms : aval<int * V4f * V4f> = AVal.constant (0, V4f.Zero, V4f.Zero)

        body {
            OnBoot [
                "const l = document.getElementById('loader');"
                "if(l) l.remove();"
                "document.body.classList.add('loaded');"
                // Pulse outline for nav actions; delayed so just-opened targets are visible first.
                "window.SuperPulse = function(selector){"
                "  setTimeout(function(){"
                "    var el = document.querySelector(selector);"
                "    if(!el) return;"
                "    el.classList.remove('pulse-outline');"
                "    void el.offsetWidth;"
                "    el.classList.add('pulse-outline');"
                "    setTimeout(function(){ el.classList.remove('pulse-outline'); }, 1600);"
                "  }, 150);"
                "};"
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
                        let visible = AVal.force model.MeshVisible
                        let bounds = AVal.force model.MeshBounds
                        let cc = AVal.force model.CommonCentroid
                        let scales = AVal.force model.DatasetScales
                        let isVisible name = Map.tryFind name visible |> Option.defaultValue true
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
                        let! resolved = resolveLayerPick frontmost
                        match resolved with
                        | Some renderPos ->
                            env.Emit [CameraMessage (OrbitMessage.SetTargetCenter(true, AnimationKind.Tanh, renderPos))]
                        | None -> ()
                    } |> Async.Start
                    false
                )

                Sg.OnTap(fun e ->
                    let placement = AVal.force model.ScanPins.Placement
                    let frontmost =
                        if e.Location.Depth < 0.9999 then Some e.WorldPosition else None
                    async {
                        let! resolved = resolveLayerPick frontmost
                        match placement, resolved with
                        | AnchorPlacement, Some renderPos ->
                            let worldPos = worldFromRender model renderPos
                            env.Emit [ScanPinMsg (PlaceAnchor worldPos)]
                        | AnchorPlacement, None -> ()
                        | _, Some renderPos ->
                            let worldPos = worldFromRender model renderPos
                            transact (fun () -> hoverCoord.Value <- Some worldPos)
                        | _, None -> ()
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
                        match pick with
                        | Some renderPos -> Some (worldFromRender model renderPos)
                        | None ->
                            match cursorScreen.Value with
                            | Some cursorPx ->
                                pickRay cursorPx (AVal.force overlaySize) (AVal.force view) (AVal.force proj)
                                |> rayPlaneZ0
                                |> Option.map (worldFromRender model)
                            | None -> None
                    let nextPlacement = if placementWanted then pick else None
                    let needHover     = hoverCoord.Value     <> nextHover
                    let needPlacement = placementHover.Value <> nextPlacement
                    if needHover || needPlacement then
                        transact (fun () ->
                            if needHover     then hoverCoord.Value     <- nextHover
                            if needPlacement then placementHover.Value <- nextPlacement)
                    // Moving over terrain (off a constellation glyph) ends a
                    // point-hover brush — the glyph re-sets it while hovered.
                    match AVal.force model.Selection.Hovered with
                    | Some (HoverPoint _) -> env.Emit [SetHovered None]
                    | _ -> ()
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
                | "r" | "R" ->
                    // Hold-R reference peek.
                    env.Emit [SetReferencePeek true]
                | "Escape" ->
                    env.Emit [ScanPinMsg CancelPlacement]
                | _ -> ()
            )
            Dom.OnKeyUp(fun e ->
                match e.Key with
                | " "     -> transact (fun () -> spaceHeld.Value <- false)
                | "Alt"   -> transact (fun () -> altHeld.Value <- false)
                | "r" | "R" -> env.Emit [SetReferencePeek false]
                | _ -> ()
            )

            GuiTopBar.topBar env model (hoverCoord :> aval<V3d option>)
            div {
                Primitives.showWhen model.MenuOpen
                GuiRail.rail env model (viewportSize :> aval<V2i>)
            }
            GuiFocus.panel env model
            GuiOverlays.toast model
            GuiOverlays.meshWheelLabel model (cursorScreen :> aval<_>)
            GuiOverlays.scaleBar model (viewportSize :> aval<V2i>)
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
