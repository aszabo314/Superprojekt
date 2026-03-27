namespace Superprojekt

open Aardvark.Base
open FSharp.Data.Adaptive
open Aardvark.Dom

module ServerActions =

    let init (env : Env<Message>) =
        task {
            try
                let! datasets = MeshData.fetchDatasets MeshView.apiBase.Value
                env.Emit [DatasetsLoaded datasets]
            with e ->
                Log.error "datasets fetch failed: %A" e
        } |> ignore

    let loadDataset (env : Env<Message>) (dataset : string) =
        task {
            try
                let! cs = MeshData.fetchCentroids MeshView.apiBase.Value dataset
                env.Emit [CentroidsLoaded cs]
            with e ->
                Log.error "centroids fetch failed: %A" e
            try
                let! bboxes = MeshData.fetchBboxes MeshView.apiBase.Value dataset
                env.Emit [ClipBoundsLoaded bboxes]
            with e ->
                Log.error "bboxes fetch failed: %A" e
        } |> ignore

    let triggerFilter (env : Env<Message>) (model : AdaptiveModel) (renderPos : V3d) =
        let cc       = AVal.force model.CommonCentroid
        let worldPos = renderPos + cc
        let names    = AList.force model.MeshNames
        env.Emit [LogDebug (sprintf "triggerFilter pos=%s world=%s meshes=%d" (renderPos.ToString("0.00")) (worldPos.ToString("0.00")) (Seq.length names))]
        task {
            try
                for name in names do
                    env.Emit [LogDebug (sprintf "  query sphere %s..." name)]
                    let! triIds =
                        Query.sphereTriangles MeshView.apiBase.Value name 0 worldPos 1.0
                        |> Async.StartAsTask
                    env.Emit [LogDebug (sprintf "  %s: %d triangles" name triIds.Length)]
                    if triIds.Length > 0 then
                        let loaded = MeshView.loadMeshAsync ignore name
                        match loaded.mesh.Value with
                        | Some mesh ->
                            let filtered = { mesh with indices = triIds }
                            env.Emit [LogDebug (sprintf "  %s: filtered %d indices" name filtered.indices.Length)]
                            env.Emit [FilteredMeshLoaded(name, renderPos, filtered.indices)]
                        | None ->
                            env.Emit [LogDebug (sprintf "  %s: mesh not loaded yet" name)]
                env.Emit [LogDebug "triggerFilter done"]
            with e ->
                env.Emit [LogDebug (sprintf "triggerFilter ERROR: %s" (string e))]
        } |> ignore
