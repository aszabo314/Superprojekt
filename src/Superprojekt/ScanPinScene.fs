namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open Aardworx.WebAssembly
open FSharp.Data.Adaptive
open Aardvark.Dom

module ScanPinScene =

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
        addBox 0.03 0.03 0.5
        addBox 0.18 0.025 0.025
        addBox 0.025 0.18 0.025
        pos.ToArray(), idx.ToArray()

    let build
            (env : Env<Message>)
            (view : aval<Trafo3d>) (proj : aval<Trafo3d>)
            (fullscreenActive : aval<bool>)
            (model : AdaptiveModel) =

        let notFullscreen = AVal.map not fullscreenActive
        let selectedId = model.ScanPins.SelectedPin
        let pinIdSet = model.ScanPins.Pins |> AMap.toASet |> ASet.map fst
        let pinsVal = model.ScanPins.Pins |> AMap.toAVal

        let pinDots =
            pinIdSet |> ASet.map (fun id ->
                let pinVal = pinsVal |> AVal.map (fun pins -> HashMap.tryFind id pins)
                let phaseVal = pinVal |> AVal.map (Option.map (fun p -> p.Phase))
                let anchorVal = pinVal |> AVal.map (Option.map (fun p -> p.Prism.AnchorPoint))
                let axisVal = pinVal |> AVal.map (Option.map (fun p -> p.Prism.AxisDirection))
                let color =
                    (selectedId, phaseVal) ||> AVal.map2 (fun sel phaseOpt ->
                        match phaseOpt with
                        | Some phase ->
                            if sel = Some id then V4d(1.0, 0.9, 0.0, 1.0)
                            elif phase = PinPhase.Placement then V4d(0.2, 1.0, 0.3, 1.0)
                            else V4d(1.0, 0.3, 0.3, 1.0)
                        | None -> V4d(0.0, 0.0, 0.0, 0.0))
                let trafo =
                    (anchorVal, axisVal) ||> AVal.map2 (fun aOpt xOpt ->
                        match aOpt, xOpt with
                        | Some a, Some axis ->
                            let axis = Vec.normalize axis
                            let right, fwd = PinGeometry.axisFrame axis
                            let rotM =
                                M44d(right.X, fwd.X, axis.X, 0.0,
                                     right.Y, fwd.Y, axis.Y, 0.0,
                                     right.Z, fwd.Z, axis.Z, 0.0,
                                     0.0,     0.0,   0.0,    1.0)
                            Trafo3d(rotM, rotM.Transposed) * Trafo3d.Translation(a)
                        | _ -> Trafo3d.Scale(0.0))
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
                        HashMap.ofList [ string DefaultSemantic.Positions, BufferView(AVal.constant (ArrayBuffer pinMarkerPos :> IBuffer), typeof<V3f>) ]
                    )
                    Sg.Index(BufferView(AVal.constant (ArrayBuffer pinMarkerIdx :> IBuffer), typeof<int>))
                    Sg.Render (AVal.constant pinMarkerIdx.Length)
                }
            )

        let adjustingHull =
            let activeId =
                model.ScanPins.Placement |> AVal.map (function
                    | AdjustingPin id -> Some id
                    | _ -> None)
            let editedPin =
                (selectedId, activeId, pinsVal) |||> AVal.map3 (fun sel act pins ->
                    let id = act |> Option.orElse sel
                    id |> Option.bind (fun id -> HashMap.tryFind id pins))

            let editedPrism = editedPin |> AVal.map (Option.map (fun p -> p.Prism))

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

            let camPos = view |> AVal.map (fun v -> v.Backward.TransformPos(V3d.Zero))
            let outlineSegs =
                (editedPrism, camPos) ||> AVal.map2 (fun prismOpt cp ->
                    match prismOpt with
                    | Some prism ->
                        PinGeometry.buildCylinderOutline prism cp
                            (V4d(1.0, 1.0, 1.0, 0.55)) (V4d(1.0, 1.0, 1.0, 0.85))
                    | None -> [||])

            ASet.ofList [
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
                sg {
                    Sg.Active hullActive
                    Sg.View view
                    Sg.Proj proj
                    Sg.BlendMode BlendMode.Blend
                    Sg.DepthTest (AVal.constant DepthTest.None)
                    Sg.Pass RenderPass.passOne
                    Lines.render outlineSegs
                }
            ]

        ASet.unionMany (ASet.ofList [pinDots; adjustingHull])
