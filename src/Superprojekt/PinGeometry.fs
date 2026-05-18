namespace Superprojekt

open Aardvark.Base

module PinGeometry =

    /// Returns (right, fwd) so (right, fwd, axis) is right-handed; up defaults to +Z unless axis is near ±Z.
    let axisFrame (axis : V3d) =
        let up = if abs axis.Z > 0.9 then V3d.OIO else V3d.OOI
        let right = Vec.cross axis up |> Vec.normalize
        let fwd = Vec.cross right axis |> Vec.normalize
        right, fwd

    let appendPolylineSegments
            (segs : ResizeArray<V3d * V3d * V4d * float>)
            (pts : V3d[]) (color : V4d) (widthPx : float) =
        for i in 0 .. pts.Length - 2 do
            let a = pts.[i]
            let b = pts.[i + 1]
            if (b - a).LengthSquared > 1e-20 then
                segs.Add((a, b, color, widthPx))

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
