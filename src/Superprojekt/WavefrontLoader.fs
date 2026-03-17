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

    let fetchCentroids (serverUrl : string) : Async<(string * V3d)[]> =
        async {
            use client = new System.Net.Http.HttpClient()
            let! json = client.GetStringAsync(serverUrl.TrimEnd('/') + "/centroids") |> Async.AwaitTask
            let doc = System.Text.Json.JsonDocument.Parse(json)
            return
                doc.RootElement.EnumerateObject()
                |> Seq.map (fun prop ->
                    let a = prop.Value.EnumerateArray() |> Seq.map (fun e -> e.GetDouble()) |> Seq.toArray
                    prop.Name, V3d(a.[0], a.[1], a.[2])
                )
                |> Seq.toArray
        }

    let fetch (serverUrl : string) (name : string) (index : int) : Async<MeshData> =
        async {
            use client = new System.Net.Http.HttpClient()
            let base' = serverUrl.TrimEnd('/')
            let meshUrl  = sprintf "%s/mesh/%s/%d"       base' name index
            let atlasUrl = sprintf "%s/mesh/%s/%d/atlas" base' name index
            let! bytes = client.GetByteArrayAsync(meshUrl) |> Async.AwaitTask
            return decode atlasUrl bytes
        }


module Query =

    open System.Net.Http
    open System.Text
    open System.Text.Json

    let private post (serverUrl : string) (path : string) (body : obj) : Async<JsonElement> =
        async {
            use client = new HttpClient()
            let json    = JsonSerializer.Serialize(body)
            use content = new StringContent(json, Encoding.UTF8, "application/json")
            let! resp = client.PostAsync(serverUrl.TrimEnd('/') + path, content) |> Async.AwaitTask
            resp.EnsureSuccessStatusCode() |> ignore
            let! text = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
            return JsonDocument.Parse(text).RootElement
        }

    /// POST /query/ray  — returns (hit, t, hitPoint, triangleId) option
    let rayHit (serverUrl : string) (name : string) (index : int) (origin : V3d) (direction : V3d) =
        async {
            let body = {| Name = name; Index = index
                          Origin    = [| origin.X;    origin.Y;    origin.Z    |]
                          Direction = [| direction.X; direction.Y; direction.Z |] |}
            let! r = post serverUrl "/query/ray" body
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
            let body = {| Name = name; Index = index
                          Point = [| queryPoint.X; queryPoint.Y; queryPoint.Z |] |}
            let! r = post serverUrl "/query/closest" body
            if r.GetProperty("found").GetBoolean() then
                let pt = r.GetProperty("point").EnumerateArray() |> Seq.map (fun e -> e.GetDouble()) |> Seq.toArray
                return Some {| point = V3d(pt.[0], pt.[1], pt.[2])
                               distanceSquared = float32 (r.GetProperty("distanceSquared").GetDouble())
                               triangleId = r.GetProperty("triangleId").GetInt32() |}
            else
                return None
        }

    /// POST /query/sphere  — returns triangle indices whose AABB overlaps the sphere
    let sphereTriangles (serverUrl : string) (name : string) (index : int) (center : V3d) (radius : float) =
        async {
            let body = {| Name = name; Index = index
                          Center = [| center.X; center.Y; center.Z |]; Radius = radius |}
            let! r = post serverUrl "/query/sphere" body
            return
                r.GetProperty("triangleIndices").EnumerateArray()
                |> Seq.map (fun e -> e.GetInt32())
                |> Seq.toArray
        }

    /// POST /query/box  — returns triangle indices whose AABB overlaps the box
    let boxTriangles (serverUrl : string) (name : string) (index : int) (min : V3d) (max : V3d) =
        async {
            let body = {| Name = name; Index = index
                          Min = [| min.X; min.Y; min.Z |]; Max = [| max.X; max.Y; max.Z |] |}
            let! r = post serverUrl "/query/box" body
            return
                r.GetProperty("triangleIndices").EnumerateArray()
                |> Seq.map (fun e -> e.GetInt32())
                |> Seq.toArray
        }
