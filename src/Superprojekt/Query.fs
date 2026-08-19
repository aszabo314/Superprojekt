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

    // The parser runs inside the document's scope so the JsonDocument (whose
    // backing buffer is an ArrayPool rental) can be disposed — parsers must
    // fully materialise their result, nothing lazy may escape the callback.
    let private post (serverUrl : string) (path : string) (json : string)
                     (parse : JsonElement -> 'T) : Async<'T> =
        async {
            use content = new StringContent(json, Encoding.UTF8, "application/json")
            let! resp = Http.client.PostAsync(serverUrl.TrimEnd('/') + path, content) |> Async.AwaitTask
            resp.EnsureSuccessStatusCode() |> ignore
            let! text = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
            use doc = JsonDocument.Parse text
            return parse doc.RootElement
        }

    let rayHit (serverUrl : string) (name : string) (index : int) (origin : V3d) (direction : V3d) =
        async {
            let json = sprintf """{"name":"%s","index":%d,"origin":%s,"direction":%s}""" name index (v3 origin) (v3 direction)
            return! post serverUrl "/query/ray" json (fun r ->
                if r.GetProperty("hit").GetBoolean() then
                    Some {| t = float32 (r.GetProperty("t").GetDouble())
                            point = readV3 (r.GetProperty "point")
                            triangleId = r.GetProperty("triangleId").GetInt32() |}
                else None)
        }

    // Weighted rigid landmark solve for one pair edge: pairs = (parentPoint,
    // childPoint, weight) at the AS-LOADED baselines — the returned world
    // transform maps the child points onto the parent points (the edge
    // transform convention); perPairResiduals feed the edge quality.
    let lsqPairs (serverUrl : string) (movingName : string) (pairs : (V3d * V3d * float)[])
            : Async<M44d * float[]> =
        async {
            let pairJson =
                pairs
                |> Array.map (fun (r, m, w) ->
                    sprintf """{"refPoint":%s,"movingPoint":%s,"weight":%.17g}""" (v3 r) (v3 m) w)
                |> String.concat ","
            let json = sprintf """{"movingName":"%s","pairs":[%s]}""" movingName pairJson
            return! post serverUrl "/query/lsq-pairs" json (fun r ->
                let tf =
                    r.GetProperty("transform").EnumerateArray()
                    |> Seq.map (fun e -> e.GetDouble()) |> Seq.toArray
                let residuals =
                    r.GetProperty("perPairResiduals").EnumerateArray()
                    |> Seq.map (fun e -> e.GetDouble()) |> Seq.toArray
                M44d(tf.[0],  tf.[1],  tf.[2],  tf.[3],
                     tf.[4],  tf.[5],  tf.[6],  tf.[7],
                     tf.[8],  tf.[9],  tf.[10], tf.[11],
                     tf.[12], tf.[13], tf.[14], tf.[15]), residuals)
        }

    // Sphere–surface contact rings; centre is in the mesh's own (untransformed)
    // world frame, points come back in the same frame.
    let contactRings (serverUrl : string) (name : string) (centre : V3d) (radius : float) (maxPoints : int) : Async<V3d[][]> =
        async {
            let json = sprintf """{"name":"%s","centre":%s,"radius":%.17g,"maxPoints":%d}"""
                        name (v3 centre) radius maxPoints
            return! post serverUrl "/query/contact-rings" json (fun r ->
                r.GetProperty("rings").EnumerateArray()
                |> Seq.map (fun ring -> ring.EnumerateArray() |> Seq.map readV3 |> Seq.toArray)
                |> Seq.toArray)
        }

    // Correspondence-point reveal: concentric contact rings + plane∩surface
    // relief cuts around a point, one flat polyline list. Point, plane normals
    // and result all live in the mesh's own (untransformed) world frame.
    let pointReveal (serverUrl : string) (name : string) (point : V3d)
                    (radii : float[]) (planes : V3d[]) (maxPoints : int) : Async<V3d[][]> =
        async {
            let arr (vs : string seq) = "[" + String.concat "," vs + "]"
            let json =
                sprintf """{"name":"%s","point":%s,"radii":%s,"planes":%s,"maxPoints":%d}"""
                    name (v3 point)
                    (arr (radii |> Seq.map (sprintf "%.17g")))
                    (arr (planes |> Seq.map v3))
                    maxPoints
            return! post serverUrl "/query/point-reveal" json (fun r ->
                r.GetProperty("lines").EnumerateArray()
                |> Seq.map (fun line -> line.EnumerateArray() |> Seq.map readV3 |> Seq.toArray)
                |> Seq.toArray)
        }

    // One pin's pairwise error distribution (see the server PairError module
    // for the measure): pooled signed samples of meshB relative to meshA with
    // world positions aligned 1:1, plus median and the LoD half-width.
    type PairPinError = {
        Ok           : bool
        Count        : int
        Median       : float
        LodHalfWidth : float
        Samples      : float[]
        Positions    : V3d[]
    }

    // Pairwise pin-error batch at explicit poses; results in request-pin order.
    let pairError
            (serverUrl : string)
            (meshA : string) (tA : M44d)
            (meshB : string) (tB : M44d)
            (pins : (string * V3d * float) list)
            : Async<PairPinError[]> =
        async {
            let pinsJson =
                pins
                |> List.map (fun (id, c, r) ->
                    sprintf """{"id":"%s","centre":%s,"radius":%.17g}""" id (v3 c) r)
                |> String.concat ","
            let json =
                sprintf """{"meshA":{"name":"%s","transform":[%s]},"meshB":{"name":"%s","transform":[%s]},"pins":[%s],"length":0,"maxPointsPerMesh":8192}"""
                    meshA (m44json tA) meshB (m44json tB) pinsJson
            return! post serverUrl "/query/pair-error" json (fun r ->
                r.GetProperty("pins").EnumerateArray()
                |> Seq.map (fun p ->
                    let samples =
                        p.GetProperty("samples").EnumerateArray()
                        |> Seq.map (fun e -> e.GetDouble()) |> Seq.toArray
                    let flat =
                        p.GetProperty("positions").EnumerateArray()
                        |> Seq.map (fun e -> e.GetDouble()) |> Seq.toArray
                    {
                        Ok           = p.GetProperty("ok").GetBoolean()
                        Count        = p.GetProperty("count").GetInt32()
                        Median       = p.GetProperty("median").GetDouble()
                        LodHalfWidth = p.GetProperty("lodHalfWidth").GetDouble()
                        Samples      = samples
                        Positions    = Array.init (flat.Length / 3) (fun i -> V3d(flat.[i*3], flat.[i*3+1], flat.[i*3+2]))
                    })
                |> Seq.toArray)
        }

    // Exact pairwise error at one picked world point (meshB relative to meshA).
    let pairErrorAt
            (serverUrl : string)
            (meshA : string) (tA : M44d)
            (meshB : string) (tB : M44d)
            (point : V3d) (radius : float)
            : Async<float option> =
        async {
            let json =
                sprintf """{"meshA":{"name":"%s","transform":[%s]},"meshB":{"name":"%s","transform":[%s]},"point":%s,"radius":%.17g,"maxDist":100}"""
                    meshA (m44json tA) meshB (m44json tB) (v3 point) radius
            return! post serverUrl "/query/pair-error-at" json (fun r ->
                if r.GetProperty("ok").GetBoolean()
                then Some (r.GetProperty("value").GetDouble())
                else None)
        }

    // Adaptive ROI fit: the smallest radius ≥ radius whose sphere captures
    // ≥ minVerts vertices of the OTHER pair mesh, capped at radius×maxFactor.
    // ok=false = the location cannot host a correspondence ROI at all.
    let roiFit
            (serverUrl : string)
            (otherName : string) (otherT : M44d)
            (centre : V3d) (radius : float) (minVerts : int) (maxFactor : float)
            : Async<bool * float> =
        async {
            let json =
                sprintf """{"otherName":"%s","otherTransform":[%s],"centre":%s,"radius":%.17g,"minVerts":%d,"maxFactor":%.17g}"""
                    otherName (m44json otherT) (v3 centre) radius minVerts maxFactor
            return! post serverUrl "/query/roi-fit" json (fun r ->
                r.GetProperty("ok").GetBoolean(), r.GetProperty("radius").GetDouble())
        }

    // Per-vertex signed distance of the MOV mesh to the REF at explicit poses,
    // in MOV's served vertex order (the in-cell false-colour map's buffer).
    // The measure is opaque — the endpoint has exactly one metric.
    let regionDistance
            (serverUrl : string)
            (targetName : string) (refName : string)
            (targetTransform : M44d) (refTransform : M44d)
            : Async<float32[]> =
        async {
            let json =
                sprintf """{"targetName":"%s","targetIndex":0,"refName":"%s","refIndex":0,"targetTransform":[%s],"refTransform":[%s]}"""
                    targetName refName (m44json targetTransform) (m44json refTransform)
            return! post serverUrl "/query/region-distance" json (fun r ->
                // Multi-million-element response: preallocate instead of
                // Seq.toArray's ResizeArray doubling.
                let dist = r.GetProperty "dist"
                let arr = Array.zeroCreate<float32> (dist.GetArrayLength())
                let mutable i = 0
                for e in dist.EnumerateArray() do
                    arr.[i] <- float32 (e.GetDouble())
                    i <- i + 1
                arr)
        }

    // Per-vertex Euclidean distance of each TARGET vertex to the OTHER mesh's
    // surface at explicit poses (metric m, in the target's served vertex
    // order) — the placement feather's gate buffer.
    let pairProximity
            (serverUrl : string)
            (targetName : string) (otherName : string)
            (targetTransform : M44d) (otherTransform : M44d)
            : Async<float32[]> =
        async {
            let json =
                sprintf """{"targetName":"%s","targetIndex":0,"otherName":"%s","otherIndex":0,"targetTransform":[%s],"otherTransform":[%s]}"""
                    targetName otherName (m44json targetTransform) (m44json otherTransform)
            return! post serverUrl "/query/pair-proximity" json (fun r ->
                let dist = r.GetProperty "dist"
                let arr = Array.zeroCreate<float32> (dist.GetArrayLength())
                let mutable i = 0
                for e in dist.EnumerateArray() do
                    arr.[i] <- float32 (e.GetDouble())
                    i <- i + 1
                arr)
        }

    // Pairwise overlap sufficiency at explicit poses (server defaults for the
    // distance/fraction/sampling knobs) → can the pair be registered at all.
    let pairOverlap
            (serverUrl : string)
            (meshA : string) (tA : M44d)
            (meshB : string) (tB : M44d)
            : Async<bool> =
        async {
            let json =
                sprintf """{"meshA":{"name":"%s","transform":[%s]},"meshB":{"name":"%s","transform":[%s]},"maxDist":0,"minFraction":0,"maxSamples":0}"""
                    meshA (m44json tA) meshB (m44json tB)
            return! post serverUrl "/query/pair-overlap" json (fun r ->
                r.GetProperty("sufficient").GetBoolean())
        }
