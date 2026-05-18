namespace Superprojekt

open System
open Aardvark.Base
open FSharp.Data.Adaptive
open Adaptify

[<RequireQualifiedAccess>]
type ScanPinId = ScanPinId of Guid with
    static member create () = ScanPinId (Guid.NewGuid())

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

/// V6 §D.7.1 — the 0D payload. The sphere alone defines a region of
/// interest; ReliabilityWeight feeds the registration solver weighting
/// once Phase 6 lands. Phase 2 always uses ReliabilityWeight = 1.0.
type PointPayload = {
    ReliabilityWeight : float
}

/// V6 §C.3 — Line / Patch payloads arrive in Phase 4 (§D.7.2 / §D.7.3).
/// Phase 2 ships only the placeholder Point case.
type PayloadType =
    | Point of PointPayload

[<RequireQualifiedAccess>]
type CorrespondenceLinkId = CorrespondenceLinkId of Guid

/// V6 §D.6 — the V6 annotation primitive. Replaces the V5 selection-prism
/// cylinder + cut plane. Centre is in render space (after dataset scale and
/// centroid offset); Sigma ≤ Radius and drives the Gaussian falloff
/// rendered by ScanPinScene + consumed by the registration / error
/// pipelines in later phases.
type ScanPin = {
    Id                   : ScanPinId
    Phase                : PinPhase
    Centre               : V3d
    Radius               : float
    Sigma                : float
    Payload              : PayloadType
    HostMeshName         : string option
    CorrespondenceLinkId : CorrespondenceLinkId option
    CreationCameraState  : CameraSnapshot
    CreatedAt            : DateTime
    DatasetColors        : Map<string, C4b>
}

/// Anchor-placement is the single placement gesture available in Phase 2;
/// the lasso variant arrives in Phase 3 (§D.3 + §D.6.1). Hover preview state
/// lives in a View-side cval (`placementHover`), so the model only carries
/// the active/idle distinction.
type PlacementState =
    | PlacementIdle
    | AnchorPlacement
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
