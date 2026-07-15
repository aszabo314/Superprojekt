module Supertests.Program

// Unit tests for the pure registration + slice-geometry pieces (RegMath,
// RegConditioning, Readiness, MeshAnalysisCore). Plain console runner (exit
// code = failure count) so no new packages enter the paket lock.

open System
open Aardvark.Base
open Superprojekt

let mutable failures = 0
let mutable total = 0

let check (name : string) (cond : bool) =
    total <- total + 1
    if cond then printfn "ok    %s" name
    else
        failures <- failures + 1
        printfn "FAIL  %s" name

let checkLe (name : string) (v : float) (tol : float) =
    check (sprintf "%s (%.3e ≤ %.0e)" name v tol) (v <= tol)

let rng = Random(20260611)
let randV3 (scale : float) =
    V3d(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5, rng.NextDouble() - 0.5) * 2.0 * scale

// Rodrigues — avoids depending on any particular Rot3d API surface.
let rotation (axis : V3d) (angle : float) =
    let k = axis.Normalized
    let K = M33d(0.0, -k.Z, k.Y, k.Z, 0.0, -k.X, -k.Y, k.X, 0.0)
    M33d.Identity + K * sin angle + K * K * (1.0 - cos angle)

let applyRigid (r : M33d) (t : V3d) (p : V3d) = r * p + t

let rotPart (m : M44d) =
    M33d(m.M00, m.M01, m.M02, m.M10, m.M11, m.M12, m.M20, m.M21, m.M22)

let maxAbsDiff (a : M44d) (b : M44d) =
    [| a.M00-b.M00; a.M01-b.M01; a.M02-b.M02; a.M03-b.M03
       a.M10-b.M10; a.M11-b.M11; a.M12-b.M12; a.M13-b.M13
       a.M20-b.M20; a.M21-b.M21; a.M22-b.M22; a.M23-b.M23
       a.M30-b.M30; a.M31-b.M31; a.M32-b.M32; a.M33-b.M33 |]
    |> Array.map abs |> Array.max

// ───────────────────────── RegMath: weighted Umeyama ──────────────────────

let umeyamaTests () =
    // (a) exact recovery of a random rigid transform, n = 3 / 4 / 50.
    for n in [ 3; 4; 50 ] do
        let r = rotation (randV3 1.0) ((rng.NextDouble() - 0.5) * 2.0)
        let t = randV3 10.0
        let pts = Array.init n (fun _ -> randV3 10.0)
        let pairs = pts |> Array.map (fun m -> m, applyRigid r t m, 1.0)
        match RegMath.solveRigid pairs with
        | None -> check (sprintf "recovery n=%d solvable" n) false
        | Some res ->
            let maxResidual = if res.PerPairResiduals.Length = 0 then infinity else Array.max res.PerPairResiduals
            checkLe (sprintf "recovery n=%d residuals" n) maxResidual 1e-9
            let rr = rotPart res.Transform
            checkLe (sprintf "recovery n=%d det(R)=+1" n) (abs (rr.Determinant - 1.0)) 1e-9

    // UTM-scale coordinates: same recovery, looser absolute tolerance.
    let rU = rotation (randV3 1.0) 0.3
    let tU = randV3 50.0
    let offset = V3d(512345.0, 5212345.0, 800.0)
    let ptsU = Array.init 20 (fun _ -> offset + randV3 200.0)
    let pairsU = ptsU |> Array.map (fun m -> m, applyRigid rU tU m, 1.0)
    match RegMath.solveRigid pairsU with
    | None -> check "recovery UTM solvable" false
    | Some res ->
        checkLe "recovery UTM residuals" (Array.max res.PerPairResiduals) 1e-5

    // (b) reflection-prone sets: a mirrored 3D set's best orthogonal map is a
    // reflection — the solver must still return det(R) = +1.
    let planar = Array.init 12 (fun _ -> let v = randV3 5.0 in V3d(v.X, v.Y, 0.0))
    let tilted = Array.init 12 (fun _ -> randV3 5.0)
    let reflPairs = tilted |> Array.map (fun m -> m, V3d(m.X, m.Y, -m.Z), 1.0)
    match RegMath.solveRigid reflPairs with
    | None -> check "reflection case solvable" false
    | Some res ->
        checkLe "reflection case det(R)=+1" (abs ((rotPart res.Transform).Determinant - 1.0)) 1e-9
    // Planar (rank-2) set under a genuine rotation still recovers exactly.
    let rP = rotation (V3d(0.3, -0.2, 0.93)) 0.7
    let tP = V3d(3.0, -1.0, 2.0)
    let planarPairs = planar |> Array.map (fun m -> m, applyRigid rP tP m, 1.0)
    match RegMath.solveRigid planarPairs with
    | None -> check "planar recovery solvable" false
    | Some res ->
        checkLe "planar recovery residuals" (Array.max res.PerPairResiduals) 1e-9
        checkLe "planar recovery det(R)=+1" (abs ((rotPart res.Transform).Determinant - 1.0)) 1e-9

    // (c) weights: duplicating a pair ≡ weight 2.0 on it.
    let basePts = Array.init 6 (fun _ -> randV3 8.0)
    let rW = rotation (randV3 1.0) 0.4
    let tW = randV3 5.0
    // make it a non-exact fit so weighting matters
    let noise = Array.init 6 (fun _ -> randV3 0.2)
    let mkPair i w = basePts.[i], applyRigid rW tW basePts.[i] + noise.[i], w
    let dup = Array.append (Array.init 6 (fun i -> mkPair i 1.0)) [| mkPair 0 1.0 |]
    let weighted = Array.init 6 (fun i -> mkPair i (if i = 0 then 2.0 else 1.0))
    match RegMath.solveRigid dup, RegMath.solveRigid weighted with
    | Some a, Some b ->
        checkLe "weight 2.0 ≡ duplicated pair" (maxAbsDiff a.Transform b.Transform) 1e-10
    | _ -> check "weight test solvable" false

    // all-zero weights degrade to the uniform problem instead of NaN
    let zeroW = basePts |> Array.map (fun m -> m, applyRigid rW tW m, 0.0)
    match RegMath.solveRigid zeroW with
    | Some res -> checkLe "zero weights fall back to uniform" (Array.max res.PerPairResiduals) 1e-9
    | None -> check "zero weights solvable" false

    // (d) collinear moving points → warning; spread points → no warning.
    let lineDir = (randV3 1.0).Normalized
    let linePts = Array.init 8 (fun i -> lineDir * (float i * 2.0))
    let linePairs = linePts |> Array.map (fun m -> m, m + V3d(1.0, 2.0, 3.0), 1.0)
    match RegMath.solveRigid linePairs with
    | Some res -> check "collinear set flags warning" res.CollinearityWarning
    | None -> check "collinear set solvable" false
    match RegMath.solveRigid pairsU with
    | Some res -> check "spread set has no collinearity warning" (not res.CollinearityWarning)
    | None -> ()

    // (e) fewer than 3 pairs is rejected (the HTTP handler maps this to 400).
    check "<3 pairs rejected" ((RegMath.solveRigid [| V3d.Zero, V3d.Zero, 1.0; V3d.IOO, V3d.IOO, 1.0 |]).IsNone)

    // (f) negative weights clamp to 0 — a pair with weight −5 must behave
    // exactly like weight 0 (it cannot pull the solve).
    let outlier i = basePts.[i], applyRigid rW tW basePts.[i] + V3d(50.0, -30.0, 20.0), -5.0
    let negW = Array.init 6 (fun i -> if i = 0 then outlier i else mkPair i 1.0)
    let zeroWOutlier = Array.init 6 (fun i -> if i = 0 then (let (m, r, _) = outlier i in m, r, 0.0) else mkPair i 1.0)
    match RegMath.solveRigid negW, RegMath.solveRigid zeroWOutlier with
    | Some a, Some b ->
        checkLe "negative weight ≡ weight 0" (maxAbsDiff a.Transform b.Transform) 1e-10
    | _ -> check "negative weight solvable" false

// ───────────────────────── RegConditioning sanity ─────────────────────────

let conditioningTests () =
    // λ2/λ1 < 1e-3 is the near-collinear threshold Readiness.compute applies.
    let line = Array.init 6 (fun i -> V3d(float i, 0.0, 0.0), 1.0)
    check "collinear spread flagged" (RegConditioning.lambdaRatio (RegConditioning.spreadEigenvalues line) < 1e-3)
    let spread = Array.init 24 (fun _ -> randV3 10.0, 1.0)
    check "spread set not flagged" (RegConditioning.lambdaRatio (RegConditioning.spreadEigenvalues spread) >= 1e-3)

// ─────────────────── Workflow panel: readiness engine ─────────────────────

let private mkRPin refAnchor (accepted : string list) : ReadinessPin =
    { RefAnchor = refAnchor
      Accepted = Set.ofList accepted }

let readinessTests () =
    let baseInput = {
        ReferenceMesh       = Some "ref"
        MovingMeshes = [ "A"; "B" ]
        EnabledPins         = []
    }
    let ready (d : Diagnostic list) = d |> List.filter (fun x -> x.Severity = Severity.Ready)
    // non-collinear spread (parabola) with anchors accepted on both meshes
    let pinsN n =
        List.init n (fun i ->
            mkRPin (Some (V3d(float i * 3.0, float (i * i), 0.5), 1.0)) [ "A"; "B" ])

    let d = Readiness.compute { baseInput with ReferenceMesh = None }
    check "no-ref blocker"
        (d |> List.exists (fun x -> x.Severity = Blocker && x.Text.Contains "reference"))
    check "no-ref never ready" (ready d |> List.isEmpty)

    let d = Readiness.compute baseInput
    check "zero pins blocker" (d |> List.exists (fun x -> x.Severity = Blocker && x.Text.Contains "≥3 pins"))

    let d = Readiness.compute { baseInput with EnabledPins = pinsN 2 }
    check "zero solvable meshes is the hard blocker"
        (d |> List.exists (fun x -> x.Severity = Blocker && x.Text.Contains "≥3 markers"))
    check "no per-mesh marker hints (superseded by the matrix)"
        (not (d |> List.exists (fun x -> x.Text.Contains "marker(s)")))
    check "2 pins not ready" (ready d |> List.isEmpty)

    let d = Readiness.compute { baseInput with EnabledPins = pinsN 3 }
    check "3 pins → exactly one Ready" (ready d |> List.length = 1)
    check "no blockers when clear" (d |> List.forall (fun x -> x.Severity <> Blocker))

    let pinU = mkRPin (Some (V3d(9.0, 1.0, 2.0), 1.0)) [ "A" ]
    let d = Readiness.compute { baseInput with EnabledPins = pinU :: pinsN 3 }
    check "no per-pin unresolved hints (superseded by the matrix)"
        (not (d |> List.exists (fun x -> x.Text.Contains "without a marker")))
    check "extra pins still ready" (ready d |> List.length = 1)

    let colinear =
        List.init 4 (fun i -> mkRPin (Some (V3d(float i, 0.0, 0.0), 1.0)) [ "A"; "B" ])
    let d = Readiness.compute { baseInput with EnabledPins = colinear }
    check "collinear anchors → warning" (d |> List.exists (fun x -> x.Text.Contains "near-collinear"))
    let d = Readiness.compute { baseInput with EnabledPins = pinsN 4 }
    check "spread anchors → no collinear warning"
        (not (d |> List.exists (fun x -> x.Text.Contains "near-collinear")))

    let d = Readiness.compute { baseInput with MovingMeshes = []; EnabledPins = pinsN 3 }
    check "no moving meshes → Info" (d |> List.exists (fun x -> x.Severity = Info && x.Text.Contains "No moving meshes"))
    check "no moving meshes never ready" (ready d |> List.isEmpty)

    let counts =
        Readiness.pairCounts
            { baseInput with
                EnabledPins = [ mkRPin (Some (V3d.Zero, 1.0)) [ "A" ]
                                mkRPin (Some (V3d.IOO, 1.0)) [ "A"; "B" ] ] }
    check "pairCounts counts markers per mesh" (counts = [ "A", 2; "B", 1 ])

// ─────────────── MeshAnalysisCore: level-set tracer · decimate · dip ───────

// Triangulated n×n-cell grid in the XY plane: vertex (i, j) at (i, j, 0).
let private gridMesh (n : int) =
    let verts = Array.init ((n + 1) * (n + 1)) (fun k -> V3d(float (k % (n + 1)), float (k / (n + 1)), 0.0))
    let tris = ResizeArray<int>()
    for j in 0 .. n - 1 do
        for i in 0 .. n - 1 do
            let v00 = j * (n + 1) + i
            let v10 = v00 + 1
            let v01 = v00 + (n + 1)
            let v11 = v01 + 1
            tris.AddRange [ v00; v10; v11 ]
            tris.AddRange [ v00; v11; v01 ]
    verts, tris.ToArray()

let sliceCoreTests () =
    let verts, tris = gridMesh 20
    let lerpRoot (sd : int -> float) (i0 : int) (i1 : int) =
        let d0 = sd i0
        let d1 = sd i1
        verts.[i0] + (d0 / (d0 - d1)) * (verts.[i1] - verts.[i0])

    // (a) circle field fully inside the grid → ONE closed chain (first point
    // repeated last) whose points sit on the circle.
    let c = V3d(10.0, 10.0, 0.0)
    let r = 5.0
    let sdC (i : int) = (verts.[i] - c).Length - r
    let chains = MeshAnalysisCore.traceLevelSet tris sdC (lerpRoot sdC)
    check "circle field → one chain" (chains.Length = 1)
    if chains.Length = 1 then
        let ch = chains.[0]
        check "circle chain closed (first = last)" (ch.Length >= 8 && ch.[0] = ch.[ch.Length - 1])
        checkLe "circle chain on the circle"
            (ch |> Array.map (fun p -> abs ((p - c).Length - r)) |> Array.max) 0.35

    // (b) plane field x − 10.5 → ONE open chain spanning the grid at x = 10.5.
    let sdP (i : int) = verts.[i].X - 10.5
    let chainsP = MeshAnalysisCore.traceLevelSet tris sdP (lerpRoot sdP)
    check "plane field → one open chain"
        (chainsP.Length = 1 && chainsP.[0].[0] <> chainsP.[0].[chainsP.[0].Length - 1])
    if chainsP.Length = 1 then
        let ys = chainsP.[0] |> Array.map (fun p -> p.Y)
        check "plane chain spans the grid" (Array.min ys <= 0.5 && Array.max ys >= 19.5)
        checkLe "plane chain at x = 10.5"
            (chainsP.[0] |> Array.map (fun p -> abs (p.X - 10.5)) |> Array.max) 1e-9

    // (c) decimate: endpoints kept exactly, total capped, no-op under the cap.
    let long = [| Array.init 500 float; Array.init 300 (fun i -> 1000.0 + float i) |]
    let dec = MeshAnalysisCore.decimate 100 long
    check "decimate keeps endpoints"
        ((long, dec) ||> Array.forall2 (fun o d -> d.[0] = o.[0] && d.[d.Length - 1] = o.[o.Length - 1]))
    check "decimate caps total" ((dec |> Array.sumBy Array.length) <= 130)
    check "decimate no-ops under the cap" (MeshAnalysisCore.decimate 1000 long = long)

    // (d) dip fit: z = 2x + y → dip ∝ (2,1) (canonical +X); flat / sparse → None.
    let slanted = [ for _ in 0 .. 40 -> let v = randV3 4.0 in V3d(v.X, v.Y, 2.0 * v.X + v.Y) ]
    match MeshAnalysisCore.dipOfPoints slanted with
    | Some u -> checkLe "dip of z=2x+y ∝ (2,1)" (Vec.distance u (V3d(2.0, 1.0, 0.0).Normalized)) 1e-9
    | None -> check "dip of z=2x+y solvable" false
    check "flat patch → no dip"
        ((MeshAnalysisCore.dipOfPoints [ for _ in 0 .. 40 -> let v = randV3 4.0 in V3d(v.X, v.Y, 0.7) ]).IsNone)
    check "sparse patch → no dip"
        ((MeshAnalysisCore.dipOfPoints [ for i in 0 .. 5 -> V3d(float i, 0.0, float i) ]).IsNone)

[<EntryPoint>]
let main _ =
    umeyamaTests ()
    conditioningTests ()
    readinessTests ()
    sliceCoreTests ()
    printfn ""
    printfn "%d/%d passed%s" (total - failures) total (if failures = 0 then "" else sprintf " — %d FAILED" failures)
    failures
