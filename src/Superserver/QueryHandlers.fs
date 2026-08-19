module QueryHandlers

open System
open System.IO
open System.Threading.Tasks
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
type LsqPairDto = { RefPoint: float[]; MovingPoint: float[]; Weight: float }

[<CLIMutable>]
type LsqPairsRequest = { MovingName: string; Pairs: LsqPairDto[] }

[<CLIMutable>]
type ContactRingsRequest = { Name: string; Centre: float[]; Radius: float; MaxPoints: int }
// Point + plane normals in the mesh's server frame (the caller bakes the
// displayed pose into the normals — verticality is a pose-time property).
type PointRevealRequest = { Name: string; Point: float[]; Radii: float[]; Planes: float[][]; MaxPoints: int }

// World pose (Forward): mesh-local + centroid → world. The server is stateless
// w.r.t. registration — callers compose and send the poses explicitly.
[<CLIMutable>]
type PairMeshDto = { Name: string; Transform: float[] }

[<CLIMutable>]
type PinRoiDto = { Id: string; Centre: float[]; Radius: float }

[<CLIMutable>]
type PairErrorRequest = {
    MeshA            : PairMeshDto
    MeshB            : PairMeshDto
    Pins             : PinRoiDto[]
    Length           : float
    MaxPointsPerMesh : int
}

// Per-vertex signed distance target→reference, in the target's served vertex
// order (so the client binds it as an aligned vertex attribute). Transforms are
// world-space rigid M44 (Forward): local + centroid → world.
[<CLIMutable>]
type RegionDistanceRequest = {
    TargetName       : string
    TargetIndex      : int
    RefName          : string
    RefIndex         : int
    TargetTransform  : float[]
    RefTransform     : float[]
}

// Per-vertex Euclidean distance of each target vertex to the other mesh's
// surface (unsigned, no overlap gate — the client's placement feather compares
// it against a live radius), in the target's served vertex order.
[<CLIMutable>]
type PairProximityRequest = {
    TargetName       : string
    TargetIndex      : int
    OtherName        : string
    OtherIndex       : int
    TargetTransform  : float[]
    OtherTransform   : float[]
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

// One error shell for every query handler: malformed / degenerate bodies → 400,
// a missing dataset/mesh → 404, anything unexpected → 500.
let private tryQuery (name : string) (body : HttpContext -> Task<HttpHandler>) : HttpHandler =
    fun next ctx -> task {
        let log = ctx.GetLogger "Superserver"
        try
            let! h = body ctx
            return! h next ctx
        with
        | :? Text.Json.JsonException
        | :? NullReferenceException
        | :? IndexOutOfRangeException
        | :? ArgumentException
        | :? FormatException as ex ->
            log.LogWarning("{Handler}: bad request ({Message})", name, ex.Message)
            return! RequestErrors.badRequest (text ex.Message) next ctx
        | :? FileNotFoundException
        | :? DirectoryNotFoundException
        | :? Collections.Generic.KeyNotFoundException as ex ->
            log.LogWarning("{Handler}: not found ({Message})", name, ex.Message)
            return! RequestErrors.notFound (text ex.Message) next ctx
        | ex ->
            log.LogError(ex, "{Handler} failed", name)
            return! ServerErrors.internalError (text ex.Message) next ctx
    }

let rayHandler : HttpHandler =
    tryQuery "ray" (fun ctx -> task {
        let! req = ctx.BindJsonAsync<RayRequest>()
        let lm   = loadMesh req.Name req.Index
        let c    = lm.parsed.centroid
        let orig = V3f(toV3d req.Origin - c)
        let dir  = V3f(toV3d req.Direction)
        let mutable hit = RayHit()
        let ok = lm.scene.Intersect(orig, dir, &hit)
        if ok then
            let worldHit = V3d(orig + dir * hit.T) + c
            return json {| hit = true; t = hit.T; point = fromV3d worldHit; triangleId = int hit.PrimitiveId |}
        else
            return json {| hit = false |}
    })

let closestHandler : HttpHandler =
    tryQuery "closest" (fun ctx -> task {
        let! req = ctx.BindJsonAsync<ClosestRequest>()
        let lm   = loadMesh req.Name req.Index
        let c    = lm.parsed.centroid
        let res  = lm.scene.GetClosestPoint(V3f(toV3d req.Point - c))
        if res.IsValid then
            let worldPt = V3d res.Point + c
            return json {| found = true; point = fromV3d worldPt; distanceSquared = res.DistanceSquared; triangleId = int res.PrimID |}
        else
            return json {| found = false |}
    })

let private mat16 (a : float[]) =
    if not (isNull a) && a.Length = 16 then
        M44d(a.[0],  a.[1],  a.[2],  a.[3],
             a.[4],  a.[5],  a.[6],  a.[7],
             a.[8],  a.[9],  a.[10], a.[11],
             a.[12], a.[13], a.[14], a.[15])
    else M44d.Identity

// Per-vertex signed M3C2-style distance (cloud-to-mesh), signed by the ref
// surface normal at the closest point. Support rule: a vertex responds only
// where the vertical world line through it pierces the reference (the meshes
// overlap in Z there); everywhere else → large sentinel the shader treats as
// "no encoding". Without that gate the closest-point distance fabricates error
// along the fringe of non-overlapping regions.
let regionDistanceHandler : HttpHandler =
    tryQuery "region-distance" (fun ctx -> task {
        let log = ctx.GetLogger "Superserver"
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
        let dnLocal = (rInv.TransformDir (V3d(0.0, 0.0, -1.0))).Normalized
        let upLocal = (rInv.TransformDir (V3d(0.0, 0.0,  1.0))).Normalized
        // M3C2 closest point with the Z-overlap support gate: the vertical
        // line through the vertex must pierce the reference, else no response.
        Parallel.For(0, pos.Length, fun i ->
                let vWorld = tT.TransformPos (V3d pos.[i] + cT)
                let vRefLocal = rInv.TransformPos vWorld - cR
                let mutable h = RayHit()
                let overlapsZ =
                    lmR.scene.Intersect(V3f vRefLocal, V3f dnLocal, &h)
                    || lmR.scene.Intersect(V3f vRefLocal, V3f upLocal, &h)
                if not overlapsZ then dist.[i] <- 1e30f
                else
                    let res = lmR.scene.GetClosestPoint(V3f vRefLocal)
                    if res.IsValid then
                        let d  = sqrt (float res.DistanceSquared)
                        let cp = V3d res.Point
                        let pid = int res.PrimID
                        if pid * 3 + 2 < refIdx.Length then
                            let p0 = V3d refPos.[refIdx.[pid*3]]
                            let p1 = V3d refPos.[refIdx.[pid*3+1]]
                            let p2 = V3d refPos.[refIdx.[pid*3+2]]
                            let nrm = Vec.cross (p1 - p0) (p2 - p0)
                            let nl  = nrm.Length
                            let s   = if nl > 1e-12 && Vec.dot (vRefLocal - cp) (nrm / nl) < 0.0 then -1.0 else 1.0
                            dist.[i] <- float32 (s * d)
                        else dist.[i] <- float32 d
                    else dist.[i] <- 1e30f) |> ignore
        log.LogInformation("region-distance {Target} vs {Ref}: {Verts} verts", req.TargetName, req.RefName, pos.Length)
        return json {| dist = dist |}
    })

// Plain closest-point distance per target vertex — deliberately WITHOUT
// region-distance's Z-overlap gate: the placement feather needs the lateral
// reach beyond the strict footprint that the gate exists to suppress.
let pairProximityHandler : HttpHandler =
    tryQuery "pair-proximity" (fun ctx -> task {
        let log = ctx.GetLogger "Superserver"
        let! req = ctx.BindJsonAsync<PairProximityRequest>()
        let lmT = loadMesh req.TargetName req.TargetIndex
        let lmO = loadMesh req.OtherName req.OtherIndex
        let tT  = mat16 req.TargetTransform
        let oInv = (mat16 req.OtherTransform).Inverse
        let cT  = lmT.parsed.centroid
        let cO  = lmO.parsed.centroid
        let pos = lmT.parsed.positions
        let dist = Array.zeroCreate<float32> pos.Length
        Parallel.For(0, pos.Length, fun i ->
                let vWorld = tT.TransformPos (V3d pos.[i] + cT)
                let vOtherLocal = oInv.TransformPos vWorld - cO
                let res = lmO.scene.GetClosestPoint(V3f vOtherLocal)
                dist.[i] <- if res.IsValid then sqrt (float32 res.DistanceSquared) else 1e30f) |> ignore
        log.LogInformation("pair-proximity {Target} vs {Other}: {Verts} verts", req.TargetName, req.OtherName, pos.Length)
        return json {| dist = dist |}
    })

let contactRingsHandler : HttpHandler =
    tryQuery "contact-rings" (fun ctx -> task {
        let log = ctx.GetLogger "Superserver"
        let! req = ctx.BindJsonAsync<ContactRingsRequest>()
        let lm = loadMesh req.Name 0
        let radius = if req.Radius <= 0.0 then 1.0 else req.Radius
        let maxPoints = if req.MaxPoints <= 0 then 4096 else min req.MaxPoints 65536
        let rings = MeshAnalysis.contactRings lm (toV3d req.Centre) radius maxPoints
        let out = rings |> Array.map (Array.map fromV3d)
        log.LogInformation("contact-rings {Name} r={Radius:F2}: {Rings} rings, {Points} pts",
            req.Name, radius, rings.Length, (rings |> Array.sumBy Array.length))
        return json {| rings = out |}
    })

let pointRevealHandler : HttpHandler =
    tryQuery "point-reveal" (fun ctx -> task {
        let log = ctx.GetLogger "Superserver"
        let! req = ctx.BindJsonAsync<PointRevealRequest>()
        let lm = loadMesh req.Name 0
        let radii = req.Radii |> Array.filter (fun r -> r > 0.0)
        let planes = req.Planes |> Array.map toV3d
        let maxPoints = if req.MaxPoints <= 0 then 2048 else min req.MaxPoints 65536
        let lines = MeshAnalysis.pointReveal lm (toV3d req.Point) radii planes maxPoints
        log.LogInformation("point-reveal {Name}: {Lines} lines, {Points} pts",
            req.Name, lines.Length, (lines |> Array.sumBy Array.length))
        return json {| lines = lines |> Array.map (Array.map fromV3d) |}
    })

let private pairMesh (dto : PairMeshDto) : PairError.PairMesh =
    { Name = dto.Name; Lm = loadMesh dto.Name 0; Transform = mat16 dto.Transform }

// Symmetric pairwise pin error at explicit poses — see the PairError module
// doc-comment for the measure and sign convention. A pin without overlap
// reports per-pin ok=false; the batch itself never fails on it.
let pairErrorHandler : HttpHandler =
    tryQuery "pair-error" (fun ctx -> task {
        let log = ctx.GetLogger "Superserver"
        let! req = ctx.BindJsonAsync<PairErrorRequest>()
        if isNull (box req.Pins) || req.Pins.Length = 0 then
            raise (ArgumentException "pair-error needs at least one pin")
        let toRoi (p : PinRoiDto) : PairError.PinRoi =
            { Id = p.Id; Centre = toV3d p.Centre
              Radius = if p.Radius <= 0.0 then 1.0 else p.Radius }
        let args : PairError.PairErrorArgs = {
            A                = pairMesh req.MeshA
            B                = pairMesh req.MeshB
            Pins             = req.Pins |> Array.map toRoi
            Length           = req.Length
            MaxPointsPerMesh = if req.MaxPointsPerMesh <= 0 then 8192 else min req.MaxPointsPerMesh 65536
        }
        let pins = PairError.run args
        log.LogInformation("pair-error {A} × {B}: {Pins} pins ({Ok} ok), {Points} samples",
            req.MeshA.Name, req.MeshB.Name, pins.Length,
            (pins |> Array.sumBy (fun p -> if p.Ok then 1 else 0)),
            (pins |> Array.sumBy (fun p -> p.Count)))
        return json {|
            pins = pins |> Array.map (fun p ->
                {| id = p.Id; ok = p.Ok; reason = p.Reason
                   normal = fromV3d p.Normal
                   count = p.Count; median = p.Median; lodHalfWidth = p.LodHalfWidth
                   samples = p.Samples; positions = p.Positions |})
        |}
    })

[<CLIMutable>]
type PairPointErrorRequest = {
    MeshA   : PairMeshDto
    MeshB   : PairMeshDto
    Point   : float[]
    Radius  : float
    MaxDist : float
}

// Exact pairwise error at one picked world point (hover readout / armed probe).
let pairErrorAtHandler : HttpHandler =
    tryQuery "pair-error-at" (fun ctx -> task {
        let log = ctx.GetLogger "Superserver"
        let! req = ctx.BindJsonAsync<PairPointErrorRequest>()
        let args : PairError.PointErrorArgs = {
            A       = pairMesh req.MeshA
            B       = pairMesh req.MeshB
            Point   = toV3d req.Point
            Radius  = if req.Radius <= 0.0 then 1.0 else req.Radius
            MaxDist = if req.MaxDist <= 0.0 then 100.0 else req.MaxDist
        }
        match PairError.atPoint args with
        | Result.Error reason ->
            log.LogInformation("pair-error-at {A} × {B}: rejected ({Reason})", req.MeshA.Name, req.MeshB.Name, reason)
            return json {| ok = false; reason = reason |}
        | Result.Ok r ->
            log.LogInformation("pair-error-at {A} × {B}: {Value:F4} m", req.MeshA.Name, req.MeshB.Name, r.Value)
            return json {|
                ok = true; value = r.Value; normal = fromV3d r.Normal
                pointA = fromV3d r.PointA; pointB = fromV3d r.PointB
            |}
    })

[<CLIMutable>]
type PairOverlapRequest = {
    MeshA       : PairMeshDto
    MeshB       : PairMeshDto
    MaxDist     : float
    MinFraction : float
    MaxSamples  : int
}

// Cheap overlap sufficiency — drives the pair matrix "possible vs impossible".
let pairOverlapHandler : HttpHandler =
    tryQuery "pair-overlap" (fun ctx -> task {
        let log = ctx.GetLogger "Superserver"
        let! req = ctx.BindJsonAsync<PairOverlapRequest>()
        let args : PairError.OverlapArgs = {
            A           = pairMesh req.MeshA
            B           = pairMesh req.MeshB
            MaxDist     = req.MaxDist
            MinFraction = req.MinFraction
            MaxSamples  = req.MaxSamples
        }
        let r = PairError.overlap args
        log.LogInformation("pair-overlap {A} × {B}: {Sufficient} (A→B {FracAB:F2}, B→A {FracBA:F2}, d≤{MaxDist:F1} m)",
            req.MeshA.Name, req.MeshB.Name, r.Sufficient, r.FracAB, r.FracBA, r.MaxDist)
        return json {|
            sufficient = r.Sufficient
            fracAB = r.FracAB; fracBA = r.FracBA
            maxDist = r.MaxDist
            samplesA = r.SamplesA; samplesB = r.SamplesB
        |}
    })

[<CLIMutable>]
type RoiFitRequest = {
    OtherName      : string
    OtherTransform : float[]
    Centre         : float[]
    Radius         : float
    MinVerts       : int
    MaxFactor      : float
}

// Adaptive ROI fit: the smallest radius ≥ Radius whose sphere at Centre
// captures ≥ MinVerts vertices of the OTHER pair mesh, capped at
// Radius×MaxFactor — a correspondence ROI needs both surfaces present.
// ok=false when even the cap captures fewer than MinVerts.
let roiFitHandler : HttpHandler =
    tryQuery "roi-fit" (fun ctx -> task {
        let log = ctx.GetLogger "Superserver"
        let! req = ctx.BindJsonAsync<RoiFitRequest>()
        let lm  = loadMesh req.OtherName 0
        let inv = (mat16 req.OtherTransform).Inverse
        let local = inv.TransformPos (toV3d req.Centre) - lm.parsed.centroid
        let r0 = if req.Radius <= 0.0 then 1.0 else req.Radius
        let minVerts = if req.MinVerts <= 0 then 20 else req.MinVerts
        let cap = r0 * (if req.MaxFactor <= 1.0 then 4.0 else req.MaxFactor)
        let cap2 = cap * cap
        let pos = lm.parsed.positions
        // One pass collecting the squared distances within the cap: the
        // MinVerts-th smallest IS the minimal radius.
        let within = System.Collections.Generic.List<float>()
        for i in 0 .. pos.Length - 1 do
            let d2 = (V3d pos.[i] - local).LengthSquared
            if d2 <= cap2 then within.Add d2
        if within.Count < minVerts then
            log.LogInformation("roi-fit {Name}: refused ({Count} < {Min} verts within cap {Cap:F2})",
                req.OtherName, within.Count, minVerts, cap)
            return json {| ok = false; radius = cap; count = within.Count |}
        else
            within.Sort()
            let rN = sqrt within.[minVerts - 1] * 1.001
            let r = max r0 (min cap rN)
            log.LogInformation("roi-fit {Name}: r {R0:F2} → {R:F2} ({Count} verts within cap)",
                req.OtherName, r0, r, within.Count)
            return json {| ok = true; radius = r; count = within.Count |}
    })

// Points arrive in world space at current poses; the returned transform is a
// delta mapping them onto the reference.
let lsqPairsHandler : HttpHandler =
    tryQuery "lsq-pairs" (fun ctx -> task {
        let log = ctx.GetLogger "Superserver"
        let! req = ctx.BindJsonAsync<LsqPairsRequest>()
        let pairs =
            if isNull (box req.Pairs) then [||]
            else
                req.Pairs |> Array.map (fun p ->
                    toV3d p.MovingPoint, toV3d p.RefPoint, p.Weight)
        match RegMath.solveRigid pairs with
        | None ->
            return RequestErrors.badRequest
                        (text (sprintf "lsq-pairs needs at least 3 pairs (got %d)" pairs.Length))
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
            return json {|
                transform = flat
                perPairResiduals = r.PerPairResiduals
                conditioning = {|
                    eigenvalues = r.Eigenvalues
                    collinearityWarning = r.CollinearityWarning
                |}
            |}
    })

