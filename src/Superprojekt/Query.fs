namespace Superprojekt

open Aardvark.Base

module Query =

    open System.Net.Http
    open System.Text
    open System.Text.Json

    let private v3 (v : V3d) = sprintf "[%.17g,%.17g,%.17g]" v.X v.Y v.Z

    let private readV3 (e : JsonElement) =
        let a = e.EnumerateArray() |> Seq.map (fun v -> v.GetDouble()) |> Seq.toArray
        V3d(a.[0], a.[1], a.[2])

    let private m44json (m : M44d) =
        System.String.Join(",",
            [| m.M00; m.M01; m.M02; m.M03
               m.M10; m.M11; m.M12; m.M13
               m.M20; m.M21; m.M22; m.M23
               m.M30; m.M31; m.M32; m.M33 |]
            |> Array.map (sprintf "%.17g"))

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
                return Some {| t = float32 (r.GetProperty("t").GetDouble())
                               point = readV3 (r.GetProperty "point")
                               triangleId = r.GetProperty("triangleId").GetInt32() |}
            else
                return None
        }

    let closestPoint (serverUrl : string) (name : string) (index : int) (queryPoint : V3d) =
        async {
            let json = sprintf """{"name":"%s","index":%d,"point":%s}""" name index (v3 queryPoint)
            let! r = post serverUrl "/query/closest" json
            if r.GetProperty("found").GetBoolean() then
                return Some {| point = readV3 (r.GetProperty "point")
                               distanceSquared = float32 (r.GetProperty("distanceSquared").GetDouble())
                               triangleId = r.GetProperty("triangleId").GetInt32() |}
            else
                return None
        }

    // Weighted rigid landmark solve. pairs = (refPoint, movingPoint, weight); the
    // moving point is taken at the load pose, so the returned transform is absolute
    // (refTransform, perPairResiduals, covEigenvalues, collinearityWarning).
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

    // Sphere–surface contact rings; centre is in the mesh's own (untransformed)
    // world frame, points come back in the same frame.
    let contactRings (serverUrl : string) (name : string) (centre : V3d) (radius : float) (maxPoints : int) : Async<V3d[][]> =
        async {
            let json = sprintf """{"name":"%s","centre":%s,"radius":%.17g,"maxPoints":%d}"""
                        name (v3 centre) radius maxPoints
            let! r = post serverUrl "/query/contact-rings" json
            return
                r.GetProperty("rings").EnumerateArray()
                |> Seq.map (fun ring -> ring.EnumerateArray() |> Seq.map readV3 |> Seq.toArray)
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
            let meshesJson =
                meshes
                |> List.map (fun (n, t) -> sprintf """{"name":"%s","transform":[%s]}""" n (m44json t))
                |> String.concat ","
            let json =
                sprintf """{"meshes":[%s],"referenceName":"%s","centre":%s,"radius":%.17g,"length":%.17g,"maxPointsPerMesh":%d}"""
                    meshesJson referenceName (v3 centre) radius length maxPointsPerMesh
            let! r = post serverUrl "/query/probe" json
            if not (r.GetProperty("ok").GetBoolean()) then
                return Result.Error (r.GetProperty("reason").GetString())
            else
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
                            Positions =
                                match d.TryGetProperty "positions" with
                                | true, pe ->
                                    let flat = pe.EnumerateArray() |> Seq.map (fun e -> e.GetDouble()) |> Seq.toArray
                                    Array.init (flat.Length / 3) (fun i -> V3d(flat.[i*3], flat.[i*3+1], flat.[i*3+2]))
                                | _ -> [||]
                            Footprint =
                                match d.TryGetProperty "footprint" with
                                | true, fe -> fe.GetDouble()
                                | _ -> 0.0
                            Intrinsics =
                                match d.TryGetProperty "intr" with
                                | true, ie -> ie.EnumerateArray() |> Seq.map (fun e -> e.GetDouble()) |> Seq.toArray
                                | _ -> [||]
                        })
                    |> Seq.toArray
                let s = r.GetProperty("sources")
                return Result.Ok {
                    ReferenceMesh = referenceName
                    Normal        = readV3 (r.GetProperty "normal")
                    Length        = r.GetProperty("length").GetDouble()
                    RefOffset     =
                        (match r.TryGetProperty "refOffset" with
                         | true, v -> v.GetDouble()
                         | _ -> 0.0)
                    XAuto         = readRange "xAuto"
                    Distributions = dists
                    Sources       =
                        {
                            DatasetError      = s.GetProperty("dataset").GetDouble()
                            AlgorithmResid    = s.GetProperty("algorithm").GetDouble()
                            LocalConditioning = s.GetProperty("conditioning").GetDouble()
                        }
                }
        }

    // Per-vertex signed distance of a target mesh to the reference, in the
    // target's served vertex order. Transforms are world-space rigid M44 (Forward).
    let regionDistance
            (serverUrl : string)
            (targetName : string) (targetIndex : int)
            (refName : string) (refIndex : int)
            (targetTransform : M44d) (refTransform : M44d) (mode : int)
            : Async<float32[]> =
        async {
            let json =
                sprintf """{"targetName":"%s","targetIndex":%d,"refName":"%s","refIndex":%d,"targetTransform":[%s],"refTransform":[%s],"mode":%d}"""
                    targetName targetIndex refName refIndex (m44json targetTransform) (m44json refTransform) mode
            let! r = post serverUrl "/query/region-distance" json
            return
                r.GetProperty("dist").EnumerateArray()
                |> Seq.map (fun e -> float32 (e.GetDouble()))
                |> Seq.toArray
        }

