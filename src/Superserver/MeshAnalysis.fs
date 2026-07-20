module MeshAnalysis

open System
open Aardvark.Base
open Aardvark.Embree
open MeshCache
open MeshAnalysisCore

// Sphere–surface contact rings: level set of |p − centre| − radius over BVH
// candidate triangles. World-space.
let contactRings (lm : LoadedMesh) (centre : V3d) (radius : float) (maxPoints : int) : V3d[][] =
    let positions = lm.parsed.positions
    let centroid = lm.parsed.centroid
    let cLocal = centre - centroid

    // A sign-changing edge has a vertex inside the sphere → its triangle bbox
    // overlaps the sphere, so the BVH candidate query loses nothing.
    let triBuf = trianglesInSphere lm (V3f cLocal) (float32 radius)

    let signedDist (i : int) = (V3d positions.[i] - cLocal).Length - radius
    let edgePoint (i0 : int) (i1 : int) =
        let d0 = signedDist i0
        let d1 = signedDist i1
        let p0 = V3d positions.[i0]
        let p1 = V3d positions.[i1]
        // Exact sphere–segment root (one root in [0,1] given a sign change);
        // linear-interp fallback for degenerate edges.
        let dir = p1 - p0
        let m = p0 - cLocal
        let a = Vec.dot dir dir
        let b = 2.0 * Vec.dot m dir
        let c = Vec.dot m m - radius * radius
        let disc = b * b - 4.0 * a * c
        let t =
            if disc >= 0.0 && a > 1e-16 then
                let sq = sqrt disc
                let t0 = (-b - sq) / (2.0 * a)
                let t1 = (-b + sq) / (2.0 * a)
                if t0 >= 0.0 && t0 <= 1.0 then t0
                elif t1 >= 0.0 && t1 <= 1.0 then t1
                else d0 / (d0 - d1)
            else d0 / (d0 - d1)
        p0 + t * dir

    traceLevelSet triBuf signedDist edgePoint
    |> Array.map (Array.map (fun p -> p + centroid))
    |> decimate maxPoints

// Slice-cell azimuth: the horizontal direction of maximum
// z-range of the surface within the pin ROI ≈ the dip direction of the LSQ
// height fit z = ax + by + c over the ROI vertices (world frame, posed by
// `transform`). Sign-canonicalised (+X, tie +Y) so repeated requests for the
// same pin agree. None when the patch is flat, degenerate or too sparse — the
// caller falls back to +X.
let dipAzimuth (lm : LoadedMesh) (transform : M44d) (centre : V3d) (radius : float) : V3d option =
    let positions = lm.parsed.positions
    let centroid  = lm.parsed.centroid
    let inv = transform.Inverse
    let cLocal = inv.TransformPos centre - centroid
    let triBuf = trianglesInSphere lm (V3f cLocal) (float32 radius)
    let seen = System.Collections.Generic.HashSet<int>()
    let r2 = radius * radius
    let pts = ResizeArray<V3d>()
    for i in triBuf do
        if seen.Add i then
            let w = transform.TransformPos (V3d positions.[i] + centroid)
            let q = w - centre
            if q.LengthSquared <= r2 then pts.Add q
    dipOfPoints pts

// Vertical cross-sections for the pin overlay charts: the mesh (posed by
// `transform`, mesh-own-world → scene world, probe convention) intersected with
// parallel planes through `centre` (normal `normal`, in-plane horizontal
// direction `uDir`, world-Z vertical), each clipped to the probe sphere
// (radius about centre). Returned as 2D chart-frame polylines — (u, v) metres
// relative to `centre` — with result.[k] = the polylines of offsets.[k].
let planeSlices (lm : LoadedMesh) (transform : M44d) (centre : V3d)
                (uDir : V3d) (normal : V3d) (radius : float)
                (offsets : float[]) (maxPointsPerPlane : int) : V2d[][][] =
    let positions = lm.parsed.positions
    let centroid = lm.parsed.centroid
    let inv = transform.Inverse
    let cLocal = inv.TransformPos centre - centroid
    let nLocal = (inv.TransformDir normal).Normalized
    let uLocal = (inv.TransformDir uDir).Normalized
    let vLocal = (inv.TransformDir V3d.OOI).Normalized

    // One candidate set serves every offset plane (all discs lie in this sphere).
    let triBuf = trianglesInSphere lm (V3f cLocal) (float32 radius)

    offsets |> Array.map (fun w ->
        let discR2 = radius * radius - w * w
        if discR2 <= 0.0 || triBuf.Length = 0 then [||]
        else
            let pLoc = cLocal + nLocal * w
            let signedDist (i : int) = Vec.dot (V3d positions.[i] - pLoc) nLocal
            let edgePoint (i0 : int) (i1 : int) =
                let d0 = signedDist i0
                let d1 = signedDist i1
                let p0 = V3d positions.[i0]
                let p1 = V3d positions.[i1]
                p0 + (d0 / (d0 - d1)) * (p1 - p0)
            let toChart (p : V3d) =
                let q = p - cLocal
                V2d(Vec.dot q uLocal, Vec.dot q vLocal)

            // Disc clip in the chart frame (u² + v² ≤ r² − w²) with the rim
            // crossing interpolated; a chain leaving the disc splits. Segments
            // with both ends outside can clip a tiny rim chord — ignored (mesh
            // edges are far shorter than the disc).
            let clipped = ResizeArray<V2d[]>()
            for chain in traceLevelSet triBuf signedDist edgePoint do
                let pts = chain |> Array.map toChart
                let cur = ResizeArray<V2d>()
                let flush () =
                    if cur.Count >= 2 then clipped.Add (cur.ToArray())
                    cur.Clear()
                let mutable prev = V2d.Zero
                let mutable prevIn = false
                for i in 0 .. pts.Length - 1 do
                    let p = pts.[i]
                    let isIn = p.LengthSquared <= discR2
                    if i > 0 && isIn <> prevIn then
                        let d = p - prev
                        let a = d.LengthSquared
                        let b = 2.0 * Vec.dot prev d
                        let c = prev.LengthSquared - discR2
                        let disc = b * b - 4.0 * a * c
                        let t =
                            if disc >= 0.0 && a > 1e-16 then
                                let sq = sqrt disc
                                let t0 = (-b - sq) / (2.0 * a)
                                if t0 >= 0.0 && t0 <= 1.0 then t0 else (-b + sq) / (2.0 * a)
                            else 0.5
                        let x = prev + d * (max 0.0 (min 1.0 t))
                        if prevIn then
                            cur.Add x
                            flush ()
                        else
                            cur.Add x
                    if isIn then cur.Add p
                    prev <- p
                    prevIn <- isIn
                flush ()
            decimate maxPointsPerPlane (clipped.ToArray()))
