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

    let private worldFromRender (model : AdaptiveModel) (renderPos : V3d) =
        let scale = DatasetScale.active (AVal.force model.ActiveDataset) (AVal.force model.DatasetScales)
        ScanPin.worldCentre (AVal.force model.CommonCentroid) scale renderPos

    let private renderBox (worldBox : Box3d) (cc : V3d) (scale : float) =
        let lo = (worldBox.Min - cc) * scale
        let hi = (worldBox.Max - cc) * scale
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
        let previewSwap     = cval false

        let fullscreenActive = spaceHeld :> aval<bool>

        // Option/Alt = layer-isolation: wheel cycles the active picking layer,
        // meshes render isolated (active solid, rest ghosted) while held. The
        // selection outlives the key — it keeps steering picks. Suspended while
        // the chart cursor is live (Alt there extends the slicing plane
        // scene-wide, needing every mesh visible).
        // Alt-held layer isolation, OR §9 align-auto (step 2: the manually-moved
        // mesh solid, rest ghosted), OR §9 movement-auto (movement layer on: the
        // moved mesh solid + glyphs, rest ghosted).
        let wheelIsolation =
            AVal.custom (fun t ->
                let held = altHeld.GetValue t
                if held then model.ActivePickingLayer.GetValue t
                else
                    match model.MovementLayer.GetValue t, model.PendingReg.GetValue t with
                    | (MovementGlyphs | MovementGrid), Some pr when not (Map.isEmpty pr.Results) ->
                        pr.Results |> Map.toSeq |> Seq.tryHead |> Option.map fst
                    | _ ->
                        // Manual move → align-auto isolates the focused mesh.
                        // Correspondences → isolate only the hovered manager row
                        // (§G brushing); base state keeps every mesh for the
                        // constellation. Other steps: no isolation.
                        match model.WorkflowStep.GetValue t with
                        | StepManualMove -> model.FocusMesh.GetValue t
                        | StepCorrespondences -> model.CorrRowHover.GetValue t |> Option.map snd
                        | _ -> None)

        // Contact-line highlight for the mesh shader: the 3D hover point drives a
        // band inside the effective (selected) pin's probe cylinder. Only the
        // effective pin contributes — one plane at a time.
        let cursorHighlight =
            let pinsVal = model.ScanPins.Pins |> AMap.toAVal
            let effectiveId =
                ScanPinModel.effectivePinIdA model.ScanPins.Placement model.ScanPins.SelectedPin
            AVal.custom (fun t ->
                let pv = PendingRegistration.isPreview (model.PendingReg.GetValue t)
                let probeOf pid =
                    HashMap.tryFind pid (pinsVal.GetValue t)
                    |> Option.bind (fun pin ->
                        match ScanPin.effectiveProbe pv pin with
                        | ProbeReady r -> Some (pin, r)
                        | _ -> None)
                match hoverCoord.GetValue t, effectiveId.GetValue t with
                | Some q, Some pid ->
                    probeOf pid |> Option.bind (fun (pin, r) ->
                        let v = q - pin.Centre
                        let dAx = Vec.dot v r.Normal
                        let radial = (v - r.Normal * dAx).Length
                        if radial <= pin.InnerRadius && abs dAx <= r.Length * 0.5 then
                            Some { Origin    = pin.Centre + r.Normal * dAx
                                   Normal    = r.Normal
                                   Clip      = true
                                   PinCentre = pin.Centre
                                   PinRadius = pin.InnerRadius
                                   CylLength = r.Length }
                        else None)
                | _ -> None)

        // Section/cutaway clipping was removed; the mesh shader keeps generic
        // clip-plane support but is fed a constant no-clip.
        let clipUniforms : aval<int * V4f * V4f> = AVal.constant (0, V4f.Zero, V4f.Zero)

        body {
            OnBoot [
                "const l = document.getElementById('loader');"
                "if(l) l.remove();"
                "document.body.classList.add('loaded');"
                // Pulse outline for nav actions (§5); delayed so just-opened
                // targets are visible first.
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

                let mutable eHandler = None

                RenderControl.OnReady (fun e ->
                    eHandler <- Some e
                    ()
                )

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
                                    let originW = ScanPin.worldCentre cc scale ray.Origin
                                    let! hit = Query.rayHit ApiConfig.apiBase.Value layer 0 originW ray.Direction
                                    match hit with
                                    | Some h -> return Some (ScanPin.renderCentre cc scale h.point)
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
                        // plain wheel = camera zoom, always
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
                    let nextHover =
                        match pick with
                        | Some renderPos -> Some (worldFromRender model renderPos)
                        | None -> None
                    let nextPlacement = if placementWanted then pick else None
                    let needHover     = hoverCoord.Value     <> nextHover
                    let needPlacement = placementHover.Value <> nextPlacement
                    if needHover || needPlacement then
                        transact (fun () ->
                            if needHover     then hoverCoord.Value     <- nextHover
                            if needPlacement then placementHover.Value <- nextPlacement)
                    // Moving over terrain (off a constellation glyph) ends the
                    // §G glyph-hover brush — the glyph re-sets it while hovered.
                    if AVal.force model.WorkflowStep = StepCorrespondences
                       && (AVal.force model.CorrRowHover).IsSome then
                        env.Emit [SetCorrRowHover None]
                    true
                )

                SceneGraph.build env info view proj fullscreenActive (placementHover :> aval<_>) cursorHighlight clipUniforms (previewSwap :> aval<bool>) wheelIsolation model
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
            GuiPanels.placementFlyout env model
            GuiOverlays.previewBanner model (fun b -> transact (fun () -> previewSwap.Value <- b))
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
