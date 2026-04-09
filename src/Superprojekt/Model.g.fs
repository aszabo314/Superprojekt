//27bb1c41-9b06-f8ef-9fd6-d4ec8f28a36d
//8917b16d-8005-0c6b-ff66-9575c48c838e
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
    let _MeshesLoaded_ = FSharp.Data.Adaptive.cset(value.MeshesLoaded)
    let _CommonCentroid_ = FSharp.Data.Adaptive.cval(value.CommonCentroid)
    let _MenuOpen_ = FSharp.Data.Adaptive.cval(value.MenuOpen)
    let _Filtered_ = FSharp.Data.Adaptive.cmap(value.Filtered)
    let _FilterCenter_ = FSharp.Data.Adaptive.cval(value.FilterCenter)
    let _DebugLog_ = FSharp.Data.Adaptive.clist(value.DebugLog)
    let _Datasets_ = FSharp.Data.Adaptive.cval(value.Datasets)
    let _ActiveDataset_ = FSharp.Data.Adaptive.cval(value.ActiveDataset)
    let _DatasetScales_ = FSharp.Data.Adaptive.cval(value.DatasetScales)
    let _DatasetCentroids_ = FSharp.Data.Adaptive.cval(value.DatasetCentroids)
    let _RevolverOn_ = FSharp.Data.Adaptive.cval(value.RevolverOn)
    let _FullscreenOn_ = FSharp.Data.Adaptive.cval(value.FullscreenOn)
    let _RevolverCenter_ = FSharp.Data.Adaptive.cval(value.RevolverCenter)
    let _DifferenceRendering_ = FSharp.Data.Adaptive.cval(value.DifferenceRendering)
    let _MinDifferenceDepth_ = FSharp.Data.Adaptive.cval(value.MinDifferenceDepth)
    let _MaxDifferenceDepth_ = FSharp.Data.Adaptive.cval(value.MaxDifferenceDepth)
    let _GhostSilhouette_ = FSharp.Data.Adaptive.cval(value.GhostSilhouette)
    let _GhostOpacity_ = FSharp.Data.Adaptive.cval(value.GhostOpacity)
    let _ClipActive_ = FSharp.Data.Adaptive.cval(value.ClipActive)
    let _ClipBox_ = FSharp.Data.Adaptive.cval(value.ClipBox)
    let _ClipBounds_ = FSharp.Data.Adaptive.cval(value.ClipBounds)
    let _ScanPins_ = AdaptiveScanPinModel(value.ScanPins)
    let _PinAxisVertical_ = FSharp.Data.Adaptive.cval(value.PinAxisVertical)
    let _CoreSampleViewMode_ = FSharp.Data.Adaptive.cval(value.CoreSampleViewMode)
    let _CoreSampleRotation_ = FSharp.Data.Adaptive.cval(value.CoreSampleRotation)
    let _CoreSamplePanZ_ = FSharp.Data.Adaptive.cval(value.CoreSamplePanZ)
    let _CoreSampleZoom_ = FSharp.Data.Adaptive.cval(value.CoreSampleZoom)
    let _DepthShadeOn_ = FSharp.Data.Adaptive.cval(value.DepthShadeOn)
    let _IsolinesOn_ = FSharp.Data.Adaptive.cval(value.IsolinesOn)
    let _ColorMode_ = FSharp.Data.Adaptive.cval(value.ColorMode)
    let _CardSystem_ = AdaptiveCardSystemModel(value.CardSystem)
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
            _MeshesLoaded_.Value <- value.MeshesLoaded
            _CommonCentroid_.Value <- value.CommonCentroid
            _MenuOpen_.Value <- value.MenuOpen
            _Filtered_.Value <- value.Filtered
            _FilterCenter_.Value <- value.FilterCenter
            _DebugLog_.Value <- value.DebugLog
            _Datasets_.Value <- value.Datasets
            _ActiveDataset_.Value <- value.ActiveDataset
            _DatasetScales_.Value <- value.DatasetScales
            _DatasetCentroids_.Value <- value.DatasetCentroids
            _RevolverOn_.Value <- value.RevolverOn
            _FullscreenOn_.Value <- value.FullscreenOn
            _RevolverCenter_.Value <- value.RevolverCenter
            _DifferenceRendering_.Value <- value.DifferenceRendering
            _MinDifferenceDepth_.Value <- value.MinDifferenceDepth
            _MaxDifferenceDepth_.Value <- value.MaxDifferenceDepth
            _GhostSilhouette_.Value <- value.GhostSilhouette
            _GhostOpacity_.Value <- value.GhostOpacity
            _ClipActive_.Value <- value.ClipActive
            _ClipBox_.Value <- value.ClipBox
            _ClipBounds_.Value <- value.ClipBounds
            _ScanPins_.Update(value.ScanPins)
            _PinAxisVertical_.Value <- value.PinAxisVertical
            _CoreSampleViewMode_.Value <- value.CoreSampleViewMode
            _CoreSampleRotation_.Value <- value.CoreSampleRotation
            _CoreSamplePanZ_.Value <- value.CoreSamplePanZ
            _CoreSampleZoom_.Value <- value.CoreSampleZoom
            _DepthShadeOn_.Value <- value.DepthShadeOn
            _IsolinesOn_.Value <- value.IsolinesOn
            _ColorMode_.Value <- value.ColorMode
            _CardSystem_.Update(value.CardSystem)
    member __.Current = __adaptive
    member __.Camera = _Camera_
    member __.MeshOrder = _MeshOrder_ :> FSharp.Data.Adaptive.amap<Microsoft.FSharp.Core.string, Microsoft.FSharp.Core.int>
    member __.MeshNames = _MeshNames_ :> FSharp.Data.Adaptive.alist<Microsoft.FSharp.Core.string>
    member __.MeshVisible = _MeshVisible_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.Map<Microsoft.FSharp.Core.string, Microsoft.FSharp.Core.bool>>
    member __.MeshesLoaded = _MeshesLoaded_ :> FSharp.Data.Adaptive.aset<Microsoft.FSharp.Core.string>
    member __.CommonCentroid = _CommonCentroid_ :> FSharp.Data.Adaptive.aval<Aardvark.Base.V3d>
    member __.MenuOpen = _MenuOpen_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
    member __.Filtered = _Filtered_ :> FSharp.Data.Adaptive.amap<Microsoft.FSharp.Core.string, (Microsoft.FSharp.Core.int)[]>
    member __.FilterCenter = _FilterCenter_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<Aardvark.Base.V3d>>
    member __.DebugLog = _DebugLog_ :> FSharp.Data.Adaptive.alist<Microsoft.FSharp.Core.string>
    member __.Datasets = _Datasets_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.list<Microsoft.FSharp.Core.string>>
    member __.ActiveDataset = _ActiveDataset_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<Microsoft.FSharp.Core.string>>
    member __.DatasetScales = _DatasetScales_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.Map<Microsoft.FSharp.Core.string, Microsoft.FSharp.Core.float>>
    member __.DatasetCentroids = _DatasetCentroids_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.Map<Microsoft.FSharp.Core.string, Aardvark.Base.V3d>>
    member __.RevolverOn = _RevolverOn_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
    member __.FullscreenOn = _FullscreenOn_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
    member __.RevolverCenter = _RevolverCenter_ :> FSharp.Data.Adaptive.aval<Aardvark.Base.V2d>
    member __.DifferenceRendering = _DifferenceRendering_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
    member __.MinDifferenceDepth = _MinDifferenceDepth_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.float>
    member __.MaxDifferenceDepth = _MaxDifferenceDepth_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.float>
    member __.GhostSilhouette = _GhostSilhouette_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
    member __.GhostOpacity = _GhostOpacity_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.float>
    member __.ClipActive = _ClipActive_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
    member __.ClipBox = _ClipBox_ :> FSharp.Data.Adaptive.aval<Aardvark.Base.Box3d>
    member __.ClipBounds = _ClipBounds_ :> FSharp.Data.Adaptive.aval<Aardvark.Base.Box3d>
    member __.ScanPins = _ScanPins_
    member __.PinAxisVertical = _PinAxisVertical_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
    member __.CoreSampleViewMode = _CoreSampleViewMode_ :> FSharp.Data.Adaptive.aval<CoreSampleViewMode>
    member __.CoreSampleRotation = _CoreSampleRotation_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.float>
    member __.CoreSamplePanZ = _CoreSamplePanZ_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.float>
    member __.CoreSampleZoom = _CoreSampleZoom_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.float>
    member __.DepthShadeOn = _DepthShadeOn_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
    member __.IsolinesOn = _IsolinesOn_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
    member __.ColorMode = _ColorMode_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
    member __.CardSystem = _CardSystem_

