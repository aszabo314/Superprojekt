namespace Superprojekt

open Aardvark.Base
open FSharp.Data.Adaptive
open Aardvark.Dom

module Primitives =

    let meshPalette =
        [| C4b(228uy,26uy,28uy);  C4b(55uy,126uy,184uy); C4b(77uy,175uy,74uy)
           C4b(152uy,78uy,163uy); C4b(255uy,127uy,0uy);  C4b(255uy,255uy,51uy)
           C4b(166uy,86uy,40uy);  C4b(247uy,129uy,191uy);C4b(153uy,153uy,153uy) |]

    let meshPaletteV4d =
        meshPalette |> Array.map (fun c -> V4d(float c.R / 255.0, float c.G / 255.0, float c.B / 255.0, 1.0))

    let meshColor (idx : int) = meshPalette.[((idx % meshPalette.Length) + meshPalette.Length) % meshPalette.Length]

    let private parseFloat (s : string) =
        match System.Double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture) with
        | true, v -> Some v
        | _ -> None

    /// Tiny labeled on/off switch. [■] or [□] + label on one line.
    let compactToggle (labelText : string) (value : aval<bool>) (onToggle : unit -> unit) =
        div {
            Class "ct"
            Dom.OnClick(fun _ -> onToggle ())
            span {
                Class "ct-box"
                value |> AVal.map (fun v -> if v then "\u25A0" else "\u25A1")
            }
            " " + labelText
        }

    /// Label + range slider + editable numeric value, all on one line.
    let inlineSlider
            (labelText : string)
            (minV : float) (maxV : float) (stepV : float)
            (format : float -> string)
            (value : aval<float>)
            (onChange : float -> unit) =
        div {
            Class "is"
            span { Class "is-label"; labelText }
            input {
                Class "is-range"
                Attribute("type", "range")
                Attribute("min",  sprintf "%.6g" minV)
                Attribute("max",  sprintf "%.6g" maxV)
                Attribute("step", sprintf "%.6g" stepV)
                value |> AVal.map (fun v -> Some (Attribute("value", sprintf "%.6g" v)))
                Dom.OnInput(fun e -> parseFloat e.Value |> Option.iter onChange)
            }
            input {
                Class "is-value"
                Attribute("type", "text")
                value |> AVal.map (fun v -> Some (Attribute("value", format v)))
                Dom.OnChange(fun e -> parseFloat e.Value |> Option.iter onChange)
            }
        }

    /// Log-spaced variant of inlineSlider (for small→large ranges like sensitivity).
    let inlineLogSlider
            (labelText : string)
            (minV : float) (maxV : float)
            (format : float -> string)
            (value : aval<float>)
            (onChange : float -> unit) =
        let toSlider v =
            let v = clamp minV maxV v
            log10 (v / minV) / log10 (maxV / minV) * 1000.0
        let fromSlider s =
            minV * (maxV / minV) ** (s / 1000.0)
        div {
            Class "is"
            span { Class "is-label"; labelText }
            input {
                Class "is-range"
                Attribute("type", "range")
                Attribute("min", "0")
                Attribute("max", "1000")
                Attribute("step", "1")
                value |> AVal.map (fun v -> Some (Attribute("value", sprintf "%.1f" (toSlider v))))
                Dom.OnInput(fun e -> parseFloat e.Value |> Option.iter (fun s -> onChange (fromSlider s)))
            }
            span { Class "is-value-ro"; value |> AVal.map format }
        }

    /// Row of mutually-exclusive mode buttons. Items: (label, isActive aval, onClick).
    let compactButtonBar (items : (string * aval<bool> * (unit -> unit)) list) =
        div {
            Class "cbb"
            AList.ofList items
            |> AList.map (fun (label, isActive, onClick) ->
                button {
                    Class "cbb-btn"
                    isActive |> AVal.map (fun a -> if a then Some (Class "cbb-btn-active") else None)
                    Dom.OnClick(fun _ -> onClick ())
                    label
                })
        }

    /// A section with a ▸/▾ triangle that expands/collapses its content.
    /// `body` is a single DOM node (wrap in a div if you need multiple children).
    let collapsibleSection (title : string) (startExpanded : bool) (body : DomNode) =
        let expanded = cval startExpanded
        div {
            Class "cs"
            div {
                Class "cs-header"
                Dom.OnClick(fun _ -> transact (fun () -> expanded.Value <- not expanded.Value))
                span {
                    Class "cs-tri"
                    (expanded :> aval<bool>) |> AVal.map (fun e -> if e then "\u25BE" else "\u25B8")
                }
                " " + title
            }
            div {
                Class "cs-body"
                (expanded :> aval<bool>) |> AVal.map (fun e ->
                    if e then None else Some (Style [Display "none"]))
                body
            }
        }

    /// Dual-handle range slider rendered as two stacked range inputs.
    /// min/max/step are adaptive so the slider updates its range when the underlying bounds change.
    let inlineRangeSliderA
            (labelText : string)
            (minV : aval<float>) (maxV : aval<float>) (stepV : aval<float>)
            (format : (float -> float -> string) option)
            (valueMin : aval<float>) (valueMax : aval<float>)
            (onChange : float -> float -> unit) =
        // Both thumbs share the full [minV, maxV] range: narrowing one thumb's
        // range to the other's current value re-normalizes its position when the
        // other is dragged. Non-crossing is enforced in the input handlers.
        let attrStep = stepV |> AVal.map (fun v -> Some (Attribute("step", sprintf "%.6g" v)))
        let attrMin  = minV  |> AVal.map (fun v -> Some (Attribute("min",  sprintf "%.6g" v)))
        let attrMax  = maxV  |> AVal.map (fun v -> Some (Attribute("max",  sprintf "%.6g" v)))
        div {
            Class "irs"
            span { Class "irs-label"; labelText }
            div {
                Class "irs-tracks"
                div { Class "irs-track-line" }
                input {
                    Class "irs-range irs-min"
                    Attribute("type", "range")
                    attrMin; attrMax; attrStep
                    valueMin |> AVal.map (fun v -> Some (Attribute("value", sprintf "%.6g" v)))
                    Dom.OnInput(fun e ->
                        match parseFloat e.Value with
                        | Some v ->
                            let hi = AVal.force valueMax
                            onChange (min v hi) hi
                        | None -> ())
                }
                input {
                    Class "irs-range irs-max"
                    Attribute("type", "range")
                    attrMin; attrMax; attrStep
                    valueMax |> AVal.map (fun v -> Some (Attribute("value", sprintf "%.6g" v)))
                    Dom.OnInput(fun e ->
                        match parseFloat e.Value with
                        | Some v ->
                            let lo = AVal.force valueMin
                            onChange lo (max v lo)
                        | None -> ())
                }
            }
            match format with
            | Some fmt ->
                span {
                    Class "irs-readout"
                    (valueMin, valueMax) ||> AVal.map2 fmt
                }
            | None -> ()
        }

    let inlineRangeSlider
            (labelText : string)
            (minV : float) (maxV : float) (stepV : float)
            (format : float -> float -> string)
            (valueMin : aval<float>) (valueMax : aval<float>)
            (onChange : float -> float -> unit) =
        inlineRangeSliderA labelText
            (AVal.constant minV) (AVal.constant maxV) (AVal.constant stepV)
            (Some format) valueMin valueMax onChange

    /// Tiny icon-only button (e.g. 🎯, 🔍, ✕, ✎) with a title-tooltip.
    let miniButton (icon : string) (tooltip : string) (onClick : unit -> unit) =
        button {
            Class "mb"
            Attribute("title", tooltip)
            Dom.OnClick(fun _ -> onClick ())
            icon
        }
