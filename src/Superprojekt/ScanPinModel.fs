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

// Sphere–surface contact rings (per mesh, registered world-space metres),
// computed server-side and cached on the pin. Invalidated (→ RingsNone, lazy
// recompute) by radius / centre / registration-transform changes; mesh
// visibility only gates rendering, never the cache.
type ContactRingState =
    | RingsNone
    | RingsRunning
    | RingsReady of Map<string, V3d[][]>

// All ScanPin geometry is metric world-space; InnerRadius and FalloffRadius
// are independent (FalloffRadius is fixed; see ScanPin.fixedFalloffRadius).
// Render-space conversion happens at pipeline boundaries. Probe: cached M3C2
// result, recomputed lazily after invalidation (ProbeNone).
type ScanPin = {
    Id                   : ScanPinId
    Phase                : PinPhase
    Centre               : V3d
    InnerRadius          : float
    FalloffRadius        : float
    // Optional registration correspondence anchors.
    Correspondence       : Correspondence option
    HostMeshName         : string option
    CreatedAt            : DateTime
    DatasetColors        : Map<string, C4b>
    Probe                : ProbeState
    // Second probe under the effective preview transforms while a
    // registration solve is pending (split violin). Never persisted.
    ProbePreview         : ProbeState
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
    // Falloff is a fixed 1.2 m soft margin *beyond* the inner radius (no GUI
    // slider — it tracks the inner radius). Probe-cylinder length is fixed too.
    let fixedFalloffDelta = 1.2
    let fixedProbeLength   = 20.0
    let falloffFor (innerRadius : float) = innerRadius + fixedFalloffDelta

    // World-space (metric) → render-space (post centroid translate, post scale).
    let renderCentre (commonCentroid : V3d) (datasetScale : float) (worldCentre : V3d) =
        (worldCentre - commonCentroid) * datasetScale
    // Render-space → world-space (metric).
    let worldCentre (commonCentroid : V3d) (datasetScale : float) (renderCentre : V3d) =
        renderCentre / datasetScale + commonCentroid
    // Metric distance/radius → render-space.
    let renderLength (datasetScale : float) (metricLength : float) =
        metricLength * datasetScale

    // The pin's reference axis: probe normal when available, world-up
    // otherwise (correct for heightfields).
    let axis (p : ScanPin) =
        match p.Probe with
        | ProbeReady r -> r.Normal
        | _ -> V3d.OOI

    let correspondence (p : ScanPin) = p.Correspondence

    let withCorrespondence (c : Correspondence option) (p : ScanPin) =
        { p with Correspondence = c }

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
