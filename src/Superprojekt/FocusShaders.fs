namespace Superprojekt

open Aardvark.Base
open FShade

// Panorama projection for the WebGL focus panel. Composed AFTER DefaultSurfaces.trafo
// (which fills WorldPosition + a standard clip pos): it keeps the WorldPosition
// varying intact — so GPU picking still resolves the true surface point — and only
// rewrites the clip position to the cylindrical (azimuth, elevation) unwrap with the
// pan/zoom camera baked in. float32-only / lambda-free (WebGL ESSL3).
module FocusShaders =

    type Vertex =
        {
            [<Position>]                            pos : V4f
            [<Semantic("WorldPosition")>]           wp  : V4f
            [<Semantic("DiffuseColorCoordinates")>] tc  : V2f
        }

    type UniformScope with
        member x.PanoEye    : V3f     = uniform?PanoEye
        member x.PanoCenter : V2f     = uniform?PanoCenter
        member x.PanoZoom   : float32 = uniform?PanoZoom
        member x.PanoAspect : float32 = uniform?PanoAspect
        member x.PanoRadFar : float32 = uniform?PanoRadFar

    // Cylindrical from the mesh origin (PanoEye). u = azimuth/π, v = elevation/(π/2),
    // both in [-1,1]; the (PanoCenter, PanoZoom) camera maps them to clip. Depth =
    // normalised radial distance so the nearest surface occludes.
    let pano (v : Vertex) =
        vertex {
            let p = v.wp.XYZ - uniform.PanoEye
            let hyp = sqrt (p.X * p.X + p.Y * p.Y)
            let az = atan2 p.Y p.X
            let el = atan2 p.Z (max 1e-6f hyp)
            let u = az / 3.1415927f
            let vv = el / 1.5707964f
            let cx = (u - uniform.PanoCenter.X) * uniform.PanoZoom / uniform.PanoAspect
            let cy = (vv - uniform.PanoCenter.Y) * uniform.PanoZoom
            let r = sqrt (p.X * p.X + p.Y * p.Y + p.Z * p.Z)
            let depth = min 0.999f (max -0.999f (r / uniform.PanoRadFar * 2.0f - 1.0f))
            return { v with pos = V4f(cx, cy, depth, 1.0f) }
        }
