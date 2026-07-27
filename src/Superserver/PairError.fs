module PairError

open System
open Aardvark.Base
open Aardvark.Embree
open MeshCache

// ============================================================================
// Pairwise reference-free error (v14 registration graph).
//
// Measure — the established M3C2-style signed distance, symmetrized. Given
// meshes A and B at explicit world poses (rigid M44d; local + centroid →
// world; the server holds no registration state — callers compose the poses),
// the error at a pin (centre c, radius r) is measured along ONE shared axis n:
// the PCA surface normal of the pooled A ∪ B vertex set inside the pin sphere,
// oriented upward (n.Z ≥ 0). Each surface is deterministically point-sampled
// inside the cylinder (axis n through c, radius r, half-length L/2); a
// sample's axial coordinate is t = ⟨p − c, n⟩.
//
// Sign convention (deterministic for fixed (A, poseA, B, poseB)): a sample
// value is the signed axial offset of B's surface relative to A's —
//     B-sample:  v = t_B − median(t_A)   (at the B sample's world position)
//     A-sample:  v = median(t_B) − t_A   (at the A sample's world position)
// pooled into ONE distribution per pin. Positive = B lies farther along +n
// than A (B above A, n points up). Swapping A ↔ B negates every value —
// symmetric measure, antisymmetric sign.
//
// Median = median of the pooled values (≈ median(t_B) − median(t_A)).
// LodHalfWidth = 1.96·√(σ_A² + σ_B²) over the two per-mesh axial spreads —
// the 95 % level-of-detection half-width for every LoD band downstream.
// ============================================================================

type PairMesh = {
    Name      : string
    Lm        : LoadedMesh
    Transform : M44d
}

type PinRoi = {
    Id     : string
    Centre : V3d
    Radius : float
}

type PinPairError = {
    Id           : string
    Ok           : bool
    Reason       : string
    Normal       : V3d
    Count        : int
    Median       : float
    LodHalfWidth : float
    // Pooled signed values, subsampled to ≤300 for payload; stats use the full set.
    Samples      : float[]
    // World-space surface position of each subsampled sample, flattened xyz,
    // aligned 1:1 with Samples (so a chart sample maps back to its 3D cell).
    Positions    : float[]
}

type PairErrorArgs = {
    A                : PairMesh
    B                : PairMesh
    Pins             : PinRoi[]
    Length           : float
    MaxPointsPerMesh : int
}

// Eigenvalues (ascending) of a symmetric 3x3 matrix, analytic (trigonometric form).
let private symEigenvalues (m00 : float) (m01 : float) (m02 : float) (m11 : float) (m12 : float) (m22 : float) =
    let p1 = m01 * m01 + m02 * m02 + m12 * m12
    if p1 < 1e-30 then
        let l = Array.sort [| m00; m11; m22 |]
        l.[0], l.[1], l.[2]
    else
        let q = (m00 + m11 + m22) / 3.0
        let d0 = m00 - q
        let d1 = m11 - q
        let d2 = m22 - q
        let p = sqrt ((d0 * d0 + d1 * d1 + d2 * d2 + 2.0 * p1) / 6.0)
        let b00 = d0 / p
        let b11 = d1 / p
        let b22 = d2 / p
        let b01 = m01 / p
        let b02 = m02 / p
        let b12 = m12 / p
        let detB =
            b00 * (b11 * b22 - b12 * b12)
            - b01 * (b01 * b22 - b12 * b02)
            + b02 * (b01 * b12 - b11 * b02)
        let r = max -1.0 (min 1.0 (detB / 2.0))
        let phi = acos r / 3.0
        let e1 = q + 2.0 * p * cos phi
        let e3 = q + 2.0 * p * cos (phi + 2.0 * Math.PI / 3.0)
        let e2 = 3.0 * q - e1 - e3
        e3, e2, e1

let private eigenvectorFor (m00 : float) m01 m02 m11 m12 m22 (lambda : float) =
    let r0 = V3d(m00 - lambda, m01, m02)
    let r1 = V3d(m01, m11 - lambda, m12)
    let r2 = V3d(m02, m12, m22 - lambda)
    let mutable best = Vec.cross r0 r1
    let c1 = Vec.cross r0 r2
    let c2 = Vec.cross r1 r2
    if c1.LengthSquared > best.LengthSquared then best <- c1
    if c2.LengthSquared > best.LengthSquared then best <- c2
    if best.Length > 1e-12 then best.Normalized else V3d.OOI

// PCA normal (smallest-eigenvalue direction) of the pooled world-space vertex
// set of BOTH meshes inside the sphere — symmetric in (A, B); None below 6 pts.
let private estimateNormal (a : PairMesh) (b : PairMesh) (centre : V3d) (radius : float) =
    let pts = ResizeArray<V3d>()
    let collect (m : PairMesh) =
        let pm = m.Lm.parsed
        let inv = m.Transform.Inverse
        let cL = inv.TransformPos centre - pm.centroid
        let vertIds = trianglesInSphere m.Lm (V3f cL) (float32 radius)
        let seen = Collections.Generic.HashSet<int>()
        let r2 = radius * radius
        for vi in vertIds do
            if seen.Add vi then
                let p = V3d pm.positions.[vi]
                if (p - cL).LengthSquared <= r2 then
                    pts.Add (m.Transform.TransformPos (p + pm.centroid))
    collect a
    collect b
    if pts.Count < 6 then None
    else
        let mutable mean = V3d.Zero
        for p in pts do mean <- mean + p
        mean <- mean / float pts.Count
        let mutable m00 = 0.0
        let mutable m01 = 0.0
        let mutable m02 = 0.0
        let mutable m11 = 0.0
        let mutable m12 = 0.0
        let mutable m22 = 0.0
        for p in pts do
            let d = p - mean
            m00 <- m00 + d.X * d.X
            m01 <- m01 + d.X * d.Y
            m02 <- m02 + d.X * d.Z
            m11 <- m11 + d.Y * d.Y
            m12 <- m12 + d.Y * d.Z
            m22 <- m22 + d.Z * d.Z
        let n = float pts.Count
        let l0, _, _ = symEigenvalues (m00 / n) (m01 / n) (m02 / n) (m11 / n) (m12 / n) (m22 / n)
        let nrm = eigenvectorFor (m00 / n) (m01 / n) (m02 / n) (m11 / n) (m12 / n) (m22 / n) l0
        Some (if nrm.Z < 0.0 then -nrm else nrm)

// Max extent of the union of the (posed) mesh bboxes projected onto the axis.
let private autoLengthAlong (meshes : PairMesh[]) (axis : V3d) =
    let mutable lo = infinity
    let mutable hi = -infinity
    for mi in meshes do
        let pm = mi.Lm.parsed
        if not pm.bbox.IsInvalid then
            let b = pm.bbox
            for ci in 0 .. 7 do
                let corner =
                    V3d((if ci &&& 1 = 0 then b.Min.X else b.Max.X),
                        (if ci &&& 2 = 0 then b.Min.Y else b.Max.Y),
                        (if ci &&& 4 = 0 then b.Min.Z else b.Max.Z))
                let t = Vec.dot (mi.Transform.TransformPos (corner + pm.centroid)) axis
                if t < lo then lo <- t
                if t > hi then hi <- t
    if hi > lo then min 100.0 (1.1 * (hi - lo)) else 10.0

// Signed axial coordinates of cylinder hits. Deterministic barycentric-lattice
// sampling of triangle interiors (density targets maxPoints over the candidate
// area) so low-res meshes still produce distributions.
let private sampleAlongAxis (mi : PairMesh) (centre : V3d) (axis : V3d) (radius : float) (halfLen : float) (maxPoints : int) =
    let pm = mi.Lm.parsed
    let inv = mi.Transform.Inverse
    let cL = inv.TransformPos centre - pm.centroid
    let aL = (inv.TransformDir axis).Normalized
    let ext =
        V3d(halfLen * abs aL.X + radius * sqrt (max 0.0 (1.0 - aL.X * aL.X)),
            halfLen * abs aL.Y + radius * sqrt (max 0.0 (1.0 - aL.Y * aL.Y)),
            halfLen * abs aL.Z + radius * sqrt (max 0.0 (1.0 - aL.Z * aL.Z)))
    let tris = trianglesInBox mi.Lm (V3f (cL - ext)) (V3f (cL + ext))
    let triCount = tris.Length / 3
    if triCount = 0 then [||]
    else
        let positions = pm.positions
        let areas = Array.zeroCreate triCount
        let mutable areaSum = 0.0
        for ti in 0 .. triCount - 1 do
            let p0 = V3d positions.[tris.[ti * 3]]
            let p1 = V3d positions.[tris.[ti * 3 + 1]]
            let p2 = V3d positions.[tris.[ti * 3 + 2]]
            let a = (Vec.cross (p1 - p0) (p2 - p0)).Length * 0.5
            areas.[ti] <- a
            areaSum <- areaSum + a
        let spacing2 = if areaSum > 1e-12 then areaSum / float maxPoints else 1.0
        let hits = ResizeArray<float * V3d>()
        let r2 = radius * radius
        for ti in 0 .. triCount - 1 do
            let p0 = V3d positions.[tris.[ti * 3]]
            let p1 = V3d positions.[tris.[ti * 3 + 1]]
            let p2 = V3d positions.[tris.[ti * 3 + 2]]
            let k = int (ceil (sqrt (2.0 * areas.[ti] / spacing2))) |> max 1 |> min 64
            let fk = float k
            for i in 0 .. k - 1 do
                for j in 0 .. k - 1 - i do
                    let u = (float i + 0.5) / fk
                    let v = (float j + 0.5) / fk
                    let p = p0 + u * (p1 - p0) + v * (p2 - p0)
                    let d = p - cL
                    let t = Vec.dot d aL
                    if abs t <= halfLen then
                        let radial = d - t * aL
                        if radial.LengthSquared <= r2 then
                            // p is centroid-relative local → own frame (+centroid) → world.
                            hits.Add(t, mi.Transform.TransformPos (p + pm.centroid))
        let arr = hits.ToArray()
        if arr.Length <= maxPoints then arr
        else
            let stride = float arr.Length / float maxPoints
            Array.init maxPoints (fun i -> arr.[int (float i * stride)])

let private quantile (sorted : float[]) (p : float) =
    let n = sorted.Length
    if n = 0 then 0.0
    else
        let h = p * float (n - 1)
        let i = int h
        if i >= n - 1 then sorted.[n - 1]
        else sorted.[i] + (h - float i) * (sorted.[i + 1] - sorted.[i])

let private medianOf (values : float[]) =
    let s = Array.sort values
    quantile s 0.5

let private stdOf (values : float[]) =
    if values.Length = 0 then 0.0
    else
        let mean = Array.average values
        sqrt ((values |> Array.sumBy (fun x -> (x - mean) * (x - mean))) / float values.Length)

let private failPin (roi : PinRoi) (reason : string) =
    { Id = roi.Id; Ok = false; Reason = reason; Normal = V3d.Zero
      Count = 0; Median = 0.0; LodHalfWidth = 0.0
      Samples = Array.empty; Positions = Array.empty }

let private pinError (a : PairMesh) (b : PairMesh) (length : float) (maxPts : int) (roi : PinRoi) =
    match estimateNormal a b roi.Centre roi.Radius with
    | None -> failPin roi "not enough vertices inside the pin sphere (need ≥ 6 pooled over both meshes)"
    | Some normal ->
        let len =
            if length > 0.0 then max 0.1 (min 1000.0 length)
            else autoLengthAlong [| a; b |] normal
        let halfLen = len * 0.5
        let sA = sampleAlongAxis a roi.Centre normal roi.Radius halfLen maxPts
        let sB = sampleAlongAxis b roi.Centre normal roi.Radius halfLen maxPts
        if sA.Length = 0 || sB.Length = 0 then
            failPin roi (sprintf "no overlap: %s has no surface inside the pin cylinder"
                                 (if sA.Length = 0 then a.Name else b.Name))
        else
            let tA = sA |> Array.map fst
            let tB = sB |> Array.map fst
            let medA = medianOf tA
            let medB = medianOf tB
            let stdA = stdOf tA
            let stdB = stdOf tB
            let pooled =
                Array.append
                    (sB |> Array.map (fun (t, p) -> t - medA, p))
                    (sA |> Array.map (fun (t, p) -> medB - t, p))
            let samples, positions =
                if pooled.Length <= 300 then
                    pooled |> Array.map fst,
                    pooled |> Array.collect (fun (_, p) -> [| p.X; p.Y; p.Z |])
                else
                    let stride = float pooled.Length / 300.0
                    let idxs = Array.init 300 (fun k -> min (pooled.Length - 1) (int (float k * stride)))
                    idxs |> Array.map (fun j -> fst pooled.[j]),
                    idxs |> Array.collect (fun j -> let p = snd pooled.[j] in [| p.X; p.Y; p.Z |])
            { Id = roi.Id; Ok = true; Reason = null; Normal = normal
              Count = pooled.Length
              Median = medianOf (pooled |> Array.map fst)
              LodHalfWidth = 1.96 * sqrt (stdA * stdA + stdB * stdB)
              Samples = samples; Positions = positions }

let run (args : PairErrorArgs) : PinPairError[] =
    args.Pins
    |> Array.Parallel.map (pinError args.A args.B args.Length args.MaxPointsPerMesh)

type PointErrorArgs = {
    A       : PairMesh
    B       : PairMesh
    Point   : V3d
    Radius  : float
    MaxDist : float
}

// Exact signed error of B relative to A at ONE world point: n = the pooled PCA
// normal within Radius of the point; each mesh's surface crossing of the line
// (Point + t·n) nearest the point (|t| ≤ MaxDist) gives t_A and t_B; the value
// t_B − t_A carries the pin distributions' sign convention exactly.
let atPoint (args : PointErrorArgs) =
    match estimateNormal args.A args.B args.Point args.Radius with
    | None -> Result.Error "not enough surface around the point (need ≥ 6 vertices inside the radius)"
    | Some n ->
        let crossing (m : PairMesh) =
            let pm = m.Lm.parsed
            let inv = m.Transform.Inverse
            let pL = inv.TransformPos args.Point - pm.centroid
            let nL = (inv.TransformDir n).Normalized
            // 1 mm origin back-off: a pick lying exactly ON a surface would put
            // the ray origin on the triangle, where the t≈0 hit can be missed.
            let eps = 1e-3
            let mutable hu = RayHit()
            let up = if m.Lm.scene.Intersect(V3f (pL - nL * eps), V3f nL, &hu) then Some (float hu.T - eps) else None
            let mutable hd = RayHit()
            let dn = if m.Lm.scene.Intersect(V3f (pL + nL * eps), V3f (-nL), &hd) then Some (eps - float hd.T) else None
            let best =
                match up, dn with
                | Some u, Some d -> Some (if abs u <= abs d then u else d)
                | Some t, None | None, Some t -> Some t
                | None, None -> None
            match best with
            | Some t when abs t <= args.MaxDist ->
                Some (t, m.Transform.TransformPos (pL + nL * t + pm.centroid))
            | _ -> None
        match crossing args.A, crossing args.B with
        | Some (tA, pA), Some (tB, pB) ->
            Result.Ok {| Value = tB - tA; Normal = n; PointA = pA; PointB = pB |}
        | None, _ -> Result.Error (sprintf "%s has no surface along the normal at this point" args.A.Name)
        | _, None -> Result.Error (sprintf "%s has no surface along the normal at this point" args.B.Name)

type OverlapArgs = {
    A           : PairMesh
    B           : PairMesh
    MaxDist     : float   // ≤ 0 → 1 % of the mean posed-bbox diagonal, clamped [0.5, 20] m
    MinFraction : float   // ≤ 0 → 0.05
    MaxSamples  : int     // ≤ 0 → 1500 per direction
}

type OverlapResult = {
    Sufficient : bool
    FracAB     : float
    FracBA     : float
    MaxDist    : float
    SamplesA   : int
    SamplesB   : int
}

let private worldBox (m : PairMesh) =
    let pm = m.Lm.parsed
    let mutable box = Box3d.Invalid
    if not pm.bbox.IsInvalid then
        let b = pm.bbox
        for ci in 0 .. 7 do
            let corner =
                V3d((if ci &&& 1 = 0 then b.Min.X else b.Max.X),
                    (if ci &&& 2 = 0 then b.Min.Y else b.Max.Y),
                    (if ci &&& 4 = 0 then b.Min.Z else b.Max.Z))
            box.ExtendBy (m.Transform.TransformPos (corner + pm.centroid))
    box

// Fraction of src's (stride-sampled) vertices whose closest point on dst lies
// within maxDist — src world → dst local through both poses.
let private coveredFraction (src : PairMesh) (dst : PairMesh) (maxDist : float) (maxSamples : int) =
    let pm = src.Lm.parsed
    let n = pm.positions.Length
    if n = 0 then 0.0, 0
    else
        let step = max 1 (n / maxSamples)
        let count = (n + step - 1) / step
        let dInv = dst.Transform.Inverse
        let cD = dst.Lm.parsed.centroid
        let maxD2 = maxDist * maxDist
        let hits =
            Array.Parallel.init count (fun k ->
                let w = src.Transform.TransformPos (V3d pm.positions.[k * step] + pm.centroid)
                let res = dst.Lm.scene.GetClosestPoint(V3f (dInv.TransformPos w - cD))
                if res.IsValid && float res.DistanceSquared <= maxD2 then 1 else 0)
        float (Array.sum hits) / float count, count

// Cheap registerability probe: SUFFICIENT ⇔ enough of one mesh lies within
// MaxDist of the other — max of the two directions, so a small mesh fully
// inside a large one passes. A posed-bbox pre-reject (boxes further than
// MaxDist apart) skips the closest-point sweep entirely.
let overlap (args : OverlapArgs) : OverlapResult =
    let boxA = worldBox args.A
    let boxB = worldBox args.B
    let maxDist =
        if args.MaxDist > 0.0 then args.MaxDist
        else max 0.5 (min 20.0 (0.005 * (boxA.Size.Length + boxB.Size.Length)))
    let minFraction = if args.MinFraction > 0.0 then args.MinFraction else 0.05
    let maxSamples = if args.MaxSamples <= 0 then 1500 else min args.MaxSamples 8192
    let boxesTouch =
        not boxA.IsInvalid && not boxB.IsInvalid &&
        Box3d(boxA.Min - V3d.One * maxDist, boxA.Max + V3d.One * maxDist).Intersects boxB
    if not boxesTouch then
        { Sufficient = false; FracAB = 0.0; FracBA = 0.0; MaxDist = maxDist; SamplesA = 0; SamplesB = 0 }
    else
        let fracAB, nA = coveredFraction args.A args.B maxDist maxSamples
        let fracBA, nB = coveredFraction args.B args.A maxDist maxSamples
        { Sufficient = max fracAB fracBA >= minFraction
          FracAB = fracAB; FracBA = fracBA; MaxDist = maxDist
          SamplesA = nA; SamplesB = nB }
