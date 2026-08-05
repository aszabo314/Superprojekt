namespace Superprojekt

open Aardvark.Base
open Aardworx.WebAssembly
open Microsoft.JSInterop
open FSharp.Data.Adaptive
open Aardvark.Dom
open Superprojekt

module ServerActions =

    let loadDataset (env : Env<Message>) (dataset : string) =
        task {
            try
                let! cs = MeshData.fetchCentroids ApiConfig.apiBase.Value dataset
                env.Emit [CentroidsLoaded cs]
            with _ -> ()
            try
                let! bboxes = MeshData.fetchBboxes ApiConfig.apiBase.Value dataset
                env.Emit [SceneBoundsLoaded bboxes]
            with _ -> ()
        } |> ignore

    let init (env : Env<Message>) =
        task {
            try
                let! datasets = MeshData.fetchDatasets ApiConfig.apiBase.Value
                env.Emit [DatasetsLoaded datasets]
                let! autoLoad =
                    match ApiConfig.urlDataset.Value |> Option.filter (fun d -> datasets |> Array.contains d) with
                    | Some d -> async { return d }
                    | None -> MeshData.fetchDefaultDataset ApiConfig.apiBase.Value
                if not (System.String.IsNullOrEmpty autoLoad) && datasets |> Array.contains autoLoad then
                    env.Emit [SetActiveDataset autoLoad]
                    loadDataset env autoLoad
            with _ -> ()
        } |> ignore

module UpdateHelpers =

    let mutable toastCts : System.Threading.CancellationTokenSource =
        new System.Threading.CancellationTokenSource()

    // Inspection guards: ONE generation shared by every scope's error / map /
    // readout fetch (cell AND graph) — any invalidation bumps it so stale
    // results land dead; the req markers give each fetch single-flight
    // semantics.
    let mutable cellErrorGen = 0
    let mutable cellErrorReqGen = -1
    let mutable cellDistReqGen = -1
    let mutable graphErrorReqGen = -1
    let mutable graphDistReqGen = -1
    let bumpCellError () = cellErrorGen <- cellErrorGen + 1

    // Pair-solve landing guard: PairSolved carries the generation it was
    // issued under; edits/aborts bump it so a stale solve can never land.
    let mutable pairSolveGen = 0
    let bumpPairSolve () = pairSolveGen <- pairSolveGen + 1

    // Pairwise-overlap sweep guard: one flight per generation; a dataset switch
    // bumps the generation so stale sweeps land dead.
    let mutable pairOverlapGen = 0
    let mutable pairOverlapReqGen = -1
    let bumpPairOverlap () = pairOverlapGen <- pairOverlapGen + 1

    // Rings depend on pin geometry + transforms, NOT visibility (which gates
    // rendering only).
    let invalidateRings (model : Model) =
        { model with ScanPins = ScanPinModel.invalidateRings model.ScanPins }

    // Drop every inspection cache at BOTH scopes (error distributions, the map
    // buffers, brush/hover readouts) — on nav, pin and pose changes alike. The
    // graph caches ride the same generation: they outlive nothing a pair cache
    // outlives.
    let invalidateCellError (model : Model) =
        bumpCellError ()
        { model with
            CellError = None; CellErrorBefore = None; CellDist = None
            GraphError = None; GraphErrorBefore = None
            GraphDist = Map.empty; GraphDistBefore = Map.empty
            BrushedSamples = Set.empty; HoverSample = None; HoverReadout = None }

    let logReach (source : string) (action : string) (subject : string) (model : Model) =
        { model with
            ReachLog =
                { At = System.DateTime.UtcNow; Source = source; Action = action; Subject = subject }
                :: model.ReachLog }

    let showToast (env : Env<Message>) (text : string) (model : Model) =
        toastCts.Cancel()
        toastCts <- new System.Threading.CancellationTokenSource()
        let token = toastCts.Token
        task {
            try
                do! System.Threading.Tasks.Task.Delay(3000, token)
                if not token.IsCancellationRequested then env.Emit [ClearToast]
            with _ -> ()
        } |> ignore
        { model with Toast = Some text }
