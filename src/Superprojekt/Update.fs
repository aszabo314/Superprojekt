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
    | ClearFilteredMesh
    | LogDebug        of string


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
            { model with MeshNames = names; MeshVisible = visible; CommonCentroid = common }
        | SetVisible(name, v) ->
            { model with MeshVisible = Map.add name v model.MeshVisible }
        | ToggleMenu ->
            { model with MenuOpen = not model.MenuOpen }
        | ShowFilteredMesh renderPos ->
            model // async work happens in the view; model unchanged here
        | FilteredMeshLoaded(name, selPt, indices) ->
            { model with FilteredMesh = Some(name, selPt, indices) }
        | ClearFilteredMesh ->
            { model with FilteredMesh = None }
        | LogDebug s ->
            let log = model.DebugLog.Add s
            let log = if log.Count > 20 then IndexList.skip (log.Count - 20) log else log
            { model with DebugLog = log }
