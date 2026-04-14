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

    /// Flood-fill the continuous between-space containing the hovered bracket.
    /// Each column is broken into brackets (pairs of consecutive events); BFS
    /// over the graph whose nodes are (columnIdx, bracketIdx) and whose edges
    /// connect nodes in adjacent columns whose z-ranges overlap. Bounding mesh
    /// identities are ignored — the gap is defined purely by geometric
    /// continuity of the open z-intervals column-to-column. Returns every
    /// bracket in the connected region, grouped by column.
    let floodContinuousBand (data : StratigraphyData) (colIdx : int) (hoverZ : float) : Map<int, (float * float) list> =
        let n = data.Columns.Length
        if n = 0 then Map.empty
        else
        let hIdx = colIdx |> max 0 |> min (n - 1)
        let bracketsOf (events : (float * string) list) =
            let e = List.toArray events
            if e.Length < 2 then [||]
            else Array.init (e.Length - 1) (fun k -> fst e.[k], fst e.[k + 1])
        let columnBrackets = Array.init n (fun i -> bracketsOf data.Columns.[i].Events)
        let hb = columnBrackets.[hIdx]
        let mutable hbIdx = -1
        for k in 0 .. hb.Length - 1 do
            let (a, b) = hb.[k]
            if hbIdx < 0 && a <= hoverZ && hoverZ < b then hbIdx <- k
        if hbIdx < 0 then Map.empty
        else
            let visited = System.Collections.Generic.HashSet<int * int>()
            let queue   = System.Collections.Generic.Queue<int * int>()
            let perColumn = System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<float * float>>()
            let enqueue col bi =
                if visited.Add((col, bi)) then
                    queue.Enqueue((col, bi))
                    match perColumn.TryGetValue col with
                    | true, lst -> lst.Add columnBrackets.[col].[bi]
                    | _ ->
                        let lst = System.Collections.Generic.List<float * float>()
                        lst.Add columnBrackets.[col].[bi]
                        perColumn.[col] <- lst
            enqueue hIdx hbIdx
            while queue.Count > 0 do
                let (col, bi) = queue.Dequeue()
                let (lo, hi) = columnBrackets.[col].[bi]
                for step in [1; -1] do
                    let ncol = (col + step + n) % n
                    let nbrs = columnBrackets.[ncol]
                    let len1 = hi - lo
                    for nbi in 0 .. nbrs.Length - 1 do
                        let (a, b) = nbrs.[nbi]
                        let ov = (min b hi) - (max a lo)
                        let len2 = b - a
                        let longer = max len1 len2
                        if longer > 0.0 && ov > 0.5 * longer then enqueue ncol nbi
            perColumn |> Seq.map (fun kv -> kv.Key, List.ofSeq kv.Value) |> Map.ofSeq

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
