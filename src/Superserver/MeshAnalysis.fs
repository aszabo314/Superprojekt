module MeshAnalysis

open System
open Aardvark.Base
open Aardvark.Embree
open MeshCache

// Marching level-set tracer shared by contactRings (sphere field) and planeSlices
// (plane field): sign-changing triangle edges become chain nodes keyed by edge,
// triangles contribute adjacency, chains are walked out both ways. `signedDist`
// is the per-vertex field, `edgePoint` the exact root on a sign-changing edge.
// Returns mesh-local point chains (closed chains repeat their first point so
// rendering has no gap).
let private traceLevelSet (triBuf : int[]) (signedDist : int -> float) (edgePoint : int -> int -> V3d) : V3d[][] =
    let triCount = triBuf.Length / 3

    let inline edgeKey (i0 : int) (i1 : int) : int64 =
        let a = min i0 i1
        let b = max i0 i1
        (int64 a <<< 32) ||| int64 b

    let edgePoints = System.Collections.Generic.Dictionary<int64, V3d>()
    let tryAddEdge (i0 : int) (i1 : int) (out : ResizeArray<int64>) =
        let key = edgeKey i0 i1
        let mutable existing = Unchecked.defaultof<V3d>
        if edgePoints.TryGetValue(key, &existing) then out.Add key
        else
            let d0 = signedDist i0
            let d1 = signedDist i1
            if (d0 > 0.0) <> (d1 > 0.0) then
                edgePoints.[key] <- edgePoint i0 i1
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
        let i0 = triBuf.[ti * 3]
        let i1 = triBuf.[ti * 3 + 1]
        let i2 = triBuf.[ti * 3 + 2]
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
        let chains = ResizeArray<V3d[]>()

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

        for start in adj.Keys |> Seq.toArray do
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
                // Closed chain iff its two end keys are adjacent.
                let isClosed =
                    combined.Count >= 3 &&
                    (let mutable n = Unchecked.defaultof<int64 * int64>
                     adj.TryGetValue(combined.[combined.Count - 1], &n)
                     && (fst n = combined.[0] || snd n = combined.[0]))
                let pts =
                    let n = combined.Count + (if isClosed then 1 else 0)
                    Array.init n (fun i -> edgePoints.[combined.[i % combined.Count]])
                if pts.Length >= 2 then chains.Add pts

        chains.ToArray()

// Cap the total point count by stride-thinning every chain (end points kept).
let private decimate (maxPoints : int) (chains : 'a[][]) : 'a[][] =
    let total = chains |> Array.sumBy Array.length
    if total <= maxPoints then chains
    else
        let stride = (total + maxPoints - 1) / maxPoints
        chains
        |> Array.map (fun r ->
            let kept = ResizeArray<'a>(r.Length / stride + 2)
            let mutable i = 0
            while i < r.Length - 1 do
                kept.Add r.[i]
                i <- i + stride
            kept.Add r.[r.Length - 1]
            kept.ToArray())
        |> Array.filter (fun r -> r.Length >= 2)

// Sphere–surface contact rings: level set of |p − centre| − radius over BVH
// candidate triangles. World-space.
let contactRings (lm : LoadedMesh) (centre : V3d) (radius : float) (maxPoints : int) : V3d[][] =
    let positions = lm.parsed.positions
    let centroid = lm.parsed.centroid
    let cLocal = centre - centroid

    // A sign-changing edge has a vertex inside the sphere → its triangle bbox
    // overlaps the sphere, so the BVH candidate query loses nothing.
    let triBuf = trianglesInSphere lm (V3f cLocal) (float32 radius)

    let signedDist (i : int) = (V3d positions.[i] - cLocal).Length - radius
    let edgePoint (i0 : int) (i1 : int) =
        let d0 = signedDist i0
        let d1 = signedDist i1
        let p0 = V3d positions.[i0]
        let p1 = V3d positions.[i1]
        // Exact sphere–segment root (one root in [0,1] given a sign change);
        // linear-interp fallback for degenerate edges.
        let dir = p1 - p0
        let m = p0 - cLocal
        let a = Vec.dot dir dir
        let b = 2.0 * Vec.dot m dir
        let c = Vec.dot m m - radius * radius
        let disc = b * b - 4.0 * a * c
        let t =
            if disc >= 0.0 && a > 1e-16 then
                let sq = sqrt disc
                let t0 = (-b - sq) / (2.0 * a)
                let t1 = (-b + sq) / (2.0 * a)
                if t0 >= 0.0 && t0 <= 1.0 then t0
                elif t1 >= 0.0 && t1 <= 1.0 then t1
                else d0 / (d0 - d1)
            else d0 / (d0 - d1)
        p0 + t * dir

    traceLevelSet triBuf signedDist edgePoint
    |> Array.map (Array.map (fun p -> p + centroid))
    |> decimate maxPoints

// Vertical cross-sections for the pin overlay charts: the mesh (posed by
// `transform`, mesh-own-world → scene world, probe convention) intersected with
// parallel planes through `centre` (normal `normal`, in-plane horizontal
// direction `uDir`, world-Z vertical), each clipped to the probe sphere
// (radius about centre). Returned as 2D chart-frame polylines — (u, v) metres
// relative to `centre` — with result.[k] = the polylines of offsets.[k].
let planeSlices (lm : LoadedMesh) (transform : M44d) (centre : V3d)
                (uDir : V3d) (normal : V3d) (radius : float)
                (offsets : float[]) (maxPointsPerPlane : int) : V2d[][][] =
    let positions = lm.parsed.positions
    let centroid = lm.parsed.centroid
    let inv = transform.Inverse
    let cLocal = inv.TransformPos centre - centroid
    let nLocal = (inv.TransformDir normal).Normalized
    let uLocal = (inv.TransformDir uDir).Normalized
    let vLocal = (inv.TransformDir V3d.OOI).Normalized

    // One candidate set serves every offset plane (all discs lie in this sphere).
    let triBuf = trianglesInSphere lm (V3f cLocal) (float32 radius)

    offsets |> Array.map (fun w ->
        let discR2 = radius * radius - w * w
        if discR2 <= 0.0 || triBuf.Length = 0 then [||]
        else
            let pLoc = cLocal + nLocal * w
            let signedDist (i : int) = Vec.dot (V3d positions.[i] - pLoc) nLocal
            let edgePoint (i0 : int) (i1 : int) =
                let d0 = signedDist i0
                let d1 = signedDist i1
                let p0 = V3d positions.[i0]
                let p1 = V3d positions.[i1]
                p0 + (d0 / (d0 - d1)) * (p1 - p0)
            let toChart (p : V3d) =
                let q = p - cLocal
                V2d(Vec.dot q uLocal, Vec.dot q vLocal)

            // Disc clip in the chart frame (u² + v² ≤ r² − w²) with the rim
            // crossing interpolated; a chain leaving the disc splits. Segments
            // with both ends outside can clip a tiny rim chord — ignored (mesh
            // edges are far shorter than the disc).
            let clipped = ResizeArray<V2d[]>()
            for chain in traceLevelSet triBuf signedDist edgePoint do
                let pts = chain |> Array.map toChart
                let cur = ResizeArray<V2d>()
                let flush () =
                    if cur.Count >= 2 then clipped.Add (cur.ToArray())
                    cur.Clear()
                let mutable prev = V2d.Zero
                let mutable prevIn = false
                for i in 0 .. pts.Length - 1 do
                    let p = pts.[i]
                    let isIn = p.LengthSquared <= discR2
                    if i > 0 && isIn <> prevIn then
                        let d = p - prev
                        let a = d.LengthSquared
                        let b = 2.0 * Vec.dot prev d
                        let c = prev.LengthSquared - discR2
                        let disc = b * b - 4.0 * a * c
                        let t =
                            if disc >= 0.0 && a > 1e-16 then
                                let sq = sqrt disc
                                let t0 = (-b - sq) / (2.0 * a)
                                if t0 >= 0.0 && t0 <= 1.0 then t0 else (-b + sq) / (2.0 * a)
                            else 0.5
                        let x = prev + d * (max 0.0 (min 1.0 t))
                        if prevIn then
                            cur.Add x
                            flush ()
                        else
                            cur.Add x
                    if isIn then cur.Add p
                    prev <- p
                    prevIn <- isIn
                flush ()
            decimate maxPointsPerPlane (clipped.ToArray()))
