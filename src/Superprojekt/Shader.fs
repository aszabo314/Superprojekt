namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering

module BlitShader =
    open FShade

    module Heat = 
        let heatMapColors =
            let fromInt (i : int) =
                C4b(
                    byte ((i >>> 16) &&& 0xFF),
                    byte ((i >>> 8) &&& 0xFF),
                    byte (i &&& 0xFF),
                    255uy
                ).ToC4f().ToV4f()   
            Array.map fromInt [|
                0x1639fa
                0x2050fa
                0x3275fb
                0x459afa
                0x55bdfb
                0x67e1fc
                0x72f9f4
                0x72f8d3
                0x72f7ad
                0x71f787
                0x71f55f
                0x70f538
                0x74f530
                0x86f631
                0x9ff633
                0xbbf735
                0xd9f938
                0xf7fa3b
                0xfae238
                0xf4be31
                0xf29c2d
                0xee7627
                0xec5223
                0xeb3b22
            |]

        [<ReflectedDefinition>]
        let heat (tc : float32) =
            let tc = clamp 0.0f 1.0f tc
            let fid = tc * float32 24 - 0.5f
            let id = int (floor fid)
            if id < 0 then 
                heatMapColors.[0]
            elif id >= 24 - 1 then
                heatMapColors.[24 - 1]
            else
                let c0 = heatMapColors.[id]
                let c1 = heatMapColors.[id + 1]
                let t = fid - float32 id
                (c0 * (1.0f - t) + c1 * t)

    
    type Fragment =
        {
            [<Color>] c : V4d
            [<Depth>] d : float
        }

    type ClippyFragment =
        {
            [<Color>] c : V4d
            [<Semantic("Normals")>] n : V4d
        }

    type UniformScope with
        member x.TextureOffset        : V2d   = x?TextureOffset
        member x.TextureScale         : V2d   = x?TextureScale
        member x.MeshCount            : int   = x?MeshCount
        member x.DifferenceRendering  : bool  = x?DifferenceRendering
        member x.MinDifferenceDepth   : float = x?MinDifferenceDepth
        member x.MaxDifferenceDepth   : float = x?MaxDifferenceDepth
        member x.SliceIndex           : int   = x?SliceIndex
        member x.ClipMin              : V3d   = x?ClipMin
        member x.ClipMax              : V3d   = x?ClipMax
        member x.GhostSilhouette      : bool  = x?GhostSilhouette
        member x.MeshVisibilityMask   : int   = x?MeshVisibilityMask
        member x.IsGhost              : bool  = x?IsGhost
        member x.MeshIndex            : int   = x?MeshIndex
        member x.CoreRadius           : float = x?CoreRadius
        member x.GhostOpacity         : float = x?GhostOpacity
        member x.CylClip              : M44d  = x?CylClip
        member x.ReferenceAxis        : V3d   = x?ReferenceAxis
        member x.SteepnessThreshold   : float = x?SteepnessThreshold
        member x.DisagreementThreshold : float = x?DisagreementThreshold
        member x.HighlightColor       : V4d   = x?HighlightColor
        member x.HighlightAlpha       : float = x?HighlightAlpha
        member x.ExploreHighlightMode : int   = x?ExploreHighlightMode
    
    let colorMap =
        [|
            V4d(1.0,  1.0,  0.0,  1.0)   // yellow
            V4d(0.0,  0.85, 1.0,  1.0)   // cyan
            V4d(0.75, 0.1,  1.0,  1.0)   // violet
            V4d(1.0,  0.35, 0.0,  1.0)   // orange
            V4d(0.0,  1.0,  0.45, 1.0)   // spring green
        |]
    let clippy (v : Effects.Vertex) =
        fragment {
            let p = v.wp.XYZ / v.wp.W
            let worldNormal = v.n |> Vec.normalize
            let mutable insideClip =
                p.X >= uniform.ClipMin.X && p.X <= uniform.ClipMax.X &&
                p.Y >= uniform.ClipMin.Y && p.Y <= uniform.ClipMax.Y &&
                p.Z >= uniform.ClipMin.Z && p.Z <= uniform.ClipMax.Z
            let cyl = uniform.CylClip
            let mutable cylEdgeT = 1.0
            if cyl.M00 <> 0.0 then
                let anchor = V3d(cyl.M10, cyl.M11, cyl.M12)
                let axis = V3d(cyl.M20, cyl.M21, cyl.M22)
                let rel = p - anchor
                let axisProj = Vec.dot rel axis
                let radial = rel - axis * axisProj
                let radialDist = Vec.length radial
                let mutable insideCyl =
                    radialDist <= cyl.M01 &&
                    axisProj >= -cyl.M03 &&
                    axisProj <= cyl.M02
                let gradWidth = max 1.0e-4 (cyl.M01 * 0.08)
                cylEdgeT <- clamp 0.0 1.0 (abs (cyl.M01 - radialDist) / gradWidth)
                if cyl.M13 > 0.5 then
                    let cutNormal = V3d(cyl.M30, cyl.M31, cyl.M32)
                    let cutD = cyl.M23
                    let signedDist = Vec.dot p cutNormal - cutD
                    if signedDist > 0.0 then insideCyl <- false
                    cylEdgeT <- min cylEdgeT (clamp 0.0 1.0 (abs signedDist / gradWidth))
                insideClip <- insideClip && insideCyl
            let mutable color = v.c
            if not uniform.IsGhost then
                if not insideClip then
                    discard()
                let boxBDist =
                        min (min (abs (uniform.ClipMin.X - p.X)) (abs (uniform.ClipMax.X - p.X)))
                            (min (min (abs (uniform.ClipMin.Y - p.Y)) (abs (uniform.ClipMax.Y - p.Y)))
                                 (min (abs (uniform.ClipMin.Z - p.Z)) (abs (uniform.ClipMax.Z - p.Z))))
                let edgeT = min (clamp 0.0 1.0 boxBDist) cylEdgeT
                if edgeT < 1.0 then
                    color <- lerp colorMap.[uniform.MeshIndex%5] color edgeT
            else
                if insideClip then
                    discard()
                color <- V4d(colorMap.[uniform.MeshIndex%5].XYZ, uniform.GhostOpacity)

            return { c = color; n = V4d(worldNormal, 1.0) }
        }

    let coreClip (v : Effects.Vertex) =
        fragment {
            let p = v.wp.XYZ / v.wp.W
            let r = sqrt(p.X * p.X + p.Y * p.Y)
            if r > uniform.CoreRadius then
                discard()
            let mutable color = v.c
            let gradWidth = max 1.0e-4 (uniform.CoreRadius * 0.08)
            let edgeT = clamp 0.0 1.0 ((uniform.CoreRadius - r) / gradWidth)
            if edgeT < 1.0 then
                color <- lerp colorMap.[uniform.MeshIndex%5] color edgeT
            return color
        }

    let colon =
        sampler2dArray {
            texture uniform?ColorTexture
            filter Filter.MinMagPoint
            addressU WrapMode.Wrap
            addressV WrapMode.Wrap
        }
    let exploreSampler =
        sampler2d {
            texture uniform?ExploreTexture
            filter Filter.MinMagPoint
            addressU WrapMode.Wrap
            addressV WrapMode.Wrap
        }
    let deputy =
        sampler2dArray {
            texture uniform?DepthTexture
            filter Filter.MinMagPoint
            addressU WrapMode.Wrap
            addressV WrapMode.Wrap
        }
    let readArray (v : Effects.Vertex) =
        fragment {
            let ndc = v.pos.XY / v.pos.W

            let mutable maxDepth = -10.0
            let mutable minDepth = 1.0
            let mutable color = V4d.Zero
            let mutable index = -1

            for i in 0 .. uniform.MeshCount - 1 do
                let di = deputy.SampleLevel(v.tc, 2*i, 0.0).X
                let c = colon.SampleLevel(v.tc, 2*i, 0.0)
                if di < 1.0 then
                    let isVis = (uniform.MeshVisibilityMask >>> i) &&& 1 <> 0
                    if isVis then
                        maxDepth <- max di maxDepth
                        if di < minDepth then
                            minDepth <- di
                            color <- c
                            index <- i
                            
            let mutable ghostMinDepth = 1.0
            if uniform.GhostSilhouette then
                for i in 0 .. uniform.MeshCount - 1 do
                    let di = deputy.SampleLevel(v.tc, 2*i+1, 0.0).X
                    let c = colon.SampleLevel(v.tc, 2*i+1, 0.0)
                    if di < minDepth then
                        color.XYZ <- color.XYZ * (1.0 - c.W) + c.XYZ * c.W
                        color.W <- color.W * (1.0 - c.W) + c.W
                        if di < ghostMinDepth then ghostMinDepth <- di

            let a = uniform.ViewProjTrafoInv * V4d(ndc, 2.0 * minDepth - 1.0, 1.0)
            let b = uniform.ViewProjTrafoInv * V4d(ndc, 2.0 * maxDepth - 1.0, 1.0)
            let a = a.XYZ / a.W
            let b = b.XYZ / b.W
            let dist = Vec.length (a - b)
            if uniform.DifferenceRendering && dist > uniform.MinDifferenceDepth then
                let h = (dist - uniform.MinDifferenceDepth) / uniform.MaxDifferenceDepth |> float32 |> Heat.heat |> V4d
                color <- h * color

            let outDepth = min minDepth ghostMinDepth
            let eCol = exploreSampler.SampleLevel(v.tc, 0.0)
            if eCol.W > 0.001 then
                color.XYZ <- color.XYZ * (1.0 - eCol.W) + eCol.XYZ * eCol.W
                color.W <- color.W * (1.0 - eCol.W) + eCol.W
            if outDepth >= 0.9999 && color.W < 0.001 then discard()

            return { c = color; d = outDepth }
        }
        
    [<ReflectedDefinition>]
    let private reconstructWorld (ndc : V2d) (depth : float) =
        let clip = V4d(ndc, 2.0 * depth - 1.0, 1.0)
        let w = uniform.ViewProjTrafoInv * clip
        w.XYZ / w.W

    // ExploreHighlightMode: 0 = SteepnessOnly, 1 = DisagreementOnly, 2 = Combined
    // Steepness uses screen-space normals (needs neighbor depth samples).
    // Disagreement uses axis-projected world depth (center pixel only — view-independent).
    // Combined: disagreement over ALL meshes, qualified by steepCount >= 1.
    let exploreHeatmap (v : Effects.Vertex) =
        fragment {
            let ndc = 2.0 * v.tc - V2d.II
            let vpSize = V2d(float uniform.ViewportSize.X, float uniform.ViewportSize.Y)
            let dx = V2d(2.0 / vpSize.X, 0.0)
            let dy = V2d(0.0, 2.0 / vpSize.Y)
            let tcx = v.tc + V2d(1.0 / vpSize.X, 0.0)
            let tcy = v.tc + V2d(0.0, 1.0 / vpSize.Y)
            let mode = uniform.ExploreHighlightMode
            let mutable steepCount = 0
            let mutable count = 0
            let mutable mean = 0.0
            let mutable s2 = 0.0
            for i in 0 .. uniform.MeshCount - 1 do
                let isVis = (uniform.MeshVisibilityMask >>> i) &&& 1 <> 0
                if isVis then
                    let di = deputy.SampleLevel(v.tc, 2*i, 0.0).X
                    if di < 0.9999 then
                        let p = reconstructWorld ndc di
                        // Disagreement: accumulate axis-projected depth for all meshes (modes 1, 2)
                        if mode <> 0 then
                            let depth = Vec.dot p uniform.ReferenceAxis
                            count <- count + 1
                            let delta = depth - mean
                            mean <- mean + delta / float count
                            let delta2 = depth - mean
                            s2 <- s2 + delta * delta2
                        // Steepness: reconstruct normal from depth derivatives (modes 0, 2)
                        if mode <> 1 then
                            let dix = deputy.SampleLevel(tcx, 2*i, 0.0).X
                            let diy = deputy.SampleLevel(tcy, 2*i, 0.0).X
                            if dix < 0.9999 && diy < 0.9999 then
                                let px = reconstructWorld (ndc + dx) dix
                                let py = reconstructWorld (ndc + dy) diy
                                let n = Vec.cross (px - p) (py - p) |> Vec.normalize
                                let alignment = abs (Vec.dot n uniform.ReferenceAxis)
                                if alignment < uniform.SteepnessThreshold then
                                    steepCount <- steepCount + 1
            // Determine alpha before pattern
            let mutable alpha = 0.0
            if mode = 0 then
                if steepCount < 1 then discard()
                alpha <- uniform.HighlightAlpha
            elif mode = 1 then
                if count < 2 then discard()
                let stddev = sqrt (s2 / float count)
                if stddev < uniform.DisagreementThreshold then discard()
                alpha <- clamp 0.3 1.0 (stddev / (uniform.DisagreementThreshold * 3.0)) * uniform.HighlightAlpha
            else
                if steepCount < 1 || count < 2 then discard()
                let stddev = sqrt (s2 / float count)
                if stddev < uniform.DisagreementThreshold then discard()
                alpha <- clamp 0.3 1.0 (stddev / (uniform.DisagreementThreshold * 3.0)) * uniform.HighlightAlpha
            // Dot grid pattern (screen-space, non-directional)
            let pixel = v.tc * vpSize
            let cell = 8.0
            let cx = (pixel.X % cell) - cell * 0.5
            let cy = (pixel.Y % cell) - cell * 0.5
            let dist = sqrt(cx * cx + cy * cy)
            if dist > cell * 0.38 then discard()
            let dotFade = clamp 0.0 1.0 (1.0 - dist / (cell * 0.38))
            let finalAlpha = alpha * (0.5 + 0.5 * dotFade)
            return V4d(uniform.HighlightColor.XYZ, finalAlpha)
        }

    let readArraySlice (v : Effects.Vertex) =
        fragment {
            let i = uniform.SliceIndex
            return { c = colon.SampleLevel(v.tc, i, 0.0)
                     d = deputy.SampleLevel(v.tc, i, 0.0).X }
        }

    let readArraySliceColor (v : Effects.Vertex) =
        fragment {
            let ndc = 2.0 * v.tc - V2d.II
            let r2 = Vec.lengthSquared ndc
            if r2 > 1.0 then discard()
            let r = sqrt r2
            if r > 0.93 then
                return uniform?BorderColor
            else
            return colon.SampleLevel(uniform.TextureOffset + uniform.TextureScale * v.tc, uniform.SliceIndex, 0.0)
        }

module Shader =
    open FShade
    open BlitShader

    type UniformScope with
        member x.FlatColor : V4d = x?FlatColor
        member x.ColorMode : int = x?ColorMode
        member x.Opacity : float = x?Opacity

    let falseColorMap =
        [|
            V4d(0.20, 0.40, 0.65, 1.0)
            V4d(0.55, 0.65, 0.30, 1.0)
            V4d(0.75, 0.55, 0.30, 1.0)
            V4d(0.40, 0.55, 0.70, 1.0)
            V4d(0.60, 0.40, 0.55, 1.0)
            V4d(0.35, 0.55, 0.55, 1.0)
        |]

    let headlight (v : Effects.Vertex) =
        fragment {
            let mutable c = v.c
            if uniform.ColorMode = 1 then
                let n = v.n |> Vec.normalize
                let toCam = uniform.CameraLocation - v.wp.XYZ |> Vec.normalize
                let ndl = max 0.15 (abs (Vec.dot n toCam))
                let baseC = falseColorMap.[uniform.MeshIndex % 6]
                c <- V4d(baseC.XYZ * ndl, c.W)
            elif uniform.ColorMode = 2 then
                let n = v.n |> Vec.normalize
                let toCam = uniform.CameraLocation - v.wp.XYZ |> Vec.normalize
                let ndl = max 0.25 (abs (Vec.dot n toCam))
                c <- V4d(ndl, ndl, ndl, c.W)
            return c
        }

    let flatColor (_v : Effects.Vertex) =
        fragment { return uniform.FlatColor }

    let vertexColor (v : Effects.Vertex) =
        fragment { return v.c }

    let nothing (v : Effects.Vertex) =
        fragment {
            return v.c
        }

    let applyOpacity (v : Effects.Vertex) =
        fragment {
            let c = v.c
            return V4d(c.X, c.Y, c.Z, c.W * uniform.Opacity)
        }

    type Fragment =
        {
            [<Semantic("PickViewPosition")>] vp : V3d
        }

    let withViewPos (v : Effects.Vertex) =
        fragment {
            let vp = uniform.ProjTrafoInv * v.pos
            let vp = vp.XYZ / vp.W
            let vp = vp + V3d(0.1, 0.0, 0.0)
            return { vp = vp.XYZ }
        }

module Lines =
    open Aardvark.Dom
    open FSharp.Data.Adaptive

    [<ReflectedDefinition>]
    module LineShader =
        open FShade

        type Vertex = {
            [<Semantic("P0")>]        p0 : V3d
            [<Semantic("P1")>]        p1 : V3d
            [<Semantic("LineColor")>] color : V4d
            [<Semantic("LineWidth")>] width : float
            [<Position>]              pos : V4d
            [<Color>]                 col : V4d
            [<VertexId>]              id : int
        }

        // Liang-Barsky clip of one segment against one frustum plane.
        // Returns the updated (t0, t1) interval; outside-callers compose 6 of these.
        let clipPlane (o : V3d) (d : V3d) (plane : V4d) (t0 : float) (t1 : float) =
            let dir = Vec.dot plane.XYZ d
            let t   = (plane.W + Vec.dot plane.XYZ o) / -dir
            let mutable a = t0
            let mutable b = t1
            if dir > 1E-9 then
                if t < b then b <- t
            elif dir < -1E-9 then
                if t > a then a <- t
            V2d(a, b)

        let line (v : Vertex) =
            vertex {
                let m = uniform.ModelViewProjTrafo
                let o = v.p0
                let d = v.p1 - v.p0
                let mutable tt = V2d(0.0, 1.0)
                tt <- clipPlane o d (-m.R3 - m.R0) tt.X tt.Y
                tt <- clipPlane o d (-m.R3 + m.R0) tt.X tt.Y
                tt <- clipPlane o d (-m.R3 - m.R1) tt.X tt.Y
                tt <- clipPlane o d (-m.R3 + m.R1) tt.X tt.Y
                tt <- clipPlane o d (-m.R3 - m.R2) tt.X tt.Y
                tt <- clipPlane o d (-m.R3 + m.R2) tt.X tt.Y

                if tt.Y > tt.X then
                    let p0w = o + tt.X * d
                    let p1w = o + tt.Y * d

                    // Each segment quad has 4 vertices; id % 4 selects the corner.
                    // bit 0 = perpendicular side (− or +), bit 1 = endpoint (p0 or p1)
                    let corner = v.id % 4
                    let mpX = if corner &&& 1 <> 0 then 1.0 else 0.0
                    let mpY = if corner &&& 2 <> 0 then 1.0 else 0.0

                    let vs   = uniform.ViewportSize
                    let p0c  = m * V4d(p0w, 1.0)
                    let p1c  = m * V4d(p1w, 1.0)
                    let p0n  = p0c.XYZ / p0c.W
                    let p1n  = p1c.XYZ / p1c.W

                    let pixelToNdc    = V2d(2.0 / float vs.X, 2.0 / float vs.Y)
                    let halfWidthPx   = v.width * 0.5

                    let diff     = p1n - p0n
                    let pixelDir = V2d(diff.X * float vs.X * 0.5, diff.Y * float vs.Y * 0.5)
                    let pixelLen = Vec.length pixelDir

                    let perpDir =
                        if pixelLen > 1e-10 then V2d(-pixelDir.Y, pixelDir.X) / pixelLen
                        else V2d(0.0, 1.0)
                    let lineDir =
                        if pixelLen > 1e-10 then pixelDir / pixelLen
                        else V2d(0.0, 1.0)

                    let perpSign = if mpX > 0.5 then 1.0 else -1.0
                    let lineSign = if mpY > 0.5 then 1.0 else -1.0
                    let perpOffset = perpDir * (perpSign * halfWidthPx) * pixelToNdc
                    let lineOffset = lineDir * (lineSign * halfWidthPx) * pixelToNdc

                    let basePos = if mpY > 0.5 then p1n.XY else p0n.XY
                    let xy      = basePos + perpOffset + lineOffset

                    let zT = if mpY > 0.5 then 1.0 else 0.0
                    let z  = p0n.Z * (1.0 - zT) + p1n.Z * zT

                    return { v with pos = V4d(xy.X, xy.Y, z, 1.0); col = v.color }
                else
                    return { v with pos = V4d(2.0, 2.0, 2.0, 1.0); col = V4d.Zero }
            }

        let fragment (v : Vertex) =
            fragment { return v.col }

    /// Render line segments as screen-space-constant-width quads.
    /// Each segment is `(p0, p1, colorRgba01, widthPixels)`; width is in CSS pixels
    /// at the current viewport resolution. Non-instanced — 4 vertices per segment.
    let render (segments : aval<(V3d * V3d * V4d * float)[]>) =
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
        sg {
            Sg.Shader { LineShader.line; LineShader.fragment }
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
