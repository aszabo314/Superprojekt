namespace Superprojekt

open Aardvark.Base
open FSharp.Data.Adaptive
open Aardvark.Dom

// Right focus panel: a large WebGL single (focused mesh, textured, pan/zoom + GPU
// correspondence pick) over a small-multiples strip of textured thumbnails, both
// from FocusScene. The Pano / Top projection toggle drives the single. Head = the
// projection toggle, ⊕ set point, ⟲ reset, ⇄ peek-reference.
module GuiFocus =

    let panel (env : Env<Message>) (model : AdaptiveModel) =
        let refMeshA = model.Registration |> AVal.map (fun r -> r.ReferenceMesh)
        let corrStep = model.WorkflowStep |> AVal.map ((=) Correspondence)

        // The selected pin's identity (§A) — shown as a colour chip + glyph + name in
        // the focus head (the focus label), mirroring the matrix row + 3D flag.
        let selectedPin =
            let pinsA = model.ScanPins.Pins |> AMap.toAVal
            (model.Selection.SelectedPin, pinsA) ||> AVal.map2 (fun sel pins ->
                sel |> Option.bind (fun id -> HashMap.tryFind id pins))
        let pinChip =
            div {
                Class "focus-pinchip"
                Primitives.showWhen (selectedPin |> AVal.map Option.isSome)
                span {
                    Class "focus-pinchip-sw"
                    selectedPin |> AVal.map (function
                        | Some p -> Some (Style [Css.Background (Primitives.c4bToRgbCss p.PinColor)])
                        | None -> None)
                }
                selectedPin |> AVal.map (function Some p -> sprintf "%s %s" p.Glyph p.ShortName | None -> "")
            }

        // A hard solo in the main view falls back to its restore set so the focus
        // still resolves a mesh.
        let visibleMeshes =
            AVal.custom (fun t ->
                let names = model.MeshNames.Content.GetValue t |> IndexList.toList
                let vis =
                    match model.MeshSolo.GetValue t with
                    | Solo(_, restore) -> restore
                    | NoSolo -> model.MeshVisible.GetValue t
                names |> List.filter (fun n -> Map.tryFind n vis |> Option.defaultValue true))
        let focusMesh =
            (model.Selection.FocusedMesh, visibleMeshes) ||> AVal.map2 (fun fm vis ->
                match fm with
                | Some m when List.contains m vis -> Some m
                | _ -> List.tryHead vis)

        // The unified correspondence editor is offered with a selected pin + a focused
        // mesh (the reference is editable like any other, §T4).
        let setAvailable =
            AVal.custom (fun t ->
                corrStep.GetValue t
                && (model.Selection.SelectedPin.GetValue t).IsSome
                && (focusMesh.GetValue t).IsSome)
        let armedHere =
            AVal.custom (fun t ->
                match model.CorrArm.GetValue t, model.Selection.SelectedPin.GetValue t, focusMesh.GetValue t with
                | Some (ap, am), Some sp, Some fm -> ap = sp && am = fm
                | _ -> false)

        let projBtn (p : FocusProjection) =
            button {
                Class "focus-proj-btn"
                Primitives.classWhen "btn-active" (model.FocusProjection |> AVal.map ((=) p))
                Dom.OnClick(fun _ -> env.Emit [SetFocusProjection p])
                FocusProjection.label p
            }

        let resetBtn =
            button {
                Class "focus-reset"
                Attribute("title", "Reset pan / zoom")
                Dom.OnClick(fun _ -> FocusScene.resetCam (AVal.force focusMesh))
                "⟲ reset"
            }

        // Locate back-out: shown only while a "frame correspondence" is in effect;
        // restores the camera + solo and clears the focus zoom.
        let backBtn =
            button {
                Class "focus-back"
                Primitives.showWhen (model.LocateBackup |> AVal.map Option.isSome)
                Attribute("title", "Back out of locate: restore camera + visibility")
                Dom.OnClick(fun _ ->
                    env.Emit [BackOutLocate]
                    FocusScene.resetCam (AVal.force focusMesh))
                "⤺ back"
            }

        // Link-views toggle (off by default): focus click flies the 3D camera, a 3D
        // recenter recenters the focus canvas. Pure camera.
        let linkBtn =
            button {
                Class "focus-link"
                Primitives.classWhen "btn-active" model.LinkViews
                Attribute("title", "Link views: focus click flies the 3D camera; 3D recenter recenters the focus")
                Dom.OnClick(fun _ -> env.Emit [ToggleLinkViews])
                model.LinkViews |> AVal.map (fun on -> if on then "⇄ linked" else "⇄ link")
            }

        // Unified arm button (§T4): one mode, two surfaces. Armed → clicking the focus
        // OR the 3D surface sets the (pin, mesh) point (ROI-clamped); stays armed.
        let setBtn =
            button {
                Class "focus-set"
                Primitives.showWhen setAvailable
                Primitives.classWhen "btn-active" armedHere
                Attribute("title", "Edit correspondence: arm, then click the focus or the 3D surface to set the point")
                Dom.OnClick(fun _ ->
                    match AVal.force model.Selection.SelectedPin, AVal.force focusMesh with
                    | Some pin, Some mesh -> env.Emit [ToggleCorrArm(pin, mesh)]
                    | _ -> ())
                armedHere |> AVal.map (fun on -> if on then "✎ editing…" else "✎ edit point")
            }

        // Overview drops the large single (T3): the focus panel is the tile mesh
        // browser + control strip only. Other modes keep the single + tile strip.
        let isOverview = model.WorkflowStep |> AVal.map ((=) Overview)
        // Aspect-locked resize handle (§T10): drag the left edge; the single's height
        // tracks the width (0.72 ratio). Pure JS on a fixed-position panel.
        let resizeHandle =
            div {
                Class "focus-resize"
                Attribute("title", "Drag to resize the focus panel (aspect-locked)")
                OnBoot [
                    "(function(){"
                    "var h=__THIS__; var panel=h.closest('.focus-panel'); if(!panel) return;"
                    "var dragging=false, startX=0, startW=0;"
                    "function setW(w){ w=Math.max(280,Math.min(820,w)); panel.style.width=w+'px';"
                    "  var sgl=panel.querySelector('.focus-single'); if(sgl) sgl.style.height=Math.round(w*0.72)+'px'; }"
                    "h.addEventListener('pointerdown',function(e){ dragging=true; startX=e.clientX; startW=panel.getBoundingClientRect().width; h.setPointerCapture(e.pointerId); e.preventDefault(); e.stopPropagation(); });"
                    "h.addEventListener('pointermove',function(e){ if(!dragging) return; setW(startW + (startX - e.clientX)); });"
                    "h.addEventListener('pointerup',function(e){ dragging=false; try{h.releasePointerCapture(e.pointerId);}catch(_){} });"
                    "})();" ]
            }
        // Displacement glyph legend (§T10): moved into the focus pane; explains the
        // load→solved arrow colour ramp. Only in the Inspect Displacement channel.
        let dispLegend =
            let show = (model.WorkflowStep, model.InspectChannel) ||> AVal.map2 (fun s c -> s = Inspect && c = ChDisplacement)
            div {
                Class "focus-displeg"
                Primitives.showWhen show
                "↗ load → solved   ·   light = small shift, dark = large"
            }
        div {
            Class "focus-panel"
            Primitives.classWhen "fp-overview" isOverview
            resizeHandle
            div {
                Class "focus-head"
                span { Class "focus-title"; "Focus" }
                pinChip
                div { Class "focus-proj"; Primitives.showWhenNot isOverview; projBtn ProjPano; projBtn ProjTop }
                div {
                    Class "focus-head-right"
                    setBtn
                    backBtn
                    linkBtn
                    resetBtn
                }
            }
            div { Class "focus-single"; Primitives.showWhenNot isOverview; FocusScene.single env model }
            dispLegend
            div { Class "focus-multiples"; FocusScene.multiples env model }
        }
