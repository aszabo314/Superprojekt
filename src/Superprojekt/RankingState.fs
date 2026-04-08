namespace Superprojekt

open FSharp.Data.Adaptive

/// Per-pin dataset ranking state. Held as global cvals: ranking is a UI concept
/// that follows the currently selected pin and resets when the dataset list changes.
/// V3 will move this onto the Model proper alongside the stratigraphy diagram state.
module RankingState =

    let datasetOrder  = cval<string list> []
    let datasetHidden = cset<string> HashSet.empty
    let topK          = cval 5
    let rankFadeOn    = cval false

    let private resync (names : string list) =
        let cur = datasetOrder.Value
        let curSet = Set.ofList cur
        let nameSet = Set.ofList names
        let kept = cur |> List.filter (fun n -> Set.contains n nameSet)
        let added = names |> List.filter (fun n -> not (Set.contains n curSet))
        let next = kept @ added
        if next <> cur then datasetOrder.Value <- next

    let ensureDatasets (names : string list) =
        if names <> [] && (datasetOrder.Value <> names || datasetOrder.Value = []) then
            transact (fun () -> resync names)

    let rankOf (name : string) : aval<int option> =
        let hiddenAVal = datasetHidden :> aset<_> |> ASet.toAVal
        (datasetOrder, hiddenAVal) ||> AVal.map2 (fun order hidden ->
            order
            |> List.filter (fun n -> not (HashSet.contains n hidden))
            |> List.tryFindIndex ((=) name))

    let inTopK (name : string) : aval<bool> =
        (rankOf name, topK) ||> AVal.map2 (fun ro k ->
            match ro with Some r -> r < k | None -> false)

    let move (name : string) (delta : int) =
        let lst = datasetOrder.Value |> List.toArray
        let idx = lst |> Array.tryFindIndex ((=) name)
        match idx with
        | Some i ->
            let j = i + delta
            if j >= 0 && j < lst.Length then
                let tmp = lst.[i]
                lst.[i] <- lst.[j]
                lst.[j] <- tmp
                transact (fun () -> datasetOrder.Value <- lst |> Array.toList)
        | None -> ()

    let toggleHidden (name : string) =
        transact (fun () ->
            if HashSet.contains name datasetHidden.Value then datasetHidden.Remove name |> ignore
            else datasetHidden.Add name |> ignore)
