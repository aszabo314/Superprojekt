# Superprojekt — assistant notes

Research prototype for interactive 3D mesh/pointcloud visualisation. Two F# projects:

- **Superserver** — ASP.NET Core + Giraffe. Serves mesh data and runs spatial queries (Embree BVH, closest-point, multi-mesh raycasts, isolines, curvature ridges, surface patches, ICP). Runs on `http://localhost:5000` and also hosts the WASM client.
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

### Mesh shader (`MeshView.MeshShader.shade`)

Inputs (custom record):
- `[<Color>] c : V4f` (from `DefaultSurfaces.diffuseTexture`)
- `[<Semantic("Normals")>] n : V3f`
- `[<Semantic("WorldPosition")>] wp : V4f`
- `[<FragCoord>] fc : V4f`

Outputs (custom record):
- `[<Color>] color : V4f`
- `[<Depth>] depth : float32`

### Ghosting rules

Every mesh fragment ends up in exactly one of three states: **opaque** (α = 1), **ghost** (α = effective ghost level), or **invisible** (discarded). The rules, in evaluation order:

1. **Effective ghost level**: `ghost = GhostOpacity` if `GhostSilhouette` is on, else `0`. With the silhouette off, everything that would render as ghost is *discarded* instead (`α < 1e-4` → discard) — "no ghost" means invisible, not translucent.
2. **MeshActive** (mesh visibility toggle): `false` → α = `ghost` for the *whole mesh*, uniformly. The lasso/pin filters below are skipped — a hidden mesh's silhouette deliberately ignores them.
3. **Lasso** (only when a polygon is committed *and* `LassoEnabled`; disabling uploads `LassoPlaneCount = 0` while keeping the polygon): `lassoComponent = 1` iff `dot(plane.xyz, p) + plane.w ≤ 0` for **all** outward-facing half-space planes `V4f(nx, ny, nz, d)`, else `0`. No active lasso → `1`.
4. **Pin isolation** (only when pins exist *and* the "Isolate pins" toggle / `AnchorGhost` uniform is on): each pin has an `InnerRadius` (hard core, weight 1) and a `FalloffRadius` (exponential decay `exp(-3·(d-inner)/(outer-inner))` to ≈0.05). `blobComponent` = max weight across all pins, `0` outside every pin's `FalloffRadius`. No pins or toggle off → `1`.
5. **Conjunctive mask**: `mask = lassoComponent * blobComponent`; final `α = lerp(ghost, 1.0, mask)`. Both filters must agree for full opacity — inside-lasso-outside-pins fragments are ghosted exactly like outside-lasso fragments. The pins carve the visible region *within* the lasso.

Consequences the rest of the stack relies on:

- **α-gated depth**: fragments with `α ≥ 0.99` write their natural window-space depth (`v.fc.Z`); everything else writes `1.0` (far plane) so ghost/falloff fragments never occlude and never produce pixel-picks. A `fullySolid` clamp pins non-hard-core fragments below the threshold so the depth-write branch can't flip mid-falloff (would create an occlusion ring).
- **Ghost colour is uniform**: fragments at ghost level always use the solid per-mesh palette colour, regardless of `RenderingMode`, so the silhouette reads as one shape.
- The provenance heatmap only paints **above-ghost** fragments; `FalloffZoneOnly` further restricts it to fragments inside at least one pin's falloff zone. The blob uniform arrays stay uploaded even when "Isolate pins" is off, because the conditioning term loops over them.

Uniforms set per draw call:
- `MeshActive`, `GhostOpacity` (pre-gated by `GhostSilhouette` on upload), `RenderingMode`, `MeshColor`, `ShadingStrength`, `SlopeThreshold`
- `LassoPlaneCount` + `LassoPlanes : Arr<N<32>, V4f>` — half-space planes (rule 3).
- `BlobCount` + `Blobs : Arr<N<32>, V4f>` = `(cx, cy, cz, innerRadiusRender)` + `BlobFalloffs : Arr<N<32>, V4f>` = `(falloffRadiusRender, 0, 0, 0)` + `AnchorGhost : int` (rule 4). Pin centres and radii are stored in **metric world-space** on the model and converted to render-space (`* datasetScale`) by `MeshView.pinBlobUniforms` — the single helper shared by the mesh and fusion scenes. Hard cap = 32.

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

Costly spatial queries (`ray-batch`, `isoline`, `curvature-ridge`, `icp`) scale with mesh count and sample density. Rules of thumb:

- **Never issue per-mesh requests in a `for` loop.** Use the batched endpoints; the server fans out with `Parallel.For`. One HTTP roundtrip with N-way server parallelism beats N sequential roundtrips by an order of magnitude even on localhost.
- **Parallelise the heavy inner loop server-side** when inputs are independent. Embree `Scene.Intersect` is thread-safe.
- **Cap density rather than grow linearly.** Bound point counts with `maxPoints` / sample strides; don't let resolution scale unbounded with region size.
- **Keep heavy post-processing off the Elm update thread.** Union-find over band caches, ICP residuals, etc. run in the background task that issued the query; only the final result message crosses into the update loop.
- **Debounce user-driven triggers.** Use a `CancellationTokenSource` ref so the next event cancels the previous.
- **Mesh caches are warmed at dataset load** by `bboxesHandler` — it calls `MeshCache.get` for every mesh + part, so the first interactive query never pays the lazy-load cost.

## Client compile order (`Superprojekt.fsproj`)

```
MeshData.fs
Query.fs
CameraModel.fs / .g.fs
OrbitTypes.fs
OrbitController.fs
ScanPinModel.fs / .g.fs
PinGeometry.fs
Model.fs / .g.fs                ← [<ModelType>], Adaptify-generated .g.fs
Persistence.fs                  ← workspace JSON serialise / apply
Shader.fs                       ← Shader.flatColor + helpers
LineShader.fs                   ← Lines.render (pixel-constant 3D lines)
Primitives.fs                   ← compactToggle, inlineSlider, compactButtonBar, etc.
Messages.fs                     ← Message DU
CardUpdate.fs / ScanPinUpdate.fs
Update.fs                       ← main reducer
MeshView.fs                     ← LoadedMesh, MeshShader.shade, buildScene, buildFusionNode, buildPanoramaNode
FusionView.fs                   ← offscreen MRT fusion pass + fullscreen composite
PanoramaView.fs                 ← offscreen cubemap capture + cylindrical reproject
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
MeshAnalysis.fs        isoline + curvature-ridge tracing, patch sampling
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
POST /api/query/grid-eval                       → per-cell stats inside a prism region
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
- `FullscreenOn`, `GhostSilhouette` (default **on**), `GhostOpacity` (0.12), `ShadingStrength` (0.15), `SlopeThresholdDeg` (15°), `AnchorGhostMode` (default **on**; "Isolate pins" in the UI — gates the pin blob filter, see Ghosting rules)
- `SceneBounds`, `MeshBounds`
- `ActivePickingLayer`
- `LassoDrawing`, `LassoVolume`, `LassoEnabled` (filter on/off, polygon kept)
- `MeshTransforms`, `Registration` (mode + reference mesh + running flag), `Retarget` (`RetargetIdle | RetargetProjecting | RetargetReviewing of RetargetCandidate[]`)
- `MeshSensorTypes`, `MeshDatasetErrors`, `MeshAlgorithmResidual`, `ProvenanceHeatmap`, `ProvenanceThreshold`, `FalloffZoneOnly`
- `FusionMode`
- `PanoramaOpen`, `Panoramas` (`Panorama list` = `{ Name; EyeWorld; Yaw }`, synthetic, regenerated on dataset load), `SelectedPanorama`, `PanoramaMode` (`PanoPhoto | PanoRender | PanoBlend`), `PanoramaBlend`
- `ScanPins`, `CardSystem`
- `RenderingMode` (Textured | Shaded | SlopeColor), `MeshSolo`, `LassoCardPos`, `GearPopoverOpen`

GUI placement:
- Left panel (`GuiPanels.leftPanel`): mesh list, pin list, error metadata, error provenance card.
- Top bar (`GuiTopBar.topBar`): hamburger, dataset selector, **◌ Lasso**, **○ Pin** placement, **◈ Fusion**, **▦ Pano**, camera reset, world coordinate readout, gear popover.
- Floating cards: pin cards are managed by `CardSystem` (`Cards.renderCards` — 3D-anchored, detachable, z-ordered); `lassoCard`, `registrationCard`, `panoramaCard` (`GuiCards.fs`) hold their position locally (`LassoCardPos` is the only persisted one). **All draggable cards share one chrome**: `Cards.cardDragHandle` / `Cards.cardPos` / `Cards.cardStyle` — don't hand-roll pointer-drag code for new cards. `lassoCard` is symbol-only: `◉/○` (enable/disable, polygon kept), `✎` (redraw), `⊘` (cancel drawing), `✕` (clear). `retargetCard` is a CSS-centered modal, not draggable.
- Gear popover (debug flyout, end of `GuiTopBar.fs`): retarget, workspace save/load, camera speed, **Ghost silhouette toggle**, **Ghost opacity slider**, **Isolate pins toggle**, shading strength, slope threshold, dataset info, mesh centroids, debug log.

## ScanPin system

A ScanPin is a 3D annotation in **metric world-space**: `Centre : V3d` (world metres), `InnerRadius : float` (hard truth — α = 1 and full evaluation weight inside; metres), `FalloffRadius : float` (exponential decay to ~0 by this distance; metres). InnerRadius and FalloffRadius are independent — `GhostOpacity` and falloff slider changes never move the inner radius. The placement flyout exposes inner radius directly and the falloff as a *relative* slider whose value is the delta `FalloffRadius - InnerRadius`; moving the inner slider preserves that delta. Pins drive the per-pixel blob in the mesh shader (`Blobs` + `BlobFalloffs` uniforms) and can host a `Point` / `Line` / `Patch` payload.

Render-space conversions happen at pipeline boundaries: `ScanPin.renderCentre cc scale` and `ScanPin.renderLength scale` in `ScanPinModel.fs`. `MeshView.buildScene` projects centres/radii to render-space on upload; `ScanPinScene.fs` does the same for marker dots, spheres, outline, patch footprint. The `Cards.projectToScreen` anchor is stashed in render-space by `ScanPinUpdate.handleMsg`. Camera focus (`OrbitMessage.SetTargetCenter`) takes render-space coords too.

**Placement workflow:** Top-bar segmented mode-selector (Profile / Plan / Auto) chooses a mode. After click-placement the pin enters `AdjustingPin` state with a flyout for radius / sigma / payload-type fine-tuning. Commit / Discard / Escape end placement.

**State:** `Placement : PlacementState` single DU on `ScanPinModel` — `PlacementIdle | AnchorPlacement | AdjustingPin of ScanPinId`. Helpers: `ScanPinModel.activePlacementId sp`, `ScanPinModel.isPlacing sp`.

**3D rendering** (`ScanPinScene.fs`): `pinDots` clickable markers, `pinSpheres` shows two translucent shells (outer = FalloffRadius, inner = InnerRadius) + selected-outline, `pinLines` for Line payloads, `pinPatchRings` for Patch payloads, `ghostPreview` for the placement hover.

## Registration

Two solve modes, both served by `POST /api/query/icp` (`MeshIcp.runIcp` — Gauss-Newton point-to-surface ICP, Embree closest-point correspondences):

- **Traditional ICP** — no anchors, uniform weights.
- **Region-restricted ICP** — committed pins become Gaussian anchor weights (`centre = pin.Centre`, `sigma = FalloffRadius`, multiplier = Point payload `ReliabilityWeight`); samples whose total weight falls below `RegionEps` (0.05) are excluded from the solve.

A third "point-pair correspondence" mode existed as a client-side stub (no server implementation, no-op button) and was **removed** — re-add it only together with a real server solver. The **Run button in the registration card is disabled** (`▶ Run (todo)`): the solve pipeline (`RunRegistration` → `Query.runIcp` → `RegistrationComplete` → `MeshTransforms` + per-mesh RMS into `MeshAlgorithmResidual`) is fully wired and the server solver is verified, but the UI is gated off until the registration feature ships. Residual *visualisation* (histogram + stats, `Registration.LastResiduals`) was removed — only the per-mesh RMS that feeds the provenance overlay is kept. The lasso never affects registration — it is purely a visual cut-away (by design, not a TODO).

## Open TODOs

- **Registration UI is gated off** — the Run button is disabled until the upcoming registration feature lands (see Registration above).
- **Panorama is a floating panel, not a docked split** (user's call), and viewpoints are synthetic — no dataset ships real imagery + poses, so one pose is generated per dataset on load. `PanoramaView.fs` captures the meshes into a colour cubemap from the pose (six 90° faces via the fusion `CompileRender` path) and reprojects cylindrically; two cubes (reference vs live state) feed Photo/Render/Blend. Click-to-place raycasts the pose ray server-side; markers are a forward projection of pins into cylindrical space. **All panorama shaders are float32-only** — WebGL2 has no double, and `dotnet build` / `fshadeaot` do NOT catch `double` GLSL (only the in-browser compile does). If real imagery + poses arrive, swap the synthetic pose generation in `Update.fs` (`SceneBoundsLoaded`) and add a Photo texture source.
- **Workspace persistence is download / upload only** (`Persistence.fs`): JSON round-trips through the browser via the gear-popover Save / Load. There is no server-side store, so state is otherwise in-memory per session. Panoramas are not persisted (regenerated on load).
- **Fusion picking is CPU-raycast**, not GPU winner-id readback — a per-tap server raycast over all visible meshes that keeps the lowest-error hit (identical winner). Revisit only if it gets slow.
- **No real cut-plane mesh intersection rendering** in the pin diagram yet. The flyout sketches the prism/blob cross-section but it is not driven by a server intersection query.

Not TODOs (accepted as-is): lasso does not scope registration (visual-only by design); the patch card's 2D↔3D hover coupling stays as it is. **Explore mode was removed entirely** (model, messages, card, top-bar button, CSS) — don't resurrect it from old branches.

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
- **FShade shaders must be float32-only.** F# `float`, `Constant.Pi`, `V3d`/`V2d`, and `member _ : float` uniforms all emit GLSL `double`/`dvec3` + fp64 `#extension` directives, which WebGL2 (ESSL3) rejects at runtime (`'double' : Illegal use of reserved word`). Use `3.1415927f`, `V3f`/`V2f`, `: float32` uniforms, and bind `1.0f` not `1.0`. **`dotnet build` and the `fshadeaot` PostBuild step do NOT catch this** — only the in-browser compile does, so always verify shader changes in a browser. Porting desktop-GL examples is the high-risk case (desktop GL supports double; WebGL2 does not).

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
