namespace Superprojekt

open Aardvark.Base
open Superprojekt

type Message =
    | CameraMessage      of OrbitMessage
    // The per-mesh 2D camera (tiles + panes: pan / zoom-to-cursor), computed
    // view-side.
    | SetTileCam         of mesh : string * TileCam
    | CentroidsLoaded    of (string * V3d)[]
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
    // The correspondence markers' reveal extent (gear debug slider) —
    // outermost metric radius; a change invalidates every reveal.
    | SetRevealRadius of float
    // Designate the registration-graph root (★, the navigator's overview
    // step). A tree member re-roots in place (registration kept, path edges
    // reversed); a mesh outside the registered tree clears the graph.
    | SetRegRoot of string
    // Focus rail: free jump to an enabled stop (the reducer re-guards
    // enablement, so a stale click can never land on a disabled level).
    | SetFocus of FocusLevel
    // Escape: up one level (Pin→Pair→Matrix; at Matrix a no-op).
    | FocusAscend
    // The Pin exit-guard popup: confirm = delete the incomplete pin and
    // perform the parked jump; cancel = stay in Pin.
    | ConfirmPinExit
    | CancelPinExit
    // Matrix cell click: select the pair (a NEW pair cascade-clears pin/point)
    // and enter its Pair level.
    | SelectPair of a:string * b:string
    // Pair-level pin list: choose the pin (enables the Pin stop).
    | SelectPin of ScanPinId
    // Pin-level focus buttons: Some mesh = focus that correspondence side
    // (that mesh alone in 3D, tiles tight on the point); None = the whole pin
    // (both meshes, tiles tight on the pin). Writes Sel.Point.
    | SelectPoint of string option
    // Transient hover preview of the Pin-level focus/arm buttons.
    | SetPinFocusHover of PinHover option
    // Pin-row hover: preview-frame the tile cameras onto this pin.
    | SetTilePinHover of ScanPinId option
    // ○ New pin hover: light the pair's overlap-region gate.
    | SetNewPinHover of bool
    // The Pin panel's radius disclosure (slider hidden until clicked).
    | ToggleRadiusEdit
    // Arm/disarm a pick (same target again = disarm; the reducer guards level
    // + validity — ArmCentre only during placement, ArmProbe at Pair/Pin).
    | ToggleArmPick of ArmTarget
    // The armed pick's cursor preview point (metric world; view-side hover
    // raycasts, throttled). Ignored while nothing is armed.
    | SetArmPreview of V3d option
    // Solve the current pair's edge from its pins (cell toolkit; needs ≥3).
    | SolvePair of a:string * b:string
    // ── In-cell error inspection. Results are gen-guarded (UpdateHelpers).
    | CellErrorComputed of gen:int * after:(ScanPinId * Query.PairPinError)[] * before:(ScanPinId * Query.PairPinError)[] option
    | CellDistComputed of gen:int * dist:float32[]
    // The graph-scope caches: the pooled per-edge sample stream and one map
    // buffer per registered child, each vs its parent.
    | GraphErrorComputed of gen:int * blocks:InspectBlock[]
    | GraphDistComputed of gen:int * dist:(string * float32[])[]
    // Diagram x-range brush → the brushed sample gid set (replaces wholesale).
    | SetBrushedSamples of int list
    // 3D hover over a brushed sample (diagram cross-highlight + exact readout).
    | SetHoverSample of int option
    | HoverReadoutComputed of gen:int * gid:int * value:float
    // A landed ArmProbe pick's exact value (the landing auto-disarms).
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
    // Tile-strip mesh isolation: transient hover preview + click lock (the
    // tiles' own interaction). Both clear on any focus jump.
    | SetTileIsolateHover of string option
    | ToggleTileIsolate of string
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
    | ToggleGearPopover
    // The hidden top-bar mesh menu (reference root + per-mesh render toggles).
    | ToggleMeshMenu
    // The top-bar jump-to-sensor dropdown.
    | ToggleSensorMenu
    // Collapse/expand the docked inspection toolbox.
    | ToggleInspectPanel
    // In-view near-plane slice: cut-plane fraction of the eye→centre distance (0 = off).
    | SetNearCut of float
    // The far twin (fraction of the eye→centre distance; ≥ 2.495 = off).
    | SetFarCut of float
    // Explicit MAIN-3D camera framing (the double-click grammar) — these own
    // the 3D radius conventions.
    | FlyToPoint of world:V3d * radius:float
    | ZoomToPin of ScanPinId
    // Fly the main 3D to a mesh's sensor/scan-camera viewpoint (roster control) —
    // the same framing the dataset load rests on.
    | FlyToSensor of string

and ScanPinMessage =
    // ── the placement transaction: modal, FREE ORDER (the arm buttons pick
    // which of centre / point A / point B lands next); the pin exists
    // IMPLICITLY the moment all three parts are placed — leaving Pin with an
    // incomplete draft raises the exit-guard (confirm-delete).
    | BeginPinTransaction of pair:(string * string)
    | DraftAreaAt of mesh:string * local:V3d
    | DraftPointAt of mesh:string * local:V3d
    | SetDraftRadius of float
    | DraftRingsComputed of Map<string, V3d[][]>
    // ── committed-pin edits (each invalidates the pair's solve). Point and
    // centre re-picks go through the armed pick like every placement pick;
    // the centre re-pick re-anchors the pin onto the hit mesh.
    | SetInnerRadius of ScanPinId * float
    | EditPointAt of ScanPinId * mesh:string * local:V3d
    | EditCentreAt of ScanPinId * mesh:string * local:V3d
    | DeletePin of ScanPinId
    | ContactRingsComputed of ScanPinId * Map<string, V3d[][]>
    // side 0 = fst Pair, 1 = snd Pair; polylines in the point's mesh's own frame.
    | PointRevealComputed of ScanPinId * side:int * V3d[][]
    | DraftRevealComputed of side:int * V3d[][]
