# Superprojekt — assistant notes

Research prototype for interactive 3D mesh/pointcloud visualisation. Two F# projects:

- **Superserver** — ASP.NET Core + Giraffe. Serves mesh data and runs spatial queries (Embree BVH, closest-point, multi-mesh raycasts, surface patches, sphere contact rings, per-vertex signed distance, ICP). Runs on `http://localhost:5000` and also hosts the WASM client.
- **Superprojekt** — Blazor WASM client. Aardvark.Dom Elm-style architecture, WebGL2 rendering. Must work on desktop and mobile; the client stays thin and pushes heavy compute to the server.

See `README.md` for what the app does and how to run it.

## Style

- Light theme, high contrast, print-appropriate.
- GUI must be readable to a non-expert at first glance.
- No comments unless the logic is non-obvious.
- Concise code, no unnecessary abstractions, no premature helpers.

## Render pipeline (forward pass + optional fusion offscreen pass)

The default path is **one forward pass** into the main framebuffer (meshes → pins → cross/labels). The earlier hybrid-forward + WBOIT pipeline was removed (see commit history if you really need it).

**FBOs are allowed.** The old ban existed only because the removed WBOIT code was fragile; the Aardworx WebGL backend handles ordinary multi-target / multi-pass pipelines fine (it uses MRT + a pick buffer internally). When **Fusion mode** is on, the normal meshes are suppressed and re-rendered in a separate offscreen pass (`FusionView.fs`) with its own colour + depth buffer where per-fragment depth = combined error, so the lowest-error surface wins `LessOrEqual` depth-testing; that colour output is composited as a fullscreen quad into the main pass and pins/cross/labels still render normally on top. Fusion needs its own depth buffer because writing error-as-depth into the shared buffer would corrupt depth-testing for everything else. The offscreen target is just colour + depth — there is **no winner-id MRT target**; picking under fusion is a CPU raycast over the visible meshes (see Picking).

```
[ passZero ]
  • meshes      : MeshShader.shade → custom α + α-gated gl_FragDepth
  • pin geometry: DepthTest.LessOrEqual, alpha-blended
[ passOne ]
  • coordinate cross + tick lines + axis-tip/integer-metre labels
    DepthTest.None — always on top.
```

### Mesh shader (`MeshShaders.fs`, `MeshShader.shade`)

Inputs (custom record):
- `[<Color>] c : V4f` (from `DefaultSurfaces.diffuseTexture`)
- `[<Semantic("Normals")>] n : V3f`
- `[<Semantic("WorldPosition")>] wp : V4f`
- `[<Semantic("SurfaceDist")>] sd : float32` (per-vertex signed M3C2 distance for the A2 surface map; sentinel `1e30` = no encoding)
- `[<FragCoord>] fc : V4f`

Outputs (custom record):
- `[<Color>] color : V4f`
- `[<Depth>] depth : float32`

### Ghosting rules

Every mesh fragment ends up in exactly one of three states: **opaque** (α = 1), **ghost** (α = effective ghost level), or **invisible** (discarded). The rules, in evaluation order:

1. **Effective ghost level**: `ghost = GhostOpacity` if `GhostSilhouette` is on, else `0`. With the silhouette off, everything that would render as ghost is *discarded* instead (`α < 1e-4` → discard) — "no ghost" means invisible, not translucent.
2. **MeshActive** (mesh visibility toggle): `false` → α = `ghost` for the *whole mesh*, uniformly. The lasso/pin filters below are skipped — a hidden mesh's silhouette deliberately ignores them.
3. **Lasso** (only when a polygon is committed *and* `LassoEnabled`; disabling uploads `LassoPlaneCount = 0` while keeping the polygon): `lassoComponent = 1` iff `dot(plane.xyz, p) + plane.w ≤ 0` for **all** outward-facing half-space planes `V4f(nx, ny, nz, d)`, else `0`. No active lasso → `1`.
4. **Pin isolation** (only when pins exist *and* the "Isolate pins" toggle / `AnchorGhost` uniform is on): each pin is a hard sphere of `InnerRadius` (weight 1 inside). `blobComponent` = `1` inside any pin's `InnerRadius`, `0` outside every pin's radius. No pins or toggle off → `1`.
5. **Conjunctive mask**: `mask = lassoComponent * blobComponent`; final `α = lerp(ghost, 1.0, mask)`. Both filters must agree for full opacity — inside-lasso-outside-pins fragments are ghosted exactly like outside-lasso fragments. The pins carve the visible region *within* the lasso.

Consequences the rest of the stack relies on:

- **α-gated depth**: fragments with `α ≥ 0.99` write their natural window-space depth (`v.fc.Z`); everything else writes `1.0` (far plane) so ghost/outside fragments never occlude and never produce pixel-picks. A `fullySolid` clamp pins ghost/outside fragments below the threshold so only fully-solid surface writes depth. The explicit `gl_FragDepth = gl_FragCoord.z` write looks like a no-op but the stack only behaves correctly because of it — don't simplify it away.
- **Ghost colour is uniform**: fragments at ghost level always use the solid per-mesh palette colour, regardless of `RenderingMode`, so the silhouette reads as one shape.
- The provenance heatmap only paints **above-ghost** fragments. The blob uniform array stays uploaded even when "Isolate pins" is off, because the conditioning term loops over it (Gaussian σ = each pin's inner radius).

Uniforms set per draw call:
- `MeshActive`, `GhostOpacity` (pre-gated by `GhostSilhouette` on upload), `RenderingMode`, `MeshColor`, `ShadingStrength`, `SlopeThreshold`
- `LassoPlaneCount` + `LassoPlanes : Arr<N<32>, V4f>` — half-space planes (rule 3).
- `BlobCount` + `Blobs : Arr<N<32>, V4f>` = `(cx, cy, cz, innerRadiusRender)` + `AnchorGhost : int` (rule 4). Pin centres and radii are stored in **metric world-space** on the model and converted to render-space (`* datasetScale`) by `MeshView.pinBlobUniforms` — the single helper shared by the mesh and fusion scenes. Hard cap = 32.
- `DistanceEncoding : int` + `DistLoD`/`DistScale : float32` + `DistBrushOn : int` + `DistBrushLo`/`DistBrushHi : float32` — the A2/A3 surface map (below).

### Signed-distance surface colour map (A2/A3)

`DistanceEncoding = 1` paints the per-vertex `SurfaceDist` attribute as a **diverging blue↔red map centred at 0** (the canonical M3C2 depiction); within `±DistLoD` the fragment reads neutral so "not significant" looks near-neutral in 3D, and `DistScale` is the saturated end (robust 95th-pct |d|). Only **above-ghost** fragments are painted, and it is strictly **per selected mesh** — `DistanceEncoding` is 1 only for the soloed (chart-sticky) mesh, never all overlapping meshes at once (the participant's own constraint).

- The per-vertex distances come from `POST /api/query/region-distance` **in the target's served vertex order**, so a `SurfaceDist` `BufferView` aligns with `loaded.pos` by construction; non-encoded meshes bind a zero buffer of the same length (so the shared shader's attribute is always present). The fetch is a generation-guarded debounced postlude (`Update.ensureSurfaceDistance`, committed-pose transforms) and lives in `Model.SurfaceDistance : Map<mesh, float32[]>`.
- **A3 range brush** (`DistBrushOn`): dragging a y-interval on the violin sets `Model.SurfaceDistBrush`; fragments with `SurfaceDist` inside `[Lo,Hi]` stay vivid, the rest wash to context grey (focus+context). UI toggle = **⬢ 3D map** in the pin-card chart head; the soloed column picks the mesh. The chart JSON carries a `brushon` flag so the drag-brush gesture only arms when the map is on.
- **A3 ruler / A4 guide** are line geometry in `ScanPinScene.fs` (not shader): the elevation cursor draws a measured ruler from the reference surface to the picked distance, and an active 3D marker pick draws the reference-normal guide line.

### `Sg.DepthMask` is forbidden

Do not add `Sg.DepthMask` anywhere. It is buggy in this Aardvark/Aardworx WebGL build and silently breaks the depth pipeline. Ordering is steered with `Sg.DepthTest` + `Sg.Pass` alone. This means lines, pin geometry, and text all write depth too — that violates the textbook "translucent shouldn't write depth" rule but is the only combination that actually renders correctly in this stack. Comments in `LineShader.fs` and `SceneGraph.fs` (search for "Sg.DepthMask") capture the reason in code; leave them.

### Picking

Pixel picking via `Sg.OnTap` / `Sg.OnPointerMove`:

```fsharp
let pick =
    if e.Location.Depth < 0.9999 then Some e.WorldPosition else None
```

Background misses leave depth at the clear value (1.0); the gate is required. The α-gated depth write in the mesh shader is what makes this work — translucent ghost fragments leave depth at 1.0 so picks pass through them down to the opaque surface behind.

Note: `Sg.OnTap`/`OnDoubleTap`/`OnLongPress` fire on background misses too. Any handler that creates state from `e.WorldPosition` MUST gate on the depth check. Without it you get pins placed at infinity, cameras flying to empty space, and (in placement mode) an unbounded loop of bogus entries.

Under **Fusion mode** the pixel pick is bypassed: `View.fs` raycasts every visible mesh server-side and keeps the lowest combined-error hit, which matches the depth-test winner shown on screen.

## Adaptive performance (critical)

In the scene graph, **never depend on an entire record when you only need a subset of its fields**. The Elm-style model replaces entire records on every update, so an `AVal.map` over a full `ScanPin` (or similar) will fire on *any* field change — even fields the computation doesn't use.

**Rule: project individual fields into separate `aval`s early, then build the dependency graph from those.**

```fsharp
// BAD — rebuilds geometry on every pin change (cut plane drag, selection, etc.)
let geo = pinVal |> AVal.map (fun po -> ... use po.Prism and po.Stratigraphy ...)

// GOOD — only rebuilds when prism or stratigraphy actually change
let prismVal = pinVal |> AVal.map (fun po -> po |> Option.map (fun p -> p.Prism))
let stratVal = pinVal |> AVal.map (fun po -> po |> Option.bind (fun p -> p.Stratigraphy))
let geo = (prismVal, stratVal) ||> AVal.map2 (fun prism strat -> ...)
```

For scene graph nodes (`Sg.Text`, `sg { ... }`), this matters even more: rebuilding an `AList` of sg nodes destroys and recreates GPU resources (font atlases, draw calls). Instead:

- **Split structure from placement.** Build static sg node lists from slowly-changing data. Use adaptive `Sg.Trafo` for fast-changing placement (uniform update, no sg rebuild).
- **Push adaptivity down.** A parent `AList.ofAVal` that rebuilds all children is expensive. An `AVal`-driven `Sg.Trafo` on each stable child is cheap.

## Server query performance

Costly spatial queries (`probe`, `contact-rings`, `icp`) scale with mesh count and sample density. Rules of thumb:

- **Never issue per-mesh requests sequentially.** Use `Query.rayHitMany` (parallel fan-out) for multi-mesh raycasts; if a multi-mesh operation becomes hot, add a batched server endpoint with `Parallel.For` fan-out instead.
- **Parallelise the heavy inner loop server-side** when inputs are independent. Embree `Scene.Intersect` is thread-safe.
- **Cap density rather than grow linearly.** Bound point counts with `maxPoints` / sample strides; don't let resolution scale unbounded with region size.
- **Keep heavy post-processing off the Elm update thread.** Union-find over band caches, ICP residuals, etc. run in the background task that issued the query; only the final result message crosses into the update loop.
- **Debounce user-driven triggers.** Use a `CancellationTokenSource` ref so the next event cancels the previous.
- **Mesh caches are warmed at dataset load** by `bboxesHandler` — it calls `MeshCache.get` for every mesh + part, so the first interactive query never pays the lazy-load cost.

## Client compile order (`Superprojekt.fsproj`)

```
MeshData.fs                     ← mesh fetch/parse, ApiConfig, shared Http.client
ProbeModel.fs                   ← M3C2 probe DTOs (ProbeResult/ProbeState/ProbeXRange/HoverProbeState)
Query.fs                        ← server query wrappers (Async), rayHitMany fan-out, probe
CameraModel.fs / .g.fs          ← OrbitState [<ModelType>]
OrbitController.fs              ← OrbitMessage DU + orbit camera
RegistrationModel.fs            ← ScanPinId, correspondence anchors, RegStep/RegLog, PendingRegistration, LastSolveEntry, readiness engine (Readiness.compute), FlyToMath, NavAction, HeatmapMode, RegJson (WASM-free, shared with Supertests)
DetailViewMath.fs               ← correspondence-detail view: ElevGrid/SymbolicPatch types + pure math (marching-squares contours, ridge/valley, niceStep, dip/strike plane fit, PCA side-azimuth, marker metrics) (WASM-free, shared with Supertests)
StudyModel.fs                   ← study-mode shared types: config DTOs + parser, predicate engine, StudyRuntime/StudySession/StudyShell (WASM-free, compiled into server + Supertests too)
StudyApi.fs                     ← /api/study/* HTTP wrappers + StudyBoot.entryToken
StudyTelemetry.fs               ← telemetry batcher (module-level queue, 5 s/50-event flush, backoff, throttling)
ScanPinModel.fs / .g.fs         ← ScanPin + Card types
PinGeometry.fs                  ← icosphere, sphere outline
Model.fs / .g.fs                ← [<ModelType>] Model + DatasetScale helpers
Persistence.fs                  ← workspace JSON serialise / apply
LineShader.fs                   ← Shader.flatColor + Lines (pixel-constant 3D lines)
Primitives.fs                   ← widgets, showWhen/showWhenNot, observedRender, provBarJs, StudyGate, ReadinessView (readiness-engine adapter)
Messages.fs                     ← Message DU (incl. StudyMessage)
StudyUpdate.fs                  ← ServerActions (init/loadDataset), study reducer + StudyEvents.derive + update-postlude + feature-gate guard
CardUpdate.fs / ScanPinUpdate.fs
Update.fs                       ← main reducer (ServerActions moved to StudyUpdate.fs)
MeshShaders.fs                  ← RenderPass + MeshShader / FusionShader / PanoramaShader
MeshView.fs                     ← LoadedMesh, visibleMeshNames, buildScene/buildFusionNode/buildPanoramaNode
FusionView.fs                   ← offscreen fusion pass + fullscreen composite
PanoramaView.fs                 ← offscreen cubemap capture + cylindrical reproject
ScanPinScene.fs                 ← pin sg nodes
SceneGraph.fs                   ← composes meshScene + pinScene + cross + labels
CardsPin.fs / Cards.fs          ← pin card body; shared card chrome (cardDragHandle/cardPos/cardStyle)
GuiTopBar.fs / GuiPanels.fs / GuiOverlays.fs / GuiCards.fs / GuiWorkflow.fs / GuiStudy.fs
View.fs                         ← App module wires Boot.run
ShaderCache.fs / Program.fs
```

`.g.fs` files are Adaptify-generated. **Never edit them by hand.** Re-run `dotnet adaptify --local --force ./src/Superprojekt/Superprojekt.fsproj` (or `adaptify.cmd` / `adaptify.sh`) after editing the corresponding `.fs` model file.

## Server compile order (`Superserver.fsproj`)

```
MeshLoader.fs          OBJ parse, centroid file, atlas paths
MeshCache.fs           Embree scene + BbTree cache (lazy, permanent)
MeshAnalysis.fs        sphere contact-ring tracing, patch sampling
MeshProbe.fs           N-mesh M3C2 probe (normal PCA, cylinder sampling, KDE, three sources)
MeshIcp.fs             ICP solver (recentred Gauss-Newton, trimmed correspondences)
RegMath.fs             weighted Umeyama rigid landmark solve (Jacobi SVD, conditioning)
StudyConfig.fs         study config + secret parsing, startup validation (Giraffe-free, in Supertests)
StudyStore.fs          JSONL session stores, balanced assignment, TRE scoring, HMAC codes (Giraffe-free, in Supertests)
QueryHandlers.fs       HTTP query handlers
StudyHandlers.fs       /api/study/* handlers + study discovery cache
Handlers.fs            routing
Program.fs             ASP.NET startup
```

## API endpoints

```
GET  /api/datasets                              → string[]
GET  /api/datasets/default                      → string (from data/default.txt, fallback = first alphabetically)
GET  /api/datasets/{dataset}/centroids          → { meshName: [x,y,z] }
GET  /api/datasets/{dataset}/bboxes             → { meshName: { min:[x,y,z], max:[x,y,z] } }
GET  /api/datasets/{dataset}/mesh/{name}        → count of OBJ files
GET  /api/datasets/{dataset}/mesh/{name}/{i}    → binary mesh
GET  /api/datasets/{dataset}/mesh/{name}/{i}/atlas → JPEG
POST /api/query/ray                             → { hit, t, point, triangleId }   Name = "dataset/mesh"
POST /api/query/closest                         → { found, point, distanceSquared, triangleId }
POST /api/query/patch                           → every triangle whose bbox overlaps the footprint sphere, projected into the frame (planar px,py + per-vertex atlas UVs + index triples), clipped to the radius disc; connectivity-agnostic so fragmented multi-tile meshes fill the footprint like a watertight DEM (height fields → no overlap to disambiguate). Optional frameNormal/frameRefDir override skips the local plane fit; maxTris bounds the output via a uniform stride. Used by the patch small-multiples anchor picker.
POST /api/query/contact-rings                   → sphere–surface intersection polylines (all rings, closed rings repeat the first point)
POST /api/query/icp                             → ICP transform + convergence + residuals
POST /api/query/lsq-pairs                       → weighted rigid landmark solve (delta onto reference + per-pair residuals + conditioning; 400 on <3 pairs)
POST /api/query/probe                           → N-mesh M3C2 probe (per-mesh distributions + KDE + three sources)
POST /api/query/region-distance                 → per-vertex signed M3C2 distance of a target mesh to the reference (cloud-to-mesh closest point, signed by ref normal), in the target's served vertex order; 1e30 sentinel where no closest point. A2 surface map.
POST /api/query/region-grid                      → n×n vertical ray-down elevation grid (z + hit flags) in the mesh's own (untransformed) world frame around a world-XY centre. Transform-independent, so the client maps it to the current pose. Correspondence-detail symbolic surface (contours / ridge-valley / dip).
POST /api/study/session                         → { token } or { demo, studyId, condition } → session + configPublic (verbatim config.json)
GET  /api/study/list                            → valid study ids (gear-popover demo picker)
POST /api/study/{sid}/events|answers|transforms|workspace|advance, GET /api/study/{sid}/complete
POST /api/study/{studyId}/tokens                → localhost-only token generation
```

All query coordinates are **absolute world space**. The server converts: `localPos = V3f(worldPos - meshCentroid)`.

Removed for lack of consumers (don't re-add without one): sphere / box / sphere-batch (old per-vertex filter), ray-batch, grid-eval, isoline, curvature-ridge (the last two went with the Line pin payload). Multi-mesh raycasts go through `Query.rayHitMany` (client-side `Async.Parallel` over `/query/ray`).

## Client Model snapshot

Top-level `Model` fields (see `Model.fs`):

- `Camera`, `MeshOrder`, `MeshNames`, `MeshVisible`, `MeshesLoaded`, `CommonCentroid`, `MenuOpen`, `SavedMenuOpen`
- `DebugLog`
- `Datasets`, `ActiveDataset`, `DatasetScales` (`{"SETSM_glacier" → 0.01}`), `DatasetCentroids`
- `FullscreenOn`, `GhostSilhouette` (default **on**), `GhostOpacity` (0.12), `ShadingStrength` (0.15), `SlopeThresholdDeg` (15°), `AnchorGhostMode` (default **on**; "Isolate pins" in the UI — gates the pin blob filter, see Ghosting rules)
- `SceneBounds`, `MeshBounds`
- `ActivePickingLayer`
- `LassoDrawing`, `LassoVolume`, `LassoEnabled` (filter on/off, polygon kept)
- `MeshTransforms` (committed render-space trafos), `Registration` (mode + reference mesh + running flag), `Retarget` (`RetargetIdle | RetargetProjecting | RetargetReviewing of RetargetCandidate[]`)
- `PendingReg` (uncommitted solve preview — deltas + rms/convergence/collinearity; “preview active” ⇔ results non-empty), `RegistrationLog` (committed `RegStep` history, newest first), `AnchorPick` (one-shot 3D correspondence-marker pick), `PatchPicker` (small-multiples picker state), `Toast`
- `MeshSensorTypes`, `MeshDatasetErrors`, `MeshAlgorithmResidual`, `HeatmapMode` (`HeatOff | HeatProvenance | HeatDiff`) + `HeatmapPrev` (Diff auto-revert), `ProvenanceThreshold`
- `SurfaceDistOn`, `SurfaceDistance` (`Map<mesh, float32[]>` per-vertex signed distance for the soloed mesh), `SurfaceDistBrush` (`(float*float) option` — A2/A3 surface map + range brush)
- `DetailGrids` (`Map<mesh, ElevGridState>` own-frame ray-down grids for the effective registration pin's marker meshes) + `DetailGridPin` (which pin they belong to) — correspondence detail view, session-only
- `FusionMode`
- `PanoramaOpen`, `Panoramas` (`Panorama list` = `{ Name; EyeWorld; Yaw }`, synthetic, regenerated on dataset load), `SelectedPanorama`, `PanoramaMode` (`PanoPhoto | PanoRender | PanoBlend`), `PanoramaBlend`
- `ScanPins`, `CardSystem`, `HoverProbe` (transient Ctrl-click probe, one global slot — carries `Radius` so the transient 3D body can be drawn)
- `ChartCursor` (chart-hover elevation cursor: pin id + signed distance + Alt-extended), `ChartHoverMesh`, `ChartStickyMesh` (column highlight; hover wins over sticky)
- `RenderingMode` (Textured | Shaded | SlopeColor), `MeshSolo`, `LassoCardPos`, `GearPopoverOpen`
- `LastSolve` (per-mesh solve diagnostics, persisted in workspace v3, cleared per mesh on rollback), `WorkflowPanelOpen`, `RegistrationCardOpen`
- `Study` (`StudyShell option` — None = Full app; `StudyActive` carries the running study session), `StudiesAvailable`

GUI placement:
- Left panel (`GuiPanels.leftPanel`): mesh list, pin list, error metadata, error provenance card.
- Top bar (`GuiTopBar.topBar`): hamburger, dataset selector, **◌ Lasso**, **○ Pin** placement, **◈ Fusion**, **▦ Pano**, **⚲ Registration** (`GuiWorkflow.workflowPanel` — a pure view over the model: shared readiness diagnostics with nav actions, per-pin correspondence-marker matrix, pending banner, error stats; auto-opens the first time a pin becomes a registration pin; never issues server queries), camera reset, world coordinate readout, gear popover.
- Floating cards: pin cards are managed by `CardSystem` (`Cards.renderCards` — 3D-anchored, detachable, z-ordered); `lassoCard`, `registrationCard`, `panoramaCard` (`GuiCards.fs`) hold their position locally (`LassoCardPos` is the only persisted one). **All draggable cards share one chrome**: `Cards.cardDragHandle` / `Cards.cardPos` / `Cards.cardStyle` — don't hand-roll pointer-drag code for new cards. `lassoCard` is symbol-only: `◉/○` (enable/disable, polygon kept), `✎` (redraw), `⊘` (cancel drawing), `✕` (clear). `retargetCard` is the only CSS-centered modal (not draggable).
- Gear popover (debug flyout, end of `GuiTopBar.fs`): retarget, workspace save/load, camera speed, **Ghost silhouette toggle**, **Ghost opacity slider**, **Isolate pins toggle**, shading strength, slope threshold, dataset info, mesh centroids, debug log.

## ScanPin system

A ScanPin is a 3D annotation in **metric world-space**: `Centre : V3d` (world metres), `InnerRadius : float` (hard truth — α = 1 and full evaluation weight inside, a hard sphere; metres). The placement flyout exposes the inner radius and the X/Y/Z position. Pins drive the per-pixel blob in the mesh shader (`Blobs` uniform). A pin carries an optional `Correspondence` directly (the old `Point`/`Line`/`Patch` payload DU was removed — every pin is an M3C2 probe + optional registration correspondence).

Render-space conversions happen at pipeline boundaries: `ScanPin.renderCentre cc scale` and `ScanPin.renderLength scale` in `ScanPinModel.fs`. `MeshView.buildScene` projects centres/radii to render-space on upload; `ScanPinScene.fs` does the same for marker dots, spheres, outline. The `Cards.projectToScreen` anchor is stashed in render-space by `ScanPinUpdate.handleMsg`. Camera focus (`OrbitMessage.SetTargetCenter`) takes render-space coords too.

**Placement workflow:** Top-bar **○ Pin** toggles placement. After click-placement the pin enters `AdjustingPin` state with a flyout for position (X/Y/Z fields) and inner radius (probe-cylinder length 20 m fixed). Commit / Discard / Escape end placement.

**State:** `Placement : PlacementState` single DU on `ScanPinModel` — `PlacementIdle | AnchorPlacement | AdjustingPin of ScanPinId`. Helpers: `ScanPinModel.activePlacementId sp`, `ScanPinModel.isPlacing sp`.

**M3C2 probe**: every pin owns `Probe : ProbeState` (`ProbeNone | ProbeRunning | ProbeReady of ProbeResult | ProbeError`) and a `ProbePreview` (the split-violin preview probe). The probe samples all visible meshes inside a cylinder (radius = InnerRadius, length = 20 m fixed `ScanPin.fixedProbeLength`, axis = PCA normal of the reference mesh inside the pin sphere) on the server (`POST /api/query/probe`, one batched round-trip carrying world-space registration transforms) and returns per-mesh signed-distance distributions (median/IQR/std/KDE, re-centred so 0 = reference median; `RefOffset` is the axial offset of that zero from the pin centre) plus the dataset/algorithm/conditioning decomposition. Computation is **lazy + debounced**: `ScanPinUpdate.ensureProbe` runs as a postlude after every reducer step and launches one 250 ms-debounced query for the effective (card-open) pin when its state is `ProbeNone`; invalidation just resets to `ProbeNone` (radius change, centre move, reference change, transforms, visibility). Stale responses are dropped by the `ProbeRunning` guard. The pin card renders the **vertical violin chart** (signed distance on the y axis, positive up, 0 = reference median, one column per mesh with median tick / IQR whisker / count badge, LoD₉₅ band, y-range presets, lock-order toggle) + three-source stacked bar from `ProbeReady` data (`CardsPin.ridgelineJs`, shared with the Ctrl-click hover-probe tooltip via the `d.mini` flag).

**Chart 2D-3D linking** (accent `#0891b2`, deliberately distinct from the mesh palette):
- *Chart → 3D elevation cursor*: hovering the chart plot at signed distance `d` renders a translucent disk orthogonal to the **probe axis** (not world-up — they only coincide for heightfields) at `centre + d·axis`, radius = InnerRadius; Alt extends it to scene bounds. State = `Model.ChartCursor`, drawn in `ScanPinScene`, gated on the cursor pin's card being open + `ProbeReady`.
- *Contact-line highlight*: while the elevation cursor is active (chart hover **or** 3D hover inside the effective pin's probe cylinder), the mesh shader darkens intersected meshes (×0.85) and brightens a smoothstep band within ±0.02 m of the plane in the same accent colour, clipped to the probe cylinder (unclipped when Alt-extended). `View.fs` builds one `aval<CursorHighlight option>` (chart cursor wins over 3D hover — one plane at a time) and threads it `SceneGraph.build → MeshView.buildScene`, which sets the shared `Cursor*` uniforms plus a per-mesh `CursorActive` bbox-vs-slab/cylinder gate ("all meshes the plane intersects"). The effect only touches **above-ghost** fragments so the ghost silhouette colour stays uniform; the 3D-hover path is uniform-only (no reducer churn, no sg rebuild).
- *3D → chart*: the 3D hover point's signed distance along the probe axis drives a cursor line on the chart while the point is inside the probe cylinder — computed from the view-local `hoverCoord` cval (threaded `View.fs → Cards.renderCards → pinCardBody`, no reducer churn) into the `data-cursor` attribute; the chart JS moves the line without re-rendering. `hoverCoord` is cleared on canvas mouse-leave so the line can't freeze stale under a card.
- *Column highlight*: hovering a column ghosts every other mesh at fixed α 0.2 (`MeshActive=false` + `GhostOpacity=0.2` overrides in `MeshView.buildScene`, independent of the GhostSilhouette toggle); clicking makes it sticky (`Model.ChartStickyMesh`, thick border in the chart, toggled off by re-click / another column / a click outside the **probe section** `.pc-probe` via a document-level listener — the boundary is the whole probe section, *not* just the chart SVG, so the chart's own header controls like **⬢ 3D map** / **⊟ slice** don't wipe the soloed column).
- *Surface map + brush (A2/A3)*: the soloed column is also the mesh painted by **⬢ 3D map** (`DistanceEncoding`); dragging a y-interval brushes `Model.SurfaceDistBrush` (focus+context wash). See "Signed-distance surface colour map".
- *JS → Elm event bus*: the chart JS has no `env` — it hit-tests locally (exact even when the chart scrolls horizontally) and posts `mv|d|alt|mesh` / `out` / `click|mesh` / `clickout` / `apick|d|mesh` / `lock|d` / `brush|lo|hi` / `brushclear` strings to the hidden `.pc-ridge-bus` input via synthetic `input` events, which `Dom.OnInput` picks up and converts to messages. Pointer-move payloads are rAF-coalesced and deduped.

**3D rendering** (`ScanPinScene.fs`): `pinDots` clickable markers, `pinRings` draws the pin's influence as thin curves in the pin's categorical colour (host-mesh palette colour, `#1a56db` fallback) — an equator ring (parametric circle ⊥ `ScanPin.axis`, radius = InnerRadius) plus the cached sphere–surface **contact rings** per *visible* mesh; α 0.6 / 1.5 px unselected, α 1.0 / 2.5 px selected, normal depth testing (occlusion is the spatial cue). There are **no filled translucent shells** any more; the equator ring is the live feedback for the inner-radius slider. `ghostPreview` for the placement hover. `hoverProbeBody` draws the transient Ctrl-click hover probe's 3D body (equator ring + axis line, A1). `pickGuide` draws the reference-normal guide line during a 3D correspondence-marker pick (A4). The elevation cursor (`cursorPlane`) also draws a measured ruler from the reference surface to the picked distance (A3 F4).

**Contact rings**: every pin caches `ContactRings : ContactRingState` (`RingsNone | RingsRunning | RingsReady of Map<mesh, V3d[][]>`, registered world-space metres). `ScanPinUpdate.ensureRings` runs as a postlude after every reducer step (next to `ensureProbe`) and launches one 250 ms-debounced per-pin fan-out of `POST /api/query/contact-rings` over **all** meshes (visibility only gates rendering, so toggling a mesh never recomputes); per-pin `CancellationTokenSource`s let several pins recompute concurrently after a registration. Registration transforms are rigid, so the client inverse-transforms the sphere centre into each mesh's own frame and maps the returned rings back. Invalidation → `RingsNone`: radius change, retarget centre move, `RegistrationComplete` / `ResetMeshTransforms` (`ScanPinModel.invalidateRings` — deliberately *not* applied on visibility changes, unlike `invalidateProbes`). The server (`MeshAnalysis.contactRings`) marches the level set of `|p − c| − r` over BVH-candidate triangles with marching-squares edge linking (exact quadratic edge–sphere roots; closed rings repeat their first point so there is no gap).

## Registration (ensemble workflow)

Two **stages**, both landing in `PendingReg` (an uncommitted preview), with an explicit Commit/Discard and a rollback-able history (`RegistrationLog`). The **⚲ Registration** panel (`GuiWorkflow.fs`) hosts the whole flow and presents it as **Stage 1 · Correspondence alignment** then **Stage 2 · Fine ICP (optional)**; the reference mesh is the ★ toggle (mesh panel ↔ panel, single selection). Two pin kinds: a **scanpin** (measure only) and a **registration pin** (carries one correspondence).

- **Stage 1 · Correspondence alignment** — `SolveCoarse` → `POST /api/query/lsq-pairs` per visible moving mesh with ≥3 correspondence-marker pairs (parallel), surfaced as the green Ready entry in the diagnostics list. Pairs come from pin **correspondences** (below); all weights are 1.0. Server: `RegMath.solveRigid`, weighted Umeyama/Arun with Jacobi SVD, right-handed completion + det(V·Uᵀ) flip so planar/collinear sets never yield reflections; response carries per-pair residuals + covariance eigenvalues + `collinearityWarning` (amber badge). (The history/log mode label is `"correspondence"`.)
- **Stage 2 · Fine ICP (optional)** — the pre-existing `POST /api/query/icp` (`MeshIcp.runIcp`), math unchanged: **Traditional** (no markers) and **Region-restricted** (committed pins become Gaussian weights — centre, sigma = InnerRadius, multiplier = 1.0; `RegionEps` 0.05). The fine-mode toggle is **hidden until a Stage-1 result is committed** (so it is never a co-equal control before then). `initialTransform` = current **committed** transform; result stored as a delta vs committed. Two solver hardening details (don't remove): the Gauss-Newton step is **linearized around the weighted correspondence centroid** (raw UTM-scale coordinates give a ~5e6 m rotation lever arm and the step diverges — 428 km translations), and correspondences are **gated at 3× the median pair distance** per iteration (partial overlap otherwise biases the fit).

**Pending preview** (`PendingReg.Results` non-empty): meshes render at `committed * delta` (`ModelTransforms.effectiveRender` — Trafo3d composition is **postfix**, committed first); the committed pose re-renders as a slate-tinted ghost; a banner shows; pin placement / retarget / fusion / dataset switch / marker picking are blocked (reducer guard + disabled buttons); probes get a second `ProbePreview` per pin (split violin: committed left desaturated, preview right, Δ-median arrow); contact rings + hover probe use effective transforms; heatmap gains a **Diff** mode (signed combined-error change vs the 1.96·√(σ_ref²+σ_M²) detection limit, blue improved / red degraded, masked → ghost; auto-reverts on commit/discard). **Commit** appends a `RegStep` (before/after transforms + rms + algo-residual before), applies transforms, re-bases markers by the world delta (`bakeAnchors`), fires the full invalidation cascade. **Discard** drops the preview (probes stay, rings recompute back). **↩** rolls back the newest step only; **Reset** rolls back everything to identity + empty log.

**Correspondences** (`ScanPin.Correspondence option`): per pin an optional `{ Enabled; RefAnchor (the reference marker — the pin centre if the host is the reference, else its closest-point projection); RefDistance; Anchors : Map<mesh, {Point; Source}>; Residuals }`. A pin is a **registration pin** iff its correspondence is enabled. Marker points are **world-space at committed poses** — commit/rollback re-base them, and all pickers are blocked during a preview. **There is no accept/reject — seeded markers apply immediately**: the old per-marker `Accepted` flag *and* the blocking auto-seed review modal (`AnchorReview*`, `GuiCards.anchorReviewCard`) were removed; demoting a registration pin back to a scanpin is the only "reject". Auto-seed (enabling the toggle, reference change, retarget move) projects via parallel `/query/closest` and writes the markers directly (the ⟳ button / `NavAction.ReseedCorrespondence` re-projects; manually-picked non-Auto markers are never overwritten). Fallback picks: **▦ patch small-multiples** (shared reference frame via the patch `frameNormal/frameRefDir` override; canvas-rendered atlas-textured triangles, viridis height ramp, fit-to-footprint zoom + clamped pan, zoom-label click resets; click = triangle hit-test → `Patch2D` marker computed client-side, exact because the frame is orthonormal: `world = centre + u·refDir + v·left + h·normal`; hover linking via the view-local `patchHover` cval into `ScanPinScene.patchLink`), **⊕ one-shot 3D pick** (shader-level solo, depth-gated, Esc, auto-advance, toggles off on re-click, with a reference-normal guide line — A4), **Shift+click violin column** (`ViolinAxial`, refAnchor + d·axis). The **reference marker is editable** too (⊕ on the reference row → pick on the reference mesh). Markers render as wireframe tetra glyphs + a line to the reference marker (follow preview deltas).

The lasso never affects registration — it is purely visual. Workspace JSON v2 persists `corr` per pin + `regLog` (the `"acc"` field is dropped on write / ignored on read); `PendingReg` is never persisted; v1 workspaces load with empty defaults.

### Correspondence detail view

A pin-card section (`CardsPin.detailSection`) showing one registration pin's correspondence markers in a to-scale **orthographic SVG** viewport — symbolic surface (height-shaded contours + ridge/valley lines) per mesh, ring+cross reference glyph + disc moving glyphs, strike/dip, measurement lines, callouts, rulers/scale bar, Top-view +Y=North compass, and a values table (Euclid/Z/Horiz/Az/Dip). Visible iff the effective pin is a registration pin with ≥1 marker.

- **SVG, not a second RenderControl** — same reason as the patch picker ([[patch-picker-html-canvas]]): a second live WebGL control is a perf ceiling here, and the content is fully 2D-projectable. Heavy math (marching-squares / dip / PCA / niceStep) is in the WASM-free, unit-tested `DetailViewMath`; the `observedRender` JS only projects + pans/zooms + builds SVG. Camera (Side/Top/Free view, azimuth, pan, zoom) is **JS-local** on `el.__dv` (like the patch picker's `el.__ppv`) so it never churns the reducer.
- **Elevation grids** come from `POST /api/query/region-grid`, sampled in each marker mesh's **own frame** (transform-independent → survive previews/commits), cached in `Model.DetailGrids` by `ScanPinUpdate.ensureDetailGrids` (debounced postlude like `ensureRings`; auto-invalidates on own-frame centre move / pin change). The JS maps grid + markers to the current pose via the committed ∘ pending world transform, so the view follows a solve preview.
- **Linking**: table-row / glyph hover → `.pc-detail-bus` → `SetCorrMarkerHover` (main-view marker brighten) + `SetChartHoverMesh` (violin column).

## User study mode

`/s/{token}` (server falls back to index.html; `<base href="/">` keeps assets at root) enters **study mode**: chrome replaced by a study bar (progress dots, goal line, gated tool strip, Next gated on step completion), instruction overlays / anchored guided tooltips, and a right-docked task pane with the question widgets. `Model.Study : StudyShell option` — `None` = Full app, everything else is study pages or the running session. Demo preview from the gear popover (condition picker FULL/NUM + study picker); only demo sessions can exit.

- **Config**: `src/Superserver/studies/{id}/config.json` (public, served verbatim) + `secret.json` (planted answers, TRE check-point pairs, gold threshold — never served). Startup validation refuses invalid studies (log). `glacier-v1` is the authored default (tutorial = Hessigheim, main = SETSM_glacier; copy is placeholder English).
- **Feature gating**: `Study.featureVisible` = `phase.allowedFeatures ∩ ¬condition.disabledFeatures`, consulted by views via `StudyGate.featureOn` and enforced again in `Update` (gated/Full-only messages no-op + toast; camera/hover messages silently). NUM hides violinChart/heatmap(Diff)/threeSourceBar/splitViolinPreview; the pin card shows a numeric median/IQR + registration-RMS table instead.
- **Predicates** (`StudyModel.Predicate`): Event/And/Or/Seq/AnswerSubmitted; event counts are cumulative since the last dataset switch (per-step resets would break P4's `And[fineSolved≥2]`, fully-cumulative would break P2's final `Seq` stage), Seq milestones monotone per step. `StudyEvents.derive` diffs (model before, after, message) into the fixed telemetry event list — one stream feeds predicates and the batcher.
- **Server stores**: per study `data/` with sessions/events/answers/advance/transforms JSONL + workspace/scores JSON, per-sid locks, balanced condition assignment, TRE scored on every transforms post (never returned; tutorial gold correctness is the single echo, 3 fails → screened), HMAC completion code gated on all non-optional advances + a `final` transforms post. One token = one session, resumable (scene resets, progress kept).
- **Don't** put telemetry or batcher state in the Elm model; `StudyTelemetry` is module-level like the reducer's CTS refs.

## Tests

`src/Supertests` (console runner, paket-managed, no extra packages) compiles `RegistrationModel.fs` + `DetailViewMath.fs` + `StudyModel.fs` + `RegMath.fs` + `StudyConfig.fs` + `StudyStore.fs` directly and covers the Umeyama solver (recovery, reflections, weights, collinearity), the RegLog commit/rollback machine, the RegJson round-trips, the detail-view math (niceStep, marker metrics + North bearing, marching-squares contours / dip / ridge-valley on synthetic patches), the predicate engine, study reducer gating, config validation, balanced assignment / HMAC codes / TRE scoring / gold screening / advance ordering — `dotnet run --project src/Supertests`. Against a running server (`ASPNETCORE_URLS=http://localhost:8002 dotnet run --project src/Superserver`): `node tools/integration.mjs` (registration HTTP flow) and `node tools/study-integration.mjs` (full study walk: balance, route security, gold echo + screen-out, resume, completion codes).

## Notes

- **Panorama viewpoints are synthetic** — one pose per dataset at the scene-bbox centre (`Update.fs`, `SceneBoundsLoaded`); the panel renders live cubemap captures reprojected cylindrically. If real imagery + poses arrive, swap the pose generation and add a Photo texture source.
- **Workspace persistence is a JSON download / upload** through the browser (`Persistence.fs`); no server-side store. Panoramas are not persisted.
- **Fusion picking is a CPU raycast** over visible meshes keeping the lowest-error hit (matches the depth-test winner).
- **Removed features — don't resurrect from old branches**: Explore mode, point-pair registration, residual histogram, per-vertex filter endpoints, the pin **Line / Patch payload modes** (every pin is now just a probe + optional correspondence; `ScanPin` carries `Correspondence` directly), the pin **reliability weight** (registration weights are uniform 1.0), the pin **falloff radius** entirely (the blob is now a hard sphere at `InnerRadius`; the `FalloffZoneOnly` heatmap toggle went with it), the **probe cylinder-length slider** (fixed 20 m), the **planarity badge**, and (session 2) the per-correspondence-marker **accept/reject flow** + the blocking **auto-seed review modal** (`AnchorReview*`, `SetAnchorDecision`/`ApplyAnchorReview`, `MeshAnchor.Accepted`) — seeded markers apply on seed, demote-to-scanpin is the only reject. The surface vocabulary dropped "landmark"/"anchor" → **registration pin** / **correspondence marker point**. The **patch small-multiples marker picker** (`/query/patch`, `PatchPickerState`) is a separate registration feature and stays.

## Aardvark.Dom gotchas

- `Attribute("for", "...")` on `<label>` is silently dropped — nest `<input>` inside `<label>` instead.
- `Attribute("checked", "")` is dropped — use `Attribute("checked", "checked")`.
- CSS `~` sibling combinator breaks (Aardvark inserts wrapper nodes) — use `:has()` on a known ancestor.
- `RenderControlInfo` and `TraversalState` both have `.Runtime` — annotate `(info : Aardvark.Dom.RenderControlInfo)` when ambiguous.
- `yield!` is not supported in Aardvark.Dom CE builders — use OnBoot JS with MutationObserver for dynamic SVG/canvas rendering.
- `NodeBuilder "svg" { ... }` can create arbitrary HTML elements but SVG attributes need special handling.
- `renderControl { ... }` can be nested inside `div { ... }` — it creates a WebGL canvas as a child element.
- `AVal.map4` does not exist — combine with `AVal.map2`/`AVal.map3`.
- `Dom.Style` for renderControl; `Style` for HTML elements.
- `Css.Custom` does not exist — use CSS classes in `style.css` for properties not covered by `Css.*`.
- **`RenderControl.ViewportSize` is framebuffer pixels** (CSS × devicePixelRatio); `RenderControl.ClientSize` is CSS pixels. Anything that mixes with DOM coordinates — HTML overlay placement (cards, scale bar, tooltips), cursor-position → NDC math (pickRay, lasso) — must use ClientSize, or it breaks on hi-dpi displays. ClientSize is `V2i.II` until the first DOM event; `View.fs` derives `overlaySize` with a ViewportSize fallback.
- `Sg.OnPointerDown(bool, handler)` — the bool is **capture-vs-bubble phase** for the Sg event bus, not pointer capture. For drag operations call `e.Context.SetPointerCapture(e.Target, e.PointerId)` inside the down handler and `ReleasePointerCapture(...)` in up.
- `Dom.OnPointerDown((...), pointerCapture = true)` — browser-level `element.setPointerCapture`; use this on renderControl canvas drags so events keep flowing when the cursor leaves the canvas.
- **`Sg.OnTap` / `Sg.OnDoubleTap` / `Sg.OnLongPress` fire on background misses too.** Always gate on `e.Location.Depth < 0.9999`.
- **FShade shaders must be float32-only.** F# `float`, `Constant.Pi`, `V3d`/`V2d`, and `member _ : float` uniforms all emit GLSL `double`/`dvec3` + fp64 `#extension` directives, which WebGL2 (ESSL3) rejects at runtime (`'double' : Illegal use of reserved word`). Use `3.1415927f`, `V3f`/`V2f`, `: float32` uniforms, and bind `1.0f` not `1.0`. **`dotnet build` and the `fshadeaot` PostBuild step do NOT catch this** — only the in-browser compile does, so always verify shader changes in a browser. Porting desktop-GL examples is the high-risk case (desktop GL supports double; WebGL2 does not).

## CSS / design

- Light theme, `'Segoe UI'`/`'Inter'`, accent `#1a56db`.
- Body bg `#f4f6f8`, panel bg `#ffffff`, text `#0f172a`.
- All styles in `wwwroot/style.css`; no inline styles except model-dependent ones (positions, data-driven colours, cursor).
- Conditional visibility uses `Primitives.showWhen` / `showWhenNot` → `.hidden` class (`display: none !important`), not inline display styles.
- `.btn-active`: darker blue with inset shadow for toggle buttons.

## fsproj notes

- Client: `Microsoft.NET.Sdk.BlazorWebAssembly`, `net8.0`, `WasmBuildNative=true`, `LocalAdaptify=true`.
- Server: `Microsoft.NET.Sdk.Web`, `net8.0`; references client project for static file hosting.
- Server runs on `http://localhost:5000`.
- Run Adaptify with `adaptify.cmd` (Windows) or `adaptify.sh` (Unix) — both wrap `dotnet adaptify --local --force ./src/Superprojekt/Superprojekt.fsproj`.
