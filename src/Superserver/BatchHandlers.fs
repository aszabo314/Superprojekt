module BatchHandlers

open System
open System.IO
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open Giraffe
open Aardvark.Base
open Aardvark.Embree
open QueryHandlers

let rayBatchHandler : HttpHandler =
    fun next ctx -> task {
        let log = ctx.GetLogger "Superserver"
        try
            let! req = ctx.BindJsonAsync<RayBatchRequest>()
            let rayCount = req.Origins.Length / 3
            let meshCount = req.Names.Length
            let meshes = Array.init meshCount (fun i ->
                let dataset, name = splitName req.Names.[i]
                MeshCache.get dataset name 0)
            let hitFlags = Array.zeroCreate<byte> rayCount
            let hitPts   = Array.zeroCreate<V3d> rayCount
            System.Threading.Tasks.Parallel.For(0, rayCount, fun i ->
                let worldOrigin = V3d(req.Origins.[i*3], req.Origins.[i*3+1], req.Origins.[i*3+2])
                let direction   = V3d(req.Directions.[i*3], req.Directions.[i*3+1], req.Directions.[i*3+2])
                let dirF = V3f direction
                let mutable bestT = System.Single.MaxValue
                let mutable bestHit = V3d.Zero
                let mutable gotHit = false
                for k in 0 .. meshCount - 1 do
                    let lm = meshes.[k]
                    let orig = V3f(worldOrigin - lm.parsed.centroid)
                    let mutable hit = RayHit()
                    if lm.scene.Intersect(orig, dirF, &hit) then
                        if hit.T < bestT then
                            bestT <- hit.T
                            bestHit <- V3d(orig + dirF * hit.T) + lm.parsed.centroid
                            gotHit <- true
                if gotHit then
                    hitFlags.[i] <- 1uy
                    hitPts.[i] <- bestHit) |> ignore
            ctx.SetContentType "application/octet-stream"
            let bufLen = 4 + rayCount * (1 + 3 * 8)
            let buf = Array.zeroCreate<byte> bufLen
            let mutable o = 0
            BitConverter.TryWriteBytes(buf.AsSpan(o, 4), rayCount) |> ignore
            o <- o + 4
            for i in 0 .. rayCount - 1 do
                buf.[o] <- hitFlags.[i]
                o <- o + 1
                BitConverter.TryWriteBytes(buf.AsSpan(o, 8), hitPts.[i].X) |> ignore
                o <- o + 8
                BitConverter.TryWriteBytes(buf.AsSpan(o, 8), hitPts.[i].Y) |> ignore
                o <- o + 8
                BitConverter.TryWriteBytes(buf.AsSpan(o, 8), hitPts.[i].Z) |> ignore
                o <- o + 8
            let hitCount = hitFlags |> Array.sumBy (fun b -> int b)
            log.LogInformation("ray-batch {Rays} rays, {Meshes} meshes, {Hits} hits", rayCount, meshCount, hitCount)
            ctx.Response.ContentLength <- Nullable<int64>(int64 buf.Length)
            do! ctx.Response.Body.WriteAsync(buf, 0, buf.Length)
            return! next ctx
        with ex ->
            log.LogError(ex, "ray-batch failed")
            return! RequestErrors.notFound (text ex.Message) next ctx
    }

let gridEvalHandler : HttpHandler =
    fun next ctx -> task {
        let log = ctx.GetLogger "Superserver"
        try
            let! req = ctx.BindJsonAsync<GridEvalRequest>()
            let anchor = toV3d req.Anchor
            let axis = toV3d req.Axis
            let result = MeshIcp.evaluateGrid req.Dataset anchor axis req.Radius req.Resolution req.ExtentForward req.ExtentBackward
            log.LogInformation("grid-eval {Dataset}: res={Resolution}, {CellCount} cells, {DatasetCount} datasets", req.Dataset, result.Resolution, result.Cells.Length, result.DatasetStats.Length)
            use ms = new MemoryStream()
            use bw = new BinaryWriter(ms, Text.Encoding.Default, leaveOpen = true)
            bw.Write(result.Resolution)
            bw.Write(result.Cells.Length)
            bw.Write(result.DatasetStats.Length)
            for (gu, gv, s) in result.Cells do
                bw.Write(gu); bw.Write(gv)
                bw.Write(s.Average); bw.Write(s.Q1); bw.Write(s.Q3)
                bw.Write(s.Min); bw.Write(s.Max); bw.Write(s.Variance)
            for ds in result.DatasetStats do
                let nameBytes = Text.Encoding.UTF8.GetBytes(ds.MeshName)
                bw.Write(nameBytes.Length)
                bw.Write(nameBytes)
                bw.Write(ds.ZMin); bw.Write(ds.ZQ1); bw.Write(ds.ZMedian)
                bw.Write(ds.ZQ3); bw.Write(ds.ZMax); bw.Write(ds.ZVariance)
            bw.Flush()
            ctx.Response.ContentType <- "application/octet-stream"
            let buf = ms.ToArray()
            ctx.Response.ContentLength <- Nullable<int64>(int64 buf.Length)
            do! ctx.Response.Body.WriteAsync(buf, 0, buf.Length)
            return! next ctx
        with ex ->
            log.LogError(ex, "grid-eval failed")
            return! RequestErrors.notFound (text ex.Message) next ctx
    }

