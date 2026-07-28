namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open Aardworx.WebAssembly
open FSharp.Data.Adaptive
open Aardvark.Dom
open Superprojekt.LineGlyphs

// The Pin level's picking surface: two side-by-side panes — mesh A | mesh B of
// the selected pair — each an independent orbit view of ITS mesh alone. The
// panes are the navigator AND the only pick surface for correspondence work:
// a click in pane A always attributes to mesh A (nearest-hit attribution in
// the shared 3D view can never reach the occluded mesh of a co-located pair).
// Picks resolve through server raycasts — Sg pointer events do not fire
// reliably in secondary render controls.
module GuiPanes =

    open Primitives

    let private pickRay (cursorPx : V2d) (vpSize : V2i) (viewTrafo : Trafo3d) (projTrafo : Trafo3d) =
        let ndc = V2d(2.0 * cursorPx.X / float vpSize.X - 1.0,
                      1.0 - 2.0 * cursorPx.Y / float vpSize.Y)
        let vp = viewTrafo * projTrafo
        let p0 = vp.Backward.TransformPosProj(V3d(ndc, -1.0))
        let p1 = vp.Backward.TransformPosProj(V3d(ndc, 1.0))
        Ray3d(p0, (p1 - p0) |> Vec.normalize)

    let private paneControl (env : Env<Message>) (model : AdaptiveModel)
                            (side : PaneSide) (name : string) (other : string) =
        let cam = match side with PaneA -> model.PaneCamA | PaneB -> model.PaneCamB
        let paneEnv = Env.map (fun m -> PaneCamMessage(side, m)) env
        let paneOn = model.Focus |> AVal.map ((=) FocusPin)
        let idxVal = model.MeshOrder |> AMap.tryFind name |> AVal.map (Option.defaultValue 0)
        let datasetScale =
            (model.ActiveDataset, model.DatasetScales) ||> AVal.map2 DatasetScale.active
        let pinsVal = model.ScanPins.Pins |> AMap.toAVal
        // Own-frame local → this pane's render position, riding the mesh's
        // displayed pose.
        let renderOf (t : AdaptiveToken) (local : V3d) =
            let cc = model.CommonCentroid.GetValue t
            let s = datasetScale.GetValue t
            ScanPin.renderCentre cc s ((MeshView.displayedWorldAt model t name).Forward.TransformPos local)
        // Click-vs-drag discrimination: a pointer-up within a few pixels of its
        // down is a pick, anything longer was an orbit drag.
        let mutable downPos : V2d option = None

        div {
            Class "pin-pane"
            div {
                Class "pane-chip"
                Attribute("title", name)
                span { Class "pmx-sw"; idxVal |> AVal.map (fun i -> Some (Style [Css.Background (c4bToRgbCss (meshColor i))])) }
                span { Class "pmx-num"; idxVal |> AVal.map (fun i -> string (i + 1)) }
                span {
                    Class "pane-chip-name"
                    model.MeshNames.Content |> AVal.map (fun ns -> friendlyName (IndexList.toList ns) name)
                }
            }
            renderControl {
                RenderControl.Samples 1
                Class "pane-rc"

                let! info = RenderControl.Info
                let! size = RenderControl.ViewportSize
                let! client = RenderControl.ClientSize
                // CSS-pixel size for cursor→ray math (ViewportSize is
                // framebuffer px; ClientSize is V2i.II until the first DOM event).
                let overlaySize =
                    (client, size) ||> AVal.map2 (fun c v ->
                        if c.X > 1 && c.Y > 1 then c else v)

                OrbitController.getAttributes paneEnv
                RenderControl.OnRendered(fun _ -> env.Emit [PaneCamMessage(side, OrbitMessage.Rendered)])
                Dom.OnMouseWheel(fun e ->
                    env.Emit [PaneCamMessage(side, OrbitMessage.Wheel (V2d(e.DeltaX, e.DeltaY) / 120.0))])

                let view = cam.view |> AVal.map CameraView.viewTrafo
                let proj =
                    size |> AVal.map (fun s ->
                        Frustum.perspective 90.0 1.0 5000.0 (float s.X / float s.Y) |> Frustum.projTrafo)

                // The pane pick: raycast THIS mesh alone (the pane IS the
                // attribution); the hit lands directly in the mesh's own frame.
                let pick (cursorPx : V2d) =
                    async {
                        let cc = AVal.force model.CommonCentroid
                        let scale = DatasetScale.forMesh (AVal.force model.DatasetScales) name
                        let ray = pickRay cursorPx (AVal.force overlaySize) (AVal.force view) (AVal.force proj)
                        let dispWorld =
                            RigidTransform.renderToWorld scale cc (AVal.force (MeshView.displayedMeshT model name))
                        let serverOrigin = dispWorld.Backward.TransformPos (ScanPin.worldCentre cc scale ray.Origin)
                        let serverDir = (dispWorld.Backward.TransformDir ray.Direction).Normalized
                        let! hit = Query.rayHit ApiConfig.apiBase.Value name 0 serverOrigin serverDir
                        match hit with
                        | Some h ->
                            let msgs =
                                match AVal.force model.ScanPins.Placement with
                                | PlacementActive(ToolArea, _) -> [ScanPinMsg (DraftAreaAt(name, h.point))]
                                | PlacementActive(ToolPoint, _) -> [ScanPinMsg (DraftPointAt(name, h.point))]
                                | PlacementIdle ->
                                    // Edit mode: the pane click re-picks the selected
                                    // committed pin's point on this mesh (atomic replace).
                                    match (AVal.force model.Sel).Pin with
                                    | Some id -> [ScanPinMsg (EditPointAt(id, name, h.point))]
                                    | None -> []
                            if not (List.isEmpty msgs) then env.Emit msgs
                        | None -> ()
                    } |> Async.Start

                Dom.OnPointerDown(fun e ->
                    downPos <- Some (V2d(float e.OffsetPosition.X, float e.OffsetPosition.Y)))
                Dom.OnPointerUp(fun e ->
                    let up = V2d(float e.OffsetPosition.X, float e.OffsetPosition.Y)
                    (match downPos with
                     | Some dp when Vec.distance dp up < 4.0 -> pick up
                     | _ -> ())
                    downPos <- None)

                Sg.View view
                Sg.Proj proj
                Sg.Pass RenderPass.passZero
                Sg.Uniform("ViewportSize", size)

                // Pane-camera coverage MRT: feeds the armed-placement
                // isolate-overlap gate inside the pane mesh shader.
                let cov0, cov1, _ = OutlineView.coverageOffscreen info model view proj
                MeshView.buildPaneScene model name paneOn (Some (other, cov0, cov1)) size

                // Committed point fills on this mesh: mesh-colour icospheres
                // (the white outlines ride in the line node below); the
                // selected pin's marker is larger.
                let meshColV4 =
                    idxVal |> AVal.map (fun i ->
                        let c = meshColor i
                        V4d(float c.R / 255.0, float c.G / 255.0, float c.B / 255.0, 1.0))
                let pinIdSet = model.ScanPins.Pins |> AMap.toASet |> ASet.map fst
                let pointFills =
                    pinIdSet |> ASet.map (fun id ->
                        let st =
                            AVal.custom (fun t ->
                                match HashMap.tryFind id (pinsVal.GetValue t) with
                                | Some p when (model.Sel.GetValue t).Pair = Some p.Pair ->
                                    let local = if name = fst p.Pair then p.PointA else p.PointB
                                    let sz = if (model.Sel.GetValue t).Pin = Some id then 0.05 else 0.038
                                    Some (renderOf t local, sz)
                                | _ -> None)
                        let trafo =
                            st |> AVal.map (function
                                | Some (c, sz) -> Trafo3d.Scale sz * Trafo3d.Translation c
                                | None -> Trafo3d.Scale 0.0)
                        sg {
                            Sg.Pass RenderPass.passOne
                            ScanPinScene.sphereShell view proj paneOn trafo meshColV4
                        })
                pointFills

                // Every line mark of the pane: white outlines of the committed
                // points, the selected pin's area sphere, and the ALL-WHITE
                // in-flight draft (uncommitted layer).
                let segs =
                    AVal.custom (fun t ->
                        let sel = model.Sel.GetValue t
                        match sel.Pair with
                        | None -> [||]
                        | Some pair ->
                            let pins = pinsVal.GetValue t
                            let out = ResizeArray<V3d * V3d * V4d * float>()
                            for (id, p) in HashMap.toSeq pins do
                                if p.Pair = pair then
                                    let local = if name = fst pair then p.PointA else p.PointB
                                    let c = renderOf t local
                                    let sz = if sel.Pin = Some id then 0.065 else 0.05
                                    addWireSphere out c sz (V4d(1.0, 1.0, 1.0, 0.95)) 1.6 16
                            (match sel.Pin |> Option.bind (fun id -> HashMap.tryFind id pins) with
                             | Some p when p.AnchorMesh = name ->
                                let cR = renderOf t p.CentreLocal
                                let rR = ScanPin.renderLength (datasetScale.GetValue t) p.InnerRadius
                                for seg in PinGeometry.buildSphereOutline cR rR (V4d(1.0, 1.0, 1.0, 0.85)) 1.5 do
                                    out.Add seg
                             | _ -> ())
                            (match model.ScanPins.Placement.GetValue t with
                             | PlacementActive(_, d) ->
                                let white = V4d(1.0, 1.0, 1.0, 0.95)
                                (match d.Area with
                                 | Some (m, local) when m = name ->
                                    let cR = renderOf t local
                                    let rR = ScanPin.renderLength (datasetScale.GetValue t) (model.QuickPinRadius.GetValue t)
                                    for seg in PinGeometry.buildSphereOutline cR rR (V4d(1.0, 1.0, 1.0, 0.8)) 1.5 do
                                        out.Add seg
                                 | _ -> ())
                                (match (if name = fst d.Pair then d.PointA else d.PointB) with
                                 | Some local ->
                                    let cR = renderOf t local
                                    addWireSphere out cR 0.06 white 1.8 20
                                    addCross out cR 0.075 white 1.8
                                 | None -> ())
                             | PlacementIdle -> ())
                            out.ToArray())
                sg {
                    Sg.Active paneOn
                    Sg.Pass RenderPass.passOne
                    Sg.DepthTest (AVal.constant DepthTest.None)
                    Sg.BlendMode (AVal.constant BlendMode.Blend)
                    Sg.NoEvents
                    Lines.render segs
                }
            }
        }

    // One Setup survey tile: an input-less top-down thumbnail of the mesh
    // (displayed pose, live survey heatmap) + identity chip + the explicit
    // ☆ Set-reference button — the tile strip IS the survey/root-selection
    // browser. Double-click flies the main camera to the mesh's sensor.
    let private surveyTile (env : Env<Message>) (model : AdaptiveModel) (name : string) =
        let onSetup = model.Focus |> AVal.map ((=) FocusSetup)
        let idxVal = model.MeshOrder |> AMap.tryFind name |> AVal.map (Option.defaultValue 0)
        let isRoot = model.RegGraph |> AVal.map (fun g -> g.Root = Some name)
        div {
            Class "setup-tile"
            Attribute("title", sprintf "%s — double-click: fly the main view to the sensor viewpoint" name)
            Dom.OnDoubleClick(fun _ -> env.Emit [FlyToSensor name])
            div {
                Class "pane-chip"
                span { Class "pmx-sw"; idxVal |> AVal.map (fun i -> Some (Style [Css.Background (c4bToRgbCss (meshColor i))])) }
                span { Class "pmx-num"; idxVal |> AVal.map (fun i -> string (i + 1)) }
                span {
                    Class "pane-chip-name"
                    model.MeshNames.Content |> AVal.map (fun ns -> friendlyName (IndexList.toList ns) name)
                }
                span { Class "pmx-root-star"; isRoot |> AVal.map (fun r -> if r then "★" else "") }
            }
            button {
                Class "rail-btn setup-ref tile-ref"
                classWhen "setup-ref-on" isRoot
                Attribute("title", "Designate as the reference root. Re-rooting inside the registered tree keeps the registration; a mesh outside it clears the graph.")
                Dom.OnClick(fun _ -> env.Emit [SetRegRoot name])
                isRoot |> AVal.map (fun r -> if r then "★ Reference" else "☆ Set reference")
            }
            renderControl {
                RenderControl.Samples 1
                Class "tile-rc"
                let! size = RenderControl.ViewportSize
                let! client = RenderControl.ClientSize
                let clientSize =
                    (client, size) ||> AVal.map2 (fun c v ->
                        if c.X > 1 && c.Y > 1 then c else v)
                // The 2D top-down tile camera: the stored pan/zoom, else the
                // bounds framing (sky = +Y — a look-down view cannot use +Z).
                let camA =
                    AVal.custom (fun t ->
                        match Map.tryFind name (model.TileCams.GetValue t) with
                        | Some c -> c
                        | None ->
                            let cc = model.CommonCentroid.GetValue t
                            let scale = DatasetScale.forMesh (model.DatasetScales.GetValue t) name
                            match Map.tryFind name (model.MeshBounds.GetValue t) with
                            | Some b when not b.IsInvalid ->
                                { Centre = ScanPin.renderCentre cc scale b.Center
                                  Radius = max 1.0 (b.Size.Length * scale * 0.55) }
                            | _ -> { Centre = V3d.Zero; Radius = 5.0 })
                // 60° horizontal fov ⇒ render units per CSS pixel at the centre plane.
                let unitsPerPx (cam : TileCam) (w : int) =
                    2.0 * cam.Radius * tan (30.0 * System.Math.PI / 180.0) / float (max 1 w)
                let mutable drag : (V2d * TileCam) option = None
                Dom.OnPointerDown((fun e ->
                    drag <- Some (V2d(float e.OffsetPosition.X, float e.OffsetPosition.Y), AVal.force camA)),
                    pointerCapture = true)
                Dom.OnPointerUp((fun _ -> drag <- None), pointerCapture = true)
                Dom.OnPointerMove(fun e ->
                    match drag with
                    | Some (p0, cam0) ->
                        // Anchored at the drag start (no incremental drift):
                        // screen right = +X, screen down = −Y in this view.
                        let p = V2d(float e.OffsetPosition.X, float e.OffsetPosition.Y)
                        let d = p - p0
                        let u = unitsPerPx cam0 (AVal.force clientSize).X
                        env.Emit [SetTileCam(name, { cam0 with Centre = cam0.Centre - V3d(d.X * u, -d.Y * u, 0.0) })]
                    | None -> ())
                Dom.OnMouseWheel(fun e ->
                    let cam = AVal.force camA
                    let s = AVal.force clientSize
                    let u = unitsPerPx cam s.X
                    let k = 1.1 ** (e.DeltaY / 120.0)
                    // Zoom anchored at the cursor: the point under it stays put
                    // (its offset from the centre scales with the radius).
                    let off = V3d((float e.OffsetPosition.X - float s.X * 0.5) * u,
                                  -(float e.OffsetPosition.Y - float s.Y * 0.5) * u, 0.0)
                    env.Emit [SetTileCam(name, { Centre = cam.Centre + off - off * k; Radius = cam.Radius * k })])
                let view =
                    camA |> AVal.map (fun c ->
                        CameraView.lookAt (c.Centre + V3d.OOI * c.Radius) c.Centre V3d.OIO |> CameraView.viewTrafo)
                let proj =
                    size |> AVal.map (fun s ->
                        Frustum.perspective 60.0 1.0 5000.0 (float s.X / float (max 1 s.Y)) |> Frustum.projTrafo)
                Sg.View view
                Sg.Proj proj
                Sg.Uniform("ViewportSize", size)
                MeshView.buildPaneScene model name onSetup None size
            }
        }

    // The Setup tile strip: per-mesh small multiples, mounted once per dataset
    // (mesh list) and visibility-scoped to the Setup level — no tiles at
    // Matrix/Pair/Pin.
    let setupTiles (env : Env<Message>) (model : AdaptiveModel) =
        let onSetup = model.Focus |> AVal.map ((=) FocusSetup)
        div {
            Class "setup-tiles"
            onSetup |> AVal.map (fun on -> if on then None else Some (Class "panes-off"))
            // Width-resize handle on the strip's left edge — pure DOM (layout
            // chrome, not model state); the tile render controls track their
            // client size, so they reflow for free.
            div {
                Class "tiles-handle"
                Attribute("title", "Drag to resize the tile strip")
                OnBoot [
                    "(function(){"
                    "var h=__THIS__; var p=h.parentElement;"
                    "h.addEventListener('pointerdown',function(e){"
                    "  e.preventDefault(); h.setPointerCapture(e.pointerId);"
                    "  function mv(ev){ var w=Math.max(160,Math.min(600,window.innerWidth-ev.clientX)); p.style.width=w+'px'; }"
                    "  function up(){ h.removeEventListener('pointermove',mv); h.removeEventListener('pointerup',up); }"
                    "  h.addEventListener('pointermove',mv); h.addEventListener('pointerup',up); });"
                    "})();"
                ]
            }
            model.MeshNames |> AList.map (surveyTile env model)
        }

    // The pane overlay: covers the central 3D area while the Pin level is
    // focused. Hidden via visibility (NOT display:none — a collapsed render
    // control would lose its viewport); the pane scenes are Sg.Active-gated so
    // hidden panes cost nothing.
    let panes (env : Env<Message>) (model : AdaptiveModel) =
        let onPin = model.Focus |> AVal.map ((=) FocusPin)
        div {
            Class "pin-panes"
            onPin |> AVal.map (fun on -> if on then None else Some (Class "panes-off"))
            let content =
                model.Sel
                |> AVal.map (fun s -> s.Pair)
                |> AVal.map (function
                    | Some (a, b) ->
                        IndexList.ofList [ paneControl env model PaneA a b
                                           paneControl env model PaneB b a ]
                    | None -> IndexList.empty)
                |> AList.ofAVal
            content
        }
