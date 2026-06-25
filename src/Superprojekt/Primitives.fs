namespace Superprojekt

open Aardvark.Base
open FSharp.Data.Adaptive
open Aardvark.Dom

module Primitives =

    let meshPalette =
        [| C4b(228uy,26uy,28uy);  C4b(55uy,126uy,184uy); C4b(77uy,175uy,74uy)
           C4b(152uy,78uy,163uy); C4b(255uy,127uy,0uy);  C4b(255uy,255uy,51uy)
           C4b(166uy,86uy,40uy);  C4b(247uy,129uy,191uy);C4b(153uy,153uy,153uy) |]

    let c4bToV3d (c : C4b) = V3d(float c.R / 255.0, float c.G / 255.0, float c.B / 255.0)
    let c4bToRgbCss (c : C4b) = sprintf "rgb(%d,%d,%d)" (int c.R) (int c.G) (int c.B)

    let meshPaletteV4d =
        meshPalette |> Array.map (fun c -> V4d(c4bToV3d c, 1.0))

    let meshColor (idx : int) = meshPalette.[((idx % meshPalette.Length) + meshPalette.Length) % meshPalette.Length]

    let c4bToHex (c : C4b) = sprintf "#%02x%02x%02x" c.R c.G c.B

    // Short, human-friendly mesh label (drops the dataset prefix, keeps a date +
    // segment tag where present).
    let shortName (name : string) =
        let mesh =
            let s = name.IndexOf('/')
            if s >= 0 then name.[s + 1 ..] else name
        if mesh.Length > 8 && mesh.[8] = '_' then
            let date = mesh.[..7]
            let si = mesh.LastIndexOf("_seg")
            if si > 0 then date + "_" + mesh.[si + 1 ..] else date
        else mesh

    // Prefix the mesh's stable 1-based order number (matches the panel palette).
    let numbered (order : HashMap<string, int>) (name : string) =
        match HashMap.tryFind name order with
        | Some i -> sprintf "%d  %s" (i + 1) (shortName name)
        | None -> shortName name

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

    let numberInput
            (labelText : string)
            (minV : float) (maxV : float) (stepV : float)
            (format : float -> string)
            (value : aval<float>)
            (onChange : float -> unit) =
        div {
            Class "is"
            span { Class "is-label"; labelText }
            input {
                Class "is-value"
                Attribute("type", "number")
                Attribute("min",  sprintf "%.6g" minV)
                Attribute("max",  sprintf "%.6g" maxV)
                Attribute("step", sprintf "%.6g" stepV)
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
    // attribute into `d`, clears, runs `body`, re-renders on mutation. Dynamic
    // markup goes through JS because the Aardvark.Dom CE has no yield!.
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

// Readiness-engine adapter: builds the engine input from individual model
// leaves (adaptive-performance rule — never the whole record), shared by the
// registration card and the workflow panel.
module ReadinessView =

    open FSharp.Data.Adaptive

    let input (model : AdaptiveModel) : aval<ReadinessInput> =
        let pinsVal = model.ScanPins.Pins |> AMap.toAVal
        let meshNamesVal = model.MeshNames |> AList.toAVal
        AVal.custom (fun t ->
            let pins = pinsVal.GetValue t
            let reg = model.Registration.GetValue t
            let names = meshNamesVal.GetValue t |> IndexList.toList
            let visible = model.MeshVisible.GetValue t
            let pending = PendingRegistration.isPreview (model.PendingReg.GetValue t)
            // No history any more: "committed step" = a mesh already carries a
            // (non-identity) committed transform from the single coarse commit.
            let hasCommitted =
                model.MeshTransforms.GetValue t
                |> Map.exists (fun _ tr -> not (tr.Forward.Equals M44d.Identity))
            let movingVisible =
                match reg.ReferenceMesh with
                | Some r ->
                    names |> List.filter (fun n ->
                        n <> r && (Map.tryFind n visible |> Option.defaultValue true))
                | None -> []
            let enabledPins =
                pins |> HashMap.toList
                |> List.choose (fun (id, p) ->
                    match ScanPin.correspondence p with
                    | Some c when c.Enabled && p.Phase = PinPhase.Committed ->
                        let marked =
                            movingVisible
                            |> List.filter (fun m -> Map.containsKey m c.Anchors)
                            |> Set.ofList
                        Some {
                            Id            = id
                            Label         = p.Name
                            RefAnchor     = c.RefAnchor |> Option.map (fun ra -> ra, 1.0)
                            Accepted      = marked
                            Unresolved    = List.length movingVisible - Set.count marked
                        }
                    | _ -> None)
            {
                ReferenceMesh       = reg.ReferenceMesh
                VisibleMovingMeshes = movingVisible
                EnabledPins         = enabledPins
                HasPending          = pending
                HasCommittedStep    = hasCommitted
                FineModeLabel       =
                    match reg.Mode with
                    | TraditionalIcp -> "Traditional ICP"
                    | RegionRestrictedIcp -> "Region-restricted"
            })
