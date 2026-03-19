module MeshLoader

open System
open System.IO
open Aardvark.Base
open Aardvark.Data.Wavefront

type ParsedMesh =
    {
        centroid  : V3d
        positions : V3f[]   // centroid-relative
        uvs       : V2f[]
        indices   : int[]   // flat triangle list  (triangleCount × 3)
        bbox      : Box3d   // AABB of positions (centroid-relative)
    }

let private findDataRoot () =
    let mutable dir = AppContext.BaseDirectory
    let mutable result = None
    while result.IsNone && not (isNull dir) do
        let candidate = Path.Combine(dir, "data")
        if Directory.Exists candidate then result <- Some candidate
        else dir <- Path.GetDirectoryName dir
    result |> Option.defaultWith (fun () -> failwith "data folder not found")

let dataRoot = lazy findDataRoot ()

let private objFiles (folder : string) =
    Directory.GetFiles(folder, "*.obj") |> Array.sort

let meshCount (name : string) =
    let folder = Path.Combine(dataRoot.Value, name)
    if Directory.Exists folder then objFiles folder |> Array.length else 0

let private parseCentroid (folder : string) =
    match Directory.GetFiles(folder, "*_centroid.txt") |> Array.tryHead with
    | None   -> failwithf "no centroid.txt in %s" folder
    | Some f ->
        let parts = File.ReadAllText(f).Trim().Split(' ')
        V3d(float parts.[0], float parts.[1], float parts.[2])

let getCentroid (name : string) : V3d option =
    let folder = Path.Combine(dataRoot.Value, name)
    if not (Directory.Exists folder) then None
    else
        match Directory.GetFiles(folder, "*_centroid.txt") |> Array.tryHead with
        | None   -> None
        | Some f ->
            let parts = File.ReadAllText(f).Trim().Split(' ')
            Some (V3d(float parts.[0], float parts.[1], float parts.[2]))

let parseMesh (name : string) (index : int) : ParsedMesh =
    let folder = Path.Combine(dataRoot.Value, name)
    if not (Directory.Exists folder) then failwithf "not found: %s" name

    let files = objFiles folder
    if index < 0 || index >= files.Length then
        failwithf "mesh index %d out of range (folder has %d)" index files.Length

    let centroid = parseCentroid folder
    let mesh     = ObjParser.Load files.[index]

    let positions =
        match mesh.Vertices with
        | :? Collections.Generic.IList<V3f> as v -> v |> Seq.map V3d |> Seq.toArray
        | :? Collections.Generic.IList<V3d> as v -> v |> Seq.toArray
        | :? Collections.Generic.IList<V4f> as v -> v |> Seq.map (fun p -> V3d(float p.X, float p.Y, float p.Z)) |> Seq.toArray
        | :? Collections.Generic.IList<V4d> as v -> v |> Seq.map (fun p -> V3d(p.X, p.Y, p.Z)) |> Seq.toArray
        | _ -> [||]

    let texCoords =
        if isNull mesh.TextureCoordinates then [||]
        else mesh.TextureCoordinates |> Seq.map Vec.xy |> Seq.toArray

    let vertexMap = Collections.Generic.Dictionary<struct(int * int), int>()
    let outPos = ResizeArray<V3f>()
    let outUv  = ResizeArray<V2f>()
    let outIdx = ResizeArray<int>()

    for set in mesh.FaceSets do
        let iPos = set.VertexIndices
        let iTc  = if isNull set.TexCoordIndices then iPos else set.TexCoordIndices
        for ti in 0 .. set.ElementCount - 1 do
            let fi  = set.FirstIndices.[ti]
            let cnt = set.FirstIndices.[ti + 1] - fi
            if cnt = 3 then
                let p0 = positions.[iPos.[fi]]
                let p1 = positions.[iPos.[fi + 1]]
                let p2 = positions.[iPos.[fi + 2]]
                if not (Vec.AnyNaN p0 || Vec.AnyNaN p1 || Vec.AnyNaN p2) then
                    for k in 0 .. 2 do
                        let pi  = iPos.[fi + k]
                        let uvi = iTc.[fi + k]
                        let key = struct(pi, uvi)
                        let idx =
                            match vertexMap.TryGetValue key with
                            | true, i -> i
                            | _ ->
                                let i = outPos.Count
                                outPos.Add(V3f positions.[pi])
                                outUv.Add(if uvi < texCoords.Length then texCoords.[uvi] else V2f.Zero)
                                vertexMap.[key] <- i
                                i
                        outIdx.Add idx

    let posArr = outPos.ToArray()
    let bbox =
        if posArr.Length = 0 then Box3d.Invalid
        else
            let mutable bmin = V3d( infinity,  infinity,  infinity)
            let mutable bmax = V3d(-infinity, -infinity, -infinity)
            for p in posArr do
                let v = V3d p
                bmin <- V3d(min bmin.X v.X, min bmin.Y v.Y, min bmin.Z v.Z)
                bmax <- V3d(max bmax.X v.X, max bmax.Y v.Y, max bmax.Z v.Z)
            Box3d(bmin, bmax)

    { centroid  = centroid
      positions = posArr
      uvs       = outUv.ToArray()
      indices   = outIdx.ToArray()
      bbox      = bbox }

let atlasPath (name : string) (index : int) =
    let folder = Path.Combine(dataRoot.Value, name)
    let files  = objFiles folder
    if index < 0 || index >= files.Length then None
    else
        let base' = Path.GetFileNameWithoutExtension files.[index]
        let jpg   = Path.Combine(folder, base' + "_atlas.jpg")
        if File.Exists jpg then Some jpg else None
