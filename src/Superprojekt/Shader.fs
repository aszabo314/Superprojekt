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

    type UniformScope with
        member x.ClipMin : V3d = x?ClipMin
        member x.ClipMax : V3d = x?ClipMax

    // Discards fragments outside [ClipMin, ClipMax] in render space.
    // ClipMin/ClipMax are worldClipBox.Min/Max − commonCentroid (computed on CPU).
    // v.wp is set by DefaultSurfaces.trafo as ModelTrafo * v.pos (render-space position).
    let clip (v : Effects.Vertex) =
        fragment {
            let p = v.wp.XYZ
            if p.X < uniform.ClipMin.X || p.X > uniform.ClipMax.X ||
               p.Y < uniform.ClipMin.Y || p.Y > uniform.ClipMax.Y ||
               p.Z < uniform.ClipMin.Z || p.Z > uniform.ClipMax.Z then discard()
            let t = 1.0
            let dxm = abs (uniform.ClipMin.X - p.X)
            let dxM = abs (uniform.ClipMax.X - p.X)
            let dym = abs (uniform.ClipMin.Y - p.Y)
            let dyM = abs (uniform.ClipMax.Y - p.Y)
            let dzm = abs (uniform.ClipMin.Z - p.Z)
            let dzM = abs (uniform.ClipMax.Z - p.Z)
            let d = min (min dxm dxM) (min (min dym dyM) (min dzm dzM))
            let c = 
                if d < t then
                    lerp V4d.IIOI v.c (d / t)
                else
                    v.c
            
            return c
        }
