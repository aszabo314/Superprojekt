namespace Superprojekt

open Aardvark.Base
open Superprojekt

type Message =
    | CameraMessage      of OrbitMessage
    | CentroidsLoaded    of (string * V3d)[]
    | PanoCentersLoaded  of (string * V3d)[]
    | LoadFinished       of string
    | SetVisible         of string * bool
    | ToggleMenu
    | ToggleGhostSilhouette
    | SetGhostOpacity of float
    | SetShadingStrength of float
    | SetSlopeThresholdDeg of float
    | SetOutlineThreshold of float
    | SetIsolineBands of float
    | ToggleAnchorGhostMode
    | SetQuickPinRadius of float
    | SetReferencePeek of bool
    | SetReferenceMesh of string option
    // Disabled until a solve exists.
    | SetRegView of RegView
    // Writes SolvedTransform directly, per visible moving mesh with ≥3 in-ROI
    // pairs, in parallel.
    | SolveCoarse
    | CoarseSolved of mesh:string * world:M44d * pairResiduals:(ScanPinId * float)[]
    | CoarseFailed of mesh:string * reason:string
    // inRoi carries per-(pin,mesh) ROI membership; out-of-ROI meshes are not
    // seeded and their stale auto markers are dropped.
    | AnchorsSeeded of refUpdates:(ScanPinId * V3d * float)[] * seeded:(ScanPinId * string * V3d)[] * inRoi:(ScanPinId * string * bool)[]
    | AnchorSeedFailed of string
    | ClearToast
    | SetMeshSensorType of string * SensorType
    | SetHeatmapMode of HeatmapMode
    // Difference sub-mode (M3C2 ↔ Δz) for the Inspect focus tiles.
    | ToggleExtrinsicZDiff
    | VarianceComputed of mesh:string * float32[]
    | FocusDistComputed of mesh:string * float32[]
    | SurfaceDistanceFailed of mesh:string * reason:string
    | SceneBoundsLoaded  of (string * Box3d)[]
    | DatasetsLoaded     of string[]
    | SetActiveDataset   of string
    | ScanPinMsg              of ScanPinMessage
    | SetRenderingMode of RenderingMode
    | ToggleMeshSolo of string
    | ResetCamera
    | ToggleGearPopover
    | RenamePin of ScanPinId * string
    | SetActivePickingLayer of string option
    // hover = peek, click = select/promote.
    | SetHovered of HoverTarget option
    | SetFocusedMesh of string option
    | SetSelectedPoint of string option
    | ReseedMesh of ScanPinId * string
    | SetWorkflowStep of WorkflowStep
    | SetInspectChannel of InspectChannel
    | SetFocusProjection of FocusProjection
    | PickCorrespondenceAt of ScanPinId * mesh:string * world:V3d
    | ToggleCorrSetMode
    // Transient hover preview of where a correspondence pick would land (metric
    // world); drives the 3D ghost while CorrSetMode is on.
    | CorrPreviewComputed of V3d option
    | SetFocusPeekReference of bool
    // aspect from the view, fovY from the fixed 90° horizontal fov.
    | FlyTo of FlyToTarget * aspect:float
    | NavTo of NavAction

and ScanPinMessage =
    | EnterAnchorPlacement
    | CancelPlacement
    | PlaceAnchor of worldCentre:V3d
    | SetInnerRadius of float
    | DeletePin of ScanPinId
    | SelectPin of ScanPinId option
    | ProbeComputed of ScanPinId * ProbeResult
    | ProbeFailed of ScanPinId * string
    | ContactRingsComputed of ScanPinId * Map<string, V3d[][]>
