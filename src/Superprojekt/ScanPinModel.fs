namespace Superprojekt

open System
open Aardvark.Base
open FSharp.Data.Adaptive
open Adaptify

// ScanPinId is in RegistrationModel.fs (shared so the registration state
// machine stays WASM-free for tests).

// Per mesh, registered world-space metres. Invalidated (→ RingsNone, lazy
// recompute) by radius / pose changes; mesh visibility only gates rendering,
// never the cache.
type ContactRingState =
    | RingsNone
    | RingsRunning
    | RingsReady of Map<string, V3d[][]>

// ATOMIC pin: {area marker + one point on each pair mesh} — it exists only
// complete; there is no partial pin. Geometry is stored mesh-local:
//  • the area marker in its placement mesh's frame — the pin RIDES that mesh
//    (a later solve that moves the mesh moves the pin),
//  • each correspondence point in its own mesh's frame.
// The points are UNCONSTRAINED — they may lie outside the area marker's ROI
// (that displacement is what registration corrects); the ROI scopes error
// analysis only. Pins are immovable and unrenamable — edits are radius,
// delete, and point re-pick.
type ScanPin = {
    Id           : ScanPinId
    // Immutable identity, assigned at creation: a random 2-char ShortName.
    ShortName    : string
    // The unordered pair this pin belongs to (PairCell.key order).
    Pair         : string * string
    AnchorMesh   : string
    CentreLocal  : V3d
    InnerRadius  : float
    // fst Pair's point / snd Pair's point, each in its mesh's own frame.
    PointA       : V3d
    PointB       : V3d
    CreatedAt    : DateTime
    ContactRings : ContactRingState
}

// The in-flight placement transaction: modal, FREE ORDER (centre and the two
// points in any sequence via the arm buttons, re-picking allowed), nothing
// persists until commit — abort rolls the whole draft back.
type PinDraft = {
    Pair   : string * string
    // (placement mesh, own-frame centre) once dropped.
    Area   : (string * V3d) option
    PointA : V3d option
    PointB : V3d option
}

module PinDraft =
    let empty (pair : string * string) =
        { Pair = pair; Area = None; PointA = None; PointB = None }
    let complete (d : PinDraft) = d.Area.IsSome && d.PointA.IsSome && d.PointB.IsSome
    let pointCount (d : PinDraft) =
        (if d.PointA.IsSome then 1 else 0) + (if d.PointB.IsSome then 1 else 0)

type PlacementState =
    | PlacementIdle
    | PlacementActive of PinDraft

// The placement transaction is the only pin-local UI state; every pick —
// draft picks AND a committed pin's point re-pick — goes through the armed
// pick (Model.ArmedPick): the ARM TARGET attributes the mesh, so any view can
// be the pick surface (the old point stands until the pick commits the
// replacement — no partial pin ever exists).
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

    let invalidateRings (sp : ScanPinModel) =
        let pins =
            sp.Pins |> HashMap.map (fun _ p ->
                match p.ContactRings with
                | RingsNone -> p
                | _ -> { p with ContactRings = RingsNone })
        { sp with Pins = pins }

module ScanPin =
    // World-space (metric) → render-space (post centroid translate, post scale).
    let renderCentre (commonCentroid : V3d) (datasetScale : float) (worldCentre : V3d) =
        (worldCentre - commonCentroid) * datasetScale
    let worldCentre (commonCentroid : V3d) (datasetScale : float) (renderCentre : V3d) =
        renderCentre / datasetScale + commonCentroid
    let renderLength (datasetScale : float) (metricLength : float) =
        metricLength * datasetScale

    // The pin rides its placement mesh: metric-world centre = that mesh's
    // displayed pose applied to the stored local centre.
    let centreWorldWith (dispWorldOfAnchor : Trafo3d) (p : ScanPin) =
        dispWorldOfAnchor.Forward.TransformPos p.CentreLocal

    // Display axis for pin/flag orientation: the project-wide average up-normal
    // when the data is terrain-like (significant normal consensus —
    // MeshView.projectUpNormal), else world-up (correct for heightfields).
    let axisWith (globalUp : V3d option) (_p : ScanPin) =
        match globalUp with
        | Some u -> u
        | None -> V3d.OOI

    // Screen-constant flag sizing: the pole height is a fixed fraction of the
    // eye→pin distance (render space), clamped in METRIC WORLD to [0.1, 20] m;
    // the gear's flag-scale multiplier scales the fraction AND both bounds.
    // Every flag element (pole, top ring, name, base cross) derives from this
    // one height, so the whole flag resizes together.
    let flagHeightRender (datasetScale : float) (flagScale : float) (eyeDistRender : float) =
        let hWorld = 0.10 * flagScale * eyeDistRender / datasetScale
        renderLength datasetScale (min (20.0 * flagScale) (max (0.1 * flagScale) hWorld))
