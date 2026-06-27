# Superprojekt — assistant notes

Research prototype for interactive 3D inspection and **registration** of geological mesh datasets (multi-epoch scans of the same terrain). Two F# projects:

- **Superserver** — ASP.NET Core + Giraffe. Serves mesh data and runs spatial queries (Embree BVH ray/closest-point, sphere contact rings, surface patches, per-vertex signed distance, N-mesh M3C2 probes, weighted rigid landmark solve). Runs on `http://localhost:5000` and also hosts the WASM client.
- **Superprojekt** — Blazor WASM client. Aardvark.Dom Elm-style architecture, WebGL2 rendering. Must work on desktop and mobile; the client stays thin and pushes heavy compute to the server.

See `README.md` for what the app does and how to run it.

## Style

- Light theme, high contrast, print-appropriate.
- GUI must be readable to a non-expert at first glance.
- No comments unless the logic is non-obvious.
- Concise code, no unnecessary abstractions, no premature helpers.

## Render pipeline (single forward pass)

The default path is **one forward pass** into the main framebuffer: meshes → pins → cross/labels. FBOs are allowed (the one offscreen consumer is the optional image-space outline pass); the historic ban was specific to removed WBOIT code.

```
[ passZero ]
  • meshes      : MeshShader.shade → custom α + α-gated gl_FragDepth
  • pin geometry: DepthTest.LessOrEqual, alpha-blended
[ passOne ]
  • coordinate cross + tick lines + axis-tip/integer-metre labels
    DepthTest.None — always on top.
```

### Mesh shader (`MeshShaders.fs`, `MeshShader.shade`)

Inputs (custom record): `[<Color>] c : V4f` (from `DefaultSurfaces.diffuseTexture`), `[<Semantic("Normals")>] n : V3f`, `[<Semantic("WorldPosition")>] wp : V4f`, `[<Semantic("SurfaceDist")>] sd : float32` (per-vertex signed M3C2 distance; sentinel `1e30` = no encoding), `[<Semantic("ShapeQ")>] shq : float32` (per-vertex triangle quality), `[<FragCoord>] fc : V4f`.

Outputs: `[<Color>] color : V4f`, `[<Depth>] depth : float32`.

### Ghosting rules

Every mesh fragment ends up **opaque** (α = 1), **ghost** (α = effective ghost level), or **invisible** (discarded). Evaluation order:

1. **Effective ghost level**: `ghost = GhostOpacity` if `GhostSilhouette` is on (pre-gated on upload), else `0` — with the silhouette off, ghost fragments are *discarded* (`α < 1e-4` → discard), so "no ghost" means invisible, not translucent.
2. **MeshActive** (visibility / isolation): `false` → α = `ghost` for the whole mesh, uniformly.
3. **Pin isolation** (only when pins exist *and* the "Isolate pins" toggle / `AnchorGhost` uniform is on): `blobComponent = 1` inside any pin's `InnerRadius`, `0` outside every pin's radius; no pins or toggle off → `1`.
4. **Final α**: `α = MeshActive ? lerp(ghost, 1, blobComponent) : ghost`.

Consequences the rest of the stack relies on:

- **α-gated depth**: fragments with `α ≥ 0.99` write their natural window-space depth (`v.fc.Z`); everything else writes `1.0` (far) so ghost/outside fragments never occlude and never produce pixel-picks. A `fullySolid` clamp pins ghost/outside fragments below the threshold. (There was a `lassoComponent`; the lasso was removed — `blobComponent` is the only mask now.)
- **Ghost colour is uniform**: ghost fragments always use the solid per-mesh palette colour regardless of `RenderingMode`, so the silhouette reads as one shape.
- The signed-distance / heatmap painters only touch **above-ghost** fragments.

Uniforms per draw call: `MeshActive`, `GhostOpacity` (pre-gated by `GhostSilhouette`), `RenderingMode`, `MeshColor`, `ShadingStrength`, `SlopeThreshold`; `BlobCount` + `Blobs : Arr<N<32>, V4f>` = `(cx,cy,cz,innerRadiusRender)` + `AnchorGhost` (centres/radii are metric world-space on the model, `* datasetScale` on upload — `MeshView.pinBlobUniforms`, hard cap 32); `DistanceEncoding` + `DistScale`; `HeatmapMode` + `SensorOrigin`/`RangeMax`; the `Cursor*` set; `ClipPlane*` (fed a constant no-clip — generic support, no live consumer).

### Error colour maps (in the mesh shader, above-ghost only)

- `DistanceEncoding = 2` — **variance** map: per-reference-vertex disagreement std (std of every visible moving mesh's signed distance), sequential grey→red, painted on the reference. This is the Inspect central-3D aggregate; gated on `WorkflowStep = Inspect` (no toggle) and `DistScale`-normalised. (The historic `= 1` single-mesh signed-distance painter was removed — pair *difference* now lives in the focus tiles, not the main view.)
- `HeatmapMode = 1/2/3` — intrinsic incidence / range-from-own-origin / triangle-shape false colour (pre-existing acquisition channels; untouched by the Phase-2 Inspect work).

The variance distances come from `POST /api/query/region-distance` (reference vs each moving mesh) reduced to a per-reference-vertex std by the generation-guarded debounced `Update.ensureVariance` postlude into `Model.SurfaceDistance` (keyed by the reference mesh); the `SurfaceDist` buffer aligns with `loaded.pos`, non-encoded meshes bind a zero buffer of the same length.

### Inspect visualizations

The focus-tile channels are now rendered **in WebGL** (per-vertex-coloured mesh, not the old
2D-canvas/`mesh-preview` path) — a unified `FocusShaders.focusColor` fragment driven by a
`FocusMode` uniform over a per-vertex `FocusScalar` buffer (`FocusScene.focusOverlay`), so the
channel toggle is a uniform switch with no shader rebuild.

- **pin** → dock **distribution**: per moving mesh, jittered raw probe samples + median/IQR box on a shared signed-distance axis with the ±LoD₉₅ band (`GuiInspector` `distData`/`distJs`, no KDE). The probe is the current `RegView` pose; the panel labels Before/After.
- **2 meshes** → focus **difference** (`FocusMode = 1`): signed M3C2 / vertical Δz vs reference, diverging blue↔neutral↔red about 0 (robust per-tile scale). Data = `region-distance` (target = moving mesh) fetched by `Update.ensureFocusDist` into `Model.FocusDist` (the mesh's served vertex order, aligns with `loaded.pos`); same generation-guarded debounce as `ensureVariance`.
- **mesh** → focus **displacement**: solved moving meshes only. The **large single** shows a white surface (`FocusMode = 3`) + **load→solved arrow line-glyphs** (`Lines.render`, exaggerated to ~18% of the fit extent for visibility, coloured light→dark blue by *true* magnitude, with XY-plane arrowheads) — forced to the ortho camera (lines can't go through the pano unwrap, so Pano collapses to Top here). The **tiles** show the sequential magnitude heatmap (`FocusMode = 2`, per-vertex `|load→solved|` computed in `focusOverlay`).
- **all meshes** → central-3D **variance**: per-reference-vertex disagreement std painted on the reference (`DistanceEncoding = 2`, above), `region-distance` + `ensureVariance`.
- **shift readout** (dock, displacement only): the focused mesh's centroid displacement load→solved, split vertical(datum)/horizontal + rotation angle, derived client-side from its `SolvedTransform`.

The reference tile (and any mesh without data) stays atlas-textured (`FocusMode = 0`).

### `Sg.DepthMask` is forbidden

Do not add `Sg.DepthMask` anywhere — it is buggy in this Aardvark/Aardworx WebGL build and silently breaks the depth pipeline. Ordering is steered with `Sg.DepthTest` + `Sg.Pass` alone. This means lines, pin geometry, and text all write depth too — that violates the textbook "translucent shouldn't write depth" rule but is the only combination that renders correctly in this stack. Leave the in-code reminders in `LineShader.fs` / `SceneGraph.fs`.

### Image-space outlines (`OutlineView.fs`)

The one offscreen pass, gated by the gear toggle: every visible mesh renders into an MRT G-buffer (`OutlineGBuffer` → world normal + window depth in target0, palette colour + coverage mask in target1); a fullscreen edge-detect (`OutlineEdge`) paints per-mesh outlines where depth/normal/coverage jumps. The coverage-mask boundary catches the silhouette *and* the near-plane cut (no inverted hull).

### Picking

Pixel picking via `Sg.OnTap` / `Sg.OnPointerMove`:

```fsharp
let pick = if e.Location.Depth < 0.9999 then Some e.WorldPosition else None
```

Background misses leave depth at the clear value (1.0); the gate is required. The α-gated depth write is what makes this work — ghost fragments leave depth at 1.0 so picks pass through them to the opaque surface behind. `Sg.OnTap`/`OnDoubleTap`/`OnLongPress` fire on background misses too — any handler that builds state from `e.WorldPosition` MUST gate on the depth check.

The **focus-panel correspondence pick** is different (no GPU there): a 2D-frame coord is inverted to a world ray (orthographic for Top, cylindrical for Pano), raycast server-side (`/query/ray`), and the hit mapped to metric world. It is gated behind a **set-correspondence toggle** (`Model.CorrSetMode`, the focus-head **⊕ set point** button): off ⇒ the focus single pans normally; on ⇒ the cursor aims the point (no pan), a throttled hover raycast feeds `Model.CorrPreview` (a live cyan 3D ghost in `ScanPinScene`), and a click commits (`PickCorrespondenceAt`, stored mesh-local) and exits the mode. Toggling off cancels without committing (the anchor was never touched, so the tile redraws the committed marker). `castWorld` in `GuiFocus` is the shared ray-cast for both the commit and the hover preview.

## Before/after registration model

There is **no preview / commit / undo history**. Per mesh:

- `Model.LoadTransforms : Map<mesh, Trafo3d>` — the immutable baseline captured at load (= "before"; identity render-space, meshes load unregistered).
- `Model.SolvedTransforms : Map<mesh, Trafo3d>` — written by the correspondence solve (= "after"); presence ⇔ solved.
- `Model.RegView = RegBefore | RegAfter` — one global toggle (top bar), disabled until any `SolvedTransform` exists, flips 3D + focus + dock together.
- **Displayed** transform (`ModelTransforms.displayedRender/displayedWorld`, `MeshView.displayedMeshT`) = `RegView = RegAfter && solved → SolvedTransform`, else `LoadTransform`. Reference + unsolved meshes therefore stay at `LoadTransform` in both states.

`SolveCoarse` → `POST /api/query/lsq-pairs` per visible moving mesh with ≥3 in-ROI correspondence pairs (parallel). Correspondence anchors are **mesh-local**, so the pairs are taken at the load pose (own-frame point); the server returns the **absolute** world transform mapping load → reference, which the reducer stores directly as `SolvedTransform` (`worldToRender`) and flips `RegView = RegAfter`. A re-solve replaces `SolvedTransform` only; `LoadTransform` is never overwritten. Server math: `RegMath.solveRigid`, weighted Umeyama/Arun with Jacobi SVD + det-flip so planar/collinear sets never reflect; response carries per-pair residuals + covariance eigenvalues + `collinearityWarning`. Changing the reference drops `SolvedTransforms` + snaps to `RegBefore` (a solve is relative to its reference).

**Correspondences** (`ScanPin.Correspondence option`): `{ RefAnchor (reference own-frame marker — the pin centre if the host is the reference, else its closest-point projection); RefDistance; Anchors : Map<mesh, {Point; Source}>; Residuals; InRoi }`. **Every pin is a registration pin** — there is no enable/disable distinction. `makeAnchor` gives each new pin `Some Correspondence.empty`, and placing a pin auto-seeds it against the reference (if one exists); a freshly placed pin is selected. `Anchors.Point` is mesh-local; display/solve derive world via the displayed transform, so the before/after toggle moves markers automatically (no `bakeAnchors`). Auto-seed (placement / reference change / ⟳) projects via parallel `/query/closest`, ROI-clamped to `roiReach`; manual `AnchorPick3D` markers are never overwritten by auto-seed. `AnchorSource = AnchorAuto | AnchorPick3D`. The 3D **constellation** (`ScanPinScene`) draws a glyph per anchor + a haloed reference glyph + lines to the reference.

## Three-mode GUI + shared selection

The left rail (`GuiRail`) has exactly three modes — **Overview · Correspondence · Inspect** (`Model.WorkflowStep`). **Containers never move on a mode change**; only their content + which affordances are interactive change (cross-faded). Region roles are fixed: rail = *what object I work on*; focus = *per-mesh spatial work*; dock = *the parts of the selected object*.

| Mode | Rail | Focus (canvas) | Dock |
|---|---|---|---|
| Overview | mesh list (hover=peek-isolate, ★ reference) | WebGL textured tiles (difference/displacement recolour in Inspect) | mesh roster |
| Correspondence | pin list + readiness diagnostics | pick surface (Pano/Top) | correspondence manager + Solve |
| Inspect | difference sub-mode + intrinsic channels | difference / displacement tiles (channel toggle) | channel toggle + pin distribution + shift readout |

**One shared selection record drives all linking** (`Model.Selection = { SelectedPin; FocusedMesh; SelectedPoint; Hovered }`). Linked highlighting is a *consequence* of every region binding to `Selection` — there are no panel-to-panel hover emitters. Grammar everywhere: **hover = peek** (writes `Selection.Hovered` via the single `SetHovered`), **click = select/promote**, **drag = edit**. Pin selection lives in `Selection.SelectedPin` (NOT `ScanPinModel`); `ScanPinUpdate.handleMsg` maintains it and drops a dangling selection when its pin is deleted.

**Focus panel** (`GuiFocus` + `FocusScene`) is **WebGL** — a large single (the focused mesh, rendered full-res + atlas-textured in render space at its displayed pose) over a strip of textured thumbnail tiles, one renderControl per visible mesh (`FocusScene.single`/`multiples`). The prior 2D-canvas/`mesh-preview`/`FocusMaps` path is gone. The earlier finding that a 2nd renderControl tanks the main view turned out wrong (measurement artefact) — many renderControls coexist fine here. **Top = strictly orthographic** (hand-built ortho matrix); **Pano = cylindrical unwrap in a vertex shader** (`FocusShaders.pano`, composed after `DefaultSurfaces.trafo` so the WorldPosition varying — and thus picking — survives; the camera is identity, the shader writes clip). A tiny pan+zoom controller (module-level `panNorm`/`zoom` cvals in `FocusScene`, no orbit) drives the single with mouse-anchored zoom; `⟲ reset` calls `FocusScene.resetCam`. **Picking is Dom-driven, not `Sg.OnTap`** (that did not fire reliably in the 2nd render control): `worldRayHit` inverts the cursor to a render-space ray (ortho drop / pano direction from the eye), maps it into the mesh's own frame, hits `/query/ray`, and maps the hit back to displayed world. In set-correspondence mode (⊕ set point) a **move** throttle-raycasts → `CorrPreviewComputed` (the live 3D ghost in the main viewport), and a **click** raycasts → `PickCorrespondenceAt` (places + exits the mode). Gated on a selected pin + a non-reference focused mesh. In Inspect the tiles recolour per channel via `focusColor`/`FocusMode` (see Inspect visualizations).

## ScanPin system

A ScanPin is a 3D annotation in **metric world-space**: `Centre : V3d`, `InnerRadius : float` (a hard sphere — α = 1 and full probe weight inside). Pins drive the per-pixel blob in the mesh shader (`Blobs` uniform). Render-space conversions happen at boundaries (`ScanPin.renderCentre`/`renderLength`).

**Placement:** Correspondence mode → **○ Place pin** → tap the reference surface. Click-and-drop: the pin is created immediately (no commit step), becomes selected, and placement ends. Radius is edited afterwards from the pin's detail panel (the Correspondence dock manager, `SetInnerRadius` on the selected pin). New pins take `Model.QuickPinRadius` (default 0.5 m) as their inner radius. `Placement : PlacementState = PlacementIdle | AnchorPlacement`.

**M3C2 probe:** every pin owns `Probe : ProbeState`. It samples all visible meshes inside a cylinder (radius = `InnerRadius`, length = 20 m fixed `ScanPin.fixedProbeLength`, axis = PCA normal of the reference inside the pin sphere) via `POST /api/query/probe` and returns per-mesh signed-distance distributions (re-centred so 0 = reference median) + the dataset/algorithm/conditioning decomposition. Lazy + debounced (`ScanPinUpdate.ensureProbe`, generation-guarded CTS); invalidation just resets to `ProbeNone`. The probe drives the Inspect dock's **pin distribution** panel (strip + box) and the difference field's ±LoD₉₅ detection-limit band.

**Contact rings:** every pin caches `ContactRings`; `ScanPinUpdate.ensureRings` debounced fan-out of `POST /api/query/contact-rings` over **all** meshes (visibility only gates rendering, never recompute), per-pin CTS. Transforms are rigid → centre inverse-transformed into each mesh's own frame, rings mapped back via the displayed transform.

**3D rendering** (`ScanPinScene.fs`): `pinDots` (clickable markers), `pinRings` (equator ring ⊥ probe axis + cached per-visible-mesh contact rings, in the pin's palette colour, occluded by geometry as the spatial cue), the correspondence `constellation`, `ghostPreview` (placement hover), and `pinGlyphs` (far-view verdict pole — green if every moving mesh's `|median| ≤ LoD₉₅`, red if any is significant). Marker world positions follow the displayed (before/after) transform because anchors are mesh-local.

## Adaptive performance (critical)

In the scene graph, **never depend on an entire record when you only need a subset of its fields**. The Elm-style model replaces entire records on every update, so an `AVal.map` over a full `ScanPin` (or `Model`) fires on *any* field change — even fields the computation doesn't use.

**Rule: project individual fields into separate `aval`s early, then build the dependency graph from those.**

```fsharp
// BAD — rebuilds on ANY pin change (probe result, selection, …)
let geo = pinVal |> AVal.map (fun po -> ... use po.ContactRings and po.InnerRadius ...)
// GOOD — only when the rings or radius actually change
let ringsVal  = pinVal |> AVal.map (Option.map (fun p -> p.ContactRings))
let radiusVal = pinVal |> AVal.map (Option.map (fun p -> p.InnerRadius))
let geo = (ringsVal, radiusVal) ||> AVal.map2 (fun rings r -> ...)
```

For scene-graph nodes (`Sg.Text`, `sg { ... }`) this matters even more: rebuilding an `AList` of sg nodes destroys and recreates GPU resources (font atlases, draw calls). Instead:

- **Split structure from placement.** Build static sg node lists from slowly-changing data; use adaptive `Sg.Trafo` for fast-changing placement (uniform update, no sg rebuild).
- **Push adaptivity down.** A parent `AList.ofAVal` that rebuilds all children is expensive; an `AVal`-driven `Sg.Trafo` on each stable child is cheap. The constellation is built as a stable `(pin × mesh)` `ASet` for exactly this reason.

## Server query performance

Costly spatial queries (`probe`, `contact-rings`, `region-distance`) scale with mesh count × sample density:

- **Never issue per-mesh requests sequentially.** Use `Query.rayHitMany` (parallel fan-out); if a multi-mesh operation becomes hot, add a batched server endpoint with `Parallel.For` instead.
- **Parallelise the heavy inner loop server-side** when inputs are independent — Embree `Scene.Intersect` is thread-safe.
- **Cap density rather than grow linearly** (`maxPoints` / sample strides / `maxTris`).
- **Debounce user-driven triggers** with a `CancellationTokenSource` + a generation counter so the next event cancels the previous and at most one fetch is in flight per invalidation (`ScanPinUpdate`/`UpdateHelpers` keep these CTS/generation refs at module level, NOT in the Elm model).
- **Mesh caches are warmed at dataset load** by `bboxesHandler` — it calls `MeshCache.get` for every mesh, so the first interactive query never pays the lazy-load cost.

## Client compile order (`Superprojekt.fsproj`)

```
MeshData.fs            mesh fetch/parse, ApiConfig, shared Http.client
ProbeModel.fs          M3C2 probe DTOs (ProbeResult/ProbeState)
Query.fs               server query wrappers (Async), rayHitMany fan-out
CameraModel.fs / .g.fs OrbitState [<ModelType>]
OrbitController.fs     OrbitMessage DU + orbit camera
RegistrationModel.fs   ScanPinId, correspondence anchors, readiness engine (Readiness.compute), FlyToMath, NavAction, RegJson, HeatmapMode (WASM-free, shared with Supertests)
ScanPinModel.fs / .g.fs ScanPin + placement state
PinGeometry.fs         icosphere, sphere outline
Model.fs / .g.fs       [<ModelType>] Model + Selection + RegView + ModelTransforms
LineShader.fs          Shader.flatColor + Lines (pixel-constant 3D lines)
Primitives.fs          widgets, showWhen/showWhenNot, observedRender, ReadinessView adapter
Messages.fs            Message DU
ScanPinUpdate.fs       pin sub-reducer + ensureProbe/ensureRings postludes
UpdateHelpers.fs       reducer helpers + debounce/generation state, seedAnchorsCore
Update.fs              main reducer + ensureVariance postlude
MeshShaders.fs         RenderPass + MeshShader + OutlineGBuffer/OutlineEdge
MeshView.fs            LoadedMesh, buildScene, load/displayed transforms, pin blobs
OutlineView.fs         offscreen image-space outline pass
ScanPinScene.fs        pin sg nodes + correspondence constellation
SceneGraph.fs          composes meshScene + pinScene + cross + labels + reference outline
FocusShaders.fs        FShade pano (cylindrical) vertex shader for the focus single
FocusScene.fs          WebGL focus renderControls (single + per-mesh tiles, ortho/pano, pan/zoom, pick)
GuiTopBar.fs           top bar (peek, before/after toggle, gear popover)
GuiOverlays.fs         toast, scale bar, orientation indicator, wheel label
GuiRail.fs             three-mode left rail
GuiFocus.fs            focus panel head + FocusScene mounts
GuiInspector.fs        mode-contextual bottom dock
View.fs                App module wires Boot.run
ShaderCache.fs / Program.fs
```

`.g.fs` files are Adaptify-generated. **Never edit them by hand.** Re-run `dotnet adaptify --local --force ./src/Superprojekt/Superprojekt.fsproj` (or `adaptify.sh`) after editing the corresponding model `.fs`.

## Server compile order (`Superserver.fsproj`)

```
MeshLoader.fs     OBJ parse, centroid file, atlas paths
MeshCache.fs      Embree scene + BbTree cache (lazy, permanent)
MeshAnalysis.fs   sphere contact-ring tracing, patch sampling
MeshProbe.fs      N-mesh M3C2 probe (normal PCA, cylinder sampling, KDE, three sources)
RegMath.fs        weighted Umeyama rigid landmark solve (Jacobi SVD, conditioning)
QueryHandlers.fs  HTTP query handlers
Handlers.fs       routing
Program.fs        ASP.NET startup
```

## API endpoints

```
GET  /api/datasets                              → string[]
GET  /api/datasets/default                      → string (from data/default.txt, fallback = first alphabetically)
GET  /api/datasets/{dataset}/centroids          → { meshName: [x,y,z] }
GET  /api/datasets/{dataset}/bboxes             → { meshName: { min:[x,y,z], max:[x,y,z] } }   (also warms the cache)
GET  /api/datasets/{dataset}/mesh/{name}        → count of OBJ files
GET  /api/datasets/{dataset}/mesh/{name}/{i}    → binary mesh
GET  /api/datasets/{dataset}/mesh/{name}/{i}/atlas → JPEG
POST /api/query/ray                             → { hit, t, point, triangleId }     Name = "dataset/mesh"
POST /api/query/closest                         → { found, point, distanceSquared, triangleId }
POST /api/query/patch                           → frame-projected triangles (planar px,py + atlas UVs + index triples), optional frameNormal/frameRefDir override
POST /api/query/contact-rings                   → sphere–surface intersection polylines (closed rings repeat the first point)
POST /api/query/lsq-pairs                       → weighted rigid landmark solve (absolute world transform moving→reference + per-pair residuals + conditioning; 400 on <3 pairs)
POST /api/query/probe                           → N-mesh M3C2 probe (per-mesh distributions + KDE + three sources)
POST /api/query/region-distance                 → per-vertex signed M3C2 distance (mode 0) or vertical Δz (mode 1) of a target mesh to the reference, in the target's served vertex order; 1e30 sentinel where no closest point
```

All query coordinates are **absolute world space**; the server converts `localPos = worldPos − meshCentroid`. (Removed for lack of consumers — don't re-add without one: `/query/icp`, sphere/box/ray-batch, grid-eval, isoline, curvature-ridge, region-grid.)

## Client Model snapshot

Top-level `Model` fields (see `Model.fs`):

- `Camera`, `MeshOrder`, `MeshNames`, `MeshVisible`, `MeshesLoaded`, `CommonCentroid`, `MenuOpen`, `SavedMenuOpen`, `DebugLog`
- `Datasets`, `ActiveDataset`, `DatasetScales` (`{"SETSM_glacier" → 0.01}`), `DatasetCentroids`
- `GhostSilhouette` (default on), `GhostOpacity`, `ShadingStrength`, `SlopeThresholdDeg`, `AnchorGhostMode` ("Isolate pins", default on), `QuickPinRadius`, `OutlineMode`
- `SceneBounds`, `MeshBounds`, `ActivePickingLayer`, `ReferencePeekHeld`
- `LoadTransforms`, `SolvedTransforms`, `RegView` (`RegBefore`/`RegAfter`), `Registration` (`{ ReferenceMesh; Running }`), `LastSolve` (per-mesh solve diagnostics)
- `MeshSensorTypes`, `HeatmapMode` (`HeatOff | HeatIncidence | HeatRange | HeatShape`), `ExtrinsicZDiff` (difference sub-mode M3C2 ↔ Δz), `SurfaceDistance` (`Map<mesh, float32[]>`, the reference variance array), `FocusDist` (`Map<mesh, float32[]>`, per moving mesh signed distance for the focus difference channel)
- `ScanPins` (`ScanPinModel`), `Selection` (`{ SelectedPin; FocusedMesh; SelectedPoint; Hovered }`)
- `RenderingMode`, `MeshSolo`, `GearPopoverOpen`
- `WorkflowStep` (`Overview | Correspondence | Inspect`), `InspectChannel` (`ChDifference | ChDisplacement`), `FocusProjection` (`ProjPano | ProjTop | ProjOblique`), `FocusPeekReference`, `CorrSetMode` (set-correspondence toggle) + `CorrPreview` (live 3D ghost point), `Toast`

GUI placement:
- Top bar (`GuiTopBar`): hamburger, camera reset, **👁 Peek** (hold), the global **Before/After** toggle, gear popover (dataset switch, rendering mode, outlines, camera speed, ghost silhouette + opacity, isolate-pins, shading strength, slope threshold, quick-pin radius, dataset info, mesh centroids, debug log).
- Left rail (`GuiRail`): the three modes.
- Right focus panel (`GuiFocus` + `FocusScene`): WebGL large-single + per-mesh tiles.
- Bottom dock (`GuiInspector`): mode-contextual content.
- Overlays (`GuiOverlays`).

## Tests

`src/Supertests` is a console runner (paket-managed, no extra packages) that compiles `RegistrationModel.fs` + `RegMath.fs` directly and covers: the Umeyama solver (recovery, reflections, weights, collinearity, <3-pairs rejection), the `RegJson` correspondence + last-solve round-trips, `RegConditioning`, the readiness engine, and the fly-to math — `dotnet run --project src/Supertests`. Against a running server (`ASPNETCORE_URLS=http://localhost:8002 dotnet run --project src/Superserver`): `node tools/integration.mjs` (closest-point seed → rigid perturbation → `/query/lsq-pairs` recovers its inverse → `/query/probe` median error shrinks → patch frame echo).

## Aardvark.Dom gotchas

- `Attribute("for", "...")` on `<label>` is silently dropped — nest `<input>` inside `<label>`.
- `Attribute("checked", "")` is dropped — use `Attribute("checked", "checked")`.
- CSS `~` sibling combinator breaks (Aardvark inserts wrapper nodes) — use `:has()` on a known ancestor.
- `RenderControlInfo` and `TraversalState` both have `.Runtime` — annotate `(info : Aardvark.Dom.RenderControlInfo)` when ambiguous.
- `yield!` is not supported in Aardvark.Dom CE builders — use OnBoot JS with MutationObserver for dynamic SVG/canvas (the `observedRender` helper, the focus-panel canvas, the orientation indicator).
- `renderControl { ... }` can be nested inside `div { ... }` — it creates a WebGL canvas child. The app has **several**: the main viewport plus the focus panel's single + one per visible mesh (`FocusScene`). Multiple controls coexist fine on this backend (an earlier "they tank the main view" finding was a measurement artefact). **`Sg.OnTap` (and the other Sg pointer events) did NOT fire reliably in the secondary focus controls** — the focus does its picking with Dom pointer handlers + a server `/query/ray` raycast instead (`FocusScene.worldRayHit`). Camera input there is also Dom-level (`Dom.OnPointerDown/Move/Up` without pointer capture — capture hijacked later clicks).
- `AVal.map4` does not exist — combine with `AVal.map2`/`AVal.map3`.
- `Dom.Style` for renderControl; `Style` for HTML elements. `Css.Custom` does not exist — use CSS classes in `style.css`.
- **`RenderControl.ViewportSize` is framebuffer pixels** (CSS × devicePixelRatio); `RenderControl.ClientSize` is CSS pixels. Anything mixing with DOM coordinates — overlay placement (scale bar, tooltips), cursor → NDC math (pickRay, focus-panel pick) — must use ClientSize or it breaks on hi-dpi. ClientSize is `V2i.II` until the first DOM event; `View.fs` derives `overlaySize` with a ViewportSize fallback.
- `Sg.OnPointerDown(bool, handler)` — the bool is **capture-vs-bubble phase** for the Sg event bus, not pointer capture. For drags call `e.Context.SetPointerCapture(e.Target, e.PointerId)` in down and `ReleasePointerCapture` in up.
- `Dom.OnPointerDown((...), pointerCapture = true)` — browser-level `element.setPointerCapture`; use on canvas drags so events keep flowing when the cursor leaves.
- **`Sg.OnTap` / `OnDoubleTap` / `OnLongPress` fire on background misses too.** Always gate on `e.Location.Depth < 0.9999`.
- **FShade shaders must be float32-only.** `float`, `Constant.Pi`, `V3d`/`V2d`, and `member _ : float` uniforms emit GLSL `double`/`dvec3` which WebGL2 (ESSL3) rejects at runtime. Use `3.1415927f`, `V3f`/`V2f`, `: float32` uniforms, bind `1.0f` not `1.0`.
- **FShade shader bodies must be lambda-free.** A local `let f x = …` inside a `fragment`/`vertex` body reads as an unsupported lambda — inline it (see `OutlineEdge.fragment`).
- **`dotnet build` and `fshadeaot` do NOT catch either shader pitfall** — only the in-browser compile does, so always verify shader changes in a browser. Porting desktop-GL examples is the high-risk case.

## fsproj notes

- Client: `Microsoft.NET.Sdk.BlazorWebAssembly`, `net8.0`, `WasmBuildNative=true`, `LocalAdaptify=true`. Build for a quick type-check with `-p:WasmBuildNative=false` (~35 s).
- Server: `Microsoft.NET.Sdk.Web`, `net8.0`; references the client project for static file hosting. Runs on `http://localhost:5000`.
- Run Adaptify with `adaptify.cmd` (Windows) or `adaptify.sh` (Unix) — both wrap `dotnet adaptify --local --force ./src/Superprojekt/Superprojekt.fsproj`.

## CSS / design

- Light theme, `'Inter'`/`'Segoe UI'`, accent `#1a56db`. Body bg `#f4f6f8`, panel bg `#ffffff`, text `#0f172a`.
- All styles in `wwwroot/style.css`; no inline styles except model-dependent ones (positions, data-driven colours, cursor).
- Conditional visibility uses `Primitives.showWhen` / `showWhenNot` → `.hidden` (`display: none !important`), not inline display styles.
- `.btn-active`: darker blue with inset shadow for toggle buttons.
