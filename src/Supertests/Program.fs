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

// ───────────────────────── Study: predicate engine ────────────────────────

let predicateTests () =
    let none (_ : string) = false
    let c (xs : (string * int) list) = Map.ofList xs
    check "event below threshold" (not (Predicate.satisfied (c []) none (PEvent("orbit", 1)) Map.empty))
    check "event at threshold" (Predicate.satisfied (c ["orbit", 1]) none (PEvent("orbit", 1)) Map.empty)
    let andP = PAnd [ PEvent("a", 1); PEvent("b", 1) ]
    check "and needs both" (not (Predicate.satisfied (c ["a", 1]) none andP Map.empty))
    check "and satisfied" (Predicate.satisfied (c ["a", 1; "b", 1]) none andP Map.empty)
    check "or either side" (Predicate.satisfied (c ["b", 1]) none (POr [ PEvent("a", 1); PEvent("b", 1) ]) Map.empty)
    check "answer predicate" (Predicate.satisfied Map.empty ((=) "Q1") (PAnswerSubmitted "Q1") Map.empty)
    check "always" (Predicate.satisfied Map.empty none PAlways Map.empty)

    // Seq: a later stage's events never complete it before earlier stages.
    let seqP = PSeq [ PEvent("a", 1); PEvent("b", 2) ]
    let prog1 = Predicate.advance (c ["b", 5]) none seqP Map.empty
    check "seq gate holds out of order" (not (Predicate.satisfied (c ["b", 5]) none seqP prog1))
    let counts2 = c ["b", 5; "a", 1]
    let prog2 = Predicate.advance counts2 none seqP prog1
    check "seq completes once ordered" (Predicate.satisfied counts2 none seqP prog2)
    // monotone progress: counts reset (step re-entry) never un-completes
    let prog3 = Predicate.advance Map.empty none seqP prog2
    check "seq progress survives count reset" (Predicate.satisfied Map.empty none seqP prog3)
    // multi-stage advance in one feed
    let seq3 = PSeq [ PEvent("x", 1); PEvent("y", 1); PEvent("z", 1) ]
    let all = c ["x", 1; "y", 1; "z", 1]
    check "seq advances through all ready stages" (Predicate.satisfied all none seq3 (Predicate.advance all none seq3 Map.empty))

    let parsed =
        Predicate.parse (parseRoot """{"seq":[{"event":"pinCommitted","min":3},{"and":[{"event":"coarseSolved"},{"answer":"T1"}]}]}""")
    check "predicate json parse"
        (parsed = PSeq [ PEvent("pinCommitted", 3); PAnd [ PEvent("coarseSolved", 1); PAnswerSubmitted "T1" ] ])
    let evs, ans = Predicate.references parsed
    check "predicate references" (evs = [ "pinCommitted"; "coarseSolved" ] && ans = [ "T1" ])

// ───────────────────────── Study: reducer gating (§4) ─────────────────────

let private mkStep id kind completion question = {
    Id = id; Kind = kind; Body = ""; Anchor = None
    Completion = completion; Question = question; Optional = false; RetryStepId = None
}

let private mkQuestion id kind confidence gold : StudyQuestion =
    { Id = id; Kind = kind; Confidence = confidence; Gold = gold; FlagPoint = None }

let private mkCfg steps : StudyConfigPublic = {
    StudyId = "t"; Title = "t"; DatasetTutorial = "tut"; DatasetMain = "main"
    DisabledFeatures = Map.ofList [ "NUM", [ "violinChart" ] ]
    Phases = [ { Id = "p"; Title = ""; GoalLine = ""; Dataset = Some "tutorial"; AllowedFeatures = [ "navigation" ]; Steps = steps } ]
    Questionnaires = Map.ofList [ "sus", [| "i1"; "i2" |] ]
    MovingPolygon = [||]
}

let gatingTests () =
    let rtAt cfg rt = Study.reevaluate cfg rt true
    // instruction: satisfied on render
    let cfg = mkCfg [ mkStep "s" KInstruction PAlways None ]
    check "instruction satisfied on render" (rtAt cfg StudyRuntime.initial).StepSatisfied
    // guidedAction: predicate-gated
    let cfg = mkCfg [ mkStep "s" KGuidedAction (PEvent("orbit", 1)) None ]
    check "guidedAction blocked before event" (not (rtAt cfg StudyRuntime.initial).StepSatisfied)
    let fed = Study.feedEvents [ "orbit" ] StudyRuntime.initial
    check "guidedAction satisfied after event" (rtAt cfg fed).StepSatisfied
    // question: answer + confidence required
    let q = mkQuestion "Q1" (SingleChoice [| "a"; "b" |]) true false
    let cfg = mkCfg [ mkStep "s" KQuestion (PAnswerSubmitted "Q1") (Some q) ]
    let withDraft d = { StudyRuntime.initial with AnswersDraft = Map.ofList [ "Q1", d ] }
    check "question blocked without answer" (not (rtAt cfg StudyRuntime.initial).StepSatisfied)
    check "question blocked without confidence"
        (not (rtAt cfg (withDraft { Value = Some (AChoice 0); Confidence = None })).StepSatisfied)
    check "question satisfied with confidence"
        (rtAt cfg (withDraft { Value = Some (AChoice 0); Confidence = Some 5 })).StepSatisfied
    // tutorial gold gating: server-confirmed correctness required
    let qg = mkQuestion "G1" (SingleChoice [| "a"; "b" |]) false true
    let cfg = mkCfg [ mkStep "s" KQuestion (PAnswerSubmitted "G1") (Some qg) ]
    let answered = { StudyRuntime.initial with AnswersDraft = Map.ofList [ "G1", { Value = Some (AChoice 0); Confidence = None } ] }
    check "tutorial gold blocked until confirmed" (not (rtAt cfg answered).StepSatisfied)
    check "tutorial gold passes when confirmed"
        (rtAt cfg { answered with GoldStatus = Map.ofList [ "G1", true ] }).StepSatisfied
    check "non-tutorial gold not gated" (Study.reevaluate cfg answered false).StepSatisfied
    // questionnaire: every grid item answered
    let cfg = mkCfg [ mkStep "s" (KQuestionnaire "sus") PAlways None ]
    let grid items = { StudyRuntime.initial with AnswersDraft = Map.ofList [ "sus", { Value = Some (AGrid (Map.ofList items)); Confidence = None } ] }
    check "questionnaire blocked when partial" (not (rtAt cfg (grid [ 0, 3.0 ])).StepSatisfied)
    check "questionnaire satisfied when complete" (rtAt cfg (grid [ 0, 3.0; 1, 4.0 ])).StepSatisfied
    // freeText min length
    let qt = mkQuestion "F" (FreeTextQ 10) false false
    let cfg = mkCfg [ mkStep "s" KQuestion (PAnswerSubmitted "F") (Some qt) ]
    let withText t = { StudyRuntime.initial with AnswersDraft = Map.ofList [ "F", { Value = Some (AText t); Confidence = None } ] }
    check "freeText below min length" (not (rtAt cfg (withText "short")).StepSatisfied)
    check "freeText at min length" (rtAt cfg (withText "long enough text")).StepSatisfied
    // retry step resolution
    let steps = [
        mkStep "a" KInstruction PAlways None
        mkStep "g" KGuidedAction (PEvent("orbit", 1)) None
        mkStep "q" KQuestion (PAnswerSubmitted "G1") (Some qg)
        { mkStep "q2" KQuestion (PAnswerSubmitted "G1") (Some qg) with RetryStepId = Some "a" }
    ]
    let cfg = mkCfg steps
    check "retry defaults to preceding guidedAction" (Study.retryStepIx cfg 0 2 = 1)
    check "explicit retry step wins" (Study.retryStepIx cfg 0 3 = 0)
    // feature gating
    let session demo cond = {
        SessionId = "x"; Condition = cond; Demo = demo; Config = cfg
        Runtime = StudyRuntime.initial
    }
    check "feature visible in FULL when allowed" (Study.featureVisibleIn (session false CondFull) "navigation")
    check "feature hidden when not allowed" (not (Study.featureVisibleIn (session false CondFull) "lasso-ish-unknown"))
    check "full mode sees everything" (Study.featureVisible None "violinChart")
    // condition filter: a phase allowing violinChart still hides it in NUM
    let cfgV = { cfg with Phases = cfg.Phases |> List.map (fun p -> { p with AllowedFeatures = [ "violinChart" ] }) }
    check "NUM filter removes starred feature"
        (not (Study.featureVisibleIn { session false CondNum with Config = cfgV } "violinChart"))
    check "FULL keeps starred feature"
        (Study.featureVisibleIn { session false CondFull with Config = cfgV } "violinChart")
    // point-in-polygon
    let poly = [| V2d(0.0, 0.0); V2d(10.0, 0.0); V2d(10.0, 10.0); V2d(0.0, 10.0) |]
    check "inside polygon" (StudyConfig.insidePolygon poly (V2d(5.0, 5.0)))
    check "outside polygon" (not (StudyConfig.insidePolygon poly (V2d(15.0, 5.0))))

// ───────────────────── Study: server config validation ────────────────────

let private dsExists (d : string) = d = "tut" || d = "main"

let validationTests () =
    let secretEmpty : StudyConfig.StudySecret =
        { Answers = Map.empty; CheckPoints = Map.empty; GoldFailThreshold = 3 }
    let baseJson = """{"studyId":"t"}"""
    let valid =
        mkCfg [ mkStep "s" KInstruction PAlways None ]
    check "valid config accepted" (StudyConfig.validate dsExists baseJson valid secretEmpty |> List.isEmpty)
    // dangling question ref in a predicate
    let dangling = mkCfg [ mkStep "s" KGuidedAction (PAnswerSubmitted "NOPE") None ]
    check "dangling question ref rejected"
        (StudyConfig.validate dsExists baseJson dangling secretEmpty
         |> List.exists (fun e -> e.Contains "NOPE"))
    // gold question without secret answer
    let goldQ = mkQuestion "G9" (SingleChoice [| "a" |]) false true
    let goldCfg = mkCfg [ mkStep "s" KQuestion (PAnswerSubmitted "G9") (Some goldQ) ]
    check "gold without secret rejected"
        (StudyConfig.validate dsExists baseJson goldCfg secretEmpty
         |> List.exists (fun e -> e.Contains "G9"))
    // unknown feature id
    let badFeat =
        { valid with Phases = valid.Phases |> List.map (fun p -> { p with AllowedFeatures = [ "warpDrive" ] }) }
    check "unknown feature rejected"
        (StudyConfig.validate dsExists baseJson badFeat secretEmpty
         |> List.exists (fun e -> e.Contains "warpDrive"))
    // unknown event in predicate
    let badEvent = mkCfg [ mkStep "s" KGuidedAction (PEvent("teleported", 1)) None ]
    check "unknown event rejected"
        (StudyConfig.validate dsExists baseJson badEvent secretEmpty
         |> List.exists (fun e -> e.Contains "teleported"))
    // missing dataset
    check "missing dataset rejected"
        (StudyConfig.validate (fun _ -> false) baseJson valid secretEmpty
         |> List.exists (fun e -> e.Contains "datasetTutorial"))
    // forbidden key scan in the public file
    check "secret key in public json rejected"
        (StudyConfig.validate dsExists """{"studyId":"t","answers":{"T1":0}}""" valid secretEmpty
         |> List.exists (fun e -> e.Contains "answers"))

// ───────────── Study: store (balance, HMAC, advance, TRE, gold) ───────────

let storeTests () =
    let tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "supertests-" + System.Guid.NewGuid().ToString "N")
    System.IO.Directory.CreateDirectory tmp |> ignore
    let dataDir = System.IO.Path.Combine(tmp, "data")
    let tokensFile = System.IO.Path.Combine(tmp, "tokens.jsonl")
    let now = DateTime.UtcNow

    // balanced assignment under 100 concurrent creations (§12.1)
    let tokens = StudyStore.generateTokens tokensFile now 100
    check "tokens generated" (tokens.Length = 100 && (tokens |> Array.distinct |> Array.length) = 100)
    let starts =
        tokens
        |> Array.map (fun tok -> async {
            return StudyStore.createSession dataDir tokensFile now (Random.Shared) (Some tok) false None })
        |> Async.Parallel
        |> Async.RunSynchronously
    let fresh =
        starts |> Array.choose (function StudyStore.Fresh s -> Some s | _ -> None)
    check "all 100 sessions created" (fresh.Length = 100)
    let nFull = fresh |> Array.filter (fun s -> s.Condition = CondFull) |> Array.length
    let nNum = fresh.Length - nFull
    check (sprintf "balanced assignment (%d/%d)" nFull nNum) (abs (nFull - nNum) <= 1)
    // resume: same token returns the same session
    let tok0 = tokens.[0]
    let sid0 = (fresh |> Array.find (fun s -> s.Token = Some tok0)).Sid
    match StudyStore.createSession dataDir tokensFile now Random.Shared (Some tok0) false None with
    | StudyStore.Resumed s -> check "same token resumes same sid" (s.Sid = sid0)
    | _ -> check "same token resumes same sid" false
    match StudyStore.createSession dataDir tokensFile now Random.Shared (Some "bogus") false None with
    | StudyStore.Refused (403, _) -> check "unknown token refused 403" true
    | _ -> check "unknown token refused 403" false

    // HMAC completion code
    let secret = StudyStore.serverSecret tmp
    let code = StudyStore.completionCode secret "abc"
    check "completion code 8 hex chars"
        (code.Length = 8 && code |> Seq.forall (fun ch -> System.Uri.IsHexDigit ch))
    check "completion code deterministic" (StudyStore.completionCode secret "abc" = code)
    check "completion code sid-dependent" (StudyStore.completionCode secret "abd" <> code)

    // study fixture: tutorial gold G1 (choice 0), two steps, TRE check points
    let refPts = [| V3d(0.0, 0.0, 0.0); V3d(10.0, 0.0, 0.0); V3d(0.0, 10.0, 0.0); V3d(0.0, 0.0, 10.0) |]
    let rot = rotation (V3d(0.3, 0.7, 0.2)) 0.4
    let trans = V3d(5.0, -3.0, 2.0)
    let t44 =
        M44d(rot.M00, rot.M01, rot.M02, trans.X,
             rot.M10, rot.M11, rot.M12, trans.Y,
             rot.M20, rot.M21, rot.M22, trans.Z,
             0.0, 0.0, 0.0, 1.0)
    let movPts = refPts |> Array.map (fun p -> t44.Inverse.TransformPos p)
    let pairs = Array.map2 (fun r m -> { StudyConfig.Ref = r; StudyConfig.Mov = m }) refPts movPts
    checkLe "TRE zero at known transform" (StudyStore.treFor pairs t44) 1e-9
    let treId = StudyStore.treFor pairs M44d.Identity
    check "TRE positive at identity" (treId > 1.0)

    let goldQ = mkQuestion "G1" (SingleChoice [| "right"; "wrong" |]) false true
    let study : StudyConfig.LoadedStudy = {
        Id = "t"; PublicJson = "{}"
        Public =
            mkCfg [
                mkStep "s1" KQuestion (PAnswerSubmitted "G1") (Some goldQ)
                mkStep "s2" KInstruction PAlways None
            ]
        Secret =
            { Answers = Map.ofList [ "G1", StudyConfig.SecretChoice 0 ]
              CheckPoints = Map.ofList [ "m1", (pairs, [||]) ]
              GoldFailThreshold = 3 }
    }
    let sid = sid0

    // gold flow: wrong ×3 → screened
    let a1 = StudyStore.appendAnswer study dataDir sid now "G1" (parseRoot "1") None
    check "gold wrong echoes false" (a1.Correct = Some false && not a1.Screened)
    let a2 = StudyStore.appendAnswer study dataDir sid now "G1" (parseRoot "1") None
    check "second fail not screened" (not a2.Screened)
    let a3 = StudyStore.appendAnswer study dataDir sid now "G1" (parseRoot "1") None
    check "third fail screens out" (a3.Screened)
    check "session status screened" ((StudyStore.findSession dataDir sid).Value.Status = "screened")
    let sid2 = (fresh |> Array.find (fun s -> s.Token = Some tokens.[1])).Sid
    let aOk = StudyStore.appendAnswer study dataDir sid2 now "G1" (parseRoot "0") None
    check "gold correct echoes true" (aOk.Correct = Some true && not aOk.Screened)

    // advance ordering: out-of-order rejected, idempotent repeat accepted
    check "out-of-order advance rejected"
        (match StudyStore.recordAdvance study dataDir sid2 now "p" "s2" with Result.Error _ -> true | _ -> false)
    check "in-order advance accepted"
        (match StudyStore.recordAdvance study dataDir sid2 now "p" "s1" with Result.Ok () -> true | _ -> false)
    check "idempotent repeat accepted"
        (match StudyStore.recordAdvance study dataDir sid2 now "p" "s1" with Result.Ok () -> true | _ -> false)

    // completion gating: refuse before final transforms + all advances
    check "complete refused before requirements"
        (match StudyStore.complete study dataDir secret sid2 with Result.Error _ -> true | _ -> false)
    StudyStore.recordAdvance study dataDir sid2 now "p" "s2" |> ignore
    check "complete still refused without final transforms"
        (match StudyStore.complete study dataDir secret sid2 with Result.Error _ -> true | _ -> false)
    StudyStore.postTransforms study dataDir sid2 now "final" (Map.ofList [ "m1", t44 ])
    match StudyStore.complete study dataDir secret sid2 with
    | Result.Ok c ->
        check "complete issues the HMAC code" (c = StudyStore.completionCode secret sid2)
        check "completion marks session completed" ((StudyStore.findSession dataDir sid2).Value.Status = "completed")
    | Result.Error e -> check (sprintf "complete issues the HMAC code (%s)" e) false
    // scores recorded with the label, never empty
    let scores = System.IO.File.ReadAllText (StudyStore.scoresPath dataDir sid2)
    check "scores recorded for final" (scores.Contains "\"final\"" && scores.Contains "m1")

    try System.IO.Directory.Delete(tmp, true) with _ -> ()

// ─────────────────── Workflow panel: readiness engine ─────────────────────

let private mkRPin label refAnchor (accepted : string list) (total : int) : ReadinessPin =
    { Id = ScanPinId.create ()
      Label = label
      RefAnchor = refAnchor
      Accepted = Set.ofList accepted
      AcceptedTotal = List.length accepted
      Unresolved = total - List.length accepted }

let readinessTests () =
    let baseInput = {
        ReferenceMesh       = Some "ref"
        VisibleMovingMeshes = [ "A"; "B" ]
        EnabledPins         = []
        HasPending          = false
        HasCommittedStep    = false
        FineModeLabel       = "Traditional ICP"
    }
    let ready (d : Diagnostic list) = d |> List.filter (fun x -> x.Severity = Severity.Ready)
    // non-collinear spread (parabola) with anchors accepted on both meshes
    let pinsN n =
        List.init n (fun i ->
            mkRPin (sprintf "p%d" i)
                (Some (V3d(float i * 3.0, float (i * i), 0.5), 1.0))
                [ "A"; "B" ] 2)

    let d = Readiness.compute { baseInput with ReferenceMesh = None }
    check "no-ref blocker in both stages"
        ((d.Coarse |> List.exists (fun x -> x.Severity = Blocker && x.Text.Contains "reference"))
         && (d.Fine |> List.exists (fun x -> x.Severity = Blocker && x.Text.Contains "reference")))
    check "no-ref highlight action"
        (d.Coarse |> List.exists (fun x -> x.Action = Some HighlightReferenceColumn))
    check "no-ref never ready" (ready d.Coarse |> List.isEmpty && ready d.Fine |> List.isEmpty)

    let d = Readiness.compute baseInput
    check "zero pins blocker" (d.Coarse |> List.exists (fun x -> x.Severity = Blocker && x.Text.Contains "≥3 pins"))

    let d = Readiness.compute { baseInput with EnabledPins = pinsN 2 }
    check "pair deficit blocker per mesh"
        (d.Coarse |> List.filter (fun x -> x.Severity = Blocker && x.Text.Contains "more accepted") |> List.length = 2)
    check "deficit counts the gap" (d.Coarse |> List.exists (fun x -> x.Text.Contains "needs 1 more"))
    check "deficit action opens filtered review"
        (d.Coarse |> List.exists (fun x -> x.Action = Some (OpenAnchorReview (Some "A"))))
    check "2 pins not ready" (ready d.Coarse |> List.isEmpty)

    let d = Readiness.compute { baseInput with EnabledPins = pinsN 3 }
    check "3 pins → exactly one coarse Ready" (ready d.Coarse |> List.length = 1)
    check "coarse Ready action" ((ready d.Coarse |> List.head).Action = Some RunCoarse)
    check "no coarse blockers when clear" (d.Coarse |> List.forall (fun x -> x.Severity <> Blocker))
    check "fine info before any commit"
        (d.Fine |> List.exists (fun x -> x.Severity = Severity.Info && x.Text.Contains "coarse first"))
    check "fine not ready before commit" (ready d.Fine |> List.isEmpty)

    let d = Readiness.compute { baseInput with EnabledPins = pinsN 3; HasCommittedStep = true }
    check "fine → exactly one Ready after commit" (ready d.Fine |> List.length = 1)
    check "fine Ready names the mode" ((ready d.Fine |> List.head).Text.Contains "Traditional ICP")
    check "fine Ready action" ((ready d.Fine |> List.head).Action = Some RunFine)

    let pinU = mkRPin "pu" (Some (V3d(9.0, 1.0, 2.0), 1.0)) [ "A" ] 2
    let d = Readiness.compute { baseInput with EnabledPins = pinU :: pinsN 3 }
    check "unresolved anchors → warning"
        (d.Coarse |> List.exists (fun x -> x.Severity = Warning && x.Text.Contains "unresolved"))
    check "unresolved action opens the pin card"
        (d.Coarse |> List.exists (fun x -> x.Action = Some (SelectPinOpenCard pinU.Id)))

    let colinear =
        List.init 4 (fun i -> mkRPin (sprintf "c%d" i) (Some (V3d(float i, 0.0, 0.0), 1.0)) [ "A"; "B" ] 2)
    let d = Readiness.compute { baseInput with EnabledPins = colinear }
    check "collinear anchors → warning" (d.Coarse |> List.exists (fun x -> x.Text.Contains "near-collinear"))
    let d = Readiness.compute { baseInput with EnabledPins = pinsN 4 }
    check "spread anchors → no collinear warning"
        (not (d.Coarse |> List.exists (fun x -> x.Text.Contains "near-collinear")))

    let d = Readiness.compute { baseInput with EnabledPins = pinsN 3; HasPending = true; HasCommittedStep = true }
    check "pending blocks coarse"
        (d.Coarse |> List.exists (fun x -> x.Severity = Blocker && x.Text.Contains "pending"))
    check "pending blocks fine"
        (d.Fine |> List.exists (fun x -> x.Severity = Blocker && x.Text.Contains "pending"))
    check "pending action focuses the pending block"
        (d.Coarse |> List.exists (fun x -> x.Action = Some (FocusRegistrationCard SectionPending)))
    check "pending kills both Ready entries" (ready d.Coarse @ ready d.Fine |> List.isEmpty)

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

// ──────────────────── Workflow panel: lastSolve model ──────────────────────

let lastSolveTests () =
    let pid = ScanPinId.create ()
    let entryCoarse = {
        Stage           = StageCoarse
        RmsBefore       = 1.25
        RmsAfter        = 0.5
        Conditioning    = Some { Eigenvalues = [| 3.0; 2.0; 0.125 |]; CollinearityWarning = false }
        PerPinResiduals = Some (Map.ofList [ pid, 0.125 ])
        Timestamp       = DateTime(2026, 6, 12, 10, 0, 0, DateTimeKind.Utc)
    }
    let entryFine = {
        Stage           = StageFine
        RmsBefore       = 0.5
        RmsAfter        = 0.25
        Conditioning    = None
        PerPinResiduals = None
        Timestamp       = DateTime(2026, 6, 12, 11, 0, 0, DateTimeKind.Utc)
    }
    let m = Map.ofList [ "ds/A", entryCoarse; "ds/B", entryFine ]
    check "lastSolve round-trip" (RegJson.readLastSolve (parseRoot (RegJson.lastSolveJ m)) = m)
    check "lastSolve empty round-trip"
        (RegJson.readLastSolve (parseRoot (RegJson.lastSolveJ Map.empty)) = Map.empty)
    // workspace without the field (old files) defaults handled by the caller;
    // rollback clears exactly the producing step's meshes
    let out = {
        TransformBefore = Trafo3d.Identity
        TransformAfter  = Trafo3d.Translation (V3d(1.0, 0.0, 0.0))
        RmsBefore       = 1.0
        RmsAfter        = 0.5
        AlgoResidBefore = 0.0
    }
    let step = {
        Step = 1; Stage = StageCoarse; Mode = "landmarks"
        Timestamp = DateTime(2026, 6, 12, 12, 0, 0, DateTimeKind.Utc)
        ReferenceMesh = "ds/ref"
        Inputs = CoarseInputs [||]
        Outputs = Map.ofList [ "ds/A", out ]
    }
    let after = LastSolve.afterRollback step m
    check "rollback clears the producing mesh"
        (not (Map.containsKey "ds/A" after) && Map.containsKey "ds/B" after)
    check "rollback of unrelated step is a no-op"
        (LastSolve.afterRollback { step with Outputs = Map.ofList [ "ds/C", out ] } m = m)

[<EntryPoint>]
let main _ =
    umeyamaTests ()
    regLogTests ()
    regJsonTests ()
    conditioningTests ()
    predicateTests ()
    gatingTests ()
    validationTests ()
    storeTests ()
    readinessTests ()
    flyToTests ()
    lastSolveTests ()
    printfn ""
    printfn "%d/%d passed%s" (total - failures) total (if failures = 0 then "" else sprintf " — %d FAILED" failures)
    failures
