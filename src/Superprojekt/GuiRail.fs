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

    // One mesh list, two column sets. Identity / order / selection / hover are
    // identical; only the trailing affordances differ (Overview = setup controls,
    // Inspect = solve state + quick isolate).
    type private MeshRowMode = RowOverview | RowInspect

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
                classWhen "rail-step-active" active
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
                body
            }

        // One mode-parameterized mesh row (§B). click = focus everywhere (Inspect
        // additionally solos, per §C auto-solo); hover = peek everywhere. Columns
        // differ by mode only.
        let meshRow (mode : MeshRowMode) (name : string) =
            let isVis  = model.MeshVisible |> AVal.map (fun m -> Map.tryFind name m |> Option.defaultValue true)
            let isSolo = model.MeshSolo |> AVal.map (function Solo(n, _) -> n = name | _ -> false)
            let isRef  = refMesh |> AVal.map ((=) (Some name))
            let idxVal = model.MeshOrder |> AMap.tryFind name |> AVal.map (Option.defaultValue 0)
            let colorVal = idxVal |> AVal.map meshColor
            let sensor = model.MeshSensorTypes |> AVal.map (fun m -> Map.tryFind name m |> Option.defaultValue UnknownSensor)
            let hovered = model.Selection.Hovered |> AVal.map (function Some (HoverMesh m) -> m = name | _ -> false)
            let focused = model.Selection.FocusedMesh |> AVal.map ((=) (Some name))
            // click → focus (universal); Inspect adds auto-solo (idempotent — clicking
            // the already-soloed mesh just keeps the focus).
            let onClick () =
                match mode with
                | RowOverview -> env.Emit [SetFocusedMesh (Some name)]
                | RowInspect ->
                    if AVal.force isSolo then env.Emit [SetFocusedMesh (Some name)]
                    else env.Emit [ToggleMeshSolo name; SetFocusedMesh (Some name)]
            let swatch = span { Class "mesh-swatch"; colorVal |> AVal.map (fun c -> Some (Style [Css.Background (hex c)])) }
            let num    = span { Class "mesh-num"; idxVal |> AVal.map (fun i -> string (i + 1)) }
            let nameSpan =
                span {
                    Class "rail-mesh-name"
                    Dom.OnClick(fun _ -> onClick ())
                    shortName name
                }
            let refBtn =
                button {
                    Class "mb mb-ref"
                    classWhen "mb-on" isRef
                    Attribute("title", "Reference mesh — all error is relative to it")
                    Dom.OnClick(fun _ ->
                        let cur = AVal.force refMesh
                        env.Emit [SetReferenceMesh (if cur = Some name then None else Some name)])
                    isRef |> AVal.map (fun r -> if r then "★" else "☆")
                }
            let visBtn =
                button {
                    Class "mb"
                    classWhen "mb-on" isVis
                    Attribute("title", "Visible")
                    Dom.OnClick(fun _ -> env.Emit [SetVisible(name, not (AVal.force isVis))])
                    isVis |> AVal.map (fun v -> if v then "●" else "○")
                }
            let soloBtn =
                button {
                    Class "mb"
                    classWhen "mb-on" isSolo
                    Attribute("title", "Isolate this mesh (hide the others); click again to restore")
                    Dom.OnClick(fun _ -> env.Emit [ToggleMeshSolo name])
                    "◐"
                }
            let sensorBtn =
                button {
                    Class "mb rail-sensor"
                    Attribute("title", "Sensor type (cycles)")
                    Dom.OnClick(fun _ -> env.Emit [SetMeshSensorType(name, sensorNext (AVal.force sensor))])
                    sensor |> AVal.map sensorLabel
                }
            let frameBtn =
                button {
                    Class "mb"
                    Attribute("title", "Frame this mesh")
                    Dom.OnClick(fun _ ->
                        match Map.tryFind name (AVal.force model.MeshBounds) with
                        | Some b -> flyTo (FlyToBounds b)
                        | None -> ())
                    "⌖"
                }
            // Inspect solve-state flag: ✓ solved · ready (≥3 markers) · k/3 short.
            let markerCount =
                model.ScanPins.Pins |> AMap.toAVal
                |> AVal.map (fun pins ->
                    pins |> HashMap.toSeq
                    |> Seq.filter (fun (_, p) ->
                        match ScanPin.correspondence p with
                        | Some c -> Map.containsKey name c.Anchors
                        | None -> false)
                    |> Seq.length)
            let solvedA = model.SolvedTransforms |> AVal.map (Map.containsKey name)
            let flagSpan =
                let txt =
                    AVal.custom (fun t ->
                        if isRef.GetValue t then "★ ref"
                        elif solvedA.GetValue t then "✓ solved"
                        elif markerCount.GetValue t >= 3 then "ready"
                        else sprintf "%d/3" (markerCount.GetValue t))
                let cls =
                    AVal.custom (fun t ->
                        if isRef.GetValue t then "rail-flag rail-flag-ref"
                        elif solvedA.GetValue t then "rail-flag rail-flag-ok"
                        elif markerCount.GetValue t >= 3 then "rail-flag rail-flag-ready"
                        else "rail-flag rail-flag-low")
                span {
                    cls |> AVal.map (Class >> Some)
                    Attribute("title", "Solve state: ✓ solved · ready (≥3 markers) · k/3 insufficient")
                    txt
                }
            let trailing =
                match mode with
                | RowOverview -> [ refBtn; visBtn; soloBtn; sensorBtn; frameBtn ]
                | RowInspect  -> [ flagSpan; visBtn; soloBtn ]
            div {
                Class "rail-mesh-row"
                classWhenNot "rail-row-dim" isVis
                classWhen "rail-row-hover" hovered
                classWhen "rail-mesh-sel" focused
                Attribute("title", name)
                // hover = peek-isolate this mesh via the shared Selection.
                Dom.OnPointerMove(fun _ -> env.Emit [SetHovered (Some (HoverMesh name))])
                Dom.OnMouseLeave(fun _ -> env.Emit [SetHovered None])
                AList.ofList ([ swatch; num; nameSpan ] @ trailing)
            }
        let overviewBody =
            div { Class "rail-mesh-list"; model.MeshNames |> AList.map (meshRow RowOverview) }

        let pinRow (id : ScanPinId) (name : string) =
            let selected = model.Selection.SelectedPin |> AVal.map ((=) (Some id))
            div {
                Class "rail-pin-row"
                classWhen "rail-pin-sel" selected
                // hover = peek the pin's constellation via the shared Selection.
                Dom.OnPointerMove(fun _ -> env.Emit [SetHovered (Some (HoverPin id))])
                Dom.OnMouseLeave(fun _ -> env.Emit [SetHovered None])
                span {
                    Class "rail-pin-name"
                    Dom.OnClick(fun _ -> env.Emit [ScanPinMsg (SelectPin (Some id))])
                    name
                }
                button {
                    Class "mb rail-pin-del"
                    Attribute("title", "Delete pin")
                    Dom.OnClick(fun _ -> env.Emit [ScanPinMsg (DeletePin id)])
                    "✕"
                }
            }
        // Stable-identity incremental list, sorted by (immutable) creation order.
        // Project to ONLY the row's inputs (id, name) so a pin's probe/ring updates
        // don't re-key its row. The old `AVal.map (… IndexList.ofList) |> AList.ofAVal`
        // minted fresh indices on every pin change, churning the whole list and
        // intermittently double-rendering a row.
        let pinList =
            model.ScanPins.Pins
            |> AMap.map (fun _ p -> p.Name, p.CreatedAt)
            |> AMap.toASet
            |> ASet.sortBy (fun (ScanPinId.ScanPinId g, (_, created)) -> created, g)
            |> AList.map (fun (id, (name, _)) -> pinRow id name)
        let placing =
            model.ScanPins.Placement |> AVal.map (function AnchorPlacement -> true | _ -> false)

        let corrBody =
            let diags = ReadinessView.input model |> AVal.map Readiness.compute
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
                div {
                    Class "rail-pins-head"
                    span { Class "rail-section-title"; "Pins" }
                    button {
                        Class "rail-btn rail-pin-add"
                        classWhen "rail-btn-active" placing
                        Attribute("title", "Place a pin — tap on the reference surface")
                        Dom.OnClick(fun _ ->
                            if AVal.force placing then env.Emit [ScanPinMsg CancelPlacement]
                            else env.Emit [ScanPinMsg EnterAnchorPlacement])
                        placing |> AVal.map (fun p -> if p then "○ placing… (Esc)" else "○ Place pin")
                    }
                }
                div { Class "rail-pin-list"; pinList }
                div { Class "rail-diags"; diags |> AVal.map IndexList.ofList |> AList.ofAVal |> AList.map diagRow }
            }

        let inspectBody =
            let focusName =
                (model.Selection.FocusedMesh, model.MeshOrder.Content)
                ||> AVal.map2 (fun fm o -> match fm with Some m -> numbered o m | None -> "— pick a mesh")
            div {
                Class "rail-step-controls"
                div { Class "rail-mesh-list"; model.MeshNames |> AList.map (meshRow RowInspect) }
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
