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
        | Correspondence -> "Register"
        | Inspect -> "Inspect"

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

// Before shows every mesh at its immutable LoadTransform; After shows solved
// meshes at their SolvedTransform (reference + unsolved stay at LoadTransform in
// both). Disabled until any SolvedTransform exists.
type RegView =
    | RegBefore
    | RegAfter

module RegView =
    let other = function RegBefore -> RegAfter | RegAfter -> RegBefore

// One shared selection record every region reads/writes; linked highlighting is a
// consequence of all panels binding here. hover = peek, click = select/promote.
type HoverTarget =
    | HoverPin   of ScanPinId
    | HoverMesh  of string
    | HoverPoint of ScanPinId * string

// THE one active selection: a mesh (matrix column), a pin (matrix row), or a
// cell = (pin, mesh) (their intersection). The matrix is the canonical driver;
// roster rows, focus tiles, 3D pin markers and 3D surface clicks set the same
// state. Every view follows it — there is no other selection state.
type ActiveSelection =
    | SelNone
    | SelMesh of string
    | SelPin  of ScanPinId
    | SelCell of ScanPinId * string

[<ModelType>]
type Selection = {
    Active  : ActiveSelection
    Hovered : HoverTarget option
}

module Selection =
    let initial = { Active = SelNone; Hovered = None }
    // The pin / mesh a selection implies — the projections every follower reads.
    let pin  = function SelPin p | SelCell (p, _) -> Some p | _ -> None
    let mesh = function SelMesh m | SelCell (_, m) -> Some m | _ -> None

// Mesh isolation (solo): while isolated, ONLY the isolated mesh is shown (the
// reference included would occlude it in Inspect). Every shown/clickable
// consumer (render MeshActive, raycasts, ring gating) goes through this one rule.
module MeshVisibility =
    let shown (solo : string option) (name : string) =
        match solo with
        | Some s -> name = s
        | None -> true

// Snapshot captured when a "frame correspondence" (locate) starts, so a single
// back-out restores the solo state to exactly what it was before (the camera is
// never touched — it only moves on explicit focus/zoom actions).
// Plain record (not a ModelType) → a single aval<LocateState option> in the model.
type LocateState = {
    PrevSolo    : string option
}

[<ModelType>]
type Model =
    {
        Camera         : OrbitState
        MeshOrder      : HashMap<string,int>
        MeshNames      : IndexList<string>
        MeshesLoaded   : HashSet<string>
        CommonCentroid : V3d

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
        // Gear multiplier on the screen-constant 3D pin-flag size AND its
        // world-metre clamp bounds (ScanPin.flagHeightRender).
        FlagScale            : float
        // Screen size (CSS px) of the brushed-sample circle+cross glyphs (gear).
        BrushDotPx           : float

        SceneBounds    : Box3d
        MeshBounds     : Map<string, Box3d>
        // Per-mesh mean sample spacing (m, from the bboxes payload); the slice
        // cells derive the ONE global window from the coarsest loaded mesh.
        MeshSpacing    : Map<string, float>

        // Slice-cell tunables (gear menu): window = N × coarsest spacing; k
        // context planes each side, spaced a fraction of the window; the global
        // vertical extent is a robust percentile over all (pin, mesh) cells.
        SliceNSamples       : float
        SliceContextCount   : float
        SliceContextSpacing : float
        SliceVertPercentile : float

        // LoadTransform is the immutable per-mesh baseline captured at load;
        // SolvedTransform (presence = solved) is written by the correspondence
        // solve; RegView picks which the meshes display. Render-space.
        LoadTransforms        : Map<string, Trafo3d>
        SolvedTransforms      : Map<string, Trafo3d>
        // The correspondence data the last solve consumed (None = no solve). The
        // solve-validity postlude clears the registration when any tracked
        // pin/point is deleted or moved — Before is the source of truth.
        SolveInputs           : SolveInputs option
        RegView               : RegView
        // Spring-loaded before/after peek: hold to momentarily show the OTHER
        // registration state (purely visual — flips the displayed transform, not
        // the committed RegView or any query).
        RegPeekHeld           : bool
        // The ★ mesh all error is relative to (None only before the first load).
        ReferenceMesh         : string option

        Toast                 : string option

        // Per-mesh intrinsic single-mesh error visualization (incidence / range /
        // shape), set from the Overview mesh list. Absent ⇒ HeatOff (textured).
        // Respected in the 3D view and the 2D focus tiles/single alike.
        MeshHeatmap           : Map<string, HeatmapMode>
        // Shape-quality cutoff: fragments below it render transparent in the Shp
        // heatmap (3D + focus). 0 = show everything.
        ShapeThreshold        : float

        // Inspect difference channel: per moving mesh, signed distance to the
        // reference (the mesh's served vertex order), painted in the 3D view and on
        // the focus tiles (the reference is never error-coloured). Lazily fetched;
        // per-pose pairs: main = the committed displayed pose, Other = the opposite
        // Before/After pose (fetched only once a solve exists). SetRegView swaps the
        // pairs in place; the reg peek selects the Other cache (visual, no query).
        FocusDist             : Map<string, float32[]>
        FocusDistOther        : Map<string, float32[]>

        ScanPins              : ScanPinModel
        Selection             : Selection

        RenderingMode       : RenderingMode
        // Isolated mesh (◐) — the one shown/clickable rule (MeshVisibility.shown).
        MeshSolo            : string option
        GearPopoverOpen     : bool
        WorkflowStep        : WorkflowStep

        // Exact-point error probe (Inspect): a clicked surface point's signed
        // difference — (mesh, metric world point, value in metres). Cleared by
        // Esc / background click / anything that invalidates the difference maps.
        PointProbe          : (string * V3d * float) option

        // Confirmation flash of the last committed correspondence pick: metric
        // world point + a generation (bumped per commit so back-to-back picks
        // restart the animation). Cleared by a short timer (ClearCorrFlash).
        CorrFlash           : (V3d * int) option

        // Unified armed correspondence editing: Some (pin, mesh) = the editor
        // is armed for that pair. While armed, the mesh is isolated in the main view
        // and clicking in EITHER the focus or the 3D view sets the point
        // (ROI-clamped; a committed pick disarms). CorrPreview = the live aim ghost
        // shown in both views. None = idle.
        CorrArm             : (ScanPinId * string) option
        CorrPreview         : V3d option
        // Per-sample distribution brushing: the set of brushed sample global
        // ids (canonical order from ScanPinScene.brushSamples). Written by the chart
        // canvases via the hidden-input bridge; read by the chart highlight + the
        // 3D brushed-sample markers.
        BrushedSamples      : Set<int>

        // Outline edge-detect threshold (depth Laplacian) + isoline band count over
        // the scene Z range + isoline alpha. Tunable from the gear menu; see
        // OutlineEdge / buildOutlineNode. Image-space outlines + isolines are always on.
        OutlineThreshold    : float
        // Silhouette/footprint line thickness (px) — the edge detect dilates by
        // sampling at ±this many texels (gear slider, per participant).
        OutlineWidthPx      : float
        IsolineBands        : float
        IsolineOpacity      : float

        // Active "frame correspondence" (locate) backup; Some while a locate is in
        // effect so re-clicking the located matrix cell restores the prior camera +
        // solo state.
        LocateBackup        : LocateState option

        // In-view near-plane slice: the cut plane sits at this fraction of the
        // eye→orbit-centre distance, ⊥ the view direction; 0 = off. Non-modal —
        // the camera stays free, a thick line marks the intersection.
        NearCutFrac         : float
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
            CommonCentroid = V3d.Zero
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
            FlagScale           = 1.0
            BrushDotPx          = 15.0
            SceneBounds    = Box3d.Invalid
            MeshBounds     = Map.empty
            MeshSpacing    = Map.empty
            SliceNSamples       = 5.0
            SliceContextCount   = 2.0
            SliceContextSpacing = 0.15
            SliceVertPercentile = 0.95
            LoadTransforms        = Map.empty
            SolvedTransforms      = Map.empty
            SolveInputs           = None
            RegView               = RegBefore
            RegPeekHeld           = false
            ReferenceMesh         = None
            Toast                 = None
            FocusDist             = Map.empty
            FocusDistOther        = Map.empty
            MeshHeatmap           = Map.empty
            ShapeThreshold        = 0.0
            ScanPins              = ScanPinModel.initial
            Selection             = Selection.initial
            RenderingMode       = Textured
            MeshSolo            = None
            GearPopoverOpen     = false
            WorkflowStep        = Overview
            PointProbe          = None
            CorrFlash           = None
            CorrArm             = None
            CorrPreview         = None
            BrushedSamples      = Set.empty
            OutlineThreshold    = 0.004
            OutlineWidthPx      = 3.0
            IsolineBands        = 700.0
            IsolineOpacity      = 0.45
            LocateBackup        = None
            NearCutFrac         = 0.0
        }
