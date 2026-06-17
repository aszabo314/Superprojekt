namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open FSharp.Data.Adaptive
open Aardvark.Dom

module Cards =

    let shortName = CardsPin.shortName
    let numbered = CardsPin.numbered

    // Shared chrome for every floating card. A drag is held in a local
    // cval<(cardPos, grabOffset) option>; `pos` is the card's current position
    // (grab offset is computed from it on pointer-down) and `onCommit` gets
    // the final position on release.
    let cardDragHandle (title : aval<string>) (pos : aval<V2d>) (dragState : cval<(V2d * V2d) option>) (onCommit : V2d -> unit) =
        div {
            Class "card-drag-handle"
            Dom.OnPointerDown((fun e ->
                if e.Button = Button.Left then
                    let cardPos = AVal.force pos
                    let grab = V2d(float e.ClientPosition.X, float e.ClientPosition.Y) - cardPos
                    transact (fun () -> dragState.Value <- Some (cardPos, grab))
            ), pointerCapture = true)
            Dom.OnPointerMove(fun e ->
                match dragState.GetValue() with
                | Some (_, grab) ->
                    let p = V2d(float e.ClientPosition.X, float e.ClientPosition.Y) - grab
                    transact (fun () -> dragState.Value <- Some (p, grab))
                | None -> ())
            Dom.OnPointerUp((fun _ ->
                match dragState.GetValue() with
                | Some (p, _) ->
                    transact (fun () -> dragState.Value <- None)
                    onCommit p
                | None -> ()
            ), pointerCapture = true)
            title
        }

    // Current position of a floating card: live drag position while dragging,
    // committed position otherwise.
    let cardPos (committed : aval<V2d>) (dragState : cval<(V2d * V2d) option>) : aval<V2d> =
        (committed, dragState :> aval<_>) ||> AVal.map2 (fun c d ->
            match d with Some (p, _) -> p | None -> c)

    // display:none when hidden, fixed-position Left/Top when shown.
    let cardStyle (visible : aval<bool>) (pos : aval<V2d>) =
        (visible, pos) ||> AVal.map2 (fun on p ->
            if not on then Some (Style [Display "none"])
            else Some (Style [Left (sprintf "%.0fpx" p.X); Top (sprintf "%.0fpx" p.Y)]))

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
        | CardDetached pos -> Some pos
        | CardAttached ->
            match card.Anchor with
            | AnchorToWorldPoint anchor ->
                match projectToScreen anchor viewTrafo vpSize with
                | Some screenPt ->
                    let pos = V2d(screenPt.X + card.Size.X * 0.4, screenPt.Y - card.Size.Y * 0.5 - 40.0)
                    Some (clampToViewport pos card.Size (V2d vpSize))
                | None -> None

    let renderCards (env : Env<Message>) (model : AdaptiveModel) (viewTrafo : aval<Trafo3d>) (vpSize : aval<V2i>) (hoverWorld : aval<V3d option>) (patchHover : cval<PatchHover option>) =
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
                let dragState = cval<(V2d * V2d) option> None
                let basePos = cardPositions |> AVal.map (fun dict ->
                    match dict.TryGetValue(cardId) with
                    | true, pos -> Some pos
                    | _ -> None)
                let effectivePos =
                    (basePos, dragState :> aval<_>) ||> AVal.map2 (fun b d ->
                        match d with
                        | Some (p, _) -> Some p
                        | None -> b)

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
                        Class "pin-card-color-bar"
                        selectedPin |> AVal.map (fun po ->
                            match po with
                            | Some p ->
                                let bg =
                                    match p.HostMeshName with
                                    | Some host ->
                                        match Map.tryFind host p.DatasetColors with
                                        | Some c -> sprintf "rgb(%d,%d,%d)" (int c.R) (int c.G) (int c.B)
                                        | None -> "#1a56db"
                                    | None -> "#1a56db"
                                Some (Style [Css.Background bg])
                            | None -> Some (Style [Display "none"]))
                    }

                    div {
                        Class "card-titlebar"

                        let isDetached = cardVal |> AVal.map (fun cOpt ->
                            match cOpt with
                            | Some c -> match c.Attachment with CardDetached _ -> true | _ -> false
                            | None -> false)

                        cardDragHandle
                            (selectedPin |> AVal.map (fun po ->
                                match po with
                                | Some pin -> sprintf "Pin · %s" pin.Name
                                | None -> "Pin"))
                            (effectivePos |> AVal.map (Option.defaultValue V2d.Zero))
                            dragState
                            (fun p -> env.Emit [CardMsg (BringToFront cardId); CardMsg (FinishDrag(cardId, p))])

                        button {
                            Class "card-btn-reattach"
                            Attribute("title", "Reattach to pin")
                            Primitives.showWhen isDetached
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
                        Primitives.showWhenNot isCollapsed
                        CardsPin.pinCardBody env model selectedPin hoverWorld patchHover
                    }
                }
            )
        }
