namespace Superprojekt

open System
open System.Text
open System.Text.Json
open Aardvark.Base

[<RequireQualifiedAccess>]
type ScanPinId = ScanPinId of Guid with
    static member create () = ScanPinId (Guid.NewGuid())

// Correspondence anchors: one per (pin × moving mesh), world-space at the
// mesh's *committed* pose. Commit/rollback re-base them by the applied world
// delta so they stay on the surface.
type AnchorSource =
    | AnchorAuto
    | AnchorPatch2D
    | AnchorPick3D
    | AnchorViolinAxial

module AnchorSource =
    let label = function
        | AnchorAuto -> "auto" | AnchorPatch2D -> "patch"
        | AnchorPick3D -> "3D" | AnchorViolinAxial -> "violin"
    let tag = function
        | AnchorAuto -> "auto" | AnchorPatch2D -> "patch2d"
        | AnchorPick3D -> "pick3d" | AnchorViolinAxial -> "violin"
    let ofTag = function
        | "patch2d" -> AnchorPatch2D | "pick3d" -> AnchorPick3D
        | "violin" -> AnchorViolinAxial | _ -> AnchorAuto

// One correspondence marker per (pin × moving mesh); a stored marker is
// applied (no separate accept/reject state).
type MeshAnchor = {
    Point    : V3d
    Source   : AnchorSource
}

type Correspondence = {
    Enabled     : bool
    // Pin centre if the host is the reference mesh, else its closest-point
    // projection onto the reference. None until seeded.
    RefAnchor   : V3d option
    RefDistance : float
    Anchors     : Map<string, MeshAnchor>
    // Per-mesh pair residual of the last coarse solve this pin took part in.
    Residuals   : Map<string, float>
}

module Correspondence =
    let empty = {
        Enabled     = true
        RefAnchor   = None
        RefDistance = 0.0
        Anchors     = Map.empty
        Residuals   = Map.empty
    }

// Registration history. Transforms are render-space; rollback restores
// TransformBefore verbatim.
type RegStage = StageCoarse | StageFine

type RegInputs =
    | CoarseInputs of (ScanPinId * float * Map<string, AnchorSource>)[]
    | FineInputs   of mode : string * anchorPins : ScanPinId[]

type RegStepOutput = {
    TransformBefore : Trafo3d
    TransformAfter  : Trafo3d
    RmsBefore       : float
    RmsAfter        : float
    AlgoResidBefore : float
}

type RegStep = {
    Step          : int
    Stage         : RegStage
    Mode          : string
    Timestamp     : DateTime
    ReferenceMesh : string
    Inputs        : RegInputs
    Outputs       : Map<string, RegStepOutput>
}

// Uncommitted solve result. Effective preview pose = committed * Delta
// (Trafo3d composition is postfix: committed applies first).
type PendingMeshResult = {
    Delta         : Trafo3d
    RmsBefore     : float
    RmsAfter      : float
}

type PendingRegistration = {
    Stage    : RegStage
    Mode     : string
    Inputs   : RegInputs
    Results  : Map<string, PendingMeshResult>
    Unsolved : string list
    Expected : int
}

module PendingRegistration =
    // Preview active ⇔ at least one solved mesh.
    let isPreview (p : PendingRegistration option) =
        match p with Some pr -> not (Map.isEmpty pr.Results) | None -> false

    let delta (mesh : string) (p : PendingRegistration option) =
        p |> Option.bind (fun pr -> Map.tryFind mesh pr.Results |> Option.map (fun r -> r.Delta))

type RegTransformState = {
    Transforms    : Map<string, Trafo3d>
    AlgoResiduals : Map<string, float>
    Log           : RegStep list   // newest first
}

module RegLog =
    let effective (committed : Trafo3d) (delta : Trafo3d) = committed * delta

    let buildStep
            (timestamp : DateTime) (referenceMesh : string)
            (pending : PendingRegistration) (st : RegTransformState) : RegStep =
        let outputs =
            pending.Results |> Map.map (fun mesh r ->
                let before = Map.tryFind mesh st.Transforms |> Option.defaultValue Trafo3d.Identity
                {
                    TransformBefore = before
                    TransformAfter  = effective before r.Delta
                    RmsBefore       = r.RmsBefore
                    RmsAfter        = r.RmsAfter
                    AlgoResidBefore = Map.tryFind mesh st.AlgoResiduals |> Option.defaultValue 0.0
                })
        {
            Step          = (st.Log |> List.tryHead |> Option.map (fun s -> s.Step) |> Option.defaultValue 0) + 1
            Stage         = pending.Stage
            Mode          = pending.Mode
            Timestamp     = timestamp
            ReferenceMesh = referenceMesh
            Inputs        = pending.Inputs
            Outputs       = outputs
        }

    let commit (step : RegStep) (st : RegTransformState) : RegTransformState =
        {
            Transforms    = step.Outputs |> Map.fold (fun m k o -> Map.add k o.TransformAfter m) st.Transforms
            AlgoResiduals = step.Outputs |> Map.fold (fun m k o -> Map.add k o.RmsAfter m) st.AlgoResiduals
            Log           = step :: st.Log
        }

    // Only the newest step can be rolled back; returns the popped step so the
    // caller can un-bake anchor points and fire the invalidation cascade.
    let rollback (st : RegTransformState) : (RegTransformState * RegStep) option =
        match st.Log with
        | step :: rest ->
            let st' = {
                Transforms    = step.Outputs |> Map.fold (fun m k o -> Map.add k o.TransformBefore m) st.Transforms
                AlgoResiduals = step.Outputs |> Map.fold (fun m k o -> Map.add k o.AlgoResidBefore m) st.AlgoResiduals
                Log           = rest
            }
            Some (st', step)
        | [] -> None

// λ2/λ1 of a weighted 3D point spread (client-side conditioning pre-check for
// the readiness line; the authoritative value comes from the server).
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

// JSON (de)serialization of the new workspace pieces, kept here (not
// Persistence.fs) so the round-trip is unit-testable outside the WASM project.
// LastSolveEntry: per-mesh diagnostics set on every solve response, survives
// commit, cleared for a mesh when its producing step is rolled back.
type SolveConditioning = {
    Eigenvalues         : float[]
    CollinearityWarning : bool
}

type LastSolveEntry = {
    Stage           : RegStage
    RmsBefore       : float
    RmsAfter        : float
    Conditioning    : SolveConditioning option
    PerPinResiduals : Map<ScanPinId, float> option
    Timestamp       : DateTime
}

module LastSolve =
    let afterRollback (step : RegStep) (m : Map<string, LastSolveEntry>) =
        step.Outputs |> Map.fold (fun acc mesh _ -> Map.remove mesh acc) m

// Camera fly-to (workflow panel §4): pure math, unit-tested. Targets are
// world-space; reducer converts to render space at the boundary.
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

// ───────────── readiness engine (workflow panel §2, shared) ─────────────
// Pure over a dedicated input DTO so Supertests can table-drive it; the
// adaptive adapter is in Primitives.ReadinessView.

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
    | RunFine
    | CommitPending
    | DiscardPending

type Diagnostic = {
    Severity : Severity
    Text     : string
    Action   : NavAction option
}

type ReadinessPin = {
    Id            : ScanPinId
    Label         : string
    // accepted reference anchor + reliability (collinearity input)
    RefAnchor     : (V3d * float) option
    // visible moving meshes with / without an accepted anchor for this pin
    Accepted      : Set<string>
    Unresolved    : int
}

type ReadinessInput = {
    ReferenceMesh       : string option
    VisibleMovingMeshes : string list
    EnabledPins         : ReadinessPin list
    HasPending          : bool
    HasCommittedStep    : bool
    FineModeLabel       : string
}

type StageDiagnostics = {
    Coarse : Diagnostic list
    Fine   : Diagnostic list
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
    let compute (input : ReadinessInput) : StageDiagnostics =
        let coarse = ResizeArray<Diagnostic>()
        let fine = ResizeArray<Diagnostic>()
        let add (l : ResizeArray<_>) severity text action =
            l.Add { Severity = severity; Text = text; Action = action }

        if input.HasPending then
            // blocks both stages, listed first; commit/discard is inline (no nav action).
            for l in [ coarse; fine ] do
                add l Blocker "Commit or discard the pending result first" None

        if input.ReferenceMesh.IsNone then
            add coarse Blocker "Designate a reference mesh (★)" (Some HighlightReferenceColumn)
            add fine Blocker "Designate a reference mesh (★)" (Some HighlightReferenceColumn)

        if List.isEmpty input.EnabledPins then
            add coarse Blocker "Enable correspondence on ≥3 pins" None

        let counts = pairCounts input
        if input.ReferenceMesh.IsSome then
            for mesh, n in counts do
                if n < 3 then
                    add coarse Blocker
                        (sprintf "%s: needs %d more correspondence marker(s)" mesh (3 - n))
                        (Some (ReseedCorrespondence (Some mesh)))

        for pin in input.EnabledPins do
            if pin.Unresolved > 0 then
                add coarse Warning
                    (sprintf "Pin %s: %d mesh(es) without a marker" pin.Label pin.Unresolved)
                    (Some (SelectPinOpenCard pin.Id))

        if List.length input.EnabledPins >= 3 && lambdaRatioOf input < 1e-3 then
            let affected =
                counts |> List.filter (fun (_, n) -> n >= 3) |> List.map fst
            let suffix =
                if List.isEmpty affected then ""
                else sprintf " (%s)" (String.concat ", " affected)
            add coarse Warning
                (sprintf "Pins near-collinear — rotation weakly constrained%s" suffix) None

        if input.ReferenceMesh.IsSome && List.isEmpty input.VisibleMovingMeshes then
            add coarse Info "No visible moving meshes to solve" None

        let coarseBlocked = coarse |> Seq.exists (fun d -> d.Severity = Blocker)
        if not coarseBlocked && counts |> List.exists (fun (_, n) -> n >= 3) then
            add coarse Ready "Ready for correspondence alignment" (Some RunCoarse)

        let fineBlocked = fine |> Seq.exists (fun d -> d.Severity = Blocker)
        if not fineBlocked then
            if not input.HasCommittedStep then
                add fine Info "Run correspondence alignment first (recommended)" None
            elif not (List.isEmpty input.VisibleMovingMeshes) then
                add fine Ready (sprintf "Ready for fine ICP (%s)" input.FineModeLabel) (Some RunFine)

        { Coarse = List.ofSeq coarse; Fine = List.ofSeq fine }

module RegJson =
    let private inv = System.Globalization.CultureInfo.InvariantCulture
    let private f (v : float) = v.ToString("G17", inv)
    let private q (s : string) =
        let sb = StringBuilder(s.Length + 2)
        sb.Append('"') |> ignore
        for c in s do
            match c with
            | '"'  -> sb.Append("\\\"") |> ignore
            | '\\' -> sb.Append("\\\\") |> ignore
            | '\n' -> sb.Append("\\n")  |> ignore
            | '\r' -> sb.Append("\\r")  |> ignore
            | '\t' -> sb.Append("\\t")  |> ignore
            | c when c < ' ' -> sb.Append(sprintf "\\u%04x" (int c)) |> ignore
            | c -> sb.Append(c) |> ignore
        sb.Append('"') |> ignore
        sb.ToString()
    let private v3 (v : V3d) = sprintf "[%s,%s,%s]" (f v.X) (f v.Y) (f v.Z)
    let private trafoJ (t : Trafo3d) =
        let m = t.Forward
        sprintf "[%s]" (String.Join(",",
            [| m.M00; m.M01; m.M02; m.M03
               m.M10; m.M11; m.M12; m.M13
               m.M20; m.M21; m.M22; m.M23
               m.M30; m.M31; m.M32; m.M33 |] |> Array.map f))

    let private rV3 (e : JsonElement) =
        let a = e.EnumerateArray() |> Seq.map (fun x -> x.GetDouble()) |> Array.ofSeq
        V3d(a.[0], a.[1], a.[2])
    let private rTrafo (e : JsonElement) =
        let a = e.EnumerateArray() |> Seq.map (fun x -> x.GetDouble()) |> Array.ofSeq
        let fwd =
            M44d(a.[0],  a.[1],  a.[2],  a.[3],
                 a.[4],  a.[5],  a.[6],  a.[7],
                 a.[8],  a.[9],  a.[10], a.[11],
                 a.[12], a.[13], a.[14], a.[15])
        Trafo3d(fwd, fwd.Inverse)
    let private tryProp (name : string) (e : JsonElement) =
        match e.TryGetProperty(name) with
        | true, v -> Some v
        | _ -> None

    let correspondenceJ (c : Correspondence) =
        let anchors =
            c.Anchors |> Map.toSeq
            |> Seq.map (fun (m, a) ->
                sprintf "%s:{\"p\":%s,\"src\":%s}" (q m) (v3 a.Point) (q (AnchorSource.tag a.Source)))
            |> String.concat ","
        let residuals =
            c.Residuals |> Map.toSeq
            |> Seq.map (fun (m, r) -> sprintf "%s:%s" (q m) (f r))
            |> String.concat ","
        sprintf "{\"enabled\":%b,\"refAnchor\":%s,\"refDist\":%s,\"anchors\":{%s},\"residuals\":{%s}}"
            c.Enabled
            (match c.RefAnchor with Some a -> v3 a | None -> "null")
            (f c.RefDistance) anchors residuals

    let readCorrespondence (e : JsonElement) : Correspondence =
        let anchors =
            match tryProp "anchors" e with
            | Some ae ->
                ae.EnumerateObject()
                |> Seq.map (fun p ->
                    p.Name, {
                        Point    = rV3 (p.Value.GetProperty "p")
                        Source   = AnchorSource.ofTag (p.Value.GetProperty("src").GetString())
                    })
                |> Map.ofSeq
            | None -> Map.empty
        let residuals =
            match tryProp "residuals" e with
            | Some re ->
                re.EnumerateObject() |> Seq.map (fun p -> p.Name, p.Value.GetDouble()) |> Map.ofSeq
            | None -> Map.empty
        {
            Enabled     = (match tryProp "enabled" e with Some v -> v.GetBoolean() | None -> true)
            RefAnchor   =
                (match tryProp "refAnchor" e with
                 | Some v when v.ValueKind <> JsonValueKind.Null -> Some (rV3 v)
                 | _ -> None)
            RefDistance = (match tryProp "refDist" e with Some v -> v.GetDouble() | None -> 0.0)
            Anchors     = anchors
            Residuals   = residuals
        }

    let private stageTag = function StageCoarse -> "coarse" | StageFine -> "fine"
    let private stageOf = function "fine" -> StageFine | _ -> StageCoarse

    let private inputsJ (i : RegInputs) =
        match i with
        | CoarseInputs pins ->
            let pinJ (ScanPinId.ScanPinId g, rel, sources : Map<string, AnchorSource>) =
                let srcs =
                    sources |> Map.toSeq
                    |> Seq.map (fun (m, s) -> sprintf "%s:%s" (q m) (q (AnchorSource.tag s)))
                    |> String.concat ","
                sprintf "{\"id\":%s,\"rel\":%s,\"sources\":{%s}}" (q (g.ToString())) (f rel) srcs
            sprintf "{\"kind\":\"coarse\",\"pins\":[%s]}" (pins |> Array.map pinJ |> String.concat ",")
        | FineInputs(mode, pins) ->
            let ids =
                pins |> Array.map (fun (ScanPinId.ScanPinId g) -> q (g.ToString())) |> String.concat ","
            sprintf "{\"kind\":\"fine\",\"mode\":%s,\"pins\":[%s]}" (q mode) ids

    let private readInputs (e : JsonElement) : RegInputs =
        match e.GetProperty("kind").GetString() with
        | "fine" ->
            let mode = e.GetProperty("mode").GetString()
            let pins =
                e.GetProperty("pins").EnumerateArray()
                |> Seq.map (fun p -> ScanPinId.ScanPinId (Guid.Parse (p.GetString())))
                |> Array.ofSeq
            FineInputs(mode, pins)
        | _ ->
            let pins =
                e.GetProperty("pins").EnumerateArray()
                |> Seq.map (fun p ->
                    let id = ScanPinId.ScanPinId (Guid.Parse (p.GetProperty("id").GetString()))
                    let rel = p.GetProperty("rel").GetDouble()
                    let sources =
                        p.GetProperty("sources").EnumerateObject()
                        |> Seq.map (fun s -> s.Name, AnchorSource.ofTag (s.Value.GetString()))
                        |> Map.ofSeq
                    id, rel, sources)
                |> Array.ofSeq
            CoarseInputs pins

    let regStepJ (s : RegStep) =
        let outputs =
            s.Outputs |> Map.toSeq
            |> Seq.map (fun (m, o) ->
                sprintf "%s:{\"before\":%s,\"after\":%s,\"rmsB\":%s,\"rmsA\":%s,\"algoB\":%s}"
                    (q m) (trafoJ o.TransformBefore) (trafoJ o.TransformAfter)
                    (f o.RmsBefore) (f o.RmsAfter) (f o.AlgoResidBefore))
            |> String.concat ","
        sprintf "{\"step\":%d,\"stage\":%s,\"mode\":%s,\"ts\":%s,\"ref\":%s,\"inputs\":%s,\"outputs\":{%s}}"
            s.Step (q (stageTag s.Stage)) (q s.Mode) (q (s.Timestamp.ToString("O", inv)))
            (q s.ReferenceMesh) (inputsJ s.Inputs) outputs

    let regLogJ (log : RegStep list) =
        sprintf "[%s]" (log |> List.map regStepJ |> String.concat ",")

    let readRegStep (e : JsonElement) : RegStep =
        let outputs =
            e.GetProperty("outputs").EnumerateObject()
            |> Seq.map (fun p ->
                p.Name, {
                    TransformBefore = rTrafo (p.Value.GetProperty "before")
                    TransformAfter  = rTrafo (p.Value.GetProperty "after")
                    RmsBefore       = p.Value.GetProperty("rmsB").GetDouble()
                    RmsAfter        = p.Value.GetProperty("rmsA").GetDouble()
                    AlgoResidBefore = p.Value.GetProperty("algoB").GetDouble()
                })
            |> Map.ofSeq
        {
            Step          = e.GetProperty("step").GetInt32()
            Stage         = stageOf (e.GetProperty("stage").GetString())
            Mode          = e.GetProperty("mode").GetString()
            Timestamp     =
                (match DateTime.TryParse(e.GetProperty("ts").GetString(), inv, Globalization.DateTimeStyles.RoundtripKind) with
                 | true, dt -> dt
                 | _ -> DateTime.UtcNow)
            ReferenceMesh = e.GetProperty("ref").GetString()
            Inputs        = readInputs (e.GetProperty "inputs")
            Outputs       = outputs
        }

    let readRegLog (e : JsonElement) : RegStep list =
        e.EnumerateArray() |> Seq.map readRegStep |> List.ofSeq

    let lastSolveJ (m : Map<string, LastSolveEntry>) =
        let entryJ (e : LastSolveEntry) =
            let cond =
                match e.Conditioning with
                | Some c ->
                    sprintf "{\"eigen\":[%s],\"collinear\":%b}"
                        (c.Eigenvalues |> Array.map f |> String.concat ",") c.CollinearityWarning
                | None -> "null"
            let perPin =
                match e.PerPinResiduals with
                | Some r ->
                    "{" + (r |> Map.toSeq
                             |> Seq.map (fun (ScanPinId.ScanPinId g, v) -> sprintf "%s:%s" (q (g.ToString())) (f v))
                             |> String.concat ",") + "}"
                | None -> "null"
            sprintf "{\"stage\":%s,\"rmsBefore\":%s,\"rmsAfter\":%s,\"cond\":%s,\"perPin\":%s,\"t\":%s}"
                (q (stageTag e.Stage)) (f e.RmsBefore) (f e.RmsAfter) cond perPin
                (q (e.Timestamp.ToString("O", inv)))
        "{" + (m |> Map.toSeq |> Seq.map (fun (mesh, e) -> sprintf "%s:%s" (q mesh) (entryJ e)) |> String.concat ",") + "}"

    let readLastSolve (e : JsonElement) : Map<string, LastSolveEntry> =
        e.EnumerateObject()
        |> Seq.map (fun p ->
            let v = p.Value
            let cond =
                match tryProp "cond" v with
                | Some c when c.ValueKind <> JsonValueKind.Null ->
                    Some {
                        Eigenvalues =
                            c.GetProperty("eigen").EnumerateArray()
                            |> Seq.map (fun x -> x.GetDouble()) |> Array.ofSeq
                        CollinearityWarning = c.GetProperty("collinear").GetBoolean()
                    }
                | _ -> None
            let perPin =
                match tryProp "perPin" v with
                | Some r when r.ValueKind <> JsonValueKind.Null ->
                    Some (r.EnumerateObject()
                          |> Seq.map (fun pr -> ScanPinId.ScanPinId (Guid.Parse pr.Name), pr.Value.GetDouble())
                          |> Map.ofSeq)
                | _ -> None
            p.Name, {
                Stage           = stageOf (v.GetProperty("stage").GetString())
                RmsBefore       = v.GetProperty("rmsBefore").GetDouble()
                RmsAfter        = v.GetProperty("rmsAfter").GetDouble()
                Conditioning    = cond
                PerPinResiduals = perPin
                Timestamp       =
                    match tryProp "t" v with
                    | Some t -> DateTime.Parse(t.GetString(), inv, System.Globalization.DateTimeStyles.RoundtripKind)
                    | None -> DateTime.MinValue
            })
        |> Map.ofSeq

// Heatmap modes; HeatDiff (registration diff) only meaningful while a solve
// preview is pending.
type HeatmapMode =
    | HeatOff
    | HeatProvenance
    | HeatDiff

module HeatmapMode =
    let tag = function HeatOff -> "off" | HeatProvenance -> "prov" | HeatDiff -> "diff"
    let ofTag = function "prov" -> HeatProvenance | "diff" -> HeatDiff | _ -> HeatOff

// One-shot 3D correspondence-marker pick.
type AnchorPickState = {
    PinId : ScanPinId
    Mesh  : string
}

// Patch small-multiples picker. Points are (patch-plane uv, height along the
// shared frame normal, atlas uv); since (refDir, left, normal) is orthonormal,
// world = Centre + u·refDir + v·left + h·normal exactly — world positions are
// reconstructed, never stored. Triangles are flat index triples into Points.
type PatchPickerEntry = {
    Mesh      : string
    Centre    : V3d
    Points    : (V2d * float * V2d)[]
    Triangles : int[]
    Crosshair : V2d
    AtlasUrl  : string
}

type PatchPickerState = {
    PinId   : ScanPinId
    Normal  : V3d
    RefDir  : V3d
    Radius  : float
    Entries : PatchPickerEntry list
    Running : bool
    Shaded  : bool
}

// Transient 2D→3D linking state for the patch picker, view-local (cval — the
// reducer never sees pointer moves). Centre/Zoom = the cell's pan/zoom viewport
// in patch coords (restricts 3D vertex ticks to what the cell shows); Point =
// live cursor position on the triangulated surface (planar uv + height).
type PatchHover = {
    Mesh   : string
    Centre : V2d
    Zoom   : float
    Point  : (V2d * float) option
}
