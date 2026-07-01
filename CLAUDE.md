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

1. **Effective ghost level (the floor)**: `ghost = GhostOpacity` if `GhostSilhouette` is on (the global **ghost floor**, toggled from the **gear** — the "Ghost silhouette" switch + opacity slider; the top-bar button was removed), else `0` — with the floor off, ghost fragments are *discarded* (`α < 1e-4` → discard), so "no ghost" means invisible, not translucent. **Solo / isolation all send non-emphasized meshes to whatever the floor is set to**: the fixed isolation dim alpha (0.15) is itself gated by `GhostSilhouette`, so with the floor off it hides rather than dims (`MeshView` GhostOpacity uniform).
2. **MeshActive** (visibility / isolation): `false` → α = `ghost` for the whole mesh, uniformly.
3. **Pin isolation** (only when pins exist *and* the effective isolation is on): on = the per-mode default `AnchorGhostMode` (set on `SetWorkflowStep` — **on in Correspondence, off in Overview/Inspect**) **OR** the spring-loaded hold modifier `IsolatePeekHeld` (top-bar **◎ Isolate** / hotkey **I**, momentary). `blobComponent = 1` inside any pin's `InnerRadius`, `0` outside every pin's radius; no pins or off → `1`. During anchor placement `AnchorGhost` is **forced on** regardless and the live hover is appended to `Blobs` as a transient "flashlight" blob (`MeshView.pinBlobUniforms`), so the terrain drops to ghost and only the existing pins + the hover preview read solid.
4. **Final α**: `α = MeshActive ? lerp(ghost, 1, blobComponent) : ghost`.

Consequences the rest of the stack relies on:

- **α-gated depth**: fragments with `α ≥ 0.99` write their natural window-space depth (`v.fc.Z`); everything else writes `1.0` (far) so ghost/outside fragments never occlude and never produce pixel-picks. A `fullySolid` clamp pins ghost/outside fragments below the threshold. `blobComponent` is the only mask.
- **Ghost colour is uniform**: ghost fragments always use the solid per-mesh palette colour regardless of `RenderingMode`, so the silhouette reads as one shape.
- The signed-distance / heatmap painters only touch **above-ghost** fragments.

Uniforms per draw call: `MeshActive`, `GhostOpacity` (pre-gated by `GhostSilhouette`), `RenderingMode`, `MeshColor`, `ShadingStrength`, `SlopeThreshold`; `BlobCount` + `Blobs : Arr<N<32>, V4f>` = `(cx,cy,cz,innerRadiusRender)` + `AnchorGhost` (centres/radii are metric world-space on the model, `* datasetScale` on upload — `MeshView.pinBlobUniforms`, hard cap 32); `DistanceEncoding` + `DistScale`; `HeatmapMode` + `SensorOrigin` (the scan sensor = mesh's panorama centre in render space, `MeshView.sensorOrigin`; no `pano-centers` entry ⇒ the mesh origin) `/RangeMax` (farthest mesh-bbox corner from that sensor, render units); `ContourSpacing` (world-Z isoline band step — set on the *outline* G-buffer draw, not the forward draw; see World-Z isolines); `ClipPlane*` (fed a constant no-clip — generic support, no live consumer); `Greyscale` (desaturates the mesh to luminance while the top-bar **🎨 Overlays** hold is down — `Model.ShowOverlaysHeld`, toggled by `SetShowOverlays`; pins are separate geometry so the pin-coloured flag labels stay coloured).

### Error colour maps (in the mesh shader, above-ghost only)

The `DistanceEncoding` selects which per-vertex `SurfaceDist` painter runs, chosen per mesh by `MeshView.inspectField` (Inspect only — `0` elsewhere):

- `DistanceEncoding = 2` — **variance** map: per-reference-vertex disagreement std (std of every visible moving mesh's signed distance), sequential grey→red, painted on the **reference** in the **no-solo** ensemble. The Inspect central-3D aggregate; `DistScale`-normalised.
- `DistanceEncoding = 1` — **difference** map on a **soloed moving mesh** (Inspect *Difference* channel): its own `region-distance` signed distance (`Model.FocusDist`), the **linear-diverging** difference colormap (neutral↔red/blue about 0), `DiffRangeScale`-scalable — the same colours as the focus difference tile, now mirrored in the main 3D view when that mesh is soloed (the enc-1 path has no per-vertex LoD, so its neutral gate is effectively 0).
- `DistanceEncoding = 3` — **displacement** magnitude on a **soloed solved moving mesh** (Inspect *Displacement* channel): client-computed per-vertex `|load→solved|`, sequential light→blue (matches the focus displacement tile).
- `HeatmapMode = 1/2/3` — intrinsic incidence / range / triangle-shape false colour (Inspect only — the uniform is forced `0` in Overview/Correspondence so those modes stay plain textured, §C). Incidence (angle between the surface normal and the direction to the sensor) and range (distance to the sensor) are both measured from `SensorOrigin` — the **scan sensor** (the mesh's panorama centre, not the interactive camera or the OBJ origin). Shape (per-vertex triangle quality) reads fully green at quality ≥ 0.75 (`v.shq / 0.75f`) so a larger share of a well-formed mesh shows as good.

The variance distances come from `POST /api/query/region-distance` (reference vs each moving mesh) reduced to a per-reference-vertex std by the generation-guarded debounced `Update.ensureVariance` postlude into `Model.SurfaceDistance` (keyed by the reference mesh); the `SurfaceDist` buffer aligns with `loaded.pos`, non-encoded meshes bind a zero buffer of the same length.

### Inspect visualizations

The focus-tile channels are rendered **in WebGL** (per-vertex-coloured mesh) — a
unified `FocusShaders.focusColor` fragment driven by a
`FocusMode` uniform over a per-vertex `FocusScalar` buffer (`FocusScene.focusOverlay`), so the
channel toggle is a uniform switch with no shader rebuild.

- **distribution** (Inspect dock): **all pins aggregated** per moving-mesh lane — jittered raw probe sample "rain" **coloured by pin** on a shared signed-distance axis with the ±LoD₉₅ band + axis scale and a pin legend (`GuiInspector`, a bespoke `OnBoot` chart canvas, no KDE). Probes are the current `RegView` pose; the panel labels Before/After. **Per-sample brushing** is bidirectional (chart↔3D, see ScanPin system).
- **2 meshes** → focus **difference** (`FocusMode = 1`): signed M3C2 / vertical Δz vs reference, **linear-diverging** (Kovesi CET-D style) about 0: neutral `(0.62,0.63,0.66)` → red(+)/blue(−), with a near-zero `t^0.6` boost so small deviations stay visible (no central perceptual flat-spot); robust per-tile scale. The neutral point reads against the light page (not white). Client helper `Primitives.Diff.color`; the matching FShade ramp is in `FocusShaders.focusColor` (mode 1) and `MeshShader.shade` (enc 1). The per-tile range is gear-scalable: `Model.DiffRangeScale` (gear slider "Difference heatmap range", default `1.0`) multiplies the `FocusHi` uniform for mode 1 only — folded into `hi` in `FocusScene.focusOverlay` (not the scalar buffer), so dragging the slider updates a uniform without re-uploading the vertex buffer. Data = `region-distance` (target = moving mesh) fetched by `Update.ensureFocusDist` into `Model.FocusDist` (the mesh's served vertex order, aligns with `loaded.pos`); same generation-guarded debounce as `ensureVariance`.
- **mesh** → focus **displacement**: solved moving meshes only. The **large single** shows a white surface (`FocusMode = 3`) + **load→solved arrow line-glyphs** (`Lines.render`, exaggerated to ~18% of the fit extent for visibility, coloured light→dark blue by *true* magnitude, with XY-plane arrowheads) — forced to the ortho camera (lines can't go through the pano unwrap, so Pano collapses to Top here). The **tiles** show the sequential magnitude heatmap (`FocusMode = 2`, per-vertex `|load→solved|` computed in `focusOverlay`).
- **all meshes** → central-3D **variance**: per-reference-vertex disagreement std painted on the reference (`DistanceEncoding = 2`, above), `region-distance` + `ensureVariance`. The `region-distance` M3C2 cutoff drops reference vertices a moving mesh doesn't cover, so non-overlapping points contribute no spurious disagreement (the `cnt ≥ 2` reduction already skips sentinels).
- **solo a moving mesh** → central-3D **that mesh's field** (§C): the soloed mesh stays solid and paints its own difference (`DistanceEncoding = 1`, `FocusDist`) or displacement-magnitude (`DistanceEncoding = 3`, client-computed) field in the main 3D view, mirroring its focus tile; the reference + every other mesh render as **empty outlines** (fill discarded — only the always-on outline silhouette remains, `MeshView` GhostOpacity `inspectSoloOther → 0`), not a faint ghost (`MeshView.inspectField` picks the encoding).
- **shift readout** (dock, displacement only): the focused mesh's centroid displacement load→solved, split vertical(datum)/horizontal + rotation angle, derived client-side from its `SolvedTransform`.

The reference tile (and any mesh without data) stays atlas-textured (`FocusMode = 0`).

### `Sg.DepthMask` is forbidden

Do not add `Sg.DepthMask` anywhere — it is buggy in this Aardvark/Aardworx WebGL build and silently breaks the depth pipeline. Ordering is steered with `Sg.DepthTest` + `Sg.Pass` alone. This means lines, pin geometry, and text all write depth too — that violates the textbook "translucent shouldn't write depth" rule but is the only combination that renders correctly in this stack. Leave the in-code reminders in `LineShader.fs` / `SceneGraph.fs`.

### Image-space outlines (`OutlineView.fs`)

The one offscreen pass, **always on** (no toggle): every **loaded** mesh — visible or ghosted (`MeshView.buildOutlineNode` gates on load state, not `MeshVisible`, so a disabled / isolated-away mesh still gets crisp silhouette outlines + world-Z isolines on top of its translucent ghost fill; the G-buffer's own depth test interleaves co-located epochs) — renders into an MRT G-buffer (`OutlineGBuffer` → **world-Z band parity** + window depth in target0, palette colour + coverage mask in target1); a fullscreen edge-detect (`OutlineEdge`) paints silhouette + cliff/occlusion outlines (where **the window depth breaks**) in the mesh **palette colour** and elevation isolines (where **the world-Z band parity flips**) in a **faint neutral grey**, both gated to covered pixels. `dEdge` is the **second difference** (depth Laplacian) `|l + r − 2c|`, *not* the first difference: window-space `gl_FragCoord.z` is linear in screen space across any planar primitive, so the Laplacian is ~0 on a smooth slope at any view angle/distance and spikes only at a genuine break (a first difference would instead measure screen-space depth *slope* and light up every grazing surface as false banded lines). `dEdge` is tested against the **`OutlineThreshold`** uniform (gear slider "Outline edge threshold", `Model.OutlineThreshold`, default `0.004`, range `0.0001–0.01`); the useful range is low because target0 is **`Rgba8`** (`OutlineView.fs`), so the window depth in `target0.w` has only 256 levels — 1 LSB ≈ 0.004, and a threshold below the quantization floor lets the staircase risers of a smooth slope read as false bands. The depth break alone traces the silhouette in this data (no normal-angle or coverage-mask term). `target0.x` carries the isoline band parity; `.yz` are unused.

### World-Z isolines (edge-detected in the outline pass)

Elevation contours locked to the **global Z axis** (render-Z ∥ global-Z, since the dataset transform is a similarity with no rotation). They share the **silhouette outline's crisp image-space edge-detect** rather than being shaded onto the surface — so they get the same hard 1px look, but their *position* is world-locked: `OutlineGBuffer` writes `parity = floor(wp.Z / ContourSpacing) mod 2` (a 0/1 value, robust at 8-bit, full-range jump at every band boundary) into `target0.x`; `OutlineEdge` flags `iEdge` as a first difference wherever that parity differs between adjacent texels (parity is a step function, so any flip is already a real band boundary — unlike the depth term, which needs the second-difference `dEdge` to reject smooth slopes). Because the band index is a pure function of world Z, each line stays welded to a fixed world-Z plane on the surface and does **not** crawl as the camera orbits — only its 1px rasterization updates per frame. `ContourSpacing` (render-space Z step) is sized for `Model.IsolineBands` bands over the scene elevation range (`MeshView.buildOutlineNode`, from world-metric `SceneBounds.Size.Z × datasetScale`), shared across meshes so parity lines up. The band count is the gear slider "Isolines over Z range" (`Model.IsolineBands`, default `700`, range `4–2000`). Isolines render in a **faint neutral grey** (the palette colour is reserved for the silhouette depth-break edges) and are **always on** (the silhouette outlines and isolines no longer have a toggle — both are part of the permanent outline pass). For a *solved* (rotated) mesh the parity follows the displayed elevation (`wp.Z` is post-pose), the intended true-height reading. **Caveat:** pure 1px, no AA (matches the silhouette); a steep face packing many bands into few pixels will merge/alias — controlled by the `IsolineBands` count (high values like the 700 default will alias on steep faces by design).

### Picking

Pixel picking via `Sg.OnTap` / `Sg.OnPointerMove`:

```fsharp
let pick = if e.Location.Depth < 0.9999 then Some e.WorldPosition else None
```

Background misses leave depth at the clear value (1.0); the gate is required. The α-gated depth write is what makes this work — ghost fragments leave depth at 1.0 so picks pass through them to the opaque surface behind. `Sg.OnTap`/`OnDoubleTap`/`OnLongPress` fire on background misses too — any handler that builds state from `e.WorldPosition` MUST gate on the depth check.

**Picks fall through ghosts via a raycast.** Because the ghost leaves depth at 1.0 the GPU pixel pick can't land on a ghosted surface, so the two viewport gestures that need a 3D point — **pin placement** (`AnchorPlacement`) and **double-tap-to-recenter** — keep the instant GPU pick as the fast path, then fall back to a server raycast when it misses (cursor over a ghost): `View.raycastNearest` bbox-culls the visible+loaded meshes, fans out parallel `/query/ray` calls (un-applying each mesh's displayed pose exactly like `worldRayHit`), and takes the **nearest** hit (first surface the ray crosses wins, mesh + coordinate); `View.resolvePick` chains pixel-pick → raycast. The placement click and the double-tap each run it once; the placement hover preview runs it **throttled (60 ms) + generation-guarded** (a round-trip per move would flood) and holds the last preview between raycasts so it doesn't flicker, clearing on a true miss.

**Clicking a mesh in 3D focuses it** (read/write parity, §B): a plain tap (no placement / not armed for correspondence) that lands on a surface runs `View.raycastNearestNamed` — the same parallel cull-and-raycast as `raycastNearest` but keeping the **mesh name** of the nearest hit — and emits `SetFocusedMesh` (in **Inspect** also `ToggleMeshSolo`, §C auto-solo). The focused mesh shows a cyan bbox outline in 3D (`SceneGraph.focusedOutline`), mirroring the rail row + focus tile; the **reference** mesh shows a prominent **gold** bbox outline (`SceneGraph.referenceOutline`) + the gold ★ focus tile.

### Camera controls

Orbit camera (`OrbitController` / `OrbitState` — a project file, **not** the Aardvark library one). Left drag = rotate, **middle drag = pan**, scroll = zoom; double-tap recenters (ghost-aware, see Picking). **Pan is locked to the world XY plane** (constant Z): `view.Right` already lies in XY (`cross(sky, dir)`), so only `view.Up` is flattened + renormalized — at a near-horizontal view screen-up ≈ world-Z and its XY projection vanishes, so it falls back to ground-forward (`view.Forward` flattened); `center.Z` stays fixed and the eye follows via `OrbitState.withView`. **Shift+left drag** is a pan alias for trackpads with no middle button: `getAttributes` remaps `Button.Left + Shift → Button.Middle` at `PointerDown`, so the whole drag path treats it as a pan (mode locked at press). Shift, not Ctrl — Ctrl+click is the macOS secondary click.

`FlyTo(target, aspect)` animates centre + radius so a bounds/sphere subtends ~25% of the viewport (the rail ⌖ frame button, readiness nav). `FlyToPoint(world, radius)` instead sets the orbit **radius directly** (close-in) on a metric-world point — the 3D side of **locate-correspondence** and **link-views** (both keep orientation, animate via `SetTargetCenter`/`SetTargetRadius`).

**Selection syncs both cameras** (the default, not a toggle): `SetFocusedMesh (Some m)` flies the 3D camera to frame that mesh (the focus single auto-shows it), `SelectPin (Some id)` flies to the pin centre, and a rail matrix cell runs `FrameCorrespondence` + `FocusScene.focusOnWorld`.

**Locate a correspondence** (a rail **matrix cell** click, `FrameCorrespondence`): one atomic action — sets `SelectedPoint`/`FocusedMesh`/`SelectedPin`, **solos** the mesh, **flies the 3D camera tight** to the anchor (`FlyToPoint`), forces `ProjTop`, and **zooms the focus canvas** onto the anchor (`FocusScene.focusOnWorld`, driven from the matrix-cell handler since `Update` precedes `FocusScene`). It snapshots the prior camera + solo/visibility into `Model.LocateBackup` on the first locate; the focus head's **⤺ back** button (`BackOutLocate`) restores them (and `FocusScene.resetCam`).

**Link-views** (`Model.LinkViews`, focus-head **⇄ link** toggle, **off by default**, pure camera): on → a clean focus-surface click flies the 3D camera to that world point (`FlyToPoint`, both projections); a 3D recenter (double-tap) recenters the focused mesh's **Top** canvas on the picked point (`FocusScene.recenterOnWorld`, Top-only — the pan maths is ortho).

**Correspondence points are set with one armed editor** (`Model.CorrArm = Some(pin, mesh)` — armed for that pin+mesh pair). The single arm button lives in the **focus head**: **✎ edit point** / **✎ editing…** (`GuiFocus.setBtn` → `ToggleCorrArm`), offered whenever a pin is selected **and** a mesh is focused (**the reference included** — editing it moves its `RefAnchor`). While armed the mesh is **isolated in the main 3D view** (`View.wheelIsolation` reads `CorrArm`), the linked focus is brought onto it, and a click in **either** the focus **or** the 3D view sets the point via `PickCorrespondenceAt`; the mode **stays armed** until re-toggled (it does *not* exit on click). A throttled hover raycast feeds `Model.CorrPreview`, drawn as a live cyan aim ghost in **both** views (3D `ScanPinScene.corrPreview` + the focus Top overlay). `PickCorrespondenceAt` is **ROI-clamped** — a pick outside the pin's `InnerRadius` sphere is rejected with a toast (this **reverses** the old "pick is pick — no ROI gate" rule) — and stored mesh-local. Competing actions (place pin, select/delete a pin, Solve, or a workflow-step switch) cancel the arm.

The **focus-panel pick** has no GPU: `FocusScene.worldRayHit` inverts the cursor to a render-space ray (orthographic for Top, cylindrical for Pano), carries it **render → metric world → the focused mesh's server frame** via that mesh's `displayedWorld` (correct in either before/after pose), raycasts server-side (`/query/ray`), and maps the hit back through `displayedWorld.Forward` to metric world (see *Coordinate systems & transform hierarchy*) — shared by both the commit and the hover preview. The **3D-view pick** aims via a throttled single-mesh `View.raycastMesh` that falls through ghosts to the isolated target.

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
- `Model.RegView = RegBefore | RegAfter` — one global toggle (top bar), disabled until any `SolvedTransform` exists, flips 3D + focus + dock together. A third **Peek** button next to it is a spring-loaded hold (`Model.RegPeekHeld`, `SetRegPeek`) that momentarily shows the *other* state — **purely visual**: `MeshView.effectiveRegView` flips the displayed transform (mesh scene + outlines + pin markers + focus), but no query/probe/reducer path peeks (they read the committed `RegView`).
- **Displayed** transform (`ModelTransforms.displayedRender/displayedWorld`, `MeshView.displayedMeshT`) = `RegView = RegAfter && solved → SolvedTransform`, else `LoadTransform`. Reference + unsolved meshes therefore stay at `LoadTransform` in both states.

`SolveCoarse` → `POST /api/query/lsq-pairs` per visible moving mesh with ≥3 correspondence pairs (parallel). **Partial overlap is fine**: the solve runs for every *solvable* mesh (≥3 in-ROI markers, a hard 3) and leaves the rest at their load pose — a sub-threshold mesh no longer emits its own hint (the pin×mesh matrix surfaces per-(pin,mesh) coverage), never a global blocker; the only hard blockers are *no reference* and *zero solvable meshes*. The dispatch toasts `Solving N of M; X needs Y more`, and the short meshes are flagged (the Correspondence dock's `k/n` badge). Correspondence anchors live in the mesh's **server frame**, so the pairs are already the load-pose own-frame point; the server returns the **absolute** world transform mapping load → reference, which the reducer stores directly as `SolvedTransform` (`worldToRender`) and flips `RegView = RegAfter` (so before/after enables even when only some meshes solved). A re-solve replaces `SolvedTransform` only; `LoadTransform` is never overwritten. Server math: `RegMath.solveRigid`, weighted Umeyama/Arun with Jacobi SVD + det-flip so planar/collinear sets never reflect; response carries per-pair residuals + covariance eigenvalues + `collinearityWarning`. Changing the reference drops `SolvedTransforms` + snaps to `RegBefore` (a solve is relative to its reference).

**Correspondences** (`ScanPin.Correspondence option`): `{ RefAnchor (reference own-frame marker — the pin centre if the host is the reference, else its closest-point projection); RefDistance; Anchors : Map<mesh, {Point; Source}>; Residuals; InRoi }`. **Every pin is a registration pin** — there is no enable/disable distinction. `makeAnchor` gives each new pin `Some Correspondence.empty`, and placing a pin auto-seeds it against the reference (if one exists); a freshly placed pin is selected. `Anchors.Point` is in the mesh's server frame; display/solve derive metric world via `displayedWorld.Forward`, so the before/after toggle moves markers automatically (no `bakeAnchors`). Auto-seed (placement / reference change / ⟳) projects via parallel `/query/closest`, **membership** ROI-clamped to `roiReach` (the probe cylinder's bounding sphere); manual `AnchorPick3D` picks are **ROI-clamped to the pin's `InnerRadius`** (rejected with a toast otherwise) and are never overwritten by auto-seed. `AnchorSource = AnchorAuto | AnchorPick3D`. The 3D **constellation** (`ScanPinScene`) draws a small wire-sphere + cross glyph per anchor + a larger one at the reference point + lines to the reference (all line geometry, fixed render size — independent of pin radius). It renders **only in the Correspondence workflow** (`constellationActive`); Overview/Inspect hide all correspondence point markers, matching the focus-panel overlay's gating (`overlaySegs` returns empty outside Correspondence). The pin markers/rings/verdict glyphs are unaffected.

## Visibility model + per-mode defaults (§A/§C)

Three **orthogonal** layers compose the same way in every mode; a mode only sets *defaults* (never special-cases the layers):

1. **Solo** (`MeshSolo`): `NoSolo` → all visible meshes shown; `Solo m` → `m` emphasized, every other mesh drops to the **ghost floor** (solo overwrites `MeshVisible` with a restore set, so the floored meshes are `MeshActive = false`).
2. **Pin isolation** (`AnchorGhostMode` + pin blobs): solid only inside the focused pins' ROI, ghost outside. Per-mode default set on `SetWorkflowStep` (**on in Correspondence, off in Overview/Inspect**); the hold modifier `IsolatePeekHeld` (◎ / hotkey I) forces it on momentarily where it's off.
3. **Ghost floor** (`GhostSilhouette`/`GhostOpacity`): what non-emphasized geometry renders as — **Ghost** (faint) or **Hidden**. Toggled from the **gear** ("Ghost silhouette" switch + opacity slider). Solo / isolation all send their others to **this** floor.

**Per-mode defaults** — `click → focus` and `hover → peek` are universal/identical everywhere; the only per-mode difference is **auto-solo**:

| Mode | click → focus | focus → auto-solo | pin isolation | 3D content |
|---|---|---|---|---|
| Overview | yes | **no** (◐ solos manually) | off | all meshes textured, no error overlay |
| Correspondence | yes | no | **on** | constellation + pin isolation |
| Inspect | yes | **yes** | off (+ hold) | no solo → variance aggregate; solo → that mesh's field |

## Three-mode GUI + shared selection

The left rail (`GuiRail`) has exactly three modes — **Overview · Correspondence · Inspect** (`Model.WorkflowStep`). **Containers never move on a mode change**; only their content + which affordances are interactive change (cross-faded). Region roles are fixed: rail = *control plane* (manage + switch); focus = *view plane* (spatial preview, active overlay, enlarged = focused); dock = *the parts of the selected object*.

| Mode | Rail | Focus (canvas) | Dock |
|---|---|---|---|
| Overview | mesh roster (hover=peek, click=focus, ⌖ frame) | textured tiles — all meshes, each a ★ ref · vis · ◐ isolate strip, hover=peek; no large single | focused-mesh summary card |
| Correspondence | pin×mesh matrix + place-pin (the global readiness hint moved to the top bar) | pick surface (Pano/Top) | pin meta (identity chip · radius · k/n · Solve) |
| Inspect | pin×mesh matrix | difference / displacement tiles (channel toggle) | channel toggles (difference sub-mode + intrinsic) + distribution + shift readout |

The **left rail is a mode switch of two components**: in **Overview** a mesh roster (`GuiRail.meshRow`); in **Correspondence + Inspect** the **pin×mesh difference matrix** (`GuiRail.matrixView`). **Read/write parity** (§B): clicking an Overview row, a focus **tile**, or a **3D mesh** all set `FocusedMesh` (3D via `raycastNearestNamed`, +solo in Inspect); the active mesh shows the matching treatment on the row (`rail-mesh-sel`), the tile (`fm-active`), and in 3D (`focusedOutline` cyan bbox). The focus **tiles** are now the single mesh browser and list **all** meshes (hidden ones dimmed via `ft-hidden`, re-enableable via the tile's own ★ ref / vis / ◐ isolate strip; hovering a tile peek-isolates that mesh in the 3D view, mirroring the Overview roster).

**The pin×mesh matrix** (`GuiRail.matrixView`): rows = pins (glyph · `ShortName` · `PinColor` swatch · a strip of per-mesh cells), columns show only mesh colour + number. Each cell = that (pin,mesh)'s ROI-median signed distance to the reference (the pin's probe median — so it is before/after-aware, the probe refetches on `RegView`), painted on the **linear-diverging** difference colormap; out-of-ROI → a faint hatch glyph, probe pending → a faint placeholder. Cells are **visibility-stable**: the probe samples *every* mesh (not just the visible ones), so hiding / soloing a mesh never blanks or recolours its cells. A cell click selects (pin,mesh) + locates it (`FrameCorrespondence`, which solos the mesh); **clicking the already-selected cell toggles it off** — `BackOutLocate` + clear selection, un-isolating the mesh. A pin-row click runs `SelectPin`. The per-mesh ★ ref / vis / ◐ isolate controls live on the tiles, **not** in the matrix.

**One shared selection record drives all linking** (`Model.Selection = { SelectedPin; FocusedMesh; SelectedPoint; Hovered }`). Linked highlighting is a *consequence* of every region binding to `Selection` — there are no panel-to-panel hover emitters. Grammar everywhere: **hover = peek** (writes `Selection.Hovered` via the single `SetHovered`), **click = select/promote**, **drag = edit**. Pin selection lives in `Selection.SelectedPin` (NOT `ScanPinModel`); `ScanPinUpdate.handleMsg` maintains it and drops a dangling selection when its pin is deleted.

**Focus panel** (`GuiFocus` + `FocusScene`) is **WebGL** — a large single (the focused mesh, rendered full-res + atlas-textured in render space at its displayed pose) over a strip of textured thumbnail tiles — **the single mesh browser**, one renderControl per mesh (`FocusScene.focusTile`/`multiples`); **all** meshes are tiled (hidden dimmed via `ft-hidden`), each tile carrying a per-mesh **★ ref · visibility · ◐ isolate** control strip (hover on the tile peek-isolates the mesh in 3D), and the **reference tile ringed gold with a ★** (`ft-ref`). **Overview drops the large single** — the panel is just the tiles. Many renderControls coexist fine here. **Top** (the default projection) **= strictly orthographic** (hand-built ortho matrix); **Pano = cylindrical unwrap in a vertex shader** (`FocusShaders.pano`, composed after `DefaultSurfaces.trafo` so the WorldPosition varying — and thus picking — survives; the camera is identity, the shader writes clip). The unwrap **eye** (`PanoEye` uniform + the pano pick-ray origin in `worldRayHit`) is the mesh's panorama centre: `Model.PanoCenters[mesh]` (absolute world) carried into the mesh's own frame (`− centroid`) then through `renderT` — so it scales and follows the before/after pose like the geometry; **no entry ⇒ the mesh origin** `(0,0,0)`. See *Panorama centre*. A tiny pan+zoom controller (per-mesh pan/zoom `cval`s kept in `FocusScene.camStates`, no orbit) drives the single with mouse-anchored zoom (**left- or middle-drag = pan**, matching the 3D view); `⟲ reset` calls `FocusScene.resetCam`, `FocusScene.focusOnWorld`/`recenterOnWorld` set a Top-view pan/zoom onto a metric-world point (locate + link-views). The focus head buttons: **✎ edit point** (`ToggleCorrArm`, the armed correspondence editor), **⤺ back** (shown only mid-locate — `BackOutLocate`), **⇄ link** (`LinkViews` toggle), **⟲ reset**; the head also shows the selected pin's identity chip. The whole panel is **resizable** via a left-edge drag handle (aspect-locked, JS in `GuiFocus`) that shows a **visible grip bar** (`.focus-resize::before`). **Picking is Dom-driven, not `Sg.OnTap`** (that did not fire reliably in the 2nd render control): `worldRayHit` inverts the cursor to a render-space ray (hand-rolled ortho drop for Top / pano direction from the eye for Pano), carries it **render → metric world → the mesh's server frame** through that mesh's `displayedWorld` (correct in either before/after pose; no per-mesh-centroid juggling), hits `/query/ray`, and maps the hit back through `displayedWorld.Forward` to metric world (see *Coordinate systems & transform hierarchy*). The pick reads the cursor in **CSS px**: the NDC math divides `ViewportSize` (framebuffer px) by the **shared `FocusScene.dpr`** the main view publishes (computed from its `ViewportSize/ClientSize` in `OnRendered`); the focus controls don't bind `RenderControl.ClientSize` themselves (framebuffer px there would offset the pick on hi-dpi). While armed (**✎ edit point** / `CorrArm`) a **move** throttle-raycasts → `CorrPreviewComputed` (the live 3D ghost), and a **click** raycasts → `PickCorrespondenceAt` (ROI-clamped, **stays armed**). Gated on a selected pin + a focused mesh (the reference included). In Inspect the tiles recolour per channel via `focusColor`/`FocusMode` (see Inspect visualizations). In **Correspondence** mode the Top single overlays each pin's **bounding-sphere circle** (true `InnerRadius` footprint, render space) + a **screen-fixed always-on-top glyph** (cross + ring, sized `0.05·fitExtent/zoom` so it holds constant on screen) at the focused mesh's anchor per pin (`FocusScene.overlaySegs` → `Lines.render`, `DepthTest.None` in `RenderPass.passOne` so it draws **always-on-top** — the surface writes depth in the default pass, so a same-pass overlay rendered *under* the mesh; the later pass + no depth test is the same trick as the main-view cross); **Top-only** because render-space lines can't ride the Pano vertex-shader unwrap (same reason the displacement arrows force Top).

## ScanPin system

A ScanPin is a 3D annotation in **metric world-space**: `Centre : V3d`, `InnerRadius : float` (a hard sphere — α = 1 and full probe weight inside). Pins drive the per-pixel blob in the mesh shader (`Blobs` uniform). Render-space conversions happen at boundaries (`ScanPin.renderCentre`/`renderLength` — the dataset transform; see *Coordinate systems & transform hierarchy*).

Every pin also carries an immutable **identity triple** assigned at creation (`ScanPinUpdate.makeAnchor`): `Glyph : string` (a preattentive Unicode shape), `ShortName : string` (a random pronounceable 2-char code, collision-checked vs other pins + mesh numbers), and `PinColor : C4b` (from a dedicated **pin palette** distinct from the mesh palette; glyph + colour share a least-used slot for redundant coding). The palette + short-code generator live in `Primitives.PinPalette` / `Primitives.PinIdentity`. This triple is the pin's **only** identity everywhere (there is no free-text pin name) — the rail matrix row, the 3D flag label (`ScanPinScene.pinLabels`), the focus head chip, the readiness-hint labels (`Primitives` — glyph + code), and its distribution sample colours.

**Placement:** Correspondence mode → **○ Place pin** → tap a surface. Click-and-drop: the pin is created immediately (no commit step), becomes selected, and placement ends. While placing, pin isolation is **forced on** and the live hover is added as a transient **flashlight** blob (see Ghosting rules), so the terrain drops to ghost and only the existing pins + the hover preview read solid; the GPU pick can also fall through a ghost via the placement raycast (see Picking). Radius is edited afterwards from the Correspondence dock's pin meta (`SetInnerRadius` on the selected pin). New pins take `Model.QuickPinRadius` (default 0.5 m) as their inner radius. `Placement : PlacementState = PlacementIdle | AnchorPlacement`.

**M3C2 probe:** every pin owns `Probe : ProbeState`. It samples **every mesh** (not just the visible ones — like contact rings; visibility gates only rendering + the distribution/3D consumers, so the matrix cells stay stable) inside a cylinder (radius = `InnerRadius`, length = 20 m fixed `ScanPin.fixedProbeLength`, axis = PCA normal of the reference inside the pin sphere) via `POST /api/query/probe` and returns per-mesh signed-distance distributions (re-centred so 0 = reference median) + the dataset/algorithm/conditioning decomposition. Each per-mesh `ProbeDistribution` also carries `Positions : V3d[]` (per-sample world positions, 1:1 with `Samples`) + `Footprint`. Lazy + debounced (`ScanPinUpdate.ensureProbe`, per-pin generation-guarded CTS — it now runs for **every** pin, not just the selected one, so the rail matrix has a cell per (pin,mesh)); invalidation just resets to `ProbeNone`. The probe drives the **rail matrix cells**, the Inspect dock's **distribution** panel, and the difference field's ±LoD₉₅ detection-limit band.

**Contact rings:** every pin caches `ContactRings`; `ScanPinUpdate.ensureRings` debounced fan-out of `POST /api/query/contact-rings` over **all** meshes (visibility only gates rendering, never recompute), per-pin CTS. Transforms are rigid → centre inverse-transformed into each mesh's own frame, rings mapped back via the displayed transform.

**3D rendering** (`ScanPinScene.fs`): `pinDots` (small **invisible** icosphere pick proxies — alpha 0, still in the depth/id pick pass — carrying the select tap), `pinMarkerLines` (the visible pin-centre marker: a small wire-box jack, yellow when selected), `pinLabels` (a 3D flag **text label** above each pin — its `ShortName` in its `PinColor`), `pinRings` (equator ring ⊥ probe axis + cached per-visible-mesh contact rings, in the pin's `PinColor`, occluded by geometry as the spatial cue), the correspondence `constellation` (wire-sphere + cross glyphs — **pure non-interactive line geometry**; hover brushing comes from the rail matrix rows/cells via `Selection.Hovered`, **not** from 3D pick proxies, which were removed because the invisible alpha-0 spheres intercepted surface picking), `ghostPreview` (placement hover), `corrPreview` (cyan wire-sphere + cross for the live armed-correspondence aim), `brushedMarkers` (the per-sample Inspect brush highlights), and `pinGlyphs` (far-view verdict pole — green if every moving mesh's `|median| ≤ LoD₉₅`, red if any is significant). All markers/glyphs are **fixed render size, independent of pin radius**, and drawn on top (an invisible proxy writes depth, so a depth-tested marker would self-occlude behind it). Marker world positions follow the displayed (before/after) transform because anchors are mesh-local.

**Per-sample brushing** (Inspect, bidirectional): `ScanPinScene.brushSamples` builds one canonical per-sample list (index = global id `gid`) consumed by the distribution chart (gid labels), the 3D `brushedMarkers`, and the 3D→chart spatial scan (`View.OnPointerMove` in Inspect). chart→3D: a drag on the chart canvas hit-tests dots and writes their gids to a hidden `input.ins-brush-bridge` (+ a bubbling `input` event) → `SetBrushedSamples` → 3D markers; 3D→chart: hovering the surface brushes nearby samples → a `data-brushed` attr the chart reads. State is `Model.BrushedSamples : Set<int>` (capped 200, cleared on mode/dataset change).

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
- **Don't build in-place-updating *lists* with `AVal.map (… IndexList.ofList) |> AList.ofAVal`.** That mints fresh `Index` keys on every recompute, so any element change diffs as remove-all + add-all — it churns the whole list (tearing down/recreating every row's DOM/GPU resources) and intermittently double-renders a row in the reconciler. Instead derive a stable-identity incremental list from the source map: `AMap.map (project to just the row's inputs) |> AMap.toASet |> ASet.sortBy key |> AList.map row`. Projecting to only the fields a row consumes means unrelated field changes (e.g. a pin's probe/ring result) don't re-key its row. The rail pin×mesh matrix (`GuiRail.matrixView`) is built this way. (Small, rarely-changing lists — the gear dataset list, the top-bar readiness hint — still use the simple form; the churn is harmless there.)
- **Never create a *transient* `aval` inside another aval's compute and read it.** `AVal.custom (fun t -> … (makeSomeAval args).GetValue t …)` — or the same with `AVal.force` — builds a fresh inner aval on every evaluation; that inner aval can drop its dependency edge so the outer **evaluates once and then stops re-firing** (it silently freezes on its first value). This bit the focus single (`focusMeshOf model` built inside `single`'s aval → the panel rendered blank because the aval never saw the meshes load) and the constellation (a transient `markerWorldOf` once built inside `constLines`). The fix is always the same: **inline** the inner computation so its `model.X.GetValue t` reads happen against the outer token directly (as `constLines` now resolves each marker), or bind the inner aval **once** outside the compute (a stable `let`, as the focus correspondence overlay does with `let pinsAval = … |> AMap.toAVal` / `let dispRenderT = MeshView.displayedMeshT …`). Reading a *stable* aval via `.GetValue t` is correct — only freshly-built-per-eval avals are the trap.

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
RegistrationModel.fs   ScanPinId, correspondence anchors, readiness engine (Readiness.compute — GLOBAL diagnostics only: no-ref / ≥3-pins / no-solvable blockers, near-collinear + no-moving, Ready-to-align; the per-mesh & per-pin hints were removed, superseded by the matrix), FlyToMath, NavAction, HeatmapMode (WASM-free, shared with Supertests)
ScanPinModel.fs / .g.fs ScanPin + placement state
PinGeometry.fs         icosphere, sphere outline
Model.fs / .g.fs       [<ModelType>] Model + Selection + RegView + ModelTransforms
LineShader.fs          Shader.flatColor + Lines (pixel-constant 3D lines)
Primitives.fs          widgets, showWhen/showWhenNot, observedRender, ReadinessView adapter, PinPalette/PinIdentity, Diff colormap, friendly mesh names (friendlyMap/friendlyName — strip the roster's common prefix/suffix)
Messages.fs            Message DU
ScanPinUpdate.fs       pin sub-reducer + ensureProbe/ensureRings postludes
UpdateHelpers.fs       reducer helpers + debounce/generation state, seedAnchorsCore
Update.fs              main reducer + ensureVariance postlude
MeshShaders.fs         RenderPass + MeshShader + OutlineGBuffer/OutlineEdge
MeshView.fs            LoadedMesh, buildScene, load/displayed transforms, pin blobs
OutlineView.fs         offscreen image-space outline pass
ScanPinScene.fs        pin sg nodes + correspondence constellation
SceneGraph.fs          composes meshScene + pinScene + cross + labels + reference/focus outlines
FocusShaders.fs        FShade pano (cylindrical) vertex shader for the focus single
FocusScene.fs          WebGL focus renderControls (single + per-mesh tiles, ortho/pano, pan/zoom, pick)
GuiTopBar.fs           top bar (isolate/overlays holds, before/after + peek hold, reconstruction-readiness hint, gear popover)
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
POST /api/query/probe                           → N-mesh M3C2 probe (per-mesh distributions + KDE + three sources; each distribution carries per-sample `positions` + `footprint`)
POST /api/query/region-distance                 → per-vertex signed M3C2 distance (mode 0) or vertical Δz (mode 1) of a target mesh to the reference, in the target's served vertex order; 1e30 sentinel where no closest point. Mode 0 also sentinels any vertex whose nearest reference point is farther than `regionMaxDistFrac` (0.02) × the queried mesh's bbox diagonal — isolated/non-overlapping points produce no error response (the M3C2 analogue of mode 1's vertical ray missing). Feeds both the variance map and the focus difference channel.
```

All query coordinates are **absolute world space**; the server converts `localPos = worldPos − meshCentroid`. (Removed for lack of consumers — don't re-add without one: `/query/icp`, `/query/patch`, sphere/box/ray-batch, grid-eval, isoline, curvature-ridge, region-grid.)

## Client Model snapshot

Top-level `Model` fields (see `Model.fs`):

- `Camera`, `MeshOrder`, `MeshNames`, `MeshVisible`, `MeshesLoaded`, `CommonCentroid`, `MenuOpen`, `SavedMenuOpen`, `DebugLog`
- `Datasets`, `ActiveDataset`, `DatasetScales` (`{"SETSM_glacier" → 0.01}`), `DatasetCentroids`, `PanoCenters` (`Map<mesh, V3d>` = per-mesh panorama/camera centre, absolute world coords; from `pano-centers.txt`, see Panorama centre)
- `GhostSilhouette` (default on), `GhostOpacity`, `ShadingStrength`, `SlopeThresholdDeg`, `AnchorGhostMode` ("Isolate pins", default on), `QuickPinRadius`, `OutlineThreshold` (outline edge-detect threshold, default `0.004`), `IsolineBands` (isolines over the scene Z range, default `700`), `DiffRangeScale` (Inspect difference-heatmap range multiplier, default `1.0`)
- `SceneBounds`, `MeshBounds`, `ActivePickingLayer`, `IsolatePeekHeld` (spring-loaded hold-to-isolate, ◎ / hotkey I), `ShowOverlaysHeld` (spring-loaded hold-to-desaturate, 🎨 / hotkey O)
- `LoadTransforms`, `SolvedTransforms`, `RegView` (`RegBefore`/`RegAfter`), `RegPeekHeld` (spring-loaded before/after peek — visual-only), `Registration` (`{ ReferenceMesh; Running }`)
- `HeatmapMode` (`HeatOff | HeatIncidence | HeatRange | HeatShape`), `ExtrinsicZDiff` (difference sub-mode M3C2 ↔ Δz), `SurfaceDistance` (`Map<mesh, float32[]>`, the reference variance array), `FocusDist` (`Map<mesh, float32[]>`, per moving mesh signed distance for the focus difference channel)
- `ScanPins` (`ScanPinModel`), `Selection` (`{ SelectedPin; FocusedMesh; SelectedPoint; Hovered }`)
- `RenderingMode`, `MeshSolo`, `GearPopoverOpen`
- `WorkflowStep` (`Overview | Correspondence | Inspect`), `InspectChannel` (`ChDifference | ChDisplacement`), `FocusProjection` (`ProjPano | ProjTop`), `CorrArm` (`(ScanPinId * string) option` — the armed correspondence editor: the pin+mesh pair being edited; isolates that mesh in 3D + aims the cyan `CorrPreview` ghost; replaces the old `CorrSetMode` + `Corr3DPick`) + `CorrPreview` (live 3D ghost point), `BrushedSamples` (`Set<int>`, the per-sample brush selection, capped 200, cleared on mode/dataset change), `Toast`
- `LinkViews` (focus↔3D camera sync, **off** default), `LocateBackup` (`LocateState option` — the prior camera + solo/visibility snapshot captured by `FrameCorrespondence`, restored by `BackOutLocate`)

GUI placement:
- Top bar (`GuiTopBar`): hamburger, **◎ Isolate** (hold), **🎨 Overlays** (hold), the global **Before/After** toggle + spring-loaded **Peek** hold, the **reconstruction-readiness hint** (global correspondence status; Correspondence only), coordinate readout (friendly mesh name), **⚙ gear** popover (dataset switch, rendering mode, outline edge threshold + isoline count + difference-heatmap-range sliders, camera speed, ghost silhouette + opacity, isolate-pins, shading strength, slope threshold, quick-pin radius, dataset info, mesh centroids, debug log). The **⟲ reset-camera** and **👻 ghost-floor** top-bar buttons were removed (ghost floor lives in the gear; double-tap still recenters).
- Left rail (`GuiRail`): Overview = mesh roster; Correspondence + Inspect = the pin×mesh matrix (Correspondence also keeps the place-pin button; the readiness diagnostics moved to the top bar).
- Right focus panel (`GuiFocus` + `FocusScene`): WebGL large-single + per-mesh tiles.
- Bottom dock (`GuiInspector`): Overview = focused-mesh summary; Correspondence = pin meta (identity chip · radius · k/n · Solve); Inspect = channel toggles + distribution (all-pin, pin-coloured, brushable) + shift readout.
- Overlays (`GuiOverlays`).

## Tests

`src/Supertests` is a console runner (paket-managed, no extra packages) that compiles `RegistrationModel.fs` + `RegMath.fs` directly and covers: the Umeyama solver (recovery, reflections, weights, collinearity, <3-pairs rejection), `RegConditioning`, the readiness engine, and the fly-to math — `dotnet run --project src/Supertests`. Against a running server (`ASPNETCORE_URLS=http://localhost:8002 dotnet run --project src/Superserver`): `node tools/integration.mjs` (closest-point seed → rigid perturbation → `/query/lsq-pairs` recovers its inverse → `/query/probe` median error shrinks).

## Aardvark.Dom gotchas

- `Attribute("for", "...")` on `<label>` is silently dropped — nest `<input>` inside `<label>`.
- `Attribute("checked", "")` is dropped — use `Attribute("checked", "checked")`.
- CSS `~` sibling combinator breaks (Aardvark inserts wrapper nodes) — use `:has()` on a known ancestor.
- `RenderControlInfo` and `TraversalState` both have `.Runtime` — annotate `(info : Aardvark.Dom.RenderControlInfo)` when ambiguous.
- `yield!` is not supported in Aardvark.Dom CE builders — use OnBoot JS with MutationObserver for dynamic SVG/canvas (the `observedRender` helper, the focus-panel canvas, the orientation indicator).
- `renderControl { ... }` can be nested inside `div { ... }` — it creates a WebGL canvas child. The app has **several**: the main viewport plus the focus panel's single + one tile per mesh (`FocusScene`). Multiple controls coexist fine on this backend. **`Sg.OnTap` (and the other Sg pointer events) did NOT fire reliably in the secondary focus controls** — the focus does its picking with Dom pointer handlers + a server `/query/ray` raycast instead (`FocusScene.worldRayHit`). Camera input there is also Dom-level (`Dom.OnPointerDown/Move/Up` without pointer capture — capture hijacked later clicks).
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
