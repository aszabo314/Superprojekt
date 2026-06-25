namespace Superprojekt

open FSharp.Data.Adaptive
open Aardvark.Base
open FSharp.Data.Adaptive
open Adaptify
open Aardvark.Dom

type RenderingMode =
    | Textured
    | Shaded
    | SlopeColor

// Left-rail workflow spine (spec §2 / v3 §B). Six steps: the old coarse step is
// split into Manual move (translate-drag) then Correspondences (the manager +
// constellation + 2D handles). One step is expanded at a time.
type WorkflowStep =
    | StepReference
    | StepManualMove
    | StepCorrespondences
    | StepFine
    | StepInspect
    | StepCommit

module WorkflowStep =
    let all = [ StepReference; StepManualMove; StepCorrespondences; StepFine; StepInspect; StepCommit ]
    let index = function
        | StepReference -> 0 | StepManualMove -> 1 | StepCorrespondences -> 2
        | StepFine -> 3 | StepInspect -> 4 | StepCommit -> 5
    let title = function
        | StepReference -> "Reference"
        | StepManualMove -> "Manual move"
        | StepCorrespondences -> "Correspondences"
        | StepFine -> "Fine ICP"
        | StepInspect -> "Inspect"
        | StepCommit -> "Commit"
    // One-word dock-header mode label (v3 §A mode indication).
    let mode = function
        | StepReference -> "Reference"
        | StepManualMove -> "Manual move"
        | StepCorrespondences -> "Correspondences"
        | StepFine -> "Fine ICP"
        | StepInspect -> "Inspect"
        | StepCommit -> "Commit"

// Focus-panel projection (spec v3 §A/§B): drives the large single (GPU) and the
// canvas small multiples together. Pano = cylindrical from a mesh origin; the
// three orthos are world-axis drops.
type FocusProjection =
    | ProjPano
    | ProjTop
    | ProjFront
    | ProjSide

module FocusProjection =
    let label = function ProjPano -> "Pano" | ProjTop -> "Top" | ProjFront -> "Front" | ProjSide -> "Side"
    let toInt = function ProjPano -> 0 | ProjTop -> 1 | ProjFront -> 2 | ProjSide -> 3

// One mesh's co-oriented preview for the canvas small multiples (server-computed,
// spec v3 §F). Verts2d is flattened [u0;v0;u1;v1;…] per emitted vertex; Tris index
// into it; Scalar is the per-vertex display value; Lo/Hi the robust scalar domain.
type FocusPreview = {
    Verts2d : float[]
    Tris    : int[]
    Scalar  : float[]
    Lo      : float
    Hi      : float
}

// Movement layer (spec §7, preview only): visualises the applied rigid motion
// over each pin ROI. Off / before→after displacement arrows / warped lattice.
type MovementMode =
    | MovementOff
    | MovementGlyphs
    | MovementGrid

module DatasetScale =
    let forMesh (scales : Map<string, float>) (meshName : string) =
        let i = meshName.IndexOf '/'
        let ds = if i >= 0 then meshName.[.. i - 1] else meshName
        Map.tryFind ds scales |> Option.defaultValue 1.0

    let active (activeDataset : string option) (scales : Map<string, float>) =
        activeDataset |> Option.bind (fun d -> Map.tryFind d scales) |> Option.defaultValue 1.0

// MeshTransforms are render-space; world-space rigid transforms (server
// queries, ICP persistence) convert through these.
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

    // World-space delta a pending render `delta` applies at committed render
    // pose `committed`. Used by scene/overlay nodes following anchors under a
    // solve preview.
    let worldDeltaOf (scale : float) (cc : V3d) (committed : Trafo3d) (delta : Trafo3d) =
        (renderToWorld scale cc committed).Inverse
        * renderToWorld scale cc (RegLog.effective committed delta)

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

        GhostSilhouette      : bool
        GhostOpacity         : float
        ShadingStrength      : float
        SlopeThresholdDeg    : float
        AnchorGhostMode      : bool
        QuickPinRadius       : float

        SceneBounds    : Box3d
        MeshBounds     : Map<string, Box3d>

        ActivePickingLayer : string option

        // Spring-loaded reference peek (hold to show only the reference mesh).
        ReferencePeekHeld : bool

        MeshTransforms        : Map<string, Trafo3d>
        Registration          : RegistrationState

        // Ensemble registration: uncommitted solve preview + correspondence-
        // anchor flows. A single commit applies into MeshTransforms; no history.
        PendingReg            : PendingRegistration option
        // Last solve diagnostics per mesh (workflow panel) — persisted.
        LastSolve             : Map<string, LastSolveEntry>
        Toast                 : string option

        MeshSensorTypes       : Map<string, SensorType>
        HeatmapMode           : HeatmapMode

        // A2: per-mesh signed-distance surface colour map — the soloed mesh is
        // painted with its per-vertex signed M3C2 distance to the reference.
        // SurfaceDistance holds the fetched per-vertex arrays (aligned with the
        // served geometry), keyed by mesh.
        SurfaceDistOn         : bool
        // §6 extrinsic mode: false = signed M3C2, true = vertical Δz.
        ExtrinsicZDiff        : bool
        // §6 all-meshes variance map: per-reference-vertex disagreement (std of
        // each visible moving mesh's distance), painted on the reference.
        // Mutually exclusive with the single-mesh extrinsic map above.
        VarianceOn            : bool
        SurfaceDistance       : Map<string, float32[]>

        ScanPins              : ScanPinModel

        // Bottom-dock pin inspector: the active moving-mesh row (B4 intrinsic
        // bars + the extrinsic surface-map target). None = topmost row.
        InspectorMesh         : string option

        // UI→3D hover highlight (None = nothing hovered): a pin row → its glyph.
        WorkflowPinHover      : ScanPinId option
        // Correspondence-manager row hover (v3 §G brushing): (pinId-as-string, mesh)
        // → ghost-isolate that mesh in 3D + brighten its glyph + pulse its 2D handle.
        CorrRowHover          : (string * string) option

        RenderingMode       : RenderingMode
        MeshSolo            : MeshSoloState
        GearPopoverOpen     : bool

        // Left workflow rail: which step is expanded (spec §2).
        WorkflowStep        : WorkflowStep

        // Right focus panel (spec v3): persistent GPU large single + canvas small
        // multiples — always present (no open/close). FocusMesh is the mesh shown
        // large + dragged in Manual move + edited in Correspondences. FocusMaps
        // caches one server preview per visible mesh for the multiples.
        FocusProjection     : FocusProjection
        FocusMesh           : string option
        FocusMaps           : Map<string, FocusPreview>
        // Peek-reference modifier (v3 §E): hold → large single re-renders as the
        // reference mesh textured, in the current panel's own frame; juxtaposition.
        FocusPeekReference  : bool

        // §9 pin-focus modifier: ghost everything outside the focused pin's ROI.
        PinFocusMode        : bool

        // §7 movement layer (preview only).
        MovementLayer       : MovementMode

        // §10 per-mesh image-space outlines (default off — gated overlay).
        OutlineMode         : bool
    }

// Committed vs effective (committed ∘ pending-delta) transforms, render and
// world space. Every query and scene-graph consumer goes through these so the
// preview pose is consistent everywhere.
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

    // World-space delta a commit (before → after, render space) applies —
    // re-bases correspondence anchors so they stay on the surface across
    // commit/rollback.
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
            MenuOpen       = true
            SavedMenuOpen  = None
            DebugLog       = IndexList.empty
            Datasets         = []
            ActiveDataset    = None
            DatasetScales    = Map.ofList ["SETSM_glacier", 0.01]
            DatasetCentroids = Map.empty
            GhostSilhouette     = true
            GhostOpacity        = 0.12
            ShadingStrength     = 0.15
            SlopeThresholdDeg   = 15.0
            AnchorGhostMode     = true
            QuickPinRadius      = 0.125
            SceneBounds    = Box3d.Invalid
            MeshBounds     = Map.empty
            ActivePickingLayer = None
            ReferencePeekHeld = false
            MeshTransforms        = Map.empty
            Registration          = RegistrationState.initial
            PendingReg            = None
            LastSolve             = Map.empty
            Toast                 = None
            MeshSensorTypes       = Map.empty
            SurfaceDistOn         = false
            ExtrinsicZDiff        = false
            VarianceOn            = false
            SurfaceDistance       = Map.empty
            HeatmapMode           = HeatOff
            ScanPins              = ScanPinModel.initial
            InspectorMesh         = None
            WorkflowPinHover      = None
            CorrRowHover          = None
            RenderingMode       = Textured
            MeshSolo            = NoSolo
            GearPopoverOpen     = false
            WorkflowStep        = StepReference
            FocusProjection     = ProjPano
            FocusMesh           = None
            FocusMaps           = Map.empty
            FocusPeekReference  = false
            PinFocusMode        = false
            MovementLayer       = MovementOff
            OutlineMode         = false
        }
