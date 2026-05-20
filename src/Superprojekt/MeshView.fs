namespace Superprojekt

open Aardvark.Base
open Aardworx.WebAssembly
open Aardvark.Rendering
open FSharp.Data.Adaptive
open Aardvark.Dom
open FShade

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

module RenderPass =
    let passMinusOne = RenderPass.main
    let passZero = RenderPass.after "zero" RenderPassOrder.Arbitrary passMinusOne
    let passOne = RenderPass.after "one" RenderPassOrder.Arbitrary passZero
    let passTwo = RenderPass.after "two" RenderPassOrder.Arbitrary passOne

[<ReflectedDefinition>]
module MeshShader =
    open FShade

    // Per-pixel opacity is the union (max) of independent "on-sources":
    //   • mesh-on toggle (uniform across the mesh)
    //   • lasso polygon — point inside the world-space frustum defined by
    //     the polygon's silhouette planes
    //   • scanpin blobs — Gaussian falloff exp(-d²/(2σ²)) per pin, taking the
    //     max across all pins
    // The hard cap on counts must match the Sg.Uniform packing below.
    [<Literal>]
    let MaxLassoPlanes = 32

    [<Literal>]
    let MaxBlobs = 32

    type UniformScope with
        member x.MeshActive      : bool    = x?MeshActive
        member x.GhostSilhouette : bool    = x?GhostSilhouette
        member x.GhostOpacity    : float32 = x?GhostOpacity
        // 0 = Textured (sample atlas), 1 = Shaded (per-mesh palette colour),
        // 2 = SlopeColor (colour by angle of surface normal to horizontal).
        member x.RenderingMode   : int     = x?RenderingMode
        member x.MeshColor       : V4f     = x?MeshColor
        // 0 = flat (full base colour, no headlight), 1 = full headlight falloff.
        member x.ShadingStrength : float32 = x?ShadingStrength
        // Slope threshold for SlopeColor mode, expressed as sin(angle):
        //   the verticality |n.Z| at which the blue band sits. Default sin(30°) = 0.5.
        member x.SlopeThreshold  : float32 = x?SlopeThreshold
        // Lasso: outward-facing half-space planes packed as V4f(nx,ny,nz,d);
        // a point p is inside iff dot(plane.xyz, p) + plane.w <= 0 for ALL i in [0, count).
        // count = 0 means "no lasso defined" — contributes nothing.
        member x.LassoPlaneCount : int     = x?LassoPlaneCount
        member x.LassoPlanes     : Arr<N<32>, V4f> = x?LassoPlanes
        // Pin blobs: world-space anchor + Gaussian sigma packed as V4f(cx,cy,cz,σ);
        // count = 0 means "no blobs" — contributes nothing.
        member x.BlobCount       : int     = x?BlobCount
        member x.Blobs           : Arr<N<32>, V4f> = x?Blobs

    let shade (v : Effects.Vertex) =
        fragment {
            let wp = v.wp.XYZ
            // Lasso half-space test. Inside iff dot(plane.xyz, wp) + plane.w <= 0
            // for all active planes. Default 1.0 (no lasso → no restriction).
            let mutable lassoMask = 1.0f
            let lc = uniform.LassoPlaneCount
            if lc > 0 then
                let mutable inside = true
                for i in 0 .. MaxLassoPlanes - 1 do
                    if i < lc then
                        let pl = uniform.LassoPlanes.[i]
                        let d = pl.X * wp.X + pl.Y * wp.Y + pl.Z * wp.Z + pl.W
                        if d > 0.0f then inside <- false
                lassoMask <- if inside then 1.0f else 0.0f
            // Gaussian blob max — soft per-pixel "on" sources, taken in the union.
            let mutable blobMax = 0.0f
            let bc = uniform.BlobCount
            if bc > 0 then
                for i in 0 .. MaxBlobs - 1 do
                    if i < bc then
                        let b = uniform.Blobs.[i]
                        let sigma = b.W
                        if sigma > 1e-6f then
                            let dx = wp.X - b.X
                            let dy = wp.Y - b.Y
                            let dz = wp.Z - b.Z
                            let d2 = dx*dx + dy*dy + dz*dz
                            let w = exp (-d2 / (2.0f * sigma * sigma))
                            if w > blobMax then blobMax <- w
            // Combine into the per-pixel mask in [0,1]:
            //   nothing active → 1.0 (no restriction)
            //   only lasso     → lassoMask (binary)
            //   only blobs     → blobMax (smooth)
            //   both           → max(lassoMask, blobMax) — union of opaque regions
            let lassoActive = lc > 0
            let blobsActive = bc > 0
            let maskFactor =
                if lassoActive && blobsActive then max lassoMask blobMax
                elif lassoActive then lassoMask
                elif blobsActive then blobMax
                else 1.0f
            // Master gate is the mesh on/off toggle. The lasso/blob mask only
            // further restricts ON meshes — it never lights up OFF meshes.
            let mutable alpha = 0.0f
            if uniform.MeshActive then
                if uniform.GhostSilhouette then
                    // Lerp from ghost → fully opaque by the mask. Inside the mask
                    // alpha = 1.0; outside the mask alpha = GhostOpacity (visible
                    // ghost). Blobs give a smooth ghost→opaque transition.
                    alpha <- uniform.GhostOpacity + (1.0f - uniform.GhostOpacity) * maskFactor
                else
                    if maskFactor < 0.05f then discard()
                    alpha <- maskFactor
            elif uniform.GhostSilhouette then
                alpha <- uniform.GhostOpacity
            else
                discard()
            if alpha < 1e-4f then discard()
            let n = v.n |> Vec.normalize
            let toCam = (uniform.CameraLocation - v.wp.XYZ) |> Vec.normalize
            let ndl = max 0.15f (abs (Vec.dot n toCam))
            let s = clamp 0.0f 1.0f uniform.ShadingStrength
            let shade = 1.0f + (ndl - 1.0f) * s
            // Slope shading (mode 2): use the world-space verticality |n.Z|.
            //   nz > T   → white, big tolerance (T = sin(threshold°))
            //   nz ≈ T   → blue (the "threshold band")
            //   nz ≈ 0.0 → hot warm-white (vertical walls)
            let nz = abs n.Z
            let whiteCol = V3f(1.0f, 1.0f, 1.0f)
            let blueCol  = V3f(0.22f, 0.45f, 0.95f)
            let hotCol   = V3f(1.0f, 0.85f, 0.55f)
            let tT = clamp 0.01f 0.99f uniform.SlopeThreshold
            let slopeCol =
                if nz > tT then
                    // upper hemisphere: blue → white over half the gap to vertical.
                    let fadeW = max 0.05f ((1.0f - tT) * 0.5f)
                    let t = clamp 0.0f 1.0f ((nz - tT) / fadeW)
                    let s = t * t * (3.0f - 2.0f * t)
                    blueCol * (1.0f - s) + whiteCol * s
                else
                    // lower hemisphere: blue at threshold, ramping up to hot at horizontal.
                    let t = clamp 0.0f 1.0f ((tT - nz) / tT)
                    let s = t * t * (3.0f - 2.0f * t)
                    blueCol * (1.0f - s) + hotCol * s
            let baseRgb =
                if uniform.RenderingMode = 1 then uniform.MeshColor.XYZ
                elif uniform.RenderingMode = 2 then slopeCol
                else v.c.XYZ
            return V4f(baseRgb * shade, alpha)
        }

    // Depth-only shader for the pre-pass; the FBO has no color attachment so
    // the fragment output is discarded.
    let depthOnly (_v : Effects.Vertex) =
        fragment { return V4f.IIII }

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
        (base_, meshTransform) ||> AVal.map2 (fun b t -> t * b)

    let private scaleFor (model : AdaptiveModel) (name : string) =
        let dataset = name.Split('/', 2).[0]
        model.DatasetScales |> AVal.map (fun m -> Map.tryFind dataset m |> Option.defaultValue 1.0)

    // Main OIT-targeted scene: every loaded mesh as a draw call, all visible (so the
    // ghost path can still produce fragments). Alpha gating happens in the shader.
    let buildScene (loadFinished : string -> unit) (model : AdaptiveModel) : aset<ISceneNode> =
        let renderingModeInt =
            model.RenderingMode |> AVal.map (function
                | Textured     -> 0
                | Shaded       -> 1
                | SlopeColor   -> 2)
        let meshIndices =
            model.MeshNames |> AList.toAVal |> AVal.map (fun names ->
                names |> Seq.mapi (fun i n -> n, i) |> Map.ofSeq)
        let palette = Primitives.meshPaletteV4d

        // ---- Lasso uniforms: count + 32-slot V4f array of half-space planes.
        let lassoPlaneCount =
            model.LassoVolume |> AVal.map (function
                | Some v -> min v.Planes.Length MeshShader.MaxLassoPlanes
                | None   -> 0)
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

        // ---- Blob uniforms: each pin packed as V4f(cx,cy,cz,sigma). Pin centres
        // are stored in render space (post-meshTrafo), matching v.wp.XYZ in the
        // mesh shader. Hard-capped at MeshShader.MaxBlobs.
        let blobsArr =
            model.ScanPins.Pins |> AMap.toAVal |> AVal.map (fun pinsMap ->
                let pins = HashMap.toArray pinsMap |> Array.map snd
                let n = min pins.Length MeshShader.MaxBlobs
                let arr = Array.zeroCreate<V4f> MeshShader.MaxBlobs
                for i in 0 .. n - 1 do
                    let p = pins.[i]
                    arr.[i] <- V4f(float32 p.Centre.X, float32 p.Centre.Y, float32 p.Centre.Z, float32 p.Sigma)
                n, arr)
        let blobCount = blobsArr |> AVal.map fst
        let blobs     = blobsArr |> AVal.map snd
        model.MeshNames |> AList.map (fun name ->
            let loaded = loadMeshAsync (fun () -> loadFinished name) name
            let isActive =
                model.MeshVisible |> AVal.map (fun m -> Map.tryFind name m |> Option.defaultValue true)
            let scale = scaleFor model name
            let meshT =
                model.MeshTransforms |> AVal.map (fun m ->
                    Map.tryFind name m |> Option.defaultValue Trafo3d.Identity)
            // Active in OIT pass when enabled, OR when ghost mode would show it.
            // The lasso / blob mask only attenuates ON-meshes; it does NOT
            // unlock OFF-meshes, so toggled-off meshes don't need force-rendering.
            let renderEnabled =
                (isActive, model.GhostSilhouette, loaded.fvc) |||> AVal.map3 (fun a g c ->
                    (a || g) && c > 3)
            let meshColor =
                meshIndices |> AVal.map (fun m ->
                    let i = Map.tryFind name m |> Option.defaultValue 0
                    V4f palette.[i % palette.Length])
            sg {
                Sg.Active renderEnabled
                Sg.Trafo (meshTrafo model.CommonCentroid loaded scale meshT)
                Sg.Shader {
                    DefaultSurfaces.trafo
                    DefaultSurfaces.diffuseTexture
                    MeshShader.shade
                    OIT.weightedBlend
                }
                Sg.Uniform("DiffuseColorTexture", loaded.tex)
                Sg.Uniform("MeshActive",      isActive)
                Sg.Uniform("GhostSilhouette", model.GhostSilhouette)
                Sg.Uniform("GhostOpacity",    model.GhostOpacity |> AVal.map float32)
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

    // Depth pre-pass: only fully-opaque meshes (i.e. enabled), trafo-only shader,
    // depth-write on. Populates the shared depth attachment that the OIT pass
    // then depth-tests against (read-only).
    let buildOpaqueDepthScene (loadFinished : string -> unit) (model : AdaptiveModel) : aset<ISceneNode> =
        model.MeshNames |> AList.map (fun name ->
            let loaded = loadMeshAsync (fun () -> loadFinished name) name
            let isActive =
                model.MeshVisible |> AVal.map (fun m -> Map.tryFind name m |> Option.defaultValue true)
            let scale = scaleFor model name
            let meshT =
                model.MeshTransforms |> AVal.map (fun m ->
                    Map.tryFind name m |> Option.defaultValue Trafo3d.Identity)
            let renderEnabled =
                (isActive, loaded.fvc) ||> AVal.map2 (fun a c -> a && c > 3)
            sg {
                Sg.Active renderEnabled
                Sg.Trafo (meshTrafo model.CommonCentroid loaded scale meshT)
                Sg.Shader { DefaultSurfaces.trafo; MeshShader.depthOnly }
                Sg.VertexAttributes(
                    HashMap.ofList [
                        string DefaultSemantic.Positions, BufferView(loaded.pos, typeof<V3f>)
                    ]
                )
                Sg.Index(BufferView(loaded.idx, typeof<int>))
                Sg.Render loaded.fvc
            }
        ) |> AList.toASet

