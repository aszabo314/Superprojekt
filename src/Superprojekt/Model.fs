namespace Superprojekt

open FSharp.Data.Adaptive
open Aardvark.Base
open Adaptify
open Aardvark.Dom
open FSharp.Data.Adaptive

type ReferenceAxisMode =
    | AlongWorldZ
    | AlongCameraView

type ExploreHighlightMode =
    | SteepnessOnly
    | DisagreementOnly
    | Combined

type RenderingMode =
    | Textured
    | Shaded
    | WhiteSurface

type MeshSoloState =
    | NoSolo
    | Solo of name:string * restore:Map<string,bool>

type ExploreMode =
    {
        Enabled            : bool
        HighlightMode      : ExploreHighlightMode
        SteepnessThreshold : float
        DisagreementThreshold : float
        HighlightColor     : C4f
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

module ExploreMode =
    let initial =
        {
            Enabled            = false
            HighlightMode      = Combined
            SteepnessThreshold = 0.3
            DisagreementThreshold = 0.05
            HighlightColor     = C4f(1.0f, 0.1f, 0.35f, 1.0f)
            HighlightAlpha     = 0.9
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
            GhostOpacity        = 0.1
            ClipActive     = false
            ClipBox        = Box3d(V3d(-1e10), V3d(1e10))
            ClipBounds     = Box3d.Invalid
            MeshBounds     = Map.empty

            ActivePickingLayer = None

            LassoDrawing = None
            LassoVolume  = None

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
