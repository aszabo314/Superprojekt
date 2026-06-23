namespace Superprojekt

open System
open Aardvark.Base

// Correspondence-detail view: pure geometry + types. WASM-free (no Aardvark.Dom /
// FShade), compiled into Supertests so the marching-squares / dip / azimuth /
// niceStep math is unit-tested. The view layer (CardsPin) serialises these
// records to JSON for the SVG renderer, which only projects + pans/zooms.

// Camera framing of the detail viewport.
type DetailViewMode =
    | DetailSide
    | DetailTop
    | DetailFree

module DetailViewMode =
    let tag = function DetailSide -> "side" | DetailTop -> "top" | DetailFree -> "free"
    let ofTag = function "top" -> DetailTop | "free" -> DetailFree | _ -> DetailSide

// Ray-down elevation grid in a mesh's OWN (untransformed) world frame. Z is
// row-major (idx = j*N + i, i along +X, j along +Y) world elevation; Hit=false
// marks a hole (no surface) that is never bridged. Transform-independent: the
// view rigidly maps it to the current pose, so it survives registration previews.
type ElevGrid = {
    N          : int
    Size       : float
    OwnCenterX : float
    OwnCenterY : float
    Z          : float[]
    Hit        : bool[]
}

type ElevGridState =
    | GridNone
    | GridRunning
    | GridReady of ElevGrid

// Symbolic surface for one marker's patch, all geometry in current world space.
type ContourSeg = { Level : float; A : V3d; B : V3d }

type SymbolicPatch = {
    Contours  : ContourSeg[]
    Ridges    : V3d[][]
    Valleys   : V3d[][]
    ZMin      : float
    ZMax      : float
    DipRad    : float
    StrikeDir : V3d   // world unit, horizontal strike line direction
    DownSlope : V3d   // world horizontal unit toward steepest descent (Zero if flat)
}

// Per-marker derived values (current pose). AzimuthRad is NaN when North unknown.
type DetailMarker = {
    Mesh       : string
    World      : V3d
    IsRef      : bool
    Euclid     : float
    Vert       : float
    Horiz      : float
    AzimuthRad : float
    Patch      : SymbolicPatch option
}

module DetailConsts =
    let patchSize          = 4.0    // sampled neighbourhood side (m)
    let patchGridN         = 48
    let contourTargetCount = 8.0
    let contourFloor       = 0.05   // m, minimum contour interval
    let rvThresh           = 0.02   // 1/m curvature for ridge / valley
    let rvMinCells         = 4
    let topMargin          = 50.0   // own-frame ray start above the marker (m)

module DetailViewMath =

    let up = V3d.OOI

    // Smallest {1,2,5}·10^k ≥ raw (>0). Used for contour intervals and rulers.
    let niceStep (raw : float) : float =
        if raw <= 0.0 || Double.IsNaN raw || Double.IsInfinity raw then 1.0
        else
            let p = floor (log10 raw)
            let baseP = 10.0 ** p
            [ 1.0; 2.0; 5.0; 10.0 ]
            |> List.map (fun m -> m * baseP)
            |> List.find (fun s -> s >= raw - 1e-12)

    // World grid node positions (current pose) — own-frame lattice rigidly mapped.
    let worldGrid (g : ElevGrid) (m : Trafo3d) : V3d[] =
        let n = g.N
        let half = g.Size * 0.5
        let step = if n > 1 then g.Size / float (n - 1) else 0.0
        Array.init (n * n) (fun k ->
            let i = k % n
            let j = k / n
            let ox = g.OwnCenterX - half + step * float i
            let oy = g.OwnCenterY - half + step * float j
            m.Forward.TransformPos (V3d(ox, oy, float g.Z.[k])))

    // z = a·x + b·y over hit nodes, centred (well-conditioned for huge UTM Z).
    // Returns slope (a,b) of the fitted plane in world horizontal coords.
    let private fitSlope (nodes : V3d[]) (hit : bool[]) : (float * float) option =
        let mutable cnt = 0
        let mutable mx = 0.0
        let mutable my = 0.0
        let mutable mz = 0.0
        for k in 0 .. nodes.Length - 1 do
            if hit.[k] then
                cnt <- cnt + 1
                mx <- mx + nodes.[k].X
                my <- my + nodes.[k].Y
                mz <- mz + nodes.[k].Z
        if cnt < 3 then None
        else
            let inv = 1.0 / float cnt
            mx <- mx * inv; my <- my * inv; mz <- mz * inv
            let mutable sxx = 0.0
            let mutable sxy = 0.0
            let mutable syy = 0.0
            let mutable sxz = 0.0
            let mutable syz = 0.0
            for k in 0 .. nodes.Length - 1 do
                if hit.[k] then
                    let dx = nodes.[k].X - mx
                    let dy = nodes.[k].Y - my
                    let dz = nodes.[k].Z - mz
                    sxx <- sxx + dx * dx
                    sxy <- sxy + dx * dy
                    syy <- syy + dy * dy
                    sxz <- sxz + dx * dz
                    syz <- syz + dy * dz
            let det = sxx * syy - sxy * sxy
            if abs det < 1e-12 then None
            else Some ((syy * sxz - sxy * syz) / det, (sxx * syz - sxy * sxz) / det)

    // Dip / strike from the fitted plane (terrain vs horizontal). DownSlope =
    // steepest-descent horizontal direction.
    let private dipStrike (nodes : V3d[]) (hit : bool[]) : float * V3d * V3d =
        match fitSlope nodes hit with
        | None -> 0.0, V3d.IOO, V3d.Zero
        | Some (a, b) ->
            let dip = atan (sqrt (a * a + b * b))
            let normal = V3d(-a, -b, 1.0).Normalized
            let strike =
                let s = Vec.cross up normal
                if s.Length > 1e-9 then s.Normalized else V3d.IOO
            let down =
                let d = V3d(-a, -b, 0.0)
                if d.Length > 1e-9 then d.Normalized else V3d.Zero
            dip, strike, down

    // Marching-squares contour segments at `levels` over the lattice (scalar =
    // world Z). Cells with any hole corner are skipped (never bridged).
    let private contourSegments (n : int) (nodes : V3d[]) (z : float[]) (hit : bool[]) (levels : float list) : ContourSeg[] =
        let out = ResizeArray<ContourSeg>()
        let inline interp (ka : int) (kb : int) (lvl : float) =
            let za = z.[ka]
            let zb = z.[kb]
            let t = if abs (zb - za) < 1e-12 then 0.5 else (lvl - za) / (zb - za)
            nodes.[ka] + (nodes.[kb] - nodes.[ka]) * t
        for j in 0 .. n - 2 do
            for i in 0 .. n - 2 do
                let k00 = j * n + i
                let k10 = k00 + 1
                let k01 = k00 + n
                let k11 = k01 + 1
                if hit.[k00] && hit.[k10] && hit.[k01] && hit.[k11] then
                    // 4 edges: bottom(00-10), right(10-11), top(11-01), left(01-00)
                    for lvl in levels do
                        let pts = ResizeArray<V3d>(4)
                        let inline edge ka kb =
                            if (z.[ka] >= lvl) <> (z.[kb] >= lvl) then pts.Add (interp ka kb lvl)
                        edge k00 k10
                        edge k10 k11
                        edge k11 k01
                        edge k01 k00
                        if pts.Count = 2 then
                            out.Add { Level = lvl; A = pts.[0]; B = pts.[1] }
                        elif pts.Count = 4 then
                            // saddle — pair adjacently (visual approximation)
                            out.Add { Level = lvl; A = pts.[0]; B = pts.[1] }
                            out.Add { Level = lvl; A = pts.[2]; B = pts.[3] }
        out.ToArray()

    // Ridge / valley node masks via discrete curvature (second difference / step²)
    // on both grid axes; chained into polylines, components shorter than
    // rvMinCells dropped. Returns (ridges, valleys) as world polylines.
    let private ridgeValley (n : int) (size : float) (nodes : V3d[]) (z : float[]) (hit : bool[]) : V3d[][] * V3d[][] =
        let step = if n > 1 then size / float (n - 1) else 1.0
        let s2 = step * step
        let ridge = Array.zeroCreate<bool> (n * n)
        let valley = Array.zeroCreate<bool> (n * n)
        for j in 1 .. n - 2 do
            for i in 1 .. n - 2 do
                let k = j * n + i
                let kl = k - 1
                let kr = k + 1
                let kd = k - n
                let ku = k + n
                if hit.[k] && hit.[kl] && hit.[kr] && hit.[kd] && hit.[ku] then
                    let cx = (z.[kl] - 2.0 * z.[k] + z.[kr]) / s2
                    let cy = (z.[kd] - 2.0 * z.[k] + z.[ku]) / s2
                    let t = DetailConsts.rvThresh
                    // Convex (crest) in at least one axis and concave in neither →
                    // ridge; the mirror → valley. "Either axis" catches *linear*
                    // crests (a roof ridge has ~0 curvature along its length);
                    // excluding the opposite sign drops saddles. (Spec said
                    // "both axes", which only fires on domes — see notes.)
                    let convex = cx < -t || cy < -t
                    let concave = cx > t || cy > t
                    if convex && not concave then ridge.[k] <- true
                    elif concave && not convex then valley.[k] <- true
        // Connected components (4-neighbour) of a mask → polylines (segments
        // between adjacent marked nodes), dropping components < rvMinCells.
        let extract (mask : bool[]) : V3d[][] =
            let seen = Array.zeroCreate<bool> (n * n)
            let polys = ResizeArray<V3d[]>()
            let stack = System.Collections.Generic.Stack<int>()
            for start in 0 .. n * n - 1 do
                if mask.[start] && not seen.[start] then
                    let comp = ResizeArray<int>()
                    stack.Clear()
                    stack.Push start
                    seen.[start] <- true
                    while stack.Count > 0 do
                        let c = stack.Pop()
                        comp.Add c
                        let i = c % n
                        let j = c / n
                        let inline tryN (ni : int) (nj : int) =
                            if ni >= 0 && ni < n && nj >= 0 && nj < n then
                                let nk = nj * n + ni
                                if mask.[nk] && not seen.[nk] then seen.[nk] <- true; stack.Push nk
                        tryN (i - 1) j
                        tryN (i + 1) j
                        tryN i (j - 1)
                        tryN i (j + 1)
                    if comp.Count >= DetailConsts.rvMinCells then
                        // emit each adjacent-in-component pair as a 2-point poly
                        let segs = ResizeArray<V3d[]>()
                        for c in comp do
                            let i = c % n
                            let j = c / n
                            let inline link (ni : int) (nj : int) =
                                if ni >= 0 && ni < n && nj >= 0 && nj < n then
                                    let nk = nj * n + ni
                                    if mask.[nk] && nk > c then segs.Add [| nodes.[c]; nodes.[nk] |]
                            link (i + 1) j
                            link i (j + 1)
                        polys.AddRange segs
            polys.ToArray()
        extract ridge, extract valley

    let symbolicPatch (g : ElevGrid) (m : Trafo3d) : SymbolicPatch =
        let n = g.N
        let nodes = worldGrid g m
        let z = Array.init (n * n) (fun k -> nodes.[k].Z)
        let mutable zMin = infinity
        let mutable zMax = -infinity
        for k in 0 .. n * n - 1 do
            if g.Hit.[k] then
                if z.[k] < zMin then zMin <- z.[k]
                if z.[k] > zMax then zMax <- z.[k]
        if not (zMax > zMin) then zMin <- 0.0; zMax <- 1.0
        let interval = max DetailConsts.contourFloor (niceStep ((zMax - zMin) / DetailConsts.contourTargetCount))
        let levels =
            let k0 = int (ceil (zMin / interval))
            let k1 = int (floor (zMax / interval))
            [ for k in k0 .. k1 -> float k * interval ]
        let contours = contourSegments n nodes z g.Hit levels
        let ridges, valleys = ridgeValley n g.Size nodes z g.Hit
        let dip, strike, down = dipStrike nodes g.Hit
        { Contours = contours; Ridges = ridges; Valleys = valleys
          ZMin = zMin; ZMax = zMax; DipRad = dip; StrikeDir = strike; DownSlope = down }

    // Euclid / vertical / horizontal offset + North bearing (deg-free; radians).
    let markerMetrics (refWorld : V3d) (world : V3d) (north : V3d option) =
        let d = world - refWorld
        let horizV = V3d(d.X, d.Y, 0.0)
        let euclid = d.Length
        let vert = d.Z
        let horiz = horizV.Length
        let az =
            match north with
            | Some nrm when horiz > 1e-9 ->
                let nn = V3d(nrm.X, nrm.Y, 0.0)
                if nn.Length < 1e-9 then nan
                else
                    let nu = nn.Normalized
                    let eu = V3d(nu.Y, -nu.X, 0.0)   // East = North rotated -90° (clockwise bearing)
                    let a = atan2 (Vec.dot horizV eu) (Vec.dot horizV nu)
                    if a < 0.0 then a + 2.0 * Math.PI else a
            | _ -> nan
        euclid, vert, horiz, az

    // Side-view azimuth: look along the smallest-spread horizontal axis so the
    // largest marker spread lies across the screen. PCA of horizontal positions.
    let sideAzimuth (worlds : V3d[]) : float =
        if worlds.Length < 2 then 0.0
        else
            let mutable mx = 0.0
            let mutable my = 0.0
            for w in worlds do mx <- mx + w.X; my <- my + w.Y
            let inv = 1.0 / float worlds.Length
            mx <- mx * inv; my <- my * inv
            let mutable sxx = 0.0
            let mutable sxy = 0.0
            let mutable syy = 0.0
            for w in worlds do
                let dx = w.X - mx
                let dy = w.Y - my
                sxx <- sxx + dx * dx
                sxy <- sxy + dx * dy
                syy <- syy + dy * dy
            // principal axis (largest eigenvalue direction) of the 2×2 covariance
            let phi = 0.5 * atan2 (2.0 * sxy) (sxx - syy)
            phi + Math.PI * 0.5
