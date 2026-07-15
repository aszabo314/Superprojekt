//79abd2c8-81f2-2d34-6951-8a4c12685307
//58d15998-b6cf-2a2f-b381-f0751c8a0c3f
#nowarn "49" // upper case patterns
#nowarn "66" // upcast is unncecessary
#nowarn "1337" // internal types
#nowarn "1182" // value is unused
namespace rec Superprojekt

open System
open FSharp.Data.Adaptive
open Adaptify
open Superprojekt
[<System.Diagnostics.CodeAnalysis.SuppressMessage("NameConventions", "*")>]
type AdaptiveOrbitState(value : OrbitState) =
    let _sky_ = FSharp.Data.Adaptive.cval(value.sky)
    let _center_ = FSharp.Data.Adaptive.cval(value.center)
    let _phi_ = FSharp.Data.Adaptive.cval(value.phi)
    let _theta_ = FSharp.Data.Adaptive.cval(value.theta)
    let _radius_ = FSharp.Data.Adaptive.cval(value.radius)
    let _centerAnimation_ = FSharp.Data.Adaptive.cval(value.centerAnimation)
    let _locationAnimation_ = FSharp.Data.Adaptive.cval(value.locationAnimation)
    let _targetPhi_ = FSharp.Data.Adaptive.cval(value.targetPhi)
    let _targetTheta_ = FSharp.Data.Adaptive.cval(value.targetTheta)
    let _targetRadius_ = FSharp.Data.Adaptive.cval(value.targetRadius)
    let _dragStarts_ = FSharp.Data.Adaptive.cval(value.dragStarts)
    let _view_ = FSharp.Data.Adaptive.cval(value.view)
    let _radiusRange_ = FSharp.Data.Adaptive.cval(value.radiusRange)
    let _thetaRange_ = FSharp.Data.Adaptive.cval(value.thetaRange)
    let _speed_ = FSharp.Data.Adaptive.cval(value.speed)
    let mutable __value = value
    let __adaptive = FSharp.Data.Adaptive.AVal.custom((fun (token : FSharp.Data.Adaptive.AdaptiveToken) -> __value))
    static member Create(value : OrbitState) = AdaptiveOrbitState(value)
    static member Unpersist = Adaptify.Unpersist.create (fun (value : OrbitState) -> AdaptiveOrbitState(value)) (fun (adaptive : AdaptiveOrbitState) (value : OrbitState) -> adaptive.Update(value))
    member __.Update(value : OrbitState) =
        if Microsoft.FSharp.Core.Operators.not((FSharp.Data.Adaptive.ShallowEqualityComparer<OrbitState>.ShallowEquals(value, __value))) then
            __value <- value
            __adaptive.MarkOutdated()
            _sky_.Value <- value.sky
            _center_.Value <- value.center
            _phi_.Value <- value.phi
            _theta_.Value <- value.theta
            _radius_.Value <- value.radius
            _centerAnimation_.Value <- value.centerAnimation
            _locationAnimation_.Value <- value.locationAnimation
            _targetPhi_.Value <- value.targetPhi
            _targetTheta_.Value <- value.targetTheta
            _targetRadius_.Value <- value.targetRadius
            _dragStarts_.Value <- value.dragStarts
            _view_.Value <- value.view
            _radiusRange_.Value <- value.radiusRange
            _thetaRange_.Value <- value.thetaRange
            _speed_.Value <- value.speed
    member __.Current = __adaptive
    member __.sky = _sky_ :> FSharp.Data.Adaptive.aval<Aardvark.Base.V3d>
    member __.center = _center_ :> FSharp.Data.Adaptive.aval<Aardvark.Base.V3d>
    member __.phi = _phi_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.float>
    member __.theta = _theta_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.float>
    member __.radius = _radius_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.float>
    member __.centerAnimation = _centerAnimation_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.Option<Animation<Aardvark.Base.V3d>>>
    member __.locationAnimation = _locationAnimation_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.Option<Animation<Aardvark.Base.V3d>>>
    member __.targetPhi = _targetPhi_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.float>
    member __.targetTheta = _targetTheta_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.float>
    member __.targetRadius = _targetRadius_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.float>
    member __.rotateButton = __value.rotateButton
    member __.panButton = __value.panButton
    member __.dragStarts = _dragStarts_ :> FSharp.Data.Adaptive.aval<Aardvark.Base.MapExt<Microsoft.FSharp.Core.int, (Aardvark.Base.V2i * Aardvark.Dom.Button)>>
    member __.lastRender = __value.lastRender
    member __.view = _view_ :> FSharp.Data.Adaptive.aval<Aardvark.Rendering.CameraView>
    member __.radiusRange = _radiusRange_ :> FSharp.Data.Adaptive.aval<Aardvark.Base.V2d>
    member __.thetaRange = _thetaRange_ :> FSharp.Data.Adaptive.aval<Aardvark.Base.V2d>
    member __.speed = _speed_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.float>

