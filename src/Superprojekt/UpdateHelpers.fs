namespace Superprojekt

open Aardvark.Base
open Aardworx.WebAssembly
open Microsoft.JSInterop
open FSharp.Data.Adaptive
open Aardvark.Dom
open Superprojekt

// Dataset bootstrap (was in the removed StudyUpdate.fs). Loads the dataset
// list + default dataset on startup; loadDataset fans out centroids + bboxes.
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
                let! autoLoad = MeshData.fetchDefaultDataset ApiConfig.apiBase.Value
                if not (System.String.IsNullOrEmpty autoLoad) && datasets |> Array.contains autoLoad then
                    env.Emit [SetActiveDataset autoLoad]
                    loadDataset env autoLoad
            with _ -> ()
        } |> ignore

// Reducer helpers + module-level debounce/generation state, split out of
// Update.fs (opened there unqualified). The giant updateCore match stays put.
module UpdateHelpers =

    let mutable toastCts : System.Threading.CancellationTokenSource =
        new System.Threading.CancellationTokenSource()
    // C3: auto-open the panel the first time a registration pin is made this session.
    let mutable requirementsSurfaced = false

    // A2 surface-distance fetch: generation bumps on every invalidation; the
    // postlude issues at most one debounced fetch per generation (no spam in flight).
    let mutable surfaceDistCts = new System.Threading.CancellationTokenSource()
    let mutable surfaceDistGen = 0
    let mutable surfaceDistReqGen = -1
    let bumpSurfaceDist () = surfaceDistGen <- surfaceDistGen + 1

    let invalidateProbes (model : Model) =
        // A2 surface map shares these triggers (reference/transforms/visibility) — drop it to re-fetch lazily.
        if not (Map.isEmpty model.SurfaceDistance) then bumpSurfaceDist ()
        { model with
            ScanPins = ScanPinModel.invalidateProbes model.ScanPins
            SurfaceDistance = Map.empty }

    // Rings depend on pin geometry + transforms, NOT visibility (which gates
    // rendering only) — so this is applied on transform changes alone, unlike invalidateProbes.
    let invalidateRings (model : Model) =
        { model with ScanPins = ScanPinModel.invalidateRings model.ScanPins }

    let clearPreviewProbes (model : Model) =
        { model with ScanPins = ScanPinModel.invalidatePreviewProbes model.ScanPins }

    // Leaving the pending preview (commit/discard/reference change): drop
    // preview probes, recompute rings at the now-current pose.
    let exitPreview (model : Model) =
        clearPreviewProbes (invalidateRings { model with PendingReg = None })

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

    let updateCorr (id : ScanPinId) (f : Correspondence -> Correspondence) (sp : ScanPinModel) =
        match HashMap.tryFind id sp.Pins with
        | Some pin ->
            let cur = ScanPin.correspondence pin |> Option.defaultValue Correspondence.empty
            { sp with Pins = HashMap.add id (ScanPin.withCorrespondence (Some (f cur)) pin) sp.Pins }
        | None -> sp

    // Anchors are world-space at committed poses; commit/rollback re-bases every
    // anchor on a moved mesh by the applied world delta so it stays on the surface.
    // Host-aware (WP14): a plain pin's centre follows its host mesh's delta; a
    // correspondence pin's centre stays static (reference frame), only its anchors follow.
    let bakeAnchors (deltas : Map<string, Trafo3d>) (sp : ScanPinModel) =
        if Map.isEmpty deltas then sp
        else
            let pins =
                sp.Pins |> HashMap.map (fun _ p ->
                    match ScanPin.correspondence p with
                    | Some c ->
                        if Map.isEmpty c.Anchors then p
                        else
                            let anchors =
                                c.Anchors |> Map.map (fun mesh a ->
                                    match Map.tryFind mesh deltas with
                                    | Some d -> { a with Point = d.Forward.TransformPos a.Point }
                                    | None -> a)
                            ScanPin.withCorrespondence (Some { c with Anchors = anchors }) p
                    | None ->
                        match p.HostMeshName |> Option.bind (fun h -> Map.tryFind h deltas) with
                        | Some d -> { p with Centre = d.Forward.TransformPos p.Centre }
                        | None -> p)
            { sp with Pins = pins }

    let correspondenceEnabledIds (model : Model) =
        model.ScanPins.Pins |> HashMap.toList
        |> List.choose (fun (id, p) ->
            match ScanPin.correspondence p with
            | Some c when c.Enabled -> Some id
            | _ -> None)

    // §4 auto-seed. refAnchor = pin centre (host = reference) or its closest-point
    // projection onto the reference; per other mesh, the closest point to refAnchor.
    // Manually-picked (non-Auto) markers are never overwritten. One parallel fan-out;
    // results land via AnchorsSeeded and apply immediately (no review modal).
    let seedAnchors (env : Env<Message>) (model : Model) (pinIds : ScanPinId list) : Model =
        match model.Registration.ReferenceMesh with
        | None -> model
        | Some refMesh ->
            let pins =
                pinIds
                |> List.choose (fun id -> HashMap.tryFind id model.ScanPins.Pins)
                |> List.filter (fun p ->
                    ScanPin.correspondence p |> Option.map (fun c -> c.Enabled) |> Option.defaultValue false)
            if List.isEmpty pins then model
            else
                let meshes = model.MeshNames |> IndexList.toList
                let trafos =
                    meshes |> List.map (fun m -> m, ModelTransforms.committedWorld model m) |> Map.ofList
                let refT = Map.tryFind refMesh trafos |> Option.defaultValue Trafo3d.Identity
                let jobs =
                    pins |> List.map (fun pin ->
                        let keep =
                            match ScanPin.correspondence pin with
                            | Some c -> c.Anchors |> Map.filter (fun _ a -> a.Source <> AnchorAuto)
                            | None -> Map.empty
                        pin.Id, pin.Centre, pin.HostMeshName, keep)
                task {
                    try
                        let! perPin =
                            jobs
                            |> List.map (fun (pinId, centre, host, keep) -> async {
                                let! refAnchor =
                                    if host = Some refMesh then async.Return (Some (centre, 0.0))
                                    else async {
                                        try
                                            let cOwn = refT.Backward.TransformPos centre
                                            let! res = Query.closestPoint ApiConfig.apiBase.Value refMesh 0 cOwn
                                            return res |> Option.map (fun r ->
                                                let world = refT.Forward.TransformPos r.point
                                                world, (world - centre).Length)
                                        with _ -> return None
                                    }
                                match refAnchor with
                                | None -> return (pinId, None, [||])
                                | Some (ra, dist) ->
                                    let targets =
                                        meshes |> List.filter (fun m ->
                                            m <> refMesh && not (Map.containsKey m keep))
                                    let! seeded =
                                        targets
                                        |> List.map (fun mesh -> async {
                                            try
                                                let t = Map.tryFind mesh trafos |> Option.defaultValue Trafo3d.Identity
                                                let cOwn = t.Backward.TransformPos ra
                                                let! res = Query.closestPoint ApiConfig.apiBase.Value mesh 0 cOwn
                                                return res |> Option.map (fun r -> pinId, mesh, t.Forward.TransformPos r.point)
                                            with _ -> return None
                                        })
                                        |> Async.Parallel
                                    return (pinId, Some (ra, dist), seeded |> Array.choose id)
                            })
                            |> Async.Parallel
                            |> Async.StartAsTask
                        let refUpdates =
                            perPin |> Array.choose (fun (pinId, raOpt, _) ->
                                raOpt |> Option.map (fun (ra, d) -> pinId, ra, d))
                        let seeded = perPin |> Array.collect (fun (_, _, s) -> s)
                        env.Emit [AnchorsSeeded(refUpdates, seeded)]
                    with ex ->
                        env.Emit [AnchorSeedFailed ex.Message]
                } |> ignore
                model
