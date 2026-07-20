namespace Superprojekt

open Aardvark.Base
open Superprojekt

type Message =
    | CameraMessage      of OrbitMessage
    | CentroidsLoaded    of (string * V3d)[]
    | PanoCentersLoaded  of (string * V3d)[]
    | LoadFinished       of string
    | ToggleGhostSilhouette
    | SetGhostOpacity of float
    | SetShadingStrength of float
    | SetSlopeThresholdDeg of float
    | SetOutlineThreshold of float
    | SetIsolineBands of float
    | SetIsolineOpacity of float
    | ToggleAnchorGhostMode
    | SetQuickPinRadius of float
    | SetFlagScale of float
    | SetBrushDotPx of float
    | SetReferenceMesh of string option
    // Disabled until a solve exists.
    | SetRegView of RegView
    // Spring-loaded hold: momentarily show the other registration state (visual only).
    | SetRegPeek of bool
    // Writes SolvedTransform directly, per moving mesh with ≥3 in-ROI pairs, in
    // parallel. Results land as ONE batch, guarded by UpdateHelpers.solveGen.
    | SolveCoarse
    | CoarseSolved of gen:int * solved:(string * M44d)[] * failed:(string * string)[]
    // inRoi carries per-(pin,mesh) ROI membership; out-of-ROI meshes are not
    // seeded and their stale auto markers are dropped.
    | AnchorsSeeded of refUpdates:(ScanPinId * V3d)[] * seeded:(ScanPinId * string * V3d)[] * inRoi:(ScanPinId * string * bool)[]
    | AnchorSeedFailed of string
    // Transient feedback from view-side guards (e.g. the placement hard-prohibit).
    | ShowToast of string
    | ClearToast
    // Per-mesh intrinsic error visualization (Overview mesh list). HeatOff = textured.
    | SetMeshHeatmap of mesh:string * HeatmapMode
    // Shp cutoff — triangles below the quality threshold render transparent.
    | SetShapeThreshold of float
    // Difference sub-mode (M3C2 ↔ Δz) for the Inspect focus tiles.
    | ToggleExtrinsicZDiff
    // gen = the issuing generation (UpdateHelpers) — stale results are dropped.
    | VarianceComputed of gen:int * mesh:string * float32[]
    | VarianceOtherComputed of gen:int * mesh:string * float32[]
    | FocusDistComputed of gen:int * mesh:string * float32[]
    | FocusDistOtherComputed of gen:int * mesh:string * float32[]
    | SurfaceDistanceFailed of mesh:string * reason:string
    // Per mesh: world bbox + mean sample spacing (m) — one fetch warms both.
    | SceneBoundsLoaded  of (string * Box3d * float)[]
    // Slice-cell tunables (§A, gear): window multiplier / context count / context
    // spacing invalidate the slice caches; the vertical percentile is view-only.
    | SetSliceNSamples of float
    | SetSliceContextCount of float
    | SetSliceContextSpacing of float
    | SetSliceVertPercentile of float
    | DatasetsLoaded     of string[]
    | SetActiveDataset   of string
    | ScanPinMsg              of ScanPinMessage
    | SetRenderingMode of RenderingMode
    | ToggleGearPopover
    | SetActivePickingLayer of string option
    // hover = peek, click = select/promote.
    | SetHovered of HoverTarget option
    // THE one selection (see Model.ActiveSelection + the handler).
    | SetSelection of ActiveSelection
    | SetWorkflowStep of WorkflowStep
    | SetFocusProjection of FocusProjection
    // Slice mode (v12 §5): toggle the pin-centred ortho measurement view;
    // AdjustSliceCut = wheel notches sweeping the cut plane through the pin.
    | SetSliceMode of bool
    | AdjustSliceCut of float
    | ToggleSliceStretch
    | PickCorrespondenceAt of ScanPinId * mesh:string * world:V3d
    // Arm/disarm the unified correspondence editor for a (pin, mesh).
    | ToggleCorrArm of ScanPinId * mesh:string
    // Hover preview of where a correspondence pick would land (metric world).
    | CorrPreviewComputed of V3d option
    // Per-sample distribution brushing: replace the brushed-sample id set.
    | SetBrushedSamples of int list
    // Explicit MAIN-3D camera framing (the double-click grammar) — selection only
    // frames the focus panel; these own the 3D radius conventions.
    | FlyToPoint of world:V3d * radius:float
    | ZoomToMesh of string
    | ZoomToPin of ScanPinId
    | BackOutLocate

and ScanPinMessage =
    | EnterAnchorPlacement
    | CancelPlacement
    | PlaceAnchor of worldCentre:V3d
    | SetInnerRadius of float
    | DeletePin of ScanPinId
    | ProbeComputed of ScanPinId * ProbeResult
    | ProbeFailed of ScanPinId * string
    | ProbeOtherComputed of ScanPinId * ProbeResult
    | ProbeOtherFailed of ScanPinId * string
    | SliceComputed of ScanPinId * PinSlice
    | SliceFailed of ScanPinId * string
    | SliceOtherComputed of ScanPinId * PinSlice
    | SliceOtherFailed of ScanPinId * string
    | ContactRingsComputed of ScanPinId * Map<string, V3d[][]>
