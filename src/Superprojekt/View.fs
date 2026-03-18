namespace Superprojekt

open Aardvark.Base
open Aardvark.Rendering
open FSharp.Data.Adaptive
open Aardvark.Dom
open Adaptify
open Superprojekt
open Aardworx.WebAssembly

type LoadedMesh =
    {
        pos  : cval<IBuffer>
        tc   : cval<IBuffer>
        idx  : cval<IBuffer>
        tex  : cval<ITexture>
        fvc  : cval<int>
        mesh : MeshData option ref
    }

module View =

    let private apiBase =
        lazy (
            let href = Window.Location.Href
            let uri = System.Uri(href)
            let mutable path = uri.AbsolutePath
            // strip filename (e.g. index.html)
            if path.Contains('.') then path <- path.Substring(0, path.LastIndexOf('/') + 1)
            path <- path.TrimEnd('/')
            uri.GetLeftPart(System.UriPartial.Authority) + path + "/api"
        )

    let private meshes = System.Collections.Generic.Dictionary<string, LoadedMesh>()

    let private loadMeshAsync (name : string) (commonCentroid : aval<V3d>) : LoadedMesh =
        match meshes.TryGetValue(name) with
        | true, m -> m
        | _ ->
            let m =
                {
                    pos  = cval (ArrayBuffer [| V3f.Zero; V3f.Zero; V3f.Zero |] :> IBuffer)
                    tc   = cval (ArrayBuffer [| V2f.Zero; V2f.Zero; V2f.Zero |] :> IBuffer)
                    idx  = cval (ArrayBuffer [| 0; 1; 2 |] :> IBuffer)
                    tex  = cval<ITexture> (AVal.force DefaultTextures.checkerboard)
                    fvc  = cval 3
                    mesh = ref None
                }
            meshes.[name] <- m
            task {
                try
                    let! mesh = MeshData.fetch apiBase.Value name 0
                    let cc = AVal.force commonCentroid
                    let rebased = mesh.positions |> Array.map (fun p -> V3f(mesh.centroid + V3d p - cc))
                    m.mesh.Value <- Some mesh
                    transact (fun () ->
                        m.pos.Value <- ArrayBuffer rebased
                        m.tc.Value  <- ArrayBuffer mesh.uvs
                        m.idx.Value <- ArrayBuffer mesh.indices
                        m.fvc.Value <- mesh.indices.Length
                    )
                    let! img = JSImage.load mesh.atlasUrl
                    transact (fun () -> m.tex.Value <- JSTexture(img, true))
                with e ->
                    Log.error "failed to load mesh %s: %A" name e
            } |> ignore
            m

    let render (name : string) (active : aval<bool>) (commonCentroid : aval<V3d>) (filteredMesh : aval<option<string * V3d * int[]>>) =
        let loaded = loadMeshAsync name commonCentroid

        let filtActive =
            filteredMesh |> AVal.map (fun fm ->
                match fm with
                | Some(n, _, indices) when n = name && indices.Length > 0 -> true
                | _ -> false)

        let filtIdx =
            filteredMesh |> AVal.map (fun fm ->
                match fm with
                | Some(n, _, indices) when n = name && indices.Length > 0 -> ArrayBuffer indices :> IBuffer
                | _ -> ArrayBuffer [| 0; 1; 2 |] :> IBuffer)

        let filtFvc =
            filteredMesh |> AVal.map (fun fm ->
                match fm with
                | Some(n, _, indices) when n = name && indices.Length > 0 -> indices.Length
                | _ -> 0)

        sg {
            Sg.Shader {
                DefaultSurfaces.trafo
                DefaultSurfaces.diffuseTexture
            }
            Sg.Uniform("DiffuseColorTexture", loaded.tex)
            Sg.VertexAttributes(
                HashMap.ofList [
                    string DefaultSemantic.Positions,              BufferView(loaded.pos, typeof<V3f>)
                    string DefaultSemantic.DiffuseColorCoordinates, BufferView(loaded.tc,  typeof<V2f>)
                ]
            )

            // main mesh
            sg {
                Sg.Active(AVal.map2 (&&) active (loaded.fvc |> AVal.map (fun c -> c > 3)))
                Sg.Index(BufferView(loaded.idx, typeof<int>))
                Sg.Render loaded.fvc
            }

            // filtered overlay
            sg {
                Sg.Shader {
                    DefaultSurfaces.trafo
                    DefaultSurfaces.constantColor C4f.Red
                }
                Sg.Uniform("DiffuseColorTexture", loaded.tex)
                Sg.VertexAttributes(
                    HashMap.ofList [
                        string DefaultSemantic.Positions,              BufferView(loaded.pos, typeof<V3f>)
                        string DefaultSemantic.DiffuseColorCoordinates, BufferView(loaded.tc,  typeof<V2f>)
                    ]
                )
                Sg.Active filtActive
                Sg.Translate(0.0, 0.0, 0.01)
                Sg.Index(BufferView(filtIdx, typeof<int>))
                Sg.Render filtFvc
            }
        }


    let view (env : Env<Message>) (model : AdaptiveModel) =

        // fetch all centroids once on startup; populates MeshNames + CommonCentroid
        let _init =
            task {
                try
                    let! cs = MeshData.fetchCentroids apiBase.Value
                    env.Emit [CentroidsLoaded cs]
                with e ->
                    Log.error "centroids fetch failed: %A" e
            }

        body {
            OnBoot [
                "const l = document.getElementById('loader');"
                "if(l) l.remove();"
                "document.body.classList.add('loaded');"
            ]

            renderControl {
                RenderControl.Samples 1
                Class "render-control"

                let! size = RenderControl.ViewportSize

                OrbitController.getAttributes (Env.map CameraMessage env)

                RenderControl.OnRendered(fun _ ->
                    env.Emit [CameraMessage OrbitMessage.Rendered]
                )

                let proj =
                    size |> AVal.map (fun s ->
                        Frustum.perspective 90.0 0.5 1000.0 (float s.X / float s.Y)
                    )

                Sg.View(model.Camera.view |> AVal.map CameraView.viewTrafo)
                Sg.Proj(proj |> AVal.map Frustum.projTrafo)

                Sg.OnPointerLeave(fun _ -> env.Emit [Hover None])

                Sg.OnDoubleTap(fun e ->
                    env.Emit [CameraMessage (OrbitMessage.SetTargetCenter(true, AnimationKind.Tanh, e.WorldPosition))]
                    false
                )

                let triggerFilter (renderPos : V3d) =
                    let cc       = AVal.force model.CommonCentroid
                    let worldPos = renderPos + cc
                    let names    = AList.force model.MeshNames
                    env.Emit [LogDebug (sprintf "triggerFilter pos=%s world=%s meshes=%d" (renderPos.ToString("0.00")) (worldPos.ToString("0.00")) (Seq.length names))]
                    task {
                        try
                            for name in names do
                                env.Emit [LogDebug (sprintf "  query sphere %s..." name)]
                                let! triIds =
                                    Query.sphereTriangles apiBase.Value name 0 worldPos 1.0
                                    |> Async.StartAsTask
                                env.Emit [LogDebug (sprintf "  %s: %d triangles" name triIds.Length)]
                                if triIds.Length > 0 then
                                    let loaded = loadMeshAsync name model.CommonCentroid
                                    match loaded.mesh.Value with
                                    | Some mesh ->
                                        let triCount = mesh.indices.Length / 3
                                        let maxTriId = if triIds.Length > 0 then triIds |> Array.max else -1
                                        env.Emit [LogDebug (sprintf "  %s: maxTriId=%d triCount=%d idxLen=%d" name maxTriId triCount mesh.indices.Length)]
                                        if maxTriId >= triCount then
                                            env.Emit [LogDebug (sprintf "  %s: ERROR triId %d out of range (mesh has %d triangles)" name maxTriId triCount)]
                                        else
                                            let filtered = MeshData.filterByTriangles triIds mesh
                                            env.Emit [LogDebug (sprintf "  %s: filtered %d indices" name filtered.indices.Length)]
                                            env.Emit [FilteredMeshLoaded(name, renderPos, filtered.indices)]
                                    | None ->
                                        env.Emit [LogDebug (sprintf "  %s: mesh not loaded yet" name)]
                            env.Emit [LogDebug "triggerFilter done"]
                        with e ->
                            env.Emit [LogDebug (sprintf "triggerFilter ERROR: %s" (string e))]
                    } |> ignore

                Sg.OnTap(fun e ->
                    if e.Ctrl && e.Button = Button.Left then
                        env.Emit [ClearFilteredMesh]
                        triggerFilter e.Position
                        false
                    else true
                )

                Sg.OnLongPress(fun e ->
                    env.Emit [LogDebug (sprintf "LongPress pos=%s world=%s" (e.Position.ToString("0.00")) (e.WorldPosition.ToString("0.00")))]
                    env.Emit [ClearFilteredMesh]
                    triggerFilter e.Position
                    false
                )

                Sg.OnPointerMove(fun p ->
                    env.Emit [Hover (Some p.Position)]
                )

                Sg.Shader {
                    DefaultSurfaces.trafo
                    DefaultSurfaces.simpleLighting
                    Shader.nothing
                }

                // floor plane
                sg {
                    Sg.Scale 10.0
                    Primitives.Quad(Quad3d(V3d(-1, -1, 0), V3d(2, 0, 0), V3d(0.0, 2.0, 0.0)), C4b.SandyBrown)
                }

                // all loaded meshes (+ per-mesh filtered overlay)
                model.MeshNames |> AList.map (fun name ->
                    let active =
                        model.MeshVisible
                        |> AVal.map (fun m -> Map.tryFind name m |> Option.defaultValue true)
                    render name active model.CommonCentroid model.FilteredMesh
                ) |> AList.toASet

                // green teapot at origin
                sg {
                    Sg.Shader {
                        DefaultSurfaces.trafo
                        DefaultSurfaces.simpleLighting
                        Shader.withViewPos
                    }
                    Primitives.Teapot(C4b.Green)
                }

                // yellow octahedron hovering above the teapot
                sg {
                    Sg.Translate(0.0, 0.0, 1.0)
                    Primitives.Octahedron(C4b.Yellow)
                }

                // red hover sphere
                sg {
                    Sg.Active(model.Hover |> AVal.map Option.isSome)
                    let pos = model.Hover |> AVal.map (function Some p -> p | None -> V3d.Zero)
                    Sg.NoEvents
                    Primitives.Sphere(pos, 0.1, C4b.Red)
                }

                // query sphere visualisation
                sg {
                    Sg.Shader {
                        DefaultSurfaces.trafo
                        DefaultSurfaces.simpleLighting
                    }
                    Sg.Active(model.FilteredMesh |> AVal.map Option.isSome)
                    let pos = model.FilteredMesh |> AVal.map (function Some(_, p, _) -> p | None -> V3d.Zero)
                    Sg.NoEvents
                    Primitives.Sphere(pos, 1.0, C4b.Yellow)
                }

                // blue stacked boxes
                sg {
                    Sg.NoEvents
                    Sg.Translate(1.0, 0.0, 0.0)
                    ASet.range (AVal.constant 0) model.Value
                    |> ASet.map (fun i ->
                        sg {
                            Sg.Translate(0.0, 0.0, float i * 0.4)
                            Primitives.Box(Box3d.FromCenterAndSize(V3d.Zero, V3d.III * 0.1), C4b.Blue)
                        }
                    )
                }
            }

            // Burger menu toggle (mobile)
            button {
                Attribute("id", "burger-btn")
                Class "burger-btn"
                Dom.OnClick(fun _ ->
                    env.Emit [ToggleMenu]
                )
                div { Class "burger-line" }
                div { Class "burger-line" }
                div { Class "burger-line" }
            }

            // HUD overlay — pure-CSS tabs
            div {
                Class "tabs"
                model.MenuOpen |> AVal.map (fun o ->
                    if o then Some (Class "tabs-open")
                    else None
                )

                div {
                    Class "tab-labels"
                    label {
                        input {
                            Attribute("type",    "radio")
                            Attribute("name",    "hud-tabs")
                            Attribute("id",      "hud-tab1")
                            Attribute("checked", "checked")
                        }
                        "Scene"
                    }
                    label {
                        input {
                            Attribute("type", "radio")
                            Attribute("name", "hud-tabs")
                            Attribute("id",   "hud-tab2")
                        }
                        "Info"
                    }
                    label {
                        input {
                            Attribute("type", "radio")
                            Attribute("name", "hud-tabs")
                            Attribute("id",   "hud-tab3")
                        }
                        "Settings"
                    }
                }

                div {
                    Class "tab-panels"

                    // ── Tab 1: Scene ──────────────────────────────────
                    div {
                        Class "tab-panel"
                        Attribute("id", "hud-panel1")

                        h1 {
                            "Counter: "
                            model.Value |> AVal.map string
                        }
                        button {
                            "+"
                            Dom.OnClick(fun _ -> env.Emit [Increment])
                        }
                        button {
                            "-"
                            Dom.OnClick(fun _ -> env.Emit [Decrement])
                        }
                        button {
                            "Clear Filter"
                            Dom.OnClick(fun _ -> env.Emit [ClearFilteredMesh])
                        }
                        h2 {
                            model.Hover |> AVal.map (function
                                | Some p -> "Hover: " + p.ToString("0.00")
                                | None   -> "Hover: none"
                            )
                        }

                        // mesh toggles
                        h3 { "Meshes" }
                        model.MeshNames |> AList.map (fun name ->
                            let isVis =
                                model.MeshVisible
                                |> AVal.map (fun m -> Map.tryFind name m |> Option.defaultValue true)
                            div {
                                label {
                                    input {
                                        Attribute("type", "checkbox")
                                        isVis |> AVal.map (fun v ->
                                            if v then Some (Attribute("checked", "checked"))
                                            else None
                                        )
                                        Dom.OnClick(fun _ ->
                                            let current = AVal.force isVis
                                            env.Emit [SetVisible(name, not current)]
                                        )
                                    }
                                    " " + name
                                }
                            }
                        )

                        ul {
                            li { "ctrl+click or long-press to filter mesh" }
                            li { "double-click to focus camera" }
                        }
                    }

                    // ── Tab 2: Info ───────────────────────────────────
                    div {
                        Class "tab-panel"
                        Attribute("id", "hud-panel2")

                        h1 { "Info" }
                        p { "Aardvark.Dom WebAssembly scaffold." }
                        p { "Elm-style architecture: Model / Update / View." }
                        p { "Rendering via WebGL with an orbit camera." }
                    }

                    // ── Tab 3: Settings ───────────────────────────────
                    div {
                        Class "tab-panel"
                        Attribute("id", "hud-panel3")

                        h1 { "Settings" }
                        p { "Nothing to configure yet." }
                    }
                }
            }

            // on-screen debug log
            div {
                Style [
                    Position "fixed"; Bottom "0"; Left "0"; Right "0"
                    MaxHeight "30vh"; OverflowY "auto"
                    Background "rgba(0,0,0,0.8)"; Color "#0f0"
                    FontFamily "monospace"; FontSize "11px"
                    Padding "4px 8px"; PointerEvents "none"
                    ZIndex 9999; StyleProperty("white-space", "pre-wrap")
                ]
                model.DebugLog |> AList.map (fun line ->
                    div { line }
                )
            }
        }

module App =
    let app =
        {
            initial   = Model.initial
            update    = Update.update
            view      = View.view
            unpersist = Unpersist.instance
        }
