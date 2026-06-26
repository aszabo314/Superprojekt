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
    Name: string; Centre: float[]; Radius: float
    // Triangle budget; over it the patch thins by a uniform stride.
    MaxTris: int
    // Optional shared-frame override (mesh-frame dirs); both must be present, else
    // null → local plane fit.
    FrameNormal: float[]; FrameRefDir: float[]
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

// Per-vertex signed distance target→reference, in the target's served vertex
// order (so the client binds it as an aligned vertex attribute). Transforms are
// world-space rigid M44 (Forward), matching the probe convention.
[<CLIMutable>]
type RegionDistanceRequest = {
    TargetName       : string
    TargetIndex      : int
    RefName          : string
    RefIndex         : int
    TargetTransform  : float[]
    RefTransform     : float[]
    // 0 = signed M3C2 closest-point (default), 1 = vertical Z difference.
    Mode             : int
}

[<CLIMutable>]
type MeshPreviewRequest = {
    Name            : string
    RefName         : string
    // Surface pose (= solved/tip pose for displacement).
    Transform       : float[]
    // Displacement base (load) pose; ignored for every other channel.
    Transform2      : float[]
    RefTransform    : float[]
    // 0 = Pano, 1 = Top, 2 = Front, 3 = Side, 4 = Oblique.
    Projection      : int
    // 0 = own origin, 1 = reference origin (pano eye).
    OriginMode      : int
    // 0 = Shade, 1 = M3C2, 2 = Zdiff, 3 = Incidence, 4 = Range, 5 = Shape, 6 = Displacement.
    Channel         : int
    MaxTris         : int
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

let private mat16 (a : float[]) =
    if not (isNull a) && a.Length = 16 then
        M44d(a.[0],  a.[1],  a.[2],  a.[3],
             a.[4],  a.[5],  a.[6],  a.[7],
             a.[8],  a.[9],  a.[10], a.[11],
             a.[12], a.[13], a.[14], a.[15])
    else M44d.Identity

// Per-vertex signed M3C2-style distance (cloud-to-mesh), signed by the ref
// surface normal at the closest point. No closest point → large sentinel the
// shader treats as "no encoding".
let regionDistanceHandler : HttpHandler =
    fun next ctx -> task {
        let log = ctx.GetLogger "Superserver"
        try
            let! req = ctx.BindJsonAsync<RegionDistanceRequest>()
            let lmT = loadMesh req.TargetName req.TargetIndex
            let lmR = loadMesh req.RefName req.RefIndex
            let tT  = mat16 req.TargetTransform
            let rInv = (mat16 req.RefTransform).Inverse
            let cT  = lmT.parsed.centroid
            let cR  = lmR.parsed.centroid
            let pos = lmT.parsed.positions
            let refPos = lmR.parsed.positions
            let refIdx = lmR.parsed.indices
            let dist = Array.zeroCreate<float32> pos.Length
            if req.Mode = 1 then
                // z-diff: vertical world ray onto the reference; signed Δz
                // (moving above reference → positive). Down then up.
                let dnLocal = (rInv.TransformDir (V3d(0.0, 0.0, -1.0))).Normalized
                let upLocal = (rInv.TransformDir (V3d(0.0, 0.0,  1.0))).Normalized
                System.Threading.Tasks.Parallel.For(0, pos.Length, fun i ->
                    let vWorld = tT.TransformPos (V3d pos.[i] + cT)
                    let vRefLocal = rInv.TransformPos vWorld - cR
                    let mutable hd = RayHit()
                    if lmR.scene.Intersect(V3f vRefLocal, V3f dnLocal, &hd) then
                        dist.[i] <- float32 hd.T
                    else
                        let mutable hu = RayHit()
                        if lmR.scene.Intersect(V3f vRefLocal, V3f upLocal, &hu) then
                            dist.[i] <- float32 (- hu.T)
                        else dist.[i] <- 1e30f) |> ignore
            else
                System.Threading.Tasks.Parallel.For(0, pos.Length, fun i ->
                    let vWorld = tT.TransformPos (V3d pos.[i] + cT)
                    let vRefLocal = rInv.TransformPos vWorld - cR
                    let res = lmR.scene.GetClosestPoint(V3f vRefLocal)
                    if res.IsValid then
                        let cp = V3d res.Point
                        let pid = int res.PrimID
                        if pid * 3 + 2 < refIdx.Length then
                            let p0 = V3d refPos.[refIdx.[pid*3]]
                            let p1 = V3d refPos.[refIdx.[pid*3+1]]
                            let p2 = V3d refPos.[refIdx.[pid*3+2]]
                            let nrm = Vec.cross (p1 - p0) (p2 - p0)
                            let nl  = nrm.Length
                            let s   = if nl > 1e-12 && Vec.dot (vRefLocal - cp) (nrm / nl) < 0.0 then -1.0 else 1.0
                            dist.[i] <- float32 (s * sqrt (float res.DistanceSquared))
                        else dist.[i] <- float32 (sqrt (float res.DistanceSquared))
                    else dist.[i] <- 1e30f) |> ignore
            log.LogInformation("region-distance {Target} vs {Ref}: {Verts} verts", req.TargetName, req.RefName, pos.Length)
            return! json {| dist = dist |} next ctx
        with ex ->
            log.LogError(ex, "region-distance failed")
            return! RequestErrors.notFound (text ex.Message) next ctx
    }

let meshPreviewHandler : HttpHandler =
    fun next ctx -> task {
        let log = ctx.GetLogger "Superserver"
        try
            let! req = ctx.BindJsonAsync<MeshPreviewRequest>()
            let lm  = loadMesh req.Name 0
            let lmR = loadMesh req.RefName 0
            let r =
                MeshPreview.preview lm lmR (mat16 req.Transform) (mat16 req.Transform2) (mat16 req.RefTransform)
                    (MeshPreview.projectionOfInt req.Projection)
                    (MeshPreview.originOfInt req.OriginMode)
                    (MeshPreview.channelOfInt req.Channel)
                    (if req.MaxTris <= 0 then 6000 else req.MaxTris)
            log.LogInformation("mesh-preview {Name} proj={Proj} ch={Ch}: {Verts} verts, {Tris} tris, {Arrows} arrows",
                req.Name, req.Projection, req.Channel, r.Verts2d.Length, r.Tris.Length / 3, r.DispMag.Length)
            return! json {| verts2d = r.Verts2d; tris = r.Tris; scalar = r.Scalar; lo = r.Lo; hi = r.Hi
                            dispBase = r.DispBase; dispTip = r.DispTip; dispMag = r.DispMag |} next ctx
        with ex ->
            log.LogError(ex, "mesh-preview failed")
            return! RequestErrors.notFound (text ex.Message) next ctx
    }

let patchHandler : HttpHandler =
    fun next ctx -> task {
        let log = ctx.GetLogger "Superserver"
        try
            let! req = ctx.BindJsonAsync<PatchRequest>()
            let lm = loadMesh req.Name 0
            let centre = toV3d req.Centre
            let radius = if req.Radius <= 0.0 then 1.0 else req.Radius
            let maxTris = if req.MaxTris <= 0 then 200000 else req.MaxTris
            let frame =
                if not (isNull req.FrameNormal) && req.FrameNormal.Length = 3
                   && not (isNull req.FrameRefDir) && req.FrameRefDir.Length = 3 then
                    Some (toV3d req.FrameNormal, toV3d req.FrameRefDir)
                else None
            let result = MeshAnalysis.patch lm centre radius maxTris frame
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
                           bandwidth = d.Bandwidth; kde = d.Kde; samples = d.Samples
                           intr = d.Intrinsics |})
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

// Weighted rigid landmark solve. Points arrive in world space at current poses;
// the returned transform is a delta mapping them onto the reference.
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

