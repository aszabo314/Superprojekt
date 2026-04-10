namespace Superprojekt

open Aardvark.Base

/// Pure geometry helpers for ScanPin rendering. Independent of the WASM runtime
/// so this file can be linked into PinDemo (desktop) as well.
module PinGeometry =

    /// Trafo that rotates a prism's axis to +Z and translates the anchor to the origin.
    /// Used by the core sample inspector.
    let coreSampleTrafo (prism : SelectionPrism) =
        let axis = -(prism.AxisDirection |> Vec.normalize)
        let up = if abs axis.Z > 0.9 then V3d.OIO else V3d.OOI
        let right = Vec.cross axis up |> Vec.normalize
        let fwd = Vec.cross right axis |> Vec.normalize
        let rotFwd = M44d(right.X, right.Y, right.Z, 0.0,
                          fwd.X,   fwd.Y,   fwd.Z,   0.0,
                          axis.X,  axis.Y,  axis.Z,  0.0,
                          0.0,     0.0,     0.0,     1.0)
        Trafo3d.Translation(-prism.AnchorPoint) * Trafo3d(rotFwd, rotFwd.Transposed)

    /// Triangle-quads forming the wireframe edges of a selection prism.
    let buildPrismWireframe (prism : SelectionPrism) (thickness : float) =
        let axis = prism.AxisDirection |> Vec.normalize
        let up = if abs axis.Z > 0.9 then V3d.OIO else V3d.OOI
        let right = Vec.cross axis up |> Vec.normalize
        let fwd = Vec.cross right axis |> Vec.normalize
        let n = prism.Footprint.Vertices.Length
        if n < 3 then [||], [||]
        else
            let to3d (v : V2d) offset = prism.AnchorPoint + right * v.X + fwd * v.Y + axis * offset
            let topVerts  = prism.Footprint.Vertices |> List.map (fun v -> to3d v prism.ExtentForward)  |> Array.ofList
            let botVerts  = prism.Footprint.Vertices |> List.map (fun v -> to3d v (-prism.ExtentBackward)) |> Array.ofList
            let positions = System.Collections.Generic.List<V3f>()
            let indices   = System.Collections.Generic.List<int>()
            let addEdge (a : V3d) (b : V3d) =
                let dir = b - a
                let perp =
                    let c = Vec.cross dir axis
                    if c.Length < 1e-10 then Vec.cross dir right |> Vec.normalize else c |> Vec.normalize
                let off = perp * thickness * 0.5
                let i0 = positions.Count
                positions.Add(V3f(a + off))
                positions.Add(V3f(a - off))
                positions.Add(V3f(b + off))
                positions.Add(V3f(b - off))
                indices.Add(i0); indices.Add(i0+1); indices.Add(i0+2)
                indices.Add(i0+1); indices.Add(i0+3); indices.Add(i0+2)
            for i in 0 .. n - 1 do
                let j = (i + 1) % n
                addEdge topVerts.[i] topVerts.[j]
                addEdge botVerts.[i] botVerts.[j]
            for i in 0 .. n - 1 do
                addEdge topVerts.[i] botVerts.[i]
            positions.ToArray(), indices.ToArray()

    /// Build a thin square-section tube (8 verts, 8 quads = 16 tris) from a to b.
    let buildLineTube (a : V3d) (b : V3d) (radius : float) =
        let dir = b - a
        let len = dir.Length
        if len < 1e-10 then [||], [||]
        else
            let axis = dir / len
            let up = if abs axis.Z > 0.9 then V3d.OIO else V3d.OOI
            let r = Vec.cross axis up |> Vec.normalize
            let f = Vec.cross r axis |> Vec.normalize
            let positions =
                [| V3f(a + r * radius + f * radius); V3f(a - r * radius + f * radius)
                   V3f(a - r * radius - f * radius); V3f(a + r * radius - f * radius)
                   V3f(b + r * radius + f * radius); V3f(b - r * radius + f * radius)
                   V3f(b - r * radius - f * radius); V3f(b + r * radius - f * radius) |]
            let indices =
                [| 0;1;5; 0;5;4
                   1;2;6; 1;6;5
                   2;3;7; 2;7;6
                   3;0;4; 3;4;7
                   0;3;2; 0;2;1
                   4;5;6; 4;6;7 |]
            positions, indices

    /// Append a colored polyline as triangle-quad ribbons into the supplied buffers.
    /// `axisRef` is used to choose a stable perpendicular direction; pass the prism axis
    /// for cylinder-wall curves and the cut-plane normal for in-plane lines.
    let appendPolylineRibbon
            (positions : ResizeArray<V3f>) (colors : ResizeArray<V4f>) (indices : ResizeArray<int>)
            (pts : V3d[]) (color : V4f) (axisRef : V3d) (thickness : float) =
        for i in 0 .. pts.Length - 2 do
            let a = pts.[i]
            let b = pts.[i + 1]
            let dir = b - a
            if dir.Length > 1e-10 then
                let perp =
                    let c = Vec.cross dir axisRef
                    if c.Length < 1e-10 then
                        let alt = if abs axisRef.X > 0.9 then V3d.OIO else V3d.IOO
                        Vec.cross dir alt |> Vec.normalize
                    else c |> Vec.normalize
                let off = perp * (thickness * 0.5)
                let i0 = positions.Count
                positions.Add(V3f(a + off)); colors.Add(color)
                positions.Add(V3f(a - off)); colors.Add(color)
                positions.Add(V3f(b + off)); colors.Add(color)
                positions.Add(V3f(b - off)); colors.Add(color)
                indices.Add(i0); indices.Add(i0 + 1); indices.Add(i0 + 2)
                indices.Add(i0 + 1); indices.Add(i0 + 3); indices.Add(i0 + 2)

    /// Build a flat disc (triangle fan) centered at `center`, perpendicular to `axis`.
    /// Returns positions and indices.
    let buildDisc (center : V3d) (axis : V3d) (radius : float) (segments : int) =
        let dir = axis |> Vec.normalize
        let up = if abs dir.Z > 0.9 then V3d.OIO else V3d.OOI
        let r = Vec.cross dir up |> Vec.normalize
        let f = Vec.cross r dir |> Vec.normalize
        let positions = Array.zeroCreate<V3f> (segments + 1)
        positions.[0] <- V3f center
        for i in 0 .. segments - 1 do
            let a = float i / float segments * Constant.PiTimesTwo
            positions.[i + 1] <- V3f(center + r * (cos a * radius) + f * (sin a * radius))
        let indices = Array.zeroCreate<int> (segments * 3)
        for i in 0 .. segments - 1 do
            let a = i + 1
            let b = if i = segments - 1 then 1 else i + 2
            indices.[i * 3] <- 0
            indices.[i * 3 + 1] <- a
            indices.[i * 3 + 2] <- b
        positions, indices

    /// Build a small oriented box (handle) centered at `center`, axis-aligned to `axis`.
    let buildHandleBox (center : V3d) (axis : V3d) (size : float) =
        let dir = axis |> Vec.normalize
        let up = if abs dir.Z > 0.9 then V3d.OIO else V3d.OOI
        let r = Vec.cross dir up |> Vec.normalize
        let f = Vec.cross r dir |> Vec.normalize
        let mk dx dy dz = V3f(center + r * (dx * size) + f * (dy * size) + dir * (dz * size))
        let positions =
            [| mk -1.0 -1.0 -1.0; mk 1.0 -1.0 -1.0; mk 1.0 1.0 -1.0; mk -1.0 1.0 -1.0
               mk -1.0 -1.0 1.0;  mk 1.0 -1.0 1.0;  mk 1.0 1.0 1.0;  mk -1.0 1.0 1.0 |]
        let indices =
            [| 0;1;2; 0;2;3;  5;4;7; 5;7;6
               4;0;3; 4;3;7;  1;5;6; 1;6;2
               0;4;5; 0;5;1;  3;2;6; 3;6;7 |]
        positions, indices

    /// Build a triangle mesh from a regular height grid. NaN cells are skipped.
    /// Returns positions, per-vertex normals (upward-biased when ambiguous), and indices.
    let buildHeightfieldMesh (gridOrigin : V2d) (cellSize : float) (resolution : int) (heights : float[]) =
        let n = resolution
        let positions = System.Collections.Generic.List<V3f>()
        let indices   = System.Collections.Generic.List<int>()
        let vmap = Array.create (n * n) -1
        for j in 0 .. n - 1 do
            for i in 0 .. n - 1 do
                let h = heights.[j * n + i]
                if not (System.Double.IsNaN h) then
                    let x = gridOrigin.X + float i * cellSize
                    let y = gridOrigin.Y + float j * cellSize
                    vmap.[j * n + i] <- positions.Count
                    positions.Add(V3f(float32 x, float32 y, float32 h))
        let inline tryGet i j =
            if i < 0 || i >= n || j < 0 || j >= n then -1
            else vmap.[j * n + i]
        for j in 0 .. n - 2 do
            for i in 0 .. n - 2 do
                let a = tryGet i j
                let b = tryGet (i + 1) j
                let c = tryGet (i + 1) (j + 1)
                let d = tryGet i (j + 1)
                if a >= 0 && b >= 0 && c >= 0 then
                    indices.Add a; indices.Add b; indices.Add c
                if a >= 0 && c >= 0 && d >= 0 then
                    indices.Add a; indices.Add c; indices.Add d
        let posArr = positions.ToArray()
        let idxArr = indices.ToArray()
        let acc = Array.create posArr.Length V3d.Zero
        let triCount = idxArr.Length / 3
        for t in 0 .. triCount - 1 do
            let i0 = idxArr.[t * 3]
            let i1 = idxArr.[t * 3 + 1]
            let i2 = idxArr.[t * 3 + 2]
            let p0 = V3d posArr.[i0]
            let p1 = V3d posArr.[i1]
            let p2 = V3d posArr.[i2]
            let nrm = Vec.cross (p1 - p0) (p2 - p0)
            let nrm = if nrm.Z < 0.0 then -nrm else nrm
            acc.[i0] <- acc.[i0] + nrm
            acc.[i1] <- acc.[i1] + nrm
            acc.[i2] <- acc.[i2] + nrm
        let normals =
            acc |> Array.map (fun n ->
                let l = n.Length
                if l < 1e-20 then V3f.OOI
                else V3f (n / l))
        posArr, normals, idxArr

    /// Quad describing the cut plane (along/across the prism axis).
    let buildCutPlaneQuad (prism : SelectionPrism) (cutPlane : CutPlaneMode) =
        let axis = prism.AxisDirection |> Vec.normalize
        let up = if abs axis.Z > 0.9 then V3d.OIO else V3d.OOI
        let right = Vec.cross axis up |> Vec.normalize
        let fwd = Vec.cross right axis |> Vec.normalize
        let r = match prism.Footprint.Vertices with v :: _ -> v.Length | _ -> 1.0
        match cutPlane with
        | CutPlaneMode.AlongAxis angleDeg ->
            let a = angleDeg * Constant.RadiansPerDegree
            let planeDir = right * cos a + fwd * sin a
            let hw = r * 1.2
            let hh = (prism.ExtentForward + prism.ExtentBackward) * 0.5
            let center = prism.AnchorPoint + axis * (prism.ExtentForward - prism.ExtentBackward) * 0.5
            [| V3f(center - planeDir * hw - axis * hh)
               V3f(center + planeDir * hw - axis * hh)
               V3f(center + planeDir * hw + axis * hh)
               V3f(center - planeDir * hw + axis * hh) |],
            [| 0;1;2; 0;2;3 |]
        | CutPlaneMode.AcrossAxis dist ->
            let center = prism.AnchorPoint + axis * dist
            let hw = r * 1.2
            [| V3f(center - right * hw - fwd * hw)
               V3f(center + right * hw - fwd * hw)
               V3f(center + right * hw + fwd * hw)
               V3f(center - right * hw + fwd * hw) |],
            [| 0;1;2; 0;2;3 |]

    /// Cut plane outline edges (4 thin tubes) + gradient fill (9-vertex quad with vertex colors)
    /// + measurement ticks. Returns (positions, colors, indices).
    let buildCutPlaneRefined (prism : SelectionPrism) (cutPlane : CutPlaneMode) =
        let axis = prism.AxisDirection |> Vec.normalize
        let up = if abs axis.Z > 0.9 then V3d.OIO else V3d.OOI
        let right = Vec.cross axis up |> Vec.normalize
        let fwd = Vec.cross right axis |> Vec.normalize
        let r = match prism.Footprint.Vertices with v :: _ -> v.Length | _ -> 1.0
        let corners, edgeU, edgeV, extentU, extentV =
            match cutPlane with
            | CutPlaneMode.AlongAxis angleDeg ->
                let a = angleDeg * Constant.RadiansPerDegree
                let planeDir = right * cos a + fwd * sin a
                let hw = r * 1.2
                let hh = (prism.ExtentForward + prism.ExtentBackward) * 0.5
                let center = prism.AnchorPoint + axis * (prism.ExtentForward - prism.ExtentBackward) * 0.5
                let c0 = center - planeDir * hw - axis * hh
                let c1 = center + planeDir * hw - axis * hh
                let c2 = center + planeDir * hw + axis * hh
                let c3 = center - planeDir * hw + axis * hh
                [| c0; c1; c2; c3 |], planeDir, axis, hw * 2.0, hh * 2.0
            | CutPlaneMode.AcrossAxis dist ->
                let center = prism.AnchorPoint + axis * dist
                let hw = r * 1.2
                let c0 = center - right * hw - fwd * hw
                let c1 = center + right * hw - fwd * hw
                let c2 = center + right * hw + fwd * hw
                let c3 = center - right * hw + fwd * hw
                [| c0; c1; c2; c3 |], right, fwd, hw * 2.0, hw * 2.0

        let positions = ResizeArray<V3f>()
        let colors = ResizeArray<V4f>()
        let indices = ResizeArray<int>()

        // Gradient fill: 3x3 grid of vertices, edges at 8% opacity, center at 0%
        let edgeAlpha = 0.08f
        let mid01 = (corners.[0] + corners.[1]) * 0.5
        let mid12 = (corners.[1] + corners.[2]) * 0.5
        let mid23 = (corners.[2] + corners.[3]) * 0.5
        let mid30 = (corners.[3] + corners.[0]) * 0.5
        let center = (corners.[0] + corners.[1] + corners.[2] + corners.[3]) * 0.25
        let gradVerts = [| corners.[0]; mid01; corners.[1]; mid30; center; mid12; corners.[3]; mid23; corners.[2] |]
        let gradAlphas = [| edgeAlpha; edgeAlpha; edgeAlpha; edgeAlpha; 0.0f; edgeAlpha; edgeAlpha; edgeAlpha; edgeAlpha |]
        let baseIdx = positions.Count
        for i in 0 .. 8 do
            positions.Add(V3f gradVerts.[i])
            colors.Add(V4f(1.0f, 1.0f, 1.0f, gradAlphas.[i]))
        let addQuad a b c d =
            indices.Add(baseIdx + a); indices.Add(baseIdx + b); indices.Add(baseIdx + c)
            indices.Add(baseIdx + a); indices.Add(baseIdx + c); indices.Add(baseIdx + d)
        addQuad 0 1 4 3
        addQuad 1 2 5 4
        addQuad 3 4 7 6
        addQuad 4 5 8 7

        // Outline edges: 4 thin tubes
        let lineThick = max 0.02 (r * 0.025)
        let addEdgeTube (a : V3d) (b : V3d) =
            let dir = (b - a) |> Vec.normalize
            let perp1 = Vec.cross dir axis
            let perp = if perp1.Length < 1e-10 then Vec.cross dir right |> Vec.normalize else perp1 |> Vec.normalize
            let off = perp * lineThick * 0.5
            let i0 = positions.Count
            positions.Add(V3f(a + off)); positions.Add(V3f(a - off))
            positions.Add(V3f(b + off)); positions.Add(V3f(b - off))
            let edgeColor = V4f(1.0f, 1.0f, 1.0f, 0.6f)
            for _ in 1..4 do colors.Add(edgeColor)
            indices.Add(i0); indices.Add(i0+1); indices.Add(i0+2)
            indices.Add(i0+1); indices.Add(i0+3); indices.Add(i0+2)
        addEdgeTube corners.[0] corners.[1]
        addEdgeTube corners.[1] corners.[2]
        addEdgeTube corners.[2] corners.[3]
        addEdgeTube corners.[3] corners.[0]

        // Measurement ticks along two opposite edges
        let tickScale =
            let ext = max extentU extentV
            if ext > 10.0 then 2.0
            elif ext > 5.0 then 1.0
            elif ext > 2.0 then 0.5
            elif ext > 0.5 then 0.1
            else 0.05
        let tickLen = min extentU extentV * 0.05
        let tickColor = V4f(1.0f, 1.0f, 1.0f, 0.4f)
        let addTick (origin : V3d) (perpDir : V3d) (isMajor : bool) =
            let len = if isMajor then tickLen * 1.8 else tickLen
            let i0 = positions.Count
            let thick = lineThick * 0.5
            let a = origin
            let b = origin + perpDir * len
            let side = Vec.cross (b - a) axis
            let side = if side.Length < 1e-10 then Vec.cross (b - a) right else side
            let side = side |> Vec.normalize |> (*) (thick * 0.5)
            positions.Add(V3f(a + side)); positions.Add(V3f(a - side))
            positions.Add(V3f(b + side)); positions.Add(V3f(b - side))
            for _ in 1..4 do colors.Add(tickColor)
            indices.Add(i0); indices.Add(i0+1); indices.Add(i0+2)
            indices.Add(i0+1); indices.Add(i0+3); indices.Add(i0+2)
            ()

        // Ticks along edge 0→1 (inward = toward edge 3→2)
        let perpIn1 = edgeV |> Vec.normalize
        let nTicks1 = int (extentU / tickScale)
        for i in 0 .. nTicks1 do
            let t = float i * tickScale / extentU
            if t <= 1.0 then
                let pt = corners.[0] + (corners.[1] - corners.[0]) * t
                addTick pt perpIn1 (i % 5 = 0)
        // Ticks along edge 3→2 (inward = toward edge 0→1)
        let perpIn2 = -perpIn1
        for i in 0 .. nTicks1 do
            let t = float i * tickScale / extentU
            if t <= 1.0 then
                let pt = corners.[3] + (corners.[2] - corners.[3]) * t
                addTick pt perpIn2 (i % 5 = 0)

        positions.ToArray(), colors.ToArray(), indices.ToArray()

    /// Returns label data for major ticks on the cut plane: (position, text, perpDir, edgeDir) list.
    /// perpDir points inward from the edge (text offset direction), edgeDir runs along the edge (text orientation).
    let cutPlaneTickLabels (prism : SelectionPrism) (cutPlane : CutPlaneMode) =
        let axis = prism.AxisDirection |> Vec.normalize
        let up = if abs axis.Z > 0.9 then V3d.OIO else V3d.OOI
        let right = Vec.cross axis up |> Vec.normalize
        let fwd = Vec.cross right axis |> Vec.normalize
        let r = match prism.Footprint.Vertices with v :: _ -> v.Length | _ -> 1.0
        let corners, edgeU, edgeV, extentU, extentV =
            match cutPlane with
            | CutPlaneMode.AlongAxis angleDeg ->
                let a = angleDeg * Constant.RadiansPerDegree
                let planeDir = right * cos a + fwd * sin a
                let hw = r * 1.2
                let hh = (prism.ExtentForward + prism.ExtentBackward) * 0.5
                let center = prism.AnchorPoint + axis * (prism.ExtentForward - prism.ExtentBackward) * 0.5
                let c0 = center - planeDir * hw - axis * hh
                let c1 = center + planeDir * hw - axis * hh
                let c3 = center - planeDir * hw + axis * hh
                [| c0; c1; c1; c3 |], planeDir, axis, hw * 2.0, hh * 2.0
            | CutPlaneMode.AcrossAxis dist ->
                let center = prism.AnchorPoint + axis * dist
                let hw = r * 1.2
                let c0 = center - right * hw - fwd * hw
                let c1 = center + right * hw - fwd * hw
                let c3 = center - right * hw + fwd * hw
                [| c0; c1; c1; c3 |], right, fwd, hw * 2.0, hw * 2.0
        let tickScale =
            let ext = max extentU extentV
            if ext > 10.0 then 2.0
            elif ext > 5.0 then 1.0
            elif ext > 2.0 then 0.5
            elif ext > 0.5 then 0.1
            else 0.05
        let perpIn = edgeV |> Vec.normalize
        let nTicks = int (extentU / tickScale)
        let tickLen = min extentU extentV * 0.05
        [ for i in 0 .. nTicks do
            if i % 5 = 0 && i > 0 then
                let t = float i * tickScale / extentU
                if t <= 1.0 then
                    let pt = corners.[0] + (corners.[1] - corners.[0]) * t
                    let labelPos = pt + perpIn * tickLen * 2.5
                    let value = float i * tickScale
                    yield (labelPos, sprintf "%.2g" value, perpIn, edgeU |> Vec.normalize) ]

    /// Solid cylinder hull surface (for picking). Returns positions and indices.
    let buildCylinderHull (prism : SelectionPrism) (segments : int) =
        let axis = prism.AxisDirection |> Vec.normalize
        let up = if abs axis.Z > 0.9 then V3d.OIO else V3d.OOI
        let right = Vec.cross axis up |> Vec.normalize
        let fwd = Vec.cross right axis |> Vec.normalize
        let r = match prism.Footprint.Vertices with v :: _ -> v.Length | _ -> 1.0
        let top = prism.AnchorPoint + axis * prism.ExtentForward
        let bot = prism.AnchorPoint - axis * prism.ExtentBackward
        let positions = Array.init (segments * 2) (fun i ->
            let ring = i / segments
            let seg = i % segments
            let a = float seg / float segments * Constant.PiTimesTwo
            let offset = right * cos a * r + fwd * sin a * r
            V3f(if ring = 0 then top + offset else bot + offset))
        let indices = System.Collections.Generic.List<int>()
        for i in 0 .. segments - 1 do
            let j = (i + 1) % segments
            let t0, t1 = i, j
            let b0, b1 = i + segments, j + segments
            indices.Add(t0); indices.Add(b0); indices.Add(t1)
            indices.Add(t1); indices.Add(b0); indices.Add(b1)
        positions, indices.ToArray()

    /// Horizontal ring indicator at a given axis-distance on the cylinder hull.
    let buildHullRing (prism : SelectionPrism) (dist : float) (thickness : float) (segments : int) =
        let axis = prism.AxisDirection |> Vec.normalize
        let up = if abs axis.Z > 0.9 then V3d.OIO else V3d.OOI
        let right = Vec.cross axis up |> Vec.normalize
        let fwd = Vec.cross right axis |> Vec.normalize
        let r = match prism.Footprint.Vertices with v :: _ -> v.Length | _ -> 1.0
        let rOuter = r * 1.02
        let center = prism.AnchorPoint + axis * dist
        let halfT = thickness * 0.5
        let positions = Array.init (segments * 2) (fun i ->
            let ring = i / segments
            let seg = i % segments
            let a = float seg / float segments * Constant.PiTimesTwo
            let offset = right * cos a * rOuter + fwd * sin a * rOuter
            V3f(center + offset + axis * (if ring = 0 then halfT else -halfT)))
        let indices = System.Collections.Generic.List<int>()
        for i in 0 .. segments - 1 do
            let j = (i + 1) % segments
            let t0, t1 = i, j
            let b0, b1 = i + segments, j + segments
            indices.Add(t0); indices.Add(b0); indices.Add(t1)
            indices.Add(t1); indices.Add(b0); indices.Add(b1)
        positions, indices.ToArray()

    /// Vertical line indicator at a given angle on the cylinder hull.
    let buildHullLine (prism : SelectionPrism) (angleDeg : float) (thickness : float) =
        let axis = prism.AxisDirection |> Vec.normalize
        let up = if abs axis.Z > 0.9 then V3d.OIO else V3d.OOI
        let right = Vec.cross axis up |> Vec.normalize
        let fwd = Vec.cross right axis |> Vec.normalize
        let r = match prism.Footprint.Vertices with v :: _ -> v.Length | _ -> 1.0
        let rOuter = r * 1.02
        let a = angleDeg * Constant.RadiansPerDegree
        let dir = right * cos a + fwd * sin a
        let perp = Vec.cross dir axis |> Vec.normalize
        let top = prism.AnchorPoint + axis * prism.ExtentForward + dir * rOuter
        let bot = prism.AnchorPoint - axis * prism.ExtentBackward + dir * rOuter
        let halfT = thickness * 0.5
        [| V3f(top + perp * halfT); V3f(top - perp * halfT); V3f(bot + perp * halfT); V3f(bot - perp * halfT) |],
        [| 0;1;2; 1;3;2 |]
