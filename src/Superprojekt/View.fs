namespace Superprojekt

open Aardvark.Base
open Aardvark.Dom.Utilities.OrbitController
open Aardvark.Rendering
open FSharp.Data.Adaptive
open Aardvark.Dom
open Adaptify
open Superprojekt

module View =

    let view (env : Env<Message>) (model : AdaptiveModel) =

        ServerActions.init env

        let cursorPosition = cval None
        let shiftHeld      = cval false
        let spaceHeld      = cval false
        let hoverCoord     = cval<V3d option> None
        let viewportSize   = cval (V2i(1, 1))

        let revolverActive   = AVal.map2 (||) (shiftHeld :> aval<_>) model.RevolverOn
        let fullscreenActive = AVal.map2 (||) (spaceHeld :> aval<_>) model.FullscreenOn
        let revolverBase =
            AVal.custom (fun t ->
                if (shiftHeld :> aval<_>).GetValue(t) then (cursorPosition :> aval<_>).GetValue(t)
                elif (model.RevolverOn :> aval<_>).GetValue(t) then Some ((model.RevolverCenter :> aval<_>).GetValue(t))
                else None
            )

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
                (model.ScanPins.PlacingMode, model.ScanPins.ActivePlacement) ||> AVal.map2 (fun pm ap ->
                    if pm.IsSome || ap.IsSome then Some (Dom.Style [Css.Cursor "crosshair"]) else None
                )

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

                Sg.OnDoubleTap(fun e ->
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
                    let inPlacement = (AVal.force model.ScanPins.PlacingMode).IsSome
                    let activePlacement = (AVal.force model.ScanPins.ActivePlacement).IsSome
                    if inPlacement && not activePlacement then
                        let camFwd = (AVal.force view).Forward.GetViewDirectionLH() |> Vec.normalize
                        env.Emit [ScanPinMsg (SetAnchor(worldPos, e.WorldPosition, V3d camFwd))]
                        false
                    elif activePlacement && e.Ctrl && e.Button = Button.Left then
                        let camFwd = (AVal.force view).Forward.GetViewDirectionLH() |> Vec.normalize
                        env.Emit [ScanPinMsg (SetAnchor(worldPos, e.WorldPosition, V3d camFwd))]
                        false
                    elif e.Ctrl && e.Button = Button.Left then
                        transact (fun () -> hoverCoord.Value <- Some worldPos)
                        env.Emit [ClearFilteredMesh]
                        ServerActions.triggerFilter env model e.Position
                        false
                    else
                        transact (fun () -> hoverCoord.Value <- Some worldPos)
                        true
                )

                Sg.OnLongPress(fun e ->
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

                let pinsVal = model.ScanPins.Pins |> AMap.toAVal
                let editedPin : aval<ScanPin option> =
                    (model.ScanPins.SelectedPin, model.ScanPins.ActivePlacement, pinsVal)
                    |||> AVal.map3 (fun sel act pins ->
                        let id = act |> Option.orElse sel
                        id |> Option.bind (fun id -> HashMap.tryFind id pins))

                let ndcOf (e : Aardvark.Dom.PointerEvent) =
                    let b = e.ClientRect
                    let tc = (V2d e.ClientPosition - b.Min) / b.Size
                    V2d(2.0 * tc.X - 1.0, 1.0 - 2.0 * tc.Y)

                let pickRay (ndc : V2d) =
                    let vp = (AVal.force view) * (AVal.force proj)
                    let p0 = vp.Backward.TransformPosProj(V3d(ndc, -1.0))
                    let p1 = vp.Backward.TransformPosProj(V3d(ndc, 1.0))
                    Ray3d(p0, (p1 - p0) |> Vec.normalize)

                let intersectPinCylinder (ray : Ray3d) (pin : ScanPin) =
                    let axis = pin.Prism.AxisDirection |> Vec.normalize
                    let r = match pin.Prism.Footprint.Vertices with v :: _ -> v.Length | _ -> 1.0
                    let anchor = pin.Prism.AnchorPoint
                    let oc = ray.Origin - anchor
                    let dPerp = ray.Direction - axis * (Vec.dot ray.Direction axis)
                    let ocPerp = oc - axis * (Vec.dot oc axis)
                    let a = Vec.dot dPerp dPerp
                    let b = 2.0 * Vec.dot ocPerp dPerp
                    let c = Vec.dot ocPerp ocPerp - r * r
                    let disc = b * b - 4.0 * a * c
                    if disc < 0.0 || a < 1e-12 then None
                    else
                        let sqrtD = sqrt disc
                        let ts = [(-b - sqrtD) / (2.0 * a); (-b + sqrtD) / (2.0 * a)]
                        ts |> List.tryPick (fun t ->
                            if t < 0.0 then None
                            else
                                let hit = ray.Origin + ray.Direction * t
                                let axDist = Vec.dot (hit - anchor) axis
                                if axDist >= -pin.Prism.ExtentBackward && axDist <= pin.Prism.ExtentForward then Some hit
                                else None)

                let emitHitMessage (pin : ScanPin) (hit : V3d) =
                    let axis = pin.Prism.AxisDirection |> Vec.normalize
                    let right, fwd = PinGeometry.axisFrame axis
                    let v = hit - pin.Prism.AnchorPoint
                    match pin.CutPlane with
                    | CutPlaneMode.AcrossAxis _ ->
                        let dist = Vec.dot v axis
                        let clamped = clamp (-pin.Prism.ExtentBackward) pin.Prism.ExtentForward dist
                        env.Emit [ScanPinMsg (SetCutPlaneDistance clamped)]
                    | CutPlaneMode.AlongAxis _ ->
                        let lx = Vec.dot v right
                        let ly = Vec.dot v fwd
                        let ang = atan2 ly lx * Constant.DegreesPerRadian
                        env.Emit [ScanPinMsg (SetCutPlaneAngle ang)]

                Dom.OnPointerDown((fun e ->
                    match AVal.force editedPin with
                    | Some pin ->
                        match intersectPinCylinder (pickRay (ndcOf e)) pin with
                        | Some hit ->
                            transact (fun () -> PinCylinderDrag.isActive.Value <- true)
                            emitHitMessage pin hit
                        | None -> ()
                    | None -> ()
                ), pointerCapture = true)

                Dom.OnPointerUp((fun _ ->
                    if AVal.force PinCylinderDrag.isActive then
                        transact (fun () -> PinCylinderDrag.isActive.Value <- false)
                ), pointerCapture = true)

                Dom.OnPointerMove(fun e ->
                    transact (fun () ->
                        let b = e.ClientRect
                        let tc = (V2d e.ClientPosition - b.Min) / b.Size
                        cursorPosition.Value <- Some (V2d(2.0 * tc.X - 1.0, 1.0 - 2.0 * tc.Y))
                        shiftHeld.Value <- e.Shift
                    )
                    if AVal.force PinCylinderDrag.isActive then
                        match AVal.force editedPin with
                        | Some pin ->
                            match intersectPinCylinder (pickRay (ndcOf e)) pin with
                            | Some hit -> emitHitMessage pin hit
                            | None -> ()
                        | None -> ()
                )

                Dom.OnPointerDown((fun e ->
                    if AVal.force model.RevolverOn && not (AVal.force shiftHeld) then
                        let b = e.ClientRect
                        let tc = (V2d e.ClientPosition - b.Min) / b.Size
                        let ndc = V2d(2.0 * tc.X - 1.0, 1.0 - 2.0 * tc.Y)
                        env.Emit [SetRevolverCenter ndc]
                ), pointerCapture = true)
                

                SceneGraph.build env info view proj revolverBase revolverActive fullscreenActive model
            }

            Dom.OnKeyDown(fun e ->
                match e.Key with
                | "Shift"  -> transact (fun () -> shiftHeld.Value <- true)
                | " "      -> transact (fun () -> spaceHeld.Value <- true)
                | "Escape" -> env.Emit [ScanPinMsg CancelPlacement]
                | _ -> ()
            )
            Dom.OnKeyUp(fun e ->
                match e.Key with
                | "Shift" -> transact (fun () -> shiftHeld.Value <- false)
                | " "     -> transact (fun () -> spaceHeld.Value <- false)
                | _ -> ()
            )

            Gui.topBar env model
            Gui.revolverBar env model
            Gui.leftPanel env model
            Gui.bottomBar env model
            Cards.renderCards env model (model.Camera.view |> AVal.map CameraView.viewTrafo) (viewportSize :> aval<V2i>)
            Gui.fullscreenInfo model
            Gui.coordinateDisplay (hoverCoord :> aval<V3d option>)
        }


module App =
    let app =
        {
            initial   = Model.initial
            update    = Update.update
            view      = View.view
            unpersist = Unpersist.instance
        }
