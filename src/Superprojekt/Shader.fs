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
        member x.CylClipActive        : int   = x?CylClipActive
        member x.CylAnchor            : V3d   = x?CylAnchor
        member x.CylAxis              : V3d   = x?CylAxis
        member x.CylRadius            : float = x?CylRadius
        member x.CylExtFwd            : float = x?CylExtFwd
        member x.CylExtBack           : float = x?CylExtBack
        member x.ExplosionOffset      : V3d   = x?ExplosionOffset
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
    let explode (v : Effects.Vertex) =
        vertex {
            let p = v.wp.XYZ / v.wp.W
            let rel = p - uniform.CylAnchor
            let axisProj = Vec.dot rel uniform.CylAxis
            let radial = rel - uniform.CylAxis * axisProj
            let inside =
                Vec.length radial <= uniform.CylRadius &&
                axisProj >= -uniform.CylExtBack &&
                axisProj <= uniform.CylExtFwd
            let mutable wp = v.wp
            if inside then
                wp <- V4d(p + uniform.ExplosionOffset, 1.0)
            return {
                v with
                    wp = wp
                    pos = uniform.ViewProjTrafo * wp
            }
        }

    let clippy (v : Effects.Vertex) =
        fragment {
            let p = v.wp.XYZ / v.wp.W
            let mutable insideClip =
                p.X >= uniform.ClipMin.X && p.X <= uniform.ClipMax.X &&
                p.Y >= uniform.ClipMin.Y && p.Y <= uniform.ClipMax.Y &&
                p.Z >= uniform.ClipMin.Z && p.Z <= uniform.ClipMax.Z
            if uniform.CylClipActive <> 0 then
                let rel = p - uniform.CylAnchor
                let axisProj = Vec.dot rel uniform.CylAxis
                let radial = rel - uniform.CylAxis * axisProj
                let radialDist = Vec.length radial
                let insideCyl =
                    radialDist <= uniform.CylRadius &&
                    axisProj >= -uniform.CylExtBack &&
                    axisProj <= uniform.CylExtFwd
                insideClip <- insideClip && insideCyl
            let mutable color = v.c
            if not uniform.IsGhost then
                if not insideClip then
                    discard()
                let bdist =
                        min (min (abs (uniform.ClipMin.X - p.X)) (abs (uniform.ClipMax.X - p.X)))
                            (min (min (abs (uniform.ClipMin.Y - p.Y)) (abs (uniform.ClipMax.Y - p.Y)))
                                 (min (abs (uniform.ClipMin.Z - p.Z)) (abs (uniform.ClipMax.Z - p.Z))))
                if bdist < 1.0 then
                    color <- lerp colorMap.[uniform.MeshIndex%5] color bdist
            else
                if insideClip then
                    discard()
                color <- V4d(colorMap.[uniform.MeshIndex%5].XYZ, uniform.GhostOpacity)

            return color
        }

    let coreClip (v : Effects.Vertex) =
        fragment {
            let p = v.wp.XYZ / v.wp.W
            let r = sqrt(p.X * p.X + p.Y * p.Y)
            if r > uniform.CoreRadius then
                discard()
            let mutable color = v.c
            let bdist = uniform.CoreRadius - r
            if bdist < 1.0 then
                color <- lerp colorMap.[uniform.MeshIndex%5] color bdist
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
            if Vec.lengthSquared ndc > 1.0 then discard()
            return colon.SampleLevel(uniform.TextureOffset + uniform.TextureScale * v.tc, uniform.SliceIndex, 0.0)
        }

module Shader =
    open FShade
    open BlitShader

    type UniformScope with
        member x.FlatColor : V4d = x?FlatColor
        member x.DepthShadeOn : int = x?DepthShadeOn
        member x.IsolinesOn : int = x?IsolinesOn
        member x.IsolineSpacing : float = x?IsolineSpacing
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
            if uniform.ColorMode <> 0 then
                let n = v.n |> Vec.normalize
                let toCam = uniform.CameraLocation - v.wp.XYZ |> Vec.normalize
                let ndl = max 0.15 (abs (Vec.dot n toCam))
                let baseC = falseColorMap.[uniform.MeshIndex % 6]
                c <- V4d(baseC.XYZ * ndl, c.W)
            return c
        }

    let flatColor (_v : Effects.Vertex) =
        fragment { return uniform.FlatColor }

    let vertexColor (v : Effects.Vertex) =
        fragment { return v.c }

    let depthShade (v : Effects.Vertex) =
        fragment {
            let mutable c = v.c
            if uniform.DepthShadeOn <> 0 then
                let clip = uniform.ModelViewProjTrafo * v.wp
                let ndcZ = clip.Z / clip.W
                let t = clamp 0.0 1.0 ((ndcZ + 1.0) * 0.5)
                let shade = 0.05 + (1.0 - t) * 0.95
                c <- V4d(c.X * shade, c.Y * shade, c.Z * shade, c.W)
            return c
        }

    let isolines (v : Effects.Vertex) =
        fragment {
            let mutable c = v.c
            if uniform.IsolinesOn <> 0 then
                let p = v.wp.XYZ / v.wp.W
                let spacing = uniform.IsolineSpacing
                let phase = p.Z / spacing
                let f = phase - floor phase
                let dist = (min f (1.0 - f)) * spacing
                if dist < 0.06 then
                    c <- V4d(1.0, 1.0, 1.0, c.W)
            return c
        }

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
