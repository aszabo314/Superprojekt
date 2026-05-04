namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open Aardworx.WebAssembly
open FSharp.Data.Adaptive
open Aardvark.Dom

module SceneGraph =

    let private meshPalette : V4d[] = Primitives.meshPaletteV4d

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

    let buildCutPlaneQuad = PinGeometry.buildCutPlaneQuad

    let private disk
            (revolverActive    : aval<bool>)
            (revolverBase      : aval<option<V2d>>)
            (colorArrTex       : aval<ITexture>)
            (viewportSize      : aval<V2i>)
            (sliceIndex        : aval<int>)
            (renderPositionNdc : aval<option<V2d>>)
            (pixelSize         : aval<float>)
            (borderColor       : aval<V4d>) =
        sg {
            Sg.Active revolverActive
            Sg.NoEvents
            let t =
                (renderPositionNdc, viewportSize, pixelSize) |||> AVal.map3 (fun ndc size ps ->
                    match ndc with
                    | Some ndc ->
                        let scale = ps / V2d size
                        Trafo3d.Scale(scale.X, scale.Y, 1.0) * Trafo3d.Translation(ndc.X, ndc.Y, 0.0)
                    | None ->
                        Trafo3d.Scale(0.0)
                )
            let textureOffset =
                (revolverBase, viewportSize, pixelSize) |||> AVal.map3 (fun ndc size ps ->
                    match ndc with
                    | Some ndc ->
                        let tc = (ndc + V2d.II) * 0.5
                        tc - 0.5 * V2d(ps, ps) / V2d size
                    | None -> V2d.Zero
                )
            let textureScale = (viewportSize, pixelSize) ||> AVal.map2 (fun s ps -> V2d(ps, ps) / V2d s)
            Sg.Uniform("TextureOffset", textureOffset)
            Sg.Uniform("TextureScale",  textureScale)
            Sg.Uniform("SliceIndex",    sliceIndex)
            Sg.Uniform("BorderColor",   borderColor)
            Sg.View Trafo3d.Identity
            Sg.Proj Trafo3d.Identity
            Sg.Uniform("ColorTexture",  colorArrTex)
            Sg.Trafo t
            Sg.Shader { DefaultSurfaces.trafo; BlitShader.readArraySliceColor }
            Primitives.FullscreenQuad
        }

    let build
        (env : Env<Message>)
        (info : Aardvark.Dom.RenderControlInfo)
        (view : aval<Trafo3d>)
        (proj : aval<Trafo3d>)
        (revolverBase     : aval<option<V2d>>)
        (revolverActive   : aval<bool>)
        (fullscreenActive : aval<bool>)
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

        let activePlacementId =
            model.ScanPins.Placement |> AVal.map (function
                | AdjustingPin(id, _) -> Some id
                | _ -> None)
        let effectiveGhostSilhouette =
            let cylClipActive =
                (model.ScanPins.SelectedPin, activePlacementId, model.ScanPins.Pins |> AMap.toAVal)
                |||> AVal.map3 (fun sel act pins ->
                    let id = act |> Option.orElse sel
                    match id |> Option.bind (fun id -> HashMap.tryFind id pins) with
                    | Some pin -> pin.GhostClip = GhostClipOn
                    | _ -> false)
            let placementPreviewActive =
                (model.ScanPins.Placement, model.ClipBounds)
                ||> AVal.map2 (fun p b -> (PinGeometry.placementPreviewPrism p b).IsSome)
            (model.GhostSilhouette, cylClipActive, placementPreviewActive)
            |||> AVal.map3 (fun g c p -> g || c || p)

        let exploreTex : aval<IBackendTexture> =
            let refAxis =
                (model.ReferenceAxis, view) ||> AVal.map2 (fun mode v ->
                    match mode with
                    | AlongWorldZ -> V3d.OOI
                    | AlongCameraView ->
                        v.Backward.TransformDir(V3d(0.0, 0.0, -1.0)) |> Vec.normalize)
            let exploreEnabled  = model.Explore |> AVal.map (fun e -> e.Enabled)
            let highlightModeInt = model.Explore |> AVal.map (fun e ->
                match e.HighlightMode with SteepnessOnly -> 0 | DisagreementOnly -> 1 | Combined -> 2)
            let steepnessThresh = model.Explore |> AVal.map (fun e -> e.SteepnessThreshold)
            let disagreementThresh = model.Explore |> AVal.map (fun e -> e.DisagreementThreshold)
            let highlightAlpha  = model.Explore |> AVal.map (fun e -> e.HighlightAlpha)
            let highlightColor =
                model.Explore |> AVal.map (fun e ->
                    let c = e.HighlightColor
                    V4d(float c.R, float c.G, float c.B, float c.A))
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
                    Sg.Uniform("ReferenceAxis",      refAxis)
                    Sg.Uniform("ExploreHighlightMode", highlightModeInt)
                    Sg.Uniform("SteepnessThreshold", steepnessThresh)
                    Sg.Uniform("DisagreementThreshold", disagreementThresh)
                    Sg.Uniform("HighlightColor",    highlightColor)
                    Sg.Uniform("HighlightAlpha",    highlightAlpha)
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

        let composite =
            sg {
                Sg.Active (AVal.map not fullscreenActive)
                MeshView.composeMeshTextures cnt colors depths exploreTexAsITex model.DifferenceRendering model.MinDifferenceDepth model.MaxDifferenceDepth clipMin clipMax effectiveGhostSilhouette meshVisibilityMask
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

        let diskRadius = model.RevolverSettings |> AVal.map (fun r -> r.CircleRadius)
        let diskNodes =
            model.MeshNames |> AList.map (fun name ->
                let order = model.MeshOrder |> AMap.tryFind name |> AVal.map (Option.defaultValue 0)
                let renderPos =
                    AVal.custom (fun t ->
                        let p = revolverBase.GetValue(t)
                        let o = order.GetValue(t)
                        let size = info.ViewportSize.GetValue(t)
                        let ps = diskRadius.GetValue(t)
                        match p with
                        | Some p -> Some (p + 2.0 * float o * (V2d(0.0, ps) / V2d size))
                        | None   -> None
                    )
                let borderCol = order |> AVal.map (fun o -> meshPalette.[o % meshPalette.Length])
                disk revolverActive revolverBase colorArrTex info.ViewportSize (sliceOf name) renderPos diskRadius borderCol
            ) |> AList.toASet

        let indicatorNodes = originIndicator view proj (AVal.map not fullscreenActive)

        let pinScene = ScanPinScene.build env view proj fullscreenActive model

        ASet.unionMany (ASet.ofList [ASet.single composite; fullscreenNodes; diskNodes; indicatorNodes; pinScene])
