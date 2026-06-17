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

            // Clip the segment against the 6 view-frustum half-spaces.
            // For each plane: parametric intersection t = -(p·o + p.w) / (p·d);
            // dir > 0 → entering at t → tighten tHi; dir < 0 → exiting at t → tighten tLo.
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

    // Straight forward-rendered alpha-blended lines: emits (rgb, α) on the
    // standard Color attachment. Callers steer ordering via Sg.DepthTest and
    // Sg.Pass — typical use is DepthTest=LessOrEqual so lines fade behind
    // opaque meshes. Sg.DepthMask is intentionally never used anywhere in
    // this project (see SceneGraph.build for the reason), so line fragments
    // also write to the depth buffer; ordering is good enough in practice.
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
