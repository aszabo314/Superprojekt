namespace Superprojekt

open FSharp.Data.Adaptive
open Aardvark.Base
open Adaptify
open Aardvark.Dom


[<ModelType>]
type Model =
    {
        Camera         : OrbitState
        MeshOrder      : HashMap<string,int>
        MeshNames      : IndexList<string>
        MeshVisible    : Map<string, bool>
        CommonCentroid : V3d
        MenuOpen       : bool

        [<CheapEquals>]
        Filtered       : HashMap<string, int[]>
        FilterCenter   : option<V3d>
        DebugLog       : IndexList<string>

        RevolverOn     : bool
        FullscreenOn   : bool
        RevolverCenter : V2d
    }

module Model =
    let initial =
        {
            Camera         = OrbitState.create V3d.Zero 1.0 0.3 3.0 Button.Left Button.Middle
            MeshOrder      = HashMap.empty
            MeshNames      = IndexList.empty
            MeshVisible    = Map.empty
            CommonCentroid = V3d.Zero
            MenuOpen       = false
            Filtered       = HashMap.empty
            FilterCenter   = None
            DebugLog       = IndexList.empty
            RevolverOn     = false
            FullscreenOn   = false
            RevolverCenter = V2d.Zero
        }
