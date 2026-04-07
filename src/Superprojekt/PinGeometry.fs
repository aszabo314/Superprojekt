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
