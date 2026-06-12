# Superprojekt

Research prototype for interactive 3D inspection of geological mesh and pointcloud datasets. Two F# projects:

- **Superserver** — ASP.NET Core + Giraffe. Serves mesh data and runs spatial queries (Embree BVH, closest-point, multi-mesh raycasts, isolines, curvature ridges, surface patches, weighted landmark solves, ICP).
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
- **ScanPins (annotations).** Place a 3D anchor on a surface. Each pin has an **Inner radius** (hard truth: α = 1 and full evaluation weight inside) and a **Falloff radius** (exponential decay beyond inner). Both stored in metric world-metres; the falloff slider is relative to the inner radius. Pin Point / Line / Patch payloads carry derived analysis. The pin's influence renders as thin outlines in the pin's colour: an **equator ring** (perpendicular to the probe axis at the inner radius) plus per-mesh **contact rings** — the exact sphere–surface intersection curves, computed server-side and cached (invalidated on radius / centre / registration changes). The curves depth-test normally, so foreground geometry occludes them.
- **N-mesh distance probe (M3C2).** Every Point-payload pin runs a server-side probe: a cylinder along the locally-estimated surface normal (PCA over the reference mesh inside the pin sphere) samples **all visible meshes** and returns one signed-distance distribution per mesh, re-centred so 0 = the reference mesh's median. The pin card shows a **vertical violin chart** (signed distance on the y axis, positive up; one column per mesh with KDE violin, median tick, IQR whisker, count badge; planarity badge, y-range presets, lock-order toggle) and a **three-source stacked bar** decomposing the error into dataset spread / algorithm offset / local conditioning. Probes recompute lazily and invalidate on radius/centre/reference/transform/visibility changes. Cylinder length is auto (1.1 × scene extent along the normal, capped at 100 m) or manual via the adjustment flyout.
- **Chart ↔ 3D linking.** Hovering the violin chart drives a cyan **slicing plane** in 3D, orthogonal to the probe axis at the hovered signed distance (clipped to the probe cylinder; hold **Alt** to extend it scene-wide). While the cursor is active, intersected meshes darken slightly and a bright **contact-line band** (~±20 cm, same cyan) traces where the plane meets each surface — scene-wide under Alt, cylinder-clipped otherwise. Hovering the 3D surface inside the probe cylinder draws the matching **elevation cursor line** on the chart and the same contact-line band at the hovered elevation. Hovering a chart column highlights that mesh in 3D (all others ghost to α 0.2); clicking a column makes the highlight **sticky** (thick border) until you click it again, another column, or anywhere outside the chart.
- **Hover probe.** Ctrl-click any surface for a transient mini-violin chart at the cursor (radius = 5% of the scene bbox diagonal). Dismissed by Escape, clicking elsewhere, or after a few seconds.
- **Filter chain.** Visibility is the conjunction of three filters: MeshActive toggle → lasso region → pin blob field. Both lasso and blob must agree for a fragment to be fully opaque; inside-lasso-outside-blob fragments fall to ghost level. The pin blob filter is gated by the **Isolate pins** toggle in the gear popover; the lasso filter by the `◉/○` button on its card.
- **Ensemble registration (two-stage, preview-first).** Designate a reference mesh (★ in the mesh panel or registration card — every error metric is relative to it; there is no absolute ground truth). **Stage 1 · Coarse:** Point pins can be promoted to **correspondence landmarks** — enabling correspondence auto-seeds one anchor per other mesh (closest-point projection of the pin's reference anchor, reviewed in an accept/reject modal), refinable three ways: co-oriented **patch small-multiples** (orthographic, atlas-textured footprints of every mesh in a shared frame; click to set the anchor), a **one-shot 3D pick** (target mesh solid, reference at 30 %, everything else ghosted; one depth-gated click), or **Shift+click on a violin column** (anchor at that signed distance along the probe axis). With ≥3 accepted pairs per moving mesh, *Solve coarse* runs a weighted rigid landmark solve (Umeyama/Arun, server-side) per mesh in parallel. **Stage 2 · Fine:** the established ICP (traditional, or region-restricted where pin centres + falloff radii weight the solve), Gauss-Newton point-to-surface with centroid-recentred linearization and trimmed correspondences (3× median gate), starting from the committed transforms. **Both stages land in a pending preview** — meshes render at the previewed pose with their committed pose as a slate ghost, the violin charts split into committed/preview half-violins with a Δ-median arrow, an RMS before → after table with convergence sparklines and collinearity badges sits in the card — until you **Commit** (appends a roll-backable history step) or **Discard**. The newest history step can be rolled back; Reset rolls back everything. Destructive actions (pin placement, retarget, fusion, dataset switch, anchor picking) are blocked while a preview is pending.
- **Registration workflow panel.** Top-level `⚲ Workflow` opens a one-stop panel over the registration state: per-mesh status chips (reference / coarse / fine / skipped) with last-solve RMS and conditioning badges, a per-pin anchor-dot matrix, a live diagnostics list from a shared readiness engine ("what exactly is missing", each entry with a one-click navigation action, the green Ready entries run the solves), the pending preview banner with commit/discard, and per-mesh error stats with RMS sparklines across committed steps. Rows fly the camera to their mesh/pin (orientation kept). The panel is a pure view — all mutations go through the existing messages and it never issues server queries.
- **Error provenance overlay.** Per-mesh sensor type + dataset-error override, combined with ICP algorithm residual and a local-conditioning heuristic over the anchors into a tunable heatmap. While a registration preview is pending, a third **Diff** mode paints the signed change of combined error (blue = improved, red = degraded) and masks everything below the 1.96·√(σ_ref²+σ_M²) detection limit to ghost level; it auto-reverts on commit/discard.
- **Fusion mode.** Top-bar `◈ Fusion` renders all visible meshes into an offscreen pass (own colour + depth target) where per-fragment depth carries combined error, so the lowest-error surface wins the depth test; the composite is drawn back as a fullscreen quad. Picking raycasts every visible mesh and keeps the same lowest-error winner.
- **Retarget.** Re-project the existing pins' anchors onto a chosen target mesh (server closest-point per pin), review the per-pin projection distances in a card, accept/reject individually, then commit.
- **User-study mode.** A token link (`/s/{token}`) turns the app into a guided study session: chrome is replaced by a study bar (progress dots, an always-visible goal line, a Next button gated on step completion), instructions appear as overlays or anchored tooltips with a live done-checkmark, and questions (choice / scene-click / numeric / free-text / SUS / Raw-TLX / ICE-T grids) dock in a task pane. Features are whitelisted per phase and filtered per condition (FULL vs NUM — NUM swaps the violin chart for a numeric table and hides the heatmaps). Studies are defined in `src/Superserver/studies/{id}/config.json` (+ a never-served `secret.json` with planted answers and TRE check points); the server stores sessions, telemetry, answers and registration-accuracy scores as per-session JSONL, assigns conditions balanced, and issues an HMAC completion code once every required step is done. A demo preview (token-free, flagged, exitable) lives in the gear popover.
- **Workspace save / load.** Serialise the session (dataset, pins, transforms, visibility, sensors, lasso, registration, camera, settings) to JSON via the gear popover and reload it later. Hand-rolled JSON in `Persistence.fs`; in-memory otherwise.
- **Panorama.** Top-bar `▦ Pano` opens a floating panel showing a cylindrical view from a synthetic viewpoint at the scene-bbox centre (no dataset ships real imagery, so one is generated per dataset on load). The meshes are rendered into a cubemap from that pose and reprojected cylindrically. **Photo / Render / Blend** modes (Photo = reference state, Render = live state, Blend = the disagreement between them), anchor markers projected into the view, click-to-place anchors via a server raycast through the pose, and a fly-to-pose button.

## Architecture

Elm-style: `Model` → `Update.update` → `View.view` → `Boot.run`.

The client compiles in this order (`src/Superprojekt/Superprojekt.fsproj`):

```
MeshData.fs                          mesh fetch / parse, shared HttpClient
ProbeModel.fs                        M3C2 probe result / state types
Query.fs                             server query wrappers (Async)
CameraModel.fs / .g.fs               OrbitState [<ModelType>]
OrbitController.fs                   orbit camera + messages
RegistrationModel.fs                 correspondence anchors, RegStep log, pending preview, RegJson
StudyModel.fs                        study config DTOs + parser, predicate engine, runtime types
StudyApi.fs                          /api/study/* wrappers + entry-token boot flag
StudyTelemetry.fs                    telemetry batcher (flush/backoff/throttle)
ScanPinModel.fs / .g.fs              ScanPin + card types
PinGeometry.fs                       icosphere + footprint geometry
Model.fs / .g.fs                     application Model [<ModelType>]
Persistence.fs                       workspace JSON serialise / apply
LineShader.fs                        flat colour + pixel-constant 3D lines
Primitives.fs                        compact GUI widgets + shared helpers
Messages.fs                          Message DU (incl. StudyMessage)
StudyUpdate.fs                       server actions + study reducer / event derivation / gate
CardUpdate.fs / ScanPinUpdate.fs     sub-reducers
Update.fs                            main reducer
MeshShaders.fs                       mesh / fusion / panorama shaders
MeshView.fs                          per-mesh scene nodes
FusionView.fs                        offscreen fusion pass + composite
PanoramaView.fs                      cubemap capture + cylindrical reproject
ScanPinScene.fs                      pin sg nodes
SceneGraph.fs                        scene composition + coordinate cross
CardsPin.fs / Cards.fs               floating pin diagrams + card chrome
GuiTopBar.fs / GuiPanels.fs          top bar + left panel
GuiOverlays.fs / GuiCards.fs         overlays + cards
GuiWorkflow.fs                       registration workflow panel
GuiStudy.fs                          study bar, overlays, task pane, question widgets
View.fs                              view function + App module
ShaderCache.fs                       FShade AOT cache
Program.fs                           Boot.run entry
```

The server is much smaller (`src/Superserver/`):

```
MeshLoader.fs                        OBJ parse + centroid file + atlas paths
MeshCache.fs                         lazy Embree scene + BbTree cache
MeshAnalysis.fs                      isoline / ridge tracing, patch sampling
MeshProbe.fs                         N-mesh M3C2 probe
MeshIcp.fs                           ICP solver
RegMath.fs                           weighted Umeyama landmark solve
StudyConfig.fs                       study config/secret parsing + validation
StudyStore.fs                        session stores, balancing, TRE scoring, HMAC codes
QueryHandlers.fs                     HTTP query handlers
StudyHandlers.fs                     /api/study/* handlers
Handlers.fs                          routing
Program.fs                           ASP.NET startup
```

## Tests

`src/Supertests` is a plain console runner (no test-framework packages) that compiles the pure registration and study modules directly — the weighted Umeyama solver, the commit/rollback registration log, the workspace JSON round-trips, the study predicate engine and step gating, study config validation, the study stores (balanced assignment, TRE scoring, HMAC completion codes, gold screening), the registration readiness engine, the camera fly-to math and the solve-diagnostics persistence:

```bash
dotnet run --project src/Supertests        # exit code = number of failures
```

`tools/integration.mjs` exercises the HTTP flow end-to-end (closest-point seeding → known rigid perturbation → `/query/lsq-pairs` recovers its inverse → `/query/icp` reduces RMS → `/query/probe` median error shrinks → patch frame override echo) against a running server:

```bash
ASPNETCORE_URLS=http://localhost:8002 dotnet run --project src/Superserver   # terminal 1
node tools/integration.mjs                                                   # terminal 2
node tools/study-integration.mjs                                             # full study walk: balance, route security, gold echo, resume, completion codes
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

- **Never per-mesh loops over HTTP issued sequentially.** Multi-mesh raycasts go through the client-side parallel fan-out (`Query.rayHitMany`); if a multi-mesh operation gets hot, add a batched server endpoint with `Parallel.For` instead.
- **Embree `Scene.Intersect` is thread-safe** — outer loops use `Parallel.For` with per-thread `ResizeArray` hit buffers.
- **Debounce user-driven triggers** with a `CancellationTokenSource` so only the final drag position hits the server.
- **Mesh caches are warmed at dataset load** by the bbox handler.

## Style

- Light theme, high contrast, print-appropriate.
- GUI must be readable to a non-expert at first glance.
- No comments unless the logic is non-obvious; concise code; no unnecessary abstractions.
- See `CLAUDE.md` for the detailed conventions an AI assistant should follow.
