namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering

// Hybrid Forward / Weighted-Blended-OIT pipeline.
//
// Two render passes share a single FBO with three colour attachments and a
// shared depth attachment:
//   - ForwardColor (Rgba8, alpha-blend)  : closest α ≥ τ fragment per pixel.
//   - Accum       (Rgba16f, additive)    : WBOIT numerator.
//   - Revealage   (Rgba8, Zero/InvSrc)   : WBOIT ∏(1−α) denominator.
//
// Pass 1 (forward opaque, DepthMask=ON, LessOrEqual):
//   • IsForwardPass uniform = true.
//   • Fragments with α < τ are discarded.
//   • Surviving fragments compete via depth test; the front-most α≥τ fragment
//     per pixel wins. Output: forwardColor = (rgb, 1), accum/revealage = 0.
//
// Pass 2 (WBOIT translucent, DepthMask=OFF, LessOrEqual against pass-1 depth):
//   • IsForwardPass uniform = false.
//   • Fragments with α ≥ τ are discarded.
//   • Translucent fragments behind the closest opaque are depth-rejected
//     (they are occluded — accepted trade-off for "ghost-through-opaque").
//   • Translucent fragments in front of all opaques accumulate via WBOIT.
//
// Compose ("WBOIT over Forward"):
//   density   = 1 − revealage.x
//   oitColor  = accum.rgb / max(accum.w, ε)
//   finalRGB  = oitColor·density + forward.rgb·forward.a·(1 − density)
//   finalA    = density + forward.a·(1 − density)
//
// When pass 1 wrote opaque (forward.a = 1) and nothing translucent stacked on
// top (density = 0) → finalRGB = forward.rgb, finalA = 1. No WBOIT bleed.
// When pass 2 accumulated ghosts on top of an opaque (density = α_ghost) →
// finalRGB = α·ghost + (1−α)·opaque, properly composited.
module OIT =

    open FShade

    let ForwardColorSemantic = Sym.ofString "ForwardColor"
    let AccumSemantic        = Sym.ofString "Accum"
    let RevealageSemantic    = Sym.ofString "Revealage"

    let revealageBlendMode =
        { BlendMode.Blend with
            SourceColorFactor      = BlendFactor.Zero
            DestinationColorFactor = BlendFactor.InvSourceColor
            SourceAlphaFactor      = BlendFactor.Zero
            DestinationAlphaFactor = BlendFactor.InvSourceAlpha }

    // α threshold splitting forward-opaque from WBOIT. A higher value puts more
    // of the lasso/blob soft transition into WBOIT (smoother boundary at the
    // expense of slightly more bleed-prone WBOIT mass near α=1). A lower value
    // pushes more of the transition into the forward path (less bleed, slightly
    // bigger visual seam at α≈τ).
    [<Literal>]
    let private alphaThreshold = 0.99f

    /// Width of the smoothstep band below `alphaThreshold` inside which the
    /// WBOIT-path revealage write is inflated toward 1.0, bridging `density`
    /// continuously across the forward/WBOIT seam. Set to 0.0 to disable
    /// (restores the raw discontinuity).
    [<Literal>]
    let private alphaBridgeBand = 0.02f

    [<ReflectedDefinition>]
    module Shaders =

        type UniformScope with
            // Pass A (true) routes α≥τ to ForwardColor; pass B (false) routes
            // α<τ to Accum/Revealage. Outer scenes set this; pin/line shaders
            // ignore it and always emit WBOIT.
            member x.IsForwardPass : bool = x?IsForwardPass

        type Fragment =
            {
                [<Color>]     c  : V4f
                [<FragCoord>] fc : V4f
            }

        type Output =
            {
                [<Semantic("ForwardColor")>] forward   : V4f
                [<Semantic("Accum")>]        accum     : V4f
                [<Semantic("Revealage")>]    revealage : V4f
            }

        // Hybrid shader for meshes that participate in both passes.
        // Branches on IsForwardPass + alphaThreshold.
        let hybridBlend (f : Fragment) =
            fragment {
                let alpha = f.c.W
                if alpha < 1e-4f then discard()
                if uniform.IsForwardPass then
                    if alpha < alphaThreshold then discard()
                    return {
                        forward   = V4f(f.c.XYZ, 1.0f)
                        accum     = V4f.Zero
                        revealage = V4f.Zero
                    }
                else
                    if alpha >= alphaThreshold then discard()
                    let a = alpha * 8.0f + 0.01f
                    let b = -f.fc.Z * 0.95f + 1.0f
                    let w = clamp 1e-2f 3e2f (a * a * a * 1e8f * b * b * b)
                    let color = V4f(f.c.XYZ * alpha, alpha) * w
                    let bandLo = alphaThreshold - alphaBridgeBand
                    let t      = clamp 0.0f 1.0f ((alpha - bandLo) / alphaBridgeBand)
                    let s      = t * t * (3.0f - 2.0f * t)
                    let aRev   = alpha * (1.0f - s) + s
                    return {
                        forward   = V4f.Zero
                        accum     = color
                        revealage = V4f(aRev, 0.0f, 0.0f, 0.0f)
                    }
            }

        // Pure WBOIT shader for content that is always translucent (pins, lines,
        // gizmos). Emits zero into ForwardColor so it never participates in the
        // forward pass even if the surrounding scene is rendered twice — the
        // blend modes treat src=0 as a no-op.
        let weightedBlend (f : Fragment) =
            fragment {
                let alpha = f.c.W
                if alpha < 1e-4f then discard()
                let a = alpha * 8.0f + 0.01f
                let b = -f.fc.Z * 0.95f + 1.0f
                let w = clamp 1e-2f 3e2f (a * a * a * 1e8f * b * b * b)
                let color = V4f(f.c.XYZ * alpha, alpha) * w
                return {
                    forward   = V4f.Zero
                    accum     = color
                    revealage = V4f(alpha, 0.0f, 0.0f, 0.0f)
                }
            }

        let private forwardSampler =
            sampler2d {
                texture uniform?ForwardColorTexture
                filter Filter.MinMagPoint
                addressU WrapMode.Clamp
                addressV WrapMode.Clamp
            }

        let private accumSampler =
            sampler2d {
                texture uniform?AccumTexture
                filter Filter.MinMagPoint
                addressU WrapMode.Clamp
                addressV WrapMode.Clamp
            }

        let private revealageSampler =
            sampler2d {
                texture uniform?RevealageTexture
                filter Filter.MinMagPoint
                addressU WrapMode.Clamp
                addressV WrapMode.Clamp
            }

        type ComposeVertex =
            {
                [<Position>] pos : V4f
                [<TexCoord>] tc  : V2f
            }

        // "WBOIT over Forward": forward sits underneath any translucent fragments
        // that were in front of it (or on a pixel where no opaque was written).
        let compose (v : ComposeVertex) =
            fragment {
                let accum0    = accumSampler.SampleLevel(v.tc, 0.0f)
                let revealage = revealageSampler.SampleLevel(v.tc, 0.0f).X
                let forward   = forwardSampler.SampleLevel(v.tc, 0.0f)
                let accum =
                    if isInfinity accum0 then V4f(accum0.W)
                    else accum0
                let oitColor = accum.XYZ / max accum.W 1e-5f
                let density  = 1.0f - revealage
                let inv      = 1.0f - density
                let finalRGB = oitColor * density + forward.XYZ * forward.W * inv
                let finalA   = density + forward.W * inv
                return V4f(finalRGB, finalA)
            }

    let hybridBlend   = Shaders.hybridBlend
    let weightedBlend = Shaders.weightedBlend
    let compose       = Shaders.compose
