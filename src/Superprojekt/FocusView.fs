namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open FSharp.Data.Adaptive
open Aardvark.Dom

// Focus-panel large single (spec v3 §B/§C/§D): the focused mesh drawn solid in
// the one secondary WebGL control, either orthographically (Top/Front/Side) or as
// a cylindrical panorama (FocusProject.vertex). Colour reuses the full MeshShader
// channel stack: textured in pick context, the active §6 channel in compare.
// Only ONE mesh renders here — the panel never holds a second live scene.
module FocusView =

    // View/proj for the rc + the panorama eye/range uniforms, shared with
    // GuiFocus so the surface-pick can invert the same projection.
    type FocusCam = {
        View       : Trafo3d
        Proj       : Trafo3d
        EyeRender  : V3f
        RangeRender: float32
        Pano       : int
    }

    let private scaleFor (model : AdaptiveModel) (name : string) =
        model.DatasetScales |> AVal.map (fun m -> DatasetScale.forMesh m name)

    // Render-space mesh→world trafo (matches MeshView.meshTrafo): base maps
    // mesh-local → render space, then the effective registration trafo.
    let fullTrafo (model : AdaptiveModel) (loaded : LoadedMesh) (name : string) =
        let scale = scaleFor model name
        let base_ =
            (model.CommonCentroid, loaded.centroid, scale) |||> AVal.map3 (fun common mesh s ->
                Trafo3d.Translation(mesh - common) * Trafo3d.Scale(s))
        (base_, MeshView.effectiveMeshT model name) ||> AVal.map2 (fun b t -> b * t)

    // Render-space bbox of a mesh at its effective pose. World corners →
    // render-local ((w-cc)·scale) → effective registration trafo.
    let private renderBox (model : AdaptiveModel) (name : string) =
        let meshT = MeshView.effectiveMeshT model name
        let scA = scaleFor model name
        AVal.custom (fun t ->
            let mb = model.MeshBounds.GetValue t
            let cc = model.CommonCentroid.GetValue t
            let scale = scA.GetValue t
            let tr = meshT.GetValue t
            match Map.tryFind name mb with
            | None -> Box3d(V3d(-5.0, -5.0, -5.0), V3d(5.0, 5.0, 5.0))
            | Some (wb : Box3d) ->
                let mutable b = Box3d.Invalid
                for ix in 0 .. 1 do
                  for iy in 0 .. 1 do
                    for iz in 0 .. 1 do
                        let w =
                            V3d((if ix = 0 then wb.Min.X else wb.Max.X),
                                (if iy = 0 then wb.Min.Y else wb.Max.Y),
                                (if iz = 0 then wb.Min.Z else wb.Max.Z))
                        b.ExtendBy(tr.Forward.TransformPos ((w - cc) * scale))
                if b.IsValid then b else Box3d(V3d(-5.0, -5.0, -5.0), V3d(5.0, 5.0, 5.0)))

    // Camera for the focused mesh. Pano eye = focus own sensor (pick) or the
    // reference own sensor (compare). Ortho fits the focus mesh bbox.
    let cam (model : AdaptiveModel) (name : string) (compareContext : aval<bool>) : aval<FocusCam> =
        let loaded = MeshView.loadMeshAsync (fun _ -> ()) name
        let scale = scaleFor model name
        let trafo = fullTrafo model loaded name
        let refMeshA = model.Registration |> AVal.map (fun r -> r.ReferenceMesh)
        let refTrafo =
            refMeshA |> AVal.bind (function
                | Some r when r <> name -> fullTrafo model (MeshView.loadMeshAsync (fun _ -> ()) r) r
                | _ -> trafo)
        let eyeRender =
            (compareContext, trafo, refTrafo) |||> AVal.map3 (fun cmp (t : Trafo3d) (rt : Trafo3d) ->
                V3f ((if cmp then rt else t).Forward.TransformPos V3d.Zero))
        let rangeRender = (loaded.localMaxR, scale) ||> AVal.map2 (fun r s -> float32 (max 1e-6 (r * s)))
        let box = renderBox model name
        AVal.custom (fun t ->
            let proj = model.FocusProjection.GetValue t
            let b = box.GetValue t
            let eye = eyeRender.GetValue t
            let rng = rangeRender.GetValue t
            let c = if b.IsValid then b.Center else V3d.Zero
            let size = if b.IsValid then b.Size else V3d(10.0, 10.0, 10.0)
            let r = max 1.0 (size.Length)
            match proj with
            | ProjPano ->
                let e = V3d eye
                let v = CameraView.lookAt e (e + V3d.IOO) V3d.OOI |> CameraView.viewTrafo
                let p = Frustum.perspective 90.0 0.1 (2.0 * r + 100.0) 1.0 |> Frustum.projTrafo
                { View = v; Proj = p; EyeRender = eye; RangeRender = rng; Pano = 1 }
            | _ ->
                let eyeO, up =
                    match proj with
                    | ProjTop   -> c + V3d(0.0, 0.0, r), V3d.OIO
                    | ProjFront -> c + V3d(0.0, -r, 0.0), V3d.OOI
                    | _         -> c + V3d(r, 0.0, 0.0), V3d.OOI
                let v = CameraView.lookAt eyeO c up |> CameraView.viewTrafo
                let half = max 1.0 ((max size.X (max size.Y size.Z)) * 0.62)
                let fr : Frustum =
                    { left = -half; right = half; bottom = -half; top = half
                      near = 0.1; far = 2.0 * r + 100.0; isOrtho = true }
                { View = v; Proj = Frustum.projTrafo fr; EyeRender = eye; RangeRender = rng; Pano = 0 })

    // Per-vertex triangle-quality attribute (incident-face mean of 4√3·A/Σl²),
    // matching MeshView's ShapeQ.
    let private shapeBuf (loaded : LoadedMesh) =
        loaded.pos |> AVal.map (fun _ ->
            match loaded.mesh.Value with
            | Some md ->
                let pos = md.positions
                let idx = md.indices
                let q   = Array.zeroCreate<float32> pos.Length
                let cnt = Array.zeroCreate<int> pos.Length
                let mutable f = 0
                while f + 2 < idx.Length do
                    let a, b, c = idx.[f], idx.[f+1], idx.[f+2]
                    let pa, pb, pc = pos.[a], pos.[b], pos.[c]
                    let denom = (pb - pa).LengthSquared + (pc - pb).LengthSquared + (pa - pc).LengthSquared
                    let area  = 0.5f * (Vec.cross (pb - pa) (pc - pa)).Length
                    let ql    = if denom > 1e-12f then clamp 0.0f 1.0f (4.0f * 1.7320508f * area / denom) else 0.0f
                    q.[a] <- q.[a] + ql; cnt.[a] <- cnt.[a] + 1
                    q.[b] <- q.[b] + ql; cnt.[b] <- cnt.[b] + 1
                    q.[c] <- q.[c] + ql; cnt.[c] <- cnt.[c] + 1
                    f <- f + 3
                for i in 0 .. pos.Length - 1 do
                    if cnt.[i] > 0 then q.[i] <- q.[i] / float32 cnt.[i]
                ArrayBuffer q :> IBuffer
            | None -> ArrayBuffer [| 0.0f; 0.0f; 0.0f |] :> IBuffer)

    // Global shared extrinsic scale = max robust |scalar| across cached previews
    // (shared colour scale across panels, spec §C compare).
    let private globalDistScale (model : AdaptiveModel) =
        model.FocusMaps |> AVal.map (fun maps ->
            let mutable mx = 1e-3
            for KeyValue(_, p) in maps do
                mx <- max mx (max (abs p.Lo) (abs p.Hi))
            float32 mx)

    let private meshColorOf (model : AdaptiveModel) (name : string) =
        let palette = Primitives.meshPaletteV4d
        model.MeshOrder |> AMap.tryFind name |> AVal.map (fun io ->
            let i = Option.defaultValue 0 io
            V4f palette.[i % palette.Length])

    // The large single. compareContext = step 4 (active §6 channel); else pick
    // (textured). geomMeshA = what to render; camMeshA = whose camera/eye to use
    // (≠ geometry under peek-reference, §E — the reference geometry seen from the
    // focused mesh's own frame). camFor name → the shared camera (so the rc + the
    // surface-pick see one projection).
    let buildSingle
            (model : AdaptiveModel)
            (geomMeshA : aval<string option>)
            (camMeshA : aval<string option>)
            (compareContext : aval<bool>)
            (camFor : string -> aval<FocusCam>) : aset<ISceneNode> =
        let refMeshA = model.Registration |> AVal.map (fun r -> r.ReferenceMesh)
        let distScaleGlobal = globalDistScale model
        geomMeshA
        |> AVal.map (function None -> IndexList.empty | Some n -> IndexList.single n)
        |> AList.ofAVal
        |> AList.map (fun name ->
            let loaded = MeshView.loadMeshAsync (fun _ -> ()) name
            let trafo = fullTrafo model loaded name
            let camA = (camMeshA |> AVal.map (Option.defaultValue name)) |> AVal.bind camFor
            let sensorOrigin = trafo |> AVal.map (fun t -> V3f (t.Forward.TransformPos V3d.Zero))

            // Channel resolution: pick = textured; compare = §6 channel
            // (reference shows shaded-relief). → (renderingMode, distEnc, heatMode).
            let channel =
                AVal.custom (fun t ->
                    let cmp = compareContext.GetValue t
                    let isRef = refMeshA.GetValue t = Some name
                    let surf = model.SurfaceDistOn.GetValue t
                    let variance = model.VarianceOn.GetValue t
                    let heat = model.HeatmapMode.GetValue t
                    let rmInt = match model.RenderingMode.GetValue t with Textured -> 0 | Shaded -> 1 | SlopeColor -> 2
                    if not cmp then (rmInt, 0, 0)
                    elif isRef then (1, 0, 0)
                    elif surf then (rmInt, 1, 0)
                    elif variance then (1, 0, 0)
                    else
                        match heat with
                        | HeatIncidence -> (rmInt, 0, 1)
                        | HeatRange     -> (rmInt, 0, 2)
                        | HeatShape     -> (rmInt, 0, 3)
                        | HeatOff       -> (rmInt, 0, 0))
            let renderingModeInt = channel |> AVal.map (fun (r, _, _) -> r)
            let distEncoding     = channel |> AVal.map (fun (_, d, _) -> d)
            let heatMode         = channel |> AVal.map (fun (_, _, h) -> h)

            let myDist = model.SurfaceDistance |> AVal.map (Map.tryFind name)
            let distBuf =
                (myDist, loaded.pos) ||> AVal.map2 (fun d _ ->
                    match d with
                    | Some arr -> ArrayBuffer arr :> IBuffer
                    | None ->
                        match loaded.mesh.Value with
                        | Some md -> ArrayBuffer (Array.zeroCreate<float32> md.positions.Length) :> IBuffer
                        | None -> ArrayBuffer [| 0.0f; 0.0f; 0.0f |] :> IBuffer)
            let distLoD =
                (model.ScanPins.SelectedPin, model.ScanPins.Pins |> AMap.toAVal)
                ||> AVal.map2 (fun sel pins ->
                    match sel |> Option.bind (fun id -> HashMap.tryFind id pins) with
                    | Some p ->
                        match p.Probe with
                        | ProbeReady r ->
                            let stdOf m =
                                r.Distributions |> Array.tryFind (fun d -> d.MeshName = m)
                                |> Option.map (fun d -> d.Std) |> Option.defaultValue 0.0
                            1.96 * sqrt (stdOf r.ReferenceMesh ** 2.0 + stdOf name ** 2.0) |> float32
                        | _ -> 0.0f
                    | None -> 0.0f)

            let view = camA |> AVal.map (fun c -> c.View)
            let proj = camA |> AVal.map (fun c -> c.Proj)
            let panoFlag = camA |> AVal.map (fun c -> c.Pano)
            let eyeRender = camA |> AVal.map (fun c -> c.EyeRender)
            let rangeRender = camA |> AVal.map (fun c -> c.RangeRender)

            let renderEnabled = loaded.fvc |> AVal.map (fun c -> c > 3)
            sg {
                Sg.View view
                Sg.Proj proj
                Sg.DepthTest (AVal.constant DepthTest.LessOrEqual)
                Sg.BlendMode (AVal.constant BlendMode.Blend)
                Sg.Active renderEnabled
                Sg.Trafo trafo
                Sg.Shader {
                    FocusProject.vertex
                    DefaultSurfaces.diffuseTexture
                    MeshShader.shade
                }
                Sg.Uniform("DiffuseColorTexture", loaded.tex)
                Sg.Uniform("FocusPano",  panoFlag)
                Sg.Uniform("FocusEye",   eyeRender)
                Sg.Uniform("FocusRange", rangeRender)
                Sg.Uniform("MeshActive",      AVal.constant true)
                Sg.Uniform("GhostOpacity",    AVal.constant 0.0f)
                Sg.Uniform("RenderingMode",   renderingModeInt)
                Sg.Uniform("MeshColor",       meshColorOf model name)
                Sg.Uniform("ShadingStrength", model.ShadingStrength |> AVal.map float32)
                Sg.Uniform("SlopeThreshold",
                    model.SlopeThresholdDeg |> AVal.map (fun d -> sin (d * System.Math.PI / 180.0) |> float32))
                Sg.Uniform("BlobCount",       AVal.constant 0)
                Sg.Uniform("Blobs",           AVal.constant (Array.zeroCreate<V4f> MeshShader.MaxBlobs))
                Sg.Uniform("AnchorGhost",     AVal.constant 0)
                Sg.Uniform("CursorActive",         AVal.constant 0)
                Sg.Uniform("CursorPlaneOrigin",    AVal.constant V3f.Zero)
                Sg.Uniform("CursorPlaneNormal",    AVal.constant V3f.OOI)
                Sg.Uniform("CursorHighlightWidth", AVal.constant 0.0f)
                Sg.Uniform("CursorDarken",         AVal.constant 1.0f)
                Sg.Uniform("CursorClip",           AVal.constant 0)
                Sg.Uniform("CursorPinCentre",      AVal.constant V3f.Zero)
                Sg.Uniform("CursorPinRadius",      AVal.constant 0.0f)
                Sg.Uniform("CursorCylLength",      AVal.constant 0.0f)
                Sg.Uniform("ClipPlaneCount",       AVal.constant 0)
                Sg.Uniform("ClipPlane0",           AVal.constant V4f.Zero)
                Sg.Uniform("ClipPlane1",           AVal.constant V4f.Zero)
                Sg.Uniform("DistanceEncoding",     distEncoding)
                Sg.Uniform("DistLoD",              distLoD)
                Sg.Uniform("DistScale",            distScaleGlobal)
                Sg.Uniform("HeatmapMode",          heatMode)
                Sg.Uniform("SensorOrigin",         sensorOrigin)
                Sg.Uniform("RangeMax",             rangeRender)
                Sg.VertexAttributes(
                    HashMap.ofList [
                        string DefaultSemantic.Positions,               BufferView(loaded.pos, typeof<V3f>)
                        string DefaultSemantic.DiffuseColorCoordinates, BufferView(loaded.tc,  typeof<V2f>)
                        string DefaultSemantic.Normals,                 BufferView(loaded.nrm, typeof<V3f>)
                        "SurfaceDist",                                  BufferView(distBuf, typeof<float32>)
                        "ShapeQ",                                       BufferView(shapeBuf loaded, typeof<float32>)
                    ])
                Sg.Index(BufferView(loaded.idx, typeof<int>))
                Sg.Render loaded.fvc
            } :> ISceneNode)
        |> AList.toASet
