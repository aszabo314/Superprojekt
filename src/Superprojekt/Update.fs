namespace Superprojekt

open Aardvark.Base
open FSharp.Data.Adaptive
open Aardvark.Dom
open Superprojekt

type Message =
    | Increment
    | Decrement
    | Hover             of option<V3d>
    | CameraMessage     of OrbitMessage
    | CentroidsLoaded   of (string * V3d)[]
    | SetVisible        of string * bool
    | ToggleMenu
    | ShowFilteredMesh  of V3d                  // render-space selection point
    | FilteredMeshLoaded of string * V3d * int[] // (mesh name, selection point, index buffer)
    | SetFilters of V3d * HashMap<string,int[]>
    | ClearFilteredMesh
    | LogDebug        of string
    | SetCurrentHoverPosition of Option<V3d>
    | CycleMeshOrder of int
    | ToggleRevolver
    | ToggleFullscreen
    | SetRevolverCenter of V2d


module Update =
    let update (env : Env<Message>) (model : Model) (msg : Message) =
        match msg with
        | CameraMessage msg ->
            { model with Camera = OrbitController.update (Env.map CameraMessage env) model.Camera msg }
        | Increment ->
            { model with Value = model.Value + 1 }
        | Decrement ->
            { model with Value = model.Value - 1 }
        | Hover p ->
            { model with Hover = p }
        | CentroidsLoaded centroids ->
            let common  = if centroids.Length > 0 then snd centroids.[0] else V3d.Zero
            let names   = centroids |> Array.map fst |> IndexList.ofArray
            let visible = centroids |> Array.fold (fun m (n, _) -> Map.add n true m) Map.empty
            let indices = centroids |> Array.mapi (fun i (n,_) -> n,i) |> HashMap.ofArray
            { model with MeshNames = names; MeshVisible = visible; CommonCentroid = common; MeshOrder = indices}
        | SetVisible(name, v) ->
            { model with MeshVisible = Map.add name v model.MeshVisible }
        | ToggleMenu ->
            { model with MenuOpen = not model.MenuOpen }
        | ShowFilteredMesh renderPos ->
            model // async work happens in the view; model unchanged here
        | FilteredMeshLoaded(name, selPt, indices) ->
            {
                model with
                    Filtered = HashMap.add name indices model.Filtered
                    FilterCenter = Some selPt
            }
            
        | ClearFilteredMesh ->
            { model with Filtered = HashMap.empty; FilterCenter = None }
        | LogDebug s ->
            let log = model.DebugLog.InsertAt (0,s)
            let log = if log.Count > 20 then IndexList.take 20 log else log
            { model with DebugLog = log }
        | SetCurrentHoverPosition p ->
            {model with CurrentHoverPosition=p}
        | SetFilters (p,map) ->
            {model with Filtered = map; FilterCenter = Some p}
        | CycleMeshOrder delta ->
            let order = model.MeshOrder
            let n = order.Count
            let newOrder =
                let delta = (delta % n) + n
                order |> HashMap.map (fun _ idx ->
                    (idx + delta) % n
                )
            { model with MeshOrder = newOrder }
        | ToggleRevolver ->
            { model with RevolverOn = not model.RevolverOn }
        | ToggleFullscreen ->
            { model with FullscreenOn = not model.FullscreenOn }
        | SetRevolverCenter ndc ->
            { model with RevolverCenter = ndc }
