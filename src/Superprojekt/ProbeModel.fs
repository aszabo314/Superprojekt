namespace Superprojekt

open System
open Aardvark.Base

// Lengths in metric world-space metres; the signed-distance axis is re-centred
// so 0 = the reference median.
type ProbeDistribution = {
    MeshName  : string
    Count     : int
    Median    : float
    Q1        : float
    Q3        : float
    Std       : float
    Kde       : (float * float)[]
    Samples   : float[]
    // World-space surface position of each sample (V3d), aligned 1:1 with Samples —
    // lets the distribution chart brush a sample back to its 3D surface cell (§T6).
    Positions : V3d[]
    // Approx spatial footprint (m) of a density-grid sample.
    Footprint : float
    // Intrinsic quality [incidence; range; shape] ∈ [0,1].
    Intrinsics : float[]
}

type ProbeSources = {
    DatasetError      : float
    AlgorithmResid    : float
    LocalConditioning : float
}

type ProbeResult = {
    ReferenceMesh : string
    Normal        : V3d
    Length        : float
    // Axial offset (m along Normal from the pin centre) of chart y=0 = the
    // reference median. chart→3D: pin.Centre + Normal·(value + RefOffset);
    // 3D→chart: dot(q − pin.Centre, Normal) − RefOffset.
    RefOffset     : float
    XAuto         : Range1d
    Distributions : ProbeDistribution[]
    Sources       : ProbeSources
}

type ProbeState =
    | ProbeNone
    | ProbeRunning
    | ProbeReady of ProbeResult
    | ProbeError of string

