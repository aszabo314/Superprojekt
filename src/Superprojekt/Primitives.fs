namespace Superprojekt

open Aardvark.Base
open FSharp.Data.Adaptive
open Aardvark.Dom

module Primitives =

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
    /// `valueMin`/`valueMax` are the adaptive min/max values, `onChange` gets (newMin, newMax).
    let inlineRangeSlider
            (labelText : string)
            (minV : float) (maxV : float) (stepV : float)
            (format : float -> float -> string)
            (valueMin : aval<float>) (valueMax : aval<float>)
            (onChange : float -> float -> unit) =
        div {
            Class "irs"
            span { Class "irs-label"; labelText }
            div {
                Class "irs-tracks"
                input {
                    Class "irs-range irs-min"
                    Attribute("type", "range")
                    Attribute("min",  sprintf "%.6g" minV)
                    Attribute("max",  sprintf "%.6g" maxV)
                    Attribute("step", sprintf "%.6g" stepV)
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
                    Attribute("min",  sprintf "%.6g" minV)
                    Attribute("max",  sprintf "%.6g" maxV)
                    Attribute("step", sprintf "%.6g" stepV)
                    valueMax |> AVal.map (fun v -> Some (Attribute("value", sprintf "%.6g" v)))
                    Dom.OnInput(fun e ->
                        match parseFloat e.Value with
                        | Some v ->
                            let lo = AVal.force valueMin
                            onChange lo (max v lo)
                        | None -> ())
                }
            }
            span {
                Class "irs-readout"
                (valueMin, valueMax) ||> AVal.map2 format
            }
        }

    /// Tiny icon-only button (e.g. 🎯, 🔍, ✕, ✎) with a title-tooltip.
    let miniButton (icon : string) (tooltip : string) (onClick : unit -> unit) =
        button {
            Class "mb"
            Attribute("title", tooltip)
            Dom.OnClick(fun _ -> onClick ())
            icon
        }
