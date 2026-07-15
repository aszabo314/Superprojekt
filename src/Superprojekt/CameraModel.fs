namespace Superprojekt

open Aardvark.Base
open Adaptify
open Aardvark.Rendering
open Aardvark.Dom

[<RequireQualifiedAccess>]
type AnimationKind =
    | Exp
    | Tanh

type Animation<'a> =
    {
        kind        : AnimationKind
        startTime   : MicroTime
        stopTime    : MicroTime
        startValue  : 'a
        stopValue   : 'a
    }

module Animation =

    let getParameter (k : AnimationKind) (t : float) =
        match k with
        | AnimationKind.Exp  -> 1.0 - exp(-8.0 * t)
        | AnimationKind.Tanh -> tanh(t * 7.0 - 3.5) * 0.5 + 0.5

    let interpolate (now : MicroTime) (x : Animation<V3d>) : V3d =
        let t = (now - x.startTime) / (x.stopTime - x.startTime)
        if t < 0.0 then x.startValue
        elif t > 1.0 then x.stopValue
        else x.startValue + (x.stopValue - x.startValue) * getParameter x.kind t

[<ModelType>]
type OrbitState =
    {
        sky     : V3d
        center  : V3d
        phi     : float
        theta   : float
        radius  : float

        centerAnimation   : Option<Animation<V3d>>
        locationAnimation : Option<Animation<V3d>>

        targetPhi    : float
        targetTheta  : float
        targetRadius : float

        [<NonAdaptive>]
        rotateButton : Button
        [<NonAdaptive>]
        panButton : Button

        dragStarts : MapExt<int, V2i * Button>

        [<NonAdaptive>]
        lastRender : Option<MicroTime>

        view : CameraView

        radiusRange : V2d
        thetaRange  : V2d
        speed       : float
    }
