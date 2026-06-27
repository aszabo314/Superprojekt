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
        // Inspect channel overlay: 0 = texture (pass through), 1 = diverging signed
        // difference, 2 = sequential displacement magnitude. FocusScalar is per vertex.
        member x.FocusMode : int     = uniform?FocusMode
        member x.FocusHi   : float32 = uniform?FocusHi
        member x.FocusLod  : float32 = uniform?FocusLod

    type ColorVertex =
        {
            [<Color>]                  c : V4f
            [<Semantic("FocusScalar")>] s : float32
        }

    // Inspect colour overlay (fragment, after diffuseTexture). FocusMode 0 keeps the
    // atlas colour; 1 = diverging (blue↔neutral↔red about 0); 2 = sequential
    // (neutral→blue by magnitude); 3 = flat white (displacement surface under the
    // arrow glyphs). 1e30 sentinel → no-data grey.
    let focusColor (v : ColorVertex) =
        fragment {
            if uniform.FocusMode = 0 then return v.c
            elif uniform.FocusMode = 3 then return V4f(0.957f, 0.969f, 0.980f, 1.0f)
            elif abs v.s >= 1e20f then return V4f(0.886f, 0.910f, 0.941f, 1.0f)
            elif uniform.FocusMode = 2 then
                let t = min 1.0f (max 0.0f (abs v.s / max 1e-6f uniform.FocusHi))
                return V4f(0.933f + (0.114f - 0.933f) * t, 0.949f + (0.306f - 0.949f) * t, 0.965f + (0.847f - 0.965f) * t, 1.0f)
            else
                let hi = max 1e-6f uniform.FocusHi
                if abs v.s < uniform.FocusLod then return V4f(0.945f, 0.961f, 0.976f, 1.0f)
                else
                    let tt = min 1.0f (max -1.0f (v.s / hi))
                    if tt >= 0.0f then
                        return V4f(0.945f + (0.863f - 0.945f) * tt, 0.961f + (0.149f - 0.961f) * tt, 0.976f + (0.149f - 0.976f) * tt, 1.0f)
                    else
                        let u = 0.0f - tt
                        return V4f(0.945f + (0.145f - 0.945f) * u, 0.961f + (0.388f - 0.961f) * u, 0.976f + (0.922f - 0.976f) * u, 1.0f)
        }

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
