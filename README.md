# Superprojekt

Research prototype for interactive 3D inspection and **registration** of geological mesh datasets (multi-epoch scans of the same terrain). Two F# projects:

- **Superserver** — ASP.NET Core + Giraffe. Serves mesh data and runs spatial queries (Embree BVH ray/closest-point, sphere contact rings, per-vertex signed distance, N-mesh M3C2 probes, and a weighted rigid landmark solve). Also hosts the WASM client.
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

- **Load & explore.** Orbit camera (drag to rotate / scroll to zoom, or touch); **middle-drag** (or **Shift + left-drag**) pans the view across the **XY plane** (constant elevation); double-tap geometry to recenter — both panning and recenter work on ghosted meshes too; hold **Space** for distraction-free fullscreen; **Alt + scroll** cycles which mesh under the cursor is *isolated* (kept solid while the rest ghost). Per-mesh image-space outlines (silhouettes plus crisp global-Z elevation isolines, both edge-detected) are always on — including on ghosted / hidden meshes, so a disabled or isolated-away scan keeps its outlines and contour lines over its faint ghost; their tuning (outline edge threshold, isoline count) alongside rendering mode (Textured / Shaded / Slope-colour), ghost silhouette + opacity, shading strength, and slope threshold live in the **⚙ gear** popover.
- **Reference peek & isolation.** Hold the **👁 Peek** button (or **R**) to show only the reference mesh; hold **◎ Isolate** (or **I**) to momentarily reveal only the pin regions. The global **👻 Ghost** toggle sets the *floor* every non-active mesh drops to — faint **Ghost** context, or fully **Hidden** — and governs how solo / peek / isolation look (the opacity slider stays in the ⚙ gear).
- **Three-mode workflow.** The left rail has exactly three modes — **Overview · Correspondence · Inspect** — that share one selection state. The containers (rail, 3D viewport, focus panel, bottom dock) never move between modes; only their content changes. Everywhere, **hover = peek** and **click = focus** — clicking a rail row, a focus tile, *or* a mesh in the 3D view focuses it (and in **Inspect** also isolates it). A single global **Before / After** toggle in the top bar (enabled once anything is solved) flips the whole app between each mesh's load pose and its solved pose.
- **Overview (setup gate).** The rail lists every mesh with a colour swatch, visibility toggle, sensor-type tag (cycles), the reference **★**, an isolate **◐**, and a frame-camera button; hovering a row peek-isolates that mesh in 3D, clicking focuses it. The bottom dock is a roster (sensor · triangle count · overlaps-the-reference · visibility). The focus panel shows atlas-textured WebGL tiles of each mesh. Overview is deliberately the "inputs" step — no error overlays appear in 3D here.
- **ScanPins.** Place a pin on a surface (**Correspondence → ○ Place pin**, then tap a surface). While placing, the terrain dims to a ghost and a moving **flashlight** reveals a solid patch under the cursor (plus any existing pins) — a live preview of where the pin lands; placement can also fall through a ghost onto a hidden surface via a server raycast. Each pin has a metric **inner radius** and runs a server-side **M3C2 distance probe**: a 20 m cylinder along the locally-estimated surface normal (PCA over the reference inside the pin sphere) samples every visible mesh and returns one signed-distance distribution per mesh, re-centred so 0 = the reference median. The pin's influence renders as a thin equator ring plus the exact per-mesh **sphere–surface contact rings** (server-computed, cached, invalidated on radius / centre / registration change).
- **Correspondence registration.** Designate a reference mesh (**★** — every error metric is relative to it; there is no absolute ground truth). **Every pin is a registration pin**: on placement it auto-seeds one **correspondence marker** per other mesh by closest-point projection of the pin's reference marker, ROI-clamped to the pin. Markers are stored *mesh-local*, so the Before/After toggle moves each marker with its mesh. Refine a marker by hand: toggle **⊕ set point** in the focus panel and click the focused mesh's surface, or hit the **⊕** button on a correspondence row to place it directly in the main 3D view — that **isolates the row's mesh** to a solid surface (the rest dim to ghost) and you click it there. Both are the same server raycast (pick is pick — no region gate). These modes are off by default so the focus pans / the 3D view orbits freely; placing exits, and clicking any other button cancels. The manager's **⌖** button *locates* a correspondence: it solos that mesh, flies the 3D camera tight to the marker, and zooms the focus canvas onto it in one move — the focus head's **⤺ back** button restores the prior camera and visibility. **Solve** runs a weighted rigid landmark solve (Umeyama/Arun, server-side) for every mesh that has **≥3** markers, in parallel, and writes each one's solved transform. **Partial overlap is fine**: meshes short of 3 markers are flagged (not blocked) and simply stay at their load pose — the solve reports *N of M* and aligns the rest. A 3D **constellation** draws each marker, the haloed reference marker, and the lines between them — shown only while in Correspondence mode (Overview and Inspect hide the correspondence markers in both the 3D view and the focus panel). There is no preview/commit and no undo history — the solve writes the result directly and the Before/After toggle is the comparison.
- **Focus panel (right).** A **WebGL** large single of the focused mesh (full-res, **atlas-textured**, pan + mouse-anchored zoom; **⊕ set point** to place correspondences) over a strip of textured thumbnail tiles, one per visible mesh — each its own render control. A **Pano / Top** toggle drives the single (**Top** by default): **Top** is strictly orthographic, **Pano** a cylindrical unwrap (vertex shader). Click a tile to focus that mesh; **⟲ reset** recentres; hold **⇄ ref** to peek the reference; **⇄ link** links the views (a focus click then flies the 3D camera to that point, and a 3D recenter recenters the focus). Picking inverts the cursor to a ray and raycasts on the server (`Sg.OnTap` does not fire reliably in the secondary controls). In Correspondence mode the Top single also overlays each pin's bounding-sphere circle and a screen-fixed, always-visible glyph at the focused mesh's correspondence point.
- **Inspect.** With **no mesh soloed**, the **central 3D** paints the all-meshes **variance** map (disagreement of every visible moving mesh) on the reference, the moving meshes dropped to the ghost floor. Reference points that a moving mesh doesn't actually cover (its nearest surface is far away) are left blank rather than painting a spurious error. **Solo a moving mesh** (click its rail row / tile / the mesh in 3D) and the central 3D instead paints *that mesh's own* field — its difference heatmap, or its displacement magnitude — mirroring its focus tile, with the rest at the floor. The bottom **dock** holds the selected pin's **distribution** (jittered raw probe samples + median/IQR box on a shared signed-distance axis with the ±LoD₉₅ band) and a **shift readout** (the focused mesh's centroid displacement split into vertical-datum / horizontal + rotation angle, derived from its solved transform). The rail's **mesh list** carries each mesh's solve-state flag (✓ solved · ready · `k/3` insufficient). The **focus tiles** recolour per channel (toggled in the dock): **difference** (signed M3C2 / Δz vs reference, diverging blue↔grey↔red about zero with a gear-adjustable range, per-vertex colour) or **displacement** (the large single shows white surface + load→solved **arrow glyphs** coloured by magnitude; tiles show a magnitude heatmap) — all rendered in WebGL. The intrinsic **incidence / range / triangle-shape** acquisition heatmaps are in the rail — incidence and range are measured from each mesh's **scan sensor** (its panorama centre), not the interactive camera.
- **Linked highlighting.** Hover anything — a rail row, a dock row, a focus tile — and the same object lights up everywhere at once, the 3D constellation glyphs included. There is a single shared selection record; no panel-to-panel wiring (the 3D constellation is a pure output: it responds to the shared hover, it isn't a hover source itself).

## Architecture

Elm-style: `Model` → `Update.update` → `View.view` → `App` (wired through Aardvark.Dom's `Boot.run`). The whole app state is one `[<ModelType>]` `Model`; `Adaptify` generates the adaptive `*.g.fs` views from it (never edit those by hand — re-run `adaptify.sh`).

The client compiles in this order (`src/Superprojekt/Superprojekt.fsproj`):

```
MeshData.fs            mesh fetch / parse, ApiConfig, shared HttpClient
ProbeModel.fs          M3C2 probe result / state types
Query.fs               server query wrappers (Async)
CameraModel.fs / .g.fs OrbitState [<ModelType>]
OrbitController.fs     orbit camera + messages
RegistrationModel.fs   ScanPinId, correspondence anchors, readiness engine, fly-to math (WASM-free, shared with Supertests)
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
MeshAnalysis.fs  sphere contact-ring tracing
MeshProbe.fs     N-mesh M3C2 probe
RegMath.fs       weighted Umeyama landmark solve (Jacobi SVD)
QueryHandlers.fs HTTP query handlers
Handlers.fs      routing
Program.fs       ASP.NET startup
```

All query coordinates are **absolute world space**; the server converts to mesh-local by subtracting the mesh centroid.

## Render pipeline

**One forward pass into the main framebuffer.** The mesh shader (`MeshShaders.fs`, `MeshShader.shade`) is the only thing that writes per-fragment depth:

- Per-fragment α = `MeshActive ? lerp(GhostOpacity, 1, mask) : GhostOpacity`, with `mask` = the pin-blob component (1 inside any pin's `InnerRadius`, 0 outside — gated by pin isolation, which defaults **on in Correspondence / off elsewhere** and can be held momentarily with **◎ Isolate** / **I**; 1 when there are no pins or isolation is off). `GhostOpacity` is the global ghost *floor* (👻 toggle): faint context, or 0 = hidden.
- **α-gated depth:** fully-solid fragments write their natural `gl_FragCoord.z`; ghost / outside fragments write 1.0 (far). So translucent ghost geometry never occludes opaque surfaces *and* picks pass straight through it to the surface behind.
- **Ghost fragments use the uniform mesh palette colour**, so a ghosted silhouette reads as one shape regardless of rendering mode.
- Pins / lines / coordinate cross / labels render in the same forward pass: pin geometry with `DepthTest.LessOrEqual` (occluded by foreground geometry — the spatial cue), the coordinate cross + tick labels in `passOne` with `DepthTest.None` (always on top). The 3D correspondence constellation draws on top (`DepthTest.None`) so it stays visible.
- Pin centres + radii are stored in metric world-space on the model and converted to render-space (`* datasetScale`) on upload.

**`Sg.DepthMask` is never used.** It is buggy in this Aardvark / Aardworx WebGL build and silently breaks the depth pipeline. Ordering is steered with `Sg.DepthTest` + `Sg.Pass` alone. This violates the textbook "translucent should not write depth" rule, but it is the only configuration that renders correctly in this stack.

**Image-space outlines** (`OutlineView.fs`, always on) are the one offscreen pass: meshes render into an MRT G-buffer (world-Z band parity + depth, palette colour + coverage mask), and a fullscreen edge-detect pass paints per-mesh silhouette outlines plus global-Z elevation isolines. Ordinary FBOs are fine in this stack.

**Picking** uses Aardvark's pixel picker. `e.Location.Depth < 0.9999` gates valid hits (background misses leave depth at the clear value 1.0). The focus-panel correspondence pick is different: a 2D-frame click is inverted to a world ray (orthographic for Top, cylindrical for Pano) and raycast server-side. Note `Sg.OnTap` / `OnDoubleTap` fire on background misses too, so any handler that builds state from the hit must gate on the depth check.

**FShade shaders are float32-only and lambda-free.** WebGL2 (ESSL3) rejects `double`/`dvec`, so `float`, `Constant.Pi`, `V3d`/`V2d`, and `member _ : float` uniforms all fail in-browser; use `3.1415927f`, `V3f`/`V2f`, `: float32`. A local `let f x = …` inside a shader body reads as a lambda FShade can't compile — inline it. Neither `dotnet build` nor the `fshadeaot` step catches these; only the in-browser compile does.

## Server query performance

Costly queries scale with mesh count × sample density. Rules learned the hard way:

- **Never issue per-mesh HTTP loops sequentially.** Multi-mesh raycasts fan out in parallel (`Async.Parallel`) rather than looping; if a multi-mesh operation gets hot, add a batched server endpoint with `Parallel.For` instead.
- **Embree `Scene.Intersect` is thread-safe** — server inner loops parallelise over independent meshes/samples.
- **Debounce user-driven triggers** with a `CancellationTokenSource` + a generation counter so only the final drag position hits the server, and at most one fetch is in flight per invalidation.
- **Mesh caches are warmed at dataset load** by the bbox handler.

## Tests

`src/Supertests` is a plain console runner (no test-framework packages) that compiles the pure modules directly (`RegistrationModel.fs` + `RegMath.fs`) and covers the weighted Umeyama solver (recovery, reflections, weights, collinearity, <3-pairs rejection), the conditioning eigenvalue helpers, the registration readiness engine, and the camera fly-to math:

```bash
dotnet run --project src/Supertests        # exit code = number of failures
```

`tools/integration.mjs` exercises the HTTP flow end-to-end against a running server (closest-point seeding → known rigid perturbation → `/query/lsq-pairs` recovers its inverse → `/query/probe` median error shrinks):

```bash
ASPNETCORE_URLS=http://localhost:8002 dotnet run --project src/Superserver   # terminal 1
node tools/integration.mjs                                                   # terminal 2
```

## Style

- Light theme, high contrast, print-appropriate.
- The GUI must be readable to a non-expert at first glance.
- No comments unless the logic is non-obvious; concise code; no unnecessary abstractions.
- See `CLAUDE.md` for the detailed conventions and pitfalls an AI assistant should follow.
