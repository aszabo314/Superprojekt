namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open FSharp.Data.Adaptive
open Aardvark.Dom

module Cards =

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

    let parseFloat (s : string) =
        match System.Double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture) with
        | true, v -> Some v
        | _ -> None

    let checkedIf (v : aval<bool>) =
        v |> AVal.map (fun on -> if on then Some (Attribute("checked", "checked")) else None)

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

    let private computeCardPos
        (card : Card)
        (viewTrafo : Trafo3d)
        (vpSize : V2i)
        : V2d option =
        match card.Attachment with
        | CardDragging(pos, _) -> Some pos
        | CardDetached pos -> Some pos
        | CardAttached ->
            match card.Anchor with
            | AnchorToWorldPoint anchor ->
                match projectToScreen anchor viewTrafo vpSize with
                | Some screenPt ->
                    let pos = V2d(screenPt.X + card.Size.X * 0.4, screenPt.Y - card.Size.Y * 0.5 - 40.0)
                    Some (clampToViewport pos card.Size (V2d vpSize))
                | None -> None

    let private diagramSvg (selectedPin : aval<ScanPin option>) =
        let svgW, svgH = 280.0, 130.0
        let pad = 28.0
        div {
            Class "card-cut-diagram"
            selectedPin |> AVal.map (fun po ->
                let json =
                    match po with
                    | Some pin -> encodeDiagramJson pin svgW svgH pad
                    | None -> "{\"paths\":[],\"legend\":[]}"
                Some (Attribute("data-diagram", json)))
            OnBoot [
                "var el = __THIS__;"
                "var last = '';"
                "function render() {"
                "  var raw = el.getAttribute('data-diagram') || '{}';"
                "  if(raw === last) return;"
                "  last = raw;"
                "  try { var data = JSON.parse(raw); } catch(e) { var data = {}; }"
                "  el.innerHTML = '';"
                sprintf "  var ns = 'http://www.w3.org/2000/svg';"
                sprintf "  var svg = document.createElementNS(ns, 'svg');"
                sprintf "  svg.setAttribute('width', '%.0f');" svgW
                sprintf "  svg.setAttribute('height', '%.0f');" svgH
                sprintf "  svg.setAttribute('viewBox', '0 0 %.0f %.0f');" svgW svgH
                "  svg.style.background = '#fafbfc'; svg.style.display = 'block';"
                "  el.appendChild(svg);"
                sprintf "  var ax = document.createElementNS(ns, 'line');"
                sprintf "  ax.setAttribute('x1','%.0f'); ax.setAttribute('y1','%.0f');" pad (svgH - pad)
                sprintf "  ax.setAttribute('x2','%.0f'); ax.setAttribute('y2','%.0f');" (svgW - pad) (svgH - pad)
                "  ax.setAttribute('stroke','#cbd5e1'); ax.setAttribute('stroke-width','1');"
                "  svg.appendChild(ax);"
                sprintf "  var ay = document.createElementNS(ns, 'line');"
                sprintf "  ay.setAttribute('x1','%.0f'); ay.setAttribute('y1','%.0f');" pad pad
                sprintf "  ay.setAttribute('x2','%.0f'); ay.setAttribute('y2','%.0f');" pad (svgH - pad)
                "  ay.setAttribute('stroke','#cbd5e1'); ay.setAttribute('stroke-width','1');"
                "  svg.appendChild(ay);"
                "  if(!data.paths || data.paths.length === 0) {"
                "    var msg = document.createElementNS(ns, 'text');"
                sprintf "    msg.setAttribute('x','%.0f'); msg.setAttribute('y','%.0f');" (svgW * 0.5) (svgH * 0.5)
                "    msg.setAttribute('text-anchor','middle'); msg.setAttribute('fill','#94a3b8');"
                "    msg.setAttribute('font-size','10');"
                "    msg.textContent = 'Awaiting cut\u2026';"
                "    svg.appendChild(msg);"
                "    return;"
                "  }"
                "  data.paths.forEach(function(item) {"
                "    var p = document.createElementNS(ns, 'path');"
                "    p.setAttribute('d', item.d);"
                "    p.setAttribute('stroke', item.c);"
                "    p.setAttribute('stroke-width', '2');"
                "    p.setAttribute('fill', 'none');"
                "    svg.appendChild(p);"
                "  });"
                "}"
                "render();"
                "new MutationObserver(function(){render();}).observe(el, {attributes:true,attributeFilter:['data-diagram']});"
            ]
        }

    let private pinCardBody (env : Env<Message>) (model : AdaptiveModel) (selectedPin : aval<ScanPin option>) =
        div {
            Class "pin-card-body"

            diagramSvg selectedPin

            div {
                Class "pin-card-strat"
                let isPlacing = (model.ScanPins.PlacingMode, model.ScanPins.ActivePlacement) ||> AVal.map2 (fun pm ap -> pm.IsSome || ap.IsSome)
                StratigraphyView.render env isPlacing selectedPin
                div {
                    Class "strat-hover-tip"
                    selectedPin |> AVal.map (fun po ->
                        match po |> Option.bind (fun p ->
                            p.Stratigraphy |> Option.bind (fun data ->
                                p.BetweenSpaceHover |> Option.bind (fun h ->
                                    if h.ColumnIdx < 0 || h.ColumnIdx >= data.Columns.Length then None
                                    else
                                        Stratigraphy.tryBracket data.Columns.[h.ColumnIdx].Events h.HoverZ
                                        |> Option.map (fun (zLo, zHi, lo, up) -> h, zLo, zHi, lo, up)))) with
                        | Some (h, zLo, zHi, lo, up) ->
                            let gap = zHi - zLo
                            let pinTag = if h.Pinned then " · pinned" else ""
                            sprintf "%s \u2194 %s · %.2fm%s" (shortName lo) (shortName up) gap pinTag
                        | None -> "")
                }
            }

            div {
                Class "pin-card-inline-controls"

                let normalized = selectedPin |> AVal.map (fun po -> po |> Option.map (fun p -> p.StratigraphyDisplay = Normalized) |> Option.defaultValue false)
                div {
                    Attribute("title", "Profile: Flat / Normalized")
                    Primitives.compactButtonBar [
                        "Flat",
                        (normalized |> AVal.map not),
                        (fun () ->
                            match AVal.force selectedPin with
                            | Some p -> env.Emit [ScanPinMsg (SetStratigraphyDisplay(p.Id, Undistorted))]
                            | None -> ())
                        "Norm",
                        normalized,
                        (fun () ->
                            match AVal.force selectedPin with
                            | Some p -> env.Emit [ScanPinMsg (SetStratigraphyDisplay(p.Id, Normalized))]
                            | None -> ())
                    ]
                }

                div {
                    Attribute("title", "Between-space highlighting")
                    Primitives.compactToggle "Gap" model.ScanPins.BetweenSpaceEnabled (fun () ->
                        env.Emit [ScanPinMsg ToggleBetweenSpaceEnabled])
                }

                let isolate = selectedPin |> AVal.map (fun po ->
                    match po with Some p -> p.GhostClip = GhostClipOn | None -> false)
                div {
                    Attribute("title", "Isolate: ghost-clip meshes outside the pin cylinder")
                    Primitives.compactToggle "Solo" isolate (fun () ->
                        match AVal.force selectedPin with
                        | Some p ->
                            let next = if p.GhostClip = GhostClipOn then GhostClipOff else GhostClipOn
                            env.Emit [ScanPinMsg (SetGhostClip(p.Id, next))]
                        | None -> ())
                }

                let ghostCut = selectedPin |> AVal.map (fun po ->
                    match po with Some p -> p.GhostClipCutPlane | None -> false)
                div {
                    Class "ct-gated"
                    isolate |> AVal.map (fun g -> if g then None else Some (Class "ct-disabled"))
                    Attribute("title", "Also clip in front of the cut plane")
                    Primitives.compactToggle "Cut" ghostCut (fun () ->
                        match AVal.force selectedPin with
                        | Some p when p.GhostClip = GhostClipOn ->
                            env.Emit [ScanPinMsg (SetGhostClipCutPlane(p.Id, not p.GhostClipCutPlane))]
                        | _ -> ())
                }
            }
        }

    let renderCards (env : Env<Message>) (model : AdaptiveModel) (viewTrafo : aval<Trafo3d>) (vpSize : aval<V2i>) =
        let allPinsVal = model.ScanPins.Pins |> AMap.toAVal
        let selectedPin =
            (model.ScanPins.SelectedPin, model.ScanPins.ActivePlacement, allPinsVal)
            |||> AVal.map3 (fun sel act pins ->
                let id = act |> Option.orElse sel
                id |> Option.bind (fun id -> HashMap.tryFind id pins))

        let cardsSnapshot = model.CardSystem.Cards |> AMap.toAVal

        let dragState = cval<(CardId * V2d * V2d) option> None

        let collapsedSet = cval (HashSet.empty<CardId>)

        let cardPositions =
            (cardsSnapshot, viewTrafo, vpSize)
            |||> AVal.map3 (fun cards vt sz ->
                let dict = System.Collections.Generic.Dictionary<CardId, V2d>()
                for (id, card) in HashMap.toSeq cards do
                    if card.Visible then
                        match computeCardPos card vt sz with
                        | Some pos -> dict.[id] <- pos
                        | None -> ()
                dict)

        let effectivePositions =
            (cardPositions, dragState :> aval<_>)
            ||> AVal.map2 (fun baseDict drag ->
                match drag with
                | None -> baseDict
                | Some (dragId, dragPos, _) ->
                    let dict = System.Collections.Generic.Dictionary<CardId, V2d>(baseDict)
                    dict.[dragId] <- dragPos
                    dict)

        div {
            Class "card-overlay"

            cardsSnapshot
            |> AVal.map (fun cards ->
                cards |> HashMap.toSeq
                |> Seq.filter (fun (_, c) -> match c.Content with StratigraphyDiagram _ -> true | _ -> false)
                |> Seq.sortBy (fun (_, c) -> c.ZOrder)
                |> Seq.map fst
                |> IndexList.ofSeq)
            |> AList.ofAVal
            |> AList.map (fun cardId ->
                let cardVal = cardsSnapshot |> AVal.map (fun cards -> HashMap.tryFind cardId cards)
                let effectivePos = effectivePositions |> AVal.map (fun dict ->
                    match dict.TryGetValue(cardId) with
                    | true, pos -> Some pos
                    | _ -> None)

                let isCollapsed =
                    (collapsedSet :> aval<_>) |> AVal.map (fun s -> HashSet.contains cardId s)

                div {
                    Class "card pin-card"
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

                    div {
                        Class "card-titlebar"

                        let isDetached = cardVal |> AVal.map (fun cOpt ->
                            match cOpt with
                            | Some c -> match c.Attachment with CardDetached _ -> true | _ -> false
                            | None -> false)

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

                            selectedPin |> AVal.map (fun po ->
                                match po with
                                | Some pin ->
                                    let p = pin.Prism.AnchorPoint
                                    sprintf "Pin  (%.1f, %.1f, %.1f)" p.X p.Y p.Z
                                | None -> "Pin")
                        }

                        button {
                            Class "card-btn-reattach"
                            Attribute("title", "Reattach to pin")
                            isDetached |> AVal.map (fun d -> if d then None else Some (Style [Display "none"]))
                            Dom.OnClick(fun _ -> env.Emit [CardMsg (RedockCard cardId)])
                            "\U0001F4CC"
                        }
                        button {
                            Class "card-btn-collapse"
                            Attribute("title", "Collapse")
                            Dom.OnClick(fun _ ->
                                transact (fun () ->
                                    let s = collapsedSet.Value
                                    if HashSet.contains cardId s then collapsedSet.Value <- HashSet.remove cardId s
                                    else collapsedSet.Value <- HashSet.add cardId s))
                            isCollapsed |> AVal.map (fun c -> if c then "+" else "\u2013")
                        }
                        button {
                            Class "card-btn-close"
                            Attribute("title", "Deselect pin")
                            Dom.OnClick(fun _ -> env.Emit [ScanPinMsg (SelectPin None)])
                            "\u00d7"
                        }
                    }

                    div {
                        Class "card-body"
                        isCollapsed |> AVal.map (fun c ->
                            if c then Some (Style [Display "none"]) else None)
                        pinCardBody env model selectedPin
                    }
                }
            )
        }
