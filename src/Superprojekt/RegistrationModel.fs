namespace Superprojekt

open System
open Aardvark.Base

[<RequireQualifiedAccess>]
type ScanPinId = ScanPinId of Guid with
    static member create () = ScanPinId (Guid.NewGuid())

type AnchorSource =
    | AnchorAuto
    | AnchorPick3D

module AnchorSource =
    let tag = function AnchorAuto -> "auto" | AnchorPick3D -> "pick3d"
    let ofTag = function "pick3d" -> AnchorPick3D | _ -> AnchorAuto

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
    RefDistance : float
    Anchors     : Map<string, MeshAnchor>
    Residuals   : Map<string, float>
    // ROI membership computed server-side during seed: true = the mesh has surface
    // inside the pin ROI (closest point ≤ roiRadius). Absent ⇒ not yet evaluated.
    InRoi       : Map<string, bool>
}

module Correspondence =
    let empty = {
        RefAnchor   = None
        RefDistance = 0.0
        Anchors     = Map.empty
        Residuals   = Map.empty
        InRoi       = Map.empty
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

    // First principal axis (dominant eigenvector) of an unweighted point set,
    // via power iteration on the covariance — used by the anchor cutaway to
    // orient the section plane along the line of maximum anchor spread.
    let dominantAxis (points : V3d[]) =
        if points.Length < 2 then V3d.OOI
        else
            let mean = (points |> Array.fold (+) V3d.Zero) / float points.Length
            let mutable xx = 0.0
            let mutable xy = 0.0
            let mutable xz = 0.0
            let mutable yy = 0.0
            let mutable yz = 0.0
            let mutable zz = 0.0
            for p in points do
                let d = p - mean
                xx <- xx + d.X * d.X
                xy <- xy + d.X * d.Y
                xz <- xz + d.X * d.Z
                yy <- yy + d.Y * d.Y
                yz <- yz + d.Y * d.Z
                zz <- zz + d.Z * d.Z
            let cov = M33d(xx, xy, xz, xy, yy, yz, xz, yz, zz)
            // seed with the coordinate axis of largest variance (not orthogonal
            // to the dominant eigenvector for non-degenerate sets)
            let mutable v =
                if xx >= yy && xx >= zz then V3d.IOO
                elif yy >= zz then V3d.OIO
                else V3d.OOI
            for _ in 0 .. 63 do
                let p = cov * v
                let l = p.Length
                if l > 1e-15 then v <- p / l
            if v.Length > 1e-9 then v.Normalized else V3d.OOI

    let lambdaRatio (eigenvalues : float[]) =
        if eigenvalues.Length < 2 || eigenvalues.[0] <= 1e-12 then 0.0
        else max 0.0 eigenvalues.[1] / eigenvalues.[0]

    let isCollinear (eigenvalues : float[]) = lambdaRatio eigenvalues < 1e-3

// Targets are world-space; reducer converts to render space at the boundary.
type FlyToTarget =
    | FlyToSphere of centre : V3d * radius : float
    | FlyToBounds of Box3d

module FlyToMath =
    // Vertical fov from the app's fixed 90° horizontal fov + viewport aspect.
    let fovY (horizontalFovDeg : float) (aspect : float) =
        2.0 * atan (tan (horizontalFovDeg * Math.PI / 360.0) / max 0.1 aspect)

    // The target sphere subtends ~25 % of the viewport height.
    let distance (fovYRad : float) (radius : float) =
        radius / tan (fovYRad * 0.125)

    let boundingSphere (target : FlyToTarget) =
        match target with
        | FlyToSphere (c, r) -> c, max 1e-3 r
        | FlyToBounds b -> b.Center, max 1e-3 (b.Size.Length * 0.5)

// Readiness engine: pure over a dedicated input DTO so Supertests can table-drive
// it; the adaptive adapter is in Primitives.ReadinessView.

type Severity =
    | Blocker
    | Warning
    | Ready
    | Info

type NavAction =
    | ReseedCorrespondence of meshFilter : string option
    | SelectPinOpenCard of ScanPinId
    | HighlightReferenceColumn
    | RunCoarse

type Diagnostic = {
    Severity : Severity
    Text     : string
    Action   : NavAction option
}

type ReadinessPin = {
    Id            : ScanPinId
    Label         : string
    // reference anchor + reliability (the collinearity input)
    RefAnchor     : (V3d * float) option
    // visible moving meshes with an anchor for this pin; Unresolved = those without
    Accepted      : Set<string>
    Unresolved    : int
}

type ReadinessInput = {
    ReferenceMesh       : string option
    VisibleMovingMeshes : string list
    EnabledPins         : ReadinessPin list
}

module Readiness =

    let pairCounts (input : ReadinessInput) =
        input.VisibleMovingMeshes
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
        let add severity text action =
            diags.Add { Severity = severity; Text = text; Action = action }

        if input.ReferenceMesh.IsNone then
            add Blocker "Set a reference (★)" (Some HighlightReferenceColumn)

        if List.isEmpty input.EnabledPins then
            add Blocker "Need ≥3 pins" None

        let counts = pairCounts input
        if input.ReferenceMesh.IsSome then
            for mesh, n in counts do
                if n < 3 then
                    add Blocker
                        (sprintf "%s: +%d marker(s)" mesh (3 - n))
                        (Some (ReseedCorrespondence (Some mesh)))

        for pin in input.EnabledPins do
            if pin.Unresolved > 0 then
                add Warning
                    (sprintf "%s: %d without a marker" pin.Label pin.Unresolved)
                    (Some (SelectPinOpenCard pin.Id))

        if List.length input.EnabledPins >= 3 && lambdaRatioOf input < 1e-3 then
            let affected =
                counts |> List.filter (fun (_, n) -> n >= 3) |> List.map fst
            let suffix =
                if List.isEmpty affected then ""
                else sprintf " (%s)" (String.concat ", " affected)
            add Warning
                (sprintf "Pins near-collinear%s" suffix) None

        if input.ReferenceMesh.IsSome && List.isEmpty input.VisibleMovingMeshes then
            add Info "No moving meshes to solve" None

        let blocked = diags |> Seq.exists (fun d -> d.Severity = Blocker)
        if not blocked && counts |> List.exists (fun (_, n) -> n >= 3) then
            add Ready "Ready to align" (Some RunCoarse)

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
