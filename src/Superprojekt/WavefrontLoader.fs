namespace Superprojekt

open Aardvark.Base
open Microsoft.FSharp.NativeInterop

#nowarn "9"

type MeshData =
    {
        centroid  : V3d
        positions : V3f[]
        uvs       : V2f[]
        indices   : int[]
        atlasUrl  : string
    }

module MeshData =

    // Binary layout (little-endian) — matches Superserver:
    //   magic           4 bytes  "MESH"
    //   vertexCount     int32
    //   indexCount      int32
    //   centroid X/Y/Z  3 × float64
    //   positions       vertexCount × 3 × float32   (centroid-relative)
    //   uvs             vertexCount × 2 × float32
    //   indices         indexCount × int32

    let decode (atlasUrl : string) (data : byte[]) : MeshData =
        use ptr = fixed data
        let mutable ptr = ptr

        let inline readByte () =
            let v = NativePtr.read ptr
            ptr <- NativePtr.add ptr 1
            v

        let inline readInt32 () =
            let v : int = NativePtr.read (NativePtr.cast ptr)
            ptr <- NativePtr.add ptr 4
            v

        let inline readDouble () =
            let v : float = NativePtr.read (NativePtr.cast ptr)
            ptr <- NativePtr.add ptr 8
            v

        let a = readByte ()
        let b = readByte ()
        let c = readByte ()
        let d = readByte ()
        if [| a; b; c; d |] <> "MESH"B then failwith "invalid mesh magic"

        let vertexCount = readInt32 ()
        let indexCount  = readInt32 ()
        let centroid    = V3d(readDouble (), readDouble (), readDouble ())

        let positions = Array.zeroCreate<V3f> vertexCount
        System.Span<V3f>(NativePtr.toVoidPtr ptr, vertexCount).CopyTo(positions)
        ptr <- NativePtr.add ptr (vertexCount * sizeof<V3f>)

        let uvs = Array.zeroCreate<V2f> vertexCount
        System.Span<V2f>(NativePtr.toVoidPtr ptr, vertexCount).CopyTo(uvs)
        ptr <- NativePtr.add ptr (vertexCount * sizeof<V2f>)

        let indices = Array.zeroCreate<int> indexCount
        System.Span<int>(NativePtr.toVoidPtr ptr, indexCount).CopyTo(indices)

        { centroid = centroid; positions = positions; uvs = uvs; indices = indices; atlasUrl = atlasUrl }

    let fetchDatasets (serverUrl : string) : Async<string[]> =
        async {
            use client = new System.Net.Http.HttpClient()
            let! json = client.GetStringAsync(serverUrl.TrimEnd('/') + "/datasets") |> Async.AwaitTask
            let doc = System.Text.Json.JsonDocument.Parse(json)
            return doc.RootElement.EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> Seq.toArray
        }

    let fetchCentroids (serverUrl : string) (dataset : string) : Async<(string * V3d)[]> =
        async {
            use client = new System.Net.Http.HttpClient()
            let url = sprintf "%s/datasets/%s/centroids" (serverUrl.TrimEnd('/')) dataset
            let! json = client.GetStringAsync(url) |> Async.AwaitTask
            let doc = System.Text.Json.JsonDocument.Parse(json)
            return
                doc.RootElement.EnumerateObject()
                |> Seq.map (fun prop ->
                    let a = prop.Value.EnumerateArray() |> Seq.map (fun e -> e.GetDouble()) |> Seq.toArray
                    dataset + "/" + prop.Name, V3d(a.[0], a.[1], a.[2])
                )
                |> Seq.toArray
        }

    /// Create a sub-mesh by selecting only the given triangle IDs.
    /// Reuses the original vertex/uv arrays — just replaces the index buffer.
    let filterByTriangles (triangleIds : int[]) (mesh : MeshData) : MeshData =
        let indices = Array.zeroCreate (triangleIds.Length * 3)
        for i = 0 to triangleIds.Length - 1 do
            let src = triangleIds.[i] * 3
            let dst = i * 3
            indices.[dst]     <- mesh.indices.[src]
            indices.[dst + 1] <- mesh.indices.[src + 1]
            indices.[dst + 2] <- mesh.indices.[src + 2]
        { mesh with indices = indices }

    /// Compact a mesh so it contains only the vertices referenced by its indices.
    /// Builds a remap from old vertex index → new vertex index using a dictionary
    /// (never iterates over all original vertices).
    let compact (mesh : MeshData) : MeshData =
        let remap = System.Collections.Generic.Dictionary<int, int>()
        let positions = System.Collections.Generic.List<V3f>()
        let uvs       = System.Collections.Generic.List<V2f>()
        let newIndices = Array.zeroCreate mesh.indices.Length
        for i = 0 to mesh.indices.Length - 1 do
            let oldIdx = mesh.indices.[i]
            let mutable newIdx = 0
            if not (remap.TryGetValue(oldIdx, &newIdx)) then
                newIdx <- positions.Count
                remap.[oldIdx] <- newIdx
                positions.Add(mesh.positions.[oldIdx])
                uvs.Add(mesh.uvs.[oldIdx])
            newIndices.[i] <- newIdx
        { mesh with positions = positions.ToArray(); uvs = uvs.ToArray(); indices = newIndices }

    let fetchBboxes (serverUrl : string) (dataset : string) : Async<(string * Box3d)[]> =
        async {
            use client = new System.Net.Http.HttpClient()
            let url = sprintf "%s/datasets/%s/bboxes" (serverUrl.TrimEnd('/')) dataset
            let! json = client.GetStringAsync(url) |> Async.AwaitTask
            let doc = System.Text.Json.JsonDocument.Parse(json)
            return
                doc.RootElement.EnumerateObject()
                |> Seq.map (fun prop ->
                    let mn = prop.Value.GetProperty("min").EnumerateArray() |> Seq.map (fun e -> e.GetDouble()) |> Seq.toArray
                    let mx = prop.Value.GetProperty("max").EnumerateArray() |> Seq.map (fun e -> e.GetDouble()) |> Seq.toArray
                    dataset + "/" + prop.Name, Box3d(V3d(mn.[0], mn.[1], mn.[2]), V3d(mx.[0], mx.[1], mx.[2]))
                )
                |> Seq.toArray
        }

    let fetch (serverUrl : string) (name : string) (index : int) : Async<MeshData> =
        async {
            let parts    = name.Split([|'/'|], 2)
            let dataset  = parts.[0]
            let meshName = parts.[1]
            use client = new System.Net.Http.HttpClient()
            let base' = serverUrl.TrimEnd('/')
            let meshUrl  = sprintf "%s/datasets/%s/mesh/%s/%d"       base' dataset meshName index
            let atlasUrl = sprintf "%s/datasets/%s/mesh/%s/%d/atlas" base' dataset meshName index
            let! bytes = client.GetByteArrayAsync(meshUrl) |> Async.AwaitTask
            return decode atlasUrl bytes
        }


module ApiConfig =
    open Aardworx.WebAssembly
    let apiBase =
        lazy (
            let href = Window.Location.Href
            let uri = System.Uri(href)
            let mutable path = uri.AbsolutePath
            if path.Contains('.') then path <- path.Substring(0, path.LastIndexOf('/') + 1)
            path <- path.TrimEnd('/')
            uri.GetLeftPart(System.UriPartial.Authority) + path + "/api"
        )

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

    /// POST /query/ray  — returns (hit, t, hitPoint, triangleId) option
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

    /// POST /query/closest  — returns (closestPoint, distanceSquared, triangleId) option
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

    let private postBinaryIndices (serverUrl : string) (path : string) (json : string) : Async<int[]> =
        async {
            use client = new HttpClient()
            use content = new StringContent(json, Encoding.UTF8, "application/json")
            let! resp = client.PostAsync(serverUrl.TrimEnd('/') + path, content) |> Async.AwaitTask
            resp.EnsureSuccessStatusCode() |> ignore
            let! buf = resp.Content.ReadAsByteArrayAsync() |> Async.AwaitTask
            let count = System.BitConverter.ToInt32(buf, 0)
            let indices = Array.zeroCreate<int> count
            System.Buffer.BlockCopy(buf, 4, indices, 0, count * 4)
            return indices
        }

    /// POST /query/sphere  — returns triangle indices (binary: int32 count + int32[])
    let sphereTriangles (serverUrl : string) (name : string) (index : int) (center : V3d) (radius : float) =
        let json = sprintf """{"name":"%s","index":%d,"center":%s,"radius":%.17g}""" name index (v3 center) radius
        postBinaryIndices serverUrl "/query/sphere" json

    /// POST /query/box  — returns triangle indices (binary: int32 count + int32[])
    let boxTriangles (serverUrl : string) (name : string) (index : int) (min : V3d) (max : V3d) =
        let json = sprintf """{"name":"%s","index":%d,"min":%s,"max":%s}""" name index (v3 min) (v3 max)
        postBinaryIndices serverUrl "/query/box" json

    /// POST /query/plane-intersection — returns 2D line segments
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

    let private postBinary (serverUrl : string) (path : string) (json : string) : Async<byte[]> =
        async {
            use client = new HttpClient()
            use content = new StringContent(json, Encoding.UTF8, "application/json")
            let! resp = client.PostAsync(serverUrl.TrimEnd('/') + path, content) |> Async.AwaitTask
            resp.EnsureSuccessStatusCode() |> ignore
            return! resp.Content.ReadAsByteArrayAsync() |> Async.AwaitTask
        }

    /// POST /query/grid-eval — returns GridEvalData
    let gridEval (serverUrl : string) (dataset : string) (anchor : V3d) (axis : V3d) (radius : float) (resolution : int) (extFwd : float) (extBack : float) =
        async {
            let json = sprintf """{"dataset":"%s","anchor":%s,"axis":%s,"radius":%.17g,"resolution":%d,"extentForward":%.17g,"extentBackward":%.17g}"""
                        dataset (v3 anchor) (v3 axis) radius resolution extFwd extBack
            let! buf = postBinary serverUrl "/query/grid-eval" json
            let mutable off = 0
            let readInt () =
                let v = System.BitConverter.ToInt32(buf, off)
                off <- off + 4; v
            let readFloat () =
                let v = System.BitConverter.ToDouble(buf, off)
                off <- off + 8; v
            let res = readInt ()
            let cellCount = readInt ()
            let dsCount = readInt ()
            let cells = Array.init (res * res) (fun _ -> { Average = nan; Q1 = nan; Q3 = nan; Min = nan; Max = nan; Variance = nan })
            let mutable gridOrigin = V2d.Zero
            let mutable cellSize = 1.0
            for ci in 0 .. cellCount - 1 do
                let gu = readInt ()
                let gv = readInt ()
                let avg = readFloat ()
                let q1 = readFloat ()
                let q3 = readFloat ()
                let mn = readFloat ()
                let mx = readFloat ()
                let var = readFloat ()
                if ci = 0 then
                    cellSize <- radius * 2.0 / float res
                    gridOrigin <- V2d(-radius + float gu * cellSize, -radius + float gv * cellSize) - V2d(float gu * cellSize, float gv * cellSize)
                cells.[gv * res + gu] <- { Average = avg; Q1 = q1; Q3 = q3; Min = mn; Max = mx; Variance = var }
            let datasetStats = Array.zeroCreate<DatasetCoreSampleStats> dsCount
            for di in 0 .. dsCount - 1 do
                let nameLen = readInt ()
                let name = Encoding.UTF8.GetString(buf, off, nameLen)
                off <- off + nameLen
                let zMin = readFloat ()
                let zQ1 = readFloat ()
                let zMed = readFloat ()
                let zQ3 = readFloat ()
                let zMax = readFloat ()
                let zVar = readFloat ()
                datasetStats.[di] <- { MeshName = name; ZMin = zMin; ZQ1 = zQ1; ZMedian = zMed; ZQ3 = zQ3; ZMax = zMax; ZVariance = zVar }
            let origin = V2d(-radius, -radius)
            let cs = radius * 2.0 / float res
            return { Resolution = res; GridOrigin = origin; CellSize = cs; Cells = cells; DatasetStats = datasetStats }
        }
