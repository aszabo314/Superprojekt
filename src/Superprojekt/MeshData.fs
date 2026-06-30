namespace Superprojekt

open Aardvark.Base
open Microsoft.FSharp.NativeInterop

#nowarn "9"

module Http =
    let client = new System.Net.Http.HttpClient()

type MeshData =
    {
        centroid  : V3d
        positions : V3f[]
        uvs       : V2f[]
        normals   : V3f[]
        indices   : int[]
        atlasUrl  : string
    }

module MeshData =

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

        let normals = Array.zeroCreate<V3f> vertexCount
        System.Span<V3f>(NativePtr.toVoidPtr ptr, vertexCount).CopyTo(normals)
        ptr <- NativePtr.add ptr (vertexCount * sizeof<V3f>)

        let indices = Array.zeroCreate<int> indexCount
        System.Span<int>(NativePtr.toVoidPtr ptr, indexCount).CopyTo(indices)

        { centroid = centroid; positions = positions; uvs = uvs; normals = normals; indices = indices; atlasUrl = atlasUrl }

    let fetchDatasets (serverUrl : string) : Async<string[]> =
        async {
            let! json = Http.client.GetStringAsync(serverUrl.TrimEnd('/') + "/datasets") |> Async.AwaitTask
            let doc = System.Text.Json.JsonDocument.Parse(json)
            return doc.RootElement.EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> Seq.toArray
        }

    let fetchDefaultDataset (serverUrl : string) : Async<string> =
        async {
            let! json = Http.client.GetStringAsync(serverUrl.TrimEnd('/') + "/datasets/default") |> Async.AwaitTask
            let doc = System.Text.Json.JsonDocument.Parse(json)
            return doc.RootElement.GetString()
        }

    // Server returns { meshName: [x,y,z] }; key each as "dataset/meshName".
    let private fetchVecMap (segment : string) (serverUrl : string) (dataset : string) : Async<(string * V3d)[]> =
        async {
            let url = sprintf "%s/datasets/%s/%s" (serverUrl.TrimEnd('/')) dataset segment
            let! json = Http.client.GetStringAsync(url) |> Async.AwaitTask
            let doc = System.Text.Json.JsonDocument.Parse(json)
            return
                doc.RootElement.EnumerateObject()
                |> Seq.map (fun prop ->
                    let a = prop.Value.EnumerateArray() |> Seq.map (fun e -> e.GetDouble()) |> Seq.toArray
                    dataset + "/" + prop.Name, V3d(a.[0], a.[1], a.[2])
                )
                |> Seq.toArray
        }

    let fetchCentroids   = fetchVecMap "centroids"
    let fetchPanoCenters = fetchVecMap "pano-centers"

    let fetchBboxes (serverUrl : string) (dataset : string) : Async<(string * Box3d)[]> =
        async {
            let url = sprintf "%s/datasets/%s/bboxes" (serverUrl.TrimEnd('/')) dataset
            let! json = Http.client.GetStringAsync(url) |> Async.AwaitTask
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
            let base' = serverUrl.TrimEnd('/')
            let meshUrl  = sprintf "%s/datasets/%s/mesh/%s/%d"       base' dataset meshName index
            let atlasUrl = sprintf "%s/datasets/%s/mesh/%s/%d/atlas" base' dataset meshName index
            let! bytes = Http.client.GetByteArrayAsync(meshUrl) |> Async.AwaitTask
            return decode atlasUrl bytes
        }

module ApiConfig =
    open Aardworx.WebAssembly
    let apiBase =
        lazy (
            let href = Window.Location.Href
            let uri = System.Uri(href)
            let mutable path = uri.AbsolutePath
            // /s/{token} is a virtual route (server serves index.html) — must not shift the API base.
            if path.StartsWith "/s/" || path = "/s" then path <- ""
            if path.Contains('.') then path <- path.Substring(0, path.LastIndexOf('/') + 1)
            path <- path.TrimEnd('/')
            uri.GetLeftPart(System.UriPartial.Authority) + path + "/api"
        )

