namespace Superprojekt

open Aardvark.Base

module Stratigraphy =

    let compute (serverUrl : string) (dataset : string) (prism : SelectionPrism) (commonCentroid : V3d) (scale : float) (angularRes : int) : Async<StratigraphyData> =
        async {
            let radius = match prism.Footprint.Vertices with v :: _ -> v.Length | _ -> 1.0
            let anchor = prism.AnchorPoint / scale + commonCentroid
            let axis = prism.AxisDirection |> Vec.normalize
            let worldR = radius / scale
            let minInner = 0.02
            let maxRings = 12
            let ringRadii =
                if worldR <= minInner then [| worldR |]
                else
                    let logOuter = log worldR
                    let logInner = log minInner
                    let desired = (worldR - minInner) / 0.25 |> ceil |> int |> max 1
                    let n = min maxRings desired
                    if n <= 1 then [| worldR |]
                    else
                        Array.init n (fun i ->
                            let t = float i / float (n - 1)
                            exp (logOuter + t * (logInner - logOuter)))
            let! (res, ringCount, perRingAngle) =
                Query.cylinderEval serverUrl dataset anchor axis ringRadii angularRes (prism.ExtentForward / scale) (prism.ExtentBackward / scale)
            let buildRing (perAngle : ResizeArray<float * string>[]) =
                Array.init res (fun i ->
                    let angle = float i / float res * System.Math.PI * 2.0
                    let events = perAngle.[i] |> Seq.toList |> List.map (fun (h, name) -> (h * scale, name)) |> List.sortBy fst
                    { Angle = angle; Events = events })
            let rings = Array.init ringCount (fun k -> buildRing perRingAngle.[k])
            let columns = rings.[0]
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
            let ringRadiiScaled = ringRadii |> Array.map (fun r -> r * scale)
            return {
                AngularResolution = res; AxisMin = axisMin; AxisMax = axisMax
                Columns = columns; ColumnMinZ = columnMinZ; ColumnMaxZ = columnMaxZ
                Rings = rings; RingRadii = ringRadiiScaled
            }
        }

    /// Returns (zLower, zUpper, lowerMesh, upperMesh) for the bracket containing `z`, else None.
    let tryBracket (events : (float * string) list) (z : float) =
        let rec go (lst : (float * string) list) =
            match lst with
            | (z0, n0) :: ((z1, n1) :: _ as rest) ->
                if z0 <= z && z < z1 then Some (z0, z1, n0, n1)
                else go rest
            | _ -> None
        go events

    /// Brackets between consecutive events, collapsing pairs closer than `minGap`. The collapse cuts
    /// per-column bracket count 10-50x on large cylinders so union-find in `buildBandCache` is practical in WASM.
    let private prunedBrackets (minGap : float) (events : (float * string) list) : (float * float)[] =
        let e = List.toArray events
        if e.Length < 2 then [||]
        else
            let kept = ResizeArray<float>()
            kept.Add(fst e.[0])
            for k in 1 .. e.Length - 1 do
                let z = fst e.[k]
                if z - kept.[kept.Count - 1] >= minGap then kept.Add z
            if kept.Count < 2 then [||]
            else Array.init (kept.Count - 1) (fun k -> kept.[k], kept.[k + 1])

    let private minGapFor (data : StratigraphyData) =
        let range = max 0.0 (data.AxisMax - data.AxisMin)
        max 1e-6 (range * 0.005)

    /// BFS over (angleIdx, ringIdx, bracketIdx); ±1 angle wraps, ±1 ring clamps; seeded at the outer-ring hover.
    let floodContinuousBand3D (data : StratigraphyData) (colIdx : int) (hoverZ : float) : Map<int * int, (float * float) list> =
        let n = data.AngularResolution
        let rings = data.Rings
        let ringCount = if isNull (box rings) then 0 else rings.Length
        if n = 0 || ringCount = 0 then Map.empty
        else
        let hIdx = colIdx |> max 0 |> min (n - 1)
        let minGap = minGapFor data
        let bracketsOf = prunedBrackets minGap
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
                for step in [1; -1] do
                    let nAng = (ang + step + n) % n
                    tryConnect ring ang bracket ring nAng
                if ring > 0 then tryConnect ring ang bracket (ring - 1) ang
                if ring < ringCount - 1 then tryConnect ring ang bracket (ring + 1) ang
            perCell |> Seq.map (fun kv -> kv.Key, List.ofSeq kv.Value) |> Map.ofSeq

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
        let minGap = minGapFor data
        let bracketsOf = prunedBrackets minGap
        let brackets =
            Array.init ringCount (fun r ->
                Array.init n (fun a -> bracketsOf rings.[r].[a].Events))
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
        for r in 0 .. ringCount - 1 do
            for a in 0 .. n - 1 do
                for bi in 0 .. brackets.[r].[a].Length - 1 do
                    parent.[(r, a, bi)] <- (r, a, bi)
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
                    let nAng = (a + 1) % n
                    tryConnectTo r nAng
                    if r < ringCount - 1 then tryConnectTo (r + 1) a
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
        let compIds = compBrackets.Keys |> Seq.mapi (fun i k -> k, i) |> dict
        let labels =
            Array.init ringCount (fun r ->
                Array.init n (fun a ->
                    Array.init brackets.[r].[a].Length (fun bi ->
                        compIds.[find (r, a, bi)])))
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

    let private lookupComponentId (cache : BandCache) (colIdx : int) (hoverZ : float) =
        let n = if cache.Brackets.Length > 0 then cache.Brackets.[0].Length else 0
        if n = 0 then None
        else
            let a = colIdx |> max 0 |> min (n - 1)
            let brs = cache.Brackets.[0].[a]
            let mutable found = -1
            for k in 0 .. brs.Length - 1 do
                let (lo, hi) = brs.[k]
                if found < 0 && lo <= hoverZ && hoverZ < hi then found <- k
            if found < 0 then None else Some cache.Labels.[0].[a].[found]

    let lookupBand2D (cache : BandCache) (colIdx : int) (hoverZ : float) : Map<int, (float * float) list> =
        match lookupComponentId cache colIdx hoverZ with
        | Some cid -> cache.Components2D.[cid]
        | None -> Map.empty

    let lookupBand3D (cache : BandCache) (colIdx : int) (hoverZ : float) : Map<int * int, (float * float) list> =
        match lookupComponentId cache colIdx hoverZ with
        | Some cid -> cache.Components3D.[cid]
        | None -> Map.empty

