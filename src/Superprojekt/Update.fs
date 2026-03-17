namespace Superprojekt

open Aardvark.Base
open FSharp.Data.Adaptive
open Aardvark.Dom
open Superprojekt

type Message =
    | Increment
    | Decrement
    | Hover             of option<V3d>
    | Click             of V3d
    | Update            of Index * V3d
    | StartDrag         of Index
    | StopDrag
    | Delete            of Index
    | Clear
    | CameraMessage     of OrbitMessage
    | CentroidsLoaded   of (string * V3d)[]
    | SetVisible        of string * bool
    | ToggleMenu


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
        | Click p ->
            { model with Points = IndexList.add p model.Points }
        | Update(idx, p) ->
            match model.DraggingPoint with
            | Some (i, _) when i = idx -> { model with DraggingPoint = Some(idx, p) }
            | _ -> model
        | StartDrag idx ->
            { model with DraggingPoint = Some (idx, model.Points.[idx]) }
        | StopDrag ->
            match model.DraggingPoint with
            | Some (idx, pt) ->
                { model with DraggingPoint = None; Points = IndexList.set idx pt model.Points }
            | None ->
                model
        | Delete p ->
            { model with Points = IndexList.remove p model.Points }
        | Clear ->
            { model with Points = IndexList.empty }
        | CentroidsLoaded centroids ->
            let common  = if centroids.Length > 0 then snd centroids.[0] else V3d.Zero
            let names   = centroids |> Array.map fst |> IndexList.ofArray
            let visible = centroids |> Array.fold (fun m (n, _) -> Map.add n true m) Map.empty
            { model with MeshNames = names; MeshVisible = visible; CommonCentroid = common }
        | SetVisible(name, v) ->
            { model with MeshVisible = Map.add name v model.MeshVisible }
        | ToggleMenu ->
            { model with MenuOpen = not model.MenuOpen }
