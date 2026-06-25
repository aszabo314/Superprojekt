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
        // Pin blobs in render space: Blobs = (cx,cy,cz,innerR). AnchorGhost = 0
        // disables the blob alpha filter but the array stays uploaded.
        member x.BlobCount       : int     = x?BlobCount
        member x.Blobs           : Arr<N<32>, V4f> = x?Blobs
        member x.AnchorGhost     : int     = x?AnchorGhost
        // Contact-line highlight at the elevation-cursor plane (render space).
        // CursorActive is per-mesh (bbox-vs-plane gate); CursorClip restricts
        // the band to the probe cylinder (off when Alt-extended scene-wide).
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
        // V4f(nx,ny,nz,-dot(n,renderOrigin)); dot(n,wp)+w > 0 is the removed
        // (camera-side) half.
        member x.ClipPlaneCount : int = x?ClipPlaneCount
        member x.ClipPlane0     : V4f = x?ClipPlane0
        member x.ClipPlane1     : V4f = x?ClipPlane1
        // A2 per-mesh signed-distance map. 1 = paint SurfaceDist with a
        // diverging map (0 = reference); |d| < DistLoD → neutral mid; DistScale
        // normalizes the saturated ends; SurfaceDist = 1e30 → keep base colour.
        member x.DistanceEncoding : int     = x?DistanceEncoding
        member x.DistLoD          : float32 = x?DistLoD
        member x.DistScale        : float32 = x?DistScale
        // §6 intrinsic heatmap channel: 0 = off, 1 = incidence, 2 = range.
        member x.HeatmapMode      : int     = x?HeatmapMode
        member x.SensorOrigin     : V3f     = x?SensorOrigin
        member x.RangeMax         : float32 = x?RangeMax

    type FragIn = {
        [<Color>]                              c  : V4f
        [<Semantic("Normals")>]                n  : V3f
        [<Semantic("WorldPosition")>]          wp : V4f
        [<Semantic("SurfaceDist")>]            sd : float32
        [<Semantic("ShapeQ")>]                 shq : float32
        [<FragCoord>]                          fc : V4f
    }

    type FragOut = {
        [<Color>] color : V4f
        [<Depth>] depth : float32
    }

    let shade (v : FragIn) =
        fragment {
            let wp = v.wp.XYZ
            // 3D sectioning (mesh only; overlays never clipped): the
            // camera-side half (dot(n,wp)+w > 0) is discarded.
            let cpc = uniform.ClipPlaneCount
            if cpc >= 1 then
                let p = uniform.ClipPlane0
                let sd = p.X * wp.X + p.Y * wp.Y + p.Z * wp.Z + p.W
                if sd > 0.0f then discard()
            if cpc >= 2 then
                let p = uniform.ClipPlane1
                let sd = p.X * wp.X + p.Y * wp.Y + p.Z * wp.Z + p.W
                if sd > 0.0f then discard()
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
            let blobsActive  = bc > 0 && uniform.AnchorGhost <> 0
            let blobComponent =
                if blobsActive then
                    if inAnyBlob then 1.0f else 0.0f
                else 1.0f
            let maskFactor = blobComponent
            let ghost = uniform.GhostOpacity
            let mutable alpha = 0.0f
            if uniform.MeshActive then
                alpha <- ghost + (1.0f - ghost) * maskFactor
            else
                alpha <- ghost
            if alpha < 1e-4f then discard()
            // α-gated depth: clamp ghost/outside fragments below opaqueThreshold
            // so only fully-solid surface writes natural depth (below).
            let blobFull  = (not blobsActive) || inAnyBlob
            let fullySolid = blobFull
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
            // silhouette reads uniformly regardless of mode.
            let aboveGhost = alpha > ghost + 1e-4f
            let mutable baseRgb =
                if not aboveGhost then uniform.MeshColor.XYZ
                elif uniform.RenderingMode = 1 then uniform.MeshColor.XYZ
                elif uniform.RenderingMode = 2 then slopeCol
                else v.c.XYZ
            // A2: per-mesh signed-distance map (canonical M3C2). Diverging blue
            // (below ref) ↔ red (above ref) centred at 0; within ±DistLoD reads
            // neutral so "not significant" looks near-neutral in 3D too.
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
                    baseRgb <- col
            // §6 variance map (sequential): per-reference-vertex disagreement std
            // (≥0) from light grey to strong red, normalised by DistScale.
            if uniform.DistanceEncoding = 2 && aboveGhost then
                let d = v.sd
                if abs d < 1e20f then
                    let scale = max 1e-6f uniform.DistScale
                    let tt = clamp 0.0f 1.0f (d / scale)
                    let loC = V3f(0.945f, 0.961f, 0.976f)
                    let hiC = V3f(0.725f, 0.110f, 0.110f)
                    baseRgb <- loC * (1.0f - tt) + hiC * tt
            // §6 intrinsic incidence heatmap: false-colour the camera-incidence
            // angle on above-ghost fragments (grazing = red, head-on = green).
            if uniform.HeatmapMode = 1 && aboveGhost then
                let incid = abs (Vec.dot n toCam)
                let lo  = V3f(0.84f, 0.19f, 0.15f)
                let mid = V3f(0.99f, 0.85f, 0.30f)
                let hi  = V3f(0.18f, 0.55f, 0.34f)
                baseRgb <-
                    if incid < 0.5f then lo + (mid - lo) * (incid * 2.0f)
                    else mid + (hi - mid) * ((incid - 0.5f) * 2.0f)
            // §6 intrinsic range heatmap: distance from the mesh's own origin
            // (= sensor) over its max range, near = blue → far = red.
            if uniform.HeatmapMode = 2 && aboveGhost then
                let rng = (wp - uniform.SensorOrigin).Length
                let tr  = clamp 0.0f 1.0f (rng / max 1e-6f uniform.RangeMax)
                let nearC = V3f(0.13f, 0.40f, 0.85f)
                let farC  = V3f(0.86f, 0.20f, 0.15f)
                baseRgb <- nearC * (1.0f - tr) + farC * tr
            // §6 intrinsic shape heatmap: per-vertex triangle quality (4√3·A/Σl²,
            // 1 = equilateral, →0 = thin/degenerate). Red = poor, green = good.
            if uniform.HeatmapMode = 3 && aboveGhost then
                let ts = clamp 0.0f 1.0f v.shq
                let loC = V3f(0.86f, 0.20f, 0.15f)
                let hiC = V3f(0.18f, 0.55f, 0.34f)
                baseRgb <- loC * (1.0f - ts) + hiC * ts
            // Contact-line highlight at the slicing plane: darken the mesh,
            // brighten a smoothstep band within CursorHighlightWidth of the
            // plane (accent #0891b2), optionally clipped to the probe cylinder.
            // Ghost fragments skipped so the silhouette colour stays uniform.
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

// Focus-panel large-single projection (spec v3 §D). Replaces DefaultSurfaces.trafo
// in the focus scene so the same mesh can be drawn either orthographically
// (FocusPano = 0 → the usual MVP) or as a cylindrical panorama from a world eye
// (FocusPano = 1 → azimuth→x, elevation→y, radial→depth). Outputs the world
// position + world normal MeshShader.shade expects, so the full channel stack
// (textured / extrinsic / intrinsic heatmaps) is reused unchanged downstream.
[<ReflectedDefinition>]
module FocusProject =
    open FShade

    [<Literal>]
    let piF = 3.1415927f

    type UniformScope with
        member x.FocusPano  : int     = x?FocusPano
        member x.FocusEye   : V3f     = x?FocusEye
        member x.FocusRange : float32 = x?FocusRange

    type Vertex = {
        [<Position>]                  pos : V4f
        [<Semantic("WorldPosition")>] wp  : V4f
        [<Semantic("Normals")>]       n   : V3f
    }

    let vertex (v : Vertex) =
        vertex {
            let world = uniform.ModelTrafo * v.pos
            let nrm = uniform.NormalMatrix * v.n
            let clip =
                if uniform.FocusPano = 1 then
                    let d = world.XYZ - uniform.FocusEye
                    let hyp = sqrt (d.X * d.X + d.Y * d.Y)
                    let phi = if hyp < 1e-9f && abs d.Z < 1e-9f then 0.0f else atan2 d.Y d.X
                    let theta = atan2 d.Z (max 1e-9f hyp)
                    let u = phi / piF
                    let vv = theta / (piF * 0.5f)
                    let dd = clamp 0.0f 1.0f (d.Length / max 1e-6f uniform.FocusRange)
                    V4f(u, vv, dd * 2.0f - 1.0f, 1.0f)
                else
                    uniform.ModelViewProjTrafo * v.pos
            return { v with pos = clip; wp = world; n = nrm }
        }

// §10 image-space outlines. G-buffer pass: write world normal + window depth to
// target0 and the per-mesh palette colour + coverage mask to target1 (MRT).
[<ReflectedDefinition>]
module OutlineGBuffer =
    open MeshShader

    type FragIn = {
        [<Semantic("Normals")>]       n  : V3f
        [<Semantic("WorldPosition")>] wp : V4f
        [<FragCoord>]                 fc : V4f
    }
    type FragOut = {
        [<Color>]                 g0 : V4f
        [<Semantic("Outline1")>]  g1 : V4f
    }
    let shade (v : FragIn) =
        fragment {
            let n = v.n |> Vec.normalize
            let col = uniform.MeshColor
            return {
                g0 = V4f(n.X * 0.5f + 0.5f, n.Y * 0.5f + 0.5f, n.Z * 0.5f + 0.5f, v.fc.Z)
                g1 = V4f(col.X, col.Y, col.Z, 1.0f)
            }
        }

// Edge-detect fullscreen pass: sample the g-buffer at centre ±1 texel; an edge =
// depth jump OR normal-angle jump OR coverage-mask boundary (the mask boundary
// catches the silhouette AND the near-plane cut → no inverted hull). Output the
// per-pixel palette colour where an edge is found, transparent elsewhere.
[<ReflectedDefinition>]
module OutlineEdge =

    type UniformScope with
        member x.OutlineTexel : V2f = x?OutlineTexel

    let private gNormal =
        sampler2d {
            texture uniform?GNormal
            filter Filter.MinMagPoint
            addressU WrapMode.Clamp
            addressV WrapMode.Clamp
        }
    let private gColor =
        sampler2d {
            texture uniform?GColor
            filter Filter.MinMagPoint
            addressU WrapMode.Clamp
            addressV WrapMode.Clamp
        }

    type Vtx = {
        [<Position>]            pos : V4f
        [<Semantic("OTc")>]     tc  : V2f
    }

    let vertex (v : Vtx) =
        vertex {
            return { v with tc = V2f(v.pos.X * 0.5f + 0.5f, v.pos.Y * 0.5f + 0.5f) }
        }

    let fragment (v : Vtx) =
        fragment {
            let ts = uniform.OutlineTexel
            let c  = gNormal.Sample(v.tc)
            let l  = gNormal.Sample(v.tc + V2f(-ts.X, 0.0f))
            let r  = gNormal.Sample(v.tc + V2f( ts.X, 0.0f))
            let u  = gNormal.Sample(v.tc + V2f(0.0f,  ts.Y))
            let d  = gNormal.Sample(v.tc + V2f(0.0f, -ts.Y))
            let m0 = gColor.Sample(v.tc).W
            let ml = gColor.Sample(v.tc + V2f(-ts.X, 0.0f)).W
            let mr = gColor.Sample(v.tc + V2f( ts.X, 0.0f)).W
            let mu = gColor.Sample(v.tc + V2f(0.0f,  ts.Y)).W
            let md = gColor.Sample(v.tc + V2f(0.0f, -ts.Y)).W
            // depth edge (window depth in .W)
            let dEdge =
                max (max (abs (c.W - l.W)) (abs (c.W - r.W)))
                    (max (abs (c.W - u.W)) (abs (c.W - d.W)))
            // normal edge (decode *2-1)
            let nC = V3f(c.X, c.Y, c.Z) * 2.0f - V3f.III
            let nDiff (s : V4f) = 1.0f - Vec.dot nC (V3f(s.X, s.Y, s.Z) * 2.0f - V3f.III)
            let nEdge = max (max (nDiff l) (nDiff r)) (max (nDiff u) (nDiff d))
            // coverage-mask boundary (object silhouette + near-plane cut)
            let mEdge = if m0 > 0.5f then 1.0f - min (min ml mr) (min mu md) else 0.0f
            let isEdge = dEdge > 0.0015f || nEdge > 0.30f || mEdge > 0.5f
            if isEdge && (m0 > 0.5f || mEdge > 0.5f) then
                let col = gColor.Sample(v.tc)
                return V4f(col.X, col.Y, col.Z, 1.0f)
            else
                return V4f(0.0f, 0.0f, 0.0f, 0.0f)
        }
