namespace Superprojekt

open FSharp.Data.Adaptive
open Aardvark.Base
open Superprojekt

module CardUpdate =

    let private cardContentPinId (c : CardContent) =
        match c with
        | PinCard id -> id

    let update (msg : CardMessage) (cs : CardSystemModel) =
        match msg with
        | CreateCardsForPin(pinId, anchor) ->
            let hasCard =
                cs.Cards |> HashMap.exists (fun _ c ->
                    match c.Content with PinCard id when id = pinId -> true | _ -> false)
            if hasCard then
                let cards = cs.Cards |> HashMap.map (fun _ c ->
                    match c.Content with
                    | PinCard id when id = pinId ->
                        { c with Visible = true; Anchor = AnchorToWorldPoint anchor }
                    | _ -> { c with Visible = false })
                { cs with Cards = cards }
            else
                let hideOthers = cs.Cards |> HashMap.map (fun _ c -> { c with Visible = false })
                let cardId = CardId.create()
                let z = cs.NextZOrder
                let card = { Id = cardId; Anchor = AnchorToWorldPoint anchor; Attachment = CardAttached; Size = V2d(310, 230); Content = PinCard pinId; Visible = true; ZOrder = z }
                let cards = hideOthers |> HashMap.add cardId card
                { cs with Cards = cards; NextZOrder = z + 1 }

        | RemoveCardsForPin pinId ->
            let cards = cs.Cards |> HashMap.map (fun _ c ->
                if cardContentPinId c.Content = pinId then { c with Visible = false } else c)
            { cs with Cards = cards }

        | FinishDrag(id, finalPos) ->
            match HashMap.tryFind id cs.Cards with
            | Some card ->
                { cs with Cards = HashMap.add id { card with Attachment = CardDetached finalPos } cs.Cards }
            | None -> cs

        | RedockCard id ->
            match HashMap.tryFind id cs.Cards with
            | Some card ->
                { cs with Cards = HashMap.add id { card with Attachment = CardAttached } cs.Cards }
            | None -> cs

        | BringToFront id ->
            match HashMap.tryFind id cs.Cards with
            | Some card ->
                let z = cs.NextZOrder
                { cs with Cards = HashMap.add id { card with ZOrder = z } cs.Cards; NextZOrder = z + 1 }
            | None -> cs
