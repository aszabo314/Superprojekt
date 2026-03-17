//ddcf501b-1839-ef0c-e16d-665722e7e4ed
//ca4046ab-c66f-53f8-44b5-e793f89c32cd
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
type AdaptiveModel(value : Model) =
    let _Camera_ = AdaptiveOrbitState(value.Camera)
    let _Value_ = FSharp.Data.Adaptive.cval(value.Value)
    let _Hover_ = FSharp.Data.Adaptive.cval(value.Hover)
    let _Points_ = FSharp.Data.Adaptive.clist(value.Points)
    let _DraggingPoint_ = FSharp.Data.Adaptive.cval(value.DraggingPoint)
    let _MeshNames_ = FSharp.Data.Adaptive.clist(value.MeshNames)
    let _MeshVisible_ = FSharp.Data.Adaptive.cval(value.MeshVisible)
    let _CommonCentroid_ = FSharp.Data.Adaptive.cval(value.CommonCentroid)
    let _MenuOpen_ = FSharp.Data.Adaptive.cval(value.MenuOpen)
    let mutable __value = value
    let __adaptive = FSharp.Data.Adaptive.AVal.custom((fun (token : FSharp.Data.Adaptive.AdaptiveToken) -> __value))
    static member Create(value : Model) = AdaptiveModel(value)
    static member Unpersist = Adaptify.Unpersist.create (fun (value : Model) -> AdaptiveModel(value)) (fun (adaptive : AdaptiveModel) (value : Model) -> adaptive.Update(value))
    member __.Update(value : Model) =
        if Microsoft.FSharp.Core.Operators.not((FSharp.Data.Adaptive.ShallowEqualityComparer<Model>.ShallowEquals(value, __value))) then
            __value <- value
            __adaptive.MarkOutdated()
            _Camera_.Update(value.Camera)
            _Value_.Value <- value.Value
            _Hover_.Value <- value.Hover
            _Points_.Value <- value.Points
            _DraggingPoint_.Value <- value.DraggingPoint
            _MeshNames_.Value <- value.MeshNames
            _MeshVisible_.Value <- value.MeshVisible
            _CommonCentroid_.Value <- value.CommonCentroid
            _MenuOpen_.Value <- value.MenuOpen
    member __.Current = __adaptive
    member __.Camera = _Camera_
    member __.Value = _Value_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.int>
    member __.Hover = _Hover_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<Aardvark.Base.V3d>>
    member __.Points = _Points_ :> FSharp.Data.Adaptive.alist<Aardvark.Base.V3d>
    member __.DraggingPoint = _DraggingPoint_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<(FSharp.Data.Adaptive.Index * Aardvark.Base.V3d)>>
    member __.MeshNames = _MeshNames_ :> FSharp.Data.Adaptive.alist<Microsoft.FSharp.Core.string>
    member __.MeshVisible = _MeshVisible_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.Map<Microsoft.FSharp.Core.string, Microsoft.FSharp.Core.bool>>
    member __.CommonCentroid = _CommonCentroid_ :> FSharp.Data.Adaptive.aval<Aardvark.Base.V3d>
    member __.MenuOpen = _MenuOpen_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>

