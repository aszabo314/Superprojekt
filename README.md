# Superprojekt

Research prototype for interactive 3D inspection of geological mesh and pointcloud datasets. Two F# projects:

- **Superserver** — ASP.NET Core + Giraffe. Serves mesh data and runs spatial queries (Embree BVH, closest-point, multi-mesh raycasts, isolines, curvature ridges, surface patches, ICP).
- **Superprojekt** — Blazor WebAssembly client. Aardvark.Dom Elm-style architecture, WebGL rendering. Runs on desktop and mobile browsers; thin client by design — heavy compute lives on the server.

## Run it

You need .NET 8 SDK and a recent browser. First-time setup:

```bash
dotnet tool restore
dotnet paket restore
```

Start the server (serves both the API and the WASM client at `http://localhost:5000`):

```bash
dotnet run --project src/Superserver
```

Open `http://localhost:5000`. The default dataset (read from `src/Superserver/data/default.txt`) auto-loads on first paint.

### Datasets

Put OBJ files in `src/Superserver/data/<dataset>/<mesh>/`:

```
data/
  default.txt              # contents = name of default dataset (optional)
  <dataset>/
    <mesh>/
      *.obj                # one file per mesh part
      *centroid.txt        # "x y z" (V3d.Zero if absent)
      *_atlas.jpg or *.jpg # texture atlas
```

The first request for a mesh parses OBJ + builds an Embree scene + BbTree; the result is cached for the process lifetime. Dataset bbox fetch warms the cache for every mesh up front, so the first interactive query never pays the lazy-load cost.

### Docker

`Dockerfile` + `docker-compose.yml` package the server + published WASM bundle behind nginx. `docker-compose up` builds and runs on port 80.

## What you can do today

- **Load and explore a dataset.** Orbit camera (drag/right-drag/scroll, or touch). Double-tap geometry to recenter. Hold Space for fullscreen review.
- **Toggle individual meshes / solo / focus.** Bulk All / None. Choose textured, shaded, or slope-color rendering.
- **Ghost silhouette.** Inactive meshes render as a faint translucent shell so you keep spatial context. Toggle + opacity in the gear popover; default on.
- **Lasso clip.** Top-level `◌ Lasso` button opens a floating card; draw a polygon in the viewport, outside-lasso fragments fade to ghost level. The card has a symbol toolbar (`◉/○` enable, `✎` redraw, `⊘` cancel, `✕` clear) — toggling the filter off keeps the polygon so you can re-enable without redrawing.
- **ScanPins (annotations).** Place a 3D anchor on a surface. Each pin has an **Inner radius** (hard truth: α = 1 and full evaluation weight inside) and a **Falloff radius** (exponential decay beyond inner). Both stored in metric world-metres; the falloff slider is relative to the inner radius. Pin Point / Line / Patch payloads carry derived analysis.
- **Filter chain.** Visibility is the conjunction of three filters: MeshActive toggle → lasso region → pin blob field. Both lasso and blob must agree for a fragment to be fully opaque; inside-lasso-outside-blob fragments fall to ghost level. The pin blob filter is gated by the **Isolate pins** toggle in the gear popover; the lasso filter by the `◉/○` button on its card.
- **Mesh registration (gated).** Reference-based ICP (traditional, or region-restricted where pin centres + falloff radii weight the solve). The server solver is implemented and verified, but the Run button is currently disabled — the registration workflow is part of the upcoming feature work.
- **Error provenance overlay.** Per-mesh sensor type + dataset-error override, combined with ICP algorithm residual and a local-conditioning heuristic over the anchors into a tunable heatmap.
- **Fusion mode.** Top-bar `◈ Fusion` renders all visible meshes into an offscreen pass (own colour + depth target) where per-fragment depth carries combined error, so the lowest-error surface wins the depth test; the composite is drawn back as a fullscreen quad. Picking raycasts every visible mesh and keeps the same lowest-error winner.
- **Retarget.** Re-project the existing pins' anchors onto a chosen target mesh (server closest-point per pin), review the per-pin projection distances in a card, accept/reject individually, then commit.
- **Workspace save / load.** Serialise the session (dataset, pins, transforms, visibility, sensors, lasso, registration, camera, settings) to JSON via the gear popover and reload it later. Hand-rolled JSON in `Persistence.fs`; in-memory otherwise.
- **Panorama.** Top-bar `▦ Pano` opens a floating panel showing a cylindrical view from a synthetic viewpoint at the scene-bbox centre (no dataset ships real imagery, so one is generated per dataset on load). The meshes are rendered into a cubemap from that pose and reprojected cylindrically. **Photo / Render / Blend** modes (Photo = reference state, Render = live state, Blend = the disagreement between them), anchor markers projected into the view, click-to-place anchors via a server raycast through the pose, and a fly-to-pose button.

## Architecture

Elm-style: `Model` → `Update.update` → `View.view` → `Boot.run`.

The client compiles in this order (`src/Superprojekt/Superprojekt.fsproj`):

```
MeshData.fs                          mesh fetch / parse, shared HttpClient
Query.fs                             server query wrappers (Async)
CameraModel.fs / .g.fs               OrbitState [<ModelType>]
OrbitController.fs                   orbit camera + messages
ScanPinModel.fs / .g.fs              ScanPin + card types
PinGeometry.fs                       icosphere + footprint geometry
Model.fs / .g.fs                     application Model [<ModelType>]
Persistence.fs                       workspace JSON serialise / apply
LineShader.fs                        flat colour + pixel-constant 3D lines
Primitives.fs                        compact GUI widgets + shared helpers
Messages.fs                          Message DU
CardUpdate.fs / ScanPinUpdate.fs     sub-reducers
Update.fs                            server actions + main reducer
MeshShaders.fs                       mesh / fusion / panorama shaders
MeshView.fs                          per-mesh scene nodes
FusionView.fs                        offscreen fusion pass + composite
PanoramaView.fs                      cubemap capture + cylindrical reproject
ScanPinScene.fs                      pin sg nodes
SceneGraph.fs                        scene composition + coordinate cross
CardsPin.fs / Cards.fs               floating pin diagrams + card chrome
GuiTopBar.fs / GuiPanels.fs          top bar + left panel
GuiOverlays.fs / GuiCards.fs         overlays + cards
View.fs                              view function + App module
ShaderCache.fs                       FShade AOT cache
Program.fs                           Boot.run entry
```

The server is much smaller (`src/Superserver/`):

```
MeshLoader.fs                        OBJ parse + centroid file + atlas paths
MeshCache.fs                         lazy Embree scene + BbTree cache
MeshAnalysis.fs                      isoline / ridge tracing, patch sampling
MeshIcp.fs                           ICP solver
QueryHandlers.fs                     HTTP query handlers
Handlers.fs                          routing
Program.fs                           ASP.NET startup
```

## Render pipeline

**Default path: one forward pass into the main framebuffer.** The mesh shader (`MeshShaders.fs`) is the only thing that writes depth per fragment:

- Per-fragment α = `lerp(GhostOpacity, 1, mask)` with `mask = lassoComponent * blobComponent` — conjunctive: both filters must agree. Inactive meshes get `GhostOpacity` everywhere.
- Lasso component is 1 inside the half-space polytope, 0 outside (or 1 if no lasso).
- Blob component is the max weight across pins: 1 inside any pin's `InnerRadius`, `exp(-3·(d-inner)/(outer-inner))` between inner and `FalloffRadius`, 0 outside every pin's range (or 1 if no pins, or if the **Isolate pins** toggle is off).
- α-gated `gl_FragDepth`: hard-core fragments write their natural `gl_FragCoord.z`; falloff and outside fragments write 1.0 (far). Result: translucent ghost geometry doesn't occlude opaque surfaces behind it, but opaque overdraw still works.
- Post-lerp clamp: non-hard-core fragments are pinned below the opaque threshold to prevent the depth-write branch from flipping mid-falloff (which would otherwise produce a visible occlusion ring).
- 32-slot uniform arrays of lasso half-space planes, pin centres + inner radii `(cx, cy, cz, innerR)`, and pin falloff radii `(falloffR, 0, 0, 0)`. Pin geometry is stored in metric world-space on the model and converted to render-space (`* datasetScale`) on upload.

Pin geometry, lines, and text are drawn in the same pass with `DepthTest.LessOrEqual` so they fade behind opaque meshes. The coordinate cross + tick labels are in `passOne` with `DepthTest.None` so they always overlay everything.

**`Sg.DepthMask` is never used.** It is buggy in this Aardvark / Aardworx WebGL build and silently breaks the depth pipeline. Ordering is steered with `Sg.DepthTest` + `Sg.Pass` alone. This violates the textbook "translucent should not write depth" rule but is the only configuration that actually renders correctly in this stack.

**Fusion mode adds an offscreen pass** (`FusionView.fs`). When `◈ Fusion` is on, the normal meshes are suppressed and re-rendered into an offscreen framebuffer (its own colour + depth, sized adaptively to the viewport) where per-fragment depth encodes combined error; the lowest-error surface therefore wins `LessOrEqual` depth-testing. That colour target is composited back into the main pass as a fullscreen quad, with pins / cross / labels still drawn on top. FBOs are fine in this stack — the old ban only applied to the removed WBOIT code. `Sg.DepthMask` is still off-limits (see below).

**The panorama uses a separate offscreen cubemap pass** (`PanoramaView.fs`), in its own nested `renderControl`. The meshes are rendered into a colour cubemap (six 90°-FOV faces, via the same `CompileRender` path as fusion) from the panorama pose, then a fullscreen quad reprojects the cube **cylindrically** (vertical = height on the cylinder, so vertical lines stay straight). Two cubes are captured — reference state and live state — and blended for the Photo/Render/Blend modes. All panorama shaders are strictly `float32`: WebGL2 has no double precision, so `float`/`Constant.Pi`/`V3d` in a shader emit GLSL `double` and fail to compile in-browser (and neither `dotnet build` nor `fshadeaot` catch it).

**Picking** uses Aardvark's pixel picker. `e.Location.Depth < 0.9999` gates valid hits (background misses leave depth at the clear value). Under fusion, picking instead raycasts every visible mesh server-side and keeps the lowest-error hit, matching the depth-test winner. Panorama click-to-place builds a world ray through the pose's cylindrical mapping and raycasts the visible meshes the same way.

## Server query performance

Costly queries scale with mesh count × angular density. Rules learned the hard way:

- **Never per-mesh loops over HTTP.** Use the batch endpoints (`ray-batch`, `grid-eval`) and let the server fan out with `Parallel.For`.
- **Embree `Scene.Intersect` is thread-safe** — outer loops use `Parallel.For` with per-thread `ResizeArray` hit buffers.
- **Debounce user-driven triggers** with a `CancellationTokenSource` so only the final drag position hits the server.
- **Mesh caches are warmed at dataset load** by the bbox handler.

## Style

- Light theme, high contrast, print-appropriate.
- GUI must be readable to a non-expert at first glance.
- No comments unless the logic is non-obvious; concise code; no unnecessary abstractions.
- See `CLAUDE.md` for the detailed conventions an AI assistant should follow.
