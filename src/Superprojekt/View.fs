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
        let hoverCoord      = cval<V3d option> None
        let viewportSize    = cval (V2i(1, 1))
        let placementHover  = cval<V3d option> None
        let cursorScreen    = cval<V2d option> None
        let registrationOpen = cval false

        let fullscreenActive = AVal.map2 (||) (spaceHeld :> aval<_>) model.FullscreenOn

        let lassoActive = model.LassoDrawing |> AVal.map Option.isSome

        body {
            OnBoot [
                "const l = document.getElementById('loader');"
                "if(l) l.remove();"
                "document.body.classList.add('loaded');"
                "window.SuperWorkspaceSave = function(filename, json){"
                "  var blob = new Blob([json], {type:'application/json'});"
                "  var url = URL.createObjectURL(blob);"
                "  var a = document.createElement('a');"
                "  a.href = url; a.download = filename;"
                "  document.body.appendChild(a); a.click(); document.body.removeChild(a);"
                "  URL.revokeObjectURL(url);"
                "};"
                "window.SuperWorkspaceLoad = function(){"
                "  return new Promise(function(resolve){"
                "    var input = document.createElement('input');"
                "    input.type = 'file'; input.accept = '.json,application/json';"
                "    input.onchange = function(){"
                "      if(input.files && input.files.length > 0){"
                "        var reader = new FileReader();"
                "        reader.onload = function(){ resolve(reader.result || ''); };"
                "        reader.onerror = function(){ resolve(''); };"
                "        reader.readAsText(input.files[0]);"
                "      } else { resolve(''); }"
                "    };"
                "    input.click();"
                "  });"
                "};"
            ]

            renderControl {
                RenderControl.Samples 1
                Class "render-control"

                (model.ScanPins.Placement, lassoActive) ||> AVal.map2 (fun p lasso ->
                    match p, lasso with
                    | PlacementIdle, false -> None
                    | _ -> Some (Dom.Style [Css.Cursor "crosshair"]))

                let! info = RenderControl.Info
                let! size = RenderControl.ViewportSize
                let! client = RenderControl.ClientSize

                // CSS-pixel canvas size for everything that mixes with DOM
                // coordinates (cursor positions, HTML overlay placement).
                // ViewportSize is framebuffer pixels = CSS × devicePixelRatio,
                // so using it for overlay math pushes cards off-screen on
                // hi-dpi displays. ClientSize is V2i.II until the first DOM
                // event arrives — fall back to the framebuffer size until then.
                let overlaySize =
                    (client, size) ||> AVal.map2 (fun c v ->
                        if c.X > 1 && c.Y > 1 then c else v)

                let mutable eHandler = None

                RenderControl.OnReady (fun e ->
                    eHandler <- Some e
                    ()
                )

                OrbitController.getAttributes (Env.map CameraMessage env)

                let mutable initial = true
                RenderControl.OnRendered(fun _ ->
                    if initial then
                        initial <- false
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

                // When ActivePickingLayer is set, prefer that layer's surface
                // over the frontmost surface — but only if the cursor ray
                // actually intersects the layer's bounding box. Falls back to
                // the GPU frontmost pick otherwise. Result is async because
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

                // Fusion picking: the composite is a flat quad, so the GPU pick
                // can't see the meshes. Raycast every visible mesh server-side
                // and keep the lowest combined-error hit (same winner as the
                // offscreen depth test).
                let resolveFusionPick () : Async<(V3d * string) option> =
                    match cursorScreen.Value with
                    | None -> async.Return None
                    | Some cursorPx ->
                        let names = MeshView.visibleMeshNames model
                        if List.isEmpty names then async.Return None
                        else
                            let ray = pickRay cursorPx (AVal.force overlaySize) (AVal.force view) (AVal.force proj)
                            let cc = AVal.force model.CommonCentroid
                            let scales = AVal.force model.DatasetScales
                            let sensors = AVal.force model.MeshSensorTypes
                            let overrides = AVal.force model.MeshDatasetErrors
                            let algo = AVal.force model.MeshAlgorithmResidual
                            let anchors =
                                AVal.force (model.ScanPins.Pins |> AMap.toAVal) |> HashMap.toSeq
                                |> Seq.choose (fun (_, pn) ->
                                    if pn.Phase = PinPhase.Committed then Some (pn.Centre, pn.FalloffRadius) else None)
                                |> Array.ofSeq
                            async {
                                let! hits =
                                    Query.rayHitMany ApiConfig.apiBase.Value names (fun name ->
                                        let scale = DatasetScale.forMesh scales name
                                        ScanPin.worldCentre cc scale ray.Origin, ray.Direction)
                                let best =
                                    hits |> Array.choose id
                                    |> Array.map (fun (name, h) ->
                                        let d, a, c = Provenance.sourcesAt name overrides sensors algo h.point anchors
                                        name, h.point, d + a + c * 0.01)
                                    |> Array.sortBy (fun (_, _, e) -> e)
                                    |> Array.tryHead
                                match best with
                                | Some (name, worldPt, _) ->
                                    let scale = DatasetScale.forMesh scales name
                                    return Some (ScanPin.renderCentre cc scale worldPt, name)
                                | None -> return None
                            }

                Sg.View view
                Sg.Proj proj

                Sg.Pass RenderPass.passZero

                Dom.OnPointerMove(fun e ->
                    let cursorPx = V2d(float e.OffsetPosition.X, float e.OffsetPosition.Y)
                    if cursorScreen.Value <> Some cursorPx then
                        transact (fun () -> cursorScreen.Value <- Some cursorPx)
                )

                Dom.OnMouseWheel(fun e ->
                    let delta = V2d(e.DeltaX, e.DeltaY) / 120.0
                    let forwardZoom () =
                        env.Emit [CameraMessage (OrbitMessage.Wheel(false, delta))]
                    if e.Alt then
                        forwardZoom ()
                    else
                        let cursorPx = V2d(float e.OffsetPosition.X, float e.OffsetPosition.Y)
                        let vpSize = AVal.force overlaySize
                        let v = AVal.force view
                        let p = AVal.force proj
                        let ray = pickRay cursorPx vpSize v p
                        let visible = AVal.force model.MeshVisible
                        let bounds = AVal.force model.MeshBounds
                        let cc = AVal.force model.CommonCentroid
                        let scales = AVal.force model.DatasetScales
                        let hits =
                            bounds |> Map.toSeq
                            |> Seq.choose (fun (name, world) ->
                                if Map.tryFind name visible |> Option.defaultValue true then
                                    let scale = DatasetScale.forMesh scales name
                                    rayBoxT ray (renderBox world cc scale) |> Option.map (fun t -> t, name)
                                else None)
                            |> Seq.sortBy fst
                            |> Seq.map snd
                            |> Array.ofSeq
                        if hits.Length < 2 then
                            forwardZoom ()
                        else
                            let cur = AVal.force model.ActivePickingLayer
                            let n = hits.Length
                            let dir = if e.DeltaY > 0.0 then 1 else -1
                            let next =
                                match cur with
                                | None -> hits.[if dir > 0 then 0 else n - 1]
                                | Some c ->
                                    match Array.tryFindIndex ((=) c) hits with
                                    | Some i -> hits.[((i + dir) % n + n) % n]
                                    | None -> hits.[if dir > 0 then 0 else n - 1]
                            env.Emit [SetActivePickingLayer (Some next)]
                )

                Sg.OnDoubleTap(fun e ->
                    match AVal.force model.LassoDrawing with
                    | Some _ ->
                        env.Emit [LassoCommit(AVal.force view, AVal.force proj, AVal.force overlaySize)]
                        false
                    | None ->
                        if AVal.force model.FusionMode then
                            async {
                                let! resolved = resolveFusionPick ()
                                match resolved with
                                | Some (renderPos, _) ->
                                    env.Emit [CameraMessage (OrbitMessage.SetTargetCenter(true, AnimationKind.Tanh, renderPos))]
                                | None -> ()
                            } |> Async.Start
                        else
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
                    match AVal.force model.LassoDrawing with
                    | Some _ ->
                        match cursorScreen.Value with
                        | Some px -> env.Emit [LassoAddVertex px]
                        | None -> ()
                        false
                    | None when e.Ctrl ->
                        // Ctrl-click = transient hover probe (spec §7.4).
                        let screenPx = cursorScreen.Value |> Option.defaultValue V2d.Zero
                        if AVal.force model.FusionMode then
                            async {
                                let! resolved = resolveFusionPick ()
                                match resolved with
                                | Some (renderPos, _) ->
                                    env.Emit [HoverProbeAt(screenPx, worldFromRender model renderPos)]
                                | None -> ()
                            } |> Async.Start
                        else
                            let frontmost =
                                if e.Location.Depth < 0.9999 then Some e.WorldPosition else None
                            async {
                                let! resolved = resolveLayerPick frontmost
                                match resolved with
                                | Some renderPos ->
                                    env.Emit [HoverProbeAt(screenPx, worldFromRender model renderPos)]
                                | None -> ()
                            } |> Async.Start
                        true
                    | None ->
                        if Option.isSome (AVal.force model.HoverProbe) then
                            env.Emit [ClearHoverProbe]
                        let placement = AVal.force model.ScanPins.Placement
                        if AVal.force model.FusionMode then
                            // Fusion: resolve the winner mesh + point on the CPU,
                            // set it as the active layer so a placed pin inherits
                            // it as host, then place / focus.
                            async {
                                let! resolved = resolveFusionPick ()
                                match placement, resolved with
                                | AnchorPlacement, Some (renderPos, mesh) ->
                                    let worldPos = worldFromRender model renderPos
                                    env.Emit [SetActivePickingLayer (Some mesh); ScanPinMsg (PlaceAnchor worldPos)]
                                | AnchorPlacement, None -> ()
                                | _, Some (renderPos, mesh) ->
                                    let worldPos = worldFromRender model renderPos
                                    env.Emit [SetActivePickingLayer (Some mesh)]
                                    transact (fun () -> hoverCoord.Value <- Some worldPos)
                                | _, None -> ()
                            } |> Async.Start
                        else
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
                    true
                )

                SceneGraph.build env info view proj fullscreenActive (placementHover :> aval<_>) model
            }

            Dom.OnKeyDown(fun e ->
                match e.Key with
                | " "      -> transact (fun () -> spaceHeld.Value <- true)
                | "Escape" ->
                    if Option.isSome (AVal.force model.HoverProbe) then
                        env.Emit [ClearHoverProbe]
                    else
                        match AVal.force model.LassoDrawing with
                        | Some _ -> env.Emit [LassoCancel]
                        | None -> env.Emit [ScanPinMsg CancelPlacement]
                | _ -> ()
            )
            Dom.OnKeyUp(fun e ->
                match e.Key with
                | " "     -> transact (fun () -> spaceHeld.Value <- false)
                | _ -> ()
            )

            GuiTopBar.topBar env model (hoverCoord :> aval<V3d option>)
            GuiPanels.leftPanel env model
            GuiPanels.placementFlyout env model
            GuiCards.lassoCard env model
            GuiCards.registrationCard env model registrationOpen
            GuiCards.registrationToggleButton registrationOpen
            GuiCards.retargetCard env model
            GuiCards.panoramaCard env model
            GuiOverlays.meshWheelLabel model (cursorScreen :> aval<_>)
            GuiOverlays.hoverProbeTooltip model (viewportSize :> aval<V2i>)
            GuiOverlays.fusionNotice model
            GuiOverlays.provenanceHoverOverlay model (hoverCoord :> aval<_>) (cursorScreen :> aval<_>)
            GuiOverlays.lassoOverlay env model (cursorScreen :> aval<_>)
            Cards.renderCards env model (model.Camera.view |> AVal.map CameraView.viewTrafo) (viewportSize :> aval<V2i>)
            GuiOverlays.fullscreenInfo model
            GuiOverlays.scaleBar model (viewportSize :> aval<V2i>)
            GuiOverlays.orientationIndicator model
        }

module App =
    let app =
        {
            initial   = Model.initial
            update    = Update.update
            view      = View.view
            unpersist = Unpersist.instance
        }
