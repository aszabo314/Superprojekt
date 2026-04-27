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
        (colorMode : aval<int>) =
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
                DefaultSurfaces.diffuseTexture
                Shader.headlight
                BlitShader.clippy
            }
            Sg.Uniform("DiffuseColorTexture", loaded.tex)
            Sg.Uniform("ClipMin", clipMin)
            Sg.Uniform("ClipMax", clipMax)
            Sg.Uniform("IsGhost", isGhost)
            Sg.Uniform("MeshIndex", meshIndex)
            Sg.Uniform("GhostOpacity", ghostOpacity)
            Sg.Uniform("ColorMode", colorMode)
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
                DefaultSemantic.Normals,      TextureFormat.Rgba16f
                DefaultSemantic.DepthStencil, TextureFormat.Depth24Stencil8
            ]

        let meshIndices =
            model.MeshNames |> AList.toAVal |> AVal.map (fun names ->
                names |> Seq.mapi (fun i a -> a, i) |> Map.ofSeq
            )
        let texCount = model.MeshNames |> AList.count |> AVal.map (max 1) |> AVal.map ((*)2)
        let colorTex =
            info.Runtime.CreateTexture2DArray(info.ViewportSize, TextureFormat.Rgba8, 1, 1, texCount)

        let normalTex =
            info.Runtime.CreateTexture2DArray(info.ViewportSize, TextureFormat.Rgba16f, 1, 1, texCount)

        let depthTex =
            info.Runtime.CreateTexture2DArray(info.ViewportSize, TextureFormat.Depth24Stencil8, 1, 1, texCount)

        let fbos =
            (colorTex, normalTex, depthTex) |||> AdaptiveResource.bind3 (fun color normal depth ->
                texCount |> AVal.map (fun cnt ->
                    Array.init cnt (fun i ->
                        info.Runtime.CreateFramebuffer(
                            signature, [
                                DefaultSemantic.Colors, color.[TextureAspect.Color, 0, i] :> IFramebufferOutput
                                DefaultSemantic.Normals, normal.[TextureAspect.Color, 0, i] :> IFramebufferOutput
                                DefaultSemantic.DepthStencil, depth.[TextureAspect.DepthStencil, 0, i] :> IFramebufferOutput
                            ]
                        )
                    )
                )
            )
        
        let activePlacementId =
            model.ScanPins.Placement |> AVal.map (function
                | AdjustingPin(id, _) -> Some id
                | _ -> None)
        let activePin =
            (model.ScanPins.SelectedPin, activePlacementId, model.ScanPins.Pins |> AMap.toAVal)
            |||> AVal.map3 (fun sel act pins ->
                let id = act |> Option.orElse sel
                id |> Option.bind (fun id -> HashMap.tryFind id pins))
        let previewPrism =
            (model.ScanPins.Placement, model.ClipBounds) ||> AVal.map2 PinGeometry.placementPreviewPrism
        let ghostClipVal = activePin |> AVal.map (Option.map (fun p -> p.GhostClip))
        let prismVal = activePin |> AVal.map (Option.map (fun p -> p.Prism))
        let cutPlaneVal = activePin |> AVal.map (Option.map (fun p -> p.CutPlane))
        let ghostCutVal = activePin |> AVal.map (Option.map (fun p -> p.GhostClipCutPlane) >> Option.defaultValue false)
        let cameraFwd = view |> AVal.map (fun v -> v.Backward.TransformDir(V3d(0.0, 0.0, -1.0)) |> Vec.normalize)
        let cylClip =
            AVal.custom (fun tok ->
                let prev = previewPrism.GetValue tok
                let gc = ghostClipVal.GetValue tok
                let prismOpt = prismVal.GetValue tok
                let cutOpt = cutPlaneVal.GetValue tok
                let ghostCut = ghostCutVal.GetValue tok
                let camFwd = cameraFwd.GetValue tok
                // Preview takes priority — force solo ghost-clip on the cylinder before commit.
                let effectivePrism, effectiveGhost, effectiveCut =
                    match prev with
                    | Some p -> Some p, Some GhostClipOn, None
                    | None -> prismOpt, gc, cutOpt
                match effectiveGhost, effectivePrism with
                | Some GhostClipOn, Some prism ->
                    let axis = Vec.normalize prism.AxisDirection
                    let r = match prism.Footprint.Vertices with v :: _ -> v.Length | _ -> 1.0
                    let cutActive, cutN, cutD =
                        if ghostCut && prev.IsNone then
                            match effectiveCut with
                            | Some cp ->
                                let right, fwd = PinGeometry.axisFrame axis
                                let planePoint, planeNormal =
                                    match cp with
                                    | CutPlaneMode.AlongAxis angleDeg ->
                                        let a = angleDeg * Constant.RadiansPerDegree
                                        let planeDir = right * cos a + fwd * sin a
                                        prism.AnchorPoint, Vec.cross planeDir axis |> Vec.normalize
                                    | CutPlaneMode.AcrossAxis dist ->
                                        prism.AnchorPoint + axis * dist, axis
                                let n =
                                    if Vec.dot planeNormal camFwd > 0.0 then -planeNormal else planeNormal
                                1.0, n, Vec.dot planePoint n
                            | None -> 0.0, V3d.Zero, 0.0
                        else 0.0, V3d.Zero, 0.0
                    M44d(
                        1.0, r, prism.ExtentForward, prism.ExtentBackward,
                        prism.AnchorPoint.X, prism.AnchorPoint.Y, prism.AnchorPoint.Z, cutActive,
                        axis.X, axis.Y, axis.Z, cutD,
                        cutN.X, cutN.Y, cutN.Z, 0.0)
                | _ -> M44d.Zero)
        let tasks =
            let filter =
                (model.ClipActive, model.ClipBox, model.CommonCentroid) |||> AVal.map3 (fun active box cc ->
                    if active then box - cc else Box3d(V3d(-1e10), V3d(1e10))
                )
            let scaleFor (name : string) =
                let dataset = name.Split('/', 2).[0]
                model.DatasetScales |> AVal.map (fun m -> Map.tryFind dataset m |> Option.defaultValue 1.0)
            let makeTask (loaded : LoadedMesh) (meshIndex : int) (isActive : aval<bool>) (scale : aval<float>) (isGhost : bool) =
                let body =
                    sg {
                        Sg.View view
                        Sg.Proj proj
                        Sg.Uniform("ViewportSize", info.ViewportSize)
                        Sg.Uniform("CylClip", cylClip)
                        let modeInt = model.RenderingMode |> AVal.map (function Textured -> 0 | Shaded -> 1 | WhiteSurface -> 2)
                        renderMesh loaded filter (AVal.constant isGhost) (AVal.constant meshIndex) isActive model.CommonCentroid scale model.GhostOpacity modeInt
                    }
                info.Runtime.CompileRender(signature, body.GetRenderObjects(TraversalState.empty info.Runtime))
            meshIndices |> AList.bind (fun meshIndices ->
                model.MeshNames |> AList.collect (fun name ->
                    let loaded = loadMeshAsync (fun () -> loadFinished name) name
                    let meshIndex = meshIndices.[name]
                    let isActive = model.MeshVisible |> AVal.map (fun m -> Map.tryFind name m |> Option.defaultValue true)
                    let scale = scaleFor name
                    AList.ofList [
                        (2 * meshIndex,     makeTask loaded meshIndex isActive scale false)
                        (2 * meshIndex + 1, makeTask loaded meshIndex isActive scale true)
                    ]
                )
            )

        let clear =
            info.Runtime.CompileClear(signature, clear { color C4f.Zero; depth 1.0; stencil 0 })

        let output =
            (colorTex, normalTex, depthTex) |||> AdaptiveResource.bind3 (fun color normal depth ->
                fbos |> AdaptiveResource.bind (fun fbos ->
                    AList.toAVal tasks |> AVal.bind (fun tasks ->
                        AVal.custom (fun t ->
                            for (i, mainTask) in tasks do
                                clear.Run(t, RenderToken.Empty, fbos.[i])
                                mainTask.Run(t, RenderToken.Empty, fbos.[i])
                            color, normal, depth
                        )
                    )
                )
            )
        let colorTex  = AdaptiveResource.map (fun (c, _, _) -> c) output
        let normalTex = AdaptiveResource.map (fun (_, n, _) -> n) output
        let depthTex  = AdaptiveResource.map (fun (_, _, d) -> d) output
        model.MeshNames |> AList.count, colorTex, normalTex, depthTex, meshIndices

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