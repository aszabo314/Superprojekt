module Supertests.Program

// Unit tests for the pure registration + slice-geometry pieces (RegMath,
// RegGraph, MeshAnalysisCore). Plain console runner (exit code = failure
// count) so no new packages enter the paket lock.

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

// ─────────────── MeshAnalysisCore: level-set tracer · decimate ─────────────

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

// ───────────────── Registration graph: tree invariant ─────────────────────

let regGraphTests () =
    let tr (x : float) = Trafo3d.Translation(V3d(x, 0.0, 0.0))
    let expectOk name r =
        match r with
        | EdgeAdded g -> g
        | EdgeClosesLoop _ -> check (sprintf "%s (unexpected loop)" name) false; RegGraph.empty
        | EdgeRejected e -> check (sprintf "%s (%s)" name e) false; RegGraph.empty
    let expectRejected name r = check name (match r with EdgeRejected _ -> true | _ -> false)
    let expectLoop name r = check name (match r with EdgeClosesLoop _ -> true | _ -> false)

    expectRejected "no root ⇒ add rejected" (RegGraph.tryAddEdge "A" "R" (tr 1.0) 1.0 RegGraph.empty)

    let g0 = { Root = Some "R"; Edges = Map.empty }
    check "root is in the tree" (RegGraph.inTree g0 "R")
    check "others start outside" (not (RegGraph.inTree g0 "A"))
    check "no edges yet" (not (RegGraph.hasEdges g0))
    expectRejected "self edge rejected" (RegGraph.tryAddEdge "R" "R" (tr 1.0) 1.0 g0)

    let g1 = expectOk "first edge A→R" (RegGraph.tryAddEdge "A" "R" (tr 1.0) 1.0 g0)
    check "A joins the tree" (RegGraph.inTree g1 "A" && RegGraph.hasEdges g1)
    // both endpoints already connected ⇒ any further edge between them is a cycle
    // Both-connected adds are now TRANSIENT loops (P9), never silent commits.
    expectLoop "A→R again ⇒ closes a transient loop" (RegGraph.tryAddEdge "A" "R" (tr 2.0) 1.0 g1)
    expectLoop "R→A ⇒ closes a transient loop" (RegGraph.tryAddEdge "R" "A" (tr 2.0) 1.0 g1)
    // neither endpoint connected ⇒ isolated component rejected
    expectRejected "C→B (both outside) ⇒ isolated rejected" (RegGraph.tryAddEdge "C" "B" (tr 1.0) 1.0 g1)

    let g2 = expectOk "chain B→A" (RegGraph.tryAddEdge "B" "A" (tr 2.0) 1.0 g1)
    let g3 = expectOk "branch C→R" (RegGraph.tryAddEdge "C" "R" (tr 3.0) 1.0 g2)
    expectLoop "B→C ⇒ closes a transient loop (committed tree untouched)" (RegGraph.tryAddEdge "B" "C" (tr 9.0) 1.0 g3)
    check "children of R" (RegGraph.children g3 "R" |> List.sort = [ "A"; "C" ])
    check "children of A" (RegGraph.children g3 "A" = [ "B" ])

    let g4 = RegGraph.withEdgeTransform "B" (tr 7.0) g3
    check "edge transform replaced" (g4.Edges.["B"].Transform.Forward.M03 = 7.0)
    check "edge endpoints unchanged" (g4.Edges.["B"].Parent = "A" && g4.Edges.["B"].Child = "B")
    check "unrelated edges untouched" (obj.ReferenceEquals(g4.Edges.["C"], g3.Edges.["C"]))

// ───────────────── Registration graph: composed worldPose ─────────────────

let regComposeTests () =
    let tr (x : float) = Trafo3d.Translation(V3d(x, 0.0, 0.0))
    let add mov ref t g =
        match RegGraph.tryAddEdge mov ref t 1.0 g with
        | EdgeAdded g -> g
        | EdgeClosesLoop _ -> check (sprintf "add %s→%s (unexpected loop)" mov ref) false; g
        | EdgeRejected e -> check (sprintf "add %s→%s (%s)" mov ref e) false; g
    let root = { Root = Some "R"; Edges = Map.empty }

    check "empty graph composes to nothing" (Map.isEmpty (RegGraph.composeAll root))

    // Star graph (every edge to the root): worldPose = the old star pose,
    // exactly — the SAME Trafo3d instance the solve stored on the edge.
    let tA = tr 1.0
    let tB = Trafo3d.RotationZ (Math.PI / 2.0)
    let star = root |> add "A" "R" tA |> add "B" "R" tB
    let starP = RegGraph.composeAll star
    check "star: root has no pose entry" (not (Map.containsKey "R" starP))
    // Trafo3d is a struct — exact value equality is the strongest observable.
    check "star: A pose = its edge transform exactly" (starP.["A"] = tA)
    check "star: B pose = its edge transform exactly" (starP.["B"] = tB)

    // Chain R←A←B: worldPose(B) = tB then A's own pose (apply child first).
    // tACh = rot90 about Z, tBCh = +2X ⇒ origin ↦ (2,0,0) ↦ (0,2,0).
    let tACh = Trafo3d.RotationZ (Math.PI / 2.0)
    let tBCh = tr 2.0
    let chain = root |> add "A" "R" tACh |> add "B" "A" tBCh
    let chainP = RegGraph.composeAll chain
    checkLe "chain: B pose composes child-first"
        (Vec.distance (chainP.["B"].Forward.TransformPos V3d.Zero) (V3d(0.0, 2.0, 0.0))) 1e-12

    // Subtree memoization: R←A←B plus a branch R←D. Editing edge A recomposes
    // A and B ONLY — proven by poisoning D's entry in prev with a sentinel:
    // composeSubtree must carry it through untouched (it never visits D).
    let tD = tr 5.0
    let g0 = chain |> add "D" "R" tD
    let p0 = RegGraph.composeAll g0
    let sentinel = tr 99.0
    let g1 = RegGraph.withEdgeTransform "A" (tr 10.0) g0
    let p1 = RegGraph.composeSubtree "A" (Map.add "D" sentinel p0) g1
    check "subtree: D never recomputed (sentinel preserved)" (p1.["D"] = sentinel)
    let full1 = RegGraph.composeAll g1
    check "subtree: A recomposed to the full-recompute pose" (p1.["A"] = full1.["A"])
    check "subtree: descendant B recomposed too" (p1.["B"] = full1.["B"])

    // Edge add composed incrementally: E under B — everything else untouched.
    let g2 = g1 |> add "E" "B" (tr 0.5)
    let p2 = RegGraph.composeSubtree "E" (Map.add "D" sentinel full1) g2
    check "add: E composed under B, rest untouched"
        (p2.["E"] = (RegGraph.composeAll g2).["E"] && p2.["D"] = sentinel && p2.["A"] = full1.["A"])

// ──────────────── Registration graph: per-edge before/after ───────────────

let regEdgeSideTests () =
    let tr (x : float) = Trafo3d.Translation(V3d(x, 0.0, 0.0))
    let add mov ref t g =
        match RegGraph.tryAddEdge mov ref t 1.0 g with
        | EdgeAdded g -> g
        | EdgeClosesLoop _ -> check (sprintf "add %s→%s (unexpected loop)" mov ref) false; g
        | EdgeRejected e -> check (sprintf "add %s→%s (%s)" mov ref e) false; g
    // 2-hop chain R ← A ← B.
    let tA = Trafo3d.RotationZ (Math.PI / 2.0)
    let tB = tr 2.0
    let g = { Root = Some "R"; Edges = Map.empty } |> add "A" "R" tA |> add "B" "A" tB

    let after = RegGraph.composeEdge "B" EdgeAfter g
    let before = RegGraph.composeEdge "B" EdgeBefore g
    check "edge B after = the committed composition" (after = RegGraph.composeAll g)
    // Only what edge B changes moves: the ancestor's pose is identical on both
    // sides; B-before rides at its parent's registered pose (edge zeroed).
    check "edge B before: ancestor A untouched" (before.["A"] = after.["A"])
    check "edge B before: B rides at its parent's pose" (before.["B"] = after.["A"])
    check "edge B before ≠ after for B" (before.["B"] <> after.["B"])

    // Edge A: its whole subtree changes (B rides along), the root side does not;
    // B keeps ITS OWN edge applied — only edge A is zeroed.
    let beforeA = RegGraph.composeEdge "A" EdgeBefore g
    check "edge A before: A unregistered" (beforeA.["A"] = Trafo3d.Identity)
    check "edge A before: descendant keeps its own edge" (beforeA.["B"] = tB)

    // The query never mutates the committed graph.
    check "composeEdge is pure" (g.Edges.["A"].Transform = tA && g.Edges.["B"].Transform = tB)

// ───────────────────── Navigator: pair-cell state ─────────────────────────

let pairCellTests () =
    let tr (x : float) = Trafo3d.Translation(V3d(x, 0.0, 0.0))
    let add mov ref q g =
        match RegGraph.tryAddEdge mov ref (tr 1.0) q g with
        | EdgeAdded g -> g
        | EdgeClosesLoop _ -> check (sprintf "add %s→%s (unexpected loop)" mov ref) false; g
        | EdgeRejected e -> check (sprintf "add %s→%s (%s)" mov ref e) false; g
    let g = { Root = Some "R"; Edges = Map.empty } |> add "A" "R" 0.8 |> add "B" "A" 0.3
    let overlap = Map.ofList [ PairCell.key "R" "C", true; PairCell.key "C" "D", false ]
    check "pair key is unordered" (PairCell.key "b" "a" = PairCell.key "a" "b")
    check "registered pair (either orientation) carries its quality"
        (PairCell.state overlap g "A" "R" = PairRegistered 0.8
         && PairCell.state overlap g "R" "A" = PairRegistered 0.8
         && PairCell.state overlap g "A" "B" = PairRegistered 0.3)
    check "overlapping unregistered pair = possible" (PairCell.state overlap g "R" "C" = PairPossible)
    check "insufficient overlap = impossible" (PairCell.state overlap g "C" "D" = PairImpossible)
    check "unknown overlap = impossible until fetched" (PairCell.state overlap g "A" "D" = PairImpossible)
    check "an edge is ground truth over the overlap verdict"
        (PairCell.state (Map.add (PairCell.key "A" "R") false overlap) g "A" "R" = PairRegistered 0.8)

// ───────────────────────── Registration graph: reroot ─────────────────────

let rerootTests () =
    let tr (x : float) = Trafo3d.Translation(V3d(x, 0.0, 0.0))
    let add mov ref t q g =
        match RegGraph.tryAddEdge mov ref t q g with
        | EdgeAdded g -> g
        | EdgeClosesLoop _ -> check (sprintf "add %s→%s (unexpected loop)" mov ref) false; g
        | EdgeRejected e -> check (sprintf "add %s→%s (%s)" mov ref e) false; g
    // R ← A(tA) ← B(tB), branch R ← D(tD).
    let tA = Trafo3d.RotationZ (Math.PI / 2.0)
    let tB = tr 2.0
    let tD = tr 5.0
    let g =
        { Root = Some "R"; Edges = Map.empty }
        |> add "A" "R" tA 0.7 |> add "B" "A" tB 0.4 |> add "D" "R" tD 0.9

    let g2 = RegGraph.reroot "B" g
    check "reroot: new root set" (g2.Root = Some "B")
    check "reroot: same members, still one tree"
        ([ "A"; "B"; "D"; "R" ] |> List.forall (RegGraph.inTree g2) && g2.Edges.Count = 3)
    // REF/MOV flips exactly along the path B→A→R; the branch D is untouched.
    check "reroot: path edges flipped (REF/MOV)"
        (g2.Edges.["A"].Parent = "B" && g2.Edges.["R"].Parent = "A"
         && not (Map.containsKey "B" g2.Edges))
    check "reroot: off-path edge untouched (reference-equal)"
        (obj.ReferenceEquals(g2.Edges.["D"], g.Edges.["D"]))
    check "reroot: quality rides its edge through the flip"
        (g2.Edges.["A"].Quality = 0.4 && g2.Edges.["R"].Quality = 0.7 && g2.Edges.["D"].Quality = 0.9)

    // Every pose recomposes relative to the new root: pose'(m) = pose(m) ∘ pose(B)⁻¹.
    let p1 = RegGraph.composeAll g
    let p2 = RegGraph.composeAll g2
    let poseOld m = if m = "R" then Trafo3d.Identity else p1.[m]
    let poseNew m = if m = "B" then Trafo3d.Identity else p2.[m]
    let probe = V3d(1.0, 2.0, 3.0)
    check "reroot: every pose relative to the new root"
        ([ "A"; "B"; "D"; "R" ] |> List.forall (fun m ->
            let expected = (poseOld m * p1.["B"].Inverse).Forward.TransformPos probe
            Vec.distance ((poseNew m).Forward.TransformPos probe) expected < 1e-9))

    // The invariant survives: adds still work, cycles still reject.
    check "reroot: tree accepts new edges" (match RegGraph.tryAddEdge "E" "B" (tr 1.0) 1.0 g2 with EdgeAdded _ -> true | _ -> false)
    check "reroot: redundant adds close transient loops" (match RegGraph.tryAddEdge "R" "D" (tr 1.0) 1.0 g2 with EdgeClosesLoop _ -> true | _ -> false)

    // Degenerate inputs: current root and non-members return the graph as-is.
    check "reroot to current root = unchanged" (obj.ReferenceEquals(RegGraph.reroot "R" g, g))
    check "reroot to non-member = unchanged (caller decides)" (obj.ReferenceEquals(RegGraph.reroot "X" g, g))

// ──────────── Registration graph: edge invalidation + solve quality ────────

let edgeInvalidationTests () =
    let tr (x : float) = Trafo3d.Translation(V3d(x, 0.0, 0.0))
    let add mov ref g =
        match RegGraph.tryAddEdge mov ref (tr 1.0) 0.5 g with
        | EdgeAdded g -> g
        | EdgeClosesLoop _ -> check (sprintf "add %s→%s (unexpected loop)" mov ref) false; g
        | EdgeRejected e -> check (sprintf "add %s→%s (%s)" mov ref e) false; g
    // R ← A ← B ← C, branch R ← D.
    let g = { Root = Some "R"; Edges = Map.empty } |> add "A" "R" |> add "B" "A" |> add "C" "B" |> add "D" "R"

    // A leaf edge drops alone.
    let g1 = RegGraph.removeEdgeCascading "C" g
    check "cascade: leaf removal drops one edge"
        (g1.Edges.Count = 3 && not (Map.containsKey "C" g1.Edges) && Map.containsKey "B" g1.Edges)
    // A mid-tree edge takes its whole subtree (stranding is forbidden).
    let g2 = RegGraph.removeEdgeCascading "A" g
    check "cascade: mid-tree removal drops the subtree"
        (g2.Edges.Count = 1 && Map.containsKey "D" g2.Edges
         && not (RegGraph.inTree g2 "A") && not (RegGraph.inTree g2 "B") && not (RegGraph.inTree g2 "C"))
    check "cascade: untouched branch keeps its edge (reference-equal)"
        (obj.ReferenceEquals(g2.Edges.["D"], g.Edges.["D"]))

    // withEdge replaces transform AND quality in place.
    let g3 = RegGraph.withEdge "D" (tr 9.0) 0.9 g
    check "withEdge replaces payload"
        (g3.Edges.["D"].Transform.Forward.M03 = 9.0 && g3.Edges.["D"].Quality = 0.9
         && g3.Edges.["D"].Parent = "R")

    // Quality: perfect solve → 1; monotone decreasing in rms; stays in (0, 1].
    check "quality: zero residuals = 1" (RegGraph.solveQuality [| 0.0; 0.0; 0.0 |] = 1.0)
    let q5 = RegGraph.solveQuality [| 0.05; 0.05; 0.05 |]
    let q20 = RegGraph.solveQuality [| 0.2; 0.2; 0.2 |]
    check "quality: halves at 5 cm rms" (abs (q5 - 0.5) < 1e-9)
    check "quality: monotone + bounded" (q20 < q5 && q20 > 0.0 && q5 < 1.0)
    check "quality: no residuals = 0" (RegGraph.solveQuality [||] = 0.0)

    // REF/MOV of a pair: REF = the endpoint nearer the root.
    check "pairRefMov: registered pair follows the edge"
        (MatrixNav.pairRefMov g "A" "B" = ("A", "B") && MatrixNav.pairRefMov g "B" "A" = ("A", "B"))
    check "pairRefMov: unregistered pair by hop depth"
        (MatrixNav.pairRefMov g "D" "B" = ("D", "B"))    // D depth 1 < B depth 2
    check "pairRefMov: unconnected mesh is always MOV"
        (MatrixNav.pairRefMov g "X" "A" = ("A", "X"))
    check "pairRefMov: both unconnected falls back to key order"
        (MatrixNav.pairRefMov g "Y" "X" = ("X", "Y"))

// ────────────── Registration graph: transient loops + resolution ──────────

let loopTests () =
    let tr (x : float) = Trafo3d.Translation(V3d(x, 0.0, 0.0))
    let add mov ref t q g =
        match RegGraph.tryAddEdge mov ref t q g with
        | EdgeAdded g -> g
        | _ -> check (sprintf "add %s→%s" mov ref) false; g
    let probe = V3d(1.0, 2.0, 3.0)
    // R ← A(rot90) ← B(+2x); branch R ← D(+5x).
    let tA = Trafo3d.RotationZ (Math.PI / 2.0)
    let g =
        { Root = Some "R"; Edges = Map.empty }
        |> add "A" "R" tA 0.9 |> add "B" "A" (tr 2.0) 0.4 |> add "D" "R" (tr 5.0) 0.7
    let poses = RegGraph.composeAll g
    // The EXACT redundant edge B→D: both paths agree ⇒ residual = identity.
    let tExact = poses.["B"] * poses.["D"].Inverse
    (match RegGraph.tryAddEdge "B" "D" tExact 0.8 g with
     | EdgeClosesLoop (cycle, residual) ->
        check "loop: cycle = the tree path B→A→R→D"
            ((cycle |> List.map (fun e -> e.Child)) = [ "B"; "A"; "D" ])
        checkLe "loop: consistent edge ⇒ residual rotation ≈ 0°"
            (RegGraph.residualRotationDeg residual) 1e-7
        checkLe "loop: consistent edge ⇒ residual displacement ≈ 0"
            (RegGraph.residualAt residual probe) 1e-9
     | _ -> check "loop: redundant add closes a transient loop" false)
    // A perturbed edge: rigid conjugation preserves the translation LENGTH, so
    // the displacement residual reads the injected 5 cm exactly; rotation 0.
    (match RegGraph.tryAddEdge "B" "D" (tExact * tr 0.05) 0.8 g with
     | EdgeClosesLoop (_, residual) ->
        checkLe "loop: 5 cm perturbation reads 5 cm"
            (abs (RegGraph.residualAt residual probe - 0.05)) 1e-9
        checkLe "loop: pure-translation perturbation ⇒ rotation 0"
            (RegGraph.residualRotationDeg residual) 1e-7
     | _ -> check "loop: perturbed add closes a loop" false)

    // Every kept edge constraint must hold after a resolve: pose(child) =
    // T ∘ pose(parent) — unique, consistent poses.
    let consistent (g2 : RegGraph) =
        let ps = RegGraph.composeAll g2
        let poseOf m = if g2.Root = Some m then Trafo3d.Identity else ps.[m]
        g2.Edges |> Map.forall (fun _ e ->
            Vec.distance
                ((poseOf e.Child).Forward.TransformPos probe)
                ((e.Transform * poseOf e.Parent).Forward.TransformPos probe) < 1e-9)

    // Remove the mid-path edge A: the detached {A, B} re-hangs through B→D,
    // A's edge re-orients (A now hangs off B).
    let g2 = RegGraph.resolveLoop "B" "D" tExact 0.8 "A" g
    check "resolve: spanning tree over the same members"
        (g2.Edges.Count = 3 && [ "A"; "B"; "D"; "R" ] |> List.forall (RegGraph.inTree g2))
    check "resolve: new edge landed with its quality"
        (g2.Edges.["B"].Parent = "D" && g2.Edges.["B"].Quality = 0.8)
    check "resolve: detached path re-oriented" (g2.Edges.["A"].Parent = "B")
    check "resolve: every kept constraint holds" (consistent g2)
    // The exact edge closes an agreeing loop ⇒ resolving must not move anything.
    let p2 = RegGraph.composeAll g2
    check "resolve: exact loop ⇒ poses unchanged"
        ([ "A"; "B"; "D" ] |> List.forall (fun m ->
            Vec.distance (p2.[m].Forward.TransformPos probe) (poses.[m].Forward.TransformPos probe) < 1e-9))

    // Removing the MOV's own edge = the plain swap.
    let g3 = RegGraph.resolveLoop "B" "D" tExact 0.8 "B" g
    check "resolve: removing the MOV's edge swaps it for the new one"
        (g3.Edges.["B"].Parent = "D" && g3.Edges.["A"].Parent = "R" && g3.Edges.Count = 3 && consistent g3)

    // REF-side removal: {D} detaches ⇒ the new edge lands INVERTED, keyed D.
    let g4 = RegGraph.resolveLoop "B" "D" tExact 0.8 "D" g
    check "resolve: ref-side removal inverts the new edge"
        (g4.Edges.["D"].Parent = "B" && g4.Edges.Count = 3 && consistent g4)

// ─────────────────────── Navigator: hop depth ─────────────────────────────

let hopDepthTests () =
    let tr = Trafo3d.Translation(V3d(1.0, 0.0, 0.0))
    let add mov ref g =
        match RegGraph.tryAddEdge mov ref tr 1.0 g with
        | EdgeAdded g -> g
        | EdgeClosesLoop _ -> check (sprintf "add %s→%s (unexpected loop)" mov ref) false; g
        | EdgeRejected e -> check (sprintf "add %s→%s (%s)" mov ref e) false; g
    let g = { Root = Some "R"; Edges = Map.empty } |> add "B" "R" |> add "C" "B"

    check "hop depth: root 0, chain counts, unconnected none"
        (MatrixNav.hopDepth g "R" = Some 0 && MatrixNav.hopDepth g "B" = Some 1
         && MatrixNav.hopDepth g "C" = Some 2 && MatrixNav.hopDepth g "A" = None)

[<EntryPoint>]
let main _ =
    umeyamaTests ()
    sliceCoreTests ()
    regGraphTests ()
    regComposeTests ()
    regEdgeSideTests ()
    pairCellTests ()
    rerootTests ()
    edgeInvalidationTests ()
    loopTests ()
    hopDepthTests ()
    printfn ""
    printfn "%d/%d passed%s" (total - failures) total (if failures = 0 then "" else sprintf " — %d FAILED" failures)
    failures
