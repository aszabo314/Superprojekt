module Handlers

open System
open System.IO
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open Giraffe
open Aardvark.Base
open Aardvark.Embree

// All coordinates in absolute world space (double precision).
// Server converts: localPos = V3f(worldPos - mesh.centroid)

[<CLIMutable>]
type RayRequest     = { Name: string; Index: int; Origin: float[]; Direction: float[] }

[<CLIMutable>]
type ClosestRequest = { Name: string; Index: int; Point: float[] }

[<CLIMutable>]
type SphereRequest  = { Name: string; Index: int; Center: float[]; Radius: float }

[<CLIMutable>]
type BoxRequest     = { Name: string; Index: int; Min: float[]; Max: float[] }

let inline private toV3d (a : float[]) = V3d(a.[0], a.[1], a.[2])
let inline private fromV3d (v : V3d)   = [| v.X; v.Y; v.Z |]

// GET /api/centroids
let centroidsHandler : HttpHandler =
    fun next ctx -> task {
        let log    = ctx.GetLogger "Superserver"
        let result = Collections.Generic.Dictionary<string, float[]>()
        for dir in Directory.GetDirectories(MeshLoader.dataRoot.Value) |> Array.sort do
            let name = Path.GetFileName dir
            match MeshLoader.getCentroid name with
            | Some c -> result.[name] <- [| c.X; c.Y; c.Z |]
            | None   -> ()
        log.LogInformation("centroids: {Count} datasets", result.Count)
        return! json result next ctx
    }

// GET /api/mesh/{name}
let meshCountHandler (name : string) : HttpHandler =
    fun next ctx -> task {
        let count = MeshLoader.meshCount name
        if count = 0 then return! RequestErrors.notFound (text $"not found: {name}") next ctx
        else            return! text (string count) next ctx
    }

// GET /api/mesh/{name}/{i}
// resp: binary  "MESH" | int32 posCount | int32 idxCount | float64×3 centroid | float32×3[] positions | float32×2[] uvs | int32[] indices
let meshHandler (name : string, index : int) : HttpHandler =
    fun next ctx -> task {
        let log = ctx.GetLogger "Superserver"
        try
            let lm   = MeshCache.get name index
            let pm   = lm.parsed
            let size = 4 + 4 + 4 + 24 + pm.positions.Length * 12 + pm.uvs.Length * 8 + pm.indices.Length * 4
            use ms = new MemoryStream(size)
            use bw = new BinaryWriter(ms, Text.Encoding.Default, leaveOpen = true)
            bw.Write("MESH"B)
            bw.Write(pm.positions.Length)
            bw.Write(pm.indices.Length)
            bw.Write(pm.centroid.X); bw.Write(pm.centroid.Y); bw.Write(pm.centroid.Z)
            for p  in pm.positions do bw.Write(p.X);  bw.Write(p.Y);  bw.Write(p.Z)
            for uv in pm.uvs       do bw.Write(uv.X); bw.Write(uv.Y)
            for i  in pm.indices   do bw.Write(i)
            ctx.Response.ContentType <- "application/octet-stream"
            ctx.Response.ContentLength <- Nullable<int64>(int64 size)
            do! ctx.Response.Body.WriteAsync(ms.GetBuffer(), 0, size)
            log.LogInformation("mesh {Name}/{Index}: {Verts} verts, {Indices} indices, {Bytes}B", name, index, pm.positions.Length, pm.indices.Length, size)
            return! next ctx
        with ex ->
            log.LogError(ex, "mesh {Name}/{Index} failed", name, index)
            return! RequestErrors.notFound (text ex.Message) next ctx
    }

// GET /api/mesh/{name}/{i}/atlas
let atlasHandler (name : string, index : int) : HttpHandler =
    fun next ctx -> task {
        match MeshLoader.atlasPath name index with
        | None -> return! RequestErrors.notFound (text $"atlas not found: {name}/{index}") next ctx
        | Some path ->
            ctx.Response.ContentType <- "image/jpeg"
            let bytes = File.ReadAllBytes path
            do! ctx.Response.Body.WriteAsync(bytes, 0, bytes.Length)
            return! next ctx
    }

// POST /api/query/ray
// body: { name, index, origin:[x,y,z], direction:[x,y,z] }
// resp: { hit, t, point:[x,y,z], triangleId }  or  { hit:false }
let rayHandler : HttpHandler =
    fun next ctx -> task {
        try
            let! req = ctx.BindJsonAsync<RayRequest>()
            let lm   = MeshCache.get req.Name req.Index
            let c    = lm.parsed.centroid
            let orig = V3f(toV3d req.Origin - c)
            let dir  = V3f(toV3d req.Direction)
            let mutable hit = RayHit()
            let ok = lm.scene.Intersect(orig, dir, &hit)
            if ok then
                let worldHit = V3d(orig + dir * hit.T) + c
                return! json {| hit = true; t = hit.T; point = fromV3d worldHit; triangleId = int hit.PrimitiveId |} next ctx
            else
                return! json {| hit = false |} next ctx
        with ex -> return! RequestErrors.notFound (text ex.Message) next ctx
    }

// POST /api/query/closest
// body: { name, index, point:[x,y,z] }
// resp: { found, point:[x,y,z], distanceSquared, triangleId }  or  { found:false }
let closestHandler : HttpHandler =
    fun next ctx -> task {
        try
            let! req = ctx.BindJsonAsync<ClosestRequest>()
            let lm   = MeshCache.get req.Name req.Index
            let c    = lm.parsed.centroid
            let res  = lm.scene.GetClosestPoint(V3f(toV3d req.Point - c))
            if res.IsValid then
                let worldPt = V3d res.Point + c
                return! json {| found = true; point = fromV3d worldPt; distanceSquared = res.DistanceSquared; triangleId = int res.PrimID |} next ctx
            else
                return! json {| found = false |} next ctx
        with ex -> return! RequestErrors.notFound (text ex.Message) next ctx
    }

let private binaryIndices (tris : int[]) : HttpHandler =
    fun next ctx -> task {
        ctx.SetContentType "application/octet-stream"
        let len = tris.Length
        let buf = Array.zeroCreate<byte> (4 + len * 4)
        BitConverter.TryWriteBytes(buf.AsSpan(0, 4), len) |> ignore
        Buffer.BlockCopy(tris, 0, buf, 4, len * 4)
        ctx.Response.ContentLength <- Nullable<int64>(int64 buf.Length)
        do! ctx.Response.Body.WriteAsync(buf, 0, buf.Length)
        return! next ctx
    }

// POST /api/query/sphere
// body: { name, index, center:[x,y,z], radius }
// resp: binary  int32 count | int32[] indices
let sphereHandler : HttpHandler =
    fun next ctx -> task {
        let log = ctx.GetLogger "Superserver"
        try
            let! req  = ctx.BindJsonAsync<SphereRequest>()
            let lm    = MeshCache.get req.Name req.Index
            let lc    = V3f(toV3d req.Center - lm.parsed.centroid)
            let tris  = MeshCache.trianglesInSphere lm lc (float32 req.Radius)
            log.LogDebug("sphere {Name}: r={Radius:F2}, {Count} indices", req.Name, req.Radius, tris.Length)
            return! binaryIndices tris next ctx
        with ex ->
            log.LogError(ex, "sphere query failed")
            return! RequestErrors.notFound (text ex.Message) next ctx
    }

// POST /api/query/box
// body: { name, index, min:[x,y,z], max:[x,y,z] }
// resp: binary  int32 count | int32[] indices
let boxHandler : HttpHandler =
    fun next ctx -> task {
        let log = ctx.GetLogger "Superserver"
        try
            let! req  = ctx.BindJsonAsync<BoxRequest>()
            let lm    = MeshCache.get req.Name req.Index
            let c     = lm.parsed.centroid
            let tris  = MeshCache.trianglesInBox lm (V3f(toV3d req.Min - c)) (V3f(toV3d req.Max - c))
            log.LogDebug("box {Name}: {Count} indices", req.Name, tris.Length)
            return! binaryIndices tris next ctx
        with ex ->
            log.LogError(ex, "box query failed")
            return! RequestErrors.notFound (text ex.Message) next ctx
    }

let webApp : HttpHandler =
    choose [
        route "/api/centroids"              >=> centroidsHandler
        routef "/api/mesh/%s/%i/atlas"      atlasHandler
        routef "/api/mesh/%s/%i"            meshHandler
        routef "/api/mesh/%s"               meshCountHandler
        route "/api/query/ray"              >=> rayHandler
        route "/api/query/closest"          >=> closestHandler
        route "/api/query/sphere"           >=> sphereHandler
        route "/api/query/box"              >=> boxHandler
    ]
