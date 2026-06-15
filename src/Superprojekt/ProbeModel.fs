namespace Superprojekt

open System
open Aardvark.Base

// N-mesh M3C2 probe results. All lengths in
// metric world-space metres; the signed-distance axis is re-centred so 0 = the
// reference mesh's median. Raw axis samples stay server-side — the client only
// receives stats + the KDE curve evaluated over XFit.
type ProbeDistribution = {
    MeshName  : string
    Count     : int
    Median    : float
    Q1        : float
    Q3        : float
    Std       : float
    Kde       : (float * float)[]
    Bandwidth : float
    // Raw re-centred samples for the small-N strip (empty for large N).
    Samples   : float[]
}

type ProbeSourcesPerMesh = {
    MeshName     : string
    IqrMetres    : float
    MedianOffset : float
    PointCount   : int
}

type ProbeSources = {
    DatasetError      : float
    AlgorithmResid    : float
    LocalConditioning : float
    PerMesh           : ProbeSourcesPerMesh[]
}

type ProbeResult = {
    ReferenceMesh : string
    Normal        : V3d
    Planarity     : float
    Planar        : bool
    Length        : float
    AutoLength    : float
    XAuto         : Range1d
    XFit          : Range1d
    Distributions : ProbeDistribution[]
    Sources       : ProbeSources
    ComputedAt    : DateTime
}

type ProbeState =
    | ProbeNone
    | ProbeRunning
    | ProbeReady of ProbeResult
    | ProbeError of string

type ProbeXRange =
    | ProbeXAuto
    | ProbeXHalf
    | ProbeXTwo
    | ProbeXTen
    | ProbeXFit

// Transient Ctrl-click probe: one global slot, never
// cached, cleared on Escape / click elsewhere / timeout.
type HoverProbeState = {
    ScreenPos : V2d
    Anchor    : V3d
    Probe     : ProbeState
}

module ProbeXRange =
    let window (r : ProbeResult) = function
        | ProbeXAuto -> r.XAuto
        | ProbeXHalf -> Range1d(-0.5, 0.5)
        | ProbeXTwo  -> Range1d(-2.0, 2.0)
        | ProbeXTen  -> Range1d(-10.0, 10.0)
        | ProbeXFit  -> r.XFit

    let label = function
        | ProbeXAuto -> "auto"
        | ProbeXHalf -> "±0.5"
        | ProbeXTwo  -> "±2"
        | ProbeXTen  -> "±10"
        | ProbeXFit  -> "fit"

    let tag = function
        | ProbeXAuto -> "auto"
        | ProbeXHalf -> "half"
        | ProbeXTwo  -> "two"
        | ProbeXTen  -> "ten"
        | ProbeXFit  -> "fit"

    let ofTag = function
        | "half" -> ProbeXHalf
        | "two"  -> ProbeXTwo
        | "ten"  -> ProbeXTen
        | "fit"  -> ProbeXFit
        | _      -> ProbeXAuto

    let all = [ ProbeXAuto; ProbeXHalf; ProbeXTwo; ProbeXTen; ProbeXFit ]
