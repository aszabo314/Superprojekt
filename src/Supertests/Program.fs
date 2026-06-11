module Supertests.Program

// Unit tests for the pure registration pieces (spec §10.1/§10.2):
//   • RegMath.solveRigid — weighted Umeyama (recovery, reflection handling,
//     weight semantics, collinearity flag, <3-pairs rejection)
//   • RegLog — commit / rollback state machine + effective-pose composition
//   • RegJson — workspace round-trip of correspondence + registration log,
//     including missing-field defaults for old workspaces
// Plain console runner (exit code = failure count) so no new packages enter
// the paket lock.

open System
open System.Text.Json
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

let transPart (m : M44d) = V3d(m.M03, m.M13, m.M23)

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

// ───────────────────────── RegLog: state machine ──────────────────────────

let trafoOfTranslation (v : V3d) = Trafo3d.Translation v

let regLogTests () =
    // Effective composition is the load-bearing convention: committed first,
    // then the delta (postfix Trafo3d composition).
    let c = Trafo3d.Translation(V3d(1.0, 0.0, 0.0)) * Trafo3d.RotationZ 0.5
    let d = Trafo3d.Translation(V3d(0.0, 2.0, 0.0))
    let p = V3d(3.0, 4.0, 5.0)
    let viaEffective = (RegLog.effective c d).Forward.TransformPos p
    let viaSequence = d.Forward.TransformPos (c.Forward.TransformPos p)
    checkLe "effective = delta ∘ committed" (viaEffective - viaSequence).Length 1e-12

    let pin1 = ScanPinId.create ()
    let pending = {
        Stage    = StageCoarse
        Mode     = "landmarks"
        Inputs   = CoarseInputs [| pin1, 1.0, Map.ofList [ "ds/B", AnchorAuto ] |]
        Results  =
            Map.ofList [
                "ds/B", { Delta = trafoOfTranslation (V3d(0.5, 0.0, 0.0)); RmsBefore = 2.0; RmsAfter = 0.5; Convergence = [||]; Collinear = false; PairResiduals = [| pin1, 0.5 |] }
                "ds/C", { Delta = trafoOfTranslation (V3d(0.0, -1.0, 0.0)); RmsBefore = 3.0; RmsAfter = 1.0; Convergence = [||]; Collinear = true; PairResiduals = [||] }
            ]
        Unsolved = []
        Expected = 0
    }
    let st0 = {
        Transforms    = Map.ofList [ "ds/B", trafoOfTranslation (V3d(10.0, 0.0, 0.0)) ]
        AlgoResiduals = Map.ofList [ "ds/B", 0.7 ]
        Log           = []
    }
    let stamp = DateTime(2026, 6, 11, 12, 0, 0, DateTimeKind.Utc)
    let step = RegLog.buildStep stamp "ds/A" pending st0
    check "step number starts at 1" (step.Step = 1)
    check "step keeps reference" (step.ReferenceMesh = "ds/A")
    let outB = step.Outputs.["ds/B"]
    checkLe "before = prior committed"
        (maxAbsDiff outB.TransformBefore.Forward (trafoOfTranslation (V3d(10.0, 0.0, 0.0))).Forward) 1e-12
    checkLe "after = committed * delta"
        (maxAbsDiff outB.TransformAfter.Forward (trafoOfTranslation (V3d(10.5, 0.0, 0.0))).Forward) 1e-12
    check "algo-before recorded" (outB.AlgoResidBefore = 0.7)

    let st1 = RegLog.commit step st0
    check "commit appends log" (st1.Log.Length = 1)
    checkLe "commit applies transform"
        (maxAbsDiff st1.Transforms.["ds/B"].Forward (trafoOfTranslation (V3d(10.5, 0.0, 0.0))).Forward) 1e-12
    check "commit swaps algo residual" (st1.AlgoResiduals.["ds/B"] = 0.5 && st1.AlgoResiduals.["ds/C"] = 1.0)

    // Second step on top, then roll back both.
    let pending2 = { pending with Stage = StageFine; Mode = "traditional-icp"; Inputs = FineInputs("traditional-icp", [||]) }
    let step2 = RegLog.buildStep stamp "ds/A" pending2 st1
    check "step numbers increment" (step2.Step = 2)
    let st2 = RegLog.commit step2 st1
    match RegLog.rollback st2 with
    | None -> check "rollback newest" false
    | Some (st1', popped) ->
        check "rollback pops newest" (popped.Step = 2 && st1'.Log.Length = 1)
        checkLe "rollback restores transforms"
            (maxAbsDiff st1'.Transforms.["ds/B"].Forward st1.Transforms.["ds/B"].Forward) 1e-12
        match RegLog.rollback st1' with
        | None -> check "rollback to empty" false
        | Some (st0', _) ->
            check "log empty after full rollback" (st0'.Log.IsEmpty)
            checkLe "transforms restored to initial"
                (maxAbsDiff st0'.Transforms.["ds/B"].Forward st0.Transforms.["ds/B"].Forward) 1e-12
            check "algo residuals restored" (st0'.AlgoResiduals.["ds/B"] = 0.7)
    check "rollback of empty log is None" ((RegLog.rollback { Transforms = Map.empty; AlgoResiduals = Map.empty; Log = [] }).IsNone)

// ───────────────────────── RegJson: round-trips ───────────────────────────

let parseRoot (json : string) =
    (JsonDocument.Parse json).RootElement

let regJsonTests () =
    let corr = {
        Enabled     = true
        RefAnchor   = Some (V3d(1.25, -3.5, 0.001))
        RefDistance = 0.125
        Anchors     =
            Map.ofList [
                "ds/B", { Point = V3d(10.0, 20.0, 30.0); Source = AnchorPatch2D; Accepted = true }
                "ds/C", { Point = V3d(-1.0, 2.5, 3.75); Source = AnchorAuto; Accepted = false }
            ]
        Residuals   = Map.ofList [ "ds/B", 0.042 ]
    }
    let corr' = RegJson.readCorrespondence (parseRoot (RegJson.correspondenceJ corr))
    check "correspondence round-trip" (corr' = corr)

    let defaults = RegJson.readCorrespondence (parseRoot "{}")
    check "correspondence missing fields → defaults"
        (defaults.Enabled && defaults.RefAnchor.IsNone && defaults.Anchors.IsEmpty && defaults.Residuals.IsEmpty)

    let pinA = ScanPinId.create ()
    let pinB = ScanPinId.create ()
    let mkOut (b : V3d) (a : V3d) = {
        TransformBefore = Trafo3d.Translation b
        TransformAfter  = Trafo3d.Translation a * Trafo3d.RotationZ 0.2
        RmsBefore       = 1.5
        RmsAfter        = 0.25
        AlgoResidBefore = 0.1
    }
    let log = [
        {
            Step = 2; Stage = StageFine; Mode = "region-icp"
            Timestamp = DateTime(2026, 6, 11, 13, 30, 12, DateTimeKind.Utc)
            ReferenceMesh = "ds/A"
            Inputs = FineInputs("region-icp", [| pinA; pinB |])
            Outputs = Map.ofList [ "ds/B", mkOut (V3d(1.0, 0.0, 0.0)) (V3d(1.5, 0.0, 0.0)) ]
        }
        {
            Step = 1; Stage = StageCoarse; Mode = "landmarks"
            Timestamp = DateTime(2026, 6, 11, 12, 0, 0, DateTimeKind.Utc)
            ReferenceMesh = "ds/A"
            Inputs = CoarseInputs [| pinA, 0.8, Map.ofList [ "ds/B", AnchorViolinAxial; "ds/C", AnchorPick3D ] |]
            Outputs =
                Map.ofList [
                    "ds/B", mkOut V3d.Zero (V3d(0.5, 1.0, 0.0))
                    "ds/C", mkOut V3d.Zero (V3d(-2.0, 0.0, 0.25))
                ]
        }
    ]
    // Trafo3d.Backward is reconstructed as Forward.Inverse on read, which can
    // differ from a composition-built Backward in the last ulp — compare via
    // a second serialization pass instead of structural equality.
    let log' = RegJson.readRegLog (parseRoot (RegJson.regLogJ log))
    check "registration log round-trip" (RegJson.regLogJ log' = RegJson.regLogJ log)
    check "registration log shape survives"
        (log'.Length = 2 && log'.Head.Stage = StageFine
         && (match log'.[1].Inputs with
             | CoarseInputs pins -> pins.Length = 1 && (let (_, rel, srcs) = pins.[0] in rel = 0.8 && srcs.["ds/B"] = AnchorViolinAxial)
             | _ -> false))
    check "empty log round-trip" (RegJson.readRegLog (parseRoot (RegJson.regLogJ [])) = [])

// ───────────────────────── RegConditioning sanity ─────────────────────────

let conditioningTests () =
    let line = Array.init 6 (fun i -> V3d(float i, 0.0, 0.0), 1.0)
    check "collinear spread flagged" (RegConditioning.isCollinear (RegConditioning.spreadEigenvalues line))
    let spread = Array.init 24 (fun _ -> randV3 10.0, 1.0)
    check "spread set not flagged" (not (RegConditioning.isCollinear (RegConditioning.spreadEigenvalues spread)))

[<EntryPoint>]
let main _ =
    umeyamaTests ()
    regLogTests ()
    regJsonTests ()
    conditioningTests ()
    printfn ""
    printfn "%d/%d passed%s" (total - failures) total (if failures = 0 then "" else sprintf " — %d FAILED" failures)
    failures
