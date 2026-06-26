module MeshPreview

open System
open System.Collections.Generic
open Aardvark.Base
open Aardvark.Embree
open MeshCache

// Co-oriented mesh preview: project a downsampled copy of one mesh into a shared
// 2D frame and tag every vertex with a per-channel display scalar. Pure CPU —
// the client fills the triangles into a 2D canvas (no extra WebGL control).

// Projection of the 2D frame. Pano = cylindrical from an eye origin; the orthos
// are world-axis drops; Oblique = isometric axonometric (keeps the vertical/datum
// axis visible — used for the displacement glyph field).
type Projection = ProjPano | ProjTop | ProjFront | ProjSide | ProjOblique

// Pano eye: the focused mesh's own origin (pick) or the reference origin (compare).
type OriginMode = OriginOwn | OriginReference

// Display scalar per vertex.
//  Shade        = Lambert relief (n·light), 0..1, channel-free (pick context).
//  M3C2/Zdiff   = signed extrinsic distance to the reference (modes 0/1).
//  Incidence    = |n · dir-to-own-sensor| (acquisition incidence, view-independent).
//  Range        = distance from the own sensor / max range, 0..1.
//  Shape        = per-vertex triangle quality 4√3·A/Σl², 0..1.
//  Displacement = white surface; the payload is the sparse base→tip arrow field.
type Channel = ChShade | ChM3C2 | ChZdiff | ChIncidence | ChRange | ChShape | ChDisplacement

let projectionOfInt = function 1 -> ProjTop | 2 -> ProjFront | 3 -> ProjSide | 4 -> ProjOblique | _ -> ProjPano
let originOfInt     = function 1 -> OriginReference | _ -> OriginOwn
let channelOfInt    = function
    | 1 -> ChM3C2 | 2 -> ChZdiff | 3 -> ChIncidence | 4 -> ChRange | 5 -> ChShape | 6 -> ChDisplacement | _ -> ChShade

type PreviewResult = {
    // Per emitted (downsampled) vertex: 2D frame coord + display scalar.
    Verts2d : float[][]
    Scalar  : float[]
    // Triangle index triples into the emitted vertices (seam-crossing pano
    // triangles dropped).
    Tris    : int[]
    // Robust scalar domain (1st/99th pct over finite values); the client unions
    // these across panels for the shared colour scale. For Displacement, [0, max
    // |displacement|] so the client gets a shared magnitude scale.
    Lo      : float
    Hi      : float
    // Displacement channel only: sparse arrows — projected base (load pose) + tip
    // (solved pose) 2D positions, and the 3D displacement magnitude per sample.
    DispBase : float[][]
    DispTip  : float[][]
    DispMag  : float[]
}

let private lightDir = (V3d(0.35, 0.25, 0.90)).Normalized
let private noData = 1e30

let private quantile (sorted : float[]) (p : float) =
    let n = sorted.Length
    if n = 0 then 0.0
    else
        let h = p * float (n - 1)
        let i = int h
        if i >= n - 1 then sorted.[n - 1]
        else sorted.[i] + (h - float i) * (sorted.[i + 1] - sorted.[i])

// Per-vertex incident-face-averaged triangle quality over the whole mesh.
let private shapeQuality (pos : V3f[]) (idx : int[]) =
    let q = Array.zeroCreate<float> pos.Length
    let cnt = Array.zeroCreate<int> pos.Length
    let mutable f = 0
    while f + 2 < idx.Length do
        let a, b, c = idx.[f], idx.[f + 1], idx.[f + 2]
        let pa, pb, pc = V3d pos.[a], V3d pos.[b], V3d pos.[c]
        let denom = (pb - pa).LengthSquared + (pc - pb).LengthSquared + (pa - pc).LengthSquared
        let area = 0.5 * (Vec.cross (pb - pa) (pc - pa)).Length
        let ql = if denom > 1e-18 then min 1.0 (max 0.0 (6.9282032302755088 * area / denom)) else 0.0
        q.[a] <- q.[a] + ql; cnt.[a] <- cnt.[a] + 1
        q.[b] <- q.[b] + ql; cnt.[b] <- cnt.[b] + 1
        q.[c] <- q.[c] + ql; cnt.[c] <- cnt.[c] + 1
        f <- f + 3
    for i in 0 .. pos.Length - 1 do
        if cnt.[i] > 0 then q.[i] <- q.[i] / float cnt.[i]
    q

// transform = the surface pose (= the solved/tip pose for displacement); transform2
// is the displacement base (load) pose, ignored for every other channel.
let preview
        (lm : LoadedMesh) (refLm : LoadedMesh)
        (transform : M44d) (transform2 : M44d) (refTransform : M44d)
        (projection : Projection) (originMode : OriginMode)
        (channel : Channel) (maxTris : int) : PreviewResult =
    let pm = lm.parsed
    let pos = pm.positions
    let nrm = pm.normals
    let idx = pm.indices
    let centroid = pm.centroid
    let triCount = idx.Length / 3

    // Decimate by vertex clustering: snap each vertex to a grid cell and keep one
    // representative original vertex per occupied cell, then rebuild triangles over
    // the representatives (dropping triangles whose corners collapse into fewer
    // than three cells, and de-duplicating). This keeps a coherent surface, unlike
    // triangle striding (the old approach), which kept every Nth triangle and so
    // left a sparse scatter of isolated triangles with holes everywhere.
    let cellOf =
        if maxTris <= 0 || triCount <= maxTris || pm.bbox.IsInvalid then
            // No decimation needed: each vertex is its own cell.
            fun (v : int) -> struct(v, 0, 0)
        else
            let e = pm.bbox.Size
            let dims = [| e.X; e.Y; e.Z |] |> Array.sortDescending
            // Surface ≈ 2D, so occupied cells ≈ (extent / cell)² over the two
            // dominant axes; target ~maxTris/2 vertices ⇒ ~maxTris triangles.
            let targetCells = float (max 16 (maxTris / 2))
            let cell = max 1e-6 (sqrt (max 1e-12 (dims.[0] * dims.[1]) / targetCells))
            let bmin = pm.bbox.Min
            fun (v : int) ->
                let p = pos.[v]
                struct( int (floor ((float p.X - bmin.X) / cell)),
                        int (floor ((float p.Y - bmin.Y) / cell)),
                        int (floor ((float p.Z - bmin.Z) / cell)) )
    let cellRep = Dictionary<struct(int * int * int), int>()
    let repOf v =
        let key = cellOf v
        match cellRep.TryGetValue key with
        | true, r -> r
        | _ -> cellRep.[key] <- v; v
    let indexOf = Dictionary<int, int>()
    let order = ResizeArray<int>()
    let triList = ResizeArray<int>()
    let seen = HashSet<struct(int * int * int)>()
    let emit v =
        match indexOf.TryGetValue v with
        | true, i -> i
        | _ -> let i = order.Count in indexOf.[v] <- i; order.Add v; i
    for ti in 0 .. triCount - 1 do
        let a = repOf idx.[ti * 3]
        let b = repOf idx.[ti * 3 + 1]
        let c = repOf idx.[ti * 3 + 2]
        if a <> b && b <> c && a <> c then
            let lo = min a (min b c)
            let hi = max a (max b c)
            let mid = a + b + c - lo - hi
            if seen.Add(struct(lo, mid, hi)) then
                triList.Add(emit a); triList.Add(emit b); triList.Add(emit c)
    let n = order.Count

    // World position of each emitted vertex (rigid pose); local→world adds the centroid.
    let world = Array.init n (fun k -> transform.TransformPos (V3d pos.[order.[k]] + centroid))

    // Pano eye + sensor (the mesh's local origin) in world space. Positions are
    // stored relative to the centroid, so the local origin maps to
    // transform·centroid. Using transform·0 would sit at the far UTM origin.
    let ownSensor = transform.TransformPos centroid
    let eye =
        match originMode with
        | OriginReference -> refTransform.TransformPos refLm.parsed.centroid
        | OriginOwn -> ownSensor

    let halfPi = Math.PI * 0.5
    // Isometric oblique (30°): keeps Z visible so a vertical/datum shift shows.
    let obliqueC = 0.86602540378
    let obliqueS = 0.5
    let project (w : V3d) =
        match projection with
        | ProjTop     -> w.X, w.Y
        | ProjFront   -> w.X, w.Z
        | ProjSide    -> w.Y, w.Z
        | ProjOblique -> (w.X - w.Y) * obliqueC, w.Z + (w.X + w.Y) * obliqueS
        | ProjPano    ->
            let d = w - eye
            let hyp = sqrt (d.X * d.X + d.Y * d.Y)
            (if hyp < 1e-9 && abs d.Z < 1e-9 then 0.0 else atan2 d.Y d.X) / Math.PI,
            (atan2 d.Z (max 1e-9 hyp)) / halfPi
    let verts2d = Array.init n (fun k -> let u, v = project world.[k] in [| u; v |])

    let maxRange =
        let mutable mx = 1e-6
        for k in 0 .. n - 1 do
            let r = (world.[k] - ownSensor).Length
            if r > mx then mx <- r
        mx
    let scalar = Array.zeroCreate<float> n
    match channel with
    | ChDisplacement -> ()   // surface rendered white; payload is the arrow field
    | ChShade ->
        for k in 0 .. n - 1 do
            let nw = (transform.TransformDir (V3d nrm.[order.[k]])).Normalized
            scalar.[k] <- clamp 0.0 1.0 (0.2 + 0.8 * max 0.0 (Vec.dot nw lightDir))
    | ChIncidence ->
        for k in 0 .. n - 1 do
            let nw = (transform.TransformDir (V3d nrm.[order.[k]])).Normalized
            let toS = world.[k] - ownSensor
            let dir = if toS.Length > 1e-9 then toS.Normalized else V3d.OOI
            scalar.[k] <- abs (Vec.dot nw dir)
    | ChRange ->
        for k in 0 .. n - 1 do
            scalar.[k] <- clamp 0.0 1.0 ((world.[k] - ownSensor).Length / maxRange)
    | ChShape ->
        let q = shapeQuality pos idx
        for k in 0 .. n - 1 do scalar.[k] <- q.[order.[k]]
    | ChM3C2 | ChZdiff ->
        let rInv = refTransform.Inverse
        let cR = refLm.parsed.centroid
        let refPos = refLm.parsed.positions
        let refIdx = refLm.parsed.indices
        if channel = ChZdiff then
            let dnLocal = (rInv.TransformDir (V3d(0.0, 0.0, -1.0))).Normalized
            let upLocal = (rInv.TransformDir (V3d(0.0, 0.0,  1.0))).Normalized
            System.Threading.Tasks.Parallel.For(0, n, fun k ->
                let vRefLocal = rInv.TransformPos world.[k] - cR
                let mutable hd = RayHit()
                if refLm.scene.Intersect(V3f vRefLocal, V3f dnLocal, &hd) then scalar.[k] <- float hd.T
                else
                    let mutable hu = RayHit()
                    if refLm.scene.Intersect(V3f vRefLocal, V3f upLocal, &hu) then scalar.[k] <- - (float hu.T)
                    else scalar.[k] <- noData) |> ignore
        else
            System.Threading.Tasks.Parallel.For(0, n, fun k ->
                let vRefLocal = rInv.TransformPos world.[k] - cR
                let res = refLm.scene.GetClosestPoint(V3f vRefLocal)
                if res.IsValid then
                    let cp = V3d res.Point
                    let pid = int res.PrimID
                    if pid * 3 + 2 < refIdx.Length then
                        let p0 = V3d refPos.[refIdx.[pid * 3]]
                        let p1 = V3d refPos.[refIdx.[pid * 3 + 1]]
                        let p2 = V3d refPos.[refIdx.[pid * 3 + 2]]
                        let nm = Vec.cross (p1 - p0) (p2 - p0)
                        let nl = nm.Length
                        let s = if nl > 1e-12 && Vec.dot (vRefLocal - cp) (nm / nl) < 0.0 then -1.0 else 1.0
                        scalar.[k] <- s * sqrt (float res.DistanceSquared)
                    else scalar.[k] <- sqrt (float res.DistanceSquared)
                else scalar.[k] <- noData) |> ignore

    // Drop pano triangles that straddle the ±π azimuth seam (|Δu| > 1 ⇔ Δφ > π).
    let outTris =
        if projection <> ProjPano then triList.ToArray()
        else
            let keep = ResizeArray<int>(triList.Count)
            let mutable j = 0
            while j + 2 < triList.Count do
                let a, b, c = triList.[j], triList.[j + 1], triList.[j + 2]
                let ua, ub, uc = verts2d.[a].[0], verts2d.[b].[0], verts2d.[c].[0]
                let span = max (abs (ua - ub)) (max (abs (ub - uc)) (abs (ua - uc)))
                if span <= 1.0 then keep.Add a; keep.Add b; keep.Add c
                j <- j + 3
            keep.ToArray()

    // Displacement field: a sparse grid of emitted vertices, each as a projected
    // base (load pose, transform2) → tip (solved pose, the surface) arrow + the 3D
    // magnitude. Capped by a uniform stride.
    let dispBase, dispTip, dispMag =
        if channel = ChDisplacement then
            let target = 220
            let st = if n > target then n / target else 1
            let bs = ResizeArray<float[]>()
            let ts = ResizeArray<float[]>()
            let ms = ResizeArray<float>()
            let mutable k = 0
            while k < n do
                let baseW = transform2.TransformPos (V3d pos.[order.[k]] + centroid)
                let bu, bv = project baseW
                bs.Add [| bu; bv |]
                ts.Add verts2d.[k]
                ms.Add ((world.[k] - baseW).Length)
                k <- k + st
            bs.ToArray(), ts.ToArray(), ms.ToArray()
        else [||], [||], [||]

    let lo, hi =
        if channel = ChDisplacement then
            0.0, (if dispMag.Length = 0 then 1.0 else max 1e-3 (Array.max dispMag))
        else
            let finite = scalar |> Array.filter (fun s -> abs s < 1e20)
            if finite.Length = 0 then 0.0, 1.0
            else
                Array.sortInPlace finite
                quantile finite 0.01, quantile finite 0.99
    { Verts2d = verts2d; Scalar = scalar; Tris = outTris; Lo = lo; Hi = hi
      DispBase = dispBase; DispTip = dispTip; DispMag = dispMag }
