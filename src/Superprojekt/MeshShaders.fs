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
        // Blobs = (cx,cy,cz,innerR) in render space. AnchorGhost = 0 disables the
        // blob alpha filter but the array stays uploaded.
        member x.BlobCount       : int     = x?BlobCount
        member x.Blobs           : Arr<N<32>, V4f> = x?Blobs
        member x.AnchorGhost     : int     = x?AnchorGhost
        // Render-space plane equations V4f(nx,ny,nz,-dot(n,renderOrigin));
        // dot(n,wp)+w > 0 is the removed (camera-side) half.
        member x.ClipPlaneCount : int = x?ClipPlaneCount
        member x.ClipPlane0     : V4f = x?ClipPlane0
        member x.ClipPlane1     : V4f = x?ClipPlane1
        // Per-vertex SurfaceDist painter (above-ghost only): 0 = none;
        // 1 = signed difference, diverging blue↔grey↔red (soloed moving mesh, Inspect
        //     Difference); 2 = variance std ≥0, sequential grey→red (reference,
        //     Inspect ensemble); 3 = displacement magnitude ≥0, sequential light→blue
        //     (soloed moving mesh, Inspect Displacement). DistScale saturates the
        //     positive end, DistLoNeg (enc 1 only) the |negative| end — both come from
        //     the ONE pin-derived Inspect range (§C, ScanPin.inspectRange), so every
        //     map shares a scale. SurfaceDist = 1e30 → keep base colour.
        member x.DistanceEncoding : int     = x?DistanceEncoding
        member x.DistScale        : float32 = x?DistScale
        member x.DistLoNeg        : float32 = x?DistLoNeg
        // Value step (m) of the difference isolines (enc 1 only); 0 disables.
        member x.DiffIsoStep      : float32 = x?DiffIsoStep
        // 0 = off, 1 = incidence, 2 = range, 3 = shape.
        member x.HeatmapMode      : int     = x?HeatmapMode
        member x.SensorOrigin     : V3f     = x?SensorOrigin
        member x.RangeMax         : float32 = x?RangeMax
        // Shp cutoff: fragments below this quality are discarded (transparent).
        member x.ShapeThreshold   : float32 = x?ShapeThreshold
        // Render-space Z step between global-Z-locked elevation isolines; 0 disables.
        // Read by the outline G-buffer pass (band parity → edge-detect), not by the
        // forward mesh shader itself.
        member x.ContourSpacing   : float32 = x?ContourSpacing
        // Show-overlays modifier (§T8): 1 → paint the mesh plain white (shading kept).
        // Pins are separate geometry (unaffected), so only they carry colour.
        member x.Whiteout         : float32 = x?Whiteout
        // Inspect de-clutter (§B5): 1 → the base surface is a plain near-white
        // (no photo texture / palette / slope), so the false-colour painters above
        // it are the only filled signal. Shading still applies.
        member x.InspectPlain     : float32 = x?InspectPlain

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
            // Sectioning (mesh only; overlays never clipped): the camera-side
            // half (dot(n,wp)+w > 0) is discarded.
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
                elif uniform.InspectPlain > 0.5f then V3f(0.957f, 0.969f, 0.980f)
                elif uniform.RenderingMode = 1 then uniform.MeshColor.XYZ
                elif uniform.RenderingMode = 2 then slopeCol
                else v.c.XYZ
            // Difference map (soloed moving mesh, Inspect): signed distance on the
            // Coolwarm diverging map (§C, CET-D01) — zero = near-white centre
            // (welded to 0), + through salmon to red, − through lavender to blue,
            // each sign normalized by its own end, near-zero t^0.6 boost. Mirrors
            // Primitives.Diff and the focus difference tile. On top: constant-value
            // isolines every DiffIsoStep metres (derivative-antialiased darkening),
            // suppressed beyond the range where the colour clamps.
            if uniform.DistanceEncoding = 1 && aboveGhost then
                let d = v.sd
                if abs d < 1e20f then
                    let hiP = max 1e-6f uniform.DistScale
                    let hiN = max 1e-6f uniform.DistLoNeg
                    let t = if d >= 0.0f then d / hiP else -d / hiN
                    let m = pow (min 1.0f t) 0.6f
                    let zeroC = V3f(0.930f, 0.907f, 0.917f)
                    let midC = if d >= 0.0f then V3f(0.906f, 0.549f, 0.464f) else V3f(0.627f, 0.612f, 0.908f)
                    let endC = if d >= 0.0f then V3f(0.752f, 0.008f, 0.022f) else V3f(0.128f, 0.316f, 0.858f)
                    baseRgb <-
                        if m < 0.5f then zeroC + (midC - zeroC) * (m * 2.0f)
                        else midC + (endC - midC) * ((m - 0.5f) * 2.0f)
                    let step = uniform.DiffIsoStep
                    if step > 1e-9f && t <= 1.0f then
                        let x = d / step
                        let g = abs (x - floor (x + 0.5f))
                        let aa = max (abs (ddx x) + abs (ddy x)) 1e-6f
                        // Fade lines out where contours pack denser than ~2 px apart
                        // (steep/grazing fragments) — else they smear into a dark blotch.
                        let fade = clamp 0.0f 1.0f ((0.5f - aa) * 4.0f)
                        let line = 0.45f + 0.55f * min 1.0f (g / (aa * 1.3f))
                        baseRgb <- baseRgb * (1.0f - fade * (1.0f - line))
            // Variance map: per-reference-vertex disagreement std (≥0) from light
            // grey to strong red, normalised by DistScale.
            if uniform.DistanceEncoding = 2 && aboveGhost then
                let d = v.sd
                if abs d < 1e20f then
                    let scale = max 1e-6f uniform.DistScale
                    let tt = clamp 0.0f 1.0f (d / scale)
                    let loC = V3f(0.945f, 0.961f, 0.976f)
                    let hiC = V3f(0.725f, 0.110f, 0.110f)
                    baseRgb <- loC * (1.0f - tt) + hiC * tt
            // Displacement magnitude (soloed moving mesh, Inspect): |load→solved| ≥0,
            // light → dark blue — matches the focus displacement tile.
            if uniform.DistanceEncoding = 3 && aboveGhost then
                let d = v.sd
                if abs d < 1e20f then
                    let hi = max 1e-6f uniform.DistScale
                    let tt = clamp 0.0f 1.0f (d / hi)
                    baseRgb <- V3f(0.93f, 0.94f, 0.98f) * (1.0f - tt) + V3f(0.118f, 0.227f, 0.541f) * tt
            // Incidence heatmap: incidence angle to the scan sensor (the mesh's
            // panorama centre, fed via SensorOrigin), grazing = red, head-on = green.
            // Uses the GEOMETRIC (per-triangle, from screen-space derivatives) normal,
            // sign-oriented by the stored vertex normal — smoothed vertex normals let
            // grazing sliver/bridging triangles read head-on. No abs: a surface facing
            // AWAY from the sensor cannot have been scanned, so it reads worst, not best.
            if uniform.HeatmapMode = 1 && aboveGhost then
                let toSensor = (uniform.SensorOrigin - wp) |> Vec.normalize
                let gx = V3f(ddx wp.X, ddx wp.Y, ddx wp.Z)
                let gy = V3f(ddy wp.X, ddy wp.Y, ddy wp.Z)
                let g0 = Vec.cross gx gy
                let nG = if g0.Length > 1e-12f then g0 |> Vec.normalize else n
                let nGo = if Vec.dot nG n < 0.0f then -nG else nG
                let incid = max 0.0f (Vec.dot nGo toSensor)
                let lo  = V3f(0.84f, 0.19f, 0.15f)
                let mid = V3f(0.99f, 0.85f, 0.30f)
                let hi  = V3f(0.18f, 0.55f, 0.34f)
                baseRgb <-
                    if incid < 0.5f then lo + (mid - lo) * (incid * 2.0f)
                    else mid + (hi - mid) * ((incid - 0.5f) * 2.0f)
            // Range heatmap: distance from the scan sensor (SensorOrigin = the mesh's
            // panorama centre) over its max range, near = blue → far = red.
            if uniform.HeatmapMode = 2 && aboveGhost then
                let rng = (wp - uniform.SensorOrigin).Length
                let tr  = clamp 0.0f 1.0f (rng / max 1e-6f uniform.RangeMax)
                let nearC = V3f(0.13f, 0.40f, 0.85f)
                let farC  = V3f(0.86f, 0.20f, 0.15f)
                baseRgb <- nearC * (1.0f - tr) + farC * tr
            // Shape heatmap: per-vertex triangle quality (4√3·A/Σl², 1 =
            // equilateral, →0 = thin/degenerate). Red = poor, green = good.
            // Below the cutoff the fragment is discarded (transparent filter).
            if uniform.HeatmapMode = 3 && aboveGhost then
                if v.shq < uniform.ShapeThreshold then discard()
                // Raise the green threshold: quality ≥ 0.75 reads fully green, so a
                // larger share of a well-formed mesh shows as good.
                let ts = clamp 0.0f 1.0f (v.shq / 0.75f)
                let loC = V3f(0.86f, 0.20f, 0.15f)
                let hiC = V3f(0.18f, 0.55f, 0.34f)
                baseRgb <- loC * (1.0f - ts) + hiC * ts
            // (World-Z isolines are NOT drawn here — they are edge-detected from a
            // band-parity field in the offscreen outline pass, so they get the same
            // crisp 1px look as the silhouette outline. See OutlineGBuffer/OutlineEdge.)
            // Show-overlays modifier (§T8): collapse the mesh to plain white (last, so
            // every false-colour map above is overridden too) — only the
            // separately-rendered pin geometry carries colour while held.
            if uniform.Whiteout > 0.5f then
                baseRgb <- V3f(1.0f, 1.0f, 1.0f)
            let depth =
                if alpha >= opaqueThreshold then v.fc.Z
                else 1.0f
            return {
                color = V4f(baseRgb * shade, alpha)
                depth = depth
            }
        }

// Outline G-buffer pass: world-Z band parity + window depth → target0, per-mesh
// palette colour + coverage mask → target1 (MRT). (target0.xyz once held the world
// normal for the removed normal-angle term; .x is now band parity, .yz unused.)
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
            let col = uniform.MeshColor
            // target0.x = world-Z band parity (0/1) → edge-detected into crisp 1px
            // isolines, world-locked since the band index is a pure function of world Z.
            // target0.w = window depth (silhouette/depth edge); .yz free (normal-angle removed).
            let parity =
                if uniform.ContourSpacing > 1e-12f then
                    let band = floor (v.wp.Z / uniform.ContourSpacing)
                    band - 2.0f * floor (band * 0.5f)
                else 0.0f
            return {
                g0 = V4f(parity, 0.0f, 0.0f, v.fc.Z)
                g1 = V4f(col.X, col.Y, col.Z, 1.0f)
            }
        }

// Edge-detect fullscreen pass: sample the g-buffer at centre ±1 texel and paint the
// per-pixel palette colour where an edge is found (transparent else). An edge = a
// window-depth BREAK (silhouette/cliff — a SECOND difference of depth so smooth slopes
// don't register; see below) OR a world-Z band-parity flip (world-locked isolines),
// both gated to covered pixels. The old normal-angle term and the coverage-mask (mEdge)
// term were dropped — the depth break already traces the silhouette in this data.
[<ReflectedDefinition>]
module OutlineEdge =

    type UniformScope with
        member x.OutlineTexel : V2f = x?OutlineTexel
        member x.OutlineThreshold : float32 = x?OutlineThreshold

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
            // target0: .w = window depth → silhouette/cliff outline; .x = world-Z
            // band parity → world-locked isolines.
            //   dEdge is the SECOND difference (depth Laplacian) of window depth, not
            //   the first difference: window-space gl_FragCoord.z is linear in screen
            //   space across any planar primitive, so |l + r - 2c| is ~0 on a smooth
            //   slope at any view angle/distance and spikes only at a genuine break
            //   (silhouette/cliff/occlusion) — at a clean step it equals the full
            //   jump. The old first difference only measured screen-space depth slope,
            //   so it lit up every grazing/near surface as false banded lines.
            //   iEdge stays a first difference — parity is a step function, so any
            //   flip is already a real band boundary.
            let dEdge =
                max (abs (l.W + r.W - 2.0f * c.W))
                    (abs (u.W + d.W - 2.0f * c.W))
            let iEdge =
                max (max (abs (c.X - l.X)) (abs (c.X - r.X)))
                    (max (abs (c.X - u.X)) (abs (c.X - d.X)))
            let depthEdge = dEdge > uniform.OutlineThreshold
            let isoEdge   = iEdge > 0.5f
            if (depthEdge || isoEdge) && m0 > 0.5f then
                // Silhouette / cliff (a window-depth break) keeps the crisp per-mesh
                // palette colour. A pure world-Z band-parity flip (an isoline) renders
                // in a faint neutral grey at reduced intensity (§T10), so elevation
                // contours read as subtle background reference, not bold palette lines.
                if depthEdge then
                    let col = gColor.Sample(v.tc)
                    return V4f(col.X, col.Y, col.Z, 1.0f)
                else
                    return V4f(0.55f, 0.57f, 0.60f, 0.45f)
            else
                return V4f(0.0f, 0.0f, 0.0f, 0.0f)
        }
