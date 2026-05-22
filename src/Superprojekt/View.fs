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
        let scale =
            AVal.force model.ActiveDataset
            |> Option.bind (fun ds -> Map.tryFind ds (AVal.force model.DatasetScales))
            |> Option.defaultValue 1.0
        let cc = AVal.force model.CommonCentroid
        renderPos / scale + cc

    // Pixel picking is inline at each Sg event handler:
    //   if e.Location.Depth < 0.9999 then Some e.WorldPosition else None
    // The mesh shader writes z = 1.0 (far plane) for any α < 0.99 fragment,
    // so ghost / transparent geometry produces no usable pick. Pin spheres /
    // grid / cross don't write depth at all, so they also can't be picked.

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
            ]

            renderControl {
                RenderControl.Samples 1
                Class "render-control"

                Dom.Style [
                    Css.Background "rgb(244, 246, 248)"
                ]
                (model.ScanPins.Placement, lassoActive) ||> AVal.map2 (fun p lasso ->
                    match p, lasso with
                    | PlacementIdle, false -> None
                    | _ -> Some (Dom.Style [Css.Cursor "crosshair"]))

                let! info = RenderControl.Info
                let! size = RenderControl.ViewportSize

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
                    let s = AVal.force size
                    if viewportSize.Value <> s then
                        transact (fun () -> viewportSize.Value <- s)
                    env.Emit [CameraMessage OrbitMessage.Rendered]
                )

                let view = model.Camera.view |> AVal.map CameraView.viewTrafo
                let proj =
                    size |> AVal.map (fun s ->
                        Frustum.perspective 90.0 1.0 5000.0 (float s.X / float s.Y) |> Frustum.projTrafo
                    )

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
                        let vpSize = AVal.force size
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
                                    let dataset =
                                        let s = name.IndexOf('/')
                                        if s >= 0 then name.[..s-1] else ""
                                    let scale = Map.tryFind dataset scales |> Option.defaultValue 1.0
                                    let lo = (world.Min - cc) * scale
                                    let hi = (world.Max - cc) * scale
                                    let box = Box3d(V3d(min lo.X hi.X, min lo.Y hi.Y, min lo.Z hi.Z),
                                                    V3d(max lo.X hi.X, max lo.Y hi.Y, max lo.Z hi.Z))
                                    rayBoxT ray box |> Option.map (fun t -> t, name)
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
                        env.Emit [LassoCommit(AVal.force view, AVal.force proj, AVal.force size)]
                        false
                    | None ->
                        if e.Location.Depth < 0.9999 then
                            let renderPos = e.WorldPosition
                            env.Emit [CameraMessage (OrbitMessage.SetTargetCenter(true, AnimationKind.Tanh, renderPos))]
                        false
                )

                Sg.OnTap(fun e ->
                    match AVal.force model.LassoDrawing with
                    | Some _ ->
                        match cursorScreen.Value with
                        | Some px -> env.Emit [LassoAddVertex px]
                        | None -> ()
                        false
                    | None ->
                        let placement = AVal.force model.ScanPins.Placement
                        let ctrlLeft = e.Ctrl && e.Button = Button.Left
                        let pick =
                            if e.Location.Depth < 0.9999 then Some e.WorldPosition else None
                        match placement, pick with
                        | AnchorPlacement, Some renderPos ->
                            env.Emit [ScanPinMsg (PlaceAnchor renderPos)]
                            false
                        | AnchorPlacement, None -> false
                        | _, Some renderPos when ctrlLeft ->
                            let worldPos = worldFromRender model renderPos
                            transact (fun () -> hoverCoord.Value <- Some worldPos)
                            env.Emit [ClearFilteredMesh]
                            ServerActions.triggerFilter env model renderPos
                            false
                        | _, Some renderPos ->
                            let worldPos = worldFromRender model renderPos
                            transact (fun () -> hoverCoord.Value <- Some worldPos)
                            true
                        | _, None -> true
                )

                Sg.OnLongPress(fun e ->
                    if e.Location.Depth < 0.9999 then
                        let renderPos = e.WorldPosition
                        let worldPos = worldFromRender model renderPos
                        transact (fun () -> hoverCoord.Value <- Some worldPos)
                        env.Emit [ClearFilteredMesh]
                        ServerActions.triggerFilter env model renderPos
                    false
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
            GuiCards.exploreCard env model
            GuiCards.registrationCard env model registrationOpen
            GuiCards.registrationToggleButton registrationOpen
            GuiOverlays.persistenceBridge env
            GuiOverlays.meshWheelLabel model (cursorScreen :> aval<_>)
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
