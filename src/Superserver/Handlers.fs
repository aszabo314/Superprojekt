module Handlers

open System
open System.IO
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open Giraffe
open Aardvark.Base
open Aardvark.Embree
open QueryHandlers

let datasetsHandler : HttpHandler =
    fun next ctx -> task {
        let datasets = MeshLoader.datasets ()
        return! json datasets next ctx
    }

let defaultDatasetHandler : HttpHandler =
    fun next ctx -> task {
        return! json (MeshLoader.defaultDataset ()) next ctx
    }

let centroidsHandler (dataset : string) : HttpHandler =
    fun next ctx -> task {
        let log    = ctx.GetLogger "Superserver"
        let result = Collections.Generic.Dictionary<string, float[]>()
        for name in MeshLoader.meshNames dataset do
            match MeshLoader.getCentroid dataset name with
            | Some c -> result.[name] <- [| c.X; c.Y; c.Z |]
            | None   -> ()
        log.LogInformation("centroids {Dataset}: {Count} meshes", dataset, result.Count)
        return! json result next ctx
    }

let bboxesHandler (dataset : string) : HttpHandler =
    fun next ctx -> task {
        let log    = ctx.GetLogger "Superserver"
        let result = Collections.Generic.Dictionary<string, {| min: float[]; max: float[] |}>()
        for name in MeshLoader.meshNames dataset do
            let count = MeshLoader.meshCount dataset name
            if count > 0 then
                let mutable wMin = V3d( infinity,  infinity,  infinity)
                let mutable wMax = V3d(-infinity, -infinity, -infinity)
                for i in 0 .. count - 1 do
                    let pm = (MeshCache.get dataset name i).parsed
                    if not pm.bbox.IsInvalid then
                        let bMin = pm.centroid + pm.bbox.Min
                        let bMax = pm.centroid + pm.bbox.Max
                        wMin <- V3d(min wMin.X bMin.X, min wMin.Y bMin.Y, min wMin.Z bMin.Z)
                        wMax <- V3d(max wMax.X bMax.X, max wMax.Y bMax.Y, max wMax.Z bMax.Z)
                if wMin.X <= wMax.X then
                    result.[name] <- {| min = fromV3d wMin; max = fromV3d wMax |}
        log.LogInformation("bboxes {Dataset}: {Count} meshes", dataset, result.Count)
        return! json result next ctx
    }

let meshCountHandler (dataset : string, name : string) : HttpHandler =
    fun next ctx -> task {
        let count = MeshLoader.meshCount dataset name
        if count = 0 then return! RequestErrors.notFound (text $"not found: {dataset}/{name}") next ctx
        else            return! text (string count) next ctx
    }

let meshHandler (dataset : string, name : string, index : int) : HttpHandler =
    fun next ctx -> task {
        let log = ctx.GetLogger "Superserver"
        try
            let lm   = MeshCache.get dataset name index
            let pm   = lm.parsed
            let size = 4 + 4 + 4 + 24 + pm.positions.Length * 12 + pm.uvs.Length * 8 + pm.normals.Length * 12 + pm.indices.Length * 4
            use ms = new MemoryStream(size)
            use bw = new BinaryWriter(ms, Text.Encoding.Default, leaveOpen = true)
            bw.Write("MESH"B)
            bw.Write(pm.positions.Length)
            bw.Write(pm.indices.Length)
            bw.Write(pm.centroid.X); bw.Write(pm.centroid.Y); bw.Write(pm.centroid.Z)
            for p  in pm.positions do bw.Write(p.X);  bw.Write(p.Y);  bw.Write(p.Z)
            for uv in pm.uvs       do bw.Write(uv.X); bw.Write(uv.Y)
            for n  in pm.normals   do bw.Write(n.X);  bw.Write(n.Y);  bw.Write(n.Z)
            for i  in pm.indices   do bw.Write(i)
            ctx.Response.ContentType <- "application/octet-stream"
            ctx.Response.ContentLength <- Nullable<int64>(int64 size)
            do! ctx.Response.Body.WriteAsync(ms.GetBuffer(), 0, size)
            log.LogInformation("mesh {Dataset}/{Name}/{Index}: {Verts} verts, {Indices} indices", dataset, name, index, pm.positions.Length, pm.indices.Length)
            return! next ctx
        with ex ->
            log.LogError(ex, "mesh {Dataset}/{Name}/{Index} failed", dataset, name, index)
            return! RequestErrors.notFound (text ex.Message) next ctx
    }

let atlasHandler (dataset : string, name : string, index : int) : HttpHandler =
    fun next ctx -> task {
        match MeshLoader.atlasPath dataset name index with
        | None -> return! RequestErrors.notFound (text $"atlas not found: {dataset}/{name}/{index}") next ctx
        | Some path ->
            ctx.Response.ContentType <- "image/jpeg"
            let bytes = File.ReadAllBytes path
            do! ctx.Response.Body.WriteAsync(bytes, 0, bytes.Length)
            return! next ctx
    }

let webApp : HttpHandler =
    choose [
        route  "/api/datasets"                                  >=> datasetsHandler
        route  "/api/datasets/default"                          >=> defaultDatasetHandler
        routef "/api/datasets/%s/centroids"                     centroidsHandler
        routef "/api/datasets/%s/bboxes"                        bboxesHandler
        routef "/api/datasets/%s/mesh/%s/%i/atlas"              (fun (d,n,i) -> atlasHandler(d,n,i))
        routef "/api/datasets/%s/mesh/%s/%i"                    (fun (d,n,i) -> meshHandler(d,n,i))
        routef "/api/datasets/%s/mesh/%s"                       (fun (d,n)   -> meshCountHandler(d,n))
        route  "/api/query/ray"                                 >=> rayHandler
        route  "/api/query/closest"                             >=> closestHandler
        route  "/api/query/patch"                               >=> patchHandler
        route  "/api/query/contact-rings"                       >=> contactRingsHandler
        route  "/api/query/icp"                                 >=> icpHandler
        route  "/api/query/lsq-pairs"                           >=> lsqPairsHandler
        route  "/api/query/probe"                               >=> probeHandler
        route  "/api/query/region-distance"                     >=> regionDistanceHandler
    ]
