//caded9ab-5c4d-9d2d-d364-5c1cc5208d9a
//0c327d25-2013-4c60-fba9-c5847c18a58c
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
    let _DebugLog_ = FSharp.Data.Adaptive.clist(value.DebugLog)
    let _Datasets_ = FSharp.Data.Adaptive.cval(value.Datasets)
    let _ActiveDataset_ = FSharp.Data.Adaptive.cval(value.ActiveDataset)
    let _DatasetScales_ = FSharp.Data.Adaptive.cval(value.DatasetScales)
    let _DatasetCentroids_ = FSharp.Data.Adaptive.cval(value.DatasetCentroids)
    let _GhostSilhouette_ = FSharp.Data.Adaptive.cval(value.GhostSilhouette)
    let _GhostOpacity_ = FSharp.Data.Adaptive.cval(value.GhostOpacity)
    let _ShadingStrength_ = FSharp.Data.Adaptive.cval(value.ShadingStrength)
    let _SlopeThresholdDeg_ = FSharp.Data.Adaptive.cval(value.SlopeThresholdDeg)
    let _AnchorGhostMode_ = FSharp.Data.Adaptive.cval(value.AnchorGhostMode)
    let _QuickPinRadius_ = FSharp.Data.Adaptive.cval(value.QuickPinRadius)
    let _SceneBounds_ = FSharp.Data.Adaptive.cval(value.SceneBounds)
    let _MeshBounds_ = FSharp.Data.Adaptive.cval(value.MeshBounds)
    let _ActivePickingLayer_ = FSharp.Data.Adaptive.cval(value.ActivePickingLayer)
    let _ReferencePeekHeld_ = FSharp.Data.Adaptive.cval(value.ReferencePeekHeld)
    let _MeshTransforms_ = FSharp.Data.Adaptive.cval(value.MeshTransforms)
    let _Registration_ = FSharp.Data.Adaptive.cval(value.Registration)
    let _PendingReg_ = FSharp.Data.Adaptive.cval(value.PendingReg)
    let _LastSolve_ = FSharp.Data.Adaptive.cval(value.LastSolve)
    let _Toast_ = FSharp.Data.Adaptive.cval(value.Toast)
    let _MeshSensorTypes_ = FSharp.Data.Adaptive.cval(value.MeshSensorTypes)
    let _HeatmapMode_ = FSharp.Data.Adaptive.cval(value.HeatmapMode)
    let _SurfaceDistOn_ = FSharp.Data.Adaptive.cval(value.SurfaceDistOn)
    let _ExtrinsicZDiff_ = FSharp.Data.Adaptive.cval(value.ExtrinsicZDiff)
    let _VarianceOn_ = FSharp.Data.Adaptive.cval(value.VarianceOn)
    let _SurfaceDistance_ = FSharp.Data.Adaptive.cval(value.SurfaceDistance)
    let _ScanPins_ = AdaptiveScanPinModel(value.ScanPins)
    let _InspectorMesh_ = FSharp.Data.Adaptive.cval(value.InspectorMesh)
    let _WorkflowPinHover_ = FSharp.Data.Adaptive.cval(value.WorkflowPinHover)
    let _CorrRowHover_ = FSharp.Data.Adaptive.cval(value.CorrRowHover)
    let _RenderingMode_ = FSharp.Data.Adaptive.cval(value.RenderingMode)
    let _MeshSolo_ = FSharp.Data.Adaptive.cval(value.MeshSolo)
    let _GearPopoverOpen_ = FSharp.Data.Adaptive.cval(value.GearPopoverOpen)
    let _WorkflowStep_ = FSharp.Data.Adaptive.cval(value.WorkflowStep)
    let _FocusProjection_ = FSharp.Data.Adaptive.cval(value.FocusProjection)
    let _FocusMesh_ = FSharp.Data.Adaptive.cval(value.FocusMesh)
    let _FocusMaps_ = FSharp.Data.Adaptive.cval(value.FocusMaps)
    let _FocusPeekReference_ = FSharp.Data.Adaptive.cval(value.FocusPeekReference)
    let _PinFocusMode_ = FSharp.Data.Adaptive.cval(value.PinFocusMode)
    let _MovementLayer_ = FSharp.Data.Adaptive.cval(value.MovementLayer)
    let _OutlineMode_ = FSharp.Data.Adaptive.cval(value.OutlineMode)
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
            _DebugLog_.Value <- value.DebugLog
            _Datasets_.Value <- value.Datasets
            _ActiveDataset_.Value <- value.ActiveDataset
            _DatasetScales_.Value <- value.DatasetScales
            _DatasetCentroids_.Value <- value.DatasetCentroids
            _GhostSilhouette_.Value <- value.GhostSilhouette
            _GhostOpacity_.Value <- value.GhostOpacity
            _ShadingStrength_.Value <- value.ShadingStrength
            _SlopeThresholdDeg_.Value <- value.SlopeThresholdDeg
            _AnchorGhostMode_.Value <- value.AnchorGhostMode
            _QuickPinRadius_.Value <- value.QuickPinRadius
            _SceneBounds_.Value <- value.SceneBounds
            _MeshBounds_.Value <- value.MeshBounds
            _ActivePickingLayer_.Value <- value.ActivePickingLayer
            _ReferencePeekHeld_.Value <- value.ReferencePeekHeld
            _MeshTransforms_.Value <- value.MeshTransforms
            _Registration_.Value <- value.Registration
            _PendingReg_.Value <- value.PendingReg
            _LastSolve_.Value <- value.LastSolve
            _Toast_.Value <- value.Toast
            _MeshSensorTypes_.Value <- value.MeshSensorTypes
            _HeatmapMode_.Value <- value.HeatmapMode
            _SurfaceDistOn_.Value <- value.SurfaceDistOn
            _ExtrinsicZDiff_.Value <- value.ExtrinsicZDiff
            _VarianceOn_.Value <- value.VarianceOn
            _SurfaceDistance_.Value <- value.SurfaceDistance
            _ScanPins_.Update(value.ScanPins)
            _InspectorMesh_.Value <- value.InspectorMesh
            _WorkflowPinHover_.Value <- value.WorkflowPinHover
            _CorrRowHover_.Value <- value.CorrRowHover
            _RenderingMode_.Value <- value.RenderingMode
            _MeshSolo_.Value <- value.MeshSolo
            _GearPopoverOpen_.Value <- value.GearPopoverOpen
            _WorkflowStep_.Value <- value.WorkflowStep
            _FocusProjection_.Value <- value.FocusProjection
            _FocusMesh_.Value <- value.FocusMesh
            _FocusMaps_.Value <- value.FocusMaps
            _FocusPeekReference_.Value <- value.FocusPeekReference
            _PinFocusMode_.Value <- value.PinFocusMode
            _MovementLayer_.Value <- value.MovementLayer
            _OutlineMode_.Value <- value.OutlineMode
    member __.Current = __adaptive
    member __.Camera = _Camera_
    member __.MeshOrder = _MeshOrder_ :> FSharp.Data.Adaptive.amap<Microsoft.FSharp.Core.string, Microsoft.FSharp.Core.int>
    member __.MeshNames = _MeshNames_ :> FSharp.Data.Adaptive.alist<Microsoft.FSharp.Core.string>
    member __.MeshVisible = _MeshVisible_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.Map<Microsoft.FSharp.Core.string, Microsoft.FSharp.Core.bool>>
    member __.MeshesLoaded = _MeshesLoaded_ :> FSharp.Data.Adaptive.aset<Microsoft.FSharp.Core.string>
    member __.CommonCentroid = _CommonCentroid_ :> FSharp.Data.Adaptive.aval<Aardvark.Base.V3d>
    member __.MenuOpen = _MenuOpen_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
    member __.SavedMenuOpen = _SavedMenuOpen_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<Microsoft.FSharp.Core.bool>>
    member __.DebugLog = _DebugLog_ :> FSharp.Data.Adaptive.alist<Microsoft.FSharp.Core.string>
    member __.Datasets = _Datasets_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.list<Microsoft.FSharp.Core.string>>
    member __.ActiveDataset = _ActiveDataset_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<Microsoft.FSharp.Core.string>>
    member __.DatasetScales = _DatasetScales_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.Map<Microsoft.FSharp.Core.string, Microsoft.FSharp.Core.float>>
    member __.DatasetCentroids = _DatasetCentroids_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.Map<Microsoft.FSharp.Core.string, Aardvark.Base.V3d>>
    member __.GhostSilhouette = _GhostSilhouette_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
    member __.GhostOpacity = _GhostOpacity_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.float>
    member __.ShadingStrength = _ShadingStrength_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.float>
    member __.SlopeThresholdDeg = _SlopeThresholdDeg_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.float>
    member __.AnchorGhostMode = _AnchorGhostMode_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
    member __.QuickPinRadius = _QuickPinRadius_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.float>
    member __.SceneBounds = _SceneBounds_ :> FSharp.Data.Adaptive.aval<Aardvark.Base.Box3d>
    member __.MeshBounds = _MeshBounds_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.Map<Microsoft.FSharp.Core.string, Aardvark.Base.Box3d>>
    member __.ActivePickingLayer = _ActivePickingLayer_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<Microsoft.FSharp.Core.string>>
    member __.ReferencePeekHeld = _ReferencePeekHeld_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
    member __.MeshTransforms = _MeshTransforms_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.Map<Microsoft.FSharp.Core.string, Aardvark.Base.Trafo3d>>
    member __.Registration = _Registration_ :> FSharp.Data.Adaptive.aval<RegistrationState>
    member __.PendingReg = _PendingReg_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<PendingRegistration>>
    member __.LastSolve = _LastSolve_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.Map<Microsoft.FSharp.Core.string, LastSolveEntry>>
    member __.Toast = _Toast_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<Microsoft.FSharp.Core.string>>
    member __.MeshSensorTypes = _MeshSensorTypes_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.Map<Microsoft.FSharp.Core.string, SensorType>>
    member __.HeatmapMode = _HeatmapMode_ :> FSharp.Data.Adaptive.aval<HeatmapMode>
    member __.SurfaceDistOn = _SurfaceDistOn_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
    member __.ExtrinsicZDiff = _ExtrinsicZDiff_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
    member __.VarianceOn = _VarianceOn_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
    member __.SurfaceDistance = _SurfaceDistance_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.Map<Microsoft.FSharp.Core.string, (Microsoft.FSharp.Core.float32)[]>>
    member __.ScanPins = _ScanPins_
    member __.InspectorMesh = _InspectorMesh_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<Microsoft.FSharp.Core.string>>
    member __.WorkflowPinHover = _WorkflowPinHover_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<ScanPinId>>
    member __.CorrRowHover = _CorrRowHover_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<(Microsoft.FSharp.Core.string * Microsoft.FSharp.Core.string)>>
    member __.RenderingMode = _RenderingMode_ :> FSharp.Data.Adaptive.aval<RenderingMode>
    member __.MeshSolo = _MeshSolo_ :> FSharp.Data.Adaptive.aval<MeshSoloState>
    member __.GearPopoverOpen = _GearPopoverOpen_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
    member __.WorkflowStep = _WorkflowStep_ :> FSharp.Data.Adaptive.aval<WorkflowStep>
    member __.FocusProjection = _FocusProjection_ :> FSharp.Data.Adaptive.aval<FocusProjection>
    member __.FocusMesh = _FocusMesh_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<Microsoft.FSharp.Core.string>>
    member __.FocusMaps = _FocusMaps_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.Map<Microsoft.FSharp.Core.string, FocusPreview>>
    member __.FocusPeekReference = _FocusPeekReference_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
    member __.PinFocusMode = _PinFocusMode_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
    member __.MovementLayer = _MovementLayer_ :> FSharp.Data.Adaptive.aval<MovementMode>
    member __.OutlineMode = _OutlineMode_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>

