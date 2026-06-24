namespace Superprojekt

open Aardvark.Base
open Superprojekt

type Message =
    | CameraMessage      of OrbitMessage
    | CentroidsLoaded    of (string * V3d)[]
    | LoadFinished       of string
    | SetVisible         of string * bool
    | ToggleMenu
    | LogDebug           of string
    | ToggleGhostSilhouette
    | SetGhostOpacity of float
    | SetShadingStrength of float
    | SetSlopeThresholdDeg of float
    | ToggleAnchorGhostMode
    | SetQuickPinRadius of float
    // Spring-loaded reference peek (hold).
    | SetReferencePeek of bool
    | SetRegistrationMode of RegistrationMode
    | SetReferenceMesh of string option
    // Stage 2 · Fine (ICP). Solves land in PendingReg, not MeshTransforms.
    | RunRegistration
    | FineSolved of mesh:string * world:Trafo3d * convergence:float[] * residuals:float[]
    | FineFailed of mesh:string * reason:string
    // Stage 1 · Coarse (landmarks via /query/lsq-pairs).
    | SolveCoarse
    | CoarseSolved of mesh:string * worldDelta:M44d * pairResiduals:(ScanPinId * float)[] * rmsBefore:float * eigenvalues:float[] * collinear:bool
    | CoarseFailed of mesh:string * reason:string
    // Pending-preview lifecycle (single commit, no history).
    | CommitRegistration
    | DiscardRegistration
    // Correspondence anchors.
    | ToggleCorrespondence of ScanPinId
    // Seeded markers apply immediately (no review modal).
    | AnchorsSeeded of refUpdates:(ScanPinId * V3d * float)[] * seeded:(ScanPinId * string * V3d)[]
    | AnchorSeedFailed of string
    | SetAnchor of ScanPinId * mesh:string * point:V3d * source:AnchorSource
    | StartAnchorPick of ScanPinId * mesh:string
    | CancelAnchorPick
    | AnchorPickHit of world:V3d
    // Patch small-multiples picker.
    | OpenPatchPicker of ScanPinId
    | ClosePatchPicker
    | TogglePatchShaded
    | PatchPickerReady of pinId:ScanPinId * normal:V3d * refDir:V3d * radius:float * entries:PatchPickerEntry list
    | PatchPickerFailed of string
    | PatchPickerClick of mesh:string * u:float * v:float * h:float
    | ShowToast of string
    | ClearToast
    | SetMeshSensorType of string * SensorType
    | SetHeatmapMode of HeatmapMode
    // A2: per-mesh signed-distance surface colour map.
    | ToggleSurfaceDistance
    | ToggleExtrinsicZDiff
    | ToggleVariance
    | VarianceComputed of mesh:string * float32[]
    | SurfaceDistanceComputed of mesh:string * float32[]
    | SurfaceDistanceFailed of mesh:string * reason:string
    | SetSurfaceDistBrush of (float * float) option
    | SceneBoundsLoaded  of (string * Box3d)[]
    | DatasetsLoaded     of string[]
    | SetActiveDataset   of string
    | ScanPinMsg              of ScanPinMessage
    | JumpToMesh of string
    | CardMsg of CardMessage
    | SetRenderingMode of RenderingMode
    | ToggleMeshSolo of string
    | ShowAllMeshes
    | HideAllMeshes
    | ResetCamera
    | ToggleGearPopover
    | EditPin of ScanPinId
    | SetActivePickingLayer of string option
    | HoverProbeAt of screenPx:V2d * world:V3d
    | HoverProbeResult of ProbeState
    | ClearHoverProbe
    | SetChartCursor of ChartCursor option
    | SetChartHoverMesh of string option
    | SetWorkflowPinHover of ScanPinId option
    | SetCorrMarkerHover of (ScanPinId * string) option
    // Correspondence-detail elevation grids (own-frame) for the effective pin.
    | DetailGridsComputed of pinId:ScanPinId * grids:(string * ElevGrid)[]
    | ChartColumnClick of meshName:string
    | ClearChartSticky
    | ToggleWorkflowPanel
    | SetWorkflowStep of WorkflowStep
    // Right focus panel (spec §5): ortho view axis, panel toggle, the moving
    // mesh under manual coarse alignment, and a render-space drag translation.
    | SetFocusAxis of FocusAxis
    | ToggleFocusPanel
    | SetAlignMesh of string option
    | TranslateAlignMesh of V3d
    | TogglePinFocus
    | SetMovementLayer of MovementMode
    | ToggleOutlines
    // Workflow panel: camera fly-to (aspect from the view; fovY from the fixed
    // 90° horizontal fov) + navigation actions.
    | FlyTo of FlyToTarget * aspect:float
    | NavTo of NavAction

and CardMessage =
    | BringToFront of CardId
    | FinishDrag of CardId * finalPos:V2d
    | RedockCard of CardId
    | CreateCardsForPin of ScanPinId * anchor:V3d
    | RemoveCardsForPin of ScanPinId

and ScanPinMessage =
    | EnterAnchorPlacement
    | CancelPlacement
    | PlaceAnchor of worldCentre:V3d
    | SetInnerRadius of float
    // Move the pin being adjusted (numeric position fields in the flyout).
    | RepositionPin of ScanPinId * V3d
    | CommitPin
    | DeletePin of ScanPinId
    | SelectPin of ScanPinId option
    | ProbeComputed of ScanPinId * ProbeResult
    | ProbeFailed of ScanPinId * string
    | ProbePreviewComputed of ScanPinId * ProbeResult
    | ProbePreviewFailed of ScanPinId * string
    | ContactRingsComputed of ScanPinId * Map<string, V3d[][]>
