module Supertests.Program

// Unit tests for the pure registration pieces:
//   • RegMath.solveRigid — weighted Umeyama (recovery, reflection handling,
//     weight semantics, collinearity flag, <3-pairs rejection)
//   • RegConditioning — spread eigenvalues / collinearity / dominant axis
//   • Readiness.compute — the correspondence readiness engine
//   • FlyToMath — camera fly-to framing
// Plain console runner (exit code = failure count) so no new packages enter
// the paket lock.

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

// ───────────────────────── RegConditioning sanity ─────────────────────────

let conditioningTests () =
    let line = Array.init 6 (fun i -> V3d(float i, 0.0, 0.0), 1.0)
    check "collinear spread flagged" (RegConditioning.isCollinear (RegConditioning.spreadEigenvalues line))
    let spread = Array.init 24 (fun _ -> randV3 10.0, 1.0)
    check "spread set not flagged" (not (RegConditioning.isCollinear (RegConditioning.spreadEigenvalues spread)))
    // dominantAxis (anchor cutaway): the PCA axis aligns with the line of
    // maximum spread regardless of sign.
    let axisDir = V3d(1.0, 2.0, -0.5) |> Vec.normalize
    let alongLine = Array.init 8 (fun i -> V3d.Zero + axisDir * (float i - 3.5) + randV3 0.01)
    let recovered = RegConditioning.dominantAxis alongLine
    checkLe "dominantAxis recovers spread line" (1.0 - abs (Vec.dot recovered axisDir)) 1e-3
    check "dominantAxis degenerate → up" (RegConditioning.dominantAxis [| V3d.Zero |] = V3d.OOI)
    // observabilityDeficiency: near-planar neighbourhood → high (weakly
    // conditioned), isotropic → ~0. Genuine ~0 distinct from absent (1.0).
    check "planar neighbourhood weakly conditioned" (RegMath.observabilityDeficiency [| 1.0; 1.0; 1e-5 |] > 0.99)
    check "isotropic neighbourhood well conditioned" (RegMath.observabilityDeficiency [| 1.0; 1.0; 1.0 |] < 1e-6)
    check "empty eigenvalues default degenerate" (RegMath.observabilityDeficiency [||] = 1.0)

// ─────────────────── Workflow panel: readiness engine ─────────────────────

let private mkRPin label refAnchor (accepted : string list) (total : int) : ReadinessPin =
    { Id = ScanPinId.create ()
      Label = label
      RefAnchor = refAnchor
      Accepted = Set.ofList accepted
      Unresolved = total - List.length accepted }

let readinessTests () =
    let baseInput = {
        ReferenceMesh       = Some "ref"
        VisibleMovingMeshes = [ "A"; "B" ]
        EnabledPins         = []
    }
    let ready (d : Diagnostic list) = d |> List.filter (fun x -> x.Severity = Severity.Ready)
    // non-collinear spread (parabola) with anchors accepted on both meshes
    let pinsN n =
        List.init n (fun i ->
            mkRPin (sprintf "p%d" i)
                (Some (V3d(float i * 3.0, float (i * i), 0.5), 1.0))
                [ "A"; "B" ] 2)

    let d = Readiness.compute { baseInput with ReferenceMesh = None }
    check "no-ref blocker"
        (d |> List.exists (fun x -> x.Severity = Blocker && x.Text.Contains "reference"))
    check "no-ref highlight action"
        (d |> List.exists (fun x -> x.Action = Some HighlightReferenceColumn))
    check "no-ref never ready" (ready d |> List.isEmpty)

    let d = Readiness.compute baseInput
    check "zero pins blocker" (d |> List.exists (fun x -> x.Severity = Blocker && x.Text.Contains "≥3 pins"))

    let d = Readiness.compute { baseInput with EnabledPins = pinsN 2 }
    check "pair deficit blocker per mesh"
        (d |> List.filter (fun x -> x.Severity = Blocker && x.Text.Contains "more correspondence marker") |> List.length = 2)
    check "deficit counts the gap" (d |> List.exists (fun x -> x.Text.Contains "needs 1 more"))
    check "deficit action reseeds the filtered mesh"
        (d |> List.exists (fun x -> x.Action = Some (ReseedCorrespondence (Some "A"))))
    check "2 pins not ready" (ready d |> List.isEmpty)

    let d = Readiness.compute { baseInput with EnabledPins = pinsN 3 }
    check "3 pins → exactly one Ready" (ready d |> List.length = 1)
    check "Ready action" ((ready d |> List.head).Action = Some RunCoarse)
    check "no blockers when clear" (d |> List.forall (fun x -> x.Severity <> Blocker))

    let pinU = mkRPin "pu" (Some (V3d(9.0, 1.0, 2.0), 1.0)) [ "A" ] 2
    let d = Readiness.compute { baseInput with EnabledPins = pinU :: pinsN 3 }
    check "missing-marker mesh → warning"
        (d |> List.exists (fun x -> x.Severity = Warning && x.Text.Contains "without a marker"))
    check "unresolved action opens the pin card"
        (d |> List.exists (fun x -> x.Action = Some (SelectPinOpenCard pinU.Id)))

    let colinear =
        List.init 4 (fun i -> mkRPin (sprintf "c%d" i) (Some (V3d(float i, 0.0, 0.0), 1.0)) [ "A"; "B" ] 2)
    let d = Readiness.compute { baseInput with EnabledPins = colinear }
    check "collinear anchors → warning" (d |> List.exists (fun x -> x.Text.Contains "near-collinear"))
    let d = Readiness.compute { baseInput with EnabledPins = pinsN 4 }
    check "spread anchors → no collinear warning"
        (not (d |> List.exists (fun x -> x.Text.Contains "near-collinear")))

// ────────────────────── Workflow panel: fly-to math ────────────────────────

let flyToTests () =
    let aspect = 16.0 / 9.0
    let fovY = FlyToMath.fovY 90.0 aspect
    checkLe "fovY closed form" (abs (fovY - 2.0 * atan (tan (Math.PI / 4.0) / aspect))) 1e-12
    let d = FlyToMath.distance fovY 5.0
    checkLe "fly-to distance closed form" (abs (d - 5.0 / tan (fovY * 0.125))) 1e-12
    check "distance grows with radius" (FlyToMath.distance fovY 10.0 > d)
    let c, r = FlyToMath.boundingSphere (FlyToBounds (Box3d(V3d(-1.0, -2.0, -3.0), V3d(1.0, 2.0, 3.0))))
    checkLe "bounds → sphere centre" (c - V3d.Zero).Length 1e-12
    checkLe "bounds → sphere radius" (abs (r - V3d(2.0, 4.0, 6.0).Length * 0.5)) 1e-12
    let c2, r2 = FlyToMath.boundingSphere (FlyToSphere(V3d(1.0, 2.0, 3.0), 4.0))
    check "sphere passthrough" (c2 = V3d(1.0, 2.0, 3.0) && r2 = 4.0)
    check "degenerate radius clamped" (snd (FlyToMath.boundingSphere (FlyToSphere(V3d.Zero, 0.0))) > 0.0)

[<EntryPoint>]
let main _ =
    umeyamaTests ()
    conditioningTests ()
    readinessTests ()
    flyToTests ()
    printfn ""
    printfn "%d/%d passed%s" (total - failures) total (if failures = 0 then "" else sprintf " — %d FAILED" failures)
    failures
