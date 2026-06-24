namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open FSharp.Data.Adaptive
open Aardvark.Dom

// Right focus panel (spec §1/§5): the single secondary WebGL control. Renders
// the scene orthographically (Top | Front | Side, one at a time) with the
// selected moving mesh solid and everything else ghosted (§9 align-auto, reused
// from the layer-isolation path). Pointer drag translates the moving mesh in
// the two in-plane axes — translate only, no rotation. This is the second of
// the at-most-two live WebGL controls.
module GuiFocus =

    open Primitives

    let private datasetScaleA (model : AdaptiveModel) =
        (model.ActiveDataset, model.DatasetScales) ||> AVal.map2 DatasetScale.active

    // Render-space scene box (world MeshBounds → render via centroid + scale),
    // so the ortho frame matches the meshes the main control draws.
    let private renderSceneBox (model : AdaptiveModel) =
        (model.MeshBounds, model.CommonCentroid, datasetScaleA model)
        |||> AVal.map3 (fun mb cc scale ->
            let mutable b = Box3d.Invalid
            for KeyValue(_, (wb : Box3d)) in mb do
                b.ExtendBy((wb.Min - cc) * scale)
                b.ExtendBy((wb.Max - cc) * scale)
            if b.IsValid then b else Box3d(V3d(-10.0, -10.0, -10.0), V3d(10.0, 10.0, 10.0)))

    // Screen right / up axes (render space) for each ortho view.
    let private screenAxes = function
        | AxisTop   -> V3d.IOO, V3d.OIO   // look -Z: right +X, up +Y
        | AxisFront -> V3d.IOO, V3d.OOI   // look +Y: right +X, up +Z
        | AxisSide  -> V3d.OIO, V3d.OOI   // look -X: right +Y, up +Z

    let panel (env : Env<Message>) (model : AdaptiveModel) =
        let refMesh = model.Registration |> AVal.map (fun r -> r.ReferenceMesh)
        let sceneBox = renderSceneBox model
        let halfExtent =
            sceneBox |> AVal.map (fun (b : Box3d) ->
                let s = b.Size
                max 1.0 ((max s.X (max s.Y s.Z)) * 0.62))

        let view =
            (model.FocusAxis, sceneBox) ||> AVal.map2 (fun axis (b : Box3d) ->
                let c = b.Center
                let r = max 1.0 (b.Size.Length)
                let eye, up =
                    match axis with
                    | AxisTop   -> c + V3d(0.0, 0.0, r), V3d.OIO
                    | AxisFront -> c + V3d(0.0, -r, 0.0), V3d.OOI
                    | AxisSide  -> c + V3d(r, 0.0, 0.0), V3d.OOI
                CameraView.lookAt eye c up |> CameraView.viewTrafo)
        let proj =
            (halfExtent, sceneBox) ||> AVal.map2 (fun half (b : Box3d) ->
                let f = 2.0 * b.Size.Length + 100.0
                let fr : Frustum =
                    { left = -half; right = half; bottom = -half; top = half
                      near = 0.1; far = f; isOrtho = true }
                Frustum.projTrafo fr)

        let meshScene =
            MeshView.buildScene
                (fun _ -> ())
                (AVal.constant None)
                (AVal.constant (0, V4f.Zero, V4f.Zero))
                (AVal.constant false)
                model.AlignMesh
                model

        let lastPos : cval<V2d option> = cval None

        let rc =
            renderControl {
                RenderControl.Samples 1
                Class "focus-rc"
                let! client = RenderControl.ClientSize

                Sg.View view
                Sg.Proj proj
                Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
                Sg.BlendMode (AVal.constant BlendMode.Blend)

                Dom.OnPointerDown((fun e ->
                    if e.Button = Button.Left then
                        transact (fun () -> lastPos.Value <- Some (V2d(float e.OffsetPosition.X, float e.OffsetPosition.Y)))),
                    pointerCapture = true)
                Dom.OnPointerUp((fun _ ->
                    if lastPos.Value.IsSome then transact (fun () -> lastPos.Value <- None)),
                    pointerCapture = true)
                Dom.OnPointerMove(fun e ->
                    match lastPos.Value with
                    | Some prev ->
                        let cur = V2d(float e.OffsetPosition.X, float e.OffsetPosition.Y)
                        let d = cur - prev
                        transact (fun () -> lastPos.Value <- Some cur)
                        let h = max 1.0 (float (AVal.force client).Y)
                        let u = 2.0 * AVal.force halfExtent / h
                        let right, upv = screenAxes (AVal.force model.FocusAxis)
                        let delta = right * (d.X * u) - upv * (d.Y * u)
                        if delta.Length > 1e-9 then env.Emit [TranslateAlignMesh delta]
                    | None -> ())

                meshScene
            }

        let rcMount =
            model.FocusOpen
            |> AVal.map (fun o -> if o then IndexList.single rc else IndexList.empty)
            |> AList.ofAVal

        // Moving-mesh selector: visible, non-reference meshes.
        let movingMeshes =
            (model.MeshNames |> AList.toAVal, refMesh, model.MeshVisible)
            |||> AVal.map3 (fun names rm vis ->
                names |> IndexList.toList
                |> List.filter (fun n -> Some n <> rm && (Map.tryFind n vis |> Option.defaultValue true))
                |> IndexList.ofList)
            |> AList.ofAVal

        let axisBtn (axis : FocusAxis) =
            button {
                Class "focus-axis-btn"
                model.FocusAxis |> AVal.map (fun a -> if a = axis then Some (Class "btn-active") else None)
                Dom.OnClick(fun _ -> env.Emit [SetFocusAxis axis])
                FocusAxis.label axis
            }

        div {
            Class "focus-panel"
            showWhen model.FocusOpen
            div {
                Class "focus-head"
                span { Class "focus-title"; "Focus · orthographic" }
                button {
                    Class "focus-close"
                    Attribute("title", "Hide focus panel")
                    Dom.OnClick(fun _ -> env.Emit [ToggleFocusPanel])
                    "×"
                }
            }
            div {
                Class "focus-toolbar"
                div { Class "focus-axes"; axisBtn AxisTop; axisBtn AxisFront; axisBtn AxisSide }
            }
            div {
                Class "focus-moving"
                span { Class "focus-sublabel"; "Move:" }
                movingMeshes |> AList.map (fun name ->
                    let sel = model.AlignMesh |> AVal.map ((=) (Some name))
                    let idxVal = model.MeshOrder |> AMap.tryFind name |> AVal.map (Option.defaultValue 0)
                    button {
                        Class "focus-moving-btn"
                        sel |> AVal.map (fun s -> if s then Some (Class "btn-active") else None)
                        Dom.OnClick(fun _ ->
                            let cur = AVal.force model.AlignMesh
                            env.Emit [SetAlignMesh (if cur = Some name then None else Some name)])
                        idxVal |> AVal.map (fun i -> sprintf "%d" (i + 1))
                    })
            }
            div { Class "focus-view"; rcMount }
            div { Class "focus-hint"; "Drag to translate the selected mesh in the view plane (translate only)." }
        }
