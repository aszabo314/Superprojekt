namespace Superprojekt

open Aardvark.Base
open Aardworx.WebAssembly
open Aardvark.Rendering
open FSharp.Data.Adaptive
open Aardvark.Dom

type LoadedMesh =
    {
        centroid : aval<V3d>
        pos  : aval<IBuffer>
        tc   : aval<IBuffer>
        idx  : aval<IBuffer>
        tex  : aval<ITexture>
        fvc  : aval<int>
        mesh : MeshData option ref
    }

module MeshView =

    let apiBase =
        lazy (
            let href = Window.Location.Href
            let uri = System.Uri(href)
            let mutable path = uri.AbsolutePath
            if path.Contains('.') then path <- path.Substring(0, path.LastIndexOf('/') + 1)
            path <- path.TrimEnd('/')
            uri.GetLeftPart(System.UriPartial.Authority) + path + "/api"
        )

    let private meshes = System.Collections.Generic.Dictionary<string, LoadedMesh>()

    let loadMeshAsync (name : string) : LoadedMesh =
        match meshes.TryGetValue(name) with
        | true, m -> m
        | _ ->
            let ccc = cval V3d.Zero
            let m =
                {
                    centroid = ccc
                    pos  = cval (ArrayBuffer [| V3f.Zero; V3f.Zero; V3f.Zero |] :> IBuffer)
                    tc   = cval (ArrayBuffer [| V2f.Zero; V2f.Zero; V2f.Zero |] :> IBuffer)
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
                        (m.idx :?> cval<IBuffer>).Value <- ArrayBuffer mesh.indices
                        (m.fvc :?> cval<int>).Value     <- mesh.indices.Length
                    )
                    let! img = JSImage.load mesh.atlasUrl
                    transact (fun () -> (m.tex :?> cval<ITexture>).Value <- JSTexture(img, true))
                with e ->
                    Log.error "failed to load mesh %s: %A" name e
            } |> ignore
            m

    let renderMesh (loaded : LoadedMesh) (active : aval<bool>) (commonCentroid : aval<V3d>) =
        sg {
            Sg.Translate((commonCentroid, loaded.centroid) ||> AVal.map2 (fun common mesh -> mesh - common))
            Sg.Shader {
                DefaultSurfaces.trafo
                DefaultSurfaces.diffuseTexture
            }
            Sg.Uniform("DiffuseColorTexture", loaded.tex)
            Sg.VertexAttributes(
                HashMap.ofList [
                    string DefaultSemantic.Positions,               BufferView(loaded.pos, typeof<V3f>)
                    string DefaultSemantic.DiffuseColorCoordinates, BufferView(loaded.tc,  typeof<V2f>)
                ]
            )
            Sg.Active(AVal.map2 (&&) active (loaded.fvc |> AVal.map (fun c -> c > 3)))
            Sg.Index(BufferView(loaded.idx, typeof<int>))
            Sg.Render loaded.fvc
        }

    let render (name : string) (active : aval<bool>) (commonCentroid : aval<V3d>) =
        let loaded = loadMeshAsync name
        renderMesh loaded active commonCentroid

    let buildMeshTextures (info : Aardvark.Dom.RenderControlInfo) (view : aval<Trafo3d>) (proj : aval<Trafo3d>) (model : AdaptiveModel) =
        let signature =
            info.Runtime.CreateFramebufferSignature [
                DefaultSemantic.Colors,       TextureFormat.Rgba8
                DefaultSemantic.DepthStencil, TextureFormat.Depth24Stencil8
            ]
        model.MeshNames |> AList.toASet |> ASet.mapToAMap (fun name ->
            let mesh =
                sg {
                    Sg.View view
                    Sg.Proj proj
                    Sg.Uniform("ViewportSize", info.ViewportSize)
                    render name (AVal.constant true) model.CommonCentroid
                }
            let objs  = mesh.GetRenderObjects(TraversalState.empty info.Runtime)
            let task  = info.Runtime.CompileRender(signature, objs)
            let color, depth =
                task |> RenderTask.renderToColorAndDepthWithClear info.ViewportSize (clear { color C4f.Zero; depth 1.0 })
            color, depth
        )

    let blitQuad (meshVisible : aval<Map<string, bool>>) (fullscreenActive : aval<bool>) (revolverActive : aval<bool>) name (color : IAdaptiveResource<IBackendTexture>) (depth : IAdaptiveResource<IBackendTexture>) =
        let active    = meshVisible |> AVal.map (fun m -> Map.tryFind name m |> Option.defaultValue true)
        let colorTex  = color |> AdaptiveResource.map (fun t -> t :> ITexture)
        let depthTex  = depth |> AdaptiveResource.map (fun t -> t :> ITexture)
        let superActive = AVal.logicalAnd [active; AVal.map not fullscreenActive]
        sg {
            Sg.Active superActive
            Sg.Shader { BlitShader.read }
            Sg.Uniform("RevolverVisible", revolverActive)
            Sg.Uniform("ColorTexture",    colorTex)
            Sg.Uniform("DepthTexture",    depthTex)
            Primitives.FullscreenQuad
        }
