module Handlers

open System
open System.IO
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open Giraffe
open Aardvark.Base
open Aardvark.Embree

// All query coordinates in absolute world space (double precision).
// Server converts: localPos = V3f(worldPos - mesh.centroid)
// Query Name field uses "dataset/meshName" format.

[<CLIMutable>]
type RayRequest     = { Name: string; Index: int; Origin: float[]; Direction: float[] }

[<CLIMutable>]
type ClosestRequest = { Name: string; Index: int; Point: float[] }

[<CLIMutable>]
type SphereRequest  = { Name: string; Index: int; Center: float[]; Radius: float }

[<CLIMutable>]
type BoxRequest     = { Name: string; Index: int; Min: float[]; Max: float[] }

[<CLIMutable>]
type PlaneIntersectionRequest = { Name: string; Index: int; PlanePoint: float[]; PlaneNormal: float[]; AxisU: float[]; AxisV: float[]; Thickness: float; MaxExtentU: float; MaxExtentV: float }

[<CLIMutable>]
type PlaneIntersectionBatchRequest = { Names: string[]; PlanePoint: float[]; PlaneNormal: float[]; AxisU: float[]; AxisV: float[]; Thickness: float; MaxExtentU: float; MaxExtentV: float }

[<CLIMutable>]
type SphereBatchRequest = { Names: string[]; Center: float[]; Radius: float }

[<CLIMutable>]
type GridEvalRequest = { Dataset: string; Anchor: float[]; Axis: float[]; Radius: float; Resolution: int; ExtentForward: float; ExtentBackward: float }

[<CLIMutable>]
type CylinderEvalRequest = { Dataset: string; Anchor: float[]; Axis: float[]; Radii: float[]; AngularResolution: int; ExtentForward: float; ExtentBackward: float }

let inline private toV3d (a : float[]) = V3d(a.[0], a.[1], a.[2])
let inline private fromV3d (v : V3d)   = [| v.X; v.Y; v.Z |]

let private splitName (fullName : string) =
    let parts = fullName.Split([|'/'|], 2)
    if parts.Length = 2 then parts.[0], parts.[1]
    else "", fullName

// GET /api/datasets
let datasetsHandler : HttpHandler =
    fun next ctx -> task {
        let datasets = MeshLoader.datasets ()
        return! json datasets next ctx
    }

// GET /api/datasets/default
let defaultDatasetHandler : HttpHandler =
    fun next ctx -> task {
        return! json (MeshLoader.defaultDataset ()) next ctx
    }

// GET /api/datasets/{dataset}/centroids
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

// GET /api/datasets/{dataset}/bboxes
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

// GET /api/datasets/{dataset}/mesh/{name}
let meshCountHandler (dataset : string, name : string) : HttpHandler =
    fun next ctx -> task {
        let count = MeshLoader.meshCount dataset name
        if count = 0 then return! RequestErrors.notFound (text $"not found: {dataset}/{name}") next ctx
        else            return! text (string count) next ctx
    }

// GET /api/datasets/{dataset}/mesh/{name}/{i}
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

// GET /api/datasets/{dataset}/mesh/{name}/{i}/atlas
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

// POST /api/query/ray
let rayHandler : HttpHandler =
    fun next ctx -> task {
        try
            let! req = ctx.BindJsonAsync<RayRequest>()
            let dataset, name = splitName req.Name
            let lm   = MeshCache.get dataset name req.Index
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
let closestHandler : HttpHandler =
    fun next ctx -> task {
        try
            let! req = ctx.BindJsonAsync<ClosestRequest>()
            let dataset, name = splitName req.Name
            let lm   = MeshCache.get dataset name req.Index
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
let sphereHandler : HttpHandler =
    fun next ctx -> task {
        let log = ctx.GetLogger "Superserver"
        try
            let! req  = ctx.BindJsonAsync<SphereRequest>()
            let dataset, name = splitName req.Name
            let lm    = MeshCache.get dataset name req.Index
            let lc    = V3f(toV3d req.Center - lm.parsed.centroid)
            let tris  = MeshCache.trianglesInSphere lm lc (float32 req.Radius)
            log.LogDebug("sphere {Name}: r={Radius:F2}, {Count} indices", req.Name, req.Radius, tris.Length)
            return! binaryIndices tris next ctx
        with ex ->
            log.LogError(ex, "sphere query failed")
            return! RequestErrors.notFound (text ex.Message) next ctx
    }

// POST /api/query/box
let boxHandler : HttpHandler =
    fun next ctx -> task {
        let log = ctx.GetLogger "Superserver"
        try
            let! req  = ctx.BindJsonAsync<BoxRequest>()
            let dataset, name = splitName req.Name
            let lm    = MeshCache.get dataset name req.Index
            let c     = lm.parsed.centroid
            let tris  = MeshCache.trianglesInBox lm (V3f(toV3d req.Min - c)) (V3f(toV3d req.Max - c))
            log.LogDebug("box {Name}: {Count} indices", req.Name, tris.Length)
            return! binaryIndices tris next ctx
        with ex ->
            log.LogError(ex, "box query failed")
            return! RequestErrors.notFound (text ex.Message) next ctx
    }

// POST /api/query/plane-intersection
let planeIntersectionHandler : HttpHandler =
    fun next ctx -> task {
        let log = ctx.GetLogger "Superserver"
        try
            let! req = ctx.BindJsonAsync<PlaneIntersectionRequest>()
            let dataset, name = splitName req.Name
            let lm = MeshCache.get dataset name req.Index
            let c = lm.parsed.centroid
            let planePoint = toV3d req.PlanePoint - c
            let planeNormal = toV3d req.PlaneNormal
            let axisU = toV3d req.AxisU
            let axisV = toV3d req.AxisV
            let segments = MeshCache.planeIntersection lm planePoint planeNormal axisU axisV req.Thickness req.MaxExtentU req.MaxExtentV
            log.LogDebug("plane-intersection {Name}: {Count} segments", req.Name, segments.Length)
            return! json {| segments = segments |} next ctx
        with ex ->
            log.LogError(ex, "plane-intersection failed")
            return! RequestErrors.notFound (text ex.Message) next ctx
    }

// POST /api/query/plane-intersection-batch
let planeIntersectionBatchHandler : HttpHandler =
    fun next ctx -> task {
        let log = ctx.GetLogger "Superserver"
        try
            let! req = ctx.BindJsonAsync<PlaneIntersectionBatchRequest>()
            let planePoint0 = toV3d req.PlanePoint
            let planeNormal = toV3d req.PlaneNormal
            let axisU = toV3d req.AxisU
            let axisV = toV3d req.AxisV
            let results = Array.zeroCreate<float[][]> req.Names.Length
            System.Threading.Tasks.Parallel.For(0, req.Names.Length, fun i ->
                let dataset, name = splitName req.Names.[i]
                let lm = MeshCache.get dataset name 0
                let c = lm.parsed.centroid
                let segs = MeshCache.planeIntersection lm (planePoint0 - c) planeNormal axisU axisV req.Thickness req.MaxExtentU req.MaxExtentV
                results.[i] <- segs) |> ignore
            let payload =
                Array.init req.Names.Length (fun i ->
                    {| name = req.Names.[i]; segments = results.[i] |})
            let total = results |> Array.sumBy (fun s -> s.Length)
            log.LogInformation("plane-intersection-batch {Count} meshes, {Total} segments", req.Names.Length, total)
            return! json {| results = payload |} next ctx
        with ex ->
            log.LogError(ex, "plane-intersection-batch failed")
            return! RequestErrors.notFound (text ex.Message) next ctx
    }

// POST /api/query/sphere-batch
let sphereBatchHandler : HttpHandler =
    fun next ctx -> task {
        let log = ctx.GetLogger "Superserver"
        try
            let! req = ctx.BindJsonAsync<SphereBatchRequest>()
            let center0 = toV3d req.Center
            let radius = float32 req.Radius
            let results = Array.zeroCreate<int[]> req.Names.Length
            System.Threading.Tasks.Parallel.For(0, req.Names.Length, fun i ->
                let dataset, name = splitName req.Names.[i]
                let lm = MeshCache.get dataset name 0
                let lc = V3f(center0 - lm.parsed.centroid)
                results.[i] <- MeshCache.trianglesInSphere lm lc radius) |> ignore
            ctx.SetContentType "application/octet-stream"
            let utf8 = System.Text.Encoding.UTF8
            let nameBytes = req.Names |> Array.map utf8.GetBytes
            let mutable total = 4
            for i in 0 .. req.Names.Length - 1 do
                total <- total + 4 + nameBytes.[i].Length + 4 + results.[i].Length * 4
            let buf = Array.zeroCreate<byte> total
            let mutable o = 0
            BitConverter.TryWriteBytes(buf.AsSpan(o, 4), req.Names.Length) |> ignore
            o <- o + 4
            for i in 0 .. req.Names.Length - 1 do
                let nb = nameBytes.[i]
                BitConverter.TryWriteBytes(buf.AsSpan(o, 4), nb.Length) |> ignore
                o <- o + 4
                Buffer.BlockCopy(nb, 0, buf, o, nb.Length)
                o <- o + nb.Length
                let idx = results.[i]
                BitConverter.TryWriteBytes(buf.AsSpan(o, 4), idx.Length) |> ignore
                o <- o + 4
                Buffer.BlockCopy(idx, 0, buf, o, idx.Length * 4)
                o <- o + idx.Length * 4
            let totalIdx = results |> Array.sumBy (fun a -> a.Length)
            log.LogInformation("sphere-batch {Count} meshes, {Total} indices", req.Names.Length, totalIdx)
            ctx.Response.ContentLength <- Nullable<int64>(int64 buf.Length)
            do! ctx.Response.Body.WriteAsync(buf, 0, buf.Length)
            return! next ctx
        with ex ->
            log.LogError(ex, "sphere-batch failed")
            return! RequestErrors.notFound (text ex.Message) next ctx
    }

// POST /api/query/grid-eval
let gridEvalHandler : HttpHandler =
    fun next ctx -> task {
        let log = ctx.GetLogger "Superserver"
        try
            let! req = ctx.BindJsonAsync<GridEvalRequest>()
            let anchor = toV3d req.Anchor
            let axis = toV3d req.Axis
            let result = MeshCache.evaluateGrid req.Dataset anchor axis req.Radius req.Resolution req.ExtentForward req.ExtentBackward
            log.LogInformation("grid-eval {Dataset}: res={Resolution}, {CellCount} cells, {DatasetCount} datasets", req.Dataset, result.Resolution, result.Cells.Length, result.DatasetStats.Length)
            // Binary response for efficiency
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

// POST /api/query/cylinder-eval
let cylinderEvalHandler : HttpHandler =
    fun next ctx -> task {
        let log = ctx.GetLogger "Superserver"
        try
            let! req = ctx.BindJsonAsync<CylinderEvalRequest>()
            let anchor = toV3d req.Anchor
            let axis = toV3d req.Axis
            let result = MeshCache.cylinderEval req.Dataset anchor axis req.Radii req.AngularResolution req.ExtentForward req.ExtentBackward
            log.LogInformation("cylinder-eval {Dataset}: res={AngularResolution}, rings={RingCount}, {HitCount} hits", req.Dataset, req.AngularResolution, result.RingCount, result.Hits.Length)
            use ms = new MemoryStream()
            use bw = new BinaryWriter(ms, Text.Encoding.Default, leaveOpen = true)
            bw.Write(result.AngularResolution)
            bw.Write(result.RingCount)
            bw.Write(result.Hits.Length)
            for h in result.Hits do
                bw.Write(h.Ring)
                bw.Write(h.Angle)
                let nameBytes = Text.Encoding.UTF8.GetBytes(h.MeshName)
                bw.Write(nameBytes.Length)
                bw.Write(nameBytes)
                bw.Write(h.Height)
            bw.Flush()
            ctx.Response.ContentType <- "application/octet-stream"
            let buf = ms.ToArray()
            ctx.Response.ContentLength <- Nullable<int64>(int64 buf.Length)
            do! ctx.Response.Body.WriteAsync(buf, 0, buf.Length)
            return! next ctx
        with ex ->
            log.LogError(ex, "cylinder-eval failed")
            return! RequestErrors.notFound (text ex.Message) next ctx
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
        route  "/api/query/sphere"                              >=> sphereHandler
        route  "/api/query/sphere-batch"                        >=> sphereBatchHandler
        route  "/api/query/box"                                 >=> boxHandler
        route  "/api/query/plane-intersection"                  >=> planeIntersectionHandler
        route  "/api/query/plane-intersection-batch"            >=> planeIntersectionBatchHandler
        route  "/api/query/grid-eval"                           >=> gridEvalHandler
        route  "/api/query/cylinder-eval"                       >=> cylinderEvalHandler
    ]
