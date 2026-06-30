# Superprojekt — assistant notes

Research prototype for interactive 3D inspection and **registration** of geological mesh datasets (multi-epoch scans of the same terrain). Two F# projects:

- **Superserver** — ASP.NET Core + Giraffe. Serves mesh data and runs spatial queries (Embree BVH ray/closest-point, sphere contact rings, per-vertex signed distance, N-mesh M3C2 probes, weighted rigid landmark solve). Runs on `http://localhost:5000` and also hosts the WASM client.
- **Superprojekt** — Blazor WASM client. Aardvark.Dom Elm-style architecture, WebGL2 rendering. Must work on desktop and mobile; the client stays thin and pushes heavy compute to the server.

See `README.md` for what the app does and how to run it.

## Style

- Light theme, high contrast, print-appropriate.
- GUI must be readable to a non-expert at first glance.
- No comments unless the logic is non-obvious.
- Concise code, no unnecessary abstractions, no premature helpers.

## Render pipeline (single forward pass)

The default path is **one forward pass** into the main framebuffer: meshes → pins → cross/labels. FBOs are allowed (the one offscreen consumer is the image-space outline pass).

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
3. **Pin isolation** (only when pins exist *and* the "Isolate pins" toggle / `AnchorGhost` uniform is on): `blobComponent = 1` inside any pin's `InnerRadius`, `0` outside every pin's radius; no pins or toggle off → `1`. During anchor placement `AnchorGhost` is **forced on** regardless of the toggle and the live hover position is appended to `Blobs` as a transient "flashlight" blob (`MeshView.pinBlobUniforms`), so the terrain drops to ghost and only the existing pins + the hover preview read solid.
4. **Final α**: `α = MeshActive ? lerp(ghost, 1, blobComponent) : ghost`.

Consequences the rest of the stack relies on:

- **α-gated depth**: fragments with `α ≥ 0.99` write their natural window-space depth (`v.fc.Z`); everything else writes `1.0` (far) so ghost/outside fragments never occlude and never produce pixel-picks. A `fullySolid` clamp pins ghost/outside fragments below the threshold. `blobComponent` is the only mask.
- **Ghost colour is uniform**: ghost fragments always use the solid per-mesh palette colour regardless of `RenderingMode`, so the silhouette reads as one shape.
- The signed-distance / heatmap painters only touch **above-ghost** fragments.

Uniforms per draw call: `MeshActive`, `GhostOpacity` (pre-gated by `GhostSilhouette`), `RenderingMode`, `MeshColor`, `ShadingStrength`, `SlopeThreshold`; `BlobCount` + `Blobs : Arr<N<32>, V4f>` = `(cx,cy,cz,innerRadiusRender)` + `AnchorGhost` (centres/radii are metric world-space on the model, `* datasetScale` on upload — `MeshView.pinBlobUniforms`, hard cap 32); `DistanceEncoding` + `DistScale`; `HeatmapMode` + `SensorOrigin`/`RangeMax`; `ContourSpacing` (world-Z isoline band step — set on the *outline* G-buffer draw, not the forward draw; see World-Z isolines); `ClipPlane*` (fed a constant no-clip — generic support, no live consumer).

### Error colour maps (in the mesh shader, above-ghost only)

- `DistanceEncoding = 2` — **variance** map: per-reference-vertex disagreement std (std of every visible moving mesh's signed distance), sequential grey→red, painted on the reference. This is the Inspect central-3D aggregate; gated on `WorkflowStep = Inspect` (no toggle) and `DistScale`-normalised. Pair *difference* lives in the focus tiles, not the main view.
- `HeatmapMode = 1/2/3` — intrinsic incidence / range-from-own-origin / triangle-shape false colour (pre-existing acquisition channels; untouched by the Phase-2 Inspect work).

The variance distances come from `POST /api/query/region-distance` (reference vs each moving mesh) reduced to a per-reference-vertex std by the generation-guarded debounced `Update.ensureVariance` postlude into `Model.SurfaceDistance` (keyed by the reference mesh); the `SurfaceDist` buffer aligns with `loaded.pos`, non-encoded meshes bind a zero buffer of the same length.

### Inspect visualizations

The focus-tile channels are rendered **in WebGL** (per-vertex-coloured mesh) — a
unified `FocusShaders.focusColor` fragment driven by a
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

The one offscreen pass, **always on** (no toggle): every **loaded** mesh — visible or ghosted (`MeshView.buildOutlineNode` gates on load state, not `MeshVisible`, so a disabled / isolated-away mesh still gets crisp silhouette outlines + world-Z isolines on top of its translucent ghost fill; the G-buffer's own depth test interleaves co-located epochs) — renders into an MRT G-buffer (`OutlineGBuffer` → **world-Z band parity** + window depth in target0, palette colour + coverage mask in target1); a fullscreen edge-detect (`OutlineEdge`) paints per-mesh lines in palette colour where **the window depth breaks** (silhouette + cliff/occlusion outlines) **or the world-Z band parity flips** (elevation isolines), both gated to covered pixels. `dEdge` is the **second difference** (depth Laplacian) `|l + r − 2c|`, *not* the first difference: window-space `gl_FragCoord.z` is linear in screen space across any planar primitive, so the Laplacian is ~0 on a smooth slope at any view angle/distance and spikes only at a genuine break (a first difference would instead measure screen-space depth *slope* and light up every grazing surface as false banded lines). `dEdge` is tested against the **`OutlineThreshold`** uniform (gear slider "Outline edge threshold", `Model.OutlineThreshold`, default `0.004`, range `0.0001–0.01`); the useful range is low because target0 is **`Rgba8`** (`OutlineView.fs`), so the window depth in `target0.w` has only 256 levels — 1 LSB ≈ 0.004, and a threshold below the quantization floor lets the staircase risers of a smooth slope read as false bands. The depth break alone traces the silhouette in this data (no normal-angle or coverage-mask term). `target0.x` carries the isoline band parity; `.yz` are unused.

### World-Z isolines (edge-detected in the outline pass)

Elevation contours locked to the **global Z axis** (render-Z ∥ global-Z, since the dataset transform is a similarity with no rotation). They share the **silhouette outline's crisp image-space edge-detect** rather than being shaded onto the surface — so they get the same hard 1px look, but their *position* is world-locked: `OutlineGBuffer` writes `parity = floor(wp.Z / ContourSpacing) mod 2` (a 0/1 value, robust at 8-bit, full-range jump at every band boundary) into `target0.x`; `OutlineEdge` flags `iEdge` as a first difference wherever that parity differs between adjacent texels (parity is a step function, so any flip is already a real band boundary — unlike the depth term, which needs the second-difference `dEdge` to reject smooth slopes). Because the band index is a pure function of world Z, each line stays welded to a fixed world-Z plane on the surface and does **not** crawl as the camera orbits — only its 1px rasterization updates per frame. `ContourSpacing` (render-space Z step) is sized for `Model.IsolineBands` bands over the scene elevation range (`MeshView.buildOutlineNode`, from world-metric `SceneBounds.Size.Z × datasetScale`), shared across meshes so parity lines up. The band count is the gear slider "Isolines over Z range" (`Model.IsolineBands`, default `700`, range `4–2000`). Isolines render in the mesh **palette colour** and are **always on** (the silhouette outlines and isolines no longer have a toggle — both are part of the permanent outline pass). For a *solved* (rotated) mesh the parity follows the displayed elevation (`wp.Z` is post-pose), the intended true-height reading. **Caveat:** pure 1px, no AA (matches the silhouette); a steep face packing many bands into few pixels will merge/alias — controlled by the `IsolineBands` count (high values like the 700 default will alias on steep faces by design).

### Picking

Pixel picking via `Sg.OnTap` / `Sg.OnPointerMove`:

```fsharp
let pick = if e.Location.Depth < 0.9999 then Some e.WorldPosition else None
```

Background misses leave depth at the clear value (1.0); the gate is required. The α-gated depth write is what makes this work — ghost fragments leave depth at 1.0 so picks pass through them to the opaque surface behind. `Sg.OnTap`/`OnDoubleTap`/`OnLongPress` fire on background misses too — any handler that builds state from `e.WorldPosition` MUST gate on the depth check.

**Pin placement falls through ghosts via a raycast.** Because the ghost leaves depth at 1.0 the GPU pixel pick can't land on a ghosted surface. During `AnchorPlacement` the placement handlers keep the instant GPU pick as the fast path, then — when it misses (cursor over a ghost) — fall back to a server raycast: `View.raycastNearest` bbox-culls the visible+loaded meshes, fans out parallel `/query/ray` calls (un-applying each mesh's displayed pose exactly like `worldRayHit`), and takes the **nearest** hit (first surface the ray crosses wins, mesh + coordinate); `View.resolvePlacement` chains pixel-pick → raycast. The click runs it once; the hover preview runs it **throttled (60 ms) + generation-guarded** (a round-trip per move would flood) and holds the last preview between raycasts so it doesn't flicker, clearing on a true miss.

The **focus-panel correspondence pick** is different (no GPU there): the cursor is inverted to a render-space ray (orthographic for Top, cylindrical for Pano), then carried **render → metric world → the focused mesh's server frame** via that mesh's `displayedWorld` (so the pick is correct in either before/after pose), raycast server-side (`/query/ray`), and the hit mapped back through `displayedWorld.Forward` to metric world — all in `FocusScene.worldRayHit` (see *Coordinate systems & transform hierarchy*). It is gated behind a **set-correspondence toggle** (`Model.CorrSetMode`, the focus-head **⊕ set point** button): off ⇒ the focus single pans normally; on ⇒ the cursor aims the point (no pan), a throttled hover raycast feeds `Model.CorrPreview` (a live cyan 3D ghost in `ScanPinScene`), and a click commits (`PickCorrespondenceAt`, **no ROI gate — pick is pick**, stored mesh-local) and exits the mode. Toggling off cancels without committing (the anchor was never touched, so the tile redraws the committed marker). `FocusScene.worldRayHit` is the shared ray-cast for both the commit and the hover preview.

## Coordinate systems & transform hierarchy

Three spaces, two transforms. Keep them strictly separate — every boundary crossing goes through a named helper, never bare `* scale` / `± centroid` arithmetic.

**Spaces**

- **Mesh / server frame** (a.k.a. *own frame*, *absolute world*): the mesh's stored OBJ coordinates `+ meshCentroid`. **Every `/api/query/*` coordinate — in and out — is in this frame**; the server computes `localPos = worldPos − meshCentroid` itself.
- **Metric world**: the app's single canonical world (metres). Pin `Centre`/`InnerRadius`, correspondence anchors-as-world, `CorrPreview`, cursor world all live here. **Metric world ≡ a mesh's server frame exactly at the load pose** — the only thing separating them is that mesh's workspace pose.
- **Render space**: what the GPU and cameras use — centroid-recentred on the shared origin, dataset-scaled, then posed.

**Two transforms — apply dataset first, then workspace:**

1. **Dataset transform** — a *similarity* (uniform scale + translation, never rotation), fixed per dataset: `Translation(meshCentroid − commonCentroid) · Scale(datasetScale)`. It is the **only** place `DatasetScale` and `CommonCentroid` enter, and the per-mesh centroid stays hidden inside the server frame. Cross it with `ScanPin.renderCentre`/`worldCentre` (points) and `ScanPin.renderLength` (lengths) — metric world ↔ render.
2. **Workspace transform** — a *rigid* pose (rotation + translation, no scale), per mesh: the before/after registration pose. `LoadTransform` (identity at load → before / reference / unsolved) or `SolvedTransform` (after / solved). Render-space form = `ModelTransforms.displayedRender` / `MeshView.displayedMeshT`; metric-world form = `ModelTransforms.displayedWorld`. `RigidTransform.worldToRender` / `renderToWorld` conjugate a rigid pose between render and metric world (the dataset similarity is the conjugator). **`displayedWorld.Backward` maps metric world → that mesh's server frame; `.Forward` maps back.**

**Discipline rules**

- **Server queries** take/return the mesh's **server frame**: convert metric world in with `displayedWorld.Backward`, map results out with `displayedWorld.Forward`. Never hand a server query render-space or raw metric-world coordinates without that step (both `/query/ray` picks — `FocusScene.worldRayHit`, `View.resolveLayerPick` — un-apply the pose first). Multi-mesh queries instead pass each mesh's `displayedWorld.Forward` matrix (`probe`, `region-distance`) and let the server place them.
- **Scene-graph geometry** is render space: convert metric-world model values at the boundary (`renderCentre` / `renderLength`), or a metric-world pose with `worldToRender`.
- **Directions** need no scale handling (render ↔ metric world is parallel — uniform scale); only the workspace rotation matters, via `displayedWorld.Backward/Forward.TransformDir`.
- **Anchors** are stored in the mesh's **server frame** (`displayedWorld.Backward world` at placement time — pose-independent), so the before/after toggle moves their displayed world via `displayedWorld.Forward` with no re-baking.

### Panorama centre (`pano-centers.txt`)

Each mesh is a stereo reconstruction from a calibrated camera; its OBJ origin `(0,0,0)` is *supposed* to be that camera, but the data is often not centred on it (one JOB mesh's origin sits ~130 m outside its own surface). So the panorama eye is data-driven, not assumed:

- **File** — one per dataset, `data/{dataset}/pano-centers.txt`. Lines `<mesh-folder> x y z`, **absolute world coords** (same frame + units as each mesh's `*centroid.txt`); `#`/blank lines ignored; unlisted meshes omitted. Hand-measured from the GUI top-bar coordinate readout (gray *world* value = server frame at load pose).
- **Server** — `MeshLoader.getPanoCenters` parses it (invariant-culture `float`, like `parseCentroidFile`); `Handlers.panoCentersHandler` serves `/api/datasets/{dataset}/pano-centers`.
- **Client** — `MeshData.fetchPanoCenters` → `PanoCentersLoaded` → `Model.PanoCenters : Map<mesh, V3d>` (cleared on `CentroidsLoaded`, i.e. dataset switch).
- **Render** — `FocusScene.focusSingle` derives the pano eye = `renderT.Forward(panoCentre − centroid)` (world → mesh frame → render); used for the `PanoEye` uniform **and** the pano `worldRayHit` pick origin. Empty map ⇒ falls back to the OBJ origin.

To add another dataset's centres: isolate each mesh (Overview `◐`), read the top-bar **world** value at its visual centre, write a line. No code change.

## Before/after registration model

There is **no preview / commit / undo history**. Per mesh:

- `Model.LoadTransforms : Map<mesh, Trafo3d>` — the immutable baseline captured at load (= "before"; identity render-space, meshes load unregistered).
- `Model.SolvedTransforms : Map<mesh, Trafo3d>` — written by the correspondence solve (= "after"); presence ⇔ solved.
- `Model.RegView = RegBefore | RegAfter` — one global toggle (top bar), disabled until any `SolvedTransform` exists, flips 3D + focus + dock together.
- **Displayed** transform (`ModelTransforms.displayedRender/displayedWorld`, `MeshView.displayedMeshT`) = `RegView = RegAfter && solved → SolvedTransform`, else `LoadTransform`. Reference + unsolved meshes therefore stay at `LoadTransform` in both states.

`SolveCoarse` → `POST /api/query/lsq-pairs` per visible moving mesh with ≥3 correspondence pairs (parallel). Correspondence anchors live in the mesh's **server frame**, so the pairs are already the load-pose own-frame point; the server returns the **absolute** world transform mapping load → reference, which the reducer stores directly as `SolvedTransform` (`worldToRender`) and flips `RegView = RegAfter`. A re-solve replaces `SolvedTransform` only; `LoadTransform` is never overwritten. Server math: `RegMath.solveRigid`, weighted Umeyama/Arun with Jacobi SVD + det-flip so planar/collinear sets never reflect; response carries per-pair residuals + covariance eigenvalues + `collinearityWarning`. Changing the reference drops `SolvedTransforms` + snaps to `RegBefore` (a solve is relative to its reference).

**Correspondences** (`ScanPin.Correspondence option`): `{ RefAnchor (reference own-frame marker — the pin centre if the host is the reference, else its closest-point projection); RefDistance; Anchors : Map<mesh, {Point; Source}>; Residuals; InRoi }`. **Every pin is a registration pin** — there is no enable/disable distinction. `makeAnchor` gives each new pin `Some Correspondence.empty`, and placing a pin auto-seeds it against the reference (if one exists); a freshly placed pin is selected. `Anchors.Point` is in the mesh's server frame; display/solve derive metric world via `displayedWorld.Forward`, so the before/after toggle moves markers automatically (no `bakeAnchors`). Auto-seed (placement / reference change / ⟳) projects via parallel `/query/closest`, **membership** ROI-clamped to `roiReach` (the probe cylinder's bounding sphere — this is the only surviving ROI test); manual `AnchorPick3D` picks are **never ROI-gated** (pick is pick) and are never overwritten by auto-seed. `AnchorSource = AnchorAuto | AnchorPick3D`. The 3D **constellation** (`ScanPinScene`) draws a small wire-sphere + cross glyph per anchor + a larger one at the reference point + lines to the reference (all line geometry, fixed render size — independent of pin radius).

## Three-mode GUI + shared selection

The left rail (`GuiRail`) has exactly three modes — **Overview · Correspondence · Inspect** (`Model.WorkflowStep`). **Containers never move on a mode change**; only their content + which affordances are interactive change (cross-faded). Region roles are fixed: rail = *what object I work on*; focus = *per-mesh spatial work*; dock = *the parts of the selected object*.

| Mode | Rail | Focus (canvas) | Dock |
|---|---|---|---|
| Overview | mesh list (hover=peek-isolate, ★ reference) | WebGL textured tiles (difference/displacement recolour in Inspect) | mesh roster |
| Correspondence | pin list + readiness diagnostics | pick surface (Pano/Top) | correspondence manager + Solve |
| Inspect | difference sub-mode + intrinsic channels | difference / displacement tiles (channel toggle) | channel toggle + pin distribution + shift readout |

**One shared selection record drives all linking** (`Model.Selection = { SelectedPin; FocusedMesh; SelectedPoint; Hovered }`). Linked highlighting is a *consequence* of every region binding to `Selection` — there are no panel-to-panel hover emitters. Grammar everywhere: **hover = peek** (writes `Selection.Hovered` via the single `SetHovered`), **click = select/promote**, **drag = edit**. Pin selection lives in `Selection.SelectedPin` (NOT `ScanPinModel`); `ScanPinUpdate.handleMsg` maintains it and drops a dangling selection when its pin is deleted.

**Focus panel** (`GuiFocus` + `FocusScene`) is **WebGL** — a large single (the focused mesh, rendered full-res + atlas-textured in render space at its displayed pose) over a strip of textured thumbnail tiles, one renderControl per visible mesh (`FocusScene.single`/`multiples`); many renderControls coexist fine here. **Top** (the default projection) **= strictly orthographic** (hand-built ortho matrix); **Pano = cylindrical unwrap in a vertex shader** (`FocusShaders.pano`, composed after `DefaultSurfaces.trafo` so the WorldPosition varying — and thus picking — survives; the camera is identity, the shader writes clip). The unwrap **eye** (`PanoEye` uniform + the pano pick-ray origin in `worldRayHit`) is the mesh's panorama centre: `Model.PanoCenters[mesh]` (absolute world) carried into the mesh's own frame (`− centroid`) then through `renderT` — so it scales and follows the before/after pose like the geometry; **no entry ⇒ the mesh origin** `(0,0,0)`. See *Panorama centre*. A tiny pan+zoom controller (per-mesh pan/zoom `cval`s kept in `FocusScene.camStates`, no orbit) drives the single with mouse-anchored zoom; `⟲ reset` calls `FocusScene.resetCam`. **Picking is Dom-driven, not `Sg.OnTap`** (that did not fire reliably in the 2nd render control): `worldRayHit` inverts the cursor to a render-space ray (hand-rolled ortho drop for Top / pano direction from the eye for Pano), carries it **render → metric world → the mesh's server frame** through that mesh's `displayedWorld` (correct in either before/after pose; no per-mesh-centroid juggling), hits `/query/ray`, and maps the hit back through `displayedWorld.Forward` to metric world (see *Coordinate systems & transform hierarchy*). The pick reads the cursor in **CSS px**: the NDC math divides `ViewportSize` (framebuffer px) by the **shared `FocusScene.dpr`** the main view publishes (computed from its `ViewportSize/ClientSize` in `OnRendered`); the focus controls don't bind `RenderControl.ClientSize` themselves (framebuffer px there would offset the pick on hi-dpi). In set-correspondence mode (⊕ set point) a **move** throttle-raycasts → `CorrPreviewComputed` (the live 3D ghost in the main viewport), and a **click** raycasts → `PickCorrespondenceAt` (places + exits the mode). Gated on a selected pin + a non-reference focused mesh. In Inspect the tiles recolour per channel via `focusColor`/`FocusMode` (see Inspect visualizations).

## ScanPin system

A ScanPin is a 3D annotation in **metric world-space**: `Centre : V3d`, `InnerRadius : float` (a hard sphere — α = 1 and full probe weight inside). Pins drive the per-pixel blob in the mesh shader (`Blobs` uniform). Render-space conversions happen at boundaries (`ScanPin.renderCentre`/`renderLength` — the dataset transform; see *Coordinate systems & transform hierarchy*).

**Placement:** Correspondence mode → **○ Place pin** → tap a surface. Click-and-drop: the pin is created immediately (no commit step), becomes selected, and placement ends. While placing, pin isolation is **forced on** and the live hover is added as a transient **flashlight** blob (see Ghosting rules), so the terrain drops to ghost and only the existing pins + the hover preview read solid; the GPU pick can also fall through a ghost via the placement raycast (see Picking). Radius is edited afterwards from the pin's detail panel (the Correspondence dock manager, `SetInnerRadius` on the selected pin). New pins take `Model.QuickPinRadius` (default 0.5 m) as their inner radius. `Placement : PlacementState = PlacementIdle | AnchorPlacement`.

**M3C2 probe:** every pin owns `Probe : ProbeState`. It samples all visible meshes inside a cylinder (radius = `InnerRadius`, length = 20 m fixed `ScanPin.fixedProbeLength`, axis = PCA normal of the reference inside the pin sphere) via `POST /api/query/probe` and returns per-mesh signed-distance distributions (re-centred so 0 = reference median) + the dataset/algorithm/conditioning decomposition. Lazy + debounced (`ScanPinUpdate.ensureProbe`, generation-guarded CTS); invalidation just resets to `ProbeNone`. The probe drives the Inspect dock's **pin distribution** panel (strip + box) and the difference field's ±LoD₉₅ detection-limit band.

**Contact rings:** every pin caches `ContactRings`; `ScanPinUpdate.ensureRings` debounced fan-out of `POST /api/query/contact-rings` over **all** meshes (visibility only gates rendering, never recompute), per-pin CTS. Transforms are rigid → centre inverse-transformed into each mesh's own frame, rings mapped back via the displayed transform.

**3D rendering** (`ScanPinScene.fs`): `pinDots` (small **invisible** icosphere pick proxies — alpha 0, still in the depth/id pick pass — carrying the select tap), `pinMarkerLines` (the visible pin-centre marker: a small wire-box jack, yellow when selected), `pinRings` (equator ring ⊥ probe axis + cached per-visible-mesh contact rings, in the pin's palette colour, occluded by geometry as the spatial cue), the correspondence `constellation` (wire-sphere + cross glyphs + `glyphProxy` invisible pick spheres for hover/focus brushing), `ghostPreview` (placement hover), `corrPreview` (cyan wire-sphere + cross for the live set-correspondence aim), and `pinGlyphs` (far-view verdict pole — green if every moving mesh's `|median| ≤ LoD₉₅`, red if any is significant). All markers/glyphs are **fixed render size, independent of pin radius**, and drawn on top (an invisible proxy writes depth, so a depth-tested marker would self-occlude behind it). Marker world positions follow the displayed (before/after) transform because anchors are mesh-local.

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
- **Don't build in-place-updating *lists* with `AVal.map (… IndexList.ofList) |> AList.ofAVal`.** That mints fresh `Index` keys on every recompute, so any element change diffs as remove-all + add-all — it churns the whole list (tearing down/recreating every row's DOM/GPU resources) and intermittently double-renders a row in the reconciler. Instead derive a stable-identity incremental list from the source map: `AMap.map (project to just the row's inputs) |> AMap.toASet |> ASet.sortBy key |> AList.map row`. Projecting to only the fields a row consumes means unrelated field changes (e.g. a pin's probe/ring result) don't re-key its row. The rail pin list (`GuiRail.pinList`) was fixed this way. (Small, rarely-changing lists — the gear dataset list, the readiness diags — still use the simple form; the churn is harmless there.)
- **Never create a *transient* `aval` inside another aval's compute and read it.** `AVal.custom (fun t -> … (makeSomeAval args).GetValue t …)` — or the same with `AVal.force` — builds a fresh inner aval on every evaluation; that inner aval can drop its dependency edge so the outer **evaluates once and then stops re-firing** (it silently freezes on its first value). This bit the focus single (`focusMeshOf model` built inside `single`'s aval → the panel rendered blank because the aval never saw the meshes load) and the constellation (`markerWorldOf` built inside `constLines`). The fix is always the same: **inline** the inner computation so its `model.X.GetValue t` reads happen against the outer token directly, or bind the inner aval **once** outside the compute (a stable `let`, as `glyphProxy` does with `let world = markerWorldOf …`). Reading a *stable* aval via `.GetValue t` is correct — only freshly-built-per-eval avals are the trap.

## Server query performance

Costly spatial queries (`probe`, `contact-rings`, `region-distance`) scale with mesh count × sample density:

- **Never issue per-mesh requests sequentially.** Fan out in parallel (`Async.Parallel`) rather than looping; if a multi-mesh operation becomes hot, add a batched server endpoint with `Parallel.For` instead.
- **Parallelise the heavy inner loop server-side** when inputs are independent — Embree `Scene.Intersect` is thread-safe.
- **Cap density rather than grow linearly** (`maxPoints` / sample strides / `maxTris`).
- **Debounce user-driven triggers** with a `CancellationTokenSource` + a generation counter so the next event cancels the previous and at most one fetch is in flight per invalidation (`ScanPinUpdate`/`UpdateHelpers` keep these CTS/generation refs at module level, NOT in the Elm model).
- **Mesh caches are warmed at dataset load** by `bboxesHandler` — it calls `MeshCache.get` for every mesh, so the first interactive query never pays the lazy-load cost.

## Client compile order (`Superprojekt.fsproj`)

```
MeshData.fs            mesh fetch/parse, ApiConfig, shared Http.client
ProbeModel.fs          M3C2 probe DTOs (ProbeResult/ProbeState)
Query.fs               server query wrappers (Async)
CameraModel.fs / .g.fs OrbitState [<ModelType>]
OrbitController.fs     OrbitMessage DU + orbit camera
RegistrationModel.fs   ScanPinId, correspondence anchors, readiness engine (Readiness.compute), FlyToMath, NavAction, HeatmapMode (WASM-free, shared with Supertests)
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
MeshAnalysis.fs   sphere contact-ring tracing
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
GET  /api/datasets/{dataset}/pano-centers       → { meshName: [x,y,z] }   (from {dataset}/pano-centers.txt; absent → {})
GET  /api/datasets/{dataset}/bboxes             → { meshName: { min:[x,y,z], max:[x,y,z] } }   (also warms the cache)
GET  /api/datasets/{dataset}/mesh/{name}/{i}    → binary mesh
GET  /api/datasets/{dataset}/mesh/{name}/{i}/atlas → JPEG
POST /api/query/ray                             → { hit, t, point, triangleId }     Name = "dataset/mesh"
POST /api/query/closest                         → { found, point, distanceSquared, triangleId }
POST /api/query/contact-rings                   → sphere–surface intersection polylines (closed rings repeat the first point)
POST /api/query/lsq-pairs                       → weighted rigid landmark solve (absolute world transform moving→reference + per-pair residuals + conditioning; 400 on <3 pairs)
POST /api/query/probe                           → N-mesh M3C2 probe (per-mesh distributions + KDE + three sources)
POST /api/query/region-distance                 → per-vertex signed M3C2 distance (mode 0) or vertical Δz (mode 1) of a target mesh to the reference, in the target's served vertex order; 1e30 sentinel where no closest point
```

All query coordinates are **absolute world space**; the server converts `localPos = worldPos − meshCentroid`. (Removed for lack of consumers — don't re-add without one: `/query/icp`, `/query/patch`, sphere/box/ray-batch, grid-eval, isoline, curvature-ridge, region-grid.)

## Client Model snapshot

Top-level `Model` fields (see `Model.fs`):

- `Camera`, `MeshOrder`, `MeshNames`, `MeshVisible`, `MeshesLoaded`, `CommonCentroid`, `MenuOpen`, `SavedMenuOpen`, `DebugLog`
- `Datasets`, `ActiveDataset`, `DatasetScales` (`{"SETSM_glacier" → 0.01}`), `DatasetCentroids`, `PanoCenters` (`Map<mesh, V3d>` = per-mesh panorama/camera centre, absolute world coords; from `pano-centers.txt`, see Panorama centre)
- `GhostSilhouette` (default on), `GhostOpacity`, `ShadingStrength`, `SlopeThresholdDeg`, `AnchorGhostMode` ("Isolate pins", default on), `QuickPinRadius`, `OutlineThreshold` (outline edge-detect threshold, default `0.004`), `IsolineBands` (isolines over the scene Z range, default `700`)
- `SceneBounds`, `MeshBounds`, `ActivePickingLayer`, `ReferencePeekHeld`
- `LoadTransforms`, `SolvedTransforms`, `RegView` (`RegBefore`/`RegAfter`), `Registration` (`{ ReferenceMesh; Running }`)
- `MeshSensorTypes`, `HeatmapMode` (`HeatOff | HeatIncidence | HeatRange | HeatShape`), `ExtrinsicZDiff` (difference sub-mode M3C2 ↔ Δz), `SurfaceDistance` (`Map<mesh, float32[]>`, the reference variance array), `FocusDist` (`Map<mesh, float32[]>`, per moving mesh signed distance for the focus difference channel)
- `ScanPins` (`ScanPinModel`), `Selection` (`{ SelectedPin; FocusedMesh; SelectedPoint; Hovered }`)
- `RenderingMode`, `MeshSolo`, `GearPopoverOpen`
- `WorkflowStep` (`Overview | Correspondence | Inspect`), `InspectChannel` (`ChDifference | ChDisplacement`), `FocusProjection` (`ProjPano | ProjTop`), `FocusPeekReference`, `CorrSetMode` (set-correspondence toggle) + `CorrPreview` (live 3D ghost point), `Toast`

GUI placement:
- Top bar (`GuiTopBar`): hamburger, camera reset, **👁 Peek** (hold), the global **Before/After** toggle, gear popover (dataset switch, rendering mode, outline edge threshold + isoline count sliders, camera speed, ghost silhouette + opacity, isolate-pins, shading strength, slope threshold, quick-pin radius, dataset info, mesh centroids, debug log).
- Left rail (`GuiRail`): the three modes.
- Right focus panel (`GuiFocus` + `FocusScene`): WebGL large-single + per-mesh tiles.
- Bottom dock (`GuiInspector`): mode-contextual content.
- Overlays (`GuiOverlays`).

## Tests

`src/Supertests` is a console runner (paket-managed, no extra packages) that compiles `RegistrationModel.fs` + `RegMath.fs` directly and covers: the Umeyama solver (recovery, reflections, weights, collinearity, <3-pairs rejection), `RegConditioning`, the readiness engine, and the fly-to math — `dotnet run --project src/Supertests`. Against a running server (`ASPNETCORE_URLS=http://localhost:8002 dotnet run --project src/Superserver`): `node tools/integration.mjs` (closest-point seed → rigid perturbation → `/query/lsq-pairs` recovers its inverse → `/query/probe` median error shrinks).

## Aardvark.Dom gotchas

- `Attribute("for", "...")` on `<label>` is silently dropped — nest `<input>` inside `<label>`.
- `Attribute("checked", "")` is dropped — use `Attribute("checked", "checked")`.
- CSS `~` sibling combinator breaks (Aardvark inserts wrapper nodes) — use `:has()` on a known ancestor.
- `RenderControlInfo` and `TraversalState` both have `.Runtime` — annotate `(info : Aardvark.Dom.RenderControlInfo)` when ambiguous.
- `yield!` is not supported in Aardvark.Dom CE builders — use OnBoot JS with MutationObserver for dynamic SVG/canvas (the `observedRender` helper, the focus-panel canvas, the orientation indicator).
- `renderControl { ... }` can be nested inside `div { ... }` — it creates a WebGL canvas child. The app has **several**: the main viewport plus the focus panel's single + one per visible mesh (`FocusScene`). Multiple controls coexist fine on this backend. **`Sg.OnTap` (and the other Sg pointer events) did NOT fire reliably in the secondary focus controls** — the focus does its picking with Dom pointer handlers + a server `/query/ray` raycast instead (`FocusScene.worldRayHit`). Camera input there is also Dom-level (`Dom.OnPointerDown/Move/Up` without pointer capture — capture hijacked later clicks).
- `AVal.map4` does not exist — combine with `AVal.map2`/`AVal.map3`.
- `Dom.Style` for renderControl; `Style` for HTML elements. `Css.Custom` does not exist — use CSS classes in `style.css`.
- **`RenderControl.ViewportSize` is framebuffer pixels** (CSS × devicePixelRatio); `RenderControl.ClientSize` is CSS pixels. Anything mixing with DOM coordinates — overlay placement (scale bar, tooltips), cursor → NDC math (pickRay, focus-panel pick) — must work in CSS px or it breaks on hi-dpi. ClientSize is `V2i.II` until the first DOM event; `View.fs` derives `overlaySize` with a ViewportSize fallback. The main control binds `RenderControl.ClientSize`; the focus controls don't — they read `ViewportSize` and divide by the shared `FocusScene.dpr` the main view publishes (from its own `ViewportSize/ClientSize`).
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
