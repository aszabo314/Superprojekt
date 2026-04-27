//2cd722f4-1501-4abe-7823-0b89cdff75fa
//bf2e25e0-ec4d-b875-0d4a-f5d6ca71ccba
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
    let _SavedMenuOpen_ = FSharp.Data.Adaptive.cval(value.SavedMenuOpen)
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
    let _ReferenceAxis_ = FSharp.Data.Adaptive.cval(value.ReferenceAxis)
    let _Explore_ = FSharp.Data.Adaptive.cval(value.Explore)
    let _ColorMode_ = FSharp.Data.Adaptive.cval(value.ColorMode)
    let _CardSystem_ = AdaptiveCardSystemModel(value.CardSystem)
    let _RenderingMode_ = FSharp.Data.Adaptive.cval(value.RenderingMode)
    let _MeshSolo_ = FSharp.Data.Adaptive.cval(value.MeshSolo)
    let _RevolverSettings_ = FSharp.Data.Adaptive.cval(value.RevolverSettings)
    let _ExploreCardPos_ = FSharp.Data.Adaptive.cval(value.ExploreCardPos)
    let _GearPopoverOpen_ = FSharp.Data.Adaptive.cval(value.GearPopoverOpen)
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
            _SavedMenuOpen_.Value <- value.SavedMenuOpen
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
            _ReferenceAxis_.Value <- value.ReferenceAxis
            _Explore_.Value <- value.Explore
            _ColorMode_.Value <- value.ColorMode
            _CardSystem_.Update(value.CardSystem)
            _RenderingMode_.Value <- value.RenderingMode
            _MeshSolo_.Value <- value.MeshSolo
            _RevolverSettings_.Value <- value.RevolverSettings
            _ExploreCardPos_.Value <- value.ExploreCardPos
            _GearPopoverOpen_.Value <- value.GearPopoverOpen
    member __.Current = __adaptive
    member __.Camera = _Camera_
    member __.MeshOrder = _MeshOrder_ :> FSharp.Data.Adaptive.amap<Microsoft.FSharp.Core.string, Microsoft.FSharp.Core.int>
    member __.MeshNames = _MeshNames_ :> FSharp.Data.Adaptive.alist<Microsoft.FSharp.Core.string>
    member __.MeshVisible = _MeshVisible_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.Map<Microsoft.FSharp.Core.string, Microsoft.FSharp.Core.bool>>
    member __.MeshesLoaded = _MeshesLoaded_ :> FSharp.Data.Adaptive.aset<Microsoft.FSharp.Core.string>
    member __.CommonCentroid = _CommonCentroid_ :> FSharp.Data.Adaptive.aval<Aardvark.Base.V3d>
    member __.MenuOpen = _MenuOpen_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
    member __.SavedMenuOpen = _SavedMenuOpen_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<Microsoft.FSharp.Core.bool>>
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
    member __.ReferenceAxis = _ReferenceAxis_ :> FSharp.Data.Adaptive.aval<ReferenceAxisMode>
    member __.Explore = _Explore_ :> FSharp.Data.Adaptive.aval<ExploreMode>
    member __.ColorMode = _ColorMode_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
    member __.CardSystem = _CardSystem_
    member __.RenderingMode = _RenderingMode_ :> FSharp.Data.Adaptive.aval<RenderingMode>
    member __.MeshSolo = _MeshSolo_ :> FSharp.Data.Adaptive.aval<MeshSoloState>
    member __.RevolverSettings = _RevolverSettings_ :> FSharp.Data.Adaptive.aval<RevolverSettings>
    member __.ExploreCardPos = _ExploreCardPos_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<Aardvark.Base.V2d>>
    member __.GearPopoverOpen = _GearPopoverOpen_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>

