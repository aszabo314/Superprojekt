module MeshIcp

open System
open Aardvark.Base
open Aardvark.Embree
open MeshCache

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

    let cells = ResizeArray<int * int * GridCellStats>()

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
