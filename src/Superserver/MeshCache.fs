module MeshCache

open System
open System.Collections.Concurrent
open Aardvark.Base
open Aardvark.Embree

type LoadedMesh =
    {
        parsed   : MeshLoader.ParsedMesh
        device   : Device
        geometry : TriangleGeometry
        scene    : Scene
        bvh      : BbTree   // BVH over per-triangle AABBs (centroid-relative, double)
    }

let private cache = ConcurrentDictionary<struct(string * string * int), LoadedMesh>()

let get (dataset : string) (name : string) (index : int) : LoadedMesh =
    cache.GetOrAdd(struct(dataset, name, index), fun _ ->
        Log.line "loading mesh %s/%s/%d" dataset name index
        let pm     = MeshLoader.parseMesh dataset name index
        let device = new Device()
        let geom   = new TriangleGeometry(device, ReadOnlyMemory<V3f>(pm.positions), ReadOnlyMemory<int>(pm.indices), RTCBuildQuality.High)
        let scene  = new Scene(device, RTCBuildQuality.High, false)
        scene.AttachGeometry(geom) |> ignore
        scene.Commit()
        let triBoxes =
            let n = pm.indices.Length / 3
            Array.init n (fun ti ->
                let p0 = V3d pm.positions.[pm.indices.[ti * 3    ]]
                let p1 = V3d pm.positions.[pm.indices.[ti * 3 + 1]]
                let p2 = V3d pm.positions.[pm.indices.[ti * 3 + 2]]
                Box3d(Fun.Min(p0, Fun.Min(p1, p2)), Fun.Max(p0, Fun.Max(p1, p2)))
            )
        let bvh = BbTree(triBoxes, BbTree.BuildFlags.CreateBoxArrays)
        { parsed = pm; device = device; geometry = geom; scene = scene; bvh = bvh }
    )

// Traverse the BbTree, collecting primitive indices whose AABB passes the overlap test.
let traverseBvh (indices : int[]) (bbt : BbTree) (overlaps : Box3d -> bool) =
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
                if lc >= 0 then
                    stack.Push lc
                else
                    let tid = -lc - 1
                    result.Add(indices.[tid*3  ])
                    result.Add(indices.[tid*3+1])
                    result.Add(indices.[tid*3+2])
            let rc = idx.[ni * 2 + 1]
            if overlaps right.[ni] then
                if rc >= 0 then
                    stack.Push rc
                else
                    let tid = -rc - 1
                    result.Add(indices.[tid*3  ])
                    result.Add(indices.[tid*3+1])
                    result.Add(indices.[tid*3+2])
    result.ToArray()

// Returns triangle indices whose AABB overlaps the query box (centroid-relative, conservative).
let trianglesInBox (lm : LoadedMesh) (bMin : V3f) (bMax : V3f) =
    let qMin = V3d bMin
    let qMax = V3d bMax
    traverseBvh lm.parsed.indices lm.bvh (fun b ->
        b.Min.X <= qMax.X && b.Max.X >= qMin.X &&
        b.Min.Y <= qMax.Y && b.Max.Y >= qMin.Y &&
        b.Min.Z <= qMax.Z && b.Max.Z >= qMin.Z)

// Returns triangle indices whose AABB overlaps the query sphere (squared-distance test).
let trianglesInSphere (lm : LoadedMesh) (center : V3f) (radius : float32) =
    let c  = V3d center
    let r2 = float radius * float radius
    traverseBvh lm.parsed.indices lm.bvh (fun b ->
        let dx = max 0.0 (max (b.Min.X - c.X) (c.X - b.Max.X))
        let dy = max 0.0 (max (b.Min.Y - c.Y) (c.Y - b.Max.Y))
        let dz = max 0.0 (max (b.Min.Z - c.Z) (c.Z - b.Max.Z))
        dx*dx + dy*dy + dz*dz <= r2)

// Intersect a plane with mesh triangles. Returns 2D line segments projected onto (axisU, axisV) basis.
// All inputs in centroid-relative coords except axisU/axisV/planeNormal which are directions.
// maxExtentU/V clip the output to [-maxExtentU, maxExtentU] × [-maxExtentV, maxExtentV] in 2D.
let planeIntersection (lm : LoadedMesh) (planePoint : V3d) (planeNormal : V3d) (axisU : V3d) (axisV : V3d) (thickness : float) (maxExtentU : float) (maxExtentV : float) =
    let n = planeNormal |> Vec.normalize
    let boxHalf = axisU * maxExtentU + axisV * maxExtentV + n * thickness
    let slabMin = planePoint - V3d(abs boxHalf.X, abs boxHalf.Y, abs boxHalf.Z)
    let slabMax = planePoint + V3d(abs boxHalf.X, abs boxHalf.Y, abs boxHalf.Z)
    let bMin = V3f(Fun.Min(slabMin, slabMax))
    let bMax = V3f(Fun.Max(slabMin, slabMax))
    let vertIndices = trianglesInBox lm bMin bMax
    let segments = ResizeArray<float[]>()
    let triCount = vertIndices.Length / 3
    for ti in 0 .. triCount - 1 do
        let i0 = vertIndices.[ti * 3]
        let i1 = vertIndices.[ti * 3 + 1]
        let i2 = vertIndices.[ti * 3 + 2]
        let p0 = V3d lm.parsed.positions.[i0]
        let p1 = V3d lm.parsed.positions.[i1]
        let p2 = V3d lm.parsed.positions.[i2]
        let d0 = Vec.dot (p0 - planePoint) n
        let d1 = Vec.dot (p1 - planePoint) n
        let d2 = Vec.dot (p2 - planePoint) n
        let pts = ResizeArray<V3d>(2)
        let inline addEdge (pa : V3d) (da : float) (pb : V3d) (db : float) =
            if (da > 0.0) <> (db > 0.0) then
                let t = da / (da - db)
                pts.Add(pa + t * (pb - pa))
        addEdge p0 d0 p1 d1
        addEdge p1 d1 p2 d2
        addEdge p2 d2 p0 d0
        if pts.Count >= 2 then
            let a = pts.[0]
            let b = pts.[1]
            let u0 = Vec.dot (a - planePoint) axisU
            let v0 = Vec.dot (a - planePoint) axisV
            let u1 = Vec.dot (b - planePoint) axisU
            let v1 = Vec.dot (b - planePoint) axisV
            if (abs u0 <= maxExtentU || abs u1 <= maxExtentU) && (abs v0 <= maxExtentV || abs v1 <= maxExtentV) then
                segments.Add [| u0; v0; u1; v1 |]
    segments.ToArray()

// Statistics helpers
type GridCellStats = { Average: float; Q1: float; Q3: float; Min: float; Max: float; Variance: float }
type DatasetStats = { MeshName: string; ZMin: float; ZQ1: float; ZMedian: float; ZQ3: float; ZMax: float; ZVariance: float }
type GridEvalResult = { Resolution: int; Cells: (int * int * GridCellStats)[]; DatasetStats: DatasetStats[] }

let private percentile (sorted : float[]) (p : float) =
    if sorted.Length = 0 then nan
    elif sorted.Length = 1 then sorted.[0]
    else
        let idx = p * float (sorted.Length - 1)
        let lo = int (floor idx)
        let hi = min (lo + 1) (sorted.Length - 1)
        let f = idx - float lo
        sorted.[lo] * (1.0 - f) + sorted.[hi] * f

let private computeStats (values : float[]) =
    if values.Length = 0 then None
    else
        let sorted = values |> Array.sort
        let avg = values |> Array.average
        let var = if values.Length > 1 then values |> Array.sumBy (fun v -> (v - avg) * (v - avg)) |> fun s -> s / float (values.Length - 1) else 0.0
        Some { Average = avg; Q1 = percentile sorted 0.25; Q3 = percentile sorted 0.75; Min = sorted.[0]; Max = sorted.[sorted.Length - 1]; Variance = var }

// Evaluate all meshes in a dataset on a regular grid within a core sample prism.
let evaluateGrid (dataset : string) (anchor : V3d) (axis : V3d) (radius : float) (resolution : int) (extFwd : float) (extBack : float) : GridEvalResult =
    let axis = axis |> Vec.normalize
    let up = if abs axis.Z > 0.9 then V3d.OIO else V3d.OOI
    let right = Vec.cross axis up |> Vec.normalize
    let fwd = Vec.cross right axis |> Vec.normalize
    let rayDir = V3f axis
    let rayLen = float32 (extFwd + extBack)

    let meshNames = MeshLoader.meshNames dataset
    let meshParts =
        meshNames |> Array.collect (fun name ->
            let count = MeshLoader.meshCount dataset name
            [| for i in 0 .. count - 1 -> name, i, get dataset name i |])

    let cellSize = 2.0 * radius / float resolution
    let r2 = radius * radius

    // Per-grid-cell: collect heights from all mesh parts
    let cells = ResizeArray<int * int * GridCellStats>()
    // Per-dataset: collect all heights across grid
    let perDatasetHeights = meshNames |> Array.map (fun _ -> ResizeArray<float>())
    let meshNameIndex = meshNames |> Array.mapi (fun i n -> n, i) |> Map.ofArray

    for gu in 0 .. resolution - 1 do
        for gv in 0 .. resolution - 1 do
            let u = -radius + (float gu + 0.5) * cellSize
            let v = -radius + (float gv + 0.5) * cellSize
            if u * u + v * v <= r2 then
                let rayOriginWorld = anchor + right * u + fwd * v - axis * extBack
                let allHeights = ResizeArray<float>()
                for name, _partIdx, lm in meshParts do
                    let c = lm.parsed.centroid
                    let orig = V3f(rayOriginWorld - c)
                    let mutable hit = RayHit()
                    if lm.scene.Intersect(orig, rayDir, &hit) && hit.T <= rayLen then
                        let h = float hit.T - extBack
                        allHeights.Add h
                        let di = meshNameIndex.[name]
                        perDatasetHeights.[di].Add h
                match computeStats (allHeights.ToArray()) with
                | Some stats -> cells.Add(gu, gv, stats)
                | None -> ()

    let dsStats =
        meshNames |> Array.mapi (fun i name ->
            let vals = perDatasetHeights.[i].ToArray()
            if vals.Length = 0 then
                { MeshName = name; ZMin = nan; ZQ1 = nan; ZMedian = nan; ZQ3 = nan; ZMax = nan; ZVariance = nan }
            else
                let sorted = vals |> Array.sort
                let avg = vals |> Array.average
                let var = if vals.Length > 1 then vals |> Array.sumBy (fun v -> (v - avg) * (v - avg)) |> fun s -> s / float (vals.Length - 1) else 0.0
                { MeshName = name; ZMin = sorted.[0]; ZQ1 = percentile sorted 0.25; ZMedian = percentile sorted 0.5; ZQ3 = percentile sorted 0.75; ZMax = sorted.[sorted.Length - 1]; ZVariance = var })

    { Resolution = resolution; Cells = cells.ToArray(); DatasetStats = dsStats }
