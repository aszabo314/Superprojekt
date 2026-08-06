namespace Superprojekt

open System
open Aardvark.Base

[<RequireQualifiedAccess>]
type ScanPinId = ScanPinId of Guid with
    static member create () = ScanPinId (Guid.NewGuid())

// Registration graph: nodes = meshes, edges = pairwise registrations. The
// committed graph is ONE rooted tree — every edge stores the rigid transform of
// its child (MOV, the endpoint farther from the root) onto its parent (REF, the
// endpoint nearer the root), in METRIC WORLD space at the meshes' as-loaded
// baselines (the lsq output convention: applying Transform to the child's
// baseline-posed surface aligns it with the parent's).
type RegEdge = {
    Child     : string
    Parent    : string
    Transform : Trafo3d
    // The edge's ONE quality scalar in [0, 1] (1 = best) — the navigator cell's
    // fill strength. Written by the pair solve alongside the transform.
    Quality   : float
}

type RegGraph = {
    // The pose anchor: worldPose(root) = identity. None only before any load.
    Root  : string option
    // child mesh → its edge. A rooted tree IS a parent map — acyclicity and
    // connectedness hold by construction of tryAddEdge.
    Edges : Map<string, RegEdge>
}

// Outcome of an edge add. Both-endpoints-already-connected is no longer a
// rejection: it closes exactly ONE fundamental cycle, TRANSIENTLY accepted for
// the caller to resolve (the P9 forced-resolution modal) — the COMMITTED graph
// stays the prior tree until then. Adds that would disconnect are still
// impossible (edges only ever connect).
type EdgeAddResult =
    | EdgeAdded of RegGraph
    | EdgeClosesLoop of cycleEdges : RegEdge list * residual : Trafo3d
    | EdgeRejected of string

// Before/after is PER EDGE: for an edge, Before = the graph with THIS edge
// unregistered (its transform = identity), After = as committed. It compares
// exactly the two edge meshes pre/post this edge — every ancestor edge stays
// applied through composition on both sides.
type EdgeSide =
    | EdgeBefore
    | EdgeAfter

module RegGraph =
    let empty = { Root = None; Edges = Map.empty }

    let inTree (g : RegGraph) (mesh : string) =
        g.Root = Some mesh || Map.containsKey mesh g.Edges

    let hasEdges (g : RegGraph) = not (Map.isEmpty g.Edges)

    // Directed tree path between two members: steps from `a` to `b`, each an
    // edge walked child→parent (up = true, apply Transform) or parent→child
    // (up = false, apply Transform.Inverse). Both must be in the tree.
    let treePath (g : RegGraph) (a : string) (b : string) : (RegEdge * bool) list =
        let rec chain n =
            match Map.tryFind n g.Edges with
            | Some e -> e :: chain e.Parent
            | None -> []
        // Strip the shared root-side suffix (the edges above the LCA).
        let rec strip (xa : RegEdge list) (xb : RegEdge list) =
            match xa, xb with
            | x :: ta, y :: tb when x.Child = y.Child -> strip ta tb
            | _ -> xa, xb
        let ua, ub = strip (List.rev (chain a)) (List.rev (chain b))
        (List.rev ua |> List.map (fun e -> e, true)) @ (ub |> List.map (fun e -> e, false))

    // Compose a directed path (apply-left-first: the first step first).
    let private pathTransformOf (steps : (RegEdge * bool) list) : Trafo3d =
        (Trafo3d.Identity, steps) ||> List.fold (fun acc (e, up) ->
            acc * (if up then e.Transform else e.Transform.Inverse))

    // Loop residual of a redundant edge (mov→ref, tNew): compose tNew with the
    // tree path back from ref to mov — identity iff the two paths agree. The
    // caller reports it as rotation° + displacement at a data point.
    let loopResidual (g : RegGraph) (mov : string) (ref : string) (tNew : Trafo3d) : Trafo3d =
        tNew * pathTransformOf (treePath g ref mov)

    // Residual rotation angle (°) of a mismatch transform.
    let residualRotationDeg (m : Trafo3d) =
        let f = m.Forward
        let trace = f.M00 + f.M11 + f.M22
        acos (clamp -1.0 1.0 ((trace - 1.0) / 2.0)) * 180.0 / System.Math.PI

    // Residual displacement (metres) the mismatch causes at a probe point.
    let residualAt (m : Trafo3d) (p : V3d) = (m.Forward.TransformPos p - p).Length

    // Committed-graph invariant: the edges form ONE tree containing the root.
    // Ref-in-tree + mov-out ⇒ added. Both in ⇒ the add closes exactly one
    // fundamental cycle — returned TRANSIENTLY (cycle edges + residual) for
    // the forced-resolution modal; the committed graph is untouched. Ref out ⇒
    // an isolated component, still impossible.
    let tryAddEdge (mov : string) (ref : string) (t : Trafo3d) (quality : float) (g : RegGraph) : EdgeAddResult =
        if g.Root.IsNone then EdgeRejected "no root designated"
        elif mov = ref then EdgeRejected "an edge needs two distinct meshes"
        elif inTree g mov && inTree g ref then
            EdgeClosesLoop (treePath g mov ref |> List.map fst, loopResidual g mov ref t)
        elif not (inTree g ref) then EdgeRejected (sprintf "%s is not connected to the root yet" ref)
        else EdgeAdded { g with Edges = Map.add mov { Child = mov; Parent = ref; Transform = t; Quality = quality } g.Edges }

    // Resolve a transient loop: commit the redundant edge (mov→ref, tNew) and
    // remove ONE tree edge on its cycle (removeChild = that edge's Child key).
    // The removal detaches a subtree containing exactly one of the endpoints
    // (the cycle crossed the removed edge once); that side re-hangs through the
    // new edge — its internal path re-orients like a reroot, the new edge
    // inverts when the REF side is the detached one — so the result is again a
    // spanning tree over the same members with uniquely defined poses.
    let resolveLoop (mov : string) (ref : string) (tNew : Trafo3d) (quality : float)
                    (removeChild : string) (g : RegGraph) : RegGraph =
        match Map.tryFind removeChild g.Edges with
        | None -> g
        | Some _ ->
            let edges = Map.remove removeChild g.Edges
            let rec topOf n =
                match Map.tryFind n edges with
                | Some e -> topOf e.Parent
                | None -> n
            let sEnd, outEnd, tChild =
                if topOf mov = removeChild then mov, ref, tNew
                else ref, mov, tNew.Inverse
            let rec pathEdges acc n =
                match Map.tryFind n edges with
                | Some e -> pathEdges (e :: acc) e.Parent
                | None -> acc
            let path = pathEdges [] sEnd
            let cleared = (edges, path) ||> List.fold (fun m e -> Map.remove e.Child m)
            let reversed =
                (cleared, path) ||> List.fold (fun m e ->
                    Map.add e.Parent
                        { Child = e.Parent; Parent = e.Child
                          Transform = e.Transform.Inverse; Quality = e.Quality } m)
            { g with Edges = Map.add sEnd { Child = sEnd; Parent = outEnd; Transform = tChild; Quality = quality } reversed }

    // Replace one edge's transform (a re-solve of that pair).
    let withEdgeTransform (child : string) (t : Trafo3d) (g : RegGraph) =
        match Map.tryFind child g.Edges with
        | Some e -> { g with Edges = Map.add child { e with Transform = t } g.Edges }
        | None -> g

    // Replace one edge's full payload (transform + quality) — the re-solve path.
    let withEdge (child : string) (t : Trafo3d) (quality : float) (g : RegGraph) =
        match Map.tryFind child g.Edges with
        | Some e -> { g with Edges = Map.add child { e with Transform = t; Quality = quality } g.Edges }
        | None -> g

    // Remove one edge AND every edge beneath it: the child's whole subtree
    // hangs through this edge — keeping those edges would strand an isolated
    // component (the committed-graph invariant forbids it).
    let removeEdgeCascading (child : string) (g : RegGraph) : RegGraph =
        let rec drop (edges : Map<string, RegEdge>) (c : string) =
            let kids = edges |> Map.toList |> List.choose (fun (k, e) -> if e.Parent = c then Some k else None)
            let edges = Map.remove c edges
            (edges, kids) ||> List.fold drop
        { g with Edges = drop g.Edges child }

    // The edge's single quality scalar from the solve's point residuals:
    // rms → (0, 1], 1 = perfect, halving at 5 cm rms (terrain-scale calibrated).
    let solveQuality (residuals : float[]) : float =
        if residuals.Length = 0 then 0.0
        else
            let rms = sqrt ((residuals |> Array.sumBy (fun r -> r * r)) / float residuals.Length)
            1.0 / (1.0 + rms / 0.05)

    let children (g : RegGraph) (parent : string) =
        g.Edges |> Map.toList |> List.choose (fun (c, e) -> if e.Parent = parent then Some c else None)

    // Re-root the tree at a member mesh: every edge on the path new-root →
    // old-root reverses (child/parent swap = the REF/MOV flip, transform
    // inverted, quality kept); every other edge is untouched — a subtree
    // hanging off a path node simply follows it. All composed poses become
    // relative to the new root: pose'(m) = pose(m) ∘ pose(newRoot)⁻¹.
    // Non-members return the graph unchanged — an existing registration cannot
    // hang off a mesh outside its tree; the caller decides what that means.
    let reroot (newRoot : string) (g : RegGraph) : RegGraph =
        if g.Root = Some newRoot || not (inTree g newRoot) then g
        else
            let rec pathEdges acc m =
                match Map.tryFind m g.Edges with
                | Some e -> pathEdges (e :: acc) e.Parent
                | None -> acc
            let path = pathEdges [] newRoot
            // Remove ALL path children first — a reversed edge re-uses the next
            // path edge's child as its key, so interleaving would drop edges.
            let cleared = (g.Edges, path) ||> List.fold (fun m e -> Map.remove e.Child m)
            let edges =
                (cleared, path) ||> List.fold (fun m e ->
                    Map.add e.Parent
                        { Child = e.Parent; Parent = e.Child
                          Transform = e.Transform.Inverse; Quality = e.Quality } m)
            { Root = Some newRoot; Edges = edges }

    // The edge of an UNORDERED mesh pair, whichever way it is oriented.
    let pairEdge (a : string) (b : string) (g : RegGraph) : RegEdge option =
        match Map.tryFind a g.Edges with
        | Some e when e.Parent = b -> Some e
        | _ ->
            match Map.tryFind b g.Edges with
            | Some e when e.Parent = a -> Some e
            | _ -> None

    // Composed world pose of every tree mesh, walking from the root: the root's
    // pose is identity and is NOT in the map; worldPose(m) = edge.Transform then
    // the parent's own pose (`edge.Transform * parentPose` — Aardvark's `*`
    // applies left first). A root-child's pose IS its edge transform (the same
    // instance), so a star graph reproduces the star poses exactly.
    let composeAll (g : RegGraph) : Map<string, Trafo3d> =
        match g.Root with
        | None -> Map.empty
        | Some root ->
            let rec walk (acc : Map<string, Trafo3d>) (parent : string) =
                (acc, children g parent) ||> List.fold (fun acc c ->
                    let pose =
                        match Map.tryFind parent acc with
                        | Some pp -> g.Edges.[c].Transform * pp
                        | None -> g.Edges.[c].Transform    // parent = root (identity)
                    walk (Map.add c pose acc) c)
            walk Map.empty root

    // The per-edge before/after pose query (peek pose-key, cell diagram sides):
    // composed world poses of the whole tree with the edge's side applied —
    // EdgeBefore zeroes exactly this one edge, so only the edge child's subtree
    // differs between the two sides.
    let composeEdge (child : string) (side : EdgeSide) (g : RegGraph) : Map<string, Trafo3d> =
        match side with
        | EdgeAfter -> composeAll g
        | EdgeBefore -> composeAll (withEdgeTransform child Trafo3d.Identity g)

    // Memoized recompute after ONE edge changed (transform replaced, or a fresh
    // edge added): only the changed child's subtree recomposes — every other
    // mesh keeps its previous, reference-equal pose. `prev` must be the current
    // composition of the same graph (minus the changed subtree).
    let composeSubtree (changedChild : string) (prev : Map<string, Trafo3d>) (g : RegGraph) : Map<string, Trafo3d> =
        match Map.tryFind changedChild g.Edges with
        | None -> composeAll g
        | Some _ ->
            let rec walk (acc : Map<string, Trafo3d>) (child : string) =
                let e = g.Edges.[child]
                let pose =
                    match Map.tryFind e.Parent acc with
                    | Some pp -> e.Transform * pp
                    | None -> e.Transform
                (Map.add child pose acc, children g child) ||> List.fold walk
            walk prev changedChild

// A transient loop awaiting FORCED resolution (the blocking modal's state):
// the redundant edge (mov→ref from a solve) + the fundamental cycle's tree
// edges + the loop residual. The committed graph stays the PRIOR tree until
// confirm; cancel simply drops this record. Selected = the cycle edge to
// remove (its Child key), None = the new edge itself.
type LoopPending = {
    Mov            : string
    Ref            : string
    Transform      : Trafo3d
    Quality        : float
    CycleEdges     : RegEdge list
    ResidualRotDeg : float
    ResidualTransM : float
    Selected       : string option
    // Transient row-hover preview of a choice, same encoding as Selected
    // (inner None = the new edge); the embedded tree highlights
    // Hover-else-Selected, so the binary choice is visible before commit.
    Hover          : string option option
}

// Cell state of the mesh×mesh navigator: the (unordered) pair either cannot be
// registered (insufficient overlap — a hole), can be (an empty vessel), or IS
// registered — then the cell carries the edge's single quality scalar.
type PairCell =
    | PairImpossible
    | PairPossible
    | PairRegistered of quality : float

module PairCell =
    // Unordered-pair key of the overlap cache.
    let key (a : string) (b : string) = (min a b, max a b)

    // overlap: pair key → sufficient. An unfetched pair reads as impossible
    // until the lazy overlap sweep lands (never "possible" on no evidence).
    let state (overlap : Map<string * string, bool>) (g : RegGraph) (a : string) (b : string) =
        match RegGraph.pairEdge a b g with
        | Some e -> PairRegistered e.Quality
        | None ->
            match Map.tryFind (key a b) overlap with
            | Some true -> PairPossible
            | _ -> PairImpossible

module MatrixNav =
    // Hops from the root along parent edges; None = not connected (or no root).
    let hopDepth (g : RegGraph) (mesh : string) : int option =
        if g.Root = Some mesh then Some 0
        else
            let rec walk acc m =
                match Map.tryFind m g.Edges with
                | Some e when g.Root = Some e.Parent -> Some (acc + 1)
                | Some e -> walk (acc + 1) e.Parent
                | None -> None
            walk 0 mesh

    // REF/MOV of an unordered pair: REF = the endpoint nearer the root (the
    // edge's parent when the pair is registered; hop depth otherwise, with
    // unconnected = farthest). Ties fall back to the pair-key order. Returns
    // (ref, mov).
    let pairRefMov (g : RegGraph) (a : string) (b : string) : string * string =
        match RegGraph.pairEdge a b g with
        | Some e -> e.Parent, e.Child
        | None ->
            let ka, kb = PairCell.key a b
            let depth m = match hopDepth g m with Some d -> d | None -> System.Int32.MaxValue
            if depth kb < depth ka then kb, ka else ka, kb

module Workflow =
    // Spanned = every mesh in the ONE rooted tree — purely topological, no
    // quality threshold ever gates it. ≥1 edge required, so a single-mesh
    // dataset (its root trivially in the tree) never reads spanned.
    let spanned (names : string list) (g : RegGraph) =
        not (List.isEmpty names)
        && RegGraph.hasEdges g
        && names |> List.forall (RegGraph.inTree g)

// The ONE in-cell error range: signed (lo, hi) in metres spanning 0 over the
// pair's pin-ROI samples, hard-capped at ±0.5 m — the shared scale of the
// false-colour map, the diagram x-range envelope and the legend. No samples →
// the full ±0.5 m.
module ErrorRange =
    let cap = 0.5
    let ofSamples (samples : seq<float>) : float * float =
        let mutable lo = 0.0
        let mutable hi = 0.0
        let mutable any = false
        for v in samples do
            any <- true
            if v < lo then lo <- v
            if v > hi then hi <- v
        if not any then (-cap, cap)
        else (max -cap lo, min cap hi)

    // Fallback scale for a cell WITHOUT pin samples: the per-vertex distance
    // distribution itself — per-sign 95th percentile (robust against the far
    // tail), 1 mm floor per side, same ±cap. Normalizing such a cell to the
    // ±cap default would wash the whole map into the near-white centre.
    let ofDistances (dist : float32[]) : float * float =
        let finite =
            dist |> Array.choose (fun v ->
                let v = float v in if abs v < 1e20 then Some v else None)
        if finite.Length = 0 then (-cap, cap)
        else
            let neg = finite |> Array.filter (fun v -> v < 0.0) |> Array.map abs |> Array.sort
            let pos = finite |> Array.filter (fun v -> v > 0.0) |> Array.sort
            let pct (a : float[]) =
                if a.Length = 0 then 0.0 else a.[min (a.Length - 1) (int (0.95 * float a.Length))]
            (max -cap (-(max 0.001 (pct neg))), min cap (max 0.001 (pct pos)))

// Drives the intrinsic per-fragment channels in the mesh shader (the extrinsic
// m3c2 surface map is the separate DistanceEncoding path).
type HeatmapMode =
    | HeatOff
    // camera-incidence (grazing-angle) false colour.
    | HeatIncidence
    // range from the mesh's own origin (= sensor).
    | HeatRange
    // triangle shape quality (thin/degenerate → low).
    | HeatShape
