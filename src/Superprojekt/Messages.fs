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
    | SetDiffRangeScale of float
    | ToggleAnchorGhostMode
    | SetQuickPinRadius of float
    // Spring-loaded hold-to-isolate modifier (forces pin isolation while held).
    | SetIsolatePeek of bool
    // Spring-loaded show-overlays modifier (greyscale-except-pins while held).
    | SetShowOverlays of bool
    // Link-views toggle (focus ↔ 3D camera sync; pure camera).
    | ToggleLinkViews
    | SetReferenceMesh of string option
    // Disabled until a solve exists.
    | SetRegView of RegView
    // Spring-loaded hold: momentarily show the other registration state (visual only).
    | SetRegPeek of bool
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
    // Per-mesh intrinsic error visualization (Overview mesh list). HeatOff = textured.
    | SetMeshHeatmap of mesh:string * HeatmapMode
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
    | SetActivePickingLayer of string option
    // hover = peek, click = select/promote.
    | SetHovered of HoverTarget option
    | SetFocusedMesh of string option
    | ReseedMesh of ScanPinId * string
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
    // aspect from the view, fovY from the fixed 90° horizontal fov.
    | FlyTo of FlyToTarget * aspect:float
    // Fly the orbit camera tight to a metric-world point: animate centre + radius
    // directly (radius = the orbit distance, not derived from a subtend).
    | FlyToPoint of world:V3d * radius:float
    // Locate a correspondence: atomic solo + focus + tight 3D fly + focus zoom,
    // capturing a back-out snapshot. BackOutLocate restores it.
    | FrameCorrespondence of ScanPinId * mesh:string
    | BackOutLocate
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
