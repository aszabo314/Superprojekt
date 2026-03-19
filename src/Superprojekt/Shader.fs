namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering

module BlitShader =
    open FShade

    type Fragment =
        {
            [<Color>] c : V4d
            [<Depth>] d : float
        }

    type UniformScope with
        member x.RevolverVisible : bool = x?RevolverVisible
        member x.TextureOffset   : V2d  = x?TextureOffset
        member x.TextureScale    : V2d  = x?TextureScale

    let colorSam =
        sampler2d {
            texture uniform?ColorTexture
            filter Filter.MinMagPoint
            addressU WrapMode.Wrap
            addressV WrapMode.Wrap
        }
    let depthSam =
        sampler2d {
            texture uniform?DepthTexture
            filter Filter.MinMagPoint
            addressU WrapMode.Wrap
            addressV WrapMode.Wrap
        }

    let read (v : Effects.Vertex) =
        fragment {
            let c =
                let original = colorSam.SampleLevel(v.tc, 0.0)
                if uniform.RevolverVisible then lerp original V4d.IIII 0.5
                else original
            return { c = c; d = depthSam.SampleLevel(v.tc, 0.0).X }
        }
    let readColorTex (v : Effects.Vertex) =
        fragment {
            let c = colorSam.SampleLevel(v.tc, 0.0)
            return c
        }
    let readColorDepthTex (v : Effects.Vertex) =
        fragment {
            return { c = colorSam.SampleLevel(v.tc, 0.0); d = depthSam.SampleLevel(v.tc, 0.0).X }
        }

    // Circular magnifier: clips to unit circle, samples with offset+scale.
    let readColor (v : Effects.Vertex) =
        fragment {
            let ndc = 2.0 * v.tc - V2d.II
            if Vec.lengthSquared ndc > 1.0 then discard()
            let b = colorSam.SampleLevel(uniform.TextureOffset + uniform.TextureScale * v.tc, 0.0)
            return b
        }

module Shader =
    open FShade

    let nothing (v : Effects.Vertex) =
        fragment {
            return v.c
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
