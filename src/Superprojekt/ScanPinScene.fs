namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open Aardworx.WebAssembly
open FSharp.Data.Adaptive
open Aardvark.Dom

module ScanPinScene =

    // Pre-baked unit icosphere (subdiv=2 — 162 verts / 320 tris).
    let private spherePos, sphereIdx = PinGeometry.buildIcosphere 2

    // Compact "+"-shaped 3D marker that survives the off-screen depth gate
    // because we render it with DepthTest.LessOrEqual; large enough to click,
    // small enough not to dominate the anchor sphere visually.
    let private pinMarkerPos, pinMarkerIdx =
        let pos = System.Collections.Generic.List<V3f>()
        let idx = System.Collections.Generic.List<int>()
        let addBox (hx : float) (hy : float) (hz : float) =
            let base0 = pos.Count
            pos.Add (V3f(-hx, -hy, -hz)); pos.Add (V3f( hx, -hy, -hz))
            pos.Add (V3f( hx,  hy, -hz)); pos.Add (V3f(-hx,  hy, -hz))
            pos.Add (V3f(-hx, -hy,  hz)); pos.Add (V3f( hx, -hy,  hz))
            pos.Add (V3f( hx,  hy,  hz)); pos.Add (V3f(-hx,  hy,  hz))
            let offs = [| 0;1;2; 0;2;3;  5;4;7; 5;7;6;  4;0;3; 4;3;7
                          1;5;6; 1;6;2;  0;4;5; 0;5;1;  3;2;6; 3;6;7 |]
            for o in offs do idx.Add(base0 + o)
        addBox 0.18 0.025 0.025
        addBox 0.025 0.18 0.025
        addBox 0.025 0.025 0.18
        pos.ToArray(), idx.ToArray()

    let private spherePosBuf = AVal.constant (ArrayBuffer spherePos :> IBuffer)
    let private sphereIdxBuf = AVal.constant (ArrayBuffer sphereIdx :> IBuffer)
    let private sphereIdxCnt = AVal.constant sphereIdx.Length
    let private markerPosBuf = AVal.constant (ArrayBuffer pinMarkerPos :> IBuffer)
    let private markerIdxBuf = AVal.constant (ArrayBuffer pinMarkerIdx :> IBuffer)
    let private markerIdxCnt = AVal.constant pinMarkerIdx.Length

    /// V6 §D.6.3 — translucent volume at Radius + inner hard-edged sphere at
    /// Sigma. Both spheres render in passOne with `DepthTest.None` so the
    /// anchor reads through scene geometry; that matches the spec's
    /// "see the sphere even through walls" intent.
    let private sphereShell
            (view : aval<Trafo3d>) (proj : aval<Trafo3d>)
            (active : aval<bool>) (trafo : aval<Trafo3d>) (color : aval<V4d>) =
        sg {
            Sg.Active active
            Sg.View view
            Sg.Proj proj
            Sg.Trafo trafo
            Sg.Pass RenderPass.passOne
            Sg.Shader { DefaultSurfaces.trafo; Shader.flatColor }
            Sg.Uniform("FlatColor", color)
            Sg.BlendMode (AVal.constant BlendMode.Blend)
            Sg.DepthTest (AVal.constant DepthTest.None)
            Sg.NoEvents
            Sg.VertexAttributes(
                HashMap.ofList [ string DefaultSemantic.Positions, BufferView(spherePosBuf, typeof<V3f>) ])
            Sg.Index(BufferView(sphereIdxBuf, typeof<int>))
            Sg.Render sphereIdxCnt
        }

    let build
            (env : Env<Message>)
            (view : aval<Trafo3d>) (proj : aval<Trafo3d>)
            (fullscreenActive : aval<bool>)
            (placementHover : aval<V3d option>)
            (model : AdaptiveModel) =

        // World→render conversion for §D.7.2 polyline points and any other
        // payload that stores world-space coordinates per §C.3.
        let datasetScale =
            (model.ActiveDataset, model.DatasetScales) ||> AVal.map2 (fun ds map ->
                match ds with
                | Some d -> Map.tryFind d map |> Option.defaultValue 1.0
                | None -> 1.0)

        let notFullscreen = AVal.map not fullscreenActive
        let selectedId = model.ScanPins.SelectedPin
        let pinIdSet = model.ScanPins.Pins |> AMap.toASet |> ASet.map fst
        let pinsVal = model.ScanPins.Pins |> AMap.toAVal
        let placementActive =
            model.ScanPins.Placement |> AVal.map (function AnchorPlacement -> true | _ -> false)

        // The clickable centre marker for each anchor. Stays small and
        // depth-tested so it shows in front of the sphere shells.
        let pinDots =
            pinIdSet |> ASet.map (fun id ->
                let pinVal = pinsVal |> AVal.map (fun pins -> HashMap.tryFind id pins)
                let phaseVal = pinVal |> AVal.map (Option.map (fun p -> p.Phase))
                let centreVal = pinVal |> AVal.map (Option.map (fun p -> p.Centre))
                let color =
                    (selectedId, phaseVal) ||> AVal.map2 (fun sel phaseOpt ->
                        match phaseOpt with
                        | Some phase ->
                            if sel = Some id then V4d(1.0, 0.9, 0.0, 1.0)
                            elif phase = PinPhase.Placement then V4d(0.2, 1.0, 0.3, 1.0)
                            else V4d(1.0, 0.3, 0.3, 1.0)
                        | None -> V4d(0.0, 0.0, 0.0, 0.0))
                let trafo =
                    centreVal |> AVal.map (function
                        | Some c -> Trafo3d.Translation c
                        | None -> Trafo3d.Scale 0.0)
                sg {
                    Sg.Active notFullscreen
                    Sg.View view
                    Sg.Proj proj
                    Sg.Trafo trafo
                    Sg.Shader { DefaultSurfaces.trafo; Shader.flatColor }
                    Sg.Uniform("FlatColor", color)
                    Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
                    Sg.OnTap(fun _ ->
                        match AVal.force placementActive with
                        | true -> true   // pass through so renderControl's PlaceAnchor handler fires
                        | false ->
                            let sel = AVal.force selectedId
                            if sel = Some id then env.Emit [ScanPinMsg (SelectPin None)]
                            else env.Emit [ScanPinMsg (SelectPin (Some id))]
                            false)
                    Sg.OnDoubleTap(fun _ ->
                        env.Emit [ScanPinMsg (FocusPin id)]
                        false)
                    Sg.VertexAttributes(
                        HashMap.ofList [ string DefaultSemantic.Positions, BufferView(markerPosBuf, typeof<V3f>) ])
                    Sg.Index(BufferView(markerIdxBuf, typeof<int>))
                    Sg.Render markerIdxCnt
                }
            )

        // Per-anchor sphere shells (outer translucent + inner solid-ish).
        // Field-projected aVals so the geometry only rebuilds when the
        // relevant field actually changes (per the CLAUDE.md adaptive-perf
        // rule).
        let pinSpheres =
            pinIdSet |> ASet.collect (fun id ->
                let pinVal = pinsVal |> AVal.map (fun pins -> HashMap.tryFind id pins)
                let isSelected = selectedId |> AVal.map (fun sel -> sel = Some id)
                let active = (notFullscreen, isSelected) ||> AVal.map2 (&&)
                let centreVal = pinVal |> AVal.map (Option.map (fun p -> p.Centre))
                let radiusVal = pinVal |> AVal.map (Option.map (fun p -> p.Radius) >> Option.defaultValue 0.0)
                let sigmaVal  = pinVal |> AVal.map (Option.map (fun p -> p.Sigma)  >> Option.defaultValue 0.0)
                let phaseVal  = pinVal |> AVal.map (Option.map (fun p -> p.Phase))
                let outerTrafo =
                    (centreVal, radiusVal) ||> AVal.map2 (fun co r ->
                        match co with
                        | Some c -> Trafo3d.Scale r * Trafo3d.Translation c
                        | None -> Trafo3d.Scale 0.0)
                let innerTrafo =
                    (centreVal, sigmaVal) ||> AVal.map2 (fun co s ->
                        match co with
                        | Some c -> Trafo3d.Scale s * Trafo3d.Translation c
                        | None -> Trafo3d.Scale 0.0)
                // Selected pin pops in yellow; placing in green; committed in red.
                let baseColor =
                    (isSelected, phaseVal) ||> AVal.map2 (fun sel phaseOpt ->
                        if sel then V3d(1.0, 0.9, 0.0)
                        else
                            match phaseOpt with
                            | Some PinPhase.Placement -> V3d(0.2, 1.0, 0.3)
                            | Some PinPhase.Committed -> V3d(1.0, 0.5, 0.5)
                            | None -> V3d.Zero)
                let outerColor = baseColor |> AVal.map (fun c -> V4d(c.X, c.Y, c.Z, 0.10))
                let innerColor = baseColor |> AVal.map (fun c -> V4d(c.X, c.Y, c.Z, 0.30))
                let outlineSegs =
                    (centreVal, radiusVal, isSelected) |||> AVal.map3 (fun co r sel ->
                        if sel then
                            match co with
                            | Some c -> PinGeometry.buildSphereOutline c r (V4d(1.0, 1.0, 1.0, 0.55)) 1.0
                            | None -> [||]
                        else [||])
                ASet.ofList [
                    sphereShell view proj active outerTrafo outerColor
                    sphereShell view proj active innerTrafo innerColor
                    sg {
                        Sg.Active active
                        Sg.View view
                        Sg.Proj proj
                        Sg.BlendMode BlendMode.Blend
                        Sg.DepthTest (AVal.constant DepthTest.None)
                        Sg.Pass RenderPass.passOne
                        Lines.render outlineSegs
                    }
                ])

        // §D.7.2 — line-on-surface polyline. Renders as pixel-constant 3D
        // lines (Shader.Lines) so the trace stays legible at any camera
        // distance. Coloured from DatasetColors[HostMeshName], or yellow
        // when the pin is selected. Field-projected aVals keep the rebuild
        // cost minimal: only Points + isSelected toggle a rebuild.
        let pinLines =
            pinIdSet |> ASet.map (fun id ->
                let pinVal = pinsVal |> AVal.map (fun pins -> HashMap.tryFind id pins)
                let isSelected = selectedId |> AVal.map (fun sel -> sel = Some id)
                let active = (notFullscreen, isSelected) ||> AVal.map2 (&&)
                let pointsVal =
                    pinVal |> AVal.map (fun po ->
                        match po with
                        | Some p ->
                            match p.Payload with
                            | Line lp -> lp.Points
                            | _ -> [||]
                        | None -> [||])
                let colorVal =
                    pinVal |> AVal.map (fun po ->
                        match po with
                        | Some p ->
                            let baseC =
                                match p.HostMeshName with
                                | Some host ->
                                    Map.tryFind host p.DatasetColors
                                    |> Option.map (fun c ->
                                        V4d(float c.R / 255.0, float c.G / 255.0, float c.B / 255.0, 0.95))
                                    |> Option.defaultValue (V4d(0.1, 0.34, 0.86, 0.95))
                                | None -> V4d(0.1, 0.34, 0.86, 0.95)
                            baseC
                        | None -> V4d.Zero)
                let renderPoints =
                    (pointsVal, model.CommonCentroid, datasetScale)
                    |||> AVal.map3 (fun pts cc scale ->
                        if pts.Length = 0 then [||]
                        else pts |> Array.map (fun p -> (p - cc) * scale))
                let segs =
                    (renderPoints, colorVal, isSelected) |||> AVal.map3 (fun pts color sel ->
                        if pts.Length < 2 then [||]
                        else
                            let c = if sel then V4d(1.0, 0.9, 0.0, 0.98) else color
                            Array.init (pts.Length - 1) (fun i -> pts.[i], pts.[i + 1], c, 2.0))
                sg {
                    Sg.Active active
                    Sg.View view
                    Sg.Proj proj
                    Sg.BlendMode BlendMode.Blend
                    Sg.DepthTest (AVal.constant DepthTest.None)
                    Sg.Pass RenderPass.passOne
                    Lines.render segs
                })

        // §D.6.1 ghost preview: a faint translucent sphere at the cursor's
        // current mesh hit while AnchorPlacement is active. Radius mirrors
        // the default-radius rule (5 % of the dataset diagonal).
        let ghostPreview =
            let defaultR =
                model.ClipBounds |> AVal.map (fun b ->
                    if b.IsInvalid then 1.0
                    else max 0.1 (b.Size.Length * 0.05))
            let active =
                (notFullscreen, placementActive, placementHover) |||> AVal.map3 (fun nf pa hOpt ->
                    nf && pa && hOpt.IsSome)
            let trafo =
                (placementHover, defaultR) ||> AVal.map2 (fun hOpt r ->
                    match hOpt with
                    | Some c -> Trafo3d.Scale r * Trafo3d.Translation c
                    | None -> Trafo3d.Scale 0.0)
            let outlineSegs =
                (placementHover, defaultR) ||> AVal.map2 (fun hOpt r ->
                    match hOpt with
                    | Some c -> PinGeometry.buildSphereOutline c r (V4d(0.1, 0.34, 0.86, 0.85)) 1.5
                    | None -> [||])
            ASet.ofList [
                sphereShell view proj active trafo (AVal.constant (V4d(0.1, 0.34, 0.86, 0.18)))
                sg {
                    Sg.Active active
                    Sg.View view
                    Sg.Proj proj
                    Sg.BlendMode BlendMode.Blend
                    Sg.DepthTest (AVal.constant DepthTest.None)
                    Sg.Pass RenderPass.passOne
                    Lines.render outlineSegs
                }
            ]

        ASet.unionMany (ASet.ofList [pinDots; pinSpheres; pinLines; ghostPreview])
