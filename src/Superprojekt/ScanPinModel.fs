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
/// once Phase 6 lands.
type PointPayload = {
    ReliabilityWeight : float
}

/// V6 §D.7.2 — line-on-surface sub-modes. ElevationIsoline carries the
/// target elevation; CurvatureRidge has no parameters (start direction is
/// re-derived from local curvature at each step).
type LineMode =
    | ElevationIsoline of elevation:float
    | CurvatureRidge

/// V6 §D.7.2 — polyline on the host mesh surface. Points are world-space;
/// ScalarVals matches Points length and stores elevation (isoline mode) or
/// curvature magnitude (ridge mode) for axis labelling in the card plot.
/// CrossMeshTraces maps mesh name → its traced polyline + scalar values
/// for cross-mesh comparison.
type LinePayload = {
    Mode            : LineMode
    Points          : V3d[]
    ScalarVals      : float[]
    CrossMeshTraces : Map<string, V3d[] * float[]>
}

/// V6 §D.7.3 — unwrapped 2D patch. ProjectedPoints stores (patch_coord,
/// world_pos) pairs; CompassNorth is the patch-space direction pointing
/// to project north. SourceMeshName is "dataset/mesh" (switchable via
/// the patch card's mesh selector). RefDirWorld + NormalWorld are stored
/// alongside so the 3D footprint can draw the compass-rose ring without
/// re-deriving the tangent plane.
type PatchPayload = {
    CenterOnMesh    : V3d
    Radius          : float
    SourceMeshName  : string
    ProjectedPoints : (V2d * V3d)[]
    CompassNorth    : V2d
    RefDirWorld     : V3d
    NormalWorld     : V3d
}

/// V6 §C.3 — the three payload kinds. Switching destroys the current
/// payload and instantiates the new one with default parameters.
type PayloadType =
    | Point of PointPayload
    | Line  of LinePayload
    | Patch of PatchPayload

/// Lightweight tag used by the flyout's Payload-type selector and the
/// `ChangePayloadType` message; carrying the heavy record types in the
/// message DU bloats the diff in every Adaptify pass.
type PayloadKind =
    | PointKind
    | LineKind
    | PatchKind

module PayloadType =
    let kind = function
        | Point _ -> PointKind
        | Line  _ -> LineKind
        | Patch _ -> PatchKind

    /// Defaults per §D.6.4 ("switching destroys the current payload and
    /// instantiates the new one with default parameters"). The Line/Patch
    /// defaults are placeholders until Phase 4b/4d wire real geometry.
    let defaultFor (radius : float) (centre : V3d) (host : string option) (kind : PayloadKind) =
        match kind with
        | PointKind ->
            Point { ReliabilityWeight = 1.0 }
        | LineKind ->
            Line {
                Mode            = ElevationIsoline centre.Z
                Points          = [||]
                ScalarVals      = [||]
                CrossMeshTraces = Map.empty
            }
        | PatchKind ->
            Patch {
                CenterOnMesh    = centre
                Radius          = radius
                SourceMeshName  = host |> Option.defaultValue ""
                ProjectedPoints = [||]
                CompassNorth    = V2d(1.0, 0.0)
                RefDirWorld     = V3d.OIO
                NormalWorld     = V3d.OOI
            }

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
