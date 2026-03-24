namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open FSharp.Data.Adaptive
open Aardvark.Dom

module Revolver =

    let private disk
            (revolverActive  : aval<bool>)
            (revolverBase    : aval<option<V2d>>)
            (meshTextures    : amap<string, IAdaptiveResource<IBackendTexture> * IAdaptiveResource<IBackendTexture>>)
            (viewportSize    : aval<V2i>)
            (dataSet         : string)
            (renderPositionNdc : aval<option<V2d>>) =
        let pixelSize = 200
        sg {
            Sg.Active revolverActive
            Sg.NoEvents
            let t =
                (renderPositionNdc, viewportSize) ||> AVal.map2 (fun ndc size ->
                    match ndc with
                    | Some ndc ->
                        let scale = float pixelSize / V2d size
                        Trafo3d.Scale(scale.X, scale.Y, 1.0) * Trafo3d.Translation(ndc.X, ndc.Y, 0.0)
                    | None ->
                        Trafo3d.Scale(0.0)
                )
                
            let texture : aval<ITexture> =
                meshTextures
                |> AMap.tryFind dataSet
                |> AVal.bind (function
                    | Some (c, _) -> c |> AdaptiveResource.map (fun t -> t :> ITexture) :> aval<_>
                    | None        -> DefaultTextures.checkerboard)
            let textureOffset =
                (revolverBase, viewportSize) ||> AVal.map2 (fun ndc size ->
                    match ndc with
                    | Some ndc ->
                        let tc = (ndc + V2d.II) * 0.5
                        tc - 0.5 * V2d(pixelSize, pixelSize) / V2d size
                    | None -> V2d.Zero
                )
            let textureScale = viewportSize |> AVal.map (fun s -> V2d pixelSize / V2d s)
            Sg.Uniform("TextureOffset", textureOffset)
            Sg.Uniform("TextureScale",  textureScale)
            Sg.View Trafo3d.Identity
            Sg.Proj Trafo3d.Identity
            Sg.Uniform("ColorTexture",  texture)
            Sg.Trafo t
            Sg.Shader {
                DefaultSurfaces.trafo
                BlitShader.readColor
            }
            Primitives.FullscreenQuad
        }

    let build
        (info : Aardvark.Dom.RenderControlInfo)
        (view : aval<Trafo3d>)
        (proj : aval<Trafo3d>)
        (revolverBase    : aval<option<V2d>>) 
        (revolverActive  : aval<bool>)     
        (fullscreenActive : aval<bool>) 
        (model : AdaptiveModel) =
        let cnt, colors, depths = MeshView.buildMeshTextures info view proj model

        MeshView.composeMeshTextures cnt colors depths
        
        // let blitNodes =
        //     textures |> AMap.toASet |> ASet.map (fun (name, (color, depth)) ->
        //         MeshView.blitQuad model.MeshVisible fullscreenActive revolverActive name color depth
        //     )
        // let fullOverlayNodes =
        //     textures |> AMap.toASet |> ASet.map (fun (name, (colorTex, depthTex)) ->
        //         let order = model.MeshOrder |> AMap.tryFind name |> AVal.map (Option.defaultValue 0)
        //         let trafo =
        //             order |> AVal.map (fun o ->
        //                 if o = 0 then
        //                     Trafo3d.Translation(V3d(0.0, 0.0, 0.1))
        //                 else
        //                     let oi = float o - 1.0
        //                     Trafo3d.Scale(V3d(0.1, 0.1, 1.0))
        //                         * Trafo3d.Translation(V3d(0.9, 0.9, 0.0))
        //                         * Trafo3d.Translation(V3d(0.0, -oi * 0.2, 0.0))
        //             )
        //         sg {
        //             Sg.Active fullscreenActive
        //             Sg.Shader {
        //                 DefaultSurfaces.trafo
        //                 BlitShader.readColorDepthTex
        //             }
        //             Sg.Trafo trafo
        //             Sg.View Trafo3d.Identity
        //             Sg.Proj Trafo3d.Identity
        //             Sg.Uniform("RevolverVisible", revolverActive)
        //             Sg.Uniform("ColorTexture",    colorTex)
        //             Sg.Uniform("DepthTexture",    depthTex)
        //             Primitives.FullscreenQuad
        //         }
        //     )
        // let diskNodes =
        //     model.MeshNames |> AList.map (fun name ->
        //         let order = model.MeshOrder |> AMap.tryFind name |> AVal.map (Option.defaultValue 0)
        //         let renderPos =
        //             (revolverBase, order, info.ViewportSize) |||> AVal.map3 (fun p o size ->
        //                 match p with
        //                 | Some p -> Some (p + 2.0 * float o * (V2d(0.0, 200.0) / V2d size))
        //                 | None   -> None
        //             )
        //         disk revolverActive revolverBase textures info.ViewportSize name renderPos
        //     ) |> AList.toASet
        //
        // ASet.unionMany
        //     (ASet.ofList [
        //         blitNodes
        //         fullOverlayNodes
        //         diskNodes
        //     ])
