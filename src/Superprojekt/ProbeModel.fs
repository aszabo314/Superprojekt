namespace Superprojekt

open System
open Aardvark.Base

// Lengths in metric world-space metres; the signed-distance axis is re-centred
// so 0 = the reference median.
type ProbeDistribution = {
    MeshName  : string
    Count     : int
    Median    : float
    Std       : float
    Samples   : float[]
    // World-space surface position of each sample (V3d), aligned 1:1 with Samples —
    // lets the distribution chart brush a sample back to its 3D surface cell (§T6).
    Positions : V3d[]
}

type ProbeResult = {
    ReferenceMesh : string
    Normal        : V3d
    Distributions : ProbeDistribution[]
}

type ProbeState =
    | ProbeNone
    | ProbeRunning
    | ProbeReady of ProbeResult
    | ProbeError of string

// Vertical cross-section cache of one pin at one registration pose (feeds the
// show-overlays hold: label profile chart + 3D centre-slice lines). Per mesh,
// per parallel plane, mesh∩plane polylines in the slice's 2D chart frame —
// u along the fixed horizontal slice direction, v along world Z, metres
// relative to the pin centre, clipped to the probe sphere. Precomputed
// server-side so the overlay toggle stays instant.
type SliceMesh = {
    MeshName : string
    // Planes.[k] = the polylines of PinSlice.Offsets.[k].
    Planes   : V2d[][][]
}

type PinSlice = {
    // Probe-sphere radius (m) the slices were clipped to (pin InnerRadius at fetch).
    Extent  : float
    // Signed plane offsets (m) along the slice normal; 0 = the centre slice.
    Offsets : float[]
    Meshes  : SliceMesh[]
}

// Slice/SliceOther mirror Probe/ProbeOther: committed displayed pose / the
// opposite Before/After pose (fetched only once a solve exists).
type SliceState =
    | SliceNone
    | SliceRunning
    | SliceReady of PinSlice
    | SliceError of string
