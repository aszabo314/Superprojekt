namespace Superprojekt

open Aardvark.Base

module Stratigraphy =

    let compute (serverUrl : string) (dataset : string) (prism : SelectionPrism) (commonCentroid : V3d) (scale : float) (angularRes : int) : Async<StratigraphyData> =
        async {
            let radius = match prism.Footprint.Vertices with v :: _ -> v.Length | _ -> 1.0
            let anchor = prism.AnchorPoint / scale + commonCentroid
            let axis = prism.AxisDirection |> Vec.normalize
            let! (res, perAngle) = Query.cylinderEval serverUrl dataset anchor axis (radius / scale) angularRes (prism.ExtentForward / scale) (prism.ExtentBackward / scale)
            let columns =
                Array.init res (fun i ->
                    let angle = float i / float res * System.Math.PI * 2.0
                    let events = perAngle.[i] |> Seq.toList |> List.map (fun (h, name) -> (h * scale, name)) |> List.sortBy fst
                    { Angle = angle; Events = events })
            // Derive axis bounds from actual data so the diagram always fits
            let mutable globalMin = infinity
            let mutable globalMax = -infinity
            for c in columns do
                for (z, _) in c.Events do
                    if z < globalMin then globalMin <- z
                    if z > globalMax then globalMax <- z
            let axisMin, axisMax =
                if System.Double.IsInfinity globalMin then -prism.ExtentBackward, prism.ExtentForward
                else
                    let range = max (globalMax - globalMin) 0.1
                    let pad = range * 0.02
                    let mid = (globalMin + globalMax) * 0.5
                    mid - range * 0.5 - pad, mid + range * 0.5 + pad
            let columnMinZ =
                columns |> Array.map (fun c ->
                    match c.Events with [] -> axisMin | _ -> c.Events |> List.map fst |> List.min)
            let columnMaxZ =
                columns |> Array.map (fun c ->
                    match c.Events with [] -> axisMax | _ -> c.Events |> List.map fst |> List.max)
            return { AngularResolution = res; AxisMin = axisMin; AxisMax = axisMax; Columns = columns; ColumnMinZ = columnMinZ; ColumnMaxZ = columnMaxZ }
        }

    /// Find the between-space bracket containing `z` in a column's sorted events.
    /// Returns (zLower, zUpper, lowerMesh, upperMesh) or None if z is below the
    /// first event or above the last.
    let tryBracket (events : (float * string) list) (z : float) =
        let rec go (lst : (float * string) list) =
            match lst with
            | (z0, n0) :: ((z1, n1) :: _ as rest) ->
                if z0 <= z && z < z1 then Some (z0, z1, n0, n1)
                else go rest
            | _ -> None
        go events

    /// Phase 3.12: per-mesh world-space offset for the explosion view.
    let explosionOffsetsFromFields (explosion : ExplosionState) (prism : SelectionPrism) (stratigraphy : StratigraphyData option) (meshNames : string[]) : Map<string, V3d> =
        if not explosion.Enabled || explosion.ExpansionFactor <= 0.0 then Map.empty
        else
            let axis = prism.AxisDirection |> Vec.normalize
            let ordered =
                match stratigraphy with
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
            let baseSpacing = (prism.ExtentForward + prism.ExtentBackward) / float n
            let factor = explosion.ExpansionFactor
            ordered
            |> Array.mapi (fun i ds ->
                let centered = float i - float (n - 1) * 0.5
                ds, axis * (centered * factor * baseSpacing))
            |> Map.ofArray

    let explosionOffsets (pin : ScanPin) (meshNames : string[]) : Map<string, V3d> =
        explosionOffsetsFromFields pin.Explosion pin.Prism pin.Stratigraphy meshNames
