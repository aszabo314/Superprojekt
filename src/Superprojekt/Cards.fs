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

    /// Nice-number tick generator (same ladder as the scale bar). Kept as a
    /// generic utility for V6 payload cards (§D.7).
    let niceTicks (lo : float) (hi : float) (targetCount : int) : float[] * float =
        let range = hi - lo
        if range <= 1e-12 || targetCount < 1 then [||], 1.0
        else
            let rough = range / float targetCount
            let mag = 10.0 ** floor (log10 rough)
            let norm = rough / mag
            let nice =
                if norm < 1.5 then 1.0
                elif norm < 3.0 then 2.0
                elif norm < 7.0 then 5.0
                else 10.0
            let step = nice * mag
            let start = ceil (lo / step) * step
            let ticks = ResizeArray<float>()
            let mutable v = start
            while v <= hi + 0.5 * step do
                if v >= lo - 1e-9 && v <= hi + 1e-9 then ticks.Add v
                v <- v + step
            ticks.ToArray(), step

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

    let private pinCardBody (_env : Env<Message>) (_model : AdaptiveModel) (selectedPin : aval<ScanPin option>) =
        // V5's cross-section SVG + cut-plane controls are gone (§B.1).
        // V6 §D.7 fills the body with payload-specific content per
        // anchor (point / line-on-surface / unwrapped patch); for now
        // the card carries only the pin coordinate readout below.
        div {
            Class "pin-card-body pin-card-empty"
            selectedPin |> AVal.map (fun po ->
                match po with
                | Some _ -> "Anchor payload coming in Phase 4."
                | None -> "")
        }

    let renderCards (env : Env<Message>) (model : AdaptiveModel) (viewTrafo : aval<Trafo3d>) (vpSize : aval<V2i>) =
        let allPinsVal = model.ScanPins.Pins |> AMap.toAVal
        let activePlacementId =
            model.ScanPins.Placement |> AVal.map (function
                | AdjustingPin id -> Some id
                | _ -> None)
        let selectedPin =
            (model.ScanPins.SelectedPin, activePlacementId, allPinsVal)
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
                |> Seq.filter (fun (_, c) -> match c.Content with PinCard _ -> true)
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
                                    let p = pin.Centre
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
                            isCollapsed |> AVal.map (fun c -> if c then "+" else "–")
                        }
                        button {
                            Class "card-btn-close"
                            Attribute("title", "Deselect pin")
                            Dom.OnClick(fun _ -> env.Emit [ScanPinMsg (SelectPin None)])
                            "×"
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
