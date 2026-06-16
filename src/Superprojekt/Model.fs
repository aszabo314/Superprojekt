namespace Superprojekt

open FSharp.Data.Adaptive
open Aardvark.Base
open Adaptify
open Aardvark.Dom
open FSharp.Data.Adaptive

type RenderingMode =
    | Textured
    | Shaded
    | SlopeColor

module DatasetScale =
    let forMesh (scales : Map<string, float>) (meshName : string) =
        let i = meshName.IndexOf '/'
        let ds = if i >= 0 then meshName.[.. i - 1] else meshName
        Map.tryFind ds scales |> Option.defaultValue 1.0

    let active (activeDataset : string option) (scales : Map<string, float>) =
        activeDataset |> Option.bind (fun d -> Map.tryFind d scales) |> Option.defaultValue 1.0

// MeshTransforms stores render-space trafos; world-space rigid transforms
// (server queries, persistence of ICP results) convert through these.
module RigidTransform =
    let worldToRender (scale : float) (cc : V3d) (worldT : Trafo3d) =
        Trafo3d.Scale(1.0 / scale)
        * Trafo3d.Translation(cc)
        * worldT
        * Trafo3d.Translation(-cc)
        * Trafo3d.Scale(scale)

    let renderToWorld (scale : float) (cc : V3d) (renderT : Trafo3d) =
        Trafo3d.Translation(-cc)
        * Trafo3d.Scale(scale)
        * renderT
        * Trafo3d.Scale(1.0 / scale)
        * Trafo3d.Translation(cc)

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

type RegistrationState = {
    Mode             : RegistrationMode
    ReferenceMesh    : string option
    Running          : bool
}

module RegistrationState =
    let initial = {
        Mode           = TraditionalIcp
        ReferenceMesh  = None
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

// A synthetic panorama: a camera pose in metric world-space from which the
// scene is rendered to a cubemap and reprojected cylindrically. No real
// imagery exists for any dataset, so one is generated per dataset on load at
// the scene bbox centre plus a couple of metres up.
type Panorama = {
    Name     : string
    EyeWorld : V3d     // metric world-space eye position
    Yaw      : float   // horizontal look offset (radians); 0 looks along +X
}

// Panorama panel display mode.
//   PanoPhoto  — the captured synthetic image (meshes in reference state)
//   PanoRender — live render of the current meshes from the pose
//   PanoBlend  — slider mix of the two (photo-vs-mesh disagreement detector)
type PanoramaMode =
    | PanoPhoto
    | PanoRender
    | PanoBlend

type LassoDraft =
    { Vertices : V2d[] }

type LassoVolume =
    {
        Planes        : V4d[]
        ScreenPolygon : V2d[]
        CommitVpSize  : V2i
    }

// 3D sectioning / cutaway. One clip-plane subsystem; the four spec modes
// (reference peek / anchor cutaway / iso-plane / focus box) are
// parameterizations of this. Origin/Normal/Axis are metric world-space and
// converted to render space at the pipeline boundary (like pins/cursor).
//   Half-space rule (shared): a mesh fragment is hidden/ghosted where
//   dot(p − origin, normal) > 0 — the producer points Normal at the half to
//   remove (toward the camera for the cutaway, up for iso clip-above).
//   CameraRelative recomputes Normal per frame: the plane contains Axis and
//   its normal = component of (toward-camera) orthogonal to Axis (Axis = 0 →
//   face the camera directly).
type ClipMode =
    | ClipHide          // discard the removed half
    | ClipGhost         // drop the removed half to context/ghost alpha
    | ClipSectionCap    // discard (optional flat cap not rendered yet)

type ClipPlane = {
    Origin         : V3d
    Normal         : V3d
    Axis           : V3d
    Mode           : ClipMode
    CameraRelative : bool
}

module ClipMode =
    let toInt = function ClipHide -> 0 | ClipGhost -> 1 | ClipSectionCap -> 2

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

        // 3D sectioning (0..2 active planes) + spring-loaded reference peek.
        // ClipPlanes holds manually-locked planes (iso-plane lock); the
        // anchor cutaway is derived live from the selected pin + camera.
        ClipPlanes        : ClipPlane list
        ReferencePeekHeld : bool
        CutawayActive     : bool
        CutawayMode       : ClipMode
        // While hovering the violin, also clip the meshes above the live
        // iso-plane (lets the user see into the section). Alt-click locks it.
        ClipAboveIso      : bool
        // Labelled anchor↔reference rulers for the selected pin (HTML overlay).
        RulerActive       : bool

        MeshTransforms        : Map<string, Trafo3d>
        Registration          : RegistrationState
        Retarget              : RetargetState

        // Ensemble registration: uncommitted solve preview, committed history,
        // correspondence-anchor flows (auto-seed review, one-shot 3D pick,
        // patch small-multiples picker).
        PendingReg            : PendingRegistration option
        RegistrationLog       : RegStep list
        // Workflow-panel nav: filters the anchor-review modal to one mesh.
        AnchorReviewFilter    : string option
        // Last solve diagnostics per mesh (workflow panel) — persisted.
        LastSolve             : Map<string, LastSolveEntry>
        AnchorReview          : AnchorReviewState
        AnchorPick            : AnchorPickState option
        PatchPicker           : PatchPickerState option
        Toast                 : string option

        MeshSensorTypes       : Map<string, SensorType>
        MeshDatasetErrors     : Map<string, float>
        MeshAlgorithmResidual : Map<string, float>
        HeatmapMode           : HeatmapMode
        // Mode to restore when HeatDiff auto-reverts on commit/discard.
        HeatmapPrev           : HeatmapMode
        ProvenanceThreshold   : float
        FalloffZoneOnly       : bool

        FusionMode            : bool

        PanoramaOpen          : bool
        Panoramas             : Panorama list
        SelectedPanorama      : int
        PanoramaMode          : PanoramaMode
        PanoramaBlend         : float

        ScanPins              : ScanPinModel
        CardSystem            : CardSystemModel
        HoverProbe            : HoverProbeState option

        // 2D-3D linking of the pin-card violin chart: chart-hover elevation
        // cursor (drives the 3D slicing plane) and mesh-column highlight
        // (hover = transient, sticky = until clicked elsewhere).
        ChartCursor           : ChartCursor option
        ChartHoverMesh        : string option
        ChartStickyMesh       : string option

        // UI→3D hover highlight: a pin row in the registration workflow card,
        // and an individual (pin × mesh) candidate row in the anchor-review
        // dialog. None = nothing hovered.
        WorkflowPinHover      : ScanPinId option
        ReviewAnchorHover     : (ScanPinId * string) option

        RenderingMode       : RenderingMode
        MeshSolo            : MeshSoloState
        LassoCardPos        : V2d option
        GearPopoverOpen     : bool

        // User-study mode: None = Full app; Some shell = study pages /
        // running session (chrome replaced, features gated).
        Study               : StudyShell option
        StudiesAvailable    : string list

        // Registration workflow panel + registration card open state
        // (model-side so navigation actions can open them; session-only).
        WorkflowPanelOpen   : bool
        RegistrationCardOpen : bool
    }

// Committed vs effective (committed ∘ pending-delta) transforms, in render and
// world space. Every server query and scene-graph consumer goes through these
// so the preview pose is consistent everywhere.
module ModelTransforms =
    let committedRender (model : Model) (mesh : string) =
        Map.tryFind mesh model.MeshTransforms |> Option.defaultValue Trafo3d.Identity

    let effectiveRender (model : Model) (mesh : string) =
        let c = committedRender model mesh
        match PendingRegistration.delta mesh model.PendingReg with
        | Some d -> RegLog.effective c d
        | None -> c

    let private toWorld (model : Model) (mesh : string) (renderT : Trafo3d) =
        RigidTransform.renderToWorld
            (DatasetScale.forMesh model.DatasetScales mesh) model.CommonCentroid renderT

    let committedWorld (model : Model) (mesh : string) =
        toWorld model mesh (committedRender model mesh)

    let effectiveWorld (model : Model) (mesh : string) =
        toWorld model mesh (effectiveRender model mesh)

    // World-space delta a commit (before → after, render space) applies to a
    // mesh — used to re-base correspondence anchors so they stay on the
    // surface across commit and rollback.
    let worldDelta (model : Model) (mesh : string) (before : Trafo3d) (after : Trafo3d) =
        (toWorld model mesh before).Inverse * toWorld model mesh after

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
            ClipPlanes        = []
            ReferencePeekHeld = false
            CutawayActive     = false
            CutawayMode       = ClipGhost
            ClipAboveIso      = false
            RulerActive       = false
            MeshTransforms        = Map.empty
            Registration          = RegistrationState.initial
            Retarget              = RetargetState.initial
            PendingReg            = None
            RegistrationLog       = []
            AnchorReviewFilter    = None
            LastSolve             = Map.empty
            AnchorReview          = AnchorReviewIdle
            AnchorPick            = None
            PatchPicker           = None
            Toast                 = None
            MeshSensorTypes       = Map.empty
            MeshDatasetErrors     = Map.empty
            MeshAlgorithmResidual = Map.empty
            HeatmapMode           = HeatOff
            HeatmapPrev           = HeatOff
            ProvenanceThreshold   = 0.01
            FalloffZoneOnly       = false
            FusionMode            = false
            PanoramaOpen          = false
            Panoramas             = []
            SelectedPanorama      = 0
            PanoramaMode          = PanoRender
            PanoramaBlend         = 0.5
            ScanPins              = ScanPinModel.initial
            CardSystem            = CardSystemModel.initial
            HoverProbe            = None
            ChartCursor           = None
            ChartHoverMesh        = None
            ChartStickyMesh       = None
            WorkflowPinHover      = None
            ReviewAnchorHover     = None
            RenderingMode       = Textured
            MeshSolo            = NoSolo
            LassoCardPos        = None
            GearPopoverOpen     = false
            Study               = None
            StudiesAvailable    = []
            WorkflowPanelOpen   = false
            RegistrationCardOpen = false
        }
