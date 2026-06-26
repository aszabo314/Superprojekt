# Superprojekt

Research prototype for interactive 3D inspection and **registration** of geological mesh datasets (multi-epoch scans of the same terrain). Two F# projects:

- **Superserver** — ASP.NET Core + Giraffe. Serves mesh data and runs spatial queries (Embree BVH ray/closest-point, sphere contact rings, surface patches, per-vertex signed distance, N-mesh M3C2 probes, and a weighted rigid landmark solve). Also hosts the WASM client.
- **Superprojekt** — Blazor WebAssembly client. Aardvark.Dom Elm-style architecture, WebGL2 rendering. Runs on desktop and mobile browsers; thin client by design — heavy compute lives on the server.

## Run it

You need the .NET 8 SDK and a recent browser. First-time setup:

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

The first request for a mesh parses the OBJ + builds an Embree scene + BbTree; the result is cached for the process lifetime. The dataset bbox fetch warms the cache for every mesh up front, so the first interactive query never pays the lazy-load cost.

### Docker

`Dockerfile` + `docker-compose.yml` package the server + published WASM bundle behind nginx. `docker-compose up` builds and runs on port 80.

## What you can do

- **Load & explore.** Orbit camera (drag / right-drag / scroll, or touch); double-tap geometry to recenter; hold **Space** for distraction-free fullscreen; **Alt + scroll** cycles which mesh under the cursor is *isolated* (kept solid while the rest ghost). Per-mesh image-space outlines, rendering mode (Textured / Shaded / Slope-colour), ghost silhouette + opacity, shading strength, and slope threshold live in the **⚙ gear** popover.
- **Reference peek.** Hold the **👁 Peek** button (or **R**) to show only the reference mesh.
- **Three-mode workflow.** The left rail has exactly three modes — **Overview · Correspondence · Inspect** — that share one selection state. The containers (rail, 3D viewport, focus panel, bottom dock) never move between modes; only their content changes. A single global **Before / After** toggle in the top bar (enabled once anything is solved) flips the whole app between each mesh's load pose and its solved pose.
- **Overview.** The rail lists every mesh with a colour swatch, visibility toggle, sensor-type tag (cycles), the reference **★**, and a frame-camera button; hovering a row peek-isolates that mesh in 3D. The bottom dock is a roster (sensor · triangle count · overlaps-the-reference · visibility). The focus panel shows atlas-textured WebGL tiles of each mesh.
- **ScanPins.** Place a pin on a surface (**Correspondence → ○ Place pin**, then tap the reference). Each pin has a metric **inner radius** and runs a server-side **M3C2 distance probe**: a 20 m cylinder along the locally-estimated surface normal (PCA over the reference inside the pin sphere) samples every visible mesh and returns one signed-distance distribution per mesh, re-centred so 0 = the reference median. The pin's influence renders as a thin equator ring plus the exact per-mesh **sphere–surface contact rings** (server-computed, cached, invalidated on radius / centre / registration change).
- **Correspondence registration.** Designate a reference mesh (**★** — every error metric is relative to it; there is no absolute ground truth). **Every pin is a registration pin**: on placement it auto-seeds one **correspondence marker** per other mesh by closest-point projection of the pin's reference marker, ROI-clamped to the pin. Markers are stored *mesh-local*, so the Before/After toggle moves each marker with its mesh. Refine a marker in the focus panel — toggle **⊕ set point** and click the focused mesh's surface to place it (a GPU pick, constrained to the pin's region). Set mode is off by default so the focus pans freely; placing exits it. With ≥3 markers per moving mesh, **Solve** runs a weighted rigid landmark solve (Umeyama/Arun, server-side) per mesh in parallel and writes each mesh's solved transform. A 3D **constellation** draws each marker, the haloed reference marker, and the lines between them. There is no preview/commit and no undo history — the solve writes the result directly and the Before/After toggle is the comparison.
- **Focus panel (right).** A **WebGL** large single of the focused mesh (full-res, **atlas-textured**, pan + mouse-anchored zoom; **⊕ set point** to place correspondences) over a strip of textured thumbnail tiles, one per visible mesh — each its own render control. A **Pano / Top** toggle drives the single: **Top** is strictly orthographic, **Pano** a cylindrical unwrap (vertex shader). Click a tile to focus that mesh; **⟲ reset** recentres; hold **⇄ ref** to peek the reference. Picking is a GPU pick (`Sg.OnTap` → surface point → correspondence).
- **Inspect.** The **central 3D** paints the all-meshes **variance** map (disagreement of every visible moving mesh) on the reference, the moving meshes dropped to faint context. The bottom **dock** holds the selected pin's **distribution** (jittered raw probe samples + median/IQR box on a shared signed-distance axis with the ±LoD₉₅ band) and a **shift readout** (the focused mesh's centroid displacement split into vertical-datum / horizontal + rotation angle, derived from its solved transform). The intrinsic **incidence / range / triangle-shape** acquisition heatmaps are in the rail. *(The per-mesh focus difference/displacement tiles were retired with the 2D-canvas focus panel; reinstate as WebGL if needed.)*
- **Contact-line cursor.** Hovering the 3D surface inside a pin's probe cylinder darkens the intersected meshes and traces a bright band where the implied plane meets each surface.
- **Linked highlighting.** Hover anything — a rail row, a dock row, a 3D constellation glyph, a focus tile — and the same object lights up everywhere at once. There is a single shared selection record; no panel-to-panel wiring.

## Architecture

Elm-style: `Model` → `Update.update` → `View.view` → `App` (wired through Aardvark.Dom's `Boot.run`). The whole app state is one `[<ModelType>]` `Model`; `Adaptify` generates the adaptive `*.g.fs` views from it (never edit those by hand — re-run `adaptify.sh`).

The client compiles in this order (`src/Superprojekt/Superprojekt.fsproj`):

```
MeshData.fs            mesh fetch / parse, ApiConfig, shared HttpClient
ProbeModel.fs          M3C2 probe result / state types
Query.fs               server query wrappers (Async)
CameraModel.fs / .g.fs OrbitState [<ModelType>]
OrbitController.fs     orbit camera + messages
RegistrationModel.fs   ScanPinId, correspondence anchors, readiness engine, RegJson, fly-to math (WASM-free, shared with Supertests)
ScanPinModel.fs / .g.fs ScanPin + placement state
PinGeometry.fs         icosphere + sphere outline
Model.fs / .g.fs       application Model + Selection + transforms [<ModelType>]
LineShader.fs          flat colour + pixel-constant 3D lines
Primitives.fs          compact GUI widgets, observedRender, readiness-engine adapter
Messages.fs            Message DU
ScanPinUpdate.fs       pin sub-reducer + lazy probe/rings postludes
UpdateHelpers.fs       reducer helpers + debounce/generation state, correspondence seeding
Update.fs              main reducer + focus-map / surface-distance postludes
MeshShaders.fs         RenderPass + MeshShader + OutlineGBuffer/OutlineEdge
MeshView.fs            per-mesh scene nodes, load/displayed transforms
OutlineView.fs         offscreen image-space outline pass
ScanPinScene.fs        pin sg nodes + correspondence constellation
SceneGraph.fs          scene composition + coordinate cross
GuiTopBar.fs           top bar + before/after toggle + gear popover
GuiOverlays.fs         toast, scale bar, orientation indicator, wheel label
GuiRail.fs             three-mode left rail
FocusScene.fs          WebGL focus render controls (+ FocusShaders.fs pano shader)
GuiFocus.fs            focus panel head + FocusScene mounts
GuiInspector.fs        mode-contextual bottom dock
View.fs                view function + App module
ShaderCache.fs         FShade AOT cache
Program.fs             Boot.run entry
```

The server (`src/Superserver/`):

```
MeshLoader.fs    OBJ parse + centroid file + atlas paths
MeshCache.fs     lazy Embree scene + BbTree cache (permanent)
MeshAnalysis.fs  sphere contact-ring tracing, patch sampling
MeshProbe.fs     N-mesh M3C2 probe
RegMath.fs       weighted Umeyama landmark solve (Jacobi SVD)
QueryHandlers.fs HTTP query handlers
Handlers.fs      routing
Program.fs       ASP.NET startup
```

All query coordinates are **absolute world space**; the server converts to mesh-local by subtracting the mesh centroid.

## Render pipeline

**One forward pass into the main framebuffer.** The mesh shader (`MeshShaders.fs`, `MeshShader.shade`) is the only thing that writes per-fragment depth:

- Per-fragment α = `MeshActive ? lerp(GhostOpacity, 1, mask) : GhostOpacity`, with `mask` = the pin-blob component (1 inside any pin's `InnerRadius`, 0 outside — gated by the **Isolate pins** toggle; 1 when there are no pins or the toggle is off).
- **α-gated depth:** fully-solid fragments write their natural `gl_FragCoord.z`; ghost / outside fragments write 1.0 (far). So translucent ghost geometry never occludes opaque surfaces *and* picks pass straight through it to the surface behind.
- **Ghost fragments use the uniform mesh palette colour**, so a ghosted silhouette reads as one shape regardless of rendering mode.
- Pins / lines / coordinate cross / labels render in the same forward pass: pin geometry with `DepthTest.LessOrEqual` (occluded by foreground geometry — the spatial cue), the coordinate cross + tick labels in `passOne` with `DepthTest.None` (always on top). The 3D correspondence constellation draws on top (`DepthTest.None`) so it stays visible.
- Pin centres + radii are stored in metric world-space on the model and converted to render-space (`* datasetScale`) on upload.

**`Sg.DepthMask` is never used.** It is buggy in this Aardvark / Aardworx WebGL build and silently breaks the depth pipeline. Ordering is steered with `Sg.DepthTest` + `Sg.Pass` alone. This violates the textbook "translucent should not write depth" rule, but it is the only configuration that renders correctly in this stack.

**Image-space outlines** (`OutlineView.fs`, gated by the gear toggle) are the one offscreen pass: meshes render into an MRT G-buffer (world normal + depth, palette colour + coverage mask), and a fullscreen edge-detect pass paints per-mesh outlines. Ordinary FBOs are fine in this stack.

**Picking** uses Aardvark's pixel picker. `e.Location.Depth < 0.9999` gates valid hits (background misses leave depth at the clear value 1.0). The focus-panel correspondence pick is different: a 2D-frame click is inverted to a world ray (orthographic for Top, cylindrical for Pano) and raycast server-side. Note `Sg.OnTap` / `OnDoubleTap` fire on background misses too, so any handler that builds state from the hit must gate on the depth check.

**FShade shaders are float32-only and lambda-free.** WebGL2 (ESSL3) rejects `double`/`dvec`, so `float`, `Constant.Pi`, `V3d`/`V2d`, and `member _ : float` uniforms all fail in-browser; use `3.1415927f`, `V3f`/`V2f`, `: float32`. A local `let f x = …` inside a shader body reads as a lambda FShade can't compile — inline it. Neither `dotnet build` nor the `fshadeaot` step catches these; only the in-browser compile does.

## Server query performance

Costly queries scale with mesh count × sample density. Rules learned the hard way:

- **Never issue per-mesh HTTP loops sequentially.** Multi-mesh raycasts go through the client-side parallel fan-out (`Query.rayHitMany`); if a multi-mesh operation gets hot, add a batched server endpoint with `Parallel.For` instead.
- **Embree `Scene.Intersect` is thread-safe** — server inner loops parallelise over independent meshes/samples.
- **Debounce user-driven triggers** with a `CancellationTokenSource` + a generation counter so only the final drag position hits the server, and at most one fetch is in flight per invalidation.
- **Mesh caches are warmed at dataset load** by the bbox handler.

## Tests

`src/Supertests` is a plain console runner (no test-framework packages) that compiles the pure modules directly (`RegistrationModel.fs` + `RegMath.fs`) and covers the weighted Umeyama solver (recovery, reflections, weights, collinearity, <3-pairs rejection), the `RegJson` correspondence + last-solve round-trips, the conditioning eigenvalue helpers, the registration readiness engine, and the camera fly-to math:

```bash
dotnet run --project src/Supertests        # exit code = number of failures
```

`tools/integration.mjs` exercises the HTTP flow end-to-end against a running server (closest-point seeding → known rigid perturbation → `/query/lsq-pairs` recovers its inverse → `/query/probe` median error shrinks → patch frame override echo):

```bash
ASPNETCORE_URLS=http://localhost:8002 dotnet run --project src/Superserver   # terminal 1
node tools/integration.mjs                                                   # terminal 2
```

## Style

- Light theme, high contrast, print-appropriate.
- The GUI must be readable to a non-expert at first glance.
- No comments unless the logic is non-obvious; concise code; no unnecessary abstractions.
- See `CLAUDE.md` for the detailed conventions and pitfalls an AI assistant should follow.
