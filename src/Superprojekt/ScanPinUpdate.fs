namespace Superprojekt

open Aardvark.Base
open Aardworx.WebAssembly
open FSharp.Data.Adaptive
open Aardvark.Dom
open Superprojekt

module ScanPinUpdate =

    // Contact-ring / reveal queries run per figure (several can recompute at
    // once after a registration), so each gets its own debounce token; the
    // singular draft keys side 0/1 (+ 2 for its area rings) in draftCts.
    let private ringsCts =
        System.Collections.Generic.Dictionary<ScanPinId, System.Threading.CancellationTokenSource>()
    let private revealCts =
        System.Collections.Generic.Dictionary<ScanPinId * int, System.Threading.CancellationTokenSource>()
    let private draftCts =
        System.Collections.Generic.Dictionary<int, System.Threading.CancellationTokenSource>()

    // Cancel-and-replace a figure's debounce token — the ONE debounce
    // discipline (the next invalidation cancels the previous fetch).
    let private restartCts (map : System.Collections.Generic.Dictionary<'k, System.Threading.CancellationTokenSource>) (id : 'k) =
        match map.TryGetValue id with
        | true, cts -> cts.Cancel()
        | _ -> ()
        let cts = new System.Threading.CancellationTokenSource()
        map.[id] <- cts
        cts.Token

    // Mint the atomic pin from a COMPLETE draft — the only way a pin is born.
    // The draft's rings carry over when ready (no refetch flash); an in-flight
    // fetch downgrades to RingsNone so the pin postlude owns the recompute.
    let private makePin (model : Model) (id : ScanPinId) (d : PinDraft)
                        (anchorMesh : string) (centreLocal : V3d) (pa : V3d) (pb : V3d) =
        let existing = model.ScanPins.Pins |> HashMap.toList |> List.map snd
        let taken =
            let pinNames = existing |> List.map (fun p -> p.ShortName) |> Set.ofList
            let meshNums = model.MeshOrder |> HashMap.toList |> List.map (fun (_, i) -> string (i + 1)) |> Set.ofList
            Set.union pinNames meshNums
        let (ScanPinId.ScanPinId g) = id
        {
            Id           = id
            ShortName    = Primitives.PinIdentity.shortName taken (g.GetHashCode())
            Pair         = d.Pair
            AnchorMesh   = anchorMesh
            CentreLocal  = centreLocal
            InnerRadius  = max 0.01 d.Radius
            PointA       = pa
            PointB       = pb
            CreatedAt    = System.DateTime.UtcNow
            ContactRings = match d.Rings with RingsReady m -> RingsReady m | _ -> RingsNone
            RevealA      = match d.RevealA with RevealReady l -> RevealReady l | _ -> RevealNone
            RevealB      = match d.RevealB with RevealReady l -> RevealReady l | _ -> RevealNone
        }

    let private updatePin (id : ScanPinId) (f : ScanPin -> ScanPin) (sp : ScanPinModel) =
        match HashMap.tryFind id sp.Pins with
        | Some pin -> { sp with Pins = HashMap.add id (f pin) sp.Pins }
        | None -> sp

    // Implicit completion: the pin IS its correspondences — the moment the
    // last of {centre, point A, point B} lands, the draft mints the pin and
    // the placement ends; there is no separate completion act.
    let private landDraft (model : Model) (sp : ScanPinModel) (d : PinDraft) =
        match d.Area, d.PointA, d.PointB with
        | Some (am, c), Some pa, Some pb ->
            let id = ScanPinId.create()
            { sp with
                Pins = HashMap.add id (makePin model id d am c pa pb) sp.Pins
                Placement = PlacementIdle }
        | _ -> { sp with Placement = PlacementActive d }

    let update (model : Model) (msg : ScanPinMessage) (sp : ScanPinModel) =
        match msg with
        | BeginPinTransaction pair ->
            { sp with Placement = PlacementActive (PinDraft.empty pair (max 0.01 model.QuickPinRadius)) }

        | DraftAreaAt(mesh, local) ->
            match sp.Placement with
            | PlacementActive d ->
                landDraft model sp { d with Area = Some(mesh, local); Rings = RingsNone }
            | PlacementIdle -> sp

        | SetDraftRadius r ->
            match sp.Placement with
            | PlacementActive d ->
                { sp with Placement = PlacementActive { d with Radius = max 0.01 r; Rings = RingsNone } }
            | PlacementIdle -> sp

        // Same stale guard as the committed pins': only a still-running draft accepts.
        | DraftRingsComputed rings ->
            match sp.Placement with
            | PlacementActive d when d.Rings = RingsRunning ->
                { sp with Placement = PlacementActive { d with Rings = RingsReady rings } }
            | _ -> sp

        | DraftPointAt(mesh, local) ->
            match sp.Placement with
            | PlacementActive d ->
                // The armed target attributed the mesh; re-picking replaces
                // (and re-derives the point's reveal).
                let d =
                    if mesh = fst d.Pair then { d with PointA = Some local; RevealA = RevealNone }
                    elif mesh = snd d.Pair then { d with PointB = Some local; RevealB = RevealNone }
                    else d
                landDraft model sp d
            | PlacementIdle -> sp

        | SetInnerRadius(id, r) ->
            sp |> updatePin id (fun pin -> { pin with InnerRadius = max 0.01 r; ContactRings = RingsNone })

        | EditPointAt(id, mesh, local) ->
            sp |> updatePin id (fun pin ->
                if mesh = fst pin.Pair then { pin with PointA = local; RevealA = RevealNone }
                elif mesh = snd pin.Pair then { pin with PointB = local; RevealB = RevealNone }
                else pin)

        | EditCentreAt(id, mesh, local) ->
            // Re-anchor: the pin rides whichever mesh the new centre landed on.
            sp |> updatePin id (fun pin ->
                if mesh = fst pin.Pair || mesh = snd pin.Pair then
                    { pin with AnchorMesh = mesh; CentreLocal = local; ContactRings = RingsNone }
                else pin)

        | DeletePin id ->
            { sp with Pins = HashMap.remove id sp.Pins }

        // Stale guard: results only land while still RingsRunning; any intervening invalidation wins.
        | ContactRingsComputed(id, rings) ->
            sp |> updatePin id (fun pin ->
                if pin.ContactRings = RingsRunning then { pin with ContactRings = RingsReady rings } else pin)

        // Same stale guard, per reveal side.
        | PointRevealComputed(id, side, lines) ->
            sp |> updatePin id (fun pin ->
                if side = 0 && pin.RevealA = RevealRunning then { pin with RevealA = RevealReady lines }
                elif side = 1 && pin.RevealB = RevealRunning then { pin with RevealB = RevealReady lines }
                else pin)

        | DraftRevealComputed(side, lines) ->
            match sp.Placement with
            | PlacementActive d when side = 0 && d.RevealA = RevealRunning ->
                { sp with Placement = PlacementActive { d with RevealA = RevealReady lines } }
            | PlacementActive d when side = 1 && d.RevealB = RevealRunning ->
                { sp with Placement = PlacementActive { d with RevealB = RevealReady lines } }
            | _ -> sp

    let handleMsg (env : Env<Message>) (model : Model) (msg : ScanPinMessage) =
        // Deliberately NO camera motion on any pin action — the main camera
        // moves only on explicit zoom actions.
        { model with ScanPins = update model msg model.ScanPins }

    // One debounced sphere∩surface fan-out over a pair's meshes. Transforms
    // are rigid: sphere intersected in each mesh's own frame (inverse-
    // transformed centre), rings mapped back.
    let private fetchRings (env : Env<Message>) (token : System.Threading.CancellationToken)
                           (centre : V3d) (radius : float) (meshes : (string * Trafo3d) list)
                           (mkMsg : Map<string, V3d[][]> -> Message) =
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
                    env.Emit [mkMsg (results |> Array.choose (fun r -> r) |> Map.ofArray)]
            with
            | :? System.OperationCanceledException -> ()
            | _ -> ()
        } |> ignore

    // One debounced correspondence-point reveal (the crosshair's local
    // geometry): point + world-vertical plane normals converted into the
    // mesh's own frame at the CURRENT pose; the mesh-local result rides the
    // pose until the next invalidation.
    let private fetchReveal (env : Env<Message>) (model : Model) (token : System.Threading.CancellationToken)
                            (mesh : string) (local : V3d) (mkMsg : V3d[][] -> Message) =
        let tw = ModelTransforms.displayedWorld model mesh
        let planes = [| tw.Backward.TransformDir V3d.IOO; tw.Backward.TransformDir V3d.OIO |]
        let r = max 0.01 model.RevealRadius
        let radii = [| 0.2 * r; 0.6 * r; r |]
        task {
            try
                do! System.Threading.Tasks.Task.Delay(250, token)
                let! lines =
                    Query.pointReveal ApiConfig.apiBase.Value mesh local radii planes 2048
                    |> Async.StartAsTask
                if not token.IsCancellationRequested then
                    env.Emit [mkMsg lines]
            with
            | :? System.OperationCanceledException -> ()
            | _ -> ()
        } |> ignore

    // Lazy intersection-figure trigger, postlude after every reducer step:
    // every pending area-ring set and point reveal — the in-flight draft
    // included, its placed parts render the full figures from the instant
    // they land — gets one debounced fetch (visibility only gates rendering,
    // so navigating never recomputes). The geometry rides the displayed
    // poses, so pose changes recompute (recomposePoses → invalidateRings).
    let ensureRings (env : Env<Message>) (model : Model) : Model =
        let sp = model.ScanPins
        // Cheap exists-check first: this postlude runs on every message (incl. per-frame
        // Rendered), so avoid allocating the filtered list when nothing is pending.
        let draftPending =
            match sp.Placement with
            | PlacementActive d ->
                (d.Area.IsSome && d.Rings = RingsNone)
                || (d.PointA.IsSome && d.RevealA = RevealNone)
                || (d.PointB.IsSome && d.RevealB = RevealNone)
            | PlacementIdle -> false
        let pinsPending =
            sp.Pins |> HashMap.exists (fun _ p ->
                p.ContactRings = RingsNone || p.RevealA = RevealNone || p.RevealB = RevealNone)
        if model.MeshNames.Count = 0 || (not draftPending && not pinsPending) then model
        else
            let pairMeshes (pair : string * string) =
                [ fst pair; snd pair ]
                |> List.map (fun n -> n, ModelTransforms.displayedWorld model n)
            let mutable pins = sp.Pins
            for (pinId, pin) in HashMap.toList sp.Pins do
                let mutable p = pin
                if p.ContactRings = RingsNone then
                    let centre =
                        ScanPin.centreWorldWith (ModelTransforms.displayedWorld model p.AnchorMesh) p
                    fetchRings env (restartCts ringsCts pinId) centre p.InnerRadius (pairMeshes p.Pair)
                        (fun m -> ScanPinMsg (ContactRingsComputed(pinId, m)))
                    p <- { p with ContactRings = RingsRunning }
                if p.RevealA = RevealNone then
                    fetchReveal env model (restartCts revealCts (pinId, 0)) (fst p.Pair) p.PointA
                        (fun l -> ScanPinMsg (PointRevealComputed(pinId, 0, l)))
                    p <- { p with RevealA = RevealRunning }
                if p.RevealB = RevealNone then
                    fetchReveal env model (restartCts revealCts (pinId, 1)) (snd p.Pair) p.PointB
                        (fun l -> ScanPinMsg (PointRevealComputed(pinId, 1, l)))
                    p <- { p with RevealB = RevealRunning }
                if not (System.Object.ReferenceEquals(p, pin)) then
                    pins <- HashMap.add pinId p pins
            let placement =
                match sp.Placement with
                | PlacementActive d when draftPending ->
                    let mutable d = d
                    (match d.Area with
                     | Some (am, local) when d.Rings = RingsNone ->
                        let centre = (ModelTransforms.displayedWorld model am).Forward.TransformPos local
                        fetchRings env (restartCts draftCts 2) centre d.Radius (pairMeshes d.Pair)
                            (fun m -> ScanPinMsg (DraftRingsComputed m))
                        d <- { d with Rings = RingsRunning }
                     | _ -> ())
                    (match d.PointA with
                     | Some local when d.RevealA = RevealNone ->
                        fetchReveal env model (restartCts draftCts 0) (fst d.Pair) local
                            (fun l -> ScanPinMsg (DraftRevealComputed(0, l)))
                        d <- { d with RevealA = RevealRunning }
                     | _ -> ())
                    (match d.PointB with
                     | Some local when d.RevealB = RevealNone ->
                        fetchReveal env model (restartCts draftCts 1) (snd d.Pair) local
                            (fun l -> ScanPinMsg (DraftRevealComputed(1, l)))
                        d <- { d with RevealB = RevealRunning }
                     | _ -> ())
                    PlacementActive d
                | p -> p
            { model with ScanPins = { sp with Pins = pins; Placement = placement } }
