namespace Superprojekt

open Aardvark.Base
open Superprojekt

type Message =
    | CameraMessage      of OrbitMessage
    // The per-mesh 2D camera (tiles + panes: pan / zoom-to-cursor), computed
    // view-side.
    | SetTileCam         of mesh : string * TileCam
    | CentroidsLoaded    of (string * V3d)[]
    | PanoCentersLoaded  of (string * V3d)[]
    | LoadFinished       of string
    | ToggleGhostSilhouette
    | SetGhostOpacity of float
    | SetShadingStrength of float
    | SetSlopeThresholdDeg of float
    | SetOutlineThreshold of float
    | SetOutlineWidth of float
    | SetIsolineBands of float
    | SetIsolineOpacity of float
    | ToggleAnchorGhostMode
    | SetQuickPinRadius of float
    | SetFlagScale of float
    // Designate the registration-graph root (★, the navigator's overview
    // step). A tree member re-roots in place (registration kept, path edges
    // reversed); a mesh outside the registered tree clears the graph.
    | SetRegRoot of string
    // Focus rail: free jump to an enabled stop (the reducer re-guards
    // enablement, so a stale click can never land on a disabled level).
    | SetFocus of FocusLevel
    // Escape: up one level (Pin→Pair→Matrix→Setup; at Setup a no-op).
    | FocusAscend
    // Matrix cell click: select the pair (a NEW pair cascade-clears pin/point)
    // and enter its Pair level.
    | SelectPair of a:string * b:string
    // Pair-level pin list: choose the pin (enables the Pin stop).
    | SelectPin of ScanPinId
    // Solve the current pair's edge from its pins (cell toolkit; needs ≥3).
    | SolvePair of a:string * b:string
    // ── In-cell error inspection. Results are gen-guarded (UpdateHelpers).
    | CellErrorComputed of gen:int * after:(ScanPinId * Query.PairPinError)[] * before:(ScanPinId * Query.PairPinError)[] option
    | CellDistComputed of gen:int * dist:float32[]
    // Diagram x-range brush → the brushed sample gid set (replaces wholesale).
    | SetBrushedSamples of int list
    // 3D hover over a brushed sample (diagram cross-highlight + exact readout).
    | SetHoverSample of int option
    | HoverReadoutComputed of gen:int * gid:int * value:float
    // The armed point-sample probe (fully transient).
    | ToggleProbeArmed
    | ProbeReadoutComputed of gen:int * world:V3d * value:float
    // The in-cell false-colour error map toggle.
    | ToggleCellMap
    // The spring-loaded blink keys (view key down/up; cell scope enforced in
    // the reducer — a peek is refused unless both pair meshes are resident).
    | SetPeekVis of bool
    | SetPeekPose of bool
    // ── The forced loop-resolution modal (P9): pick exactly one cycle edge to
    // remove (None = the just-added edge itself); confirm commits, cancel
    // discards the redundant edge and the prior tree stands.
    | SelectLoopEdge of string option
    | ConfirmLoopResolution
    | CancelLoopResolution
    // Solve landing: the world transform mapping the child's baseline points
    // onto the parent's + per-pin residuals (the quality input). gen-guarded.
    | PairSolved of gen:int * child:string * parent:string * world:M44d * residuals:float[]
    // Transient feedback from view-side guards (e.g. the placement hard-prohibit).
    | ShowToast of string
    | ClearToast
    // Per-mesh intrinsic error visualization (Overview mesh list). HeatOff = textured.
    | SetMeshHeatmap of mesh:string * HeatmapMode
    // Setup mesh isolation: transient hover preview + click lock (survey rows).
    // Both clear on leaving the Setup view.
    | SetSetupIsolateHover of string option
    | ToggleSetupIsolate of string
    // Matrix-cell hover: preview the pair's screen-space overlap area in 3D.
    | SetMatrixHoverPair of (string * string) option
    // Shp cutoff — triangles below the quality threshold render transparent.
    | SetShapeThreshold of float
    // One batch from the lazy pairwise-overlap sweep: (meshA, meshB, sufficient).
    | PairOverlapComputed of gen:int * (string * string * bool)[]
    // Per-mesh world bboxes; the fetch doubles as the server cache warmer.
    | SceneBoundsLoaded  of (string * Box3d)[]
    | DatasetsLoaded     of string[]
    | SetActiveDataset   of string
    | ScanPinMsg              of ScanPinMessage
    | SetRenderingMode of RenderingMode
    // Row/col order of the pair-matrix navigator.
    | SetMatrixOrder of MatrixOrder
    | ToggleGearPopover
    // In-view near-plane slice: cut-plane fraction of the eye→centre distance (0 = off).
    | SetNearCut of float
    // Explicit MAIN-3D camera framing (the double-click grammar) — these own
    // the 3D radius conventions.
    | FlyToPoint of world:V3d * radius:float
    | ZoomToPin of ScanPinId
    // Fly the main 3D to a mesh's sensor/scan-camera viewpoint (roster control) —
    // the same framing the dataset load rests on.
    | FlyToSensor of string

and ScanPinMessage =
    // ── the placement transaction: modal, FREE ORDER, Esc aborts wholesale.
    | BeginPinTransaction of pair:(string * string)
    | SetDraftTool of DraftTool
    | DraftAreaAt of mesh:string * local:V3d
    | DraftPointAt of mesh:string * local:V3d
    | CommitPin
    | AbortPinTransaction
    // ── committed-pin edits (each invalidates the pair's solve). A point
    // re-pick needs no arming: the Pin level's pane click attributes the mesh.
    | SetInnerRadius of ScanPinId * float
    | EditPointAt of ScanPinId * mesh:string * local:V3d
    | DeletePin of ScanPinId
    | ContactRingsComputed of ScanPinId * Map<string, V3d[][]>
