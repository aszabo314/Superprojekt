namespace Superprojekt

open System
open Aardvark.Base

/// V3 stratigraphy computation. Casts z-aligned rays at a grid of (angle, axisPosition)
/// points on the cylinder surface and records intersection events per dataset.
///
/// STUB(server): the real implementation queries the server for ray-mesh intersections
/// against each loaded mesh. For now `compute` synthesizes plausible-looking data so the
/// renderer can be developed and tested in isolation.
module Stratigraphy =

    /// Synthesize a stratigraphy that visually resembles real data:
    /// - `datasetCount` layered surfaces, monotonically rising in z with sinusoidal undulation
    ///   plus per-dataset phase + amplitude
    /// - some "missing" patches (gaps) so between-spaces have varying width
    /// - one or two folds (multiple intersections per ray) on a couple of datasets
    let private mockColumn (datasets : string[]) (angle : float) (axisMin : float) (axisMax : float) =
        let range = axisMax - axisMin
        let layerSpacing = range / float (datasets.Length + 1)
        let events =
            datasets
            |> Array.mapi (fun i name ->
                let basis = axisMin + layerSpacing * float (i + 1)
                let amp   = layerSpacing * 0.35
                let phase = float i * 0.7
                let z = basis + amp * sin (angle * 2.0 + phase) + 0.15 * amp * cos (angle * 5.0 + float i)
                // Drop a few samples to simulate gaps in coverage.
                let drop = (i + int (angle * 4.0 / Math.PI)) % 17 = 0
                if drop then []
                else
                    // Datasets 1 and 4 produce a fold near angle ≈ π for variety.
                    if (i = 1 || i = 4) && abs (angle - Math.PI) < 0.5 then
                        let z2 = z + amp * 0.4
                        [ (z, name); (z2, name) ]
                    else [ (z, name) ])
            |> List.ofArray
            |> List.concat
            |> List.sortBy fst
        events

    /// STUB(server): mock implementation. Returns synthetic data without contacting the server.
    /// Replace with real ray-mesh queries once the server endpoint exists.
    let compute (prism : SelectionPrism) (datasets : string[]) (angularRes : int) : Async<StratigraphyData> =
        async {
            // Simulate a small delay so the UI can show a "computing" state.
            do! Async.Sleep 50
            let axisMin = -prism.ExtentBackward
            let axisMax =  prism.ExtentForward
            let columns =
                Array.init angularRes (fun i ->
                    let angle = float i / float angularRes * Constant.PiTimesTwo
                    let events = mockColumn datasets angle axisMin axisMax
                    { Angle = angle; Events = events })
            let columnMinZ =
                columns |> Array.map (fun c ->
                    match c.Events with
                    | [] -> axisMin
                    | _  -> c.Events |> List.map fst |> List.min)
            let columnMaxZ =
                columns |> Array.map (fun c ->
                    match c.Events with
                    | [] -> axisMax
                    | _  -> c.Events |> List.map fst |> List.max)
            return {
                AngularResolution = angularRes
                AxisMin = axisMin
                AxisMax = axisMax
                Columns = columns
                ColumnMinZ = columnMinZ
                ColumnMaxZ = columnMaxZ
            }
        }

    /// Phase 3.12: per-mesh world-space offset for the explosion view.
    /// Datasets are sorted ascending by mean intersection z (from `pin.Stratigraphy`),
    /// then displaced along the prism axis by `(centered_rank * factor * baseSpacing)`,
    /// where `baseSpacing = (extentForward + extentBackward) / N`. The displacement is
    /// centered so that the middle dataset stays approximately in place.
    let explosionOffsets (pin : ScanPin) (meshNames : string[]) : Map<string, Aardvark.Base.V3d> =
        if not pin.Explosion.Enabled || pin.Explosion.ExpansionFactor <= 0.0 then Map.empty
        else
            let axis = pin.Prism.AxisDirection |> Aardvark.Base.Vec.normalize
            let ordered =
                match pin.Stratigraphy with
                | Some data when data.Columns.Length > 0 ->
                    let mutable acc = Map.empty<string, float * int>
                    for col in data.Columns do
                        for (z, name) in col.Events do
                            match Map.tryFind name acc with
                            | Some (s, c) -> acc <- Map.add name (s + z, c + 1) acc
                            | None -> acc <- Map.add name (z, 1) acc
                    let ranked =
                        acc |> Map.toArray
                            |> Array.map (fun (k, (s, c)) -> k, s / float c)
                            |> Array.sortBy snd
                            |> Array.map fst
                    let inSet = Set.ofArray ranked
                    let extras = meshNames |> Array.filter (fun d -> not (Set.contains d inSet))
                    Array.append ranked extras
                | _ -> meshNames
            let n = max 1 ordered.Length
            let baseSpacing = (pin.Prism.ExtentForward + pin.Prism.ExtentBackward) / float n
            let factor = pin.Explosion.ExpansionFactor
            ordered
            |> Array.mapi (fun i ds ->
                let centered = float i - float (n - 1) * 0.5
                ds, axis * (centered * factor * baseSpacing))
            |> Map.ofArray
