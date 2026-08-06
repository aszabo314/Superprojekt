namespace Superprojekt

open System
open Aardvark.Base
open FSharp.Data.Adaptive
open Aardvark.Rendering
open Aardvark.Application
open Superprojekt
open Aardvark.Dom

type OrbitMessage =
    | PointerDown of id : int * button : Button * isTouch : bool * pos : V2i
    | PointerUp   of id : int * isTouch : bool * V2i
    | PointerMove of id : int * button : Button * isTouch : bool * V2i
    | Wheel       of delta : V2d

    | Rendered
    | SetTargetCenter of AnimationKind * V3d
    | SetTargetRadius of float
    | SetTarget       of center : V3d * radius : float * phi : float * theta : float

    | SetSpeed of float

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module OrbitState =
    let clamp (min : float) (max : float) (value : float) =
        if value > max then max
        elif value < min then min
        else value

    let withView (s : OrbitState) =
        let ct  = cos s.theta
        let dir = V3d(cos s.phi * ct, sin s.phi * ct, sin s.theta)
        let l =
            if s.radius <= 1.02 * s.radiusRange.X then s.center
            else dir * s.radius + s.center
        let r  = Vec.cross s.sky dir |> Vec.normalize
        let up = Vec.cross dir r     |> Vec.normalize
        { s with view = CameraView(s.sky, l, -dir, up, r) }

    let create (center : V3d) (phi : float) (theta : float) (r : float) (rotateButton : Button) (panButton : Button) =
        let thetaRange  = V2d(-Constant.PiHalf + 0.0001, Constant.PiHalf - 0.0001)
        let radiusRange = V2d(0.1, 40000000.0)
        let r     = clamp radiusRange.X radiusRange.Y r
        let theta = clamp thetaRange.X thetaRange.Y theta
        let phi   = phi % Constant.PiTimesTwo
        withView {
            sky    = V3d.OOI
            center = center
            phi    = phi
            theta  = theta
            radius = r

            locationAnimation = None
            centerAnimation   = None
            targetPhi    = phi
            targetTheta  = theta
            targetRadius = r

            dragStarts   = MapExt.empty
            rotateButton = rotateButton
            panButton    = panButton

            lastRender = None
            view       = Unchecked.defaultof<_>

            radiusRange = radiusRange
            thetaRange  = thetaRange
            speed       = 0.9
        }

module OrbitController =
    let private sw = System.Diagnostics.Stopwatch.StartNew()
    let time() = sw.MicroTime

    let update (_env : Env<OrbitMessage>) (model : OrbitState) (msg : OrbitMessage) =
        match msg with

        | SetTarget(center, r, phi, theta) ->
            let phi = phi % Constant.PiTimesTwo
            let now = time()
            let dstLocation =
                let ct = cos theta
                V3d(cos phi * ct, sin phi * ct, sin theta) * r + center
            let animDuration = MicroTime.FromMilliseconds 350.0
            let centerAnim =
                { kind = AnimationKind.Tanh; startValue = model.center; stopValue = center
                  startTime = now; stopTime = now + animDuration }
            let locationAnim =
                { kind = AnimationKind.Tanh; startValue = model.view.Location; stopValue = dstLocation
                  startTime = now; stopTime = now + animDuration }
            OrbitState.withView {
                model with
                    centerAnimation   = Some centerAnim
                    locationAnimation = Some locationAnim
                    lastRender        = None
            }

        | SetSpeed v -> { model with speed = v }

        | SetTargetRadius tr ->
            OrbitState.withView { model with targetRadius = OrbitState.clamp model.radiusRange.X model.radiusRange.Y tr; lastRender = None; locationAnimation = None }

        | SetTargetCenter(kind, tc) ->
            let now = time()
            let animDuration = MicroTime.FromMilliseconds 350.0
            let anim =
                { kind = kind; startValue = model.center; stopValue = tc
                  startTime = now; stopTime = now + animDuration }
            OrbitState.withView {
                model with
                    centerAnimation = Some anim
                    lastRender      = None
            }

        | PointerDown(id, button, _isTouch, p) ->
            let s = MapExt.add id (p, button) model.dragStarts
            { model with dragStarts = s; lastRender = None }

        | PointerUp(id, _isTouch, _p) ->
            match MapExt.tryRemove id model.dragStarts with
            | Some (_, s) -> { model with dragStarts = s; lastRender = None }
            | None -> model

        | Wheel delta ->
            OrbitState.withView {
                model with
                    targetRadius =
                        OrbitState.clamp model.radiusRange.X model.radiusRange.Y
                              (model.targetRadius * 1.1 ** delta.Y)
            }

        | PointerMove(id, _button, isTouch, p) ->
            match model.dragStarts.Count with
            | 1 ->
                match MapExt.tryFind id model.dragStarts with
                | Some(start, button) ->
                    // RIGHT is the always-available camera hand (left is the
                    // pick while armed): right-drag orbits, Shift+right pans
                    // (remapped to the pan button at the event edge).
                    let left   = button = model.rotateButton || button = Button.Right
                    let middle = button = model.panButton
                    if isTouch || left then
                        let delta  = p - start
                        let dphi   = float delta.X * -0.005
                        let dtheta = float delta.Y *  0.005
                        if not (Fun.IsTiny dphi) || not (Fun.IsTiny dtheta) then
                            OrbitState.withView
                                { model with
                                    dragStarts  = MapExt.add id (p, button) model.dragStarts
                                    targetPhi   = (model.targetPhi + dphi) % Constant.PiTimesTwo
                                    targetTheta = OrbitState.clamp model.thetaRange.X model.thetaRange.Y (model.targetTheta + dtheta) }
                        else
                            model
                    elif middle then
                        let delta = p - start
                        let newCenter =
                            let r = max model.radius 0.3
                            // Lock the pan to the world XY plane (constant Z): view.Right
                            // already lies in XY (= cross(sky,dir)); only view.Up carries
                            // a Z component, so flatten + renormalize it. center.Z stays
                            // fixed and the camera location follows via withView. At a
                            // near-horizontal view screen-up ≈ world-Z (out of plane, so
                            // its XY projection vanishes) — fall back to ground-forward so
                            // vertical drag still pans (they coincide at moderate tilts).
                            let flatXY (v : V3d) =
                                let f = V3d(v.X, v.Y, 0.0)
                                if f.Length > 1e-6 then Some f.Normalized else None
                            let upXY =
                                match flatXY model.view.Up with
                                | Some u -> u
                                | None -> flatXY model.view.Forward |> Option.defaultValue V3d.Zero
                            model.center +
                            model.view.Right * (float delta.X * -0.001 * r) +
                            upXY             * (float delta.Y *  0.001 * r)
                        OrbitState.withView
                            { model with
                                dragStarts      = MapExt.add id (p, button) model.dragStarts
                                centerAnimation = None
                                center          = newCenter }
                    else
                        model
                | None ->
                    model
            | 2 ->
                match MapExt.tryFind id model.dragStarts with
                | Some (op, button) ->
                    let np = p
                    let _otherId, (otherPos, _) = model.dragStarts |> MapExt.toSeq |> Seq.find (fun (k, _) -> k <> id)
                    // Two touches on the same pixel would divide by zero → NaN radius
                    // that clamp passes through, wedging zoom until the next fly-to.
                    let denom  = Vec.length (V2d (op - otherPos))
                    let scale  = if denom > 1e-6 then Vec.length (V2d (np - otherPos)) / denom else 1.0
                    let r      = OrbitState.clamp model.radiusRange.X model.radiusRange.Y (model.targetRadius / scale)
                    let delta  = 0.5 * V2d(np - op)
                    let dphi   = delta.X * -0.005
                    let dtheta = delta.Y *  0.005
                    OrbitState.withView
                        { model with
                            dragStarts  = MapExt.add id (p, button) model.dragStarts
                            targetPhi   = (model.targetPhi + dphi) % Constant.PiTimesTwo
                            targetTheta = OrbitState.clamp model.thetaRange.X model.thetaRange.Y (model.targetTheta + dtheta)
                            targetRadius = r }
                | None ->
                    model
            | _ ->
                model

        | Rendered ->
            let dphi    =
                let a = (model.targetPhi - model.phi) % Constant.PiTimesTwo
                if a < -Constant.Pi then Constant.PiTimesTwo + a
                elif a > Constant.Pi then a - Constant.PiTimesTwo
                else a
            let dtheta  = model.targetTheta  - model.theta
            let dradius = model.targetRadius - model.radius
            let now     = time()
            let dt      =
                match model.lastRender with
                | Some last -> (now - last)
                | None      -> MicroTime.Zero
            let delta = model.speed * dt.TotalSeconds / 0.05
            let part  = if dt.TotalSeconds > 0.0 then OrbitState.clamp 0.0 1.0 delta else 0.0
            let model = { model with lastRender = Some now }

            let model =
                if abs dphi > 0.0 then
                    if Fun.IsTiny(dphi, 1E-4) then OrbitState.withView { model with phi = model.targetPhi }
                    else OrbitState.withView { model with phi = (model.phi + part * dphi) % Constant.PiTimesTwo }
                else model

            let model =
                if abs dtheta > 0.0 then
                    if Fun.IsTiny(dtheta, 1E-4) then OrbitState.withView { model with theta = model.targetTheta }
                    else OrbitState.withView { model with theta = model.theta + part * dtheta }
                else model

            let model =
                if abs dradius > 0.0 then
                    if Fun.IsTiny(dradius, 1E-4) then OrbitState.withView { model with radius = model.targetRadius }
                    else OrbitState.withView { model with radius = model.radius + part * dradius }
                else model

            let model =
                match model.centerAnimation with
                | Some anim ->
                    match model.locationAnimation with
                    | Some locAnim ->
                        let inline setLocation (location : V3d) (center : V3d) (m : OrbitState) =
                            let diff  = location - center
                            let r     = Vec.Length diff
                            let phi   = atan2 diff.Y diff.X
                            let theta = asin (diff.Z / r)
                            OrbitState.withView { m with center = center; radius = r; targetRadius = r; phi = phi; targetPhi = phi; theta = theta; targetTheta = theta }
                        match anim.kind with
                        | AnimationKind.Exp ->
                            if now >= anim.stopTime then
                                setLocation locAnim.stopValue anim.stopValue { model with centerAnimation = None; locationAnimation = None }
                            else
                                let dLoc    = locAnim.stopValue - model.view.Location
                                let dCenter = anim.stopValue - model.center
                                setLocation (model.view.Location + part * dLoc) (model.center + part * dCenter) model
                        | _ ->
                            if now < anim.stopTime then
                                setLocation (Animation.interpolate now locAnim) (Animation.interpolate now anim) model
                            else
                                setLocation locAnim.stopValue anim.stopValue { model with centerAnimation = None; locationAnimation = None }
                    | None ->
                        let dcenter  = anim.stopValue - model.center
                        let dCurrent = Vec.length dcenter
                        match anim.kind with
                        | AnimationKind.Exp ->
                            if Fun.IsTiny(dCurrent, 1E-4) then
                                OrbitState.withView { model with center = anim.stopValue; centerAnimation = None }
                            else
                                OrbitState.withView { model with center = model.center + part * dcenter }
                        | _ ->
                            if dCurrent > 0.0 then
                                OrbitState.withView { model with center = Animation.interpolate now anim }
                            else
                                { model with centerAnimation = None }
                | None ->
                    model

            model

    let getAttributes (env : Env<OrbitMessage>) =
        att {
            Dom.OnPointerDown((fun e ->
                if e.PointerType = PointerType.Mouse then
                    // Shift + left/right drag = XY pan: remap to the pan
                    // (middle) button so the whole drag path treats it as a
                    // pan. (Shift, not Ctrl — Ctrl+click is the secondary
                    // click on macOS.) Right is the always-available camera
                    // hand — it orbits bare and pans with Shift.
                    let btn =
                        if (e.Button = Button.Left || e.Button = Button.Right) && e.Shift
                        then Button.Middle else e.Button
                    env.Emit [PointerDown(e.PointerId, btn, false, e.OffsetPosition)]
            ), pointerCapture = true)
            Dom.OnPointerUp((fun e ->
                if e.PointerType = PointerType.Mouse then
                    env.Emit [PointerUp(e.PointerId, false, e.OffsetPosition)]
            ), pointerCapture = true)
            Dom.OnPointerMove(fun e ->
                if e.PointerType = PointerType.Mouse then
                    env.Emit [PointerMove(e.PointerId, e.Button, false, e.OffsetPosition)]
            )
            Dom.OnContextMenu(ignore, preventDefault = true)

            Dom.OnTouchStart((fun e ->
                e.ChangedTouches |> HashMap.toList |> List.map (fun (id, t) ->
                    PointerDown(id, Button.None, true, t.OffsetPosition)
                ) |> env.Emit
            ), preventDefault = true)
            Dom.OnTouchMove((fun e ->
                e.ChangedTouches |> HashMap.toList |> List.map (fun (id, t) ->
                    PointerMove(id, Button.None, true, t.OffsetPosition)
                ) |> env.Emit
            ), preventDefault = true)
            Dom.OnTouchCancel((fun e ->
                e.ChangedTouches |> HashMap.toList |> List.map (fun (id, t) ->
                    PointerUp(id, true, t.OffsetPosition)
                ) |> env.Emit
            ), preventDefault = true)
            Dom.OnTouchEnd((fun e ->
                e.ChangedTouches |> HashMap.toList |> List.map (fun (id, t) ->
                    PointerUp(id, true, t.OffsetPosition)
                ) |> env.Emit
            ), preventDefault = true)
        }
