namespace PinDemo

open Aardvark.Base
open Aardvark.Rendering
open Aardvark.SceneGraph
open FSharp.Data.Adaptive
open Aardvark.Dom
open Superprojekt

module PanelView =

    let private c4bToHex (c : C4b) =
        sprintf "#%02x%02x%02x" c.R c.G c.B

    let private shortName (name : string) =
        let s = name.IndexOf('/')
        if s >= 0 then name.[s + 1 ..] else name

    let private profileSection () =
        div {
            Class "panel-section"
            h3 { "Elevation profile" }
            div {
                Attribute("id", "pin-diagram-root")
                Attribute("data-diagram", SvgDiagram.encodeDiagramJson PanelState.pin)
                OnBoot SvgDiagram.bootJs
            }
        }

    let private viewModeButtons () =
        div {
            Class "btn-row"
            button {
                PanelState.coreViewMode |> AVal.map (fun m ->
                    if m = SideView then Some (Class "btn-active") else None)
                Dom.OnClick(fun _ ->
                    transact (fun () -> PanelState.coreViewMode.Value <- SideView))
                "Side"
            }
            button {
                PanelState.coreViewMode |> AVal.map (fun m ->
                    if m = TopView then Some (Class "btn-active") else None)
                Dom.OnClick(fun _ ->
                    transact (fun () -> PanelState.coreViewMode.Value <- TopView))
                "Top"
            }
        }

    let private aggregationButtons () =
        div {
            Class "btn-row"
            for (mode, label) in [Average, "Avg"; Q1, "Q1"; Q3, "Q3"; Difference, "Diff"] do
                button {
                    PanelState.aggregationMode |> AVal.map (fun m ->
                        if m = mode then Some (Class "btn-active") else None)
                    Dom.OnClick(fun _ ->
                        transact (fun () -> PanelState.aggregationMode.Value <- mode))
                    label
                }
        }

    let private coreSampleSection () =
        div {
            Class "panel-section"
            h3 { "Core sample" }
            viewModeButtons ()
            div {
                Class "pin-mini-wrapper"
                CoreSampleView.render ()
            }
            div {
                Class "effect-toggles"
                label {
                    input {
                        Attribute("type", "checkbox")
                        Attribute("checked", "checked")
                        Dom.OnChange(fun _ ->
                            transact (fun () -> PanelState.depthShadeOn.Value <- not PanelState.depthShadeOn.Value))
                    }
                    "Depth"
                }
                label {
                    input {
                        Attribute("type", "checkbox")
                        Attribute("checked", "checked")
                        Dom.OnChange(fun _ ->
                            transact (fun () -> PanelState.isolinesOn.Value <- not PanelState.isolinesOn.Value))
                    }
                    "Isolines"
                }
                label {
                    input {
                        Attribute("type", "checkbox")
                        Dom.OnChange(fun _ ->
                            transact (fun () -> PanelState.colorMode.Value <- not PanelState.colorMode.Value))
                    }
                    "Color"
                }
            }
            div {
                Style [MarginTop "8px"; FontSize "12px"; Color "#475569"]
                "Cut plane"
            }
            div {
                input {
                    Attribute("type", "range")
                    Attribute("min", "-10"); Attribute("max", "10"); Attribute("step", "0.1")
                    Style [Width "100%"]
                    PanelState.cutMode |> AVal.map (fun cm ->
                        let v = match cm with CutPlaneMode.AcrossAxis d -> d | _ -> 0.0
                        Some (Attribute("value", sprintf "%.2f" v)))
                    Dom.OnInput(fun e ->
                        match System.Double.TryParse(e.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture) with
                        | true, v -> transact (fun () -> PanelState.cutMode.Value <- CutPlaneMode.AcrossAxis v)
                        | _ -> ())
                }
            }
        }

    let private datasetByName =
        PanelState.datasets
        |> Array.map (fun d -> d.MeshName, d)
        |> Map.ofArray

    let private rankingRow (d : DummyDataset) =
        let isHidden = PanelState.datasetHidden |> ASet.contains d.MeshName
        let rank     = PanelState.rankOf d.MeshName
        let inK      = PanelState.inTopK d.MeshName
        div {
            (isHidden, inK) ||> AVal.map2 (fun h k ->
                let cls =
                    if h then "rank-row hidden"
                    elif not k then "rank-row out-of-topk"
                    else "rank-row"
                Some (Class cls))
            div {
                Class "rank-badge"
                rank |> AVal.map (fun r ->
                    match r with
                    | Some i -> sprintf "#%d" (i + 1)
                    | None -> "—")
            }
            div {
                Class "rank-color"
                Style [Background (c4bToHex d.Color)]
            }
            div {
                Class "rank-name"
                shortName d.MeshName
            }
            div {
                Class "rank-variance"
                sprintf "σ=%.1f" (sqrt d.Stats.ZVariance)
            }
            div {
                Class "rank-buttons"
                button {
                    Class "rank-move"
                    Dom.OnClick(fun _ -> PanelState.moveDataset d.MeshName -1)
                    "▲"
                }
                button {
                    Class "rank-move"
                    Dom.OnClick(fun _ -> PanelState.moveDataset d.MeshName 1)
                    "▼"
                }
                button {
                    Class "rank-toggle"
                    Dom.OnClick(fun _ ->
                        let h = AVal.force isHidden
                        transact (fun () ->
                            if h then PanelState.datasetHidden.Remove d.MeshName |> ignore
                            else PanelState.datasetHidden.Add d.MeshName |> ignore))
                    isHidden |> AVal.map (fun h -> if h then "Show" else "Hide")
                }
            }
        }

    let private visibleCount () =
        (PanelState.datasetOrder, PanelState.datasetHidden :> aset<_> |> ASet.toAVal, PanelState.topK)
        |||> AVal.map3 (fun order hidden k ->
            let total = order.Length
            let visible =
                order
                |> List.filter (fun n -> not (HashSet.contains n hidden))
                |> List.truncate k
                |> List.length
            sprintf "Rendering top %d of %d datasets" visible total)

    let private topKControls () =
        div {
            Class "rank-controls"
            label {
                "Top K "
                input {
                    Attribute("type", "number")
                    Attribute("min", "1")
                    Attribute("max", string PanelState.datasets.Length)
                    Attribute("step", "1")
                    Style [Width "48px"]
                    PanelState.topK |> AVal.map (fun k -> Some (Attribute("value", string k)))
                    Dom.OnInput(fun e ->
                        match System.Int32.TryParse(e.Value) with
                        | true, k when k >= 1 -> transact (fun () -> PanelState.topK.Value <- k)
                        | _ -> ())
                }
            }
            label {
                input {
                    Attribute("type", "checkbox")
                    PanelState.rankFadeOn |> AVal.map (fun on ->
                        if on then Some (Attribute("checked", "checked")) else None)
                    Dom.OnChange(fun _ ->
                        transact (fun () -> PanelState.rankFadeOn.Value <- not PanelState.rankFadeOn.Value))
                }
                "Rank fade"
            }
        }

    let private rankingSection () =
        div {
            Class "panel-section"
            h3 { "Datasets" }
            aggregationButtons ()
            topKControls ()
            div {
                Class "rank-list"
                PanelState.datasetOrder
                |> AVal.map IndexList.ofList
                |> AList.ofAVal
                |> AList.map (fun name ->
                    match Map.tryFind name datasetByName with
                    | Some d -> rankingRow d
                    | None -> div { Class "rank-row missing" })
            }
            div {
                Class "rank-footer"
                visibleCount ()
            }
        }

    let view (_env : Env<unit>) (_ : unit) =
        body {
            Class "demo-body"
            OnBoot [
                "if(!document.getElementById('pindemo-css')) {"
                "  var l = document.createElement('link');"
                "  l.id = 'pindemo-css';"
                "  l.rel = 'stylesheet';"
                "  l.href = '/style.css';"
                "  document.head.appendChild(l);"
                "}"
            ]
            div {
                Class "panel"
                div {
                    Class "panel-header"
                    h2 { "ScanPin" }
                    div {
                        Class "panel-coords"
                        let p = PanelState.pin.Prism.AnchorPoint
                        sprintf "(%.1f, %.1f, %.1f)" p.X p.Y p.Z
                    }
                }
                profileSection ()
                coreSampleSection ()
                rankingSection ()
            }
        }
