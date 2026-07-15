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
    | ToggleAnchorGhostMode
    | SetQuickPinRadius of float
    | SetFlagScale of float
    // Spring-loaded show-overlays modifier (greyscale-except-pins while held).
    | SetShowOverlays of bool
    | SetReferenceMesh of string option
    // Disabled until a solve exists.
    | SetRegView of RegView
    // Spring-loaded hold: momentarily show the other registration state (visual only).
    | SetRegPeek of bool
    // Writes SolvedTransform directly, per visible moving mesh with ≥3 in-ROI
    // pairs, in parallel.
    | SolveCoarse
    | CoarseSolved of mesh:string * world:M44d
    | CoarseFailed of mesh:string * reason:string
    // inRoi carries per-(pin,mesh) ROI membership; out-of-ROI meshes are not
    // seeded and their stale auto markers are dropped.
    | AnchorsSeeded of refUpdates:(ScanPinId * V3d)[] * seeded:(ScanPinId * string * V3d)[] * inRoi:(ScanPinId * string * bool)[]
    | AnchorSeedFailed of string
    | ClearToast
    // Per-mesh intrinsic error visualization (Overview mesh list). HeatOff = textured.
    | SetMeshHeatmap of mesh:string * HeatmapMode
    // Shp cutoff — triangles below the quality threshold render transparent.
    | SetShapeThreshold of float
    // Difference sub-mode (M3C2 ↔ Δz) for the Inspect focus tiles.
    | ToggleExtrinsicZDiff
    | VarianceComputed of mesh:string * float32[]
    | VarianceOtherComputed of mesh:string * float32[]
    | FocusDistComputed of mesh:string * float32[]
    | FocusDistOtherComputed of mesh:string * float32[]
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
    // THE selection: mesh / pin / cell (pin, mesh). Every entry path (matrix,
    // roster, tiles, 3D) emits this; a cell selection is the locate (solo +
    // backup). The focus panel frames the selection; the main 3D camera still
    // moves only on double-click (ZoomTo*/FlyToPoint).
    | SetSelection of ActiveSelection
    | SetWorkflowStep of WorkflowStep
    | SetInspectChannel of InspectChannel
    | SetFocusProjection of FocusProjection
    | PickCorrespondenceAt of ScanPinId * mesh:string * world:V3d
    // Arm/disarm the unified correspondence editor for a (pin, mesh): isolates the
    // mesh, brings the linked focus onto it, and accepts picks from focus OR 3D until
    // disarmed. Re-issuing for the armed pair disarms.
    | ToggleCorrArm of ScanPinId * mesh:string
    // Transient hover preview of where a correspondence pick would land (metric
    // world); drives the aim ghost in both views while armed.
    | CorrPreviewComputed of V3d option
    // Per-sample distribution brushing (§T6): replace the brushed-sample id set.
    | SetBrushedSamples of int list
    // Fly the orbit camera tight to a metric-world point: animate centre + radius
    // directly (radius = the orbit distance, not derived from a subtend).
    | FlyToPoint of world:V3d * radius:float
    // Explicit MAIN-3D camera framing (the double-click grammar) — selection only
    // frames the focus panel; these own the 3D radius conventions.
    | ZoomToMesh of string
    | ZoomToPin of ScanPinId
    // Restore the camera + solo/visibility captured at the first cell locate.
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
