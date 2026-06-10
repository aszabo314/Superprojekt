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

    let showWhen (v : aval<bool>) =
        v |> AVal.map (fun on -> if on then None else Some (Class "hidden"))

    let showWhenNot (v : aval<bool>) =
        v |> AVal.map (fun on -> if on then Some (Class "hidden") else None)

    let compactToggle (labelText : string) (value : aval<bool>) (onToggle : unit -> unit) =
        div {
            Class "ct"
            Dom.OnClick(fun _ -> onToggle ())
            span {
                Class "ct-box"
                value |> AVal.map (fun v -> if v then "■" else "□")
            }
            " " + labelText
        }

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

    let collapsibleSection (title : string) (startExpanded : bool) (body : DomNode) =
        let expanded = cval startExpanded
        div {
            Class "cs"
            div {
                Class "cs-header"
                Dom.OnClick(fun _ -> transact (fun () -> expanded.Value <- not expanded.Value))
                span {
                    Class "cs-tri"
                    (expanded :> aval<bool>) |> AVal.map (fun e -> if e then "▾" else "▸")
                }
                " " + title
            }
            div {
                Class "cs-body"
                showWhen (expanded :> aval<bool>)
                body
            }
        }

    // OnBoot wrapper for attribute-driven SVG/DOM rendering: parses the JSON
    // attribute into `d`, clears the element, runs `body`, and re-renders on
    // attribute mutation (the Aardvark.Dom CE has no yield!, so dynamic markup
    // goes through JS).
    let observedRender (attr : string) (fallback : string) (body : string list) =
        OnBoot (
            [ "(function(){"
              "var el = __THIS__;"
              "var ns = 'http://www.w3.org/2000/svg';"
              "var last = '';"
              "function render(){"
              sprintf "var raw = el.getAttribute('%s') || '%s';" attr fallback
              "if(raw === last) return; last = raw;"
              "var d; try { d = JSON.parse(raw); } catch(e) { return; }"
              "el.innerHTML = '';" ]
            @ body @
            [ "}"
              "render();"
              sprintf "new MutationObserver(render).observe(el,{attributes:true,attributeFilter:['%s']});" attr
              "})();" ])

    let provBarJs = [
        "if(!d || d.length < 3) return;"
        "var colours = ['#60a5fa','#f59e0b','#a78bfa'];"
        "d.forEach(function(p, i){"
        "  var s = document.createElement('div');"
        "  s.style.width = p + '%';"
        "  s.style.background = colours[i];"
        "  s.style.height = '100%';"
        "  el.appendChild(s);"
        "});" ]
