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

    /// V6 §D.3 — fixed-size cap on lasso polygon vertex count. The polygon
    /// produces one half-plane per edge; 32 covers the planetary expert's
    /// hand-drawn polygons with plenty of headroom.
    [<Literal>]
    let MaxLassoPlanes = 32

    /// V6 §D.9 — fixed-size mesh-uniform arrays for the error provenance
    /// heatmap. Phase 7 ships with 16 meshes; the existing scenes never
    /// exceed 8. Anchors cap at 32 — the lasso uses the same cap, and
    /// 32 anchors is well beyond what a user places manually.
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
        // V6 §D.4 — dual-signal explore uniforms. Per-signal enable
        // flags + thresholds + colours; MixModeInt 0=SideBySide,
        // 1=Blended, 2=Alternating; ExploreTime is wall-clock seconds.
        member x.FcEnabled            : int   = x?FcEnabled
        member x.DgEnabled            : int   = x?DgEnabled
        member x.FcThreshold          : float = x?FcThreshold
        member x.DgThreshold          : float = x?DgThreshold
        member x.FcColor              : V4d   = x?FcColor
        member x.DgColor              : V4d   = x?DgColor
        member x.MixModeInt           : int   = x?MixModeInt
        member x.ExploreTime          : float = x?ExploreTime
        member x.HighlightAlpha       : float = x?HighlightAlpha
        // V6 §D.2 — ghost detail mode: 0=Outline 1=+Curvature 2=+Terrain.
        member x.GhostDetailMode      : int   = x?GhostDetailMode
        member x.LassoPlaneCount      : int   = x?LassoPlaneCount
        member x.LassoPlanes          : Arr<N<32>, V4d> = x?LassoPlanes
        // V6 §D.9 — error provenance heatmap uniforms.
        member x.ProvenanceEnabled    : int   = x?ProvenanceEnabled
        member x.ProvenanceThreshold  : float = x?ProvenanceThreshold
        member x.FalloffZoneOnly      : int   = x?FalloffZoneOnly
        member x.ProvenanceDataset    : Arr<N<16>, float> = x?ProvenanceDataset
        member x.ProvenanceAlgorithm  : Arr<N<16>, float> = x?ProvenanceAlgorithm
        member x.ProvenanceAnchorCount: int   = x?ProvenanceAnchorCount
        member x.ProvenanceAnchors    : Arr<N<32>, V4d> = x?ProvenanceAnchors
        // V6 §D.10 — fusion mesh
        member x.FusionMode           : int   = x?FusionMode
    
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
            // V6 §D.3 — lasso sweep volume. Each half-plane contains the
            // commit-time camera position; fragments with a positive signed
            // distance against any plane lie outside the cone.
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

            // V6 §D.10 — Fusion mode selects per-pixel the mesh with the
            // minimum total error (w_d * dataset + w_a * algo + w_c * cond)
            // rather than the front-most. Depth + colour come from the
            // chosen winner so the rendered surface stays geometrically
            // coherent at that pixel.
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
                            // Local conditioning approximated by density from
                            // anchor list, same heuristic as the heatmap.
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
                            // Weights chosen to keep the three signals on
                            // roughly comparable footing on Mars Kodiak.
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

            // V6 §D.2 — ghost detail "+ Curvature" / "+ Terrain features".
            // Modulates the just-composited ghost colour with a screen-
            // space curvature term from the front-most ghost slice. Cool
            // (blue) for low curvature, warm (orange-red) for high.
            // Terrain features (mode 2) additionally widens the high-band
            // so ridges read as a thin bright crest — a cheap surrogate
            // for the spec's "ridge/valley polyline rasterisation".
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
                    // Depth Laplacian normalised by centre depth — a cheap
                    // proxy for surface curvature in screen space.
                    let lap = abs (dXp + dXm + dYp + dYm - 4.0 * dC) / max 1e-4 dC
                    let curv = clamp 0.0 1.0 (lap * 1500.0)
                    let widened =
                        if uniform.GhostDetailMode > 1 then
                            // Terrain features: emphasise the high band so
                            // ridges pop. Curve = clamp(curv * 2 - 0.3).
                            clamp 0.0 1.0 (curv * 2.0 - 0.3)
                        else curv
                    // Cool→warm gradient: low = #2563eb (blue), high = #f97316 (orange).
                    let tintLo = V3d(0.15, 0.39, 0.92)
                    let tintHi = V3d(0.98, 0.45, 0.09)
                    let tint = tintLo * (1.0 - widened) + tintHi * widened
                    let mix = 0.35  // faint per spec
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

            // V6 §D.9 — error provenance heatmap. When enabled, override
            // colour by the dominant source's hue if the per-pixel total
            // error exceeds the threshold and (if FalloffZoneOnly is on)
            // the pixel falls inside at least one anchor's falloff zone.
            if uniform.ProvenanceEnabled <> 0 && index >= 0 && index < 16 then
                let ndcProv = v.pos.XY / v.pos.W
                let clipP = V4d(ndcProv, 2.0 * minDepth - 1.0, 1.0)
                let worldP = uniform.ViewProjTrafoInv * clipP
                let p = worldP.XYZ / worldP.W
                let dErr = uniform.ProvenanceDataset.[index]
                let aErr = uniform.ProvenanceAlgorithm.[index]
                // Local conditioning from anchor distribution.
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
                // Angular diversity is heavy in a shader; approximate with
                // (density^0.5) — more anchors ⇒ better-conditioned.
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

    // V6 §D.4 — dual-signal Explore heatmap. For each pixel:
    //  - Feature confidence = curvature × steepness (screen-space).
    //    Curvature is estimated as the angular variation between the
    //    centre-pixel reconstructed normal and the four-neighbour
    //    normals, summed and clamped to [0, 1]; steepness is the
    //    absolute dot of the centre normal with the reference axis.
    //  - Disagreement = depth-stddev across visible meshes (V5 formula).
    // The two scores are gated by their respective thresholds and
    // composited per MixModeInt (0=SideBySide, 1=Blended, 2=Alternating).
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

            // Disagreement: depth stddev across all visible meshes.
            let mutable dgCount = 0
            let mutable dgMean  = 0.0
            let mutable dgS2    = 0.0
            // Feature confidence: pick the front-most visible mesh's
            // depth at this pixel and compute curvature × steepness on it.
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
                    // Curvature proxy: 1 - average dot of centre normal with
                    // the four diagonal-neighbour normals (saturated).
                    let curv =
                        let s =
                            (1.0 - max 0.0 (Vec.dot nC nL))
                            + (1.0 - max 0.0 (Vec.dot nC nR))
                            + (1.0 - max 0.0 (Vec.dot nC nU))
                            + (1.0 - max 0.0 (Vec.dot nC nD))
                        clamp 0.0 1.0 (s * 0.5)
                    // Steepness: how non-aligned the centre normal is with
                    // the reference axis (1 = perpendicular = steep).
                    let steep = 1.0 - abs (Vec.dot nC uniform.ReferenceAxis)
                    fcScore <- curv * steep

            // Apply per-signal thresholds. Each score either becomes a
            // normalised intensity in [0, 1] or 0 if below threshold.
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

            // Dot grid pattern shared by both signals.
            let pixel = v.tc * vpSize
            let cell = 8.0
            let cx = (pixel.X % cell) - cell * 0.5
            let cy = (pixel.Y % cell) - cell * 0.5
            let dist = sqrt(cx * cx + cy * cy)
            if dist > cell * 0.38 then discard()
            let dotFade = clamp 0.0 1.0 (1.0 - dist / (cell * 0.38))

            // Composite: pick a colour + alpha per MixMode when both
            // signals are active.
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
                    // SideBySide: 8 px stripes alternating between the two
                    // signal colours along the screen-x axis.
                    let stripe = floor (pixel.X / 8.0) % 2.0
                    if stripe < 0.5 then
                        col <- uniform.FcColor.XYZ
                        alpha <- uniform.HighlightAlpha * (0.3 + 0.7 * fcInt)
                    else
                        col <- uniform.DgColor.XYZ
                        alpha <- uniform.HighlightAlpha * (0.3 + 0.7 * dgInt)
                elif m = 2 then
                    // Alternating: flip the two colours on a 1-second cycle.
                    let phase = floor (uniform.ExploreTime * 1.5) % 2.0
                    if phase < 0.5 then
                        col <- uniform.FcColor.XYZ
                        alpha <- uniform.HighlightAlpha * (0.3 + 0.7 * fcInt)
                    else
                        col <- uniform.DgColor.XYZ
                        alpha <- uniform.HighlightAlpha * (0.3 + 0.7 * dgInt)
                else
                    // Blended (default): per-channel weighted mean.
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
