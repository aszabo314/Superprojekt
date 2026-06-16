namespace Superprojekt

open Aardvark.Base
open FSharp.Data.Adaptive
open Aardvark.Dom

// Registration workflow panel (spec §3): a pure view over the model. Every
// mutation dispatches an existing message; the panel never issues server
// queries. Standard floating-card chrome, four collapsible sections.
module GuiWorkflow =

    open Primitives

    let private sevRank = function
        | Blocker -> 0
        | Warning -> 1
        | Severity.Info -> 2
        | Severity.Ready -> 3

    let private sevClass = function
        | Blocker -> "wfp-diag-blocker"
        | Warning -> "wfp-diag-warning"
        | Severity.Info -> "wfp-diag-info"
        | Severity.Ready -> "wfp-diag-ready"

    let private sevIcon = function
        | Blocker -> "✖"
        | Warning -> "⚠"
        | Severity.Info -> "ℹ"
        | Severity.Ready -> "✔"

    let private stageLabel = function
        | StageCoarse -> "coarse"
        | StageFine -> "fine"

    let private hex (c : C4b) = sprintf "rgb(%d,%d,%d)" (int c.R) (int c.G) (int c.B)

    // Median-offset-across-pins strip (spec WP10): one row per moving mesh,
    // one mark per pin at its signed median offset, shared x-scale, ±LoD band.
    // A flat row (marks aligned) ⇒ a rigid/datum offset (H-A); a varying row
    // ⇒ spatially-varying real change (H-B). Marks post hover/click to the
    // .wfp-medstrip-bus for 3D + pin linking.
    let private medStripJs = [
        "  if(!d || !d.rows || d.rows.length === 0){ var p=document.createElement('div'); p.className='wfp-medstrip-empty'; p.textContent='No per-pin medians yet (enable correspondence pins and probe).'; el.appendChild(p); return; }"
        "  var rows = d.rows;"
        "  var w = Math.max(220, el.clientWidth || 260);"
        "  var labelW = 62, rowH = 18, padTop = 16, padBot = 12;"
        "  var H = padTop + rows.length * rowH + padBot;"
        "  var x0 = d.xmin, x1 = d.xmax; if(!(x1 > x0)){ x0=-0.1; x1=0.1; }"
        "  var plotL = labelW, plotR = w - 8;"
        "  function sx(v){ return plotL + (v - x0)/(x1 - x0) * (plotR - plotL); }"
        "  var svg = document.createElementNS(ns,'svg'); svg.setAttribute('width', w); svg.setAttribute('height', H); svg.setAttribute('viewBox','0 0 '+w+' '+H);"
        "  function ln(xa,ya,xb,yb,st,sw,dash){ var l=document.createElementNS(ns,'line'); l.setAttribute('x1',xa); l.setAttribute('y1',ya); l.setAttribute('x2',xb); l.setAttribute('y2',yb); l.setAttribute('stroke',st); l.setAttribute('stroke-width',sw); if(dash) l.setAttribute('stroke-dasharray',dash); svg.appendChild(l); }"
        "  function tx(x,y,s,a,fill){ var t=document.createElementNS(ns,'text'); t.setAttribute('x',x); t.setAttribute('y',y); t.setAttribute('text-anchor',a||'middle'); t.setAttribute('font-family','SF Mono, Monaco, monospace'); t.setAttribute('font-size','8'); t.setAttribute('fill',fill||'#475569'); t.textContent=s; svg.appendChild(t); }"
        "  var bus = (function(){ var s = el.closest('.wfp-section'); return s ? s.querySelector('.wfp-medstrip-bus') : null; })();"
        "  function send(v){ if(bus){ bus.value=v; bus.dispatchEvent(new Event('input',{bubbles:true})); } }"
        "  tx(sx(x0), padTop-6, x0.toFixed(2), 'start', '#94a3b8'); tx(sx(x1), padTop-6, x1.toFixed(2), 'end', '#94a3b8');"
        "  if(0>=x0 && 0<=x1) ln(sx(0), padTop-4, sx(0), H-padBot, '#64748b','1','3,3');"
        "  rows.forEach(function(r, i){ var cy = padTop + i*rowH + rowH/2;"
        "    tx(4, cy+3, r.name, 'start', '#0f172a');"
        "    if(r.lod>0){ var bx0=Math.max(plotL, sx(-r.lod)), bx1=Math.min(plotR, sx(r.lod)); if(bx1>bx0){ var bnd=document.createElementNS(ns,'rect'); bnd.setAttribute('x',bx0); bnd.setAttribute('y',cy-5); bnd.setAttribute('width',bx1-bx0); bnd.setAttribute('height',10); bnd.setAttribute('fill','#94a3b8'); bnd.setAttribute('fill-opacity','0.16'); svg.appendChild(bnd); } }"
        "    ln(plotL, cy, plotR, cy, '#e2e8f0','1');"
        "    (r.marks||[]).forEach(function(m){ var cxp=Math.max(plotL,Math.min(plotR,sx(m.x))); var c=document.createElementNS(ns,'circle'); c.setAttribute('cx',cxp.toFixed(1)); c.setAttribute('cy',cy); c.setAttribute('r','3.2'); c.setAttribute('fill',m.sig?r.color:'#cbd5e1'); c.setAttribute('stroke','#334155'); c.setAttribute('stroke-width','0.5'); c.style.cursor='pointer'; var ttl=document.createElementNS(ns,'title'); ttl.textContent=m.label+': '+m.x.toFixed(3)+' m'+(m.sig?'':' (n.s.)'); c.appendChild(ttl); c.addEventListener('pointerenter',function(){ send('hover|'+r.mesh); }); c.addEventListener('pointerleave',function(){ send('out'); }); c.addEventListener('click',function(){ send('click|'+m.id); }); svg.appendChild(c); });"
        "  });"
        "  el.appendChild(svg);"
    ]

    let workflowPanel (env : Env<Message>) (model : AdaptiveModel) (viewportSize : aval<V2i>) =
        let dragState : cval<(V2d * V2d) option> = cval None
        let committedPos = cval (V2d(64.0, 110.0))
        let pos = Cards.cardPos (committedPos :> aval<_>) dragState

        let readinessInput = ReadinessView.input model
        let diagnostics = readinessInput |> AVal.map Readiness.compute
        let refMesh = model.Registration |> AVal.map (fun r -> r.ReferenceMesh)
        let pinsVal = model.ScanPins.Pins |> AMap.toAVal
        let flyTo (target : FlyToTarget) =
            let s = AVal.force viewportSize
            env.Emit [FlyTo(target, float s.X / float (max 1 s.Y))]

        // ── 3.1 meshes ─────────────────────────────────────────────────
        let meshRow (name : string) =
            let isVis = model.MeshVisible |> AVal.map (fun m -> Map.tryFind name m |> Option.defaultValue true)
            let isRef = refMesh |> AVal.map ((=) (Some name))
            let colorVal =
                model.MeshOrder |> AMap.tryFind name |> AVal.map (Option.defaultValue 0 >> meshColor)
            // chip: Reference / Fine ✓ / Coarse ✓ / Skipped / Unregistered /
            // Hidden — committed stages from the log, Skipped = a solve ran
            // but this visible moving mesh lacked the 3 accepted pairs.
            let chip =
                AVal.custom (fun t ->
                    let visible =
                        model.MeshVisible.GetValue t |> Map.tryFind name |> Option.defaultValue true
                    if not visible then "Hidden", "wfp-chip-hidden"
                    elif (model.Registration.GetValue t).ReferenceMesh = Some name then
                        "Reference", "wfp-chip-ref"
                    else
                        let log = model.RegistrationLog.GetValue t
                        let hasFine =
                            log |> List.exists (fun st -> st.Stage = StageFine && Map.containsKey name st.Outputs)
                        let hasCoarse =
                            log |> List.exists (fun st -> st.Stage = StageCoarse && Map.containsKey name st.Outputs)
                        if hasFine then "Fine ✓", "wfp-chip-fine"
                        elif hasCoarse then "Coarse ✓", "wfp-chip-coarse"
                        else
                            let ls = model.LastSolve.GetValue t
                            let input = readinessInput.GetValue t
                            let pairs = Readiness.pairCounts input |> Map.ofList
                            if not (Map.isEmpty ls) && not (Map.containsKey name ls)
                               && List.contains name input.VisibleMovingMeshes
                               && (Map.tryFind name pairs |> Option.defaultValue 0) < 3 then
                                "Skipped", "wfp-chip-skipped"
                            else "Unregistered", "wfp-chip-none")
            let lastEntry = model.LastSolve |> AVal.map (Map.tryFind name)
            let condWarn =
                lastEntry |> AVal.map (fun e ->
                    match e with
                    | Some e ->
                        e.Conditioning |> Option.map (fun c -> c.CollinearityWarning) |> Option.defaultValue false
                    | None -> false)
            div {
                Class "wfp-row wfp-mesh-row"
                isVis |> AVal.map (fun v -> if v then None else Some (Class "wfp-row-dim"))
                span {
                    Class "mesh-swatch"
                    colorVal |> AVal.map (fun c -> Some (Style [Css.Background (hex c)]))
                }
                span { Class "wfp-mesh-name"; Attribute("title", name); Cards.shortName name }
                button {
                    Class "mb mb-ref"
                    isRef |> AVal.map (fun r -> if r then Some (Class "mb-on") else None)
                    Attribute("title", "Reference mesh — all error metrics are relative to it")
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
                span {
                    chip |> AVal.map (fun (_, cls) -> Some (Class (sprintf "wfp-chip %s" cls)))
                    chip |> AVal.map fst
                }
                span {
                    Class "wfp-chip wfp-chip-cond"
                    showWhen condWarn
                    Attribute("title", "Last solve flagged near-collinear anchors — rotation weakly constrained")
                    "⚠"
                }
                span {
                    Class "wfp-rms"
                    Attribute("title", "RMS after the last solve of this mesh")
                    lastEntry |> AVal.map (function
                        | Some e -> sprintf "%.3f" e.RmsAfter
                        | None -> "—")
                }
                button {
                    Class "mb"
                    Attribute("title", "Frame this mesh in the viewport")
                    Dom.OnClick(fun _ ->
                        match Map.tryFind name (AVal.force model.MeshBounds) with
                        | Some b -> flyTo (FlyToBounds b)
                        | None -> ())
                    "⌖"
                }
            }

        // ── 3.2 correspondence pins ────────────────────────────────────
        // (pinId, label, host, dots (mesh, colourHex, state), accepted, M,
        // reliability, worst coarse residual, centre, falloff)
        let corrRows =
            AVal.custom (fun t ->
                let pins = pinsVal.GetValue t
                let input = readinessInput.GetValue t
                let order = model.MeshOrder.Content.GetValue t
                let ls = model.LastSolve.GetValue t
                let colourOf m =
                    hex (meshColor (HashMap.tryFind m order |> Option.defaultValue 0))
                pins |> HashMap.toList
                |> List.choose (fun (id, p) ->
                    match ScanPin.correspondence p with
                    | Some c when c.Enabled && p.Phase = PinPhase.Committed ->
                        let dots =
                            input.VisibleMovingMeshes |> List.map (fun m ->
                                let state =
                                    match Map.tryFind m c.Anchors with
                                    | Some a when a.Accepted -> 2   // filled
                                    | Some _ -> 1                   // hollow (seeded)
                                    | None -> 0                     // red ring (missing)
                                m, colourOf m, state)
                        let accepted = dots |> List.filter (fun (_, _, st) -> st = 2) |> List.length
                        let resid =
                            ls |> Map.toList
                            |> List.choose (fun (_, e) -> e.PerPinResiduals |> Option.bind (Map.tryFind id))
                            |> function [] -> None | xs -> Some (List.max xs)
                        Some (id,
                              sprintf "(%.1f, %.1f, %.1f)" p.Centre.X p.Centre.Y p.Centre.Z,
                              p.HostMeshName, dots, accepted,
                              List.length input.VisibleMovingMeshes,
                              resid, p.Centre, p.FalloffRadius)
                    | _ -> None)
                |> IndexList.ofList)

        let otherPins =
            pinsVal |> AVal.map (fun pins ->
                pins |> HashMap.toList
                |> List.choose (fun (id, p) ->
                    let enabled =
                        ScanPin.correspondence p |> Option.map (fun c -> c.Enabled) |> Option.defaultValue false
                    if p.Phase = PinPhase.Committed && not enabled then
                        Some (id, sprintf "(%.1f, %.1f, %.1f)" p.Centre.X p.Centre.Y p.Centre.Z)
                    else None)
                |> IndexList.ofList)

        let pinRow (id, label : string, host : string option, dots, accepted, total, resid, centre, falloff) =
            div {
                Class "wfp-row wfp-pin-row"
                div {
                    Class "wfp-rowmain"
                    Attribute("title", "Select pin and frame it in the viewport")
                    Dom.OnClick(fun _ ->
                        env.Emit [ScanPinMsg (SelectPin (Some id))]
                        flyTo (FlyToSphere(centre, falloff)))
                    span { Class "wfp-pin-label"; label }
                    span {
                        Class "wfp-pin-host"
                        host |> Option.map Cards.shortName |> Option.defaultValue "—"
                    }
                    div {
                        Class "wfp-dots"
                        for (mesh, colour, state) in dots do
                            span {
                                Class (match state with
                                       | 2 -> "wfp-dot wfp-dot-filled"
                                       | 1 -> "wfp-dot wfp-dot-hollow"
                                       | _ -> "wfp-dot wfp-dot-missing")
                                Attribute("title",
                                    sprintf "%s — %s" (Cards.shortName mesh)
                                        (match state with
                                         | 2 -> "accepted"
                                         | 1 -> "seeded, not accepted"
                                         | _ -> "no anchor"))
                                // border + fill via currentColor (the Css API
                                // has no BorderColor; colour is data-driven)
                                Style [ Css.Color (if state = 0 then "#dc2626" else colour) ]
                            }
                    }
                    span { Class "wfp-count"; sprintf "%d/%d" accepted total }
                    span {
                        Class "wfp-resid"
                        Attribute("title", "Worst per-mesh residual of the last coarse solve")
                        let residTxt = match resid with Some r -> sprintf "r %.3f" r | None -> "r —"
                        residTxt
                    }
                }
                button {
                    Class "mb"
                    Attribute("title", "Exclude from the correspondence solve")
                    Dom.OnClick(fun _ -> env.Emit [ToggleCorrespondence id])
                    "⊘"
                }
                button {
                    Class "mb"
                    Attribute("title", "Open the pin card")
                    Dom.OnClick(fun _ -> env.Emit [NavTo (SelectPinOpenCard id)])
                    "▤"
                }
            }

        let othersExpanded = cval false

        // ── 3.3/3.4 derived data ───────────────────────────────────────
        let pendingInfo =
            model.PendingReg |> AVal.map (fun pr ->
                match pr with
                | Some pr when not (Map.isEmpty pr.Results) ->
                    let stage = match pr.Stage with StageCoarse -> "coarse" | StageFine -> "fine"
                    let line =
                        pr.Results |> Map.toList
                        |> List.map (fun (m, r) ->
                            sprintf "%s %.3f→%.3f" (Cards.shortName m) r.RmsBefore r.RmsAfter)
                        |> String.concat " · "
                    Some (stage, line)
                | _ -> None)

        let diagList =
            diagnostics |> AVal.map (fun d ->
                (d.Coarse @ d.Fine)
                |> List.distinctBy (fun x -> x.Text)
                |> List.sortBy (fun x -> sevRank x.Severity)
                |> IndexList.ofList)
            |> AList.ofAVal

        let historyLine =
            model.RegistrationLog |> AVal.map (fun log ->
                match log with
                | [] -> "No registration committed yet."
                | step :: _ ->
                    let outs = step.Outputs |> Map.toList |> List.map snd
                    let rms =
                        if List.isEmpty outs then "—"
                        else
                            sprintf "RMS %.3f→%.3f"
                                (outs |> List.averageBy (fun o -> o.RmsBefore))
                                (outs |> List.averageBy (fun o -> o.RmsAfter))
                    sprintf "#%d %s %s · %s   (%d step%s)"
                        step.Step (stageLabel step.Stage) step.Mode rms
                        (List.length log) (if List.length log = 1 then "" else "s"))

        // per visible moving mesh: last committed before/after + stage +
        // RMS-after series across committed steps (oldest → newest)
        let statsRows =
            AVal.custom (fun t ->
                let log = model.RegistrationLog.GetValue t
                let input = readinessInput.GetValue t
                let chrono = List.rev log
                input.VisibleMovingMeshes
                |> List.map (fun mesh ->
                    let series =
                        chrono
                        |> List.choose (fun st -> Map.tryFind mesh st.Outputs |> Option.map (fun o -> o.RmsAfter))
                        |> Array.ofList
                    let last =
                        log |> List.tryPick (fun st ->
                            Map.tryFind mesh st.Outputs |> Option.map (fun o -> o.RmsBefore, o.RmsAfter))
                    let hasFine =
                        log |> List.exists (fun st -> st.Stage = StageFine && Map.containsKey mesh st.Outputs)
                    let stage =
                        if hasFine then "fine"
                        elif last.IsSome then "coarse"
                        else "—"
                    mesh, last, stage, series)
                |> IndexList.ofList)

        let aggregateLine =
            statsRows |> AVal.map (fun rows ->
                let solved =
                    rows |> IndexList.toList |> List.choose (fun (_, last, _, _) -> last |> Option.map snd)
                let total = IndexList.count rows
                if List.isEmpty solved then sprintf "meshes solved 0/%d" total
                else
                    sprintf "mean %.3f · max %.3f · meshes solved %d/%d"
                        (List.average solved) (List.max solved) (List.length solved) total)

        // Median-offset-across-pins strip data (WP10): per moving mesh, the
        // signed median offset at each enabled committed-probe pin + a
        // representative ±LoD band.
        let medStripJson =
            AVal.custom (fun t ->
                let pins = pinsVal.GetValue t
                let input = readinessInput.GetValue t
                let order = model.MeshOrder.Content.GetValue t
                let colourOf m = hex (meshColor (HashMap.tryFind m order |> Option.defaultValue 0))
                let probed =
                    pins |> HashMap.toList
                    |> List.choose (fun (id, p) ->
                        match ScanPin.correspondence p with
                        | Some c when c.Enabled ->
                            match p.Probe with ProbeReady r -> Some (id, p, r) | _ -> None
                        | _ -> None)
                if List.isEmpty probed || List.isEmpty input.VisibleMovingMeshes then "{\"rows\":[]}"
                else
                    let pinData =
                        probed |> List.map (fun (id, p, r) ->
                            let refStd =
                                r.Distributions |> Array.tryFind (fun d -> d.MeshName = r.ReferenceMesh)
                                |> Option.map (fun d -> d.Std) |> Option.defaultValue 0.0
                            let label = sprintf "(%.0f,%.0f,%.0f)" p.Centre.X p.Centre.Y p.Centre.Z
                            let byMesh =
                                r.Distributions |> Array.choose (fun d ->
                                    if d.Count > 0 then Some (d.MeshName, (d.Median, d.Std)) else None)
                                |> Map.ofArray
                            id, label, refStd, byMesh)
                    let allMed =
                        pinData |> List.collect (fun (_,_,_,bm) -> bm |> Map.toList |> List.map (fun (_, (m,_)) -> m))
                    let xmn0 = if List.isEmpty allMed then -0.1 else List.min allMed
                    let xmx0 = if List.isEmpty allMed then 0.1 else List.max allMed
                    let pad = max 0.05 ((xmx0 - xmn0) * 0.15)
                    let sb = System.Text.StringBuilder()
                    sb.Append(sprintf "{\"xmin\":%.5g,\"xmax\":%.5g,\"rows\":[" (xmn0 - pad) (xmx0 + pad)) |> ignore
                    input.VisibleMovingMeshes |> List.iteri (fun ri mesh ->
                        if ri > 0 then sb.Append(',') |> ignore
                        let marks =
                            pinData |> List.choose (fun (id, label, refStd, bm) ->
                                match Map.tryFind mesh bm with
                                | Some (med, std) ->
                                    let lod = 1.96 * sqrt (refStd*refStd + std*std)
                                    let (ScanPinId.ScanPinId g) = id
                                    Some (g.ToString(), label, med, abs med >= lod, lod)
                                | None -> None)
                        let lodMean =
                            match marks with
                            | [] -> 0.0
                            | _ -> (marks |> List.sumBy (fun (_,_,_,_,l) -> l)) / float marks.Length
                        sb.Append(sprintf "{\"mesh\":\"%s\",\"name\":\"%s\",\"color\":\"%s\",\"lod\":%.5g,\"marks\":["
                                    mesh (Cards.shortName mesh) (colourOf mesh) lodMean) |> ignore
                        marks |> List.iteri (fun mi (g, label, med, sg, _) ->
                            if mi > 0 then sb.Append(',') |> ignore
                            sb.Append(sprintf "{\"id\":\"%s\",\"label\":\"%s\",\"x\":%.5g,\"sig\":%b}" g label med sg) |> ignore)
                        sb.Append("]}") |> ignore)
                    sb.Append("]}") |> ignore
                    sb.ToString())
        let onMedStripEvent (v : string) =
            let parts = v.Split('|')
            match parts.[0] with
            | "hover" when parts.Length >= 2 -> env.Emit [SetChartHoverMesh (Some parts.[1])]
            | "out" -> env.Emit [SetChartHoverMesh None]
            | "click" when parts.Length >= 2 ->
                match System.Guid.TryParse parts.[1] with
                | true, g -> env.Emit [ScanPinMsg (SelectPin (Some (ScanPinId.ScanPinId g)))]
                | _ -> ()
            | _ -> ()

        // ── assembly ───────────────────────────────────────────────────
        div {
            Class "card workflow-panel"
            Cards.cardStyle model.WorkflowPanelOpen pos
            div {
                Class "card-titlebar"
                Cards.cardDragHandle (AVal.constant "Registration workflow") pos dragState (fun p ->
                    transact (fun () -> committedPos.Value <- p))
                button {
                    Class "card-btn-close"
                    Attribute("title", "Close")
                    Dom.OnClick(fun _ -> env.Emit [ToggleWorkflowPanel])
                    "×"
                }
            }
            div {
                Class "card-body workflow-panel-body"

                collapsibleSection "Meshes" true (
                    div {
                        Class "wfp-section"
                        model.MeshNames |> AList.map meshRow
                    })

                collapsibleSection "Correspondence pins" true (
                    div {
                        Class "wfp-section"
                        div {
                            Class "wfp-pairs-line"
                            readinessInput |> AVal.map (fun i ->
                                let counts = Readiness.pairCounts i
                                if List.isEmpty counts then "pairs per mesh: —"
                                else
                                    "pairs per mesh: " +
                                    (counts
                                     |> List.map (fun (m, n) -> sprintf "%s: %d" (Cards.shortName m) n)
                                     |> String.concat "  "))
                        }
                        corrRows |> AList.ofAVal |> AList.map pinRow
                        div {
                            Class "wfp-others-head"
                            showWhen (otherPins |> AVal.map (IndexList.isEmpty >> not))
                            Dom.OnClick(fun _ ->
                                transact (fun () -> othersExpanded.Value <- not othersExpanded.Value))
                            (othersExpanded :> aval<_>, otherPins) ||> AVal.map2 (fun e o ->
                                sprintf "%s Other pins (%d)" (if e then "▾" else "▸") (IndexList.count o))
                        }
                        div {
                            showWhen (othersExpanded :> aval<_>)
                            otherPins |> AList.ofAVal |> AList.map (fun (id, label) ->
                                div {
                                    Class "wfp-row wfp-other-row"
                                    span { Class "wfp-pin-label"; label }
                                    button {
                                        Class "mb"
                                        Attribute("title", "Enable as correspondence landmark (auto-seeds anchors)")
                                        Dom.OnClick(fun _ -> env.Emit [ToggleCorrespondence id])
                                        "＋"
                                    }
                                })
                        }
                    })

                collapsibleSection "Registration status" true (
                    div {
                        Class "wfp-section"
                        div {
                            Class "wfp-pending"
                            showWhen (pendingInfo |> AVal.map Option.isSome)
                            div {
                                Class "wfp-pending-text"
                                pendingInfo |> AVal.map (function
                                    | Some (stage, line) -> sprintf "Previewing %s result — %s" stage line
                                    | None -> "")
                            }
                            div {
                                Class "lp-commit-row"
                                button {
                                    Class "lp-commit"
                                    Dom.OnClick(fun _ -> env.Emit [NavTo CommitPending])
                                    "✓ Commit"
                                }
                                button {
                                    Class "lp-discard"
                                    Dom.OnClick(fun _ -> env.Emit [NavTo DiscardPending])
                                    "✕ Discard"
                                }
                            }
                        }
                        div {
                            Class "wfp-diag-list"
                            diagList |> AList.map (fun d ->
                                div {
                                    Class (sprintf "wfp-diag %s" (sevClass d.Severity))
                                    span { Class "wfp-diag-icon"; sevIcon d.Severity }
                                    span { Class "wfp-diag-text"; d.Text }
                                    match d.Action with
                                    | Some action ->
                                        button {
                                            Class (if d.Severity = Severity.Ready then "wfp-diag-go lp-commit" else "wfp-diag-go mb")
                                            Attribute("title", "Take this step")
                                            Dom.OnClick(fun _ -> env.Emit [NavTo action])
                                            let goLabel = if d.Severity = Severity.Ready then "▶" else "→"
                                            goLabel
                                        }
                                    | None -> ()
                                })
                        }
                        div {
                            Class "wfp-history"
                            span { Class "wfp-history-line"; historyLine }
                            button {
                                Class "mb"
                                Attribute("title", "Open the registration card's history")
                                Dom.OnClick(fun _ -> env.Emit [NavTo (FocusRegistrationCard SectionHistory)])
                                "▤"
                            }
                        }
                    })

                collapsibleSection "Error stats" true (
                    div {
                        Class "wfp-section"
                        div {
                            Class "wfp-stats-head"
                            span { Class "wfp-stats-cell wfp-stats-mesh"; "mesh" }
                            span { Class "wfp-stats-cell"; "RMS before→after" }
                            span { Class "wfp-stats-cell"; "Δ%" }
                            span { Class "wfp-stats-cell"; "stage" }
                            span { Class "wfp-stats-cell"; "trend" }
                        }
                        statsRows |> AList.ofAVal |> AList.map (fun (mesh, last, stage, series) ->
                            div {
                                Class "wfp-stats-row"
                                span { Class "wfp-stats-cell wfp-stats-mesh"; Attribute("title", mesh); Cards.shortName mesh }
                                span {
                                    Class "wfp-stats-cell"
                                    let rmsTxt =
                                        match last with
                                        | Some (b, a) -> sprintf "%.3f → %.3f" b a
                                        | None -> "—"
                                    rmsTxt
                                }
                                span {
                                    Class "wfp-stats-cell"
                                    let deltaTxt =
                                        match last with
                                        | Some (b, a) when abs b > 1e-12 -> sprintf "%+.1f%%" ((a - b) / b * 100.0)
                                        | _ -> "—"
                                    deltaTxt
                                }
                                span { Class "wfp-stats-cell"; stage }
                                span { Class "wfp-stats-cell reg-spark"; GuiCards.spark series }
                            })
                        div { Class "wfp-aggregate"; aggregateLine }
                        div {
                            Class "wfp-medstrip-head"
                            Attribute("title", "Each row is a moving mesh; each dot a pin's signed median offset. A flat row across pins = a rigid/datum offset; a varying row = spatially-varying change. Grey band = ±LoD95.")
                            "Median offset across pins"
                        }
                        input {
                            Class "wfp-medstrip-bus"
                            Attribute("type", "text")
                            Dom.OnInput(fun e -> onMedStripEvent e.Value)
                        }
                        div {
                            Class "wfp-medstrip"
                            medStripJson |> AVal.map (fun j -> Some (Attribute("data-medstrip", j)))
                            Primitives.observedRender "data-medstrip" "{}" medStripJs
                        }
                    })
            }
        }
