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

/// V3: a single z-aligned ray query result for one mesh at one (angle, axisPosition) sample point.
type RayMeshIntersection = {
    DatasetId : string
    /// Z-values where the ray intersects this mesh's surface.
    /// Multiple values possible for non-heightfield meshes (folds, overhangs).
    ZValues : float list
}

/// V3: a single column in the stratigraphy diagram (one angular position on the cylinder).
type StratigraphyColumn = {
    /// Angle around the cylinder axis (radians, 0 to 2π).
    Angle : float
    /// All intersection events in this column, sorted by z ascending.
    Events : (float * string) list
}

/// V3: full stratigraphy data for one ScanPin.
/// STUB(server): computed by casting z-aligned rays at a grid of (angle, axisPosition)
/// points on the cylinder surface.
type StratigraphyData = {
    AngularResolution : int
    AxisMin : float
    AxisMax : float
    Columns : StratigraphyColumn[]
    /// Per-column min/max z across all datasets (for normalization).
    ColumnMinZ : float[]
    ColumnMaxZ : float[]
}

/// V3: display mode for the stratigraphy diagram.
type StratigraphyDisplayMode =
    | Undistorted
    | Normalized

/// V3: state for the explosion view inside the ScanPin cylinder.
type ExplosionState = {
    /// 0 = no explosion. Each mesh i is displaced by (i * ExpansionFactor * spacing).
    ExpansionFactor : float
    Enabled : bool
}

module ExplosionState =
    let initial = { ExpansionFactor = 0.0; Enabled = false }

/// V3: state for the between-space hover highlighting.
type BetweenSpaceHighlight = {
    LowerDataset : string
    UpperDataset : string
    Angle  : float
    ZLower : float
    ZUpper : float
    Active : bool
}

/// V3: ghost clipping cylinder toggle for a pin.
type GhostClipMode =
    | GhostClipOff
    | GhostClipOn

/// V3: extracted-line toggles for a pin.
type ExtractedLinesMode = {
    ShowCutPlaneLines     : bool
    ShowCylinderEdgeLines : bool
}

module ExtractedLinesMode =
    let initial = { ShowCutPlaneLines = true; ShowCylinderEdgeLines = true }

type ScanPin = {
    Id                   : ScanPinId
    Phase                : PinPhase
    Prism                : SelectionPrism
    CutPlane             : CutPlaneMode
    CreationCameraState  : CameraSnapshot
    CutResults           : Map<string, CutResult>
    DatasetColors        : Map<string, C4b>
    GridEval             : GridEvalData option

    // ── V3 fields ──────────────────────────────────────────────
    Stratigraphy         : StratigraphyData option
    StratigraphyDisplay  : StratigraphyDisplayMode
    GhostClip            : GhostClipMode
    ExtractedLines       : ExtractedLinesMode
    Explosion            : ExplosionState
    BetweenSpaceHover    : BetweenSpaceHighlight option
}

[<RequireQualifiedAccess>]
type FootprintMode =
    | Circle
    | Polygon

type PlacementState =
    | PlacementIdle
    | DefiningFootprint of
        anchorPoint : V3d *
        anchorRenderPos : V3d *
        axisDirection : V3d *
        currentRadius : float *
        footprintMode : FootprintMode
    | DefiningCutPlane of prism : SelectionPrism
    | Adjusting of pinId : ScanPinId

[<ModelType>]
type ScanPinModel = {
    Pins             : HashMap<ScanPinId, ScanPin>
    ActivePlacement  : ScanPinId option
    SelectedPin      : ScanPinId option
    PlacingMode      : FootprintMode option
}

module ScanPinModel =
    let initial = {
        Pins            = HashMap.empty
        ActivePlacement = None
        SelectedPin     = None
        PlacingMode     = None
    }

// ── Card system ──────────────────────────────────────────────

[<RequireQualifiedAccess>]
type CardId = CardId of Guid with
    static member create () = CardId (Guid.NewGuid())

type CardEdge = EdgeTop | EdgeBottom | EdgeLeft | EdgeRight

type CardAnchor =
    | AnchorToWorldPoint of V3d
    | AnchorToCard of parentId:CardId * edge:CardEdge

type CardAttachment =
    | CardAttached
    | CardDetached of screenPos:V2d
    | CardDragging of cardPos:V2d * grabOffset:V2d

type CardContent =
    | StratigraphyDiagram of ScanPinId
    | PinControls of ScanPinId

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

