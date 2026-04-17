namespace Superprojekt

open Aardvark.Base

module Stratigraphy =

    let compute (serverUrl : string) (dataset : string) (prism : SelectionPrism) (commonCentroid : V3d) (scale : float) (angularRes : int) : Async<StratigraphyData> =
        async {
            let radius = match prism.Footprint.Vertices with v :: _ -> v.Length | _ -> 1.0
            let anchor = prism.AnchorPoint / scale + commonCentroid
            let axis = prism.AxisDirection |> Vec.normalize
            let worldR = radius / scale
            let step = 0.25
            let minInner = 0.02
            let ringRadii =
                let rs = ResizeArray<float>()
                let mutable r = worldR
                while r >= minInner do
                    rs.Add r
                    r <- r - step
                if rs.Count = 0 then rs.Add (max worldR minInner)
                rs.ToArray()
            let! (res, ringCount, perRingAngle) =
                Query.cylinderEval serverUrl dataset anchor axis ringRadii angularRes (prism.ExtentForward / scale) (prism.ExtentBackward / scale)
            let buildRing (perAngle : ResizeArray<float * string>[]) =
                Array.init res (fun i ->
                    let angle = float i / float res * System.Math.PI * 2.0
                    let events = perAngle.[i] |> Seq.toList |> List.map (fun (h, name) -> (h * scale, name)) |> List.sortBy fst
                    { Angle = angle; Events = events })
            let rings = Array.init ringCount (fun k -> buildRing perRingAngle.[k])
            let columns = rings.[0]
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
            // RingRadii reported in client (scaled) space to match Columns' z units
            let ringRadiiScaled = ringRadii |> Array.map (fun r -> r * scale)
            return {
                AngularResolution = res; AxisMin = axisMin; AxisMax = axisMax
                Columns = columns; ColumnMinZ = columnMinZ; ColumnMaxZ = columnMaxZ
                Rings = rings; RingRadii = ringRadiiScaled
            }
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
    /// 3D flood fill over (angleIdx, ringIdx, bracketIdx) with ±1 angle (wrap) and
    /// ±1 ring (clamp) neighbors. Seed is the hovered bracket on the outer ring.
    let floodContinuousBand3D (data : StratigraphyData) (colIdx : int) (hoverZ : float) : Map<int * int, (float * float) list> =
        let n = data.AngularResolution
        let rings = data.Rings
        let ringCount = if isNull (box rings) then 0 else rings.Length
        if n = 0 || ringCount = 0 then Map.empty
        else
        let hIdx = colIdx |> max 0 |> min (n - 1)
        let bracketsOf (events : (float * string) list) =
            let e = List.toArray events
            if e.Length < 2 then [||]
            else Array.init (e.Length - 1) (fun k -> fst e.[k], fst e.[k + 1])
        // brackets.[ring].[angle] = (lo, hi)[]
        let brackets =
            Array.init ringCount (fun r ->
                Array.init n (fun a -> bracketsOf rings.[r].[a].Events))
        let seedRing = 0
        let hb = brackets.[seedRing].[hIdx]
        let mutable hbIdx = -1
        for k in 0 .. hb.Length - 1 do
            let (a, b) = hb.[k]
            if hbIdx < 0 && a <= hoverZ && hoverZ < b then hbIdx <- k
        if hbIdx < 0 then Map.empty
        else
            let visited = System.Collections.Generic.HashSet<int * int * int>()
            let queue   = System.Collections.Generic.Queue<int * int * int>()
            let perCell = System.Collections.Generic.Dictionary<int * int, System.Collections.Generic.List<float * float>>()
            let enqueue ring ang bi =
                if visited.Add((ring, ang, bi)) then
                    queue.Enqueue((ring, ang, bi))
                    let key = (ang, ring)
                    match perCell.TryGetValue key with
                    | true, lst -> lst.Add brackets.[ring].[ang].[bi]
                    | _ ->
                        let lst = System.Collections.Generic.List<float * float>()
                        lst.Add brackets.[ring].[ang].[bi]
                        perCell.[key] <- lst
            enqueue seedRing hIdx hbIdx
            let tryConnect ring ang (lo, hi) nRing nAng =
                let nbrs = brackets.[nRing].[nAng]
                let len1 = hi - lo
                for nbi in 0 .. nbrs.Length - 1 do
                    let (a, b) = nbrs.[nbi]
                    let ov = (min b hi) - (max a lo)
                    let len2 = b - a
                    let longer = max len1 len2
                    if longer > 0.0 && ov > 0.5 * longer then enqueue nRing nAng nbi
            while queue.Count > 0 do
                let (ring, ang, bi) = queue.Dequeue()
                let bracket = brackets.[ring].[ang].[bi]
                // angular neighbors (wrap)
                for step in [1; -1] do
                    let nAng = (ang + step + n) % n
                    tryConnect ring ang bracket ring nAng
                // radial neighbors (clamp)
                if ring > 0 then tryConnect ring ang bracket (ring - 1) ang
                if ring < ringCount - 1 then tryConnect ring ang bracket (ring + 1) ang
            perCell |> Seq.map (fun kv -> kv.Key, List.ofSeq kv.Value) |> Map.ofSeq

    /// 2D view: restrict the 3D flood to the outer ring (seed ring).
    let floodContinuousBand (data : StratigraphyData) (colIdx : int) (hoverZ : float) : Map<int, (float * float) list> =
        floodContinuousBand3D data colIdx hoverZ
        |> Seq.choose (fun kv ->
            let (ang, ring) = kv.Key
            if ring = 0 then Some (ang, kv.Value) else None)
        |> Map.ofSeq

    let buildBandCache (data : StratigraphyData) : BandCache =
        let n = data.AngularResolution
        let rings = data.Rings
        let ringCount = if isNull (box rings) then 0 else rings.Length
        let bracketsOf (events : (float * string) list) =
            let e = List.toArray events
            if e.Length < 2 then [||]
            else Array.init (e.Length - 1) (fun k -> fst e.[k], fst e.[k + 1])
        let brackets =
            Array.init ringCount (fun r ->
                Array.init n (fun a -> bracketsOf rings.[r].[a].Events))
        // Union-find
        let parent = System.Collections.Generic.Dictionary<int * int * int, int * int * int>()
        let rec find x =
            match parent.TryGetValue x with
            | true, p when p <> x ->
                let r = find p
                parent.[x] <- r
                r
            | _ -> x
        let union a b =
            let ra = find a
            let rb = find b
            if ra <> rb then parent.[ra] <- rb
        // Initialize all bracket nodes
        for r in 0 .. ringCount - 1 do
            for a in 0 .. n - 1 do
                for bi in 0 .. brackets.[r].[a].Length - 1 do
                    parent.[(r, a, bi)] <- (r, a, bi)
        // Connect neighbors using same overlap criterion as floodContinuousBand3D
        for r in 0 .. ringCount - 1 do
            for a in 0 .. n - 1 do
                for bi in 0 .. brackets.[r].[a].Length - 1 do
                    let (lo, hi) = brackets.[r].[a].[bi]
                    let len1 = hi - lo
                    let tryConnectTo nR nA =
                        let nbrs = brackets.[nR].[nA]
                        for nbi in 0 .. nbrs.Length - 1 do
                            let (a2, b2) = nbrs.[nbi]
                            let ov = (min b2 hi) - (max a2 lo)
                            let len2 = b2 - a2
                            let longer = max len1 len2
                            if longer > 0.0 && ov > 0.5 * longer then
                                union (r, a, bi) (nR, nA, nbi)
                    // angular neighbors (wrap)
                    let nAng = (a + 1) % n
                    tryConnectTo r nAng
                    // radial neighbors
                    if r < ringCount - 1 then tryConnectTo (r + 1) a
        // Group brackets by component
        let compBrackets = System.Collections.Generic.Dictionary<int * int * int, System.Collections.Generic.List<int * int * int>>()
        for r in 0 .. ringCount - 1 do
            for a in 0 .. n - 1 do
                for bi in 0 .. brackets.[r].[a].Length - 1 do
                    let root = find (r, a, bi)
                    match compBrackets.TryGetValue root with
                    | true, lst -> lst.Add((r, a, bi))
                    | _ ->
                        let lst = System.Collections.Generic.List<int * int * int>()
                        lst.Add((r, a, bi))
                        compBrackets.[root] <- lst
        // Assign sequential component IDs
        let compIds = compBrackets.Keys |> Seq.mapi (fun i k -> k, i) |> dict
        let labels =
            Array.init ringCount (fun r ->
                Array.init n (fun a ->
                    Array.init brackets.[r].[a].Length (fun bi ->
                        compIds.[find (r, a, bi)])))
        // Build result maps per component
        let numComponents = compIds.Count
        let comp3D = Array.init numComponents (fun _ -> System.Collections.Generic.Dictionary<int * int, System.Collections.Generic.List<float * float>>())
        let comp2D = Array.init numComponents (fun _ -> System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<float * float>>())
        for kv in compBrackets do
            let cid = compIds.[kv.Key]
            for (r, a, bi) in kv.Value do
                let br = brackets.[r].[a].[bi]
                let key3 = (a, r)
                match comp3D.[cid].TryGetValue key3 with
                | true, lst -> lst.Add br
                | _ ->
                    let lst = System.Collections.Generic.List<float * float>()
                    lst.Add br
                    comp3D.[cid].[key3] <- lst
                if r = 0 then
                    match comp2D.[cid].TryGetValue a with
                    | true, lst -> lst.Add br
                    | _ ->
                        let lst = System.Collections.Generic.List<float * float>()
                        lst.Add br
                        comp2D.[cid].[a] <- lst
        {
            Brackets = brackets
            Labels = labels
            Components3D = comp3D |> Array.map (fun d -> d |> Seq.map (fun kv -> kv.Key, List.ofSeq kv.Value) |> Map.ofSeq)
            Components2D = comp2D |> Array.map (fun d -> d |> Seq.map (fun kv -> kv.Key, List.ofSeq kv.Value) |> Map.ofSeq)
        }

    let lookupBand2D (cache : BandCache) (colIdx : int) (hoverZ : float) : Map<int, (float * float) list> =
        let n = if cache.Brackets.Length > 0 then cache.Brackets.[0].Length else 0
        if n = 0 then Map.empty
        else
        let a = colIdx |> max 0 |> min (n - 1)
        let brs = cache.Brackets.[0].[a]
        let mutable found = -1
        for k in 0 .. brs.Length - 1 do
            let (lo, hi) = brs.[k]
            if found < 0 && lo <= hoverZ && hoverZ < hi then found <- k
        if found < 0 then Map.empty
        else cache.Components2D.[cache.Labels.[0].[a].[found]]

    let lookupBand3D (cache : BandCache) (colIdx : int) (hoverZ : float) : Map<int * int, (float * float) list> =
        let n = if cache.Brackets.Length > 0 then cache.Brackets.[0].Length else 0
        if n = 0 then Map.empty
        else
        let a = colIdx |> max 0 |> min (n - 1)
        let brs = cache.Brackets.[0].[a]
        let mutable found = -1
        for k in 0 .. brs.Length - 1 do
            let (lo, hi) = brs.[k]
            if found < 0 && lo <= hoverZ && hoverZ < hi then found <- k
        if found < 0 then Map.empty
        else cache.Components3D.[cache.Labels.[0].[a].[found]]

