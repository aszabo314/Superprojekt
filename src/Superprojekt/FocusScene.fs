namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open Aardvark.Application
open Aardworx.WebAssembly
open FSharp.Data.Adaptive
open Aardvark.Dom

// WebGL focus panel. Each mesh is rendered full-res and textured in render space at
// its displayed pose (same frame as the main view). Top = strictly orthographic; Pano
// = cylindrical unwrap in the vertex shader. A tiny pan+zoom controller (no orbit)
// drives the single; the tiles are static thumbnails. Correspondence picking is
// Dom-driven (cursor → render ray → server raycast → 3D preview ghost on move, place
// on click); the `Sg.OnTap` GPU pick did not fire reliably in this 2nd render control.
module FocusScene =

    // Pan/zoom of the single, in fit-relative units (any focused mesh frames at
    // zoom 1 / pan 0). Module-level: one single control, state survives rebuilds.
    let private panNorm = cval V2d.Zero
    let private zoom = cval 1.0
    let resetCam () = transact (fun () -> panNorm.Value <- V2d.Zero; zoom.Value <- 1.0)
    let mutable private dragging = false
    let mutable private lastPx = V2i.Zero
    // Hover-preview throttle + generation guard (drops out-of-order raycast results).
    let private nowMs () = float System.DateTime.UtcNow.Ticks / 10000.0
    let mutable private hoverGen = 0
    let mutable private lastHoverMs = 0.0

    // GL orthographic projection (RH, -Z forward; near→-1, far→+1).
    let private orthoProj (hw : float) (hh : float) (near : float) (far : float) =
        let m =
            M44d(1.0 / hw, 0.0,      0.0,                 0.0,
                 0.0,      1.0 / hh, 0.0,                 0.0,
                 0.0,      0.0,      -2.0 / (far - near), -(far + near) / (far - near),
                 0.0,      0.0,      0.0,                 1.0)
        Trafo3d(m, m.Inverse)

    // local mesh → render space (the main view's meshTrafo): centroid-relative →
    // common-relative → dataset scale → displayed (before/after) pose.
    let private renderTrafoOf (model : AdaptiveModel) (name : string) (loaded : LoadedMesh) =
        let scale = model.DatasetScales |> AVal.map (fun m -> DatasetScale.forMesh m name)
        let baseT =
            (loaded.centroid, model.CommonCentroid, scale)
            |||> AVal.map3 (fun c common s -> Trafo3d.Translation(c - common) * Trafo3d.Scale s)
        (baseT, MeshView.displayedMeshT model name) ||> AVal.map2 (fun b t -> b * t), scale

    // Top-down ortho view + projection framing the displayed render centroid,
    // radius = localMaxR·scale, offset by (pan, zoom).
    let private orthoCam
            (size : aval<V2i>) (fitCenter : aval<V3d>) (fitExtent : aval<float>)
            (pan : aval<V2d>) (zoomA : aval<float>) =
        let view =
            AVal.custom (fun t ->
                let fc = fitCenter.GetValue t
                let ext = fitExtent.GetValue t
                let p = pan.GetValue t
                CameraView.lookAt (V3d(fc.X + p.X * ext, fc.Y + p.Y * ext, fc.Z + (ext + 1.0) * 5.0))
                                  (V3d(fc.X + p.X * ext, fc.Y + p.Y * ext, fc.Z)) (V3d(0.0, 1.0, 0.0))
                |> CameraView.viewTrafo)
        let proj =
            AVal.custom (fun t ->
                let s = size.GetValue t
                let ext = fitExtent.GetValue t
                let he = ext / max 1e-3 (zoomA.GetValue t)
                let aspect = float s.X / float (max 1 s.Y)
                orthoProj (he * aspect) he 0.01 ((ext + 1.0) * 12.0))
        view, proj

    let private vattrs (loaded : LoadedMesh) =
        HashMap.ofList [
            string DefaultSemantic.Positions,               BufferView(loaded.pos, typeof<V3f>)
            string DefaultSemantic.DiffuseColorCoordinates, BufferView(loaded.tc,  typeof<V2f>)
        ]

    // Focused mesh (Selection.FocusedMesh, falling back to first visible); while
    // peek-reference is held the single shows the reference instead.
    let private focusMeshOf (model : AdaptiveModel) =
        AVal.custom (fun t ->
            if model.FocusPeekReference.GetValue t then (model.Registration.GetValue t).ReferenceMesh
            else
                let names = model.MeshNames.Content.GetValue t |> IndexList.toList
                let vis = model.MeshVisible.GetValue t
                let visible = names |> List.filter (fun n -> Map.tryFind n vis |> Option.defaultValue true)
                match model.Selection.FocusedMesh.GetValue t with
                | Some m when List.contains m visible -> Some m
                | _ -> List.tryHead visible)

    // Large single: render-space, textured. Top = orthographic; Pano = cylindrical
    // unwrap (camera identity; the shader writes clip directly). Picking is Dom-driven
    // (the Sg pick didn't fire reliably in this 2nd control): the cursor is inverted to
    // a render-space ray, raycast on the server, and the hit drives a live 3D preview
    // ghost on move + the placement on click. Shared pan/zoom, mouse-anchored zoom.
    let private focusSingle (env : Env<Message>) (model : AdaptiveModel) (name : string) (proj : FocusProjection) : DomNode =
        let loaded = MeshView.loadMeshAsync (fun () -> ()) name
        let renderT, scale = renderTrafoOf model name loaded
        let fitCenter = renderT |> AVal.map (fun t -> t.Forward.TransformPos V3d.Zero)
        let fitExtent = (loaded.localMaxR, scale) ||> AVal.map2 (fun r s -> max 1e-4 (r * s * 1.15))
        let isPano = (proj = ProjPano)
        renderControl {
            RenderControl.Samples 1
            Class "focus-rc"
            let! size = RenderControl.ViewportSize
            // Cursor (px,py) → world surface point via a server raycast. Build the
            // render-space ray (ortho drop / pano direction from the eye), map it into
            // the mesh's own frame for the server, map the hit back to displayed world.
            let worldRayHit (px : float) (py : float) : Async<V3d option> =
                let s = AVal.force size
                let w = float (max 1 s.X)
                let h = float (max 1 s.Y)
                let aspect = w / h
                let clipX = 2.0 * px / w - 1.0
                let clipY = 1.0 - 2.0 * py / h
                let fc = AVal.force fitCenter
                let ext = AVal.force fitExtent
                let z = zoom.Value
                let pan = panNorm.Value
                let originR, dirR =
                    if isPano then
                        let u = pan.X + clipX * aspect / z
                        let v = pan.Y + clipY / z
                        let az = u * System.Math.PI
                        let el = v * System.Math.PI * 0.5
                        fc, V3d(cos el * cos az, cos el * sin az, sin el)
                    else
                        let halfE = ext / max 1e-3 z
                        V3d(fc.X + pan.X * ext + clipX * halfE * aspect,
                            fc.Y + pan.Y * ext + clipY * halfE,
                            fc.Z + (ext + 1.0) * 5.0), V3d(0.0, 0.0, -1.0)
                let rT = AVal.force renderT
                let sc = AVal.force scale
                let cc = AVal.force model.CommonCentroid
                let mc = AVal.force loaded.centroid
                let originAbsW = rT.Backward.TransformPos originR + mc
                let dirLocal = (rT.Backward.TransformDir dirR).Normalized
                async {
                    let! hit = Query.rayHit ApiConfig.apiBase.Value name 0 originAbsW dirLocal
                    return hit |> Option.map (fun hh -> rT.Forward.TransformPos (hh.point - mc) / sc + cc)
                }
            // Set mode: move = live 3D preview ghost (throttled), click = place + exit.
            // Otherwise: drag = pan.
            Dom.OnPointerDown(fun e ->
                let p = e.OffsetPosition
                lastPx <- p
                if AVal.force model.CorrSetMode then
                    if e.Button = Button.Left then
                        match AVal.force model.Selection.SelectedPin with
                        | Some pinId ->
                            async {
                                match! worldRayHit (float p.X) (float p.Y) with
                                | Some world -> env.Emit [PickCorrespondenceAt(pinId, name, world)]
                                | None -> ()
                            } |> Async.Start
                        | None -> ()
                elif e.Button = Button.Left then dragging <- true)
            Dom.OnPointerUp(fun _ -> dragging <- false)
            Dom.OnPointerMove(fun e ->
                let p = e.OffsetPosition
                if AVal.force model.CorrSetMode then
                    let now = nowMs ()
                    if now - lastHoverMs > 60.0 then
                        lastHoverMs <- now
                        hoverGen <- hoverGen + 1
                        let gen = hoverGen
                        async {
                            let! wld = worldRayHit (float p.X) (float p.Y)
                            if gen = hoverGen then env.Emit [CorrPreviewComputed wld]
                        } |> Async.Start
                elif dragging then
                    let hh = float (max 1 (AVal.force size).Y)
                    let d = p - lastPx
                    let k = 2.0 / (hh * max 1e-3 zoom.Value)
                    transact (fun () ->
                        panNorm.Value <- panNorm.Value + V2d(-float d.X * k, float d.Y * k))
                lastPx <- p)
            Dom.OnMouseLeave(fun _ ->
                if AVal.force model.CorrSetMode then
                    hoverGen <- hoverGen + 1
                    env.Emit [CorrPreviewComputed None])
            // Mouse-anchored zoom: keep the plane point under the cursor fixed.
            Dom.OnMouseWheel(fun e ->
                let s = AVal.force size
                let w = float (max 1 s.X)
                let h = float (max 1 s.Y)
                let aspect = w / h
                let clipX = 2.0 * float lastPx.X / w - 1.0
                let clipY = 1.0 - 2.0 * float lastPx.Y / h
                let z = zoom.Value
                let z' = clamp 0.05 200.0 (z * (1.1 ** (-e.DeltaY / 120.0)))
                transact (fun () ->
                    zoom.Value <- z'
                    panNorm.Value <- panNorm.Value + V2d(clipX * aspect * (1.0/z - 1.0/z'), clipY * (1.0/z - 1.0/z'))))
            let viewT, projT =
                if isPano then AVal.constant Trafo3d.Identity, AVal.constant Trafo3d.Identity
                else orthoCam size fitCenter fitExtent (panNorm :> aval<_>) (zoom :> aval<_>)
            Sg.View viewT
            Sg.Proj projT
            if isPano then
                sg {
                    Sg.Trafo renderT
                    Sg.Shader { DefaultSurfaces.trafo; FocusShaders.pano; DefaultSurfaces.diffuseTexture }
                    Sg.Uniform("DiffuseColorTexture", loaded.tex)
                    Sg.Uniform("PanoEye",    fitCenter |> AVal.map (fun c -> V3f(float32 c.X, float32 c.Y, float32 c.Z)))
                    Sg.Uniform("PanoCenter", (panNorm :> aval<_>) |> AVal.map (fun p -> V2f(float32 p.X, float32 p.Y)))
                    Sg.Uniform("PanoZoom",   (zoom :> aval<_>) |> AVal.map float32)
                    Sg.Uniform("PanoAspect", size |> AVal.map (fun s -> float32 (float s.X / float (max 1 s.Y))))
                    Sg.Uniform("PanoRadFar", fitExtent |> AVal.map (fun e -> float32 (e * 2.0)))
                    Sg.NoEvents
                    Sg.VertexAttributes(vattrs loaded)
                    Sg.Index(BufferView(loaded.idx, typeof<int>))
                    Sg.Render loaded.fvc
                }
            else
                sg {
                    Sg.Trafo renderT
                    Sg.Shader { DefaultSurfaces.trafo; DefaultSurfaces.diffuseTexture }
                    Sg.Uniform("DiffuseColorTexture", loaded.tex)
                    Sg.NoEvents
                    Sg.VertexAttributes(vattrs loaded)
                    Sg.Index(BufferView(loaded.idx, typeof<int>))
                    Sg.Render loaded.fvc
                }
        }

    // One static thumbnail tile per mesh, clickable to focus (always ortho top-down).
    let private focusTile (env : Env<Message>) (model : AdaptiveModel) (name : string) : DomNode =
        let loaded = MeshView.loadMeshAsync (fun () -> ()) name
        let renderT, scale = renderTrafoOf model name loaded
        let fitCenter = renderT |> AVal.map (fun t -> t.Forward.TransformPos V3d.Zero)
        let fitExtent = (loaded.localMaxR, scale) ||> AVal.map2 (fun r s -> max 1e-4 (r * s * 1.15))
        let rc =
            renderControl {
                RenderControl.Samples 1
                Class "focus-rc"
                let! size = RenderControl.ViewportSize
                let view, proj = orthoCam size fitCenter fitExtent (AVal.constant V2d.Zero) (AVal.constant 1.0)
                Sg.View view
                Sg.Proj proj
                sg {
                    Sg.Trafo renderT
                    Sg.Shader { DefaultSurfaces.trafo; DefaultSurfaces.diffuseTexture }
                    Sg.Uniform("DiffuseColorTexture", loaded.tex)
                    Sg.NoEvents
                    Sg.VertexAttributes(vattrs loaded)
                    Sg.Index(BufferView(loaded.idx, typeof<int>))
                    Sg.Render loaded.fvc
                }
            }
        let idxVal = model.MeshOrder |> AMap.tryFind name |> AVal.map (Option.defaultValue 0)
        let colorCss = idxVal |> AVal.map (fun i -> Primitives.c4bToRgbCss (Primitives.meshColor i))
        let active = model.Selection.FocusedMesh |> AVal.map ((=) (Some name))
        div {
            Class "focus-tile"
            active |> AVal.map (fun a -> if a then Some (Class "fm-active") else None)
            Attribute("title", "click → focus this mesh")
            Dom.OnClick(fun _ -> env.Emit [SetFocusedMesh (Some name)])
            rc
            div {
                Class "fm-label"
                span { Class "fm-sw"; colorCss |> AVal.map (fun c -> Some (Style [Css.Background c])) }
                Primitives.shortName name
            }
        }

    // The large single (keyed by (mesh, projection) so a projection toggle rebuilds).
    let single (env : Env<Message>) (model : AdaptiveModel) =
        (focusMeshOf model, model.FocusProjection)
        ||> AVal.map2 (fun m proj ->
            match m with Some n -> IndexList.single (n, proj) | None -> IndexList.empty)
        |> AList.ofAVal
        |> AList.map (fun (n, proj) -> focusSingle env model n proj)

    // One control per visible mesh, keyed by the stable mesh index.
    let multiples (env : Env<Message>) (model : AdaptiveModel) =
        let tileNames =
            AVal.custom (fun t ->
                let vis = model.MeshVisible.GetValue t
                model.MeshNames.Content.GetValue t
                |> IndexList.filter (fun n -> Map.tryFind n vis |> Option.defaultValue true))
        tileNames |> AList.ofAVal |> AList.map (focusTile env model)
