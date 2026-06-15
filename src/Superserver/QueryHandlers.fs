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
type PatchRequest = {
    Name: string; Centre: float[]; Radius: float; MaxPoints: int
    // Optional shared-frame override (mesh-frame directions); both must be
    // present to take effect. Absent fields bind to null → local plane fit.
    FrameNormal: float[]; FrameRefDir: float[]
    // true → planar projection + triangle index triples (absent binds false).
    Triangles: bool
}

[<CLIMutable>]
type LsqPairDto = { RefPoint: float[]; MovingPoint: float[]; Weight: float }

[<CLIMutable>]
type LsqPairsRequest = { MovingName: string; Pairs: LsqPairDto[] }

[<CLIMutable>]
type ContactRingsRequest = { Name: string; Centre: float[]; Radius: float; MaxPoints: int }

[<CLIMutable>]
type ProbeMeshDto = { Name: string; Transform: float[] }

[<CLIMutable>]
type ProbeRequest = {
    Meshes           : ProbeMeshDto[]
    ReferenceName    : string
    Centre           : float[]
    Radius           : float
    Length           : float
    MaxPointsPerMesh : int
}

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

let loadMesh (fullName : string) (index : int) =
    let dataset, name = splitName fullName
    MeshCache.get dataset name index

let rayHandler : HttpHandler =
    fun next ctx -> task {
        try
            let! req = ctx.BindJsonAsync<RayRequest>()
            let lm   = loadMesh req.Name req.Index
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
            let lm   = loadMesh req.Name req.Index
            let c    = lm.parsed.centroid
            let res  = lm.scene.GetClosestPoint(V3f(toV3d req.Point - c))
            if res.IsValid then
                let worldPt = V3d res.Point + c
                return! json {| found = true; point = fromV3d worldPt; distanceSquared = res.DistanceSquared; triangleId = int res.PrimID |} next ctx
            else
                return! json {| found = false |} next ctx
        with ex -> return! RequestErrors.notFound (text ex.Message) next ctx
    }

let patchHandler : HttpHandler =
    fun next ctx -> task {
        let log = ctx.GetLogger "Superserver"
        try
            let! req = ctx.BindJsonAsync<PatchRequest>()
            let lm = loadMesh req.Name 0
            let centre = toV3d req.Centre
            let radius = if req.Radius <= 0.0 then 1.0 else req.Radius
            let maxPoints = if req.MaxPoints <= 0 then 4096 else req.MaxPoints
            let frame =
                if not (isNull req.FrameNormal) && req.FrameNormal.Length = 3
                   && not (isNull req.FrameRefDir) && req.FrameRefDir.Length = 3 then
                    Some (toV3d req.FrameNormal, toV3d req.FrameRefDir)
                else None
            let result = MeshAnalysis.patch lm centre radius maxPoints frame req.Triangles
            let pts = result.Points |> Array.map (fun p -> [| p.Px; p.Py; p.Wx; p.Wy; p.Wz; p.U; p.V |])
            log.LogInformation("patch {Name} r={Radius:F2}: {Count} pts, {Tris} tris", req.Name, radius, pts.Length, result.Triangles.Length / 3)
            return! json {| points = pts; triangles = result.Triangles; refDir = fromV3d result.RefDirWorld; normal = fromV3d result.NormalWorld |} next ctx
        with ex ->
            log.LogError(ex, "patch failed")
            return! RequestErrors.notFound (text ex.Message) next ctx
    }

let contactRingsHandler : HttpHandler =
    fun next ctx -> task {
        let log = ctx.GetLogger "Superserver"
        try
            let! req = ctx.BindJsonAsync<ContactRingsRequest>()
            let lm = loadMesh req.Name 0
            let radius = if req.Radius <= 0.0 then 1.0 else req.Radius
            let maxPoints = if req.MaxPoints <= 0 then 4096 else req.MaxPoints
            let rings = MeshAnalysis.contactRings lm (toV3d req.Centre) radius maxPoints
            let out = rings |> Array.map (Array.map fromV3d)
            log.LogInformation("contact-rings {Name} r={Radius:F2}: {Rings} rings, {Points} pts",
                req.Name, radius, rings.Length, (rings |> Array.sumBy Array.length))
            return! json {| rings = out |} next ctx
        with ex ->
            log.LogError(ex, "contact-rings failed")
            return! RequestErrors.notFound (text ex.Message) next ctx
    }

let probeHandler : HttpHandler =
    fun next ctx -> task {
        let log = ctx.GetLogger "Superserver"
        try
            let! req = ctx.BindJsonAsync<ProbeRequest>()
            let meshes =
                req.Meshes |> Array.map (fun m ->
                    let trafo =
                        if m.Transform <> null && m.Transform.Length = 16 then
                            let t = m.Transform
                            M44d(t.[0],  t.[1],  t.[2],  t.[3],
                                 t.[4],  t.[5],  t.[6],  t.[7],
                                 t.[8],  t.[9],  t.[10], t.[11],
                                 t.[12], t.[13], t.[14], t.[15])
                        else M44d.Identity
                    { MeshProbe.Name = m.Name; MeshProbe.Lm = loadMesh m.Name 0; MeshProbe.Transform = trafo })
            let args : MeshProbe.ProbeArgs = {
                Meshes           = meshes
                ReferenceName    = req.ReferenceName
                Centre           = toV3d req.Centre
                Radius           = if req.Radius <= 0.0 then 1.0 else req.Radius
                Length           = req.Length
                MaxPointsPerMesh = if req.MaxPointsPerMesh <= 0 then 8192 else req.MaxPointsPerMesh
            }
            match MeshProbe.run args with
            | Result.Error reason ->
                log.LogInformation("probe ref={Ref}: rejected ({Reason})", req.ReferenceName, reason)
                return! json {| ok = false; reason = reason |} next ctx
            | Result.Ok r ->
                let dists =
                    r.Distributions |> Array.map (fun d ->
                        {| name = d.Name; count = d.Count
                           median = d.Median; q1 = d.Q1; q3 = d.Q3; std = d.Std
                           bandwidth = d.Bandwidth; kde = d.Kde; samples = d.Samples |})
                let perMesh =
                    r.PerMesh |> Array.map (fun p ->
                        {| name = p.Name; iqr = p.Iqr; medianOffset = p.MedianOffset; count = p.Count |})
                log.LogInformation("probe ref={Ref} r={Radius:F2} L={Length:F1}: {Meshes} meshes, {Points} pts",
                    req.ReferenceName, args.Radius, r.Length,
                    r.Distributions.Length, (r.Distributions |> Array.sumBy (fun d -> d.Count)))
                return! json {|
                    ok = true
                    normal = fromV3d r.Normal
                    planarity = r.Planarity
                    length = r.Length
                    autoLength = r.AutoLength
                    refOffset = r.RefOffset
                    xAuto = [| fst r.XAuto; snd r.XAuto |]
                    xFit = [| fst r.XFit; snd r.XFit |]
                    distributions = dists
                    sources = {| dataset = r.DatasetError
                                 algorithm = r.AlgorithmResid
                                 conditioning = r.LocalConditioning
                                 perMesh = perMesh |}
                |} next ctx
        with ex ->
            log.LogError(ex, "probe failed")
            return! RequestErrors.notFound (text ex.Message) next ctx
    }

// Weighted rigid landmark solve for the coarse registration stage.
// Points arrive in world space at current poses; the returned transform is a
// delta mapping current-world moving points onto the reference.
let lsqPairsHandler : HttpHandler =
    fun next ctx -> task {
        let log = ctx.GetLogger "Superserver"
        try
            let! req = ctx.BindJsonAsync<LsqPairsRequest>()
            let pairs =
                if isNull (box req.Pairs) then [||]
                else
                    req.Pairs |> Array.map (fun p ->
                        toV3d p.MovingPoint, toV3d p.RefPoint, p.Weight)
            match RegMath.solveRigid pairs with
            | None ->
                return! RequestErrors.badRequest
                            (text (sprintf "lsq-pairs needs at least 3 pairs (got %d)" pairs.Length)) next ctx
            | Some r ->
                let m = r.Transform
                let flat = [|
                    m.M00; m.M01; m.M02; m.M03
                    m.M10; m.M11; m.M12; m.M13
                    m.M20; m.M21; m.M22; m.M23
                    m.M30; m.M31; m.M32; m.M33
                |]
                log.LogInformation("lsq-pairs mov={Mov}: {Pairs} pairs, rms={Rms:F4}, collinear={Coll}",
                    req.MovingName, pairs.Length,
                    sqrt ((r.PerPairResiduals |> Array.sumBy (fun x -> x * x)) / float (max 1 r.PerPairResiduals.Length)),
                    r.CollinearityWarning)
                return! json {|
                    transform = flat
                    perPairResiduals = r.PerPairResiduals
                    conditioning = {|
                        eigenvalues = r.Eigenvalues
                        collinearityWarning = r.CollinearityWarning
                    |}
                |} next ctx
        with ex ->
            log.LogError(ex, "lsq-pairs failed")
            return! RequestErrors.badRequest (text ex.Message) next ctx
    }

let icpHandler : HttpHandler =
    fun next ctx -> task {
        let log = ctx.GetLogger "Superserver"
        try
            let! req = ctx.BindJsonAsync<IcpRequest>()
            let lmRef = loadMesh req.ReferenceName 0
            let lmMov = loadMesh req.MovingName 0

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
            match MeshIcp.runIcp lmRef lmMov initial stride maxIter weights eps with
            | Result.Error reason ->
                log.LogWarning("icp ref={Ref} mov={Mov}: aborted ({Reason})",
                    req.ReferenceName, req.MovingName, reason)
                return! json {| ok = false; reason = reason |} next ctx
            | Result.Ok result ->
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
                return! json {| ok = true; transform = flat; convergence = result.Convergence; residuals = result.Residuals |} next ctx
        with ex ->
            log.LogError(ex, "icp failed")
            return! RequestErrors.notFound (text ex.Message) next ctx
    }

