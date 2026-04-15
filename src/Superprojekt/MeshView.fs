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
    let passThree = RenderPass.after "three" RenderPassOrder.Arbitrary passTwo
    
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

    let renderMesh
        (loaded : LoadedMesh)
        (filter : aval<Box3d>)
        (isGhost : aval<bool>)
        (meshIndex : aval<int>)
        (active : aval<bool>)
        (commonCentroid : aval<V3d>)
        (meshScale : aval<float>)
        (ghostOpacity : aval<float>)
        (colorMode : aval<bool>)
        (explosionOffset : aval<V3d>) =
        let scaledFilter =
            (meshScale, filter) ||> AVal.map2 (fun scale (f : Box3d) ->
                Box3d(f.Min * scale, f.Max * scale)
            )
        let clipMin =
            active |> AVal.bind (fun a ->
                if a then scaledFilter |> AVal.map _.Min
                else isGhost |> AVal.map (fun g ->
                    if g then V3d.III * 10000.0 else V3d.III * -10000.0))
        let clipMax =
            active |> AVal.bind (fun a ->
                if a then scaledFilter |> AVal.map _.Max
                else isGhost |> AVal.map (fun g ->
                    if g then V3d.III * -10000.0 else V3d.III * 10000.0))
        let depthTestMode =
            isGhost |> AVal.map (fun g -> if g then DepthTest.Always else DepthTest.LessOrEqual)
        let trafo =
            (commonCentroid, loaded.centroid, meshScale) |||> AVal.map3 (fun common mesh scale ->
                Trafo3d.Translation(mesh - common) * Trafo3d.Scale(scale)
            )
        sg {
            Sg.Trafo trafo
            Sg.Shader {
                DefaultSurfaces.trafo
                BlitShader.explode
                DefaultSurfaces.diffuseTexture
                Shader.headlight
                BlitShader.clippy
            }
            Sg.Uniform("DiffuseColorTexture", loaded.tex)
            Sg.Uniform("ExplosionOffset", explosionOffset)
            Sg.Uniform("ClipMin", clipMin)
            Sg.Uniform("ClipMax", clipMax)
            Sg.Uniform("IsGhost", isGhost)
            Sg.Uniform("MeshIndex", meshIndex)
            Sg.Uniform("GhostOpacity", ghostOpacity)
            Sg.Uniform("ColorMode", colorMode |> AVal.map (fun b -> if b then 1 else 0))
            Sg.BlendMode BlendMode.Blend
            Sg.DepthTest depthTestMode
            Sg.VertexAttributes(
                HashMap.ofList [
                    string DefaultSemantic.Positions,               BufferView(loaded.pos, typeof<V3f>)
                    string DefaultSemantic.DiffuseColorCoordinates, BufferView(loaded.tc,  typeof<V2f>)
                    string DefaultSemantic.Normals,                 BufferView(loaded.nrm, typeof<V3f>)
                ]
            )
            Sg.Active(loaded.fvc |> AVal.map (fun c -> c > 3))
            Sg.Index(BufferView(loaded.idx, typeof<int>))
            Sg.Render loaded.fvc
        }

    let buildMeshTextures (info : RenderControlInfo) (loadFinished : string -> unit) (view : aval<Trafo3d>) (proj : aval<Trafo3d>) (model : AdaptiveModel) =
        let signature =
            info.Runtime.CreateFramebufferSignature [
                DefaultSemantic.Colors,       TextureFormat.Rgba8
                DefaultSemantic.DepthStencil, TextureFormat.Depth24Stencil8
            ]

        let meshIndices =
            model.MeshNames |> AList.toAVal |> AVal.map (fun names ->
                names |> Seq.mapi (fun i a -> a, i) |> Map.ofSeq
            )
        let texCount = model.MeshNames |> AList.count |> AVal.map (max 1) |> AVal.map ((*)2)
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
        
        let activePin =
            (model.ScanPins.SelectedPin, model.ScanPins.ActivePlacement, model.ScanPins.Pins |> AMap.toAVal)
            |||> AVal.map3 (fun sel act pins ->
                let id = act |> Option.orElse sel
                id |> Option.bind (fun id -> HashMap.tryFind id pins))
        let cylClip =
            activePin |> AVal.map (fun pinOpt ->
                match pinOpt with
                | Some pin when pin.GhostClip = GhostClipOn || pin.Explosion.Enabled ->
                    let axis = pin.Prism.AxisDirection |> Vec.normalize
                    let r = match pin.Prism.Footprint.Vertices with v :: _ -> v.Length | _ -> 1.0
                    let active = if pin.GhostClip = GhostClipOn then 1 else 0
                    active, pin.Prism.AnchorPoint, axis, r, pin.Prism.ExtentForward, pin.Prism.ExtentBackward
                | _ ->
                    0, V3d.Zero, V3d.OOI, 1.0, 1.0, 1.0)
        let cylActive  = cylClip |> AVal.map (fun (a,_,_,_,_,_) -> a)
        let cylAnchor  = cylClip |> AVal.map (fun (_,b,_,_,_,_) -> b)
        let cylAxis    = cylClip |> AVal.map (fun (_,_,c,_,_,_) -> c)
        let cylRadius  = cylClip |> AVal.map (fun (_,_,_,d,_,_) -> d)
        let cylExtFwd  = cylClip |> AVal.map (fun (_,_,_,_,e,_) -> e)
        let cylExtBack = cylClip |> AVal.map (fun (_,_,_,_,_,f) -> f)
        let explosionMap =
            (activePin, model.MeshNames |> AList.toAVal) ||> AVal.map2 (fun pinOpt names ->
                match pinOpt with
                | Some pin -> Stratigraphy.explosionOffsets pin (IndexList.toArray names)
                | None -> Map.empty)

        let tasks =
            let filter =
                (model.ClipActive, model.ClipBox, model.CommonCentroid) |||> AVal.map3 (fun active box cc ->
                    if active then box - cc else Box3d(V3d(-1e10), V3d(1e10))
                )
            let scaleFor (name : string) =
                let dataset = name.Split('/', 2).[0]
                model.DatasetScales |> AVal.map (fun m -> Map.tryFind dataset m |> Option.defaultValue 1.0)
            meshIndices |> AList.bind (fun meshIndices ->
                model.MeshNames |> AList.collect (fun name ->
                    let loaded = loadMeshAsync (fun () -> loadFinished name) name
                    let meshIndex = meshIndices.[name]
                    let textureIndexSolid = 2*meshIndex
                    let textureIndexGhost = textureIndexSolid+1
                    let isActive = model.MeshVisible |> AVal.map (fun m -> Map.tryFind name m |> Option.defaultValue true)
                    let scale = scaleFor name
                    let explosionOffset = explosionMap |> AVal.map (fun m -> Map.tryFind name m |> Option.defaultValue V3d.Zero)
                    let solidSg =
                        sg {
                            Sg.View view
                            Sg.Proj proj
                            Sg.Uniform("ViewportSize", info.ViewportSize)
                            Sg.Uniform("CylClipActive", cylActive)
                            Sg.Uniform("CylAnchor", cylAnchor)
                            Sg.Uniform("CylAxis", cylAxis)
                            Sg.Uniform("CylRadius", cylRadius)
                            Sg.Uniform("CylExtFwd", cylExtFwd)
                            Sg.Uniform("CylExtBack", cylExtBack)
                            renderMesh loaded filter (AVal.constant false) (AVal.constant meshIndex) isActive model.CommonCentroid scale model.GhostOpacity model.ColorMode explosionOffset
                        }
                    let solidTask = info.Runtime.CompileRender(signature, solidSg.GetRenderObjects(TraversalState.empty info.Runtime))

                    let ghostSg =
                        sg {
                            Sg.View view
                            Sg.Proj proj
                            Sg.Uniform("ViewportSize", info.ViewportSize)
                            Sg.Uniform("CylClipActive", cylActive)
                            Sg.Uniform("CylAnchor", cylAnchor)
                            Sg.Uniform("CylAxis", cylAxis)
                            Sg.Uniform("CylRadius", cylRadius)
                            Sg.Uniform("CylExtFwd", cylExtFwd)
                            Sg.Uniform("CylExtBack", cylExtBack)
                            renderMesh loaded filter (AVal.constant true) (AVal.constant meshIndex) isActive model.CommonCentroid scale model.GhostOpacity model.ColorMode explosionOffset
                        }
                    let ghostTask = info.Runtime.CompileRender(signature, ghostSg.GetRenderObjects(TraversalState.empty info.Runtime))
                    AList.ofList [
                        (textureIndexSolid, solidTask)
                        (textureIndexGhost, ghostTask)
                    ]
                )
            )

        let clear =
            info.Runtime.CompileClear(signature, clear { color C4f.Zero; depth 1.0; stencil 0 })

        let output =
            (colorTex, depthTex, fbos) |||> AdaptiveResource.bind3 (fun color depth fbos ->
                AList.toAVal tasks |> AVal.bind (fun tasks ->
                    AVal.custom (fun t ->
                        for (i, mainTask) in tasks do
                            clear.Run(t, RenderToken.Empty, fbos.[i])
                            mainTask.Run(t, RenderToken.Empty, fbos.[i])
                        color, depth
                    )
                )
            )
        let colorTex = AdaptiveResource.map fst output
        let depthTex = AdaptiveResource.map snd output
        model.MeshNames |> AList.count, colorTex, depthTex, meshIndices

    let composeMeshTextures
        (count : aval<int>)
        (colors : aval<IBackendTexture>)
        (depths : aval<IBackendTexture>)
        (explore : aval<ITexture>)
        (differenceRendering    : aval<bool>)
        (minDifferenceDepth     : aval<float>)
        (maxDifferenceDepth     : aval<float>)
        (clipMin                : aval<V3d>)
        (clipMax                : aval<V3d>)
        (ghostSilhouette        : aval<bool>)
        (meshVisibilityMask     : aval<int>) =
        let colorTex = colors |> AdaptiveResource.map (fun t -> t :> ITexture)
        let depthTex = depths |> AdaptiveResource.map (fun t -> t :> ITexture)
        sg {
            Sg.Shader { BlitShader.readArray }
            Sg.Uniform("MeshCount",          count)
            Sg.Uniform("ColorTexture",          colorTex)
            Sg.Uniform("DepthTexture",          depthTex)
            Sg.Uniform("ExploreTexture",        explore)
            Sg.Uniform("DifferenceRendering",   differenceRendering)
            Sg.Uniform("MinDifferenceDepth",    minDifferenceDepth)
            Sg.Uniform("MaxDifferenceDepth",    maxDifferenceDepth)
            Sg.Uniform("ClipMin",               clipMin)
            Sg.Uniform("ClipMax",               clipMax)
            Sg.Uniform("GhostSilhouette",       ghostSilhouette)
            Sg.Uniform("MeshVisibilityMask",    meshVisibilityMask)
            Primitives.FullscreenQuad
        }