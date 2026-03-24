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
                //if t>0.5f then c1 else c0
                (c0 * (1.0f - t) + c1 * t)

    
    type Fragment =
        {
            [<Color>] c : V4d
            [<Depth>] d : float
        }

    type UniformScope with
        member x.RevolverVisible      : bool  = x?RevolverVisible
        member x.TextureOffset        : V2d   = x?TextureOffset
        member x.TextureScale         : V2d   = x?TextureScale
        member x.TextureCount         : int   = x?TextureCount
        member x.DifferenceRendering  : bool  = x?DifferenceRendering
        member x.MinDifferenceDepth   : float = x?MinDifferenceDepth
        member x.MaxDifferenceDepth   : float = x?MaxDifferenceDepth
        member x.SliceIndex           : int   = x?SliceIndex
    
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
        
    let colon =
        sampler2dArray {
            texture uniform?ColorTexture
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
            
            let mutable minDepth = 10.0
            let mutable maxDepth = -10.0
            
            let mutable d = 10.0
            let mutable color = V4d.IOOI
            for i in 0 .. uniform.TextureCount - 1 do
                let di = deputy.SampleLevel(v.tc, i, 0.0).X
                let c = colon.SampleLevel(v.tc, i, 0.0)
                
                if di < 1.0 && c.W >= 0.01 then
                    minDepth <- min di minDepth
                    maxDepth <- max di maxDepth
                    
                if di < d then
                    d <- di
                    color <- c
                    
            let ndc = v.pos.XY / v.pos.W
            let a = uniform.ViewProjTrafoInv * V4d(ndc, 2.0 * minDepth - 1.0, 1.0)
            let b = uniform.ViewProjTrafoInv * V4d(ndc, 2.0 * maxDepth - 1.0, 1.0)
                    
            let a = a.XYZ / a.W
            let b = b.XYZ / b.W
            
            let dist = Vec.length (a - b)
            if uniform.DifferenceRendering && dist > uniform.MinDifferenceDepth then
                let h = (dist - uniform.MinDifferenceDepth) / uniform.MaxDifferenceDepth |> float32 |> Heat.heat |> V4d
                color <- h * color
            return { c = color; d = d }
        }   
        
    // Single array slice with depth — for fullscreen tiles.
    let readArraySlice (v : Effects.Vertex) =
        fragment {
            let i = uniform.SliceIndex
            return { c = colon.SampleLevel(v.tc, i, 0.0)
                     d = deputy.SampleLevel(v.tc, i, 0.0).X }
        }

    // Circular magnifier on array slice — for revolver disks.
    let readArraySliceColor (v : Effects.Vertex) =
        fragment {
            let ndc = 2.0 * v.tc - V2d.II
            if Vec.lengthSquared ndc > 1.0 then discard()
            return colon.SampleLevel(uniform.TextureOffset + uniform.TextureScale * v.tc, uniform.SliceIndex, 0.0)
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
        member x.Order : int = x?Order

    [<ReflectedDefinition>]
    let orderColor (o : int) =
        match o % 3 with
        | 0 -> V4d(1.0, 1.0, 0.0, 1.0)
        | 1 -> V4d(0.5, 1.0, 0.5, 1.0)
        | 2 -> V4d(1.0, 0.5, 1.0, 1.0)
        | _ -> V4d(1.0, 1.0, 1.0, 1.0)
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
            let ocol = orderColor uniform.Order
            let c = 
                if d < t then
                    lerp ocol v.c (d / t)
                else
                    v.c
            
            return c
        }
