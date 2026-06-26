module MeshAnalysis

open System
open Aardvark.Base
open Aardvark.Embree
open MeshCache

// Sphere–surface contact rings: level set of |p − centre| − radius traced by
// marching-squares edge keys + linking over BVH candidate triangles. Returns
// every ring; closed rings repeat their first point so rendering has no gap.
// Points are world-space.
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
// local plane fit and projects into the supplied frame (origin = centre) so
// several meshes share one co-oriented projection.
//
// The data are height fields, so there is no overlap to disambiguate and mesh
// connectivity is irrelevant: the patch is every triangle whose bbox overlaps
// the footprint sphere, projected into the frame; a fragmented multi-tile mesh
// fills the footprint like a watertight DEM. (Px,Py) is the orthographic
// projection (origin = centre); Triangles index into Points. maxTris bounds the
// output via a uniform stride over the (spatially grouped) BVH triangle order,
// so a dense mesh thins evenly rather than collapsing to a core.
let patch (lm : LoadedMesh) (centre : V3d) (radius : float) (maxTris : int) (frame : (V3d * V3d) option) : PatchResult =
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
    let triCount = triBuf.Length / 3

    let normal, refDir =
        match frame with
        | Some (n, r) ->
            let nn = if n.Length > 1e-9 then n.Normalized else V3d.OOI
            nn, orthoRef nn r
        | None when triCount = 0 -> V3d.OOI, V3d.OIO
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

    // Footprint clip: keep a triangle if any corner falls inside the radius disc.
    // The gathered sphere is slightly larger so straddling rim triangles fill the
    // edge; this trims the annulus the client circle-clips away anyway.
    let r2 = radius * radius
    let inline planarIn v =
        let dv = V3d positions.[v] - centreLocal
        let px = Vec.dot dv refDir
        let py = Vec.dot dv leftDir
        px * px + py * py <= r2

    let stride = if maxTris > 0 && triCount > maxTris then triCount / maxTris else 1
    let indexOf = System.Collections.Generic.Dictionary<int, int>()
    let pts = ResizeArray<PatchPoint>()
    let emit v =
        match indexOf.TryGetValue v with
        | true, i -> i
        | _ ->
            let i = pts.Count
            indexOf.[v] <- i
            let vp = V3d positions.[v]
            let dv = vp - centreLocal
            let dvTan = dv - normal * Vec.dot dv normal
            let world = vp + centroid
            let uv = if v < uvs.Length then V2d uvs.[v] else V2d.Zero
            pts.Add { Px = Vec.dot dvTan refDir; Py = Vec.dot dvTan leftDir
                      Wx = world.X; Wy = world.Y; Wz = world.Z; U = uv.X; V = uv.Y }
            i
    let tris = ResizeArray<int>()
    let mutable ti = 0
    while ti < triCount do
        let i0, i1, i2 = triBuf.[ti * 3], triBuf.[ti * 3 + 1], triBuf.[ti * 3 + 2]
        if planarIn i0 || planarIn i1 || planarIn i2 then
            let a = emit i0
            let b = emit i1
            let c = emit i2
            tris.Add a; tris.Add b; tris.Add c
        ti <- ti + stride
    { Points = pts.ToArray(); Triangles = tris.ToArray(); RefDirWorld = refDir; NormalWorld = normal }
