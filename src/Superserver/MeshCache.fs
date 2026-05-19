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
        bvh      : BbTree
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

let trianglesInBox (lm : LoadedMesh) (bMin : V3f) (bMax : V3f) =
    let qMin = V3d bMin
    let qMax = V3d bMax
    traverseBvh lm.parsed.indices lm.bvh (fun b ->
        b.Min.X <= qMax.X && b.Max.X >= qMin.X &&
        b.Min.Y <= qMax.Y && b.Max.Y >= qMin.Y &&
        b.Min.Z <= qMax.Z && b.Max.Z >= qMin.Z)

let trianglesInSphere (lm : LoadedMesh) (center : V3f) (radius : float32) =
    let c  = V3d center
    let r2 = float radius * float radius
    traverseBvh lm.parsed.indices lm.bvh (fun b ->
        let dx = max 0.0 (max (b.Min.X - c.X) (c.X - b.Max.X))
        let dy = max 0.0 (max (b.Min.Y - c.Y) (c.Y - b.Max.Y))
        let dz = max 0.0 (max (b.Min.Z - c.Z) (c.Z - b.Max.Z))
        dx*dx + dy*dy + dz*dz <= r2)

let planeIntersection (lm : LoadedMesh) (planePoint : V3d) (planeNormal : V3d) (axisU : V3d) (axisV : V3d) (thickness : float) (maxExtentU : float) (maxExtentV : float) =
    let n = planeNormal |> Vec.normalize
    let tileTarget = 8.0
    let nU = max 1 (min 8 (int (ceil (maxExtentU * 2.0 / tileTarget))))
    let nV = max 1 (min 8 (int (ceil (maxExtentV * 2.0 / tileTarget))))
    let tileHalfU = maxExtentU / float nU
    let tileHalfV = maxExtentV / float nV
    let perTile = Array.init (nU * nV) (fun _ -> ResizeArray<float[]>())
    let tileOpts = System.Threading.Tasks.ParallelOptions(MaxDegreeOfParallelism = 4)
    System.Threading.Tasks.Parallel.For(0, nU * nV, tileOpts, fun ti ->
        let iu = ti % nU
        let iv = ti / nU
        let uC = -maxExtentU + (float iu * 2.0 + 1.0) * tileHalfU
        let vC = -maxExtentV + (float iv * 2.0 + 1.0) * tileHalfV
        let uLo = uC - tileHalfU
        let uHi = uC + tileHalfU
        let vLo = vC - tileHalfV
        let vHi = vC + tileHalfV
        let tilePlanePoint = planePoint + axisU * uC + axisV * vC
        let hx = abs axisU.X * tileHalfU + abs axisV.X * tileHalfV + abs n.X * thickness
        let hy = abs axisU.Y * tileHalfU + abs axisV.Y * tileHalfV + abs n.Y * thickness
        let hz = abs axisU.Z * tileHalfU + abs axisV.Z * tileHalfV + abs n.Z * thickness
        let boxHalf = V3d(hx, hy, hz)
        let slabMin = tilePlanePoint - boxHalf
        let slabMax = tilePlanePoint + boxHalf
        let bMin = V3f(Fun.Min(slabMin, slabMax))
        let bMax = V3f(Fun.Max(slabMin, slabMax))
        let vertIndices = trianglesInBox lm bMin bMax
        let local = perTile.[ti]
        let triCount = vertIndices.Length / 3
        for tix in 0 .. triCount - 1 do
            let i0 = vertIndices.[tix * 3]
            let i1 = vertIndices.[tix * 3 + 1]
            let i2 = vertIndices.[tix * 3 + 2]
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
                    let uM = 0.5 * (u0 + u1)
                    let vM = 0.5 * (v0 + v1)
                    let inU = (uM >= uLo || iu = 0) && (uM < uHi || iu = nU - 1)
                    let inV = (vM >= vLo || iv = 0) && (vM < vHi || iv = nV - 1)
                    if inU && inV then local.Add [| u0; v0; u1; v1 |]) |> ignore
    let total = perTile |> Array.sumBy (fun b -> b.Count)
    let out = Array.zeroCreate<float[]> total
    let mutable off = 0
    for b in perTile do
        b.CopyTo(out, off)
        off <- off + b.Count
    out

