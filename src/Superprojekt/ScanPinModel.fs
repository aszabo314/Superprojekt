namespace Superprojekt

open System
open Aardvark.Base
open FSharp.Data.Adaptive
open Adaptify

// ScanPinId is in RegistrationModel.fs (shared so the registration state
// machine stays WASM-free for tests).

// Per mesh, registered world-space metres. Invalidated (→ RingsNone, lazy
// recompute) by radius / centre / transform changes; mesh visibility only gates
// rendering, never the cache.
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
    Centre               : V3d
    InnerRadius          : float
    Correspondence       : Correspondence option
    HostMeshName         : string option
    CreatedAt            : DateTime
    DatasetColors        : Map<string, C4b>
    Probe                : ProbeState
    ContactRings         : ContactRingState
}

type PlacementState =
    | PlacementIdle
    | AnchorPlacement

// Pin selection lives in Model.Selection, not here — the placement state machine
// is the only pin-local UI state.
[<ModelType>]
type ScanPinModel = {
    Pins        : HashMap<ScanPinId, ScanPin>
    Placement   : PlacementState
}

module ScanPinModel =
    let initial = {
        Pins        = HashMap.empty
        Placement   = PlacementIdle
    }

    let isPlacing (sp : ScanPinModel) =
        match sp.Placement with
        | PlacementIdle -> false
        | _ -> true

    // Unchanged pins are returned as-is so the adaptive map diff sees no change.
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

module ScanPin =
    let fixedProbeLength   = 20.0

    // World-space (metric) → render-space (post centroid translate, post scale).
    let renderCentre (commonCentroid : V3d) (datasetScale : float) (worldCentre : V3d) =
        (worldCentre - commonCentroid) * datasetScale
    let worldCentre (commonCentroid : V3d) (datasetScale : float) (renderCentre : V3d) =
        renderCentre / datasetScale + commonCentroid
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

