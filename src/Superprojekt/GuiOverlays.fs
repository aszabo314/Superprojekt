namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open FSharp.Data.Adaptive
open Aardvark.Dom

module GuiOverlays =

    let meshWheelLabel (model : AdaptiveModel) (cursorScreen : aval<V2d option>) =
        div {
            Class "mesh-wheel-label"
            (model.ActivePickingLayer, cursorScreen) ||> AVal.map2 (fun layer cOpt ->
                match layer, cOpt with
                | Some _, Some pos ->
                    Some (Style [
                        Left (sprintf "%.0fpx" (pos.X + 14.0))
                        Top  (sprintf "%.0fpx" (pos.Y - 10.0))
                    ])
                | _ -> Some (Style [Display "none"]))
            model.ActivePickingLayer |> AVal.map (function
                | Some name -> Cards.shortName name
                | None -> "")
        }

    // Measurement rulers: one HTML label per accepted anchor↔reference of the
    // selected pin, at the connector midpoint. Default shows the distance
    // (the live pair gap = the per-pair residual once a solve preview shrinks
    // the gap); the title carries the endpoints. HTML so it is always legible
    // and depth-tested against overlays, not meshes.
    let rulerOverlay (model : AdaptiveModel) (view : aval<Trafo3d>) (viewportSize : aval<V2i>) =
        let projectToScreen (p : V3d) (viewTrafo : Trafo3d) (vp : V2i) =
            let aspect = float vp.X / max 1.0 (float vp.Y)
            let proj = Frustum.perspective 90.0 1.0 5000.0 aspect |> Frustum.projTrafo
            let h = (proj.Forward * viewTrafo.Forward) * V4d(p, 1.0)
            if h.W < 0.1 then None
            else
                let ndc = h.XYZ / h.W
                if abs ndc.X > 1.2 || abs ndc.Y > 1.2 then None
                else Some (V2d((ndc.X * 0.5 + 0.5) * float vp.X, (1.0 - (ndc.Y * 0.5 + 0.5)) * float vp.Y))
        let labels =
            AVal.custom (fun t ->
                if not (model.RulerActive.GetValue t) then []
                else
                    let sel =
                        match model.ScanPins.Placement.GetValue t with
                        | AdjustingPin id -> Some id
                        | _ -> model.ScanPins.SelectedPin.GetValue t
                    match sel |> Option.bind (fun id -> HashMap.tryFind id ((model.ScanPins.Pins |> AMap.toAVal).GetValue t)) with
                    | Some pin ->
                        match ScanPin.correspondence pin with
                        | Some corr when corr.Enabled ->
                            match corr.RefAnchor with
                            | Some refA ->
                                let pending = model.PendingReg.GetValue t
                                let transforms = model.MeshTransforms.GetValue t
                                let scales = model.DatasetScales.GetValue t
                                let cc = model.CommonCentroid.GetValue t
                                let s = DatasetScale.active (model.ActiveDataset.GetValue t) scales
                                let vtr = view.GetValue t
                                let vp = viewportSize.GetValue t
                                let refR = ScanPin.renderCentre cc s refA
                                corr.Anchors |> Map.toList |> List.choose (fun (mesh, a) ->
                                    if not a.Accepted then None
                                    else
                                        let hasDelta = (PendingRegistration.delta mesh pending).IsSome
                                        let aw =
                                            match PendingRegistration.delta mesh pending with
                                            | Some d ->
                                                let scale = DatasetScale.forMesh scales mesh
                                                let cT = Map.tryFind mesh transforms |> Option.defaultValue Trafo3d.Identity
                                                let wb = RigidTransform.renderToWorld scale cc cT
                                                let wa = RigidTransform.renderToWorld scale cc (RegLog.effective cT d)
                                                (wb.Inverse * wa).Forward.TransformPos a.Point
                                            | None -> a.Point
                                        let mid = (ScanPin.renderCentre cc s aw + refR) * 0.5
                                        match projectToScreen mid vtr vp with
                                        | Some px -> Some (mesh, px, (aw - refA).Length, hasDelta)
                                        | None -> None)
                            | None -> []
                        | _ -> []
                    | None -> [])
        div {
            Class "ruler-overlay"
            labels |> AVal.map IndexList.ofList |> AList.ofAVal |> AList.map (fun (mesh, px, dist, hasDelta) ->
                div {
                    Class "ruler-label"
                    Style [Left (sprintf "%.0fpx" px.X); Top (sprintf "%.0fpx" px.Y)]
                    Attribute("title",
                        sprintf "%s ↔ reference: %.3f m (%s)" (Cards.shortName mesh) dist
                            (if hasDelta then "residual" else "pre-alignment gap"))
                    sprintf "%.3f m" dist
                })
        }

    // Ctrl-click hover probe: compressed ridgeline at the cursor,
    // kept inside the viewport; dismissed by Escape / click / timeout.
    let hoverProbeTooltip (model : AdaptiveModel) (viewportSize : aval<V2i>) =
        let posStyle =
            (model.HoverProbe, viewportSize) ||> AVal.map2 (fun hp vp ->
                match hp with
                | Some h ->
                    let x = max 0.0 (min (h.ScreenPos.X + 14.0) (float vp.X - 256.0))
                    let y = max 0.0 (min (h.ScreenPos.Y + 14.0) (float vp.Y - 190.0))
                    Some (Style [
                        Left (sprintf "%.0fpx" x)
                        Top  (sprintf "%.0fpx" y)
                    ])
                | None -> Some (Style [Display "none"]))
        let colors =
            model.MeshOrder |> AMap.toAVal |> AVal.map (fun order ->
                order |> HashMap.toSeq
                |> Seq.map (fun (n, i) -> n, Primitives.meshColor i)
                |> Map.ofSeq)
        let json =
            (model.HoverProbe, colors) ||> AVal.map2 (fun hp cols ->
                match hp with
                | Some h -> CardsPin.probeStateJson true false ProbeXAuto None cols None h.Probe
                | None -> "{\"status\":\"none\"}")
        div {
            Class "hover-probe-tip"
            posStyle
            json |> AVal.map (fun j -> Some (Attribute("data-ridge", j)))
            Primitives.observedRender "data-ridge" "{}" CardsPin.ridgelineJs
        }

    // Heatmap probe under the cursor. Sources mode reuses
    // Provenance.sourcesAt so the numbers agree with the shader; Diff mode
    // shows the signed combined-error change and the detection limit (LoD).
    let provenanceHoverOverlay
            (model : AdaptiveModel)
            (hoverWorld : aval<V3d option>)
            (cursorScreen : aval<V2d option>) =
        // (cursor px, mesh label, numbers line, bar json)
        let payload =
            AVal.custom (fun t ->
                let mode = model.HeatmapMode.GetValue t
                let cOpt = cursorScreen.GetValue t
                let wOpt = hoverWorld.GetValue t
                let layer = model.ActivePickingLayer.GetValue t
                match mode, cOpt, wOpt with
                | HeatOff, _, _ | _, None, _ | _, _, None -> None
                | HeatProvenance, Some px, Some w ->
                    let sensors   = model.MeshSensorTypes.GetValue t
                    let overrides = model.MeshDatasetErrors.GetValue t
                    let algo      = model.MeshAlgorithmResidual.GetValue t
                    let pinsMap   = (model.ScanPins.Pins |> AMap.toAVal).GetValue t
                    let anchors =
                        pinsMap |> HashMap.toSeq
                        |> Seq.choose (fun (_, p) ->
                            if p.Phase = PinPhase.Committed then Some (p.Centre, p.FalloffRadius)
                            else None)
                        |> Array.ofSeq
                    let mesh = layer |> Option.defaultValue ""
                    let d, a, c = Provenance.sourcesAt mesh overrides sensors algo w anchors
                    let label =
                        match layer with
                        | Some name -> Cards.shortName name
                        | None -> "— no layer —"
                    let cM = c * 0.01
                    let total = max 1e-6 (d + a + cM)
                    let bar =
                        sprintf "[%.1f,%.1f,%.1f]"
                            (d / total * 100.0) (a / total * 100.0) (cM / total * 100.0)
                    Some (px, label, sprintf "D %.3fm • A %.3fm • C %.0f" d a c, bar)
                | HeatDiff, Some px, Some w ->
                    match layer with
                    | None -> Some (px, "— no layer —", "wheel-cycle onto a mesh layer for Δ", "[]")
                    | Some mesh ->
                        let pending = model.PendingReg.GetValue t
                        match pending |> Option.bind (fun pr -> Map.tryFind mesh pr.Results) with
                        | None -> Some (px, Cards.shortName mesh, "no pending delta for this mesh", "[]")
                        | Some res ->
                            let sensors   = model.MeshSensorTypes.GetValue t
                            let overrides = model.MeshDatasetErrors.GetValue t
                            let pinsMap   = (model.ScanPins.Pins |> AMap.toAVal).GetValue t
                            let anchors =
                                pinsMap |> HashMap.toSeq
                                |> Seq.choose (fun (_, p) ->
                                    if p.Phase = PinPhase.Committed then Some (p.Centre, p.FalloffRadius)
                                    else None)
                                |> Array.ofSeq
                            // committed-pose position of the hovered point
                            let scale = DatasetScale.forMesh (model.DatasetScales.GetValue t) mesh
                            let cc = model.CommonCentroid.GetValue t
                            let committed =
                                Map.tryFind mesh (model.MeshTransforms.GetValue t)
                                |> Option.defaultValue Trafo3d.Identity
                            let wb = RigidTransform.renderToWorld scale cc committed
                            let wa = RigidTransform.renderToWorld scale cc (RegLog.effective committed res.Delta)
                            let deltaW = wb.Inverse * wa
                            let wc = deltaW.Backward.TransformPos w
                            let condP = Provenance.localConditioning w anchors
                            let condC = Provenance.localConditioning wc anchors
                            let algoBefore =
                                Map.tryFind mesh (model.MeshAlgorithmResidual.GetValue t)
                                |> Option.defaultValue 0.0
                            let combinedP = res.RmsAfter + 0.01 * min (condP * 0.01) 50.0
                            let combinedC = algoBefore + 0.01 * min (condC * 0.01) 50.0
                            let dd = combinedP - combinedC
                            let sigmaRef =
                                match (model.Registration.GetValue t).ReferenceMesh with
                                | Some r -> Provenance.datasetError overrides sensors r
                                | None -> 0.0
                            let sigmaM = Provenance.datasetError overrides sensors mesh
                            let lod = 1.96 * sqrt (sigmaRef * sigmaRef + sigmaM * sigmaM)
                            let verdict =
                                if abs dd < lod then "below detection"
                                elif dd < 0.0 then "improved"
                                else "degraded"
                            Some (px, Cards.shortName mesh,
                                  sprintf "Δ %+.4f m • LoD %.4f m • %s" dd lod verdict, "[]")
                | _ -> None)
        let visStyle =
            payload |> AVal.map (fun p ->
                match p with
                | Some (px, _, _, _) ->
                    Some (Style [
                        Left (sprintf "%.0fpx" (px.X + 16.0))
                        Top  (sprintf "%.0fpx" (px.Y + 18.0))
                    ])
                | None -> Some (Style [Display "none"]))
        let label =
            payload |> AVal.map (function
                | Some (_, l, _, _) -> l
                | None -> "")
        let nums =
            payload |> AVal.map (function
                | Some (_, _, n, _) -> n
                | None -> "")
        let barAttr =
            payload |> AVal.map (function
                | Some (_, _, _, bar) -> Some (Attribute("data-prov", bar))
                | None -> Some (Attribute("data-prov", "[]")))
        div {
            Class "prov-hover"
            visStyle
            div { Class "prov-hover-mesh"; label }
            div {
                Class "pc-bar prov-hover-bar"
                barAttr
                Primitives.observedRender "data-prov" "[]" Primitives.provBarJs
            }
            div { Class "prov-hover-nums"; nums }
        }

    // Thin banner while a registration solve preview is pending (spec §6).
    let previewBanner (model : AdaptiveModel) (setSwap : bool -> unit) =
        div {
            Class "preview-banner"
            Primitives.showWhen (model.PendingReg |> AVal.map PendingRegistration.isPreview)
            span { Class "preview-banner-text"; "Previewing unregistered result — commit or discard" }
            // Hold to compare: render-time swap to the committed (before) pose.
            button {
                Class "preview-banner-swap"
                Attribute("title", "Hold to compare: shows the committed (before) pose while held")
                Dom.OnPointerDown((fun _ -> setSwap true), pointerCapture = true)
                Dom.OnPointerUp((fun _ -> setSwap false), pointerCapture = true)
                "⇄ Hold: before"
            }
        }

    // Transient feedback for blocked/failed actions (auto-clears).
    let toast (model : AdaptiveModel) =
        div {
            Class "app-toast"
            Primitives.showWhen (model.Toast |> AVal.map Option.isSome)
            model.Toast |> AVal.map (Option.defaultValue "")
        }

    let fusionNotice (model : AdaptiveModel) =
        div {
            Class "fusion-notice"
            Primitives.showWhen ((model.FusionMode, model.MeshTransforms) ||> AVal.map2 (fun f m -> f && Map.isEmpty m))
            "◈ Fusion shows the reference mesh until you register. Run a registration to fuse the visible meshes by lowest error."
        }

    let lassoOverlay (env : Env<Message>) (model : AdaptiveModel) (cursorScreen : aval<V2d option>) =
        let stateJson =
            (model.LassoDrawing, cursorScreen) ||> AVal.map2 (fun drawing cursor ->
                let drawingArr =
                    match drawing with
                    | Some d -> d.Vertices
                    | None -> [||]
                let cursorArr =
                    match drawing, cursor with
                    | Some _, Some c -> [| c |]
                    | _ -> [||]
                let fmtArr (a : V2d[]) =
                    a |> Array.map (fun p -> sprintf "[%.1f,%.1f]" p.X p.Y) |> String.concat ","
                sprintf "{\"d\":[%s],\"c\":[%s]}"
                    (fmtArr drawingArr) (fmtArr cursorArr))
        div {
            Class "lasso-overlay"
            stateJson |> AVal.map (fun j -> Some (Attribute("data-lasso", j)))
            Primitives.observedRender "data-lasso" "{}" [
                "  function poly(points, attrs){"
                "    var p = document.createElementNS(ns, 'polyline');"
                "    p.setAttribute('points', points.map(function(pt){return pt[0]+','+pt[1];}).join(' '));"
                "    for(var k in attrs) p.setAttribute(k, attrs[k]);"
                "    return p;"
                "  }"
                "  var svg = document.createElementNS(ns, 'svg');"
                "  svg.setAttribute('class','lasso-svg');"
                "  var rect = el.getBoundingClientRect();"
                "  svg.setAttribute('width', rect.width);"
                "  svg.setAttribute('height', rect.height);"
                "  el.appendChild(svg);"
                "  if(d.d && d.d.length > 0){"
                "    if(d.d.length >= 2)"
                "      svg.appendChild(poly(d.d, {stroke:'#0f172a','stroke-width':'1.5',fill:'none'}));"
                "    d.d.forEach(function(pt){"
                "      var c = document.createElementNS(ns, 'circle');"
                "      c.setAttribute('cx', pt[0]); c.setAttribute('cy', pt[1]);"
                "      c.setAttribute('r','3'); c.setAttribute('fill','#0f172a');"
                "      svg.appendChild(c);"
                "    });"
                "    if(d.c && d.c.length > 0){"
                "      var lastPt = d.d[d.d.length-1];"
                "      var line = document.createElementNS(ns, 'line');"
                "      line.setAttribute('x1', lastPt[0]); line.setAttribute('y1', lastPt[1]);"
                "      line.setAttribute('x2', d.c[0][0]); line.setAttribute('y2', d.c[0][1]);"
                "      line.setAttribute('stroke','#0f172a'); line.setAttribute('stroke-width','1');"
                "      line.setAttribute('stroke-dasharray','4,4');"
                "      svg.appendChild(line);"
                "    }"
                "  }"
            ]
        }

    let private niceRoundDistance (d : float) =
        if d <= 0.0 || System.Double.IsNaN d || System.Double.IsInfinity d then 1.0
        else
            let steps = [| 0.1; 0.2; 0.5; 1.0; 2.0; 5.0; 10.0; 20.0; 50.0; 100.0; 200.0; 500.0; 1000.0 |]
            let mag = 10.0 ** floor (log10 d)
            let norm = d / mag
            let picked =
                if norm < 0.15 then 0.1
                elif norm < 0.35 then 0.2
                elif norm < 0.75 then 0.5
                elif norm < 1.5 then 1.0
                elif norm < 3.5 then 2.0
                elif norm < 7.5 then 5.0
                else 10.0
            let v = picked * mag
            steps |> Array.minBy (fun s -> abs (log (s / v)))

    let private formatMeters (m : float) =
        if m >= 1000.0 then sprintf "%g km" (m / 1000.0)
        elif m >= 1.0 then sprintf "%g m" m
        else sprintf "%g cm" (m * 100.0)

    let scaleBar (model : AdaptiveModel) (viewportSize : aval<V2i>) =
        let targetPx = 100.0
        let barInfo =
            AVal.custom (fun t ->
                let radius = model.Camera.radius.GetValue t
                let vp = viewportSize.GetValue t
                let ds = model.ActiveDataset.GetValue t
                let scales = model.DatasetScales.GetValue t
                let scale =
                    match ds with
                    | Some d -> Map.tryFind d scales |> Option.defaultValue 1.0
                    | None -> 1.0
                let h = max 1 vp.Y
                let verticalFov = 90.0 * Constant.RadiansPerDegree
                let renderPerPixel = 2.0 * tan (verticalFov * 0.5) * radius / float h
                let realAt100 = targetPx * renderPerPixel / scale
                let nice = niceRoundDistance realAt100
                let px = nice * scale / renderPerPixel
                let px = if System.Double.IsNaN px || System.Double.IsInfinity px then targetPx else max 10.0 (min 400.0 px)
                px, formatMeters nice)
        let barPx = barInfo |> AVal.map fst
        let barLabel = barInfo |> AVal.map snd
        div {
            Class "scale-bar"
            div {
                Class "sb-bar"
                barPx |> AVal.map (fun px -> Some (Style [Width (sprintf "%.0fpx" px)]))
                span { Class "sb-cap sb-cap-l" }
                span { Class "sb-line" }
                span { Class "sb-cap sb-cap-r" }
            }
            div { Class "sb-label"; barLabel }
        }

    let orientationIndicator (model : AdaptiveModel) =
        let axisJson =
            model.Camera.view |> AVal.map (fun cv ->
                let vt = CameraView.viewTrafo cv
                let tr (v : V3d) = vt.Forward.TransformDir v
                let x = tr V3d.IOO
                let y = tr V3d.OIO
                let z = tr V3d.OOI
                let fmt (v : V3d) (name : string) (color : string) =
                    sprintf "{\"x\":%f,\"y\":%f,\"z\":%f,\"n\":\"%s\",\"c\":\"%s\"}" v.X v.Y v.Z name color
                sprintf "[%s,%s,%s]"
                    (fmt x "X" "#dc2626")
                    (fmt y "Y" "#16a34a")
                    (fmt z "Z" "#2563eb"))
        div {
            Class "orient-indicator"
            axisJson |> AVal.map (fun json -> Some (Attribute("data-axes", json)))
            Primitives.observedRender "data-axes" "[]" [
                "  var W = 60, H = 60, L = 22, cx = W/2, cy = H/2;"
                "  var svg = document.createElementNS(ns, 'svg');"
                "  svg.setAttribute('width', W); svg.setAttribute('height', H);"
                "  svg.setAttribute('viewBox', '0 0 ' + W + ' ' + H);"
                "  d.sort(function(a,b){return a.z - b.z;});"
                "  d.forEach(function(a){"
                "    var ex = cx + a.x * L, ey = cy - a.y * L;"
                "    var ln = document.createElementNS(ns, 'line');"
                "    ln.setAttribute('x1', cx); ln.setAttribute('y1', cy);"
                "    ln.setAttribute('x2', ex); ln.setAttribute('y2', ey);"
                "    ln.setAttribute('stroke', a.c); ln.setAttribute('stroke-width','2');"
                "    ln.setAttribute('stroke-linecap','round');"
                "    svg.appendChild(ln);"
                "    if(a.z > -0.2){"
                "      var tx = document.createElementNS(ns, 'text');"
                "      tx.setAttribute('x', cx + a.x * (L + 6));"
                "      tx.setAttribute('y', cy - a.y * (L + 6) + 3);"
                "      tx.setAttribute('fill', a.c);"
                "      tx.setAttribute('font-size','9');"
                "      tx.setAttribute('font-family','monospace');"
                "      tx.setAttribute('text-anchor','middle');"
                "      tx.textContent = a.n;"
                "      svg.appendChild(tx);"
                "    }"
                "  });"
                "  el.appendChild(svg);"
            ]
        }

    let fullscreenInfo (model : AdaptiveModel) =
        div {
            Class "fullscreen-info"
            Primitives.showWhen model.FullscreenOn
            model.ActiveDataset |> AVal.map (fun ds ->
                match ds with
                | Some d -> div { Class "fullscreen-info-title"; d }
                | None   -> div { []  })
            model.MeshNames |> AList.map (fun name ->
                let order = model.MeshOrder |> AMap.tryFind name |> AVal.map (Option.defaultValue 0)
                div {
                    (order, model.Registration) ||> AVal.map2 (fun o reg ->
                        let star = if reg.ReferenceMesh = Some name then " ★" else ""
                        sprintf "%d  %s%s" (o + 1) (Cards.shortName name) star)
                })
        }
