namespace Superprojekt

open Aardvark.Base
open FShade

// Focus-panel fragment overlay: recolours the textured surface for the Inspect
// channels and the per-mesh intrinsic heatmaps. The 360° and Top projections are
// both ordinary cameras (view/proj trafos in FocusScene) — no custom vertex stage.
// float32-only / lambda-free (WebGL ESSL3).
module FocusShaders =

    type UniformScope with
        // Focus overlay: 0 = texture (pass through), 1 = diverging signed difference,
        // 2 = sequential displacement magnitude, 3 = flat white; the per-mesh intrinsic
        // layers 4 = incidence, 5 = range, 6 = shape carry a pre-normalized [0,1]
        // FocusScalar and map to the same colours as the 3D mesh-shader heatmaps.
        // FocusHi saturates the positive end; FocusLoNeg (mode 1 only) the |negative|
        // end — both from the unified pin-derived Inspect range (§C), matching the 3D
        // mesh shader so tiles, single and 3D read on one scale.
        member x.FocusMode  : int     = uniform?FocusMode
        member x.FocusHi    : float32 = uniform?FocusHi
        member x.FocusLoNeg : float32 = uniform?FocusLoNeg
        // Value step (m) of the difference isolines (mode 1 only); 0 disables.
        member x.FocusIsoStep : float32 = uniform?FocusIsoStep
        // Shp cutoff (mode 6): fragments below this quality are discarded.
        member x.FocusShapeThreshold : float32 = uniform?FocusShapeThreshold

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
            // Shape: poor red → good green (quality ≥ 0.75 reads fully green);
            // below the cutoff the fragment is discarded (transparent filter).
            elif uniform.FocusMode = 6 then
                if v.s < uniform.FocusShapeThreshold then discard()
                let ts = clamp 0.0f 1.0f (v.s / 0.75f)
                let loC = V3f(0.86f, 0.20f, 0.15f)
                let hiC = V3f(0.18f, 0.55f, 0.34f)
                return V4f(loC * (1.0f - ts) + hiC * ts, 1.0f)
            elif abs v.s >= 1e20f then return V4f(0.886f, 0.910f, 0.941f, 1.0f)
            elif uniform.FocusMode = 2 then
                // Displacement ramp — MUST match MeshShader.shade enc 3 and the
                // dock legend (GuiOverlays): light → dark blue, one ramp everywhere.
                let t = min 1.0f (max 0.0f (abs v.s / max 1e-6f uniform.FocusHi))
                return V4f(0.93f + (0.118f - 0.93f) * t, 0.94f + (0.227f - 0.94f) * t, 0.98f + (0.541f - 0.98f) * t, 1.0f)
            else
                // Coolwarm diverging difference map (§C, CET-D01): zero = near-white
                // centre (welded to 0; grey means "no signal", not "0"), + through
                // salmon to red, − through lavender to blue, each sign normalized by
                // its own end (FocusHi / FocusLoNeg) with the near-zero t^0.6 boost.
                // Constant-value isolines every FocusIsoStep metres (derivative-
                // antialiased darkening, suppressed where the colour clamps).
                // Mirrors Primitives.Diff + MeshShader.shade enc 1.
                let a = abs v.s
                let hi = if v.s >= 0.0f then max 1e-6f uniform.FocusHi else max 1e-6f uniform.FocusLoNeg
                let t = min 1.0f (max 0.0f (a / hi))
                let m = pow t 0.6f
                let zeroC = V3f(0.930f, 0.907f, 0.917f)
                let midC = if v.s >= 0.0f then V3f(0.906f, 0.549f, 0.464f) else V3f(0.627f, 0.612f, 0.908f)
                let endC = if v.s >= 0.0f then V3f(0.752f, 0.008f, 0.022f) else V3f(0.128f, 0.316f, 0.858f)
                let mutable rgb =
                    if m < 0.5f then zeroC + (midC - zeroC) * (m * 2.0f)
                    else midC + (endC - midC) * ((m - 0.5f) * 2.0f)
                let step = uniform.FocusIsoStep
                if step > 1e-9f && t < 1.0f then
                    let x = v.s / step
                    let g = abs (x - floor (x + 0.5f))
                    let aa = max (abs (ddx x) + abs (ddy x)) 1e-6f
                    // Fade lines out where contours pack denser than ~2 px apart
                    // (steep fragments) — else they smear into a dark blotch.
                    let fade = clamp 0.0f 1.0f ((0.5f - aa) * 4.0f)
                    let line = 0.45f + 0.55f * min 1.0f (g / (aa * 1.3f))
                    rgb <- rgb * (1.0f - fade * (1.0f - line))
                return V4f(rgb, 1.0f)
        }
