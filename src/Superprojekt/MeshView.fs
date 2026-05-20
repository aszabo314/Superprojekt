namespace Superprojekt

open Aardvark.Base
open Aardworx.WebAssembly
open Aardvark.Rendering
open FSharp.Data.Adaptive
open Aardvark.Dom
open FShade

type LoadedMesh =
    {
        centroid : aval<V3d>
        pos  : aval<IBuffer>
        tc   : aval<IBuffer>
        nrm  : aval<IBuffer>
        idx  : aval<IBuffer>
        tex  : aval<ITexture>
        fvc  : aval<int>
        mesh : MeshData option ref
    }

module RenderPass =
    let passMinusOne = RenderPass.main
    let passZero = RenderPass.after "zero" RenderPassOrder.Arbitrary passMinusOne
    let passOne = RenderPass.after "one" RenderPassOrder.Arbitrary passZero
    let passTwo = RenderPass.after "two" RenderPassOrder.Arbitrary passOne

[<ReflectedDefinition>]
module MeshShader =
    open FShade

    type UniformScope with
        member x.MeshActive      : bool    = x?MeshActive
        member x.GhostSilhouette : bool    = x?GhostSilhouette
        member x.GhostOpacity    : float32 = x?GhostOpacity

    // Headlight + opacity: disabled meshes go to alpha=0 (or GhostOpacity in ghost
    // mode), enabled meshes keep alpha=1. All shader math in float32.
    let shade (v : Effects.Vertex) =
        fragment {
            let mutable alpha = 0.0f
            if uniform.MeshActive then
                alpha <- 1.0f
            elif uniform.GhostSilhouette then
                alpha <- uniform.GhostOpacity
            if alpha < 1e-4f then discard()
            let n = v.n |> Vec.normalize
            let toCam = (uniform.CameraLocation - v.wp.XYZ) |> Vec.normalize
            let ndl = max 0.15f (abs (Vec.dot n toCam))
            return V4f(v.c.XYZ * ndl, alpha)
        }

    // Depth-only shader for the pre-pass; the FBO has no color attachment so
    // the fragment output is discarded.
    let depthOnly (_v : Effects.Vertex) =
        fragment { return V4f.IIII }

module MeshView =

    let apiBase = ApiConfig.apiBase

    let private meshes = System.Collections.Generic.Dictionary<string, LoadedMesh>()

    let loadMeshAsync (finished : unit -> unit) (name : string) : LoadedMesh =
        match meshes.TryGetValue(name) with
        | true, m -> m
        | _ ->
            let ccc = cval V3d.Zero
            let m =
                {
                    centroid = ccc
                    pos  = cval (ArrayBuffer [| V3f.Zero; V3f.Zero; V3f.Zero |] :> IBuffer)
                    tc   = cval (ArrayBuffer [| V2f.Zero; V2f.Zero; V2f.Zero |] :> IBuffer)
                    nrm  = cval (ArrayBuffer [| V3f.OOI; V3f.OOI; V3f.OOI |] :> IBuffer)
                    idx  = cval (ArrayBuffer [| 0; 1; 2 |] :> IBuffer)
                    tex  = cval<ITexture> (AVal.force DefaultTextures.checkerboard)
                    fvc  = cval 3
                    mesh = ref None
                }
            meshes.[name] <- m
            task {
                try
                    let! mesh = MeshData.fetch apiBase.Value name 0
                    m.mesh.Value <- Some mesh
                    transact (fun () ->
                        ccc.Value <- mesh.centroid
                        (m.pos :?> cval<IBuffer>).Value <- ArrayBuffer mesh.positions
                        (m.tc  :?> cval<IBuffer>).Value <- ArrayBuffer mesh.uvs
                        (m.nrm :?> cval<IBuffer>).Value <- ArrayBuffer mesh.normals
                        (m.idx :?> cval<IBuffer>).Value <- ArrayBuffer mesh.indices
                        (m.fvc :?> cval<int>).Value     <- mesh.indices.Length
                    )
                    let! img = JSImage.load mesh.atlasUrl
                    transact (fun () -> (m.tex :?> cval<ITexture>).Value <- JSTexture(img, true))

                    finished()
                with e ->
                    Log.error "failed to load mesh %s: %A" name e
            } |> ignore
            m

    let private meshTrafo
        (commonCentroid : aval<V3d>) (loaded : LoadedMesh)
        (meshScale : aval<float>) (meshTransform : aval<Trafo3d>) =
        let base_ =
            (commonCentroid, loaded.centroid, meshScale) |||> AVal.map3 (fun common mesh scale ->
                Trafo3d.Translation(mesh - common) * Trafo3d.Scale(scale))
        (base_, meshTransform) ||> AVal.map2 (fun b t -> t * b)

    let private scaleFor (model : AdaptiveModel) (name : string) =
        let dataset = name.Split('/', 2).[0]
        model.DatasetScales |> AVal.map (fun m -> Map.tryFind dataset m |> Option.defaultValue 1.0)

    // Main OIT-targeted scene: every loaded mesh as a draw call, all visible (so the
    // ghost path can still produce fragments). Alpha gating happens in the shader.
    let buildScene (loadFinished : string -> unit) (model : AdaptiveModel) : aset<ISceneNode> =
        model.MeshNames |> AList.map (fun name ->
            let loaded = loadMeshAsync (fun () -> loadFinished name) name
            let isActive =
                model.MeshVisible |> AVal.map (fun m -> Map.tryFind name m |> Option.defaultValue true)
            let scale = scaleFor model name
            let meshT =
                model.MeshTransforms |> AVal.map (fun m ->
                    Map.tryFind name m |> Option.defaultValue Trafo3d.Identity)
            // Active in OIT pass when enabled, OR when ghost mode would show it.
            let renderEnabled =
                (isActive, model.GhostSilhouette, loaded.fvc) |||> AVal.map3 (fun a g c ->
                    (a || g) && c > 3)
            sg {
                Sg.Active renderEnabled
                Sg.Trafo (meshTrafo model.CommonCentroid loaded scale meshT)
                Sg.Shader {
                    DefaultSurfaces.trafo
                    DefaultSurfaces.diffuseTexture
                    MeshShader.shade
                    OIT.weightedBlend
                }
                Sg.Uniform("DiffuseColorTexture", loaded.tex)
                Sg.Uniform("MeshActive",      isActive)
                Sg.Uniform("GhostSilhouette", model.GhostSilhouette)
                Sg.Uniform("GhostOpacity",    model.GhostOpacity |> AVal.map float32)
                Sg.VertexAttributes(
                    HashMap.ofList [
                        string DefaultSemantic.Positions,               BufferView(loaded.pos, typeof<V3f>)
                        string DefaultSemantic.DiffuseColorCoordinates, BufferView(loaded.tc,  typeof<V2f>)
                        string DefaultSemantic.Normals,                 BufferView(loaded.nrm, typeof<V3f>)
                    ]
                )
                Sg.Index(BufferView(loaded.idx, typeof<int>))
                Sg.Render loaded.fvc
            }
        ) |> AList.toASet

    // Depth pre-pass: only fully-opaque meshes (i.e. enabled), trafo-only shader,
    // depth-write on. Populates the shared depth attachment that the OIT pass
    // then depth-tests against (read-only).
    let buildOpaqueDepthScene (loadFinished : string -> unit) (model : AdaptiveModel) : aset<ISceneNode> =
        model.MeshNames |> AList.map (fun name ->
            let loaded = loadMeshAsync (fun () -> loadFinished name) name
            let isActive =
                model.MeshVisible |> AVal.map (fun m -> Map.tryFind name m |> Option.defaultValue true)
            let scale = scaleFor model name
            let meshT =
                model.MeshTransforms |> AVal.map (fun m ->
                    Map.tryFind name m |> Option.defaultValue Trafo3d.Identity)
            let renderEnabled =
                (isActive, loaded.fvc) ||> AVal.map2 (fun a c -> a && c > 3)
            sg {
                Sg.Active renderEnabled
                Sg.Trafo (meshTrafo model.CommonCentroid loaded scale meshT)
                Sg.Shader { DefaultSurfaces.trafo; MeshShader.depthOnly }
                Sg.VertexAttributes(
                    HashMap.ofList [
                        string DefaultSemantic.Positions, BufferView(loaded.pos, typeof<V3f>)
                    ]
                )
                Sg.Index(BufferView(loaded.idx, typeof<int>))
                Sg.Render loaded.fvc
            }
        ) |> AList.toASet
