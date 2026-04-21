namespace Superprojekt

open System
open Aardvark.Base
open FSharp.Data.Adaptive
open Adaptify

[<RequireQualifiedAccess>]
type ScanPinId = ScanPinId of Guid with
    static member create () = ScanPinId (Guid.NewGuid())

type FootprintPolygon = {
    Vertices : V2d list
}

type SelectionPrism = {
    AnchorPoint    : V3d
    AxisDirection  : V3d
    Footprint      : FootprintPolygon
    ExtentForward  : float
    ExtentBackward : float
}

[<RequireQualifiedAccess>]
type CutPlaneMode =
    | AlongAxis  of angleDegrees : float
    | AcrossAxis of distanceFromAnchor : float

type CutResult = {
    MeshName  : string
    Polylines : V2d list list
}

[<RequireQualifiedAccess>]
type PinPhase =
    | Placement
    | Committed

type CameraSnapshot = {
    Center : V3d
    Radius : float
    Phi    : float
    Theta  : float
}

type RayMeshIntersection = {
    DatasetId : string
    ZValues : float list
}

type StratigraphyColumn = {
    Angle : float
    Events : (float * string) list
}

type StratigraphyData = {
    AngularResolution : int
    AxisMin : float
    AxisMax : float
    Columns : StratigraphyColumn[]
    ColumnMinZ : float[]
    ColumnMaxZ : float[]
    Rings : StratigraphyColumn[][]
    RingRadii : float[]
}

/// Pre-computed cache for O(1) between-space hover lookups.
type BandCache = {
    Brackets : (float * float)[][][]
    Labels : int[][][]
    Components3D : Map<int * int, (float * float) list>[]
    Components2D : Map<int, (float * float) list>[]
}

type StratigraphyDisplayMode =
    | Undistorted
    | Normalized

type BetweenSpaceHover = {
    ColumnIdx : int
    HoverZ    : float
    Pinned    : bool
}

type GhostClipMode =
    | GhostClipOff
    | GhostClipOn

type ExtractedLinesMode = {
    ShowCutPlaneLines     : bool
    ShowCylinderEdgeLines : bool
}

module ExtractedLinesMode =
    let initial = { ShowCutPlaneLines = true; ShowCylinderEdgeLines = false }

type CutAspectMode =
    | CutAspectFit
    | CutAspectOneToOne

type CutLineHover = {
    MeshName    : string
    DiagramPos  : V2d
    WorldPos    : V3d
    CutDistance : float
    Elevation   : float
}

type ScanPin = {
    Id                   : ScanPinId
    Phase                : PinPhase
    Prism                : SelectionPrism
    CutPlane             : CutPlaneMode
    CreationCameraState  : CameraSnapshot
    CutResults           : Map<string, CutResult>
    CutResultsPlane      : CutPlaneMode
    DatasetColors        : Map<string, C4b>
    Stratigraphy         : StratigraphyData option
    BandCache            : BandCache option
    StratigraphyDisplay  : StratigraphyDisplayMode
    GhostClip            : GhostClipMode
    GhostClipCutPlane    : bool
    ExtractedLines       : ExtractedLinesMode
    BetweenSpaceHover    : BetweenSpaceHover option
    CutAspect            : CutAspectMode
    CutLineHover         : CutLineHover option
}

type PlacementMode =
    | ProfileMode
    | PlanMode
    | AutoMode

type ProfilePlacementState =
    | ProfileWaitingForFirstPoint
    | ProfileWaitingForSecondPoint of firstPoint:V3d * previewPos:V3d option

type PlanPlacementState =
    | PlanWaitingForDrag
    | PlanDragging of center:V3d * currentRadius:float

type AutoPreview = {
    Center         : V3d
    Axis           : V3d
    Radius         : float
    CutPlaneMode   : CutPlaneMode
    DominantNormal : V3d
}

type AutoPlacementState =
    | AutoHovering of AutoPreview option

type PlacementState =
    | PlacementIdle
    | ProfilePlacement of ProfilePlacementState
    | PlanPlacement of PlanPlacementState
    | AutoPlacement of AutoPlacementState
    | AdjustingPin of ScanPinId * PlacementMode

[<ModelType>]
type ScanPinModel = {
    Pins                : HashMap<ScanPinId, ScanPin>
    SelectedPin         : ScanPinId option
    Placement           : PlacementState
    LastPlacementMode   : PlacementMode
    BetweenSpaceEnabled : bool
}

module ScanPinModel =
    let initial = {
        Pins                = HashMap.empty
        SelectedPin         = None
        Placement           = PlacementIdle
        LastPlacementMode   = ProfileMode
        BetweenSpaceEnabled = false
    }

    let activePlacementId (sp : ScanPinModel) =
        match sp.Placement with
        | AdjustingPin(id, _) -> Some id
        | _ -> None

    let isPlacing (sp : ScanPinModel) =
        match sp.Placement with
        | PlacementIdle -> false
        | _ -> true

[<RequireQualifiedAccess>]
type CardId = CardId of Guid with
    static member create () = CardId (Guid.NewGuid())

type CardAnchor =
    | AnchorToWorldPoint of V3d

type CardAttachment =
    | CardAttached
    | CardDetached of screenPos:V2d
    | CardDragging of cardPos:V2d * grabOffset:V2d

type CardContent =
    | StratigraphyDiagram of ScanPinId

type Card = {
    Id         : CardId
    Anchor     : CardAnchor
    Attachment : CardAttachment
    Size       : V2d
    Content    : CardContent
    Visible    : bool
    ZOrder     : int
}

[<ModelType>]
type CardSystemModel = {
    Cards       : HashMap<CardId, Card>
    DraggedCard : CardId option
    NextZOrder  : int
}

module CardSystemModel =
    let initial = {
        Cards       = HashMap.empty
        DraggedCard = None
        NextZOrder  = 1
    }

module PinCylinderDrag =
    let isActive : cval<bool> = cval false
