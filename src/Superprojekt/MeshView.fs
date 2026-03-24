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

    let renderMeshGhost (loaded : LoadedMesh) (commonCentroid : aval<V3d>) =
        sg {
            Sg.Translate((commonCentroid, loaded.centroid) ||> AVal.map2 (fun common mesh -> mesh - common))
            Sg.Shader {
                DefaultSurfaces.trafo
                DefaultSurfaces.diffuseTexture
                Shader.ghost
            }
            Sg.Uniform("DiffuseColorTexture", loaded.tex)
            Sg.VertexAttributes(
                HashMap.ofList [
                    string DefaultSemantic.Positions,               BufferView(loaded.pos, typeof<V3f>)
                    string DefaultSemantic.DiffuseColorCoordinates, BufferView(loaded.tc,  typeof<V2f>)
                ]
            )
            Sg.BlendMode BlendMode.Blend
            Sg.Active(loaded.fvc |> AVal.map (fun c -> c > 3))
            Sg.Index(BufferView(loaded.idx, typeof<int>))
            Sg.Render loaded.fvc
        }

    let renderMesh (loaded : LoadedMesh) (order : aval<int>)  (active : aval<bool>) (commonCentroid : aval<V3d>) (clipBox : aval<Box3d>) =
        let clipMin = AVal.map2 (fun (b : Box3d) cc -> b.Min - cc) clipBox commonCentroid
        let clipMax = AVal.map2 (fun (b : Box3d) cc -> b.Max - cc) clipBox commonCentroid
        sg {
            Sg.Translate((commonCentroid, loaded.centroid) ||> AVal.map2 (fun common mesh -> mesh - common))
            Sg.Shader {
                DefaultSurfaces.trafo
                DefaultSurfaces.diffuseTexture
                Shader.clip
            }
            Sg.Uniform("DiffuseColorTexture", loaded.tex)
            Sg.Uniform("ClipMin", clipMin)
            Sg.Uniform("ClipMax", clipMax)
            Sg.Uniform("Order", order)
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

    let render (name : string) (order : aval<int>) (active : aval<bool>) (commonCentroid : aval<V3d>) (clipBox : aval<Box3d>) =
        let loaded = loadMeshAsync name
        renderMesh loaded order active commonCentroid clipBox

    let buildMeshTextures (info : Aardvark.Dom.RenderControlInfo) (view : aval<Trafo3d>) (proj : aval<Trafo3d>) (model : AdaptiveModel) =
        let signature =
            info.Runtime.CreateFramebufferSignature [
                DefaultSemantic.Colors,       TextureFormat.Rgba8
                DefaultSemantic.DepthStencil, TextureFormat.Depth24Stencil8
            ]
        
        let meshIndices =
            model.MeshNames |> AList.toAVal |> AVal.map (fun names ->
                names |> Seq.mapi (fun i a -> a, i) |> Map.ofSeq    
            )
        let texCount = model.MeshNames |> AList.count |> AVal.map (max 1)
        let colorTex =
            info.Runtime.CreateTexture2DArray(info.ViewportSize, TextureFormat.Rgba8, 1, 1, texCount)
        
        let depthTex =
            info.Runtime.CreateTexture2DArray(info.ViewportSize, TextureFormat.Depth24Stencil8, 1, 1, texCount)
        
        
        
        
        let fbos =
            (colorTex, depthTex) ||> AdaptiveResource.bind2 (fun color depth ->
                texCount |> AVal.map (fun cnt ->
                    Array.init cnt (fun i ->
                        info.Runtime.CreateFramebuffer(
                            signature, [
                                DefaultSemantic.Colors, color.[TextureAspect.Color, 0, i] :> IFramebufferOutput
                                DefaultSemantic.DepthStencil, depth.[TextureAspect.DepthStencil, 0, i] :> IFramebufferOutput
                            ]
                        )    
                    )
                )
  
            )
        
        let tasks =
            meshIndices |> AList.bind (fun meshIndices ->
                model.MeshNames |> AList.map (fun name ->
                    let order  = model.MeshOrder   |> AMap.tryFind name |> AVal.map (Option.defaultValue 0)
                    let isVis  = model.MeshVisible |> AVal.map (fun m -> Map.tryFind name m |> Option.defaultValue true)
                    let loaded = loadMeshAsync name
                    let ghostSg =
                        sg {
                            Sg.View view
                            Sg.Proj proj
                            Sg.Uniform("ViewportSize", info.ViewportSize)
                            Sg.Active model.GhostSilhouette
                            renderMeshGhost loaded model.CommonCentroid
                        }
                    let mainSg =
                        sg {
                            Sg.View view
                            Sg.Proj proj
                            Sg.Uniform("ViewportSize", info.ViewportSize)
                            renderMesh loaded order isVis model.CommonCentroid model.ClipBox
                        }
                    let ghostTask = info.Runtime.CompileRender(signature, ghostSg.GetRenderObjects(TraversalState.empty info.Runtime))
                    let mainTask  = info.Runtime.CompileRender(signature, mainSg.GetRenderObjects(TraversalState.empty info.Runtime))
                    meshIndices.[name], ghostTask, mainTask
                )
            )

        let clear =
            info.Runtime.CompileClear(signature, clear { color C4f.Zero; depth 1.0; stencil 0 })

        let output =
            (colorTex, depthTex, fbos) |||> AdaptiveResource.bind3 (fun color depth fbos ->
                AList.toAVal tasks |> AVal.bind (fun tasks ->
                    AVal.custom (fun t ->
                        for (i, ghostTask, mainTask) in tasks do
                            clear.Run(t, RenderToken.Empty, fbos.[i])
                            ghostTask.Run(t, RenderToken.Empty, fbos.[i])
                            mainTask.Run(t, RenderToken.Empty, fbos.[i])
                        color, depth
                    )
                )
            )
        let colorTex = AdaptiveResource.map fst output
        let depthTex = AdaptiveResource.map snd output
        model.MeshNames |> AList.count, colorTex, depthTex, meshIndices

    let blitQuad
        (meshVisible : aval<Map<string, bool>>)
        (fullscreenActive : aval<bool>)
        (revolverActive : aval<bool>)
        name
        (color : IAdaptiveResource<IBackendTexture>)
        (depth : IAdaptiveResource<IBackendTexture>) =
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

    let composeMeshTextures
        (count : aval<int>)
        (colors : aval<IBackendTexture>)
        (depths : aval<IBackendTexture>)
        (differenceRendering : aval<bool>)
        (minDifferenceDepth  : aval<float>)
        (maxDifferenceDepth  : aval<float>) =
        let colorTex = colors |> AdaptiveResource.map (fun t -> t :> ITexture)
        let depthTex = depths |> AdaptiveResource.map (fun t -> t :> ITexture)
        sg {
            Sg.Shader { BlitShader.readArray }
            Sg.Uniform("TextureCount",          count)
            Sg.Uniform("ColorTexture",          colorTex)
            Sg.Uniform("DepthTexture",          depthTex)
            Sg.Uniform("DifferenceRendering",   differenceRendering)
            Sg.Uniform("MinDifferenceDepth",    minDifferenceDepth)
            Sg.Uniform("MaxDifferenceDepth",    maxDifferenceDepth)
            Primitives.FullscreenQuad
        }