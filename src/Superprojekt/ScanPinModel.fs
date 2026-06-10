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

type PointPayload = {
    ReliabilityWeight : float
}

type LineMode =
    | ElevationIsoline of elevation:float
    | CurvatureRidge

type LinePayload = {
    Mode            : LineMode
    Points          : V3d[]
    ScalarVals      : float[]
    CrossMeshTraces : Map<string, V3d[] * float[]>
}

type PatchPayload = {
    CenterOnMesh    : V3d
    Radius          : float
    SourceMeshName  : string
    ProjectedPoints : (V2d * V3d)[]
    CompassNorth    : V2d
    RefDirWorld     : V3d
    NormalWorld     : V3d
}

type PayloadType =
    | Point of PointPayload
    | Line  of LinePayload
    | Patch of PatchPayload

type PayloadKind =
    | PointKind
    | LineKind
    | PatchKind

module PayloadType =
    let kind = function
        | Point _ -> PointKind
        | Line  _ -> LineKind
        | Patch _ -> PatchKind

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

// All ScanPin geometry is metric world-space; InnerRadius and FalloffRadius
// are independent. Render-space conversion happens at pipeline boundaries.
type ScanPin = {
    Id                   : ScanPinId
    Phase                : PinPhase
    Centre               : V3d
    InnerRadius          : float
    FalloffRadius        : float
    Payload              : PayloadType
    HostMeshName         : string option
    CreationCameraState  : CameraSnapshot
    CreatedAt            : DateTime
    DatasetColors        : Map<string, C4b>
}

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

module ScanPin =
    // World-space (metric) → render-space (post centroid translate, post scale).
    let renderCentre (commonCentroid : V3d) (datasetScale : float) (worldCentre : V3d) =
        (worldCentre - commonCentroid) * datasetScale
    // Render-space → world-space (metric).
    let worldCentre (commonCentroid : V3d) (datasetScale : float) (renderCentre : V3d) =
        renderCentre / datasetScale + commonCentroid
    // Metric distance/radius → render-space.
    let renderLength (datasetScale : float) (metricLength : float) =
        metricLength * datasetScale

[<RequireQualifiedAccess>]
type CardId = CardId of Guid with
    static member create () = CardId (Guid.NewGuid())

type CardAnchor =
    | AnchorToWorldPoint of V3d

type CardAttachment =
    | CardAttached
    | CardDetached of screenPos:V2d

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
    NextZOrder  : int
}

module CardSystemModel =
    let initial = {
        Cards       = HashMap.empty
        NextZOrder  = 1
    }
