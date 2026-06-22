namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open FShade

module RenderPass =
    let passMinusOne = RenderPass.main
    let passZero = RenderPass.after "zero" RenderPassOrder.Arbitrary passMinusOne
    let passOne = RenderPass.after "one" RenderPassOrder.Arbitrary passZero

// Per-fragment ghosting rules are documented in CLAUDE.md ("Ghosting rules").
[<ReflectedDefinition>]
module MeshShader =

    [<Literal>]
    let MaxLassoPlanes = 32

    [<Literal>]
    let MaxBlobs = 32

    [<Literal>]
    let opaqueThreshold = 0.99f

    type UniformScope with
        member x.MeshActive      : bool    = x?MeshActive
        member x.GhostOpacity    : float32 = x?GhostOpacity
        // 0 = Textured, 1 = Shaded (palette colour), 2 = SlopeColor.
        member x.RenderingMode   : int     = x?RenderingMode
        member x.MeshColor       : V4f     = x?MeshColor
        member x.ShadingStrength : float32 = x?ShadingStrength
        // sin(threshold angle) for SlopeColor.
        member x.SlopeThreshold  : float32 = x?SlopeThreshold
        // Outward-facing half-space planes V4f(nx,ny,nz,d); inside iff
        // dot(xyz, p) + w <= 0 for all. count = 0 → no restriction.
        member x.LassoPlaneCount : int     = x?LassoPlaneCount
        member x.LassoPlanes     : Arr<N<32>, V4f> = x?LassoPlanes
        // Pin blobs in render space: Blobs = (cx,cy,cz,innerR). AnchorGhost = 0
        // disables the blob alpha filter; the array stays uploaded so the
        // provenance conditioning (Gaussian σ = innerR) can still loop over it.
        member x.BlobCount       : int     = x?BlobCount
        member x.Blobs           : Arr<N<32>, V4f> = x?Blobs
        member x.AnchorGhost     : int     = x?AnchorGhost
        // 0 = off, 1 = provenance sources, 2 = registration diff.
        member x.HeatmapMode       : int     = x?HeatmapMode
        member x.ProvThreshold     : float32 = x?ProvThreshold
        member x.MeshDatasetError  : float32 = x?MeshDatasetError
        member x.MeshAlgoResidual  : float32 = x?MeshAlgoResidual
        // Registration diff mode (HeatmapMode = 2): signed change of the
        // combined error between the committed and the previewed pose.
        // DiffInvDelta maps a preview-pose render position back to its
        // committed-pose position; algo residuals come per mesh from the
        // pending solve; DiffSigmaRef is the reference's dataset error.
        member x.DiffAlgoBefore    : float32 = x?DiffAlgoBefore
        member x.DiffAlgoAfter     : float32 = x?DiffAlgoAfter
        member x.DiffInvDelta      : M44f    = x?DiffInvDelta
        member x.DiffSigmaRef      : float32 = x?DiffSigmaRef
        // Contact-line highlight at the elevation-cursor slicing plane, all
        // in render space. CursorActive is per-mesh (bbox-vs-plane gate);
        // CursorClip restricts the band to the probe cylinder (off while the
        // chart cursor is Alt-extended scene-wide).
        member x.CursorActive         : int     = x?CursorActive
        member x.CursorPlaneOrigin    : V3f     = x?CursorPlaneOrigin
        member x.CursorPlaneNormal    : V3f     = x?CursorPlaneNormal
        member x.CursorHighlightWidth : float32 = x?CursorHighlightWidth
        member x.CursorDarken         : float32 = x?CursorDarken
        member x.CursorClip           : int     = x?CursorClip
        member x.CursorPinCentre      : V3f     = x?CursorPinCentre
        member x.CursorPinRadius      : float32 = x?CursorPinRadius
        member x.CursorCylLength      : float32 = x?CursorCylLength
        // 3D sectioning: up to two render-space plane equations
        // V4f(nx,ny,nz, -dot(n,renderOrigin)); a fragment with
        // dot(n,wp)+w > 0 is on the removed (camera-side) half. Mode:
        // 0 = Hide/SectionCap (discard), 1 = Ghost (drop to ghost alpha).
        member x.ClipPlaneCount : int = x?ClipPlaneCount
        member x.ClipPlane0     : V4f = x?ClipPlane0
        member x.ClipPlane1     : V4f = x?ClipPlane1
        // A2 per-mesh signed-distance surface colour map. 1 = paint the
        // SurfaceDist vertex attribute with a diverging map (0 = reference);
        // |d| < DistLoD → neutral mid (not significant); DistScale normalizes
        // the saturated ends. SurfaceDist = 1e30 sentinel → keep base colour.
        member x.DistanceEncoding : int     = x?DistanceEncoding
        member x.DistLoD          : float32 = x?DistLoD
        member x.DistScale        : float32 = x?DistScale
        // A3 range brush: when on, SurfaceDist outside [Lo,Hi] is washed to
        // context (focus+context); inside keeps the diverging colour.
        member x.DistBrushOn      : int     = x?DistBrushOn
        member x.DistBrushLo      : float32 = x?DistBrushLo
        member x.DistBrushHi      : float32 = x?DistBrushHi

    type FragIn = {
        [<Color>]                              c  : V4f
        [<Semantic("Normals")>]                n  : V3f
        [<Semantic("WorldPosition")>]          wp : V4f
        [<Semantic("SurfaceDist")>]            sd : float32
        [<FragCoord>]                          fc : V4f
    }

    type FragOut = {
        [<Color>] color : V4f
        [<Depth>] depth : float32
    }

    let shade (v : FragIn) =
        fragment {
            let wp = v.wp.XYZ
            // 3D sectioning (mesh geometry only; overlays are never clipped):
            // the camera-side half (dot(n,wp)+w > 0) is discarded.
            let cpc = uniform.ClipPlaneCount
            if cpc >= 1 then
                let p = uniform.ClipPlane0
                let sd = p.X * wp.X + p.Y * wp.Y + p.Z * wp.Z + p.W
                if sd > 0.0f then discard()
            if cpc >= 2 then
                let p = uniform.ClipPlane1
                let sd = p.X * wp.X + p.Y * wp.Y + p.Z * wp.Z + p.W
                if sd > 0.0f then discard()
            let mutable lassoMask = 1.0f
            let lc = uniform.LassoPlaneCount
            if lc > 0 then
                let mutable inside = true
                for i in 0 .. MaxLassoPlanes - 1 do
                    if i < lc then
                        let pl = uniform.LassoPlanes.[i]
                        let d = pl.X * wp.X + pl.Y * wp.Y + pl.Z * wp.Z + pl.W
                        if d > 0.0f then inside <- false
                lassoMask <- if inside then 1.0f else 0.0f
            let mutable inAnyBlob = false
            let bc = uniform.BlobCount
            if bc > 0 then
                for i in 0 .. MaxBlobs - 1 do
                    if i < bc then
                        let b      = uniform.Blobs.[i]
                        let inner  = b.W
                        let dx = wp.X - b.X
                        let dy = wp.Y - b.Y
                        let dz = wp.Z - b.Z
                        let d  = sqrt (dx*dx + dy*dy + dz*dz)
                        if d <= inner then inAnyBlob <- true
            let lassoActive  = lc > 0
            let blobsActive  = bc > 0 && uniform.AnchorGhost <> 0
            let lassoComponent =
                if lassoActive then lassoMask else 1.0f
            let blobComponent =
                if blobsActive then
                    if inAnyBlob then 1.0f else 0.0f
                else 1.0f
            let maskFactor = lassoComponent * blobComponent
            let ghost = uniform.GhostOpacity
            let mutable alpha = 0.0f
            if uniform.MeshActive then
                alpha <- ghost + (1.0f - ghost) * maskFactor
            else
                alpha <- ghost
            if alpha < 1e-4f then discard()
            // Clamp ghost/outside fragments below opaqueThreshold so the
            // depth-write branch only fires for fully-solid surface.
            let lassoFull = (lc = 0) || lassoMask >= 1.0f
            let blobFull  = (not blobsActive) || inAnyBlob
            let fullySolid = lassoFull && blobFull
            if uniform.MeshActive && not fullySolid then
                alpha <- min alpha (opaqueThreshold - 0.01f)
            let n = v.n |> Vec.normalize
            let toCam = (uniform.CameraLocation - v.wp.XYZ) |> Vec.normalize
            let ndl = max 0.15f (abs (Vec.dot n toCam))
            let s = clamp 0.0f 1.0f uniform.ShadingStrength
            let shade = 1.0f + (ndl - 1.0f) * s
            let nz = abs n.Z
            let whiteCol = V3f(1.0f, 1.0f, 1.0f)
            let blueCol  = V3f(0.22f, 0.45f, 0.95f)
            let hotCol   = V3f(1.0f, 0.85f, 0.55f)
            let tT = clamp 0.01f 0.99f uniform.SlopeThreshold
            let slopeCol =
                if nz > tT then
                    let fadeW = max 0.05f ((1.0f - tT) * 0.5f)
                    let t = clamp 0.0f 1.0f ((nz - tT) / fadeW)
                    let s = t * t * (3.0f - 2.0f * t)
                    blueCol * (1.0f - s) + whiteCol * s
                else
                    let t = clamp 0.0f 1.0f ((tT - nz) / tT)
                    let s = t * t * (3.0f - 2.0f * t)
                    blueCol * (1.0f - s) + hotCol * s
            // Ghost-level fragments always use the solid mesh colour so the
            // silhouette reads uniformly regardless of rendering mode.
            let aboveGhost = alpha > ghost + 1e-4f
            let mutable baseRgb =
                if not aboveGhost then uniform.MeshColor.XYZ
                elif uniform.RenderingMode = 1 then uniform.MeshColor.XYZ
                elif uniform.RenderingMode = 2 then slopeCol
                else v.c.XYZ
            if uniform.HeatmapMode = 1 && aboveGhost then
                let mutable wSum = 0.0f
                let mutable validCount = 0
                for i in 0 .. MaxBlobs - 1 do
                    if i < bc then
                        let bi = uniform.Blobs.[i]
                        let sigma = bi.W
                        if sigma > 1e-6f then
                            let dx = wp.X - bi.X
                            let dy = wp.Y - bi.Y
                            let dz = wp.Z - bi.Z
                            let d2 = dx*dx + dy*dy + dz*dz
                            let w = exp (-d2 / (2.0f * sigma * sigma))
                            if w > 0.05f then
                                wSum <- wSum + w
                                validCount <- validCount + 1
                let mutable maxCos = 0.0f
                if validCount >= 2 then
                    for i in 0 .. MaxBlobs - 1 do
                        if i < bc then
                            let bi = uniform.Blobs.[i]
                            let sigmaI = bi.W
                            if sigmaI > 1e-6f then
                                let dxI = wp.X - bi.X
                                let dyI = wp.Y - bi.Y
                                let dzI = wp.Z - bi.Z
                                let dI2 = dxI*dxI + dyI*dyI + dzI*dzI
                                let wI = exp (-dI2 / (2.0f * sigmaI * sigmaI))
                                if wI > 0.05f then
                                    let lenI = sqrt dI2
                                    if lenI > 1e-9f then
                                        let nix = (bi.X - wp.X) / lenI
                                        let niy = (bi.Y - wp.Y) / lenI
                                        let niz = (bi.Z - wp.Z) / lenI
                                        for j in 0 .. MaxBlobs - 1 do
                                            if j > i && j < bc then
                                                let bj = uniform.Blobs.[j]
                                                let sigmaJ = bj.W
                                                if sigmaJ > 1e-6f then
                                                    let dxJ = wp.X - bj.X
                                                    let dyJ = wp.Y - bj.Y
                                                    let dzJ = wp.Z - bj.Z
                                                    let dJ2 = dxJ*dxJ + dyJ*dyJ + dzJ*dzJ
                                                    let wJ = exp (-dJ2 / (2.0f * sigmaJ * sigmaJ))
                                                    if wJ > 0.05f then
                                                        let lenJ = sqrt dJ2
                                                        if lenJ > 1e-9f then
                                                            let njx = (bj.X - wp.X) / lenJ
                                                            let njy = (bj.Y - wp.Y) / lenJ
                                                            let njz = (bj.Z - wp.Z) / lenJ
                                                            let dotV = nix*njx + niy*njy + niz*njz
                                                            let cAbs = abs dotV
                                                            if cAbs > maxCos then maxCos <- cAbs
                let mutable cond = 1e6f
                if validCount >= 2 then
                    let angDiv = 1.0f - maxCos
                    let raw = 1.0f / (wSum * angDiv + 1e-3f)
                    cond <- if raw > 1e6f then 1e6f else raw
                let d = uniform.MeshDatasetError
                let a = uniform.MeshAlgoResidual
                let cScaled = cond * 0.01f
                let total = max d (max a cScaled)
                if total >= uniform.ProvThreshold then
                    let datasetCol = V3f(0.376f, 0.647f, 0.980f) // #60a5fa
                    let algoCol    = V3f(0.961f, 0.620f, 0.044f) // #f59e0b
                    let condCol    = V3f(0.655f, 0.545f, 0.913f) // #a78bfa
                    let domCol =
                        if d >= a && d >= cScaled then datasetCol
                        elif a >= cScaled then algoCol
                        else condCol
                    baseRgb <- domCol
            // Registration diff (HeatmapMode = 2, only meaningful while a
            // solve preview is pending): per-fragment signed change of the
            // combined error (preview − committed). Dataset error cancels;
            // algorithm residual changes per mesh, conditioning changes with
            // the fragment's pose. Fragments below the detection limit
            // 1.96·√(σ_ref² + σ_M²) drop to context/ghost level; the rest get
            // a diverging blue (improved) / red (degraded) map.
            if uniform.HeatmapMode = 2 && aboveGhost then
                let wpc4 = uniform.DiffInvDelta * V4f(wp.X, wp.Y, wp.Z, 1.0f)
                let wpcx = wpc4.X
                let wpcy = wpc4.Y
                let wpcz = wpc4.Z
                // conditioning at the preview-pose position
                let mutable wSumP = 0.0f
                let mutable validP = 0
                let mutable maxCosP = 0.0f
                let mutable wSumC = 0.0f
                let mutable validC = 0
                let mutable maxCosC = 0.0f
                for i in 0 .. MaxBlobs - 1 do
                    if i < bc then
                        let bi = uniform.Blobs.[i]
                        let sigma = bi.W
                        if sigma > 1e-6f then
                            let dxP = wp.X - bi.X
                            let dyP = wp.Y - bi.Y
                            let dzP = wp.Z - bi.Z
                            let d2P = dxP*dxP + dyP*dyP + dzP*dzP
                            let wP = exp (-d2P / (2.0f * sigma * sigma))
                            if wP > 0.05f then
                                wSumP <- wSumP + wP
                                validP <- validP + 1
                            let dxC = wpcx - bi.X
                            let dyC = wpcy - bi.Y
                            let dzC = wpcz - bi.Z
                            let d2C = dxC*dxC + dyC*dyC + dzC*dzC
                            let wC = exp (-d2C / (2.0f * sigma * sigma))
                            if wC > 0.05f then
                                wSumC <- wSumC + wC
                                validC <- validC + 1
                for i in 0 .. MaxBlobs - 1 do
                    if i < bc then
                        let bi = uniform.Blobs.[i]
                        let sigmaI = bi.W
                        if sigmaI > 1e-6f then
                            for j in 0 .. MaxBlobs - 1 do
                                if j > i && j < bc then
                                    let bj = uniform.Blobs.[j]
                                    let sigmaJ = bj.W
                                    if sigmaJ > 1e-6f then
                                        if validP >= 2 then
                                            let dI2 = (wp.X-bi.X)*(wp.X-bi.X) + (wp.Y-bi.Y)*(wp.Y-bi.Y) + (wp.Z-bi.Z)*(wp.Z-bi.Z)
                                            let dJ2 = (wp.X-bj.X)*(wp.X-bj.X) + (wp.Y-bj.Y)*(wp.Y-bj.Y) + (wp.Z-bj.Z)*(wp.Z-bj.Z)
                                            let wI = exp (-dI2 / (2.0f * sigmaI * sigmaI))
                                            let wJ = exp (-dJ2 / (2.0f * sigmaJ * sigmaJ))
                                            if wI > 0.05f && wJ > 0.05f then
                                                let lI = sqrt dI2
                                                let lJ = sqrt dJ2
                                                if lI > 1e-9f && lJ > 1e-9f then
                                                    let dotV =
                                                        ((bi.X-wp.X)*(bj.X-wp.X) + (bi.Y-wp.Y)*(bj.Y-wp.Y) + (bi.Z-wp.Z)*(bj.Z-wp.Z)) / (lI * lJ)
                                                    let cAbs = abs dotV
                                                    if cAbs > maxCosP then maxCosP <- cAbs
                                        if validC >= 2 then
                                            let dI2 = (wpcx-bi.X)*(wpcx-bi.X) + (wpcy-bi.Y)*(wpcy-bi.Y) + (wpcz-bi.Z)*(wpcz-bi.Z)
                                            let dJ2 = (wpcx-bj.X)*(wpcx-bj.X) + (wpcy-bj.Y)*(wpcy-bj.Y) + (wpcz-bj.Z)*(wpcz-bj.Z)
                                            let wI = exp (-dI2 / (2.0f * sigmaI * sigmaI))
                                            let wJ = exp (-dJ2 / (2.0f * sigmaJ * sigmaJ))
                                            if wI > 0.05f && wJ > 0.05f then
                                                let lI = sqrt dI2
                                                let lJ = sqrt dJ2
                                                if lI > 1e-9f && lJ > 1e-9f then
                                                    let dotV =
                                                        ((bi.X-wpcx)*(bj.X-wpcx) + (bi.Y-wpcy)*(bj.Y-wpcy) + (bi.Z-wpcz)*(bj.Z-wpcz)) / (lI * lJ)
                                                    let cAbs = abs dotV
                                                    if cAbs > maxCosC then maxCosC <- cAbs
                let mutable condP = 1e6f
                if validP >= 2 then
                    let raw = 1.0f / (wSumP * (1.0f - maxCosP) + 1e-3f)
                    condP <- if raw > 1e6f then 1e6f else raw
                let mutable condC = 1e6f
                if validC >= 2 then
                    let raw = 1.0f / (wSumC * (1.0f - maxCosC) + 1e-3f)
                    condC <- if raw > 1e6f then 1e6f else raw
                let combinedP = uniform.DiffAlgoAfter  + 0.01f * (min (condP * 0.01f) 50.0f)
                let combinedC = uniform.DiffAlgoBefore + 0.01f * (min (condC * 0.01f) 50.0f)
                let dd = combinedP - combinedC
                let lod =
                    max 1e-6f
                        (1.96f * sqrt (uniform.DiffSigmaRef * uniform.DiffSigmaRef
                                       + uniform.MeshDatasetError * uniform.MeshDatasetError))
                if abs dd < lod then
                    baseRgb <- uniform.MeshColor.XYZ
                    alpha <- min (max ghost 0.12f) 0.5f
                else
                    let tt = clamp -1.0f 1.0f (dd / (3.0f * lod))
                    let blueCol2 = V3f(0.149f, 0.388f, 0.922f) // #2563eb improved
                    let redCol2  = V3f(0.863f, 0.149f, 0.149f) // #dc2626 degraded
                    let midCol2  = V3f(0.945f, 0.961f, 0.976f) // #f1f5f9 neutral
                    baseRgb <-
                        if tt >= 0.0f then midCol2 * (1.0f - tt) + redCol2 * tt
                        else midCol2 * (1.0f + tt) + blueCol2 * (-tt)
            // A2: per-mesh signed-distance surface colour map (the canonical
            // M3C2 depiction). Diverging blue (below ref) ↔ red (above ref)
            // centred at 0; within ±DistLoD the fragment reads neutral, so
            // "not significant" looks near-neutral in 3D too.
            if uniform.DistanceEncoding = 1 && aboveGhost then
                let d = v.sd
                if abs d < 1e20f then
                    let lod = max 1e-6f uniform.DistLoD
                    let neutral = V3f(0.945f, 0.961f, 0.976f) // #f1f5f9
                    let mutable col = neutral
                    if abs d >= lod then
                        let scale = max 1e-6f uniform.DistScale
                        let tt = clamp -1.0f 1.0f (d / scale)
                        let belowCol = V3f(0.149f, 0.388f, 0.922f) // #2563eb (negative)
                        let aboveCol = V3f(0.863f, 0.149f, 0.149f) // #dc2626 (positive)
                        col <-
                            if tt >= 0.0f then neutral * (1.0f - tt) + aboveCol * tt
                            else neutral * (1.0f + tt) + belowCol * (-tt)
                    // A3 brush: out-of-band fragments wash to context grey.
                    if uniform.DistBrushOn = 1 && (d < uniform.DistBrushLo || d > uniform.DistBrushHi) then
                        baseRgb <- V3f(0.82f, 0.85f, 0.88f)
                    else
                        baseRgb <- col
            // Contact-line highlight at the active slicing plane: darken the
            // intersected mesh, brighten a smoothstep band within
            // CursorHighlightWidth of the plane (accent #0891b2 — the slicing
            // plane's colour), optionally clipped to the probe cylinder.
            // Ghost-level fragments are skipped so the silhouette colour
            // stays uniform.
            if uniform.CursorActive <> 0 && aboveGhost then
                let co = uniform.CursorPlaneOrigin
                let cn = uniform.CursorPlaneNormal
                let sd = (wp.X - co.X) * cn.X + (wp.Y - co.Y) * cn.Y + (wp.Z - co.Z) * cn.Z
                let ad = abs sd
                let hw = uniform.CursorHighlightWidth
                let mutable amount = 0.0f
                if hw > 1e-9f && ad < hw then
                    let tt = clamp 0.0f 1.0f (ad / hw)
                    amount <- 1.0f - tt * tt * (3.0f - 2.0f * tt)
                if uniform.CursorClip <> 0 then
                    let pc = uniform.CursorPinCentre
                    let axial = (wp.X - pc.X) * cn.X + (wp.Y - pc.Y) * cn.Y + (wp.Z - pc.Z) * cn.Z
                    let rx = wp.X - pc.X - axial * cn.X
                    let ry = wp.Y - pc.Y - axial * cn.Y
                    let rz = wp.Z - pc.Z - axial * cn.Z
                    let radial = sqrt (rx*rx + ry*ry + rz*rz)
                    if radial > uniform.CursorPinRadius || abs axial > uniform.CursorCylLength * 0.5f then
                        amount <- 0.0f
                let hiCol = V3f(0.031f, 0.569f, 0.698f)
                let darkened = baseRgb * uniform.CursorDarken
                baseRgb <- darkened * (1.0f - amount) + hiCol * amount
            let depth =
                if alpha >= opaqueThreshold then v.fc.Z
                else 1.0f
            return {
                color = V4f(baseRgb * shade, alpha)
                depth = depth
            }
        }

// Writes combined provenance error as gl_FragDepth so the lowest-error mesh
// wins the offscreen LessOrEqual depth test. Conditioning is the same
// density × angular-diversity heuristic as the heatmap.
[<ReflectedDefinition>]
module FusionShader =
    open MeshShader

    type FragIn = {
        [<Color>]                              c  : V4f
        [<Semantic("Normals")>]                n  : V3f
        [<Semantic("WorldPosition")>]          wp : V4f
        [<FragCoord>]                          fc : V4f
    }

    type FragOut = {
        [<Color>] color : V4f
        [<Depth>] depth : float32
    }

    let shade (v : FragIn) =
        fragment {
            let wp = v.wp.XYZ
            let bc = uniform.BlobCount
            let mutable wSum = 0.0f
            let mutable validCount = 0
            for i in 0 .. MeshShader.MaxBlobs - 1 do
                if i < bc then
                    let bi = uniform.Blobs.[i]
                    let sigma = bi.W
                    if sigma > 1e-6f then
                        let dx = wp.X - bi.X
                        let dy = wp.Y - bi.Y
                        let dz = wp.Z - bi.Z
                        let d2 = dx*dx + dy*dy + dz*dz
                        let w = exp (-d2 / (2.0f * sigma * sigma))
                        if w > 0.05f then
                            wSum <- wSum + w
                            validCount <- validCount + 1
            let mutable maxCos = 0.0f
            if validCount >= 2 then
                for i in 0 .. MeshShader.MaxBlobs - 1 do
                    if i < bc then
                        let bi = uniform.Blobs.[i]
                        let sigmaI = bi.W
                        if sigmaI > 1e-6f then
                            let dxI = wp.X - bi.X
                            let dyI = wp.Y - bi.Y
                            let dzI = wp.Z - bi.Z
                            let dI2 = dxI*dxI + dyI*dyI + dzI*dzI
                            let wI = exp (-dI2 / (2.0f * sigmaI * sigmaI))
                            if wI > 0.05f then
                                let lenI = sqrt dI2
                                if lenI > 1e-9f then
                                    let nix = (bi.X - wp.X) / lenI
                                    let niy = (bi.Y - wp.Y) / lenI
                                    let niz = (bi.Z - wp.Z) / lenI
                                    for j in 0 .. MeshShader.MaxBlobs - 1 do
                                        if j > i && j < bc then
                                            let bj = uniform.Blobs.[j]
                                            let sigmaJ = bj.W
                                            if sigmaJ > 1e-6f then
                                                let dxJ = wp.X - bj.X
                                                let dyJ = wp.Y - bj.Y
                                                let dzJ = wp.Z - bj.Z
                                                let dJ2 = dxJ*dxJ + dyJ*dyJ + dzJ*dzJ
                                                let wJ = exp (-dJ2 / (2.0f * sigmaJ * sigmaJ))
                                                if wJ > 0.05f then
                                                    let lenJ = sqrt dJ2
                                                    if lenJ > 1e-9f then
                                                        let njx = (bj.X - wp.X) / lenJ
                                                        let njy = (bj.Y - wp.Y) / lenJ
                                                        let njz = (bj.Z - wp.Z) / lenJ
                                                        let dotV = nix*njx + niy*njy + niz*njz
                                                        let cAbs = abs dotV
                                                        if cAbs > maxCos then maxCos <- cAbs
            let mutable cond = 1e6f
            if validCount >= 2 then
                let angDiv = 1.0f - maxCos
                let raw = 1.0f / (wSum * angDiv + 1e-3f)
                cond <- if raw > 1e6f then 1e6f else raw
            let d = uniform.MeshDatasetError
            let a = uniform.MeshAlgoResidual
            let cScaled = cond * 0.01f
            let combined = d + a + 0.01f * (min cScaled 50.0f)
            let depth = clamp 0.0001f 0.9999f (combined * 0.3f)
            let nn = v.n |> Vec.normalize
            let toCam = (uniform.CameraLocation - wp) |> Vec.normalize
            let ndl = max 0.2f (abs (Vec.dot nn toCam))
            let rgb = v.c.XYZ * ndl
            return { color = V4f(rgb, 1.0f); depth = depth }
        }

// Plain textured surface + headlight, natural depth — panorama captures want
// the meshes as-is (no lasso/blob/ghost filters).
[<ReflectedDefinition>]
module PanoramaShader =

    type FragIn = {
        [<Color>]                     c  : V4f
        [<Semantic("Normals")>]       n  : V3f
        [<Semantic("WorldPosition")>] wp : V4f
    }

    let shade (v : FragIn) =
        fragment {
            let nn = v.n |> Vec.normalize
            let toCam = (uniform.CameraLocation - v.wp.XYZ) |> Vec.normalize
            let ndl = max 0.25f (abs (Vec.dot nn toCam))
            return V4f(v.c.XYZ * ndl, 1.0f)
        }
