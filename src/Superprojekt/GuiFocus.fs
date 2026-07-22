namespace Superprojekt

open Aardvark.Base
open FSharp.Data.Adaptive
open Aardvark.Dom

// Right focus panel: a large WebGL single (focused mesh, textured, top-down
// pan/zoom + server-raycast correspondence pick) over a small-multiples strip of
// textured thumbnails, both from FocusScene. Head = the selected-pin chip and
// ✎ edit point (the armed correspondence editor).
module GuiFocus =

    let panel (env : Env<Message>) (model : AdaptiveModel) =
        let corrStep = model.WorkflowStep |> AVal.map ((=) Correspondence)

        // The selected pin's identity — the near-black name label (name-only),
        // mirroring the matrix row head + 3D flag.
        let selectedPinId = model.Selection.Active |> AVal.map Selection.pin
        let selectedPin =
            let pinsA = model.ScanPins.Pins |> AMap.toAVal
            (selectedPinId, pinsA) ||> AVal.map2 (fun sel pins ->
                sel |> Option.bind (fun id -> HashMap.tryFind id pins))
        let pinChip =
            div {
                Class "focus-pinchip"
                Primitives.showWhen (selectedPin |> AVal.map Option.isSome)
                selectedPin |> AVal.map (function Some p -> p.ShortName | None -> "")
            }

        // Same resolution rule as FocusScene.single — the head buttons always target
        // the mesh the single shows.
        let focusMesh =
            (model.Selection.Active, model.MeshNames.Content) ||> AVal.map2 (fun sel ns ->
                let names = IndexList.toList ns
                match Selection.mesh sel with
                | Some m when List.contains m names -> Some m
                | _ -> List.tryHead names)

        // The unified correspondence editor is offered with a selected pin + a
        // resolved single mesh (the reference is editable like any other).
        let setAvailable =
            AVal.custom (fun t ->
                corrStep.GetValue t
                && (Selection.pin (model.Selection.Active.GetValue t)).IsSome
                && (focusMesh.GetValue t).IsSome)
        let armedHere =
            AVal.custom (fun t ->
                match model.CorrArm.GetValue t, Selection.pin (model.Selection.Active.GetValue t), focusMesh.GetValue t with
                | Some (ap, am), Some sp, Some fm -> ap = sp && am = fm
                | _ -> false)

        // Unified arm button: one mode, two surfaces. Armed → the next click on
        // the focus OR the 3D surface sets the (pin, mesh) point (ROI-clamped) and
        // ends the edit; an out-of-ROI click toasts and stays armed.
        let setBtn =
            button {
                Class "focus-set"
                Primitives.showWhen setAvailable
                Primitives.classWhen "btn-active" armedHere
                Attribute("title", "Edit correspondence: arm, then click the focus or the 3D surface to set the point")
                Dom.OnClick(fun _ ->
                    match Selection.pin (AVal.force model.Selection.Active), AVal.force focusMesh with
                    | Some pin, Some mesh -> env.Emit [ToggleCorrArm(pin, mesh)]
                    | _ -> ())
                armedHere |> AVal.map (fun on -> if on then "✎ editing…" else "✎ edit point")
            }

        // Overview drops the large single: the focus panel is the tile mesh
        // browser + control strip only.
        let isOverview = model.WorkflowStep |> AVal.map ((=) Overview)
        // Aspect-locked resize handle: drag the left edge; the single's height
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
        div {
            Class "focus-panel"
            Primitives.classWhen "fp-overview" isOverview
            resizeHandle
            div {
                Class "focus-head"
                pinChip
                div {
                    Class "focus-head-right"
                    setBtn
                }
            }
            div { Class "focus-single"; Primitives.showWhenNot isOverview; FocusScene.single env model }
            div { Class "focus-multiples"; FocusScene.multiples env model }
        }
