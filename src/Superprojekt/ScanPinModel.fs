namespace Superprojekt

open System
open Aardvark.Base
open FSharp.Data.Adaptive
open Adaptify

// ScanPinId moved to RegistrationModel.fs (shared with the registration
// types so the pure registration state machine stays WASM-free for tests).

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
    // Ensemble-registration correspondence (spec: extends the Point payload,
    // not a new payload type). None = never enabled.
    Correspondence    : Correspondence option
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
            Point { ReliabilityWeight = 1.0; Correspondence = None }
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

// Sphere–surface contact rings (per mesh, registered world-space metres),
// computed server-side and cached on the pin. Invalidated (→ RingsNone, lazy
// recompute) by radius / centre / registration-transform changes; mesh
// visibility only gates rendering, never the cache.
type ContactRingState =
    | RingsNone
    | RingsRunning
    | RingsReady of Map<string, V3d[][]>

// All ScanPin geometry is metric world-space; InnerRadius and FalloffRadius
// are independent. Render-space conversion happens at pipeline boundaries.
// Probe: cached M3C2 probe result for Point payloads, recomputed lazily after
// invalidation (ProbeNone). ProbeLengthOverride None = server auto-length.
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
    Probe                : ProbeState
    // Second probe under the effective preview transforms while a
    // registration solve is pending (split violin). Never persisted.
    ProbePreview         : ProbeState
    ProbeLengthOverride  : float option
    ProbeLockOrder       : bool
    ProbeXRange          : ProbeXRange
    ContactRings         : ContactRingState
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

    // Probe invalidation: identical pins are returned as-is so the
    // adaptive map diff sees no change.
    let invalidateProbes (sp : ScanPinModel) =
        let pins =
            sp.Pins |> HashMap.map (fun _ p ->
                match p.Probe with
                | ProbeNone -> p
                | _ -> { p with Probe = ProbeNone })
        { sp with Pins = pins }

    let invalidateRings (sp : ScanPinModel) =
        let pins =
            sp.Pins |> HashMap.map (fun _ p ->
                match p.ContactRings with
                | RingsNone -> p
                | _ -> { p with ContactRings = RingsNone })
        { sp with Pins = pins }

    let invalidatePreviewProbes (sp : ScanPinModel) =
        let pins =
            sp.Pins |> HashMap.map (fun _ p ->
                match p.ProbePreview with
                | ProbeNone -> p
                | _ -> { p with ProbePreview = ProbeNone })
        { sp with Pins = pins }

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

    // The pin's reference axis: probe normal when available, Patch normal as
    // a fallback, world-up otherwise (correct for heightfields).
    let axis (p : ScanPin) =
        match p.Probe with
        | ProbeReady r -> r.Normal
        | _ ->
            match p.Payload with
            | Patch pp when pp.NormalWorld.Length > 1e-9 -> pp.NormalWorld.Normalized
            | _ -> V3d.OOI

    let correspondence (p : ScanPin) =
        match p.Payload with
        | Point pp -> pp.Correspondence
        | _ -> None

    let withCorrespondence (c : Correspondence option) (p : ScanPin) =
        match p.Payload with
        | Point pp -> { p with Payload = Point { pp with Correspondence = c } }
        | _ -> p

    // The probe that matches what's on screen: the preview probe while a
    // registration preview is pending (and ready), the committed one otherwise.
    let effectiveProbe (previewPending : bool) (p : ScanPin) =
        if previewPending then
            match p.ProbePreview with
            | ProbeReady _ -> p.ProbePreview
            | _ -> p.Probe
        else p.Probe

// Elevation cursor driven by hovering a pin card's violin chart: a signed
// distance (metres) along the pin's probe axis. Extended = Alt held, the 3D
// slicing plane grows from pin-radius disk to scene-wide.
type ChartCursor = {
    PinId    : ScanPinId
    Distance : float
    Extended : bool
}

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
