# Superprojekt — assistant notes

Research prototype for interactive 3D inspection and **registration** of geological mesh datasets (multi-epoch scans of the same terrain). Two F# projects:

- **Superserver** — ASP.NET Core + Giraffe. Serves mesh data and runs the spatial queries (Embree BVH); hosts the WASM client at `http://localhost:5000`.
- **Superprojekt** — Blazor WASM client. Aardvark.Dom Elm-style architecture, WebGL2 rendering. Thin client; heavy compute goes to the server.

See `README.md` for what the app does and how to run it. This file collects the **rules and pitfalls**; behaviour is documented by the code.

## Style

- Light theme, high contrast, print-appropriate.
- GUI must be readable to a non-expert at first glance.
- Colour families are disjoint (§B1): the scalar gradients own red/blue (diverging, variance, displacement, range) and red→yellow→green (incidence/shape), plus pale grey = no-data and gold = reference. Mesh identity = the cool/earth family (teal · ochre · slate · cyan · brown — `Primitives.meshPalette`); pin identity = the vivid warm/purple family (`Primitives.PinPalette`, glyph-paired). Identity rides on thin marks (swatches, outlines, rings, chart layers) — gradients fill areas; never hand new UI a colour from another family. Correspondence markers, the picked-point crosshair/aim ghost and the pin circle all use the PIN colour; slice-profile lines + their anchor dots stay mesh-coloured (they mark mesh profiles).
- No comments unless the logic is non-obvious.
- Concise code, no unnecessary abstractions, no premature helpers.

## State rules (Elm-style)

- One `[<ModelType>]` `Model`; Adaptify generates the `.g.fs` files. **Never edit `.g.fs` by hand** — run `./adaptify.sh` (or `adaptify.cmd`) after editing a model file.
- ONE selection: `Model.Selection` = `Active : ActiveSelection` (`SelNone`/`SelMesh`/`SelPin`/`SelCell(pin,mesh)`) + the transient `Hovered`. The pin×mesh matrix is the canonical driver (column-head = mesh, row-head = pin, cell = intersection); roster rows, focus tiles, 3D pin dots and 3D surface clicks emit the same `SetSelection` (a 3D background miss ⇒ `SelNone`); a deleted pin degrades `SelCell→SelMesh`, `SelPin→SelNone` (ScanPinUpdate). Every view is a pure follower via `Selection.pin`/`Selection.mesh` — never add a second selection state or panel-to-panel hover emitters. Grammar everywhere: **hover = peek, click = select, double-click = main-3D zoom, drag = edit**. A focus tile maps its click *through* the selection: nothing/mesh selected → `SelMesh` of the tile's mesh, pin/cell selected → `SelCell(pin, tileMesh)`, and re-clicking the tile of the current target = the double-click zoom (`FocusScene.cellZoom` is the shared fly-to-marker, also behind the matrix cell double-click).
- Selection frames the **focus panel**, never the main 3D camera: the focus single + tiles DERIVE their camera from the selection in BOTH projections (`FocusScene.selBaseFrame` — pin AND cell share ONE close-up scale, the pin's influence circle filling the view height, only the centre differs (pin centre vs that mesh's marker); mesh/none → whole-mesh fit (Top) / untouched look-around (360°)) composed with user pan/zoom offsets; there are no imperative focus-camera helpers to call at click sites. The 360° close-up is angular: pan = eye→target (azimuth, elevation), zoom so the vertical half-fov = the pin's angular radius — the pano zoom convention is **VERTICAL fov** (`panoFov`/`panoHalfTans`; the horizontal Frustum arg is derived per aspect), so the close-up fills the height identically in both projections; drag/wheel anchor maths must run on the EFFECTIVE zoom (base ⊕ user). The projection toggle switches the single AND the tiles together (tiles mirror the displacement→Top collapse). Offsets: mesh/none persists per (mesh, projection), but a pin/cell target mints a FRESH offset on every selection change (`camPair`) — a focused pin/point must ALWAYS open at its close-up, never restore a stale zoomed-out state; don't re-key them back into `camStates`. A cell selection IS the locate: solo + first-locate backup (`BackOutLocate` restores; re-clicking the located cell backs out), Inspect pin-ROI — the user's Top/360° choice is respected (never force a projection at a click site). Main-3D framing goes through `ZoomToMesh`/`ZoomToPin`/`FlyToPoint`, only from double-click handlers. A control whose *single* click **toggles** state must route both handlers through `ClickGate` (Primitives.fs) — a double-click's two leading clicks/taps fire first and would toggle twice — and its double handler must itself end in the desired state (select + zoom, never toggle), because a slow double-click can let the deferred single fire in between.
- `MeshVisibility.shown` (Model.fs) is the **single** shown/clickable rule: the per-mesh visibility toggles plus the `MeshSolo : string option` isolation overlay (solo never mutates `MeshVisible`; exiting isolation resets every toggle to ON). Every consumer — render `MeshActive`, raycast candidate sets, ring/constellation gating — goes through it; don't special-case visibility anywhere else.
- Registration state: `LoadTransforms` (immutable per-mesh baseline) / `SolvedTransforms` (presence ⇔ solved; a re-solve replaces it wholesale) / `RegView` (one global Before/After). Displayed pose = `ModelTransforms.displayedRender`/`displayedWorld` for queries (committed view) and `MeshView.displayedMeshT` for rendering (also flips while the spring-loaded Peek hold is down — the peek is purely visual, no query may read it). Correspondence anchors are stored in the mesh's **server frame**, so the Before/After toggle moves them with the mesh — no re-baking.
- Correspondences are **Before-only**: the Before state is the single source of truth — anchors are detected (seeding evaluates at `displayedWorldAt RegBefore` regardless of the view), picked and edited there exclusively; After only *displays* the moved points. Entry points force the view back (`UpdateHelpers.applyRegView RegBefore` in `ToggleCorrArm`, placement, `SetInnerRadius`; `PickCorrespondenceAt` rejects in After as the safety net; the dock XYZ editor disables). Two zones: an anchor must lie within the **pin sphere** (`InnerRadius` — seed accept, pick clamp, and the resize kill in `SetInnerRadius`), while `InRoi` membership (can the probe measure here) uses the wider `ScanPin.roiReach`. A solve records its provenance (`Model.SolveInputs`: refMesh + every (pin, mesh) anchor point consumed); the `ensureSolveValidity` postlude clears the registration the moment any tracked pin/point is deleted or moved.
- Probes are per-pose: `ScanPin.Probe` = the **committed** displayed pose (every consumer — matrix, inspect range, brushing — reads this one); `ScanPin.ProbeOther` = the same probe at the opposite Before/After pose, fetched only once a solve exists, consumed **only** by the violin chart's inactive half. `SetRegView` **swaps** a ready (Probe, ProbeOther) pair in place instead of refetching (and clears `BrushedSamples` — gids index the committed pose's canonical array); everywhere else the two invalidate together (`ScanPinModel.invalidateProbes`). `ScanPin.Slice`/`SliceOther` (vertical cross-section polylines feeding the show-overlays hold: label profile charts + 3D centre-slice lines) carry the **same** pose pairing — swapped by `SetRegView`, dropped by `invalidateProbes`, and the reg peek only *selects* the other cache (never queries). Slice frame constants/helpers live in `ScanPin` (`sliceUDir`/`sliceOffsets`/`sliceToWorld`/`sliceUV`). The Inspect scalar maps carry the pairing too: `SurfaceDistance`/`SurfaceDistanceOther` (variance on the reference) and `FocusDist`/`FocusDistOther` (per-mesh difference) — Other fetched only once a solve exists, swapped wholesale by `SetRegView` (which also cancels the in-flight CTSes + bumps both generations, since a landed result would file under the wrong pose), selected by the reg peek in `MeshView.inspectField`/`FocusScene.focusOverlay` so the paint flips with the geometry.
- Mode-dependent behaviour (per-mode isolation defaults, Inspect focus/pin policies) lives in the **reducer** (`SetWorkflowStep`, `SetSelection`), not in view-layer click handlers — so every entry path behaves identically. Outside Inspect a `SelMesh` selection ghost-emphasizes in the shader chain (`MeshView` `isActive`); in Inspect it routes through the solo overlay.
- Debounce/generation state (CTS + counters) lives at module level in `UpdateHelpers`/`ScanPinUpdate`, **not** in the Elm model. Visibility changes must go through `UpdateHelpers.setMeshVisible` — it invalidates the visibility-derived caches (variance map, focus-dist generation, brushed-sample ids).

## Render pipeline (single forward pass)

One forward pass into the main framebuffer — `passZero`: meshes (custom α + α-gated depth) then pin geometry (`DepthTest.LessOrEqual`, blended); `passOne`: coordinate cross, labels, overlay lines (`DepthTest.None`, always on top). The one offscreen consumer is the image-space outline pass.

Contracts the rest of the stack relies on (`MeshShader.shade`):

- **α-gated depth**: fragments with α ≥ 0.99 write their natural window depth; ghost/outside fragments write 1.0 (far) — so ghosts never occlude anything and pixel picks fall straight through them.
- **Ghost colour is uniform**: ghost fragments always use the solid per-mesh palette colour regardless of rendering mode, so a ghost silhouette reads as one shape.
- Solid/ghost/invisible is decided per fragment from `MeshActive` × the global ghost floor (`GhostSilhouette`/`GhostOpacity`; floor off ⇒ ghost fragments are *discarded*, i.e. hidden not translucent) × the pin-isolation blob mask (`Blobs` uniform array, hard cap 32, metric → render at upload). The scalar-field painters (difference/variance/displacement/heatmaps) only touch **above-ghost** fragments. **Inspect is de-cluttered (§B5)**: the per-mesh ghost floor is forced to 0 (every non-emphasized fragment discards — context meshes read as outline-only via the outline pre-pass, which renders ALL loaded meshes independently of the main pass) and `InspectPlain` swaps the base surface to plain near-white (no photo texture/palette/slope under the false-colour maps; shading kept).

### One Inspect error range (never per-mesh normalization)

Every Inspect false-colour map reads on the **same pin-derived scale** — do not reintroduce per-mesh/per-tile normalization (robust percentiles, user range sliders):

- `ScanPin.inspectRange` (ScanPinModel.fs) = signed (lo, hi) in metres over every ready pin's ROI probe samples on the moving meshes, always spanning 0, **hard-capped at ±0.5 m**; no pins ⇒ the full ±0.5 m. Adaptive wrapper: `MeshView.inspectRange`.
- Consumers: 3D `MeshShader` (`DistScale` = hi, `DistLoNeg` = |lo|; variance σ saturates at max(|lo|, hi)), focus tiles/single (`FocusHi`/`FocusLoNeg`), the bottom-centre legend (`GuiOverlays.colorLegend`, Inspect only), and the brushed sample dots (per-**pin** envelope via `ScanPin.pinErrorRange`).
- The diverging map is **asymmetric piecewise Coolwarm** (Colorcet CET-D01, as shipped by Maple — a deliberate user choice over the earlier yellow-centred map): zero = the ramp's near-white centre #EDE7EA (welded to 0 — grey is reserved for "within LoD / no data", never "0"), each sign runs zero→mid→saturated end (lavender→blue #2151DB / salmon→red #C00206) normalized by its own end with the t^0.6 near-zero boost (`Primitives.Diff.colorSignedV3`, mirrored in `MeshShaders`/`FocusShaders` — keep all three in sync). Values outside clamp to the end colours.
- Difference maps carry **value isolines**: dark derivative-antialiased contours every `Diff.isoStep` metres (a nice 1/2/5 step ≈ span/8 of the shared range, so 0 is always a contour), suppressed where the colour clamps; step is passed as a uniform (3D + focus), not model state. `ddx`/`ddy` exist in FShade, `fwidth` does NOT.
- Displacement is *not* an error metric: it saturates at `MeshView.displacementRange` — the exact global max |load→solved| over each solved mesh's world-bbox corners (rigid ⇒ max at a corner), uncapped.
- Intrinsic heatmaps (§B3): **Range** normalizes by the ONE all-mesh end `MeshView.rangeMaxWorld` (legend shown outside Inspect while any Dst is active); **Incidence** uses the geometric (screen-derivative) normal sign-oriented by the stored vertex normal, clamped at 0 — never `abs` (away-facing = never scanned = worst); **Shape** discards fragments below the global `ShapeThreshold` (Overview rail slider).
- The dock chart is ONE selection-driven one-sided **stacked histogram** (GuiInspector.fs): mesh → its samples stacked by pin (canonical order CreatedAt, guid), pin → that pin stacked by mesh (mesh colours), cell → the single pair (median tick), nothing/reference → the ensemble stacked by pin. 48 bins over the shared x-range (both poses, all moving meshes — comparable across selections). Solved → the diagram over-draws the **other** pose as a near-black step outline of its total (shape only, no colour); fill = emphasized pose, Peek flips only the emphasis. The chart carries NO selection UI of its own (metric toggles only: Difference|Displacement, M3C2|Δz); the shift readout sits beside it in both channels (re-render on resize via ResizeObserver).
- Sample brushing is **chart-drag only** (`SetBrushedSamples` from the dock chart's JS bridge): an x-range over the *conceptual* samples of the current diagram — gids stay indices into the canonical array (`ScanPinScene.brushSamples`, committed pose). Brushing = **sole focus**: a non-empty brush suppresses every Inspect error map (3D `inspectField` → enc 0, focus overlay → texture, displacement arrows off) and only the brushed dots carry value — small solid dots coloured on the SHARED inspect range (`ScanPinScene.brushedDotGeometry`, rendered in main 3D + focus single + tiles); the legend switches to "Brushed samples". A plain chart click clears and restores the maps. No 3D hover-reveal of samples.

### `Sg.DepthMask` is forbidden

Buggy in this Aardvark/Aardworx WebGL build — it silently breaks the depth pipeline. Steer ordering with `Sg.DepthTest` + `Sg.Pass` alone. Lines, pin geometry and text therefore all write depth; that violates the textbook "translucent shouldn't write depth" rule but is the only combination that renders correctly in this stack. Leave the in-code reminders in `LineShader.fs` / `SceneGraph.fs`.

### Image-space outline pass (`OutlineView.fs`)

MRT G-buffer (world-Z band parity + window depth → target0, palette colour + coverage → target1) → fullscreen edge-detect painting silhouettes + elevation isolines. Two non-obvious choices — keep them:

- The depth edge is the **second difference** (Laplacian) `|l + r − 2c|`, *not* a first difference: window depth is linear in screen space across any planar primitive, so the Laplacian is ~0 on a smooth slope at any view angle and spikes only at a genuine break. A first difference measures screen-space depth *slope* and lights up every grazing surface as false bands.
- target0 is **`Rgba8`**, so the stored window depth has 256 levels (1 LSB ≈ 0.004). An `OutlineThreshold` below that quantization floor makes the staircase risers of a smooth slope read as false bands.
- The isoline signal is band **parity** (`floor(wp.Z / ContourSpacing) mod 2`) — a step function, so its edge is a plain first difference; because the band index is a pure function of world Z, the contours stay welded to fixed world-Z planes and do not crawl as the camera orbits.
- Per-mesh FOOTPRINT contours come from a second, occlusion-free pass: an additive coverage MRT (one channel per mesh, 2×Rgba8, NO depth — cap 8 meshes) + the `OutlineCoverageEdge` composite, which outlines each channel's covered↔uncovered transition in that mesh's palette colour. The depth-tested combined G-buffer can only ever outline the visible **union** (no depth break where one mesh ends over a co-located one; hidden boundaries aren't in the buffer at all) — never remove the coverage pass in favour of it. Footprint lines obey the same `OutlineMask` flags (> 0.25).
- Per-mesh outline gating: the G-buffer writes a mesh id (`MeshId` = (index+1)/255, target0.y) and the edge composite gates lines through the `OutlineMask` slot array (`MeshView.outlineMask`): 1 = silhouette + isolines, 0.5 = **silhouette only** (Inspect pair view — with a mesh isolated, everything except it and the reference keeps just its contour, with a small G-buffer depth push so the co-located pair wins ties).
- `ContourSpacing` is **camera-adaptive** (§B2): ~24 contours across the view derived from the orbit radius, SNAPPED to a nice 1/2/5 world-metre step (discrete ticks — zooming out thins the lines stepwise, orbiting never changes them). The gear's `IsolineBands` sets the densest allowed spacing; the far end caps at ≥4 contours over the scene Z range. The difference-map value isolines need no camera term — their in-shader derivative fade already suppresses overcrowding.

### Picking

- `Sg.OnTap` / `OnDoubleTap` / `OnLongPress` **fire on background misses too** — every handler that builds state from the hit must gate: `if e.Location.Depth < 0.9999 then Some e.WorldPosition else None` (background leaves depth at the clear value 1.0).
- Ghost fragments leave depth at 1.0, so the GPU pixel pick cannot land on them. Anything that needs a 3D point on a possibly-ghosted surface keeps the GPU pick as the fast path and falls back to a server raycast (`View.resolvePick` / `raycastNearest*` / `raycastMesh`) — un-apply the mesh's displayed pose before the query, re-apply it to the hit. Hover-driven raycasts must be throttled (~60 ms) + generation-guarded.
- **Every node without `Sg.NoEvents` writes the GPU pick buffer** (id + `gl_FragCoord.z`, blending forced off there — screen alpha is irrelevant, `DepthTest.None` wins unconditionally). Overlay/composite geometry — especially fullscreen quads like the outline composite — must set `Sg.NoEvents` or it hijacks every pick with its own depth.
- The focus panel picks are **Dom-driven + server raycast** (`FocusScene.worldRayHit`) — `Sg.OnTap` does not fire reliably in the secondary render controls. Its cursor→NDC math must run in **CSS px**: the focus controls read `ViewportSize` ÷ the shared `FocusScene.dpr` published by the main view (binding `RenderControl.ClientSize` in a secondary control blanks it).

## Coordinate systems & transform hierarchy

Three spaces, two transforms. Keep them strictly separate — every boundary crossing goes through a named helper, never bare `* scale` / `± centroid` arithmetic.

**Spaces**

- **Mesh / server frame**: the mesh's stored OBJ coordinates `+ meshCentroid`. **Every `/api/query/*` coordinate — in and out — is in this frame**; the server subtracts the centroid itself.
- **Metric world**: the app's single canonical world (metres). Pin centres/radii, correspondence anchors-as-world, cursor world all live here. Metric world ≡ a mesh's server frame exactly at the load pose.
- **Render space**: what the GPU and cameras use — centroid-recentred, dataset-scaled, then posed.

**Two transforms — dataset first, then workspace:**

1. **Dataset transform** — a *similarity* (uniform scale + translation, never rotation), fixed per dataset. The **only** place `DatasetScale` and `CommonCentroid` enter. Cross it with `ScanPin.renderCentre`/`worldCentre` (points) and `ScanPin.renderLength` (lengths).
2. **Workspace transform** — a *rigid* per-mesh pose (the before/after registration pose). Render form = `ModelTransforms.displayedRender` / `MeshView.displayedMeshT`; metric-world form = `ModelTransforms.displayedWorld`. `RigidTransform.worldToRender`/`renderToWorld` conjugate a rigid pose between the two (the dataset similarity is the conjugator). **`displayedWorld.Backward` maps metric world → the mesh's server frame; `.Forward` maps back.**

**Discipline rules**

- Server queries: convert metric world in with `displayedWorld.Backward`, map results out with `.Forward`. Multi-mesh queries (`probe`, `region-distance`) instead pass each mesh's `displayedWorld.Forward` matrix and let the server place them.
- Scene-graph geometry is render space: convert model values at the boundary (`renderCentre`/`renderLength`, or `worldToRender` for poses).
- Directions need no scale handling (uniform scale ⇒ parallel); only the workspace rotation matters (`TransformDir`).
- Anchors are stored in the mesh's server frame (`displayedWorld.Backward world` at placement time — pose-independent).

## Panorama centre (`pano-centers.txt`)

Each mesh's OBJ origin is *supposed* to be its scan camera, but the data is often not centred on it — so the panorama eye is data-driven: one optional file per dataset, `data/{dataset}/pano-centers.txt`, lines `<mesh-folder> x y z` in **absolute world coords** (same frame as `*centroid.txt`); unlisted meshes fall back to the mesh origin. Served at `/api/datasets/{d}/pano-centers`, held in `Model.PanoCenters`. It is the sensor origin for the incidence/range heatmaps and the position of the focus panel's 360° camera (rendering and pick rays). To add centres: isolate a mesh, read the top-bar **world** coordinate at its visual centre, write a line — no code change.

## Adaptive performance (critical)

In the scene graph, **never depend on an entire record when you only need a subset of its fields**. The Elm-style model replaces whole records on every update, so an `AVal.map` over a full `ScanPin` (or `Model`) fires on *any* field change.

**Rule: project individual fields into separate `aval`s early, then build the dependency graph from those.**

```fsharp
// BAD — rebuilds on ANY pin change (probe result, selection, …)
let geo = pinVal |> AVal.map (fun po -> ... po.ContactRings ... po.InnerRadius ...)
// GOOD — only when the rings or radius actually change
let ringsVal  = pinVal |> AVal.map (Option.map (fun p -> p.ContactRings))
let radiusVal = pinVal |> AVal.map (Option.map (fun p -> p.InnerRadius))
let geo = (ringsVal, radiusVal) ||> AVal.map2 (fun rings r -> ...)
```

For scene-graph nodes (`Sg.Text`, `sg { ... }`) this matters even more: rebuilding an `AList` of sg nodes destroys and recreates GPU resources (font atlases, draw calls). Therefore:

- **Split structure from placement.** Build static sg node lists from slowly-changing data; use adaptive `Sg.Trafo` for fast-changing placement (uniform update, no rebuild).
- **Push adaptivity down.** A parent `AList.ofAVal` that rebuilds all children is expensive; an `AVal`-driven `Sg.Trafo` per stable child is cheap.
- **Don't build in-place-updating *lists* with `AVal.map (… IndexList.ofList) |> AList.ofAVal`.** That mints fresh `Index` keys every recompute, so any element change diffs as remove-all + add-all — churning every row's DOM/GPU resources and intermittently double-rendering in the reconciler. Derive a stable-identity incremental list instead: `AMap.map (project to just the row's inputs) |> AMap.toASet |> ASet.sortBy key |> AList.map row` (the rail pin×mesh matrix is built this way). Small, rarely-changing lists may use the simple form.
- **Never create a *transient* `aval` inside another aval's compute and read it.** `AVal.custom (fun t -> … (makeSomeAval args).GetValue t …)` (or `AVal.force` of a freshly built aval) can drop the dependency edge, so the outer aval **evaluates once and silently freezes**. This bit the focus single (rendered blank because it never saw the meshes load) and the constellation. Fix: **inline** the inner computation so its `model.X.GetValue t` reads hit the outer token directly, or bind the inner aval **once** outside the compute (a stable `let`). Reading a *stable* aval via `.GetValue t` is correct — only per-eval-built avals are the trap.

## Server query performance

Costly spatial queries (`probe`, `contact-rings`, `region-distance`) scale with mesh count × sample density:

- **Never issue per-mesh requests sequentially** — fan out with `Async.Parallel`; if a multi-mesh operation becomes hot, add a batched server endpoint with `Parallel.For`.
- **Parallelise the heavy server inner loop** when inputs are independent — Embree `Scene.Intersect` is thread-safe.
- **Cap density rather than grow linearly** (`maxPoints` / sample strides / `maxTris`).
- **Debounce user-driven triggers** with a `CancellationTokenSource` + generation counter so the next event cancels the previous and at most one fetch is in flight per invalidation.
- **Mesh caches are warmed at dataset load** by `bboxesHandler`, so the first interactive query never pays the lazy-load cost.

## Client compile order (`Superprojekt.fsproj`)

```
MeshData.fs            mesh fetch/parse, ApiConfig, shared Http.client
ProbeModel.fs          M3C2 probe DTOs
Query.fs               server query wrappers (Async)
CameraModel.fs / .g.fs OrbitState [<ModelType>]
OrbitController.fs     orbit camera + messages (project file, NOT the Aardvark library one)
RegistrationModel.fs   ScanPinId, anchors, readiness engine (WASM-free, shared with Supertests)
ScanPinModel.fs / .g.fs ScanPin + placement state
PinGeometry.fs         icosphere, sphere outline
Model.fs / .g.fs       [<ModelType>] Model + Selection + MeshVisibility + ModelTransforms
LineShader.fs          flat colour + pixel-constant 3D lines + vertex-coloured dot batches
Primitives.fs          widgets, showWhen, observedRender, palettes, Diff colormap, friendly names
Messages.fs            Message DU
ScanPinUpdate.fs       pin sub-reducer + probe/rings postludes
UpdateHelpers.fs       reducer helpers + debounce/generation state + anchor seeding
Update.fs              main reducer + variance/focus-dist postludes
MeshShaders.fs         RenderPass + MeshShader + OutlineGBuffer/OutlineEdge
MeshView.fs            LoadedMesh, buildScene, displayed transforms, pin blobs
OutlineView.fs         offscreen image-space outline pass
ScanPinScene.fs        pin sg nodes + constellation + brushed samples
SceneGraph.fs          scene composition + cross + labels + reference/focus outlines
FocusShaders.fs        focus colour fragment (Inspect/heatmap overlays)
FocusScene.fs          focus render controls (single + tiles), 360°/Top cameras, pick
GuiTopBar.fs           top bar + gear popover
GuiOverlays.fs         toast, scale bar, orientation indicator, wheel label, pin flag tags
GuiRail.fs             three-mode left rail (roster · pin×mesh matrix · Solve)
GuiFocus.fs            focus panel head + FocusScene mounts
GuiInspector.fs        mode-contextual bottom dock (distribution + brush bridge)
View.fs                view function + App module
ShaderCache.fs / Program.fs
```

## Server compile order (`Superserver.fsproj`)

```
MeshLoader.fs     OBJ parse, centroid file, atlas paths
MeshCache.fs      Embree scene + BbTree cache (lazy, permanent)
MeshAnalysis.fs   sphere contact-ring tracing
MeshProbe.fs      N-mesh M3C2 probe
RegMath.fs        weighted Umeyama rigid landmark solve (Jacobi SVD, conditioning)
QueryHandlers.fs  HTTP query handlers
Handlers.fs       routing
Program.fs        ASP.NET startup
```

## API endpoints

```
GET  /api/datasets                              → string[]
GET  /api/datasets/default                      → string (data/default.txt, fallback = first)
GET  /api/datasets/{dataset}/centroids          → { meshName: [x,y,z] }
GET  /api/datasets/{dataset}/pano-centers       → { meshName: [x,y,z] }   (absent file → {})
GET  /api/datasets/{dataset}/bboxes             → { meshName: { min, max } }   (warms the cache)
GET  /api/datasets/{dataset}/mesh/{name}/{i}    → binary mesh
GET  /api/datasets/{dataset}/mesh/{name}/{i}/atlas → JPEG
POST /api/query/ray                             → { hit, t, point, triangleId }   Name = "dataset/mesh"
POST /api/query/closest                         → { found, point, distanceSquared, triangleId }
POST /api/query/contact-rings                   → sphere–surface intersection polylines
POST /api/query/lsq-pairs                       → weighted rigid solve (absolute world transform + residuals + conditioning; 400 on <3 pairs)
POST /api/query/probe                           → N-mesh M3C2 probe (per-mesh distributions + per-sample positions)
POST /api/query/slice                           → N-mesh vertical cross-sections: mesh∩plane polylines for every mesh × parallel plane offset in one request (plane frame = centre + uDir/normal, disc-clipped to the probe sphere), returned as flat (u,v) chart-frame pairs
POST /api/query/region-distance                 → per-vertex signed M3C2 distance (mode 0) or vertical Δz (mode 1) of a target mesh to the reference, in the target's served vertex order; both modes share one support rule — a vertex responds only where the vertical world line through it pierces the reference (Z-overlap), else 1e30 sentinel — so M3C2 never fabricates error in non-overlap fringes, and the variance map (which skips sentinels per mesh) only aggregates meshes that overlap there
```

All query coordinates are **absolute world space**; the server computes `localPos = worldPos − meshCentroid`. (Endpoints without consumers were removed — `/query/icp`, `/query/patch`, sphere/box/ray-batch, grid-eval, isoline, curvature-ridge, region-grid; don't re-add one without a consumer.)

## Tests

`src/Supertests` — console runner (no test packages) compiling `RegistrationModel.fs` + `RegMath.fs` directly: `dotnet run --project src/Supertests`. Integration against a running server: `ASPNETCORE_URLS=http://localhost:8002 dotnet run --project src/Superserver`, then `node tools/integration.mjs`.

## Aardvark.Dom gotchas

- `Attribute("for", "...")` on `<label>` is silently dropped — nest `<input>` inside `<label>`.
- `Attribute("checked", "")` is dropped — use `Attribute("checked", "checked")`.
- CSS `~` sibling combinator breaks (Aardvark inserts wrapper nodes) — use `:has()` on a known ancestor.
- `RenderControlInfo` and `TraversalState` both have `.Runtime` — annotate `(info : Aardvark.Dom.RenderControlInfo)` when ambiguous.
- `yield!` is not supported in Aardvark.Dom CE builders — use OnBoot JS with MutationObserver for dynamic SVG/canvas (the `observedRender` helper).
- **OnBoot may run before a *later-sibling* node is mounted.** Don't capture a following sibling at boot (`querySelector` stored in a closure freezes `null` forever) — look siblings up **lazily** inside the handler that needs them. Boot-time capture is only safe for **ancestors** (`closest(...)`).
- `renderControl { ... }` nests fine inside `div { ... }`; multiple controls coexist. But **`Sg.OnTap` (and other Sg pointer events) do NOT fire reliably in secondary render controls** — use Dom pointer handlers + a server raycast there. Camera input in secondary controls is also Dom-level, **without** pointer capture (capture hijacked later clicks).
- `AVal.map4` does not exist — combine with `AVal.map2`/`AVal.map3`.
- `Dom.Style` for renderControl; `Style` for HTML elements. `Css.Custom` does not exist — use CSS classes in `style.css`.
- **`RenderControl.ViewportSize` is framebuffer pixels**; `RenderControl.ClientSize` is CSS pixels. Anything mixing with DOM coordinates (overlay placement, cursor → NDC) must work in CSS px or it breaks on hi-dpi. ClientSize is `V2i.II` until the first DOM event; the main control binds ClientSize with a ViewportSize fallback, the focus controls divide ViewportSize by the shared `FocusScene.dpr` instead (binding ClientSize there blanks the control).
- `Sg.OnPointerDown(bool, handler)` — the bool is capture-vs-bubble **phase**, not pointer capture. For drags call `e.Context.SetPointerCapture(e.Target, e.PointerId)` in down and release in up.
- `Dom.OnPointerDown((...), pointerCapture = true)` — browser-level pointer capture; use on canvas drags so events keep flowing when the cursor leaves.
- **`Sg.OnTap` / `OnDoubleTap` / `OnLongPress` fire on background misses too.** Always gate on `e.Location.Depth < 0.9999`.
- **FShade shaders must be float32-only.** `float`, `Constant.Pi`, `V3d`/`V2d`, and `member _ : float` uniforms emit GLSL `double`/`dvec3`, which WebGL2 (ESSL3) rejects at runtime. Use `3.1415927f`, `V3f`/`V2f`, `: float32` uniforms, bind `1.0f` not `1.0`.
- **FShade shader bodies must be lambda-free.** A local `let f x = …` inside a `fragment`/`vertex` body reads as an unsupported lambda — inline it.
- **`dotnet build` and `fshadeaot` do NOT catch either shader pitfall** — only the in-browser compile does, so always verify shader changes in a browser. Porting desktop-GL examples is the high-risk case.

## fsproj notes

- Client: `Microsoft.NET.Sdk.BlazorWebAssembly`, `net8.0`, `WasmBuildNative=true`, `LocalAdaptify=true`. Quick type-check: build with `-p:WasmBuildNative=false` (~35 s).
- Server: `Microsoft.NET.Sdk.Web`, `net8.0`; references the client project for static hosting. Runs on `http://localhost:5000`.
- Run Adaptify with `adaptify.cmd` (Windows) or `adaptify.sh` (Unix).

## CSS / design

- Light theme, `'Inter'`/`'Segoe UI'`, accent `#1a56db`. Body bg `#f4f6f8`, panel bg `#ffffff`, text `#0f172a`.
- All styles in `wwwroot/style.css`; no inline styles except model-dependent ones (positions, data-driven colours, cursor).
- Conditional visibility uses `Primitives.showWhen`/`showWhenNot` → `.hidden` (`display: none !important`), not inline display styles.
- The bottom dock's height is the `--dock-h` root var (default 220px, dragged via the dock's top-edge `.dock-resize` handle). Anything anchored to the dock top — the render-control height, the bottom-anchored overlays — must read `var(--dock-h)`, never a hardcoded px offset.
- `.btn-active`: darker blue with inset shadow for toggle buttons.
