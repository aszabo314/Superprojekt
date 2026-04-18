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
                env.Emit [ClipBoundsLoaded bboxes]
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

    let triggerFilter (env : Env<Message>) (model : AdaptiveModel) (renderPos : V3d) =
        let cc       = AVal.force model.CommonCentroid
        let worldPos = renderPos + cc
        let names    = AList.force model.MeshNames |> IndexList.toArray
        task {
            try
                let! results =
                    Query.sphereTrianglesBatch MeshView.apiBase.Value names worldPos 1.0
                    |> Async.StartAsTask
                for name, triIds in results do
                    if triIds.Length > 0 then
                        let loaded = MeshView.loadMeshAsync ignore name
                        match loaded.mesh.Value with
                        | Some _ -> env.Emit [FilteredMeshLoaded(name, renderPos, triIds)]
                        | None -> ()
            with _ -> ()
        } |> ignore
