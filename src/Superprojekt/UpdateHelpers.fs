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

    // A2 surface-distance fetch: generation bumps on every invalidation; the
    // postlude issues at most one debounced fetch per generation (no spam in flight).
    let mutable surfaceDistCts = new System.Threading.CancellationTokenSource()
    let mutable surfaceDistGen = 0
    let mutable surfaceDistReqGen = -1
    let bumpSurfaceDist () = surfaceDistGen <- surfaceDistGen + 1

    // Focus-panel small-multiples previews: one debounced fan-out per generation
    // (projection / channel / context / reference / transform / visibility bump).
    let mutable focusMapsCts = new System.Threading.CancellationTokenSource()
    let mutable focusMapsGen = 0
    let mutable focusMapsReqGen = -1
    let bumpFocusMaps () = focusMapsGen <- focusMapsGen + 1
    let invalidateFocusMaps (model : Model) =
        if not (Map.isEmpty model.FocusMaps) then bumpFocusMaps ()
        { model with FocusMaps = Map.empty }

    // Effective visible meshes for the focus multiples: a hard solo (main view)
    // hides others, but the multiples follow the pre-solo restore set so every
    // panel cell stays present (spec §E "always visible").
    let effectiveVisibleMeshes (model : Model) =
        let vis = match model.MeshSolo with Solo(_, restore) -> restore | NoSolo -> model.MeshVisible
        model.MeshNames |> IndexList.toList
        |> List.filter (fun n -> Map.tryFind n vis |> Option.defaultValue true)

    let invalidateProbes (model : Model) =
        // A2 surface map + focus previews share these triggers (reference/transforms/visibility) — drop to re-fetch lazily.
        if not (Map.isEmpty model.SurfaceDistance) then bumpSurfaceDist ()
        if not (Map.isEmpty model.FocusMaps) then bumpFocusMaps ()
        { model with
            ScanPins = ScanPinModel.invalidateProbes model.ScanPins
            SurfaceDistance = Map.empty
            FocusMaps = Map.empty }

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

    // Pin ROI reach: the probe cylinder's bounding-sphere radius (radius
    // InnerRadius ⊥ axis, length fixedProbeLength along it). A mesh whose closest
    // point to the pin centre is within this covers the ROI; beyond it the mesh
    // does not reach the pin (v3 §C out-of-ROI).
    let roiReach (innerRadius : float) =
        sqrt (innerRadius * innerRadius + (ScanPin.fixedProbeLength * 0.5) ** 2.0)

    // §C auto-seed (ROI-clamped). refAnchor = pin centre (host = reference) or its
    // closest-point projection onto the reference; per moving mesh the closest
    // point to refAnchor. Membership = the seeded point within roiReach of the pin
    // centre; out-of-ROI meshes are not seeded. forceMeshes overrides the
    // "keep manual markers" rule for the listed meshes (the ⟳ per-mesh re-seed).
    let private seedAnchorsCore (env : Env<Message>) (model : Model) (pinIds : ScanPinId list)
                                (forceMeshes : Set<string>) : unit =
        match model.Registration.ReferenceMesh with
        | None -> ()
        | Some refMesh ->
            let pins =
                pinIds
                |> List.choose (fun id -> HashMap.tryFind id model.ScanPins.Pins)
                |> List.filter (fun p ->
                    ScanPin.correspondence p |> Option.map (fun c -> c.Enabled) |> Option.defaultValue false)
            if List.isEmpty pins then ()
            else
                let meshes = model.MeshNames |> IndexList.toList
                let trafos =
                    meshes |> List.map (fun m -> m, ModelTransforms.committedWorld model m) |> Map.ofList
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
                                | None -> return (pinId, None, [||], [||])
                                | Some (ra, dist) ->
                                    let targets =
                                        meshes |> List.filter (fun m ->
                                            m <> refMesh && not (Map.containsKey m keep))
                                    let! resolved =
                                        targets
                                        |> List.map (fun mesh -> async {
                                            try
                                                let t = Map.tryFind mesh trafos |> Option.defaultValue Trafo3d.Identity
                                                let cOwn = t.Backward.TransformPos ra
                                                let! res = Query.closestPoint ApiConfig.apiBase.Value mesh 0 cOwn
                                                return mesh, (res |> Option.map (fun r -> t.Forward.TransformPos r.point))
                                            with _ -> return mesh, None
                                        })
                                        |> Async.Parallel
                                    // In-ROI ⇔ the candidate is within reach of the pin centre.
                                    let inRoi =
                                        resolved |> Array.map (fun (mesh, cand) ->
                                            let inside =
                                                match cand with
                                                | Some p -> (p - centre).Length <= reach
                                                | None -> false
                                            pinId, mesh, inside)
                                    let seeded =
                                        resolved |> Array.choose (fun (mesh, cand) ->
                                            match cand with
                                            | Some p when (p - centre).Length <= reach -> Some (pinId, mesh, p)
                                            | _ -> None)
                                    return (pinId, Some (ra, dist), seeded, inRoi)
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

    // ⟳ re-seed one mesh's correspondence for one pin (v3 §F) — force-overwrites
    // even a manually-picked marker for that mesh.
    let reseedOneMesh (env : Env<Message>) (model : Model) (pinId : ScanPinId) (mesh : string) : Model =
        seedAnchorsCore env model [pinId] (Set.singleton mesh)
        model
