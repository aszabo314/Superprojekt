open System
open System.IO
open System.Collections.Concurrent
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Giraffe
open Aardvark.Base
open Aardvark.Data.Wavefront
open Aardvark.Embree

// ── Raw parsed mesh (OBJ → unified vertex buffer, centroid-relative) ─────────

type ParsedMesh =
    {
        centroid  : V3d
        positions : V3f[]   // centroid-relative
        uvs       : V2f[]
        indices   : int[]   // flat triangle list  (triangleCount × 3)
    }

// ── In-memory Embree scene + BbTree ──────────────────────────────────────────

type LoadedMesh =
    {
        parsed   : ParsedMesh
        device   : Device
        geometry : TriangleGeometry
        scene    : Scene
        bvh      : BbTree   // BVH over per-triangle AABBs (centroid-relative, double)
    }

// ── OBJ loading ───────────────────────────────────────────────────────────────

module MeshLoader =

    let private findDataRoot () =
        let mutable dir = AppContext.BaseDirectory
        let mutable result = None
        while result.IsNone && not (isNull dir) do
            let candidate = Path.Combine(dir, "data")
            if Directory.Exists candidate then result <- Some candidate
            else dir <- Path.GetDirectoryName dir
        result |> Option.defaultWith (fun () -> failwith "data folder not found")

    let dataRoot = lazy findDataRoot ()

    let private objFiles (folder : string) =
        Directory.GetFiles(folder, "*.obj") |> Array.sort

    let meshCount (name : string) =
        let folder = Path.Combine(dataRoot.Value, name)
        if Directory.Exists folder then objFiles folder |> Array.length else 0

    let parseMesh (name : string) (index : int) : ParsedMesh =
        let folder = Path.Combine(dataRoot.Value, name)
        if not (Directory.Exists folder) then failwithf "not found: %s" name

        let files = objFiles folder
        if index < 0 || index >= files.Length then
            failwithf "mesh index %d out of range (folder has %d)" index files.Length

        let objFile      = files.[index]
        let centroidFile =
            Directory.GetFiles(folder, "*_centroid.txt") |> Array.tryHead
            |> Option.defaultWith (fun () -> failwithf "no centroid.txt in %s" folder)

        let centroid =
            let parts = File.ReadAllText(centroidFile).Trim().Split(' ')
            V3d(float parts.[0], float parts.[1], float parts.[2])

        let mesh = ObjParser.Load objFile

        let positions =
            match mesh.Vertices with
            | :? System.Collections.Generic.IList<V3f> as v -> v |> Seq.map V3d |> Seq.toArray
            | :? System.Collections.Generic.IList<V3d> as v -> v |> Seq.toArray
            | :? System.Collections.Generic.IList<V4f> as v -> v |> Seq.map (fun p -> V3d(float p.X, float p.Y, float p.Z)) |> Seq.toArray
            | :? System.Collections.Generic.IList<V4d> as v -> v |> Seq.map (fun p -> V3d(p.X, p.Y, p.Z)) |> Seq.toArray
            | _ -> [||]

        let texCoords =
            if isNull mesh.TextureCoordinates then [||]
            else mesh.TextureCoordinates |> Seq.map Vec.xy |> Seq.toArray

        let vertexMap = System.Collections.Generic.Dictionary<struct(int * int), int>()
        let outPos = ResizeArray<V3f>()
        let outUv  = ResizeArray<V2f>()
        let outIdx = ResizeArray<int>()

        for set in mesh.FaceSets do
            let iPos = set.VertexIndices
            let iTc  = if isNull set.TexCoordIndices then iPos else set.TexCoordIndices
            for ti in 0 .. set.ElementCount - 1 do
                let fi  = set.FirstIndices.[ti]
                let cnt = set.FirstIndices.[ti + 1] - fi
                if cnt = 3 then
                    let p0 = positions.[iPos.[fi]]
                    let p1 = positions.[iPos.[fi + 1]]
                    let p2 = positions.[iPos.[fi + 2]]
                    if not (Vec.AnyNaN p0 || Vec.AnyNaN p1 || Vec.AnyNaN p2) then
                        for k in 0 .. 2 do
                            let pi  = iPos.[fi + k]
                            let uvi = iTc.[fi + k]
                            let key = struct(pi, uvi)
                            let idx =
                                match vertexMap.TryGetValue key with
                                | true, i -> i
                                | _ ->
                                    let i = outPos.Count
                                    outPos.Add(V3f positions.[pi])
                                    outUv.Add(if uvi < texCoords.Length then texCoords.[uvi] else V2f.Zero)
                                    vertexMap.[key] <- i
                                    i
                            outIdx.Add idx

        { centroid  = centroid
          positions = outPos.ToArray()
          uvs       = outUv.ToArray()
          indices   = outIdx.ToArray() }

    // Binary layout (little-endian):
    //   "MESH"  vertexCount  indexCount  centroid(3×f64)
    //   positions(V3f[])  uvs(V2f[])  indices(int[])

    let serialize (name : string) (index : int) : byte[] =
        let pm     = parseMesh name index
        let folder = Path.Combine(dataRoot.Value, name)
        let files  = objFiles folder
        let base'  = Path.GetFileNameWithoutExtension files.[index]
        let jpg    = Path.Combine(folder, base' + "_atlas.jpg")
        if not (File.Exists jpg) then failwithf "atlas not found: %s" jpg
        use ms = new MemoryStream()
        use bw = new BinaryWriter(ms)
        bw.Write("MESH"B)
        bw.Write(pm.positions.Length)
        bw.Write(pm.indices.Length)
        bw.Write(pm.centroid.X); bw.Write(pm.centroid.Y); bw.Write(pm.centroid.Z)
        for p in pm.positions do bw.Write(p.X); bw.Write(p.Y); bw.Write(p.Z)
        for uv in pm.uvs      do bw.Write(uv.X); bw.Write(uv.Y)
        for i  in pm.indices  do bw.Write(i)
        ms.ToArray()

    let atlasPath (name : string) (index : int) =
        let folder = Path.Combine(dataRoot.Value, name)
        let files  = objFiles folder
        if index < 0 || index >= files.Length then None
        else
            let base' = Path.GetFileNameWithoutExtension files.[index]
            let jpg   = Path.Combine(folder, base' + "_atlas.jpg")
            if File.Exists jpg then Some jpg else None


// ── Embree scene cache (load-on-demand, kept forever) ─────────────────────────

module MeshCache =

    let private cache = ConcurrentDictionary<struct(string * int), LoadedMesh>()

    let get (name : string) (index : int) : LoadedMesh =
        cache.GetOrAdd(struct(name, index), fun _ ->
            let pm     = MeshLoader.parseMesh name index
            let device = new Device()
            let geom   = new TriangleGeometry(device, ReadOnlyMemory<V3f>(pm.positions), ReadOnlyMemory<int>(pm.indices), RTCBuildQuality.High)
            let scene  = new Scene(device, RTCBuildQuality.High, false)
            scene.AttachGeometry(geom)
            scene.Commit()
            let triBoxes =
                let n = pm.indices.Length / 3
                Array.init n (fun ti ->
                    let p0 = V3d pm.positions.[pm.indices.[ti * 3    ]]
                    let p1 = V3d pm.positions.[pm.indices.[ti * 3 + 1]]
                    let p2 = V3d pm.positions.[pm.indices.[ti * 3 + 2]]
                    Box3d(Fun.Min(p0, Fun.Min(p1, p2)), Fun.Max(p0, Fun.Max(p1, p2)))
                )
            let bvh = BbTree(triBoxes, BbTree.BuildFlags.CreateBoxArrays ||| BbTree.BuildFlags.LeafLimit16)
            { parsed = pm; device = device; geometry = geom; scene = scene; bvh = bvh }
        )


// ── Spatial query helpers ─────────────────────────────────────────────────────

// Traverse the BbTree, collecting primitive indices whose AABB passes the overlap test.
let private traverseBvh (bbt : BbTree) (overlaps : Box3d -> bool) =
    let result = ResizeArray<int>()
    if bbt.NodeCount > 0 then
        let idx   = bbt.IndexArray
        let left  = bbt.LeftBoxArray
        let right = bbt.RightBoxArray
        let stack = System.Collections.Generic.Stack<int>()
        stack.Push 0
        while stack.Count > 0 do
            let ni = stack.Pop()
            let lc = idx.[ni * 2]
            if overlaps left.[ni] then
                if lc >= 0 then stack.Push lc else result.Add(-lc - 1)
            let rc = idx.[ni * 2 + 1]
            if overlaps right.[ni] then
                if rc >= 0 then stack.Push rc else result.Add(-rc - 1)
    result.ToArray()

// Returns triangle indices whose AABB overlaps the query box (centroid-relative, conservative).
let private trianglesInBox (lm : LoadedMesh) (bMin : V3f) (bMax : V3f) =
    let qMin = V3d bMin
    let qMax = V3d bMax
    traverseBvh lm.bvh (fun b ->
        b.Min.X <= qMax.X && b.Max.X >= qMin.X &&
        b.Min.Y <= qMax.Y && b.Max.Y >= qMin.Y &&
        b.Min.Z <= qMax.Z && b.Max.Z >= qMin.Z)

// Returns triangle indices whose AABB overlaps the query sphere (squared-distance test).
let private trianglesInSphere (lm : LoadedMesh) (center : V3f) (radius : float32) =
    let c  = V3d center
    let r2 = float radius * float radius
    traverseBvh lm.bvh (fun b ->
        let dx = max 0.0 (max (b.Min.X - c.X) (c.X - b.Max.X))
        let dy = max 0.0 (max (b.Min.Y - c.Y) (c.Y - b.Max.Y))
        let dz = max 0.0 (max (b.Min.Z - c.Z) (c.Z - b.Max.Z))
        dx*dx + dy*dy + dz*dz <= r2)


// ── JSON request/response types ───────────────────────────────────────────────

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


// ── HTTP handlers ─────────────────────────────────────────────────────────────

// GET /centroids
let centroidsHandler : HttpHandler =
    fun next ctx -> task {
        let result = System.Collections.Generic.Dictionary<string, float[]>()
        for dir in Directory.GetDirectories(MeshLoader.dataRoot.Value) |> Array.sort do
            let name = Path.GetFileName dir
            match Directory.GetFiles(dir, "*_centroid.txt") |> Array.tryHead with
            | Some f ->
                let parts = File.ReadAllText(f).Trim().Split(' ')
                result.[name] <- [| float parts.[0]; float parts.[1]; float parts.[2] |]
            | None -> ()
        return! json result next ctx
    }

// GET /mesh/{name}
let meshCountHandler (name : string) : HttpHandler =
    fun next ctx -> task {
        let count = MeshLoader.meshCount name
        if count = 0 then return! RequestErrors.notFound (text $"not found: {name}") next ctx
        else            return! text (string count) next ctx
    }

// GET /mesh/{name}/{i}
let meshHandler (name : string, index : int) : HttpHandler =
    fun next ctx -> task {
        try
            let bytes = MeshLoader.serialize name index
            ctx.Response.ContentType <- "application/octet-stream"
            do! ctx.Response.Body.WriteAsync(bytes, 0, bytes.Length)
            return! next ctx
        with ex -> return! RequestErrors.notFound (text ex.Message) next ctx
    }

// GET /mesh/{name}/{i}/atlas
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

// POST /query/ray
// body: { name, index, origin:[x,y,z], direction:[x,y,z] }
// resp: { hit, t, point:[x,y,z], triangleId }  or  { hit:false }
let rayHandler : HttpHandler =
    fun next ctx -> task {
        let! req = ctx.BindJsonAsync<RayRequest>()
        let lm   = MeshCache.get req.Name req.Index
        let c    = lm.parsed.centroid
        let orig = V3f(toV3d req.Origin - c)
        let dir  = V3f(toV3d req.Direction)
        let mutable hit = RayHit()
        let ok = lm.scene.Intersect(orig, dir, &hit)
        if ok then
            let localHit = orig + dir * hit.T
            let worldHit = V3d localHit + c
            return! json {| hit = true; t = hit.T; point = fromV3d worldHit; triangleId = int hit.PrimitiveId |} next ctx
        else
            return! json {| hit = false |} next ctx
    }

// POST /query/closest
// body: { name, index, point:[x,y,z] }
// resp: { found, point:[x,y,z], distanceSquared, triangleId }  or  { found:false }
let closestHandler : HttpHandler =
    fun next ctx -> task {
        let! req = ctx.BindJsonAsync<ClosestRequest>()
        let lm   = MeshCache.get req.Name req.Index
        let c    = lm.parsed.centroid
        let qp   = V3f(toV3d req.Point - c)
        let res  = lm.scene.GetClosestPoint(qp)
        if res.IsValid then
            let worldPt = V3d res.Point + c
            return! json {| found = true; point = fromV3d worldPt; distanceSquared = res.DistanceSquared; triangleId = int res.PrimID |} next ctx
        else
            return! json {| found = false |} next ctx
    }

let private binaryIndices (tris : int[]) : HttpHandler =
    fun next ctx -> task {
        ctx.SetContentType "application/octet-stream"
        let len = tris.Length
        let buf = Array.zeroCreate<byte> (4 + len * 4)
        System.BitConverter.TryWriteBytes(buf.AsSpan(0, 4), len) |> ignore
        System.Buffer.BlockCopy(tris, 0, buf, 4, len * 4)
        ctx.Response.ContentLength <- Nullable<int64>(int64 buf.Length)
        do! ctx.Response.Body.WriteAsync(buf, 0, buf.Length)
        return Some ctx
    }

// POST /query/sphere
// body: { name, index, center:[x,y,z], radius }
// resp: binary  int32 count | int32[] indices
let sphereHandler : HttpHandler =
    fun next ctx -> task {
        let! req = ctx.BindJsonAsync<SphereRequest>()
        let lm   = MeshCache.get req.Name req.Index
        let c    = lm.parsed.centroid
        let lc   = V3f(toV3d req.Center - c)
        let tris = trianglesInSphere lm lc (float32 req.Radius)
        return! binaryIndices tris next ctx
    }

// POST /query/box
// body: { name, index, min:[x,y,z], max:[x,y,z] }
// resp: binary  int32 count | int32[] indices
let boxHandler : HttpHandler =
    fun next ctx -> task {
        let! req = ctx.BindJsonAsync<BoxRequest>()
        let lm   = MeshCache.get req.Name req.Index
        let c    = lm.parsed.centroid
        let lMin = V3f(toV3d req.Min - c)
        let lMax = V3f(toV3d req.Max - c)
        let tris = trianglesInBox lm lMin lMax
        return! binaryIndices tris next ctx
    }


// ── Routing ───────────────────────────────────────────────────────────────────

// GET  /centroids
// GET  /mesh/{name}
// GET  /mesh/{name}/{i}
// GET  /mesh/{name}/{i}/atlas
// POST /query/ray
// POST /query/closest
// POST /query/sphere
// POST /query/box

let webApp =
    choose [
        route "/"                       >=> text "hello"
        route "/centroids"              >=> centroidsHandler
        routef "/mesh/%s/%i/atlas"      atlasHandler
        routef "/mesh/%s/%i"            meshHandler
        routef "/mesh/%s"               meshCountHandler
        route "/query/ray"              >=> rayHandler
        route "/query/closest"          >=> closestHandler
        route "/query/sphere"           >=> sphereHandler
        route "/query/box"              >=> boxHandler
    ]

[<EntryPoint>]
let main args =
    Aardvark.Init()
    let builder = WebApplication.CreateBuilder(args)
    builder.Services.AddCors()    |> ignore
    builder.Services.AddGiraffe() |> ignore
    let app = builder.Build()
    app.UseCors(fun p -> p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader() |> ignore)
    app.UseGiraffe(webApp)
    app.Run()
    0
