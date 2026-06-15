namespace Superprojekt

open Aardvark.Base

module Query =

    open System.Net.Http
    open System.Text
    open System.Text.Json

    let private v3 (v : V3d) = sprintf "[%.17g,%.17g,%.17g]" v.X v.Y v.Z

    let private post (serverUrl : string) (path : string) (json : string) : Async<JsonElement> =
        async {
            use content = new StringContent(json, Encoding.UTF8, "application/json")
            let! resp = Http.client.PostAsync(serverUrl.TrimEnd('/') + path, content) |> Async.AwaitTask
            resp.EnsureSuccessStatusCode() |> ignore
            let! text = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
            return JsonDocument.Parse(text).RootElement
        }

    let rayHit (serverUrl : string) (name : string) (index : int) (origin : V3d) (direction : V3d) =
        async {
            let json = sprintf """{"name":"%s","index":%d,"origin":%s,"direction":%s}""" name index (v3 origin) (v3 direction)
            let! r = post serverUrl "/query/ray" json
            if r.GetProperty("hit").GetBoolean() then
                let pt = r.GetProperty("point").EnumerateArray() |> Seq.map (fun e -> e.GetDouble()) |> Seq.toArray
                return Some {| t = float32 (r.GetProperty("t").GetDouble())
                               point = V3d(pt.[0], pt.[1], pt.[2])
                               triangleId = r.GetProperty("triangleId").GetInt32() |}
            else
                return None
        }

    let closestPoint (serverUrl : string) (name : string) (index : int) (queryPoint : V3d) =
        async {
            let json = sprintf """{"name":"%s","index":%d,"point":%s}""" name index (v3 queryPoint)
            let! r = post serverUrl "/query/closest" json
            if r.GetProperty("found").GetBoolean() then
                let pt = r.GetProperty("point").EnumerateArray() |> Seq.map (fun e -> e.GetDouble()) |> Seq.toArray
                return Some {| point = V3d(pt.[0], pt.[1], pt.[2])
                               distanceSquared = float32 (r.GetProperty("distanceSquared").GetDouble())
                               triangleId = r.GetProperty("triangleId").GetInt32() |}
            else
                return None
        }

    let isoline (serverUrl : string) (name : string) (elevation : float) (seed : V3d) (maxPoints : int) : Async<V3d[]> =
        async {
            let json = sprintf """{"name":"%s","elevation":%.17g,"seed":%s,"maxPoints":%d}"""
                        name elevation (v3 seed) maxPoints
            let! r = post serverUrl "/query/isoline" json
            let pts =
                r.GetProperty("polyline").EnumerateArray() |> Seq.map (fun e ->
                    let a = e.EnumerateArray() |> Seq.map (fun v -> v.GetDouble()) |> Seq.toArray
                    V3d(a.[0], a.[1], a.[2])
                ) |> Seq.toArray
            return pts
        }

    let runIcp
            (serverUrl : string)
            (referenceName : string) (movingName : string)
            (initialTransform : M44d)
            (sampleStride : int) (maxIterations : int)
            (anchors : (V3d * float * float)[])
            (regionEps : float)
            : Async<Trafo3d * float[] * float[]> =
        async {
            let m = initialTransform
            let initJson =
                sprintf "[%s]"
                    (System.String.Join(",",
                        [| m.M00; m.M01; m.M02; m.M03
                           m.M10; m.M11; m.M12; m.M13
                           m.M20; m.M21; m.M22; m.M23
                           m.M30; m.M31; m.M32; m.M33 |]
                        |> Array.map (sprintf "%.17g")))
            let centresFlat =
                "[" +
                System.String.Join(",",
                    anchors |> Array.collect (fun (c, _, _) -> [| c.X; c.Y; c.Z |])
                            |> Array.map (sprintf "%.17g")) +
                "]"
            let sigmasFlat =
                "[" +
                System.String.Join(",",
                    anchors |> Array.map (fun (_, s, _) -> s) |> Array.map (sprintf "%.17g")) +
                "]"
            let weightsFlat =
                "[" +
                System.String.Join(",",
                    anchors |> Array.map (fun (_, _, w) -> w) |> Array.map (sprintf "%.17g")) +
                "]"
            let json =
                sprintf """{"referenceName":"%s","movingName":"%s","initialTransform":%s,"sampleStride":%d,"maxIterations":%d,"anchorCentres":%s,"anchorSigmas":%s,"anchorWeights":%s,"regionEps":%.17g}"""
                    referenceName movingName initJson sampleStride maxIterations centresFlat sigmasFlat weightsFlat regionEps
            let! r = post serverUrl "/query/icp" json
            let tf =
                r.GetProperty("transform").EnumerateArray()
                |> Seq.map (fun e -> e.GetDouble()) |> Seq.toArray
            let conv =
                r.GetProperty("convergence").EnumerateArray()
                |> Seq.map (fun e -> e.GetDouble()) |> Seq.toArray
            let resi =
                r.GetProperty("residuals").EnumerateArray()
                |> Seq.map (fun e -> e.GetDouble()) |> Seq.toArray
            let fwd =
                M44d(tf.[0],  tf.[1],  tf.[2],  tf.[3],
                     tf.[4],  tf.[5],  tf.[6],  tf.[7],
                     tf.[8],  tf.[9],  tf.[10], tf.[11],
                     tf.[12], tf.[13], tf.[14], tf.[15])
            let trafo = Trafo3d(fwd, fwd.Inverse)
            return trafo, conv, resi
        }

    // Weighted rigid landmark solve (coarse registration). pairs =
    // (refPoint, movingPoint, weight) in world space at current poses.
    // Returns (worldDelta, perPairResiduals, covEigenvalues, collinearityWarning).
    let lsqPairs (serverUrl : string) (movingName : string) (pairs : (V3d * V3d * float)[])
            : Async<M44d * float[] * float[] * bool> =
        async {
            let pairJson =
                pairs
                |> Array.map (fun (r, m, w) ->
                    sprintf """{"refPoint":%s,"movingPoint":%s,"weight":%.17g}""" (v3 r) (v3 m) w)
                |> String.concat ","
            let json = sprintf """{"movingName":"%s","pairs":[%s]}""" movingName pairJson
            let! r = post serverUrl "/query/lsq-pairs" json
            let tf =
                r.GetProperty("transform").EnumerateArray()
                |> Seq.map (fun e -> e.GetDouble()) |> Seq.toArray
            let delta =
                M44d(tf.[0],  tf.[1],  tf.[2],  tf.[3],
                     tf.[4],  tf.[5],  tf.[6],  tf.[7],
                     tf.[8],  tf.[9],  tf.[10], tf.[11],
                     tf.[12], tf.[13], tf.[14], tf.[15])
            let residuals =
                r.GetProperty("perPairResiduals").EnumerateArray()
                |> Seq.map (fun e -> e.GetDouble()) |> Seq.toArray
            let cond = r.GetProperty("conditioning")
            let eigen =
                cond.GetProperty("eigenvalues").EnumerateArray()
                |> Seq.map (fun e -> e.GetDouble()) |> Seq.toArray
            let collinear = cond.GetProperty("collinearityWarning").GetBoolean()
            return delta, residuals, eigen, collinear
        }

    // Patch sampler with the shared-frame override, per-point atlas UVs and
    // triangle index triples (patch small-multiples picker; (px,py) are the
    // planar frame projection in this mode). frame directions are in the
    // mesh's own frame; pass None for the reference patch (local plane fit).
    let patchInFrame
            (serverUrl : string) (name : string) (centre : V3d) (radius : float) (maxPoints : int)
            (frame : (V3d * V3d) option)
            : Async<(V2d * V3d * V2d)[] * int[] * V3d * V3d> =
        async {
            let frameJson =
                match frame with
                | Some (n, r) -> sprintf ""","frameNormal":%s,"frameRefDir":%s""" (v3 n) (v3 r)
                | None -> ""
            let json = sprintf """{"name":"%s","centre":%s,"radius":%.17g,"maxPoints":%d,"triangles":true%s}"""
                        name (v3 centre) radius maxPoints frameJson
            let! r = post serverUrl "/query/patch" json
            let pts =
                r.GetProperty("points").EnumerateArray() |> Seq.map (fun e ->
                    let a = e.EnumerateArray() |> Seq.map (fun v -> v.GetDouble()) |> Seq.toArray
                    let uv = if a.Length >= 7 then V2d(a.[5], a.[6]) else V2d.Zero
                    V2d(a.[0], a.[1]), V3d(a.[2], a.[3], a.[4]), uv
                ) |> Seq.toArray
            let tris =
                match r.TryGetProperty "triangles" with
                | true, t -> t.EnumerateArray() |> Seq.map (fun e -> e.GetInt32()) |> Seq.toArray
                | _ -> [||]
            let readVec (prop : string) =
                let a = r.GetProperty(prop).EnumerateArray() |> Seq.map (fun e -> e.GetDouble()) |> Seq.toArray
                V3d(a.[0], a.[1], a.[2])
            return pts, tris, readVec "refDir", readVec "normal"
        }

    let patch (serverUrl : string) (name : string) (centre : V3d) (radius : float) (maxPoints : int) : Async<(V2d * V3d)[] * V3d * V3d> =
        async {
            let json = sprintf """{"name":"%s","centre":%s,"radius":%.17g,"maxPoints":%d}"""
                        name (v3 centre) radius maxPoints
            let! r = post serverUrl "/query/patch" json
            let pts =
                r.GetProperty("points").EnumerateArray() |> Seq.map (fun e ->
                    let a = e.EnumerateArray() |> Seq.map (fun v -> v.GetDouble()) |> Seq.toArray
                    V2d(a.[0], a.[1]), V3d(a.[2], a.[3], a.[4])
                ) |> Seq.toArray
            let readVec (prop : string) =
                let a = r.GetProperty(prop).EnumerateArray() |> Seq.map (fun e -> e.GetDouble()) |> Seq.toArray
                V3d(a.[0], a.[1], a.[2])
            return pts, readVec "refDir", readVec "normal"
        }

    let curvatureRidge (serverUrl : string) (name : string) (seed : V3d) (thresholdRad : float) (maxPoints : int) : Async<V3d[] * float[]> =
        async {
            let json = sprintf """{"name":"%s","seed":%s,"thresholdRad":%.17g,"maxPoints":%d}"""
                        name (v3 seed) thresholdRad maxPoints
            let! r = post serverUrl "/query/curvature-ridge" json
            let pts =
                r.GetProperty("polyline").EnumerateArray() |> Seq.map (fun e ->
                    let a = e.EnumerateArray() |> Seq.map (fun v -> v.GetDouble()) |> Seq.toArray
                    V3d(a.[0], a.[1], a.[2])
                ) |> Seq.toArray
            let scalars =
                r.GetProperty("scalars").EnumerateArray() |> Seq.map (fun e -> e.GetDouble())
                |> Seq.toArray
            return pts, scalars
        }

    // Sphere–surface contact rings; centre is in the mesh's own (untransformed)
    // world frame, points come back in the same frame.
    let contactRings (serverUrl : string) (name : string) (centre : V3d) (radius : float) (maxPoints : int) : Async<V3d[][]> =
        async {
            let json = sprintf """{"name":"%s","centre":%s,"radius":%.17g,"maxPoints":%d}"""
                        name (v3 centre) radius maxPoints
            let! r = post serverUrl "/query/contact-rings" json
            return
                r.GetProperty("rings").EnumerateArray()
                |> Seq.map (fun ring ->
                    ring.EnumerateArray()
                    |> Seq.map (fun p ->
                        let a = p.EnumerateArray() |> Seq.map (fun v -> v.GetDouble()) |> Seq.toArray
                        V3d(a.[0], a.[1], a.[2]))
                    |> Seq.toArray)
                |> Seq.toArray
        }

    // length <= 0.0 → server auto-computes from the union bbox extent along the normal.
    let probe
            (serverUrl : string)
            (meshes : (string * M44d) list)
            (referenceName : string)
            (centre : V3d) (radius : float) (length : float)
            (maxPointsPerMesh : int)
            : Async<Result<ProbeResult, string>> =
        async {
            let m44 (m : M44d) =
                System.String.Join(",",
                    [| m.M00; m.M01; m.M02; m.M03
                       m.M10; m.M11; m.M12; m.M13
                       m.M20; m.M21; m.M22; m.M23
                       m.M30; m.M31; m.M32; m.M33 |]
                    |> Array.map (sprintf "%.17g"))
            let meshesJson =
                meshes
                |> List.map (fun (n, t) -> sprintf """{"name":"%s","transform":[%s]}""" n (m44 t))
                |> String.concat ","
            let json =
                sprintf """{"meshes":[%s],"referenceName":"%s","centre":%s,"radius":%.17g,"length":%.17g,"maxPointsPerMesh":%d}"""
                    meshesJson referenceName (v3 centre) radius length maxPointsPerMesh
            let! r = post serverUrl "/query/probe" json
            if not (r.GetProperty("ok").GetBoolean()) then
                return Result.Error (r.GetProperty("reason").GetString())
            else
                let readVec (prop : string) =
                    let a = r.GetProperty(prop).EnumerateArray() |> Seq.map (fun e -> e.GetDouble()) |> Seq.toArray
                    V3d(a.[0], a.[1], a.[2])
                let readRange (prop : string) =
                    let a = r.GetProperty(prop).EnumerateArray() |> Seq.map (fun e -> e.GetDouble()) |> Seq.toArray
                    Range1d(a.[0], a.[1])
                let dists =
                    r.GetProperty("distributions").EnumerateArray()
                    |> Seq.map (fun d ->
                        {
                            MeshName  = d.GetProperty("name").GetString()
                            Count     = d.GetProperty("count").GetInt32()
                            Median    = d.GetProperty("median").GetDouble()
                            Q1        = d.GetProperty("q1").GetDouble()
                            Q3        = d.GetProperty("q3").GetDouble()
                            Std       = d.GetProperty("std").GetDouble()
                            Bandwidth = d.GetProperty("bandwidth").GetDouble()
                            Kde       =
                                d.GetProperty("kde").EnumerateArray()
                                |> Seq.map (fun p ->
                                    let a = p.EnumerateArray() |> Seq.map (fun e -> e.GetDouble()) |> Seq.toArray
                                    a.[0], a.[1])
                                |> Seq.toArray
                            Samples   =
                                match d.TryGetProperty "samples" with
                                | true, se -> se.EnumerateArray() |> Seq.map (fun e -> e.GetDouble()) |> Seq.toArray
                                | _ -> [||]
                        })
                    |> Seq.toArray
                let s = r.GetProperty("sources")
                let perMesh =
                    s.GetProperty("perMesh").EnumerateArray()
                    |> Seq.map (fun p ->
                        {
                            MeshName     = p.GetProperty("name").GetString()
                            IqrMetres    = p.GetProperty("iqr").GetDouble()
                            MedianOffset = p.GetProperty("medianOffset").GetDouble()
                            PointCount   = p.GetProperty("count").GetInt32()
                        })
                    |> Seq.toArray
                let planarity = r.GetProperty("planarity").GetDouble()
                return Result.Ok {
                    ReferenceMesh = referenceName
                    Normal        = readVec "normal"
                    Planarity     = planarity
                    Planar        = planarity <= 0.5
                    Length        = r.GetProperty("length").GetDouble()
                    AutoLength    = r.GetProperty("autoLength").GetDouble()
                    XAuto         = readRange "xAuto"
                    XFit          = readRange "xFit"
                    Distributions = dists
                    Sources       =
                        {
                            DatasetError      = s.GetProperty("dataset").GetDouble()
                            AlgorithmResid    = s.GetProperty("algorithm").GetDouble()
                            LocalConditioning = s.GetProperty("conditioning").GetDouble()
                            PerMesh           = perMesh
                        }
                    ComputedAt    = System.DateTime.UtcNow
                }
        }

    let rayHitMany (serverUrl : string) (names : string list) (rayFor : string -> V3d * V3d) =
        names
        |> List.map (fun name ->
            async {
                let origin, dir = rayFor name
                let! h = rayHit serverUrl name 0 origin dir
                return h |> Option.map (fun hit -> name, hit)
            })
        |> Async.Parallel

