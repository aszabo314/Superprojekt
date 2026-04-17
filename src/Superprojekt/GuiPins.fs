namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open FSharp.Data.Adaptive
open Aardvark.Dom

module GuiPins =

    let shortName = Cards.shortName

    let pinsTabPanel (env : Env<Message>) (model : AdaptiveModel) =
        let sp = model.ScanPins
        let isPlacing = (sp.PlacingMode, sp.ActivePlacement) ||> AVal.map2 (fun pm ap -> pm.IsSome || ap.IsSome)

        div {
            Class "tab-panel"
            Attribute("id", "hud-panel4")

            h3 { "ScanPins" }

            div {
                Class "btn-row"
                button {
                    isPlacing |> AVal.map (fun on ->
                        if on then Some (Class "btn-active") else None)
                    Dom.OnClick(fun _ ->
                        let placing = AVal.force isPlacing
                        if placing then env.Emit [ScanPinMsg CancelPlacement]
                        else env.Emit [ScanPinMsg (StartPlacement FootprintMode.Circle)]
                    )
                    isPlacing |> AVal.map (fun on ->
                        if on then "Cancel" else "Place pin")
                }
            }
            p {
                sp.PlacingMode |> AVal.map (fun pm ->
                    if pm.IsSome then "Tap on the 3D view to place anchor." else "")
            }

            let activePin =
                (sp.ActivePlacement, sp.Pins |> AMap.toAVal) ||> AVal.map2 (fun id pins ->
                    id |> Option.bind (fun i -> HashMap.tryFind i pins))
            div {
                activePin |> AVal.map (fun p ->
                    if p.IsNone then Some (Style [Display "none"]) else None)

                div {
                    "Radius  "
                    input {
                        Attribute("type", "range")
                        Attribute("min", "0.1"); Attribute("max", "20"); Attribute("step", "0.1")
                        Class "range-full"
                        activePin |> AVal.map (fun p ->
                            match p with
                            | Some pin ->
                                match pin.Prism.Footprint.Vertices with
                                | v :: _ -> Some (Attribute("value", sprintf "%.1f" v.Length))
                                | _ -> Some (Attribute("value", "1"))
                            | None -> None)
                        Dom.OnInput(fun e ->
                            Cards.parseFloat e.Value |> Option.iter (fun v -> env.Emit [ScanPinMsg (SetFootprintRadius v)]))
                    }
                    span {
                        Class "param-readout"
                        activePin |> AVal.map (fun p ->
                            match p with
                            | Some pin ->
                                match pin.Prism.Footprint.Vertices with
                                | v :: _ -> sprintf "%.2f m" v.Length
                                | _ -> "1.00 m"
                            | None -> "")
                    }
                }
                div {
                    "Length  "
                    input {
                        Attribute("type", "range")
                        Attribute("min", "0.5"); Attribute("max", "100"); Attribute("step", "0.5")
                        Class "range-full"
                        activePin |> AVal.map (fun p ->
                            match p with
                            | Some pin -> Some (Attribute("value", sprintf "%.1f" pin.Prism.ExtentBackward))
                            | None -> None)
                        Dom.OnInput(fun e ->
                            Cards.parseFloat e.Value |> Option.iter (fun v -> env.Emit [ScanPinMsg (SetPinLength v)]))
                    }
                    span {
                        Class "param-readout"
                        activePin |> AVal.map (fun p ->
                            match p with
                            | Some pin -> sprintf "%.2f m" pin.Prism.ExtentBackward
                            | None -> "")
                    }
                }
                div {
                    Class "btn-row mt-6"
                    button {
                        Class "btn-active"
                        Dom.OnClick(fun _ -> env.Emit [ScanPinMsg CommitPin])
                        "Commit"
                    }
                    button {
                        Dom.OnClick(fun _ -> env.Emit [ScanPinMsg CancelPlacement])
                        "Discard"
                    }
                }
            }

            h3 { "Pins" }
            let guiPinsVal = sp.Pins |> AMap.toAVal
            let pinIdList =
                guiPinsVal
                |> AVal.map (fun pins -> pins |> HashMap.toSeq |> Seq.map fst |> Seq.sort |> IndexList.ofSeq)
                |> AList.ofAVal
            pinIdList |> AList.map (fun id ->
                let pinVal = guiPinsVal |> AVal.map (fun pins -> HashMap.tryFind id pins)
                let isSelected = sp.SelectedPin |> AVal.map (fun sel -> sel = Some id)
                div {
                    Class "pin-item"
                    isSelected |> AVal.map (fun s ->
                        if s then Some (Class "pin-item-selected") else None)
                    div {
                        Dom.OnClick(fun _ ->
                            let sel = AVal.force sp.SelectedPin
                            if sel = Some id then env.Emit [ScanPinMsg (SelectPin None)]
                            else env.Emit [ScanPinMsg (SelectPin (Some id))])
                        Class "pin-item-label"
                        pinVal |> AVal.map (fun pinOpt ->
                            match pinOpt with
                            | Some pin ->
                                let p = pin.Prism.AnchorPoint
                                let phase = if pin.Phase = PinPhase.Placement then " [placing]" else ""
                                sprintf "(%.1f, %.1f, %.1f)%s" p.X p.Y p.Z phase
                            | None -> "(removed)")
                    }
                    div {
                        Class "btn-row mt-4"
                        button {
                            Class "btn-sm"
                            Dom.OnClick(fun _ -> env.Emit [ScanPinMsg (FocusPin id)])
                            "Focus"
                        }
                        button {
                            Class "btn-sm"
                            Dom.OnClick(fun _ -> env.Emit [ScanPinMsg (DeletePin id)])
                            "Delete"
                        }
                    }
                }
            )
        }
