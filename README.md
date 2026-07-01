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

- **Load & explore.** Orbit camera (drag to rotate / scroll to zoom, or touch); **middle-drag** (or **Shift + left-drag**) pans across the **XY plane** (constant elevation); double-tap geometry to recenter — both work on ghosted meshes; hold **Space** for distraction-free fullscreen; **Alt + scroll** cycles which mesh under the cursor is *isolated* (kept solid while the rest ghost). Per-mesh image-space outlines (silhouettes plus crisp global-Z elevation isolines, both edge-detected; isolines in a faint neutral grey) are always on — including on ghosted / hidden meshes. Their tuning alongside rendering mode (Textured / Shaded / Slope-colour), ghost opacity, shading strength, and slope threshold live in the **⚙ gear** popover.
- **Isolation & overlays.** Hold **◎ Isolate** (or **I**) to momentarily reveal only the pin regions; hold **🎨 Overlays** (or **O**) to greyscale the whole scene *except* the pin colours, so pin correspondence across views is unmistakable. The **ghost floor** (in the **⚙ gear**) sets what every non-active mesh drops to — faint **Ghost** context, or fully **Hidden** — and governs how solo / isolation look.
- **Three-mode workflow.** The left rail has exactly three modes — **Overview · Correspondence · Inspect** — that share one selection state. The containers (rail, 3D viewport, focus panel, bottom dock) never move between modes; only their content changes. Everywhere, **hover = peek** and **click = focus**, and **any selection tightly syncs both cameras** onto that spot (fly the 3D camera + show it in the focus). A single global **Before / After** toggle in the top bar (enabled once anything is solved) flips the whole app between each mesh's load pose and its solved pose; a spring-loaded **Peek** button beside it momentarily shows the *other* state while held. The top bar also carries the **reconstruction-readiness** status (shown only in Correspondence).
- **Tiles are the mesh browser.** The focus panel's tiles are the single mesh browser: each tile selects (focus), **peek-isolates the mesh in 3D on hover**, and carries the per-mesh controls that live *once* there — reference **★**, visibility, **◐ isolate**. **All** meshes are tiled (hidden ones dimmed) so a hidden mesh can be re-enabled; the reference tile is ringed gold with a **★**. In **Overview** the focus panel is *just* the tiles (no large single); the rail is a mesh roster and the dock a focused-mesh summary. Meshes display a **friendly name** — the file name with the roster's common prefix/suffix stripped, so `job_0789, job_0791, …` read as `0789, 0791, …`.
- **ScanPins & identity.** Place a pin (**Correspondence → ○ Place pin**, then tap a surface). A moving **flashlight** previews where the pin lands even over ghosted terrain (server raycast). Every pin gets an immutable **identity triple** at creation — a preattentive **glyph**, a random 2-char **code**, and a distinct **pin colour** (a palette separate from the mesh palette) — its *only* identity everywhere (there is no free-text pin name): the matrix row, a 3D flag label above the pin, the focus chip, and its distribution samples. A pin has a metric **inner radius** and runs a server **M3C2 probe** (a 20 m cylinder along the PCA surface normal of the reference inside the pin sphere) returning one signed-distance distribution per mesh, re-centred so 0 = the reference median; its influence renders as an equator ring plus per-mesh **sphere–surface contact rings** (cached; invalidated on radius / centre / registration change).
- **The matrix.** In Correspondence + Inspect the left rail **is** the **pin × mesh difference matrix**: rows = pins (glyph · code · colour), cells = the before/after signed distance to the reference painted on the linear-diverging colormap (out-of-ROI → a faint hatch). Cells are **visibility-stable** — hiding or isolating a mesh never blanks or recolours them (the probe covers every mesh) — and always clickable. Click a pin row to select it (mirrored in the focus panel — chip + overlay); click a mesh column to focus that mesh (identical to clicking its focus tile, and the column highlights when focused — the matrix and focus panel stay linked both ways); click a cell to select that **(pin, mesh)** and *locate* it (solo + fly the 3D camera + zoom the focus); **click the same cell again to clear the selection and un-isolate**. Deleting a pin (**✕**) asks for confirmation first. It is the sole pin / correspondence browser (columns show only mesh colour + number — the per-mesh controls live on the tiles).
- **Correspondence registration.** Designate a reference mesh (**★** — every error metric is relative to it). **Every pin is a registration pin**: on placement it auto-seeds one **correspondence marker** per other mesh by closest-point projection of the pin's reference marker, ROI-clamped to the pin; markers are stored *mesh-local* so Before/After moves each with its mesh. Refine a marker by hand with **one armed editor**: **✎ edit point** (offered on the selected pin + focused mesh, the reference included) arms it, then clicking in **either** the focus *or* the 3D view sets the point — ROI-clamped, with the mesh isolated and a live cyan ghost previewing in both views — and it **stays armed** until you disarm. **Solve** runs a weighted rigid landmark solve (Umeyama/Arun, server-side) for every mesh with **≥3** markers, in parallel. **Partial overlap is fine**: meshes short of 3 markers are flagged (not blocked) and stay at their load pose — the solve reports *N of M*. A 3D **constellation** draws each marker + lines to the haloed reference marker (Correspondence only). No preview/commit and no undo — the solve writes directly and Before/After is the comparison.
- **Focus panel (right).** A **WebGL** large single of the focused mesh (full-res, **atlas-textured**, pan with **left- or middle-drag** + mouse-anchored zoom) over a strip of per-mesh tiles. A **Pano / Top** toggle drives the single (**Top** by default, strictly orthographic; **Pano** a cylindrical unwrap). The panel is **resizable** — drag its left edge (aspect-locked, with a visible grip bar). Head buttons: **✎ edit point** (arm correspondence editing), **⤺ back** (restore camera mid-locate), **⇄ link** (focus↔3D camera sync), **⟲ reset**. Picking inverts the cursor to a server raycast (`Sg.OnTap` does not fire reliably in the secondary controls). In Correspondence the Top single overlays each pin's bounding-sphere circle + a screen-fixed glyph at the focused mesh's marker.
- **Inspect (arity by rendering).** With **no mesh soloed**, the **central 3D** paints the all-meshes **variance** map on the reference (moving meshes at the ghost floor); reference points a moving mesh doesn't cover are left blank. **Solo a moving mesh** and the central 3D paints *that mesh's own* field — difference or displacement — while the reference (and the rest) render as an **empty outline** for overlap context, so the metric arity is readable from rendering alone. The bottom **dock** holds the **distribution** — every pin's ROI samples on a shared signed-distance axis, coloured **by pin**, with the ±LoD₉₅ band + axis — plus the channel toggle and a **shift readout**. The distribution is **brushable both ways**: drag samples in the chart to light their surface cells in 3D, and hover the 3D surface to light the nearby samples. The **difference** map is **linear-diverging** (blue↔neutral↔red — no central flat-spot, small deviations stay visible) with the ±LoD neutral gate, used identically in the matrix cells, the focus tiles, and the soloed 3D. The intrinsic **incidence / range / triangle-shape** acquisition heatmaps (measured from each mesh's **scan sensor** = its panorama centre) are in the dock.
- **Linked highlighting.** Hover anything — a matrix row, a legend chip, a tile, the 3D — and the same object lights up everywhere at once, the 3D constellation and the brushed samples included. One shared selection record; no panel-to-panel wiring.

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
ScanPinScene.fs        pin sg nodes + constellation + brushed-sample markers
SceneGraph.fs          scene composition + coordinate cross + reference/focus outlines
FocusShaders.fs        FShade pano (cylindrical) vertex + focus colour fragment
FocusScene.fs          WebGL focus render controls (single + tiles) + brushSamples
GuiTopBar.fs           top bar + before/after + peek + readiness hint + gear popover
GuiOverlays.fs         toast, scale bar, orientation indicator, wheel label
GuiRail.fs             three-mode left rail (Overview roster · pin×mesh matrix)
GuiFocus.fs            focus panel head + FocusScene mounts (resize handle)
GuiInspector.fs        mode-contextual bottom dock (+ distribution + brush bridge)
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
