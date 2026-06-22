namespace Superprojekt

open Aardvark.Base
open Aardworx.WebAssembly
open Aardvark.Rendering
open FSharp.Data.Adaptive
open Aardvark.Dom

type LoadedMesh =
    {
        centroid : aval<V3d>
        pos  : aval<IBuffer>
        tc   : aval<IBuffer>
        nrm  : aval<IBuffer>
        idx  : aval<IBuffer>
        tex  : aval<ITexture>
        fvc  : aval<int>
        mesh : MeshData option ref
    }

// Elevation-cursor slicing-plane highlight (world-space metres). Built in
// View.fs from the chart cursor (priority) or the 3D hover point inside the
// effective pin's probe cylinder; None = off.
type CursorHighlight =
    {
        Origin    : V3d
        Normal    : V3d
        Clip      : bool
        PinCentre : V3d
        PinRadius : float
        CylLength : float
    }

module MeshView =

    let apiBase = ApiConfig.apiBase

    let private meshes = System.Collections.Generic.Dictionary<string, LoadedMesh>()

    let loadMeshAsync (finished : unit -> unit) (name : string) : LoadedMesh =
        match meshes.TryGetValue(name) with
        | true, m -> m
        | _ ->
            let ccc = cval V3d.Zero
            let m =
                {
                    centroid = ccc
                    pos  = cval (ArrayBuffer [| V3f.Zero; V3f.Zero; V3f.Zero |] :> IBuffer)
                    tc   = cval (ArrayBuffer [| V2f.Zero; V2f.Zero; V2f.Zero |] :> IBuffer)
                    nrm  = cval (ArrayBuffer [| V3f.OOI; V3f.OOI; V3f.OOI |] :> IBuffer)
                    idx  = cval (ArrayBuffer [| 0; 1; 2 |] :> IBuffer)
                    tex  = cval<ITexture> (AVal.force DefaultTextures.checkerboard)
                    fvc  = cval 3
                    mesh = ref None
                }
            meshes.[name] <- m
            task {
                try
                    let! mesh = MeshData.fetch apiBase.Value name 0
                    m.mesh.Value <- Some mesh
                    transact (fun () ->
                        ccc.Value <- mesh.centroid
                        (m.pos :?> cval<IBuffer>).Value <- ArrayBuffer mesh.positions
                        (m.tc  :?> cval<IBuffer>).Value <- ArrayBuffer mesh.uvs
                        (m.nrm :?> cval<IBuffer>).Value <- ArrayBuffer mesh.normals
                        (m.idx :?> cval<IBuffer>).Value <- ArrayBuffer mesh.indices
                        (m.fvc :?> cval<int>).Value     <- mesh.indices.Length
                    )
                    let! img = JSImage.load mesh.atlasUrl
                    transact (fun () -> (m.tex :?> cval<ITexture>).Value <- JSTexture(img, true))

                    finished()
                with e ->
                    Log.error "failed to load mesh %s: %A" name e
            } |> ignore
            m

    let private meshTrafo
        (commonCentroid : aval<V3d>) (loaded : LoadedMesh)
        (meshScale : aval<float>) (meshTransform : aval<Trafo3d>) =
        let base_ =
            (commonCentroid, loaded.centroid, meshScale) |||> AVal.map3 (fun common mesh scale ->
                Trafo3d.Translation(mesh - common) * Trafo3d.Scale(scale))
        // Postfix composition (a * b applies a first): base maps mesh-local →
        // render space, THEN the registration trafo (a render-space map). Don't
        // flip to `t * b` — it applies the render-space map to mesh-local
        // coords, wrong for scaled datasets / large rotations and inconsistent
        // with the renderToWorld query paths.
        (base_, meshTransform) ||> AVal.map2 (fun b t -> b * t)

    let private scaleFor (model : AdaptiveModel) (name : string) =
        model.DatasetScales |> AVal.map (fun m -> DatasetScale.forMesh m name)

    // Committed render trafo, and the effective one (committed ∘ pending
    // preview delta) every mesh renders with while a solve preview is open.
    let committedMeshT (model : AdaptiveModel) (name : string) =
        model.MeshTransforms |> AVal.map (fun m ->
            Map.tryFind name m |> Option.defaultValue Trafo3d.Identity)

    let effectiveMeshT (model : AdaptiveModel) (name : string) =
        (model.MeshTransforms, model.PendingReg) ||> AVal.map2 (fun m pending ->
            let c = Map.tryFind name m |> Option.defaultValue Trafo3d.Identity
            match PendingRegistration.delta name pending with
            | Some d -> RegLog.effective c d
            | None -> c)

    let visibleMeshNames (model : AdaptiveModel) =
        let visible = AVal.force model.MeshVisible
        model.MeshNames |> AList.toAVal |> AVal.force |> IndexList.toList
        |> List.filter (fun n -> Map.tryFind n visible |> Option.defaultValue true)

    // Pin blobs as a 32-slot uniform array, metric → render space (centre xyz,
    // inner radius w). Shared by the mesh shader (isolation + provenance) and
    // the fusion shader (conditioning).
    let private pinBlobUniforms (model : AdaptiveModel) =
        let datasetScale =
            (model.ActiveDataset, model.DatasetScales) ||> AVal.map2 DatasetScale.active
        let blobsArr =
            (model.ScanPins.Pins |> AMap.toAVal, model.CommonCentroid, datasetScale)
            |||> AVal.map3 (fun pinsMap cc scale ->
                let pins  = HashMap.toArray pinsMap |> Array.map snd
                let n     = min pins.Length MeshShader.MaxBlobs
                let centres  = Array.zeroCreate<V4f> MeshShader.MaxBlobs
                for i in 0 .. n - 1 do
                    let p  = pins.[i]
                    let cr = (p.Centre - cc) * scale
                    let ir = float32 (p.InnerRadius * scale)
                    centres.[i]  <- V4f(float32 cr.X, float32 cr.Y, float32 cr.Z, ir)
                n, centres)
        blobsArr |> AVal.map (fun (n, _) -> n),
        blobsArr |> AVal.map (fun (_, c) -> c)

    // Smoothstep half-width of the contact-line highlight band (metres) and
    // the darkening applied to the rest of an intersected mesh.
    [<Literal>]
    let private cursorHighlightWidth = 0.02
    [<Literal>]
    let private cursorDarken = 0.85f

    let buildScene (loadFinished : string -> unit) (cursor : aval<CursorHighlight option>) (clip : aval<int * V4f * V4f>) (previewSwap : aval<bool>) (wheelIsolation : aval<string option>) (model : AdaptiveModel) : aset<ISceneNode> =
        let renderingModeInt =
            model.RenderingMode |> AVal.map (function
                | Textured     -> 0
                | Shaded       -> 1
                | SlopeColor   -> 2)
        let meshIndices =
            model.MeshNames |> AList.toAVal |> AVal.map (fun names ->
                names |> Seq.mapi (fun i n -> n, i) |> Map.ofSeq)
        let palette = Primitives.meshPaletteV4d

        // LassoEnabled gates count to 0 (disabled = no effect) while the volume
        // is kept for re-enabling.
        let lassoPlaneCount =
            (model.LassoVolume, model.LassoEnabled) ||> AVal.map2 (fun lv on ->
                match lv with
                | Some v when on -> min v.Planes.Length MeshShader.MaxLassoPlanes
                | _              -> 0)
        let lassoPlanes =
            model.LassoVolume |> AVal.map (fun lv ->
                let arr = Array.zeroCreate<V4f> MeshShader.MaxLassoPlanes
                match lv with
                | Some v ->
                    let n = min v.Planes.Length MeshShader.MaxLassoPlanes
                    for i in 0 .. n - 1 do
                        let p = v.Planes.[i]
                        arr.[i] <- V4f(float32 p.X, float32 p.Y, float32 p.Z, float32 p.W)
                | None -> ()
                arr)

        let blobCount, blobs = pinBlobUniforms model
        let clipCount  = clip |> AVal.map (fun (c, _, _) -> c)
        let clipPlane0 = clip |> AVal.map (fun (_, p, _) -> p)
        let clipPlane1 = clip |> AVal.map (fun (_, _, p) -> p)
        // Cursor-plane uniforms shared by every mesh (metric → render once).
        // CursorActive is the only per-mesh one (below).
        let cursorRender =
            let datasetScaleA =
                (model.ActiveDataset, model.DatasetScales) ||> AVal.map2 DatasetScale.active
            (cursor, model.CommonCentroid, datasetScaleA) |||> AVal.map3 (fun cOpt cc s ->
                match cOpt with
                | Some c ->
                    V3f (ScanPin.renderCentre cc s c.Origin),
                    V3f c.Normal,
                    (if c.Clip then 1 else 0),
                    V3f (ScanPin.renderCentre cc s c.PinCentre),
                    float32 (ScanPin.renderLength s c.PinRadius),
                    float32 (ScanPin.renderLength s c.CylLength),
                    float32 (ScanPin.renderLength s cursorHighlightWidth)
                | None -> V3f.Zero, V3f.OOI, 0, V3f.Zero, 0.0f, 0.0f, 0.0f)
        let cursorOrigin = cursorRender |> AVal.map (fun (o, _, _, _, _, _, _) -> o)
        let cursorNormal = cursorRender |> AVal.map (fun (_, n, _, _, _, _, _) -> n)
        let cursorClip   = cursorRender |> AVal.map (fun (_, _, c, _, _, _, _) -> c)
        let cursorPinC   = cursorRender |> AVal.map (fun (_, _, _, p, _, _, _) -> p)
        let cursorPinR   = cursorRender |> AVal.map (fun (_, _, _, _, r, _, _) -> r)
        let cursorCylLen = cursorRender |> AVal.map (fun (_, _, _, _, _, l, _) -> l)
        let cursorWidth  = cursorRender |> AVal.map (fun (_, _, _, _, _, _, w) -> w)
        // Chart column highlight (hover wins over sticky): highlighted mesh
        // normal, others drop to the uniform-ghost path (MeshActive=false) at
        // fixed 0.2 α — independent of GhostSilhouette (explicit gesture).
        // Suspended during anchor placement so the column can't ghost (and
        // un-pick) the terrain you're aiming at — same as the isolation suspend.
        let chartHighlight =
            (model.ChartHoverMesh, model.ChartStickyMesh, model.ScanPins.Placement)
            |||> AVal.map3 (fun hov sticky pl ->
                match pl with
                | AnchorPlacement -> None
                | _ -> hov |> Option.orElse sticky)
        // Reference peek (spring-loaded): while held with a reference set, the
        // reference is the only solid mesh — transient, no eye-state mutation.
        let peekTarget =
            (model.ReferencePeekHeld, model.Registration) ||> AVal.map2 (fun held reg ->
                if held then reg.ReferenceMesh else None)
        // Auto-suspend pin isolation while placing an anchor so the terrain
        // stays visible for aiming (auto-restored, no model mutation).
        let anchorGhost =
            (model.AnchorGhostMode, model.ScanPins.Placement) ||> AVal.map2 (fun on pl ->
                match pl with
                | AnchorPlacement -> 0
                | _ -> if on then 1 else 0)
        let heatmapModeInt =
            model.HeatmapMode |> AVal.map (function
                | HeatOff -> 0
                | HeatProvenance -> 1
                | HeatDiff -> 2)
        let diffSigmaRef =
            (model.Registration, model.MeshSensorTypes, model.MeshDatasetErrors)
            |||> AVal.map3 (fun reg sensors overrides ->
                match reg.ReferenceMesh with
                | Some r -> Provenance.datasetError overrides sensors r |> float32
                | None -> 0.0f)
        let provThreshold =
            model.ProvenanceThreshold |> AVal.map float32
        model.MeshNames |> AList.map (fun name ->
            let loaded = loadMeshAsync (fun () -> loadFinished name) name
            // One-shot 3D anchor pick: target mesh solid (forced visible),
            // reference at α 0.3, everything else ghosts — all shader-level.
            // Option/Alt isolates the wheel-selected picking layer the same way
            // (pick mode wins when both active).
            let isActive =
                AVal.custom (fun t ->
                    match model.AnchorPick.GetValue t with
                    | Some pick -> pick.Mesh = name
                    | None ->
                    match peekTarget.GetValue t with
                    | Some target -> target = name
                    | None ->
                        match wheelIsolation.GetValue t with
                        | Some iso -> iso = name
                        | None ->
                            let vis =
                                Map.tryFind name (model.MeshVisible.GetValue t)
                                |> Option.defaultValue true
                            match chartHighlight.GetValue t with
                            | Some hm -> vis && hm = name
                            | None -> vis)
            let scale = scaleFor model name
            // Effective pose = committed ∘ pending preview delta; while the
            // before/after swap is held, render the committed pose instead.
            let meshT =
                (effectiveMeshT model name, committedMeshT model name, previewSwap)
                |||> AVal.map3 (fun eff comm swap -> if swap then comm else eff)
            // Inactive meshes still render (as ghost); gate only on load state
            // and fusion mode (the composite replaces the normal pass).
            let renderEnabled =
                (loaded.fvc, model.FusionMode) ||> AVal.map2 (fun c f -> c > 3 && not f)
            let meshColor =
                meshIndices |> AVal.map (fun m ->
                    let i = Map.tryFind name m |> Option.defaultValue 0
                    V4f palette.[i % palette.Length])
            // A2: per-vertex signed distance for this mesh (None unless soloed/
            // encoded). Projected early so a refetch doesn't churn other meshes.
            let myDist = model.SurfaceDistance |> AVal.map (Map.tryFind name)
            let distBuf =
                (myDist, loaded.pos) ||> AVal.map2 (fun d _pos ->
                    match d with
                    | Some arr -> ArrayBuffer arr :> IBuffer
                    | None ->
                        match loaded.mesh.Value with
                        | Some md -> ArrayBuffer (Array.zeroCreate<float32> md.positions.Length) :> IBuffer
                        | None -> ArrayBuffer [| 0.0f; 0.0f; 0.0f |] :> IBuffer)
            let distEncoding =
                (model.SurfaceDistOn, model.ChartStickyMesh, myDist) |||> AVal.map3 (fun on sticky d ->
                    if on && sticky = Some name && Option.isSome d then 1 else 0)
            // Saturated end of the diverging map: robust (95th pct |d|).
            let distScale =
                myDist |> AVal.map (function
                    | Some arr ->
                        let valid = arr |> Array.choose (fun x -> if abs x < 1e20f then Some (abs x) else None)
                        if valid.Length = 0 then 1.0f
                        else
                            Array.sortInPlace valid
                            max 1e-3f valid.[int (0.95 * float (valid.Length - 1))]
                    | None -> 1.0f)
            // Detection limit (neutral mid) from the selected pin's probe:
            // 1.96·√(σ_ref² + σ_mesh²); 0 → no neutral band.
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
                            let refStd = stdOf r.ReferenceMesh
                            let mStd = stdOf name
                            1.96 * sqrt (refStd*refStd + mStd*mStd) |> float32
                        | _ -> 0.0f
                    | None -> 0.0f)
            // A3 range brush for the encoded mesh: (on, lo, hi).
            let distBrush =
                (model.SurfaceDistBrush, model.SurfaceDistOn, model.ChartStickyMesh)
                |||> AVal.map3 (fun b on sticky ->
                    match b with
                    | Some (lo, hi) when on && sticky = Some name -> 1, float32 lo, float32 hi
                    | _ -> 0, 0.0f, 0.0f)
            let meshDatasetErr =
                (model.MeshSensorTypes, model.MeshDatasetErrors)
                ||> AVal.map2 (fun sensors overrides ->
                    Provenance.datasetError overrides sensors name |> float32)
            let meshAlgoRes =
                model.MeshAlgorithmResidual
                |> AVal.map (fun m ->
                    Map.tryFind name m |> Option.defaultValue 0.0 |> float32)
            // Diff-mode inputs: per-mesh algo residual before/after the pending
            // solve + the inverse preview delta (preview-pose fragment →
            // committed-pose). No pending delta → diffs to zero (masked).
            let diffData =
                (model.PendingReg, model.MeshAlgorithmResidual) ||> AVal.map2 (fun pending algo ->
                    let before = Map.tryFind name algo |> Option.defaultValue 0.0 |> float32
                    match pending |> Option.bind (fun pr -> Map.tryFind name pr.Results) with
                    | Some r -> float32 r.RmsAfter, before, M44f r.Delta.Backward
                    | None -> before, before, M44f.Identity)
            // "All meshes the plane intersects": the cursor effect activates
            // only on meshes whose registered-world bbox touches the highlight
            // slab — and, when clipped, the cylinder's bounding sphere.
            // World-metric math; conservative on rotation.
            let cursorActive =
                AVal.custom (fun t ->
                    match cursor.GetValue t with
                    | None -> 0
                    | Some c ->
                        match Map.tryFind name (model.MeshBounds.GetValue t) with
                        | None -> 1
                        | Some box ->
                            let tw =
                                let committed =
                                    Map.tryFind name (model.MeshTransforms.GetValue t)
                                    |> Option.defaultValue Trafo3d.Identity
                                let eff =
                                    match PendingRegistration.delta name (model.PendingReg.GetValue t) with
                                    | Some d -> RegLog.effective committed d
                                    | None -> committed
                                RigidTransform.renderToWorld
                                    (DatasetScale.forMesh (model.DatasetScales.GetValue t) name)
                                    (model.CommonCentroid.GetValue t) eff
                            let mutable dMin = infinity
                            let mutable dMax = -infinity
                            let mutable bMin = V3d(infinity, infinity, infinity)
                            let mutable bMax = V3d(-infinity, -infinity, -infinity)
                            for ix in 0 .. 1 do
                                for iy in 0 .. 1 do
                                    for iz in 0 .. 1 do
                                        let corner =
                                            V3d((if ix = 0 then box.Min.X else box.Max.X),
                                                (if iy = 0 then box.Min.Y else box.Max.Y),
                                                (if iz = 0 then box.Min.Z else box.Max.Z))
                                        let p = tw.Forward.TransformPos corner
                                        let d = Vec.dot (p - c.Origin) c.Normal
                                        if d < dMin then dMin <- d
                                        if d > dMax then dMax <- d
                                        bMin <- V3d(min bMin.X p.X, min bMin.Y p.Y, min bMin.Z p.Z)
                                        bMax <- V3d(max bMax.X p.X, max bMax.Y p.Y, max bMax.Z p.Z)
                            let slabHit = dMin <= cursorHighlightWidth && dMax >= -cursorHighlightWidth
                            let cylHit =
                                if not c.Clip then true
                                else
                                    let q = c.PinCentre
                                    let cl =
                                        V3d(clamp bMin.X bMax.X q.X,
                                            clamp bMin.Y bMax.Y q.Y,
                                            clamp bMin.Z bMax.Z q.Z)
                                    let bound = sqrt (c.PinRadius * c.PinRadius + 0.25 * c.CylLength * c.CylLength)
                                    (cl - q).Length <= bound
                            if slabHit && cylHit then 1 else 0)
            let surface =
                sg {
                    Sg.Active renderEnabled
                    Sg.Trafo (meshTrafo model.CommonCentroid loaded scale meshT)
                    Sg.Shader {
                        DefaultSurfaces.trafo
                        DefaultSurfaces.diffuseTexture
                        MeshShader.shade
                    }
                    Sg.Uniform("DiffuseColorTexture", loaded.tex)
                    Sg.Uniform("MeshActive",      isActive)
                    // GhostSilhouette off → 0 → ghost path discards. Anchor-pick
                    // wins over chart-column highlight; both are explicit
                    // gestures with fixed alphas, independent of the toggle.
                    Sg.Uniform("GhostOpacity",
                        AVal.custom (fun t ->
                            match model.AnchorPick.GetValue t with
                            | Some pick when pick.Mesh <> name ->
                                if (model.Registration.GetValue t).ReferenceMesh = Some name
                                then 0.3f else 0.08f
                            | _ ->
                                match peekTarget.GetValue t with
                                | Some target when target <> name -> 0.12f
                                | _ ->
                                match wheelIsolation.GetValue t with
                                | Some iso when iso <> name -> 0.15f
                                | _ ->
                                    match chartHighlight.GetValue t with
                                    | Some hm when hm <> name -> 0.2f
                                    | _ ->
                                        if model.GhostSilhouette.GetValue t
                                        then float32 (model.GhostOpacity.GetValue t)
                                        else 0.0f))
                    Sg.Uniform("RenderingMode",   renderingModeInt)
                    Sg.Uniform("MeshColor",       meshColor)
                    Sg.Uniform("ShadingStrength", model.ShadingStrength |> AVal.map float32)
                    Sg.Uniform("SlopeThreshold",
                        model.SlopeThresholdDeg |> AVal.map (fun d ->
                            sin (d * System.Math.PI / 180.0) |> float32))
                    Sg.Uniform("LassoPlaneCount", lassoPlaneCount)
                    Sg.Uniform("LassoPlanes",     lassoPlanes)
                    Sg.Uniform("BlobCount",       blobCount)
                    Sg.Uniform("Blobs",           blobs)
                    Sg.Uniform("AnchorGhost",     anchorGhost)
                    Sg.Uniform("HeatmapMode",       heatmapModeInt)
                    Sg.Uniform("ProvThreshold",     provThreshold)
                    Sg.Uniform("MeshDatasetError",  meshDatasetErr)
                    Sg.Uniform("MeshAlgoResidual",  meshAlgoRes)
                    Sg.Uniform("DiffAlgoAfter",     diffData |> AVal.map (fun (a, _, _) -> a))
                    Sg.Uniform("DiffAlgoBefore",    diffData |> AVal.map (fun (_, b, _) -> b))
                    Sg.Uniform("DiffInvDelta",      diffData |> AVal.map (fun (_, _, m) -> m))
                    Sg.Uniform("DiffSigmaRef",      diffSigmaRef)
                    Sg.Uniform("CursorActive",         cursorActive)
                    Sg.Uniform("CursorPlaneOrigin",    cursorOrigin)
                    Sg.Uniform("CursorPlaneNormal",    cursorNormal)
                    Sg.Uniform("CursorHighlightWidth", cursorWidth)
                    Sg.Uniform("CursorDarken",         AVal.constant cursorDarken)
                    Sg.Uniform("CursorClip",           cursorClip)
                    Sg.Uniform("CursorPinCentre",      cursorPinC)
                    Sg.Uniform("CursorPinRadius",      cursorPinR)
                    Sg.Uniform("CursorCylLength",      cursorCylLen)
                    Sg.Uniform("ClipPlaneCount",       clipCount)
                    Sg.Uniform("ClipPlane0",           clipPlane0)
                    Sg.Uniform("ClipPlane1",           clipPlane1)
                    Sg.Uniform("DistanceEncoding",     distEncoding)
                    Sg.Uniform("DistLoD",              distLoD)
                    Sg.Uniform("DistScale",            distScale)
                    Sg.Uniform("DistBrushOn",          distBrush |> AVal.map (fun (o, _, _) -> o))
                    Sg.Uniform("DistBrushLo",          distBrush |> AVal.map (fun (_, l, _) -> l))
                    Sg.Uniform("DistBrushHi",          distBrush |> AVal.map (fun (_, _, h) -> h))
                    Sg.VertexAttributes(
                        HashMap.ofList [
                            string DefaultSemantic.Positions,               BufferView(loaded.pos, typeof<V3f>)
                            string DefaultSemantic.DiffuseColorCoordinates, BufferView(loaded.tc,  typeof<V2f>)
                            string DefaultSemantic.Normals,                 BufferView(loaded.nrm, typeof<V3f>)
                            "SurfaceDist",                                  BufferView(distBuf, typeof<float32>)
                        ]
                    )
                    Sg.Index(BufferView(loaded.idx, typeof<int>))
                    Sg.Render loaded.fvc
                }
            // While this mesh has a pending preview delta, also show its
            // committed pose via the uniform-ghost path in a slate tint. Ghost
            // fragments write far depth so picks pass through to the preview.
            let ghostActive =
                (renderEnabled, model.PendingReg, previewSwap) |||> AVal.map3 (fun r pending swap ->
                    r && (not swap) && (PendingRegistration.delta name pending |> Option.isSome))
            let committedGhost =
                sg {
                    Sg.Active ghostActive
                    Sg.Trafo (meshTrafo model.CommonCentroid loaded scale (committedMeshT model name))
                    Sg.Shader {
                        DefaultSurfaces.trafo
                        DefaultSurfaces.diffuseTexture
                        MeshShader.shade
                    }
                    Sg.NoEvents
                    Sg.Uniform("DiffuseColorTexture", loaded.tex)
                    Sg.Uniform("MeshActive",      AVal.constant false)
                    // Slate committed-pose ghost obeys the opacity slider too
                    // (its slate MeshColor distinguishes it, not a fixed alpha).
                    Sg.Uniform("GhostOpacity",    model.GhostOpacity |> AVal.map float32)
                    Sg.Uniform("RenderingMode",   AVal.constant 1)
                    Sg.Uniform("MeshColor",       AVal.constant (V4f(0.45f, 0.49f, 0.55f, 1.0f)))
                    Sg.Uniform("ShadingStrength", AVal.constant 0.0f)
                    Sg.Uniform("SlopeThreshold",  AVal.constant 0.5f)
                    Sg.Uniform("LassoPlaneCount", AVal.constant 0)
                    Sg.Uniform("LassoPlanes",     lassoPlanes)
                    Sg.Uniform("BlobCount",       AVal.constant 0)
                    Sg.Uniform("Blobs",           blobs)
                    Sg.Uniform("AnchorGhost",     AVal.constant 0)
                    Sg.Uniform("HeatmapMode",       AVal.constant 0)
                    Sg.Uniform("ProvThreshold",     AVal.constant 1.0f)
                    Sg.Uniform("MeshDatasetError",  AVal.constant 0.0f)
                    Sg.Uniform("MeshAlgoResidual",  AVal.constant 0.0f)
                    Sg.Uniform("DiffAlgoAfter",     AVal.constant 0.0f)
                    Sg.Uniform("DiffAlgoBefore",    AVal.constant 0.0f)
                    Sg.Uniform("DiffInvDelta",      AVal.constant M44f.Identity)
                    Sg.Uniform("DiffSigmaRef",      AVal.constant 0.0f)
                    Sg.Uniform("CursorActive",         AVal.constant 0)
                    Sg.Uniform("CursorPlaneOrigin",    cursorOrigin)
                    Sg.Uniform("CursorPlaneNormal",    cursorNormal)
                    Sg.Uniform("CursorHighlightWidth", cursorWidth)
                    Sg.Uniform("CursorDarken",         AVal.constant cursorDarken)
                    Sg.Uniform("CursorClip",           cursorClip)
                    Sg.Uniform("CursorPinCentre",      cursorPinC)
                    Sg.Uniform("CursorPinRadius",      cursorPinR)
                    Sg.Uniform("CursorCylLength",      cursorCylLen)
                    Sg.Uniform("ClipPlaneCount",       clipCount)
                    Sg.Uniform("ClipPlane0",           clipPlane0)
                    Sg.Uniform("ClipPlane1",           clipPlane1)
                    Sg.Uniform("DistanceEncoding",     AVal.constant 0)
                    Sg.Uniform("DistLoD",              AVal.constant 0.0f)
                    Sg.Uniform("DistScale",            AVal.constant 1.0f)
                    Sg.Uniform("DistBrushOn",          AVal.constant 0)
                    Sg.Uniform("DistBrushLo",          AVal.constant 0.0f)
                    Sg.Uniform("DistBrushHi",          AVal.constant 0.0f)
                    Sg.VertexAttributes(
                        HashMap.ofList [
                            string DefaultSemantic.Positions,               BufferView(loaded.pos, typeof<V3f>)
                            string DefaultSemantic.DiffuseColorCoordinates, BufferView(loaded.tc,  typeof<V2f>)
                            string DefaultSemantic.Normals,                 BufferView(loaded.nrm, typeof<V3f>)
                            "SurfaceDist",                                  BufferView(distBuf, typeof<float32>)
                        ]
                    )
                    Sg.Index(BufferView(loaded.idx, typeof<int>))
                    Sg.Render loaded.fvc
                }
            sg { surface; committedGhost }
        ) |> AList.toASet

    let buildFusionNode (model : AdaptiveModel) (view : aval<Trafo3d>) (proj : aval<Trafo3d>) : ISceneNode =
        let blobCount, blobs = pinBlobUniforms model
        // Before registration the meshes aren't aligned, so fusing is
        // meaningless: show only the reference mesh.
        let hasRegistered =
            model.MeshTransforms |> AVal.map (fun m -> not (Map.isEmpty m))
        let refMesh =
            model.Registration |> AVal.map (fun r -> r.ReferenceMesh)
        let nodes =
            model.MeshNames |> AList.map (fun name ->
                let loaded = loadMeshAsync (fun () -> ()) name
                let isActive =
                    model.MeshVisible |> AVal.map (fun m -> Map.tryFind name m |> Option.defaultValue true)
                let scale = scaleFor model name
                let meshT = effectiveMeshT model name
                let regGate =
                    (hasRegistered, refMesh) ||> AVal.map2 (fun reg rm ->
                        match rm with
                        | Some r -> reg || r = name
                        | None   -> true)
                let renderEnabled =
                    (loaded.fvc, isActive, regGate) |||> AVal.map3 (fun c a g -> c > 3 && a && g)
                let meshDatasetErr =
                    (model.MeshSensorTypes, model.MeshDatasetErrors)
                    ||> AVal.map2 (fun sensors overrides ->
                        Provenance.datasetError overrides sensors name |> float32)
                let meshAlgoRes =
                    model.MeshAlgorithmResidual
                    |> AVal.map (fun m -> Map.tryFind name m |> Option.defaultValue 0.0 |> float32)
                sg {
                    Sg.Active renderEnabled
                    Sg.Trafo (meshTrafo model.CommonCentroid loaded scale meshT)
                    Sg.Shader {
                        DefaultSurfaces.trafo
                        DefaultSurfaces.diffuseTexture
                        FusionShader.shade
                    }
                    Sg.Uniform("DiffuseColorTexture", loaded.tex)
                    Sg.Uniform("MeshDatasetError", meshDatasetErr)
                    Sg.Uniform("MeshAlgoResidual", meshAlgoRes)
                    Sg.VertexAttributes(
                        HashMap.ofList [
                            string DefaultSemantic.Positions,               BufferView(loaded.pos, typeof<V3f>)
                            string DefaultSemantic.DiffuseColorCoordinates, BufferView(loaded.tc,  typeof<V2f>)
                            string DefaultSemantic.Normals,                 BufferView(loaded.nrm, typeof<V3f>)
                        ]
                    )
                    Sg.Index(BufferView(loaded.idx, typeof<int>))
                    Sg.Render loaded.fvc
                }
            ) |> AList.toASet
        sg {
            Sg.View view
            Sg.Proj proj
            Sg.Uniform("BlobCount",    blobCount)
            Sg.Uniform("Blobs",        blobs)
            nodes
        }

    // useTransforms = false renders the reference state (identity transforms,
    // all visible) for Photo mode; true renders the live state for Render mode.
    let buildPanoramaNode (model : AdaptiveModel) (useTransforms : bool) (view : aval<Trafo3d>) (proj : aval<Trafo3d>) : ISceneNode =
        let nodes =
            model.MeshNames |> AList.map (fun name ->
                let loaded = loadMeshAsync (fun () -> ()) name
                let isActive =
                    if useTransforms then
                        model.MeshVisible |> AVal.map (fun m -> Map.tryFind name m |> Option.defaultValue true)
                    else AVal.constant true
                let scale = scaleFor model name
                let meshT =
                    if useTransforms then effectiveMeshT model name
                    else AVal.constant Trafo3d.Identity
                let renderEnabled =
                    (loaded.fvc, isActive) ||> AVal.map2 (fun c a -> c > 3 && a)
                sg {
                    Sg.Active renderEnabled
                    Sg.Trafo (meshTrafo model.CommonCentroid loaded scale meshT)
                    Sg.Shader {
                        DefaultSurfaces.trafo
                        DefaultSurfaces.diffuseTexture
                        PanoramaShader.shade
                    }
                    Sg.Uniform("DiffuseColorTexture", loaded.tex)
                    Sg.VertexAttributes(
                        HashMap.ofList [
                            string DefaultSemantic.Positions,               BufferView(loaded.pos, typeof<V3f>)
                            string DefaultSemantic.DiffuseColorCoordinates, BufferView(loaded.tc,  typeof<V2f>)
                            string DefaultSemantic.Normals,                 BufferView(loaded.nrm, typeof<V3f>)
                        ]
                    )
                    Sg.Index(BufferView(loaded.idx, typeof<int>))
                    Sg.Render loaded.fvc
                }
            ) |> AList.toASet
        sg {
            Sg.View view
            Sg.Proj proj
            nodes
        }
