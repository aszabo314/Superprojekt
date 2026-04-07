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
    let corePanZ     = cval 0.0
    let coreZoom     = cval 1.0

    // ── Dataset ranking / visibility ─────────────────────────────
    let datasetOrder =
        clist (datasets |> Array.map (fun d -> d.MeshName) |> Array.toList)

    let datasetHidden = cset<string> HashSet.empty

    // ── Aggregation overlay (summary mesh mode) ──────────────────
    let aggregationMode = cval Average
    let showSummary     = cval false

    // ── Rendering effects ────────────────────────────────────────
    let depthShadeOn = cval true
    let isolinesOn   = cval true

    // ── Footprint radius ─────────────────────────────────────────
    let footprintRadius =
        let r =
            match pin.Prism.Footprint.Vertices with
            | v :: _ -> v.Length
            | _ -> 5.0
        cval r
