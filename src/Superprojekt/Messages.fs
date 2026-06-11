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
    | SetRegistrationMode of RegistrationMode
    | SetReferenceMesh of string option
    | RunRegistration
    | RegistrationComplete of string * Trafo3d * float[] * float[]
    | RegistrationFailed of string
    | ResetMeshTransforms
    | SetMeshSensorType of string * SensorType
    | SetMeshDatasetError of string * float option
    | ToggleProvenanceHeatmap
    | SetProvenanceThreshold of float
    | ToggleFalloffZoneOnly
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
    | ChartColumnClick of meshName:string
    | ClearChartSticky
    | TogglePanorama
    | PanoramasGenerated of Panorama list
    | SelectPanorama of int
    | SetPanoramaMode of PanoramaMode
    | SetPanoramaBlend of float
    | FlyToPanorama of int

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
    // Delta added to InnerRadius to get FalloffRadius — slider is relative.
    | SetFalloffDelta of float
    | CommitPin
    | DeletePin of ScanPinId
    | SelectPin of ScanPinId option
    | FocusPin of ScanPinId
    | ChangePayloadType of ScanPinId * PayloadKind
    | SetReliabilityWeight of ScanPinId * float
    | SetLineMode of ScanPinId * LineMode
    | IsolineComputed of ScanPinId * V3d[] * elevation:float
    | RidgeComputed of ScanPinId * V3d[] * scalars:float[]
    | LineCrossMeshComputed of ScanPinId * meshName:string * V3d[] * scalars:float[]
    | PatchComputed of ScanPinId * (V2d * V3d)[] * refDir:V3d * normal:V3d
    | ProbeComputed of ScanPinId * ProbeResult
    | ProbeFailed of ScanPinId * string
    | SetProbeLength of ScanPinId * float option
    | ToggleProbeLockOrder of ScanPinId
    | SetProbeXRange of ScanPinId * ProbeXRange
