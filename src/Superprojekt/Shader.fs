namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering

module Shader =
    open FShade
    open BlitShader

    type UniformScope with
        member x.FlatColor : V4d = x?FlatColor
        member x.ColorMode : int = x?ColorMode
        member x.Opacity : float = x?Opacity

    let falseColorMap =
        [|
            V4d(0.20, 0.40, 0.65, 1.0)
            V4d(0.55, 0.65, 0.30, 1.0)
            V4d(0.75, 0.55, 0.30, 1.0)
            V4d(0.40, 0.55, 0.70, 1.0)
            V4d(0.60, 0.40, 0.55, 1.0)
            V4d(0.35, 0.55, 0.55, 1.0)
        |]

    let headlight (v : Effects.Vertex) =
        fragment {
            let mutable c = v.c
            if uniform.ColorMode = 1 then
                let n = v.n |> Vec.normalize
                let toCam = uniform.CameraLocation - v.wp.XYZ |> Vec.normalize
                let ndl = max 0.15 (abs (Vec.dot n toCam))
                let baseC = falseColorMap.[uniform.MeshIndex % 6]
                c <- V4d(baseC.XYZ * ndl, c.W)
            elif uniform.ColorMode = 2 then
                let n = v.n |> Vec.normalize
                let toCam = uniform.CameraLocation - v.wp.XYZ |> Vec.normalize
                let ndl = max 0.25 (abs (Vec.dot n toCam))
                c <- V4d(ndl, ndl, ndl, c.W)
            return c
        }

    let flatColor (_v : Effects.Vertex) =
        fragment { return uniform.FlatColor }

    let vertexColor (v : Effects.Vertex) =
        fragment { return v.c }

    let nothing (v : Effects.Vertex) =
        fragment {
            return v.c
        }

    let applyOpacity (v : Effects.Vertex) =
        fragment {
            let c = v.c
            return V4d(c.X, c.Y, c.Z, c.W * uniform.Opacity)
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

