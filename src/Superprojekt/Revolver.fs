namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open Aardworx.WebAssembly
open FSharp.Data.Adaptive
open Aardvark.Dom

module Revolver =

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

    // Axis indicator at render-space origin (= common centroid).
    // +Y = North (green, dominant), +X = East (red), +Z = Up (blue). Sizes in render-space units.
    let private originIndicator (view : aval<Trafo3d>) (proj : aval<Trafo3d>) (active : aval<bool>) =
        let box color trafo =
            sg { Sg.Active active; Sg.View view; Sg.Proj proj; axisBox color trafo }
        ASet.ofList [
            box (V4d(0.88, 0.88, 0.88, 1.0)) (Trafo3d.Scale 1.5)
            box (V4d(0.1,  0.72, 0.1,  1.0)) (Trafo3d.Scale(1.0, 5.0, 1.0) * Trafo3d.Translation(0.0, 2.5, 0.0))  // N shaft
            box (V4d(0.1,  0.72, 0.1,  1.0)) (Trafo3d.Scale(2.2, 2.0, 2.2) * Trafo3d.Translation(0.0, 6.0, 0.0))  // N tip
            box (V4d(0.82, 0.15, 0.1,  1.0)) (Trafo3d.Scale(3.0, 0.75, 0.75) * Trafo3d.Translation(1.5, 0.0, 0.0)) // E
            box (V4d(0.15, 0.35, 0.9,  1.0)) (Trafo3d.Scale(0.75, 0.75, 3.0) * Trafo3d.Translation(0.0, 0.0, 1.5)) // U
        ]

    let buildPrismWireframe (prism : SelectionPrism) (thickness : float) =
        let axis = prism.AxisDirection |> Vec.normalize
        let up = if abs axis.Z > 0.9 then V3d.OIO else V3d.OOI
        let right = Vec.cross axis up |> Vec.normalize
        let fwd = Vec.cross right axis |> Vec.normalize
        let n = prism.Footprint.Vertices.Length
        if n < 3 then [||], [||]
        else
            let to3d (v : V2d) offset = prism.AnchorPoint + right * v.X + fwd * v.Y + axis * offset
            let topVerts  = prism.Footprint.Vertices |> List.map (fun v -> to3d v prism.ExtentForward)  |> Array.ofList
            let botVerts  = prism.Footprint.Vertices |> List.map (fun v -> to3d v (-prism.ExtentBackward)) |> Array.ofList
            let positions = System.Collections.Generic.List<V3f>()
            let indices   = System.Collections.Generic.List<int>()
            let addEdge (a : V3d) (b : V3d) =
                let dir = b - a
                let perp =
                    let c = Vec.cross dir axis
                    if c.Length < 1e-10 then Vec.cross dir right |> Vec.normalize else c |> Vec.normalize
                let off = perp * thickness * 0.5
                let i0 = positions.Count
                positions.Add(V3f(a + off))
                positions.Add(V3f(a - off))
                positions.Add(V3f(b + off))
                positions.Add(V3f(b - off))
                indices.Add(i0); indices.Add(i0+1); indices.Add(i0+2)
                indices.Add(i0+1); indices.Add(i0+3); indices.Add(i0+2)
            for i in 0 .. n - 1 do
                let j = (i + 1) % n
                addEdge topVerts.[i] topVerts.[j]
                addEdge botVerts.[i] botVerts.[j]
            for i in 0 .. n - 1 do
                addEdge topVerts.[i] botVerts.[i]
            positions.ToArray(), indices.ToArray()

    let buildCutPlaneQuad (prism : SelectionPrism) (cutPlane : CutPlaneMode) =
        let axis = prism.AxisDirection |> Vec.normalize
        let up = if abs axis.Z > 0.9 then V3d.OIO else V3d.OOI
        let right = Vec.cross axis up |> Vec.normalize
        let fwd = Vec.cross right axis |> Vec.normalize
        let r = match prism.Footprint.Vertices with v :: _ -> v.Length | _ -> 1.0
        match cutPlane with
        | CutPlaneMode.AlongAxis angleDeg ->
            let a = angleDeg * Constant.RadiansPerDegree
            let planeDir = right * cos a + fwd * sin a
            let hw = r * 1.2
            let hh = (prism.ExtentForward + prism.ExtentBackward) * 0.5
            let center = prism.AnchorPoint + axis * (prism.ExtentForward - prism.ExtentBackward) * 0.5
            [| V3f(center - planeDir * hw - axis * hh)
               V3f(center + planeDir * hw - axis * hh)
               V3f(center + planeDir * hw + axis * hh)
               V3f(center - planeDir * hw + axis * hh) |],
            [| 0;1;2; 0;2;3 |]
        | CutPlaneMode.AcrossAxis dist ->
            let center = prism.AnchorPoint + axis * dist
            let hw = r * 1.2
            [| V3f(center - right * hw - fwd * hw)
               V3f(center + right * hw - fwd * hw)
               V3f(center + right * hw + fwd * hw)
               V3f(center - right * hw + fwd * hw) |],
            [| 0;1;2; 0;2;3 |]

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
        let colorArrTex = colors |> AdaptiveResource.map (fun t -> t :> ITexture)
        let depthArrTex = depths |> AdaptiveResource.map (fun t -> t :> ITexture)

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

        let composite =
            sg {
                Sg.Active (AVal.map not fullscreenActive)
                MeshView.composeMeshTextures cnt colors depths model.DifferenceRendering model.MinDifferenceDepth model.MaxDifferenceDepth clipMin clipMax model.GhostSilhouette meshVisibilityMask
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

        let pinDots =
            let notFullscreen = AVal.map not fullscreenActive
            let selectedId = model.ScanPins.SelectedPin
            model.ScanPins.Pins |> AMap.toASet |> ASet.map (fun (id, pin) ->
                let color =
                    selectedId |> AVal.map (fun sel ->
                        if sel = Some id then V4d(1.0, 0.9, 0.0, 1.0)
                        elif pin.Phase = PinPhase.Placement then V4d(0.2, 1.0, 0.3, 1.0)
                        else V4d(1.0, 0.3, 0.3, 1.0))
                sg {
                    Sg.Active notFullscreen
                    Sg.View view
                    Sg.Proj proj
                    Sg.Trafo (AVal.constant (Trafo3d.Scale(0.5) * Trafo3d.Translation(pin.Prism.AnchorPoint)))
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
            model.ScanPins.Pins |> AMap.toASet |> ASet.collect (fun (id, pin) ->
                let isSelected = selectedId |> AVal.map (fun sel -> sel = Some id)
                let wireColor =
                    selectedId |> AVal.map (fun sel ->
                        if sel = Some id then V4d(1.0, 0.85, 0.0, 0.7)
                        elif pin.Phase = PinPhase.Placement then V4d(0.2, 1.0, 0.3, 0.5)
                        else V4d(0.6, 0.6, 0.6, 0.35))
                let wirePos, wireIdx = buildPrismWireframe pin.Prism 0.05
                let planePos, planeIdx = buildCutPlaneQuad pin.Prism pin.CutPlane
                let prismSg =
                    if wireIdx.Length = 0 then None
                    else Some (
                        sg {
                            Sg.Active notFullscreen
                            Sg.View view
                            Sg.Proj proj
                            Sg.Shader { DefaultSurfaces.trafo; Shader.flatColor }
                            Sg.Uniform("FlatColor", wireColor)
                            Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
                            Sg.NoEvents
                            Sg.VertexAttributes(
                                HashMap.ofList [ string DefaultSemantic.Positions, BufferView(AVal.constant (ArrayBuffer wirePos :> IBuffer), typeof<V3f>) ])
                            Sg.Index(BufferView(AVal.constant (ArrayBuffer wireIdx :> IBuffer), typeof<int>))
                            Sg.Render (AVal.constant wireIdx.Length)
                        })
                let planeSg =
                    sg {
                        Sg.Active (AVal.map2 (&&) notFullscreen isSelected)
                        Sg.View view
                        Sg.Proj proj
                        Sg.Shader { DefaultSurfaces.trafo; Shader.flatColor }
                        Sg.Uniform("FlatColor", AVal.constant (V4d(1.0, 0.9, 0.3, 0.25)))
                        Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
                        Sg.NoEvents
                        Sg.VertexAttributes(
                            HashMap.ofList [ string DefaultSemantic.Positions, BufferView(AVal.constant (ArrayBuffer planePos :> IBuffer), typeof<V3f>) ])
                        Sg.Index(BufferView(AVal.constant (ArrayBuffer planeIdx :> IBuffer), typeof<int>))
                        Sg.Render (AVal.constant planeIdx.Length)
                    }
                match prismSg with
                | Some w -> ASet.ofList [w; planeSg]
                | None   -> ASet.single planeSg
            )

        ASet.unionMany (ASet.ofList [ASet.single composite; fullscreenNodes; diskNodes; indicatorNodes; pinDots; pinPrisms])
