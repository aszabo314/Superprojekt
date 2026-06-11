namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open FShade

module RenderPass =
    let passMinusOne = RenderPass.main
    let passZero = RenderPass.after "zero" RenderPassOrder.Arbitrary passMinusOne
    let passOne = RenderPass.after "one" RenderPassOrder.Arbitrary passZero
    let passTwo = RenderPass.after "two" RenderPassOrder.Arbitrary passOne

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
        // Pin blobs in render space: Blobs = (cx,cy,cz,innerR),
        // BlobFalloffs = (falloffR,0,0,0). AnchorGhost = 0 disables the blob
        // alpha filter; the arrays stay uploaded for provenance conditioning.
        member x.BlobCount       : int     = x?BlobCount
        member x.Blobs           : Arr<N<32>, V4f> = x?Blobs
        member x.BlobFalloffs    : Arr<N<32>, V4f> = x?BlobFalloffs
        member x.AnchorGhost     : int     = x?AnchorGhost
        member x.ProvenanceHeatmap : int     = x?ProvenanceHeatmap
        member x.ProvThreshold     : float32 = x?ProvThreshold
        member x.FalloffZoneOnly   : int     = x?FalloffZoneOnly
        member x.MeshDatasetError  : float32 = x?MeshDatasetError
        member x.MeshAlgoResidual  : float32 = x?MeshAlgoResidual
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
            let mutable blobMax = 0.0f
            let mutable inHardCore = false
            let mutable inAnyBlob = false
            let bc = uniform.BlobCount
            if bc > 0 then
                for i in 0 .. MaxBlobs - 1 do
                    if i < bc then
                        let b      = uniform.Blobs.[i]
                        let f      = uniform.BlobFalloffs.[i]
                        let inner  = b.W
                        let outer  = f.X
                        let dx = wp.X - b.X
                        let dy = wp.Y - b.Y
                        let dz = wp.Z - b.Z
                        let d  = sqrt (dx*dx + dy*dy + dz*dz)
                        let w =
                            if d <= inner then
                                inHardCore <- true
                                inAnyBlob  <- true
                                1.0f
                            elif d <= outer && outer > inner then
                                inAnyBlob <- true
                                let t = (d - inner) / (outer - inner)
                                exp (-3.0f * t)
                            else 0.0f
                        if w > blobMax then blobMax <- w
            let lassoActive  = lc > 0
            let blobsActive  = bc > 0 && uniform.AnchorGhost <> 0
            let lassoComponent =
                if lassoActive then lassoMask else 1.0f
            let blobComponent =
                if blobsActive then
                    if inAnyBlob then blobMax else 0.0f
                else 1.0f
            let maskFactor = lassoComponent * blobComponent
            let ghost = uniform.GhostOpacity
            let mutable alpha = 0.0f
            if uniform.MeshActive then
                alpha <- ghost + (1.0f - ghost) * maskFactor
            else
                alpha <- ghost
            if alpha < 1e-4f then discard()
            // Clamp non-hard-core fragments below opaqueThreshold so the
            // depth-write branch can't flip mid-falloff (occlusion ring).
            let lassoFull = (lc = 0) || lassoMask >= 1.0f
            let blobFull  = (not blobsActive) || inHardCore
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
            if uniform.ProvenanceHeatmap <> 0 && aboveGhost then
                let zoneOk = uniform.FalloffZoneOnly = 0 || inAnyBlob
                if zoneOk then
                    let mutable wSum = 0.0f
                    let mutable validCount = 0
                    for i in 0 .. MaxBlobs - 1 do
                        if i < bc then
                            let bi = uniform.Blobs.[i]
                            let sigma = uniform.BlobFalloffs.[i].X
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
                                let sigmaI = uniform.BlobFalloffs.[i].X
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
                                                    let sigmaJ = uniform.BlobFalloffs.[j].X
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
                    let sigma = uniform.BlobFalloffs.[i].X
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
                        let sigmaI = uniform.BlobFalloffs.[i].X
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
                                            let sigmaJ = uniform.BlobFalloffs.[j].X
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
