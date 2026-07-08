//7a194dff-3156-dcb6-fced-7c0b081dd561
//0ff7a7af-7a02-c7a5-1b85-2e704e18169a
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
type AdaptiveSelection(value : Selection) =
    let _SelectedPin_ = FSharp.Data.Adaptive.cval(value.SelectedPin)
    let _FocusedMesh_ = FSharp.Data.Adaptive.cval(value.FocusedMesh)
    let _Hovered_ = FSharp.Data.Adaptive.cval(value.Hovered)
    let mutable __value = value
    let __adaptive = FSharp.Data.Adaptive.AVal.custom((fun (token : FSharp.Data.Adaptive.AdaptiveToken) -> __value))
    static member Create(value : Selection) = AdaptiveSelection(value)
    static member Unpersist = Adaptify.Unpersist.create (fun (value : Selection) -> AdaptiveSelection(value)) (fun (adaptive : AdaptiveSelection) (value : Selection) -> adaptive.Update(value))
    member __.Update(value : Selection) =
        if Microsoft.FSharp.Core.Operators.not((FSharp.Data.Adaptive.ShallowEqualityComparer<Selection>.ShallowEquals(value, __value))) then
            __value <- value
            __adaptive.MarkOutdated()
            _SelectedPin_.Value <- value.SelectedPin
            _FocusedMesh_.Value <- value.FocusedMesh
            _Hovered_.Value <- value.Hovered
    member __.Current = __adaptive
    member __.SelectedPin = _SelectedPin_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<ScanPinId>>
    member __.FocusedMesh = _FocusedMesh_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<Microsoft.FSharp.Core.string>>
    member __.Hovered = _Hovered_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<HoverTarget>>
[<System.Diagnostics.CodeAnalysis.SuppressMessage("NameConventions", "*")>]
type AdaptiveModel(value : Model) =
    let _Camera_ = AdaptiveOrbitState(value.Camera)
    let _MeshOrder_ = FSharp.Data.Adaptive.cmap(value.MeshOrder)
    let _MeshNames_ = FSharp.Data.Adaptive.clist(value.MeshNames)
    let _MeshVisible_ = FSharp.Data.Adaptive.cval(value.MeshVisible)
    let _MeshesLoaded_ = FSharp.Data.Adaptive.cset(value.MeshesLoaded)
    let _CommonCentroid_ = FSharp.Data.Adaptive.cval(value.CommonCentroid)
    let _DebugLog_ = FSharp.Data.Adaptive.clist(value.DebugLog)
    let _Datasets_ = FSharp.Data.Adaptive.cval(value.Datasets)
    let _ActiveDataset_ = FSharp.Data.Adaptive.cval(value.ActiveDataset)
    let _DatasetScales_ = FSharp.Data.Adaptive.cval(value.DatasetScales)
    let _DatasetCentroids_ = FSharp.Data.Adaptive.cval(value.DatasetCentroids)
    let _PanoCenters_ = FSharp.Data.Adaptive.cval(value.PanoCenters)
    let _GhostSilhouette_ = FSharp.Data.Adaptive.cval(value.GhostSilhouette)
    let _GhostOpacity_ = FSharp.Data.Adaptive.cval(value.GhostOpacity)
    let _ShadingStrength_ = FSharp.Data.Adaptive.cval(value.ShadingStrength)
    let _SlopeThresholdDeg_ = FSharp.Data.Adaptive.cval(value.SlopeThresholdDeg)
    let _AnchorGhostMode_ = FSharp.Data.Adaptive.cval(value.AnchorGhostMode)
    let _QuickPinRadius_ = FSharp.Data.Adaptive.cval(value.QuickPinRadius)
    let _SceneBounds_ = FSharp.Data.Adaptive.cval(value.SceneBounds)
    let _MeshBounds_ = FSharp.Data.Adaptive.cval(value.MeshBounds)
    let _ActivePickingLayer_ = FSharp.Data.Adaptive.cval(value.ActivePickingLayer)
    let _ShowOverlaysHeld_ = FSharp.Data.Adaptive.cval(value.ShowOverlaysHeld)
    let _LoadTransforms_ = FSharp.Data.Adaptive.cval(value.LoadTransforms)
    let _SolvedTransforms_ = FSharp.Data.Adaptive.cval(value.SolvedTransforms)
    let _SolveInputs_ = FSharp.Data.Adaptive.cval(value.SolveInputs)
    let _RegView_ = FSharp.Data.Adaptive.cval(value.RegView)
    let _RegPeekHeld_ = FSharp.Data.Adaptive.cval(value.RegPeekHeld)
    let _Registration_ = FSharp.Data.Adaptive.cval(value.Registration)
    let _Toast_ = FSharp.Data.Adaptive.cval(value.Toast)
    let _MeshHeatmap_ = FSharp.Data.Adaptive.cval(value.MeshHeatmap)
    let _ExtrinsicZDiff_ = FSharp.Data.Adaptive.cval(value.ExtrinsicZDiff)
    let _SurfaceDistance_ = FSharp.Data.Adaptive.cval(value.SurfaceDistance)
    let _SurfaceDistanceOther_ = FSharp.Data.Adaptive.cval(value.SurfaceDistanceOther)
    let _FocusDist_ = FSharp.Data.Adaptive.cval(value.FocusDist)
    let _FocusDistOther_ = FSharp.Data.Adaptive.cval(value.FocusDistOther)
    let _ScanPins_ = AdaptiveScanPinModel(value.ScanPins)
    let _Selection_ = AdaptiveSelection(value.Selection)
    let _RenderingMode_ = FSharp.Data.Adaptive.cval(value.RenderingMode)
    let _MeshSolo_ = FSharp.Data.Adaptive.cval(value.MeshSolo)
    let _GearPopoverOpen_ = FSharp.Data.Adaptive.cval(value.GearPopoverOpen)
    let _WorkflowStep_ = FSharp.Data.Adaptive.cval(value.WorkflowStep)
    let _InspectChannel_ = FSharp.Data.Adaptive.cval(value.InspectChannel)
    let _FocusProjection_ = FSharp.Data.Adaptive.cval(value.FocusProjection)
    let _CorrArm_ = FSharp.Data.Adaptive.cval(value.CorrArm)
    let _CorrPreview_ = FSharp.Data.Adaptive.cval(value.CorrPreview)
    let _BrushedSamples_ = FSharp.Data.Adaptive.cval(value.BrushedSamples)
    let _OutlineThreshold_ = FSharp.Data.Adaptive.cval(value.OutlineThreshold)
    let _IsolineBands_ = FSharp.Data.Adaptive.cval(value.IsolineBands)
    let _LocateBackup_ = FSharp.Data.Adaptive.cval(value.LocateBackup)
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
            _DebugLog_.Value <- value.DebugLog
            _Datasets_.Value <- value.Datasets
            _ActiveDataset_.Value <- value.ActiveDataset
            _DatasetScales_.Value <- value.DatasetScales
            _DatasetCentroids_.Value <- value.DatasetCentroids
            _PanoCenters_.Value <- value.PanoCenters
            _GhostSilhouette_.Value <- value.GhostSilhouette
            _GhostOpacity_.Value <- value.GhostOpacity
            _ShadingStrength_.Value <- value.ShadingStrength
            _SlopeThresholdDeg_.Value <- value.SlopeThresholdDeg
            _AnchorGhostMode_.Value <- value.AnchorGhostMode
            _QuickPinRadius_.Value <- value.QuickPinRadius
            _SceneBounds_.Value <- value.SceneBounds
            _MeshBounds_.Value <- value.MeshBounds
            _ActivePickingLayer_.Value <- value.ActivePickingLayer
            _ShowOverlaysHeld_.Value <- value.ShowOverlaysHeld
            _LoadTransforms_.Value <- value.LoadTransforms
            _SolvedTransforms_.Value <- value.SolvedTransforms
            _SolveInputs_.Value <- value.SolveInputs
            _RegView_.Value <- value.RegView
            _RegPeekHeld_.Value <- value.RegPeekHeld
            _Registration_.Value <- value.Registration
            _Toast_.Value <- value.Toast
            _MeshHeatmap_.Value <- value.MeshHeatmap
            _ExtrinsicZDiff_.Value <- value.ExtrinsicZDiff
            _SurfaceDistance_.Value <- value.SurfaceDistance
            _SurfaceDistanceOther_.Value <- value.SurfaceDistanceOther
            _FocusDist_.Value <- value.FocusDist
            _FocusDistOther_.Value <- value.FocusDistOther
            _ScanPins_.Update(value.ScanPins)
            _Selection_.Update(value.Selection)
            _RenderingMode_.Value <- value.RenderingMode
            _MeshSolo_.Value <- value.MeshSolo
            _GearPopoverOpen_.Value <- value.GearPopoverOpen
            _WorkflowStep_.Value <- value.WorkflowStep
            _InspectChannel_.Value <- value.InspectChannel
            _FocusProjection_.Value <- value.FocusProjection
            _CorrArm_.Value <- value.CorrArm
            _CorrPreview_.Value <- value.CorrPreview
            _BrushedSamples_.Value <- value.BrushedSamples
            _OutlineThreshold_.Value <- value.OutlineThreshold
            _IsolineBands_.Value <- value.IsolineBands
            _LocateBackup_.Value <- value.LocateBackup
    member __.Current = __adaptive
    member __.Camera = _Camera_
    member __.MeshOrder = _MeshOrder_ :> FSharp.Data.Adaptive.amap<Microsoft.FSharp.Core.string, Microsoft.FSharp.Core.int>
    member __.MeshNames = _MeshNames_ :> FSharp.Data.Adaptive.alist<Microsoft.FSharp.Core.string>
    member __.MeshVisible = _MeshVisible_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.Map<Microsoft.FSharp.Core.string, Microsoft.FSharp.Core.bool>>
    member __.MeshesLoaded = _MeshesLoaded_ :> FSharp.Data.Adaptive.aset<Microsoft.FSharp.Core.string>
    member __.CommonCentroid = _CommonCentroid_ :> FSharp.Data.Adaptive.aval<Aardvark.Base.V3d>
    member __.DebugLog = _DebugLog_ :> FSharp.Data.Adaptive.alist<Microsoft.FSharp.Core.string>
    member __.Datasets = _Datasets_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.list<Microsoft.FSharp.Core.string>>
    member __.ActiveDataset = _ActiveDataset_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<Microsoft.FSharp.Core.string>>
    member __.DatasetScales = _DatasetScales_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.Map<Microsoft.FSharp.Core.string, Microsoft.FSharp.Core.float>>
    member __.DatasetCentroids = _DatasetCentroids_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.Map<Microsoft.FSharp.Core.string, Aardvark.Base.V3d>>
    member __.PanoCenters = _PanoCenters_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.Map<Microsoft.FSharp.Core.string, Aardvark.Base.V3d>>
    member __.GhostSilhouette = _GhostSilhouette_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
    member __.GhostOpacity = _GhostOpacity_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.float>
    member __.ShadingStrength = _ShadingStrength_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.float>
    member __.SlopeThresholdDeg = _SlopeThresholdDeg_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.float>
    member __.AnchorGhostMode = _AnchorGhostMode_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
    member __.QuickPinRadius = _QuickPinRadius_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.float>
    member __.SceneBounds = _SceneBounds_ :> FSharp.Data.Adaptive.aval<Aardvark.Base.Box3d>
    member __.MeshBounds = _MeshBounds_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.Map<Microsoft.FSharp.Core.string, Aardvark.Base.Box3d>>
    member __.ActivePickingLayer = _ActivePickingLayer_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<Microsoft.FSharp.Core.string>>
    member __.ShowOverlaysHeld = _ShowOverlaysHeld_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
    member __.LoadTransforms = _LoadTransforms_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.Map<Microsoft.FSharp.Core.string, Aardvark.Base.Trafo3d>>
    member __.SolvedTransforms = _SolvedTransforms_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.Map<Microsoft.FSharp.Core.string, Aardvark.Base.Trafo3d>>
    member __.SolveInputs = _SolveInputs_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<SolveInputs>>
    member __.RegView = _RegView_ :> FSharp.Data.Adaptive.aval<RegView>
    member __.RegPeekHeld = _RegPeekHeld_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
    member __.Registration = _Registration_ :> FSharp.Data.Adaptive.aval<RegistrationState>
    member __.Toast = _Toast_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<Microsoft.FSharp.Core.string>>
    member __.MeshHeatmap = _MeshHeatmap_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.Map<Microsoft.FSharp.Core.string, HeatmapMode>>
    member __.ExtrinsicZDiff = _ExtrinsicZDiff_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
    member __.SurfaceDistance = _SurfaceDistance_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.Map<Microsoft.FSharp.Core.string, (Microsoft.FSharp.Core.float32)[]>>
    member __.SurfaceDistanceOther = _SurfaceDistanceOther_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.Map<Microsoft.FSharp.Core.string, (Microsoft.FSharp.Core.float32)[]>>
    member __.FocusDist = _FocusDist_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.Map<Microsoft.FSharp.Core.string, (Microsoft.FSharp.Core.float32)[]>>
    member __.FocusDistOther = _FocusDistOther_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.Map<Microsoft.FSharp.Core.string, (Microsoft.FSharp.Core.float32)[]>>
    member __.ScanPins = _ScanPins_
    member __.Selection = _Selection_
    member __.RenderingMode = _RenderingMode_ :> FSharp.Data.Adaptive.aval<RenderingMode>
    member __.MeshSolo = _MeshSolo_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<Microsoft.FSharp.Core.string>>
    member __.GearPopoverOpen = _GearPopoverOpen_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
    member __.WorkflowStep = _WorkflowStep_ :> FSharp.Data.Adaptive.aval<WorkflowStep>
    member __.InspectChannel = _InspectChannel_ :> FSharp.Data.Adaptive.aval<InspectChannel>
    member __.FocusProjection = _FocusProjection_ :> FSharp.Data.Adaptive.aval<FocusProjection>
    member __.CorrArm = _CorrArm_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<(ScanPinId * Microsoft.FSharp.Core.string)>>
    member __.CorrPreview = _CorrPreview_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<Aardvark.Base.V3d>>
    member __.BrushedSamples = _BrushedSamples_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.Set<Microsoft.FSharp.Core.int>>
    member __.OutlineThreshold = _OutlineThreshold_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.float>
    member __.IsolineBands = _IsolineBands_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.float>
    member __.LocateBackup = _LocateBackup_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<LocateState>>

