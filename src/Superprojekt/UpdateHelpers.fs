namespace Superprojekt

open Aardvark.Base
open Aardworx.WebAssembly
open Microsoft.JSInterop
open FSharp.Data.Adaptive
open Aardvark.Dom
open Superprojekt

// Dataset bootstrap: loads the dataset list + default dataset on startup;
// loadDataset fans out centroids + bboxes.
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
                let! autoLoad = MeshData.fetchDefaultDataset ApiConfig.apiBase.Value
                if not (System.String.IsNullOrEmpty autoLoad) && datasets |> Array.contains autoLoad then
                    env.Emit [SetActiveDataset autoLoad]
                    loadDataset env autoLoad
            with _ -> ()
        } |> ignore

module UpdateHelpers =

    let mutable toastCts : System.Threading.CancellationTokenSource =
        new System.Threading.CancellationTokenSource()

    // Surface-distance fetch: generation bumps on every invalidation; the postlude
    // issues at most one debounced fetch per generation (no spam in flight).
    let mutable surfaceDistCts = new System.Threading.CancellationTokenSource()
    let mutable surfaceDistGen = 0
    let mutable surfaceDistReqGen = -1
    let bumpSurfaceDist () = surfaceDistGen <- surfaceDistGen + 1

    // Focus difference channel fetch (region-distance per moving mesh) — same
    // generation-guarded debounce as the variance map.
    let mutable focusDistCts = new System.Threading.CancellationTokenSource()
    let mutable focusDistGen = 0
    let mutable focusDistReqGen = -1
    let bumpFocusDist () = focusDistGen <- focusDistGen + 1
    let invalidateFocusDist (model : Model) =
        if not (Map.isEmpty model.FocusDist) then bumpFocusDist ()
        { model with FocusDist = Map.empty }

    let invalidateProbes (model : Model) =
        // The variance + focus-difference maps share the same triggers — drop to
        // re-fetch lazily.
        if not (Map.isEmpty model.SurfaceDistance) then bumpSurfaceDist ()
        if not (Map.isEmpty model.FocusDist) then bumpFocusDist ()
        { model with
            ScanPins = ScanPinModel.invalidateProbes model.ScanPins
            SurfaceDistance = Map.empty
            FocusDist = Map.empty }

    // Rings depend on pin geometry + transforms, NOT visibility (which gates
    // rendering only) — so this is applied on transform changes alone, unlike invalidateProbes.
    let invalidateRings (model : Model) =
        { model with ScanPins = ScanPinModel.invalidateRings model.ScanPins }

    // Replace the visibility map, invalidating the visibility-derived Inspect data:
    // the variance aggregate is defined over the visible moving meshes (refetch), a
    // newly shown mesh may still lack its difference field (bump lets ensureFocusDist
    // fetch the missing entries; present entries are kept), and the brushed sample
    // ids index a visibility-dependent canonical array (would dangle).
    let setMeshVisible (vis : Map<string, bool>) (model : Model) =
        if vis = model.MeshVisible then model
        else
            if not (Map.isEmpty model.SurfaceDistance) then bumpSurfaceDist ()
            bumpFocusDist ()
            { model with MeshVisible = vis; SurfaceDistance = Map.empty; BrushedSamples = Set.empty }

    let allVisible (model : Model) =
        model.MeshNames |> IndexList.toSeq |> Seq.map (fun n -> n, true) |> Map.ofSeq

    // Isolation is an overlay (MeshVisible untouched on entry). The bump lets
    // ensureFocusDist fetch the isolated mesh's difference field if it is missing
    // (e.g. the mesh was hidden when Inspect fetched the visible set).
    let enterSolo (name : string) (model : Model) =
        if model.MeshSolo = Some name then model
        else
            bumpFocusDist ()
            { model with MeshSolo = Some name }

    // Ending isolation — ◐ re-click, workflow switch, or the Inspect pin-focus swap —
    // resets every visibility toggle to ON (spec: leaving isolation shows everything).
    let exitSolo (model : Model) =
        if model.MeshSolo.IsNone then model
        else setMeshVisible (allVisible model) { model with MeshSolo = None }

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

    // Every pin carries a correspondence, so this is effectively all pins.
    let correspondenceEnabledIds (model : Model) =
        model.ScanPins.Pins |> HashMap.toList
        |> List.choose (fun (id, p) ->
            match ScanPin.correspondence p with
            | Some _ -> Some id
            | None -> None)

    // Pin ROI reach: the probe cylinder's bounding-sphere radius (radius
    // InnerRadius ⊥ axis, length fixedProbeLength along it). A mesh whose closest
    // point to the pin centre is within this covers the ROI; beyond it it does not.
    let roiReach (innerRadius : float) =
        sqrt (innerRadius * innerRadius + (ScanPin.fixedProbeLength * 0.5) ** 2.0)

    // ROI-clamped auto-seed. refAnchor = pin centre (host = reference) or its
    // closest-point projection onto the reference; per moving mesh the closest
    // point to refAnchor. Anchors are stored mesh-local (own-frame closest point),
    // so the before/after toggle moves them with the mesh. Membership = the
    // candidate mapped to displayed world within roiReach of the pin centre;
    // out-of-ROI meshes are not seeded. forceMeshes overrides the keep-manual rule.
    let private seedAnchorsCore (env : Env<Message>) (model : Model) (pinIds : ScanPinId list)
                                (forceMeshes : Set<string>) : unit =
        match model.Registration.ReferenceMesh with
        | None -> ()
        | Some refMesh ->
            let pins =
                pinIds
                |> List.choose (fun id -> HashMap.tryFind id model.ScanPins.Pins)
                |> List.filter (fun p -> ScanPin.correspondence p |> Option.isSome)
            if List.isEmpty pins then ()
            else
                let meshes = model.MeshNames |> IndexList.toList
                let trafos =
                    meshes |> List.map (fun m -> m, ModelTransforms.displayedWorld model m) |> Map.ofList
                let refT = Map.tryFind refMesh trafos |> Option.defaultValue Trafo3d.Identity
                let jobs =
                    pins |> List.map (fun pin ->
                        let keep =
                            match ScanPin.correspondence pin with
                            | Some c -> c.Anchors |> Map.filter (fun m a -> a.Source <> AnchorAuto && not (Set.contains m forceMeshes))
                            | None -> Map.empty
                        pin.Id, pin.Centre, pin.InnerRadius, pin.HostMeshName, keep)
                task {
                    try
                        let! perPin =
                            jobs
                            |> List.map (fun (pinId, centre, innerR, host, keep) -> async {
                                let reach = roiReach innerR
                                // refAnchor stored in the reference own frame (= world,
                                // since the reference always sits at its LoadTransform).
                                let! refAnchor =
                                    if host = Some refMesh then
                                        async.Return (Some (refT.Backward.TransformPos centre, 0.0))
                                    else async {
                                        try
                                            let cOwn = refT.Backward.TransformPos centre
                                            let! res = Query.closestPoint ApiConfig.apiBase.Value refMesh 0 cOwn
                                            return res |> Option.map (fun r ->
                                                let world = refT.Forward.TransformPos r.point
                                                r.point, (world - centre).Length)
                                        with _ -> return None
                                    }
                                match refAnchor with
                                | None -> return (pinId, None, [||], [||])
                                | Some (raOwn, dist) ->
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
                                                // (own-frame point, displayed-world point)
                                                return mesh, (res |> Option.map (fun r -> r.point, t.Forward.TransformPos r.point))
                                            with _ -> return mesh, None
                                        })
                                        |> Async.Parallel
                                    // In-ROI ⇔ the candidate (displayed world) is within reach of the pin centre.
                                    let inRoi =
                                        resolved |> Array.map (fun (mesh, cand) ->
                                            let inside =
                                                match cand with
                                                | Some (_, w) -> (w - centre).Length <= reach
                                                | None -> false
                                            pinId, mesh, inside)
                                    let seeded =
                                        resolved |> Array.choose (fun (mesh, cand) ->
                                            match cand with
                                            | Some (own, w) when (w - centre).Length <= reach -> Some (pinId, mesh, own)
                                            | _ -> None)
                                    return (pinId, Some (raWorld, dist), seeded, inRoi)
                            })
                            |> Async.Parallel
                            |> Async.StartAsTask
                        let refUpdates =
                            perPin |> Array.choose (fun (pinId, raOpt, _, _) ->
                                raOpt |> Option.map (fun (ra, d) -> pinId, ra, d))
                        let seeded = perPin |> Array.collect (fun (_, _, s, _) -> s)
                        let inRoi = perPin |> Array.collect (fun (_, _, _, r) -> r)
                        env.Emit [AnchorsSeeded(refUpdates, seeded, inRoi)]
                    with ex ->
                        env.Emit [AnchorSeedFailed ex.Message]
                } |> ignore

    let seedAnchors (env : Env<Message>) (model : Model) (pinIds : ScanPinId list) : Model =
        seedAnchorsCore env model pinIds Set.empty
        model

    // Re-seed one mesh's correspondence for one pin — force-overwrites even a
    // manually-picked marker for that mesh.
    let reseedOneMesh (env : Env<Message>) (model : Model) (pinId : ScanPinId) (mesh : string) : Model =
        seedAnchorsCore env model [pinId] (Set.singleton mesh)
        model
