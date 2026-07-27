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

