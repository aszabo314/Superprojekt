namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering

module Shader =
    open FShade

    type UniformScope with
        member x.FlatColor : V4f = x?FlatColor

    let flatColor (_v : Effects.Vertex) =
        fragment { return uniform.FlatColor }

module Lines =
    open Aardvark.Dom
    open FSharp.Data.Adaptive
    open FShade

    type Vertex = {
        [<Semantic("P0")>]        p0    : V3f
        [<Semantic("P1")>]        p1    : V3f
        [<Semantic("LineColor")>] color : V4f
        [<Semantic("LineWidth")>] width : float32
        [<Position>]              pos   : V4f
        [<Color>]                 col   : V4f
        [<VertexId>]              id    : int
    }

    let line (v : Vertex) =
        vertex {
            let m = uniform.ModelViewProjTrafo
            let o = v.p0
            let d = v.p1 - v.p0
            let mutable tLo = 0.0f
            let mutable tHi = 1.0f

            // Clip against the 6 frustum half-spaces: t = -(p·o + p.w)/(p·d);
            // dir > 0 → entering → tighten tHi; dir < 0 → exiting → tighten tLo.
            let pl0  = -m.R3 - m.R0
            let dir0 = Vec.dot pl0.XYZ d
            let tp0  = (pl0.W + Vec.dot pl0.XYZ o) / -dir0
            if dir0 > 1e-9f then
                if tp0 < tHi then tHi <- tp0
            elif dir0 < -1e-9f then
                if tp0 > tLo then tLo <- tp0

            let pl1  = -m.R3 + m.R0
            let dir1 = Vec.dot pl1.XYZ d
            let tp1  = (pl1.W + Vec.dot pl1.XYZ o) / -dir1
            if dir1 > 1e-9f then
                if tp1 < tHi then tHi <- tp1
            elif dir1 < -1e-9f then
                if tp1 > tLo then tLo <- tp1

            let pl2  = -m.R3 - m.R1
            let dir2 = Vec.dot pl2.XYZ d
            let tp2  = (pl2.W + Vec.dot pl2.XYZ o) / -dir2
            if dir2 > 1e-9f then
                if tp2 < tHi then tHi <- tp2
            elif dir2 < -1e-9f then
                if tp2 > tLo then tLo <- tp2

            let pl3  = -m.R3 + m.R1
            let dir3 = Vec.dot pl3.XYZ d
            let tp3  = (pl3.W + Vec.dot pl3.XYZ o) / -dir3
            if dir3 > 1e-9f then
                if tp3 < tHi then tHi <- tp3
            elif dir3 < -1e-9f then
                if tp3 > tLo then tLo <- tp3

            let pl4  = -m.R3 - m.R2
            let dir4 = Vec.dot pl4.XYZ d
            let tp4  = (pl4.W + Vec.dot pl4.XYZ o) / -dir4
            if dir4 > 1e-9f then
                if tp4 < tHi then tHi <- tp4
            elif dir4 < -1e-9f then
                if tp4 > tLo then tLo <- tp4

            let pl5  = -m.R3 + m.R2
            let dir5 = Vec.dot pl5.XYZ d
            let tp5  = (pl5.W + Vec.dot pl5.XYZ o) / -dir5
            if dir5 > 1e-9f then
                if tp5 < tHi then tHi <- tp5
            elif dir5 < -1e-9f then
                if tp5 > tLo then tLo <- tp5

            if tHi > tLo then
                let p0w = o + tLo * d
                let p1w = o + tHi * d

                let corner = v.id % 4
                let mpX = if corner &&& 1 <> 0 then 1.0f else 0.0f
                let mpY = if corner &&& 2 <> 0 then 1.0f else 0.0f

                let vs   = uniform.ViewportSize
                let p0c  = m * V4f(p0w, 1.0f)
                let p1c  = m * V4f(p1w, 1.0f)
                let p0n  = p0c.XYZ / p0c.W
                let p1n  = p1c.XYZ / p1c.W

                let pixelToNdc  = V2f(2.0f / float32 vs.X, 2.0f / float32 vs.Y)
                let halfWidthPx = v.width * 0.5f

                let diff     = p1n - p0n
                let pixelDir = V2f(diff.X * float32 vs.X * 0.5f, diff.Y * float32 vs.Y * 0.5f)
                let pixelLen = Vec.length pixelDir

                let perpDir =
                    if pixelLen > 1e-10f then V2f(-pixelDir.Y, pixelDir.X) / pixelLen
                    else V2f(0.0f, 1.0f)
                let lineDir =
                    if pixelLen > 1e-10f then pixelDir / pixelLen
                    else V2f(0.0f, 1.0f)

                let perpSign = if mpX > 0.5f then 1.0f else -1.0f
                let lineSign = if mpY > 0.5f then 1.0f else -1.0f
                let perpOffset = perpDir * (perpSign * halfWidthPx) * pixelToNdc
                let lineOffset = lineDir * (lineSign * halfWidthPx) * pixelToNdc

                let basePos = if mpY > 0.5f then p1n.XY else p0n.XY
                let xy      = basePos + perpOffset + lineOffset

                let zT = if mpY > 0.5f then 1.0f else 0.0f
                let z  = p0n.Z * (1.0f - zT) + p1n.Z * zT

                return { v with pos = V4f(xy.X, xy.Y, z, 1.0f); col = v.color }
            else
                return { v with pos = V4f(2.0f, 2.0f, 2.0f, 1.0f); col = V4f.Zero }
        }

    let fragment (v : Vertex) =
        fragment { return v.col }

    let private buildBuffers (segments : aval<(V3d * V3d * V4d * float)[]>) =
        let buffers =
            segments |> AVal.map (fun segs ->
                let n = segs.Length
                let len = max 1 (4 * n)
                let p0Buf    = Array.zeroCreate<V3f>     len
                let p1Buf    = Array.zeroCreate<V3f>     len
                let colBuf   = Array.zeroCreate<V4f>     len
                let widthBuf = Array.zeroCreate<float32> len
                let indices  = Array.zeroCreate<int>     (max 1 (6 * n))
                for i in 0 .. n - 1 do
                    let (p0, p1, c, w) = segs.[i]
                    let p0f = V3f p0
                    let p1f = V3f p1
                    let cf  = V4f(float32 c.X, float32 c.Y, float32 c.Z, float32 c.W)
                    let wf  = float32 w
                    let b   = i * 4
                    for k in 0 .. 3 do
                        p0Buf.[b + k]    <- p0f
                        p1Buf.[b + k]    <- p1f
                        colBuf.[b + k]   <- cf
                        widthBuf.[b + k] <- wf
                    let ib = i * 6
                    indices.[ib + 0] <- b
                    indices.[ib + 1] <- b + 1
                    indices.[ib + 2] <- b + 2
                    indices.[ib + 3] <- b + 1
                    indices.[ib + 4] <- b + 3
                    indices.[ib + 5] <- b + 2
                p0Buf, p1Buf, colBuf, widthBuf, indices, n)
        let p0Arr    = buffers |> AVal.map (fun (a,_,_,_,_,_) -> ArrayBuffer a :> IBuffer)
        let p1Arr    = buffers |> AVal.map (fun (_,a,_,_,_,_) -> ArrayBuffer a :> IBuffer)
        let colArr   = buffers |> AVal.map (fun (_,_,a,_,_,_) -> ArrayBuffer a :> IBuffer)
        let widthArr = buffers |> AVal.map (fun (_,_,_,a,_,_) -> ArrayBuffer a :> IBuffer)
        let idxArr   = buffers |> AVal.map (fun (_,_,_,_,a,_) -> ArrayBuffer a :> IBuffer)
        let count    = buffers |> AVal.map (fun (_,_,_,_,_,n) -> if n = 0 then 0 else 6 * n)
        p0Arr, p1Arr, colArr, widthArr, idxArr, count

    // Alpha-blended lines; callers steer ordering via Sg.DepthTest/Sg.Pass.
    // Sg.DepthMask is never used (buggy here), so line fragments also write
    // depth — see SceneGraph.build.
    let render (segments : aval<(V3d * V3d * V4d * float)[]>) =
        let p0Arr, p1Arr, colArr, widthArr, idxArr, count = buildBuffers segments
        sg {
            Sg.Shader { line; fragment }
            Sg.NoEvents
            Sg.VertexAttributes(
                HashMap.ofList [
                    "P0",        BufferView(p0Arr,    typeof<V3f>)
                    "P1",        BufferView(p1Arr,    typeof<V3f>)
                    "LineColor", BufferView(colArr,   typeof<V4f>)
                    "LineWidth", BufferView(widthArr, typeof<float32>)
                ])
            Sg.Index(BufferView(idxArr, typeof<int>))
            Sg.Render count
        }

// Shared line-glyph builders for the 3D + focus overlays — segments appended as
// (a, b, colour, width) for Lines.render. ONE home so the spec-critical
// conventions (dash phase, ring segment counts, arrowhead shape) cannot fork
// between the main scene and the focus views.
module LineGlyphs =

    // Orthonormal (u, v) basis ⊥ a (possibly unnormalized/degenerate) normal,
    // plus the normalized normal itself.
    let basisFromNormal (n : V3d) =
        let nN = if n.Length > 1e-9 then n.Normalized else V3d.OOI
        let u = (if abs nN.Z < 0.9 then Vec.cross nN V3d.OOI else Vec.cross nN V3d.IOO).Normalized
        nN, u, Vec.cross nN u

    let addRing (out : ResizeArray<V3d * V3d * V4d * float>)
                (c : V3d) (u : V3d) (v : V3d) (r : float) (col : V4d) (width : float) (segs : int) =
        for i in 0 .. segs - 1 do
            let a0 = float i / float segs * Constant.PiTimesTwo
            let a1 = float (i + 1) / float segs * Constant.PiTimesTwo
            out.Add(c + (u * cos a0 + v * sin a0) * r, c + (u * cos a1 + v * sin a1) * r, col, width)

    let addRingXY out c r col w segs = addRing out c V3d.IOO V3d.OIO r col w segs

    // Ring facing the eye (approximate sphere silhouette) — the 360° focus views.
    let addRingFacing out (eye : V3d) (c : V3d) r col w segs =
        if (c - eye).Length > 1e-9 then
            let _, u, v = basisFromNormal (c - eye)
            addRing out c u v r col w segs

    // Dashed ring (every other segment drawn) — the uncoloured selection circle.
    let addDashedRing (out : ResizeArray<V3d * V3d * V4d * float>)
                      (c : V3d) (u : V3d) (v : V3d) (r : float) (col : V4d) (width : float) (segs : int) =
        for i in 0 .. segs - 1 do
            if i % 2 = 0 then
                let a0 = float i / float segs * Constant.PiTimesTwo
                let a1 = float (i + 1) / float segs * Constant.PiTimesTwo
                out.Add(c + (u * cos a0 + v * sin a0) * r, c + (u * cos a1 + v * sin a1) * r, col, width)

    let addDashedRingXY out c r col w segs = addDashedRing out c V3d.IOO V3d.OIO r col w segs

    let addDashedRingFacing out (eye : V3d) (c : V3d) r col w segs =
        if (c - eye).Length > 1e-9 then
            let _, u, v = basisFromNormal (c - eye)
            addDashedRing out c u v r col w segs

    // Arrow a→b: thin shaft + a line-triangle tip oriented to face the eye.
    // Head scales with the shaft but caps at a modest render size.
    let addArrow (out : ResizeArray<V3d * V3d * V4d * float>)
                 (a : V3d) (b : V3d) (eye : V3d) (col : V4d) (width : float) =
        let d = b - a
        if d.Length > 1e-9 then
            let dN = d.Normalized
            let side =
                let c = Vec.cross (b - eye) dN
                if c.Length > 1e-9 then c.Normalized
                else (Vec.cross dN (if abs dN.Z < 0.9 then V3d.OOI else V3d.IOO)).Normalized
            let hl = min (d.Length * 0.35) 0.12
            let hw = hl * 0.45
            let back = b - dN * hl
            out.Add(a, back, col, width)
            out.Add(b, back + side * hw, col, width)
            out.Add(b, back - side * hw, col, width)
            out.Add(back + side * hw, back - side * hw, col, width)

    // Top-view arrow a→b in the XY plane; head cap supplied by the caller
    // (screen-fixed glyph size).
    let addArrowXY (out : ResizeArray<V3d * V3d * V4d * float>)
                   (a : V3d) (b : V3d) (headLen : float) (col : V4d) (w : float) =
        let d = b - a
        if d.Length > 1e-9 then
            let dN = d.Normalized
            let side =
                let c = Vec.cross V3d.OOI dN
                if c.Length > 1e-9 then c.Normalized else V3d.IOO
            let hl = min (d.Length * 0.4) headLen
            let hw = hl * 0.45
            let back = b - dN * hl
            out.Add(a, back, col, w)
            out.Add(b, back + side * hw, col, w)
            out.Add(b, back - side * hw, col, w)
            out.Add(back + side * hw, back - side * hw, col, w)

    // Wire sphere (three axis-aligned great circles) of radius r at c.
    let addWireSphere (out : ResizeArray<V3d * V3d * V4d * float>)
                      (c : V3d) (r : float) (col : V4d) (width : float) (segs : int) =
        addRing out c V3d.IOO V3d.OIO r col width segs
        addRing out c V3d.IOO V3d.OOI r col width segs
        addRing out c V3d.OIO V3d.OOI r col width segs

    // Small 3-axis cross (half-length r) marking an exact point at c.
    let addCross (out : ResizeArray<V3d * V3d * V4d * float>)
                 (c : V3d) (r : float) (col : V4d) (width : float) =
        out.Add(c - V3d.IOO * r, c + V3d.IOO * r, col, width)
        out.Add(c - V3d.OIO * r, c + V3d.OIO * r, col, width)
        out.Add(c - V3d.OOI * r, c + V3d.OOI * r, col, width)

    // XY-only cross — the focus Top glyph.
    let addCrossXY (out : ResizeArray<V3d * V3d * V4d * float>)
                   (c : V3d) (r : float) (col : V4d) (w : float) =
        out.Add(c - V3d.IOO * r, c + V3d.IOO * r, col, w)
        out.Add(c - V3d.OIO * r, c + V3d.OIO * r, col, w)

    // 12 edges of an axis-aligned box (half-extents hx,hy,hz) at c.
    let addBoxOutline (out : ResizeArray<V3d * V3d * V4d * float>)
                      (c : V3d) (hx : float) (hy : float) (hz : float) (col : V4d) (width : float) =
        let v = [|
            V3d(-hx, -hy, -hz); V3d( hx, -hy, -hz); V3d( hx, hy, -hz); V3d(-hx, hy, -hz)
            V3d(-hx, -hy,  hz); V3d( hx, -hy,  hz); V3d( hx, hy,  hz); V3d(-hx, hy,  hz) |]
        let e = [| 0,1; 1,2; 2,3; 3,0; 4,5; 5,6; 6,7; 7,4; 0,4; 1,5; 2,6; 3,7 |]
        for (a, b) in e do out.Add(c + v.[a], c + v.[b], col, width)
