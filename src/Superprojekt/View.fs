namespace Superprojekt

open Aardvark.Base
open Aardvark.Dom.Utilities.OrbitController
open Aardvark.Rendering
open FSharp.Data.Adaptive
open Aardvark.Dom
open Adaptify
open Superprojekt

module View =

    let view (env : Env<Message>) (model : AdaptiveModel) =

        ServerActions.init env

        let spaceHeld      = cval false
        let hoverCoord     = cval<V3d option> None
        let viewportSize   = cval (V2i(1, 1))

        let fullscreenActive = AVal.map2 (||) (spaceHeld :> aval<_>) model.FullscreenOn

        body {
            OnBoot [
                "const l = document.getElementById('loader');"
                "if(l) l.remove();"
                "document.body.classList.add('loaded');"
            ]


            renderControl {
                RenderControl.Samples 1
                Class "render-control"

                Dom.Style [
                    Css.Background "rgb(244, 246, 248)"
                ]

                let! info = RenderControl.Info
                let! size = RenderControl.ViewportSize

                let mutable eHandler = None

                RenderControl.OnReady (fun e ->
                    eHandler <- Some e
                    ()
                )

                OrbitController.getAttributes (Env.map CameraMessage env)

                let mutable initial = true
                RenderControl.OnRendered(fun _ ->
                    if initial then
                        initial <- false
                    let s = AVal.force size
                    if viewportSize.Value <> s then
                        transact (fun () -> viewportSize.Value <- s)
                    env.Emit [CameraMessage OrbitMessage.Rendered]
                )

                let view = model.Camera.view |> AVal.map CameraView.viewTrafo
                let proj =
                    size |> AVal.map (fun s ->
                        Frustum.perspective 90.0 1.0 5000.0 (float s.X / float s.Y) |> Frustum.projTrafo
                    )

                Sg.View view
                Sg.Proj proj

                Sg.Pass RenderPass.passZero

                Sg.OnDoubleTap(fun e ->
                    if e.Location.Depth < 0.9999 then
                        env.Emit [CameraMessage (OrbitMessage.SetTargetCenter(true, AnimationKind.Tanh, e.WorldPosition))]
                    false
                )

                Sg.OnTap(fun e ->
                    let scale =
                        AVal.force model.ActiveDataset
                        |> Option.bind (fun ds -> Map.tryFind ds (AVal.force model.DatasetScales))
                        |> Option.defaultValue 1.0
                    let cc = AVal.force model.CommonCentroid
                    let worldPos = e.WorldPosition / scale + cc
                    let hitGeometry = e.Location.Depth < 0.9999
                    if e.Ctrl && e.Button = Button.Left && hitGeometry then
                        transact (fun () -> hoverCoord.Value <- Some worldPos)
                        env.Emit [ClearFilteredMesh]
                        ServerActions.triggerFilter env model e.Position
                        false
                    else
                        transact (fun () -> hoverCoord.Value <- Some worldPos)
                        true
                )

                Sg.OnLongPress(fun e ->
                    if e.Location.Depth < 0.9999 then
                        let scale =
                            AVal.force model.ActiveDataset
                            |> Option.bind (fun ds -> Map.tryFind ds (AVal.force model.DatasetScales))
                            |> Option.defaultValue 1.0
                        let cc = AVal.force model.CommonCentroid
                        transact (fun () -> hoverCoord.Value <- Some (e.WorldPosition / scale + cc))
                        env.Emit [ClearFilteredMesh]
                        ServerActions.triggerFilter env model e.Position
                    false
                )

                Sg.OnPointerMove(fun e ->
                    let scale =
                        AVal.force model.ActiveDataset
                        |> Option.bind (fun ds -> Map.tryFind ds (AVal.force model.DatasetScales))
                        |> Option.defaultValue 1.0
                    let cc = AVal.force model.CommonCentroid
                    transact (fun () -> hoverCoord.Value <- Some (e.WorldPosition / scale + cc))
                    true
                )

                SceneGraph.build env info view proj fullscreenActive model
            }

            Dom.OnKeyDown(fun e ->
                match e.Key with
                | " "      -> transact (fun () -> spaceHeld.Value <- true)
                | "Escape" -> env.Emit [ScanPinMsg CancelPlacement]
                | _ -> ()
            )
            Dom.OnKeyUp(fun e ->
                match e.Key with
                | " "     -> transact (fun () -> spaceHeld.Value <- false)
                | _ -> ()
            )

            Gui.topBar env model (hoverCoord :> aval<V3d option>)
            Gui.leftPanel env model
            Gui.placementFlyout env model
            Gui.exploreCard env model
            Cards.renderCards env model (model.Camera.view |> AVal.map CameraView.viewTrafo) (viewportSize :> aval<V2i>)
            Gui.fullscreenInfo model
            Gui.scaleBar model (viewportSize :> aval<V2i>)
            Gui.orientationIndicator model
        }


module App =
    let app =
        {
            initial   = Model.initial
            update    = Update.update
            view      = View.view
            unpersist = Unpersist.instance
        }
