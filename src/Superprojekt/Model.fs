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

// One block of the canonical inspect sample stream: ONE pin's pooled error,
// always measured MOV-relative-to-REF. gid = an index into the concatenation of
// the blocks' Samples in stream order — the selected pair's pins inside the
// workspace, every established edge's pins (child vs parent) at Matrix; the
// brush addresses those gids, so both scopes read one stream shape.
type InspectBlock = {
    Mov : string
    Ref : string
    Pin : ScanPinId
    Err : Query.PairPinError
}

// The three-level focus rail: Matrix · Pair · Pin — each a strictly smaller
// scope of WHAT IS LOOKED AT, never a tool mode (tools stay a toolkit inside
// their level). Free navigation among enabled stops; Escape ascends one level.
type FocusLevel =
    | FocusMatrix
    | FocusPair
    | FocusPin

// The ONE per-mesh 2D top-down camera (Setup survey tiles AND the Pin panes —
// a mesh keeps its view across levels): Centre = the look-at point (render
// space), Radius = the eye height above it. Pan/zoom-to-cursor only — no
// orbit; absent from the map = the default bounds framing.
type TileCam = { Centre : V3d; Radius : float }

// The armed pick — the ONE picking mode (no pick without an arm; only camera
// moves are exempt): while armed, a click in ANY view (main 3D or any tile)
// places this pick and the left button no longer orbits; the ARM TARGET is
// the attribution (an ArmPoint pick raycasts its own mesh alone, ArmCentre
// and ArmProbe raycast both pair meshes — nearest hit lands). Disarm = a
// landed pick, Esc, or clicking the arm control again. ArmProbe = the
// inspection point probe (Pair + Pin); the rest are Pin-level pin picks.
type ArmTarget =
    | ArmCentre
    | ArmPoint of string
    | ArmProbe

// Transient hover preview of the Pin-level focus/arm buttons: what the 3D
// visibility WOULD narrow to on click (one side, or the whole pin).
type PinHover =
    | HoverSide of string
    | HoverBoth

// The scoped per-level selection: each level's choice, remembered across
// level jumps (Matrix keeps the last pair, Pair reopens the last pin);
// changing an ancestor clears its descendants. Point = the mesh side of the
// pin's correspondence point in focus (the Pin level's focus buttons): it
// narrows the 3D view to that mesh and re-frames the tiles onto the point;
// None = the whole pin (both meshes). Plain record → ONE aval.
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
        | FocusPair | FocusMatrix -> FocusMatrix

    // Reachability: Matrix always; Pair needs a chosen pair; Pin a chosen pin
    // or a placement transaction in flight.
    let enabled (sel : FocusSelection) (placing : bool) = function
        | FocusMatrix -> true
        | FocusPair -> sel.Pair.IsSome
        | FocusPin -> sel.Pair.IsSome && (sel.Pin.IsSome || placing)

// The ONE shown/clickable rule per focus level: Matrix shows all meshes
// (narrowed transiently by the matrix hover preview); Pair isolates the
// selected pair's two meshes; Pin narrows further to the effective focus mesh.
// The tile isolate intersects EVERY level's scope. Every consumer — render
// MeshActive, raycast candidate sets, coverage gating — goes through it.
module MeshVisibility =
    // The ONE effective (isolate, pinFocus) narrowing pair fed to `shown`: a
    // transient target — ◎-side hover > armed A/B pick > tile hover —
    // REPLACES the committed lock+point pair on BOTH components (hovering
    // another mesh while one is isolated must preview THAT mesh isolated;
    // intersecting with the stale lock would show nothing), and un-hover
    // falls back, so the committed state restores with zero bookkeeping.
    // ◉-Pin hover previews the release (no narrowing). An armed centre/probe
    // keeps the lock but lifts the point narrowing (aiming needs both
    // meshes).
    let effectiveNarrowing (hover : PinHover option) (armed : ArmTarget option)
                           (isoHover : string option) (isoLock : string option)
                           (point : string option) =
        match hover with
        | Some (HoverSide m) -> Some m, Some m
        | Some HoverBoth -> None, None
        | None ->
            match armed with
            | Some (ArmPoint m) -> Some m, Some m
            | Some ArmCentre | Some ArmProbe -> isoLock, None
            | None ->
                match isoHover with
                | Some m -> Some m, Some m
                | None -> isoLock, point

    // The brush's colour-isolation frame isolates the mesh the samples are
    // anchored to (the pair's MOV) — a DEFAULT isolate only: an explicit tile
    // lock (and through it every transient preview and the vis peek) still
    // wins, so the mode composes with the isolation state instead of overriding
    // it.
    let withBrushIsolate (brushMov : string option) (isoLock : string option) =
        match isoLock with
        | Some _ -> isoLock
        | None -> brushMov

    // `matrixScope` narrows the Matrix level to a named set (None = every
    // mesh): while the graph error map paints, only the meshes that CARRY a
    // parent-relative error stay solid — the reference root and unregistered
    // meshes drop to their outlines, since a white surface there would read as
    // "registered and fine".
    let shown (focus : FocusLevel) (selPair : (string * string) option)
              (isolate : string option) (hoverPair : (string * string) option)
              (matrixScope : Set<string> option) (pinFocus : string option) (name : string) =
        let inScope =
            match focus with
            | FocusMatrix ->
                (match hoverPair with Some (a, b) -> name = a || name = b | None -> true)
                && (match matrixScope with Some s -> Set.contains name s | None -> true)
            | FocusPair ->
                match selPair with
                | Some (a, b) -> name = a || name = b
                | None -> true
            | FocusPin ->
                match selPair with
                | Some (a, b) ->
                    (match pinFocus with
                     | Some m -> name = m
                     | None -> name = a || name = b)
                | None -> true
        inScope && (match isolate with Some m -> name = m | None -> true)

    // Pin scoping mirrors mesh scoping: every pin at the Matrix survey, only
    // the selected pair's pins inside its Pair/Pin scope.
    let pinShown (focus : FocusLevel) (selPair : (string * string) option) (pair : string * string) =
        match focus with
        | FocusMatrix -> true
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

        GhostSilhouette      : bool
        GhostOpacity         : float
        ShadingStrength      : float
        SlopeThresholdDeg    : float
        AnchorGhostMode      : bool
        QuickPinRadius       : float
        // Gear multiplier on the screen-constant 3D pin-flag size AND its
        // world-metre clamp bounds (ScanPin.flagHeightRender).
        FlagScale            : float
        // Outermost metric radius of the correspondence markers' local-
        // geometry reveal (rings at ×0.2/×0.6/×1.0, cuts fade over it).
        RevealRadius         : float

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
        // Tile-strip mesh isolation (any level): the clicked lock + the
        // transient tile-hover preview (hover wins over the lock). Both are
        // wiped on any focus jump — never a persistent mode.
        TileIsolate           : string option
        TileIsolateHover      : string option
        // Matrix-cell hover: the pair whose screen-space overlap area previews
        // in 3D (per-pixel coverage test in the mesh shader). Transient — wiped
        // on cell leave, descend, tab switch and dataset switch.
        MatrixHoverPair       : (string * string) option
        // Shape-quality cutoff: fragments below it render transparent in the Shp
        // heatmap (3D + focus). 0 = show everything.
        ShapeThreshold        : float

        ScanPins              : ScanPinModel

        RenderingMode       : RenderingMode
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
        // The GRAPH-scope error stream (Matrix): every established edge's pins,
        // child-relative-to-parent, in canonical edge×pin order — the pooled
        // union the graph histogram and its brush read. Held in BOTH states so
        // the Matrix pose peek flips the error field with the geometry:
        // After = both endpoints at their composed poses (the residual),
        // Before = both at their as-loaded baselines (the raw disagreement).
        // They land in one message, so the two are never out of step.
        GraphError          : InspectBlock[] option
        GraphErrorBefore    : InspectBlock[] option
        // The GRAPH-scope twin (Matrix): per registered CHILD mesh, its
        // per-vertex signed distance vs its PARENT — every established edge's
        // moving-side buffer at once, in the same two states. Empty ⇔ nothing
        // to paint (a zero-edge graph is legitimately blank).
        GraphDist           : Map<string, float32[]>
        GraphDistBefore     : Map<string, float32[]>
        // False-colour error map toggle (in-cell inspect tool; MOV only).
        CellMapOn           : bool
        // Diagram brush: sample gids = indices into the canonical inspect
        // sample stream of the CURRENT scope (MeshView.inspectBlocksAt),
        // capped at 12000; it clears when a jump crosses between scopes.
        BrushedSamples      : Set<int>
        // 3D-hovered brushed sample (diagram cross-highlight) + its exact
        // value from the exact-point endpoint.
        HoverSample         : int option
        HoverReadout        : (int * float) option
        // The landed probe readout (ArmProbe): transient — survives the
        // landing's auto-disarm so the value stays readable, wiped by the next
        // arm, any focus jump and every cell invalidation.
        ProbeReadout        : (V3d * float) option
        // The armed pick + its cursor preview (metric world, on the armed
        // surface) — the preview renders in EVERY view at once. Both
        // transient: wiped on disarm, any focus jump, dataset switch.
        ArmedPick           : ArmTarget option
        ArmPreview          : V3d option
        // Focus/arm button hover: the transient Pin-level visibility preview.
        PinFocusHover       : PinHover option
        // Pin-row hover: the tile cameras preview-frame this pin while it
        // lasts (a click makes the framing persistent via SelectPin).
        TilePinHover        : ScanPinId option
        // ○ New pin button hover: lights the pair's overlap-region gate in the
        // main 3D (only the overlap is a valid pin location). Transient.
        NewPinHover         : bool
        // The Pin panel's radius disclosure: the slider stays hidden until its
        // edit is clicked. Transient — collapses on pin change and focus jump.
        PinRadiusEditOpen   : bool
        // The two spring-loaded blink-comparator keys (pair scope; hold to
        // swap, release to return; zero config):
        //   PeekVis  — the isolation flips to the pair's OTHER mesh (same spot,
        //              other epoch); derived in the shown rule, the isolate
        //              lock itself never moves. Needs a pair-mesh isolate, so
        //              it exists in the pair workspace ALONE.
        //   PeekPose — as-loaded instead of composed: the pair's MOV inside the
        //              workspace (REF static — "did registration help?"), the
        //              WHOLE graph at Matrix. Purely visual.
        PeekVis             : bool
        PeekPose            : bool
        // The transient loop awaiting FORCED resolution — the blocking modal is
        // the whole interaction; the committed graph stays the prior tree.
        LoopPending         : LoopPending option
        // The Pin exit-guard: leaving Pin with an incomplete pin (an in-flight
        // draft) parks the wanted destination here and raises the blocking
        // confirm-delete popup — confirm jumps (the jump rolls the draft
        // back), cancel stays. Esc and rail jumps share this one path.
        PinExitPending      : FocusLevel option
        GearPopoverOpen     : bool
        // The hidden top-bar mesh menu: reference-root designation + per-mesh
        // render toggles (deliberately out of the workflow rail).
        MeshMenuOpen        : bool
        // The top-bar jump-to-sensor dropdown (per-mesh main-camera jumps).
        SensorMenuOpen      : bool
        // The docked inspection toolbox's expand state (collapsed = the thin
        // header edge alone) — a view preference, survives level jumps.
        InspectOpen         : bool

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
        // The far twin: fragments BEYOND this fraction discard. Off sits at
        // the slider's RIGHT end (≥ 2.495) — a small fraction cuts almost
        // everything, so "off" cannot be the 0 end like the near cut.
        FarCutFrac          : float
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

    // A mesh's sensor position: the mesh origin, whose world coordinate is the
    // *centroid.txt value (the radial-scan pipeline centres each OPC on its scan
    // station — data-verified, not an assumption). Metric world at load pose:
    let sensorWorld (model : Model) (mesh : string) =
        Map.tryFind mesh model.DatasetCentroids |> Option.defaultValue model.CommonCentroid

    // …and in render space (load pose): (world − common)·scale.
    let sensorRender (model : Model) (mesh : string) =
        ScanPin.renderCentre model.CommonCentroid (DatasetScale.forMesh model.DatasetScales mesh)
            (sensorWorld model mesh)

    // The first mesh in the list's sensor position (render space), the anchor for the
    // coordinate cross + the camera's resting target. Empty list → render origin.
    let firstSensorRender (model : Model) =
        match model.MeshNames |> IndexList.toList with
        | first :: _ -> sensorRender model first
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
            GhostSilhouette     = true
            GhostOpacity        = 0.12
            ShadingStrength     = 0.15
            SlopeThresholdDeg   = 15.0
            AnchorGhostMode     = true
            QuickPinRadius      = 0.5
            FlagScale           = 1.0
            RevealRadius        = 0.5
            SceneBounds    = Box3d.Invalid
            MeshBounds     = Map.empty
            LoadTransforms        = Map.empty
            RegGraph              = RegGraph.empty
            ComposedPoses         = Map.empty
            PairOverlaps          = Map.empty
            Toast                 = None
            MeshHeatmap           = Map.empty
            TileIsolate           = None
            TileIsolateHover      = None
            MatrixHoverPair       = None
            ShapeThreshold        = 0.0
            ScanPins              = ScanPinModel.initial
            RenderingMode       = Textured
            Focus               = FocusMatrix
            Sel                 = FocusSelection.empty
            CellError           = None
            CellErrorBefore     = None
            CellDist            = None
            GraphError          = None
            GraphErrorBefore    = None
            GraphDist           = Map.empty
            GraphDistBefore     = Map.empty
            CellMapOn           = false
            BrushedSamples      = Set.empty
            HoverSample         = None
            HoverReadout        = None
            ProbeReadout        = None
            ArmedPick           = None
            ArmPreview          = None
            PinFocusHover       = None
            TilePinHover        = None
            NewPinHover         = false
            PinRadiusEditOpen   = false
            PeekVis             = false
            PeekPose            = false
            LoopPending         = None
            PinExitPending      = None
            GearPopoverOpen     = false
            MeshMenuOpen        = false
            SensorMenuOpen      = false
            InspectOpen         = true
            OutlineThreshold    = 0.004
            OutlineWidthPx      = 3.0
            IsolineBands        = 700.0
            IsolineOpacity      = 0.45
            NearCutFrac         = 0.0
            FarCutFrac          = 2.5
        }
