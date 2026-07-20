// Pure algorithmic core of MeshAnalysis — no Embree/MeshCache dependency, so
// Supertests compiles it directly (like RegMath).
module MeshAnalysisCore

open Aardvark.Base

// Marching level-set tracer shared by contactRings (sphere field) and planeSlices
// (plane field): sign-changing triangle edges become chain nodes keyed by edge,
// triangles contribute adjacency, chains are walked out both ways. `signedDist`
// is the per-vertex field, `edgePoint` the exact root on a sign-changing edge.
// Returns mesh-local point chains (closed chains repeat their first point so
// rendering has no gap).
let traceLevelSet (triBuf : int[]) (signedDist : int -> float) (edgePoint : int -> int -> V3d) : V3d[][] =
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
let decimate (maxPoints : int) (chains : 'a[][]) : 'a[][] =
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

// Dip direction of the LSQ height fit z = ax + by + c from centred point
// moments; sign-canonicalised (+X, tie +Y). None: sparse (< 8 points), a
// degenerate normal system, or a flat patch.
let dipFromMoments (n : float) (sx : float) (sy : float) (sz : float)
                   (sxx : float) (syy : float) (sxy : float) (sxz : float) (syz : float) : V3d option =
    if n < 8.0 then None
    else
        let det3 (m00, m01, m02) (m10, m11, m12) (m20, m21, m22) =
            m00 * (m11 * m22 - m12 * m21) - m01 * (m10 * m22 - m12 * m20) + m02 * (m10 * m21 - m11 * m20)
        let d = det3 (sxx, sxy, sx) (sxy, syy, sy) (sx, sy, n)
        if abs d <= 1e-10 * max 1.0 (sxx * syy * n) then None
        else
            let a = det3 (sxz, sxy, sx) (syz, syy, sy) (sz, sy, n) / d
            let b = det3 (sxx, sxz, sx) (sxy, syz, sy) (sx, sz, n) / d
            let g = V2d(a, b)
            if g.Length < 1e-4 then None
            else
                let u = g.Normalized
                let u = if u.X < 0.0 || (abs u.X < 1e-9 && u.Y < 0.0) then -u else u
                Some (V3d(u.X, u.Y, 0.0))

let dipOfPoints (pts : seq<V3d>) : V3d option =
    let mutable n = 0.0
    let mutable sx = 0.0
    let mutable sy = 0.0
    let mutable sz = 0.0
    let mutable sxx = 0.0
    let mutable syy = 0.0
    let mutable sxy = 0.0
    let mutable sxz = 0.0
    let mutable syz = 0.0
    for q in pts do
        n   <- n + 1.0
        sx  <- sx + q.X;  sy  <- sy + q.Y;  sz  <- sz + q.Z
        sxx <- sxx + q.X * q.X
        syy <- syy + q.Y * q.Y
        sxy <- sxy + q.X * q.Y
        sxz <- sxz + q.X * q.Z
        syz <- syz + q.Y * q.Z
    dipFromMoments n sx sy sz sxx syy sxy sxz syz
