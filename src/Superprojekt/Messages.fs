namespace Superprojekt

open Aardvark.Base
open Superprojekt

type Message =
    | CameraMessage      of OrbitMessage
    | CentroidsLoaded    of (string * V3d)[]
    | LoadFinished       of string
    | SetVisible         of string * bool
    | ToggleMenu
    | FilteredMeshLoaded of string * V3d * int[]
    | ClearFilteredMesh
    | LogDebug           of string
    | ToggleFullscreen
    | ToggleDifferenceRendering
    | ToggleGhostSilhouette
    | SetGhostOpacity of float
    | SetShadingStrength of float
    | SetSlopeThresholdDeg of float
    | ToggleAnchorGhostMode
    | SetRegistrationMode of RegistrationMode
    | SetReferenceMesh of string option
    | RunRegistration
    | RegistrationProgress of int * float
    | RegistrationComplete of string * Trafo3d * float[] * float[]
    | RegistrationFailed of string
    | ResetMeshTransforms
    | SetMeshSensorType of string * SensorType
    | SetMeshDatasetError of string * float option
    | ToggleProvenanceHeatmap
    | SetProvenanceThreshold of float
    | ToggleFalloffZoneOnly
    | ToggleFusionMode
    | SaveWorkspace
    | LoadWorkspace of string
    | SetMinDifferenceDepth of float
    | SetMaxDifferenceDepth of float
    | SceneBoundsLoaded  of (string * Box3d)[]
    | DatasetsLoaded     of string[]
    | SetActiveDataset   of string
    | SetDatasetScale    of string * float
    | ScanPinMsg              of ScanPinMessage
    | JumpToMesh of string
    | ToggleColorMode
    | CardMsg of CardMessage
    | ExploreMsg of ExploreModeMessage
    | SetRenderingMode of RenderingMode
    | ToggleMeshSolo of string
    | ShowAllMeshes
    | HideAllMeshes
    | ResetCamera
    | SetExploreCardPos of V2d
    | ToggleGearPopover
    | EditPin of ScanPinId
    | SetActivePickingLayer of string option
    | LassoBegin
    | LassoAddVertex of V2d
    | LassoCommit    of viewTrafo:Trafo3d * projTrafo:Trafo3d * vpSize:V2i
    | LassoCancel
    | LassoClear

and ExploreSignal =
    | FeatureConfidenceSignal
    | DisagreementSignal

and ExploreModeMessage =
    | SetExploreEnabled of bool
    | SetReferenceAxisMode of ReferenceAxisMode
    | SetSignalEnabled of ExploreSignal * bool
    | SetSignalThreshold of ExploreSignal * float
    | SetSignalColor of ExploreSignal * C4f
    | SetMixMode of MixMode

and CardMessage =
    | BringToFront of CardId
    | FinishDrag of CardId * finalPos:V2d
    | RedockCard of CardId
    | CreateCardsForPin of ScanPinId * anchor:V3d
    | RemoveCardsForPin of ScanPinId

and ScanPinMessage =
    | EnterAnchorPlacement
    | CancelPlacement
    | PlaceAnchor of centre:V3d
    | SetAnchorRadius of float
    | SetAnchorSigma of float
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
