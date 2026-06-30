namespace Superprojekt

open Aardvark.Base

module PinGeometry =

    let buildIcosphere (subdivisions : int) : V3f[] * int[] =
        let phi = (1.0 + sqrt 5.0) * 0.5
        let a = 1.0 / sqrt (1.0 + phi * phi)
        let b = phi * a
        let verts = ResizeArray<V3d>([
            V3d(-a,  b, 0.0); V3d( a,  b, 0.0); V3d(-a, -b, 0.0); V3d( a, -b, 0.0)
            V3d(0.0, -a,  b); V3d(0.0,  a,  b); V3d(0.0, -a, -b); V3d(0.0,  a, -b)
            V3d( b, 0.0, -a); V3d( b, 0.0,  a); V3d(-b, 0.0, -a); V3d(-b, 0.0,  a)
        ])
        let mutable faces =
            ResizeArray<int * int * int>([
                (0,11,5); (0,5,1); (0,1,7); (0,7,10); (0,10,11)
                (1,5,9); (5,11,4); (11,10,2); (10,7,6); (7,1,8)
                (3,9,4); (3,4,2); (3,2,6); (3,6,8); (3,8,9)
                (4,9,5); (2,4,11); (6,2,10); (8,6,7); (9,8,1)
            ])
        for _ in 1 .. subdivisions do
            let cache = System.Collections.Generic.Dictionary<int * int, int>()
            let getMid i j =
                let key = if i < j then (i, j) else (j, i)
                match cache.TryGetValue key with
                | true, v -> v
                | _ ->
                    let m = ((verts.[i] + verts.[j]) * 0.5) |> Vec.normalize
                    verts.Add m
                    let idx = verts.Count - 1
                    cache.[key] <- idx
                    idx
            let newFaces = ResizeArray<int * int * int>(faces.Count * 4)
            for (a, b, c) in faces do
                let ab = getMid a b
                let bc = getMid b c
                let ca = getMid c a
                newFaces.Add (a, ab, ca)
                newFaces.Add (b, bc, ab)
                newFaces.Add (c, ca, bc)
                newFaces.Add (ab, bc, ca)
            faces <- newFaces
        let positions = verts |> Seq.map (fun v -> V3f v) |> Array.ofSeq
        let indices =
            let arr = Array.zeroCreate (faces.Count * 3)
            for fi in 0 .. faces.Count - 1 do
                let (a, b, c) = faces.[fi]
                arr.[3 * fi]     <- a
                arr.[3 * fi + 1] <- b
                arr.[3 * fi + 2] <- c
            arr
        positions, indices

    let buildSphereOutline (centre : V3d) (radius : float) (color : V4d) (widthPx : float) =
        let n = 64
        let segs = ResizeArray<V3d * V3d * V4d * float>(3 * n)
        let circle (basisU : V3d) (basisV : V3d) =
            for i in 0 .. n - 1 do
                let a0 = float i       / float n * Constant.PiTimesTwo
                let a1 = float (i + 1) / float n * Constant.PiTimesTwo
                let p0 = centre + (basisU * cos a0 + basisV * sin a0) * radius
                let p1 = centre + (basisU * cos a1 + basisV * sin a1) * radius
                segs.Add((p0, p1, color, widthPx))
        circle V3d.IOO V3d.OIO
        circle V3d.IOO V3d.OOI
        circle V3d.OIO V3d.OOI
        segs.ToArray()
