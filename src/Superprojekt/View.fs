namespace Superprojekt

open Aardvark.Base
open Aardvark.Dom.Utilities.OrbitController
open Aardvark.Rendering
open FSharp.Data.Adaptive
open Aardvark.Dom
open Adaptify
open Superprojekt

module View =

    /// Slab-test ray-box intersection. Returns Some t (entry distance ≥ 0)
    /// when the ray hits the box ahead of the camera; None otherwise.
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

    let view (env : Env<Message>) (model : AdaptiveModel) =

        ServerActions.init env

        let spaceHeld       = cval false
        let hoverCoord      = cval<V3d option> None
        let viewportSize    = cval (V2i(1, 1))
        let placementHover  = cval<V3d option> None
        // V6 §D.1 mesh-wheel — cursor position in viewport px, used to
        // position the floating active-picking-layer label and the lasso
        // overlay's "next segment" preview.
        let cursorScreen    = cval<V2d option> None
        // V6 §D.8 — registration solver card open/close state.
        let registrationOpen = cval false

        let fullscreenActive = AVal.map2 (||) (spaceHeld :> aval<_>) model.FullscreenOn

        let placementActive =
            model.ScanPins.Placement |> AVal.map (function AnchorPlacement -> true | _ -> false)

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

                // Canvas-px cursor tracking — Sg-level events deliver
                // e.Position as V3d world coords, so we capture screen px
                // here at the DOM layer and let lasso/wheel-label consume it.
                Dom.OnPointerMove(fun e ->
                    let cursorPx = V2d(float e.OffsetPosition.X, float e.OffsetPosition.Y)
                    if cursorScreen.Value <> Some cursorPx then
                        transact (fun () -> cursorScreen.Value <- Some cursorPx)
                )

                // V6 §D.1 mesh-wheel: cycle the active picking layer when at
                // least two visible meshes intersect the cursor ray; otherwise
                // forward to the orbit zoom. Alt forces zoom unconditionally.
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
                    // Double-tap during lasso drawing commits the polygon.
                    match AVal.force model.LassoDrawing with
                    | Some _ ->
                        env.Emit [LassoCommit(AVal.force view, AVal.force proj, AVal.force size)]
                        false
                    | None ->
                        if e.Location.Depth < 0.9999 then
                            env.Emit [CameraMessage (OrbitMessage.SetTargetCenter(true, AnimationKind.Tanh, e.WorldPosition))]
                        false
                )

                Sg.OnTap(fun e ->
                    let scale =
                        AVal.force model.ActiveDataset
                        |> Option.bind (fun ds -> Map.tryFind ds (AVal.force model.DatasetScales))
                        |> Option.defaultValue 1.0
                    let cc = AVal.force model.CommonCentroid
                    let worldPos = e.WorldPosition / scale + cc
                    let hitGeometry = e.Location.Depth < 0.9999
                    let placement = AVal.force model.ScanPins.Placement
                    match AVal.force model.LassoDrawing with
                    | Some _ ->
                        // Each click adds a vertex at the cursor's canvas
                        // pixel position (tracked by the Dom.OnPointerMove
                        // handler below — Sg-level e.Position is V3d world,
                        // not pixel coords, so we read from the cval).
                        match cursorScreen.Value with
                        | Some px -> env.Emit [LassoAddVertex px]
                        | None -> ()
                        false
                    | None ->
                        match placement with
                        | AnchorPlacement when hitGeometry ->
                            env.Emit [ScanPinMsg (PlaceAnchor e.WorldPosition)]
                            false
                        | _ ->
                            if e.Ctrl && e.Button = Button.Left && hitGeometry then
                                transact (fun () -> hoverCoord.Value <- Some worldPos)
                                env.Emit [ClearFilteredMesh]
                                ServerActions.triggerFilter env model e.WorldPosition
                                false
                            else
                                transact (fun () -> hoverCoord.Value <- Some worldPos)
                                true
                )

                Sg.OnLongPress(fun e ->
                    if e.Location.Depth < 0.9999 then
                        let scale =
                            AVal.force model.ActiveDataset
                            |> Option.bind (fun ds -> Map.tryFind ds (AVal.force model.DatasetScales))
                            |> Option.defaultValue 1.0
                        let cc = AVal.force model.CommonCentroid
                        transact (fun () -> hoverCoord.Value <- Some (e.WorldPosition / scale + cc))
                        env.Emit [ClearFilteredMesh]
                        ServerActions.triggerFilter env model e.Position
                    false
                )

                Sg.OnPointerMove(fun e ->
                    let scale =
                        AVal.force model.ActiveDataset
                        |> Option.bind (fun ds -> Map.tryFind ds (AVal.force model.DatasetScales))
                        |> Option.defaultValue 1.0
                    let cc = AVal.force model.CommonCentroid
                    let hitGeometry = e.Location.Depth < 0.9999
                    transact (fun () -> hoverCoord.Value <- Some (e.WorldPosition / scale + cc))
                    if AVal.force placementActive then
                        let next = if hitGeometry then Some e.WorldPosition else None
                        if placementHover.Value <> next then
                            transact (fun () -> placementHover.Value <- next)
                    elif placementHover.Value.IsSome then
                        transact (fun () -> placementHover.Value <- None)
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

            Gui.topBar env model (hoverCoord :> aval<V3d option>)
            Gui.leftPanel env model
            Gui.placementFlyout env model
            Gui.exploreCard env model
            Gui.registrationCard env model registrationOpen
            Gui.registrationToggleButton registrationOpen
            Gui.meshWheelLabel model (cursorScreen :> aval<_>)
            Gui.lassoOverlay env model (cursorScreen :> aval<_>)
            Cards.renderCards env model (model.Camera.view |> AVal.map CameraView.viewTrafo) (viewportSize :> aval<V2i>)
            Gui.fullscreenInfo model
            Gui.scaleBar model (viewportSize :> aval<V2i>)
            Gui.orientationIndicator model
        }


module App =
    let app =
        {
            initial   = Model.initial
            update    = Update.update
            view      = View.view
            unpersist = Unpersist.instance
        }
