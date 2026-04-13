namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open FSharp.Data.Adaptive
open Aardvark.Dom

module Cards =

    let private coreSampleTrafo = PinGeometry.coreSampleTrafo

    let shortName (name : string) =
        let mesh =
            let s = name.IndexOf('/')
            if s >= 0 then name.[s + 1 ..] else name
        if mesh.Length > 8 && mesh.[8] = '_' then
            let date = mesh.[..7]
            let si = mesh.LastIndexOf("_seg")
            if si > 0 then date + "_" + mesh.[si + 1 ..] else date
        else mesh

    let c4bToHex (c : C4b) =
        sprintf "#%02x%02x%02x" c.R c.G c.B

    let encodeDiagramJson (pin : ScanPin) (svgW : float) (svgH : float) (pad : float) =
        let results = pin.CutResults |> Map.toList
        if results.IsEmpty then "{\"paths\":[],\"legend\":[]}"
        else
            let allPts = results |> List.collect (fun (_, cr) -> cr.Polylines |> List.collect id)
            let xs = allPts |> List.map (fun (p : V2d) -> p.X)
            let ys = allPts |> List.map (fun (p : V2d) -> p.Y)
            let xMin = xs |> List.min
            let xMax = xs |> List.max
            let yMin = ys |> List.min
            let yMax = ys |> List.max
            let xRange = if xMax - xMin < 1e-9 then 1.0 else xMax - xMin
            let yRange = if yMax - yMin < 1e-9 then 1.0 else yMax - yMin
            let toSvgX x = pad + (x - xMin) / xRange * (svgW - 2.0 * pad)
            let toSvgY y = (svgH - pad) - (y - yMin) / yRange * (svgH - 2.0 * pad)
            let paths =
                results |> List.collect (fun (name, cr) ->
                    let color = pin.DatasetColors |> Map.tryFind name |> Option.defaultValue (C4b(100uy,100uy,100uy)) |> c4bToHex
                    cr.Polylines |> List.map (fun pts ->
                        let d = pts |> List.mapi (fun i (p : V2d) ->
                            let cmd = if i = 0 then "M" else "L"
                            sprintf "%s%.1f,%.1f" cmd (toSvgX p.X) (toSvgY p.Y)) |> String.concat " "
                        sprintf "{\"d\":\"%s\",\"c\":\"%s\"}" (d.Replace("\"","\\\"")) color))
            let legend =
                results |> List.map (fun (name, _) ->
                    let color = pin.DatasetColors |> Map.tryFind name |> Option.defaultValue (C4b(100uy,100uy,100uy)) |> c4bToHex
                    sprintf "{\"n\":\"%s\",\"c\":\"%s\"}" (shortName name) color)
            sprintf "{\"paths\":[%s],\"legend\":[%s],\"xMin\":%.4g,\"xMax\":%.4g,\"yMin\":%.4g,\"yMax\":%.4g}"
                (paths |> String.concat ",") (legend |> String.concat ",") xMin xMax yMin yMax

    let parseFloat (s : string) =
        match System.Double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture) with
        | true, v -> Some v
        | _ -> None

    let checkedIf (v : aval<bool>) =
        v |> AVal.map (fun on -> if on then Some (Attribute("checked", "checked")) else None)

    // ── Helpers to project 3D anchor to screen ──────────────────────

    let private projectToScreen (anchor : V3d) (viewTrafo : Trafo3d) (vpSize : V2i) =
        let aspect = float vpSize.X / max 1.0 (float vpSize.Y)
        let proj = Frustum.perspective 90.0 1.0 5000.0 aspect |> Frustum.projTrafo
        let m = proj.Forward * viewTrafo.Forward
        let h = m * V4d(anchor, 1.0)
        if h.W < 0.1 then None
        else
            let ndc = h.XYZ / h.W
            if abs ndc.X > 2.0 || abs ndc.Y > 2.0 then None
            else
                let px = (ndc.X * 0.5 + 0.5) * float vpSize.X
                let py = (1.0 - (ndc.Y * 0.5 + 0.5)) * float vpSize.Y
                Some (V2d(px, py))

    let private clampToViewport (pos : V2d) (size : V2d) (vp : V2d) =
        let x = max 0.0 (min pos.X (vp.X - size.X))
        let y = max 0.0 (min pos.Y (vp.Y - size.Y))
        V2d(x, y)

    // ── Compute card positions ──────────────────────────────────────

    let private computeCardPos
        (card : Card)
        (allCards : HashMap<CardId, Card>)
        (viewTrafo : Trafo3d)
        (vpSize : V2i)
        (cardPositions : System.Collections.Generic.Dictionary<CardId, V2d>)
        : V2d option =

        match card.Attachment with
        | CardDragging(pos, _) -> Some pos
        | CardDetached pos -> Some pos
        | CardAttached ->
            match card.Anchor with
            | AnchorToWorldPoint anchor ->
                match projectToScreen anchor viewTrafo vpSize with
                | Some screenPt ->
                    let pos = V2d(screenPt.X + 20.0, screenPt.Y - card.Size.Y * 0.5)
                    Some (clampToViewport pos card.Size (V2d vpSize))
                | None -> None
            | AnchorToCard(parentId, edge) ->
                match cardPositions.TryGetValue(parentId) with
                | true, parentPos ->
                    let parent = allCards |> HashMap.tryFind parentId
                    let parentSize = parent |> Option.map (fun p -> p.Size) |> Option.defaultValue (V2d(310, 400))
                    let pos =
                        match edge with
                        | EdgeBottom -> V2d(parentPos.X, parentPos.Y + parentSize.Y)
                        | EdgeTop    -> V2d(parentPos.X, parentPos.Y - card.Size.Y)
                        | EdgeRight  -> V2d(parentPos.X + parentSize.X, parentPos.Y)
                        | EdgeLeft   -> V2d(parentPos.X - card.Size.X, parentPos.Y)
                    Some (clampToViewport pos card.Size (V2d vpSize))
                | _ -> None

    // ── Content renderers ───────────────────────────────────────────

    let private stratigraphyContent (env : Env<Message>) (model : AdaptiveModel) (selectedPin : aval<ScanPin option>) =
        let svgW, svgH = 280.0, 140.0
        let pad = 30.0
        div {
            Class "card-strat-content"

            div {
                Class "card-diagram-root"
                selectedPin |> AVal.map (fun pinOpt ->
                    let json =
                        match pinOpt with
                        | Some pin -> encodeDiagramJson pin svgW svgH pad
                        | None -> "{\"paths\":[],\"legend\":[]}"
                    Some (Attribute("data-diagram", json)))
                OnBoot [
                    "var el = __THIS__;"
                    "var last = '';"
                    "var hadPaths = false;"
                    "function render() {"
                    "  var raw = el.getAttribute('data-diagram') || '{}';"
                    "  if(raw === last) return;"
                    "  try { var data = JSON.parse(raw); } catch(e) { var data = {}; }"
                    "  var hasPaths = data.paths && data.paths.length > 0;"
                    "  if(!hasPaths && hadPaths) return;"
                    "  last = raw;"
                    "  el.innerHTML = '';"
                    sprintf "  var ns = 'http://www.w3.org/2000/svg';"
                    sprintf "  var svg = document.createElementNS(ns, 'svg');"
                    sprintf "  svg.setAttribute('width', '%.0f');" svgW
                    sprintf "  svg.setAttribute('height', '%.0f');" svgH
                    sprintf "  svg.setAttribute('viewBox', '0 0 %.0f %.0f');" svgW svgH
                    "  svg.style.background = '#1e293b';"
                    "  svg.style.borderRadius = '3px';"
                    "  svg.style.display = 'block';"
                    "  el.appendChild(svg);"
                    sprintf "  var ax = document.createElementNS(ns, 'line');"
                    sprintf "  ax.setAttribute('x1','%.0f'); ax.setAttribute('y1','%.0f');" pad (svgH - pad)
                    sprintf "  ax.setAttribute('x2','%.0f'); ax.setAttribute('y2','%.0f');" (svgW - pad) (svgH - pad)
                    "  ax.setAttribute('stroke','#475569'); ax.setAttribute('stroke-width','1');"
                    "  svg.appendChild(ax);"
                    sprintf "  var ay = document.createElementNS(ns, 'line');"
                    sprintf "  ay.setAttribute('x1','%.0f'); ay.setAttribute('y1','%.0f');" pad pad
                    sprintf "  ay.setAttribute('x2','%.0f'); ay.setAttribute('y2','%.0f');" pad (svgH - pad)
                    "  ay.setAttribute('stroke','#475569'); ay.setAttribute('stroke-width','1');"
                    "  svg.appendChild(ay);"
                    "  if(!hasPaths) {"
                    "    var msg = document.createElementNS(ns, 'text');"
                    sprintf "    msg.setAttribute('x','%.0f'); msg.setAttribute('y','%.0f');" (svgW * 0.5) (svgH * 0.5)
                    "    msg.setAttribute('text-anchor','middle'); msg.setAttribute('fill','#475569');"
                    "    msg.setAttribute('font-size','11'); msg.setAttribute('font-family','system-ui,sans-serif');"
                    "    msg.textContent = 'Awaiting cut data\u2026';"
                    "    svg.appendChild(msg);"
                    "    return;"
                    "  }"
                    "  hadPaths = true;"
                    "  function addText(x,y,txt,anchor) {"
                    "    var t = document.createElementNS(ns, 'text');"
                    "    t.setAttribute('x',x); t.setAttribute('y',y);"
                    "    t.setAttribute('font-size','9'); t.setAttribute('fill','#94a3b8');"
                    "    t.setAttribute('font-family','system-ui,sans-serif');"
                    "    if(anchor) t.setAttribute('text-anchor',anchor);"
                    "    t.textContent = txt; svg.appendChild(t);"
                    "  }"
                    "  if(data.xMin!==undefined) {"
                    sprintf "    addText(%.0f, %.0f, data.xMin.toFixed(1), 'start');" pad (svgH - pad + 12.0)
                    sprintf "    addText(%.0f, %.0f, data.xMax.toFixed(1), 'end');" (svgW - pad) (svgH - pad + 12.0)
                    sprintf "    addText(%.0f, %.0f, data.yMax.toFixed(1), 'end');" (pad - 4.0) (pad + 3.0)
                    sprintf "    addText(%.0f, %.0f, data.yMin.toFixed(1), 'end');" (pad - 4.0) (svgH - pad + 3.0)
                    "  }"
                    "  var tip = document.createElementNS(ns, 'text');"
                    "  tip.setAttribute('x','5'); tip.setAttribute('y','12');"
                    "  tip.setAttribute('font-size','10'); tip.setAttribute('fill','#e2e8f0');"
                    "  tip.setAttribute('font-family','system-ui,sans-serif');"
                    "  tip.setAttribute('font-weight','600');"
                    "  tip.style.pointerEvents = 'none';"
                    "  svg.appendChild(tip);"
                    "  var allPaths = [];"
                    "  data.paths.forEach(function(item,idx) {"
                    "    var p = document.createElementNS(ns, 'path');"
                    "    p.setAttribute('d', item.d);"
                    "    p.setAttribute('stroke', item.c);"
                    "    p.setAttribute('stroke-width', '2');"
                    "    p.setAttribute('fill', 'none');"
                    "    p.style.cursor = 'pointer';"
                    "    p.setAttribute('stroke-opacity', '0.85');"
                    "    allPaths.push(p);"
                    "    var leg = data.legend && data.legend[idx] ? data.legend[idx] : null;"
                    "    p.addEventListener('mouseenter', function() {"
                    "      allPaths.forEach(function(q) { q.setAttribute('stroke-opacity', q===p?'1':'0.2'); q.setAttribute('stroke-width', q===p?'3':'1.5'); });"
                    "      if(leg) tip.textContent = leg.n;"
                    "    });"
                    "    p.addEventListener('mouseleave', function() {"
                    "      allPaths.forEach(function(q) { q.setAttribute('stroke-opacity','0.85'); q.setAttribute('stroke-width','2'); });"
                    "      tip.textContent = '';"
                    "    });"
                    "    svg.appendChild(p);"
                    "  });"
                    "}"
                    "render();"
                    "new MutationObserver(function(){render();}).observe(el, {attributes:true,attributeFilter:['data-diagram']});"
                ]
            }

            div {
                Class "strat-wrapper mt-4"
                let isPlacing = (model.ScanPins.PlacingMode, model.ScanPins.ActivePlacement) ||> AVal.map2 (fun pm ap -> pm.IsSome || ap.IsSome)
                StratigraphyView.render env isPlacing selectedPin
            }
            div {
                Class "card-btn-row mt-4"
                button {
                    selectedPin |> AVal.map (fun po ->
                        match po with
                        | Some p when p.StratigraphyDisplay = Undistorted -> Some (Class "btn-active")
                        | _ -> None)
                    Dom.OnClick(fun _ ->
                        match AVal.force selectedPin with
                        | Some p -> env.Emit [ScanPinMsg (SetStratigraphyDisplay(p.Id, Undistorted))]
                        | None -> ())
                    "Undistorted"
                }
                button {
                    selectedPin |> AVal.map (fun po ->
                        match po with
                        | Some p when p.StratigraphyDisplay = Normalized -> Some (Class "btn-active")
                        | _ -> None)
                    Dom.OnClick(fun _ ->
                        match AVal.force selectedPin with
                        | Some p -> env.Emit [ScanPinMsg (SetStratigraphyDisplay(p.Id, Normalized))]
                        | None -> ())
                    "Normalized"
                }
            }
        }

    let private controlsContent (env : Env<Message>) (model : AdaptiveModel) (selectedPin : aval<ScanPin option>) =
        div {
            Class "card-controls-content"
            div {
                Class "card-btn-row"
                button {
                    selectedPin |> AVal.map (fun po ->
                        match po with
                        | Some p when p.GhostClip = GhostClipOn -> Some (Class "btn-active")
                        | _ -> None)
                    Dom.OnClick(fun _ ->
                        match AVal.force selectedPin with
                        | Some p ->
                            let next = if p.GhostClip = GhostClipOn then GhostClipOff else GhostClipOn
                            env.Emit [ScanPinMsg (SetGhostClip(p.Id, next))]
                        | None -> ())
                    "Ghost Clip"
                }
                button {
                    selectedPin |> AVal.map (fun po ->
                        match po with
                        | Some p when p.ExtractedLines.ShowCutPlaneLines -> Some (Class "btn-active")
                        | _ -> None)
                    Dom.OnClick(fun _ ->
                        match AVal.force selectedPin with
                        | Some p -> env.Emit [ScanPinMsg (SetShowCutPlaneLines(p.Id, not p.ExtractedLines.ShowCutPlaneLines))]
                        | None -> ())
                    "Cut Lines"
                }
                button {
                    selectedPin |> AVal.map (fun po ->
                        match po with
                        | Some p when p.ExtractedLines.ShowCylinderEdgeLines -> Some (Class "btn-active")
                        | _ -> None)
                    Dom.OnClick(fun _ ->
                        match AVal.force selectedPin with
                        | Some p -> env.Emit [ScanPinMsg (SetShowCylinderEdgeLines(p.Id, not p.ExtractedLines.ShowCylinderEdgeLines))]
                        | None -> ())
                    "Edge Lines"
                }
            }
            div {
                Class "card-btn-row mt-4"
                button {
                    selectedPin |> AVal.map (fun po ->
                        match po with
                        | Some p when p.Explosion.Enabled -> Some (Class "btn-active")
                        | _ -> None)
                    Dom.OnClick(fun _ ->
                        match AVal.force selectedPin with
                        | Some p -> env.Emit [ScanPinMsg (SetExplosionEnabled(p.Id, not p.Explosion.Enabled))]
                        | None -> ())
                    "Explode"
                }
                input {
                    Attribute("type", "range")
                    Attribute("min", "0"); Attribute("max", "3"); Attribute("step", "0.05")
                    Class "range-full"
                    selectedPin |> AVal.map (fun po ->
                        match po with
                        | Some p -> Some (Attribute("value", sprintf "%.2f" p.Explosion.ExpansionFactor))
                        | None -> None)
                    Dom.OnInput(fun e ->
                        match AVal.force selectedPin, parseFloat e.Value with
                        | Some p, Some v -> env.Emit [ScanPinMsg (SetExplosionFactor(p.Id, v))]
                        | _ -> ())
                }
            }
            div {
                Class "card-toggles"
                label {
                    input {
                        Attribute("type", "checkbox")
                        checkedIf model.DepthShadeOn
                        Dom.OnChange(fun _ -> env.Emit [ToggleDepthShade])
                    }
                    "Depth"
                }
                label {
                    input {
                        Attribute("type", "checkbox")
                        checkedIf model.IsolinesOn
                        Dom.OnChange(fun _ -> env.Emit [ToggleIsolines])
                    }
                    "Isolines"
                }
                label {
                    input {
                        Attribute("type", "checkbox")
                        checkedIf model.ColorMode
                        Dom.OnChange(fun _ -> env.Emit [ToggleColorMode])
                    }
                    "Color"
                }
            }
        }

    let private rankingContent (env : Env<Message>) (model : AdaptiveModel) (selectedPin : aval<ScanPin option>) =
        let selectedGridEval = selectedPin |> AVal.map (Option.bind (fun p -> p.GridEval))
        div {
            Class "card-ranking-content"
            div {
                Class "rank-controls"
                label {
                    "Top K "
                    input {
                        Attribute("type", "number")
                        Attribute("min", "1")
                        Class "input-xs"
                        RankingState.topK |> AVal.map (fun k -> Some (Attribute("value", string k)))
                        Dom.OnInput(fun e ->
                            match System.Int32.TryParse(e.Value) with
                            | true, k when k >= 1 -> transact (fun () -> RankingState.topK.Value <- k)
                            | _ -> ())
                    }
                }
                label {
                    input {
                        Attribute("type", "checkbox")
                        checkedIf RankingState.rankFadeOn
                        Dom.OnChange(fun _ ->
                            transact (fun () -> RankingState.rankFadeOn.Value <- not RankingState.rankFadeOn.Value))
                    }
                    "Rank fade"
                }
            }
            div {
                Class "rank-list"
                selectedGridEval |> AVal.map (fun geOpt ->
                    let names =
                        match geOpt with
                        | Some ge -> ge.DatasetStats |> Array.map (fun s -> s.MeshName) |> Array.toList
                        | None -> []
                    RankingState.ensureDatasets names
                    Some (Attribute("data-names", System.String.Join("|", names))))
                RankingState.datasetOrder
                |> AVal.map IndexList.ofList
                |> AList.ofAVal
                |> AList.map (fun name ->
                    let statsOpt =
                        selectedGridEval |> AVal.map (fun geOpt ->
                            match geOpt with
                            | Some ge -> ge.DatasetStats |> Array.tryFind (fun s -> s.MeshName = name)
                            | None -> None)
                    let isHidden = RankingState.datasetHidden |> ASet.contains name
                    let rank = RankingState.rankOf name
                    let inK = RankingState.inTopK name
                    div {
                        (isHidden, inK) ||> AVal.map2 (fun h k ->
                            let cls =
                                if h then "rank-row hidden"
                                elif not k then "rank-row out-of-topk"
                                else "rank-row"
                            Some (Class cls))
                        div {
                            Class "rank-badge"
                            rank |> AVal.map (fun r ->
                                match r with
                                | Some i -> sprintf "#%d" (i + 1)
                                | None -> "\u2014")
                        }
                        div {
                            Class "rank-name"
                            shortName name
                        }
                        div {
                            Class "rank-variance"
                            statsOpt |> AVal.map (fun so ->
                                match so with
                                | Some s -> sprintf "\u03c3=%.1f" (sqrt s.ZVariance)
                                | None -> "\u2014")
                        }
                        div {
                            Class "rank-buttons"
                            button {
                                Class "rank-move"
                                Dom.OnClick(fun _ -> RankingState.move name -1)
                                "\u25b2"
                            }
                            button {
                                Class "rank-move"
                                Dom.OnClick(fun _ -> RankingState.move name 1)
                                "\u25bc"
                            }
                            button {
                                Class "rank-toggle"
                                Dom.OnClick(fun _ -> RankingState.toggleHidden name)
                                isHidden |> AVal.map (fun h -> if h then "Show" else "Hide")
                            }
                        }
                    }
                )
            }
        }

    // ── Card title by content type ──────────────────────────────────

    let private cardTitle (content : CardContent) =
        match content with
        | StratigraphyDiagram _ -> "Stratigraphy"
        | PinControls _ -> "Controls"
        | DatasetRanking _ -> "Datasets"

    // ── Main card system renderer ───────────────────────────────────

    let renderCards (env : Env<Message>) (model : AdaptiveModel) (viewTrafo : aval<Trafo3d>) (vpSize : aval<V2i>) =
        let allPinsVal = model.ScanPins.Pins |> AMap.toAVal
        let selectedPin =
            (model.ScanPins.SelectedPin, model.ScanPins.ActivePlacement, allPinsVal)
            |||> AVal.map3 (fun sel act pins ->
                let id = act |> Option.orElse sel
                id |> Option.bind (fun id -> HashMap.tryFind id pins))

        let cardsSnapshot = model.CardSystem.Cards |> AMap.toAVal

        // ── Local drag state (never touches model during movement) ────
        let dragState = cval<(CardId * V2d * V2d) option> None

        // ── Local collapse state ────────────────────────────────────
        let collapsedSet = cval (HashSet.empty<CardId>)

        let cardPositions =
            (cardsSnapshot, viewTrafo, vpSize)
            |||> AVal.map3 (fun cards vt sz ->
                let dict = System.Collections.Generic.Dictionary<CardId, V2d>()
                let cardMap = cards
                // First pass: world-anchored cards
                for (id, card) in HashMap.toSeq cardMap do
                    if card.Visible then
                        match card.Anchor with
                        | AnchorToWorldPoint _ ->
                            match computeCardPos card cardMap vt sz dict with
                            | Some pos -> dict.[id] <- pos
                            | None -> ()
                        | _ -> ()
                // Second pass: card-anchored sub-cards
                for (id, card) in HashMap.toSeq cardMap do
                    if card.Visible && not (dict.ContainsKey id) then
                        match computeCardPos card cardMap vt sz dict with
                        | Some pos -> dict.[id] <- pos
                        | None -> ()
                dict)

        // Drag-aware positions: dragged card uses local pos, sub-cards follow via delta
        let effectivePositions =
            (cardPositions, cardsSnapshot, dragState :> aval<_>)
            |||> AVal.map3 (fun baseDict cards drag ->
                match drag with
                | None -> baseDict
                | Some (dragId, dragPos, _) ->
                    let dict = System.Collections.Generic.Dictionary<CardId, V2d>(baseDict)
                    let baseParentPos = match baseDict.TryGetValue(dragId) with true, p -> p | _ -> dragPos
                    let delta = dragPos - baseParentPos
                    dict.[dragId] <- dragPos
                    for (id, card) in HashMap.toSeq cards do
                        if id <> dragId && card.Visible then
                            match card.Anchor with
                            | AnchorToCard(pid, _) when pid = dragId ->
                                match baseDict.TryGetValue(id) with
                                | true, cp -> dict.[id] <- cp + delta
                                | _ -> ()
                            | _ -> ()
                    dict)

        let anchorScreenPositions =
            (cardsSnapshot, viewTrafo, vpSize)
            |||> AVal.map3 (fun cards vt sz ->
                let dict = System.Collections.Generic.Dictionary<CardId, V2d>()
                for (id, card) in HashMap.toSeq cards do
                    match card.Anchor with
                    | AnchorToWorldPoint anchor ->
                        match projectToScreen anchor vt sz with
                        | Some pos -> dict.[id] <- pos
                        | None -> ()
                    | _ -> ()
                dict)

        div {
            Class "card-overlay"

            // Leader lines SVG — rendered via OnBoot + MutationObserver
            div {
                Attribute("id", "card-leader-lines")
                (cardPositions, anchorScreenPositions, cardsSnapshot) |||> AVal.map3 (fun posDict ancDict cards ->
                    let sb = System.Text.StringBuilder()
                    for (id, card) in HashMap.toSeq cards do
                        if card.Visible then
                            match card.Attachment with
                            | CardDetached _ | CardDragging _ ->
                                match posDict.TryGetValue(id), ancDict.TryGetValue(id) with
                                | (true, cardPos), (true, ancPos) ->
                                    let cx = cardPos.X + card.Size.X * 0.5
                                    let cy = cardPos.Y
                                    let ax = ancPos.X
                                    let ay = ancPos.Y
                                    let dx = cx - ax
                                    let cpx = ax + dx * 0.5
                                    let cpy = ay - 30.0
                                    sb.AppendFormat("{0},{1},{2},{3},{4},{5}|", ax, ay, cpx, cpy, cx, cy) |> ignore
                                | _ -> ()
                            | CardAttached ->
                                // Show leader line if card is clamped (far from anchor)
                                match posDict.TryGetValue(id), ancDict.TryGetValue(id) with
                                | (true, cardPos), (true, ancPos) ->
                                    let dist = (V2d(cardPos.X + card.Size.X * 0.5, cardPos.Y) - ancPos).Length
                                    if dist > 80.0 then
                                        let cx = cardPos.X + card.Size.X * 0.5
                                        let cy = cardPos.Y
                                        let ax = ancPos.X
                                        let ay = ancPos.Y
                                        let dx = cx - ax
                                        let cpx = ax + dx * 0.5
                                        let cpy = ay - 30.0
                                        sb.AppendFormat("{0},{1},{2},{3},{4},{5}|", ax, ay, cpx, cpy, cx, cy) |> ignore
                                | _ -> ()
                    Some (Attribute("data-leaders", sb.ToString())))
                OnBoot [
                    "var el = __THIS__;"
                    "function render() {"
                    "  var raw = el.getAttribute('data-leaders') || '';"
                    "  el.innerHTML = '';"
                    "  if(!raw) return;"
                    "  var ns = 'http://www.w3.org/2000/svg';"
                    "  var svg = document.createElementNS(ns, 'svg');"
                    "  svg.style.cssText = 'position:fixed;left:0;top:0;width:100%;height:100%;pointer-events:none;z-index:999;';"
                    "  var segs = raw.split('|').filter(function(s){return s.length>0;});"
                    "  segs.forEach(function(seg) {"
                    "    var v = seg.split(',').map(Number);"
                    "    var p = document.createElementNS(ns, 'path');"
                    "    p.setAttribute('d', 'M'+v[0]+','+v[1]+' Q'+v[2]+','+v[3]+' '+v[4]+','+v[5]);"
                    "    p.setAttribute('stroke', 'rgba(100,116,139,0.4)');"
                    "    p.setAttribute('stroke-width', '1');"
                    "    p.setAttribute('stroke-dasharray', '4,3');"
                    "    p.setAttribute('fill', 'none');"
                    "    svg.appendChild(p);"
                    "  });"
                    "  el.appendChild(svg);"
                    "}"
                    "render();"
                    "new MutationObserver(function(){render();}).observe(el, {attributes:true,attributeFilter:['data-leaders']});"
                ]
            }

            // Render each card (sorted by ZOrder so later = on top in DOM)
            cardsSnapshot
            |> AVal.map (fun cards ->
                cards |> HashMap.toSeq
                |> Seq.sortBy (fun (_, c) -> c.ZOrder)
                |> Seq.map fst
                |> IndexList.ofSeq)
            |> AList.ofAVal
            |> AList.map (fun cardId ->
                let cardVal = cardsSnapshot |> AVal.map (fun cards -> HashMap.tryFind cardId cards)
                let posVal = cardPositions |> AVal.map (fun dict ->
                    match dict.TryGetValue(cardId) with
                    | true, pos -> Some pos
                    | _ -> None)

                let effectivePos = effectivePositions |> AVal.map (fun dict ->
                    match dict.TryGetValue(cardId) with
                    | true, pos -> Some pos
                    | _ -> None)

                let isCollapsed =
                    (collapsedSet :> aval<_>) |> AVal.map (fun s -> HashSet.contains cardId s)

                div {
                    Class "card"
                    (cardVal, effectivePos) ||> AVal.map2 (fun cOpt pOpt ->
                        match cOpt, pOpt with
                        | Some card, Some pos when card.Visible ->
                            Some (Style [
                                Left (sprintf "%.0fpx" pos.X)
                                Top (sprintf "%.0fpx" pos.Y)
                                Width (sprintf "%.0fpx" card.Size.X)
                                Css.Visibility "visible"
                            ])
                        | _ ->
                            Some (Style [Display "none"]))

                    // Title bar: drag region + buttons side by side
                    div {
                        Class "card-titlebar"

                        let isDetached = cardVal |> AVal.map (fun cOpt ->
                            match cOpt with
                            | Some c -> match c.Attachment with CardDetached _ -> true | _ -> false
                            | None -> false)

                        let isSubCard = cardVal |> AVal.map (fun cOpt ->
                            match cOpt with
                            | Some c -> match c.Anchor with AnchorToCard _ -> true | _ -> false
                            | None -> false)

                        // Drag handle (takes remaining space via flex:1)
                        div {
                            Class "card-drag-handle"
                            Dom.OnPointerDown((fun e ->
                                if e.Button = Button.Left then
                                    let cardPos =
                                        match AVal.force effectivePos with
                                        | Some p -> p
                                        | None -> V2d.Zero
                                    let grabOffset = V2d(float e.ClientPosition.X, float e.ClientPosition.Y) - cardPos
                                    transact (fun () -> dragState.Value <- Some (cardId, cardPos, grabOffset))
                            ), pointerCapture = true)
                            Dom.OnPointerMove(fun e ->
                                match dragState.GetValue() with
                                | Some (id, _, offset) when id = cardId ->
                                    let newPos = V2d(float e.ClientPosition.X, float e.ClientPosition.Y) - offset
                                    transact (fun () -> dragState.Value <- Some (id, newPos, offset))
                                | _ -> ())
                            Dom.OnPointerUp((fun _ ->
                                match dragState.GetValue() with
                                | Some (id, pos, _) when id = cardId ->
                                    transact (fun () -> dragState.Value <- None)
                                    env.Emit [CardMsg (BringToFront id); CardMsg (FinishDrag(id, pos))]
                                | _ -> ()
                            ), pointerCapture = true)

                            cardVal |> AVal.map (fun cOpt ->
                                match cOpt with
                                | Some card -> cardTitle card.Content
                                | None -> "")
                        }

                        // Re-dock chevron (only when detached)
                        button {
                            Class "card-btn-dock"
                            isDetached |> AVal.map (fun d -> if d then None else Some (Style [Display "none"]))
                            Dom.OnClick(fun _ -> env.Emit [CardMsg (RedockCard cardId)])
                            "\u25c2"
                        }
                        // Collapse X (sub-cards only)
                        button {
                            Class "card-btn-close"
                            isSubCard |> AVal.map (fun sub -> if sub then None else Some (Style [Display "none"]))
                            Dom.OnClick(fun _ ->
                                transact (fun () ->
                                    let s = collapsedSet.Value
                                    if HashSet.contains cardId s then collapsedSet.Value <- HashSet.remove cardId s
                                    else collapsedSet.Value <- HashSet.add cardId s))
                            isCollapsed |> AVal.map (fun c -> if c then "+" else "\u00d7")
                        }
                    }

                    // Content area (hidden when collapsed)
                    div {
                        Class "card-body"
                        isCollapsed |> AVal.map (fun c ->
                            if c then Some (Style [Display "none"]) else None)

                        let contentType = AVal.force cardVal |> Option.map (fun c -> c.Content)
                        match contentType with
                        | Some (StratigraphyDiagram _) -> stratigraphyContent env model selectedPin
                        | Some (PinControls _) -> controlsContent env model selectedPin
                        | Some (DatasetRanking _) -> rankingContent env model selectedPin
                        | None -> ()
                    }
                }
            )
        }
