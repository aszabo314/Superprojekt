namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open Aardworx.WebAssembly
open FSharp.Data.Adaptive
open Aardvark.Dom

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

    // Coordinate cross at render-space origin with thin axis lines, 0.25 tick marks, and distance labels.
    let private originIndicator (view : aval<Trafo3d>) (proj : aval<Trafo3d>) (active : aval<bool>) =
        let thickness = 0.04
        let axisLength = 3.0
        let tickSpacing = 0.25
        let tickLen = 0.12
        let labelSize = 0.15

        let toC4b (c : V4d) = C4b(byte(c.X*255.0), byte(c.Y*255.0), byte(c.Z*255.0))
        let darken (c : V4d) = toC4b (V4d(c.X * 0.55, c.Y * 0.55, c.Z * 0.55, 1.0))

        // Text in XY plane by default. Rotate so it stands upright (Z-up) and faces outward per axis.
        let textTrafoX = Trafo3d.RotationX(Constant.PiHalf)   // XY -> XZ plane, readable from +Y
        let textTrafoY = Trafo3d.RotationX(Constant.PiHalf) * Trafo3d.RotationZ(Constant.PiHalf) // YZ plane, readable from +X
        let textTrafoZ = Trafo3d.RotationX(Constant.PiHalf)   // XZ plane, upright

        let axisLine color (dir : V3d) =
            let half = dir * axisLength * 0.5
            let trafo =
                let s = V3d(thickness, thickness, thickness) + dir * (axisLength - thickness)
                Trafo3d.Scale(s) * Trafo3d.Translation(half)
            sg { Sg.Active active; Sg.View view; Sg.Proj proj; axisBox color trafo }

        let ticksAndLabels color (dir : V3d) (perpA : V3d) (textRot : Trafo3d) =
            let n = int (axisLength / tickSpacing)
            let textColor = darken color
            [ for i in 1 .. n do
                let dist = float i * tickSpacing
                let center = dir * dist
                let tickTrafo =
                    let s = perpA * tickLen + dir * thickness + (V3d.III - abs perpA - abs dir) * thickness
                    Trafo3d.Scale(abs s) * Trafo3d.Translation(center)
                yield sg { Sg.Active active; Sg.View view; Sg.Proj proj; axisBox color tickTrafo }
                if i % 4 = 0 then
                    let labelPos = center + perpA * (tickLen * 0.5 + labelSize * 1.2)
                    let trafo = Trafo3d.Scale(labelSize) * textRot * Trafo3d.Translation(labelPos)
                    yield sg {
                        Sg.Active active; Sg.View view; Sg.Proj proj
                        Sg.Trafo (AVal.constant trafo)
                        Sg.Text(sprintf "%.0f" dist, color = AVal.constant textColor, align = TextAlignment.Center)
                    }
            ]

        let xColor = V4d(0.82, 0.15, 0.1, 1.0)
        let yColor = V4d(0.1, 0.72, 0.1, 1.0)
        let zColor = V4d(0.15, 0.35, 0.9, 1.0)

        ASet.ofList [
            // Origin dot
            sg { Sg.Active active; Sg.View view; Sg.Proj proj; axisBox (V4d(0.88, 0.88, 0.88, 1.0)) (Trafo3d.Scale 0.08) }
            // Axis lines
            axisLine xColor V3d.IOO
            axisLine yColor V3d.OIO
            axisLine zColor V3d.OOI
            // Axis labels at tips
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
            // Ticks + distance labels
            yield! ticksAndLabels xColor V3d.IOO V3d.OOI textTrafoX
            yield! ticksAndLabels yColor V3d.OIO V3d.IOO textTrafoY
            yield! ticksAndLabels zColor V3d.OOI V3d.IOO textTrafoZ
        ]

    let buildPrismWireframe = PinGeometry.buildPrismWireframe
    let buildCutPlaneQuad = PinGeometry.buildCutPlaneQuad

    let private disk
            (revolverActive    : aval<bool>)
            (revolverBase      : aval<option<V2d>>)
            (colorArrTex       : aval<ITexture>)
            (viewportSize      : aval<V2i>)
            (sliceIndex        : aval<int>)
            (renderPositionNdc : aval<option<V2d>>) =
        let pixelSize = 200
        sg {
            Sg.Active revolverActive
            Sg.NoEvents
            let t =
                (renderPositionNdc, viewportSize) ||> AVal.map2 (fun ndc size ->
                    match ndc with
                    | Some ndc ->
                        let scale = float pixelSize / V2d size
                        Trafo3d.Scale(scale.X, scale.Y, 1.0) * Trafo3d.Translation(ndc.X, ndc.Y, 0.0)
                    | None ->
                        Trafo3d.Scale(0.0)
                )
            let textureOffset =
                (revolverBase, viewportSize) ||> AVal.map2 (fun ndc size ->
                    match ndc with
                    | Some ndc ->
                        let tc = (ndc + V2d.II) * 0.5
                        tc - 0.5 * V2d(pixelSize, pixelSize) / V2d size
                    | None -> V2d.Zero
                )
            let textureScale = viewportSize |> AVal.map (fun s -> V2d pixelSize / V2d s)
            Sg.Uniform("TextureOffset", textureOffset)
            Sg.Uniform("TextureScale",  textureScale)
            Sg.Uniform("SliceIndex",    sliceIndex)
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
        
        let cnt, colors, depths, meshIndices = MeshView.buildMeshTextures info loadFinished view proj model
        let colorArrTex  = colors  |> AdaptiveResource.map (fun t -> t :> ITexture)
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

        let effectiveGhostSilhouette =
            let cylClipActive =
                (model.ScanPins.SelectedPin, model.ScanPins.ActivePlacement, model.ScanPins.Pins |> AMap.toAVal)
                |||> AVal.map3 (fun sel act pins ->
                    let id = act |> Option.orElse sel
                    match id |> Option.bind (fun id -> HashMap.tryFind id pins) with
                    | Some pin -> pin.GhostClip = GhostClipOn
                    | _ -> false)
            (model.GhostSilhouette, cylClipActive, model.ClipActive)
            |||> AVal.map3 (fun g c box -> g || c || box)

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

        let diskNodes =
            model.MeshNames |> AList.map (fun name ->
                let order = model.MeshOrder |> AMap.tryFind name |> AVal.map (Option.defaultValue 0)
                let renderPos =
                    (revolverBase, order, info.ViewportSize) |||> AVal.map3 (fun p o size ->
                        match p with
                        | Some p -> Some (p + 2.0 * float o * (V2d(0.0, 200.0) / V2d size))
                        | None   -> None
                    )
                disk revolverActive revolverBase colorArrTex info.ViewportSize (sliceOf name) renderPos
            ) |> AList.toASet

        let indicatorNodes = originIndicator view proj (AVal.map not fullscreenActive)

        let pinIdSet = model.ScanPins.Pins |> AMap.toASet |> ASet.map fst
        let pinsVal = model.ScanPins.Pins |> AMap.toAVal

        let pinDots =
            let notFullscreen = AVal.map not fullscreenActive
            let selectedId = model.ScanPins.SelectedPin
            pinIdSet |> ASet.map (fun id ->
                let pinVal = pinsVal |> AVal.map (fun pins -> HashMap.tryFind id pins)
                let color =
                    (selectedId, pinVal) ||> AVal.map2 (fun sel pinOpt ->
                        match pinOpt with
                        | Some pin ->
                            if sel = Some id then V4d(1.0, 0.9, 0.0, 1.0)
                            elif pin.Phase = PinPhase.Placement then V4d(0.2, 1.0, 0.3, 1.0)
                            else V4d(1.0, 0.3, 0.3, 1.0)
                        | None -> V4d(0.0, 0.0, 0.0, 0.0))
                let trafo = pinVal |> AVal.map (fun pinOpt ->
                    match pinOpt with
                    | Some pin -> Trafo3d.Scale(0.5) * Trafo3d.Translation(pin.Prism.AnchorPoint)
                    | None -> Trafo3d.Scale(0.0))
                sg {
                    Sg.Active notFullscreen
                    Sg.View view
                    Sg.Proj proj
                    Sg.Trafo trafo
                    Sg.Shader { DefaultSurfaces.trafo; Shader.flatColor }
                    Sg.Uniform("FlatColor", color)
                    Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
                    Sg.OnTap(fun _ ->
                        let sel = AVal.force selectedId
                        if sel = Some id then env.Emit [ScanPinMsg (SelectPin None)]
                        else env.Emit [ScanPinMsg (SelectPin (Some id))]
                        false)
                    Sg.OnDoubleTap(fun _ ->
                        env.Emit [ScanPinMsg (FocusPin id)]
                        false)
                    Sg.VertexAttributes(
                        HashMap.ofList [ string DefaultSemantic.Positions, BufferView(AVal.constant (ArrayBuffer boxPos :> IBuffer), typeof<V3f>) ]
                    )
                    Sg.Index(BufferView(AVal.constant (ArrayBuffer boxIdx :> IBuffer), typeof<int>))
                    Sg.Render (AVal.constant boxIdx.Length)
                }
            )

        let pinPrisms =
            let notFullscreen = AVal.map not fullscreenActive
            let selectedId = model.ScanPins.SelectedPin
            pinIdSet |> ASet.collect (fun id ->
                let pinVal = pinsVal |> AVal.map (fun pins -> HashMap.tryFind id pins)
                let isSelected = selectedId |> AVal.map (fun sel -> sel = Some id)
                // Split pin into prism-only (for structure) and cutPlane (for placement).
                // Structure only changes on resize; placement changes on every drag.
                let prismVal = pinVal |> AVal.map (fun po -> po |> Option.map (fun p -> p.Prism))
                let cutPlaneVal = pinVal |> AVal.map (fun po -> po |> Option.map (fun p -> p.CutPlane))
                let planeData =
                    (prismVal, cutPlaneVal) ||> AVal.map2 (fun po co ->
                        match po, co with
                        | Some prism, Some cp -> PinGeometry.buildCutPlaneRefined prism cp
                        | _ -> [||], [||], [||])
                let planePos = planeData |> AVal.map (fun (p,_,_) -> p)
                let planeCol = planeData |> AVal.map (fun (_,c,_) -> c)
                let planeIdx = planeData |> AVal.map (fun (_,_,i) -> i)
                let tickStructure =
                    prismVal |> AVal.map (fun po ->
                        match po with
                        | Some prism -> PinGeometry.tickLabelStructure prism
                        | None -> [])
                let isActiveAndSelected = AVal.map2 (&&) notFullscreen isSelected
                let labelNodes =
                    tickStructure
                    |> AVal.map (fun labels ->
                        labels |> List.map (fun (edge, unitT, text) ->
                            let trafo =
                                (prismVal, cutPlaneVal) ||> AVal.map2 (fun po co ->
                                    match po, co with
                                    | Some prism, Some cp -> PinGeometry.tickLabelWorldTrafo prism cp edge unitT
                                    | _ -> Trafo3d.Identity)
                            (text, trafo)) |> IndexList.ofList)
                    |> AList.ofAVal
                    |> AList.map (fun (text, trafo) ->
                        sg {
                            Sg.Active isActiveAndSelected
                            Sg.View view
                            Sg.Proj proj
                            Sg.Pass RenderPass.passTwo
                            Sg.Trafo trafo
                            Sg.Text(text, color = AVal.constant (C4b(160uy, 160uy, 160uy)), align = TextAlignment.Center)
                        })
                    |> AList.toASet
                let centroidTrafo =
                    (prismVal, cutPlaneVal) ||> AVal.map2 (fun po co ->
                        match po, co with
                        | Some prism, Some cp -> PinGeometry.centroidLabelTrafo prism cp
                        | _ -> Trafo3d.Identity)
                let centroidText =
                    model.CommonCentroid |> AVal.map (fun cc ->
                        sprintf "%.2f, %.2f, %.2f" cc.X cc.Y cc.Z)
                let centroidNode =
                    ASet.ofList [
                        sg {
                            Sg.Active isActiveAndSelected
                            Sg.View view
                            Sg.Proj proj
                            Sg.Pass RenderPass.passTwo
                            Sg.Trafo centroidTrafo
                            Sg.Text(centroidText, color = AVal.constant (C4b(130uy, 140uy, 160uy)), align = TextAlignment.Center)
                        }
                    ]
                ASet.union
                    (ASet.ofList [
                        sg {
                            Sg.Active isActiveAndSelected
                            Sg.View view
                            Sg.Proj proj
                            Sg.Pass RenderPass.passOne
                            Sg.Shader { DefaultSurfaces.trafo; Shader.vertexColor }
                            Sg.BlendMode BlendMode.Blend
                            Sg.DepthTest (AVal.constant DepthTest.None)
                            Sg.NoEvents
                            Sg.VertexAttributes(
                                HashMap.ofList [
                                    string DefaultSemantic.Positions, BufferView(planePos |> AVal.map (fun p -> ArrayBuffer p :> IBuffer), typeof<V3f>)
                                    string DefaultSemantic.Colors,    BufferView(planeCol |> AVal.map (fun c -> ArrayBuffer c :> IBuffer), typeof<V4f>)
                                ])
                            Sg.Index(BufferView(planeIdx |> AVal.map (fun i -> ArrayBuffer i :> IBuffer), typeof<int>))
                            Sg.Render(planeIdx |> AVal.map Array.length)
                        }
                    ])
                    (ASet.unionMany (ASet.ofList [labelNodes; centroidNode]))
            )

        let betweenSpaceBand =
            let notFullscreen = AVal.map not fullscreenActive
            let selectedId = model.ScanPins.SelectedPin
            pinIdSet |> ASet.collect (fun id ->
                let pinVal = pinsVal |> AVal.map (fun pins -> HashMap.tryFind id pins)
                let isSelected = selectedId |> AVal.map (fun sel -> sel = Some id)
                let bsEnabled = model.ScanPins.BetweenSpaceEnabled
                let isActiveAndSelected = (notFullscreen, isSelected, bsEnabled) |||> AVal.map3 (fun nf sel bs -> nf && sel && bs)
                let prismVal = pinVal |> AVal.map (fun po -> po |> Option.map (fun p -> p.Prism))
                let stratVal = pinVal |> AVal.map (fun po -> po |> Option.bind (fun p -> p.Stratigraphy))
                let cacheVal = pinVal |> AVal.map (fun po -> po |> Option.bind (fun p -> p.BandCache))
                let hoverVal =
                    (pinVal, bsEnabled) ||> AVal.map2 (fun po enabled ->
                        if not enabled then None
                        else po |> Option.bind (fun p -> p.BetweenSpaceHover)
                                |> Option.map (fun h -> h.ColumnIdx, h.HoverZ))
                let prismCacheVal = (prismVal, cacheVal) ||> AVal.map2 (fun a b -> a, b)
                let geo =
                    (prismCacheVal, stratVal, hoverVal) |||> AVal.map3 (fun (prismO, cacheO) dataO hOpt ->
                        match prismO, dataO, hOpt with
                        | Some prism, Some data, Some (col, z) -> PinGeometry.buildBetweenSpaceSurfaces prism data cacheO col z
                        | _ -> ([||], [||]), ([||], [||]), ([||], [||]))
                let dummyP = [| V3f.Zero |]
                let safeP (a : V3f[]) = if a.Length = 0 then dummyP else a
                let upperPos = geo |> AVal.map (fun ((p, _), _, _) -> ArrayBuffer (safeP p) :> IBuffer)
                let upperIdx = geo |> AVal.map (fun ((_, i), _, _) -> ArrayBuffer i :> IBuffer)
                let upperCnt = geo |> AVal.map (fun ((_, i), _, _) -> i.Length)
                let lowerPos = geo |> AVal.map (fun (_, (p, _), _) -> ArrayBuffer (safeP p) :> IBuffer)
                let lowerIdx = geo |> AVal.map (fun (_, (_, i), _) -> ArrayBuffer i :> IBuffer)
                let lowerCnt = geo |> AVal.map (fun (_, (_, i), _) -> i.Length)
                let sidePos  = geo |> AVal.map (fun (_, _, (p, _)) -> ArrayBuffer (safeP p) :> IBuffer)
                let sideIdx  = geo |> AVal.map (fun (_, _, (_, i)) -> ArrayBuffer i :> IBuffer)
                let sideCnt  = geo |> AVal.map (fun (_, _, (_, i)) -> i.Length)
                let makeSurface (color : V4d) (positions : aval<IBuffer>) (idx : aval<IBuffer>) (cnt : aval<int>) =
                    sg {
                        Sg.Active isActiveAndSelected
                        Sg.View view
                        Sg.Proj proj
                        Sg.Pass RenderPass.passTwo
                        Sg.Shader { DefaultSurfaces.trafo; Shader.flatColor }
                        Sg.Uniform("FlatColor", AVal.constant color)
                        Sg.BlendMode BlendMode.Blend
                        Sg.DepthTest (AVal.constant DepthTest.None)
                        Sg.NoEvents
                        Sg.VertexAttributes(
                            HashMap.ofList [
                                string DefaultSemantic.Positions, BufferView(positions, typeof<V3f>)
                            ])
                        Sg.Index(BufferView(idx, typeof<int>))
                        Sg.Render cnt
                    }
                ASet.ofList [
                    makeSurface (V4d(1.0, 1.0, 0.9, 0.55)) upperPos upperIdx upperCnt
                    makeSurface (V4d(1.0, 0.95, 0.7, 0.55)) lowerPos lowerIdx lowerCnt
                    makeSurface (V4d(1.0, 0.97, 0.8, 0.40)) sidePos  sideIdx  sideCnt
                ]
            )

        let extractedLines =
            let notFullscreen = AVal.map not fullscreenActive
            let toV4f (c : C4b) =
                let f = c.ToC4f()
                V4f(f.R, f.G, f.B, 1.0f)

            let meshNamesVal = model.MeshNames |> AList.toAVal |> AVal.map IndexList.toArray
            pinIdSet |> ASet.collect (fun id ->
                let pinVal = pinsVal |> AVal.map (fun pins -> HashMap.tryFind id pins)

                // Project individual fields so each geometry only rebuilds when its inputs change.
                // cutGeo depends on cut results (arrive from server, not on every drag).
                let cutResultsDep =
                    pinVal |> AVal.map (fun po ->
                        po |> Option.bind (fun p ->
                            if p.ExtractedLines.ShowCutPlaneLines && not (Map.isEmpty p.CutResults)
                            then Some (p.Prism, p.CutResultsPlane, p.CutResults, p.DatasetColors, p.Stratigraphy)
                            else None))
                // edgeDeps: stratigraphy + prism + colors — does NOT depend on cut plane.
                let edgeDeps =
                    pinVal |> AVal.map (fun po ->
                        po |> Option.bind (fun p ->
                            if p.ExtractedLines.ShowCylinderEdgeLines
                            then Some (p.Prism, p.Stratigraphy, p.DatasetColors)
                            else None))

                let cutGeo =
                    (cutResultsDep, meshNamesVal) ||> AVal.map2 (fun depsOpt names ->
                        match depsOpt with
                        | Some(prism, cutPlane, cutResults, datasetColors, strat) ->
                            let axis = prism.AxisDirection |> Vec.normalize
                            let up = if abs axis.Z > 0.9 then V3d.OIO else V3d.OOI
                            let right = Vec.cross axis up |> Vec.normalize
                            let fwd = Vec.cross right axis |> Vec.normalize
                            let planePoint, axisU, axisV, planeNormal =
                                match cutPlane with
                                | CutPlaneMode.AlongAxis angleDeg ->
                                    let a = angleDeg * Constant.RadiansPerDegree
                                    let dir = right * cos a + fwd * sin a
                                    let normal = Vec.cross dir axis |> Vec.normalize
                                    prism.AnchorPoint, dir, axis, normal
                                | CutPlaneMode.AcrossAxis dist ->
                                    prism.AnchorPoint + axis * dist, right, fwd, axis
                            let positions = ResizeArray<V3f>()
                            let colors = ResizeArray<V4f>()
                            let indices = ResizeArray<int>()
                            let thickness = 0.06
                            for KeyValue(name, cr) in cutResults do
                                let color = datasetColors |> Map.tryFind name |> Option.defaultValue (C4b(120uy,120uy,120uy)) |> toV4f
                                for poly in cr.Polylines do
                                    let pts3d =
                                        poly |> List.map (fun (p : V2d) -> planePoint + axisU * p.X + axisV * p.Y) |> Array.ofList
                                    PinGeometry.appendPolylineRibbon positions colors indices pts3d color planeNormal thickness
                            positions.ToArray(), colors.ToArray(), indices.ToArray()
                        | _ -> [||], [||], [||])

                let edgeGeo =
                    (edgeDeps, meshNamesVal) ||> AVal.map2 (fun depsOpt names ->
                        match depsOpt with
                        | Some(prism, Some data, datasetColors) when data.Columns.Length >= 2 ->
                            let axis = prism.AxisDirection |> Vec.normalize
                            let up = if abs axis.Z > 0.9 then V3d.OIO else V3d.OOI
                            let right = Vec.cross axis up |> Vec.normalize
                            let fwd = Vec.cross right axis |> Vec.normalize
                            let radius = match prism.Footprint.Vertices with v :: _ -> v.Length | _ -> 1.0
                            let toPoint (angle : float) (z : float) =
                                prism.AnchorPoint + (right * cos angle + fwd * sin angle) * radius + axis * z
                            let datasets =
                                data.Columns
                                |> Array.collect (fun c -> c.Events |> List.map snd |> List.toArray)
                                |> Array.distinct
                            let positions = ResizeArray<V3f>()
                            let colors = ResizeArray<V4f>()
                            let indices = ResizeArray<int>()
                            let thickness = 0.05
                            for ds in datasets do
                                let color = datasetColors |> Map.tryFind ds |> Option.defaultValue (C4b(120uy,120uy,120uy)) |> toV4f
                                let perColumn =
                                    data.Columns |> Array.map (fun col ->
                                        col.Events
                                        |> List.filter (fun (_, n) -> n = ds)
                                        |> List.map fst
                                        |> List.sort)
                                let maxLanes = perColumn |> Array.map List.length |> Array.fold max 0
                                for lane in 0 .. maxLanes - 1 do
                                    let mutable accum = ResizeArray<V3d>()
                                    let flush () =
                                        if accum.Count >= 2 then
                                            PinGeometry.appendPolylineRibbon positions colors indices (accum.ToArray()) color axis thickness
                                        accum <- ResizeArray<V3d>()
                                    for ci in 0 .. data.Columns.Length - 1 do
                                        let zs = perColumn.[ci]
                                        if lane < zs.Length then
                                            accum.Add(toPoint data.Columns.[ci].Angle zs.[lane])
                                        else
                                            flush ()
                                    if accum.Count > 0 then
                                        let zs0 = perColumn.[0]
                                        if lane < zs0.Length then
                                            accum.Add(toPoint data.Columns.[0].Angle zs0.[lane])
                                        flush ()
                            positions.ToArray(), colors.ToArray(), indices.ToArray()
                        | _ -> [||], [||], [||])

                let dummyP = [| V3f.Zero |]
                let dummyC = [| V4f.Zero |]
                let safeP (a : V3f[]) = if a.Length = 0 then dummyP else a
                let safeC (a : V4f[]) = if a.Length = 0 then dummyC else a
                let cutPos = cutGeo |> AVal.map (fun (p,_,_) -> ArrayBuffer (safeP p) :> IBuffer)
                let cutCol = cutGeo |> AVal.map (fun (_,c,_) -> ArrayBuffer (safeC c) :> IBuffer)
                let cutIdx = cutGeo |> AVal.map (fun (_,_,i) -> ArrayBuffer i :> IBuffer)
                let cutCnt = cutGeo |> AVal.map (fun (_,_,i) -> i.Length)
                let edgePos = edgeGeo |> AVal.map (fun (p,_,_) -> ArrayBuffer (safeP p) :> IBuffer)
                let edgeCol = edgeGeo |> AVal.map (fun (_,c,_) -> ArrayBuffer (safeC c) :> IBuffer)
                let edgeIdx = edgeGeo |> AVal.map (fun (_,_,i) -> ArrayBuffer i :> IBuffer)
                let edgeCnt = edgeGeo |> AVal.map (fun (_,_,i) -> i.Length)

                ASet.ofList [
                    sg {
                        Sg.Active notFullscreen
                        Sg.View view
                        Sg.Proj proj
                        Sg.Shader { DefaultSurfaces.trafo; Shader.vertexColor }
                        Sg.DepthTest (AVal.constant DepthTest.None)
                        Sg.NoEvents
                        Sg.VertexAttributes(
                            HashMap.ofList [
                                string DefaultSemantic.Positions, BufferView(cutPos, typeof<V3f>)
                                string DefaultSemantic.Colors,    BufferView(cutCol, typeof<V4f>) ])
                        Sg.Index(BufferView(cutIdx, typeof<int>))
                        Sg.Render cutCnt
                    }
                    sg {
                        Sg.Active notFullscreen
                        Sg.View view
                        Sg.Proj proj
                        Sg.Shader { DefaultSurfaces.trafo; Shader.vertexColor }
                        Sg.DepthTest (AVal.constant DepthTest.None)
                        Sg.NoEvents
                        Sg.VertexAttributes(
                            HashMap.ofList [
                                string DefaultSemantic.Positions, BufferView(edgePos, typeof<V3f>)
                                string DefaultSemantic.Colors,    BufferView(edgeCol, typeof<V4f>) ])
                        Sg.Index(BufferView(edgeIdx, typeof<int>))
                        Sg.Render edgeCnt
                    }
                ])

        // Hull picking: click/drag on the cylinder hull to set cut plane.
        // Replaces the old rail+handle slider (AcrossAxis) and cap-disc picker (AlongAxis).
        let hullPicking =
            let notFullscreen = AVal.map not fullscreenActive
            let selectedId = model.ScanPins.SelectedPin
            let activeId = model.ScanPins.ActivePlacement
            let editedPin =
                (selectedId, activeId, pinsVal) |||> AVal.map3 (fun sel act pins ->
                    let id = act |> Option.orElse sel
                    id |> Option.bind (fun id -> HashMap.tryFind id pins))

            let hullDragging : cval<bool> = cval false

            let editedPrism = editedPin |> AVal.map (fun po -> po |> Option.map (fun p -> p.Prism))
            let editedCutPlane = editedPin |> AVal.map (fun po -> po |> Option.map (fun p -> p.CutPlane))

            let hullGeometry =
                editedPrism |> AVal.map (fun prismOpt ->
                    match prismOpt with
                    | Some prism ->
                        let p, i = PinGeometry.buildCylinderHull prism 64
                        p, i, true
                    | None -> [||], [||], false)

            let hullPos = hullGeometry |> AVal.map (fun (p,_,_) -> ArrayBuffer p :> IBuffer)
            let hullIdx = hullGeometry |> AVal.map (fun (_,i,_) -> ArrayBuffer i :> IBuffer)
            let hullCnt = hullGeometry |> AVal.map (fun (_,i,_) -> i.Length)
            let hullActive =
                (notFullscreen, hullGeometry) ||> AVal.map2 (fun nf (_,_,act) -> nf && act)

            let indicatorGeometry =
                (editedPrism, editedCutPlane) ||> AVal.map2 (fun prismOpt cpOpt ->
                    match prismOpt, cpOpt with
                    | Some prism, Some cp ->
                        let r = match prism.Footprint.Vertices with v :: _ -> v.Length | _ -> 1.0
                        let thick = max 0.03 (r * 0.04)
                        match cp with
                        | CutPlaneMode.AcrossAxis dist ->
                            let p, i = PinGeometry.buildHullRing prism dist thick 64
                            p, i, true
                        | CutPlaneMode.AlongAxis angleDeg ->
                            let p, i = PinGeometry.buildHullLine prism angleDeg thick
                            p, i, true
                    | _ -> [||], [||], false)

            let indPos = indicatorGeometry |> AVal.map (fun (p,_,_) -> ArrayBuffer p :> IBuffer)
            let indIdx = indicatorGeometry |> AVal.map (fun (_,i,_) -> ArrayBuffer i :> IBuffer)
            let indCnt = indicatorGeometry |> AVal.map (fun (_,i,_) -> i.Length)
            let indActive = (notFullscreen, indicatorGeometry) ||> AVal.map2 (fun nf (_,_,act) -> nf && act)

            let emitFromHit (hit : V3d) =
                match AVal.force editedPin with
                | Some pin ->
                    let axis = pin.Prism.AxisDirection |> Vec.normalize
                    let up = if abs axis.Z > 0.9 then V3d.OIO else V3d.OOI
                    let right = Vec.cross axis up |> Vec.normalize
                    let fwd = Vec.cross right axis |> Vec.normalize
                    let v = hit - pin.Prism.AnchorPoint
                    match pin.CutPlane with
                    | CutPlaneMode.AcrossAxis _ ->
                        let dist = Vec.dot v axis
                        let clamped = clamp (-pin.Prism.ExtentBackward) pin.Prism.ExtentForward dist
                        env.Emit [ScanPinMsg (SetCutPlaneDistance clamped)]
                    | CutPlaneMode.AlongAxis _ ->
                        let lx = Vec.dot v right
                        let ly = Vec.dot v fwd
                        let ang = atan2 ly lx * Constant.DegreesPerRadian
                        env.Emit [ScanPinMsg (SetCutPlaneAngle ang)]
                | None -> ()

            let pickRayOf (e : ScenePointerEvent) =
                let vp = e.ViewProjTrafo
                let pixel = e.Pixel
                let s = e.ViewportSize
                let ndc = V2d(2.0 * float pixel.X / float s.X - 1.0, 1.0 - 2.0 * float pixel.Y / float s.Y)
                let p0 = vp.Backward.TransformPosProj(V3d(ndc, -1.0))
                let p1 = vp.Backward.TransformPosProj(V3d(ndc, 1.0))
                Ray3d(p0, (p1 - p0) |> Vec.normalize)

            let intersectCylinder (ray : Ray3d) =
                match AVal.force editedPin with
                | Some pin ->
                    let axis = pin.Prism.AxisDirection |> Vec.normalize
                    let r = match pin.Prism.Footprint.Vertices with v :: _ -> v.Length | _ -> 1.0
                    let anchor = pin.Prism.AnchorPoint
                    let oc = ray.Origin - anchor
                    let d_perp = ray.Direction - axis * (Vec.dot ray.Direction axis)
                    let oc_perp = oc - axis * (Vec.dot oc axis)
                    let a = Vec.dot d_perp d_perp
                    let b = 2.0 * Vec.dot oc_perp d_perp
                    let c = Vec.dot oc_perp oc_perp - r * r
                    let disc = b * b - 4.0 * a * c
                    if disc < 0.0 || a < 1e-12 then None
                    else
                        let sqrtD = sqrt disc
                        let t1 = (-b - sqrtD) / (2.0 * a)
                        let t2 = (-b + sqrtD) / (2.0 * a)
                        let tryT t =
                            if t < 0.0 then None
                            else
                                let hit = ray.Origin + ray.Direction * t
                                let axDist = Vec.dot (hit - anchor) axis
                                if axDist >= -pin.Prism.ExtentBackward && axDist <= pin.Prism.ExtentForward then Some hit
                                else None
                        match tryT t1 with
                        | Some h -> Some h
                        | None -> tryT t2
                | None -> None

            let updateFromPointerRay (e : ScenePointerEvent) =
                match intersectCylinder (pickRayOf e) with
                | Some hit -> emitFromHit hit
                | None -> ()

            let pickCylinder =
                editedPin |> AVal.map (fun pinOpt ->
                    match pinOpt with
                    | Some pin ->
                        let axis = pin.Prism.AxisDirection |> Vec.normalize
                        let r = match pin.Prism.Footprint.Vertices with v :: _ -> v.Length | _ -> 1.0
                        let p0 = pin.Prism.AnchorPoint - axis * pin.Prism.ExtentBackward
                        let p1 = pin.Prism.AnchorPoint + axis * pin.Prism.ExtentForward
                        let cyl = Cylinder3d(p0, p1, r)
                        Intersectable.cylinder cyl
                    | None ->
                        Intersectable.cylinder ( Cylinder3d(V3d.Zero, V3d.OOI, 0.001)))

            // Dummy single-triangle for the pick node (degenerate, invisible)
            let dummyPos = AVal.constant (ArrayBuffer [| V3f.Zero; V3f.OOI * 0.001f; V3f.IOO * 0.001f |] :> IBuffer)
            let dummyIdx = AVal.constant (ArrayBuffer [| 0; 1; 2 |] :> IBuffer)

            ASet.ofList [
                // Pick node: Intersectable cylinder + event handlers + tiny dummy geometry
                sg {
                    Sg.Active hullActive
                    Sg.View view
                    Sg.Proj proj
                    Sg.Uniform("FlatColor", AVal.constant (V4d(0.0, 0.0, 0.0, 0.0)))
                    Sg.DepthTest (AVal.constant DepthTest.None)
                    Sg.OnPointerDown(true, fun e ->
                        match AVal.force editedPin with
                        | Some _ ->
                            transact (fun () -> hullDragging.Value <- true)
                            updateFromPointerRay e
                            false
                        | None ->
                            true)
                    Sg.OnPointerMove(fun e ->
                        if AVal.force hullDragging then
                            updateFromPointerRay e
                            false
                        else true
                        )
                    Sg.OnPointerUp(true, fun _ ->
                        if AVal.force hullDragging then
                            transact (fun () -> hullDragging.Value <- false)
                            false
                        else true
                        )
                    sg {
                        Sg.Intersectable pickCylinder
                        Sg.VertexAttributes(
                            HashMap.ofList [ string DefaultSemantic.Positions, BufferView(dummyPos, typeof<V3f>) ])
                        Sg.Shader { DefaultSurfaces.trafo; Shader.flatColor }
                        Sg.Index(BufferView(dummyIdx, typeof<int>))
                        Sg.Render (AVal.constant 3)
                    }
                }
                // Visual hull: transparent white, no picking
                sg {
                    Sg.Active hullActive
                    Sg.View view
                    Sg.Proj proj
                    Sg.Shader { DefaultSurfaces.trafo; Shader.flatColor }
                    Sg.Uniform("FlatColor", AVal.constant (V4d(1.0, 1.0, 1.0, 0.1)))
                    Sg.BlendMode (AVal.constant BlendMode.Blend)
                    Sg.DepthTest (AVal.constant DepthTest.None)
                    Sg.Pass RenderPass.passOne
                    Sg.NoEvents
                    Sg.VertexAttributes(
                        HashMap.ofList [ string DefaultSemantic.Positions, BufferView(hullPos, typeof<V3f>) ])
                    Sg.Index(BufferView(hullIdx, typeof<int>))
                    Sg.Render hullCnt
                }
                // Cut plane indicator (ring or line on hull)
                sg {
                    Sg.Active indActive
                    Sg.View view
                    Sg.Proj proj
                    Sg.Shader { DefaultSurfaces.trafo; Shader.flatColor }
                    Sg.Uniform("FlatColor", AVal.constant (V4d(1.0, 1.0, 1.0, 0.9)))
                    Sg.DepthTest (AVal.constant DepthTest.None)
                    Sg.Pass RenderPass.passOne
                    Sg.VertexAttributes(
                        HashMap.ofList [ string DefaultSemantic.Positions, BufferView(indPos, typeof<V3f>) ])
                    Sg.Index(BufferView(indIdx, typeof<int>))
                    Sg.Render indCnt
                }
            ]

        ASet.unionMany (ASet.ofList [ASet.single composite; fullscreenNodes; diskNodes; indicatorNodes; pinDots; pinPrisms; betweenSpaceBand; extractedLines; hullPicking])
