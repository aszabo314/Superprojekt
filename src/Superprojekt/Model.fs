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

// The four-level focus rail: Setup · Matrix · Pair · Pin — each a strictly
// smaller scope of WHAT IS LOOKED AT, never a tool mode (tools stay a toolkit
// inside their level). Free navigation among enabled stops; Escape ascends one
// level.
type FocusLevel =
    | FocusSetup
    | FocusMatrix
    | FocusPair
    | FocusPin

// The ONE per-mesh 2D top-down camera (Setup survey tiles AND the Pin panes —
// a mesh keeps its view across levels): Centre = the look-at point (render
// space), Radius = the eye height above it. Pan/zoom-to-cursor only — no
// orbit; absent from the map = the default bounds framing.
type TileCam = { Centre : V3d; Radius : float }

// The scoped per-level selection: each level's choice, remembered across
// level jumps (Matrix keeps the last pair, Pair reopens the last pin);
// changing an ancestor clears its descendants. Point = the mesh side of the
// pin's correspondence point in focus. Plain record → ONE aval.
type FocusSelection = {
    // PairCell.key order.
    Pair  : (string * string) option
    Pin   : ScanPinId option
    Point : string option
}

module FocusSelection =
    let empty : FocusSelection = { Pair = None; Pin = None; Point = None }

module FocusLevel =
    let parent = function
        | FocusPin -> FocusPair
        | FocusPair -> FocusMatrix
        | FocusMatrix | FocusSetup -> FocusSetup

    // Reachability: Setup/Matrix always; Pair needs a chosen pair; Pin a
    // chosen pin or a placement transaction in flight.
    let enabled (sel : FocusSelection) (placing : bool) = function
        | FocusSetup | FocusMatrix -> true
        | FocusPair -> sel.Pair.IsSome
        | FocusPin -> sel.Pair.IsSome && (sel.Pin.IsSome || placing)

// The ONE shown/clickable rule per focus level: Setup/Matrix show all meshes
// (narrowed transiently by the Setup isolate / the matrix hover preview);
// Pair/Pin isolate the selected pair's two meshes. Every consumer — render
// MeshActive, raycast candidate sets, coverage gating — goes through it.
module MeshVisibility =
    let shown (focus : FocusLevel) (selPair : (string * string) option)
              (isolate : string option) (hoverPair : (string * string) option) (name : string) =
        match focus with
        | FocusSetup -> (match isolate with Some m -> name = m | None -> true)
        | FocusMatrix -> (match hoverPair with Some (a, b) -> name = a || name = b | None -> true)
        | FocusPair | FocusPin ->
            match selPair with
            | Some (a, b) -> name = a || name = b
            | None -> true

    // Pin scoping mirrors mesh scoping: every pin at the survey levels, only
    // the selected pair's pins inside its Pair/Pin scope.
    let pinShown (focus : FocusLevel) (selPair : (string * string) option) (pair : string * string) =
        match focus with
        | FocusSetup | FocusMatrix -> true
        | FocusPair | FocusPin -> selPair = Some pair

[<ModelType>]
type Model =
    {
        Camera         : OrbitState
        // The per-mesh 2D cameras (tiles + panes); reset on dataset switch.
        TileCams       : Map<string, TileCam>
        MeshOrder      : HashMap<string,int>
        MeshNames      : IndexList<string>
        MeshesLoaded   : HashSet<string>
        CommonCentroid : V3d

        Datasets         : string list
        ActiveDataset    : string option
        DatasetScales    : Map<string, float>
        DatasetCentroids : Map<string, V3d>
        // Per-mesh panorama centre (the calibrated-camera origin), absolute world
        // coords — same frame as the centroid. Read from <dataset>/pano-centers.txt
        // on load; sensor-viewpoint framing subtracts the mesh centroid for its eye.
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

        SceneBounds    : Box3d
        MeshBounds     : Map<string, Box3d>

        // The immutable per-mesh as-loaded baseline (render-space); a mesh
        // without a composed graph pose displays this.
        LoadTransforms        : Map<string, Trafo3d>
        // The registration graph: root (★ — the pose anchor all error is
        // relative to) + committed pairwise edges. Plain record → ONE aval.
        RegGraph              : RegGraph
        // worldPose per tree mesh in RENDER space (edge transforms composed to
        // the root, conjugated through the dataset similarity) — the displayed
        // registered pose. Derived from RegGraph by the reducer (recompose on
        // any edge/root change); mirrors Edges 1:1, so empty ⇔ unregistered.
        ComposedPoses         : Map<string, Trafo3d>
        // Pairwise registerability, unordered pair key (PairCell.key) →
        // sufficient overlap. Evaluated ONCE per dataset at the as-loaded
        // baselines (registerability is intrinsic to the pair, not the poses) by
        // the lazy overlap sweep; drives the navigator's impossible/possible.
        PairOverlaps          : Map<string * string, bool>

        Toast                 : string option

        // Per-mesh intrinsic single-mesh error visualization (incidence / range /
        // shape), set from the Overview mesh list. Absent ⇒ HeatOff (textured).
        // Respected in the 3D view and the Setup survey tiles alike.
        MeshHeatmap           : Map<string, HeatmapMode>
        // Setup-scoped mesh isolation (survey rows): the clicked lock + the
        // transient button-hover preview (hover wins over the lock). Both are
        // wiped on leaving the Setup view — never a persistent mode.
        SetupIsolate          : string option
        SetupIsolateHover     : string option
        // Matrix-cell hover: the pair whose screen-space overlap area previews
        // in 3D (per-pixel coverage test in the mesh shader). Transient — wiped
        // on cell leave, descend, tab switch and dataset switch.
        MatrixHoverPair       : (string * string) option
        // Shape-quality cutoff: fragments below it render transparent in the Shp
        // heatmap (3D + focus). 0 = show everything.
        ShapeThreshold        : float

        ScanPins              : ScanPinModel

        RenderingMode       : RenderingMode
        // Row/col order of the pair-matrix navigator (a view preference).
        MatrixOrder         : MatrixOrder
        // The focus rail's current stop + the per-level selection it navigates.
        Focus               : FocusLevel
        Sel                 : FocusSelection

        // ── In-cell error inspection (transient per cell — every cache clears
        // on nav/pin/pose changes via invalidateCellError). Sample values are
        // stored MOV-relative-to-REF (flipped at landing if the request
        // orientation differed).
        // Per-pin pairwise error at the CURRENT poses, canonical pin order.
        CellError           : (ScanPinId * Query.PairPinError)[] option
        // The same pins at the pair edge's BEFORE poses (registered pairs) —
        // the diagram's before/after diff outline.
        CellErrorBefore     : (ScanPinId * Query.PairPinError)[] option
        // MOV's per-vertex signed distance vs REF (the false-colour buffer).
        CellDist            : float32[] option
        // False-colour error map toggle (in-cell inspect tool; MOV only).
        CellMapOn           : bool
        // Diagram brush: sample gids = indices into the canonical CellError
        // sample concatenation, capped at 200.
        BrushedSamples      : Set<int>
        // 3D-hovered brushed sample (diagram cross-highlight) + its exact
        // value from the exact-point endpoint.
        HoverSample         : int option
        HoverReadout        : (int * float) option
        // Armed point-sample probe: fully transient — the readout vanishes on
        // disarm, persists nothing, links to no diagram.
        ProbeArmed          : bool
        ProbeReadout        : (V3d * float) option
        // The two spring-loaded blink-comparator keys (cell scope only, REF/MOV
        // from the tree; hold to swap, release to return; zero config):
        //   PeekVis  — the MOV mesh blinks OFF (the REF alone answers "same rock?");
        //   PeekPose — the MOV displays AS-LOADED instead of composed (REF
        //              static — "did registration help?"). Purely visual.
        PeekVis             : bool
        PeekPose            : bool
        // The transient loop awaiting FORCED resolution — the blocking modal is
        // the whole interaction; the committed graph stays the prior tree.
        LoopPending         : LoopPending option
        GearPopoverOpen     : bool

        // Outline edge-detect threshold (depth Laplacian) + isoline band count over
        // the scene Z range + isoline alpha. Tunable from the gear menu; see
        // OutlineEdge / buildOutlineNode. Image-space outlines + isolines are always on.
        OutlineThreshold    : float
        // Silhouette/footprint line thickness (px) — the edge detect dilates by
        // sampling at ±this many texels (gear slider, per participant).
        OutlineWidthPx      : float
        IsolineBands        : float
        IsolineOpacity      : float

        // In-view near-plane slice: the cut plane sits at this fraction of the
        // eye→orbit-centre distance, ⊥ the view direction; 0 = off. Non-modal —
        // the camera stays free, a thick line marks the intersection.
        NearCutFrac         : float
    }

// Displayed = the pose a mesh currently shows: its composed graph pose when it
// is in the registration tree, its immutable as-loaded baseline otherwise.
// Every query and scene-graph consumer goes through these.
module ModelTransforms =
    let loadRender (model : Model) (mesh : string) =
        Map.tryFind mesh model.LoadTransforms |> Option.defaultValue Trafo3d.Identity

    let displayedRender (model : Model) (mesh : string) =
        match Map.tryFind mesh model.ComposedPoses with
        | Some t -> t
        | None -> loadRender model mesh

    let private toWorld (model : Model) (mesh : string) (renderT : Trafo3d) =
        RigidTransform.renderToWorld
            (DatasetScale.forMesh model.DatasetScales mesh) model.CommonCentroid renderT

    let displayedWorld (model : Model) (mesh : string) =
        toWorld model mesh (displayedRender model mesh)

    // The as-loaded baseline in metric world — anchor seeding and solve inputs
    // evaluate here (correspondences are baseline data, never pose followers).
    let loadWorld (model : Model) (mesh : string) =
        toWorld model mesh (loadRender model mesh)

    // Per-edge before/after pose of `mesh` (the pose query the pair peek and
    // cell diagrams read): the committed composition, with `edgeChild`'s edge
    // zeroed on EdgeBefore. Meshes outside the tree stay at their baseline.
    let edgeWorld (edgeChild : string) (side : EdgeSide) (model : Model) (mesh : string) : Trafo3d =
        match Map.tryFind mesh (RegGraph.composeEdge edgeChild side model.RegGraph) with
        | Some w -> w
        | None -> loadWorld model mesh

    // Rebuild ComposedPoses from the graph: compose the edge transforms to the
    // root in metric world (RegGraph.composeAll), conjugate each into render
    // space. Full recompute — the subtree-memoized path (composeSubtree) is for
    // the per-edge re-solve flows.
    let recomposePoses (model : Model) : Model =
        let render =
            RegGraph.composeAll model.RegGraph
            |> Map.map (fun mesh w ->
                RigidTransform.worldToRender (DatasetScale.forMesh model.DatasetScales mesh) model.CommonCentroid w)
        { model with ComposedPoses = render }

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
            TileCams       = Map.empty
            MeshOrder      = HashMap.empty
            MeshNames      = IndexList.empty
            MeshesLoaded   = HashSet.empty
            CommonCentroid = V3d.Zero
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
            SceneBounds    = Box3d.Invalid
            MeshBounds     = Map.empty
            LoadTransforms        = Map.empty
            RegGraph              = RegGraph.empty
            ComposedPoses         = Map.empty
            PairOverlaps          = Map.empty
            Toast                 = None
            MeshHeatmap           = Map.empty
            SetupIsolate          = None
            SetupIsolateHover     = None
            MatrixHoverPair       = None
            ShapeThreshold        = 0.0
            ScanPins              = ScanPinModel.initial
            RenderingMode       = Textured
            MatrixOrder         = OrderSensor
            Focus               = FocusSetup
            Sel                 = FocusSelection.empty
            CellError           = None
            CellErrorBefore     = None
            CellDist            = None
            CellMapOn           = true
            BrushedSamples      = Set.empty
            HoverSample         = None
            HoverReadout        = None
            ProbeArmed          = false
            ProbeReadout        = None
            PeekVis             = false
            PeekPose            = false
            LoopPending         = None
            GearPopoverOpen     = false
            OutlineThreshold    = 0.004
            OutlineWidthPx      = 3.0
            IsolineBands        = 700.0
            IsolineOpacity      = 0.45
            NearCutFrac         = 0.0
        }
