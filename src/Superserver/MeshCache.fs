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
//
// For large cut planes, the slab AABB can return tens to hundreds of thousands of triangles and the
// per-triangle plane test runs serially. We tile the plane into a (nU × nV) grid, run each tile's
// BVH query + plane test in parallel, and deduplicate by emitting each segment from the tile that
// contains its 2D midpoint.
let planeIntersection (lm : LoadedMesh) (planePoint : V3d) (planeNormal : V3d) (axisU : V3d) (axisV : V3d) (thickness : float) (maxExtentU : float) (maxExtentV : float) =
    let n = planeNormal |> Vec.normalize
    // Tile target ~8 m per edge; cap at 8×8. Small cuts fall through to a single-tile pass with no overhead.
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
                    // Dedup: emit only if the 2D midpoint lies in this tile. Outer-edge tiles claim
                    // their open side unconditionally so segments whose midpoint spills past the
                    // global [-maxExtent, +maxExtent] aren't dropped.
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

// V6 §D.7.2 — elevation-isoline marching. For each triangle with vertex Z
// values straddling `elevation` (world-space), compute the segment where
// the triangle crosses the horizontal plane. Segments share endpoints
// via mesh edges, so we key intersection points by edge id and link the
// per-triangle pair to build adjacency. A connected-component walk
// produces polylines; the result is the polyline whose closest point to
// `seed` is nearest (preferring longer lines on ties).
//
// Output: world-space polyline as float[] of x0,y0,z0,x1,y1,z1,...
let isoline (lm : LoadedMesh) (elevation : float) (seed : V3d) (maxPoints : int) : float[] =
    let positions = lm.parsed.positions
    let centroid = lm.parsed.centroid
    let indices = lm.parsed.indices
    let triCount = indices.Length / 3
    let elevLocal = float32 (elevation - centroid.Z)

    let inline edgeKey (i0 : int) (i1 : int) : int64 =
        let a = min i0 i1
        let b = max i0 i1
        (int64 a <<< 32) ||| int64 b

    let edgePoints = System.Collections.Generic.Dictionary<int64, V3d>()
    let inline tryAddEdge (i0 : int) (i1 : int) (out : ResizeArray<int64>) =
        let key = edgeKey i0 i1
        let mutable existing = Unchecked.defaultof<V3d>
        if edgePoints.TryGetValue(key, &existing) then out.Add key
        else
            let p0 = positions.[i0]
            let p1 = positions.[i1]
            let d0 = p0.Z - elevLocal
            let d1 = p1.Z - elevLocal
            if (d0 > 0.0f) <> (d1 > 0.0f) then
                let t = float d0 / float (d0 - d1)
                let pt = V3d p0 + t * (V3d p1 - V3d p0)
                edgePoints.[key] <- pt
                out.Add key

    let adj = System.Collections.Generic.Dictionary<int64, int64 * int64>()
    let inline addAdj (a : int64) (b : int64) =
        let mutable existing = Unchecked.defaultof<int64 * int64>
        if adj.TryGetValue(a, &existing) then
            let (x, y) = existing
            if y = -1L then adj.[a] <- (x, b)
        else
            adj.[a] <- (b, -1L)

    let scratch = ResizeArray<int64>(3)
    for ti in 0 .. triCount - 1 do
        let i0 = indices.[ti * 3]
        let i1 = indices.[ti * 3 + 1]
        let i2 = indices.[ti * 3 + 2]
        scratch.Clear()
        tryAddEdge i0 i1 scratch
        tryAddEdge i1 i2 scratch
        tryAddEdge i2 i0 scratch
        if scratch.Count = 2 then
            addAdj scratch.[0] scratch.[1]
            addAdj scratch.[1] scratch.[0]

    if edgePoints.Count = 0 then [||]
    else
        let visited = System.Collections.Generic.HashSet<int64>()
        let polylines = ResizeArray<V3d[]>()

        let walkFrom (start : int64) (avoid : int64) =
            let acc = ResizeArray<int64>()
            acc.Add start
            visited.Add start |> ignore
            let mutable last = avoid
            let mutable cur = start
            let mutable keepGoing = true
            while keepGoing do
                let mutable nbrs = Unchecked.defaultof<int64 * int64>
                if adj.TryGetValue(cur, &nbrs) then
                    let (a, b) = nbrs
                    let nxt =
                        if a <> last && a <> -1L && not (visited.Contains a) then a
                        elif b <> last && b <> -1L && not (visited.Contains b) then b
                        else -1L
                    if nxt = -1L then keepGoing <- false
                    else
                        acc.Add nxt
                        visited.Add nxt |> ignore
                        last <- cur
                        cur <- nxt
                else keepGoing <- false
            acc

        let allKeys = adj.Keys |> Seq.toArray
        for start in allKeys do
            if not (visited.Contains start) then
                let (a, b) = adj.[start]
                let forward = walkFrom start -1L
                let backward =
                    if forward.Count >= 2 then
                        let secondNeighbor = if forward.[1] = a then b else a
                        if secondNeighbor = -1L || visited.Contains secondNeighbor then
                            ResizeArray<int64>()
                        else
                            walkFrom secondNeighbor start
                    else ResizeArray<int64>()
                let combined = ResizeArray<int64>(forward.Count + backward.Count)
                for i in backward.Count - 1 .. -1 .. 0 do combined.Add backward.[i]
                for k in forward do combined.Add k
                let pts = combined |> Seq.map (fun k -> edgePoints.[k] + centroid) |> Array.ofSeq
                polylines.Add pts

        if polylines.Count = 0 then [||]
        else
            let scored =
                polylines |> Seq.map (fun pts ->
                    let mutable minD2 = System.Double.MaxValue
                    for p in pts do
                        let d2 = (p - seed).LengthSquared
                        if d2 < minD2 then minD2 <- d2
                    let mutable len = 0.0
                    for i in 1 .. pts.Length - 1 do
                        len <- len + (pts.[i] - pts.[i - 1]).Length
                    pts, minD2, len)
                |> Array.ofSeq
            // Prefer the line whose nearest point is closest to the seed; on
            // near-ties favour the longer line.
            let chosen, _, _ =
                scored |> Array.maxBy (fun (_, d2, len) -> len / (1.0 + d2 * 0.5))
            let n = min chosen.Length maxPoints
            let out = Array.zeroCreate<float> (n * 3)
            for i in 0 .. n - 1 do
                out.[i * 3]     <- chosen.[i].X
                out.[i * 3 + 1] <- chosen.[i].Y
                out.[i * 3 + 2] <- chosen.[i].Z
            out

// V6 §D.7.2 — curvature-ridge tracing via dihedral angles. Per-edge ridge
// classification: for any mesh edge with two adjacent triangles, the
// dihedral angle is the angle between the two triangle normals; edges
// whose dihedral exceeds `thresholdRad` are "ridge edges". Connected
// ridge edges (linked by shared vertices) form polylines; we walk the
// component nearest the seed.
// Returns (worldPolyline, perVertexDihedral) — dihedral in radians, sampled
// as the largest ridge-edge angle incident to each polyline vertex (so the
// scalar trace in the card plot peaks at sharp corners along the ridge).
let curvatureRidgeWithScalars (lm : LoadedMesh) (seed : V3d) (thresholdRad : float) (maxPoints : int) : float[] * float[] =
    let positions = lm.parsed.positions
    let centroid = lm.parsed.centroid
    let indices = lm.parsed.indices
    let triCount = indices.Length / 3

    let inline edgeKey (a : int) (b : int) =
        let lo = min a b
        let hi = max a b
        (int64 lo <<< 32) ||| int64 hi

    // edge → (tri0, tri1); tri1 = -1 for boundary edges.
    let edgeTris = System.Collections.Generic.Dictionary<int64, int * int>()
    let inline addTri (a : int) (b : int) (ti : int) =
        let k = edgeKey a b
        let mutable existing = (0, 0)
        if edgeTris.TryGetValue(k, &existing) then
            let (t1, _) = existing
            edgeTris.[k] <- (t1, ti)
        else
            edgeTris.[k] <- (ti, -1)

    for ti in 0 .. triCount - 1 do
        let i0 = indices.[ti * 3]
        let i1 = indices.[ti * 3 + 1]
        let i2 = indices.[ti * 3 + 2]
        addTri i0 i1 ti
        addTri i1 i2 ti
        addTri i2 i0 ti

    // Triangle normals (local mesh space; sign doesn't matter since we
    // compare angle magnitudes via abs(dot)).
    let triNormals = Array.zeroCreate<V3d> triCount
    for ti in 0 .. triCount - 1 do
        let p0 = V3d positions.[indices.[ti * 3]]
        let p1 = V3d positions.[indices.[ti * 3 + 1]]
        let p2 = V3d positions.[indices.[ti * 3 + 2]]
        let n = Vec.cross (p1 - p0) (p2 - p0)
        let l = n.Length
        triNormals.[ti] <- if l > 1e-12 then n / l else V3d.OOI

    // Vertex adjacency through ridge edges. Each vertex stores up to two
    // neighbours (matching the isoline graph shape); higher-valence
    // junctions just keep the first two we see — the walk simply stops
    // at the junction, which is acceptable for V6's prototype.
    let cosT = cos thresholdRad
    let vertAdj = System.Collections.Generic.Dictionary<int, int * int>()
    let inline addAdj (a : int) (b : int) =
        let mutable existing = (0, -1)
        if vertAdj.TryGetValue(a, &existing) then
            let (x, y) = existing
            if y = -1 then vertAdj.[a] <- (x, b)
        else
            vertAdj.[a] <- (b, -1)

    let ridgeVerts = System.Collections.Generic.HashSet<int>()
    // Per-vertex peak dihedral over incident ridge edges, in radians.
    let vertDihedral = System.Collections.Generic.Dictionary<int, float>()
    let inline bumpDihedral (v : int) (d : float) =
        let mutable existing = 0.0
        if vertDihedral.TryGetValue(v, &existing) then
            if d > existing then vertDihedral.[v] <- d
        else vertDihedral.[v] <- d
    for kv in edgeTris do
        let (t1, t2) = kv.Value
        if t2 >= 0 then
            let dot = abs (Vec.dot triNormals.[t1] triNormals.[t2])
            if dot < cosT then
                let key = kv.Key
                let v0 = int (key >>> 32)
                let v1 = int (key &&& 0xFFFFFFFFL)
                addAdj v0 v1
                addAdj v1 v0
                ridgeVerts.Add v0 |> ignore
                ridgeVerts.Add v1 |> ignore
                let dihedral = acos (min 1.0 (max -1.0 dot))
                bumpDihedral v0 dihedral
                bumpDihedral v1 dihedral

    if ridgeVerts.Count = 0 then [||], [||]
    else
        let visited = System.Collections.Generic.HashSet<int>()
        let polylines = ResizeArray<V3d[] * float[]>()

        let walkFrom (start : int) (avoid : int) =
            let acc = ResizeArray<int>()
            acc.Add start
            visited.Add start |> ignore
            let mutable last = avoid
            let mutable cur = start
            let mutable keep = true
            while keep do
                let mutable n = (0, -1)
                if vertAdj.TryGetValue(cur, &n) then
                    let (a, b) = n
                    let nxt =
                        if a <> last && a <> -1 && not (visited.Contains a) then a
                        elif b <> last && b <> -1 && not (visited.Contains b) then b
                        else -1
                    if nxt = -1 then keep <- false
                    else
                        acc.Add nxt
                        visited.Add nxt |> ignore
                        last <- cur
                        cur <- nxt
                else keep <- false
            acc

        for start in Array.ofSeq vertAdj.Keys do
            if not (visited.Contains start) then
                let (a, b) = vertAdj.[start]
                let forward = walkFrom start -1
                let backward =
                    if forward.Count >= 2 then
                        let second = if forward.[1] = a then b else a
                        if second = -1 || visited.Contains second then ResizeArray<int>()
                        else walkFrom second start
                    else ResizeArray<int>()
                let combined = ResizeArray<int>(forward.Count + backward.Count)
                for i in backward.Count - 1 .. -1 .. 0 do combined.Add backward.[i]
                for k in forward do combined.Add k
                let pts =
                    combined |> Seq.map (fun vi -> V3d positions.[vi] + centroid) |> Array.ofSeq
                let scalars =
                    combined |> Seq.map (fun vi ->
                        let mutable v = 0.0
                        if vertDihedral.TryGetValue(vi, &v) then v else 0.0)
                    |> Array.ofSeq
                polylines.Add (pts, scalars)

        if polylines.Count = 0 then [||], [||]
        else
            let scored =
                polylines |> Seq.map (fun (pts, sc) ->
                    let mutable minD2 = System.Double.MaxValue
                    for p in pts do
                        let d2 = (p - seed).LengthSquared
                        if d2 < minD2 then minD2 <- d2
                    let mutable len = 0.0
                    for i in 1 .. pts.Length - 1 do
                        len <- len + (pts.[i] - pts.[i - 1]).Length
                    pts, sc, minD2, len)
                |> Array.ofSeq
            let chosenPts, chosenSc, _, _ =
                scored |> Array.maxBy (fun (_, _, d2, len) -> len / (1.0 + d2 * 0.5))
            let n = min chosenPts.Length maxPoints
            let outPts = Array.zeroCreate<float> (n * 3)
            let outSc  = Array.zeroCreate<float> n
            for i in 0 .. n - 1 do
                outPts.[i * 3]     <- chosenPts.[i].X
                outPts.[i * 3 + 1] <- chosenPts.[i].Y
                outPts.[i * 3 + 2] <- chosenPts.[i].Z
                outSc.[i] <- chosenSc.[i]
            outPts, outSc

let curvatureRidge (lm : LoadedMesh) (seed : V3d) (thresholdRad : float) (maxPoints : int) : float[] =
    let pts, _ = curvatureRidgeWithScalars lm seed thresholdRad maxPoints
    pts

// V6 §D.7.3 — azimuthal-equidistant patch projection. Walks vertices
// within `radius` of `centre` on the mesh surface (Dijkstra-on-the-edge-
// graph for geodesic distance), then projects each vertex into 2D patch
// coordinates `(d * cos bearing, d * sin bearing)` with `bearing` measured
// from world +Y projected into the local tangent plane.
//
// Result: per-vertex (px, py, world x, world y, world z) plus the tangent
// plane's reference direction `refDir` in world space (for the compass
// rose). Output is capped at `maxPoints` via uniform stride sampling.
type PatchPoint = { Px : float; Py : float; Wx : float; Wy : float; Wz : float }
type PatchResult = { Points : PatchPoint[]; RefDirWorld : V3d; NormalWorld : V3d }

let patch (lm : LoadedMesh) (centre : V3d) (radius : float) (maxPoints : int) : PatchResult =
    let positions = lm.parsed.positions
    let centroid = lm.parsed.centroid
    let centreLocal = centre - centroid

    // BVH sphere query — fetch every triangle whose AABB intersects the
    // (slightly padded) sphere around centre.
    let triBuf =
        trianglesInSphere lm (V3f centreLocal) (float32 (radius * 1.2))

    if triBuf.Length = 0 then
        { Points = [||]; RefDirWorld = V3d.OIO; NormalWorld = V3d.OOI }
    else
        let triCount = triBuf.Length / 3

        // Average triangle normal — used as the local tangent-plane normal
        // at the centre. Good enough for prototype patches; the spec calls
        // for "mesh normal at centre" which on a discrete mesh is just a
        // local average anyway.
        let mutable nSum = V3d.Zero
        for ti in 0 .. triCount - 1 do
            let p0 = V3d positions.[triBuf.[ti * 3]]
            let p1 = V3d positions.[triBuf.[ti * 3 + 1]]
            let p2 = V3d positions.[triBuf.[ti * 3 + 2]]
            nSum <- nSum + Vec.cross (p1 - p0) (p2 - p0)
        let normal =
            if nSum.Length > 1e-9 then Vec.normalize nSum else V3d.OOI
        let worldNorth = V3d.OIO
        let projN = worldNorth - normal * Vec.dot worldNorth normal
        let refDir =
            if projN.Length > 1e-9 then Vec.normalize projN
            else
                let projX = V3d.IOO - normal * Vec.dot V3d.IOO normal
                if projX.Length > 1e-9 then Vec.normalize projX else V3d.IOO
        let leftDir = Vec.cross normal refDir

        // Vertex adjacency restricted to the triangles in the sphere.
        let adj = System.Collections.Generic.Dictionary<int, ResizeArray<int>>()
        let inline addEdge a b =
            let mutable l = Unchecked.defaultof<ResizeArray<int>>
            if adj.TryGetValue(a, &l) then ()
            else
                l <- ResizeArray<int>()
                adj.[a] <- l
            if not (l.Contains b) then l.Add b
        for ti in 0 .. triCount - 1 do
            let i0 = triBuf.[ti * 3]
            let i1 = triBuf.[ti * 3 + 1]
            let i2 = triBuf.[ti * 3 + 2]
            addEdge i0 i1
            addEdge i1 i0
            addEdge i1 i2
            addEdge i2 i1
            addEdge i2 i0
            addEdge i0 i2

        // Seed Dijkstra at the vertex closest to the centre.
        let mutable seedV = -1
        let mutable seedD2 = System.Double.MaxValue
        for v in adj.Keys do
            let d2 = (V3d positions.[v] - centreLocal).LengthSquared
            if d2 < seedD2 then seedD2 <- d2; seedV <- v

        if seedV < 0 then
            { Points = [||]; RefDirWorld = refDir; NormalWorld = normal }
        else
            let dist = System.Collections.Generic.Dictionary<int, float>()
            let pq = System.Collections.Generic.PriorityQueue<int, float>()
            dist.[seedV] <- 0.0
            pq.Enqueue(seedV, 0.0)
            while pq.Count > 0 do
                let v = pq.Dequeue()
                let mutable d = 0.0
                if dist.TryGetValue(v, &d) then
                    if d <= radius then
                        let mutable nbrs = Unchecked.defaultof<ResizeArray<int>>
                        if adj.TryGetValue(v, &nbrs) then
                            let vp = V3d positions.[v]
                            for n in nbrs do
                                let np = V3d positions.[n]
                                let alt = d + (np - vp).Length
                                if alt <= radius then
                                    let mutable cur = System.Double.MaxValue
                                    let has = dist.TryGetValue(n, &cur)
                                    if not has || alt < cur then
                                        dist.[n] <- alt
                                        pq.Enqueue(n, alt)

            // Project each reached vertex into patch space.
            let out = ResizeArray<PatchPoint>(dist.Count)
            for kv in dist do
                let v = kv.Key
                let d = kv.Value
                let vp = V3d positions.[v]
                let dv = vp - centreLocal
                let dvTan = dv - normal * Vec.dot dv normal
                let world = vp + centroid
                let x = Vec.dot dvTan refDir
                let y = Vec.dot dvTan leftDir
                let bearing = atan2 y x
                let px = d * cos bearing
                let py = d * sin bearing
                out.Add { Px = px; Py = py; Wx = world.X; Wy = world.Y; Wz = world.Z }

            let pts = out.ToArray()
            // Down-sample if too many for the JSON payload / card render.
            let final =
                if pts.Length <= maxPoints then pts
                else
                    let stride = pts.Length / maxPoints
                    let n = pts.Length / stride
                    Array.init n (fun i -> pts.[i * stride])
            { Points = final; RefDirWorld = refDir; NormalWorld = normal }

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

// Cast rays on concentric cylinder surfaces at regular angular intervals.
// Returns per-ring, per-angle, per-mesh intersection heights along the prism axis.
// Rings are provided outer-to-inner; index 0 is the prism wall.
type CylinderEvalHit = { Ring: int; Angle: int; MeshName: string; Height: float }
type CylinderEvalResult = { AngularResolution: int; RingCount: int; Hits: CylinderEvalHit[] }

let cylinderEval (dataset : string) (anchor : V3d) (axis : V3d) (radii : float[]) (angularRes : int) (extFwd : float) (extBack : float) : CylinderEvalResult =
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

    let eps = 1.0e-4f
    let perRing = Array.init radii.Length (fun _ -> ResizeArray<CylinderEvalHit>())
    System.Threading.Tasks.Parallel.For(0, radii.Length, fun ri ->
        let radius = radii.[ri]
        let local = perRing.[ri]
        for ai in 0 .. angularRes - 1 do
            let angle = float ai / float angularRes * Math.PI * 2.0
            let dir = right * cos angle + fwd * sin angle
            let rayOriginWorld = anchor + dir * radius - axis * extBack
            for name, _partIdx, lm in meshParts do
                let c = lm.parsed.centroid
                let baseOrig = V3f(rayOriginWorld - c)
                let mutable tOffset = 0.0f
                let mutable keep = true
                while keep do
                    let orig = baseOrig + rayDir * tOffset
                    let mutable hit = RayHit()
                    if lm.scene.Intersect(orig, rayDir, &hit) && tOffset + hit.T <= rayLen then
                        let h = float (tOffset + hit.T) - float extBack
                        local.Add { Ring = ri; Angle = ai; MeshName = name; Height = h }
                        tOffset <- tOffset + hit.T + eps
                    else
                        keep <- false) |> ignore
    let hits =
        let total = perRing |> Array.sumBy (fun b -> b.Count)
        let arr = Array.zeroCreate<CylinderEvalHit> total
        let mutable off = 0
        for b in perRing do
            b.CopyTo(arr, off)
            off <- off + b.Count
        arr

    { AngularResolution = angularRes; RingCount = radii.Length; Hits = hits }


// V6 §D.8 — point-to-point ICP between two meshes. Uses the
// small-rotation linearisation (R ≈ I + [ω]×) per iteration so we only
// solve a 6×6 linear system instead of an SVD; many iterations
// recover the full rotation. Optional per-correspondence weights enter
// the normal equations directly (for §D.8 region-restricted mode the
// caller passes anchor Gaussian weights).
type IcpResult = {
    Transform     : M44d         // final rigid transform on the moving mesh, world-space
    Convergence   : float[]      // per-iteration RMS residual (metres)
    Residuals     : float[]      // per-correspondence residual at the final iteration
}

[<AutoOpen>]
module private IcpMath =
    let inline skew (v : V3d) =
        M33d(
            0.0, -v.Z, v.Y,
            v.Z, 0.0, -v.X,
            -v.Y, v.X, 0.0)

    /// Solve a small dense linear system via Gauss elimination with
    /// partial pivoting. Returns Some x or None when singular.
    let solveDense (a : float[,]) (b : float[]) : float[] option =
        let n = Array2D.length1 a
        let A = Array2D.copy a
        let B = Array.copy b
        let mutable singular = false
        for k in 0 .. n - 1 do
            if not singular then
                let mutable piv = k
                for i in k + 1 .. n - 1 do
                    if abs A.[i, k] > abs A.[piv, k] then piv <- i
                if piv <> k then
                    for j in 0 .. n - 1 do
                        let tmp = A.[k, j]
                        A.[k, j] <- A.[piv, j]
                        A.[piv, j] <- tmp
                    let tmp = B.[k]
                    B.[k] <- B.[piv]
                    B.[piv] <- tmp
                if abs A.[k, k] < 1e-12 then singular <- true
                else
                    for i in k + 1 .. n - 1 do
                        let factor = A.[i, k] / A.[k, k]
                        for j in k .. n - 1 do
                            A.[i, j] <- A.[i, j] - factor * A.[k, j]
                        B.[i] <- B.[i] - factor * B.[k]
        if singular then None
        else
            let x = Array.zeroCreate<float> n
            for i in n - 1 .. -1 .. 0 do
                let mutable s = B.[i]
                for j in i + 1 .. n - 1 do
                    s <- s - A.[i, j] * x.[j]
                x.[i] <- s / A.[i, i]
            Some x

    /// Rodrigues exponential map: ω (axis-angle) → rotation matrix.
    let rotFromOmega (omega : V3d) =
        let theta = omega.Length
        if theta < 1e-12 then M33d.Identity
        else
            let k = omega / theta
            let K = skew k
            M33d.Identity + K * sin theta + K * K * (1.0 - cos theta)

    /// Per-iteration ICP step. Returns (R_delta, t_delta, rms).
    let icpStep (pairs : ResizeArray<struct (V3d * V3d * float)>) =
        let A = Array2D.zeroCreate<float> 6 6
        let B = Array.zeroCreate<float> 6
        let mutable rmsSum = 0.0
        let mutable wSum = 0.0
        let J = Array2D.zeroCreate<float> 3 6
        for i in 0 .. pairs.Count - 1 do
            let struct (ai, bi, wi) = pairs.[i]
            let r0 = ai.X - bi.X
            let r1 = ai.Y - bi.Y
            let r2 = ai.Z - bi.Z
            // J = [-[a]× | I_3]; row form:
            //   [  0,   a.Z, -a.Y, 1, 0, 0 ]
            //   [-a.Z,  0,    a.X, 0, 1, 0 ]
            //   [ a.Y, -a.X,  0,   0, 0, 1 ]
            J.[0, 0] <- 0.0;     J.[0, 1] <- ai.Z;   J.[0, 2] <- -ai.Y; J.[0, 3] <- 1.0; J.[0, 4] <- 0.0; J.[0, 5] <- 0.0
            J.[1, 0] <- -ai.Z;   J.[1, 1] <- 0.0;    J.[1, 2] <-  ai.X; J.[1, 3] <- 0.0; J.[1, 4] <- 1.0; J.[1, 5] <- 0.0
            J.[2, 0] <-  ai.Y;   J.[2, 1] <- -ai.X;  J.[2, 2] <-  0.0;  J.[2, 3] <- 0.0; J.[2, 4] <- 0.0; J.[2, 5] <- 1.0
            for a in 0 .. 5 do
                for b in 0 .. 5 do
                    A.[a, b] <- A.[a, b] + wi * (J.[0, a] * J.[0, b] + J.[1, a] * J.[1, b] + J.[2, a] * J.[2, b])
                B.[a] <- B.[a] - wi * (J.[0, a] * r0 + J.[1, a] * r1 + J.[2, a] * r2)
            rmsSum <- rmsSum + wi * (r0 * r0 + r1 * r1 + r2 * r2)
            wSum <- wSum + wi
        let x =
            match solveDense A B with
            | Some xs -> xs
            | None -> [| 0.0; 0.0; 0.0; 0.0; 0.0; 0.0 |]
        let omega = V3d(x.[0], x.[1], x.[2])
        let t = V3d(x.[3], x.[4], x.[5])
        let R = rotFromOmega omega
        R, t, sqrt (rmsSum / max 1.0 wSum)

/// Run point-to-point ICP. `initial` is the moving mesh's starting
/// world-space transform; `anchorWeights`, if Some, gives a per-vertex
/// Gaussian weight (>= 0) for region-restricted mode — pairs with weight
/// below `regionEps` are skipped entirely.
///
/// `sampleStride` controls how aggressively the moving mesh is
/// subsampled (every N-th vertex); higher = faster + noisier.
let runIcp
        (lmRef : LoadedMesh) (lmMov : LoadedMesh)
        (initial : M44d) (sampleStride : int) (maxIter : int)
        (anchorWeights : (V3d -> float) option) (regionEps : float)
        : IcpResult =
    let movPos = lmMov.parsed.positions
    let movCentroid = lmMov.parsed.centroid
    let refCentroid = lmRef.parsed.centroid

    let stride = max 1 sampleStride
    let sampleCount = (movPos.Length + stride - 1) / stride
    let samplesWorld =
        Array.init sampleCount (fun i ->
            let idx = min (i * stride) (movPos.Length - 1)
            V3d movPos.[idx] + movCentroid)

    let mutable currR =
        M33d(initial.M00, initial.M01, initial.M02,
             initial.M10, initial.M11, initial.M12,
             initial.M20, initial.M21, initial.M22)
    let mutable currTr = V3d(initial.M03, initial.M13, initial.M23)

    let convergence = ResizeArray<float>(maxIter)
    let mutable finalResiduals : float[] = [||]
    let mutable converged = false
    let mutable lastRms = System.Double.MaxValue
    let mutable iter = 0
    while iter < maxIter && not converged do
        let pairs = ResizeArray<struct (V3d * V3d * float)>(samplesWorld.Length)
        for s in samplesWorld do
            let aMoved = currR * s + currTr
            let w =
                match anchorWeights with
                | Some f -> f aMoved
                | None -> 1.0
            if w > regionEps then
                let res = lmRef.scene.GetClosestPoint(V3f(aMoved - refCentroid))
                if res.IsValid then
                    let bWorld = V3d(res.Point) + refCentroid
                    pairs.Add(struct (aMoved, bWorld, w))
        if pairs.Count < 6 then
            iter <- maxIter
        else
            let Rd, td, rms = icpStep pairs
            convergence.Add rms
            currR <- Rd * currR
            currTr <- Rd * currTr + td
            if iter = maxIter - 1 || abs (lastRms - rms) < 1e-7 then
                finalResiduals <-
                    pairs |> Seq.map (fun (struct (a, b, _)) ->
                        let a' = Rd * a + td
                        (a' - b).Length)
                    |> Array.ofSeq
                if abs (lastRms - rms) < 1e-7 then converged <- true
            lastRms <- rms
            iter <- iter + 1

    let finalT =
        M44d(currR.M00, currR.M01, currR.M02, currTr.X,
             currR.M10, currR.M11, currR.M12, currTr.Y,
             currR.M20, currR.M21, currR.M22, currTr.Z,
             0.0, 0.0, 0.0, 1.0)
    { Transform = finalT; Convergence = convergence.ToArray(); Residuals = finalResiduals }

