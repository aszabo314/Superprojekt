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
    | ToggleFullscreen
    | ToggleGhostSilhouette
    | SetGhostOpacity of float
    | SetShadingStrength of float
    | SetSlopeThresholdDeg of float
    | ToggleAnchorGhostMode
    // 3D sectioning / cutaway.
    | SetReferencePeek of bool
    | SetClipPlanes of ClipPlane list
    | ToggleCutaway
    | SetCutawayMode of ClipMode
    | ToggleClipAboveIso
    // Lock the chart-driven iso-plane at a signed distance into ClipPlanes.
    | LockIsoPlane of float
    | ToggleRuler
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
    // Pending-preview lifecycle + history.
    | CommitRegistration
    | DiscardRegistration
    | RollbackRegStep
    | ResetRegistration
    // Correspondence anchors.
    | ToggleCorrespondence of ScanPinId
    // Seeded correspondence markers apply immediately (no review modal).
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
    | SetMeshDatasetError of string * float option
    | SetHeatmapMode of HeatmapMode
    | SetProvenanceThreshold of float
    // A2: per-mesh signed-distance surface colour map.
    | ToggleSurfaceDistance
    | SurfaceDistanceComputed of mesh:string * float32[]
    | ToggleFusionMode
    | SceneBoundsLoaded  of (string * Box3d)[]
    | DatasetsLoaded     of string[]
    | SetActiveDataset   of string
    | SetDatasetScale    of string * float
    | ScanPinMsg              of ScanPinMessage
    | JumpToMesh of string
    | CardMsg of CardMessage
    | SetRenderingMode of RenderingMode
    | ToggleMeshSolo of string
    | ShowAllMeshes
    | HideAllMeshes
    | ResetCamera
    | SetLassoCardPos of V2d
    | ToggleGearPopover
    | EditPin of ScanPinId
    | SetActivePickingLayer of string option
    | LassoBegin
    | ToggleLassoEnabled
    | LassoAddVertex of V2d
    | LassoCommit    of viewTrafo:Trafo3d * projTrafo:Trafo3d * vpSize:V2i
    | LassoCancel
    | LassoClear
    | SaveWorkspace
    | LoadWorkspaceJson of string
    | StartRetarget of targetMesh:string
    | RetargetCandidatesReady of RetargetCandidate[]
    | SetRetargetDecision of ScanPinId * RetargetDecision
    | CommitRetarget
    | CancelRetarget
    | HoverProbeAt of screenPx:V2d * world:V3d
    | HoverProbeResult of ProbeState
    | ClearHoverProbe
    | SetChartCursor of ChartCursor option
    | SetChartHoverMesh of string option
    | SetWorkflowPinHover of ScanPinId option
    | ChartColumnClick of meshName:string
    | ClearChartSticky
    | TogglePanorama
    | PanoramasGenerated of Panorama list
    | SelectPanorama of int
    | SetPanoramaMode of PanoramaMode
    | SetPanoramaBlend of float
    | FlyToPanorama of int
    | StudiesLoaded of string[]
    | ToggleWorkflowPanel
    // Workflow panel: camera fly-to (aspect supplied by the view — fovY
    // derives from the fixed 90° horizontal fov) and navigation actions.
    | FlyTo of FlyToTarget * aspect:float
    | NavTo of NavAction
    | StudyMsg of StudyMessage

and StudyMessage =
    // Session lifecycle (§1/§10): real entry via /s/{token}, demo entry from
    // the gear popover, exit only for demo sessions.
    | StudyJoin of token:string
    | StudyStartDemo of studyId:string * StudyCondition
    | StudySessionStarted of StudySessionInit
    | StudySessionFailed of message:string
    | StudyExitDemo
    // Runtime (§4): Next gating, instruction overlay, tutorial gold flow.
    | StudyNext
    | StudyReopenOverlay
    | StudyCloseOverlay
    | StudyGoldResult of questionId:string * correct:bool * screened:bool
    | StudyCompletionCode of string
    | StudyCompletionFailed of string
    | StudySetAsFinal
    // Answer drafts (§7): post immediately on change, again on Next.
    | StudySetChoice of questionId:string * option_:int
    | StudySetNumber of questionId:string * value:float
    | StudySetText of questionId:string * text:string
    | StudySetGridItem of questionId:string * item:int * value:float
    | StudySetConfidence of questionId:string * confidence:int
    | StudyArmSceneClick of questionId:string
    | StudyCancelSceneClick
    | StudySceneClickHit of world:V3d

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
