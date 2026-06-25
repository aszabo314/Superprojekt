namespace Superprojekt

open Aardvark.Base
open FSharp.Data.Adaptive
open Aardvark.Dom

// Left workflow rail (spec §2): a vertical stepper — 1 Reference · 2 Coarse
// align · 3 Fine ICP · 4 Inspect · 5 Commit — with one step expanded at a time
// and a PINS list underneath. A near-pure view: every control dispatches an
// existing message; it never issues server queries itself. Replaces the old
// left mesh panel + floating registration panel.
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
        let previewOn = model.PendingReg |> AVal.map PendingRegistration.isPreview
        let pinsVal   = model.ScanPins.Pins |> AMap.toAVal
        let flyTo (target : FlyToTarget) =
            let s = AVal.force viewportSize
            env.Emit [FlyTo(target, float s.X / float (max 1 s.Y))]

        // ── per-step readiness pill ───────────────────────────────────────
        let stepStatus (step : WorkflowStep) : aval<Pill * string> =
            AVal.custom (fun t ->
                let hasRef = (model.Registration.GetValue t).ReferenceMesh |> Option.isSome
                let preview = PendingRegistration.isPreview (model.PendingReg.GetValue t)
                let solved = not (Map.isEmpty (model.LastSolve.GetValue t))
                match step with
                | StepReference ->
                    if hasRef then PillReady, "reference set"
                    else PillBlock, "pick a reference ★"
                | StepManualMove ->
                    if not hasRef then PillBlock, "needs a reference"
                    else PillInfo, "drag in the focus panel to translate"
                | StepCorrespondences ->
                    if not hasRef then PillBlock, "needs a reference"
                    elif preview then PillReady, "preview ready"
                    elif solved then PillReady, "aligned"
                    else PillInfo, "place ≥3 correspondences, then solve"
                | StepFine ->
                    if not solved && not preview then PillInfo, "optional · coarse first"
                    else PillInfo, "optional"
                | StepInspect ->
                    if hasRef then PillInfo, "error layers"
                    else PillBlock, "needs a reference"
                | StepCommit ->
                    if preview then PillReady, "ready to commit"
                    else PillInfo, "nothing pending")

        // ── stepper header ────────────────────────────────────────────────
        let stepHeader (step : WorkflowStep) =
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
                    Attribute("title", "")
                    status |> AVal.map (fun (p, _) ->
                        match p with PillReady -> "✔" | PillWarn -> "⚠" | PillBlock -> "✖" | PillInfo -> "•")
                }
            }

        let stepBody (step : WorkflowStep) (body : DomNode) =
            div {
                Class "rail-body"
                showWhen (curStep |> AVal.map ((=) step))
                div { Class "rail-hint"; (stepStatus step) |> AVal.map snd }
                body
            }

        // ── 1 Reference: mesh list (ref / visibility / sensor / frame) ─────
        let meshRow (name : string) =
            let isVis  = model.MeshVisible |> AVal.map (fun m -> Map.tryFind name m |> Option.defaultValue true)
            let isRef  = refMesh |> AVal.map ((=) (Some name))
            let idxVal = model.MeshOrder |> AMap.tryFind name |> AVal.map (Option.defaultValue 0)
            let colorVal = idxVal |> AVal.map meshColor
            let sensor = model.MeshSensorTypes |> AVal.map (fun m -> Map.tryFind name m |> Option.defaultValue UnknownSensor)
            div {
                Class "rail-mesh-row"
                isVis |> AVal.map (fun v -> if v then None else Some (Class "rail-row-dim"))
                span { Class "mesh-swatch"; colorVal |> AVal.map (fun c -> Some (Style [Css.Background (hex c)])) }
                span { Class "mesh-num"; idxVal |> AVal.map (fun i -> string (i + 1)) }
                span { Class "rail-mesh-name"; Attribute("title", name); shortName name }
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
        let meshList =
            model.MeshNames |> AList.map meshRow

        // ── PINS list (all steps) ─────────────────────────────────────────
        let pinList =
            pinsVal
            |> AVal.map (fun pins ->
                pins |> HashMap.toList |> List.sortBy (fun (_, p) -> p.CreatedAt)
                |> List.map snd |> IndexList.ofList)
            |> AList.ofAVal
        let pinRow (pin : ScanPin) =
            let selected = model.ScanPins.SelectedPin |> AVal.map ((=) (Some pin.Id))
            let isCorr =
                pin.Correspondence |> Option.map (fun c -> c.Enabled) |> Option.defaultValue false
            div {
                Class "rail-pin-row"
                selected |> AVal.map (fun s -> if s then Some (Class "rail-pin-sel") else None)
                // §G brushing: pin-row hover brightens this pin's 3D constellation.
                Dom.OnPointerMove(fun _ -> env.Emit [SetWorkflowPinHover (Some pin.Id)])
                Dom.OnMouseLeave(fun _ -> env.Emit [SetWorkflowPinHover None])
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
                    Class "mb"
                    Attribute("title", "Edit position / radius")
                    Dom.OnClick(fun _ -> env.Emit [EditPin pin.Id])
                    "✎"
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

        // ── step bodies ───────────────────────────────────────────────────
        let referenceBody =
            div { Class "rail-mesh-list"; meshList }

        let manualMoveBody =
            div {
                Class "rail-step-controls"
                div { Class "rail-note"; "Drag in the focus panel (Top/Front/Side) to translate the selected moving mesh in the view plane. Others ghost out automatically." }
            }

        let correspondencesBody =
            // Readiness diagnostics (revived engine) — blocker/warning/ready with
            // one-click nav actions (NavTo). The dock manager hosts the rows + solve.
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
                div { Class "rail-diags"; diags |> AVal.map IndexList.ofList |> AList.ofAVal |> AList.map diagRow }
            }

        let fineBody =
            let mode = model.Registration |> AVal.map (fun r -> r.Mode)
            div {
                Class "rail-step-controls"
                div { Class "rail-note"; "Optional. Refine with ICP after a coarse alignment." }
                div {
                    Class "rail-toggle-row"
                    compactToggle "Region-restricted (weight toward pins)"
                        (mode |> AVal.map (fun m -> m = RegionRestrictedIcp))
                        (fun () ->
                            let m = AVal.force mode
                            env.Emit [SetRegistrationMode (if m = RegionRestrictedIcp then TraditionalIcp else RegionRestrictedIcp)])
                }
                button {
                    Class "rail-btn"
                    Dom.OnClick(fun _ -> env.Emit [RunRegistration])
                    "Run / re-run ICP"
                }
            }

        let inspectBody =
            div {
                Class "rail-step-controls"
                div { Class "rail-note"; "Error layers and pin glyphs render in the viewport. (Heatmaps rebuild in a later step.)" }
                div {
                    Class "rail-toggle-row"
                    compactToggle "Pin focus — ghost outside the focused pin's ROI"
                        model.PinFocusMode (fun () -> env.Emit [TogglePinFocus])
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
                    Class "rail-toggle-row"
                    compactToggle "Extrinsic map (paints the soloed moving mesh — click a violin column)"
                        model.SurfaceDistOn (fun () -> env.Emit [ToggleSurfaceDistance])
                }
                div {
                    Class "rail-toggle-row"
                    span { Class "rail-sublabel"; "Extrinsic:" }
                    compactButtonBar [
                        "M3C2", (model.ExtrinsicZDiff |> AVal.map not),  (fun () -> if AVal.force model.ExtrinsicZDiff then env.Emit [ToggleExtrinsicZDiff])
                        "Δz",   (model.ExtrinsicZDiff :> aval<bool>),    (fun () -> if not (AVal.force model.ExtrinsicZDiff) then env.Emit [ToggleExtrinsicZDiff])
                    ]
                }
                div {
                    Class "rail-toggle-row"
                    compactToggle "Variance map — disagreement of all visible moving meshes (≥2), painted on the reference"
                        model.VarianceOn (fun () -> env.Emit [ToggleVariance])
                }
                div {
                    Class "rail-toggle-row"
                    span { Class "rail-sublabel"; "Movement (preview):" }
                    compactButtonBar [
                        "Off",    (model.MovementLayer |> AVal.map (fun m -> m = MovementOff)),    (fun () -> env.Emit [SetMovementLayer MovementOff])
                        "Arrows", (model.MovementLayer |> AVal.map (fun m -> m = MovementGlyphs)), (fun () -> env.Emit [SetMovementLayer MovementGlyphs])
                        "Grid",   (model.MovementLayer |> AVal.map (fun m -> m = MovementGrid)),   (fun () -> env.Emit [SetMovementLayer MovementGrid])
                    ]
                }
            }

        let commitBody =
            div {
                Class "rail-step-controls"
                div {
                    showWhen previewOn
                    div { Class "rail-note"; "Previewing the new pose against the committed one." }
                    div {
                        Class "rail-commit-row"
                        button {
                            Class "rail-btn rail-btn-primary"
                            Dom.OnClick(fun _ -> env.Emit [CommitRegistration])
                            "Commit"
                        }
                        button {
                            Class "rail-btn"
                            Dom.OnClick(fun _ -> env.Emit [DiscardRegistration])
                            "Discard"
                        }
                    }
                }
                div {
                    showWhenNot previewOn
                    div { Class "rail-note"; "Nothing to commit — run a solve first." }
                }
            }

        // ── assembled rail ────────────────────────────────────────────────
        div {
            Class "workflow-rail"
            div {
                Class "rail-steps"
                stepHeader StepReference
                stepBody StepReference referenceBody
                stepHeader StepManualMove
                stepBody StepManualMove manualMoveBody
                stepHeader StepCorrespondences
                stepBody StepCorrespondences correspondencesBody
                stepHeader StepFine
                stepBody StepFine fineBody
                stepHeader StepInspect
                stepBody StepInspect inspectBody
                stepHeader StepCommit
                stepBody StepCommit commitBody
            }
            div {
                Class "rail-pins"
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
            }
        }
