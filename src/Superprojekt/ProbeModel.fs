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
