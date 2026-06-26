namespace Superprojekt

open Aardvark.Base
open FSharp.Data.Adaptive
open Aardvark.Dom

// Left rail: three modes (Overview · Correspondence · Inspect), one expanded at
// a time; the container never moves, only the active mode's detail changes.
// Pure view — every control dispatches an existing message and never issues
// server queries itself.
module GuiRail =

    open Primitives

    type private Pill = PillReady | PillWarn | PillBlock | PillInfo

    let private pillClass = function
        | PillReady -> "rail-pill rail-pill-ready"
        | PillWarn  -> "rail-pill rail-pill-warn"
        | PillBlock -> "rail-pill rail-pill-block"
        | PillInfo  -> "rail-pill rail-pill-info"

    let private sensorLabel = function
        | RoverStereo -> "Rover" | Satellite -> "Sat"
        | Photogrammetry -> "Photo" | LiDAR -> "LiDAR" | UnknownSensor -> "—"

    let private sensorNext = function
        | UnknownSensor -> RoverStereo | RoverStereo -> Satellite
        | Satellite -> Photogrammetry | Photogrammetry -> LiDAR | LiDAR -> UnknownSensor

    let private hex = Primitives.c4bToRgbCss

    let rail (env : Env<Message>) (model : AdaptiveModel) (viewportSize : aval<V2i>) =
        let refMesh   = model.Registration |> AVal.map (fun r -> r.ReferenceMesh)
        let curStep   = model.WorkflowStep
        let pinsVal   = model.ScanPins.Pins |> AMap.toAVal
        let flyTo (target : FlyToTarget) =
            let s = AVal.force viewportSize
            env.Emit [FlyTo(target, float s.X / float (max 1 s.Y))]

        let stepStatus (step : WorkflowStep) : aval<Pill * string> =
            AVal.custom (fun t ->
                let hasRef = (model.Registration.GetValue t).ReferenceMesh |> Option.isSome
                let solved = not (Map.isEmpty (model.SolvedTransforms.GetValue t))
                match step with
                | Overview ->
                    if hasRef then PillReady, "reference set"
                    else PillBlock, "pick a reference ★"
                | Correspondence ->
                    if not hasRef then PillBlock, "needs a reference"
                    elif solved then PillReady, "aligned"
                    else PillInfo, "place ≥3 correspondences, then solve"
                | Inspect ->
                    if hasRef then PillInfo, "error layers"
                    else PillBlock, "needs a reference")

        let modeHeader (step : WorkflowStep) =
            let active = curStep |> AVal.map ((=) step)
            let status = stepStatus step
            button {
                Class "rail-step"
                active |> AVal.map (fun a -> if a then Some (Class "rail-step-active") else None)
                Dom.OnClick(fun _ -> env.Emit [SetWorkflowStep step])
                span { Class "rail-step-no"; string (WorkflowStep.index step + 1) }
                span { Class "rail-step-title"; WorkflowStep.title step }
                span {
                    status |> AVal.map (fun (p, _) -> Some (Class (pillClass p)))
                    status |> AVal.map (fun (p, _) ->
                        match p with PillReady -> "✔" | PillWarn -> "⚠" | PillBlock -> "✖" | PillInfo -> "•")
                }
            }

        let modeBody (step : WorkflowStep) (body : DomNode) =
            div {
                Class "rail-body"
                showWhen (curStep |> AVal.map ((=) step))
                div { Class "rail-hint"; (stepStatus step) |> AVal.map snd }
                body
            }

        let meshRow (name : string) =
            let isVis  = model.MeshVisible |> AVal.map (fun m -> Map.tryFind name m |> Option.defaultValue true)
            let isRef  = refMesh |> AVal.map ((=) (Some name))
            let idxVal = model.MeshOrder |> AMap.tryFind name |> AVal.map (Option.defaultValue 0)
            let colorVal = idxVal |> AVal.map meshColor
            let sensor = model.MeshSensorTypes |> AVal.map (fun m -> Map.tryFind name m |> Option.defaultValue UnknownSensor)
            let hovered = model.Selection.Hovered |> AVal.map (function Some (HoverMesh m) -> m = name | _ -> false)
            div {
                Class "rail-mesh-row"
                isVis |> AVal.map (fun v -> if v then None else Some (Class "rail-row-dim"))
                hovered |> AVal.map (fun h -> if h then Some (Class "rail-row-hover") else None)
                // hover = peek-isolate this mesh via the shared Selection.
                Dom.OnPointerMove(fun _ -> env.Emit [SetHovered (Some (HoverMesh name))])
                Dom.OnMouseLeave(fun _ -> env.Emit [SetHovered None])
                span { Class "mesh-swatch"; colorVal |> AVal.map (fun c -> Some (Style [Css.Background (hex c)])) }
                span { Class "mesh-num"; idxVal |> AVal.map (fun i -> string (i + 1)) }
                span {
                    Class "rail-mesh-name"; Attribute("title", name)
                    Dom.OnClick(fun _ -> env.Emit [SetFocusedMesh (Some name)])
                    shortName name
                }
                button {
                    Class "mb mb-ref"
                    isRef |> AVal.map (fun r -> if r then Some (Class "mb-on") else None)
                    Attribute("title", "Reference mesh — all error is relative to it")
                    Dom.OnClick(fun _ ->
                        let cur = AVal.force refMesh
                        env.Emit [SetReferenceMesh (if cur = Some name then None else Some name)])
                    isRef |> AVal.map (fun r -> if r then "★" else "☆")
                }
                button {
                    Class "mb"
                    isVis |> AVal.map (fun v -> if v then Some (Class "mb-on") else None)
                    Attribute("title", "Visible")
                    Dom.OnClick(fun _ -> env.Emit [SetVisible(name, not (AVal.force isVis))])
                    isVis |> AVal.map (fun v -> if v then "●" else "○")
                }
                button {
                    Class "mb rail-sensor"
                    Attribute("title", "Sensor type (cycles)")
                    Dom.OnClick(fun _ -> env.Emit [SetMeshSensorType(name, sensorNext (AVal.force sensor))])
                    sensor |> AVal.map sensorLabel
                }
                button {
                    Class "mb"
                    Attribute("title", "Frame this mesh")
                    Dom.OnClick(fun _ ->
                        match Map.tryFind name (AVal.force model.MeshBounds) with
                        | Some b -> flyTo (FlyToBounds b)
                        | None -> ())
                    "⌖"
                }
            }
        let overviewBody =
            div { Class "rail-mesh-list"; model.MeshNames |> AList.map meshRow }

        let pinList =
            pinsVal
            |> AVal.map (fun pins ->
                pins |> HashMap.toList |> List.sortBy (fun (_, p) -> p.CreatedAt)
                |> List.map snd |> IndexList.ofList)
            |> AList.ofAVal
        let pinRow (pin : ScanPin) =
            let selected = model.Selection.SelectedPin |> AVal.map ((=) (Some pin.Id))
            let isCorr =
                pin.Correspondence |> Option.map (fun c -> c.Enabled) |> Option.defaultValue false
            div {
                Class "rail-pin-row"
                selected |> AVal.map (fun s -> if s then Some (Class "rail-pin-sel") else None)
                // hover = peek the pin's constellation via the shared Selection.
                Dom.OnPointerMove(fun _ -> env.Emit [SetHovered (Some (HoverPin pin.Id))])
                Dom.OnMouseLeave(fun _ -> env.Emit [SetHovered None])
                span {
                    Class "rail-pin-name"
                    Dom.OnClick(fun _ -> env.Emit [ScanPinMsg (SelectPin (Some pin.Id))])
                    pin.Name
                }
                button {
                    Class (if isCorr then "mb mb-on" else "mb")
                    Attribute("title", "Registration correspondence (promote / demote)")
                    Dom.OnClick(fun _ -> env.Emit [ToggleCorrespondence pin.Id])
                    "⚲"
                }
                button {
                    Class "mb rail-pin-del"
                    Attribute("title", "Delete pin")
                    Dom.OnClick(fun _ -> env.Emit [ScanPinMsg (DeletePin pin.Id)])
                    "✕"
                }
            }
        let placing =
            model.ScanPins.Placement |> AVal.map (function AnchorPlacement -> true | _ -> false)

        let corrBody =
            let diags = ReadinessView.input model |> AVal.map (fun inp -> (Readiness.compute inp).Coarse)
            let sevClass = function Blocker -> "block" | Warning -> "warn" | Ready -> "ready" | Info -> "info"
            let sevIcon  = function Blocker -> "✖" | Warning -> "⚠" | Ready -> "✔" | Info -> "•"
            let diagRow (d : Diagnostic) =
                div {
                    Class (sprintf "rail-diag rail-diag-%s" (sevClass d.Severity))
                    span { Class "rail-diag-ic"; sevIcon d.Severity }
                    span { Class "rail-diag-tx"; d.Text }
                    match d.Action with
                    | Some a ->
                        button { Class "rail-diag-go"; Attribute("title", "go"); Dom.OnClick(fun _ -> env.Emit [NavTo a]); "→" }
                    | None -> span { Class "hidden" }
                }
            div {
                Class "rail-step-controls"
                div { Class "rail-note"; "Place pins on the reference; auto-seeded markers project onto each mesh. Edit handles in the focus panel; manage + solve in the dock below." }
                div {
                    Class "rail-pins-head"
                    span { Class "rail-section-title"; "Pins" }
                    button {
                        Class "rail-btn rail-pin-add"
                        placing |> AVal.map (fun p -> if p then Some (Class "rail-btn-active") else None)
                        Attribute("title", "Place a pin — tap on the reference surface")
                        Dom.OnClick(fun _ ->
                            if AVal.force placing then env.Emit [ScanPinMsg CancelPlacement]
                            else env.Emit [ScanPinMsg EnterAnchorPlacement])
                        placing |> AVal.map (fun p -> if p then "○ placing… (Esc)" else "○ Place pin")
                    }
                }
                div { Class "rail-pin-list"; pinList |> AList.map pinRow }
                div { Class "rail-diags"; diags |> AVal.map IndexList.ofList |> AList.ofAVal |> AList.map diagRow }
            }

        let inspectBody =
            let focusName =
                (model.Selection.FocusedMesh, model.MeshOrder.Content)
                ||> AVal.map2 (fun fm o -> match fm with Some m -> numbered o m | None -> "— pick a mesh")
            div {
                Class "rail-step-controls"
                div { Class "rail-note"; "Difference & displacement show per-mesh in the focus; variance shows on the reference in 3D. Before/after follows the global toggle." }
                div {
                    Class "rail-light-row"
                    span { Class "rail-sublabel"; "Focused:" }
                    span { Class "rail-light-v"; focusName }
                }
                div {
                    Class "rail-toggle-row"
                    span { Class "rail-sublabel"; "Difference:" }
                    compactButtonBar [
                        "M3C2", (model.ExtrinsicZDiff |> AVal.map not),  (fun () -> if AVal.force model.ExtrinsicZDiff then env.Emit [ToggleExtrinsicZDiff])
                        "Δz",   (model.ExtrinsicZDiff :> aval<bool>),    (fun () -> if not (AVal.force model.ExtrinsicZDiff) then env.Emit [ToggleExtrinsicZDiff])
                    ]
                }
                div {
                    Class "rail-toggle-row"
                    span { Class "rail-sublabel"; "Intrinsic:" }
                    compactButtonBar [
                        "Off",       (model.HeatmapMode |> AVal.map (fun m -> m = HeatOff)),       (fun () -> env.Emit [SetHeatmapMode HeatOff])
                        "Incidence", (model.HeatmapMode |> AVal.map (fun m -> m = HeatIncidence)),  (fun () -> env.Emit [SetHeatmapMode HeatIncidence])
                        "Range",     (model.HeatmapMode |> AVal.map (fun m -> m = HeatRange)),      (fun () -> env.Emit [SetHeatmapMode HeatRange])
                        "Shape",     (model.HeatmapMode |> AVal.map (fun m -> m = HeatShape)),      (fun () -> env.Emit [SetHeatmapMode HeatShape])
                    ]
                }
                div {
                    Class "rail-note rail-note-sub"
                    "Variance — disagreement of all visible moving meshes (≥2) — paints on the reference in 3D automatically while Inspect is active."
                }
            }

        div {
            Class "workflow-rail"
            div {
                Class "rail-steps"
                modeHeader Overview
                modeBody Overview overviewBody
                modeHeader Correspondence
                modeBody Correspondence corrBody
                modeHeader Inspect
                modeBody Inspect inspectBody
            }
        }
