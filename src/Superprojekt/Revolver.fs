namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open FSharp.Data.Adaptive
open Aardvark.Dom

// ── Shaders ───────────────────────────────────────────────────────────────────

module BlitShader =
    open FShade

    type Fragment =
        {
            [<Color>] c : V4d
            [<Depth>] d : float
        }

    type UniformScope with
        member x.RevolverVisible : bool = x?RevolverVisible
        member x.TextureOffset   : V2d  = x?TextureOffset
        member x.TextureScale    : V2d  = x?TextureScale

    let colorSam =
        sampler2d {
            texture uniform?ColorTexture
            filter Filter.MinMagPoint
            addressU WrapMode.Wrap
            addressV WrapMode.Wrap
        }
    let depthSam =
        sampler2d {
            texture uniform?DepthTexture
            filter Filter.MinMagPoint
            addressU WrapMode.Wrap
            addressV WrapMode.Wrap
        }

    let read (v : Effects.Vertex) =
        fragment {
            let c =
                let original = colorSam.SampleLevel(v.tc, 0.0)
                if uniform.RevolverVisible then lerp original V4d.IIII 0.5
                else original
            return { c = c; d = depthSam.SampleLevel(v.tc, 0.0).X }
        }
    let readColorTex (v : Effects.Vertex) =
        fragment {
            let c = colorSam.SampleLevel(v.tc, 0.0)
            return c
        }
    let readColorDepthTex (v : Effects.Vertex) =
        fragment {
            return { c = colorSam.SampleLevel(v.tc, 0.0); d = depthSam.SampleLevel(v.tc, 0.0).X }
        }

    // Circular magnifier: clips to unit circle, samples with offset+scale.
    let readColor (v : Effects.Vertex) =
        fragment {
            let ndc = 2.0 * v.tc - V2d.II
            if Vec.lengthSquared ndc > 1.0 then discard()
            let b = colorSam.SampleLevel(uniform.TextureOffset + uniform.TextureScale * v.tc, 0.0)
            return b
        }


// ── Revolver overlay ──────────────────────────────────────────────────────────

module Revolver =

    /// Renders each mesh into its own off-screen color+depth texture.
    let private buildMeshTextures (info : Aardvark.Dom.RenderControlInfo) (view : aval<Trafo3d>) (proj : aval<Trafo3d>) (model : AdaptiveModel) =
        let signature =
            info.Runtime.CreateFramebufferSignature [
                DefaultSemantic.Colors,       TextureFormat.Rgba8
                DefaultSemantic.DepthStencil, TextureFormat.Depth24Stencil8
            ]
        model.MeshNames |> AList.toASet |> ASet.mapToAMap (fun name ->
            let mesh =
                sg {
                    Sg.View view
                    Sg.Proj proj
                    Sg.Uniform("ViewportSize", info.ViewportSize)
                    MeshView.render name (AVal.constant true) model.CommonCentroid
                }
            let objs        = mesh.GetRenderObjects(TraversalState.empty info.Runtime)
            let task        = info.Runtime.CompileRender(signature, objs)
            let color, depth =
                task |> RenderTask.renderToColorAndDepthWithClear info.ViewportSize (clear { color C4f.Zero; depth 1.0 })
            color, depth
        )

    /// Full-screen quad that blits one mesh texture back to the screen.
    /// Dims the scene when the revolver is active (RevolverVisible uniform).
    let private blitQuad (meshVisible : aval<Map<string, bool>>) (spaceHeld : aval<bool>) (shiftHeld : aval<bool>) name (color : IAdaptiveResource<IBackendTexture>) (depth : IAdaptiveResource<IBackendTexture>) =
        let active = meshVisible |> AVal.map (fun m -> Map.tryFind name m |> Option.defaultValue true)
        let colorTex = color |> AdaptiveResource.map (fun t -> t :> ITexture)
        let depthTex = depth |> AdaptiveResource.map (fun t -> t :> ITexture)
        let superActive = AVal.logicalAnd [active; AVal.map not spaceHeld]
        sg {
            Sg.Active superActive
            Sg.Shader { BlitShader.read }
            Sg.Uniform("RevolverVisible", shiftHeld)
            Sg.Uniform("ColorTexture",    colorTex)
            Sg.Uniform("DepthTexture",    depthTex)
            Primitives.FullscreenQuad
        }

    /// Circular magnifier disk positioned at `renderPositionNdc`.
    let private disk
            (shiftHeld       : aval<bool>)
            (cursorPosition  : aval<option<V2d>>)
            (meshTextures    : amap<string, IAdaptiveResource<IBackendTexture> * IAdaptiveResource<IBackendTexture>>)
            (viewportSize    : aval<V2i>)
            (dataSet         : string)
            (renderPositionNdc : aval<option<V2d>>) =
        let pixelSize = 200
        sg {
            Sg.Active shiftHeld
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
                (cursorPosition, viewportSize) ||> AVal.map2 (fun ndc size ->
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

    /// Builds the complete revolver scene graph:
    ///   - blit quads (one per mesh, optional dim when active)
    ///   - magnifier disks (one per mesh, stacked vertically by MeshOrder)
    let build
        (info : Aardvark.Dom.RenderControlInfo)
        (view : aval<Trafo3d>)
        (proj : aval<Trafo3d>)
        (cursorPosition : aval<option<V2d>>)
        (spaceHeld : aval<bool>)
        (shiftHeld : aval<bool>)
        (model : AdaptiveModel) =
        let textures = buildMeshTextures info view proj model
        
        let blitNodes =
            textures |> AMap.toASet |> ASet.map (fun (name, (color, depth)) ->
                blitQuad model.MeshVisible spaceHeld shiftHeld name color depth
            )
        let fullOverlayNodes =
            textures |> AMap.toASet |> ASet.map (fun (name,(colorTex,depthTex)) ->
                let order = model.MeshOrder |> AMap.tryFind name |> AVal.map (Option.defaultValue 0)
                let trafo =
                    order |> AVal.map (fun o ->
                        if o = 0 then
                            Trafo3d.Translation(V3d(0.0,0.0,0.1))
                        else
                            let oi = float o - 1.0
                            Trafo3d.Scale(V3d(0.1,0.1,1.0))
                                * Trafo3d.Translation(V3d(0.9,0.9,0.0))
                                * Trafo3d.Translation(V3d(0.0, -oi*0.2, 0.0))
                    )
                sg {
                    Sg.Active spaceHeld
                    Sg.Shader {
                        DefaultSurfaces.trafo
                        BlitShader.readColorDepthTex
                    }
                    Sg.Trafo trafo
                    Sg.View Trafo3d.Identity
                    Sg.Proj Trafo3d.Identity
                    Sg.Uniform("RevolverVisible", shiftHeld)
                    Sg.Uniform("ColorTexture",    colorTex)
                    Sg.Uniform("DepthTexture",    depthTex)
                    Primitives.FullscreenQuad
                }   
                
            )
        let diskNodes =
            model.MeshNames |> AList.map (fun name ->
                let order = model.MeshOrder |> AMap.tryFind name |> AVal.map (Option.defaultValue 0)
                let renderPos =
                    (cursorPosition, order, info.ViewportSize) |||> AVal.map3 (fun p o size ->
                        match p with
                        | Some p -> Some (p + 2.0 * float o * (V2d(0.0, 200.0) / V2d size))
                        | None   -> None
                    )
                disk shiftHeld cursorPosition textures info.ViewportSize name renderPos
            ) |> AList.toASet

        ASet.unionMany
            (ASet.ofList [
                blitNodes
                fullOverlayNodes
                diskNodes
            ])
