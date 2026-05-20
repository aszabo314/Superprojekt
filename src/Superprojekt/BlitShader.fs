namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering

// Weighted Blended Order-Independent Transparency.
// Ported from C:\temp\trash\Shader.WeightedBlendedOIT.fs (Aardvark reference).
// http://casual-effects.blogspot.com/2015/03/implemented-weighted-blended-order.html
//
// Pipeline:
//   - Every 3D leaf shader chain ends in OIT.weightedBlend, which emits MRT
//     outputs to two attachments: Accum (Rgba16f, additive blend) and
//     Revealage (Rgba8, Zero/InvSourceColor blend → ∏(1-α)).
//   - A depth pre-pass populates a shared depth buffer with the closest
//     fully-opaque fragment per pixel; the OIT pass depth-tests against it.
//   - OIT.compose reads back Accum + Revealage and produces (baseColor, density)
//     to alpha-blend over the screen.
module OIT =

    open FShade

    let AccumSemantic     = Sym.ofString "Accum"
    let RevealageSemantic = Sym.ofString "Revealage"

    let revealageBlendMode =
        { BlendMode.Blend with
            SourceColorFactor      = BlendFactor.Zero
            DestinationColorFactor = BlendFactor.InvSourceColor
            SourceAlphaFactor      = BlendFactor.Zero
            DestinationAlphaFactor = BlendFactor.InvSourceAlpha }

    [<ReflectedDefinition>]
    module Shaders =

        // All shader math is in float32 / V4f / V3f to match GLSL ES single-precision.

        type Fragment =
            {
                [<Color>]     c  : V4f
                [<FragCoord>] fc : V4f
            }

        type Output =
            {
                [<Semantic("Accum")>]     accum     : V4f
                [<Semantic("Revealage")>] revealage : V4f
            }

        let weightedBlend (f : Fragment) =
            fragment {
                let alpha = f.c.W
                if alpha < 1e-4f then discard()
                let a = alpha * 8.0f + 0.01f
                let b = -f.fc.Z * 0.95f + 1.0f
                let w = clamp 1e-2f 3e2f (a * a * a * 1e8f * b * b * b)
                let color = V4f(f.c.XYZ * alpha, alpha) * w
                return { accum = color; revealage = V4f(alpha, 0.0f, 0.0f, 0.0f) }
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

        let compose (v : ComposeVertex) =
            fragment {
                let accum0    = accumSampler.SampleLevel(v.tc, 0.0f)
                let revealage = revealageSampler.SampleLevel(v.tc, 0.0f).X
                let accum =
                    if isInfinity accum0 then V4f(accum0.W)
                    else accum0
                let baseColor = accum.XYZ / max accum.W 1e-5f
                let density = 1.0f - revealage
                return V4f(baseColor, density)
            }

    let weightedBlend = Shaders.weightedBlend
    let compose       = Shaders.compose
