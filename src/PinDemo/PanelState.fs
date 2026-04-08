namespace PinDemo

open Aardvark.Base
open FSharp.Data.Adaptive
open Superprojekt

type AggregationMode = Average | Q1 | Q3 | Difference

module PanelState =

    let pin : ScanPin = DummyData.pin

    let datasets : DummyDataset[] = DummyData.datasets

    // ── Cut plane ────────────────────────────────────────────────
    let cutMode = cval (CutPlaneMode.AcrossAxis 0.0)

    // ── Core sample 3D view ──────────────────────────────────────
    let coreViewMode = cval SideView
    let coreRotation = cval 0.0   // radians around Z
    let coreZoom     = cval 1.0

    // ── Dataset ranking / visibility ─────────────────────────────
    let datasetHidden = cset<string> HashSet.empty
    let datasetOrder  = cval (datasets |> Array.map (fun d -> d.MeshName) |> Array.toList)
    let topK          = cval 5
    let rankFadeOn    = cval false

    /// Rank within the visible (non-hidden) ordering, or None if hidden / not in list.
    let rankOf (name : string) : aval<int option> =
        let hiddenAVal = datasetHidden :> aset<_> |> ASet.toAVal
        (datasetOrder, hiddenAVal) ||> AVal.map2 (fun order hidden ->
            order
            |> List.filter (fun n -> not (HashSet.contains n hidden))
            |> List.tryFindIndex ((=) name))

    let inTopK (name : string) : aval<bool> =
        (rankOf name, topK) ||> AVal.map2 (fun ro k ->
            match ro with Some r -> r < k | None -> false)

    let opacityOf (name : string) : aval<float> =
        (rankOf name, topK, rankFadeOn) |||> AVal.map3 (fun ro k fade ->
            if not fade then 1.0
            else
                match ro with
                | Some r when k > 1 ->
                    let t = float r / float (k - 1)
                    1.0 - t * 0.75
                | Some _ -> 1.0
                | None -> 0.0)

    let moveDataset (name : string) (delta : int) =
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

    // ── Aggregation overlay (summary mesh mode) ──────────────────
    let aggregationMode = cval Average

    // ── Rendering effects ────────────────────────────────────────
    let depthShadeOn = cval true
    let isolinesOn   = cval true
    let colorMode    = cval false
