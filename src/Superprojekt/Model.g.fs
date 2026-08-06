//59de5879-8d21-dba6-c02f-3ab4f911e4e3
//58f75556-4db5-9420-72b8-36636da70f77
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
    let _TileCams_ = FSharp.Data.Adaptive.cval(value.TileCams)
    let _MeshOrder_ = FSharp.Data.Adaptive.cmap(value.MeshOrder)
    let _MeshNames_ = FSharp.Data.Adaptive.clist(value.MeshNames)
    let _MeshesLoaded_ = FSharp.Data.Adaptive.cset(value.MeshesLoaded)
    let _CommonCentroid_ = FSharp.Data.Adaptive.cval(value.CommonCentroid)
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
    let _FlagScale_ = FSharp.Data.Adaptive.cval(value.FlagScale)
    let _RevealRadius_ = FSharp.Data.Adaptive.cval(value.RevealRadius)
    let _SceneBounds_ = FSharp.Data.Adaptive.cval(value.SceneBounds)
    let _MeshBounds_ = FSharp.Data.Adaptive.cval(value.MeshBounds)
    let _LoadTransforms_ = FSharp.Data.Adaptive.cval(value.LoadTransforms)
    let _RegGraph_ = FSharp.Data.Adaptive.cval(value.RegGraph)
    let _ComposedPoses_ = FSharp.Data.Adaptive.cval(value.ComposedPoses)
    let _PairOverlaps_ = FSharp.Data.Adaptive.cval(value.PairOverlaps)
    let _Toast_ = FSharp.Data.Adaptive.cval(value.Toast)
    let _MeshHeatmap_ = FSharp.Data.Adaptive.cval(value.MeshHeatmap)
    let _TileIsolate_ = FSharp.Data.Adaptive.cval(value.TileIsolate)
    let _TileIsolateHover_ = FSharp.Data.Adaptive.cval(value.TileIsolateHover)
    let _MatrixHoverPair_ = FSharp.Data.Adaptive.cval(value.MatrixHoverPair)
    let _ShapeThreshold_ = FSharp.Data.Adaptive.cval(value.ShapeThreshold)
    let _ScanPins_ = AdaptiveScanPinModel(value.ScanPins)
    let _RenderingMode_ = FSharp.Data.Adaptive.cval(value.RenderingMode)
    let _Focus_ = FSharp.Data.Adaptive.cval(value.Focus)
    let _Sel_ = FSharp.Data.Adaptive.cval(value.Sel)
    let _ReachLog_ = FSharp.Data.Adaptive.cval(value.ReachLog)
    let _ReachLogOpen_ = FSharp.Data.Adaptive.cval(value.ReachLogOpen)
    let _Checkpoints_ = FSharp.Data.Adaptive.cval(value.Checkpoints)
    let _CheckpointName_ = FSharp.Data.Adaptive.cval(value.CheckpointName)
    let _CellError_ = FSharp.Data.Adaptive.cval(value.CellError)
    let _CellErrorBefore_ = FSharp.Data.Adaptive.cval(value.CellErrorBefore)
    let _CellDist_ = FSharp.Data.Adaptive.cval(value.CellDist)
    let _GraphError_ = FSharp.Data.Adaptive.cval(value.GraphError)
    let _GraphErrorBefore_ = FSharp.Data.Adaptive.cval(value.GraphErrorBefore)
    let _GraphDist_ = FSharp.Data.Adaptive.cval(value.GraphDist)
    let _GraphDistBefore_ = FSharp.Data.Adaptive.cval(value.GraphDistBefore)
    let _CellMapOn_ = FSharp.Data.Adaptive.cval(value.CellMapOn)
    let _BrushedSamples_ = FSharp.Data.Adaptive.cval(value.BrushedSamples)
    let _HoverSample_ = FSharp.Data.Adaptive.cval(value.HoverSample)
    let _HoverReadout_ = FSharp.Data.Adaptive.cval(value.HoverReadout)
    let _ArmedPick_ = FSharp.Data.Adaptive.cval(value.ArmedPick)
    let _ArmPreview_ = FSharp.Data.Adaptive.cval(value.ArmPreview)
    let _PinFocusHover_ = FSharp.Data.Adaptive.cval(value.PinFocusHover)
    let _TilePinHover_ = FSharp.Data.Adaptive.cval(value.TilePinHover)
    let _NewPinHover_ = FSharp.Data.Adaptive.cval(value.NewPinHover)
    let _PinRadiusEditOpen_ = FSharp.Data.Adaptive.cval(value.PinRadiusEditOpen)
    let _PeekVis_ = FSharp.Data.Adaptive.cval(value.PeekVis)
    let _PeekPose_ = FSharp.Data.Adaptive.cval(value.PeekPose)
    let _LoopPending_ = FSharp.Data.Adaptive.cval(value.LoopPending)
    let _PinExitPending_ = FSharp.Data.Adaptive.cval(value.PinExitPending)
    let _PairConnectWarn_ = FSharp.Data.Adaptive.cval(value.PairConnectWarn)
    let _PendingResolves_ = FSharp.Data.Adaptive.cval(value.PendingResolves)
    let _GearPopoverOpen_ = FSharp.Data.Adaptive.cval(value.GearPopoverOpen)
    let _MeshMenuOpen_ = FSharp.Data.Adaptive.cval(value.MeshMenuOpen)
    let _SensorMenuOpen_ = FSharp.Data.Adaptive.cval(value.SensorMenuOpen)
    let _InspectOpen_ = FSharp.Data.Adaptive.cval(value.InspectOpen)
    let _OutlineThreshold_ = FSharp.Data.Adaptive.cval(value.OutlineThreshold)
    let _OutlineWidthPx_ = FSharp.Data.Adaptive.cval(value.OutlineWidthPx)
    let _IsolineBands_ = FSharp.Data.Adaptive.cval(value.IsolineBands)
    let _IsolineOpacity_ = FSharp.Data.Adaptive.cval(value.IsolineOpacity)
    let _NearCutFrac_ = FSharp.Data.Adaptive.cval(value.NearCutFrac)
    let _FarCutFrac_ = FSharp.Data.Adaptive.cval(value.FarCutFrac)
    let mutable __value = value
    let __adaptive = FSharp.Data.Adaptive.AVal.custom((fun (token : FSharp.Data.Adaptive.AdaptiveToken) -> __value))
    static member Create(value : Model) = AdaptiveModel(value)
    static member Unpersist = Adaptify.Unpersist.create (fun (value : Model) -> AdaptiveModel(value)) (fun (adaptive : AdaptiveModel) (value : Model) -> adaptive.Update(value))
    member __.Update(value : Model) =
        if Microsoft.FSharp.Core.Operators.not((FSharp.Data.Adaptive.ShallowEqualityComparer<Model>.ShallowEquals(value, __value))) then
            __value <- value
            __adaptive.MarkOutdated()
            _Camera_.Update(value.Camera)
            _TileCams_.Value <- value.TileCams
            _MeshOrder_.Value <- value.MeshOrder
            _MeshNames_.Value <- value.MeshNames
            _MeshesLoaded_.Value <- value.MeshesLoaded
            _CommonCentroid_.Value <- value.CommonCentroid
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
            _FlagScale_.Value <- value.FlagScale
            _RevealRadius_.Value <- value.RevealRadius
            _SceneBounds_.Value <- value.SceneBounds
            _MeshBounds_.Value <- value.MeshBounds
            _LoadTransforms_.Value <- value.LoadTransforms
            _RegGraph_.Value <- value.RegGraph
            _ComposedPoses_.Value <- value.ComposedPoses
            _PairOverlaps_.Value <- value.PairOverlaps
            _Toast_.Value <- value.Toast
            _MeshHeatmap_.Value <- value.MeshHeatmap
            _TileIsolate_.Value <- value.TileIsolate
            _TileIsolateHover_.Value <- value.TileIsolateHover
            _MatrixHoverPair_.Value <- value.MatrixHoverPair
            _ShapeThreshold_.Value <- value.ShapeThreshold
            _ScanPins_.Update(value.ScanPins)
            _RenderingMode_.Value <- value.RenderingMode
            _Focus_.Value <- value.Focus
            _Sel_.Value <- value.Sel
            _ReachLog_.Value <- value.ReachLog
            _ReachLogOpen_.Value <- value.ReachLogOpen
            _Checkpoints_.Value <- value.Checkpoints
            _CheckpointName_.Value <- value.CheckpointName
            _CellError_.Value <- value.CellError
            _CellErrorBefore_.Value <- value.CellErrorBefore
            _CellDist_.Value <- value.CellDist
            _GraphError_.Value <- value.GraphError
            _GraphErrorBefore_.Value <- value.GraphErrorBefore
            _GraphDist_.Value <- value.GraphDist
            _GraphDistBefore_.Value <- value.GraphDistBefore
            _CellMapOn_.Value <- value.CellMapOn
            _BrushedSamples_.Value <- value.BrushedSamples
            _HoverSample_.Value <- value.HoverSample
            _HoverReadout_.Value <- value.HoverReadout
            _ArmedPick_.Value <- value.ArmedPick
            _ArmPreview_.Value <- value.ArmPreview
            _PinFocusHover_.Value <- value.PinFocusHover
            _TilePinHover_.Value <- value.TilePinHover
            _NewPinHover_.Value <- value.NewPinHover
            _PinRadiusEditOpen_.Value <- value.PinRadiusEditOpen
            _PeekVis_.Value <- value.PeekVis
            _PeekPose_.Value <- value.PeekPose
            _LoopPending_.Value <- value.LoopPending
            _PinExitPending_.Value <- value.PinExitPending
            _PairConnectWarn_.Value <- value.PairConnectWarn
            _PendingResolves_.Value <- value.PendingResolves
            _GearPopoverOpen_.Value <- value.GearPopoverOpen
            _MeshMenuOpen_.Value <- value.MeshMenuOpen
            _SensorMenuOpen_.Value <- value.SensorMenuOpen
            _InspectOpen_.Value <- value.InspectOpen
            _OutlineThreshold_.Value <- value.OutlineThreshold
            _OutlineWidthPx_.Value <- value.OutlineWidthPx
            _IsolineBands_.Value <- value.IsolineBands
            _IsolineOpacity_.Value <- value.IsolineOpacity
            _NearCutFrac_.Value <- value.NearCutFrac
            _FarCutFrac_.Value <- value.FarCutFrac
    member __.Current = __adaptive
    member __.Camera = _Camera_
    member __.TileCams = _TileCams_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.Map<Microsoft.FSharp.Core.string, TileCam>>
    member __.MeshOrder = _MeshOrder_ :> FSharp.Data.Adaptive.amap<Microsoft.FSharp.Core.string, Microsoft.FSharp.Core.int>
    member __.MeshNames = _MeshNames_ :> FSharp.Data.Adaptive.alist<Microsoft.FSharp.Core.string>
    member __.MeshesLoaded = _MeshesLoaded_ :> FSharp.Data.Adaptive.aset<Microsoft.FSharp.Core.string>
    member __.CommonCentroid = _CommonCentroid_ :> FSharp.Data.Adaptive.aval<Aardvark.Base.V3d>
    member __.Datasets = _Datasets_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.list<Microsoft.FSharp.Core.string>>
    member __.ActiveDataset = _ActiveDataset_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<Microsoft.FSharp.Core.string>>
    member __.DatasetScales = _DatasetScales_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.Map<Microsoft.FSharp.Core.string, Microsoft.FSharp.Core.float>>
    member __.DatasetCentroids = _DatasetCentroids_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.Map<Microsoft.FSharp.Core.string, Aardvark.Base.V3d>>
    member __.GhostSilhouette = _GhostSilhouette_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
    member __.GhostOpacity = _GhostOpacity_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.float>
    member __.ShadingStrength = _ShadingStrength_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.float>
    member __.SlopeThresholdDeg = _SlopeThresholdDeg_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.float>
    member __.AnchorGhostMode = _AnchorGhostMode_ :> FSharp.Data.Adaptive.aval<LevelFlags>
    member __.QuickPinRadius = _QuickPinRadius_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.float>
    member __.FlagScale = _FlagScale_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.float>
    member __.RevealRadius = _RevealRadius_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.float>
    member __.SceneBounds = _SceneBounds_ :> FSharp.Data.Adaptive.aval<Aardvark.Base.Box3d>
    member __.MeshBounds = _MeshBounds_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.Map<Microsoft.FSharp.Core.string, Aardvark.Base.Box3d>>
    member __.LoadTransforms = _LoadTransforms_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.Map<Microsoft.FSharp.Core.string, Aardvark.Base.Trafo3d>>
    member __.RegGraph = _RegGraph_ :> FSharp.Data.Adaptive.aval<RegGraph>
    member __.ComposedPoses = _ComposedPoses_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.Map<Microsoft.FSharp.Core.string, Aardvark.Base.Trafo3d>>
    member __.PairOverlaps = _PairOverlaps_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.Map<(Microsoft.FSharp.Core.string * Microsoft.FSharp.Core.string), Microsoft.FSharp.Core.bool>>
    member __.Toast = _Toast_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<Microsoft.FSharp.Core.string>>
    member __.MeshHeatmap = _MeshHeatmap_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.Map<Microsoft.FSharp.Core.string, HeatmapMode>>
    member __.TileIsolate = _TileIsolate_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<Microsoft.FSharp.Core.string>>
    member __.TileIsolateHover = _TileIsolateHover_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<Microsoft.FSharp.Core.string>>
    member __.MatrixHoverPair = _MatrixHoverPair_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<(Microsoft.FSharp.Core.string * Microsoft.FSharp.Core.string)>>
    member __.ShapeThreshold = _ShapeThreshold_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.float>
    member __.ScanPins = _ScanPins_
    member __.RenderingMode = _RenderingMode_ :> FSharp.Data.Adaptive.aval<RenderingMode>
    member __.Focus = _Focus_ :> FSharp.Data.Adaptive.aval<FocusLevel>
    member __.Sel = _Sel_ :> FSharp.Data.Adaptive.aval<FocusSelection>
    member __.ReachLog = _ReachLog_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.list<ReachEvent>>
    member __.ReachLogOpen = _ReachLogOpen_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
    member __.Checkpoints = _Checkpoints_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.list<Microsoft.FSharp.Core.string>>
    member __.CheckpointName = _CheckpointName_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.string>
    member __.CellError = _CellError_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<((ScanPinId * Query.PairPinError))[]>>
    member __.CellErrorBefore = _CellErrorBefore_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<((ScanPinId * Query.PairPinError))[]>>
    member __.CellDist = _CellDist_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<(Microsoft.FSharp.Core.float32)[]>>
    member __.GraphError = _GraphError_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<(InspectBlock)[]>>
    member __.GraphErrorBefore = _GraphErrorBefore_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<(InspectBlock)[]>>
    member __.GraphDist = _GraphDist_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.Map<Microsoft.FSharp.Core.string, (Microsoft.FSharp.Core.float32)[]>>
    member __.GraphDistBefore = _GraphDistBefore_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.Map<Microsoft.FSharp.Core.string, (Microsoft.FSharp.Core.float32)[]>>
    member __.CellMapOn = _CellMapOn_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
    member __.BrushedSamples = _BrushedSamples_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.Set<Microsoft.FSharp.Core.int>>
    member __.HoverSample = _HoverSample_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<Microsoft.FSharp.Core.int>>
    member __.HoverReadout = _HoverReadout_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<(Microsoft.FSharp.Core.int * Microsoft.FSharp.Core.float)>>
    member __.ArmedPick = _ArmedPick_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<ArmTarget>>
    member __.ArmPreview = _ArmPreview_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<Aardvark.Base.V3d>>
    member __.PinFocusHover = _PinFocusHover_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<PinHover>>
    member __.TilePinHover = _TilePinHover_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<ScanPinId>>
    member __.NewPinHover = _NewPinHover_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
    member __.PinRadiusEditOpen = _PinRadiusEditOpen_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
    member __.PeekVis = _PeekVis_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
    member __.PeekPose = _PeekPose_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
    member __.LoopPending = _LoopPending_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<LoopPending>>
    member __.PinExitPending = _PinExitPending_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<FocusLevel>>
    member __.PairConnectWarn = _PairConnectWarn_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.option<(Microsoft.FSharp.Core.string * Microsoft.FSharp.Core.string)>>
    member __.PendingResolves = _PendingResolves_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Collections.list<(Microsoft.FSharp.Core.string * Microsoft.FSharp.Core.string)>>
    member __.GearPopoverOpen = _GearPopoverOpen_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
    member __.MeshMenuOpen = _MeshMenuOpen_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
    member __.SensorMenuOpen = _SensorMenuOpen_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.bool>
    member __.InspectOpen = _InspectOpen_ :> FSharp.Data.Adaptive.aval<LevelFlags>
    member __.OutlineThreshold = _OutlineThreshold_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.float>
    member __.OutlineWidthPx = _OutlineWidthPx_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.float>
    member __.IsolineBands = _IsolineBands_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.float>
    member __.IsolineOpacity = _IsolineOpacity_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.float>
    member __.NearCutFrac = _NearCutFrac_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.float>
    member __.FarCutFrac = _FarCutFrac_ :> FSharp.Data.Adaptive.aval<Microsoft.FSharp.Core.float>

