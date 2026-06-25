namespace Superprojekt

open System
open Aardvark.Base
open FSharp.Data.Adaptive
open Adaptify

// ScanPinId is in RegistrationModel.fs (shared so the registration state
// machine stays WASM-free for tests).

[<RequireQualifiedAccess>]
type PinPhase =
    | Placement
    | Committed

// Sphere–surface contact rings (per mesh, registered world-space metres),
// server-computed, cached on the pin. Invalidated (→ RingsNone, lazy recompute)
// by radius / centre / registration-transform changes; mesh visibility only
// gates rendering, never the cache.
type ContactRingState =
    | RingsNone
    | RingsRunning
    | RingsReady of Map<string, V3d[][]>

// Human-readable short pin names (adjective + noun), derived deterministically
// from the pin id so a pin always gets the same name.
module PinNames =
    let private adjectives =
        [| "Amber"; "Brisk"; "Calm"; "Dusky"; "Early"; "Fleet"; "Grave"; "Hazel"
           "Ivory"; "Jolly"; "Keen"; "Lush"; "Misty"; "Noble"; "Olive"; "Pale"
           "Quiet"; "Rusty"; "Slate"; "Tawny"; "Umber"; "Vivid"; "Wry"; "Zesty" |]
    let private nouns =
        [| "Otter"; "Finch"; "Cedar"; "Ridge"; "Delta"; "Heron"; "Maple"; "Quartz"
           "Birch"; "Coral"; "Dune"; "Ember"; "Fjord"; "Gull"; "Holly"; "Inlet"
           "Jasper"; "Knoll"; "Larch"; "Moss"; "Nook"; "Reef"; "Spruce"; "Thorn" |]
    let generate (ScanPinId.ScanPinId g : ScanPinId) =
        let h = g.GetHashCode() &&& 0x7FFFFFFF
        sprintf "%s %s" adjectives.[h % adjectives.Length] nouns.[(h / adjectives.Length) % nouns.Length]

// Geometry is metric world-space (InnerRadius = hard-core radius); render-space
// conversion happens at pipeline boundaries.
type ScanPin = {
    Id                   : ScanPinId
    Name                 : string
    Phase                : PinPhase
    Centre               : V3d
    InnerRadius          : float
    // Optional registration correspondence anchors.
    Correspondence       : Correspondence option
    HostMeshName         : string option
    CreatedAt            : DateTime
    DatasetColors        : Map<string, C4b>
    Probe                : ProbeState
    // Second probe under effective preview transforms while a solve is pending
    // (split violin). Never persisted.
    ProbePreview         : ProbeState
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

    // The pin under inspection: the one being adjusted, else the selected one.
    let effectivePinId (sp : ScanPinModel) =
        activePlacementId sp |> Option.orElse sp.SelectedPin

    // Adaptive form, built from already-projected leaves (field-projection rule).
    let effectivePinIdA (placement : aval<PlacementState>) (selected : aval<ScanPinId option>) =
        (placement, selected) ||> AVal.map2 (fun pl sel ->
            match pl with AdjustingPin id -> Some id | _ -> sel)

    let isPlacing (sp : ScanPinModel) =
        match sp.Placement with
        | PlacementIdle -> false
        | _ -> true

    // Invalidation: unchanged pins are returned as-is so the adaptive map
    // diff sees no change.
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
    // Probe-cylinder length is fixed (no GUI slider).
    let fixedProbeLength   = 20.0

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

    // The probe matching what's on screen: preview probe while a preview is
    // pending (and ready), committed one otherwise.
    let effectiveProbe (previewPending : bool) (p : ScanPin) =
        if previewPending then
            match p.ProbePreview with
            | ProbeReady _ -> p.ProbePreview
            | _ -> p.Probe
        else p.Probe

