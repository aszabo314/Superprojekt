module MeshProbe

open System
open Aardvark.Base
open MeshCache

type ProbeMeshInput = {
    Name      : string
    Lm        : LoadedMesh
    Transform : M44d
}

type ProbeArgs = {
    Meshes           : ProbeMeshInput[]
    ReferenceName    : string
    Centre           : V3d
    Radius           : float
    Length           : float
    MaxPointsPerMesh : int
}

type ProbeDistribution = {
    Name      : string
    Count     : int
    Median    : float
    Q1        : float
    Q3        : float
    Std       : float
    Bandwidth : float
    Kde       : float[][]
    // Raw re-centred samples, subsampled to ≤300 for payload; stats use the full set.
    Samples   : float[]
    // ROI-averaged intrinsic quality [incidence; range; shape] ∈ [0,1].
    Intrinsics : float[]
}

type PerMeshSource = { Name : string; Iqr : float; MedianOffset : float; Count : int }

type ProbeResult = {
    Normal            : V3d
    Planarity         : float
    Length            : float
    AutoLength        : float
    // Axial offset (m along Normal from the pin centre) of the re-centred zero:
    // distributions are shifted so 0 = ref median, which sits at
    // centre + Normal·RefOffset in 3D.
    RefOffset         : float
    XAuto             : float * float
    XFit              : float * float
    Distributions     : ProbeDistribution[]
    DatasetError      : float
    AlgorithmResid    : float
    LocalConditioning : float
    PerMesh           : PerMeshSource[]
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

// PCA normal of the reference-mesh vertices inside the pin sphere.
let private estimateNormal (mi : ProbeMeshInput) (centre : V3d) (radius : float) =
    let pm = mi.Lm.parsed
    let inv = mi.Transform.Inverse
    let cL = inv.TransformPos centre - pm.centroid
    let vertIds = trianglesInSphere mi.Lm (V3f cL) (float32 radius)
    let seen = Collections.Generic.HashSet<int>()
    let pts = ResizeArray<V3d>()
    let r2 = radius * radius
    for vi in vertIds do
        if seen.Add vi then
            let p = V3d pm.positions.[vi]
            if (p - cL).LengthSquared <= r2 then pts.Add p
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
        let l0, l1, l2 = symEigenvalues (m00 / n) (m01 / n) (m02 / n) (m11 / n) (m12 / n) (m22 / n)
        let nLocal = eigenvectorFor (m00 / n) (m01 / n) (m02 / n) (m11 / n) (m12 / n) (m22 / n) l0
        let nWorld = (mi.Transform.TransformDir nLocal).Normalized
        let nWorld = if nWorld.Z < 0.0 then -nWorld else nWorld
        let planarity = if l1 > 1e-30 then l0 / l1 else 1.0
        // Geometric observability deficiency (1 − λmin/λmax of the
        // neighbourhood covariance): near-planar patch → ≈1 (weakly conditioned
        // for a 3D solve), isotropic → ≈0. Same formula as
        // RegMath.observabilityDeficiency.
        let condDeficiency = if l2 > 1e-30 then max 0.0 (min 1.0 (1.0 - l0 / l2)) else 1.0
        Some (nWorld, planarity, condDeficiency)

// Max extent of the union of all (transformed) mesh bboxes projected onto the axis.
let private autoLengthAlong (meshes : ProbeMeshInput[]) (axis : V3d) =
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
let private sampleAlongAxis (mi : ProbeMeshInput) (centre : V3d) (axis : V3d) (radius : float) (halfLen : float) (maxPoints : int) =
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
        let hits = ResizeArray<float>()
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
                        if radial.LengthSquared <= r2 then hits.Add t
        let arr = hits.ToArray()
        if arr.Length <= maxPoints then arr
        else
            let stride = float arr.Length / float maxPoints
            Array.init maxPoints (fun i -> arr.[int (float i * stride)])

// ROI-cylinder-averaged intrinsic quality, all [0,1] higher = better: incidence =
// surface-vs-probe-axis alignment (view-independent), range = proximity to the
// mesh-origin sensor, shape = triangle regularity.
let private meshIntrinsics (mi : ProbeMeshInput) (centre : V3d) (axis : V3d) (radius : float) (halfLen : float) =
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
    let positions = pm.positions
    // Sensor = the mesh's own origin (calibration convention); world = trafo·0.
    let sensor = mi.Transform.TransformPos V3d.Zero
    let maxRange =
        let b = pm.bbox
        if b.IsInvalid then 1.0
        else
            let mutable mx = 1e-6
            for ci in 0 .. 7 do
                let corner =
                    V3d((if ci &&& 1 = 0 then b.Min.X else b.Max.X),
                        (if ci &&& 2 = 0 then b.Min.Y else b.Max.Y),
                        (if ci &&& 4 = 0 then b.Min.Z else b.Max.Z))
                let w = mi.Transform.TransformPos (corner + pm.centroid)
                let r = (w - sensor).Length
                if r > mx then mx <- r
            mx
    let r2 = radius * radius
    let mutable nUsed = 0
    let mutable incSum = 0.0
    let mutable rngSum = 0.0
    let mutable shpSum = 0.0
    for ti in 0 .. triCount - 1 do
        let p0 = V3d positions.[tris.[ti * 3]]
        let p1 = V3d positions.[tris.[ti * 3 + 1]]
        let p2 = V3d positions.[tris.[ti * 3 + 2]]
        let g = (p0 + p1 + p2) / 3.0
        let d = g - cL
        let t = Vec.dot d aL
        let radial = (d - t * aL).LengthSquared
        if abs t <= halfLen && radial <= r2 then
            let e0 = p1 - p0
            let e1 = p2 - p0
            let cr = Vec.cross e0 e1
            let area = cr.Length * 0.5
            if area > 1e-20 then
                let nW = (mi.Transform.TransformDir cr.Normalized).Normalized
                incSum <- incSum + abs (Vec.dot nW axis)
                let gw = mi.Transform.TransformPos (g + pm.centroid)
                rngSum <- rngSum + max 0.0 (1.0 - min 1.0 ((gw - sensor).Length / maxRange))
                let l2 = e0.LengthSquared + e1.LengthSquared + (p2 - p1).LengthSquared
                let q = if l2 > 1e-20 then 6.9282032302755088 * area / l2 else 0.0
                shpSum <- shpSum + min 1.0 (max 0.0 q)
                nUsed <- nUsed + 1
    if nUsed = 0 then [| 0.0; 0.0; 0.0 |]
    else [| incSum / float nUsed; rngSum / float nUsed; shpSum / float nUsed |]

let private quantile (sorted : float[]) (p : float) =
    let n = sorted.Length
    if n = 0 then 0.0
    else
        let h = p * float (n - 1)
        let i = int h
        if i >= n - 1 then sorted.[n - 1]
        else sorted.[i] + (h - float i) * (sorted.[i + 1] - sorted.[i])

let run (args : ProbeArgs) : Result<ProbeResult, string> =
    match args.Meshes |> Array.tryFindIndex (fun m -> m.Name = args.ReferenceName) with
    | None -> Result.Error "reference mesh is not part of the probe set"
    | Some refIdx ->
        match estimateNormal args.Meshes.[refIdx] args.Centre args.Radius with
        | None -> Result.Error "not enough reference-mesh vertices inside the pin sphere (need ≥ 6)"
        | Some (normal, planarity, condDeficiency) ->
            let autoLen = autoLengthAlong args.Meshes normal
            let length = if args.Length > 0.0 then max 0.1 (min 1000.0 args.Length) else autoLen
            let halfLen = length * 0.5
            let maxPts = args.MaxPointsPerMesh
            let raw =
                args.Meshes
                |> Array.Parallel.map (fun mi -> sampleAlongAxis mi args.Centre normal args.Radius halfLen maxPts)
            let intrinsics =
                args.Meshes
                |> Array.Parallel.map (fun mi -> meshIntrinsics mi args.Centre normal args.Radius halfLen)
            // Re-centre so 0 = the reference mesh's median.
            let refMedian =
                let r = Array.sort raw.[refIdx]
                quantile r 0.5
            let sorted =
                raw |> Array.map (fun ts ->
                    let a = ts |> Array.map (fun t -> t - refMedian)
                    Array.sortInPlace a
                    a)
            let stats =
                sorted |> Array.map (fun a ->
                    if a.Length = 0 then struct (0.0, 0.0, 0.0, 0.0)
                    else
                        let med = quantile a 0.5
                        let q1 = quantile a 0.25
                        let q3 = quantile a 0.75
                        let mean = Array.average a
                        let var = (a |> Array.sumBy (fun x -> (x - mean) * (x - mean))) / float a.Length
                        struct (med, q1, q3, sqrt var))
            let bandwidths =
                Array.init sorted.Length (fun i ->
                    let a = sorted.[i]
                    let struct (_, q1, q3, std) = stats.[i]
                    if a.Length < 4 then 0.0
                    else
                        let iqr = q3 - q1
                        let sigma = if iqr > 0.0 then min std (iqr / 1.34) else std
                        let h = 0.9 * sigma * (float a.Length ** -0.2)
                        if h > 1e-9 then h else 0.0)
            // Chart auto range = union of median ± 3·IQR, floored to ±0.1 m;
            // fit range = data extent padded by 3·bandwidth.
            let mutable aLo = infinity
            let mutable aHi = -infinity
            let mutable fLo = infinity
            let mutable fHi = -infinity
            for i in 0 .. sorted.Length - 1 do
                let a = sorted.[i]
                if a.Length > 0 then
                    let struct (med, q1, q3, _) = stats.[i]
                    let iqr = q3 - q1
                    aLo <- min aLo (med - 3.0 * iqr)
                    aHi <- max aHi (med + 3.0 * iqr)
                    fLo <- min fLo (a.[0] - 3.0 * bandwidths.[i])
                    fHi <- max fHi (a.[a.Length - 1] + 3.0 * bandwidths.[i])
            if Double.IsInfinity aLo then (aLo <- -0.1; aHi <- 0.1)
            if aHi - aLo < 0.2 then
                let c = (aHi + aLo) * 0.5
                aLo <- c - 0.1
                aHi <- c + 0.1
            if Double.IsInfinity fLo then (fLo <- aLo; fHi <- aHi)
            let gLo = min aLo fLo
            let gHi = max aHi fHi
            let gridN = 256
            let dx = (gHi - gLo) / float (gridN - 1)
            let dists =
                Array.Parallel.init sorted.Length (fun i ->
                    let a = sorted.[i]
                    let struct (med, q1, q3, std) = stats.[i]
                    let h = bandwidths.[i]
                    let kde =
                        if a.Length < 4 || h <= 0.0 then [||]
                        else
                            let norm = 1.0 / (float a.Length * h * sqrt (2.0 * Math.PI))
                            Array.init gridN (fun gi ->
                                let x = gLo + float gi * dx
                                let mutable s = 0.0
                                for t in a do
                                    let z = (x - t) / h
                                    if abs z < 6.0 then s <- s + exp (-0.5 * z * z)
                                [| x; s * norm |])
                    let samples =
                        if a.Length <= 300 then a
                        else
                            let stride = float a.Length / 300.0
                            Array.init 300 (fun k -> a.[min (a.Length - 1) (int (float k * stride))])
                    { Name = args.Meshes.[i].Name; Count = a.Length
                      Median = med; Q1 = q1; Q3 = q3; Std = std
                      Bandwidth = h; Kde = kde
                      Samples = samples; Intrinsics = intrinsics.[i] })
            // Three-source decomposition: dataset = IQR of the union,
            // algorithm = RMS of non-reference median offsets, conditioning =
            // radius × observability deficiency (below).
            let union =
                let all = Array.concat sorted
                Array.sortInPlace all
                all
            let datasetError =
                if union.Length = 0 then 0.0
                else quantile union 0.75 - quantile union 0.25
            let offsets =
                dists
                |> Array.mapi (fun i d -> i, d)
                |> Array.choose (fun (i, d) -> if i <> refIdx && d.Count > 0 then Some d.Median else None)
            let algorithmResid =
                if offsets.Length = 0 then 0.0
                else sqrt ((offsets |> Array.sumBy (fun o -> o * o)) / float offsets.Length)
            // Local conditioning: in-plane positional uncertainty ≈ pin radius
            // scaled by the geometric observability deficiency (planar →
            // ~radius, isotropic → ~0). Geometric, not a sample-count heuristic.
            let conditioning = args.Radius * condDeficiency
            let perMesh =
                dists |> Array.map (fun d ->
                    { Name = d.Name; Iqr = d.Q3 - d.Q1; MedianOffset = d.Median; Count = d.Count })
            Result.Ok {
                Normal = normal
                Planarity = planarity
                Length = length
                AutoLength = autoLen
                RefOffset = refMedian
                XAuto = (aLo, aHi)
                XFit = (gLo, gHi)
                Distributions = dists
                DatasetError = datasetError
                AlgorithmResid = algorithmResid
                LocalConditioning = conditioning
                PerMesh = perMesh
            }
