namespace Superprojekt

open Aardvark.Base
open FSharp.Data.Adaptive
open Aardvark.Dom

module Gui =

    let burgerButton (env : Env<Message>) =
        button {
            Attribute("id", "burger-btn")
            Class "burger-btn"
            Dom.OnClick(fun _ -> env.Emit [ToggleMenu])
            div { Class "burger-line" }
            div { Class "burger-line" }
            div { Class "burger-line" }
        }

    let hudTabs (env : Env<Message>) (model : AdaptiveModel) =
        div {
            Class "tabs"
            model.MenuOpen |> AVal.map (fun o ->
                if o then Some (Class "tabs-open") else None
            )

            div {
                Class "tab-labels"
                label {
                    input {
                        Attribute("type",    "radio")
                        Attribute("name",    "hud-tabs")
                        Attribute("id",      "hud-tab1")
                        Attribute("checked", "checked")
                    }
                    "Scene"
                }
                label {
                    input {
                        Attribute("type", "radio")
                        Attribute("name", "hud-tabs")
                        Attribute("id",   "hud-tab2")
                    }
                    "Overlay"
                }
                label {
                    input {
                        Attribute("type", "radio")
                        Attribute("name", "hud-tabs")
                        Attribute("id",   "hud-tab3")
                    }
                    "Clip"
                }
            }

            div {
                Class "tab-panels"

                div {
                    Class "tab-panel"
                    Attribute("id", "hud-panel1")

                    h3 { "Meshes" }
                    model.MeshNames |> AList.map (fun name ->
                        let isVis =
                            model.MeshVisible
                            |> AVal.map (fun m -> Map.tryFind name m |> Option.defaultValue true)
                        div {
                            label {
                                input {
                                    Attribute("type", "checkbox")
                                    isVis |> AVal.map (fun v ->
                                        if v then Some (Attribute("checked", "checked")) else None
                                    )
                                    Dom.OnClick(fun _ ->
                                        let current = AVal.force isVis
                                        env.Emit [SetVisible(name, not current)]
                                    )
                                }
                                " " + name
                            }
                        }
                    )

                    button { "Clear Filter"; Dom.OnClick(fun _ -> env.Emit [ClearFilteredMesh]) }

                    ul {
                        li { "ctrl+click or long-press to filter" }
                        li { "double-click to focus camera" }
                        li { "shift+scroll to cycle mesh order" }
                    }
                }

                div {
                    Class "tab-panel"
                    Attribute("id", "hud-panel2")

                    h3 { "Revolver" }
                    p { "Magnifier disks per mesh (also: hold Shift)" }
                    button {
                        model.RevolverOn |> AVal.map (fun on ->
                            if on then Some (Class "btn-active") else None
                        )
                        Dom.OnClick(fun _ -> env.Emit [ToggleRevolver])
                        model.RevolverOn |> AVal.map (fun on ->
                            if on then "Revolver: ON" else "Revolver: OFF"
                        )
                    }
                    p {
                        model.RevolverOn |> AVal.map (fun on ->
                            if on then "Tap on the 3D view to reposition the magnifier." else ""
                        )
                    }

                    h3 { "Fullscreen overlay" }
                    p { "Show each mesh in a tile (also: hold Space)" }
                    button {
                        model.FullscreenOn |> AVal.map (fun on ->
                            if on then Some (Class "btn-active") else None
                        )
                        Dom.OnClick(fun _ -> env.Emit [ToggleFullscreen])
                        model.FullscreenOn |> AVal.map (fun on ->
                            if on then "Fullscreen: ON" else "Fullscreen: OFF"
                        )
                    }

                    h3 { "Difference rendering" }
                    div {
                        label {
                            input {
                                Attribute("type", "checkbox")
                                model.DifferenceRendering |> AVal.map (fun on ->
                                    if on then Some (Attribute("checked", "checked")) else None
                                )
                                Dom.OnClick(fun _ -> env.Emit [ToggleDifferenceRendering])
                            }
                            " Show pixel-wise difference between meshes"
                        }
                    }

                    h3 { "Mesh order" }
                    p { "Determines stacking order of revolver and fullscreen tiles." }
                    div {
                        Class "btn-row"
                        button { "◀ Prev"; Dom.OnClick(fun _ -> env.Emit [CycleMeshOrder -1]) }
                        button { "Next ▶"; Dom.OnClick(fun _ -> env.Emit [CycleMeshOrder  1]) }
                    }
                    model.MeshNames |> AList.map (fun name ->
                        let order = model.MeshOrder |> AMap.tryFind name |> AVal.map (Option.defaultValue 0)
                        div {
                            order |> AVal.map (fun o -> sprintf "%d  %s" (o + 1) name)
                        }
                    )
                }

                div {
                    Class "tab-panel"
                    Attribute("id", "hud-panel3")

                    h3 { "Workspace Clip" }

                    let slider (getValue : Box3d -> float)
                               (setValue : Box3d -> float -> Box3d)
                               (bMin : Box3d -> float) (bMax : Box3d -> float) =
                        div {
                            input {
                                Attribute("type", "range")
                                Attribute("step", "any")
                                model.ClipBounds |> AVal.map (fun b ->
                                    if b.IsInvalid then Some (Attribute("disabled", "disabled")) else None)
                                model.ClipBounds |> AVal.map (fun b ->
                                    if b.IsInvalid then None else Some (Attribute("min", sprintf "%.6g" (bMin b))))
                                model.ClipBounds |> AVal.map (fun b ->
                                    if b.IsInvalid then None else Some (Attribute("max", sprintf "%.6g" (bMax b))))
                                model.ClipBox |> AVal.map (fun b ->
                                    Some (Attribute("value", sprintf "%.6g" (getValue b))))
                                Style [Width "100%"]
                                Dom.OnInput(fun e ->
                                    match System.Double.TryParse(e.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture) with
                                    | true, v -> env.Emit [SetClipBox(setValue (AVal.force model.ClipBox) v)]
                                    | _ -> ()
                                )
                            }
                            model.ClipBox |> AVal.map (fun b -> sprintf "%.2f" (getValue b))
                        }

                    p { "X min" }
                    slider (fun b -> b.Min.X)
                           (fun box v -> Box3d(V3d(min v box.Max.X, box.Min.Y, box.Min.Z), box.Max))
                           (fun b -> b.Min.X) (fun b -> b.Max.X)
                    p { "X max" }
                    slider (fun b -> b.Max.X)
                           (fun box v -> Box3d(box.Min, V3d(max v box.Min.X, box.Max.Y, box.Max.Z)))
                           (fun b -> b.Min.X) (fun b -> b.Max.X)

                    p { "Y min" }
                    slider (fun b -> b.Min.Y)
                           (fun box v -> Box3d(V3d(box.Min.X, min v box.Max.Y, box.Min.Z), box.Max))
                           (fun b -> b.Min.Y) (fun b -> b.Max.Y)
                    p { "Y max" }
                    slider (fun b -> b.Max.Y)
                           (fun box v -> Box3d(box.Min, V3d(box.Max.X, max v box.Min.Y, box.Max.Z)))
                           (fun b -> b.Min.Y) (fun b -> b.Max.Y)

                    p { "Z min" }
                    slider (fun b -> b.Min.Z)
                           (fun box v -> Box3d(V3d(box.Min.X, box.Min.Y, min v box.Max.Z), box.Max))
                           (fun b -> b.Min.Z) (fun b -> b.Max.Z)
                    p { "Z max" }
                    slider (fun b -> b.Max.Z)
                           (fun box v -> Box3d(box.Min, V3d(box.Max.X, box.Max.Y, max v box.Min.Z)))
                           (fun b -> b.Min.Z) (fun b -> b.Max.Z)

                    button { "Reset to bounds"; Dom.OnClick(fun _ -> env.Emit [ResetClip]) }
                }
            }
        }

    let debugLog (model : AdaptiveModel) =
        div {
            Style [
                Position "fixed"; Bottom "0"; Left "0"; Right "0"
                MaxHeight "30vh"; OverflowY "auto"
                Background "rgba(0,0,0,0.8)"; Color "#0f0"
                FontFamily "monospace"; FontSize "11px"
                Padding "4px 8px"; PointerEvents "none"
                ZIndex 9999; StyleProperty("white-space", "pre-wrap")
            ]
            model.DebugLog |> AList.map (fun line -> div { line })
        }
