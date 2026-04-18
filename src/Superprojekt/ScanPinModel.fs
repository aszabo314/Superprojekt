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
}

[<RequireQualifiedAccess>]
type FootprintMode =
    | Circle

[<ModelType>]
type ScanPinModel = {
    Pins             : HashMap<ScanPinId, ScanPin>
    ActivePlacement  : ScanPinId option
    SelectedPin      : ScanPinId option
    PlacingMode      : FootprintMode option
    BetweenSpaceEnabled : bool
}

module ScanPinModel =
    let initial = {
        Pins            = HashMap.empty
        ActivePlacement = None
        SelectedPin     = None
        PlacingMode     = None
        BetweenSpaceEnabled = false
    }

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
