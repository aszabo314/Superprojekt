namespace Superprojekt

open Aardvark.Base

module PinGeometry =

    /// Returns (right, fwd) so (right, fwd, axis) is right-handed; up defaults to +Z unless axis is near ±Z.
    let axisFrame (axis : V3d) =
        let up = if abs axis.Z > 0.9 then V3d.OIO else V3d.OOI
        let right = Vec.cross axis up |> Vec.normalize
        let fwd = Vec.cross right axis |> Vec.normalize
        right, fwd

    let coreSampleTrafo (prism : SelectionPrism) =
        let axis = -(prism.AxisDirection |> Vec.normalize)
        let right, fwd = axisFrame axis
        let rotFwd = M44d(right.X, right.Y, right.Z, 0.0,
                          fwd.X,   fwd.Y,   fwd.Z,   0.0,
                          axis.X,  axis.Y,  axis.Z,  0.0,
                          0.0,     0.0,     0.0,     1.0)
        Trafo3d.Translation(-prism.AnchorPoint) * Trafo3d(rotFwd, rotFwd.Transposed)

    /// Returns ((upperPos, upperIdx), (lowerPos, lowerIdx), (sidePos, sideIdx)) for the between-space volume.
    /// Quads are emitted only when all four corner nodes have brackets; side walls close the resulting boundary.
    let buildBetweenSpaceSurfaces (prism : SelectionPrism) (data : StratigraphyData) (cache : BandCache option) (colIdx : int) (hoverZ : float) =
        let axis = prism.AxisDirection |> Vec.normalize
        let right, fwd = axisFrame axis
        let n = data.AngularResolution
        let rings = if isNull (box data.Rings) then [||] else data.Rings
        let ringCount = rings.Length
        let radii = data.RingRadii
        let empty = [||], [||]
        if n = 0 || ringCount = 0 then empty, empty, empty
        else
            let band =
                match cache with
                | Some c -> Stratigraphy.lookupBand3D c colIdx hoverZ
                | None -> Stratigraphy.floodContinuousBand3D data colIdx hoverZ
            if Map.isEmpty band then empty, empty, empty
            else
            let firstBracket (ang : int) (ring : int) =
                match Map.tryFind (ang, ring) band with
                | Some (b :: _) -> Some b
                | _ -> None
            let pos (ang : int) (ring : int) (z : float) : V3f =
                let t = float ang / float n * System.Math.PI * 2.0
                let rr = radii.[ring]
                V3f (prism.AnchorPoint + (right * cos t + fwd * sin t) * rr + axis * z)
            let upperPos = System.Collections.Generic.List<V3f>()
            let upperIdx = System.Collections.Generic.List<int>()
            let lowerPos = System.Collections.Generic.List<V3f>()
            let lowerIdx = System.Collections.Generic.List<int>()
            let sidePos  = System.Collections.Generic.List<V3f>()
            let sideIdx  = System.Collections.Generic.List<int>()
            let quadComplete (i : int) (k : int) =
                if k < 0 || k >= ringCount - 1 then false
                else
                    let iN = (i + 1) % n
                    (firstBracket i k).IsSome &&
                    (firstBracket iN k).IsSome &&
                    (firstBracket iN (k+1)).IsSome &&
                    (firstBracket i (k+1)).IsSome
            for i in 0 .. n - 1 do
                let iNext = (i + 1) % n
                for k in 0 .. ringCount - 2 do
                    match firstBracket i k, firstBracket iNext k, firstBracket iNext (k+1), firstBracket i (k+1) with
                    | Some (lo00, hi00), Some (lo10, hi10), Some (lo11, hi11), Some (lo01, hi01) ->
                        let b = upperPos.Count
                        upperPos.Add (pos i     k     hi00)
                        upperPos.Add (pos iNext k     hi10)
                        upperPos.Add (pos iNext (k+1) hi11)
                        upperPos.Add (pos i     (k+1) hi01)
                        upperIdx.Add b;       upperIdx.Add (b + 1); upperIdx.Add (b + 2)
                        upperIdx.Add b;       upperIdx.Add (b + 2); upperIdx.Add (b + 3)
                        let b = lowerPos.Count
                        lowerPos.Add (pos i     k     lo00)
                        lowerPos.Add (pos iNext k     lo10)
                        lowerPos.Add (pos iNext (k+1) lo11)
                        lowerPos.Add (pos i     (k+1) lo01)
                        lowerIdx.Add b;       lowerIdx.Add (b + 1); lowerIdx.Add (b + 2)
                        lowerIdx.Add b;       lowerIdx.Add (b + 2); lowerIdx.Add (b + 3)
                    | _ -> ()
            let emitWall iA kA iB kB =
                match firstBracket iA kA, firstBracket iB kB with
                | Some (loA, hiA), Some (loB, hiB) ->
                    let pA_lo = pos iA kA loA
                    let pB_lo = pos iB kB loB
                    let pB_hi = pos iB kB hiB
                    let pA_hi = pos iA kA hiA
                    let b = sidePos.Count
                    sidePos.Add pA_lo; sidePos.Add pB_lo; sidePos.Add pB_hi; sidePos.Add pA_hi
                    sideIdx.Add b;       sideIdx.Add (b + 1); sideIdx.Add (b + 2)
                    sideIdx.Add b;       sideIdx.Add (b + 2); sideIdx.Add (b + 3)
                | _ -> ()
            for i in 0 .. n - 1 do
                let iNext = (i + 1) % n
                for k in 0 .. ringCount - 1 do
                    let boundary = not (quadComplete i (k - 1) && quadComplete i k)
                    if boundary then emitWall i k iNext k
            for i in 0 .. n - 1 do
                let iPrev = (i + n - 1) % n
                for k in 0 .. ringCount - 2 do
                    let boundary = not (quadComplete iPrev k && quadComplete i k)
                    if boundary then emitWall i k i (k + 1)
            (upperPos.ToArray(), upperIdx.ToArray()),
            (lowerPos.ToArray(), lowerIdx.ToArray()),
            (sidePos.ToArray(),  sideIdx.ToArray())

    let appendPolylineSegments
            (segs : ResizeArray<V3d * V3d * V4d * float>)
            (pts : V3d[]) (color : V4d) (widthPx : float) =
        for i in 0 .. pts.Length - 2 do
            let a = pts.[i]
            let b = pts.[i + 1]
            if (b - a).LengthSquared > 1e-20 then
                segs.Add((a, b, color, widthPx))

    let buildCutPlaneQuad (prism : SelectionPrism) (cutPlane : CutPlaneMode) =
        let axis = prism.AxisDirection |> Vec.normalize
        let right, fwd = axisFrame axis
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

    let private cutPlaneCorners (prism : SelectionPrism) (cutPlane : CutPlaneMode) =
        let axis = prism.AxisDirection |> Vec.normalize
        let right, fwd = axisFrame axis
        let r = match prism.Footprint.Vertices with v :: _ -> v.Length | _ -> 1.0
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
            [| c0; c1; c2; c3 |], hw * 2.0, hh * 2.0
        | CutPlaneMode.AcrossAxis dist ->
            let center = prism.AnchorPoint + axis * dist
            let hw = r * 1.2
            let c0 = center - right * hw - fwd * hw
            let c1 = center + right * hw - fwd * hw
            let c2 = center + right * hw + fwd * hw
            let c3 = center - right * hw + fwd * hw
            [| c0; c1; c2; c3 |], hw * 2.0, hw * 2.0

    /// Translucent gradient quad fill of the cut plane (no edges/grid — those are line segments).
    let buildCutPlaneFill (prism : SelectionPrism) (cutPlane : CutPlaneMode) =
        let corners, _, _ = cutPlaneCorners prism cutPlane
        let edgeAlpha = 0.08f
        let mid01 = (corners.[0] + corners.[1]) * 0.5
        let mid12 = (corners.[1] + corners.[2]) * 0.5
        let mid23 = (corners.[2] + corners.[3]) * 0.5
        let mid30 = (corners.[3] + corners.[0]) * 0.5
        let center = (corners.[0] + corners.[1] + corners.[2] + corners.[3]) * 0.25
        let verts = [| corners.[0]; mid01; corners.[1]; mid30; center; mid12; corners.[3]; mid23; corners.[2] |]
        let alphas = [| edgeAlpha; edgeAlpha; edgeAlpha; edgeAlpha; 0.0f; edgeAlpha; edgeAlpha; edgeAlpha; edgeAlpha |]
        let positions = verts |> Array.map V3f
        let colors    = alphas |> Array.map (fun a -> V4f(1.0f, 1.0f, 1.0f, a))
        let indices = [|
            0;1;4;  0;4;3
            1;2;5;  1;5;4
            3;4;7;  3;7;6
            4;5;8;  4;8;7
        |]
        positions, colors, indices

    /// Boundary frame + grid lines as `(p0, p1, color, widthPx)` segments for `Lines.render`.
    let buildCutPlaneEdges (prism : SelectionPrism) (cutPlane : CutPlaneMode) =
        let corners, extentU, extentV = cutPlaneCorners prism cutPlane
        let edgeColor  = V4d(1.0, 1.0, 1.0, 0.6)
        let majorColor = V4d(1.0, 1.0, 1.0, 0.30)
        let minorColor = V4d(1.0, 1.0, 1.0, 0.15)
        let edgeWidth  = 1.5
        let gridWidth  = 1.0
        let segs = ResizeArray<V3d * V3d * V4d * float>()
        segs.Add(corners.[0], corners.[1], edgeColor, edgeWidth)
        segs.Add(corners.[1], corners.[2], edgeColor, edgeWidth)
        segs.Add(corners.[2], corners.[3], edgeColor, edgeWidth)
        segs.Add(corners.[3], corners.[0], edgeColor, edgeWidth)
        let tickScale = 0.25
        let nTicksU = int (extentU / tickScale)
        for i in 0 .. nTicksU do
            let t = float i * tickScale / extentU
            if t <= 1.0 then
                let a = corners.[0] + (corners.[1] - corners.[0]) * t
                let b = corners.[3] + (corners.[2] - corners.[3]) * t
                let c = if i % 4 = 0 then majorColor else minorColor
                segs.Add(a, b, c, gridWidth)
        let nTicksV = int (extentV / tickScale)
        for i in 0 .. nTicksV do
            let t = float i * tickScale / extentV
            if t <= 1.0 then
                let a = corners.[0] + (corners.[3] - corners.[0]) * t
                let b = corners.[1] + (corners.[2] - corners.[1]) * t
                let c = if i % 4 = 0 then majorColor else minorColor
                segs.Add(a, b, c, gridWidth)
        segs.ToArray()

    /// EdgeU = along edge 0→1 (bottom), EdgeV = along edge 0→3 (left).
    type TickEdge = EdgeU | EdgeV

    let private labelTrafo (x : V3d) (y : V3d) (z : V3d) (pos : V3d) (size : float) =
        Trafo3d(
            M44d(x.X, y.X, z.X, pos.X,
                 x.Y, y.Y, z.Y, pos.Y,
                 x.Z, y.Z, z.Z, pos.Z,
                 0.0, 0.0, 0.0, 1.0),
            M44d(x.X, x.Y, x.Z, -Vec.dot x pos,
                 y.X, y.Y, y.Z, -Vec.dot y pos,
                 z.X, z.Y, z.Z, -Vec.dot z pos,
                 0.0, 0.0, 0.0, 1.0)) *
        Trafo3d.Scale(size)

    let private cutPlaneFrame (prism : SelectionPrism) (cutPlane : CutPlaneMode) =
        let axis = prism.AxisDirection |> Vec.normalize
        let right, fwd = axisFrame axis
        let r = match prism.Footprint.Vertices with v :: _ -> v.Length | _ -> 1.0
        let hw = r * 1.2
        let hh = (prism.ExtentForward + prism.ExtentBackward) * 0.5
        match cutPlane with
        | CutPlaneMode.AlongAxis angleDeg ->
            let a = angleDeg * Constant.RadiansPerDegree
            let planeDir = right * cos a + fwd * sin a
            let center = prism.AnchorPoint + axis * (prism.ExtentForward - prism.ExtentBackward) * 0.5
            let c0 = center - planeDir * hw - axis * hh
            let c1 = center + planeDir * hw - axis * hh
            let c3 = center - planeDir * hw + axis * hh
            c0, c1, c3, planeDir |> Vec.normalize, axis |> Vec.normalize
        | CutPlaneMode.AcrossAxis dist ->
            let center = prism.AnchorPoint + axis * dist
            let c0 = center - right * hw - fwd * hw
            let c1 = center + right * hw - fwd * hw
            let c3 = center - right * hw + fwd * hw
            c0, c1, c3, right |> Vec.normalize, fwd |> Vec.normalize

    let tickLabelWorldTrafo (prism : SelectionPrism) (cutPlane : CutPlaneMode) (edge : TickEdge) (unitT : float) =
        let c0, c1, c3, edgeDirU, edgeDirV = cutPlaneFrame prism cutPlane
        let planeNormal = Vec.cross edgeDirU edgeDirV |> Vec.normalize
        let labelSize = 0.05
        let offset = labelSize * 1.5
        let pt =
            match edge with
            | EdgeU -> c0 + (c1 - c0) * unitT - edgeDirV * offset
            | EdgeV -> c0 + (c3 - c0) * unitT - edgeDirU * offset
        labelTrafo edgeDirU edgeDirV planeNormal pt labelSize

    let centroidLabelTrafo (prism : SelectionPrism) (cutPlane : CutPlaneMode) =
        let c0, _, _, edgeDirU, edgeDirV = cutPlaneFrame prism cutPlane
        let planeNormal = Vec.cross edgeDirU edgeDirV |> Vec.normalize
        let labelSize = 0.04
        let offset = labelSize * 2.0
        let pt = c0 - edgeDirU * offset - edgeDirV * offset
        labelTrafo edgeDirU edgeDirV planeNormal pt labelSize

    /// Profile/Plan in-progress preview prism. None when the gesture hasn't produced
    /// enough info yet. `bounds.SizeZ` drives ExtentBackward so the cylinder spans the stack.
    let placementPreviewPrism (placement : PlacementState) (bounds : Box3d) =
        let autoDepth () = if bounds.IsInvalid then 10.0 else min 10.0 bounds.SizeZ
        let ring r =
            { Vertices = [ for k in 0 .. 31 ->
                            let a = float k * 2.0 * System.Math.PI / 32.0
                            V2d(cos a * r, sin a * r) ] }
        match placement with
        | ProfilePlacement (ProfileWaitingForSecondPoint(p1, Some p2)) ->
            let diff = p2 - p1
            let len = diff.Length
            if len < 1e-3 then None
            else
                let center = (p1 + p2) * 0.5
                let radius = max 0.1 (len * 0.6)
                Some {
                    AnchorPoint = center
                    AxisDirection = V3d.OOI
                    Footprint = ring radius
                    ExtentForward = 1.0
                    ExtentBackward = autoDepth ()
                }
        | PlanPlacement (PlanDragging(center, r)) when r >= 0.05 ->
            Some {
                AnchorPoint = center
                AxisDirection = V3d.OOI
                Footprint = ring r
                ExtentForward = 1.0
                ExtentBackward = autoDepth ()
            }
        | _ -> None

    let buildCylinderHull (prism : SelectionPrism) (segments : int) =
        let axis = prism.AxisDirection |> Vec.normalize
        let right, fwd = axisFrame axis
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
            indices.Add(t0); indices.Add(t1); indices.Add(b0)
            indices.Add(t1); indices.Add(b1); indices.Add(b0)
        positions, indices.ToArray()

    /// Top + bottom ring polylines plus 2 view-dependent silhouette edges, as Lines.render segments.
    /// `camPos` in world space; if camera lies on the axis, the silhouette term is skipped.
    let buildCylinderOutline (prism : SelectionPrism) (camPos : V3d) (ringColor : V4d) (silhColor : V4d) =
        let axis = prism.AxisDirection |> Vec.normalize
        let right, fwd = axisFrame axis
        let r = match prism.Footprint.Vertices with v :: _ -> v.Length | _ -> 1.0
        let topCenter = prism.AnchorPoint + axis * prism.ExtentForward
        let botCenter = prism.AnchorPoint - axis * prism.ExtentBackward
        let n = 64
        let ringWidth = 1.0
        let silhWidth = 1.5
        let segs = ResizeArray<V3d * V3d * V4d * float>(2 * n + 2)
        for i in 0 .. n - 1 do
            let a0 = float i       / float n * Constant.PiTimesTwo
            let a1 = float (i + 1) / float n * Constant.PiTimesTwo
            let d0 = right * cos a0 + fwd * sin a0
            let d1 = right * cos a1 + fwd * sin a1
            segs.Add((topCenter + d0 * r, topCenter + d1 * r, ringColor, ringWidth))
            segs.Add((botCenter + d0 * r, botCenter + d1 * r, ringColor, ringWidth))
        let toCam = camPos - prism.AnchorPoint
        let camProj = toCam - axis * Vec.dot toCam axis
        if camProj.LengthSquared > 1e-12 then
            let camDirPerp = camProj |> Vec.normalize
            let silhDir = Vec.cross axis camDirPerp |> Vec.normalize
            segs.Add((topCenter + silhDir * r, botCenter + silhDir * r, silhColor, silhWidth))
            segs.Add((topCenter - silhDir * r, botCenter - silhDir * r, silhColor, silhWidth))
        segs.ToArray()

    let buildHullRing (prism : SelectionPrism) (dist : float) (thickness : float) (segments : int) =
        let axis = prism.AxisDirection |> Vec.normalize
        let right, fwd = axisFrame axis
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
            indices.Add(t0); indices.Add(t1); indices.Add(b0)
            indices.Add(t1); indices.Add(b1); indices.Add(b0)
        positions, indices.ToArray()

    /// Auto-mode parameters from a ray-grid response: weight by steepness × proximity, average normals,
    /// then AcrossAxis if N aligns with refAxis else AlongAxis. None when fewer than 3 hot hits.
    let deriveAutoPreview
            (hits : (V3d * V3d) option[])
            (clickWorld : V3d)
            (refAxisWorld : V3d)
            (scale : float)
            : (V3d * CutPlaneMode * float * V3d) option =
        let hot =
            hits |> Array.choose (function
                | Some (pt, n) ->
                    let nn = n |> Vec.normalize
                    let aligned = abs (Vec.dot nn refAxisWorld)
                    let steepW = max 0.0 (1.0 - aligned / 0.9)
                    let d = (pt - clickWorld).Length
                    let prox = exp (-d * d / 25.0)
                    let w = steepW * prox
                    if w > 1e-3 then Some (pt, nn, w) else None
                | None -> None)
        if hot.Length < 3 then None
        else
            let mutable sum = V3d.Zero
            let mutable wsum = 0.0
            for (_, n, w) in hot do
                let s = if Vec.dot n refAxisWorld < 0.0 then -1.0 else 1.0
                sum <- sum + n * (s * w)
                wsum <- wsum + w
            let n =
                if wsum < 1e-6 then refAxisWorld
                else (sum / wsum) |> Vec.normalize
            let z = refAxisWorld |> Vec.normalize
            let alignedN = abs (Vec.dot n z)
            let axisWorld, cutPlane =
                if alignedN > 0.95 then
                    z, CutPlaneMode.AcrossAxis 0.0
                else
                    let proj = z - n * Vec.dot z n
                    let axis = proj |> Vec.normalize
                    let nPerp = n - axis * Vec.dot n axis
                    let right, fwd = axisFrame axis
                    let a = atan2 (Vec.dot nPerp fwd) (Vec.dot nPerp right)
                    axis, CutPlaneMode.AlongAxis (a * Constant.DegreesPerRadian)
            let axisUnit = axisWorld |> Vec.normalize
            let transverseR =
                hot |> Array.map (fun (pt, _, _) ->
                    let v = pt - clickWorld
                    (v - axisUnit * Vec.dot v axisUnit).Length)
                |> (fun arr -> if arr.Length = 0 then 1.0 else Array.max arr)
            let radiusWorld = max 0.5 transverseR
            Some (axisWorld, cutPlane, radiusWorld * scale, n)

    let autoPreviewPrism (preview : AutoPreview) (bounds : Box3d) =
        let depth = if bounds.IsInvalid then 10.0 else min 10.0 bounds.SizeZ
        let n = 32
        let footprint =
            { Vertices = [ for k in 0 .. n - 1 ->
                            let a = float k * 2.0 * System.Math.PI / float n
                            V2d(cos a * preview.Radius, sin a * preview.Radius) ] }
        {
            AnchorPoint    = preview.Center
            AxisDirection  = preview.Axis
            Footprint      = footprint
            ExtentForward  = 1.0
            ExtentBackward = depth
        }

    let buildHullLine (prism : SelectionPrism) (angleDeg : float) (thickness : float) =
        let axis = prism.AxisDirection |> Vec.normalize
        let right, fwd = axisFrame axis
        let r = match prism.Footprint.Vertices with v :: _ -> v.Length | _ -> 1.0
        let rOuter = r * 1.02
        let a = angleDeg * Constant.RadiansPerDegree
        let dir = right * cos a + fwd * sin a
        let perp = Vec.cross dir axis |> Vec.normalize
        let top1 = prism.AnchorPoint + axis * prism.ExtentForward + dir * rOuter
        let bot1 = prism.AnchorPoint - axis * prism.ExtentBackward + dir * rOuter
        let top2 = prism.AnchorPoint + axis * prism.ExtentForward - dir * rOuter
        let bot2 = prism.AnchorPoint - axis * prism.ExtentBackward - dir * rOuter
        let halfT = thickness * 0.5
        [| V3f(top1 + perp * halfT); V3f(top1 - perp * halfT); V3f(bot1 + perp * halfT); V3f(bot1 - perp * halfT)
           V3f(top2 + perp * halfT); V3f(top2 - perp * halfT); V3f(bot2 + perp * halfT); V3f(bot2 - perp * halfT) |],
        [| 0;1;2; 1;3;2; 4;5;6; 5;7;6 |]
