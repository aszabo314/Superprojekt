namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering

module Shader =
    open FShade

    type UniformScope with
        member x.FlatColor : V4f    = x?FlatColor
        member x.Opacity   : float32 = x?Opacity

    let flatColor (_v : Effects.Vertex) =
        fragment { return uniform.FlatColor }

    let vertexColor (v : Effects.Vertex) =
        fragment { return v.c }

    let applyOpacity (v : Effects.Vertex) =
        fragment {
            let c = v.c
            return V4f(c.X, c.Y, c.Z, c.W * uniform.Opacity)
        }

    type Fragment =
        {
            [<Semantic("PickViewPosition")>] vp : V3f
        }

    let withViewPos (v : Effects.Vertex) =
        fragment {
            let vp = uniform.ProjTrafoInv * v.pos
            let vp = vp.XYZ / vp.W
            let vp = vp + V3f(0.1f, 0.0f, 0.0f)
            return { vp = vp.XYZ }
        }
