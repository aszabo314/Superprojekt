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
    // Immutable identity pair (§A), assigned at creation: a distinct PinColor
    // (from the pin palette) + a random 2-char ShortName, everywhere shown as a
    // colour-filled element with the name inside: matrix row, 3D flag label,
    // focus label, samples.
    ShortName            : string
    PinColor             : C4b
    Centre               : V3d
    InnerRadius          : float
    Correspondence       : Correspondence
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

    // Slice-only invalidation (window/context tunables): the probes stay valid.
    let invalidateSlices (sp : ScanPinModel) =
        let pins =
            sp.Pins |> HashMap.map (fun _ p ->
                match p.Slice, p.SliceOther with
                | SliceNone, SliceNone -> p
                | _ -> { p with Slice = SliceNone; SliceOther = SliceNone })
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

    // MEASUREMENT reach: the probe cylinder's bounding-sphere radius (radius
    // InnerRadius ⊥ axis, length fixedProbeLength along it). This is the InRoi
    // membership rule — "the mesh has surface the probe can measure here". It is
    // NOT the correspondence rule: anchors live within the pin sphere itself
    // (InnerRadius), enforced at seed, pick and resize alike.
    let roiReach (innerRadius : float) =
        sqrt (innerRadius * innerRadius + (fixedProbeLength * 0.5) ** 2.0)

    // Frame of the vertical cross-section cache (§A): the cut plane contains the
    // pin's section azimuth (chart u — a world-horizontal unit fitted server-side
    // on the reference's dip direction, PinSlice.UDir; ONE line per pin, shared by
    // every cell of its matrix row) and world Z (chart v); parallel context planes
    // offset along the horizontal normal.
    let sliceNormalOf (uDir : V3d) = Vec.cross V3d.OOI uDir

    // ONE global horizontal window (m) for every slice cell: N × the coarsest
    // loaded mesh's sample spacing, so even the coarsest mesh shows shape.
    // None until spacings arrive (callers fall back to the pin diameter).
    let sliceWindow (nSamples : float) (spacings : Map<string, float>) : float option =
        let coarsest = spacings |> Map.fold (fun acc _ s -> max acc s) 0.0
        if coarsest <= 0.0 then None else Some (nSamples * coarsest)

    // Context-plane offsets: k each side, spaced a fraction of the window.
    let sliceOffsets (k : int) (spacingFrac : float) (window : float) =
        [| -k .. k |] |> Array.map (fun i -> float i * spacingFrac * window)

    // Disc-clip radius that still covers the full window on every offset plane.
    let sliceClipRadius (window : float) (offsets : float[]) =
        let halfW = window * 0.5
        let wMax = offsets |> Array.fold (fun a w -> max a (abs w)) 0.0
        sqrt (halfW * halfW + wMax * wMax) * 1.0001

    // Chart frame (u, v) at plane offset w → metric world.
    let sliceToWorld (centre : V3d) (uDir : V3d) (w : float) (q : V2d) =
        centre + uDir * q.X + sliceNormalOf uDir * w + V3d.OOI * q.Y

    // Metric world → chart frame of the CENTRE slice (the out-of-plane component
    // drops — i.e. the point is projected onto the centre plane).
    let sliceUV (centre : V3d) (uDir : V3d) (p : V3d) =
        let q = p - centre
        V2d(Vec.dot q uDir, q.Z)

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

    // Selection-circle anchor: the pin centre lifted to the median Z of the
    // contact-ring vertices (the outline the user actually sees on the terrain);
    // falls back to the centre until rings land. World space (metric).
    let selectionCircleCentre (p : ScanPin) =
        match p.ContactRings with
        | RingsReady m ->
            let zs = [| for KeyValue (_, rings) in m do for ring in rings do for v in ring -> v.Z |]
            if zs.Length = 0 then p.Centre
            else
                Array.sortInPlace zs
                V3d(p.Centre.X, p.Centre.Y, zs.[zs.Length / 2])
        | _ -> p.Centre

    // The dashed white selection circle sits just outside the influence ring —
    // the ONE spec constant shared by main 3D + focus single + tiles.
    let selectionCircleRadius (p : ScanPin) = p.InnerRadius * 1.12

    // Screen-constant flag sizing: the pole height is a fixed fraction of the
    // eye→pin distance (render space), clamped in METRIC WORLD to [0.1, 20] m;
    // the gear's flag-scale multiplier scales the fraction AND both bounds.
    // Every flag element (pole, top ring, name, base cross) derives from this
    // one height, so the whole flag resizes together.
    let flagHeightRender (datasetScale : float) (flagScale : float) (eyeDistRender : float) =
        let hWorld = 0.10 * flagScale * eyeDistRender / datasetScale
        renderLength datasetScale (min (20.0 * flagScale) (max (0.1 * flagScale) hWorld))

    // Render-space tip of the flag pole (base = pin centre, along the pin axis) —
    // shared by the 3D pole geometry and the show-overlays 2D name tags.
    let flagTopRender (commonCentroid : V3d) (datasetScale : float) (flagHeight : float) (p : ScanPin) =
        let a = axis p
        let aN = if a.Length > 1e-9 then a.Normalized else V3d.OOI
        renderCentre commonCentroid datasetScale p.Centre + aN * flagHeight

    let correspondence (p : ScanPin) = p.Correspondence

    let withCorrespondence (c : Correspondence) (p : ScanPin) =
        { p with Correspondence = c }

    // Signed error range (m, spanning 0) of one probe: min/max over its ready
    // ROI samples on the moving meshes. None until the probe lands. Takes the
    // ProbeState (not the pin) so callers can project per-field (adaptive perf).
    let probeErrorRange (refMesh : string option) (probe : ProbeState) : (float * float) option =
        match probe with
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
    let inspectRange (refMesh : string option) (probes : seq<ProbeState>) : float * float =
        let cap = 0.5
        let mutable lo = 0.0
        let mutable hi = 0.0
        let mutable any = false
        for probe in probes do
            match probeErrorRange refMesh probe with
            | Some (l, h) ->
                any <- true
                if l < lo then lo <- l
                if h > hi then hi <- h
            | None -> ()
        if not any then (-cap, cap)
        else (max -cap lo, min cap hi)

