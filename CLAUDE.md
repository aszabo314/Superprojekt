# Superprojekt — assistant notes

Research prototype for interactive 3D inspection and **registration** of geological mesh datasets (multi-epoch scans of the same terrain). Two F# projects:

- **Superserver** — ASP.NET Core + Giraffe. Serves mesh data and runs the spatial queries (Embree BVH); hosts the WASM client at `http://localhost:5000`.
- **Superprojekt** — Blazor WASM client. Aardvark.Dom Elm-style architecture, WebGL2 rendering. Thin client; heavy compute goes to the server.

See `README.md` for what the app does and how to run it. This file collects the **rules and pitfalls**; behaviour is documented by the code.

## Style

- Light theme, high contrast, print-appropriate.
- GUI must be readable to a non-expert at first glance.
- Colour families are disjoint: the scalar gradients own red/blue (diverging difference) and red→yellow→green (incidence/shape), plus pale grey = no-data and **gold = the reference root** (the `--ref-gold` CSS token family — never fork the hue). Mesh identity = distinct vivid hues (`Primitives.meshPalette`), chosen to stay clear of the diverging map's red/blue ends and near-white centre. Identity rides on thin marks (swatches, outlines, rings, chart layers) — gradients fill areas; never hand new UI a colour from another family.
- 3D marks are **duplex**: a white core with a near-black ink outline (`LineGlyphs.duplex`), readable on any terrain. All-white (no ink) is the *uncommitted* layer — pin drafts and aim previews only. Committed pin point markers = mesh-colour fill + white outline.
- Comments follow **Comment discipline** below.
- Concise code, no unnecessary abstractions, no premature helpers.

## Comment discipline

One test for every comment: does it say something the reader cannot get from the code under it? If not, it is redundant and **forbidden**.

Never write:

- **Restatements** — a comment that paraphrases the line or block below it (`let lastPick = … // the last pick`). Clear naming does that job; if the name isn't clear, fix the name.
- **Spec references** ("implements v14 §5") — specs are transient working documents, deleted at review time; a pointer into one is instantly dead.
- **Absence notes** ("feature X left out per spec") — code never documents what it doesn't contain.

The only reasons a comment exists:

- The code's form or function is genuinely **non-obvious** — a trick, an invariant, a unit, a coordinate frame.
- The code looks wrong or needlessly complicated because an **external library constraint** forces the shape (Aardvark/FShade/WebGL gotchas) — name the constraint so nobody "simplifies" it back.
- The code is **performance-shaped** — deliberately structured for the adaptive/render/query hot path — say what the shape buys, or the next edit flattens it.

Form: as short as possible. State the constraint or invariant, nothing else — no flourishes, no context recap, no justification essays.

## State rules (Elm-style)

- One `[<ModelType>]` `Model`; Adaptify generates the `.g.fs` files. **Never edit `.g.fs` by hand** — run `./adaptify.sh` (or `adaptify.cmd`) after editing a model file.

### Registration graph (the core state)

- `RegGraph = { Root : string option; Edges : Map<string, RegEdge> }` (RegistrationModel.fs) — a parent map, i.e. a **rooted tree**; `RegEdge = { Child; Parent; Transform; Quality }`. Child = MOV, Parent = REF (nearer the root). `Transform` is **metric world**, mapping the child's *as-loaded baseline* onto the parent's (the lsq convention); ancestor registration composes on top, so an edge never re-bakes when something above it changes.
- Composition: `pose(m) = edge.Transform * parentPose` (`RegGraph.composeAll`; Aardvark `a * b` applies **a first**). Root = identity, absent from the map. `Model.ComposedPoses` is the render-space projection (`ModelTransforms.recomposePoses` via `RigidTransform.worldToRender`); displayed pose = composed pose else as-loaded baseline (`ModelTransforms.displayedRender`/`displayedWorld`).
- **The committed graph is always a spanning tree.** `tryAddEdge` returns `EdgeAddResult`: `EdgeAdded` (ref in tree ∧ mov not), `EdgeRejected` (isolated / no root), `EdgeClosesLoop (cycleEdges, residual)` — a loop is only ever **transient** state (`Model.LoopPending` + the blocking modal); resolution removes exactly one cycle edge (`RegGraph.resolveLoop`) or discards the new edge. Never commit a graph with a cycle or a second component.
- `RegGraph.reroot` keeps the registration when the new root is a tree member: every edge on the new-root→old-root path reverses (Child/Parent swap, Transform inverted, Quality kept). **Ordering hazard in `reroot`/`resolveLoop`: remove ALL path children from the map before re-adding the reversed edges** — a reversed edge re-uses the next path edge's child as its key, so interleaving remove/add drops edges. Designating a non-member root clears the graph (a tree cannot hang off an outside mesh).
- `RegGraph.removeEdgeCascading` drops the edge **and its whole subtree** — a stranded component would break the invariant.
- Edge before/after: `EdgeSide = EdgeBefore | EdgeAfter`; `composeEdge child side g` — Before = the committed graph with *this one edge* zeroed to identity (ancestors still apply). `ModelTransforms.edgeWorld` is the metric-world form. This per-edge pairing feeds the chart's before-outline and the before pin batches; there is **no global Before/After state**.
- Solve flow: `SolvePair` (≥3 pins) orients via the tree — existing edge ⇒ re-solve same orientation, else un-treed mesh = MOV onto treed REF; pin point pairs feed `/api/query/lsq-pairs` at the **as-loaded baselines**. `PairSolved` (guarded by `pairSolveGen`) must distinguish a same-pair re-solve by checking `e.Parent = parent` — `Map.containsKey child` alone misroutes a redundant pair whose child keys an edge elsewhere. A loop-closing result stages `LoopPending` with the weakest-quality cycle edge pre-selected. `Quality = RegGraph.solveQuality residuals` = 1/(1+rms/0.05).

### Navigation: the focus rail, selection & visibility

- **Four-level focus rail** (`FocusLevel = FocusSetup | FocusMatrix | FocusPair | FocusPin`, `Model.Focus`): strictly narrowing scopes of *what is looked at*, never tool modes — the pair toolkit (pins, Solve, inspection, peeks) stays inside its level. Free jumps among **enabled** stops via `SetFocus` (the reducer re-guards `FocusLevel.enabled`: Setup/Matrix always, Pair needs a selected pair, Pin a selected pin or an in-flight placement); `FocusAscend` = one level up (Esc). The `normalizeFocus` post-step demotes to the nearest enabled ancestor whenever a reducer step retracts a level's subject (pin deleted, placement aborted, selection cleared).
- **Scoped selection manager** (`FocusSelection = { Pair; Pin; Point }` — ONE plain-record aval `Model.Sel`): per-level selection with **memory** (re-entering Pair restores the remembered pair incl. its last pin; the matrix highlights it) and **cascade clear** (a new pair clears pin+point+the in-cell caches; root designation and dataset switch clear all). `SelectPair` = matrix cell click (selects AND enters Pair); `SelectPin` = pin-row click (commit auto-selects the newborn pin; deleting the selected pin clears it). Selection is deliberately *scoped per level* — never regress to a global selection blob or panel-to-panel hover/selection emitters.
- `MeshVisibility.shown focus selPair isolate hoverPair name` (Model.fs) is the **single** shown/clickable rule: Setup/Matrix = all meshes (the Setup isolate narrows to one, the matrix hover to the hovered pair), Pair/Pin = the selected pair only; `MeshVisibility.pinShown` mirrors it for pins (scene nodes + blobs). Every consumer — render `MeshActive` (one shared `shownCtx` aval → cheap per-mesh projections), raycast candidate sets, coverage gating — goes through it; don't special-case visibility anywhere else. The Setup isolate (`SetupIsolate` click-lock + `SetupIsolateHover` button-hover preview; hover wins) and the matrix hover (`MatrixHoverPair` — also drives the shader's per-pixel overlap preview) are level-scoped transients — `jumpFocus` (the ONE focus-change path) wipes them, plus the peeks and the armed probe, on every jump.
- ONE `Esc` chain, in the view's key handler: loop modal (cancel) > probe disarm > `FocusAscend`. Leaving Pin mid-placement aborts the transaction **through the jump itself** (`jumpFocus`: Pin → elsewhere ⇒ full rollback), so Esc needs no placement branch. New cancellable states slot into this chain, they don't get their own key.
- The in-cell caches ride the **pair selection**, not the visit: Pair⇄Matrix jumps keep them (instant re-entry); only a pair change / pin edit / pose change / reroot / dataset switch invalidates.
- REF/MOV of a pair is derived, never stored: `MatrixNav.pairRefMov` (edge parent if registered, else smaller hop depth to the root, unconnected = MOV, tie → key order).

### Pins

- `ScanPin` is **atomic**: `{ Id; ShortName; Pair (PairCell.key order); AnchorMesh; CentreLocal; InnerRadius; PointA; PointB; CreatedAt; ContactRings }` — points are non-optional; no partial pin exists. Birth only through the placement transaction: `PlacementState.PlacementActive of DraftTool * PinDraft` — Area/Points sub-tools in free order, ✓ Commit is the only creation path, abort = full rollback (the draft never touches the pin map). `BeginPinTransaction` jumps focus to the **Pin level** — the two panes are the only picking surface; leaving Pin (Esc or a rail jump) aborts. Point edits need no arming state: at the Pin level a pane click IS the re-pick (`EditPointAt`, atomic replace — the old point stands until the click commits).
- The pin **rides its anchor mesh**: world centre = `displayedWorld(AnchorMesh)` ∘ `CentreLocal` everywhere (`ScanPin.centreWorldWith`); `CentreLocal`/`PointA`/`PointB` are stored in their mesh's **own frame**, so poses never re-bake stored geometry. Points are unconstrained (outside the area sphere is legal — the radius scopes analysis, not editing).
- **Any pin edit (radius / point re-pick / delete) on a registered pair drops the edge cascadingly** (+ toast + `invalidateCellError` + `bumpPairSolve` so in-flight solves land dead). A solve's validity is exactly its input pins.

### In-cell inspection caches

- `Model.CellError` / `CellErrorBefore` (per-pin `/query/pair-error` batches at displayed poses; Before uses the edge-before poses via `composeEdge`) and `CellDist` (`/query/region-distance` MOV vs REF) share ONE generation (`cellErrorGen`); `invalidateCellError` bumps it on pair-selection change/pin edit/solve/edge drop/reroot/dataset switch (level jumps alone do NOT — the caches ride the selection). Lazy single-flight postludes: `ensureCellError`/`ensureCellDist` (Update.fs).
- Samples are stored **MOV-relative-to-REF** (sign flipped at landing when MOV was meshA in the request). Brush gids = indices into the canonical CellError sample concatenation — any refetch invalidates the brush.
- `MeshView.cellRange` is THE shared scale selector for the 3D map uniforms, the chart x-range and the legend: `ErrorRange.ofSamples` over the pin samples when any exist (always spans 0, hard cap ±0.5 m), else `ErrorRange.ofDistances` over the per-vertex distance distribution (per-sign 95th percentile, 1 mm floors — a pinless cell on the ±cap default would wash near-white). Never per-mesh normalization, no user range sliders.
- Brush = **sole focus**: a non-empty brush suppresses the error map; a plain chart click clears it. The false-colour map paints the MOV mesh ONLY (the reference never carries error colour; `InspectPlain` swaps only MOV's base to near-white).

### Peek keys

- **V** (visibility: MOV blinks off) and **B** (pose: MOV shows as-loaded) are spring-loaded — the keys AND the top-bar hold-buttons (`.tb-peeks`, shown at Pair only; pointer capture so the release lands even off-button) — **Pair-level scope only** (refused at Pin — a pose blink would move the picking surface mid-placement), refused unless both pair meshes are GPU-resident and no loop modal is open; releases always land; peeks clear on any focus jump and on dataset switch.
- The peeks are **purely visual**: `MeshView.displayedMeshT`/`displayedWorldAt` are peek-aware (rendering + view-side picks + surface-riding pin markers follow the blink); `ModelTransforms.*` (reducer/query side) is peek-**blind** — no query may read a peeked pose. The vis peek flips `Sg.Active` on all three mesh node families (main surface, outline G-buffer, footprint coverage) — never the ghost floor, a blink needs a clean swap. Zero refetch: during the pose peek the error map rides MOV's surface with registered-pose values (accepted approximation).

### Secondary views: the Pin panes & the Setup survey tiles (GuiPanes.fs)

- The **Pin tiles**: the pair's two picking tiles (mesh A above mesh B) in a thin right-edge strip — the TWIN of the Setup strip (same tile styling, same width-resize handle, same top-down camera; the main 3D stays visible beside it) and the only correspondence-picking surface. Tile clicks (drag-free pointer-up within ~4 px; drags pan, wheel zooms to the cursor) raycast **that tile's mesh alone** server-side — the tile is the attribution (tile A ⇒ point A / anchor on A), which is what fixes placement on co-located pairs: nearest-hit attribution in one shared view can never reach the occluded mesh. `Sg.OnTap` stays banned in these controls (unreliable — the documented gotcha); Dom events + server raycasts only.
- The **Setup survey tiles**: a width-resizable right-edge strip (drag handle, pure-DOM chrome), one top-down thumbnail per mesh (small multiples) — identity chip, ★, the explicit ☆ Set-reference button (still the ONLY root-change path), double-click = fly the main camera to the sensor. Tiles honour the per-mesh survey heatmap switches and carry the shared **top-down ORTHOGRAPHIC 2D camera only** (`Model.TileCams` + `SetTileCam`: XY drag-pan anchored at the drag start, wheel zoom TO the cursor; `Radius` drives the ortho half-width via tan 30° so pre-ortho cameras kept their framing; the eye rides `Radius + scene-Z-extent` above the centre plane so nothing near-clips at any zoom; reset on dataset switch; the Pin tiles read the same map) — no orbit, no selection emitters; not a return of the v13 selection-framed focus panel.
- Both share `MeshView.buildPaneScene` (the shipped `MeshShader.shade` with inspection modes off, survey heatmap live). Panes pass `Some (other, cov0, cov1)` — a per-pane coverage MRT from the pane camera (`OutlineView.coverageOffscreen`) feeding the isolate-overlap gate while a placement is armed (solid only where BOTH pair channels cover the pixel; beyond the 8-channel cap the gate disengages outright). Tiles pass `None` — the Coverage sampler slots are then fed the checkerboard default texture (the slot must be bound even though the gate never reads it), converted `IBackendTexture → ITexture` through `AVal.map` (aval is invariant — the aval itself cannot upcast).
- Both overlays hide via **`visibility`, never `display:none`** (a collapsed render control loses its viewport) and gate their scenes with `Sg.Active` on the focus level, so hidden views cost ~nothing per frame. The pane pair rebuilds per selected pair; the tile strip mounts once per dataset.

### Misc

- Debounce/generation state (CTS + counters) lives at module level in `UpdateHelpers`/`ScanPinUpdate`, **not** in the Elm model.
- The server is stateless w.r.t. the registration tree — every query carries explicit transforms.

## Render pipeline (single forward pass)

One forward pass into the main framebuffer — `passZero`: meshes (custom α + α-gated depth) then pin geometry (`DepthTest.LessOrEqual`, blended); `passOne`: coordinate cross, labels, overlay lines, dark duplex copies (`DepthTest.None`, always on top); `passTwo`: the duplex **white cores**, so they deterministically layer over their dark copies (within-pass order is arbitrary). Offscreen consumers: the image-space outline pass and the footprint coverage MRT (plus one coverage MRT per Pin pane, from that pane's camera). The secondary views (Pin panes, Setup tiles) are their own render controls — see **Secondary views** above.

Contracts the rest of the stack relies on (`MeshShader.shade`):

- **α-gated depth**: fragments with α ≥ 0.99 write their natural window depth; ghost/outside fragments write 1.0 (far) — so ghosts never occlude anything and pixel picks fall straight through them.
- **Ghost colour is uniform**: ghost fragments always use the solid per-mesh palette colour regardless of rendering mode, so a ghost silhouette reads as one shape.
- Solid/ghost/invisible is decided per fragment from `MeshActive` × the global ghost floor (`GhostSilhouette`/`GhostOpacity`; floor off ⇒ ghost fragments are *discarded*, i.e. hidden not translucent) × the pin-isolation blob mask (`Blobs` uniform array, hard cap 32, metric → render at upload) × the matrix-hover overlap gate (`OverlapPreview`: solid only where the footprint coverage MRT covers the pixel in BOTH hovered-pair channels — a screen-space test along the camera ray, sampled at `gl_FragCoord`/`ViewportSize`). The scalar-field painters (difference/heatmaps) only touch **above-ghost** fragments.
- **Near-plane cut**: `NearCutFrac` (top-bar ▤ Cut slider) discards fragments in front of a camera-forward plane and paints a flat-ink intersection band; the outline G-buffer applies the same cut so silhouettes follow it.

### One in-cell error range (never per-mesh normalization)

- The difference map, chart and legend all read the ONE `MeshView.cellRange` scale (pin samples, else the distance distribution — see State rules) — signed, spans 0, capped ±0.5 m. Shader uniforms: `DistScale` = hi, `DistLoNeg` = |lo|. Sentinel (no-Z-overlap) fragments paint PALE GREY — grey = no-data, the near-white centre stays reserved for "difference ≈ 0".
- The diverging map is **asymmetric piecewise Coolwarm** (Colorcet CET-D01 — a deliberate user choice over the earlier yellow-centred map): zero = the ramp's near-white centre #EDE7EA (welded to 0 — grey is reserved for "no data", never "0"), each sign runs zero→mid→saturated end (lavender→blue #2151DB / salmon→red #C00206) normalized by its own end with the t^0.6 near-zero boost (`Primitives.Diff.colorSignedV3`, mirrored in `MeshShaders` — keep the two in sync). Values outside clamp to the end colours.
- Difference maps carry **value isolines**: dark derivative-antialiased contours every `Diff.isoStep` metres (a nice 1/2/5 step ≈ span/8 of the shared range, so 0 is always a contour), suppressed where the colour clamps; step is passed as a uniform, not model state. `ddx`/`ddy` exist in FShade, `fwidth` does NOT.
- Intrinsic heatmaps (per mesh, Setup survey rows): **Range** normalizes by the ONE all-mesh end `MeshView.rangeMaxWorld`; **Incidence** uses the geometric (screen-derivative) normal sign-oriented by the stored vertex normal, clamped at 0 — never `abs` (away-facing = never scanned = worst); **Shape** discards fragments below the global `ShapeThreshold` slider.
- The bottom-centre legend (`GuiOverlays.colorLegend`) has exactly two states: the in-cell difference map (wins while the cell map paints) else the Range heatmap while any mesh has it active; hidden otherwise.

### The cell chart

ONE canvas diagram (`GuiRail.chartJs` + `chartData`): the MOV mesh's error across the pair's pins, pin-source-stacked 48-bin histogram (achromatic pin ramp, per-pin median ticks, pooled-LoD band, mm axis), full furniture always — a pinless cell renders the same furniture with a placeholder, never a blank panel. Registered pairs overlay the edge-before histogram as a near-black step outline ("fill now · line before"). x-drag brush + hover cross-highlight ride a shared JS bridge; re-render on resize via ResizeObserver. The chart carries no selection UI.

### `Sg.DepthMask` is forbidden

Buggy in this Aardvark/Aardworx WebGL build — it silently breaks the depth pipeline. Steer ordering with `Sg.DepthTest` + `Sg.Pass` alone. Lines, pin geometry and text therefore all write depth; that violates the textbook "translucent shouldn't write depth" rule but is the only combination that renders correctly in this stack. Leave the in-code reminders in `LineShader.fs` / `SceneGraph.fs`.

### Image-space outline pass (`OutlineView.fs`)

MRT G-buffer (world-Z band parity + window depth → target0, palette colour + coverage → target1) → fullscreen edge-detect painting silhouettes + elevation isolines. Non-obvious choices — keep them:

- The depth edge is the **second difference** (Laplacian) `|l + r − 2c|`, *not* a first difference: window depth is linear in screen space across any planar primitive, so the Laplacian is ~0 on a smooth slope at any view angle and spikes only at a genuine break. A first difference measures screen-space depth *slope* and lights up every grazing surface as false bands.
- `OutlineWidthPx` (gear slider) widens lines by sampling the depth break at ±width texels while the parity edge stays ±1 — silhouettes thicken, isolines don't.
- target0 is **`Rgba8`**; the window depth is packed hi (.w) / lo (.z) as 16-bit fixed point. The edge detect reads the HI byte alone — 256 levels (1 LSB ≈ 0.004), so an `OutlineThreshold` below that quantization floor makes the staircase risers of a smooth slope read as false bands.
- The isoline signal is band **parity** (`floor(wp.Z / ContourSpacing) mod 2`) — a step function, so its edge is a plain first difference; because the band index is a pure function of world Z, the contours stay welded to fixed world-Z planes and do not crawl as the camera orbits.
- Per-mesh FOOTPRINT contours come from a second, occlusion-free pass: an additive coverage MRT (one channel per mesh, 2×Rgba8, NO depth — cap 8 meshes) + the `OutlineCoverageEdge` composite, which outlines each channel's covered↔uncovered transition in that mesh's palette colour. The depth-tested combined G-buffer can only ever outline the visible **union** (no depth break where one mesh ends over a co-located one; hidden boundaries aren't in the buffer at all) — never remove the coverage pass in favour of it. The main-view MRT renders ONCE per frame (`OutlineView.coverageOffscreen`, shared through SceneGraph) — the footprint composite *and* the forward mesh shader's matrix-hover overlap preview both sample the same textures; each Pin pane renders its own from the pane camera for the armed-placement gate. A pair mesh beyond the 8-channel cap disables the preview/gate outright (`MeshView.overlapPreviewUniforms` / `buildPaneScene`) rather than half-testing.
- The G-buffer writes a mesh id (`MeshId` = (index+1)/255, target0.y) and the edge composite gates lines through the `OutlineMask` slot array — currently all-on, but the slot machinery is the hook for any future per-mesh gating.
- `ContourSpacing` is **camera-adaptive**: ~24 contours across the view derived from the orbit radius, SNAPPED to a nice 1/2/5 world-metre step (discrete ticks — zooming out thins the lines stepwise, orbiting never changes them). The gear's `IsolineBands` sets the densest allowed spacing; the far end caps at ≥4 contours over the scene Z range. The difference-map value isolines need no camera term — their in-shader derivative fade already suppresses overcrowding.

### Picking

- `Sg.OnTap` / `OnDoubleTap` / `OnLongPress` **fire on background misses too** — every handler that builds state from the hit must gate: `if e.Location.Depth < 0.9999 then Some e.WorldPosition else None` (background leaves depth at the clear value 1.0).
- Ghost fragments leave depth at 1.0, so the GPU pixel pick cannot land on them. Anything that needs a 3D point on a possibly-ghosted surface keeps the GPU pick as the fast path and falls back to a server raycast (`View.resolvePick` / `raycastNearest*`; the Pin panes raycast their single mesh directly in `GuiPanes`) — un-apply the mesh's displayed pose before the query, re-apply it to the hit. Hover-driven raycasts must be throttled (~60–80 ms) + generation-guarded.
- **Every node without `Sg.NoEvents` writes the GPU pick buffer** (id + `gl_FragCoord.z`, blending forced off there — screen alpha is irrelevant, `DepthTest.None` wins unconditionally). Overlay/composite geometry — especially fullscreen quads like the outline composite — must set `Sg.NoEvents` or it hijacks every pick with its own depth.

## Coordinate systems & transform hierarchy

Three spaces, two transforms. Keep them strictly separate — every boundary crossing goes through a named helper, never bare `* scale` / `± centroid` arithmetic.

**Spaces**

- **Mesh / server frame**: the mesh's stored OBJ coordinates `+ meshCentroid`. **Every `/api/query/*` coordinate — in and out — is in this frame**; the server subtracts the centroid itself.
- **Metric world**: the app's single canonical world (metres). Pin centres/radii/points-as-world, cursor world, graph edge transforms all live here. Metric world ≡ a mesh's server frame exactly at the load pose.
- **Render space**: what the GPU and cameras use — centroid-recentred, dataset-scaled, then posed.

**Two transforms — dataset first, then workspace:**

1. **Dataset transform** — a *similarity* (uniform scale + translation, never rotation), fixed per dataset. The **only** place `DatasetScale` and `CommonCentroid` enter. Cross it with `ScanPin.renderCentre`/`worldCentre` (points) and `ScanPin.renderLength` (lengths).
2. **Workspace transform** — a *rigid* per-mesh pose composed from the registration graph (else the as-loaded baseline). Render form = `ModelTransforms.displayedRender` / `MeshView.displayedMeshT` (the latter also peek-aware); metric-world form = `ModelTransforms.displayedWorld` / `MeshView.displayedWorldAt`. `RigidTransform.worldToRender`/`renderToWorld` conjugate a rigid pose between the two (the dataset similarity is the conjugator). **`displayedWorld.Backward` maps metric world → the mesh's server frame; `.Forward` maps back.**

**Discipline rules**

- Server queries: convert metric world in with `displayedWorld.Backward`, map results out with `.Forward`. Pair queries (`pair-error`, `pair-overlap`, `region-distance`, `lsq-pairs`) instead pass each mesh's world transform explicitly and let the server place them.
- Scene-graph geometry is render space: convert model values at the boundary (`renderCentre`/`renderLength`, or `worldToRender` for poses).
- Directions need no scale handling (uniform scale ⇒ parallel); only the workspace rotation matters (`TransformDir`).
- Pin geometry (`CentreLocal`, `PointA`, `PointB`) is stored in its mesh's **own frame** (`displayedWorld.Backward world` at pick time) — pose-independent, moves with the mesh for free.

## Panorama centre (`pano-centers.txt`)

Each mesh's OBJ origin is *supposed* to be its scan camera, but the data is often not centred on it — so the sensor position is data-driven: one optional file per dataset, `data/{dataset}/pano-centers.txt`, lines `<mesh-folder> x y z` in **absolute world coords** (same frame as `*centroid.txt`); unlisted meshes fall back to the mesh origin. Served at `/api/datasets/{d}/pano-centers`, held in `Model.PanoCenters`. Consumers: the sensor origin for the incidence/range heatmaps, the Setup rows' fly-to-sensor jump (`FlyToSensor`), the dataset-load camera framing, and the coordinate-cross position. To add centres: read the top-bar **world** coordinate at a mesh's visual centre, write a line — no code change.

## Adaptive performance (critical)

In the scene graph, **never depend on an entire record when you only need a subset of its fields**. The Elm-style model replaces whole records on every update, so an `AVal.map` over a full `ScanPin` (or `Model`) fires on *any* field change.

**Rule: project individual fields into separate `aval`s early, then build the dependency graph from those.**

```fsharp
// BAD — rebuilds on ANY pin change (rings result, radius, …)
let geo = pinVal |> AVal.map (fun po -> ... po.ContactRings ... po.InnerRadius ...)
// GOOD — only when the rings or radius actually change
let ringsVal  = pinVal |> AVal.map (Option.map (fun p -> p.ContactRings))
let radiusVal = pinVal |> AVal.map (Option.map (fun p -> p.InnerRadius))
let geo = (ringsVal, radiusVal) ||> AVal.map2 (fun rings r -> ...)
```

For scene-graph nodes (`Sg.Text`, `sg { ... }`) this matters even more: rebuilding an `AList` of sg nodes destroys and recreates GPU resources (font atlases, draw calls). Therefore:

- **Split structure from placement.** Build static sg node lists from slowly-changing data; use adaptive `Sg.Trafo` for fast-changing placement (uniform update, no rebuild).
- **Push adaptivity down.** A parent `AList.ofAVal` that rebuilds all children is expensive; an `AVal`-driven `Sg.Trafo` per stable child is cheap.
- **Don't build in-place-updating *lists* with `AVal.map (… IndexList.ofList) |> AList.ofAVal`.** That mints fresh `Index` keys every recompute, so any element change diffs as remove-all + add-all — churning every row's DOM/GPU resources and intermittently double-rendering in the reconciler. Derive a stable-identity incremental list instead: `AMap.map (project to just the row's inputs) |> AMap.toASet |> ASet.sortBy key |> AList.map row`. Small, rarely-changing lists may use the simple form.
- **Never create a *transient* `aval` inside another aval's compute and read it.** `AVal.custom (fun t -> … (makeSomeAval args).GetValue t …)` (or `AVal.force` of a freshly built aval) can drop the dependency edge, so the outer aval **evaluates once and silently freezes** (historically: a panel rendered blank because it never saw the meshes load). Fix: **inline** the inner computation so its `model.X.GetValue t` reads hit the outer token directly, or bind the inner aval **once** outside the compute (a stable `let`). Reading a *stable* aval via `.GetValue t` is correct — only per-eval-built avals are the trap.

Sanctioned exception: the 3D pin **flags** are screen-constant — every element derives from the ONE per-pin `ScanPin.flagHeightRender` (fixed fraction of the eye distance, clamped to 0.1–20 m metric world, × the gear's `FlagScale`), so their line/trafo avals read `view` and recompute per camera move on purpose (a handful of pins; the label is `Sg.Trafo`-only). Only the name billboards (Z-yaw to the eye).

## Server query performance

Costly spatial queries (`pair-error`, `contact-rings`, `region-distance`) scale with mesh size × sample density:

- **Never issue per-pair/per-pin requests sequentially** — batch into one request where the endpoint supports it (`pair-error` takes all pins of a pair); fan independent fetches out with `Async.Parallel` (the pair-overlap sweep).
- **Parallelise the heavy server inner loop** when inputs are independent — Embree `Scene.Intersect` is thread-safe (`Parallel.For` in the handlers).
- **Cap density rather than grow linearly** (`maxPointsPerMesh` / sample strides / per-pin sample caps).
- **Debounce user-driven triggers** with a `CancellationTokenSource` + generation counter so the next event cancels the previous and at most one fetch is in flight per invalidation.
- **Mesh caches are warmed at dataset load** by `bboxesHandler`, so the first interactive query never pays the lazy-load cost.

## Client compile order (`Superprojekt.fsproj`)

```
MeshData.fs            mesh fetch/parse, ApiConfig, shared Http.client
Query.fs               server query wrappers (Async)
CameraModel.fs / .g.fs OrbitState [<ModelType>]
OrbitController.fs     orbit camera + messages (project file, NOT the Aardvark library one)
RegistrationModel.fs   ScanPinId, RegGraph/RegEdge, PairCell, MatrixNav, ErrorRange (WASM-free, shared with Supertests)
ScanPinModel.fs / .g.fs ScanPin + placement state
PinGeometry.fs         icosphere, sphere outline
Model.fs / .g.fs       [<ModelType>] Model + FocusLevel/FocusSelection + MeshVisibility + ModelTransforms
LineShader.fs          flat colour + pixel-constant 3D lines, LineGlyphs
Primitives.fs          widgets, showWhen, observedRender, palettes, Diff colormap, friendly names
Messages.fs            Message DU
ScanPinUpdate.fs       pin transaction sub-reducer + contact-rings postlude
UpdateHelpers.fs       reducer helpers + debounce/generation state
Update.fs              main reducer + cell-error/cell-dist/pair-overlap postludes
MeshShaders.fs         RenderPass + MeshShader + outline/coverage shaders
MeshView.fs            LoadedMesh, buildScene, displayed transforms (peek-aware), pin blobs, offscreen nodes
OutlineView.fs         offscreen image-space outline/coverage passes
ScanPinScene.fs        pin sg nodes: markers, rings, flags, brushed samples
SceneGraph.fs          scene composition + cross + labels + root outline
GuiPanes.fs            secondary views: Pin panes (pick surface) + Setup survey tiles
GuiTopBar.fs           top bar (Cut slider, world coord, gear popover)
GuiOverlays.fs         toast, scale bar, colour legend, orientation indicator, loop modal
GuiRail.fs             left navigator: focus rail + per-level views (survey rows, matrix, pair workspace, pin controls)
View.fs                view function, pick routing, Esc/peek keys + App module
ShaderCache.fs / Program.fs
```

## Server compile order (`Superserver.fsproj`)

```
MeshLoader.fs        OBJ parse, centroid file, atlas paths
MeshCache.fs         Embree scene + BbTree cache (lazy, permanent)
MeshAnalysisCore.fs  pure level-set tracer + decimate (WASM/Embree-free, shared with Supertests)
MeshAnalysis.fs      sphere contact-ring tracing
PairError.fs         pairwise symmetric M3C2-style error (pin batches, at-point, overlap)
RegMath.fs           weighted Umeyama rigid landmark solve (Jacobi SVD, conditioning)
QueryHandlers.fs     HTTP query handlers
Handlers.fs          routing
Program.fs           ASP.NET startup
```

## API endpoints

```
GET  /api/datasets                              → string[]
GET  /api/datasets/default                      → string (data/default.txt, fallback = first)
GET  /api/datasets/{dataset}/centroids          → { meshName: [x,y,z] }
GET  /api/datasets/{dataset}/pano-centers       → { meshName: [x,y,z] }   (absent file → {})
GET  /api/datasets/{dataset}/bboxes             → { meshName: { min, max } }   (warms the mesh cache)
GET  /api/datasets/{dataset}/mesh/{name}/{i}    → binary mesh
GET  /api/datasets/{dataset}/mesh/{name}/{i}/atlas → JPEG
POST /api/query/ray                             → { hit, t, point, triangleId }   Name = "dataset/mesh"
POST /api/query/closest                         → { found, point, distanceSquared, triangleId }
POST /api/query/contact-rings                   → sphere–surface intersection polylines
POST /api/query/lsq-pairs                       → weighted rigid solve (absolute world transform + residuals + conditioning; 400 on <3 pairs)
POST /api/query/pair-error                      → per-pin pooled symmetric error of mesh B rel A at explicit poses (median, LoD half-width, samples + positions; per-pin ok=false on no overlap — the batch never fails on it)
POST /api/query/pair-error-at                   → exact signed value at one picked point (1 mm ray back-off so on-surface picks register)
POST /api/query/pair-overlap                    → registerability of a pair at supplied poses (two-way closest-point coverage fractions)
POST /api/query/region-distance                 → per-vertex signed M3C2 distance of a target mesh to a reference mesh, in the target's served vertex order; a vertex responds only where the vertical world line through it pierces the reference (Z-overlap), else 1e30 sentinel — so error is never fabricated in non-overlap fringes
```

All query coordinates are **absolute world space**; the server computes `localPos = worldPos − meshCentroid`; pair queries carry explicit per-mesh transforms. Endpoints without consumers get deleted — don't re-add one without a consumer.

## Tests

`src/Supertests` — console runner (no test packages) compiling `RegistrationModel.fs` + `RegMath.fs` + `MeshAnalysisCore.fs` directly: `dotnet run --project src/Supertests`. The registration-graph invariants (tree, reroot, cascade, loop resolution, composition) live here — extend them when touching `RegGraph`. Integration against a running server (covers lsq-pairs, pair-error, pair-error-at, pair-overlap, region-distance): `ASPNETCORE_URLS=http://localhost:8002 dotnet run --project src/Superserver`, then `node tools/integration.mjs`.

## F# pitfalls (learned the hard way)

- **Deleting a DU case turns its remaining patterns into catch-alls.** `function DeletedCase -> true | _ -> false` still compiles — F# reparses the name as a *variable pattern* that matches everything (only warning FS0049 betrays it), silently inverting the logic. After removing a case, grep for its name AND check the build for FS0049/FS0025/FS0026 before trusting green.
- **`Trafo3d` is a struct** — `obj.ReferenceEquals` on boxed values is always false. Memoization/identity tests must use value equality (or prove non-recompute via sentinel poisoning), never reference identity.
- **Aardvark `a * b` composes apply-a-first** — the opposite of textbook matrix notation. The graph composition `edge.Transform * parentPose` and every conjugation in `RigidTransform` depend on this; sanity-check any new composition with a translation-only case.
- `Error`/`Ok` are shadowed by Aardvark.Base — qualify `Result.Ok`/`Result.Error` in client code.

## Aardvark.Dom gotchas

- `Attribute("for", "...")` on `<label>` is silently dropped — nest `<input>` inside `<label>`.
- `Attribute("checked", "")` is dropped — use `Attribute("checked", "checked")`.
- CSS `~` sibling combinator breaks (Aardvark inserts wrapper nodes) — use `:has()` on a known ancestor.
- `RenderControlInfo` and `TraversalState` both have `.Runtime` — annotate `(info : Aardvark.Dom.RenderControlInfo)` when ambiguous.
- `yield!` is not supported in Aardvark.Dom CE builders — use OnBoot JS with MutationObserver for dynamic SVG/canvas (the `observedRender` helper). CE ambiguity errors (FS0792) around `AList`/`aval` expressions in `div { }` are fixed by binding the expression to a `let` first.
- **OnBoot may run before a *later-sibling* node is mounted.** Don't capture a following sibling at boot (`querySelector` stored in a closure freezes `null` forever) — look siblings up **lazily** inside the handler that needs them. Boot-time capture is only safe for **ancestors** (`closest(...)`).
- `renderControl { ... }` nests fine inside `div { ... }`, but **`Sg.OnTap` (and other Sg pointer events) do NOT fire reliably in secondary render controls** — use Dom pointer handlers + a server raycast if one is ever added again.
- `AVal.map4` does not exist — combine with `AVal.map2`/`AVal.map3`.
- `Dom.Style` for renderControl; `Style` for HTML elements. `Css.Custom` does not exist — use CSS classes in `style.css`.
- **`RenderControl.ViewportSize` is framebuffer pixels**; `RenderControl.ClientSize` is CSS pixels. Anything mixing with DOM coordinates (overlay placement, cursor → NDC) must work in CSS px or it breaks on hi-dpi. ClientSize is `V2i.II` until the first DOM event; the main control binds ClientSize with a ViewportSize fallback.
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
- Conditional visibility uses `Primitives.showWhen` → `.hidden` (`display: none !important`), not inline display styles.
- Gold root markers use the `--ref-gold`/`--ref-gold-dark`/`--ref-gold-pale` tokens — never a literal gold hex.
- Toggle-active states: `.tb-btn-active` (top bar), `.rail-btn-active` (navigator), `.cbb-btn-active` (compact button bars) — darker blue, inset shadow.
