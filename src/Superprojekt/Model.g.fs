//6a7a8a50-0222-070f-2e43-ff0de5c5453b
//30dc3a6d-35e7-e65f-45cd-0ac07c153100
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
    let _MeshOrder_ = FSharp.Data.Adaptive.cmap(value.MeshOrder)
    let _MeshNames_ = FSharp.Data.Adaptive.clist(value.MeshNames)
    let _MeshVisible_ = FSharp.Data.Adaptive.cval(value.MeshVisible)
    let _CommonCentroid_ = FSharp.Data.Adaptive.cval(value.CommonCentroid)
    let _MenuOpen_ = FSharp.Data.Adaptive.cval(value.MenuOpen)
    let _Filtered_ = FSharp.Data.Adaptive.cmap(value.Filtered)
    let _FilterCenter_ = FSharp.Data.Adaptive.cval(value.FilterCenter)
    let _DebugLog_ = FSharp.Data.Adaptive.clist(value.DebugLog)
    let _RevolverOn_ = FSharp.Data.Adaptive.cval(value.RevolverOn)
    let _FullscreenOn_ = FSharp.Data.Adaptive.cval(value.FullscreenOn)
    let _RevolverCenter_ = FSharp.Data.Adaptive.cval(value.RevolverCenter)
    let _DifferenceRendering_ = FSharp.Data.Adaptive.cval(value.DifferenceRendering)
    let _MinDifferenceDepth_ = FSharp.Data.Adaptive.cval(value.MinDifferenceDepth)
    let _MaxDifferenceDepth_ = FSharp.Data.Adaptive.cval(value.MaxDifferenceDepth)
    let _GhostSilhouette_ = FSharp.Data.Adaptive.cval(value.GhostSilhouette)
    let _ClipBox_ = FSharp.Data.Adaptive.cval(value.ClipBox)
    let _ClipBounds_ = FSharp.Data.Adaptive.cval(value.ClipBounds)
    let mutable __value = value
    let __adaptive = FSharp.Data.Adaptive.AVal.custom((fun (token : FSharp.Data.Adaptive.AdaptiveToken) -> __value))
    static member Create(value : Model) = AdaptiveModel(value)
    static member Unpersist = Adaptify.Unpersist.create (fun (value : Model) -> AdaptiveModel(value)) (fun (adaptive : AdaptiveModel) (value : Model) -> adaptive.Update(value))
    member __.Update(value : Model) =
        if Microsoft.FSharp.Core.Operators.not((FSharp.Data.Adaptive.ShallowEqualityComparer<Model>.ShallowEquals(value, __value))) then
            __value <- value
            __adaptive.MarkOutdated()
            _Camera_.Update(value.Camera)
            _MeshOrder_.Value <- value.MeshOrder
            _MeshNames_.Value <- value.MeshNames
            _MeshVisible_.Value <- value.MeshVisible
            _CommonCentroid_.Value <- value.CommonCentroid
            _MenuOpen_.Value <- value.MenuOpen
            _Filtered_.Value <- value.Filtered
            _FilterCenter_.Value <- value.FilterCenter
            _DebugLog_.Value <- value.DebugLog
            _RevolverOn_.Value <- value.RevolverOn
            _FullscreenOn_.Value <- value.FullscreenOn
            _RevolverCenter_.Value <- value.RevolverCenter
            _DifferenceRendering_.Value <- value.DifferenceRendering
            _MinDifferenceDepth_.Value <- value.MinDifferenceDepth
            _MaxDifferenceDepth_.Value <- value.MaxDifferenceDepth
            _GhostSilhouette_.Value <- value.GhostSilhouette
            _ClipBox_.Value <- value.ClipBox
            _ClipBounds_.Value <- value.ClipBounds
    member __.Current = __adaptive
    member __.Camera = _Camera_
    member __.MeshOrder = _MeshOrder_ :> FSharp.Data.Adaptive.amap<Microsoft.FSharp.Core.string, Microsoft.FSharp.Core.int>
    member __.MeshNames = _MeshNames_ :> FSharp.Data.Adaptive.alist<Microsoft.FSharp.Core.string>
    member __.MeshVisible = _MeshVisible_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.Map<Microsoft.FSharp.Core.string, Microsoft.FSharp.Core.bool>>
    member __.CommonCentroid = _CommonCentroid_ :> FSharp.Data.Adaptive.aval<Aardvark.Base.V3d>
    member __.MenuOpen = _MenuOpen_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
    member __.Filtered = _Filtered_ :> FSharp.Data.Adaptive.amap<Microsoft.FSharp.Core.string, (Microsoft.FSharp.Core.int)[]>
    member __.FilterCenter = _FilterCenter_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<Aardvark.Base.V3d>>
    member __.DebugLog = _DebugLog_ :> FSharp.Data.Adaptive.alist<Microsoft.FSharp.Core.string>
    member __.RevolverOn = _RevolverOn_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
    member __.FullscreenOn = _FullscreenOn_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
    member __.RevolverCenter = _RevolverCenter_ :> FSharp.Data.Adaptive.aval<Aardvark.Base.V2d>
    member __.DifferenceRendering = _DifferenceRendering_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
    member __.MinDifferenceDepth = _MinDifferenceDepth_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.float>
    member __.MaxDifferenceDepth = _MaxDifferenceDepth_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.float>
    member __.GhostSilhouette = _GhostSilhouette_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
    member __.ClipBox = _ClipBox_ :> FSharp.Data.Adaptive.aval<Aardvark.Base.Box3d>
    member __.ClipBounds = _ClipBounds_ :> FSharp.Data.Adaptive.aval<Aardvark.Base.Box3d>

