namespace Superprojekt

open FSharp.Data.Adaptive
open Aardvark.Base
open Adaptify
open Aardvark.Dom
open FSharp.Data.Adaptive

type ReferenceAxisMode =
    | AlongWorldZ
    | AlongCameraView

type SignalState = {
    Enabled   : bool
    Threshold : float
    Color     : C4f
}

type MixMode =
    | SideBySide
    | Blended
    | Alternating

type RenderingMode =
    | Textured
    | Shaded
    | SlopeColor

type MeshSoloState =
    | NoSolo
    | Solo of name:string * restore:Map<string,bool>

type SensorType =
    | RoverStereo
    | Satellite
    | Photogrammetry
    | LiDAR
    | UnknownSensor

type RegistrationMode =
    | TraditionalIcp
    | RegionRestrictedIcp
    | PointPairPlusRefinement

type RegistrationState = {
    Mode             : RegistrationMode
    ReferenceMesh    : string option
    LastResiduals    : float[]
    Running          : bool
}

module RegistrationState =
    let initial = {
        Mode           = TraditionalIcp
        ReferenceMesh  = None
        LastResiduals  = [||]
        Running        = false
    }

type RetargetDecision =
    | RetargetUndecided
    | RetargetAccept
    | RetargetReject

type RetargetCandidate = {
    PinId              : ScanPinId
    OriginalCentre     : V3d
    OriginalHostMesh   : string option
    FalloffRadius      : float
    ProjectedCentre    : V3d
    ProjectionDistance : float
    TargetMesh         : string
    Decision           : RetargetDecision
}

type RetargetState =
    | RetargetIdle
    | RetargetProjecting of targetMesh:string
    | RetargetReviewing  of candidates:RetargetCandidate[]

module RetargetState =
    let initial = RetargetIdle

type ExploreMode =
    {
        Enabled            : bool
        FeatureConfidence  : SignalState
        Disagreement       : SignalState
        MixMode            : MixMode
        HighlightAlpha     : float
    }

type LassoDraft =
    { Vertices : V2d[] }

type LassoVolume =
    {
        Planes        : V4d[]
        ScreenPolygon : V2d[]
        CommitVpSize  : V2i
    }

module Provenance =
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

    let localConditioning (p : V3d) (anchors : (V3d * float)[]) =
        if anchors.Length < 2 then 1e6
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

    let dominantSource (d : float) (a : float) (c : float) =
        let cScaled = c * 0.01
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
                Color     = C4f(1.0f, 0.55f, 0.10f, 1.0f)
            }
            Disagreement = {
                Enabled   = true
                Threshold = 0.05
                Color     = C4f(0.15f, 0.55f, 1.0f, 1.0f)
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

        DebugLog       : IndexList<string>

        Datasets         : string list
        ActiveDataset    : string option
        DatasetScales    : Map<string, float>
        DatasetCentroids : Map<string, V3d>

        FullscreenOn         : bool
        GhostSilhouette      : bool
        GhostOpacity         : float
        ShadingStrength      : float
        SlopeThresholdDeg    : float
        AnchorGhostMode      : bool

        SceneBounds    : Box3d
        MeshBounds     : Map<string, Box3d>

        ActivePickingLayer : string option

        LassoDrawing : LassoDraft option
        LassoVolume  : LassoVolume option
        LassoEnabled : bool

        MeshTransforms        : Map<string, Trafo3d>
        Registration          : RegistrationState
        Retarget              : RetargetState

        MeshSensorTypes       : Map<string, SensorType>
        MeshDatasetErrors     : Map<string, float>
        MeshAlgorithmResidual : Map<string, float>
        ProvenanceHeatmap     : bool
        ProvenanceThreshold   : float
        FalloffZoneOnly       : bool

        FusionMode            : bool

        ScanPins              : ScanPinModel
        ReferenceAxis         : ReferenceAxisMode
        Explore               : ExploreMode
        ColorMode             : bool
        CardSystem            : CardSystemModel

        RenderingMode       : RenderingMode
        MeshSolo            : MeshSoloState
        ExploreCardPos      : V2d option
        LassoCardPos        : V2d option
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
            DebugLog       = IndexList.empty
            Datasets         = []
            ActiveDataset    = None
            DatasetScales    = Map.ofList ["SETSM_glacier", 0.01]
            DatasetCentroids = Map.empty
            FullscreenOn        = false
            GhostSilhouette     = true
            GhostOpacity        = 0.12
            ShadingStrength     = 0.15
            SlopeThresholdDeg   = 15.0
            AnchorGhostMode     = true
            SceneBounds    = Box3d.Invalid
            MeshBounds     = Map.empty
            ActivePickingLayer = None
            LassoDrawing = None
            LassoVolume  = None
            LassoEnabled = true
            MeshTransforms        = Map.empty
            Registration          = RegistrationState.initial
            Retarget              = RetargetState.initial
            MeshSensorTypes       = Map.empty
            MeshDatasetErrors     = Map.empty
            MeshAlgorithmResidual = Map.empty
            ProvenanceHeatmap     = false
            ProvenanceThreshold   = 0.01
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
            LassoCardPos        = None
            GearPopoverOpen     = false
        }
