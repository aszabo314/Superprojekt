module MeshIcp

open System
open Aardvark.Base
open Aardvark.Embree
open MeshCache

type IcpResult = {
    Transform     : M44d
    Convergence   : float[]
    Residuals     : float[]
}

[<AutoOpen>]
module private IcpMath =
    let inline skew (v : V3d) =
        M33d(
            0.0, -v.Z, v.Y,
            v.Z, 0.0, -v.X,
            -v.Y, v.X, 0.0)

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

    let rotFromOmega (omega : V3d) =
        let theta = omega.Length
        if theta < 1e-12 then M33d.Identity
        else
            let k = omega / theta
            let K = skew k
            M33d.Identity + K * sin theta + K * K * (1.0 - cos theta)

    // Linearizes rotation around the weighted correspondence centroid: raw
    // UTM-scale coords give a ~5e6 m lever arm, an ill-conditioned normal
    // matrix, and a divergent step. Solved recentred, recomposed to a world map.
    let icpStep (pairs : ResizeArray<struct (V3d * V3d * float)>) =
        let mutable cSum = V3d.Zero
        let mutable cW = 0.0
        for i in 0 .. pairs.Count - 1 do
            let struct (ai, _, wi) = pairs.[i]
            cSum <- cSum + ai * wi
            cW <- cW + wi
        let c = if cW > 0.0 then cSum / cW else V3d.Zero
        let A = Array2D.zeroCreate<float> 6 6
        let B = Array.zeroCreate<float> 6
        let mutable rmsSum = 0.0
        let mutable wSum = 0.0
        let J = Array2D.zeroCreate<float> 3 6
        for i in 0 .. pairs.Count - 1 do
            let struct (ai, bi, wi) = pairs.[i]
            let al = ai - c
            let r0 = ai.X - bi.X
            let r1 = ai.Y - bi.Y
            let r2 = ai.Z - bi.Z

            J.[0, 0] <- 0.0;     J.[0, 1] <- al.Z;   J.[0, 2] <- -al.Y; J.[0, 3] <- 1.0; J.[0, 4] <- 0.0; J.[0, 5] <- 0.0
            J.[1, 0] <- -al.Z;   J.[1, 1] <- 0.0;    J.[1, 2] <-  al.X; J.[1, 3] <- 0.0; J.[1, 4] <- 1.0; J.[1, 5] <- 0.0
            J.[2, 0] <-  al.Y;   J.[2, 1] <- -al.X;  J.[2, 2] <-  0.0;  J.[2, 3] <- 0.0; J.[2, 4] <- 0.0; J.[2, 5] <- 1.0
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
        // p ↦ R(p−c)+c+t as a world map p ↦ R·p + tWorld. The centroid maps to
        // c+t, so this step's displacement is |t| — NOT |tWorld|, which (I−R)c
        // inflates for any rotation about a far-from-origin centroid.
        let tWorld = c - R * c + t
        R, tWorld, sqrt (rmsSum / max 1.0 wSum), t.Length

// Abort rather than return a divergent pose: a step displacing the centroid by
// more than a few combined mesh-extents is overlap-starvation runaway, not a
// gap-closing step. Generous so only absurd motions trip it.
let divergenceGate (refDiag : float) (movDiag : float) = max 100.0 (3.0 * (refDiag + movDiag))
let isRunawayStep (stepDisplacement : float) (gate : float) =
    not (System.Double.IsFinite stepDisplacement) || stepDisplacement > gate

// Reference is "small" when its bbox diagonal is under this fraction of the
// mover's — the overlap-starvation regime where naive closest-point ICP drags
// the mover (the study's flung-mesh defect).
let smallReferenceRegime (refDiag : float) (movDiag : float) =
    refDiag > 1e-6 && movDiag > 1e-6 && refDiag / movDiag < 0.4

let runIcp
        (lmRef : LoadedMesh) (lmMov : LoadedMesh)
        (initial : M44d) (sampleStride : int) (maxIter : int)
        (anchorWeights : (V3d -> float) option) (regionEps : float)
        : Result<IcpResult, string> =
    let movPos = lmMov.parsed.positions
    let movCentroid = lmMov.parsed.centroid
    let refCentroid = lmRef.parsed.centroid

    let refBox = lmRef.parsed.bbox
    let movBox = lmMov.parsed.bbox
    let refDiag = if refBox.IsInvalid then 0.0 else refBox.Size.Length
    let movDiag = if movBox.IsInvalid then 0.0 else movBox.Size.Length
    let smallRef = smallReferenceRegime refDiag movDiag
    let gate = divergenceGate refDiag movDiag
    // Restrict moving samples to the reference's world region (+ margin) so
    // far-away non-overlapping points can't form biasing correspondences.
    let refWorldBox =
        if refBox.IsInvalid then Box3d.Invalid
        else
            let m = 0.5 * refDiag
            let mv = V3d(m, m, m)
            Box3d(refCentroid + refBox.Min - mv, refCentroid + refBox.Max + mv)
    let inRegion (p : V3d) =
        not smallRef || refWorldBox.IsInvalid || refWorldBox.Contains p

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
    let mutable aborted = false
    let mutable lastRms = System.Double.MaxValue
    let mutable iter = 0
    while iter < maxIter && not converged && not aborted do
        let pairs = ResizeArray<struct (V3d * V3d * float)>(samplesWorld.Length)
        for s in samplesWorld do
            let aMoved = currR * s + currTr
            let w =
                match anchorWeights with
                | Some f -> f aMoved
                | None -> 1.0
            if w > regionEps && inRegion aMoved then
                let res = lmRef.scene.GetClosestPoint(V3f(aMoved - refCentroid))
                if res.IsValid then
                    let bWorld = V3d(res.Point) + refCentroid
                    pairs.Add(struct (aMoved, bWorld, w))
        if pairs.Count < 6 then
            iter <- maxIter
        else
            // Trimmed correspondences: with partial overlap, pairs from
            // non-overlapping regions bias the solve — gate at 3× median dist.
            let pairs =
                if pairs.Count < 12 then pairs
                else
                    let dists = pairs |> Seq.map (fun (struct (a, b, _)) -> (a - b).Length) |> Array.ofSeq
                    let sorted = Array.copy dists
                    Array.sortInPlace sorted
                    let gate = max (3.0 * sorted.[sorted.Length / 2]) 1e-6
                    let filtered = ResizeArray<struct (V3d * V3d * float)>(pairs.Count)
                    for i in 0 .. pairs.Count - 1 do
                        if dists.[i] <= gate then filtered.Add pairs.[i]
                    if filtered.Count >= 6 then filtered else pairs
            let Rd, td, rms, stepDisp = icpStep pairs
            // Divergence guard: a runaway step aborts before the flung pose is
            // ever applied or returned.
            if isRunawayStep stepDisp gate then
                aborted <- true
            else
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

    if aborted then
        Result.Error "insufficient overlap — fine ICP diverged; try region-restricted mode or a tighter reference region"
    else
        let finalT =
            M44d(currR.M00, currR.M01, currR.M02, currTr.X,
                 currR.M10, currR.M11, currR.M12, currTr.Y,
                 currR.M20, currR.M21, currR.M22, currTr.Z,
                 0.0, 0.0, 0.0, 1.0)
        Result.Ok { Transform = finalT; Convergence = convergence.ToArray(); Residuals = finalResiduals }
