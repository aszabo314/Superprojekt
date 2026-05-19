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

    [<Literal>]
    let MaxLassoPlanes = 32

    [<Literal>]
    let MaxProvenanceMeshes = 16
    [<Literal>]
    let MaxProvenanceAnchors = 32

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
        member x.GhostOpacity         : float = x?GhostOpacity
        member x.CylClip              : M44d  = x?CylClip
        member x.ReferenceAxis        : V3d   = x?ReferenceAxis
        member x.FcEnabled            : int   = x?FcEnabled
        member x.DgEnabled            : int   = x?DgEnabled
        member x.FcThreshold          : float = x?FcThreshold
        member x.DgThreshold          : float = x?DgThreshold
        member x.FcColor              : V4d   = x?FcColor
        member x.DgColor              : V4d   = x?DgColor
        member x.MixModeInt           : int   = x?MixModeInt
        member x.ExploreTime          : float = x?ExploreTime
        member x.HighlightAlpha       : float = x?HighlightAlpha
        member x.GhostDetailMode      : int   = x?GhostDetailMode
        member x.LassoPlaneCount      : int   = x?LassoPlaneCount
        member x.LassoPlanes          : Arr<N<32>, V4d> = x?LassoPlanes
        member x.ProvenanceEnabled    : int   = x?ProvenanceEnabled
        member x.ProvenanceThreshold  : float = x?ProvenanceThreshold
        member x.FalloffZoneOnly      : int   = x?FalloffZoneOnly
        member x.ProvenanceDataset    : Arr<N<16>, float> = x?ProvenanceDataset
        member x.ProvenanceAlgorithm  : Arr<N<16>, float> = x?ProvenanceAlgorithm
        member x.ProvenanceAnchorCount: int   = x?ProvenanceAnchorCount
        member x.ProvenanceAnchors    : Arr<N<32>, V4d> = x?ProvenanceAnchors
        member x.FusionMode           : int   = x?FusionMode

    let colorMap =
        [|
            V4d(1.0,  1.0,  0.0,  1.0)
            V4d(0.0,  0.85, 1.0,  1.0)
            V4d(0.75, 0.1,  1.0,  1.0)
            V4d(1.0,  0.35, 0.0,  1.0)
            V4d(0.0,  1.0,  0.45, 1.0)
        |]
    let clippy (v : Effects.Vertex) =
        fragment {
            let p = v.wp.XYZ / v.wp.W
            let worldNormal = v.n |> Vec.normalize
            let mutable insideClip =
                p.X >= uniform.ClipMin.X && p.X <= uniform.ClipMax.X &&
                p.Y >= uniform.ClipMin.Y && p.Y <= uniform.ClipMax.Y &&
                p.Z >= uniform.ClipMin.Z && p.Z <= uniform.ClipMax.Z
            let lc = uniform.LassoPlaneCount
            if lc > 0 then
                let mutable insideLasso = true
                for i in 0 .. 31 do
                    if i < lc then
                        let plane = uniform.LassoPlanes.[i]
                        let d = plane.X * p.X + plane.Y * p.Y + plane.Z * p.Z + plane.W
                        if d > 0.0 then insideLasso <- false
                if not insideLasso then insideClip <- false
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

            let fusion = uniform.FusionMode <> 0
            let mutable bestErr = 1e9

            for i in 0 .. uniform.MeshCount - 1 do
                let di = deputy.SampleLevel(v.tc, 2*i, 0.0).X
                let c = colon.SampleLevel(v.tc, 2*i, 0.0)
                if di < 1.0 then
                    let isVis = (uniform.MeshVisibilityMask >>> i) &&& 1 <> 0
                    if isVis then
                        maxDepth <- max di maxDepth
                        if fusion then
                            let mutable density = 0.0
                            let clipP = V4d(ndc, 2.0 * di - 1.0, 1.0)
                            let worldP4 = uniform.ViewProjTrafoInv * clipP
                            let pW = worldP4.XYZ / worldP4.W
                            for ai in 0 .. 31 do
                                if ai < uniform.ProvenanceAnchorCount then
                                    let a = uniform.ProvenanceAnchors.[ai]
                                    let sigma = a.W
                                    if sigma > 1e-6 then
                                        let d2 =
                                            (pW.X - a.X) * (pW.X - a.X)
                                            + (pW.Y - a.Y) * (pW.Y - a.Y)
                                            + (pW.Z - a.Z) * (pW.Z - a.Z)
                                        let w = exp (-d2 / (2.0 * sigma * sigma))
                                        if w > 0.05 then density <- density + w
                            let cond =
                                if density > 1e-6 then 1.0 / (density + 1e-3)
                                else 1e3
                            let dErr =
                                if i < 16 then uniform.ProvenanceDataset.[i] else 0.0
                            let aErr =
                                if i < 16 then uniform.ProvenanceAlgorithm.[i] else 0.0
                            let total = dErr + aErr + cond * 0.01
                            if total < bestErr then
                                bestErr <- total
                                minDepth <- di
                                color <- c
                                index <- i
                        else
                            if di < minDepth then
                                minDepth <- di
                                color <- c
                                index <- i

            let mutable ghostMinDepth = 1.0
            let mutable ghostWinner = -1
            if uniform.GhostSilhouette then
                for i in 0 .. uniform.MeshCount - 1 do
                    let di = deputy.SampleLevel(v.tc, 2*i+1, 0.0).X
                    let c = colon.SampleLevel(v.tc, 2*i+1, 0.0)
                    if di < minDepth then
                        color.XYZ <- color.XYZ * (1.0 - c.W) + c.XYZ * c.W
                        color.W <- color.W * (1.0 - c.W) + c.W
                        if di < ghostMinDepth then
                            ghostMinDepth <- di
                            ghostWinner <- i

            if uniform.GhostSilhouette && uniform.GhostDetailMode > 0 && ghostWinner >= 0 then
                let vp = V2d(float uniform.ViewportSize.X, float uniform.ViewportSize.Y)
                let pxX = 1.0 / vp.X
                let pxY = 1.0 / vp.Y
                let tcxp = v.tc + V2d(pxX, 0.0)
                let tcyp = v.tc + V2d(0.0, pxY)
                let tcxm = v.tc - V2d(pxX, 0.0)
                let tcym = v.tc - V2d(0.0, pxY)
                let slice = 2 * ghostWinner + 1
                let dC = ghostMinDepth
                let dXp = deputy.SampleLevel(tcxp, slice, 0.0).X
                let dXm = deputy.SampleLevel(tcxm, slice, 0.0).X
                let dYp = deputy.SampleLevel(tcyp, slice, 0.0).X
                let dYm = deputy.SampleLevel(tcym, slice, 0.0).X
                if dXp < 0.9999 && dXm < 0.9999 && dYp < 0.9999 && dYm < 0.9999 then
                    let lap = abs (dXp + dXm + dYp + dYm - 4.0 * dC) / max 1e-4 dC
                    let curv = clamp 0.0 1.0 (lap * 1500.0)
                    let widened =
                        if uniform.GhostDetailMode > 1 then
                            clamp 0.0 1.0 (curv * 2.0 - 0.3)
                        else curv
                    let tintLo = V3d(0.15, 0.39, 0.92)
                    let tintHi = V3d(0.98, 0.45, 0.09)
                    let tint = tintLo * (1.0 - widened) + tintHi * widened
                    let mix = 0.35
                    color.XYZ <- color.XYZ * (1.0 - mix) + tint * mix

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

            if uniform.ProvenanceEnabled <> 0 && index >= 0 && index < 16 then
                let ndcProv = v.pos.XY / v.pos.W
                let clipP = V4d(ndcProv, 2.0 * minDepth - 1.0, 1.0)
                let worldP = uniform.ViewProjTrafoInv * clipP
                let p = worldP.XYZ / worldP.W
                let dErr = uniform.ProvenanceDataset.[index]
                let aErr = uniform.ProvenanceAlgorithm.[index]
                let mutable density = 0.0
                let mutable totalWeight = 0.0
                for i in 0 .. 31 do
                    if i < uniform.ProvenanceAnchorCount then
                        let a = uniform.ProvenanceAnchors.[i]
                        let sigma = a.W
                        if sigma > 1e-6 then
                            let d2 = (p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y) + (p.Z - a.Z) * (p.Z - a.Z)
                            let w = exp (-d2 / (2.0 * sigma * sigma))
                            if w > 0.05 then
                                density <- density + w
                                totalWeight <- max totalWeight w
                let cond =
                    if density > 1e-6 then 1.0 / (density + 1e-3)
                    else 1e3
                let cScaled = cond * 0.01
                let total = dErr + aErr + cScaled
                let zoneOk = uniform.FalloffZoneOnly = 0 || totalWeight > 0.05
                if total > uniform.ProvenanceThreshold && zoneOk then
                    let mutable provCol = V3d.Zero
                    if dErr >= aErr && dErr >= cScaled then provCol <- V3d(0.95, 0.30, 0.30)
                    elif aErr >= cScaled then provCol <- V3d(0.30, 0.80, 0.40)
                    else provCol <- V3d(0.35, 0.50, 0.95)
                    let alpha = 0.55
                    color.XYZ <- color.XYZ * (1.0 - alpha) + provCol * alpha
                    color.W <- max color.W alpha

            if outDepth >= 0.9999 && color.W < 0.001 then discard()

            return { c = color; d = outDepth }
        }

    [<ReflectedDefinition>]
    let private reconstructWorld (ndc : V2d) (depth : float) =
        let clip = V4d(ndc, 2.0 * depth - 1.0, 1.0)
        let w = uniform.ViewProjTrafoInv * clip
        w.XYZ / w.W

    let exploreHeatmap (v : Effects.Vertex) =
        fragment {
            let ndc = 2.0 * v.tc - V2d.II
            let vpSize = V2d(float uniform.ViewportSize.X, float uniform.ViewportSize.Y)
            let pxX = 1.0 / vpSize.X
            let pxY = 1.0 / vpSize.Y
            let dxNdc = V2d(2.0 * pxX, 0.0)
            let dyNdc = V2d(0.0, 2.0 * pxY)
            let tcx = v.tc + V2d(pxX, 0.0)
            let tcy = v.tc + V2d(0.0, pxY)
            let tcxm = v.tc - V2d(pxX, 0.0)
            let tcym = v.tc - V2d(0.0, pxY)
            let fcOn = uniform.FcEnabled <> 0
            let dgOn = uniform.DgEnabled <> 0

            let mutable dgCount = 0
            let mutable dgMean  = 0.0
            let mutable dgS2    = 0.0
            let mutable bestI = -1
            let mutable bestDepth = 1.0
            for i in 0 .. uniform.MeshCount - 1 do
                let isVis = (uniform.MeshVisibilityMask >>> i) &&& 1 <> 0
                if isVis then
                    let di = deputy.SampleLevel(v.tc, 2 * i, 0.0).X
                    if di < 0.9999 then
                        if dgOn then
                            let p = reconstructWorld ndc di
                            let depth = Vec.dot p uniform.ReferenceAxis
                            dgCount <- dgCount + 1
                            let delta = depth - dgMean
                            dgMean <- dgMean + delta / float dgCount
                            dgS2 <- dgS2 + delta * (depth - dgMean)
                        if di < bestDepth then
                            bestDepth <- di
                            bestI <- i

            let mutable fcScore = 0.0
            if fcOn && bestI >= 0 then
                let p = reconstructWorld ndc bestDepth
                let dxp = deputy.SampleLevel(tcx,  2 * bestI, 0.0).X
                let dxm = deputy.SampleLevel(tcxm, 2 * bestI, 0.0).X
                let dyp = deputy.SampleLevel(tcy,  2 * bestI, 0.0).X
                let dym = deputy.SampleLevel(tcym, 2 * bestI, 0.0).X
                if dxp < 0.9999 && dxm < 0.9999 && dyp < 0.9999 && dym < 0.9999 then
                    let pxp = reconstructWorld (ndc + dxNdc) dxp
                    let pxm = reconstructWorld (ndc - dxNdc) dxm
                    let pyp = reconstructWorld (ndc + dyNdc) dyp
                    let pym = reconstructWorld (ndc - dyNdc) dym
                    let nC  = Vec.cross (pxp - p)   (pyp - p)   |> Vec.normalize
                    let nL  = Vec.cross (p   - pxm) (pyp - pxm) |> Vec.normalize
                    let nR  = Vec.cross (pxp - pxm) (pyp - pxp) |> Vec.normalize
                    let nU  = Vec.cross (pxp - pym) (p   - pym) |> Vec.normalize
                    let nD  = Vec.cross (pxp - p)   (pym - p)   |> Vec.normalize
                    let curv =
                        let s =
                            (1.0 - max 0.0 (Vec.dot nC nL))
                            + (1.0 - max 0.0 (Vec.dot nC nR))
                            + (1.0 - max 0.0 (Vec.dot nC nU))
                            + (1.0 - max 0.0 (Vec.dot nC nD))
                        clamp 0.0 1.0 (s * 0.5)
                    let steep = 1.0 - abs (Vec.dot nC uniform.ReferenceAxis)
                    fcScore <- curv * steep

            let fcInt =
                if fcOn && fcScore > uniform.FcThreshold then
                    clamp 0.0 1.0 ((fcScore - uniform.FcThreshold) / max 1e-3 (1.0 - uniform.FcThreshold))
                else 0.0
            let dgInt =
                if dgOn && dgCount >= 2 then
                    let stddev = sqrt (dgS2 / float dgCount)
                    if stddev > uniform.DgThreshold then
                        clamp 0.0 1.0 ((stddev - uniform.DgThreshold) / (uniform.DgThreshold * 3.0))
                    else 0.0
                else 0.0

            if fcInt <= 0.0 && dgInt <= 0.0 then discard()

            let pixel = v.tc * vpSize
            let cell = 8.0
            let cx = (pixel.X % cell) - cell * 0.5
            let cy = (pixel.Y % cell) - cell * 0.5
            let dist = sqrt(cx * cx + cy * cy)
            if dist > cell * 0.38 then discard()
            let dotFade = clamp 0.0 1.0 (1.0 - dist / (cell * 0.38))

            let mutable col = V3d.Zero
            let mutable alpha = 0.0
            if fcInt > 0.0 && dgInt <= 0.0 then
                col <- uniform.FcColor.XYZ
                alpha <- uniform.HighlightAlpha * (0.3 + 0.7 * fcInt)
            elif dgInt > 0.0 && fcInt <= 0.0 then
                col <- uniform.DgColor.XYZ
                alpha <- uniform.HighlightAlpha * (0.3 + 0.7 * dgInt)
            else
                let m = uniform.MixModeInt
                if m = 0 then
                    let stripe = floor (pixel.X / 8.0) % 2.0
                    if stripe < 0.5 then
                        col <- uniform.FcColor.XYZ
                        alpha <- uniform.HighlightAlpha * (0.3 + 0.7 * fcInt)
                    else
                        col <- uniform.DgColor.XYZ
                        alpha <- uniform.HighlightAlpha * (0.3 + 0.7 * dgInt)
                elif m = 2 then
                    let phase = floor (uniform.ExploreTime * 1.5) % 2.0
                    if phase < 0.5 then
                        col <- uniform.FcColor.XYZ
                        alpha <- uniform.HighlightAlpha * (0.3 + 0.7 * fcInt)
                    else
                        col <- uniform.DgColor.XYZ
                        alpha <- uniform.HighlightAlpha * (0.3 + 0.7 * dgInt)
                else
                    let w1 = fcInt
                    let w2 = dgInt
                    let wSum = max 1e-3 (w1 + w2)
                    col <- (uniform.FcColor.XYZ * w1 + uniform.DgColor.XYZ * w2) / wSum
                    alpha <- uniform.HighlightAlpha * (0.3 + 0.7 * max fcInt dgInt)

            let finalAlpha = alpha * (0.5 + 0.5 * dotFade)
            return V4d(col, finalAlpha)
        }

    let readArraySlice (v : Effects.Vertex) =
        fragment {
            let i = uniform.SliceIndex
            return { c = colon.SampleLevel(v.tc, i, 0.0)
                     d = deputy.SampleLevel(v.tc, i, 0.0).X }
        }

