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

    let private boxplotSvg (s : DatasetCoreSampleStats) (w : float) (h : float) =
        // The shared y-range for boxplots: union over all datasets.
        let stats = PanelState.datasets |> Array.map (fun d -> d.Stats)
        let yMin = stats |> Array.map (fun s -> s.ZMin) |> Array.min
        let yMax = stats |> Array.map (fun s -> s.ZMax) |> Array.max
        let r = if yMax - yMin < 1e-9 then 1.0 else yMax - yMin
        let toX v = (v - yMin) / r * w
        let cy = h / 2.0
        let bx1 = toX s.ZQ1
        let bx2 = toX s.ZQ3
        let med = toX s.ZMedian
        sprintf
            "<svg width='%.0f' height='%.0f' viewBox='0 0 %.0f %.0f' xmlns='http://www.w3.org/2000/svg'>\
             <line x1='%.1f' y1='%.1f' x2='%.1f' y2='%.1f' stroke='#64748b' stroke-width='1'/>\
             <rect x='%.1f' y='%.1f' width='%.1f' height='%.1f' fill='#cbd5e1' stroke='#475569' stroke-width='1'/>\
             <line x1='%.1f' y1='%.1f' x2='%.1f' y2='%.1f' stroke='#0f172a' stroke-width='2'/>\
             </svg>"
            w h w h
            (toX s.ZMin) cy (toX s.ZMax) cy
            bx1 (cy - 6.0) (bx2 - bx1) 12.0
            med (cy - 7.0) med (cy + 7.0)

    let private rankingRow (d : DummyDataset) =
        let isHidden = PanelState.datasetHidden |> ASet.contains d.MeshName
        div {
            isHidden |> AVal.map (fun h ->
                Some (Class (if h then "rank-row hidden" else "rank-row")))
            div {
                Class "rank-color"
                Style [Background (c4bToHex d.Color)]
            }
            div {
                Class "rank-name"
                shortName d.MeshName
            }
            div {
                Class "rank-box"
                Attribute("data-svg", boxplotSvg d.Stats 120.0 16.0)
                OnBoot [
                    "var el = __THIS__;"
                    "el.innerHTML = el.getAttribute('data-svg') || '';"
                ]
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

    let private visibleCount () =
        let total = PanelState.datasets.Length
        let hiddenCount =
            PanelState.datasetHidden
            |> ASet.count
        hiddenCount |> AVal.map (fun h ->
            sprintf "Showing %d of %d datasets" (total - h) total)

    let private rankingSection () =
        div {
            Class "panel-section"
            h3 { "Datasets" }
            aggregationButtons ()
            div {
                Class "rank-list"
                for d in PanelState.datasets do
                    rankingRow d
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
