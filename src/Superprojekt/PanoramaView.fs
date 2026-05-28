namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open Aardworx.WebAssembly
open FSharp.Data.Adaptive
open Aardvark.Dom
open FShade

// Cylindrical reprojection of a captured cubemap onto a fullscreen quad.
// Two cubes are sampled — Photo (reference state) and Render (live state) —
// and blended by PanoBlend so the panel can show either or a disagreement mix.
// The vertical axis is Z (this app's up); horizontal wraps a full 360°.
[<ReflectedDefinition>]
module PanoReproject =
    open FShade

    let private photoCube =
        samplerCube {
            texture uniform?PhotoCube
            filter Filter.MinMagLinear
            addressU WrapMode.Wrap
            addressV WrapMode.Clamp
        }
    let private renderCube =
        samplerCube {
            texture uniform?RenderCube
            filter Filter.MinMagLinear
            addressU WrapMode.Wrap
            addressV WrapMode.Clamp
        }

    type Vtx = {
        [<Position>]            pos : V4f
        [<Semantic("PanoNdc")>] ndc : V2f
    }

    // All-float32: WebGL2 (ESSL3) has no double precision, so every value here
    // must stay float32 — using F# `float`, `Constant.Pi` or `V3d` makes FShade
    // emit `double`/`dvec3` and the shader fails to compile.
    type UniformScope with
        member x.PanoYaw    : float32 = x?PanoYaw
        member x.PanoVScale : float32 = x?PanoVScale
        member x.PanoBlend  : float32 = x?PanoBlend

    [<Literal>]
    let private piF = 3.1415927f

    let vertex (v : Vtx) =
        vertex { return { v with ndc = V2f(v.pos.X, v.pos.Y) } }

    let fragment (v : Vtx) =
        fragment {
            let phi = v.ndc.X * piF + uniform.PanoYaw
            // Cylindrical: the vertical screen coordinate maps to a height on the
            // unit cylinder, so vertical world lines stay straight (no pole
            // pinching as in an equirectangular map).
            let h   = v.ndc.Y * uniform.PanoVScale
            let dir = V3f(cos phi, sin phi, h) |> Vec.normalize
            let a = photoCube.SampleLevel(dir, 0.0f)
            let b = renderCube.SampleLevel(dir, 0.0f)
            let t = uniform.PanoBlend
            return a * (1.0f - t) + b * t
        }

module PanoramaView =

    let private quadPos =
        [| V3f(-1.0f, -1.0f, 0.0f); V3f(1.0f, -1.0f, 0.0f)
           V3f( 1.0f,  1.0f, 0.0f); V3f(-1.0f, 1.0f, 0.0f) |]
    let private quadIdx = [| 0; 1; 2; 0; 2; 3 |]

    // Six 90°-FOV cube-face projections, in the face order panorama.fs used so
    // the captured cube matches the samplerCube convention.
    let private faceProjs =
        let p = Frustum.perspective 90.0 0.1 10000.0 1.0 |> Frustum.projTrafo
        [| p
           Trafo3d.RotationY(Constant.Pi) * p
           Trafo3d.RotationX(Constant.PiHalf)  * Trafo3d.RotationZ(-Constant.PiHalf) * p
           Trafo3d.RotationX(-Constant.PiHalf) * Trafo3d.RotationZ( Constant.PiHalf) * p
           Trafo3d.RotationY(-Constant.PiHalf) * p
           Trafo3d.RotationY( Constant.PiHalf) * p |]

    [<Literal>]
    let private cubeSize = 1024

    // Builds the panel scene: render the meshes into two colour cubemaps (Photo
    // = reference state, Render = live state) from the selected panorama pose,
    // then a fullscreen quad reprojects them cylindrically. Lives in its own
    // renderControl, so info.Runtime is the panel's runtime.
    let build (info : Aardvark.Dom.RenderControlInfo) (model : AdaptiveModel) : aset<ISceneNode> =
        let runtime = info.Runtime

        let datasetScale =
            (model.ActiveDataset, model.DatasetScales) ||> AVal.map2 (fun dsOpt scales ->
                dsOpt |> Option.bind (fun ds -> Map.tryFind ds scales) |> Option.defaultValue 1.0)

        // Pose (world eye + yaw), falling back to the scene-bounds centre.
        let poseWorld =
            (model.Panoramas, model.SelectedPanorama, model.SceneBounds)
            |||> AVal.map3 (fun ps i sb ->
                match List.tryItem i ps with
                | Some p -> p.EyeWorld, p.Yaw
                | None ->
                    let c = if sb.IsValid then sb.Center + V3d(0.0, 0.0, 2.0) else V3d.Zero
                    c, 0.0)
        let eyeRender =
            (poseWorld, model.CommonCentroid, datasetScale)
            |||> AVal.map3 (fun (e, _) cc s -> (e - cc) * s)
        let yaw = poseWorld |> AVal.map (fun (_, y) -> float32 y)
        let view = eyeRender |> AVal.map (fun e -> Trafo3d.Translation(-e))

        let signature =
            runtime.CreateFramebufferSignature [
                DefaultSemantic.Colors,       TextureFormat.Rgba8
                DefaultSemantic.DepthStencil, TextureFormat.Depth24Stencil8
            ]

        let faceTask (useTransforms : bool) (proj : Trafo3d) : IRenderTask =
            let node = MeshView.buildPanoramaNode model useTransforms view (AVal.constant proj)
            let objs, _ = node.GetObjects(TraversalState.empty runtime)
            runtime.CompileRender(signature, objs)

        let clr = clear { color C4f.White; depth 1.0 }
        let photoCube =
            faceProjs |> Array.map (faceTask false) |> CubeMap
            |> RenderTask.renderToColorCubeMipWithUniformClear (AVal.constant cubeSize) clr
        let renderCube =
            faceProjs |> Array.map (faceTask true) |> CubeMap
            |> RenderTask.renderToColorCubeMipWithUniformClear (AVal.constant cubeSize) clr

        let panoBlend =
            (model.PanoramaMode, model.PanoramaBlend) ||> AVal.map2 (fun m b ->
                match m with
                | PanoPhoto  -> 0.0f
                | PanoRender -> 1.0f
                | PanoBlend  -> float32 b)

        let quad =
            sg {
                Sg.DepthTest (AVal.constant DepthTest.None)
                Sg.Shader {
                    PanoReproject.vertex
                    PanoReproject.fragment
                }
                Sg.Uniform("PhotoCube",  photoCube)
                Sg.Uniform("RenderCube", renderCube)
                Sg.Uniform("PanoYaw",    yaw)
                Sg.Uniform("PanoVScale", AVal.constant 1.0f)
                Sg.Uniform("PanoBlend",  panoBlend)
                Sg.VertexAttributes(
                    HashMap.ofList [
                        string DefaultSemantic.Positions,
                            BufferView(AVal.constant (ArrayBuffer quadPos :> IBuffer), typeof<V3f>)
                    ]
                )
                Sg.Index(BufferView(AVal.constant (ArrayBuffer quadIdx :> IBuffer), typeof<int>))
                Sg.Render (AVal.constant quadIdx.Length)
            }

        ASet.single quad
