namespace Superprojekt

open System
open Aardvark.Base

// N-mesh M3C2 probe results. Lengths in metric world-space metres; the
// signed-distance axis is re-centred so 0 = the reference median. Raw samples
// stay server-side — the client gets stats + the KDE curve.
type ProbeDistribution = {
    MeshName  : string
    Count     : int
    Median    : float
    Q1        : float
    Q3        : float
    Std       : float
    Kde       : (float * float)[]
    // Raw re-centred samples for the raincloud "rain" (≤300, subsampled server-side).
    Samples   : float[]
    // ROI-averaged intrinsic quality [incidence; range; shape] ∈ [0,1] (inspector B4).
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

