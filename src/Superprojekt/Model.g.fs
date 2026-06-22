//9199b5fd-11b3-5226-45ee-26581034eb0e
//5636045f-dc67-1811-8068-d2d27f701da9
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
    let _LassoDrawing_ = FSharp.Data.Adaptive.cval(value.LassoDrawing)
    let _LassoVolume_ = FSharp.Data.Adaptive.cval(value.LassoVolume)
    let _LassoEnabled_ = FSharp.Data.Adaptive.cval(value.LassoEnabled)
    let _ClipPlanes_ = FSharp.Data.Adaptive.cval(value.ClipPlanes)
    let _ReferencePeekHeld_ = FSharp.Data.Adaptive.cval(value.ReferencePeekHeld)
    let _CutawayActive_ = FSharp.Data.Adaptive.cval(value.CutawayActive)
    let _ClipAboveIso_ = FSharp.Data.Adaptive.cval(value.ClipAboveIso)
    let _RulerActive_ = FSharp.Data.Adaptive.cval(value.RulerActive)
    let _MeshTransforms_ = FSharp.Data.Adaptive.cval(value.MeshTransforms)
    let _Registration_ = FSharp.Data.Adaptive.cval(value.Registration)
    let _Retarget_ = FSharp.Data.Adaptive.cval(value.Retarget)
    let _PendingReg_ = FSharp.Data.Adaptive.cval(value.PendingReg)
    let _RegistrationLog_ = FSharp.Data.Adaptive.cval(value.RegistrationLog)
    let _LastSolve_ = FSharp.Data.Adaptive.cval(value.LastSolve)
    let _AnchorPick_ = FSharp.Data.Adaptive.cval(value.AnchorPick)
    let _PatchPicker_ = FSharp.Data.Adaptive.cval(value.PatchPicker)
    let _Toast_ = FSharp.Data.Adaptive.cval(value.Toast)
    let _MeshSensorTypes_ = FSharp.Data.Adaptive.cval(value.MeshSensorTypes)
    let _MeshDatasetErrors_ = FSharp.Data.Adaptive.cval(value.MeshDatasetErrors)
    let _MeshAlgorithmResidual_ = FSharp.Data.Adaptive.cval(value.MeshAlgorithmResidual)
    let _HeatmapMode_ = FSharp.Data.Adaptive.cval(value.HeatmapMode)
    let _HeatmapPrev_ = FSharp.Data.Adaptive.cval(value.HeatmapPrev)
    let _ProvenanceThreshold_ = FSharp.Data.Adaptive.cval(value.ProvenanceThreshold)
    let _SurfaceDistOn_ = FSharp.Data.Adaptive.cval(value.SurfaceDistOn)
    let _SurfaceDistance_ = FSharp.Data.Adaptive.cval(value.SurfaceDistance)
    let _SurfaceDistBrush_ = FSharp.Data.Adaptive.cval(value.SurfaceDistBrush)
    let _FusionMode_ = FSharp.Data.Adaptive.cval(value.FusionMode)
    let _PanoramaOpen_ = FSharp.Data.Adaptive.cval(value.PanoramaOpen)
    let _Panoramas_ = FSharp.Data.Adaptive.cval(value.Panoramas)
    let _SelectedPanorama_ = FSharp.Data.Adaptive.cval(value.SelectedPanorama)
    let _PanoramaMode_ = FSharp.Data.Adaptive.cval(value.PanoramaMode)
    let _PanoramaBlend_ = FSharp.Data.Adaptive.cval(value.PanoramaBlend)
    let _ScanPins_ = AdaptiveScanPinModel(value.ScanPins)
    let _CardSystem_ = AdaptiveCardSystemModel(value.CardSystem)
    let _HoverProbe_ = FSharp.Data.Adaptive.cval(value.HoverProbe)
    let _ChartCursor_ = FSharp.Data.Adaptive.cval(value.ChartCursor)
    let _ChartHoverMesh_ = FSharp.Data.Adaptive.cval(value.ChartHoverMesh)
    let _ChartStickyMesh_ = FSharp.Data.Adaptive.cval(value.ChartStickyMesh)
    let _WorkflowPinHover_ = FSharp.Data.Adaptive.cval(value.WorkflowPinHover)
    let _CorrMarkerHover_ = FSharp.Data.Adaptive.cval(value.CorrMarkerHover)
    let _RenderingMode_ = FSharp.Data.Adaptive.cval(value.RenderingMode)
    let _MeshSolo_ = FSharp.Data.Adaptive.cval(value.MeshSolo)
    let _LassoCardPos_ = FSharp.Data.Adaptive.cval(value.LassoCardPos)
    let _GearPopoverOpen_ = FSharp.Data.Adaptive.cval(value.GearPopoverOpen)
    let _Study_ = FSharp.Data.Adaptive.cval(value.Study)
    let _StudiesAvailable_ = FSharp.Data.Adaptive.cval(value.StudiesAvailable)
    let _WorkflowPanelOpen_ = FSharp.Data.Adaptive.cval(value.WorkflowPanelOpen)
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
            _LassoDrawing_.Value <- value.LassoDrawing
            _LassoVolume_.Value <- value.LassoVolume
            _LassoEnabled_.Value <- value.LassoEnabled
            _ClipPlanes_.Value <- value.ClipPlanes
            _ReferencePeekHeld_.Value <- value.ReferencePeekHeld
            _CutawayActive_.Value <- value.CutawayActive
            _ClipAboveIso_.Value <- value.ClipAboveIso
            _RulerActive_.Value <- value.RulerActive
            _MeshTransforms_.Value <- value.MeshTransforms
            _Registration_.Value <- value.Registration
            _Retarget_.Value <- value.Retarget
            _PendingReg_.Value <- value.PendingReg
            _RegistrationLog_.Value <- value.RegistrationLog
            _LastSolve_.Value <- value.LastSolve
            _AnchorPick_.Value <- value.AnchorPick
            _PatchPicker_.Value <- value.PatchPicker
            _Toast_.Value <- value.Toast
            _MeshSensorTypes_.Value <- value.MeshSensorTypes
            _MeshDatasetErrors_.Value <- value.MeshDatasetErrors
            _MeshAlgorithmResidual_.Value <- value.MeshAlgorithmResidual
            _HeatmapMode_.Value <- value.HeatmapMode
            _HeatmapPrev_.Value <- value.HeatmapPrev
            _ProvenanceThreshold_.Value <- value.ProvenanceThreshold
            _SurfaceDistOn_.Value <- value.SurfaceDistOn
            _SurfaceDistance_.Value <- value.SurfaceDistance
            _SurfaceDistBrush_.Value <- value.SurfaceDistBrush
            _FusionMode_.Value <- value.FusionMode
            _PanoramaOpen_.Value <- value.PanoramaOpen
            _Panoramas_.Value <- value.Panoramas
            _SelectedPanorama_.Value <- value.SelectedPanorama
            _PanoramaMode_.Value <- value.PanoramaMode
            _PanoramaBlend_.Value <- value.PanoramaBlend
            _ScanPins_.Update(value.ScanPins)
            _CardSystem_.Update(value.CardSystem)
            _HoverProbe_.Value <- value.HoverProbe
            _ChartCursor_.Value <- value.ChartCursor
            _ChartHoverMesh_.Value <- value.ChartHoverMesh
            _ChartStickyMesh_.Value <- value.ChartStickyMesh
            _WorkflowPinHover_.Value <- value.WorkflowPinHover
            _CorrMarkerHover_.Value <- value.CorrMarkerHover
            _RenderingMode_.Value <- value.RenderingMode
            _MeshSolo_.Value <- value.MeshSolo
            _LassoCardPos_.Value <- value.LassoCardPos
            _GearPopoverOpen_.Value <- value.GearPopoverOpen
            _Study_.Value <- value.Study
            _StudiesAvailable_.Value <- value.StudiesAvailable
            _WorkflowPanelOpen_.Value <- value.WorkflowPanelOpen
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
    member __.LassoDrawing = _LassoDrawing_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<LassoDraft>>
    member __.LassoVolume = _LassoVolume_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<LassoVolume>>
    member __.LassoEnabled = _LassoEnabled_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
    member __.ClipPlanes = _ClipPlanes_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.list<ClipPlane>>
    member __.ReferencePeekHeld = _ReferencePeekHeld_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
    member __.CutawayActive = _CutawayActive_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
    member __.ClipAboveIso = _ClipAboveIso_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
    member __.RulerActive = _RulerActive_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
    member __.MeshTransforms = _MeshTransforms_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.Map<Microsoft.FSharp.Core.string, Aardvark.Base.Trafo3d>>
    member __.Registration = _Registration_ :> FSharp.Data.Adaptive.aval<RegistrationState>
    member __.Retarget = _Retarget_ :> FSharp.Data.Adaptive.aval<RetargetState>
    member __.PendingReg = _PendingReg_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<PendingRegistration>>
    member __.RegistrationLog = _RegistrationLog_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.list<RegStep>>
    member __.LastSolve = _LastSolve_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.Map<Microsoft.FSharp.Core.string, LastSolveEntry>>
    member __.AnchorPick = _AnchorPick_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<AnchorPickState>>
    member __.PatchPicker = _PatchPicker_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<PatchPickerState>>
    member __.Toast = _Toast_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<Microsoft.FSharp.Core.string>>
    member __.MeshSensorTypes = _MeshSensorTypes_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.Map<Microsoft.FSharp.Core.string, SensorType>>
    member __.MeshDatasetErrors = _MeshDatasetErrors_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.Map<Microsoft.FSharp.Core.string, Microsoft.FSharp.Core.float>>
    member __.MeshAlgorithmResidual = _MeshAlgorithmResidual_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.Map<Microsoft.FSharp.Core.string, Microsoft.FSharp.Core.float>>
    member __.HeatmapMode = _HeatmapMode_ :> FSharp.Data.Adaptive.aval<HeatmapMode>
    member __.HeatmapPrev = _HeatmapPrev_ :> FSharp.Data.Adaptive.aval<HeatmapMode>
    member __.ProvenanceThreshold = _ProvenanceThreshold_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.float>
    member __.SurfaceDistOn = _SurfaceDistOn_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
    member __.SurfaceDistance = _SurfaceDistance_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.Map<Microsoft.FSharp.Core.string, (Microsoft.FSharp.Core.float32)[]>>
    member __.SurfaceDistBrush = _SurfaceDistBrush_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<(Microsoft.FSharp.Core.float * Microsoft.FSharp.Core.float)>>
    member __.FusionMode = _FusionMode_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
    member __.PanoramaOpen = _PanoramaOpen_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
    member __.Panoramas = _Panoramas_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.list<Panorama>>
    member __.SelectedPanorama = _SelectedPanorama_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.int>
    member __.PanoramaMode = _PanoramaMode_ :> FSharp.Data.Adaptive.aval<PanoramaMode>
    member __.PanoramaBlend = _PanoramaBlend_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.float>
    member __.ScanPins = _ScanPins_
    member __.CardSystem = _CardSystem_
    member __.HoverProbe = _HoverProbe_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<HoverProbeState>>
    member __.ChartCursor = _ChartCursor_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<ChartCursor>>
    member __.ChartHoverMesh = _ChartHoverMesh_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<Microsoft.FSharp.Core.string>>
    member __.ChartStickyMesh = _ChartStickyMesh_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<Microsoft.FSharp.Core.string>>
    member __.WorkflowPinHover = _WorkflowPinHover_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<ScanPinId>>
    member __.CorrMarkerHover = _CorrMarkerHover_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<(ScanPinId * Microsoft.FSharp.Core.string)>>
    member __.RenderingMode = _RenderingMode_ :> FSharp.Data.Adaptive.aval<RenderingMode>
    member __.MeshSolo = _MeshSolo_ :> FSharp.Data.Adaptive.aval<MeshSoloState>
    member __.LassoCardPos = _LassoCardPos_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<Aardvark.Base.V2d>>
    member __.GearPopoverOpen = _GearPopoverOpen_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
    member __.Study = _Study_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<StudyShell>>
    member __.StudiesAvailable = _StudiesAvailable_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.list<Microsoft.FSharp.Core.string>>
    member __.WorkflowPanelOpen = _WorkflowPanelOpen_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>

