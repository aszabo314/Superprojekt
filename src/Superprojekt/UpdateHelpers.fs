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
                let! pcs = MeshData.fetchPanoCenters ApiConfig.apiBase.Value dataset
                env.Emit [PanoCentersLoaded pcs]
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

    // Correspondence-commit confirmation flash (same restartable-timer pattern
    // as the toast).
    let mutable corrFlashCts : System.Threading.CancellationTokenSource =
        new System.Threading.CancellationTokenSource()

    // Solve fan-out guard: CoarseSolved carries the generation it was issued
    // under; anything that clears the registration bumps it, so a stale solve
    // can never land after ensureSolveValidity / a reference or dataset change.
    let mutable solveGen = 0
    let bumpSolveGen () = solveGen <- solveGen + 1

    // Focus difference channel fetch (region-distance per moving mesh) — same
    // generation-guarded debounce as the variance map.
    let mutable focusDistCts = new System.Threading.CancellationTokenSource()
    let mutable focusDistGen = 0
    let mutable focusDistReqGen = -1
    let bumpFocusDist () = focusDistGen <- focusDistGen + 1
    let invalidateFocusDist (model : Model) =
        if not (Map.isEmpty model.FocusDist && Map.isEmpty model.FocusDistOther) then bumpFocusDist ()
        // The exact-point probe reads the dropped field — it goes with it.
        { model with FocusDist = Map.empty; FocusDistOther = Map.empty; PointProbe = None }

    let invalidateProbes (model : Model) =
        // The focus-difference maps (both poses) share the probe triggers —
        // drop to re-fetch lazily.
        { (invalidateFocusDist model) with
            ScanPins = ScanPinModel.invalidateProbes model.ScanPins }

    // Rings depend on pin geometry + transforms, NOT visibility (which gates
    // rendering only) — so this is applied on transform changes alone, unlike invalidateProbes.
    let invalidateRings (model : Model) =
        { model with ScanPins = ScanPinModel.invalidateRings model.ScanPins }

    // Switch the committed Before/After view: swap every pose-baked pair cache in
    // place (probes, slices, the Inspect scalar maps), cancel in-flight scalar
    // fetches (a result landing after the swap would file under the wrong pose),
    // drop the pose-indexed brush gids, refetch rings.
    let applyRegView (v : RegView) (model : Model) =
        if model.RegView = v || Map.isEmpty model.SolvedTransforms then model
        else
            focusDistCts.Cancel()
            bumpFocusDist ()
            invalidateRings
                { model with
                    RegView = v
                    BrushedSamples = Set.empty
                    PointProbe = None
                    ScanPins = ScanPinModel.swapProbeViews model.ScanPins
                    FocusDist = model.FocusDistOther
                    FocusDistOther = model.FocusDist }

    // The bump lets ensureFocusDist fetch the isolated mesh's difference field
    // if it is missing.
    let enterSolo (name : string) (model : Model) =
        if model.MeshSolo = Some name then model
        else
            bumpFocusDist ()
            { model with MeshSolo = Some name }

    // Symmetric bump: leaving solo re-opens the whole ensemble's difference
    // fields, some of which may have been skipped while isolated.
    let exitSolo (model : Model) =
        if model.MeshSolo.IsNone then model
        else
            bumpFocusDist ()
            { model with MeshSolo = None }

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
            { sp with Pins = HashMap.add id (ScanPin.withCorrespondence (f (ScanPin.correspondence pin)) pin) sp.Pins }
        | None -> sp

    // Every pin IS a registration correspondence.
    let allPinIds (model : Model) =
        model.ScanPins.Pins |> HashMap.toList |> List.map fst

    // ROI-clamped auto-seed. refAnchor = the pin centre's closest-point
    // projection onto the reference; per moving mesh the closest point to
    // refAnchor. Anchors are stored mesh-local (own-frame closest point),
    // so the before/after toggle moves them with the mesh. All geometry is
    // evaluated at the BEFORE (load) pose regardless of the current view —
    // correspondences exist in the Before state only, so a mesh that a solve
    // moved into a pin's area must NOT gain a marker. Two zones: an anchor is
    // accepted within the pin sphere (InnerRadius); InRoi membership (can the
    // probe measure here) uses the wider ScanPin.roiReach. Manually-picked
    // markers are kept.
    let private seedAnchorsCore (env : Env<Message>) (model : Model) (pinIds : ScanPinId list) : unit =
        match model.ReferenceMesh with
        | None -> ()
        | Some refMesh ->
            let pins =
                pinIds |> List.choose (fun id -> HashMap.tryFind id model.ScanPins.Pins)
            if List.isEmpty pins then ()
            else
                let meshes = model.MeshNames |> IndexList.toList
                let trafos =
                    meshes |> List.map (fun m -> m, ModelTransforms.displayedWorldAt RegBefore model m) |> Map.ofList
                let refT = Map.tryFind refMesh trafos |> Option.defaultValue Trafo3d.Identity
                let jobs =
                    pins |> List.map (fun pin ->
                        let keep = (ScanPin.correspondence pin).Anchors |> Map.filter (fun _ a -> a.Source <> AnchorAuto)
                        pin.Id, pin.Centre, pin.InnerRadius, keep)
                task {
                    try
                        let! perPin =
                            jobs
                            |> List.map (fun (pinId, centre, innerR, keep) -> async {
                                let reach = ScanPin.roiReach innerR
                                // refAnchor stored in the reference own frame (= world,
                                // since the reference always sits at its LoadTransform).
                                let! refAnchor =
                                    async {
                                        try
                                            let cOwn = refT.Backward.TransformPos centre
                                            let! res = Query.closestPoint ApiConfig.apiBase.Value refMesh 0 cOwn
                                            return res |> Option.map (fun r -> r.point)
                                        with _ -> return None
                                    }
                                // The refAnchor is a correspondence point too — a
                                // projection landing outside the pin sphere is rejected.
                                let refAnchor =
                                    refAnchor |> Option.filter (fun raOwn ->
                                        (refT.Forward.TransformPos raOwn - centre).Length <= innerR)
                                match refAnchor with
                                | None -> return (pinId, None, [||], [||])
                                | Some raOwn ->
                                    let raWorld = refT.Forward.TransformPos raOwn
                                    let targets =
                                        meshes |> List.filter (fun m ->
                                            m <> refMesh && not (Map.containsKey m keep))
                                    let! resolved =
                                        targets
                                        |> List.map (fun mesh -> async {
                                            try
                                                let t = Map.tryFind mesh trafos |> Option.defaultValue Trafo3d.Identity
                                                let cOwn = t.Backward.TransformPos raWorld
                                                let! res = Query.closestPoint ApiConfig.apiBase.Value mesh 0 cOwn
                                                // (own-frame point, Before-pose world point)
                                                return mesh, (res |> Option.map (fun r -> r.point, t.Forward.TransformPos r.point))
                                            with _ -> return mesh, None
                                        })
                                        |> Async.Parallel
                                    // In-ROI ⇔ the candidate (Before world) is within measurement reach.
                                    let inRoi =
                                        resolved |> Array.map (fun (mesh, cand) ->
                                            let inside =
                                                match cand with
                                                | Some (_, w) -> (w - centre).Length <= reach
                                                | None -> false
                                            pinId, mesh, inside)
                                    // An anchor must lie within the pin sphere itself.
                                    let seeded =
                                        resolved |> Array.choose (fun (mesh, cand) ->
                                            match cand with
                                            | Some (own, w) when (w - centre).Length <= innerR -> Some (pinId, mesh, own)
                                            | _ -> None)
                                    return (pinId, Some raWorld, seeded, inRoi)
                            })
                            |> Async.Parallel
                            |> Async.StartAsTask
                        let refUpdates =
                            perPin |> Array.choose (fun (pinId, raOpt, _, _) ->
                                raOpt |> Option.map (fun ra -> pinId, ra))
                        let seeded = perPin |> Array.collect (fun (_, _, s, _) -> s)
                        let inRoi = perPin |> Array.collect (fun (_, _, _, r) -> r)
                        env.Emit [AnchorsSeeded(refUpdates, seeded, inRoi)]
                    with ex ->
                        env.Emit [AnchorSeedFailed ex.Message]
                } |> ignore

    let seedAnchors (env : Env<Message>) (model : Model) (pinIds : ScanPinId list) : Model =
        seedAnchorsCore env model pinIds
        model
