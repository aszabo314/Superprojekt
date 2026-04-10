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

        let cursorPosition = cval None
        let shiftHeld      = cval false
        let spaceHeld      = cval false
        let logVisible     = cval true
        let hoverCoord     = cval<V3d option> None
        let viewportSize   = cval (V2i(1, 1))

        let revolverActive   = AVal.map2 (||) (shiftHeld :> aval<_>) model.RevolverOn
        let fullscreenActive = AVal.map2 (||) (spaceHeld :> aval<_>) model.FullscreenOn
        let revolverBase =
            AVal.custom (fun t ->
                if (shiftHeld :> aval<_>).GetValue(t) then (cursorPosition :> aval<_>).GetValue(t)
                elif (model.RevolverOn :> aval<_>).GetValue(t) then Some ((model.RevolverCenter :> aval<_>).GetValue(t))
                else None
            )

        body {
            OnBoot [
                "const l = document.getElementById('loader');"
                "if(l) l.remove();"
                "document.body.classList.add('loaded');"
            ]

            Dom.OnMouseWheel((fun e ->
                if e.Shift || spaceHeld |> AVal.force then
                    env.Emit [ CycleMeshOrder (sign e.DeltaY) ]
                    false
                else
                    true
            ), true)

            renderControl {
                RenderControl.Samples 1
                Class "render-control"
                
                Dom.Style [
                    Css.Background "rgb(244, 246, 248)"
                ]
                (model.ScanPins.PlacingMode, model.ScanPins.ActivePlacement) ||> AVal.map2 (fun pm ap ->
                    if pm.IsSome || ap.IsSome then Some (Dom.Style [Css.Cursor "crosshair"]) else None
                )

                let! info = RenderControl.Info
                let! size = RenderControl.ViewportSize

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
                    let inPlacement = (AVal.force model.ScanPins.PlacingMode).IsSome
                    if inPlacement then
                        let camFwd = (AVal.force view).Forward.GetViewDirectionLH() |> Vec.normalize
                        env.Emit [ScanPinMsg (SetAnchor(worldPos, e.WorldPosition, V3d camFwd))]
                        false
                    elif e.Ctrl && e.Button = Button.Left then
                        transact (fun () -> hoverCoord.Value <- Some worldPos)
                        env.Emit [ClearFilteredMesh]
                        ServerActions.triggerFilter env model e.Position
                        false
                    else
                        transact (fun () -> hoverCoord.Value <- Some worldPos)
                        true
                )

                Sg.OnLongPress(fun e ->
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

                Dom.OnPointerMove(fun e ->
                    transact (fun () ->
                        let b = e.ClientRect
                        let tc = (V2d e.ClientPosition - b.Min) / b.Size
                        cursorPosition.Value <- Some (V2d(2.0 * tc.X - 1.0, 1.0 - 2.0 * tc.Y))
                        shiftHeld.Value <- e.Shift
                    )
                )

                Dom.OnPointerDown(fun e ->
                    if AVal.force model.RevolverOn && not (AVal.force shiftHeld) then
                        let b = e.ClientRect
                        let tc = (V2d e.ClientPosition - b.Min) / b.Size
                        let ndc = V2d(2.0 * tc.X - 1.0, 1.0 - 2.0 * tc.Y)
                        env.Emit [SetRevolverCenter ndc]
                )
                

                SceneGraph.build env info view proj revolverBase revolverActive fullscreenActive model
            }

            Dom.OnKeyDown(fun e ->
                match e.Key with
                | "Shift"  -> transact (fun () -> shiftHeld.Value <- true)
                | " "      -> transact (fun () -> spaceHeld.Value <- true)
                | "Escape" -> env.Emit [ScanPinMsg CancelPlacement]
                | _ -> ()
            )
            Dom.OnKeyUp(fun e ->
                match e.Key with
                | "Shift" -> transact (fun () -> shiftHeld.Value <- false)
                | " "     -> transact (fun () -> spaceHeld.Value <- false)
                | _ -> ()
            )

            Gui.burgerButton env
            Gui.hudTabs env model
            Cards.renderCards env model (model.Camera.view |> AVal.map CameraView.viewTrafo) (viewportSize :> aval<V2i>)
            Gui.fullscreenInfo model
            Gui.debugLogToggle logVisible
            Gui.debugLog (logVisible :> aval<bool>) model
            Gui.coordinateDisplay (hoverCoord :> aval<V3d option>)
        }


module App =
    let app =
        {
            initial   = Model.initial
            update    = Update.update
            view      = View.view
            unpersist = Unpersist.instance
        }
