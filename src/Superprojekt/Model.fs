namespace Superprojekt

open FSharp.Data.Adaptive
open Aardvark.Base
open Adaptify
open Aardvark.Dom
open FSharp.Data.Adaptive

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

        [<CheapEquals>]
        Filtered       : HashMap<string, int[]>
        FilterCenter   : option<V3d>
        DebugLog       : IndexList<string>

        RevolverOn           : bool
        FullscreenOn         : bool
        RevolverCenter       : V2d
        DifferenceRendering  : bool
        MinDifferenceDepth   : float
        MaxDifferenceDepth   : float
        GhostSilhouette      : bool

        ClipBox        : Box3d   // active clip range (render-space uniforms computed from this)
        ClipBounds     : Box3d   // world-space union of all dataset bboxes; Box3d.Invalid until loaded
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
            Filtered       = HashMap.empty
            FilterCenter   = None
            DebugLog       = IndexList.empty
            RevolverOn          = false
            FullscreenOn        = false
            RevolverCenter      = V2d.Zero
            DifferenceRendering = false
            MinDifferenceDepth  = 3.0
            MaxDifferenceDepth  = 10.0
            GhostSilhouette     = false
            ClipBox        = Box3d(V3d(-1e10), V3d(1e10))
            ClipBounds     = Box3d.Invalid
        }
