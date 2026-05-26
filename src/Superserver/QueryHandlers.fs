module QueryHandlers

open System
open System.IO
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open Giraffe
open Aardvark.Base
open Aardvark.Embree

[<CLIMutable>]
type RayRequest     = { Name: string; Index: int; Origin: float[]; Direction: float[] }

[<CLIMutable>]
type ClosestRequest = { Name: string; Index: int; Point: float[] }

[<CLIMutable>]
type PlaneIntersectionRequest = { Name: string; Index: int; PlanePoint: float[]; PlaneNormal: float[]; AxisU: float[]; AxisV: float[]; Thickness: float; MaxExtentU: float; MaxExtentV: float }

[<CLIMutable>]
type PlaneIntersectionBatchRequest = { Names: string[]; PlanePoint: float[]; PlaneNormal: float[]; AxisU: float[]; AxisV: float[]; Thickness: float; MaxExtentU: float; MaxExtentV: float }

[<CLIMutable>]
type RayBatchRequest = { Names: string[]; Origins: float[]; Directions: float[] }

[<CLIMutable>]
type GridEvalRequest = { Dataset: string; Anchor: float[]; Axis: float[]; Radius: float; Resolution: int; ExtentForward: float; ExtentBackward: float }

[<CLIMutable>]
type CylinderEvalRequest = { Dataset: string; Anchor: float[]; Axis: float[]; Radii: float[]; AngularResolution: int; ExtentForward: float; ExtentBackward: float }

[<CLIMutable>]
type IsolineRequest = { Name: string; Elevation: float; Seed: float[]; MaxPoints: int }

[<CLIMutable>]
type RidgeRequest = { Name: string; Seed: float[]; ThresholdRad: float; MaxPoints: int }

[<CLIMutable>]
type PatchRequest = { Name: string; Centre: float[]; Radius: float; MaxPoints: int }

[<CLIMutable>]
type IcpRequest = {
    ReferenceName    : string
    MovingName       : string
    InitialTransform : float[]
    SampleStride     : int
    MaxIterations    : int
    AnchorCentres    : float[]
    AnchorSigmas     : float[]
    AnchorWeights    : float[]
    RegionEps        : float
}

let inline toV3d (a : float[]) = V3d(a.[0], a.[1], a.[2])
let inline fromV3d (v : V3d)   = [| v.X; v.Y; v.Z |]

let splitName (fullName : string) =
    let parts = fullName.Split([|'/'|], 2)
    if parts.Length = 2 then parts.[0], parts.[1]
    else "", fullName

let rayHandler : HttpHandler =
    fun next ctx -> task {
        try
            let! req = ctx.BindJsonAsync<RayRequest>()
            let dataset, name = splitName req.Name
            let lm   = MeshCache.get dataset name req.Index
            let c    = lm.parsed.centroid
            let orig = V3f(toV3d req.Origin - c)
            let dir  = V3f(toV3d req.Direction)
            let mutable hit = RayHit()
            let ok = lm.scene.Intersect(orig, dir, &hit)
            if ok then
                let worldHit = V3d(orig + dir * hit.T) + c
                return! json {| hit = true; t = hit.T; point = fromV3d worldHit; triangleId = int hit.PrimitiveId |} next ctx
            else
                return! json {| hit = false |} next ctx
        with ex -> return! RequestErrors.notFound (text ex.Message) next ctx
    }

let closestHandler : HttpHandler =
    fun next ctx -> task {
        try
            let! req = ctx.BindJsonAsync<ClosestRequest>()
            let dataset, name = splitName req.Name
            let lm   = MeshCache.get dataset name req.Index
            let c    = lm.parsed.centroid
            let res  = lm.scene.GetClosestPoint(V3f(toV3d req.Point - c))
            if res.IsValid then
                let worldPt = V3d res.Point + c
                return! json {| found = true; point = fromV3d worldPt; distanceSquared = res.DistanceSquared; triangleId = int res.PrimID |} next ctx
            else
                return! json {| found = false |} next ctx
        with ex -> return! RequestErrors.notFound (text ex.Message) next ctx
    }

let planeIntersectionHandler : HttpHandler =
    fun next ctx -> task {
        let log = ctx.GetLogger "Superserver"
        try
            let! req = ctx.BindJsonAsync<PlaneIntersectionRequest>()
            let dataset, name = splitName req.Name
            let lm = MeshCache.get dataset name req.Index
            let c = lm.parsed.centroid
            let planePoint = toV3d req.PlanePoint - c
            let planeNormal = toV3d req.PlaneNormal
            let axisU = toV3d req.AxisU
            let axisV = toV3d req.AxisV
            let segments = MeshCache.planeIntersection lm planePoint planeNormal axisU axisV req.Thickness req.MaxExtentU req.MaxExtentV
            log.LogDebug("plane-intersection {Name}: {Count} segments", req.Name, segments.Length)
            return! json {| segments = segments |} next ctx
        with ex ->
            log.LogError(ex, "plane-intersection failed")
            return! RequestErrors.notFound (text ex.Message) next ctx
    }

let isolineHandler : HttpHandler =
    fun next ctx -> task {
        let log = ctx.GetLogger "Superserver"
        try
            let! req = ctx.BindJsonAsync<IsolineRequest>()
            let dataset, name = splitName req.Name
            let lm = MeshCache.get dataset name 0
            let seed = toV3d req.Seed
            let maxPoints = if req.MaxPoints <= 0 then 4096 else req.MaxPoints
            let flat = MeshAnalysis.isoline lm req.Elevation seed maxPoints
            let n = flat.Length / 3
            let pts = Array.init n (fun i -> [| flat.[i * 3]; flat.[i * 3 + 1]; flat.[i * 3 + 2] |])
            log.LogInformation("isoline {Name} z={Elevation:F3}: {Count} pts", req.Name, req.Elevation, n)
            return! json {| polyline = pts |} next ctx
        with ex ->
            log.LogError(ex, "isoline failed")
            return! RequestErrors.notFound (text ex.Message) next ctx
    }

let curvatureRidgeHandler : HttpHandler =
    fun next ctx -> task {
        let log = ctx.GetLogger "Superserver"
        try
            let! req = ctx.BindJsonAsync<RidgeRequest>()
            let dataset, name = splitName req.Name
            let lm = MeshCache.get dataset name 0
            let seed = toV3d req.Seed
            let threshold = if req.ThresholdRad <= 0.0 then 0.4 else req.ThresholdRad
            let maxPoints = if req.MaxPoints <= 0 then 4096 else req.MaxPoints
            let flat, scalars = MeshAnalysis.curvatureRidgeWithScalars lm seed threshold maxPoints
            let n = flat.Length / 3
            let pts = Array.init n (fun i -> [| flat.[i * 3]; flat.[i * 3 + 1]; flat.[i * 3 + 2] |])
            log.LogInformation("curvature-ridge {Name} θ={Threshold:F2}rad: {Count} pts", req.Name, threshold, n)
            return! json {| polyline = pts; scalars = scalars |} next ctx
        with ex ->
            log.LogError(ex, "curvature-ridge failed")
            return! RequestErrors.notFound (text ex.Message) next ctx
    }

let patchHandler : HttpHandler =
    fun next ctx -> task {
        let log = ctx.GetLogger "Superserver"
        try
            let! req = ctx.BindJsonAsync<PatchRequest>()
            let dataset, name = splitName req.Name
            let lm = MeshCache.get dataset name 0
            let centre = toV3d req.Centre
            let radius = if req.Radius <= 0.0 then 1.0 else req.Radius
            let maxPoints = if req.MaxPoints <= 0 then 4096 else req.MaxPoints
            let result = MeshAnalysis.patch lm centre radius maxPoints
            let pts = result.Points |> Array.map (fun p -> [| p.Px; p.Py; p.Wx; p.Wy; p.Wz |])
            log.LogInformation("patch {Name} r={Radius:F2}: {Count} pts", req.Name, radius, pts.Length)
            return! json {| points = pts; refDir = fromV3d result.RefDirWorld; normal = fromV3d result.NormalWorld |} next ctx
        with ex ->
            log.LogError(ex, "patch failed")
            return! RequestErrors.notFound (text ex.Message) next ctx
    }

let icpHandler : HttpHandler =
    fun next ctx -> task {
        let log = ctx.GetLogger "Superserver"
        try
            let! req = ctx.BindJsonAsync<IcpRequest>()
            let refDataset, refName = splitName req.ReferenceName
            let movDataset, movName = splitName req.MovingName
            let lmRef = MeshCache.get refDataset refName 0
            let lmMov = MeshCache.get movDataset movName 0

            let initial =
                if req.InitialTransform <> null && req.InitialTransform.Length = 16 then
                    let m = req.InitialTransform
                    M44d(m.[0], m.[1], m.[2], m.[3],
                         m.[4], m.[5], m.[6], m.[7],
                         m.[8], m.[9], m.[10], m.[11],
                         m.[12], m.[13], m.[14], m.[15])
                else M44d.Identity

            let weights =
                let aC = if isNull req.AnchorCentres then [||] else req.AnchorCentres
                let aS = if isNull req.AnchorSigmas  then [||] else req.AnchorSigmas
                let aW = if isNull req.AnchorWeights then [||] else req.AnchorWeights
                let n = aS.Length
                if n = 0 || aC.Length < n * 3 then None
                else
                    let centres = Array.init n (fun i -> V3d(aC.[i * 3], aC.[i * 3 + 1], aC.[i * 3 + 2]))
                    let f (p : V3d) =
                        let mutable s = 0.0
                        for i in 0 .. n - 1 do
                            let sigma = aS.[i]
                            if sigma > 1e-6 then
                                let d2 = (p - centres.[i]).LengthSquared
                                let w = exp (-d2 / (2.0 * sigma * sigma))
                                let mult = if i < aW.Length then aW.[i] else 1.0
                                s <- s + mult * w
                        min 1.0 s
                    Some f

            let stride = if req.SampleStride <= 0 then 50 else req.SampleStride
            let maxIter = if req.MaxIterations <= 0 then 30 else req.MaxIterations
            let eps = if req.RegionEps <= 0.0 then 0.0 else req.RegionEps
            let result = MeshIcp.runIcp lmRef lmMov initial stride maxIter weights eps

            let m = result.Transform
            let flat = [|
                m.M00; m.M01; m.M02; m.M03
                m.M10; m.M11; m.M12; m.M13
                m.M20; m.M21; m.M22; m.M23
                m.M30; m.M31; m.M32; m.M33
            |]
            log.LogInformation("icp ref={Ref} mov={Mov}: {Iters} iters, final RMS={Rms:F4}",
                req.ReferenceName, req.MovingName, result.Convergence.Length,
                (if result.Convergence.Length > 0 then result.Convergence.[result.Convergence.Length - 1] else 0.0))
            return! json {| transform = flat; convergence = result.Convergence; residuals = result.Residuals |} next ctx
        with ex ->
            log.LogError(ex, "icp failed")
            return! RequestErrors.notFound (text ex.Message) next ctx
    }

