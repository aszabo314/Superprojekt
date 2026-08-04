namespace Superprojekt

open Aardvark.Base
open Aardvark.Dom.Utilities.OrbitController
open Aardvark.Rendering
open FSharp.Data.Adaptive
open Aardvark.Dom
open Adaptify
open Superprojekt

module View =

    let private rayBoxT (ray : Ray3d) (box : Box3d) : float option =
        let inv = V3d(1.0 / ray.Direction.X, 1.0 / ray.Direction.Y, 1.0 / ray.Direction.Z)
        let tx1 = (box.Min.X - ray.Origin.X) * inv.X
        let tx2 = (box.Max.X - ray.Origin.X) * inv.X
        let ty1 = (box.Min.Y - ray.Origin.Y) * inv.Y
        let ty2 = (box.Max.Y - ray.Origin.Y) * inv.Y
        let tz1 = (box.Min.Z - ray.Origin.Z) * inv.Z
        let tz2 = (box.Max.Z - ray.Origin.Z) * inv.Z
        let tmin = max (max (min tx1 tx2) (min ty1 ty2)) (min tz1 tz2)
        let tmax = min (min (max tx1 tx2) (max ty1 ty2)) (max tz1 tz2)
        if tmax < 0.0 || tmin > tmax then None
        else Some (max tmin 0.0)

    let private pickRay (cursorPx : V2d) (vpSize : V2i) (viewTrafo : Trafo3d) (projTrafo : Trafo3d) =
        let ndc = V2d(2.0 * cursorPx.X / float vpSize.X - 1.0,
                      1.0 - 2.0 * cursorPx.Y / float vpSize.Y)
        let vp = viewTrafo * projTrafo
        let p0 = vp.Backward.TransformPosProj(V3d(ndc, -1.0))
        let p1 = vp.Backward.TransformPosProj(V3d(ndc, 1.0))
        Ray3d(p0, (p1 - p0) |> Vec.normalize)

    let private nowMs () = float System.DateTime.UtcNow.Ticks / 10000.0

    // Cursor ray ∩ the render-space Z=0 plane. Render Z=0 ⟺ world Z = CommonCentroid.Z,
    // so this is a horizontal plane at the dataset's mean elevation — the readout
    // fallback when the ray misses every mesh.
    let private rayPlaneZ0 (ray : Ray3d) : V3d option =
        if abs ray.Direction.Z < 1e-6 then None
        else
            let t = -ray.Origin.Z / ray.Direction.Z
            if t > 0.0 then Some (ray.Origin + t * ray.Direction) else None

    let private worldFromRender (model : AdaptiveModel) (renderPos : V3d) =
        let scale = DatasetScale.active (AVal.force model.ActiveDataset) (AVal.force model.DatasetScales)
        ScanPin.worldCentre (AVal.force model.CommonCentroid) scale renderPos

    let private renderBox (worldBox : Box3d) (cc : V3d) (scale : float) =
        let lo = ScanPin.renderCentre cc scale worldBox.Min
        let hi = ScanPin.renderCentre cc scale worldBox.Max
        Box3d(V3d(min lo.X hi.X, min lo.Y hi.Y, min lo.Z hi.Z),
              V3d(max lo.X hi.X, max lo.Y hi.Y, max lo.Z hi.Z))

    let view (env : Env<Message>) (model : AdaptiveModel) =

        ServerActions.init env

        let spaceHeld       = cval false
        let hoverCoord      = cval<V3d option> None
        let viewportSize    = cval (V2i(1, 1))
        let cursorScreen    = cval<V2d option> None
        // Sample-hover throttle (3D → diagram cross-highlight + exact readout).
        let mutable sampleHoverMs = 0.0
        // Armed-pick preview throttle (GPU-pick hover → SetArmPreview).
        let mutable armPreviewMs = 0.0

        let fullscreenActive = spaceHeld :> aval<bool>

        // Section/cutaway clipping was removed; the mesh shader keeps generic
        // clip-plane support but is fed a constant no-clip.
        let clipUniforms : aval<int * V4f * V4f> = AVal.constant (0, V4f.Zero, V4f.Zero)

        // Shown = clickable: the raycast candidate set mirrors what renders
        // solid (the focus scope + the tile isolate + the matrix hover + the
        // Pin-level focus/arm narrowing), evaluated at event time.
        let shownNow () =
            let focus = AVal.force model.Focus
            let sel = AVal.force model.Sel
            let hp = AVal.force model.MatrixHoverPair
            let isoLock =
                MeshVisibility.withBrushIsolate
                    (MeshView.brushFrameAt model AdaptiveToken.Top |> Option.map snd)
                    (AVal.force model.TileIsolate)
            let isoRaw, pfRaw =
                MeshVisibility.effectiveNarrowing (AVal.force model.PinFocusHover)
                    (AVal.force model.ArmedPick) (AVal.force model.TileIsolateHover)
                    isoLock sel.Point
            // Mirror the vis peek's isolate (+ point-narrowing) swap — shown =
            // clickable holds during the blink too.
            let iso, pf =
                match isoRaw, sel.Pair with
                | Some m, Some (a, b) when AVal.force model.PeekVis && (m = a || m = b) ->
                    let other = if m = a then b else a
                    Some other, (pfRaw |> Option.map (fun x -> if x = m then other else x))
                | _ -> isoRaw, pfRaw
            let gs = MeshView.graphMapScopeAt model AdaptiveToken.Top
            fun (name : string) -> MeshVisibility.shown focus sel.Pair iso hp gs pf name

        body {
            OnBoot [
                "const l = document.getElementById('loader');"
                "if(l) l.remove();"
                "document.body.classList.add('loaded');"
                // Data-URL download helper (the session-log export); returns a
                // value so JSRuntime.Invoke<bool> has something to unmarshal.
                "window.spDownloadText = function(name, text){"
                "  var a = document.createElement('a');"
                "  a.href = 'data:application/json;charset=utf-8,' + encodeURIComponent(text);"
                "  a.download = name; document.body.appendChild(a); a.click(); a.remove();"
                "  return true;"
                "};"
                // Data-state checkpoints (the ⚙ debug menu) — namespaced
                // localStorage; list returns \n-joined names (JSRuntime
                // marshals plain strings only).
                "window.spCkSave = function(n, j){ try{ localStorage.setItem('spCk:'+n, j); return true; }catch(e){ return false; } };"
                "window.spCkLoad = function(n){ return localStorage.getItem('spCk:'+n)||''; };"
                "window.spCkDel = function(n){ localStorage.removeItem('spCk:'+n); return true; };"
                "window.spCkList = function(){ var r=[]; for(var i=0;i<localStorage.length;i++){ var k=localStorage.key(i);"
                "  if(k&&k.indexOf('spCk:')===0) r.push(k.substring(5)); } r.sort(); return r.join('\\n'); };"
            ]

            renderControl {
                RenderControl.Samples 1
                Class "render-control"

                let! info = RenderControl.Info
                let! size = RenderControl.ViewportSize
                let! client = RenderControl.ClientSize

                // CSS-pixel size for anything mixing with DOM coords (cursor,
                // overlay placement). ViewportSize is framebuffer px (CSS ×
                // devicePixelRatio) → pushes cards off-screen on hi-dpi; use
                // ClientSize, falling back to framebuffer until the first DOM
                // event (ClientSize is V2i.II until then).
                let overlaySize =
                    (client, size) ||> AVal.map2 (fun c v ->
                        if c.X > 1 && c.Y > 1 then c else v)

                OrbitController.getAttributes (Env.map CameraMessage env)

                RenderControl.OnRendered(fun _ ->
                    let s = AVal.force overlaySize
                    if viewportSize.Value <> s then
                        transact (fun () -> viewportSize.Value <- s)
                    env.Emit [CameraMessage OrbitMessage.Rendered]
                )

                let view = model.Camera.view |> AVal.map CameraView.viewTrafo
                // Near 1 cm / far 1000 m METRIC (× DatasetScale into render
                // units) — close-up inspection must not near-clip. Must match
                // the overlay tooltip projection below.
                let projScale =
                    (model.ActiveDataset, model.DatasetScales) ||> AVal.map2 DatasetScale.active
                let proj =
                    (size, projScale) ||> AVal.map2 (fun s sc ->
                        Frustum.perspective 90.0 (0.01 * sc) (1000.0 * sc) (float s.X / float s.Y) |> Frustum.projTrafo)

                // Cursor → nearest mesh surface via server raycast, ghost-agnostic
                // (the server just intersects geometry, ignoring the GPU ghost).
                // Bbox-culls shown+loaded meshes, raycasts the survivors in
                // parallel, returns the mesh name + render-space hit nearest the
                // camera + the hit triangle.
                let raycastNearestNamed () : Async<(string * V3d * int) option> =
                    match cursorScreen.Value with
                    | None -> async.Return None
                    | Some cursorPx ->
                        let cc = AVal.force model.CommonCentroid
                        let scales = AVal.force model.DatasetScales
                        let shown = shownNow ()
                        let bounds = AVal.force model.MeshBounds
                        let ray = pickRay cursorPx (AVal.force overlaySize) (AVal.force view) (AVal.force proj)
                        let candidates =
                            bounds |> Map.toSeq
                            |> Seq.choose (fun (name, world) ->
                                if shown name then
                                    let scale = DatasetScale.forMesh scales name
                                    if (rayBoxT ray (renderBox world cc scale)).IsSome
                                    then Some (name, scale) else None
                                else None)
                            |> Array.ofSeq
                        if candidates.Length = 0 then async.Return None
                        else
                            async {
                                let! hits =
                                    candidates
                                    |> Array.map (fun (name, scale) ->
                                        async {
                                            let dispWorld = RigidTransform.renderToWorld scale cc (AVal.force (MeshView.displayedMeshT model name))
                                            let serverOrigin = dispWorld.Backward.TransformPos (ScanPin.worldCentre cc scale ray.Origin)
                                            let serverDir = (dispWorld.Backward.TransformDir ray.Direction).Normalized
                                            let! hit = Query.rayHit ApiConfig.apiBase.Value name 0 serverOrigin serverDir
                                            return hit |> Option.map (fun h ->
                                                let rp = ScanPin.renderCentre cc scale (dispWorld.Forward.TransformPos h.point)
                                                Vec.dot (rp - ray.Origin) ray.Direction, name, rp, h.triangleId)
                                        })
                                    |> Async.Parallel
                                return
                                    hits |> Array.choose id |> Array.sortBy (fun (d, _, _, _) -> d)
                                    |> Array.tryHead |> Option.map (fun (_, n, rp, tri) -> n, rp, tri)
                            }

                let raycastNearest () : Async<V3d option> =
                    async {
                        let! hit = raycastNearestNamed ()
                        return hit |> Option.map (fun (_, rp, _) -> rp)
                    }

                // Solid GPU pixel pick first, then fall through a ghost to the
                // nearest raycast surface. Used by pin placement and by
                // double-tap-to-recenter, so both work on ghosted meshes too.
                let resolvePick (frontmost : V3d option) : Async<V3d option> =
                    match frontmost with
                    | Some _ -> async.Return frontmost
                    | None -> raycastNearest ()

                Sg.View view
                Sg.Proj proj

                Sg.Pass RenderPass.passZero

                Dom.OnPointerMove(fun e ->
                    let cursorPx = V2d(float e.OffsetPosition.X, float e.OffsetPosition.Y)
                    if cursorScreen.Value <> Some cursorPx then
                        transact (fun () -> cursorScreen.Value <- Some cursorPx)
                )

                // Clear hoverCoord on leave, else the top-bar readout keeps a
                // stale last-on-canvas coordinate over the HTML overlays; the
                // armed preview follows the same rule.
                Dom.OnMouseLeave(fun _ ->
                    if hoverCoord.Value.IsSome then
                        transact (fun () -> hoverCoord.Value <- None)
                    if (AVal.force model.ArmPreview).IsSome then
                        env.Emit [SetArmPreview None]
                )

                Dom.OnMouseWheel(fun e ->
                    let delta = V2d(e.DeltaX, e.DeltaY) / 120.0
                    env.Emit [CameraMessage (OrbitMessage.Wheel delta)]
                )

                Sg.OnDoubleTap(fun e ->
                    let frontmost =
                        if e.Location.Depth < 0.9999 then Some e.WorldPosition else None
                    async {
                        // Solid surface first, then fall through a ghost via raycast,
                        // so double-tap-to-recenter works on ghosted meshes too.
                        let! resolved = resolvePick frontmost
                        match resolved with
                        | Some renderPos ->
                            env.Emit [CameraMessage (OrbitMessage.SetTargetCenter(AnimationKind.Tanh, renderPos))]
                        | None -> ()
                    } |> Async.Start
                    false
                )

                // Exact pairwise error at a metric-world point (the P0
                // exact-point endpoint), oriented MOV-relative-to-REF like every
                // stored sample; k runs on the landed value. The pair comes from
                // the hovered sample's own block, so a graph-scope dot measures
                // against ITS parent.
                let exactPairValueAt (refM : string) (mov : string) (world : V3d) (radius : float) (k : float -> unit) =
                    let ka, kb = PairCell.key refM mov
                    let flip = mov = ka
                    let tOf (m : string) =
                        let cc = AVal.force model.CommonCentroid
                        let scale = DatasetScale.forMesh (AVal.force model.DatasetScales) m
                        (RigidTransform.renderToWorld scale cc (AVal.force (MeshView.displayedMeshT model m))).Forward
                    async {
                        let! v = Query.pairErrorAt ApiConfig.apiBase.Value ka (tOf ka) kb (tOf kb) world radius
                        match v with
                        | Some v -> k (if flip then -v else v)
                        | None -> ()
                    } |> Async.Start

                Sg.OnTap(fun _ ->
                    // No pick without an arm (A5): the armed pick captures the
                    // click — centre/point/probe all route through it; an
                    // unarmed tap does nothing here.
                    if (AVal.force model.ArmedPick).IsSome then
                        (match cursorScreen.Value with
                         | Some cur ->
                            let ray = pickRay cur (AVal.force overlaySize) (AVal.force view) (AVal.force proj)
                            GuiPanes.armedPick env model ray
                         | None -> ())
                    true
                )

                Sg.OnPointerMove(fun e ->
                    let pick =
                        if e.Location.Depth < 0.9999 then Some e.WorldPosition else None
                    // Readout fallback: when the cursor ray misses every mesh, drop it
                    // onto the render Z=0 plane (dataset mean elevation) so the top-bar
                    // coordinate keeps reading over open ground / off-mesh.
                    let nextHover =
                        match pick with
                        | Some renderPos -> Some (worldFromRender model renderPos)
                        | None ->
                            match cursorScreen.Value with
                            | Some cursorPx ->
                                pickRay cursorPx (AVal.force overlaySize) (AVal.force view) (AVal.force proj)
                                |> rayPlaneZ0
                                |> Option.map (worldFromRender model)
                            | None -> None
                    if hoverCoord.Value <> nextHover then
                        transact (fun () -> hoverCoord.Value <- nextHover)
                    // Armed-pick cursor preview: the GPU pick IS the armed
                    // surface (the visibility rule isolates the armed
                    // candidates; ghosts fall through) — zero server traffic.
                    if (AVal.force model.ArmedPick).IsSome then
                        let now = nowMs ()
                        if now - armPreviewMs > 40.0 then
                            armPreviewMs <- now
                            let next = pick |> Option.map (worldFromRender model)
                            if AVal.force model.ArmPreview <> next then
                                env.Emit [SetArmPreview next]
                    // 3D → diagram: hover the nearest brushed sample (screen
                    // space, ≤ 12 px) → cross-highlight + exact-value readout.
                    // Throttled; the exact value fetches only on a gid change.
                    let brush = AVal.force model.BrushedSamples
                    if not (Set.isEmpty brush) then
                        let now = nowMs ()
                        if now - sampleHoverMs > 80.0 then
                            sampleHoverMs <- now
                            match cursorScreen.Value, MeshView.inspectBlocksAt model AdaptiveToken.Top with
                            | Some cur, blocks when blocks.Length > 0 ->
                                let cc = AVal.force model.CommonCentroid
                                let scale = DatasetScale.active (AVal.force model.ActiveDataset) (AVal.force model.DatasetScales)
                                let vp = (AVal.force view) * (AVal.force proj)
                                let sizePx = AVal.force overlaySize
                                let toScreen (w : V3d) =
                                    let ndc = vp.Forward.TransformPosProj (ScanPin.renderCentre cc scale w)
                                    V2d(0.5 * (ndc.X + 1.0) * float sizePx.X, 0.5 * (1.0 - ndc.Y) * float sizePx.Y)
                                let mutable best = -1
                                let mutable bestD = 12.0
                                let mutable bestHit = None
                                let mutable gid = 0
                                for b in blocks do
                                    let r = b.Err
                                    for i in 0 .. r.Samples.Length - 1 do
                                        if Set.contains gid brush && i < r.Positions.Length then
                                            let d = Vec.distance (toScreen r.Positions.[i]) cur
                                            if d < bestD then
                                                bestD <- d
                                                best <- gid
                                                bestHit <- Some (b, r.Positions.[i])
                                        gid <- gid + 1
                                let cur = AVal.force model.HoverSample
                                if best >= 0 then
                                    if cur <> Some best then
                                        env.Emit [SetHoverSample (Some best)]
                                        match bestHit with
                                        | Some (b, pos) ->
                                            let radius =
                                                HashMap.tryFind b.Pin (AVal.force (model.ScanPins.Pins |> AMap.toAVal))
                                                |> Option.map (fun p -> p.InnerRadius)
                                                |> Option.defaultValue (AVal.force model.QuickPinRadius)
                                            let gen = UpdateHelpers.cellErrorGen
                                            exactPairValueAt b.Ref b.Mov pos radius (fun v ->
                                                env.Emit [HoverReadoutComputed(gen, best, v)])
                                        | None -> ()
                                elif cur.IsSome then
                                    env.Emit [SetHoverSample None]
                            | _ -> ()
                    true
                )

                SceneGraph.build env info view proj fullscreenActive clipUniforms model
            }

            GuiPanes.tileStrip env model

            // Pick-value tooltips riding their 3D points (the armed probe's
            // readout + the hovered brushed sample), projected with the same
            // camera the main view renders with.
            let viewTOut = model.Camera.view |> AVal.map CameraView.viewTrafo
            // Same frustum as the main render control (near 1 cm / far 1000 m
            // metric) or the tooltips drift off their 3D points.
            let projTOut =
                let projScale =
                    (model.ActiveDataset, model.DatasetScales) ||> AVal.map2 DatasetScale.active
                (viewportSize, projScale) ||> AVal.map2 (fun s sc ->
                    Frustum.perspective 90.0 (0.01 * sc) (1000.0 * sc) (float s.X / float (max 1 s.Y)) |> Frustum.projTrafo)
            let screenOf (t : FSharp.Data.Adaptive.AdaptiveToken) (world : V3d) =
                let cc = model.CommonCentroid.GetValue t
                let scale = DatasetScale.active (model.ActiveDataset.GetValue t) (model.DatasetScales.GetValue t)
                let rp = ScanPin.renderCentre cc scale world
                let vT = viewTOut.GetValue t
                // View space looks down −Z; a non-negative Z is behind the eye.
                if (vT.Forward.TransformPos rp).Z >= 0.0 then None
                else
                    let ndc = (vT * projTOut.GetValue t).Forward.TransformPosProj rp
                    let s = (viewportSize :> aval<V2i>).GetValue t
                    Some (V2d(0.5 * (ndc.X + 1.0) * float s.X, 0.5 * (1.0 - ndc.Y) * float s.Y))
            let probeTip =
                AVal.custom (fun t ->
                    // The readout survives the landing's auto-disarm; the next
                    // arm / jump / invalidation clears it.
                    match model.ProbeReadout.GetValue t with
                    | Some (w, v) ->
                        screenOf t w |> Option.map (fun p -> p, sprintf "%+.1f mm" (v * 1000.0))
                    | None -> None)
            let hoverTip =
                AVal.custom (fun t ->
                    match model.HoverReadout.GetValue t with
                    | Some (gid, v) when model.HoverSample.GetValue t = Some gid ->
                        // gid indexes the canonical inspect sample stream.
                        let rec find (i : int) (bs : InspectBlock list) =
                            match bs with
                            | [] -> None
                            | b :: rest ->
                                if i < b.Err.Samples.Length then
                                    (if i < b.Err.Positions.Length then Some b.Err.Positions.[i] else None)
                                else find (i - b.Err.Samples.Length) rest
                        match find gid (Array.toList (MeshView.inspectBlocksAt model t)) with
                        | Some w -> screenOf t w |> Option.map (fun p -> p, sprintf "%+.1f mm" (v * 1000.0))
                        | None -> None
                    | _ -> None)
            let pickTip (extraClass : string) (dataA : aval<(V2d * string) option>) =
                div {
                    Class ("pick-tip " + extraClass)
                    Primitives.showWhen (dataA |> AVal.map Option.isSome)
                    dataA |> AVal.map (function
                        | Some (p, _) -> Some (Style [Left (sprintf "%.0fpx" p.X); Top (sprintf "%.0fpx" p.Y)])
                        | None -> None)
                    dataA |> AVal.map (function Some (_, s) -> s | None -> "")
                }
            pickTip "pick-tip-probe" probeTip
            pickTip "pick-tip-hover" hoverTip

            Dom.OnKeyDown(fun e ->
                match e.Key with
                | " " ->
                    transact (fun () -> spaceHeld.Value <- true)
                // The two spring-loaded blink keys — V in the pair workspace
                // alone, B there and over the whole graph at Matrix; the
                // reducer refuses a press whose subject isn't resident and
                // registered. Key repeat is absorbed by its idempotence guard.
                | "v" | "V" -> env.Emit [SetPeekVis true]
                | "b" | "B" -> env.Emit [SetPeekPose true]
                | "Escape" ->
                    // ONE Esc: the innermost in-progress action cancels first —
                    // the pin exit-guard popup (cancel = stay) > the blocking
                    // loop modal (cancel = discard the redundant edge) > a
                    // CENTRELESS placement aborts straight to Pair (nothing
                    // worth guarding yet — skips the disarm step on purpose) >
                    // armed-pick disarm (probe included — every pick is an
                    // arm) > ascend one focus level. Ascending out of Pin with
                    // a centred draft raises the exit-guard (the reducer's
                    // gate); rail jumps go through the same gate, so Esc and
                    // jumps stay consistent.
                    if (AVal.force model.PinExitPending).IsSome then
                        env.Emit [CancelPinExit]
                    elif (AVal.force model.LoopPending).IsSome then
                        env.Emit [CancelLoopResolution]
                    else
                        let centrelessDraft =
                            AVal.force model.Focus = FocusPin &&
                            (match AVal.force model.ScanPins.Placement with
                             | PlacementActive d -> d.Area.IsNone
                             | PlacementIdle -> false)
                        if centrelessDraft then env.Emit [FocusAscend]
                        else
                            match AVal.force model.ArmedPick with
                            | Some target -> env.Emit [ToggleArmPick target]
                            | None -> env.Emit [FocusAscend]
                | _ -> ()
            )
            Dom.OnKeyUp(fun e ->
                match e.Key with
                | " "     -> transact (fun () -> spaceHeld.Value <- false)
                | "v" | "V" -> env.Emit [SetPeekVis false]
                | "b" | "B" -> env.Emit [SetPeekPose false]
                | _ -> ()
            )

            // Armed-pick quasi-mode hook: an empty element the CSS scrim
            // rules key off (body:has(.arm-flag.on) — the body's own class is
            // boot-managed, so the flag rides a mounted child instead).
            div {
                Class "arm-flag"
                Primitives.classWhen "on" (model.ArmedPick |> AVal.map Option.isSome)
            }

            GuiTopBar.topBar env model (hoverCoord :> aval<V3d option>)
            // Left column: the navigator rail with the docked inspection
            // toolbox directly below it (one fixed flex column, so the dock
            // rides the rail's height).
            div {
                Class "left-col"
                // The home level seats TWO navigators side by side — the
                // column widens there and returns to the narrow rail inside
                // the pair workspace.
                Primitives.classWhen "left-col-home" (model.Focus |> AVal.map ((=) FocusMatrix))
                GuiRail.rail env model
                GuiRail.inspectPanel env model
                // ONE right-edge width-resize handle for the whole column
                // (rail + inspection dock together — the left-side mirror of
                // the tile strip's handle). Pure DOM; writes --leftw or
                // --lefthomew on the column (home keeps its own width) and
                // re-derives the chart's aspect-true --charth on the dock.
                div {
                    Class "left-handle"
                    Attribute("title", "Drag to resize the left panels")
                    OnBoot [
                        "(function(){"
                        "var h=__THIS__; var col=h.parentElement;"
                        "function vname(){ return col.classList.contains('left-col-home') ? '--lefthomew' : '--leftw'; }"
                        "function apply(w){"
                        "  var home=col.classList.contains('left-col-home');"
                        "  var min=home?380:220;"
                        "  var max=Math.max(min,home?window.innerWidth-500:window.innerWidth*0.5);"
                        "  w=Math.max(min,Math.min(max,w));"
                        "  col.style.setProperty(vname(),w+'px');"
                        "  var c=col.querySelector('.cw-chart');"
                        "  if(c){ var d=c.closest('.inspect-dock');"
                        "    var r=c.getBoundingClientRect(), dr=d.getBoundingClientRect();"
                        "    var reserve=Math.max(0,dr.bottom-r.bottom);"
                        "    var hMax=window.innerHeight-r.top-reserve-10;"
                        "    var hT=(w-20)*160/236;"
                        "    d.style.setProperty('--charth',Math.max(160,Math.min(hMax,hT))+'px'); } }"
                        "h.addEventListener('pointerdown',function(e){"
                        "  e.preventDefault(); h.setPointerCapture(e.pointerId);"
                        "  function mv(ev){ apply(ev.clientX); }"
                        "  function up(){ h.removeEventListener('pointermove',mv); h.removeEventListener('pointerup',up); }"
                        "  h.addEventListener('pointermove',mv); h.addEventListener('pointerup',up); });"
                        "window.addEventListener('resize',function(){"
                        "  var w=parseFloat(col.style.getPropertyValue(vname())); if(w) apply(w); });"
                        "})();"
                    ]
                }
            }
            GuiOverlays.toast model
            GuiOverlays.spannedBanner env model
            GuiOverlays.scaleBar model (viewportSize :> aval<V2i>)
            GuiOverlays.colorLegend model
            GuiOverlays.orientationIndicator model
            GuiOverlays.loopModal env model
            GuiOverlays.pinExitModal env model
        }

module App =
    let app =
        {
            initial   = Model.initial
            update    = Update.update
            view      = View.view
            unpersist = Unpersist.instance
        }
