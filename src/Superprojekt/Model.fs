namespace Superprojekt

open FSharp.Data.Adaptive
open Aardvark.Base
open FSharp.Data.Adaptive // re-open after Aardvark.Base so HashSet resolves to the adaptive one
open Adaptify
open Aardvark.Dom

type RenderingMode =
    | Textured
    | Shaded
    | SlopeColor

type WorkflowStep =
    | Overview
    | Correspondence
    | Inspect

module WorkflowStep =
    let index = function Overview -> 0 | Correspondence -> 1 | Inspect -> 2
    let title = function
        | Overview -> "Overview"
        | Correspondence -> "Correspondence"
        | Inspect -> "Inspect"

// ProjPano = 360° perspective look-around fixed at the mesh's panorama centre;
// Top = the world vertical drop.
type FocusProjection =
    | ProjPano
    | ProjTop

module FocusProjection =
    let label = function ProjPano -> "360°" | ProjTop -> "Top"

// Inspect focus-tile channel: Difference = pair signed-distance heatmap, Displacement
// = load→solved motion glyphs. The central-3D variance map ignores this toggle.
type InspectChannel =
    | ChDifference
    | ChDisplacement

module DatasetScale =
    let forMesh (scales : Map<string, float>) (meshName : string) =
        let i = meshName.IndexOf '/'
        let ds = if i >= 0 then meshName.[.. i - 1] else meshName
        Map.tryFind ds scales |> Option.defaultValue 1.0

    let active (activeDataset : string option) (scales : Map<string, float>) =
        activeDataset |> Option.bind (fun d -> Map.tryFind d scales) |> Option.defaultValue 1.0

// Load/Solved transforms are render-space; server queries work in world space and
// convert through these.
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

type RegistrationState = {
    ReferenceMesh    : string option
}

module RegistrationState =
    let initial = {
        ReferenceMesh  = None
    }

// Before shows every mesh at its immutable LoadTransform; After shows solved
// meshes at their SolvedTransform (reference + unsolved stay at LoadTransform in
// both). Disabled until any SolvedTransform exists.
type RegView =
    | RegBefore
    | RegAfter

// One shared selection record every region reads/writes; linked highlighting is a
// consequence of all panels binding here. hover = peek, click = select/promote.
type HoverTarget =
    | HoverPin   of ScanPinId
    | HoverMesh  of string
    | HoverPoint of ScanPinId * string

[<ModelType>]
type Selection = {
    SelectedPin   : ScanPinId option
    FocusedMesh   : string option
    Hovered       : HoverTarget option
}

module Selection =
    let initial = { SelectedPin = None; FocusedMesh = None; Hovered = None }

// Mesh isolation (solo) is a pure overlay over the per-mesh visibility toggles —
// it never mutates MeshVisible. While isolated, ONLY the isolated mesh is shown
// (the reference included would occlude it in Inspect); with no isolation the
// per-mesh toggles decide. Every shown/clickable consumer (render MeshActive,
// raycasts, ring gating) goes through this one rule.
module MeshVisibility =
    let shown (solo : string option) (visible : Map<string, bool>) (name : string) =
        match solo with
        | Some s -> name = s
        | None -> Map.tryFind name visible |> Option.defaultValue true

// Snapshot captured when a "frame correspondence" (locate) starts, so a single
// back-out restores the camera + solo/visibility to exactly what they were before.
// Plain record (not a ModelType) → a single aval<LocateState option> in the model.
type LocateState = {
    PrevSolo    : string option
    PrevVisible : Map<string, bool>
    PrevCenter  : V3d
    PrevRadius  : float
    PrevPhi     : float
    PrevTheta   : float
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
        // Per-mesh panorama centre (the calibrated-camera origin), absolute world
        // coords — same frame as the centroid. Read from <dataset>/pano-centers.txt
        // on load; the focus pano subtracts the mesh centroid to place its eye.
        PanoCenters      : Map<string, V3d>

        GhostSilhouette      : bool
        GhostOpacity         : float
        ShadingStrength      : float
        SlopeThresholdDeg    : float
        AnchorGhostMode      : bool
        QuickPinRadius       : float

        SceneBounds    : Box3d
        MeshBounds     : Map<string, Box3d>

        ActivePickingLayer : string option

        // Spring-loaded pin-isolation peek (hold to momentarily force pin isolation
        // on in modes where it defaults off — mirrors the reference peek).
        IsolatePeekHeld   : bool
        // Spring-loaded show-overlays modifier (§T8): hold to greyscale the scene
        // except the pin colours, making pin correspondence across views unmistakable.
        ShowOverlaysHeld  : bool

        // LoadTransform is the immutable per-mesh baseline captured at load;
        // SolvedTransform (presence = solved) is written by the correspondence
        // solve; RegView picks which the meshes display. Render-space.
        LoadTransforms        : Map<string, Trafo3d>
        SolvedTransforms      : Map<string, Trafo3d>
        RegView               : RegView
        // Spring-loaded before/after peek: hold to momentarily show the OTHER
        // registration state (purely visual — flips the displayed transform, not
        // the committed RegView or any query).
        RegPeekHeld           : bool
        Registration          : RegistrationState

        Toast                 : string option

        // Per-mesh intrinsic single-mesh error visualization (incidence / range /
        // shape), set from the Overview mesh list. Absent ⇒ HeatOff (textured).
        // Respected in the 3D view and the 2D focus tiles/single alike.
        MeshHeatmap           : Map<string, HeatmapMode>

        // Difference sub-mode for the Inspect focus tiles: false = signed M3C2,
        // true = vertical Δz.
        ExtrinsicZDiff        : bool
        // All-meshes variance map (std of each visible moving mesh's distance),
        // painted on the reference whenever Inspect is the active mode (the central
        // 3D aggregate). SurfaceDistance holds the fetched per-reference-vertex
        // array, keyed by the reference mesh.
        SurfaceDistance       : Map<string, float32[]>
        // Inspect difference channel: per moving mesh, signed distance to the reference
        // (the mesh's served vertex order), painted on the focus tile. Lazily fetched.
        FocusDist             : Map<string, float32[]>

        ScanPins              : ScanPinModel
        Selection             : Selection

        RenderingMode       : RenderingMode
        // Isolated mesh (◐) — an overlay over MeshVisible (see MeshVisibility.shown).
        // Exiting isolation resets every visibility toggle to ON.
        MeshSolo            : string option
        GearPopoverOpen     : bool
        WorkflowStep        : WorkflowStep
        // Inspect focus-tile channel (difference vs displacement).
        InspectChannel      : InspectChannel

        // Pano / Top projection of the WebGL focus single.
        FocusProjection     : FocusProjection

        // Unified armed correspondence editing (§T4): Some (pin, mesh) = the editor
        // is armed for that pair. While armed, the mesh is isolated in the main view,
        // the linked focus is brought onto it, and clicking in EITHER the focus or the
        // 3D view sets the point (ROI-clamped). The mode STAYS armed until the user
        // disarms (one mode, two surfaces). CorrPreview = the live aim ghost shown in
        // both views. None = idle.
        CorrArm             : (ScanPinId * string) option
        CorrPreview         : V3d option
        // Per-sample distribution brushing (§T6): the set of brushed sample global
        // ids (canonical order from ScanPinScene.brushSamples). Written by the chart
        // canvas (via the hidden-input bridge) and by the 3D spatial hover; read by
        // the chart highlight + the 3D brushed-sample markers.
        BrushedSamples      : Set<int>

        // Outline edge-detect threshold (depth Laplacian) + isoline band count over
        // the scene Z range. Tunable from the gear menu; see OutlineEdge /
        // buildOutlineNode. Image-space outlines + isolines are always on.
        OutlineThreshold    : float
        IsolineBands        : float

        // Active "frame correspondence" (locate) backup; Some while a locate is in
        // effect so re-clicking the located matrix cell restores the prior camera +
        // solo state.
        LocateBackup        : LocateState option
    }

// Displayed = the pose a mesh currently shows: at RegAfter a solved mesh uses its
// SolvedTransform, everything else (reference + unsolved) stays at its immutable
// LoadTransform. Every query and scene-graph consumer goes through these, so the
// before/after toggle stays consistent everywhere.
module ModelTransforms =
    let loadRender (model : Model) (mesh : string) =
        Map.tryFind mesh model.LoadTransforms |> Option.defaultValue Trafo3d.Identity

    let displayedRenderAt (view : RegView) (model : Model) (mesh : string) =
        match view, Map.tryFind mesh model.SolvedTransforms with
        | RegAfter, Some t -> t
        | _ -> loadRender model mesh

    let displayedRender (model : Model) (mesh : string) =
        displayedRenderAt model.RegView model mesh

    let private toWorld (model : Model) (mesh : string) (renderT : Trafo3d) =
        RigidTransform.renderToWorld
            (DatasetScale.forMesh model.DatasetScales mesh) model.CommonCentroid renderT

    let displayedWorld (model : Model) (mesh : string) =
        toWorld model mesh (displayedRender model mesh)

    let displayedWorldAt (view : RegView) (model : Model) (mesh : string) =
        toWorld model mesh (displayedRenderAt view model mesh)

    // A mesh's panorama centre in render space (load pose): stored PanoCenters[mesh]
    // (absolute world), else the centroid (= the mesh origin) — then (world − common)·scale.
    let panoCenterRender (model : Model) (mesh : string) =
        let world =
            match Map.tryFind mesh model.PanoCenters with
            | Some w -> w
            | None -> Map.tryFind mesh model.DatasetCentroids |> Option.defaultValue model.CommonCentroid
        ScanPin.renderCentre model.CommonCentroid (DatasetScale.forMesh model.DatasetScales mesh) world

    // The first mesh in the list's panorama centre (render space), the anchor for the
    // coordinate cross + the camera's resting target. Empty list → render origin.
    let firstPanoCenterRender (model : Model) =
        match model.MeshNames |> IndexList.toList with
        | first :: _ -> panoCenterRender model first
        | [] -> V3d.Zero

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
            PanoCenters      = Map.empty
            GhostSilhouette     = true
            GhostOpacity        = 0.12
            ShadingStrength     = 0.15
            SlopeThresholdDeg   = 15.0
            AnchorGhostMode     = true
            QuickPinRadius      = 0.5
            SceneBounds    = Box3d.Invalid
            MeshBounds     = Map.empty
            ActivePickingLayer = None
            IsolatePeekHeld   = false
            ShowOverlaysHeld  = false
            LoadTransforms        = Map.empty
            SolvedTransforms      = Map.empty
            RegView               = RegBefore
            RegPeekHeld           = false
            Registration          = RegistrationState.initial
            Toast                 = None
            ExtrinsicZDiff        = false
            SurfaceDistance       = Map.empty
            FocusDist             = Map.empty
            MeshHeatmap           = Map.empty
            ScanPins              = ScanPinModel.initial
            Selection             = Selection.initial
            RenderingMode       = Textured
            MeshSolo            = None
            GearPopoverOpen     = false
            WorkflowStep        = Overview
            InspectChannel      = ChDifference
            FocusProjection     = ProjTop
            CorrArm             = None
            CorrPreview         = None
            BrushedSamples      = Set.empty
            OutlineThreshold    = 0.004
            IsolineBands        = 700.0
            LocateBackup        = None
        }
