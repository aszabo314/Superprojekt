namespace Superprojekt

open FSharp.Data.Adaptive
open Aardvark.Base
open Adaptify
open Aardvark.Dom
open FSharp.Data.Adaptive

type ReferenceAxisMode =
    | AlongWorldZ
    | AlongCameraView

/// V6 §D.4 — per-signal toggle for the dual-signal Explore mode.
/// `Threshold` is the sensitivity slider; meaning differs per signal:
/// for feature confidence it's the minimum curvature×steepness score,
/// for disagreement it's the minimum cross-mesh depth stddev in metres.
type SignalState = {
    Enabled   : bool
    Threshold : float
    Color     : C4f
}

/// V6 §D.4 — composition mode when both signals are active.
type MixMode =
    | SideBySide   // alternating stripe pattern in two hues
    | Blended      // arithmetic mean of both signals
    | Alternating  // time-cycled flicker between the two

/// V6 §D.2 — ghost silhouette detail level. OutlineOnly is the V5
/// behaviour; PlusCurvature blends a faint curvature colour gradient
/// onto the silhouette; PlusTerrainFeatures additionally rasterises
/// ridge/valley lines. Terrain features are deferred until Phase 9
/// polish — selectable but the same as PlusCurvature for now.
type GhostDetail =
    | OutlineOnly
    | PlusCurvature
    | PlusTerrainFeatures

type RenderingMode =
    | Textured
    | Shaded
    | WhiteSurface

type MeshSoloState =
    | NoSolo
    | Solo of name:string * restore:Map<string,bool>

/// V6 §D.9 — sensor type drives the default dataset-error value when
/// no per-mesh override is supplied. Distance-dependent sensors fall
/// back to the static defaults in `Provenance.defaultDatasetError`.
type SensorType =
    | RoverStereo
    | Satellite
    | Photogrammetry
    | LiDAR
    | UnknownSensor

/// V6 §D.8 — registration solver. RegistrationMode picks the solve
/// strategy; LastResiduals + ConvergenceLog feed the residuals
/// histogram and iteration log on the panel.
type RegistrationMode =
    | TraditionalIcp
    | RegionRestrictedIcp
    | PointPairPlusRefinement

type RegistrationIteration = { Iter : int; Rms : float }

type RegistrationState = {
    Mode             : RegistrationMode
    ReferenceMesh    : string option
    LastResiduals    : float[]                  // per-correspondence residuals from the most recent solve
    ConvergenceLog   : RegistrationIteration[]  // (iter, residual-rms) per solve iteration
    Running          : bool
}

module RegistrationState =
    let initial = {
        Mode           = TraditionalIcp
        ReferenceMesh  = None
        LastResiduals  = [||]
        ConvergenceLog = [||]
        Running        = false
    }

/// V6 §D.4 — restructured Explore card state. `Enabled` is the master
/// toggle that shows/hides the tuning card. Each signal carries its
/// own enable + threshold + colour; `MixMode` chooses how to composite
/// when both are on.
type ExploreMode =
    {
        Enabled            : bool
        FeatureConfidence  : SignalState
        Disagreement       : SignalState
        MixMode            : MixMode
        HighlightAlpha     : float
    }

/// V6 §D.3 — in-progress lasso polygon. Wrapped in a record so Adaptify
/// treats it as an opaque value (a plain `cval`) instead of deep-tracking
/// the `V2d list` collection it once was.
type LassoDraft =
    { Vertices : V2d[] }

/// V6 §D.3 — committed lasso clip. `Planes` are world-space half-planes
/// (V4d = normal.xyz, offset); a fragment is outside the lasso volume if
/// any plane's signed distance is positive. The volume is a cone with
/// apex at the camera position captured at commit time, so it stays
/// world-anchored when the camera moves afterwards.
type LassoVolume =
    {
        Planes        : V4d[]
        ScreenPolygon : V2d[]      // px in commit viewport — kept for display only
        CommitVpSize  : V2i        // viewport size at commit time
    }

/// V6 §D.9 — helpers for the three error sources (dataset / algorithm /
/// conditioning) at a given point. Everything here is in **world space**
/// metres so the values are comparable across signals.
module Provenance =
    /// Per-sensor default dataset-error value in metres. RoverStereo
    /// is distance-dependent in the spec — we approximate with a flat
    /// 0.5 m default for prototype purposes; a real implementation
    /// would consult per-vertex camera distance.
    let defaultDatasetError (sensor : SensorType) =
        match sensor with
        | RoverStereo     -> 0.5
        | Satellite       -> 0.25
        | Photogrammetry  -> 0.008
        | LiDAR           -> 0.0005
        | UnknownSensor   -> 0.01

    let datasetError (overrides : Map<string, float>) (sensors : Map<string, SensorType>) (mesh : string) =
        match Map.tryFind mesh overrides with
        | Some v -> v
        | None ->
            Map.tryFind mesh sensors
            |> Option.defaultValue UnknownSensor
            |> defaultDatasetError

    /// Local conditioning at point `p` based on anchor distribution.
    /// Density × angular diversity heuristic from §D.9. `anchors` is
    /// a list of (centre, sigma) world-space pairs; only anchors with
    /// `weight(p) > 0.05` contribute.
    let localConditioning (p : V3d) (anchors : (V3d * float)[]) =
        if anchors.Length < 2 then 1e6  // ill-conditioned by default
        else
            let weighted =
                anchors
                |> Array.choose (fun (c, sigma) ->
                    if sigma < 1e-6 then None
                    else
                        let d2 = (p - c).LengthSquared
                        let w = exp (-d2 / (2.0 * sigma * sigma))
                        if w > 0.05 then Some (c, w) else None)
            if weighted.Length < 2 then 1e6
            else
                let density = weighted |> Array.sumBy snd
                // Angular diversity: 1 - max |cos angle| over (anchor - p) directions.
                let dirs =
                    weighted |> Array.map (fun (c, _) ->
                        let v = c - p
                        if v.Length > 1e-9 then v / v.Length else V3d.OOI)
                let mutable maxCos = 0.0
                for i in 0 .. dirs.Length - 1 do
                    for j in i + 1 .. dirs.Length - 1 do
                        let c = abs (Vec.dot dirs.[i] dirs.[j])
                        if c > maxCos then maxCos <- c
                let angDiv = 1.0 - maxCos
                let cond = 1.0 / (density * angDiv + 1e-3)
                min cond 1e6

    /// Returns (dataset, algorithm, conditioning) all in metres.
    let sourcesAt
            (mesh : string)
            (datasetOverrides : Map<string, float>)
            (sensors : Map<string, SensorType>)
            (algoResiduals : Map<string, float>)
            (worldPoint : V3d)
            (anchors : (V3d * float)[]) =
        let dErr = datasetError datasetOverrides sensors mesh
        let aErr = Map.tryFind mesh algoResiduals |> Option.defaultValue 0.0
        let cErr = localConditioning worldPoint anchors
        dErr, aErr, cErr

    /// Pick the dominant source (0 = dataset, 1 = algorithm, 2 =
    /// conditioning). Conditioning is scaled down because its raw
    /// values are unitless and would otherwise always dominate.
    let dominantSource (d : float) (a : float) (c : float) =
        let cScaled = c * 0.01   // conditioning is unitless; rough scale to match metres
        if d >= a && d >= cScaled then 0
        elif a >= cScaled then 1
        else 2

module ExploreMode =
    let initial =
        {
            Enabled = false
            FeatureConfidence = {
                Enabled   = true
                Threshold = 0.3
                Color     = C4f(1.0f, 0.55f, 0.10f, 1.0f)  // warm orange — V5 default
            }
            Disagreement = {
                Enabled   = true
                Threshold = 0.05
                Color     = C4f(0.15f, 0.55f, 1.0f, 1.0f)  // cool blue
            }
            MixMode        = Blended
            HighlightAlpha = 0.9
        }

[<ModelType>]
type Model =
    {
        Camera         : OrbitState
        MeshOrder      : HashMap<string,int>
        MeshNames      : IndexList<string>
        MeshVisible    : Map<string, bool>
        MeshesLoaded   : HashSet<string>
        CommonCentroid : V3d
        MenuOpen       : bool
        SavedMenuOpen  : bool option

        [<CheapEquals>]
        Filtered       : HashMap<string, int[]>
        FilterCenter   : option<V3d>
        DebugLog       : IndexList<string>

        Datasets         : string list
        ActiveDataset    : string option
        DatasetScales    : Map<string, float>
        DatasetCentroids : Map<string, V3d>

        FullscreenOn         : bool
        DifferenceRendering  : bool
        MinDifferenceDepth   : float
        MaxDifferenceDepth   : float
        GhostSilhouette      : bool
        GhostDetail          : GhostDetail
        GhostOpacity         : float

        ClipActive     : bool
        ClipBox        : Box3d   // active clip range (render-space uniforms computed from this)
        ClipBounds     : Box3d   // world-space union of all dataset bboxes; Box3d.Invalid until loaded
        MeshBounds     : Map<string, Box3d>   // per-mesh world-space bbox (input to mesh-wheel ray test)

        // V6 §D.1 mesh-wheel
        ActivePickingLayer : string option

        // V6 §D.3 polygonal lasso
        LassoDrawing : LassoDraft option   // in-progress polygon vertices (viewport px)
        LassoVolume  : LassoVolume option

        // V6 §D.8 — per-mesh render-space rigid transform applied on top
        // of the dataset-scale + centroid-offset pipeline. Map.empty means
        // every mesh stays at the reference pose.
        MeshTransforms        : Map<string, Trafo3d>
        Registration          : RegistrationState

        // V6 §D.9 — per-mesh provenance state. SensorTypes / DatasetErrors
        // feed the dataset-error component (default per-sensor table with
        // user override); AlgorithmResidual is the post-ICP per-mesh RMS;
        // LocalConditioning is computed from the live anchor distribution.
        MeshSensorTypes       : Map<string, SensorType>
        MeshDatasetErrors     : Map<string, float>       // user override in metres; None ⇒ sensor default
        MeshAlgorithmResidual : Map<string, float>       // post-solve RMS per mesh, metres
        ProvenanceHeatmap     : bool                     // global heatmap toggle
        ProvenanceThreshold   : float                    // minimum total error in metres to paint
        FalloffZoneOnly       : bool                     // clip metrics + heatmap to anchor falloff zones

        // V6 §D.10 — Fusion mesh mode. When on, the composition pass
        // picks per-pixel the visible mesh with the lowest combined
        // error (dataset + algorithm + conditioning) instead of the
        // front-most one. Off by default.
        FusionMode            : bool

        ScanPins              : ScanPinModel
        ReferenceAxis         : ReferenceAxisMode
        Explore               : ExploreMode
        ColorMode             : bool
        CardSystem            : CardSystemModel

        RenderingMode       : RenderingMode
        MeshSolo            : MeshSoloState
        ExploreCardPos      : V2d option
        GearPopoverOpen     : bool
    }

module Model =
    let initial =
        {
            Camera         = OrbitState.create V3d.Zero 1.0 0.3 3.0 Button.Left Button.Middle
            MeshOrder      = HashMap.empty
            MeshNames      = IndexList.empty
            MeshesLoaded   = HashSet.empty
            MeshVisible    = Map.empty
            CommonCentroid = V3d.Zero
            MenuOpen       = false
            SavedMenuOpen  = None
            Filtered       = HashMap.empty
            FilterCenter   = None
            DebugLog       = IndexList.empty
            Datasets         = []
            ActiveDataset    = None
            DatasetScales    = Map.ofList ["SETSM_glacier", 0.01]
            DatasetCentroids = Map.empty

            FullscreenOn        = false
            DifferenceRendering = false
            MinDifferenceDepth  = 3.0
            MaxDifferenceDepth  = 10.0
            GhostSilhouette     = false
            GhostDetail         = OutlineOnly
            GhostOpacity        = 0.1
            ClipActive     = false
            ClipBox        = Box3d(V3d(-1e10), V3d(1e10))
            ClipBounds     = Box3d.Invalid
            MeshBounds     = Map.empty

            ActivePickingLayer = None

            LassoDrawing = None
            LassoVolume  = None

            MeshTransforms        = Map.empty
            Registration          = RegistrationState.initial

            MeshSensorTypes       = Map.empty
            MeshDatasetErrors     = Map.empty
            MeshAlgorithmResidual = Map.empty
            ProvenanceHeatmap     = false
            ProvenanceThreshold   = 0.01    // 1 cm: paint anything above
            FalloffZoneOnly       = false

            FusionMode            = false

            ScanPins              = ScanPinModel.initial
            ReferenceAxis         = AlongWorldZ
            Explore               = ExploreMode.initial
            ColorMode             = false
            CardSystem            = CardSystemModel.initial

            RenderingMode       = Textured
            MeshSolo            = NoSolo
            ExploreCardPos      = None
            GearPopoverOpen     = false
        }
