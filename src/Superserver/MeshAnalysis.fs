module MeshAnalysis

open System
open Aardvark.Base
open Aardvark.Embree
open MeshCache
open MeshAnalysisCore

// Level set of |p − cLocal| − radius over the candidate triangles (mesh-local).
let private sphereCut (positions : V3f[]) (cLocal : V3d) (radius : float) (triBuf : int[]) =
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

// Level set of the signed plane distance (linear field → linear-interp root is
// exact) over the candidate triangles (mesh-local).
let private planeCut (positions : V3f[]) (cLocal : V3d) (n : V3d) (triBuf : int[]) =
    let signedDist (i : int) = Vec.dot (V3d positions.[i] - cLocal) n
    let edgePoint (i0 : int) (i1 : int) =
        let d0 = signedDist i0
        let d1 = signedDist i1
        let p0 = V3d positions.[i0]
        let p1 = V3d positions.[i1]
        p0 + (d0 / (d0 - d1)) * (p1 - p0)
    traceLevelSet triBuf signedDist edgePoint

// Sphere–surface contact rings: sphereCut over BVH candidate triangles.
// World-space.
let contactRings (lm : LoadedMesh) (centre : V3d) (radius : float) (maxPoints : int) : V3d[][] =
    let positions = lm.parsed.positions
    let centroid = lm.parsed.centroid
    let cLocal = centre - centroid

    // A sign-changing edge has a vertex inside the sphere → its triangle bbox
    // overlaps the sphere, so the BVH candidate query loses nothing.
    let triBuf = trianglesInSphere lm (V3f cLocal) (float32 radius)

    sphereCut positions cLocal radius triBuf
    |> Array.map (Array.map (fun p -> p + centroid))
    |> decimate maxPoints

// Local-geometry reveal around a correspondence point: concentric contact
// rings + plane∩surface relief cuts through the point, one flat polyline
// list. ONE candidate query at the outermost radius serves every cut; plane
// cuts may overshoot the sphere by a triangle — the client's distance fade
// makes the hard clip unnecessary. World-space.
let pointReveal (lm : LoadedMesh) (centre : V3d) (radii : float[]) (planes : V3d[]) (maxPoints : int) : V3d[][] =
    let positions = lm.parsed.positions
    let centroid = lm.parsed.centroid
    let cLocal = centre - centroid
    let maxR = radii |> Array.fold max 0.0
    if maxR <= 0.0 then [||]
    else
        let triBuf = trianglesInSphere lm (V3f cLocal) (float32 maxR)
        [|
            for r in radii do yield! sphereCut positions cLocal r triBuf
            for n in planes do
                if n.Length > 1e-9 then yield! planeCut positions cLocal n.Normalized triBuf
        |]
        |> Array.map (Array.map (fun p -> p + centroid))
        |> decimate maxPoints
