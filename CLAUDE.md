# Superprojekt — assistant notes

Research prototype for interactive 3D mesh/pointcloud visualisation. Two F# projects:

- **Superserver** — ASP.NET Core + Giraffe. Serves mesh data and runs spatial queries (Embree BVH, plane intersections, cylinder evaluation, ICP). Runs on `http://localhost:5000` and also hosts the WASM client.
- **Superprojekt** — Blazor WASM client. Aardvark.Dom Elm-style architecture, WebGL2 rendering. Must work on desktop and mobile; the client stays thin and pushes heavy compute to the server.

See `README.md` for what the app does and how to run it.

## Style

- Light theme, high contrast, print-appropriate.
- GUI must be readable to a non-expert at first glance.
- No comments unless the logic is non-obvious.
- Concise code, no unnecessary abstractions, no premature helpers.

## Render pipeline (single forward pass)

There is **one render pass** into the main framebuffer. There is no OIT, no compose pass, no FBO. The earlier hybrid-forward + WBOIT pipeline was removed (see commit history if you really need it).

```
[ passZero ]
  • meshes      : MeshShader.shade → custom α + α-gated gl_FragDepth
  • pin geometry: DepthTest.LessOrEqual, alpha-blended
[ passOne ]
  • coordinate cross + tick lines + axis-tip/integer-metre labels
    DepthTest.None — always on top.
```

### Mesh shader (`MeshView.MeshShader.shade`)

Inputs (custom record):
- `[<Color>] c : V4f` (from `DefaultSurfaces.diffuseTexture`)
- `[<Semantic("Normals")>] n : V3f`
- `[<Semantic("WorldPosition")>] wp : V4f`
- `[<FragCoord>] fc : V4f`

Outputs (custom record):
- `[<Color>] color : V4f`
- `[<Depth>] depth : float32`

Per-fragment α:
- `MeshActive = false` → α = `GhostOpacity`
- `MeshActive = true`  → α = `lerp(GhostOpacity, 1, mask)` with `mask = max(lassoMask, blobMax)`
  - no lasso, no blobs → `mask = 1.0` → α = 1.0
  - lasso only → `mask = 1.0` inside, `0.0` outside
  - blobs only → smooth Gaussian falloff `exp(-d² / (2σ²))`
  - both → union (max) of the two

α-gated depth output:
- α ≥ 0.99 → writes `v.fc.Z` (window-space depth, identical to the rasterizer's natural depth)
- α < 0.99 → writes `1.0f` (far plane) so the fragment never occludes anything

Discard at `α < 1e-4f` so the gated path doesn't bother with truly invisible fragments.

Uniforms set per draw call:
- `MeshActive`, `GhostOpacity`, `RenderingMode`, `MeshColor`, `ShadingStrength`, `SlopeThreshold`
- `LassoPlaneCount` + `LassoPlanes : Arr<N<32>, V4f>` — outward-facing half-space planes packed as `V4f(nx, ny, nz, d)`; inside iff `dot(plane.xyz, p) + plane.w <= 0` for ALL active planes.
- `BlobCount` + `Blobs : Arr<N<32>, V4f>` — each pin packed as `V4f(cx, cy, cz, σ)` in render space (post-meshTrafo), matching `v.wp.XYZ`. Hard cap = 32.

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

Costly spatial queries (`cylinder-eval`, `plane-intersection`) scale with mesh count and ring/angle density. Rules of thumb:

- **Never issue per-mesh requests in a `for` loop.** Use the batched endpoints; the server fans out with `Parallel.For`. One HTTP roundtrip with N-way server parallelism beats N sequential roundtrips by an order of magnitude even on localhost.
- **Parallelise the heavy inner loop server-side** when inputs are independent. Embree `Scene.Intersect` is thread-safe.
- **Cap density rather than grow linearly.** Log-spaced ring ladders; angular resolution defaults to 180, not 360.
- **Keep heavy post-processing off the Elm update thread.** Union-find over band caches, ICP residuals, etc. run in the background task that issued the query; only the final result message crosses into the update loop.
- **Debounce user-driven triggers.** Cut-plane sliders ~300 ms, stratigraphy recomputes ~500 ms. Use a `CancellationTokenSource` ref so the next event cancels the previous.
- **Mesh caches are warmed at dataset load** by `bboxesHandler` — it calls `MeshCache.get` for every mesh + part, so the first interactive query never pays the lazy-load cost.

## Client compile order (`Superprojekt.fsproj`)

```
RankingState.fs
MeshData.fs
BspTree.fs
Query.fs
CameraModel.fs / .g.fs
OrbitTypes.fs
OrbitController.fs
ScanPinModel.fs / .g.fs
PinGeometry.fs
Model.fs / .g.fs                ← [<ModelType>], Adaptify-generated .g.fs
BlitShader.fs                   ← empty placeholder, kept so the fsproj is stable
Shader.fs                       ← Shader.flatColor + helpers
LineShader.fs                   ← Lines.render (pixel-constant 3D lines)
Primitives.fs                   ← compactToggle, inlineSlider, compactButtonBar, etc.
Messages.fs                     ← Message DU
CardUpdate.fs / ScanPinUpdate.fs
Update.fs                       ← main reducer
MeshView.fs                     ← LoadedMesh, MeshShader.shade, buildScene
ServerActions.fs                ← init + loadDataset (datasets list + centroids + bboxes)
ScanPinScene.fs                 ← pin sg nodes
SceneGraph.fs                   ← composes meshScene + pinScene + cross + labels
CardsPin.fs / Cards.fs
GuiTopBar.fs / GuiPanels.fs / GuiOverlays.fs / GuiCards.fs
View.fs                         ← App module wires Boot.run
ShaderCache.fs / Program.fs
```

`.g.fs` files are Adaptify-generated. **Never edit them by hand.** Re-run `dotnet adaptify --local --force ./src/Superprojekt/Superprojekt.fsproj` (or `adaptify.cmd` / `adaptify.sh`) after editing the corresponding `.fs` model file.

## Server compile order (`Superserver.fsproj`)

```
MeshLoader.fs          OBJ parse, centroid file, atlas paths
MeshCache.fs           Embree scene + BbTree cache (lazy, permanent)
MeshAnalysis.fs        cylinder evaluation, patch sampling, ridge tracing
MeshIcp.fs             ICP solver
QueryHandlers.fs       per-mesh HTTP handlers
BatchHandlers.fs       multi-mesh HTTP handlers (Parallel.For fan-out)
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
POST /api/query/ray-batch                       → binary closest-hit per ray across N meshes
POST /api/query/ray-grid                        → binary closest-hit + normal per ray
POST /api/query/plane-intersection              → single mesh, 2D cut polylines
POST /api/query/plane-intersection-batch        → multi-mesh, Parallel.For server-side
POST /api/query/grid-eval                       → per-cell stats inside a core sample prism
POST /api/query/cylinder-eval                   → per-ring per-angle mesh intersection heights
POST /api/query/isoline                         → polyline at a given elevation
POST /api/query/curvature-ridge                 → polyline along a curvature ridge
POST /api/query/patch                           → tangent + normal at a point, plus neighbour sample
POST /api/query/icp                             → ICP transform + convergence + residuals
```

All query coordinates are **absolute world space**. The server converts: `localPos = V3f(worldPos - meshCentroid)`.

The sphere / box / sphere-batch endpoints used by the old per-vertex filter feature were removed along with the `Filtered` model field — don't re-add them without a clear consumer.

## Client Model snapshot

Top-level `Model` fields (see `Model.fs`):

- `Camera`, `MeshOrder`, `MeshNames`, `MeshVisible`, `MeshesLoaded`, `CommonCentroid`, `MenuOpen`, `SavedMenuOpen`
- `DebugLog`
- `Datasets`, `ActiveDataset`, `DatasetScales` (`{"SETSM_glacier" → 0.01}`), `DatasetCentroids`
- `FullscreenOn`, `GhostSilhouette` (default **on**), `GhostOpacity` (0.5), `ShadingStrength` (0.5), `SlopeThresholdDeg` (30°), `AnchorGhostMode` (default **on**)
- `SceneBounds`, `MeshBounds`
- `ActivePickingLayer`
- `LassoDrawing`, `LassoVolume`
- `MeshTransforms`, `Registration` (mode + reference mesh + residuals + convergence + running flag)
- `MeshSensorTypes`, `MeshDatasetErrors`, `MeshAlgorithmResidual`, `ProvenanceHeatmap`, `ProvenanceThreshold`, `FalloffZoneOnly`
- `FusionMode`
- `ScanPins`, `ReferenceAxis` (AlongWorldZ | AlongCameraView), `Explore`, `ColorMode`, `CardSystem`
- `RenderingMode` (Textured | Shaded | SlopeColor), `MeshSolo`, `ExploreCardPos`, `GearPopoverOpen`

GUI placement:
- Left panel (`GuiPanels.leftPanel`): mesh list, pin list, error metadata, error provenance card, lasso card.
- Top bar (`GuiTopBar.topBar`): hamburger, dataset selector, pin-placement mode segmented control, explore toggle, camera reset, world coordinate readout, gear popover.
- Gear popover (debug flyout, end of `GuiTopBar.fs`): reference axis, camera speed, **Ghost silhouette toggle**, **Ghost opacity slider**, **Anchor-blob ghost toggle**, shading strength, slope threshold, dataset info, mesh centroids, debug log.

## ScanPin system

A ScanPin is a 3D annotation with a world-space anchor, a Gaussian falloff radius, and a payload (`Point` / `Line` / `Patch`). Pins drive the per-pixel blob in the mesh shader (`Blobs` uniform) and can host derived line/patch overlays.

**Placement workflow:** Top-bar segmented mode-selector (Profile / Plan / Auto) chooses a mode. After click-placement the pin enters `AdjustingPin` state with a flyout for radius / sigma / payload-type fine-tuning. Commit / Discard / Escape end placement.

**State:** `Placement : PlacementState` single DU on `ScanPinModel` — `PlacementIdle | AnchorPlacement | AdjustingPin of ScanPinId`. Helpers: `ScanPinModel.activePlacementId sp`, `ScanPinModel.isPlacing sp`.

**3D rendering** (`ScanPinScene.fs`): `pinDots` clickable markers, `pinSpheres` translucent shell + sigma sphere + selected-outline, `pinLines` for Line payloads, `pinPatchRings` for Patch payloads, `ghostPreview` for the placement hover.

## Open TODOs

- **Dead toggles to either re-wire or remove**: `ProvenanceHeatmap`, `FalloffZoneOnly`, `FusionMode`, `MeshAlgorithmResidual`, `CardSystem`-driven visuals. Their model fields and GUI controls survived the OIT removal but their render-time consumers were in shader paths that no longer exist.
- **`ActivePickingLayer`** is still toggled by wheel-zoom in `View.fs` but no longer restricts picking — pick happens against whatever is in the depth buffer.
- **No JSON / workspace persistence**: removed. Pins, lasso, transforms are all in-memory per session.
- **No real cut-plane mesh intersection rendering** in the pin diagram yet. The flyout shows the prism/blob but the cross-section profile is sketched out, not driven by `/query/plane-intersection-batch`.
- **No arcball gizmo** for pin axis tweaks; the flyout slider is the only adjustment.
- **No top-view mode** for the core sample inspector — `CoreSampleViewMode = TopView` exists on the model but isn't wired to a renderControl.
- **Ranking / BspTree / MeshIcp residual visualisation** is partial. Residuals come back from `/query/icp` but the chart in `GuiPanels` is rudimentary.

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
- `Sg.OnPointerDown(bool, handler)` — the bool is **capture-vs-bubble phase** for the Sg event bus, not pointer capture. For drag operations call `e.Context.SetPointerCapture(e.Target, e.PointerId)` inside the down handler and `ReleasePointerCapture(...)` in up.
- `Dom.OnPointerDown((...), pointerCapture = true)` — browser-level `element.setPointerCapture`; use this on renderControl canvas drags so events keep flowing when the cursor leaves the canvas.
- **`Sg.OnTap` / `Sg.OnDoubleTap` / `Sg.OnLongPress` fire on background misses too.** Always gate on `e.Location.Depth < 0.9999`.

## CSS / design

- Light theme, `'Segoe UI'`/`'Inter'`, accent `#1a56db`.
- Body bg `#f4f6f8`, panel bg `#ffffff`, text `#0f172a`.
- Render canvas (`.render-control`): `linear-gradient(to top, #d0dce8, #eaf1f8)`.
- All styles in `wwwroot/style.css`; no inline styles except model-dependent ones (e.g. cursor).
- `.btn-active`: darker blue with inset shadow for toggle buttons.

## fsproj notes

- Client: `Microsoft.NET.Sdk.BlazorWebAssembly`, `net8.0`, `WasmBuildNative=true`, `LocalAdaptify=true`.
- Server: `Microsoft.NET.Sdk.Web`, `net8.0`; references client project for static file hosting.
- Server runs on `http://localhost:5000`.
- Run Adaptify with `adaptify.cmd` (Windows) or `adaptify.sh` (Unix) — both wrap `dotnet adaptify --local --force ./src/Superprojekt/Superprojekt.fsproj`.
