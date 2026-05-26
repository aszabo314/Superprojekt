namespace Superprojekt

open Aardvark.Base
open FSharp.Data.Adaptive
open Aardvark.Dom

module ServerActions =

    let loadDataset (env : Env<Message>) (dataset : string) =
        task {
            try
                let! cs = MeshData.fetchCentroids MeshView.apiBase.Value dataset
                env.Emit [CentroidsLoaded cs]
            with _ -> ()
            try
                let! bboxes = MeshData.fetchBboxes MeshView.apiBase.Value dataset
                env.Emit [SceneBoundsLoaded bboxes]
            with _ -> ()
        } |> ignore

    let init (env : Env<Message>) =
        task {
            try
                let! datasets = MeshData.fetchDatasets MeshView.apiBase.Value
                env.Emit [DatasetsLoaded datasets]
                let! autoLoad = MeshData.fetchDefaultDataset MeshView.apiBase.Value
                if not (System.String.IsNullOrEmpty autoLoad) && datasets |> Array.contains autoLoad then
                    env.Emit [SetActiveDataset autoLoad]
                    loadDataset env autoLoad
            with _ -> ()
        } |> ignore

