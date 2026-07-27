namespace Superprojekt

open Aardvark.Base
open Aardworx.WebAssembly
open FSharp.Data.Adaptive
open Aardvark.Dom
open Superprojekt

module ScanPinUpdate =

    // Contact-ring queries run per pin (several can recompute at once after a
    // registration), so each pin gets its own debounce token.
    let private ringsCts =
        System.Collections.Generic.Dictionary<ScanPinId, System.Threading.CancellationTokenSource>()

    // Cancel-and-replace a pin's debounce token — the ONE per-pin debounce
    // discipline (the next invalidation cancels the previous fetch).
    let private restartCts (map : System.Collections.Generic.Dictionary<ScanPinId, System.Threading.CancellationTokenSource>) (id : ScanPinId) =
        match map.TryGetValue id with
        | true, cts -> cts.Cancel()
        | _ -> ()
        let cts = new System.Threading.CancellationTokenSource()
        map.[id] <- cts
        cts.Token

    // Mint the atomic pin from a COMPLETE draft — the only way a pin is born.
    let private makePin (model : Model) (id : ScanPinId)
                        (pair : string * string) (anchorMesh : string) (centreLocal : V3d)
                        (pa : V3d) (pb : V3d) =
        let existing = model.ScanPins.Pins |> HashMap.toList |> List.map snd
        let taken =
            let pinNames = existing |> List.map (fun p -> p.ShortName) |> Set.ofList
            let meshNums = model.MeshOrder |> HashMap.toList |> List.map (fun (_, i) -> string (i + 1)) |> Set.ofList
            Set.union pinNames meshNums
        let (ScanPinId.ScanPinId g) = id
        {
            Id           = id
            ShortName    = Primitives.PinIdentity.shortName taken (g.GetHashCode())
            Pair         = pair
            AnchorMesh   = anchorMesh
            CentreLocal  = centreLocal
            InnerRadius  = max 0.01 model.QuickPinRadius
            PointA       = pa
            PointB       = pb
            CreatedAt    = System.DateTime.UtcNow
            ContactRings = RingsNone
        }

    let private updatePin (id : ScanPinId) (f : ScanPin -> ScanPin) (sp : ScanPinModel) =
        match HashMap.tryFind id sp.Pins with
        | Some pin -> { sp with Pins = HashMap.add id (f pin) sp.Pins }
        | None -> sp

    let update (model : Model) (msg : ScanPinMessage) (sp : ScanPinModel) =
        match msg with
        | BeginPinTransaction pair ->
            { sp with Placement = PlacementActive(ToolArea, PinDraft.empty pair); Edit = EditIdle }

        | SetDraftTool tool ->
            match sp.Placement with
            | PlacementActive(_, d) -> { sp with Placement = PlacementActive(tool, d) }
            | PlacementIdle -> sp

        | DraftAreaAt(mesh, local) ->
            match sp.Placement with
            | PlacementActive(_, d) ->
                // Dropping the area auto-advances to point picking; the tool
                // buttons re-arm either sub-tool at any time (free order).
                { sp with Placement = PlacementActive(ToolPoint, { d with Area = Some(mesh, local) }) }
            | PlacementIdle -> sp

        | DraftPointAt(mesh, local) ->
            match sp.Placement with
            | PlacementActive(tool, d) ->
                // The hit mesh attributes the point; re-picking replaces.
                let d =
                    if mesh = fst d.Pair then { d with PointA = Some local }
                    elif mesh = snd d.Pair then { d with PointB = Some local }
                    else d
                { sp with Placement = PlacementActive(tool, d) }
            | PlacementIdle -> sp

        | CommitPin ->
            match sp.Placement with
            | PlacementActive(_, d) ->
                match d.Area, d.PointA, d.PointB with
                | Some (am, c), Some pa, Some pb ->
                    let id = ScanPinId.create()
                    { sp with
                        Pins = HashMap.add id (makePin model id d.Pair am c pa pb) sp.Pins
                        Placement = PlacementIdle }
                | _ -> sp    // incomplete — the commit control is disabled anyway
            | PlacementIdle -> sp

        | AbortPinTransaction ->
            // Full rollback: the draft never touched the pin map.
            { sp with Placement = PlacementIdle }

        | SetInnerRadius(id, r) ->
            sp |> updatePin id (fun pin -> { pin with InnerRadius = max 0.01 r; ContactRings = RingsNone })

        | BeginPointEdit(id, mesh) ->
            { sp with Edit = EditPoint(id, mesh); Placement = PlacementIdle }

        | CancelPointEdit ->
            { sp with Edit = EditIdle }

        | EditPointAt(id, mesh, local) ->
            let sp = { sp with Edit = EditIdle }
            sp |> updatePin id (fun pin ->
                if mesh = fst pin.Pair then { pin with PointA = local }
                elif mesh = snd pin.Pair then { pin with PointB = local }
                else pin)

        | DeletePin id ->
            { sp with Pins = HashMap.remove id sp.Pins }

        // Stale guard: results only land while still RingsRunning; any intervening invalidation wins.
        | ContactRingsComputed(id, rings) ->
            sp |> updatePin id (fun pin ->
                if pin.ContactRings = RingsRunning then { pin with ContactRings = RingsReady rings } else pin)

    let handleMsg (env : Env<Message>) (model : Model) (msg : ScanPinMessage) =
        // Deliberately NO camera motion on any pin action — the main camera
        // moves only on explicit zoom actions.
        { model with ScanPins = update model msg model.ScanPins }

    // Lazy contact-ring trigger, postlude after every reducer step: every
    // RingsNone pin gets one debounced fan-out over ITS PAIR's meshes
    // (visibility only gates rendering, so navigating never recomputes).
    // Transforms are rigid: sphere intersected in each mesh's own frame
    // (inverse-transformed centre), rings mapped back. The centre itself rides
    // the anchor mesh's displayed pose, so rings recompute on pose changes
    // (recomposePoses → invalidateRings).
    let ensureRings (env : Env<Message>) (model : Model) : Model =
        let sp = model.ScanPins
        // Cheap exists-check first: this postlude runs on every message (incl. per-frame
        // Rendered), so avoid allocating the filtered list when nothing is pending.
        if model.MeshNames.Count = 0 || not (sp.Pins |> HashMap.exists (fun _ p -> p.ContactRings = RingsNone)) then model
        else
            let pending =
                sp.Pins |> HashMap.toList
                |> List.filter (fun (_, p) -> p.ContactRings = RingsNone)
            let mutable pins = sp.Pins
            for (pinId, pin) in pending do
                let token = restartCts ringsCts pinId
                let centre =
                    ScanPin.centreWorldWith (ModelTransforms.displayedWorld model pin.AnchorMesh) pin
                let radius = pin.InnerRadius
                let meshes =
                    [ fst pin.Pair; snd pin.Pair ]
                    |> List.map (fun n -> n, ModelTransforms.displayedWorld model n)
                task {
                    try
                        do! System.Threading.Tasks.Task.Delay(250, token)
                        let! results =
                            meshes
                            |> List.map (fun (n, tw) -> async {
                                try
                                    let cOwn = tw.Backward.TransformPos centre
                                    let! rings = Query.contactRings ApiConfig.apiBase.Value n cOwn radius 4096
                                    let ringsWorld = rings |> Array.map (Array.map tw.Forward.TransformPos)
                                    return if ringsWorld.Length = 0 then None else Some (n, ringsWorld)
                                with _ -> return None })
                            |> Async.Parallel
                            |> Async.StartAsTask
                        if not token.IsCancellationRequested then
                            let map = results |> Array.choose (fun r -> r) |> Map.ofArray
                            env.Emit [ScanPinMsg (ContactRingsComputed(pinId, map))]
                    with
                    | :? System.OperationCanceledException -> ()
                    | _ -> ()
                } |> ignore
                pins <- HashMap.add pinId { pin with ContactRings = RingsRunning } pins
            { model with ScanPins = { sp with Pins = pins } }
