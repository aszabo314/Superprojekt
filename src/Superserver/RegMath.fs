module RegMath

open Aardvark.Base

// Weighted rigid absolute orientation (Umeyama/Arun, no scale) for the
// landmark registration solve. Pure math — unit-tested standalone.

type LsqResult = {
    Transform           : M44d      // delta: maps current-world moving points onto the reference
    PerPairResiduals    : float[]   // |T(m_i) − r_i| post-solve
    Eigenvalues         : float[]   // weighted covariance of moving points, descending
    CollinearityWarning : bool      // λ2/λ1 < 1e-3
}

// Geometric observability deficiency from covariance eigenvalues: 1 − λmin/λmax.
// Near-planar patch → ≈1 (weakly conditioned for a 3D solve), isotropic → ≈0.
// Shared formula with the probe's local-conditioning source.
let observabilityDeficiency (eigenvalues : float[]) =
    if eigenvalues.Length = 0 then 1.0
    else
        let mn = Array.min eigenvalues
        let mx = Array.max eigenvalues
        if mx > 1e-30 then max 0.0 (min 1.0 (1.0 - mn / mx)) else 1.0

// Jacobi eigen decomposition of a symmetric 3×3 → eigenvalues (descending) +
// matching eigenvectors (columns).
let symEigen3 (m : M33d) : float[] * V3d[] =
    let a = [| [| m.M00; m.M01; m.M02 |]; [| m.M10; m.M11; m.M12 |]; [| m.M20; m.M21; m.M22 |] |]
    let v = [| [| 1.0; 0.0; 0.0 |]; [| 0.0; 1.0; 0.0 |]; [| 0.0; 0.0; 1.0 |] |]
    for _ in 0 .. 49 do
        let mutable p = 0
        let mutable q = 1
        let mutable off = abs a.[0].[1]
        if abs a.[0].[2] > off then off <- abs a.[0].[2]; p <- 0; q <- 2
        if abs a.[1].[2] > off then off <- abs a.[1].[2]; p <- 1; q <- 2
        if off > 1e-15 then
            let theta = 0.5 * (a.[q].[q] - a.[p].[p]) / a.[p].[q]
            let t = (if theta >= 0.0 then 1.0 else -1.0) / (abs theta + sqrt (theta * theta + 1.0))
            let c = 1.0 / sqrt (t * t + 1.0)
            let s = t * c
            for k in 0 .. 2 do
                let akp = a.[k].[p]
                let akq = a.[k].[q]
                a.[k].[p] <- c * akp - s * akq
                a.[k].[q] <- s * akp + c * akq
            for k in 0 .. 2 do
                let apk = a.[p].[k]
                let aqk = a.[q].[k]
                a.[p].[k] <- c * apk - s * aqk
                a.[q].[k] <- s * apk + c * aqk
            for k in 0 .. 2 do
                let vkp = v.[k].[p]
                let vkq = v.[k].[q]
                v.[k].[p] <- c * vkp - s * vkq
                v.[k].[q] <- s * vkp + c * vkq
    let evals = [| a.[0].[0]; a.[1].[1]; a.[2].[2] |]
    let order = [| 0; 1; 2 |] |> Array.sortByDescending (fun i -> evals.[i])
    order |> Array.map (fun i -> evals.[i]),
    order |> Array.map (fun i -> V3d(v.[0].[i], v.[1].[i], v.[2].[i]))

let private fromCols (c0 : V3d) (c1 : V3d) (c2 : V3d) =
    M33d(c0.X, c1.X, c2.X,
         c0.Y, c1.Y, c2.Y,
         c0.Z, c1.Z, c2.Z)

let private anyOrthonormal (u : V3d) =
    let axis = if abs u.X < 0.9 then V3d.IOO else V3d.OIO
    (Vec.cross u axis).Normalized

// pairs: (movingPoint, refPoint, weight) in a common (world) frame, current
// poses. None when fewer than 3 pairs.
let solveRigid (pairs : (V3d * V3d * float)[]) : LsqResult option =
    if pairs.Length < 3 then None
    else
        let raw = pairs |> Array.map (fun (m, r, w) -> m, r, max 0.0 w)
        let wTot = raw |> Array.sumBy (fun (_, _, w) -> w)
        // All-zero weights degenerate to the uniform problem.
        let pts = if wTot > 1e-12 then raw else raw |> Array.map (fun (m, r, _) -> m, r, 1.0)
        let wSum = pts |> Array.sumBy (fun (_, _, w) -> w)
        let mMean = (pts |> Array.sumBy (fun (m, _, w) -> m * w)) / wSum
        let rMean = (pts |> Array.sumBy (fun (_, r, w) -> r * w)) / wSum

        // H = Σ wᵢ (mᵢ−m̄)(rᵢ−r̄)ᵀ
        let mutable h = M33d.Zero
        for (m, r, w) in pts do
            let dm = m - mMean
            let dr = r - rMean
            h <- h + M33d(w * dm.X * dr.X, w * dm.X * dr.Y, w * dm.X * dr.Z,
                          w * dm.Y * dr.X, w * dm.Y * dr.Y, w * dm.Y * dr.Z,
                          w * dm.Z * dr.X, w * dm.Z * dr.Y, w * dm.Z * dr.Z)

        // SVD H = UΣVᵀ via eigen of HᵀH = VΣ²Vᵀ; U completed orthonormally
        // where σ vanishes (planar / collinear sets).
        let hth = h.Transposed * h
        let lams, vCols = symEigen3 hth
        let sigmas = lams |> Array.map (fun l -> sqrt (max 0.0 l))
        let sigMax = max sigmas.[0] 1e-300
        let mutable v0 = vCols.[0]
        let mutable v1 = vCols.[1]
        let mutable v2 = vCols.[2]
        // Right-handed V (eigenvector signs are arbitrary).
        if Vec.dot (Vec.cross v0 v1) v2 < 0.0 then v2 <- -v2
        let usable i = sigmas.[i] > 1e-9 * sigMax
        let u0 =
            let raw = if usable 0 then h * v0 else V3d.IOO
            if raw.Length > 1e-300 then raw.Normalized else V3d.IOO
        let u1 =
            let raw = if usable 1 then h * v1 else anyOrthonormal u0
            let ortho = raw - u0 * Vec.dot raw u0
            if ortho.Length > 1e-12 then ortho.Normalized else anyOrthonormal u0
        // Right-handed completion — the σ₃ direction is the reflection-prone
        // one; det(V·Uᵀ) below decides whether it gets flipped.
        let u2 = Vec.cross u0 u1
        let vMat = fromCols v0 v1 v2
        let uMat = fromCols u0 u1 u2
        let d = (vMat * uMat.Transposed).Determinant
        let dSign = if d < 0.0 then -1.0 else 1.0
        let flip = M33d(1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, dSign)
        let r = vMat * flip * uMat.Transposed
        let t = rMean - r * mMean

        let residuals =
            pts |> Array.map (fun (m, rf, _) -> (r * m + t - rf).Length)

        // Conditioning: weighted covariance of the moving points.
        let mutable cov = M33d.Zero
        for (m, _, w) in pts do
            let dm = m - mMean
            cov <- cov + M33d(w * dm.X * dm.X, w * dm.X * dm.Y, w * dm.X * dm.Z,
                              w * dm.Y * dm.X, w * dm.Y * dm.Y, w * dm.Y * dm.Z,
                              w * dm.Z * dm.X, w * dm.Z * dm.Y, w * dm.Z * dm.Z)
        let covLams, _ = symEigen3 (cov * (1.0 / wSum))
        let collinear =
            covLams.[0] <= 1e-12 || (max 0.0 covLams.[1]) / covLams.[0] < 1e-3

        Some {
            Transform =
                M44d(r.M00, r.M01, r.M02, t.X,
                     r.M10, r.M11, r.M12, t.Y,
                     r.M20, r.M21, r.M22, t.Z,
                     0.0, 0.0, 0.0, 1.0)
            PerPairResiduals    = residuals
            Eigenvalues         = covLams
            CollinearityWarning = collinear
        }
