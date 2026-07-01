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
        // Focus overlay: 0 = texture (pass through), 1 = diverging signed difference,
        // 2 = sequential displacement magnitude, 3 = flat white; the per-mesh intrinsic
        // layers 4 = incidence, 5 = range, 6 = shape carry a pre-normalized [0,1]
        // FocusScalar and map to the same colours as the 3D mesh-shader heatmaps.
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
            // Intrinsic per-mesh heatmaps (pre-normalized scalar) — same ramps as the
            // 3D mesh shader. Incidence: grazing red → head-on green (via yellow).
            elif uniform.FocusMode = 4 then
                let incid = clamp 0.0f 1.0f v.s
                let lo  = V3f(0.84f, 0.19f, 0.15f)
                let mid = V3f(0.99f, 0.85f, 0.30f)
                let hi  = V3f(0.18f, 0.55f, 0.34f)
                if incid < 0.5f then return V4f(lo + (mid - lo) * (incid * 2.0f), 1.0f)
                else return V4f(mid + (hi - mid) * ((incid - 0.5f) * 2.0f), 1.0f)
            // Range: near blue → far red.
            elif uniform.FocusMode = 5 then
                let tr = clamp 0.0f 1.0f v.s
                let nearC = V3f(0.13f, 0.40f, 0.85f)
                let farC  = V3f(0.86f, 0.20f, 0.15f)
                return V4f(nearC * (1.0f - tr) + farC * tr, 1.0f)
            // Shape: poor red → good green (quality ≥ 0.75 reads fully green).
            elif uniform.FocusMode = 6 then
                let ts = clamp 0.0f 1.0f (v.s / 0.75f)
                let loC = V3f(0.86f, 0.20f, 0.15f)
                let hiC = V3f(0.18f, 0.55f, 0.34f)
                return V4f(loC * (1.0f - ts) + hiC * ts, 1.0f)
            elif abs v.s >= 1e20f then return V4f(0.886f, 0.910f, 0.941f, 1.0f)
            elif uniform.FocusMode = 2 then
                let t = min 1.0f (max 0.0f (abs v.s / max 1e-6f uniform.FocusHi))
                return V4f(0.933f + (0.114f - 0.933f) * t, 0.949f + (0.306f - 0.949f) * t, 0.965f + (0.847f - 0.965f) * t, 1.0f)
            else
                // Linear-diverging difference map (§C, Kovesi CET-D style): neutral
                // (0.62,0.63,0.66) → red (+) / blue (−), with a near-zero perceptual
                // boost (t^0.6) so small deviations stay visible (no central flat-spot).
                // ±LoD gate kept: within FocusLod → neutral; outside, ramp gate→FocusHi.
                // Mirrors Primitives.Diff exactly.
                let hi = max 1e-6f uniform.FocusHi
                let a = abs v.s
                if a < uniform.FocusLod then return V4f(0.62f, 0.63f, 0.66f, 1.0f)
                else
                    let denom = max 1e-6f (hi - uniform.FocusLod)
                    let m = pow (min 1.0f (max 0.0f ((a - uniform.FocusLod) / denom))) 0.6f
                    if v.s >= 0.0f then
                        return V4f(0.62f + (0.80f - 0.62f) * m, 0.63f + (0.12f - 0.63f) * m, 0.66f + (0.12f - 0.66f) * m, 1.0f)
                    else
                        return V4f(0.62f + (0.13f - 0.62f) * m, 0.63f + (0.34f - 0.63f) * m, 0.66f + (0.74f - 0.66f) * m, 1.0f)
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
