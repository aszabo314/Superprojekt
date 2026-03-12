namespace Superprojekt

open Aardvark.Base
open FSharp.Data.Adaptive
open Aardvark.Dom
open Superprojekt

type Message =
    | Increment
    | Decrement
    | Hover         of option<V3d>
    | Click         of V3d
    | Update        of Index * V3d
    | StartDrag     of Index
    | StopDrag
    | Delete        of Index
    | Clear
    | CameraMessage of OrbitMessage


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
