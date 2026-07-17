namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open FShade

module RenderPass =
    let passMinusOne = RenderPass.main
    let passZero = RenderPass.after "zero" RenderPassOrder.Arbitrary passMinusOne
    let passOne = RenderPass.after "one" RenderPassOrder.Arbitrary passZero

// Per-fragment ghosting rules are documented in CLAUDE.md ("Render pipeline").
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
        // Slice mode (v12 §5): camera forward + cut-plane distance (the ortho
        // near, render units) + falloff width behind it. FadeDist 0 = off.
        member x.SliceFwd       : V3f     = x?SliceFwd
        member x.SliceFadeNear  : float32 = x?SliceFadeNear
        member x.SliceFadeDist  : float32 = x?SliceFadeDist
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
        // Outline G-buffer pass only: the mesh's identity ((index+1)/255 — 8-bit
        // exact in the Rgba8 target) written to target0.y, so the edge composite
        // can gate lines per mesh; 0 = background.
        member x.MeshId           : float32 = x?MeshId
        // Outline G-buffer pass only: small window-depth push applied to
        // silhouette-only context meshes, so co-located inspected surfaces win the
        // depth contest deterministically (no per-pixel ID/colour alternation
        // where epochs differ by only noise).
        member x.OutlineDepthBias : float32 = x?OutlineDepthBias
        // Show-overlays modifier: 1 → paint the mesh plain white (shading kept).
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
            // Slice-mode falloff (v12 §5): beyond the cut plane (the ortho near)
            // alpha falls off over SliceFadeDist and then discards — only the
            // profile band + a few cm stay visible. In front of the cut GL
            // near-clips the geometry, so no test is needed there.
            let mutable sliceMul = 1.0f
            if uniform.SliceFadeDist > 0.0f then
                let dAlong = Vec.dot (wp - uniform.CameraLocation) uniform.SliceFwd
                let over = dAlong - uniform.SliceFadeNear
                if over > uniform.SliceFadeDist then discard()
                elif over > 0.0f then sliceMul <- 1.0f - over / uniform.SliceFadeDist
            let mutable inAnyBlob = false
            let bc = uniform.BlobCount
            let blobsActive = bc > 0 && uniform.AnchorGhost <> 0
            // The distance loop only matters while pin isolation consumes it —
            // skip it entirely in the (common) non-Register modes.
            if blobsActive then
                for i in 0 .. MaxBlobs - 1 do
                    if i < bc then
                        let b      = uniform.Blobs.[i]
                        let inner  = b.W
                        let dx = wp.X - b.X
                        let dy = wp.Y - b.Y
                        let dz = wp.Z - b.Z
                        let d  = sqrt (dx*dx + dy*dy + dz*dz)
                        if d <= inner then inAnyBlob <- true
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
            alpha <- alpha * sliceMul
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
                    if step > 1e-9f && t < 1.0f then
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
                // Quality ≥ 0.75 reads fully green, so a larger share of a
                // well-formed mesh shows as good.
                let ts = clamp 0.0f 1.0f (v.shq / 0.75f)
                let loC = V3f(0.86f, 0.20f, 0.15f)
                let hiC = V3f(0.18f, 0.55f, 0.34f)
                baseRgb <- loC * (1.0f - ts) + hiC * ts
            // (World-Z isolines are NOT drawn here — they are edge-detected from a
            // band-parity field in the offscreen outline pass, so they get the same
            // crisp 1px look as the silhouette outline. See OutlineGBuffer/OutlineEdge.)
            // Show-overlays modifier: collapse the mesh to plain white (last, so
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

// Outline G-buffer pass: world-Z band parity + mesh id + window depth → target0,
// per-mesh palette colour + coverage mask → target1 (MRT). Depth is written from
// the fragment (fc.Z + OutlineDepthBias) so silhouette-only context meshes can be
// pushed behind the inspected pair.
[<ReflectedDefinition>]
module OutlineGBuffer =
    open MeshShader

    type FragIn = {
        [<Semantic("WorldPosition")>] wp : V4f
        [<FragCoord>]                 fc : V4f
    }
    type FragOut = {
        [<Color>]                 g0 : V4f
        [<Semantic("Outline1")>]  g1 : V4f
        [<Depth>]                 depth : float32
    }
    let shade (v : FragIn) =
        fragment {
            let col = uniform.MeshColor
            // target0.x = world-Z band parity (0/1) → edge-detected into crisp 1px
            // isolines, world-locked since the band index is a pure function of world Z.
            // target0.y = mesh id ((index+1)/255; 0 = background) → per-mesh line gating.
            // target0.w = window depth (silhouette/depth edge).
            let parity =
                if uniform.ContourSpacing > 1e-12f then
                    let band = floor (v.wp.Z / uniform.ContourSpacing)
                    band - 2.0f * floor (band * 0.5f)
                else 0.0f
            return {
                g0 = V4f(parity, uniform.MeshId, 0.0f, v.fc.Z)
                g1 = V4f(col.X, col.Y, col.Z, 1.0f)
                depth = v.fc.Z + uniform.OutlineDepthBias
            }
        }

// Additive per-mesh coverage pass: each mesh accumulates a constant into ITS OWN
// channel (index 0-3 → target0.rgba, 4-7 → target1.rgba) with NO depth — pure
// screen-space footprints, occlusion-free. The coverage-edge composite turns each
// channel's covered↔uncovered transition into that mesh's own closed contour, so
// overlapping/co-located meshes get separate outlines (the depth-tested combined
// G-buffer can only ever outline the visible union — no depth break where one
// mesh ends over another).
[<ReflectedDefinition>]
module OutlineCoverage =
    type UniformScope with
        member x.CoverageChannel : int = x?CoverageChannel
    type Vtx = { [<Position>] pos : V4f }
    type FragOut = {
        [<Color>]                 c0 : V4f
        [<Semantic("Coverage1")>] c1 : V4f
    }
    let shade (v : Vtx) =
        fragment {
            let k = uniform.CoverageChannel
            let mutable a = V4f(0.0f, 0.0f, 0.0f, 0.0f)
            let mutable b = V4f(0.0f, 0.0f, 0.0f, 0.0f)
            if k = 0 then a <- V4f(0.25f, 0.0f, 0.0f, 0.0f)
            elif k = 1 then a <- V4f(0.0f, 0.25f, 0.0f, 0.0f)
            elif k = 2 then a <- V4f(0.0f, 0.0f, 0.25f, 0.0f)
            elif k = 3 then a <- V4f(0.0f, 0.0f, 0.0f, 0.25f)
            elif k = 4 then b <- V4f(0.25f, 0.0f, 0.0f, 0.0f)
            elif k = 5 then b <- V4f(0.0f, 0.25f, 0.0f, 0.0f)
            elif k = 6 then b <- V4f(0.0f, 0.0f, 0.25f, 0.0f)
            else b <- V4f(0.0f, 0.0f, 0.0f, 0.25f)
            return { c0 = a; c1 = b }
        }

// Placement-suitability coverage pass (v12 §2, placement-armed only): like
// OutlineCoverage (additive, occlusion-free, one channel per mesh, cap 8) but
// each fragment writes a SHAPE-WEIGHTED value 0.25·(0.2 + 0.8·quality) — so the
// composite can read both "covered" (value above a floor even at quality 0) and
// an approximate per-mesh shape quality at that pixel. Multiple surface layers
// along the ray add up; the composite clamps, biasing the estimate crisp — an
// accepted approximation for terrain-like scans.
[<ReflectedDefinition>]
module SuitabilityCoverage =
    type UniformScope with
        member x.CoverageChannel : int = x?CoverageChannel
    type Vtx = {
        [<Position>]           pos : V4f
        [<Semantic("ShapeQ")>] shq : float32
    }
    type FragOut = {
        [<Color>]                 c0 : V4f
        [<Semantic("Coverage1")>] c1 : V4f
    }
    let shade (v : Vtx) =
        fragment {
            let s = 0.25f * (0.2f + 0.8f * (clamp 0.0f 1.0f v.shq))
            let k = uniform.CoverageChannel
            let mutable a = V4f(0.0f, 0.0f, 0.0f, 0.0f)
            let mutable b = V4f(0.0f, 0.0f, 0.0f, 0.0f)
            if k = 0 then a <- V4f(s, 0.0f, 0.0f, 0.0f)
            elif k = 1 then a <- V4f(0.0f, s, 0.0f, 0.0f)
            elif k = 2 then a <- V4f(0.0f, 0.0f, s, 0.0f)
            elif k = 3 then a <- V4f(0.0f, 0.0f, 0.0f, s)
            elif k = 4 then b <- V4f(s, 0.0f, 0.0f, 0.0f)
            elif k = 5 then b <- V4f(0.0f, s, 0.0f, 0.0f)
            elif k = 6 then b <- V4f(0.0f, 0.0f, s, 0.0f)
            else b <- V4f(0.0f, 0.0f, 0.0f, s)
            return { c0 = a; c1 = b }
        }

// Fused placement-suitability composite (v12 §2): per pixel, count the covered
// suitability channels. 0 → transparent (no surface); 1 → flat textureless grey
// (the region visibly loses detail — placement is prohibited there); ≥2 → a
// screen-space hatch WOVEN from the covered meshes' palette colours, its
// saturation/crispness modulated by the MIN shape quality across them (crisp +
// saturated where all are well-formed, muted/muddy where any is poor).
// Semi-transparent throughout, and the isoline/outline composite draws after it
// — so isolines and shape indicators stay readable through the overlay.
[<ReflectedDefinition>]
module SuitabilityComposite =

    type UniformScope with
        member x.CoverageColors : Arr<N<8>, V4f> = x?CoverageColors

    let private suit0 =
        sampler2d {
            texture uniform?Suit0
            filter Filter.MinMagPoint
            addressU WrapMode.Clamp
            addressV WrapMode.Clamp
        }
    let private suit1 =
        sampler2d {
            texture uniform?Suit1
            filter Filter.MinMagPoint
            addressU WrapMode.Clamp
            addressV WrapMode.Clamp
        }

    type Frag = {
        [<Position>]        pos : V4f
        [<Semantic("OTc")>] tc  : V2f
        [<FragCoord>]       fc  : V4f
    }

    let fragment (v : Frag) =
        fragment {
            let cA = suit0.Sample(v.tc)
            let cB = suit1.Sample(v.tc)
            let vals = Arr<N<8>, float32>()
            vals.[0] <- cA.X
            vals.[1] <- cA.Y
            vals.[2] <- cA.Z
            vals.[3] <- cA.W
            vals.[4] <- cB.X
            vals.[5] <- cB.Y
            vals.[6] <- cB.Z
            vals.[7] <- cB.W
            let th = 0.04f
            let mutable n = 0
            let mutable minShape = 1.0f
            let mutable avg = V3f(0.0f, 0.0f, 0.0f)
            for i in 0 .. 7 do
                if vals.[i] > th then
                    n <- n + 1
                    let sh = clamp 0.0f 1.0f ((vals.[i] * 4.0f - 0.2f) / 0.8f)
                    if sh < minShape then minShape <- sh
                    avg <- avg + uniform.CoverageColors.[i].XYZ
            if n = 0 then
                return V4f(0.0f, 0.0f, 0.0f, 0.0f)
            elif n = 1 then
                return V4f(0.62f, 0.63f, 0.66f, 0.78f)
            else
                let avgC = avg / float32 n
                // Diagonal weave: consecutive screen bands cycle through the
                // covered meshes' colours (no colour cap below the 8-channel MRT).
                let band = int (floor ((v.fc.X + v.fc.Y) / 6.0f))
                let sel = ((band % n) + n) % n
                let mutable cnt = 0
                let mutable stripeCol = avgC
                for i in 0 .. 7 do
                    if vals.[i] > th then
                        if cnt = sel then stripeCol <- uniform.CoverageColors.[i].XYZ
                        cnt <- cnt + 1
                let lum = 0.299f * stripeCol.X + 0.587f * stripeCol.Y + 0.114f * stripeCol.Z
                let greyC = V3f(lum, lum, lum)
                let satCol = greyC + (stripeCol - greyC) * (0.25f + 0.75f * minShape)
                let colr = avgC + (satCol - avgC) * (0.3f + 0.7f * minShape)
                let alpha = 0.35f + 0.2f * minShape
                return V4f(colr.X, colr.Y, colr.Z, alpha)
        }

// Edge-detect fullscreen pass: sample the g-buffer at centre ±1 texel and paint the
// per-pixel palette colour where an edge is found (transparent else). An edge = a
// window-depth BREAK (silhouette/cliff — a SECOND difference of depth so smooth slopes
// don't register; see below) OR a world-Z band-parity flip (world-locked isolines),
// both gated to covered pixels. The depth break alone traces the silhouette in this
// data — no normal-angle or coverage-mask term is needed.
[<ReflectedDefinition>]
module OutlineEdge =

    type UniformScope with
        member x.OutlineTexel : V2f = x?OutlineTexel
        member x.OutlineThreshold : float32 = x?OutlineThreshold
        // Alpha of the grey elevation isolines (gear slider; silhouettes stay opaque).
        member x.IsolineOpacity : float32 = x?IsolineOpacity
        // Per-mesh line gate, indexed by the G-buffer mesh id (target0.y):
        // .X = 1 → silhouette + isolines, 0.5 → silhouette only (Inspect pair
        // view context), 0 → no lines (the mesh still occludes in the G-buffer).
        member x.OutlineMask : Arr<N<32>, V4f> = x?OutlineMask
        // Slice mode (v12 §5 follow-up): line alpha falls off with the stored
        // window depth — valid because the slice camera is ORTHO, where window
        // depth is linear in eye distance and 0 sits exactly at the cut plane.
        // Value = 1/fade-range in depth01 units; 0 = off (perspective views).
        member x.OutlineDistFade : float32 = x?OutlineDistFade

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
                // Per-mesh gate: the centre pixel's mesh id selects its mask slot.
                let slot = min 31 (max 0 (int (c.Y * 255.0f + 0.5f) - 1))
                let flag = uniform.OutlineMask.[slot].X
                // Slice-mode distance falloff: lines fade with depth behind the
                // cut (c.W = 0 at the near plane under the slice ortho), and the
                // opacity is CAPPED at 10 % across the board — the terrain
                // profiles own the view; lines are background reference only.
                let capOn = uniform.OutlineDistFade > 0.0f
                let distMul =
                    if capOn then max 0.0f (1.0f - c.W * uniform.OutlineDistFade)
                    else 1.0f
                // Silhouette / cliff (a window-depth break) keeps the crisp per-mesh
                // palette colour. A pure world-Z band-parity flip (an isoline) renders
                // in a faint neutral grey at reduced intensity, so elevation
                // contours read as subtle background reference, not bold palette lines.
                // (No local helper — FShade bodies must stay lambda-free.)
                if depthEdge && flag > 0.25f then
                    let col = gColor.Sample(v.tc)
                    let a = if capOn then min 0.1f distMul else distMul
                    return V4f(col.X, col.Y, col.Z, a)
                elif isoEdge && flag > 0.75f then
                    let a0 = uniform.IsolineOpacity * distMul
                    let a = if capOn then min 0.1f a0 else a0
                    return V4f(0.55f, 0.57f, 0.60f, a)
                else
                    return V4f(0.0f, 0.0f, 0.0f, 0.0f)
            else
                return V4f(0.0f, 0.0f, 0.0f, 0.0f)
        }

// Footprint-contour composite over the coverage MRT: a channel's covered pixel
// with an uncovered 4-neighbour is that mesh's OWN boundary (1px inside, closed,
// independent of what covers it) — gated by the same per-mesh OutlineMask
// (> 0.25 like the silhouettes) and painted in the mesh's palette colour.
// Coincident boundaries: the highest mesh index wins the pixel.
[<ReflectedDefinition>]
module OutlineCoverageEdge =

    type UniformScope with
        member x.OutlineTexel : V2f = x?OutlineTexel
        member x.OutlineMask : Arr<N<32>, V4f> = x?OutlineMask
        member x.CoverageColors : Arr<N<8>, V4f> = x?CoverageColors

    let private cov0 =
        sampler2d {
            texture uniform?Coverage0
            filter Filter.MinMagPoint
            addressU WrapMode.Clamp
            addressV WrapMode.Clamp
        }
    let private cov1 =
        sampler2d {
            texture uniform?Coverage1
            filter Filter.MinMagPoint
            addressU WrapMode.Clamp
            addressV WrapMode.Clamp
        }

    let fragment (v : OutlineEdge.Vtx) =
        fragment {
            let ts = uniform.OutlineTexel
            let th = 0.12f
            let cA = cov0.Sample(v.tc)
            let lA = cov0.Sample(v.tc + V2f(-ts.X, 0.0f))
            let rA = cov0.Sample(v.tc + V2f( ts.X, 0.0f))
            let uA = cov0.Sample(v.tc + V2f(0.0f,  ts.Y))
            let dA = cov0.Sample(v.tc + V2f(0.0f, -ts.Y))
            let cB = cov1.Sample(v.tc)
            let lB = cov1.Sample(v.tc + V2f(-ts.X, 0.0f))
            let rB = cov1.Sample(v.tc + V2f( ts.X, 0.0f))
            let uB = cov1.Sample(v.tc + V2f(0.0f,  ts.Y))
            let dB = cov1.Sample(v.tc + V2f(0.0f, -ts.Y))
            let mutable col = V4f(0.0f, 0.0f, 0.0f, 0.0f)
            if cA.X > th && (lA.X < th || rA.X < th || uA.X < th || dA.X < th) && uniform.OutlineMask.[0].X > 0.25f then col <- uniform.CoverageColors.[0]
            if cA.Y > th && (lA.Y < th || rA.Y < th || uA.Y < th || dA.Y < th) && uniform.OutlineMask.[1].X > 0.25f then col <- uniform.CoverageColors.[1]
            if cA.Z > th && (lA.Z < th || rA.Z < th || uA.Z < th || dA.Z < th) && uniform.OutlineMask.[2].X > 0.25f then col <- uniform.CoverageColors.[2]
            if cA.W > th && (lA.W < th || rA.W < th || uA.W < th || dA.W < th) && uniform.OutlineMask.[3].X > 0.25f then col <- uniform.CoverageColors.[3]
            if cB.X > th && (lB.X < th || rB.X < th || uB.X < th || dB.X < th) && uniform.OutlineMask.[4].X > 0.25f then col <- uniform.CoverageColors.[4]
            if cB.Y > th && (lB.Y < th || rB.Y < th || uB.Y < th || dB.Y < th) && uniform.OutlineMask.[5].X > 0.25f then col <- uniform.CoverageColors.[5]
            if cB.Z > th && (lB.Z < th || rB.Z < th || uB.Z < th || dB.Z < th) && uniform.OutlineMask.[6].X > 0.25f then col <- uniform.CoverageColors.[6]
            if cB.W > th && (lB.W < th || rB.W < th || uB.W < th || dB.W < th) && uniform.OutlineMask.[7].X > 0.25f then col <- uniform.CoverageColors.[7]
            return col
        }
