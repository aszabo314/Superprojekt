namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open Aardworx.WebAssembly
open FSharp.Data.Adaptive
open Aardvark.Dom
open FShade

module SceneGraph =

    let private boxPos =
        [|  V3f(-0.5f, -0.5f, -0.5f); V3f( 0.5f, -0.5f, -0.5f); V3f( 0.5f,  0.5f, -0.5f); V3f(-0.5f,  0.5f, -0.5f)
            V3f(-0.5f, -0.5f,  0.5f); V3f( 0.5f, -0.5f,  0.5f); V3f( 0.5f,  0.5f,  0.5f); V3f(-0.5f,  0.5f,  0.5f) |]
    let private boxIdx =
        [| 0;1;2; 0;2;3;  5;4;7; 5;7;6;  4;0;3; 4;3;7;  1;5;6; 1;6;2;  0;4;5; 0;5;1;  3;2;6; 3;6;7 |]

    let private axisBox (color : V4d) (trafo : Trafo3d) =
        sg {
            Sg.Trafo (AVal.constant trafo)
            Sg.Shader { DefaultSurfaces.trafo; Shader.flatColor }
            Sg.Uniform("FlatColor", AVal.constant color)
            Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
            Sg.NoEvents
            Sg.VertexAttributes(
                HashMap.ofList [ string DefaultSemantic.Positions, BufferView(AVal.constant (ArrayBuffer boxPos :> IBuffer), typeof<V3f>) ]
            )
            Sg.Index(BufferView(AVal.constant (ArrayBuffer boxIdx :> IBuffer), typeof<int>))
            Sg.Render (AVal.constant boxIdx.Length)
        }

    let private originIndicator (view : aval<Trafo3d>) (proj : aval<Trafo3d>) (active : aval<bool>) =
        let axisLength = 3.0
        let tickSpacing = 0.25
        let tickLen = 0.12
        let labelSize = 0.15

        let toC4b (c : V4d) = C4b(byte(c.X*255.0), byte(c.Y*255.0), byte(c.Z*255.0))
        let darken (c : V4d) = toC4b (V4d(c.X * 0.55, c.Y * 0.55, c.Z * 0.55, 1.0))

        let textTrafoX = Trafo3d.RotationX(Constant.PiHalf)
        let textTrafoY = Trafo3d.RotationX(Constant.PiHalf) * Trafo3d.RotationZ(Constant.PiHalf)
        let textTrafoZ = Trafo3d.RotationX(Constant.PiHalf)

        let xColor = V4d(0.82, 0.15, 0.1, 1.0)
        let yColor = V4d(0.1, 0.72, 0.1, 1.0)
        let zColor = V4d(0.15, 0.35, 0.9, 1.0)

        let tickSegs (color : V4d) (dir : V3d) (perpA : V3d) =
            let n = int (axisLength / tickSpacing)
            let half = perpA * (tickLen * 0.5)
            [| for i in 1 .. n do
                let center = dir * (float i * tickSpacing)
                yield center - half, center + half, color, 1.5 |]

        let allLineSegs =
            AVal.constant (Array.concat [
                [| V3d.Zero, V3d.IOO * axisLength, xColor, 2.0
                   V3d.Zero, V3d.OIO * axisLength, yColor, 2.0
                   V3d.Zero, V3d.OOI * axisLength, zColor, 2.0 |]
                tickSegs xColor V3d.IOO V3d.OOI
                tickSegs yColor V3d.OIO V3d.IOO
                tickSegs zColor V3d.OOI V3d.IOO
            ])

        let labelNodes (color : V4d) (dir : V3d) (perpA : V3d) (textRot : Trafo3d) =
            let n = int (axisLength / tickSpacing)
            let textColor = darken color
            [ for i in 1 .. n do
                if i % 4 = 0 then
                    let dist = float i * tickSpacing
                    let center = dir * dist
                    let labelPos = center + perpA * (tickLen * 0.5 + labelSize * 1.2)
                    let trafo = Trafo3d.Scale(labelSize) * textRot * Trafo3d.Translation(labelPos)
                    yield sg {
                        Sg.Active active; Sg.View view; Sg.Proj proj
                        Sg.Trafo (AVal.constant trafo)
                        Sg.Text(sprintf "%.0f" dist, color = AVal.constant textColor, align = TextAlignment.Center)
                    } ]

        ASet.ofList [
            sg { Sg.Active active; Sg.View view; Sg.Proj proj; axisBox (V4d(0.88, 0.88, 0.88, 1.0)) (Trafo3d.Scale 0.08) }
            sg { Sg.Active active; Sg.View view; Sg.Proj proj; Lines.render allLineSegs }
            yield! [
                let tipOffset = axisLength + labelSize * 1.5
                sg { Sg.Active active; Sg.View view; Sg.Proj proj
                     Sg.Trafo (AVal.constant (Trafo3d.Scale(labelSize * 1.5) * textTrafoX * Trafo3d.Translation(V3d.IOO * tipOffset)))
                     Sg.Text("X", color = AVal.constant (darken xColor), align = TextAlignment.Center) }
                sg { Sg.Active active; Sg.View view; Sg.Proj proj
                     Sg.Trafo (AVal.constant (Trafo3d.Scale(labelSize * 1.5) * textTrafoY * Trafo3d.Translation(V3d.OIO * tipOffset)))
                     Sg.Text("Y", color = AVal.constant (darken yColor), align = TextAlignment.Center) }
                sg { Sg.Active active; Sg.View view; Sg.Proj proj
                     Sg.Trafo (AVal.constant (Trafo3d.Scale(labelSize * 1.5) * textTrafoZ * Trafo3d.Translation(V3d.OOI * tipOffset)))
                     Sg.Text("Z", color = AVal.constant (darken zColor), align = TextAlignment.Center) }
            ]
            yield! labelNodes xColor V3d.IOO V3d.OOI textTrafoX
            yield! labelNodes yColor V3d.OIO V3d.IOO textTrafoY
            yield! labelNodes zColor V3d.OOI V3d.IOO textTrafoZ
        ]

    let build
        (env : Env<Message>)
        (info : Aardvark.Dom.RenderControlInfo)
        (view : aval<Trafo3d>)
        (proj : aval<Trafo3d>)
        (fullscreenActive : aval<bool>)
        (placementHover : aval<V3d option>)
        (model : AdaptiveModel) =
        
        let loadFinished (name : string) =
            env.Emit [ LoadFinished name ]
        
        let cnt, colors, normals, depths, meshIndices = MeshView.buildMeshTextures info loadFinished view proj model
        let colorArrTex  = colors  |> AdaptiveResource.map (fun t -> t :> ITexture)
        let normalArrTex = normals |> AdaptiveResource.map (fun t -> t :> ITexture)
        let depthArrTex  = depths  |> AdaptiveResource.map (fun t -> t :> ITexture)

        let sliceOf name =
            meshIndices |> AVal.map (fun m -> 2 * (Map.tryFind name m |> Option.defaultValue 0))

        let clipMin = AVal.map2 (fun (b : Box3d) cc -> b.Min - cc) model.ClipBox model.CommonCentroid
        let clipMax = AVal.map2 (fun (b : Box3d) cc -> b.Max - cc) model.ClipBox model.CommonCentroid

        let meshVisibilityMask =
            (model.MeshVisible, meshIndices) ||> AVal.map2 (fun vis indices ->
                indices |> Map.fold (fun mask name i ->
                    if Map.tryFind name vis |> Option.defaultValue true then mask ||| (1 <<< i) else mask
                ) 0
            )

        let effectiveGhostSilhouette = model.GhostSilhouette

        let exploreTex : aval<IBackendTexture> =
            let refAxis =
                (model.ReferenceAxis, view) ||> AVal.map2 (fun mode v ->
                    match mode with
                    | AlongWorldZ -> V3d.OOI
                    | AlongCameraView ->
                        v.Backward.TransformDir(V3d(0.0, 0.0, -1.0)) |> Vec.normalize)
            let exploreEnabled = model.Explore |> AVal.map (fun e -> e.Enabled)
            let fcEnabled = model.Explore |> AVal.map (fun e -> if e.FeatureConfidence.Enabled then 1 else 0)
            let dgEnabled = model.Explore |> AVal.map (fun e -> if e.Disagreement.Enabled then 1 else 0)
            let fcThresh  = model.Explore |> AVal.map (fun e -> e.FeatureConfidence.Threshold)
            let dgThresh  = model.Explore |> AVal.map (fun e -> e.Disagreement.Threshold)
            let mixModeInt = model.Explore |> AVal.map (fun e ->
                match e.MixMode with SideBySide -> 0 | Blended -> 1 | Alternating -> 2)
            let highlightAlpha = model.Explore |> AVal.map (fun e -> e.HighlightAlpha)
            let fcColor =
                model.Explore |> AVal.map (fun e ->
                    let c = e.FeatureConfidence.Color
                    V4d(float c.R, float c.G, float c.B, float c.A))
            let dgColor =
                model.Explore |> AVal.map (fun e ->
                    let c = e.Disagreement.Color
                    V4d(float c.R, float c.G, float c.B, float c.A))
            // Time uniform feeds the Alternating mix flicker. A simple
            // wall-clock seconds value re-evaluates on every frame because
            // the AVal.custom binding below is invalidated; this matches
            // the existing per-frame eval pattern used for the heatmap.
            let exploreTime =
                AVal.custom (fun _ ->
                    let now = System.DateTime.UtcNow
                    float (now.TimeOfDay.TotalSeconds))
            let signature =
                info.Runtime.CreateFramebufferSignature [
                    DefaultSemantic.Colors, TextureFormat.Rgba8
                ]
            let tex = info.Runtime.CreateTexture2D(info.ViewportSize, TextureFormat.Rgba8, 1, 1)
            let fbo =
                tex |> AdaptiveResource.bind (fun t ->
                    AVal.constant (
                        info.Runtime.CreateFramebuffer(
                            signature,
                            [ DefaultSemantic.Colors, t.[TextureAspect.Color, 0, 0] :> IFramebufferOutput ]
                        )
                    )
                )
            let taskSg =
                sg {
                    Sg.Shader { BlitShader.exploreHeatmap }
                    Sg.Uniform("MeshCount",          cnt)
                    Sg.Uniform("DepthTexture",       depthArrTex)
                    Sg.Uniform("ViewportSize",       info.ViewportSize)
                    Sg.Uniform("MeshVisibilityMask", meshVisibilityMask)
                    Sg.Uniform("ReferenceAxis",        refAxis)
                    Sg.Uniform("FcEnabled",            fcEnabled)
                    Sg.Uniform("DgEnabled",            dgEnabled)
                    Sg.Uniform("FcThreshold",          fcThresh)
                    Sg.Uniform("DgThreshold",          dgThresh)
                    Sg.Uniform("FcColor",              fcColor)
                    Sg.Uniform("DgColor",              dgColor)
                    Sg.Uniform("MixModeInt",           mixModeInt)
                    Sg.Uniform("ExploreTime",          exploreTime)
                    Sg.Uniform("HighlightAlpha",       highlightAlpha)
                    Sg.View view
                    Sg.Proj proj
                    Primitives.FullscreenQuad
                }
            let renderTask = info.Runtime.CompileRender(signature, taskSg.GetRenderObjects(TraversalState.empty info.Runtime))
            let clearTask = info.Runtime.CompileClear(signature, clear { color C4f.Zero })
            let mutable lastEnabled = false
            tex |> AdaptiveResource.bind (fun t ->
                fbo |> AVal.bind (fun fbo ->
                    AVal.custom (fun tok ->
                        let enabled = exploreEnabled.GetValue(tok)
                        if enabled then
                            clearTask.Run(tok, RenderToken.Empty, fbo)
                            renderTask.Run(tok, RenderToken.Empty, fbo)
                        elif lastEnabled then
                            clearTask.Run(tok, RenderToken.Empty, fbo)
                        lastEnabled <- enabled
                        t :> IBackendTexture
                    )
                )
            )
        let exploreTexAsITex = exploreTex |> AVal.map (fun t -> t :> ITexture)

        let ghostDetailInt =
            model.GhostDetail |> AVal.map (function
                | OutlineOnly -> 0
                | PlusCurvature -> 1
                | PlusTerrainFeatures -> 2)

        // V6 §D.9 — assemble provenance heatmap uniforms.
        let provenanceEnabled = model.ProvenanceHeatmap |> AVal.map (fun b -> if b then 1 else 0)
        let falloffOnly       = model.FalloffZoneOnly   |> AVal.map (fun b -> if b then 1 else 0)
        let provThreshold     = model.ProvenanceThreshold

        // Per-mesh dataset / algorithm error packed into fixed-size arrays
        // indexed by MeshOrder. Slots beyond MeshCount carry zeroes.
        let provenanceArrays =
            (model.MeshOrder |> AMap.toAVal,
             model.MeshSensorTypes, model.MeshDatasetErrors,
             model.MeshAlgorithmResidual)
            |> fun (a, b, c, d) ->
                AVal.custom (fun tok ->
                    let order = a.GetValue tok
                    let sensors = b.GetValue tok
                    let overrides = c.GetValue tok
                    let algo = d.GetValue tok
                    let datasetArr = Arr<N<16>, float>()
                    let algoArr = Arr<N<16>, float>()
                    for (name, idx) in HashMap.toSeq order do
                        if idx >= 0 && idx < 16 then
                            datasetArr.[idx] <- Provenance.datasetError overrides sensors name
                            algoArr.[idx]    <- Map.tryFind name algo |> Option.defaultValue 0.0
                    datasetArr, algoArr)
        let provenanceDataset   = provenanceArrays |> AVal.map fst
        let provenanceAlgorithm = provenanceArrays |> AVal.map snd

        // Anchor array: world-space (centre.xyz, sigma) packed into V4d's,
        // capped at MaxProvenanceAnchors (32).
        let provenanceAnchors =
            (model.ScanPins.Pins |> AMap.toAVal, model.CommonCentroid,
             model.ActiveDataset, model.DatasetScales)
            |> fun (a, b, c, d) ->
                AVal.custom (fun tok ->
                    let pins = a.GetValue tok
                    let cc = b.GetValue tok
                    let ds = c.GetValue tok
                    let scales = d.GetValue tok
                    let scale = ds |> Option.bind (fun n -> Map.tryFind n scales) |> Option.defaultValue 1.0
                    let arr = Arr<N<32>, V4d>()
                    let mutable n = 0
                    for (_, pin) in HashMap.toSeq pins do
                        if pin.Phase = PinPhase.Committed && n < 32 then
                            let world = pin.Centre / scale + cc
                            let sigma = pin.Sigma / scale
                            arr.[n] <- V4d(world.X, world.Y, world.Z, sigma)
                            n <- n + 1
                    arr, n)
        let provenanceAnchorsArr = provenanceAnchors |> AVal.map fst
        let provenanceAnchorCount = provenanceAnchors |> AVal.map snd

        let composite =
            sg {
                Sg.Active (AVal.map not fullscreenActive)
                MeshView.composeMeshTextures cnt colors depths exploreTexAsITex
                    model.DifferenceRendering model.MinDifferenceDepth model.MaxDifferenceDepth
                    clipMin clipMax
                    effectiveGhostSilhouette ghostDetailInt
                    provenanceEnabled provThreshold falloffOnly
                    provenanceDataset provenanceAlgorithm
                    provenanceAnchorCount provenanceAnchorsArr
                    meshVisibilityMask
            }

        let fullscreenNodes =
            model.MeshNames |> AList.map (fun name ->
                let order = model.MeshOrder |> AMap.tryFind name |> AVal.map (Option.defaultValue 0)
                let trafo =
                    order |> AVal.map (fun o ->
                        if o = 0 then
                            Trafo3d.Translation(V3d(0.0, 0.0, 0.1))
                        else
                            let oi = float o - 1.0
                            Trafo3d.Scale(V3d(0.1, 0.1, 1.0))
                                * Trafo3d.Translation(V3d(0.9, 0.9, 0.0))
                                * Trafo3d.Translation(V3d(0.0, -oi * 0.2, 0.0))
                    )
                sg {
                    Sg.Active fullscreenActive
                    Sg.Shader { DefaultSurfaces.trafo; BlitShader.readArraySlice }
                    Sg.Trafo trafo
                    Sg.View Trafo3d.Identity
                    Sg.Proj Trafo3d.Identity
                    Sg.Uniform("ColorTexture", colorArrTex)
                    Sg.Uniform("DepthTexture", depthArrTex)
                    Sg.Uniform("SliceIndex",   sliceOf name)
                    Primitives.FullscreenQuad
                }
            ) |> AList.toASet

        let indicatorNodes = originIndicator view proj (AVal.map not fullscreenActive)

        let pinScene = ScanPinScene.build env view proj fullscreenActive placementHover model

        ASet.unionMany (ASet.ofList [ASet.single composite; fullscreenNodes; indicatorNodes; pinScene])
