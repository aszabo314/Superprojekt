namespace Superprojekt

open Aardvark.Base
open FSharp.Data.Adaptive
open Aardvark.Dom
open Superprojekt

type Message =
    | CameraMessage      of OrbitMessage
    | CentroidsLoaded    of (string * V3d)[]
    | SetVisible         of string * bool
    | ToggleMenu
    | FilteredMeshLoaded of string * V3d * int[]    // (mesh name, selection point, index buffer)
    | ClearFilteredMesh
    | LogDebug           of string
    | CycleMeshOrder     of int
    | ToggleRevolver
    | ToggleFullscreen
    | SetRevolverCenter  of V2d


module Update =
    let update (env : Env<Message>) (model : Model) (msg : Message) =
        match msg with
        | CameraMessage msg ->
            { model with Camera = OrbitController.update (Env.map CameraMessage env) model.Camera msg }
        | CentroidsLoaded centroids ->
            let common  = if centroids.Length > 0 then snd centroids.[0] else V3d.Zero
            let names   = centroids |> Array.map fst |> IndexList.ofArray
            let visible = centroids |> Array.fold (fun m (n, _) -> Map.add n true m) Map.empty
            let indices = centroids |> Array.mapi (fun i (n,_) -> n,i) |> HashMap.ofArray
            { model with MeshNames = names; MeshVisible = visible; CommonCentroid = common; MeshOrder = indices }
        | SetVisible(name, v) ->
            { model with MeshVisible = Map.add name v model.MeshVisible }
        | ToggleMenu ->
            { model with MenuOpen = not model.MenuOpen }
        | FilteredMeshLoaded(name, selPt, indices) ->
            { model with Filtered = HashMap.add name indices model.Filtered; FilterCenter = Some selPt }
        | ClearFilteredMesh ->
            { model with Filtered = HashMap.empty; FilterCenter = None }
        | LogDebug s ->
            let log = model.DebugLog.InsertAt(0, s)
            let log = if log.Count > 20 then IndexList.take 20 log else log
            { model with DebugLog = log }
        | CycleMeshOrder delta ->
            let n = model.MeshOrder.Count
            let d = (delta % n) + n
            { model with MeshOrder = model.MeshOrder |> HashMap.map (fun _ idx -> (idx + d) % n) }
        | ToggleRevolver ->
            { model with RevolverOn = not model.RevolverOn }
        | ToggleFullscreen ->
            { model with FullscreenOn = not model.FullscreenOn }
        | SetRevolverCenter ndc ->
            { model with RevolverCenter = ndc }
