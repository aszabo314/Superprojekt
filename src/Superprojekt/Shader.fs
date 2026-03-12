namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering

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
