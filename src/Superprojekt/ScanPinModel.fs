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

// Geometry is metric world-space (InnerRadius = hard-core radius); render-space
// conversion happens at pipeline boundaries.
type ScanPin = {
    Id                   : ScanPinId
    // Immutable identity triple (§A), assigned at creation: a preattentive Glyph +
    // a distinct PinColor (paired, from the pin palette) + a random 2-char ShortName.
    // The pin's identity everywhere: matrix row, 3D flag label, focus label, samples.
    Glyph                : string
    ShortName            : string
    PinColor             : C4b
    Centre               : V3d
    InnerRadius          : float
    Correspondence       : Correspondence option
    HostMeshName         : string option
    CreatedAt            : DateTime
    DatasetColors        : Map<string, C4b>
    // Probe = the committed displayed pose (every consumer reads this one).
    // ProbeOther = the SAME probe at the opposite Before/After pose — fetched only
    // once a solve exists; feeds the violin chart's inactive half. SetRegView swaps
    // the two when both are ready (no refetch).
    Probe                : ProbeState
    ProbeOther           : ProbeState
    // Vertical cross-section cache for the show-overlays hold, same pose pairing
    // as the probes: Slice = committed displayed pose, SliceOther = the opposite
    // Before/After pose. SetRegView swaps a ready pair; the reg peek merely
    // selects the other cache (visual only).
    Slice                : SliceState
    SliceOther           : SliceState
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
    // Slices share every probe trigger (poses / reference / pin geometry), so
    // they invalidate together.
    let invalidateProbes (sp : ScanPinModel) =
        let pins =
            sp.Pins |> HashMap.map (fun _ p ->
                match p.Probe, p.ProbeOther, p.Slice, p.SliceOther with
                | ProbeNone, ProbeNone, SliceNone, SliceNone -> p
                | _ -> { p with Probe = ProbeNone; ProbeOther = ProbeNone
                                Slice = SliceNone; SliceOther = SliceNone })
        { sp with Pins = pins }

    // Before/After toggled: a ready (Probe, ProbeOther) pair is exactly the two
    // poses, so swap in place — no server round trip; anything unpaired refetches.
    // Slices carry the same pose pairing and swap alongside.
    let swapProbeViews (sp : ScanPinModel) =
        let pins =
            sp.Pins |> HashMap.map (fun _ p ->
                match p.Probe, p.ProbeOther, p.Slice, p.SliceOther with
                | ProbeNone, ProbeNone, SliceNone, SliceNone -> p
                | _ ->
                    let probe, probeOther =
                        match p.Probe, p.ProbeOther with
                        | ProbeReady a, ProbeReady b -> ProbeReady b, ProbeReady a
                        | _ -> ProbeNone, ProbeNone
                    let slice, sliceOther =
                        match p.Slice, p.SliceOther with
                        | SliceReady a, SliceReady b -> SliceReady b, SliceReady a
                        | _ -> SliceNone, SliceNone
                    { p with Probe = probe; ProbeOther = probeOther
                             Slice = slice; SliceOther = sliceOther })
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

    // Fixed frame of the vertical cross-section cache: the cut plane contains
    // world X (chart u) and world Z (chart v); parallel planes offset along
    // world Y, three each side at radius/4 (disc-clipped, so the outermost still
    // spans ~⅔ of the pin). A fixed direction keeps the slices comparable across
    // pins and poses.
    let sliceUDir   = V3d.IOO
    let sliceNormal = V3d.OIO
    let sliceOffsets (radius : float) =
        [| -3 .. 3 |] |> Array.map (fun i -> float i * radius * 0.25)

    // Chart frame (u, v) at plane offset w → metric world.
    let sliceToWorld (centre : V3d) (w : float) (q : V2d) =
        centre + sliceUDir * q.X + sliceNormal * w + V3d.OOI * q.Y

    // Metric world → chart frame of the CENTRE slice (the Y component drops —
    // i.e. the point is projected onto the centre plane).
    let sliceUV (centre : V3d) (p : V3d) =
        let q = p - centre
        V2d(Vec.dot q sliceUDir, q.Z)

    // Index of the centre plane (offset closest to 0).
    let sliceCentreIndex (s : PinSlice) =
        let mutable best = 0
        for k in 1 .. s.Offsets.Length - 1 do
            if abs s.Offsets.[k] < abs s.Offsets.[best] then best <- k
        best

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

    // Far-view flag pole: height grows with the pin's error magnitude
    // (max |median offset| across moving meshes; 0 until the probe lands).
    let flagMagnitude (p : ScanPin) =
        match p.Probe with
        | ProbeReady r ->
            let moving =
                r.Distributions
                |> Array.filter (fun d -> d.MeshName <> r.ReferenceMesh && d.Count > 0)
            if moving.Length = 0 then 0.0
            else moving |> Array.map (fun d -> abs d.Median) |> Array.max
        | _ -> 0.0

    // Render-space tip of the flag pole (base = pin centre, along the pin axis) —
    // shared by the 3D pole geometry and the show-overlays 2D name tags.
    let flagTopRender (commonCentroid : V3d) (datasetScale : float) (p : ScanPin) =
        let a = axis p
        let aN = if a.Length > 1e-9 then a.Normalized else V3d.OOI
        let h = renderLength datasetScale (p.InnerRadius * 1.5 + flagMagnitude p * 3.0)
        renderCentre commonCentroid datasetScale p.Centre + aN * h

    let correspondence (p : ScanPin) = p.Correspondence

    let withCorrespondence (c : Correspondence option) (p : ScanPin) =
        { p with Correspondence = c }

    // Signed error range (m, spanning 0) of one pin: min/max over its ready probe's
    // ROI samples on the moving meshes. None until the probe lands.
    let pinErrorRange (refMesh : string option) (p : ScanPin) : (float * float) option =
        match p.Probe with
        | ProbeReady r ->
            let mutable lo = 0.0
            let mutable hi = 0.0
            let mutable any = false
            for d in r.Distributions do
                if Some d.MeshName <> refMesh then
                    for v in d.Samples do
                        any <- true
                        if v < lo then lo <- v
                        if v > hi then hi <- v
            if any then Some (lo, hi) else None
        | _ -> None

    // The one Inspect error range: min/max over every pin's ROI samples (the regions
    // inside pins are the only ground truth), hard-capped at ±0.5 m; values outside
    // clamp to the end colours. No pins / no probes → the full ±0.5 m.
    let inspectRange (refMesh : string option) (pins : seq<ScanPin>) : float * float =
        let cap = 0.5
        let mutable lo = 0.0
        let mutable hi = 0.0
        let mutable any = false
        for p in pins do
            match pinErrorRange refMesh p with
            | Some (l, h) ->
                any <- true
                if l < lo then lo <- l
                if h > hi then hi <- h
            | None -> ()
        if not any then (-cap, cap)
        else (max -cap lo, min cap hi)

