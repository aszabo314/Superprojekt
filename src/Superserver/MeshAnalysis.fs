module MeshAnalysis

open System
open Aardvark.Base
open Aardvark.Embree
open MeshCache

// Sphere–surface contact rings: level set of |p − centre| − radius traced by
// marching-squares edge keys + linking over BVH candidate triangles. Returns
// every ring (a pin sphere can touch a surface in several places); closed rings
// repeat their first point so rendering has no gap. Points are world-space.
let contactRings (lm : LoadedMesh) (centre : V3d) (radius : float) (maxPoints : int) : V3d[][] =
    let positions = lm.parsed.positions
    let centroid = lm.parsed.centroid
    let cLocal = centre - centroid

    // A sign-changing edge has a vertex inside the sphere → its triangle bbox
    // overlaps the sphere, so the BVH candidate query loses nothing.
    let triBuf = trianglesInSphere lm (V3f cLocal) (float32 radius)
    let triCount = triBuf.Length / 3

    let inline edgeKey (i0 : int) (i1 : int) : int64 =
        let a = min i0 i1
        let b = max i0 i1
        (int64 a <<< 32) ||| int64 b

    let inline signedDist (i : int) = (V3d positions.[i] - cLocal).Length - radius

    let edgePoints = System.Collections.Generic.Dictionary<int64, V3d>()
    let inline tryAddEdge (i0 : int) (i1 : int) (out : ResizeArray<int64>) =
        let key = edgeKey i0 i1
        let mutable existing = Unchecked.defaultof<V3d>
        if edgePoints.TryGetValue(key, &existing) then out.Add key
        else
            let d0 = signedDist i0
            let d1 = signedDist i1
            if (d0 > 0.0) <> (d1 > 0.0) then
                let p0 = V3d positions.[i0]
                let p1 = V3d positions.[i1]
                // Exact sphere–segment root (one root in [0,1] given a sign
                // change); linear-interp fallback for degenerate edges.
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
                edgePoints.[key] <- p0 + t * dir
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
        let rings = ResizeArray<V3d[]>()

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
                // Closed ring iff the chain's two end keys are adjacent.
                let isClosed =
                    combined.Count >= 3 &&
                    (let mutable n = Unchecked.defaultof<int64 * int64>
                     adj.TryGetValue(combined.[combined.Count - 1], &n)
                     && (fst n = combined.[0] || snd n = combined.[0]))
                let pts =
                    let n = combined.Count + (if isClosed then 1 else 0)
                    Array.init n (fun i ->
                        edgePoints.[combined.[i % combined.Count]] + centroid)
                if pts.Length >= 2 then rings.Add pts

        let total = rings |> Seq.sumBy (fun r -> r.Length)
        if total <= maxPoints then rings.ToArray()
        else
            let stride = (total + maxPoints - 1) / maxPoints
            rings
            |> Seq.map (fun r ->
                let kept = ResizeArray<V3d>(r.Length / stride + 2)
                let mutable i = 0
                while i < r.Length - 1 do
                    kept.Add r.[i]
                    i <- i + stride
                kept.Add r.[r.Length - 1]
                kept.ToArray())
            |> Seq.filter (fun r -> r.Length >= 2)
            |> Array.ofSeq

type PatchPoint = { Px : float; Py : float; Wx : float; Wy : float; Wz : float; U : float; V : float }
type PatchResult = { Points : PatchPoint[]; Triangles : int[]; RefDirWorld : V3d; NormalWorld : V3d }

// frame: optional (normal, refDir) override in the mesh's own frame — skips the
// local plane fit and projects into the supplied frame (origin = centre) so the
// patch small-multiples picker shares one co-oriented projection across meshes.
//
// withTriangles changes the output contract: (Px,Py) become the exact planar
// orthographic projection onto the frame (consistent with the picker's
// (u,v)→world inversion) instead of geodesic-polar unrolling; the maxPoints cap
// keeps the geodesically nearest vertices (a connected disc) instead of stride
// decimation; Triangles carries index triples into Points for triangles whose
// corners all survived.
let patch (lm : LoadedMesh) (centre : V3d) (radius : float) (maxPoints : int) (frame : (V3d * V3d) option) (withTriangles : bool) : PatchResult =
    let positions = lm.parsed.positions
    let uvs = lm.parsed.uvs
    let centroid = lm.parsed.centroid
    let centreLocal = centre - centroid

    let orthoRef (normal : V3d) (cand : V3d) =
        let proj = cand - normal * Vec.dot cand normal
        if proj.Length > 1e-9 then Vec.normalize proj
        else
            let projX = V3d.IOO - normal * Vec.dot V3d.IOO normal
            if projX.Length > 1e-9 then Vec.normalize projX else V3d.IOO

    let triBuf = trianglesInSphere lm (V3f centreLocal) (float32 (radius * 1.2))
    if triBuf.Length = 0 then
        let n, r =
            match frame with
            | Some (n, r) ->
                let nn = if n.Length > 1e-9 then n.Normalized else V3d.OOI
                nn, orthoRef nn r
            | None -> V3d.OOI, V3d.OIO
        { Points = [||]; Triangles = [||]; RefDirWorld = r; NormalWorld = n }
    else
        let triCount = triBuf.Length / 3

        let normal, refDir =
            match frame with
            | Some (n, r) ->
                let nn = if n.Length > 1e-9 then n.Normalized else V3d.OOI
                nn, orthoRef nn r
            | None ->
                let mutable nSum = V3d.Zero
                for ti in 0 .. triCount - 1 do
                    let p0 = V3d positions.[triBuf.[ti * 3]]
                    let p1 = V3d positions.[triBuf.[ti * 3 + 1]]
                    let p2 = V3d positions.[triBuf.[ti * 3 + 2]]
                    nSum <- nSum + Vec.cross (p1 - p0) (p2 - p0)
                let normal = if nSum.Length > 1e-9 then Vec.normalize nSum else V3d.OOI
                normal, orthoRef normal V3d.OIO
        let leftDir = Vec.cross normal refDir

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

        let mutable seedV = -1
        let mutable seedD2 = System.Double.MaxValue
        for v in adj.Keys do
            let d2 = (V3d positions.[v] - centreLocal).LengthSquared
            if d2 < seedD2 then seedD2 <- d2; seedV <- v

        if seedV < 0 then
            { Points = [||]; Triangles = [||]; RefDirWorld = refDir; NormalWorld = normal }
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

            if withTriangles then
                let kept =
                    let all = dist |> Seq.toArray
                    if all.Length <= maxPoints then all
                    else
                        all |> Array.sortBy (fun kv -> kv.Value) |> Array.truncate maxPoints
                let indexOf = System.Collections.Generic.Dictionary<int, int>(kept.Length)
                let pts =
                    kept |> Array.mapi (fun i kv ->
                        let v = kv.Key
                        indexOf.[v] <- i
                        let vp = V3d positions.[v]
                        let dv = vp - centreLocal
                        let dvTan = dv - normal * Vec.dot dv normal
                        let world = vp + centroid
                        let uv = if v < uvs.Length then V2d uvs.[v] else V2d.Zero
                        { Px = Vec.dot dvTan refDir; Py = Vec.dot dvTan leftDir
                          Wx = world.X; Wy = world.Y; Wz = world.Z; U = uv.X; V = uv.Y })
                let tris = ResizeArray<int>(triCount * 3)
                for ti in 0 .. triCount - 1 do
                    let mutable a = 0
                    let mutable b = 0
                    let mutable c = 0
                    if indexOf.TryGetValue(triBuf.[ti * 3], &a)
                       && indexOf.TryGetValue(triBuf.[ti * 3 + 1], &b)
                       && indexOf.TryGetValue(triBuf.[ti * 3 + 2], &c) then
                        tris.Add a; tris.Add b; tris.Add c
                { Points = pts; Triangles = tris.ToArray(); RefDirWorld = refDir; NormalWorld = normal }
            else
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
                    let uv = if v < uvs.Length then V2d uvs.[v] else V2d.Zero
                    out.Add { Px = px; Py = py; Wx = world.X; Wy = world.Y; Wz = world.Z; U = uv.X; V = uv.Y }

                let pts = out.ToArray()

                let final =
                    if pts.Length <= maxPoints then pts
                    else
                        let stride = pts.Length / maxPoints
                        let n = pts.Length / stride
                        Array.init n (fun i -> pts.[i * stride])
                { Points = final; Triangles = [||]; RefDirWorld = refDir; NormalWorld = normal }
