namespace Superprojekt

open Aardvark.Base

module Query =

    open System.Net.Http
    open System.Text
    open System.Text.Json

    let private v3 (v : V3d) = sprintf "[%.17g,%.17g,%.17g]" v.X v.Y v.Z

    let private post (serverUrl : string) (path : string) (json : string) : Async<JsonElement> =
        async {
            use client = new HttpClient()
            use content = new StringContent(json, Encoding.UTF8, "application/json")
            let! resp = client.PostAsync(serverUrl.TrimEnd('/') + path, content) |> Async.AwaitTask
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

    let planeIntersection (serverUrl : string) (name : string) (index : int) (planePoint : V3d) (planeNormal : V3d) (axisU : V3d) (axisV : V3d) (thickness : float) (maxExtentU : float) (maxExtentV : float) =
        async {
            let json = sprintf """{"name":"%s","index":%d,"planePoint":%s,"planeNormal":%s,"axisU":%s,"axisV":%s,"thickness":%.17g,"maxExtentU":%.17g,"maxExtentV":%.17g}"""
                        name index (v3 planePoint) (v3 planeNormal) (v3 axisU) (v3 axisV) thickness maxExtentU maxExtentV
            let! r = post serverUrl "/query/plane-intersection" json
            let segments =
                r.GetProperty("segments").EnumerateArray() |> Seq.map (fun seg ->
                    let a = seg.EnumerateArray() |> Seq.map (fun e -> e.GetDouble()) |> Seq.toArray
                    V2d(a.[0], a.[1]), V2d(a.[2], a.[3])
                ) |> Seq.toList
            return segments
        }

    let planeIntersectionBatch (serverUrl : string) (names : string[]) (planePoint : V3d) (planeNormal : V3d) (axisU : V3d) (axisV : V3d) (thickness : float) (maxExtentU : float) (maxExtentV : float) =
        async {
            let namesJson =
                let sb = System.Text.StringBuilder()
                sb.Append('[') |> ignore
                for i in 0 .. names.Length - 1 do
                    if i > 0 then sb.Append(',') |> ignore
                    sb.Append('"').Append(names.[i]).Append('"') |> ignore
                sb.Append(']') |> ignore
                sb.ToString()
            let json = sprintf """{"names":%s,"planePoint":%s,"planeNormal":%s,"axisU":%s,"axisV":%s,"thickness":%.17g,"maxExtentU":%.17g,"maxExtentV":%.17g}"""
                        namesJson (v3 planePoint) (v3 planeNormal) (v3 axisU) (v3 axisV) thickness maxExtentU maxExtentV
            let! r = post serverUrl "/query/plane-intersection-batch" json
            let results =
                r.GetProperty("results").EnumerateArray() |> Seq.map (fun entry ->
                    let name = entry.GetProperty("name").GetString()
                    let segments =
                        entry.GetProperty("segments").EnumerateArray() |> Seq.map (fun seg ->
                            let a = seg.EnumerateArray() |> Seq.map (fun e -> e.GetDouble()) |> Seq.toArray
                            V2d(a.[0], a.[1]), V2d(a.[2], a.[3])
                        ) |> Seq.toList
                    name, segments
                ) |> Seq.toList
            return results
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

    let rayGrid (serverUrl : string) (names : string[]) (rays : (V3d * V3d)[]) : Async<(V3d * V3d) option[]> =
        async {
            let namesJson =
                let sb = System.Text.StringBuilder()
                sb.Append('[') |> ignore
                for i in 0 .. names.Length - 1 do
                    if i > 0 then sb.Append(',') |> ignore
                    sb.Append('"').Append(names.[i]).Append('"') |> ignore
                sb.Append(']') |> ignore
                sb.ToString()
            let flatten (pick : V3d * V3d -> V3d) =
                let parts = ResizeArray<string>()
                for i in 0 .. rays.Length - 1 do
                    let v = pick rays.[i]
                    parts.Add(sprintf "%.17g,%.17g,%.17g" v.X v.Y v.Z)
                "[" + String.concat "," parts + "]"
            let originsJson    = flatten fst
            let directionsJson = flatten snd
            let json = sprintf """{"names":%s,"origins":%s,"directions":%s}""" namesJson originsJson directionsJson
            use client = new HttpClient()
            use content = new StringContent(json, Encoding.UTF8, "application/json")
            let! resp = client.PostAsync(serverUrl.TrimEnd('/') + "/query/ray-grid", content) |> Async.AwaitTask
            resp.EnsureSuccessStatusCode() |> ignore
            let! buf = resp.Content.ReadAsByteArrayAsync() |> Async.AwaitTask
            let mutable o = 0
            let rayCount = System.BitConverter.ToInt32(buf, o)
            o <- o + 4
            let results = Array.zeroCreate<(V3d * V3d) option> rayCount
            for i in 0 .. rayCount - 1 do
                let flag = buf.[o]
                o <- o + 1
                let hx = System.BitConverter.ToDouble(buf, o)
                o <- o + 8
                let hy = System.BitConverter.ToDouble(buf, o)
                o <- o + 8
                let hz = System.BitConverter.ToDouble(buf, o)
                o <- o + 8
                let nx = float (System.BitConverter.ToSingle(buf, o))
                o <- o + 4
                let ny = float (System.BitConverter.ToSingle(buf, o))
                o <- o + 4
                let nz = float (System.BitConverter.ToSingle(buf, o))
                o <- o + 4
                results.[i] <- if flag <> 0uy then Some (V3d(hx, hy, hz), V3d(nx, ny, nz)) else None
            return results
        }

    let rayBatch (serverUrl : string) (names : string[]) (rays : (V3d * V3d)[]) : Async<V3d option[]> =
        async {
            let namesJson =
                let sb = System.Text.StringBuilder()
                sb.Append('[') |> ignore
                for i in 0 .. names.Length - 1 do
                    if i > 0 then sb.Append(',') |> ignore
                    sb.Append('"').Append(names.[i]).Append('"') |> ignore
                sb.Append(']') |> ignore
                sb.ToString()
            let flatten (pick : V3d * V3d -> V3d) =
                let parts = ResizeArray<string>()
                for i in 0 .. rays.Length - 1 do
                    let v = pick rays.[i]
                    parts.Add(sprintf "%.17g,%.17g,%.17g" v.X v.Y v.Z)
                "[" + String.concat "," parts + "]"
            let originsJson    = flatten fst
            let directionsJson = flatten snd
            let json = sprintf """{"names":%s,"origins":%s,"directions":%s}""" namesJson originsJson directionsJson
            use client = new HttpClient()
            use content = new StringContent(json, Encoding.UTF8, "application/json")
            let! resp = client.PostAsync(serverUrl.TrimEnd('/') + "/query/ray-batch", content) |> Async.AwaitTask
            resp.EnsureSuccessStatusCode() |> ignore
            let! buf = resp.Content.ReadAsByteArrayAsync() |> Async.AwaitTask
            let mutable o = 0
            let rayCount = System.BitConverter.ToInt32(buf, o)
            o <- o + 4
            let results = Array.zeroCreate<V3d option> rayCount
            for i in 0 .. rayCount - 1 do
                let flag = buf.[o]
                o <- o + 1
                let x = System.BitConverter.ToDouble(buf, o)
                o <- o + 8
                let y = System.BitConverter.ToDouble(buf, o)
                o <- o + 8
                let z = System.BitConverter.ToDouble(buf, o)
                o <- o + 8
                results.[i] <- if flag <> 0uy then Some (V3d(x, y, z)) else None
            return results
        }

    let private postBinary (serverUrl : string) (path : string) (json : string) : Async<byte[]> =
        async {
            use client = new HttpClient()
            use content = new StringContent(json, Encoding.UTF8, "application/json")
            let! resp = client.PostAsync(serverUrl.TrimEnd('/') + path, content) |> Async.AwaitTask
            resp.EnsureSuccessStatusCode() |> ignore
            return! resp.Content.ReadAsByteArrayAsync() |> Async.AwaitTask
        }

    let cylinderEval (serverUrl : string) (dataset : string) (anchor : V3d) (axis : V3d) (radii : float[]) (angularRes : int) (extFwd : float) (extBack : float) =
        async {
            let radiiJson =
                "[" + (radii |> Array.map (sprintf "%.17g") |> String.concat ",") + "]"
            let json = sprintf """{"dataset":"%s","anchor":%s,"axis":%s,"radii":%s,"angularResolution":%d,"extentForward":%.17g,"extentBackward":%.17g}"""
                        dataset (v3 anchor) (v3 axis) radiiJson angularRes extFwd extBack
            let! buf = postBinary serverUrl "/query/cylinder-eval" json
            let mutable off = 0
            let readInt () =
                let v = System.BitConverter.ToInt32(buf, off)
                off <- off + 4; v
            let readFloat () =
                let v = System.BitConverter.ToDouble(buf, off)
                off <- off + 8; v
            let res = readInt ()
            let ringCount = readInt ()
            let hitCount = readInt ()
            let perRingAngle =
                Array.init ringCount (fun _ ->
                    Array.init res (fun _ -> ResizeArray<float * string>()))
            for _ in 0 .. hitCount - 1 do
                let ri = readInt ()
                let ai = readInt ()
                let nameLen = readInt ()
                let name = Encoding.UTF8.GetString(buf, off, nameLen)
                off <- off + nameLen
                let h = readFloat ()
                perRingAngle.[ri].[ai].Add(h, dataset + "/" + name)
            return res, ringCount, perRingAngle
        }
