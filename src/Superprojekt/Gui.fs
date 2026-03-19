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
