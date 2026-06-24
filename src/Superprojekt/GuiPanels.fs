namespace Superprojekt

open Aardvark.Base
open FSharp.Data.Adaptive
open Aardvark.Dom

module GuiPanels =

    open Primitives

    // Pin-adjust flyout: shown while a pin is being placed/adjusted (inner
    // radius + numeric X/Y/Z + commit/discard). The mesh/pin/registration
    // panels moved to the left workflow rail (GuiRail).
    let placementFlyout (env : Env<Message>) (model : AdaptiveModel) =
        let sp = model.ScanPins
        let activePlacementId =
            sp.Placement |> AVal.map (function
                | AdjustingPin id -> Some id
                | _ -> None)
        let activePin =
            activePlacementId |> AVal.bind (function
                | Some i -> sp.Pins |> AMap.tryFind i
                | None -> AVal.constant None)
        let adjusting = sp.Placement |> AVal.map (function AdjustingPin _ -> true | _ -> false)
        let flyoutClass =
            (adjusting, model.MenuOpen) ||> AVal.map2 (fun adj open_ ->
                if not adj then "placement-flyout hidden"
                elif open_ then "placement-flyout pf-left-open"
                else "placement-flyout pf-left-closed")
        div {
            flyoutClass |> AVal.map (fun c -> Some (Class c))
            div { Class "lp-section-title"; "Adjust Anchor" }
            div {
            let innerR =
                activePin |> AVal.map (Option.map (fun p -> p.InnerRadius) >> Option.defaultValue 1.0)
            inlineLogSlider "Inner radius" 0.01 10000.0 (sprintf "%.2f m") innerR (fun v ->
                env.Emit [ScanPinMsg (SetInnerRadius v)])

            let pinId = activePlacementId

            // Numeric reposition: set the pin centre live while adjusting.
            let centre =
                activePin |> AVal.map (Option.map (fun p -> p.Centre) >> Option.defaultValue V3d.Zero)
            let posInput (lbl : string) (get : V3d -> float) (upd : V3d -> float -> V3d) =
                div {
                    Class "pf-pos-field"
                    span { Class "pf-pos-lbl"; lbl }
                    input {
                        Class "pf-pos-input"
                        Attribute("type", "number")
                        Attribute("step", "0.1")
                        centre |> AVal.map (fun c -> Some (Attribute("value", sprintf "%.2f" (get c))))
                        Dom.OnChange(fun e ->
                            match System.Double.TryParse(e.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture) with
                            | true, v ->
                                match AVal.force pinId with
                                | Some id -> env.Emit [ScanPinMsg (RepositionPin(id, upd (AVal.force centre) v))]
                                | None -> ()
                            | _ -> ())
                    }
                }
            div { Class "lp-sublabel"; "Position (m)" }
            div {
                Class "pf-pos-fields"
                posInput "X" (fun c -> c.X) (fun c v -> V3d(v, c.Y, c.Z))
                posInput "Y" (fun c -> c.Y) (fun c v -> V3d(c.X, v, c.Z))
                posInput "Z" (fun c -> c.Z) (fun c v -> V3d(c.X, c.Y, v))
            }
            }

            div {
                Class "lp-commit-row"
                button {
                    Class "lp-commit"
                    Dom.OnClick(fun _ -> env.Emit [ScanPinMsg CommitPin])
                    "✓ Commit"
                }
                button {
                    Class "lp-discard"
                    Dom.OnClick(fun _ -> env.Emit [ScanPinMsg CancelPlacement])
                    "✕ Discard"
                }
            }
        }
