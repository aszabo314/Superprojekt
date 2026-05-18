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

/// V5's selection cylinder. The fields survive into Phase 1 as the
/// renderable carrier of an "annotated region in 3D" — Phase 2 replaces
/// it with an Anchor Sphere (§D.6) and `Footprint`/`ExtentForward`/
/// `ExtentBackward` collapse into a single Radius + σ.
type SelectionPrism = {
    AnchorPoint    : V3d
    AxisDirection  : V3d
    Footprint      : FootprintPolygon
    ExtentForward  : float
    ExtentBackward : float
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

type ScanPin = {
    Id                   : ScanPinId
    Phase                : PinPhase
    Prism                : SelectionPrism
    CreationCameraState  : CameraSnapshot
    DatasetColors        : Map<string, C4b>
}

/// V5's three placement-mode gestures (§B.4) are gone; only the
/// idle/adjusting binary state survives. Phase 2 will reuse
/// `AdjustingPin` as the destination state for the V6 single-click and
/// lasso placement gestures (§D.6.1).
type PlacementState =
    | PlacementIdle
    | AdjustingPin of ScanPinId

[<ModelType>]
type ScanPinModel = {
    Pins        : HashMap<ScanPinId, ScanPin>
    SelectedPin : ScanPinId option
    Placement   : PlacementState
}

module ScanPinModel =
    let initial = {
        Pins        = HashMap.empty
        SelectedPin = None
        Placement   = PlacementIdle
    }

    let activePlacementId (sp : ScanPinModel) =
        match sp.Placement with
        | AdjustingPin id -> Some id
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

/// Floating-card payload. V5's only case (StratigraphyDiagram) is gone; the
/// card-system infrastructure (drag / redock / collapse / close) is preserved
/// for V6 anchor-sphere payload cards (§D.7) by carrying a pin reference only.
type CardContent =
    | PinCard of ScanPinId

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
