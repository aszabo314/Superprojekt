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

let private cache = ConcurrentDictionary<struct(string * int), LoadedMesh>()

let get (name : string) (index : int) : LoadedMesh =
    cache.GetOrAdd(struct(name, index), fun _ ->
        Log.line "loading mesh %s/%d" name index
        let pm     = MeshLoader.parseMesh name index
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
