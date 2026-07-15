namespace Superprojekt

open System
open Aardvark.Base

[<RequireQualifiedAccess>]
type ScanPinId = ScanPinId of Guid with
    static member create () = ScanPinId (Guid.NewGuid())

type AnchorSource =
    | AnchorAuto
    | AnchorPick3D

// Point is mesh-local (the mesh's own untransformed frame); the world position is
// derived via the mesh's displayed transform, so the before/after toggle moves
// each anchor with its mesh automatically.
type MeshAnchor = {
    Point    : V3d
    Source   : AnchorSource
}

// Every pin is a registration correspondence (there is no enable/disable
// distinction); a pin's correspondence is seeded as soon as a reference exists.
type Correspondence = {
    // Pin centre if the host is the reference mesh, else its closest-point
    // projection onto the reference. None until seeded.
    RefAnchor   : V3d option
    Anchors     : Map<string, MeshAnchor>
    // ROI membership computed server-side during seed: true = the mesh has surface
    // inside the pin ROI (closest point ≤ roiRadius). Absent ⇒ not yet evaluated.
    InRoi       : Map<string, bool>
}

module Correspondence =
    let empty = {
        RefAnchor   = None
        Anchors     = Map.empty
        InRoi       = Map.empty
    }

    // The (pin, mesh) anchor in the mesh's OWN frame: the reference mesh's marker
    // is the RefAnchor, any other mesh's its Anchors entry. Callers map to world
    // via the mesh's displayed pose (a no-op for the reference at load pose).
    let anchorOwn (isRef : bool) (mesh : string) (c : Correspondence) =
        if isRef then c.RefAnchor
        else Map.tryFind mesh c.Anchors |> Option.map (fun a -> a.Point)

// Provenance of a solve: the exact correspondence data it consumed — per pin the
// reference anchor and the mesh-local anchor point of every (pin, mesh) pair fed
// to the solver. A registration is only as valid as these inputs: if any tracked
// pin/point is deleted or moved afterwards, the solve is stale and is cleared
// (the solve-validity postlude compares against this snapshot).
type SolveInputs = {
    RefMesh : string
    Pins    : Map<ScanPinId, V3d * Map<string, V3d>>
}

// λ2/λ1 of a weighted 3D point spread (client-side conditioning pre-check for the
// readiness line; the authoritative value comes from the server).
module RegConditioning =
    let private jacobiEigenvalues (m : M33d) =
        let a = [|
            [| m.M00; m.M01; m.M02 |]
            [| m.M10; m.M11; m.M12 |]
            [| m.M20; m.M21; m.M22 |]
        |]
        for _ in 0 .. 31 do
            let mutable p = 0
            let mutable q = 1
            let mutable off = abs a.[0].[1]
            if abs a.[0].[2] > off then off <- abs a.[0].[2]; p <- 0; q <- 2
            if abs a.[1].[2] > off then off <- abs a.[1].[2]; p <- 1; q <- 2
            if off > 1e-14 then
                let app = a.[p].[p]
                let aqq = a.[q].[q]
                let apq = a.[p].[q]
                let theta = 0.5 * (aqq - app) / apq
                let t = (if theta >= 0.0 then 1.0 else -1.0) / (abs theta + sqrt (theta * theta + 1.0))
                let c = 1.0 / sqrt (t * t + 1.0)
                let s = t * c
                for k in 0 .. 2 do
                    let akp = a.[k].[p]
                    let akq = a.[k].[q]
                    a.[k].[p] <- c * akp - s * akq
                    a.[k].[q] <- s * akp + c * akq
                for k in 0 .. 2 do
                    let apk = a.[p].[k]
                    let aqk = a.[q].[k]
                    a.[p].[k] <- c * apk - s * aqk
                    a.[q].[k] <- s * apk + c * aqk
        [| a.[0].[0]; a.[1].[1]; a.[2].[2] |] |> Array.sortDescending

    // Descending eigenvalues of the weighted covariance of `points`.
    let spreadEigenvalues (points : (V3d * float)[]) =
        let wSum = points |> Array.sumBy snd
        if points.Length < 2 || wSum <= 0.0 then [| 0.0; 0.0; 0.0 |]
        else
            let mean = (points |> Array.sumBy (fun (p, w) -> p * w)) / wSum
            let mutable xx = 0.0
            let mutable xy = 0.0
            let mutable xz = 0.0
            let mutable yy = 0.0
            let mutable yz = 0.0
            let mutable zz = 0.0
            for (p, w) in points do
                let d = p - mean
                xx <- xx + w * d.X * d.X
                xy <- xy + w * d.X * d.Y
                xz <- xz + w * d.X * d.Z
                yy <- yy + w * d.Y * d.Y
                yz <- yz + w * d.Y * d.Z
                zz <- zz + w * d.Z * d.Z
            let s = 1.0 / wSum
            jacobiEigenvalues (M33d(xx * s, xy * s, xz * s,
                                    xy * s, yy * s, yz * s,
                                    xz * s, yz * s, zz * s))

    let lambdaRatio (eigenvalues : float[]) =
        if eigenvalues.Length < 2 || eigenvalues.[0] <= 1e-12 then 0.0
        else max 0.0 eigenvalues.[1] / eigenvalues.[0]

// Readiness engine: pure over a dedicated input DTO so Supertests can table-drive
// it; the adaptive adapter is in Primitives.ReadinessView.

type Severity =
    | Blocker
    | Warning
    | Ready
    | Info

type Diagnostic = {
    Severity : Severity
    Text     : string
}

type ReadinessPin = {
    // reference anchor + reliability (the collinearity input)
    RefAnchor     : (V3d * float) option
    // moving meshes with an anchor for this pin
    Accepted      : Set<string>
}

type ReadinessInput = {
    ReferenceMesh : string option
    MovingMeshes  : string list
    EnabledPins   : ReadinessPin list
}

module Readiness =

    let pairCounts (input : ReadinessInput) =
        input.MovingMeshes
        |> List.map (fun mesh ->
            mesh, (input.EnabledPins |> List.sumBy (fun p -> if Set.contains mesh p.Accepted then 1 else 0)))

    let lambdaRatioOf (input : ReadinessInput) =
        input.EnabledPins
        |> List.choose (fun p -> p.RefAnchor)
        |> Array.ofList
        |> RegConditioning.spreadEigenvalues
        |> RegConditioning.lambdaRatio

    // Rules evaluated in order, all matches emitted; display sorts by severity.
    let compute (input : ReadinessInput) : Diagnostic list =
        let diags = ResizeArray<Diagnostic>()
        let add severity text =
            diags.Add { Severity = severity; Text = text }

        // Hard blockers are only: no reference, and zero SOLVABLE meshes (so a
        // partial overlap still solves the meshes that do have ≥3 markers). A mesh
        // short of 3 markers is a per-mesh WARNING, not a global blocker.
        if input.ReferenceMesh.IsNone then
            add Blocker "Set a reference (★)"

        if List.isEmpty input.EnabledPins then
            add Blocker "Need ≥3 pins"

        let counts = pairCounts input
        let anySolvable = counts |> List.exists (fun (_, n) -> n >= 3)

        // Per-mesh ("+N marker(s)") and per-pin ("N without a marker") hints were
        // removed — the pin×mesh matrix now surfaces that detail. Only the GLOBAL
        // reconstruction readiness remains (it moves to the top bar).

        // Zero solvable meshes (pins exist, moving meshes exist, none reaches 3) is
        // the only marker-related hard blocker.
        if input.ReferenceMesh.IsSome
           && not (List.isEmpty input.EnabledPins)
           && not (List.isEmpty input.MovingMeshes)
           && not anySolvable then
            add Blocker "No mesh has ≥3 markers yet"

        if List.length input.EnabledPins >= 3 && lambdaRatioOf input < 1e-3 then
            let affected =
                counts |> List.filter (fun (_, n) -> n >= 3) |> List.map fst
            let suffix =
                if List.isEmpty affected then ""
                else sprintf " (%s)" (String.concat ", " affected)
            add Warning (sprintf "Pins near-collinear%s" suffix)

        if input.ReferenceMesh.IsSome && List.isEmpty input.MovingMeshes then
            add Info "No moving meshes to solve"

        let blocked = diags |> Seq.exists (fun d -> d.Severity = Blocker)
        if not blocked && counts |> List.exists (fun (_, n) -> n >= 3) then
            add Ready "Ready to align"

        List.ofSeq diags

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
