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

    // Per-pixel opacity for a mesh:
    //   • If MeshActive is false the whole mesh renders as a faint ghost at
    //     uniform.GhostOpacity.
    //   • If MeshActive is true a mask is computed as the union (max) of:
    //       – lasso polygon: 1.0 inside the world-space half-space frustum, 0.0
    //         outside; if no lasso is defined this contributes 1.0 unrestricted.
    //       – scanpin blobs: Gaussian falloff exp(-d²/(2σ²)) per pin, max across
    //         all pins; if no blobs this contributes 0.0.
    //     The final alpha is lerp(GhostOpacity, 1.0, mask) — so inside the
    //     mask the mesh is fully opaque and outside it fades down to the
    //     ghost level. With no lasso and no blobs the mask is 1.0 everywhere
    //     and the mesh is fully opaque.
    //
    // Depth output is α-gated: fragments with α ≥ 0.99 write their natural
    // depth (gl_FragCoord.z) so they occlude things behind them and so the
    // depth-buffer pixel-picker can resolve a world position; fragments with
    // α < 0.99 write 1.0 (far plane) so they never occlude anything and so
    // opaque fragments anywhere in the scene overdraw them.
    [<Literal>]
    let MaxLassoPlanes = 32

    [<Literal>]
    let MaxBlobs = 32

    [<Literal>]
    let opaqueThreshold = 0.99f

    type UniformScope with
        member x.MeshActive      : bool    = x?MeshActive
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

    type FragIn = {
        [<Color>]                              c  : V4f
        [<Semantic("Normals")>]                n  : V3f
        [<Semantic("WorldPosition")>]          wp : V4f
        [<FragCoord>]                          fc : V4f
    }

    type FragOut = {
        [<Color>] color : V4f
        [<Depth>] depth : float32
    }

    let shade (v : FragIn) =
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
            let ghost = uniform.GhostOpacity
            let mutable alpha = 0.0f
            if uniform.MeshActive then
                // Inside mask → fully opaque, outside → ghost level.
                alpha <- ghost + (1.0f - ghost) * maskFactor
            else
                alpha <- ghost
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
                    let fadeW = max 0.05f ((1.0f - tT) * 0.5f)
                    let t = clamp 0.0f 1.0f ((nz - tT) / fadeW)
                    let s = t * t * (3.0f - 2.0f * t)
                    blueCol * (1.0f - s) + whiteCol * s
                else
                    let t = clamp 0.0f 1.0f ((tT - nz) / tT)
                    let s = t * t * (3.0f - 2.0f * t)
                    blueCol * (1.0f - s) + hotCol * s
            let baseRgb =
                if uniform.RenderingMode = 1 then uniform.MeshColor.XYZ
                elif uniform.RenderingMode = 2 then slopeCol
                else v.c.XYZ
            let depth =
                if alpha >= opaqueThreshold then v.fc.Z
                else 1.0f
            return {
                color = V4f(baseRgb * shade, alpha)
                depth = depth
            }
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
        (base_, meshTransform) ||> AVal.map2 (fun b t -> t * b)

    let private scaleFor (model : AdaptiveModel) (name : string) =
        let dataset = name.Split('/', 2).[0]
        model.DatasetScales |> AVal.map (fun m -> Map.tryFind dataset m |> Option.defaultValue 1.0)

    // Forward mesh scene: one draw call per mesh, plain alpha blending plus
    // shader-driven custom depth (α-gated). Every mesh is always rendered —
    // active meshes resolve their alpha from the lasso/blob rules, inactive
    // meshes show as a faint ghost at GhostOpacity.
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
            // Inactive meshes still render as ghost outline, so the only reason
            // to gate Sg.Active is the load-not-yet-arrived case (fvc <= 3).
            let renderEnabled =
                loaded.fvc |> AVal.map (fun c -> c > 3)
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
                }
                Sg.Uniform("DiffuseColorTexture", loaded.tex)
                Sg.Uniform("MeshActive",      isActive)
                // GhostSilhouette gates the ghost — when off, push 0 so the
                // shader's inactive-mesh path discards (alpha < 1e-4).
                Sg.Uniform("GhostOpacity",
                    (model.GhostSilhouette, model.GhostOpacity)
                    ||> AVal.map2 (fun on op -> if on then float32 op else 0.0f))
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


