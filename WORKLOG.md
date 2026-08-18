# Worklog

> **Standing rules for this rework (2026-07-27):** documentation files
> (CLAUDE.md, README.md) are FROZEN until the entire rework is finished —
> do not edit them per-change. Log every change here as it lands; at the
> end, reconstruct the net documentation updates from this file.
> *(A1–A3 amendment instance completed 2026-07-28 — docs reconstructed.)*
> *(A4–A7 amendment instance completed 2026-07-29 — docs reconstructed.)*
> *(A8–A10 amendment instance completed 2026-07-30 — docs reconstructed.)*
> *(A11 amendment instance completed 2026-07-30 — docs reconstructed.)*
> *(A12–A13 amendment instance completed 2026-07-30 — docs reconstructed.)*

## User-study mode (2026-08-18, DONE)

`/study` at the end of the URL launches the app in user-study mode
(`Model.Study : StudyPhase = StudyOff | StudyWarmup | StudyLive`; constants in
`StudyMode`: warm-up = `ScanPin - UserStory` (initially DemoHaus, changed same
day), study = `study`).

- **Boot**: `ApiConfig.studyUrl` detects the path suffix; `apiBase` strips
  `/study` like the `/s/` virtual route (the server's `MapFallbackToFile`
  already serves index.html there — zero server changes).
  `ServerActions.init` emits `SetStudyPhase StudyWarmup` BEFORE the dataset
  fetches (no debug-strength flash) and auto-loads `DemoHaus` instead of the
  server default (graceful fallback if absent).
- **Top bar** (`GuiTopBar`, inside `.tb-right`): the `.tb-study` banner —
  "Warm-up mode" + the accent-blue "Start User Study" button (toast if the
  study dataset is missing). Banner is accent-blue tinted, NOT gold/amber —
  gold fill is the reference's colour.
- **Start-confirm modal** (follow-up same day): the button raises a blocking
  confirm (`Model.StudyStartPending`, `GuiOverlays.studyStartModal` on the
  loop-modal chrome) instead of switching directly; its confirm emits
  `[SetStudyStartPending false; SetActiveDataset "study"; SetStudyPhase
  StudyLive]` + `loadDataset`, after which the text reads "Study mode" and
  the button is gone. Cancel/Esc = stay in warm-up; slotted into the ONE Esc
  chain after the pair-connect pre-warning.
- **Faint debug chrome**: `classWhen "study-on"` on `.tb-right`; CSS faints
  the ▦/⚙/≣ tiny buttons to light-grey-on-white in both phases. They stay
  clickable — the gear menu's dataset switch is the exit path.
- **Exit rule**: the `SetActiveDataset` reducer sets `Study = StudyOff`
  whenever a PREVIOUS dataset existed (`model.ActiveDataset.IsSome`) — boot's
  first load is exempt, and the start button's own switch re-enters via the
  `SetStudyPhase` riding behind it in the same emit batch.
- Adaptify ran (Model.g.fs +5); build 0 errors / 52 pre-existing warnings.
- **Verified in-browser** (playwright vs `localhost:8002/study`): warm-up
  banner + button render, debug buttons faint, DemoHaus (not the server
  default "study") auto-loads; Start flips the banner, removes the button,
  streams the study dataset; a gear-menu switch to Hessigheim drops the
  banner and restores the chrome; zero console errors. Screenshots in the
  session scratchpad (`study-1-warmup/2-live/3-exited.png`).

## F9 — aiming & linking legibility (2026-08-17, DONE)

Spec: `ScanPin_v14_F9_aiming_linking_legibility.md` (final-release test-session
fixes). Governing rule adopted: gold FILL = reference identity, gold
OUTLINE/GLOW = transient hover/focus — highlighting is never a fill.

- **T1 pin hover → strong top-down link**: `ScanPinScene.addHighlightRing`
  recoloured white→gold (`Primitives.refGoldV3d`) — flows into the main-3D
  loud highlight AND the tile highlight ring; the highlightNode label box goes
  gold too. NEW `GuiPanes.addEdgeArrow` (off-frame link arrow: target outside
  0.88 × the tile's ortho half-extents → ink-under-gold chevron clamped to the
  frame border, aimed at the target, ~14 px via `unitsPerPx`, Z = the cam
  centre plane) + a per-tile arrow segs node (passTwo, DepthTest.None) whose
  T1 target is the `highlightPin` subject's centre (hovered row > focused
  pin). Composes with the auto-refocus: on-frame = the gold ring, panned-away
  = the arrow.
- **T2 armed correspondence pick gates the tiles**: `.tile-pick-off` on the
  non-armed pair tile while `ArmPoint` is armed (`pointer-events:none` +
  `::after` veil rgba(15,23,42,.45) — the A9 scrim scoped to the strip, weaker
  than A9's .5 so the T3 sibling reads through), `.tile-pick-on` on the armed
  mesh's tile (white outline + accent glow, the A9 lit vocabulary).
  Centre/probe picks accept both meshes and gate nothing.
- **T3 sibling through the dim**: NEW `ScanPinScene.armedSiblingAt` — while
  ArmPoint m is armed, the OTHER pair mesh's already-placed point of the pin
  being edited (draft, else selected pin). Exempt from the armed ×0.15 fade
  AND the mesh-solid muting (arming isolates the armed mesh, which would mute
  the sibling — exactly the mark that must stay): full-strength crosshair +
  gold halo in the main 3D (`addGoldHaloC`, GlyphLines CAM unit-circle ring)
  and in its tile (world-space ink+gold rings at 1.25·h in crossSegs); the
  T1 edge arrow also aims at it when off the tile's frame.
- **T4 thicker correspondence glyph + knob**: crosshair rim 5.4→7.2 (ink 3.4 /
  core 1.7 kept), reveal ground lines 1.4→2.2; ALL crosshair strokes and the
  reveal width scale by NEW `Model.MarkerWeight` (init 1.0, clamp 0.5–3.0,
  `SetMarkerWeight`, adaptify rerun) — gear "Debug & settings" slider "Marker
  line weight" (0.5–3.0 ×, beside the reveal radius). `addCrosshairGlyph`/`C`
  and `addRevealLines` gained the weight param; callers (main crosshairNode,
  revealSegs, tile crossSegs, tile reveal) read the model inside their segs
  AVals.
- **T5 matrix hover → connected scanpins**: NEW `ScanPinScene.matrixLinkNode`
  (main 3D, linesNodeTop): at FocusMatrix, a hovered cell (`MatrixHoverPair`,
  key-normalized) gold-rings the pins of that pair; a hovered mesh subject
  (`TileIsolateHover` — tree node / strip tile) gold-rings every pin on any
  pair touching that mesh. Hover transients clear it by themselves; no state.
- Docs updated in place (CLAUDE.md): colour-family rule (gold fill vs gold
  outline), triplex crosshair + weight knob, arming bullet (tile gating +
  sibling exemption), matrix-hover pin link, tile pin-row link + edge arrow.
- Checklist: T1 ✔ (ScanPinScene.fs addHighlightRing/highlightNode ·
  GuiPanes.fs addEdgeArrow + arrow node) · T2 ✔ (GuiPanes.fs meshTile classes ·
  style.css .tile-pick-off/.tile-pick-on) · T3 ✔ (ScanPinScene.fs
  armedSiblingAt/addGoldHaloC/crosshairNode · GuiPanes.fs crossSegs +
  arrowSegs) · T4 ✔ (Model.fs MarkerWeight · Messages/Update · GuiTopBar
  slider · ScanPinScene glyph builders) · T5 ✔ (ScanPinScene.fs
  matrixLinkNode).
- Build green after every task (0 errors, 52 pre-existing warnings); no shader
  code touched (widths/colours are per-segment data), so no FShade→ESSL3 risk.
  OWED: the interactive in-browser pass (hover/arm flows need a human).

## Test-session feedback round (2026-08-17)

Five small fixes from a user session:

- **Histogram y-axis pinned across the pose peek** — confirmed real: the JS
  count→height scale (`maxC`) was derived from the payload's `h`/`hb` arrays, and
  while peeked the fill IS the before distribution, so the after state's counts
  left the payload entirely and the axis rescaled on the flip (visible whenever
  the post-registration spike tops the before histogram — i.e. almost always).
  Both chart bodies now ship `ymax` = max stacked-bin count over BOTH states
  (pair body respects the Pin-level narrowing; graph body reads the after cache
  `GraphError` explicitly since `inspectBlocksAt` already returns the peeked
  side), and `chartJs` pins `maxC` to it. Unpeeked rendering is unchanged (the
  old max over h+hb equals `ymax` there).
- **Distribution-average ticks removed** — the per-pin median ticks (pair body)
  and the pooled median tick (graph body), the short strokes rising from the
  axis: relics of an older iteration. JS drawing block dropped, `med` removed
  from both payloads (`medOf` computations gone with it).
- **Matrix + registration tree scale with the sidebar** (aspect-preserving,
  scale-up only — below natural size both keep their scroll behaviour): the
  tree's SVG now sizes via its viewBox (`width:100%`, `min-width` = natural,
  `height:auto`); the pair matrix gets a boot-time fit via CSS `zoom` (scales
  layout AND hit-testing, unlike `transform`) driven by a ResizeObserver on the
  panel body, with `.pmx { width: fit-content }` so the natural width is
  measurable (reset-measure-set keeps it idempotent).
- **Right-click on the histogram clears the brush** — `contextmenu` handler
  (preventDefault + empty emit); left-button gate added to `pointerdown` so a
  right-click never starts a drag.
- **Centre/radius edits no longer unregister the pair** — the edge-drop match in
  Update.fs narrowed from {`SetInnerRadius`, `EditPointAt`, `EditCentreAt`,
  `DeletePin`} to {`EditPointAt`, `DeletePin`}: a solve's inputs are exactly the
  correspondence point pairs (centre/radius only scope analysis, `lsq-pairs`
  never sees them). Centre/radius edits still `invalidateCellError` (the ROI
  moved) via the untouched preceding match; the centre-pick tooltip's
  "unregisters the pair" corrected. CLAUDE.md pin-edit rule + chart bullets
  updated.

## Study preparation (2026-08-17)

- **Study dataset bbox files fixed** (`src/Superserver/data/study/*/model_bbox.txt`) —
  meshA/C/D carried the *source scans'* uncropped world bboxes (meshA byte-identical
  to Job_0789's, meshC ~2.3 km wide); regenerated all four as
  `minX minY minZ maxX maxY maxZ` absolute world = OBJ vertex bounds + centroid
  (meshB agreed with the recompute to ~1 µm, confirming the convention). Nothing in
  the app reads these files (the server recomputes from the parsed mesh) — the fix
  is for external tooling only.
- **Sensor ▾ button removed** (never worked properly; study GUI must be clean):
  the top-bar jump-to-sensor dropdown (GuiTopBar.fs block + `.tb-sensor*` /
  `.tb-menu-left` CSS), `ToggleSensorMenu` + `FlyToSensor` messages, the
  `SensorMenuOpen` model flag (adaptify rerun), their reducer cases, and the
  arm-quasi-mode popover-close site. KEPT: the measured-stations layer
  (`*sensor.txt` → `/sensors` → `DatasetSensors` → `ModelTransforms.sensorWorld`) —
  still consumed by the dataset-load camera framing — and the heatmap sensor
  origin (the posed mesh origin, independent of the button). CLAUDE.md sensor
  section updated (it also predated the measured-stations layer; `/sensors` added
  to the endpoint list).
- **`global.json` SDK pin reverted to `8.0.0`** — the study-dataset commit bumped
  it to `8.0.424` (that machine's SDK); with `rollForward: latestFeature` the
  `8.0.0` floor resolves on every machine, the exact pin only where 8.0.424+ is
  installed (roll-forward never selects a lower patch — dotnet was dead on this
  machine repo-wide).

## Performance improvements (2026-08-17)

Session-degradation audit implemented per `performance-audit-spec.md` (WP-1…WP-12;
the spec file is the transient working doc — delete at review). Symptom: input
latency grew over a session while the renderer held vsync. Root causes: per-event
work scaling with session content (pins/edges/samples), unbounded accumulation
(mesh cache, ReachLog rendering, CTS dicts, JsonDocument rentals), and
`AList.ofAVal` churn tearing down live DOM/GPU state.

- **WP-1 `ensurePairOverlaps` (Update.fs)** — single-flight `reqGen` check now
  FIRST (O(1) per message once issued); names via `IndexList.toArray` (the list
  `.[i]` indexing was O(i) inside the n² loop → ~n³/message); NEW dataset-prefix
  guard that returns **without consuming the generation** — fixes the real bug:
  `SetActiveDataset` bumps the gen and clears `PairOverlaps` while `MeshNames`
  still holds the OLD dataset's names, so the same-message postlude issued a
  stale sweep, consumed the gen, and the new dataset's sweep never ran (matrix
  read all-impossible after every switch). Flow now: switch → dsOk false, no
  issue → `CentroidsLoaded` lands the new names → sweep issues. Fan-out capped
  `Async.Parallel(jobs, 6)`. (`PairOverlapComputed` already gen-guards.)
- **WP-2 postlude guards before allocation (Update.fs)** — `ensureCellError` no
  longer builds+sorts the full pin list per message when cached (P grows with
  every pin placed); `ensureGraphError`/`ensureGraphDist` check focus/cache
  before materialising the edge list; `trackSpanned` short-circuits on
  `ReferenceEquals(model0.RegGraph, model.RegGraph)` (record — reference-safe;
  MeshNames only changes in CentroidsLoaded, which replaces RegGraph too).
- **WP-3 chart bridge (GuiRail.fs)** — `chartJs.render()` caches per-attribute:
  `data-chart` re-`JSON.parse`d and `_dots` rebuilt only when the string
  actually changed; `data-brushed` re-split only on change (previously every
  hover-rate render re-parsed the ~30–165 KB payload). Payload: the redundant
  `"g":[0..n-1]` gid arrays (~40 %) replaced by one `"g0"` offset — gids are the
  contiguous canonical block by construction; JS derives `gid = g0+q`.
  `chartData` is now `openA |> AVal.bind`-gated (`inspectBody`/`graphBody` take
  `openA`): a collapsed dock computes and marshals NOTHING.
- **WP-4 list identity (GuiRail.fs, GuiTopBar.fs)** — `inspectPanel` content
  keyed by VALUE through `AMap.ofAVal (HashMap.single key ())`: HashMap delta
  cancellation keeps the chart node (observers, JS brush state, canvas) alive
  across `Sel` replacements with an unchanged pair and Pair⇄Pin jumps; only a
  real key change swaps the subtree. `pairPins` rebuilt as an incremental
  projection (`AMap.filter |> AMap.map (ShortName, CreatedAt)`) so rings/reveal
  landings cancel to empty deltas — one solve no longer tears the pin rows down
  ~18×; `pinRow` takes `(id, shortName)`; `pinCount` = `ASet.count`. ReachLog
  popover label + rows `AVal.bind`-gated on `ReachLogOpen` — a CLOSED popover
  costs zero per logged action (the list itself stays untrimmed by design).
- **WP-5 legend (GuiOverlays.fs)** — percentile crop memoised per
  (mesh, buffer-reference) in a ref cell (the O(V log V) sort ran per mouse-move
  while a crop target was hovered/locked); whole `legendJson` now
  `AVal.bind`-gated on visibility; `hoveredValue` walks the block array without
  `Array.toList`.
- **WP-6 token-helper memos (MeshView.fs)** — `graphMapScopeAt`'s painted set
  memoised on the `RegGraph` instance (was `Set.ofSeq` per call, called per pin
  per marker recompute); `inspectRangeAt`'s pooled-distance range memoised on
  the `GraphDist(Before)` Map instance (was E×V concat + 3 arrays + 2 sorts,
  reached from four avals); `cellPaint`'s Pin-ROI mask memoised on
  (buffer-ref, centre, r²) in a ref cell — rings landings no longer reallocate
  the multi-MB masked array, and the stable reference keeps `distBuf` equal.
- **WP-7 dim → `MarkDim` uniform (LineShader.fs, ScanPinScene.fs)** — `Lines`
  fragment multiplies a `MarkDim` uniform (default 1; `Lines.renderWith`).
  Per-pin rings and reveals split: geometry customs carry dim=1 and depend on
  figure inputs alone; the armed/◎-hover/pin-scope/mesh-solid fades ride
  per-node `dimA` uniforms; `pinShown` moved to `Sg.Active`; the anchorage cue
  is its own Active-gated node (`cueSegsOf`); reveals split per SIDE (each
  follows its own mesh's visibility; hidden ⇒ Active off, not empty geometry).
  Draft area/reveal nodes restructured identically. Hover transients now cost
  uniform updates, never ring/reveal re-tessellation.
- **WP-8 `GlyphLines` (LineShader.fs, ScanPinScene.fs)** — new screen-constant
  line-glyph pipeline (the Discs pattern): buffers hold centre + UNIT offsets;
  `h = clamp(GlyphMinR, GlyphMaxR, GlyphFrac·eyeDist) × GlyphScale` in the
  VERTEX stage; two variants — world-axis offsets (`renderWorld`: jacks via
  `addGlyphBox`, flag pole+ring with per-pin axes baked into offsets) and
  camera-plane offsets (`renderCam` + `GlyphRight/Up`: crosshairs via
  `addCrosshairGlyphC`). Flag sizing = `flagHeightRender` with FlagScale
  factored into `GlyphScale` (frac 0.10, clamp 0.1·ds…20·ds); crosshair frac
  0.025 unclamped. `pinMarkerLines`/`crosshairNode`/`pinFlags` no longer read
  `view` — a camera move re-tessellates NOTHING (was ~85 segs/pin/frame, ×5
  trafo compositions per pin). `hoverRingNode`'s O(all-samples) gid lookup split
  into a view-independent `hoverPosA`; the per-frame part is one 32-seg ring.
- **WP-9 disc slot-alpha + zeroBuf (LineShader.fs, ScanPinScene.fs,
  MeshView.fs)** — `Discs` gains a `DiscSlot` vertex attribute + `DiscAlpha`
  `Arr<N<32>, V4f>` uniform (alpha in .X; slot = display index, the
  OutlineMask/Blobs convention; α<0.004 discards): `brushedDots` geometry
  depends on brush+data alone, `dotAlphas` moves the per-mesh visibility as 32
  floats per hover (was up to 12000×17 vertices re-uploaded per hover
  transient). `distBuf`'s None branch reuses one per-load `zeroBuf` (the pane
  path's shape) instead of allocating a V-sized zero array per cellPaint
  invalidation (every brush-drag hit it for every mesh).
- **WP-10 JSON layer (Query.fs, MeshData.fs)** — `post` takes the parser as a
  callback and `use`-scopes the `JsonDocument` (ArrayPool rentals were dropped
  on every response — multi-MB for region-distance); all 9 query parsers moved
  into callbacks (all fully materialise); `regionDistance` preallocates via
  `GetArrayLength` (no ResizeArray doubling on multi-million-element arrays);
  the four MeshData parse sites now `use` their documents.
- **WP-11 lifecycle hygiene (MeshView.fs, ScanPinUpdate.fs, UpdateHelpers.fs,
  Update.fs)** — `MeshView.meshes`/`pendingFinished`/`displayedTCache` evicted
  on dataset switch (prefix-keyed, inside `loadMeshAsync` — the reducer cannot
  call MeshView by compile order); switching back re-fetches (memory-bounded
  beats cached-forever on a heap that never shrinks). Load-FAILURE path drops
  the placeholder + pending callbacks so later rebuilds retry (was: dead
  closures accumulated per request forever, peeks never gated). Figure-debounce
  CTSs: replaced ones disposed; `DeletePin` drops its three entries; new
  `ScanPinUpdate.resetFigureDebounce()` called from `SetActiveDataset` +
  `ApplyCheckpoint`. Debounce is now tokenless `Task.Delay 250` + an
  `IsCancellationRequested` check — a pose change no longer throws 3P
  `OperationCanceledException`s (expensive on Mono WASM); late-cancel fetches
  land dead on the existing stale guards. `showToast` disposes the replaced CTS.
- **WP-12 stable identity at the adaptive edges (MeshView.fs, View.fs,
  GuiPanes.fs)** — `displayedMeshT` memoised per mesh name (event handlers
  force it per pick/hover; a fresh custom per call registered transient weak
  edges on ~6 cvals each time; ONE AdaptiveModel per app); cache shares the
  mesh-eviction lifecycle. `buildRootCoverageNode` root as
  `HashSet.single |> ASet.ofAVal` — HashSet delta cancellation keeps the
  per-tile nodes alive across every RegGraph change that doesn't move the root
  (was: remove+add per graph change × N tiles). View.fs hover readout reads
  `Pins.Content` (was a fresh `AMap.toAVal` forced per hover-gid change);
  GuiPanes roiFit toast reads `MeshOrder.Content` (was a fresh `AMap.tryFind`).
- **Deliberately unchanged** (see the spec's deferred list): offscreen
  FBO/signature/task disposal (binary-only backend, rare trigger post-WP-11),
  the per-frame `Rendered` message flow, ReachLog data growth, the
  `body:has(.arm-flag.on)` scrim mechanism, View.fs's throttled brush-hover
  scan, per-camera-frame `orientationIndicator`/`treeData` payloads.
- Green: client type-check (`-p:WasmBuildNative=false`) 0 errors, only
  pre-existing FS0044/FShade warnings; Supertests 97/97; integration 32/32 on
  :8002. **In-browser verification** (headless Chromium via puppeteer-core at
  the served app): zero console errors/warnings, all shaders compile ("cache
  written"), full scene + tiles render; drove a complete pin placement through
  the guided flow (centre via tile overlap patch, A/B via tile surfaces) — pin
  minted, flag pole+ring+label (GlyphLines world), crosshair (GlyphLines cam),
  area figure + tile reveal rings, and the Pin-level histogram (g0 payload) all
  render correctly.
- CLAUDE.md updated same-day (user-requested exception to the docs freeze):
  the Adaptive-performance section's sanctioned-exception paragraph corrected
  (jacks/crosshairs/flags now GlyphLines vertex-stage; CPU camera-rebuilds are
  only the hover ring + highlight + label trafos) and a "Performance patterns"
  block added — postlude guard-first, reference-keyed memos, MarkDim/DiscAlpha
  uniforms, GlyphLines, AVal.bind gating of hidden UI, value-keyed single-child
  swaps + delta-cancelling row projections, JS-bridge parse guards, JsonDocument
  disposal, cache/CTS lifecycles.

## Peek-hold feedback + workspace error map flips with the pose peek (2026-08-06)

- **Peek-hold visual feedback** (style.css only): while a peek button is
  held (`.tb-peek.tb-btn-active` — pointer hold or the V/B keys, same
  aval) it gains a light-blue highlight ring and a small "peeking" chip
  attached below the button. The chip is absolutely positioned (out of
  layout), so the hold never shifts the top bar.
- **Pair/Pin false-colour map now flips with the pose peek**, matching
  the Matrix convention (colours always describe the on-screen
  geometry). Previously the workspace map kept the registered-pose
  values while the peek blinked MOV to its as-loaded baseline
  (documented as an accepted approximation — now revoked by the user).
  - New `Model.CellDistBefore : float32[] option`: MOV's per-vertex
    region-distance at the workspace peek's exact geometry — MOV at its
    as-loaded baseline (`ModelTransforms.loadWorld`), REF as displayed.
    NOT the per-edge `composeEdge` convention: the peek drops MOV all
    the way to baseline, so the buffer matches what is rendered.
  - `ensureCellDist` fetches both states in one flight (before only for
    registered pairs — `RegGraph.pairEdge`; the peek is guarded to
    them) and lands them in ONE message —
    `CellDistComputed(gen, after, before)` — so a held peek can never
    read a half-landed flip. Cleared together everywhere
    (`invalidateCellError`, dataset switch).
  - New `MeshView.cellDistAt` — `graphSideAt`'s pair-scope twin: the
    peeked state's buffer, read by `cellPaint` (Pair/Pin branch) AND
    both legend sites in `GuiOverlays.colorLegend` (the map-on gate and
    the crop-to-mesh range, whose Matrix branches were already
    peek-aware). Peeked + before missing paints NOTHING (like Matrix's
    `Map.tryFind` miss) — never stale after-values on before poses.
    The Pin ROI mask already rides the peek (`displayedWorldAt` is
    peek-aware), so the pin-local mask follows the blinked anchor for
    free.
  - Deliberately NOT flipped: the workspace scale/legend/chart. The
    range stays the after-state cell range (peek-blind, held across the
    flip — no per-state renormalization, so the flip truthfully shows
    the before state saturating toward the ramp ends), and the chart
    keeps its permanent per-edge before outline; the brush/dot stream
    stays the after samples. Matrix behaviour is unchanged.
- CLAUDE.md supersessions: **Peek keys** "Inside the workspace the pose
  peek stays zero-refetch: the error map rides MOV's surface with
  registered-pose values (accepted approximation)" → superseded (the
  workspace map now flips via the resident `CellDistBefore` buffer;
  still zero-refetch at peek time). **Inspection caches** "The two
  states exist only at graph scope" → superseded for the DIST buffer
  (`CellDistBefore` joins; the error/sample caches remain
  graph-scope-only two-state).

## Fix: tile-strip growth loop on fractional-dpr displays (2026-08-06)

- Windows-only bug (Chrome, 125%/150% display scale): the ortho tiles
  grew ~1px per frame indefinitely. Root cause — a layout feedback
  loop: the Aardworx swapchain rewrites the tile canvas's
  width/height ATTRIBUTES every rendered frame
  (`round(devicePixelRatio × getBoundingClientRect)`); the canvas is
  `display:inline` with style 100%/100%, and `.mesh-tile` (flex item,
  `aspect-ratio: 3/2`) had no `min-height: 0`, so its automatic flex
  minimum read the canvas's intrinsic size + the inline baseline gap
  and grew past the aspect height — feeding a bigger rect into the
  next frame's attribute write. Integer dpr (macOS Retina = 2) makes
  the attribute write round-trip losslessly, so layout reaches a
  fixed point and the loop never advances — hence invisible on Mac;
  fractional dpr keeps re-dirtying layout and ratchets forever.
- Fix (style.css only): `min-height: 0` on `.mesh-tile` (mirror of
  the existing `min-width: 0` — closes the feedback channel; the tile
  can never exceed its aspect height) + `.tile-rc canvas
  { display: block; }` (kills the baseline-gap increment).
- Verified by standalone repro (exact tile DOM + the framework's
  per-frame attribute write) in headless Chromium at dpr 1.0 / 1.25 /
  1.5 / 2.0: unfixed ratchets unbounded; each fix line alone AND both
  together give 0.000 px drift over 120 frames.
- No CLAUDE.md supersessions (bug fix; no behaviour/architecture
  change).

## Measured sensor stations: *sensor.txt + /sensors endpoint (2026-08-06)

- The user estimated the four JOB scan stations in-app (⚙ camera
  readout, no registrations) and supplied them for storage in TRUE world
  space. **Transformation-chain check**: the readout emits
  `render/scale + CommonCentroid`; at identity poses and JOB scale 1
  that IS the server frame = stored + per-mesh centroid = original true
  world — the CommonCentroid shift cancels (subtracted into render
  space, added back by the readout) and the per-mesh centroid never
  enters. The supplied values were therefore stored VERBATIM.
- **Data**: new `model_sensor.txt` next to each JOB mesh (all three
  variants JOB / JOB_lowpoly / JOB_lowpoly2), same format as
  centroid.txt (# comment + one `x y z` line, absolute world):
  0789 (18.8467 −3.6765 3393768.6686) · 0791 (19.6485 −3.6826
  3393770.6629) · 0792 (19.0767 −1.0301 3393770.5086) · 0805 (20.9098
  −1.2670 3393770.2529). Notably 0792's measured station sits WITH the
  others — overriding the earlier "its origin 190 m away is its
  station" belief (its exported frame origin is arbitrary after all).
- **Server**: `MeshLoader.getSensor` (`*sensor.txt` glob; None when
  absent — meaningful, unlike getCentroid's zero default) +
  `GET /api/datasets/{ds}/sensors` → `{ meshName: [x,y,z] }`, only
  meshes WITH a file (verified live: JOB returns all 4, Hessigheim {}).
- **Client**: `MeshData.fetchSensors`; `SensorsLoaded` message;
  `Model.DatasetSensors : Map<string, V3d>` (adaptify rerun);
  `CentroidsLoaded` resets it, `loadDataset` fetches sensors after
  centroids. `ModelTransforms.sensorWorld` now PREFERS the measured
  station and falls back to the mesh origin (centroid.txt) — so the
  Sensor ▾ first-person jump, the dataset-load framing and the
  coordinate cross all ride the measured stations automatically. The
  incidence/range heatmap still uses the posed mesh origin (untouched —
  separate semantics, would change analysis output).
- CLAUDE.md supersessions (docs frozen; for reconstruction): the API
  list gains `/api/datasets/{ds}/sensors`; the "Sensor positions"
  section's "there is no separate sensor file or endpoint" clause is
  now false — measured `*sensor.txt` overrides exist for JOB.
- Builds green (server + client, 0 errors).

## Camera readout in the ⚙ debug menu (2026-08-06)

- User confirmed there is NO separate sensor file (the earlier "noted in
  a text file" was misspoken); they will reconstruct the sensor centres
  by hand. The ⚙ popover gained a **Camera readout** section below the
  per-mesh centroid info: live **Eye** and **Orbit centre** rows in
  absolute world coordinates (the `*centroid.txt` frame, `%.4f`,
  monospace, selectable) each with a ⧉ copy-to-clipboard button
  (`window.spCopy`, View.fs OnBoot). Workflow: Sensor ▾ lands
  first-person; navigate to where the scanner stood (fully zoomed in the
  eye sits ON the orbit centre); copy.
- **Registered meshes add a third row kind**: "Eye in *n mesh*" — the eye
  un-posed into that mesh's own file frame via
  `MeshView.displayedWorldAt(...).Backward` (only shown while the mesh
  has a composed pose, where file frame ≠ world; at baseline the world
  row IS the file value).
- Perf: row values gate on `GearPopoverOpen` (`AVal.bind` →
  constant when closed), so the closed menu adds zero per-camera-move
  work; per-row `AVal.custom` reads the stable `eyeWorldA` (sanctioned
  form). CSS: `.tb-cam-*` matching the gear popover's dark chrome.
- Build green (0 errors).

## Sensor jump goes first-person (2026-08-06)

- **Data discovery** (the user reported the jump landing "at the origin of
  a mesh" instead of the sensor): next to each mesh only two text files
  exist — `*centroid.txt` (ONE world point) and `model_bbox.txt` (world
  min/max of the ORIGINAL uncropped scan; consumed by NOTHING, server
  bboxes recompute from vertices). The centroid value IS the stored-frame
  origin's world coordinate, and the radial-OPC stored frame is
  station-centred, so origin ≡ centroid.txt ≡ the sensor — the flown-to
  POINT was already correct. Verified numerically: Job_0792's origin sits
  165–213 m off its scanned face (a genuine across-the-valley station);
  all four JOB meshes' world vertex means coincide (world = stored +
  centroid holds). Data wart found: Job_0791 was re-exported RE-CENTRED
  (stored vertex mean exactly 0, bbox.txt = crop bbox) — its true station
  is lost, its "sensor" is the crop mean (noted at
  `ModelTransforms.sensorWorld`).
- **The actual wiring bug**: `FlyToSensor` set the orbit CENTRE to the
  station but parked the EYE 10 m away looking at it (`FlyToPoint(world,
  10)`) — reading as "camera inspects a spot on the mesh", never a sensor
  viewpoint.
- **Fix**: the jump now lands the eye ON the station — SetTargetCenter
  (Tanh) at the posed sensor + `SetTargetRadius 0.0`, which clamps to the
  orbit floor (0.1 render units) where `OrbitState.withView` snaps the
  eye exactly onto the centre. First-person view, current bearing kept;
  wheel-out zooms the orbit back away from the station.
- CLAUDE.md supersession (docs frozen; for reconstruction): "Sensor
  positions" consumer list — `FlyToSensor` is no longer "a close
  sensor-viewpoint orbit via FlyToPoint at 10 m" but a first-person
  landing at zero orbit distance.
- Build green (0 errors).

## GUI touch-ups round 5 (2026-08-06)

- **Histogram hover restacks to the baseline**: the hovered pin row's
  amber slice (data-hilite) no longer floats mid-stack — the chart JS
  reorders the fill stack so the hilited series draws FIRST (bottom,
  shared baseline) with the rest stacked above; a pure rendering
  rearrangement (same bins, same totals, medians/dots untouched).
- **Clear brush at Matrix**: graphBody's `.cw-chart-tools` row gained the
  same "⊗ Clear brush" button the pair body has (the brush is global
  state, so the wiring is identical).
- **Cut sliders**: labels renamed "▤ Cut"/"▤ Far" → "▤ Near cut" /
  "▤ Far cut"; each `.tb-cut` is now ONE bordered group (tb-btn chrome:
  border + white bg + radius) enclosing label · slider · value box, with
  the loose inner gaps removed (`is-label min-width 0`, gap 6→4, range
  100→90 px, group margin 10→4 px).
- **Pin-row buttons rearranged**: ⌖ fly-to · ✕ delete · ▸ open-at-Pin
  (was ⌖ · ▸ · ✕); the ▸ goto button now wears the blue clickable
  scheme (`.cw-goto`: accent border/text on `#e8effc`, inverting to
  solid accent on hover) as the row's primary navigation.
- Build green (0 errors).

## Tile strip: wheel containment + 3D mark parity (2026-08-06)

- **Wheel containment**: wheel over a tile now zooms the tile ONLY — a
  non-passive `wheel` listener on each `.mesh-tile` (OnBoot) calls
  `preventDefault`, so the strip's `overflow-y` scroll no longer fires
  alongside the zoom. The strip chrome (gaps, resize handle, background)
  still scrolls normally.
- **Pin-mark parity with the main 3D** (the 3D always wins; the tiles had
  kept outdated marks): the committed glyph builders are hoisted to
  ScanPinScene MODULE level — `addCrosshairGlyph`, `addAreaRing`,
  `addContactRings`, `addHighlightRing`, `addRevealLines`, plus the
  shared highlight-subject rule `highlightPin` — and BOTH views render
  through them (the 3D `build` now delegates to the same functions), so
  the views cannot drift again. The tiles draw:
  - EVERY pair pin's area figure (thin duplex equator ring + pure-white
    contact rings + the dashed anchorage ring in the anchor tile) instead
    of the previous subject-only wire-sphere outline;
  - the point's intersection reveal (own point only — the other side's
    relief belongs to the other tile);
  - the ×1.18 bold dashed highlight double ring for the hovered/selected
    pin (`highlightPin`, pin-scope isolation suppresses it);
  - triplex CROSSHAIR correspondence locators (mesh-identity colour over
    ink under a white rim, open centre), screen-constant at 0.025 × the
    tile camera radius — the ortho analogue of the 3D's eye distance —
    REPLACING the icosphere dot fills + white wire-sphere point outlines.
    The crosshairs live in their own node reading the tile RADIUS alone
    (a pan never rebuilds them) and render in passTwo, deterministically
    over the area/reveal lines.
  armDim and pinScopeDim carry over unchanged; the armed cursor preview
  was already synchronized and stays as-was. `ScanPinScene.sphereShell`
  deleted (its last consumer was the tile fills). CLAUDE.md's "the TILES
  keep mesh-colour dot fills + white outlines" clause is superseded —
  reconstruct at doc-freeze end.
- Build green (0 errors).

## F1–F8 test-session fix package (started 2026-08-05)

Eight specs from the test session: ScanPin_v14_F1_palette …
ScanPin_v14_F8_conflict_dialog. Implementing in order, build green after
each task; entries below record each spec's changes as they land.

### F8 — conflict (loop-resolution) dialog rework (2026-08-05)

- **T1 specifics instead of vagueness**: the modal title is now "Two
  paths now connect" + a `.lm-meshes` chip row naming the redundant
  edge's two meshes (number + root-aware F1 swatch). The residual line
  reads "Loop residual: the two paths disagree by X° and Y" and carries
  a tooltip defining it (going around the loop one way vs the other,
  displacement read at the moving mesh's data); every row's
  "quality N.NN" carries a tooltip defining the number (1/(1+rms/5 cm)
  over the edge's pin residuals, remove-the-lowest guidance).
- **T2 embedded interactive tree**: `LoopPending` gained a transient
  `Hover : string option option` (same encoding as `Selected`; NEW
  message `HoverLoopChoice`, row mouseenter/leave). The modal embeds the
  registration tree (`data-looptree` + observedRender — the treePanel
  layout JS reduced to a static preview): hover-else-selection drives
  the per-choice preview — the edge a confirm would REMOVE renders red
  dashed, the NEW edge renders dashed green while kept and red when it
  is the one discarded; a one-line hint explains the encoding. Confirm
  applies exactly the highlighted choice (unchanged reducer path).
- Build green; Supertests 97/97 (LoopPending is in the shared
  RegistrationModel — no test constructs it).

### F1–F8 package status

All eight specs implemented, every task DONE (checklists below); client
+ server builds green throughout, Supertests 97/97, integration suite
32/32 (incl. 3 new roi-fit checks). OWED: the in-browser verify pass —
dotnet build cannot validate FShade→ESSL3, and this package changed two
shaders (the OutlineEdge/OutlineCoverageEdge ink under-strokes) plus
substantial GUI (palette/gold sweep, panel + histogram reworks, tree
ribbon/preview SVGs, RMB camera, both new modals' flows). Checklists:

- F1: T1 ✔ (Primitives.fs meshPalette) · T2 ✔ (refGold/meshColorRoot +
  every identity site + CSS tokens) · T3 ✔ (both edge composites' ink
  under-stroke)
- F2: T1 ✔ (roi-fit endpoint + armedPick validation) · T2 ✔
  (PairConnectWarn modal)
- F3: T1 ✔ (pinScopeDim, 3D + tiles) · T2 ✔ (highlightNode ring +
  label box) · T3 ✔ (orbitCue = camera-moving)
- F4: T1 ✔ (sequence panel + stepper) · T2 ✔ (bordered rows,
  fly/open/delete, inert body) · T3 ✔ (PendingResolves cascade)
- F5: T1 ✔ (legend + dashed outline) · T2 ✔ (peek in pair/pin chart) ·
  T3 ✔ (uniform grey + data-hilite) · T4 ✔ (bin-quantized + single-bin)
  · T5 ✔ (clear-brush in the ex-probe slot)
- F6: T1 ✔ (hover edge + dashed preview) · T2 ✔ (.pmx-redundant) ·
  T3 ✔ (tree ribbon, toast deleted) · T4 ✔ (RMB orbit/pan/repick)
- F7: T1 ✔ (legend crops to the pointed mesh) · T2 ✔ (scope re-admits
  the pointed mesh) · T3 ✔ (Isolate pins → top bar) · T4 ✔ (map toggle
  beside the chart, both live)
- F8: T1 ✔ (named meshes + defined numbers) · T2 ✔ (embedded tree +
  hover preview)

### F7 — error-map/false-colour polish + toolbox placement (2026-08-05)

- **T1 legend follows the pointed mesh**: `GuiOverlays.colorLegend` —
  while the difference map paints, a tile/tree hover (`TileIsolateHover`)
  or the isolate lock CROPS the legend's displayed range to that mesh's
  own error extent (5th–95th pct of its resident per-vertex buffer:
  `GraphDist[Before]` per peeked side at Matrix, `CellDist` for the MOV
  inside the workspace), title gains "· mesh N". The colour MAPPING
  stays the shared scale — the bar shows the ramp segment the mesh
  actually uses, so nothing renormalizes; un-pointing restores the full
  scope range.
- **T2 reference visible when pointed at**: `MeshView.graphMapScopeAt`
  now re-admits the hovered/locked mesh into the painted set. This fixes
  the real defect: the hover narrowing INTERSECTS with the map scope, so
  hovering the root (or an unregistered mesh) in matrix false-colour
  blanked the scene (empty intersection) instead of showing the mesh.
- **T3 Isolate-pins moved to the top bar**: the compactToggle left both
  inspect bodies; the top-bar view-control cluster (after Sensor ▾)
  gained the "◍ Isolate pins" `tb-btn` (`tb-btn-active` state) —
  a render mode among render controls; the per-level `AnchorGhostMode`
  LevelFlags semantics unchanged.
- **T4 error-map toggle = the histogram's peer**: both inspect bodies
  restructured — the old cw-tools row is gone; a `.cw-chart-tools` row
  DIRECTLY above the chart canvas holds the "Error map (3D)" toggle
  (labelled as the spatial twin of the distribution; tooltip says both
  stay live — no either/or was introduced) and, in the pair body, the
  F5 clear-brush button.
- Build green.

### F6 — matrix↔tree coupling + finished ribbon + right-mouse camera (2026-08-05)

- **T1 cell hover → tree edge**: `treePanel`'s treeData now folds in
  `MatrixHoverPair` — a hovered REGISTERED pair marks its edge
  (`"hov":true` → the selected-edge styling), an unregistered pair emits
  `"hovP":[a,b]` and the SVG draws a dashed blue preview of the edge a
  solve would insert (inert, under the nodes). Works from the cell's own
  hover and every other MatrixHoverPair source.
- **T2 tree-redundant cells fade**: `pairCellView` gained `isRedundant`
  (PairPossible ∧ both meshes `hopDepth`-connected — same predicate as
  the F2 pre-warning) → `.pmx-redundant`: borderless faint hint
  (opacity 0.55) so tree-COMPLETING cells stand out; the tooltip explains
  ("already connected through the tree — a direct link would only add a
  loop").
- **T3 finished ribbon replaces the centre toast**: `GuiOverlays.
  spannedBanner`, its View mount, the `.spanned-banner`/`.spb-*` CSS,
  `Model.SpannedNoticeOpen` (+ adaptify) and `DismissSpannedNotice` are
  DELETED. The tree panel now carries `.tree-ribbon` ("✓ all meshes
  connected" + the "Assess global quality →" button, LogReach source
  "tree") — purely DERIVED from `Workflow.spanned`, so disconnecting
  clears it with zero bookkeeping. `trackSpanned` keeps logging the
  spanned/unspanned transitions (reaching record) but owns no notice
  state; `AssessGlobalQuality` survives unchanged minus the notice flag.
- **T4 right mouse = camera, always**: OrbitController PointerMove treats
  `Button.Right` as rotate (left stays when unarmed; the armed swallow
  only eats the LEFT rotate-begin, so RMB orbits while a pick is armed);
  the event edge remaps Shift+LEFT AND Shift+RIGHT to the pan button
  (`Dom.OnContextMenu preventDefault` was already in place). NEW right
  DOUBLE-click = orbit-centre repick: a manual two-click detector
  (browsers fire no dblclick for the right button) on `Sg.OnPointerDown`
  (`e.Original.Button`), through the same `resolvePick` ghost-fallback
  path as the left double-tap. Left picks, right moves.
- Build green, no FS0049/25/26 (message case deleted → grep + warning
  check done).

### F5 — histogram system pass (2026-08-05)

All in GuiRail (`chartJs` + the two bodies) unless noted.

- **T1 legend + dashed outline**: the before/after step outline is now
  DASHED (`setLineDash([4,3])`); the old inline "fill now · line before"
  caption is deleted in favour of a REAL legend — payload fields
  `legF`/`legL` render a grey fill swatch + a dashed-line sample with
  their names ("error now"/"as loaded" + "before registration" in the
  workspace, "error vs parents" at Matrix). Both bodies send them.
- **T2 peek in every histogram**: the pair/pin body's chartData is now
  pose-peek-aware — while B is held (and the before cache is resident)
  the FILL, per-pin medians and LoD band swap to the edge-before state
  ("— as loaded" in the title), on the same fixed axis; gids/values stay
  the canonical now-stream, so a brush held across the peek highlights
  the corresponding region, exactly the Matrix convention. Matrix
  already flipped (graphSideAt) — unchanged.
- **T3 pair-mode grey + pin-linked**: the achromatic per-pin ramp
  (`greyOf`) is deleted — every series renders ONE uniform grey #787878
  (the matrix pooled grey); the stack survives only as data. NEW
  `data-hilite` attribute (the hovered pin row's ShortName via
  `TilePinHover`) repaints that pin's stack slice + median tick amber in
  place — the F4 row hover lights its contribution.
- **T4 bin-quantized brushing**: the drag snaps to whole bins
  (`snapRange` over `el._bin`), a drag-free click SELECTS the single bin
  under the cursor (an empty bin yields no gids = clear — the old
  click-clears behaviour lives on through empty space), and hovering
  outlines the bin under the cursor (`el._hb`, cleared on pointerleave).
- **T5 clear-brush button**: the ex-probe slot in the pair inspect body
  is now "⊗ Clear brush" (`.cw-clearbrush`, amber when armed with a
  selection, disabled when the brush is empty) → `SetBrushedSamples []`.
- Build green.

### F4 — pin panel sequence + pin-row controls + re-solve cascade (2026-08-05)

- **T1 the panel reads as its sequence** (GuiRail `cellWorkspace`): the old
  cw-tools row (New pin beside Solve) + separate cw-finish footer deleted;
  the panel is now top-to-bottom ① `＋ New pin` as the PRIMARY button
  (`.cw-newpin`, accent-filled; same overlap-gate hover) ② the pin list
  ③ the "N remaining" workflow line of its own (`.cw-remaining`: "2 more
  pins needed to solve" / "4 pins placed — ready to solve" / "— solved")
  ④ the linked two-step `.cw-steps`: [⌖ Solve] → [✓ Finish pair] with an
  arrow connector; `.cw-step-lit` marks the NEXT step (Solve lit while
  ≥3 pins ∧ unsolved, Finish lit once solved). The count no longer hides
  inside the Solve button label.
- **T2 pin rows with real controls**: rows are bordered cards; the row
  BODY is inert (OnClick/OnDoubleClick deleted) — hover still sets
  `TilePinHover`, which now drives the F3 loud highlight + tile framing
  (and the F5 histogram link). Three real buttons per row: ⌖ fly-to
  (`ZoomToPin`), ▸ open at the Pin level (`SelectPin` + `SetFocus
  FocusPin`, LogReach "open-pin"), ✕ delete (bordered red, confirm kept).
- **T3 re-solve cascade**: NEW `Model.PendingResolves : (string*string)
  list` (+ adaptify). The pin-edit edge drop records the dropped edge's
  SUBTREE as (child, parent) pairs parent-first (`subtreeDependents`,
  BFS) — merged distinct-by-child into the queue; the toast names the
  dependent count. Every solve COMMIT drains the queue
  (`continuePendingResolves`): the first entry whose parent is back in
  the tree, child free and pair still ≥3 pins launches `SolvePair`
  (its own commit re-enters for the rest); an entry that cannot solve
  is SKIPPED WITH A TOAST naming the reason (too few pins / ref left the
  tree) — placed after the "registered" toast so the notice stays
  visible. Queue clears on dataset switch, checkpoint apply and re-root
  (stale orientations). Supertests 97/97.
- Build green, no FS0049/25/26.

### F3 — pin focus/isolation/highlight + orbit-centre visibility (2026-08-05)

- **T1 pin-scope isolation**: NEW shared rule `ScanPinScene.pinScopeIsoOn`/
  `pinScopeDim` (module level — the tiles read it too): at FocusPin with
  Isolate-pins ON (same armed-centre suspension as the mesh-side anchor
  ghost) every pin whose id ≠ `Sel.Pin` drops to 0.08 alpha — the subject
  is the selected pin, and an active draft is always the subject
  (placement clears the pin selection). Threaded through the 3D centre
  jacks, `addAreaFigure` (new `scopeDim` param; draft passes 1.0), the
  crosshairs (muted, never hidden — factor composes with `markerAlphaAt`)
  and the reveals, plus the tile glyphs (per-pin `fillNode` colour — the
  fill helper gained a colour param — and the white wire outlines). Flags/
  labels stay (navigation furniture).
- **T2 loud highlight (isolation OFF)**: NEW `highlightNode` in
  ScanPinScene — subject = the hovered pin row's pin (`TilePinHover`),
  else `Sel.Pin` at Pair/Pin; suppressed while pin-scope isolation is on.
  Draws a BOLD dashed second ring (r×1.18, white 3.0 over ink 5.0,
  DepthTest.None — reads through terrain) in the ground-ring plane, plus
  a white-over-ink box around the flag label (mirrors the pinLabels
  billboard math: same flag height, yaw and 1.25 h lift).
- **T3 orbit-centre cue**: `SceneGraph.orbitCue` visibility extended from
  "rotate drag with easing lead" to CAMERA IS MOVING — rotate easing OR a
  held pan drag OR a radius easing (wheel zoom) OR a centre/location
  fly-to animation; hidden when still.
- Build green, no FS0049/25/26.

### F2 — adaptive ROI radius + already-connected pre-warning (2026-08-05)

- **T1 adaptive ROI**: NEW endpoint `POST /api/query/roi-fit`
  (QueryHandlers `RoiFitRequest`/`roiFitHandler` + Handlers route +
  `Query.roiFit`): the smallest radius ≥ the requested one whose sphere
  captures ≥ MinVerts vertices of the OTHER pair mesh (one pass collecting
  d² ≤ cap², the N-th smallest IS the minimal radius), capped at
  radius×MaxFactor; ok=false past the cap. Client: `GuiPanes.armedPick`
  validates every CENTRE landing (draft and committed re-pick alike)
  against the non-anchor pair mesh at N=20, cap ×4 — grown radius emits
  `SetDraftRadius`/`SetInnerRadius` BEFORE the centre message (the centre
  landing can mint the pin) + an explanatory toast; a refused location
  emits only a warn toast and the arm stays lit for another try.
  Integration: 3 new roi-fit checks, suite 32/32 green.
- **T2 already-connected pre-warning**: NEW model field
  `Model.PairConnectWarn : (string*string) option` (+ adaptify) + messages
  `ConfirmPairConnectWarn`/`CancelPairConnectWarn`. The `SelectPair`
  reducer branch parks a NEW pair behind the blocking confirm when the
  tree already connects both meshes with NO direct edge
  (`RegGraph.pairEdge` none ∧ both `MatrixNav.hopDepth` Some — the
  spanning tree makes membership = connectivity); the remembered same-pair
  re-entry never warns. Confirm = the normal new-pair entry (cascade clear
  + `jumpFocus FocusPair`), cancel = stay. `GuiOverlays.pairConnectModal`
  (loop-modal chrome, meshes named by number), mounted in View; Esc chain
  slot after the loop modal; dataset switch clears the parked pair.
- Build green (server + full wasm client), no FS0049/25/26.

### F1 — Okabe-Ito palette + dynamic reference gold (2026-08-05)

- **T1 palette replaced** (Primitives.fs `meshPalette`): the old vivid-hue
  set (teal/orange/purple/… — mesh-1 green vs mesh-4 blue collided) is
  deleted; the first six slots are the Okabe-Ito colour-blind-safe six —
  1 #0072B2 blue · 2 #009E73 bluish green · 3 #D55E00 vermillion ·
  4 #CC79A7 reddish purple · 5 #56B4E9 sky · 6 #F0E442 yellow — with
  three off-palette extension hues (purple #9333EA, brown #92400E, olive
  #4D7C0F) for slots 7–9. Okabe-Ito orange #E69F00 is deliberately absent.
- **T2 dynamic reference gold**: `Primitives.refGold` = #E69F00 (the
  excluded Okabe-Ito orange); `refGoldV3d` now derives from it; the CSS
  token family updated (`--ref-gold: #e69f00`, dark #a16207, pale
  #f9e4bb). NEW rule `Primitives.meshColorRoot isRoot idx`: the mesh
  currently root renders gold INSTEAD of its slot colour, the slot
  returns on re-root. Threaded through every identity-colour site:
  MeshView `buildScene`/`buildOutlineNode`/`buildPaneScene` MeshColor
  uniforms (rootA = RegGraph.Root), `coverageColorsA` (root slot gold
  before the brush/map-iso rules), ScanPinScene `meshColAt` (crosshairs,
  centre jacks, tile marks), GuiPanes tile chip + tile pin fills,
  GuiTopBar sensor + mesh-menu swatches, GuiRail `numSwatch`/`meshChip`/
  pin-level `chip` + the tree node stroke (treePanel JSON `c`).
- **T3 pale-slot legibility**: both image-space line composites gained an
  ink under-stroke (the duplex rule for lines) — `OutlineEdge` paints a
  pinInk rim where the depth-break test hits at ±(width+1.5) texels but
  not at the core; `OutlineCoverageEdge` same for footprint contours
  (8 extra halo taps, `col.W < 0.5f && halo → ink`). Slot-6 yellow reads
  on light terrain; every palette line gets the same subtle rim.
- Build green (0 errors, no FS0049/25/26). Shader changes need the
  in-browser verify pass (dotnet build can't validate FShade→ESSL3).

## GUI touch-ups OUTSIDE the specs, round 4 (2026-08-05)

Same standing: user-directed polish, no governing spec.

- **Probe removed entirely** (user: never used, and the control confused
  users). Deleted the `ArmProbe` DU case (Model.fs `ArmTarget`), the
  `ProbeReadout` model field (+ adaptify re-run), the
  `ProbeReadoutComputed` message, the reducer's probe validity/held-reading
  branches (`ToggleArmPick` simplifies to the pin arms), the
  `GuiPanes.probeValueAt` fetch + `armedPick`'s probe route, the probe
  cross glyph in both armed-preview builders (ScanPinScene + tiles), the
  probe tooltip (`pick-tip-probe`) in View.fs, the ⊕ Probe button + probe
  readout row in the pair inspect body, and the `.cw-probe*`/
  `.cw-readout-probe` CSS. `/api/query/pair-error-at` KEEPS its consumer —
  the hovered brushed dot's exact-value fetch (View.fs) — so the endpoint
  stays. DU-deletion check done: repo grep clean (remaining "probe" hits
  are the unrelated `residualAt` probe point and the pin "probe axis"
  comment), build free of FS0049/FS0025/FS0026. The Esc chain loses its
  probe clause by construction (probe arming no longer exists).
- **Compact toggles read as real checkboxes** (user: the Isolate pins /
  Error map controls didn't read as toggle-able properties). Reworked
  `Primitives.compactToggle`: the `■/□` glyph becomes a proper checkbox —
  a bordered 14 px box (`.ct-box`), accent-blue filled with a white ✓ when
  on (`.ct-on` on the row), hover tints the border. Same control everywhere
  it's used, so the gear popover's Ghost-silhouette toggle inherits the
  look for free.
- **Armed previews depth-compose** (user: the correspondence hover circles
  and the centre sphere outline floated over the meshes with no depth
  composition). `ScanPinScene.armPreviewMarks` switches `linesNodeTop`
  (DepthTest.None) → `linesNode` (LessOrEqual) — the same depth-tested
  blended-lines chrome the committed rings/reveals use, so terrain now
  occludes the far side of the wire glyphs. Safe for aiming: the preview
  centre always sits on the frontmost solid surface (it IS the GPU pick),
  so the marker can never fully hide. Tiles deliberately keep
  DepthTest.None (top-down navigation marks). CLAUDE.md's "armed aim
  previews" paragraph will need the depth note at reconstruction.
- **Live sphere∩surface intersection for the centre placement** (user
  asked for the mesh–sphere intersection line strip during the hover
  preview). Implemented as a SHADER band, not a server fetch: new
  `ArmSphere` uniform (V4f render-space centre + commit radius, W<=0 off)
  in `MeshShader.shade` — a derivative-antialiased ~2 px white band where
  |dist(wp, centre) − r| ≈ 0, painted on above-ghost fragments only,
  before the cut-line ink (which still wins). Chosen over a per-hover
  `/query/contact-rings` round-trip: zero latency, zero server load, and
  inherently depth-correct (the band lives on the surface being shaded);
  the real traced rings still land with the commit. Wired in `buildScene`
  AND `buildPaneScene` (tiles preview it too — one model state, every
  view). The overlap gate composes for free: outside the valid both-cover
  region fragments drop to ghost, so the band vanishes with the surface.
  New `MeshView.armCommitRadiusAt` = the ONE "radius the landing would
  commit" rule (draft's / selected pin's / quick default), now shared by
  the shader uniform and both wire-preview builders (was duplicated).
  FShade pitfalls honoured (float32 literals, no local lambdas, ddx/ddy);
  browser validation still owed — dotnet build can't catch shader faults.
- **Crosshair white rim** (user: the camera-aligned correspondence
  crosshairs were poorly visible against the dark grey background — the
  ink under-stroke vanishes there). `ScanPinScene.addCrosshair` arms go
  triplex: a white rim (width 5.4, α 0.85) UNDER the ink (3.4) under the
  mesh-colour core (1.7) — on terrain the ink still separates the colour,
  over the void the rim carries the glyph. Same painter's-order layering
  the ink→core pair already used (one Lines draw, DepthTest.None); dim
  factors apply to all three layers, so muted/armed fades keep working.
  Serves committed pins and the draft alike (one shared builder).

## GUI touch-ups OUTSIDE the specs, round 3 (2026-08-04)

Same standing: user-directed polish, no governing spec.

- **Per-level view flags.** New plain record `LevelFlags { AtMatrix; AtPair;
  AtPin }` (Model.fs, + `LevelFlags.get/set`); `AnchorGhostMode` and
  `InspectOpen` are now `LevelFlags` instead of `bool` — each rail stop keeps
  its own toggle state. Defaults: Isolate pins off/off/ON (Pin), inspect dock
  collapsed/OPEN/OPEN. `ToggleAnchorGhostMode`/`ToggleInspectPanel` flip the
  CURRENT level's flag; `AssessGlobalQuality` opens the MATRIX flag
  explicitly. `MeshView.buildScene`'s anchor-ghost suspension and both
  inspect bodies' toggles read `LevelFlags.get focus`. Adaptify re-run.
- **Error-map isolation** (a render mode like the brush's). New
  `MeshView.mapFrameAt` = the (REF, MOV) frame while the pair map paints
  (map on, no brush) — MOV folds in as a DEFAULT isolate exactly like the
  brush frame's, so the REF drops out of the scene entirely and an explicit
  lock / hover preview / peek still wins. New `MeshView.committedIsoLockAt`
  is the ONE lock+defaults composition, now read by all three
  effective-narrowing sites (buildScene shownCtx, View.shownNow,
  ScanPinScene isoLockAt). `mapIsolationAt` (either scope's map painting)
  additionally zeroes every mesh's ghost floor (non-painted meshes vanish
  outright, Matrix root/unregistered included) and greys every outline: new
  `OutlineGrey` uniform in `OutlineEdge.fragment` (silhouettes → luminance)
  + `coverageColorsA` greyscale branch (footprints). Tiles and the root
  gold overlays are untouched (tile strips keep identity; the map owns the
  MAIN view's colour). V-peek guards deliberately DON'T see the map default
  (same reducer-can't-see-view-state rule as the brush default).
- **Finish pair** (`.cw-finish` footer, bottom-right accent-filled
  `.cw-finish-btn`): enabled while the pair's edge exists (Solve committed
  it; a pin edit drops it and re-disables), click = `SetFocus FocusMatrix`.
- **Pin panel: focus column REMOVED.** The ◎ point A/B and ◉ Pin buttons,
  `SelectPoint` (message + reducer case) and `framePointTiles` are gone —
  view steering at Pin is tile clicks alone (`ToggleTileIsolate` still keeps
  `Sel.Point` in step; `ZoomToPin` survives on the 3D double-tap). The panel
  is now a single "Edit" header over a 2×2 grid: ✚ point A · ✚ point B ·
  ◯ Centre · ⌀ Radius. Grepped for the deleted DU case; no FS0049/25/26.
- **Finish pin / Cancel** (Pin-level `.cw-finish` footer): Finish enabled
  exactly when placement is idle (a pin is atomic — complete ⇔ minted),
  Cancel enabled while placing and both points are NOT yet placed; both
  emit `SetFocus FocusPair` — the ONE navigation path, so the exit-guard
  still confirms a centred draft (Cancel of a centreless draft exits
  silently). Corner accepted: a draft holding A+B but no centre (manual
  re-arm order) disables both buttons — Esc still exits.
- **Guided placement.** After `DraftAreaAt`/`DraftPointAt` the reducer
  re-arms the next missing part (centre → point A → point B; free order
  converges) instead of leaving the pick disarmed — ○ New pin now walks all
  three steps hands-free. New `GuiOverlays.placementBanner` (top-centre,
  accent-filled `.place-banner`, z above the armed veil, pointer-events
  none): "Step k of 3 — place the pin centre / place the correspondence
  point on mesh N", purely derived from (PlacementActive, ArmedPick) — no
  step state. Committed-pin edits show no banner.
- Verified: client type-check build green; Supertests 97/97. Browser pass
  still owed (banner layering, map isolation visuals, guided flow feel).

## GUI touch-ups OUTSIDE the specs, round 2 (2026-08-04)

Same standing: user-directed polish, no governing spec.

- **V peek arms on ANY effective isolation**, not just the committed lock:
  the reducer guard and the top-bar `canVis` now run the same
  `MeshVisibility.effectiveNarrowing` the shown rule uses — tile hover,
  ◎-side hover and an armed A/B pick all enable the flip (the swap itself
  always operated on the effective isolate; only the guards were lock-bound).
  Brush-default isolation alone still doesn't arm it (view-side state the
  reducer can't see).
- **Tree selection → hover.** `HomeMeshSel`/`SelectHomeMesh` (and the
  "mark-mesh" log action) are GONE. Tree node hover now emits
  `SetTileIsolateHover` — exactly the tile-hover 3D isolation preview — and
  the matrix heads light from that same transient (renamed
  `.pmx-head-hover`; a strip-tile hover therefore also lights them —
  deliberate unification). Tree EDGE hover emits `SetMatrixHoverPair` — the
  matrix cell-hover overlap preview — and the corresponding cell lights via
  new `.pmx-cellhover` (fed by cell-own hover and tree-edge hover alike,
  key-order-insensitively via `PairCell.key`). Hover rings/edge highlights in
  the SVG are JS-local (hover stays OUT of the tree JSON — a mid-hover
  rebuild would swallow the mouseleave); a `.tree-canvas` container
  OnMouseLeave clears both transients as the stale-hover guard. Edge CLICK
  (open-pair) stays.
- **Graph histogram gains the pooled before-outline**: `graphBody` now bins
  the union of every edge's `GraphErrorBefore` samples on the same fixed
  axis into the chart's existing `hb` slot ("fill now · line before" at
  Matrix too). Present whenever the before cache holds samples; during the
  pose peek fill and line coincide by construction.
- **Data-state checkpoints in the ⚙ debug menu**: NEW `CheckpointStore.fs`
  (fsproj: after ScanPinModel.g) — sprintf-out / System.Text.Json-in of the
  SCENARIO data only: dataset name, `RegGraph` (root + edges as 16-cell
  Forward matrices + quality), pins (id/name/pair/anchor/centre/radius/
  points/created; rings + reveals restore as not-fetched). Browser
  localStorage under `spCk:` via View-boot helpers (`spCkSave/Load/Del/List`).
  The ⚙ panel: name input + Save, and per-checkpoint Load / ⟳ overwrite / ✕
  delete rows (`Model.Checkpoints`/`CheckpointName`; list refreshed by the
  view around every store op and on ⚙ open). The VIEW owns storage IO;
  `ApplyCheckpoint` (reducer) applies parsed data only — jumps home, clears
  selection/caches (`invalidateCellError`), recomposes poses; a checkpoint
  from another dataset rides a `SetActiveDataset` + load in front, and the
  reducer refuses a mismatch. Loading a spanned graph fires the spanned
  notice (a real disconnected→spanned transition — accepted).
- adaptify re-run (HomeMeshSel out, Checkpoints/CheckpointName in); quick
  client build 0 errors (no FS0049/25/26), Supertests 97/97, full native
  build green. Browser pass still owed.

## GUI touch-ups OUTSIDE the specs (2026-08-04)

Post-N1–N4 polish requested directly by the user — no spec documents govern
these; this entry is the record.

- **Mesh roster removed entirely** (redundant with the registration tree):
  `GuiRail.rosterPanel` + its View mount + all `.roster-dock`/`.ros-*` CSS.
  With it went its spanning-progress line and the macro-state chip — and the
  chip's only backing, `MacroState`/`Workflow.macroState`
  (RegistrationModel.fs), is deleted as dead code (`Workflow.spanned` stays —
  it drives the notice transition; grep confirmed no `Macro*` pattern
  survives, so no catch-all hazard). `HomeMeshSel` lives on — the tree node
  is now its only entry point; the `"roster"` log source is gone.
- **Mesh names out of the main GUI** — the arbitrary job strings carry no
  information; the mesh NUMBER (display order, swatch-paired) is the identity
  everywhere: pair-workspace chips, tile chips, Sensor ▾ rows, ▦ mesh-menu
  rows (its popover narrowed 460→340), matrix head tooltips ("mesh N"), tree
  tooltips. Names survive ONLY in the ⚙ debug menu's per-mesh info rows and
  in the session log's subjects. Dead CSS (`.cw-chip-name`,
  `.pane-chip-name`, `.tb-mesh-menu-name`) removed; the tree JSON dropped its
  `name` field. (Legend + loop modal already spoke numbers.)
- **Session log → top-right button** (`≣`, beside ⚙): `GuiRail.logPanel`
  became a `tb-gear-popover` in GuiTopBar (dark chrome, 40-entry tail,
  scrolling, same ⤓ export), available at EVERY level now, not just home.
  `ReachLogOpen` = the popover; arming closes it with the other top-bar
  popovers.
- **Left column resizable as ONE unit**: the inspect dock's private handle
  (--dockw) is gone; a single full-height `.left-handle` on `.left-col`'s
  right edge — the mirror of the tile strip's — resizes rail + dock together.
  It writes `--leftw` (narrow levels, clamp [220, 50 vw]) or `--lefthomew`
  (home, clamp [380, 100 vw − 500]) so the two modes keep independent widths,
  and re-derives the chart's aspect-true `--charth` on the dock. The handle
  sits on the column, NOT the scrolling rail (the veil/scroll gotcha), and
  the armed veil disables it via the existing `.left-col > *` rule.
- Quick client build 0 errors (no FS0049/25/26), Supertests 97/97, full
  native client-via-server build green. Browser pass still owed alongside the
  earlier batches.

## N4 — home two-navigator stage + shared selection + reaching-log (2026-08-03)

Spec: `ScanPin_v14_N4_home_stage_logging.md`. The home level grew from "the
matrix" into a navigation workspace; the reaching-log is the primary workshop
deliverable.

- **Stage**: matrix ‖ tree split landed with N3's `homeStage` (equal flex,
  identical chrome — a SPLIT, so there is no active-view toggle to log).
- **Persistence**: the roster (N1) and the spanned notice (N2) are their own
  surfaces — the roster a left-col sibling panel, the banner a fixed overlay —
  present at home regardless of which navigator is used.
- **One shared selection, three entry points**: mesh subject =
  `HomeMeshSel` (roster row ∧ tree node → roster `.ros-sel`, matrix
  `.pmx-head-homesel`, tree node ring); pair subject = `Sel.Pair` (matrix
  cell ∧ tree edge → `SelectPair`, the existing descend path → matrix
  `.pmx-sel`, tree edge highlight, roster `.ros-in-pair` tint on both member
  rows). SelectPair clears the mesh subject — one subject at a time.
- **Reaching-log** (first-class): NEW `ReachEvent {At; Source; Action;
  Subject}` + `Model.ReachLog` (newest first, never trimmed, survives dataset
  switches) + `LogReach` — views emit it ALONGSIDE their action message
  (source-attributed at the click site; the reducer never logs an action
  itself, so nothing double-counts). Sites: matrix cell `open-pair`, tree
  node/edge `mark-mesh`/`open-pair`, roster row `mark-mesh`, rail stops
  `jump`, banner `assess-global`/`dismiss-notice`; `trackSpanned` logs the
  `spanned`/`unspanned` transitions reducer-side. **`GuiRail.logPanel`**
  (home level, collapsible, `ReachLogOpen`): 14-entry tail + "⤓ export" →
  full JSON download via the `spDownloadText` boot helper (data-URL anchor).
- adaptify re-run; client quick build, FULL native client-via-server build and
  Supertests (97/97) all green.

## N3 — rooted registration tree (workshop) (2026-08-03)

Spec: `ScanPin_v14_N3_rooted_tree.md`. ROUGH by design — static SVG re-render
per state change, no animation/pan/zoom; a co-equal PEER of the matrix, never
a subpanel.

- **`GuiRail.treePanel`**: root at top, edges = established registrations,
  depth (y) = `MatrixNav.hopDepth` (provenance-path length), tidy-tree x
  (leaves take slots in mesh order, parents centre over first+last child).
  Disconnected meshes float as a dashed-outline island row below a dashed
  separator ("not connected yet"). Node = white circle, mesh-colour ring +
  number (thin identity marks); root adds a `var(--ref-gold)` halo (CSS var
  via style.stroke — no literal gold hex). Data rides ONE `data-tree` JSON
  attribute through `observedRender` (MutationObserver re-render = live
  growth); nodes/edges carry `sel` flags so the selection highlights re-render
  with the state.
- **Clicks through a hidden `.tree-bridge` input** (observedRender rebuilds
  the SVG wholesale, so handlers cannot live on Aardvark-managed nodes; value
  `n|mesh|seq` / `e|child|seq`, seq forces a change): node → `SelectHomeMesh`
  (the SAME subject a roster row marks), edge → `SelectPair(child, parent)` —
  the existing cell-selection/descend path, no new detail panel. Invisible
  fat hit strokes widen the targets; `<title>` tooltips name the subjects.
- **`homeStage`** (rail body at Matrix): matrix ‖ tree in two `.home-nav`
  panels — equal flex, identical chrome; `.left-col-home` widens the left
  column to seat both (600px, capped against the tile strip).
- Build green after each task.

## N2 — spanned-state event (workshop) (2026-08-03)

Spec: `ScanPin_v14_N2_spanned_event.md`. Completion is purely topological —
no quality gate anywhere.

- **NEW `MacroState`/`Workflow`** (RegistrationModel.fs, WASM-free):
  `spanned names g` = names nonempty ∧ hasEdges ∧ every mesh inTree (≥1 edge,
  so a single-mesh dataset never reads spanned); `macroState` =
  MacroDisconnected → MacroSpanned (notice open) → MacroRefining (notice put
  away; optional, never announced as required). Chip in the roster head
  (`.ros-state-*`).
- **NEW `Model.SpannedNoticeOpen`** + `Update.trackSpanned` (post-step, against
  the pre-step model): the notice opens exactly on the disconnected→spanned
  TRANSITION and closes the moment the graph disconnects again (edge drop /
  root-clear / dataset switch) — re-spanning re-fires. Explicit reset on
  SetActiveDataset too.
- **`GuiOverlays.spannedBanner`** (fixed top-centre, `.spanned-banner`, brief
  pulse): "All meshes registered — you can now assess global quality." with
  ✕ (`DismissSpannedNotice` → refining) and **Assess global quality →**
  (`AssessGlobalQuality`): jumps to Matrix (through the Pin exit-guard if a
  centred draft is in flight — the notice then stays for a retry), opens the
  inspect dock and turns the graph error map on — the EXISTING A12
  instruments; no global scalar exists.
- adaptify re-run; client + Supertests builds green after each task.

## N1 — mesh roster + spanning progress (workshop) (2026-08-03)

Spec: `ScanPin_v14_N1_mesh_roster.md`. Workshop build — ADDITIVE, parallel to
the matrix (no consolidation, no winner), topology only: no quality number
anywhere in navigation.

- **`GuiRail.rosterPanel`** (View.fs left-col, between the rail and the
  inspect dock; shown at the home/Matrix level only): one row per mesh
  (swatch · number · friendly name) + a topological badge — `connected` (in
  the registration tree; filled achromatic) / `not yet` (outlined vessel) /
  `no overlap` (pale no-data grey; only when EVERY pair of the mesh reads
  known-insufficient `= Some false` in PairOverlaps — an unfetched pair must
  not read "no overlap" mid-sweep) / `root ★` (gold tokens). Explicitly NO
  reachability/components traversal — a per-mesh cache read.
- **Spanning-progress line**: "N of M meshes connected; X, Y not yet." /
  "All M meshes connected." — live off RegGraph.inTree.
- **Shared selection hook**: NEW `Model.HomeMeshSel : string option` +
  `SelectHomeMesh` (toggle; guarded on mesh existence). Roster row click sets
  it; the matrix row/col heads mark it (`.pmx-head-homesel`). Cleared by
  SelectPair (the pair takes over as the home subject), SetRegRoot and the
  dataset switch. A cross-surface highlight ONLY — never enters
  MeshVisibility.
- CSS `.roster-dock`/`.ros-*`: badges reuse the matrix's achromatic state
  grammar (filled / outlined vessel / pale hole).
- adaptify re-run; build green after each task.

## A13 — the Matrix peek flips the error state (2026-07-30)

Spec: `ScanPin_v14_A13_matrix_peek_flips_error.md`. Client only. Principle: at
Matrix the pose peek stops being a geometry blink and becomes a **state**
toggle — the error field flips in lockstep with the poses, so the colours
always describe what is on screen. **Before** = every edge measured with BOTH
endpoints at their as-loaded baselines (the raw pre-registration
disagreement); **After** = the composed residual. Pair/Pin are untouched: they
pair before/after per EDGE, and no peek reaches their error.

- **T1 — the two-state global map.** `Model.GraphDistBefore` joins `GraphDist`
  (and `GraphErrorBefore` joins `GraphError`), filled by the SAME postludes:
  `ensureGraphDist`/`ensureGraphError` now run two sweeps — one at
  `ModelTransforms.displayedWorld`, one at `loadWorld` — started together and
  awaited together, so the extra state costs latency-nothing beyond the server's
  parallelism. Both states land in ONE message (`GraphDistComputed(gen, after,
  before)` / `GraphErrorComputed(gen, after, before)`), mirroring
  `CellErrorComputed`, so a peek can never catch a half-landed flip. The before
  pin ROIs are re-placed at the baseline too (`roisAt world`) — a pin's centre
  rides its anchor mesh, so the measuring sphere has to move with it.
  - The swap: new `MeshView.graphSideAt` = `PeekPose ? EdgeBefore : EdgeAfter`
    (reusing the existing `EdgeSide` vocabulary), read ONLY inside Matrix
    branches. `cellPaint`'s Matrix arm picks the peeked buffer — both are
    resident, so the key costs one vertex-attribute upload and never a refetch.
    `GuiOverlays.diffOn` follows.
  - `graphMapScopeAt` is untouched and peek-blind (it keys on registration,
    not pose), so the excluded meshes are the same outlines in both states.
- **T2 — the scale is fixed across the flip.** `MeshView.inspectRangeAt`'s
  Matrix arm is deliberately peek-BLIND: it reads the **before** blocks (else
  the before per-vertex maps), falling back to the after twins only while the
  before state is absent. Renormalizing per state would recolour the residual
  to full range and the flip would show no improvement at all — the whole point
  is that "after" **collapses toward neutral** on one stable ruler. The legend
  reads the same helper, so it holds too.
- **T3 — the two-state pooled histogram.** `MeshView.inspectBlocksAt`'s Matrix
  arm returns the peeked stream, so the flip reaches the chart, the 3D dots,
  the hover search and the readouts through the ONE stream abstraction — no new
  per-consumer branching. `graphBody`'s axis and binning now come from
  `inspectRangeAt` (the fixed before-state range) instead of the displayed
  samples, and the title names the state ("— as loaded" / "— all registered
  edges") so a silent distribution swap can't be misread.
  - **Known limit, deliberate:** gids are running offsets into the displayed
    stream, and the two states are NOT sample-aligned — the server re-estimates
    the pin normal and re-samples per pose, so "sample k" is a different
    physical point in each state (and a pin with no overlap before registration
    contributes none). A brush held across the flip therefore highlights the
    corresponding *region* of the distribution, not the same physical samples.
    No mapping could be faithful here, so none is fabricated; the brush is not
    cleared either (the peek must stay zero-bookkeeping).
- **T4 — scope + trigger guards.** Audited: every `graphSideAt` /
  `Graph*Before` read sits inside a `FocusMatrix` arm (`inspectBlocksAt`,
  `inspectRangeAt`, `cellPaint`, `diffOn`, and `graphBody`, which mounts only
  at Matrix). `inspectBody` and every pair branch are byte-identical.
  `PeekVis`'s only consumers remain the three isolate-swap sites
  (`MeshView.buildScene`, `ScanPinScene.isoLockAt`, `View.shownNow`) — it
  touches no error field at any scope.

Green after every task: client type-check 52 warnings / 0 errors; Supertests
97/97. Browser-unverified (no shader source changed; the flip is a buffer +
uniform swap).

**Docs reconstructed** at the close of this instance, covering A12 + the A12
follow-ups + A13: CLAUDE.md — "In-cell inspection caches" rewritten as
"Inspection caches — two scopes, one shape" (the graph caches, the two states,
`InspectBlock`/`inspectBlocksAt`, `inspectRangeAt`, the brush cap + cross-scope
clear + the not-sample-aligned limit, the map's excluded-outline rule, the
brush's pair-only spatial frame, the toolbox at every level); "Peek keys"
(V pair-only and hidden at Matrix, B whole-graph, the Matrix state toggle);
the shown-rule signature (`matrixScope`); "One in-cell error range" →
"One error range per scope"; the legend's two titles; "The cell chart" split
into the pair body and the pooled graph body; the ≤4000 → ≤12000 marker note;
the top-bar peek clause; the Model.fs compile-order line. README.md — the
inspection toolbox now docks at every stop, a new **Inspect the whole graph**
bullet, and the peek-keys bullet rewritten for the two scopes and the flip.

## A12 — global inspect at Matrix scope (2026-07-30)

Spec: `ScanPin_v14_A12_matrix_global_inspect.md`. Client only. Principle:
inspect is the top rung of the scope ladder — the same toolbox at Matrix, every
instrument resolved against the whole tree, every quantity **parent-relative**
(a child vs the neighbour one hop toward the root). Only established edges
contribute; a zero-edge graph is legitimately empty.

- **T1 — the inspect toolbox at Matrix.** `GuiRail.inspectPanel` is visible
  whenever `focus = Matrix || Sel.Pair.IsSome` (was: a selected pair at
  Pair/Pin), same dock, same collapsible header, same `InspectOpen`
  preference. Its content branches on the level: the existing `inspectBody`
  (pair) or a new `graphBody` (Matrix) — error-map toggle (T3) + the pooled
  diagram + brush bridge + hover readout (T4). The graph body deliberately
  carries NO probe and no isolate-pins toggle: `ArmProbe` is reducer-guarded
  to Pair/Pin, and the spec scopes the Matrix instruments to T2–T4.
- **T2 — the pose peek goes global.** `MeshView.peekMovAt` → `peekPoseAt`: at
  Matrix EVERY mesh blinks to its as-loaded baseline (there is no REF/MOV at
  graph scope), inside the workspace only the pair's MOV as before. Still a
  trafo-uniform swap only, so both states stay GPU-resident, the camera never
  moves and nothing reflows. New reducer guard `Update.peekPoseOk` — Matrix
  needs ≥1 edge and every edge-participating mesh resident; the pair branch is
  the old rule (loaded + registered). The VIS peek is unchanged (its
  `peekPairLoaded` scope already excluded Matrix) and its top-bar button is now
  HIDDEN at Matrix (`peekBtn` gained a `showA` gate) — the one deliberate
  exception to "disabled, never hidden", since a REF/MOV flip has no meaning at
  graph scope at all.
- **T3 — the parent-relative error map (union).** New `Model.GraphDist :
  Map<string, float32[]>` (per registered CHILD, its per-vertex signed distance
  vs its PARENT) + `GraphDistComputed`; filled by `Update.ensureGraphDist` —
  one `region-distance` per edge at the displayed poses, fanned out with
  `Async.Parallel`, lazy/single-flight on the SHARED inspection generation
  (`invalidateCellError` clears both scopes' caches: they outlive nothing a
  pair cache outlives).
  - `MeshView.cellPaint` now branches on focus: at Matrix a mesh paints
    `GraphDist[name]` (so every registered child paints at once), the pair
    branch is unchanged. `InspectPlain` follows for free (it keys on
    `DistanceEncoding = 1`).
  - ONE shared scale: new `MeshView.inspectRangeAt` (pair cell inside the
    workspace; at Matrix the pooled edge samples, else the pooled per-vertex
    distributions) replaces the `cellRange` reads in the map uniforms, the
    legend and the dots. `GuiOverlays.diffOn` lights at Matrix on
    (map ∧ GraphDist) ∨ brush, titled "Difference vs parents" — the same one
    legend, never a second.
  - Excluded meshes: `MeshVisibility.shown` gained a `matrixScope :
    Set<string> option` narrowing, fed from `MeshView.graphMapScopeAt` (the
    tree's meshes while the Matrix map paints, else None) at all three shown
    sites (`buildScene` shownCtx, `View.shownNow`, `ScanPinScene.markerAlphaAt`).
    An unregistered mesh drops to the ghost floor, so what remains is its
    outline (G-buffer silhouette + footprint contour, both visibility-blind) —
    never a white surface that would read as "registered and fine".
  - `anchorGhostOn` (Isolate pins) now ALSO suspends while the graph map paints
    — same derived-suspension pattern as the armed centre pick. Without it the
    default-on pin isolation would confine the global map to pin patches and
    T3 could not be seen at all.
- **T4 — the pooled graph histogram + brushing.** New `InspectBlock` record
  (Model.fs: `Mov`/`Ref`/`Pin`/`Err`) and `Model.GraphError : InspectBlock[]
  option` + `GraphErrorComputed`, filled by `Update.ensureGraphError`: one
  `pair-error` batch per established edge over that edge's pins, requested
  PARENT-first (the endpoint returns meshB-relative-to-meshA, so
  child-relative-to-parent needs no flip), fanned out in parallel — order is
  preserved, so the canonical edge×pin gid stream is deterministic.
  - THE unifying piece: `MeshView.inspectBlocksAt` = the canonical gid stream
    of the CURRENT scope (the pair's pins projected into blocks, or
    `GraphError`). Every gid consumer now walks it — the 3D dots, the hover
    ring, `View`'s 12 px hover search + exact-value fetch (which takes the
    hovered block's own Ref/Mov, so a graph dot measures against ITS parent),
    the tooltip and the legend tick.
  - The chart: ONE monochrome series, pooled SAMPLES (not bin-added counts),
    current state only — no `hb` before-outline (N ghosts would be
    unreadable), no per-edge colour key. Pooled median tick + mean LoD band,
    same 48 bins, same furniture/placeholder discipline.
  - Dots: `brushedDots` walks the blocks and takes each block's OWN
    `markerAlphaAt b.Mov` (the A10 marker rule per owner — at graph scope the
    owners are many); colours from `inspectRangeAt`. `MeshView.brushActiveAt`
    (brush ∧ a non-empty stream) replaces `brushFrameAt.IsSome` as the
    `ColorIsolate` gate, so the whitening mode lights at Matrix too.
    `brushFrameAt` stays pair-only: at graph scope nothing is isolated and no
    footprint goes gold (many owners, and the root keeps its own gold).
  - Cross-scope hygiene: `Update.clearBrushAcross` wipes brush+hover when a
    jump crosses Matrix ⇄ pair-workspace (the same gids address a different
    stream); Pair⇄Pin keeps it. Applied in `jumpFocus` AND `normalizeFocus`
    (the demotion path bypasses jumpFocus). The brush cap went 4000 → 12000:
    the widest scope is now the whole graph, and the old cap would have
    silently truncated a graph-wide brush.

Green after every task: client type-check 52 warnings / 0 errors; Supertests
97/97. NOT verified in a browser (no shader source changed in A12, but the
Matrix paths themselves are unexercised outside a run).

### A12 follow-ups (2026-07-30)

Two review fixes on the landed A12. Both touch the Matrix scope only.

- **Isolate pins at Matrix** — `graphBody` now carries the same "Isolate pins"
  toggle as `inspectBody`, on the same `AnchorGhostMode` state. *Supersedes the
  T1 bullet's "no isolate-pins toggle" and the T3 bullet's graph-map
  suspension:* `MeshView.anchorGhostOn` no longer stands down while the graph
  map paints (only the armed centre pick still suspends it). The suspension
  existed **because** Matrix had no control — with the toggle present it would
  have made that control dead on arrival, and the default-on isolation is now
  the same first-look Matrix as Pair.
- **The reference is outline-only under the graph map** —
  `MeshView.graphMapScopeAt` narrows to the edge CHILDREN alone (was: children
  ∪ parents, i.e. the whole tree). The solid set is now exactly the PAINTED set
  — every mesh that carries a parent-relative error — so the reference root
  joins the unregistered meshes as an excluded outline. Only the moving side is
  relevant while the map is on, and an unpainted white surface in the middle of
  the map read as "registered and fine". Intermediate nodes are unaffected:
  they are children of their own edge, so they stay solid and painted.

Client type-check green: 52 warnings / 0 errors. Still browser-unverified.

## A11 — brush-point rendering: disc locators + colour isolation (2026-07-30)

Spec: `ScanPin_v14_A11_brush_point_rendering.md`. Client only.

- **T1 — the dot is a flat camera-facing DISC.** The wire-sphere+cross brush
  glyph is DELETED. New `Discs` module (LineShader.fs, next to `Lines`):
  own float32-only vertex+fragment shaders, billboarding AND the
  screen-constant sizing done in the VERTEX stage (`DiscRight`/`DiscUp`/
  `DiscMinR`/`DiscMaxR` uniforms + `[<Literal>] screenFrac = 0.008` of the eye
  distance), so the buffers hold plain unit-circle offsets and rebuild only
  when the dot SET changes — a camera move costs three uniform updates, not a
  re-upload of ≤4000 dots. Radius clamped in METRIC world to [5 mm, 0.5 m]
  (`ScanPin.renderLength`). The fragment paints a thin ink rim past |off| >
  0.80 so a near-white dot still reads on the whitened surface. Node =
  `ScanPinScene.brushedSampleNode` (DepthTest.None, blended, `Sg.NoEvents`);
  hover/select behaviour untouched (View.fs's 12 px screen-space search).
  The hovered dot's emphasis moved OUT of the dot buffers into
  `hoverRingNode` — ONE duplex camera-facing ring at 2.2× the disc radius, so
  a hover never re-uploads the geometry (and the dot keeps its VALUE colour).
- **T2 — value colour on the ONE shared scale.** Dot colour =
  `Primitives.Diff.colorSignedV3` over `MeshView.cellRange` — the same ramp and
  the same range as the surface false-colour map, chart and legend.
  `GuiOverlays.colorLegend`'s `diffOn` now also lights on a non-empty brush
  (the map's own gate OR dots exist) — the existing legend, never a second one.
- **T3 — colour isolation while brushed.** New `MeshView.brushFrameAt model t`
  = (REF, MOV) of the selected pair while dots exist, else None — THE frame
  every consumer reads.
  - Scene whitens: `MeshShader.ColorIsolate` uniform (new) — the GHOST branch
    takes the plain near-white too (a ghosted mesh stays ghosted, it just stops
    carrying identity colour) and the three intrinsic heatmap painters are
    suppressed (Shp keeps its cutoff — that is a filter, not colour);
    `InspectPlain` now = `distEncoding = 1 || colorIsolate`, so every mesh's
    base goes near-white. Tiles pass `ColorIsolate = 0` (the strip keeps its
    colours).
  - Spatial frame: `MeshVisibility.withBrushIsolate` (Model.fs) folds MOV in as
    the DEFAULT isolate — an explicit `TileIsolate` (and through it every
    transient preview and the vis peek) still wins, so the mode COMPOSES.
    Applied at all four isoLock sites (`MeshView.buildScene` shownCtx,
    `View.shownNow`, `ScanPinScene.isoLockAt` → markerAlphaAt/isoCueMeshAt).
  - REF = its gold footprint: `MeshView.outlineMask` (was constant all-on) now
    gates the G-BUFFER composite to MOV alone while brushed (the G-buffer holds
    every mesh regardless of visibility, so a ghost's palette silhouette would
    otherwise compete); new `footprintMask` gates the FOOTPRINT composite to the
    pair; new `coverageColorsA` repaints the REF channel `Primitives.refGoldV3d`.
    `OutlineView.buildCoverage` takes the colours as an aval.
- **T4 — dots ride the anchor mesh.** `brushAlphaAt` = `markerAlphaAt` of the
  frame's MOV (the A10 rule): full / 0.15 faded / gone — carried in the dot
  colour's alpha and the hover ring's.
- **T5 — no 3D ruler** (none existed; none added). The legend JSON gained
  `"hov"` = the hovered dot's normalized position on the bar (the tooltip's
  exact probed value, falling back to the dot's own sample until that fetch
  lands); the SVG draws a white-haloed `#d97706` tick — the diagram's own hover
  amber — appended last so it rides over bar and ticks.
- Green after every task: client type-check (52 pre-existing warnings, 0
  errors), Supertests 97/97. No `[<ModelType>]` change ⇒ no adaptify run.
- NOT verified in a browser: the new `Discs` shaders and the `ColorIsolate`
  branch (the documented FShade failure mode is runtime-only).

## Post-A10: hover previews REPLACE the isolation; crosshairs mute (2026-07-30)

- **Was inconsistent (user report)**: with a mesh isolated, hovering a
  DIFFERENT mesh (its tile, or the other side's ◎ button) previewed
  nothing — the hover narrowing intersected the stale lock, so the mesh
  being pointed at was exactly the invisible one.
- **ONE narrowing helper** `MeshVisibility.effectiveNarrowing hover armed
  isoHover isoLock point` (Model.fs) replaces `pinFocusMesh`: a transient
  target (◎-side hover > armed A/B pick > tile hover) now REPLACES the
  committed `TileIsolate`+`Sel.Point` pair on **both** components, so the
  hovered mesh becomes the isolated one; ◉-Pin hover previews the release
  (no narrowing); an armed centre/probe keeps the lock but lifts the
  point narrowing (aiming needs both meshes); un-hover falls back to the
  committed pair — restore is free, no state is written. All four
  consumers go through it (`MeshView.buildScene` shownCtx,
  `View.shownNow`, `ScanPinScene.markerAlphaAt`/`isoCueMeshAt`), so
  shown = clickable and the marks follow the same preview.
- **Crosshairs MUTE, never vanish (user spec)**: `markerAlphaAt` now
  returns three states — solid under the effective narrowing = 1.0,
  solid only under the committed state = 0.15, neither = None (hidden).
  The reveal hides on None (it would float in the air); the crosshair
  takes the same factor but floors at 0.15 instead of hiding, so a point
  whose mesh is hidden by isolation reads as a faint ghost locator.
- Docs: CLAUDE.md shown-rule bullet (effectiveNarrowing), crosshair
  bullet (mutes, not exempt), reveal-visibility bullet (three states),
  peek bullet; README pin passage (hover preview + muting).
- Green: client type-check.

## A10 — correspondence marker rework: crosshair + intersection reveal (2026-07-30)

- **Ball marker DELETED (main 3D)**: the mesh-colour icosphere + white
  wire-sphere correspondence marker (`markerNodes`/`pointMarkers`/
  `draftMarkers`, incl. A8's unified builders) is gone. The TILES keep
  their dot fills — a 2D top-down small multiple doesn't occlude and
  needs its point mark; A10's target is the occluding 3D body. Area
  rings, centre jack, flags unchanged.
- **Crosshair locator** (`crosshairNode`): camera-aligned (view
  right/up basis), screen-constant (outer radius 0.025 × eye distance),
  OPEN centre — the centre IS the pick point and stays bare; mesh-
  identity colour over an ink under-stroke; DepthTest.None. EXEMPT from
  the mesh-solid visibility rule (it is the locator) — only the pair
  scope (`pinShown`) and the global armed ×0.15 fade apply. One segs
  pass covers committed pins AND the draft's placed points.
- **Intersection reveal** (`pointReveals`/`draftReveal` + `revealSegs`):
  the point's OWN mesh's local geometry — 3 concentric sphere∩surface
  rings (metric radii ×0.2/×0.6/×1.0 of `Model.RevealRadius`, default
  0.5 m) + 2 world-vertical axis-aligned plane∩surface relief cuts (an
  X in plan) — white fading to transparent with metric distance
  (outermost ring keeps ~0.2, cut overshoot runs out linearly past
  rMax). Follows `markerAlphaAt` (hide/fade) + armed fade; normal depth
  testing. Gear ("Debug & settings") slider "Marker reveal radius (m)"
  = `SetRevealRadius` → invalidates every reveal.
- **Server**: `MeshAnalysis` refactored into shared `sphereCut` +
  `planeCut` level-set cuts (the existing tracer, exact roots;
  contactRings now = sphereCut over the BVH candidate set); NEW
  `pointReveal` = one candidate query at the outermost radius serving
  all 5 cuts (plane cuts may overshoot by a triangle — the client fade
  handles it). NEW endpoint `POST /api/query/point-reveal`
  { name, point, radii, planes, maxPoints } → { lines } — point/normals
  in the mesh's server frame, the CALLER bakes the displayed pose into
  the vertical normals. Smoke-tested live (JOB dataset: 20 lines/809
  pts, distances 0.003–0.536 m for rMax 0.5).
- **Client cache**: `RevealState` (None/Running/Ready of V3d[][]) per
  point side on `ScanPin` (RevealA/B) AND `PinDraft` — polylines stored
  MESH-LOCAL (ride the pose); pose changes still invalidate
  (verticality bakes the pose; `invalidateRings` now also resets
  reveals), as do point re-picks (`EditPointAt`/`DraftPointAt`) and the
  radius slider (`ScanPinModel.invalidateReveals`). `ensureRings`
  extended: per-figure debounce CTS (`revealCts` keyed pin×side,
  `draftCts` keyed side/area), `fetchReveal` shares the postlude;
  `landDraft` carries Ready reveals into the newborn. Messages:
  `PointRevealComputed`/`DraftRevealComputed` with the same
  Running-only stale guard.
- DOC DEBT: CLAUDE.md Style (committed point marker = crosshair, not
  fill+outline), Pins marker bullet (crosshair exempt / reveal follows),
  API endpoint list (+point-reveal), server compile-order note
  unchanged; README pin passage (crosshair + reveal, debug slider).
- Green: server build, client type-check, Supertests 97/97, live
  endpoint smoke test.

## A9 — modal armed-picking scrim + lit cancel button (2026-07-30)

- **Armed = an unmissable, click-safe quasi-mode**: while ANY pick is
  armed, every non-pick surface is scrimmed AND inert; only the main 3D
  view, the pair tiles and the arming button stay live.
- **CSS hook**: View mounts an empty `.arm-flag` div, adaptively classed
  `on` while `ArmedPick.IsSome`; all rules key off
  `body:has(.arm-flag.on)` (descendant :has — Aardvark wraps mounted
  nodes; body's own class list is boot-managed, so no root class).
- **Inert**: `pointer-events: none` on `.top-bar`, `.left-col > *`, and
  the strip's `.tiles-handle` (strict scope — resize is not picking).
  **Veils**: `::after` (rgba(15,23,42,.5), z 500) on the two
  NON-SCROLLING containers `.top-bar` + `.left-col` (a veil on the
  scrolling rail would scroll away with its content; the left column's
  veil spans rail + dock + gap). Passive furniture (toast, scale bar,
  legend, orientation indicator) dims to opacity 0.35 — no floating
  dark boxes over the 3D view.
- **Lit cancel** (`.arm-lit`, set with `rail-btn-active` on the arm
  buttons + the probe button): `position:relative; z-index:501` (above
  the 500 veil in the same `.left-col` stacking context),
  `pointer-events:auto` (the ONE re-enabled element), white ring +
  accent-blue glow. Clicking it, Esc, or a landed pick disarms → scrim
  clears (all derived, zero bookkeeping).
- **Reducer**: arming closes the top-bar popovers (gear / mesh / sensor
  menus) — an open one would float dead outside the bar's veil box.
- The in-scene ×0.15 armed fade of existing marks is RETAINED (the
  scene-level focus cue; scrim + lit button are the UI-level signature).
- DOC DEBT: CLAUDE.md Pins/arming bullet + Misc (menus close on arm);
  README picking passage (scrim + lit cancel).
- Green: client type-check.

## A8 — draft = committed pin appearance (2026-07-30)

- **Principle (spec A8)**: a pin renders identically whether being placed
  or committed — a draft is a pin with parts missing; each part snaps to
  its final appearance the instant it lands. The all-white pre-commit
  visual mode is DELETED (all-white now = aim previews only).
- **PinDraft owns `Radius` + `Rings`** (`ContactRingState`): radius seeded
  from `QuickPinRadius` at `BeginPinTransaction`; the ⌀ Radius edit +
  slider serve the draft too (`SetDraftRadius`); centre re-pick / radius
  change / pose change (`invalidateRings`) reset the draft's rings.
- **Draft contact rings**: `ensureRings` covers the draft via the shared
  `fetchRings` fan-out (own CTS, `DraftRingsComputed`, same
  RingsRunning stale guard); `landDraft`/`makePin` carry RingsReady into
  the newborn (no refetch flash), in-flight downgrades to RingsNone.
- **ScanPinScene**: `addAreaFigure` = ONE area builder (duplex equator +
  anchorage cue + axis + white contact rings) for `pinRings` AND the new
  `draftAreaNode`; the centre jack loop covers the draft's centre;
  `markerNodes` = ONE committed-style marker builder (mesh-colour fill +
  white wire outline) for `pointMarkers` AND `draftMarkers`; `draftMarks`
  (white sphere outline + wire+cross glyphs) DELETED. ArmCentre preview
  radius = the radius the landing commits (draft's, else the selected
  pin's — was QuickPinRadius even for committed re-picks).
- **Tiles (GuiPanes)**: draft point = mesh-colour fill (`fillNode`,
  subject size 0.05) + white 0.065 wire outline (cross deleted); draft
  area sphere = the subject-pin `areaSphere` incl. the dashed anchorage
  ring on the anchor tile; radius = draft radius.
- **Blob mask + tile framing** read the draft's own radius (MeshView
  `pinBlobUniforms`, Update `framePinTiles`).
- Kept draft-specific: the completeness flag + exit guard + panel
  progress cue; no flag/label for the draft (identity arrives at mint).
- DOC DEBT: CLAUDE.md Style "all-white = uncommitted layer" now means aim
  previews only; Pins section (draft renders as pin, PinDraft fields,
  DraftRingsComputed); README placement passage ("draft renders
  all-white" gone; radius editable during placement).
- Green: client type-check; adaptify no-op (PinDraft is not a ModelType).

## Marker visibility rides the mesh + 3D anchorage cue (2026-07-30)

- **General rule (user spec)**: a correspondence point marker — committed
  or a draft's placed point — shows only while its mesh renders solid.
  `ScanPinScene.markerAlphaAt` evaluates the ONE shown rule twice: with
  committed inputs (tile-isolate lock / `Sel.Point`, peek-swapped; no
  hovers, no armed) → not shown = HIDDEN (floating marker); with preview
  inputs (+ tile hover, ◎-side hover via `pinFocusMesh`, matrix hover) →
  not shown = FADED 0.15 (transparent preview, not a pop). The armed
  transient stays excluded — the global armed fade covers it. Applied to
  `pointMarkers` (fill + outline) and the draft's placed side points.
- **Pin areas never hide with isolation** (per the tiles' rule): every
  pair pin's rings render at their original locations; pins ANCHORED to
  the effective/previewed isolated mesh (`isoCueMeshAt`: ◎-side hover >
  tile hover > lock > Sel.Point, peek-swapped) add the tiles' dashed
  anchorage ring in 3D (r×1.08, white dashed, `addDashedRing`).
- **◎-side hover fade** (`anchorHoverDimAt`): pin marks (equator ring +
  axis + contact rings + centre jack) of pins NOT anchored to the
  hovered mesh fade to 0.15; flags/labels stay (navigation furniture).
- Docs: CLAUDE.md Pins bullet, README pin-panel passage.
- Green: client type-check.

## Vis peek redesigned: isolate swap instead of MOV blink-off (2026-07-29)

- **Was broken**: V always blinked the same mesh — `peekMovAt` derives
  MOV via `MatrixNav.pairRefMov`, which for an unregistered pair falls
  through to key order, so the "moving" mesh never depended on what the
  user was looking at.
- **New semantics (user spec)**: V depends on the isolate state — with a
  pair mesh isolated, holding V flips the isolation to the pair's OTHER
  mesh (only it visible, same spot other epoch); release reverts to the
  previous isolate. No isolate ⇒ the peek does nothing (button greyed).
- **Implementation — derived, not stored**: the swap happens in the two
  effective-isolate sites of the shown rule (`MeshView.buildScene`
  shownCtx + `View.shownNow`, so shown = clickable holds during the
  blink); the Pin level's point narrowing (`pinFocusMesh`) swaps WITH
  the isolate — `Sel.Point` rides the same lock, so scope ∩ iso would
  otherwise go empty and the blink would show nothing solid; the
  `TileIsolate` lock never moves, so revert is automatic.
  The old MOV-hide (`peekVisHiddenAt` + gates on the three node
  families: main surface, G-buffer, coverage) is DELETED — the blink now
  renders exactly like clicking the other tile. `peekMovAt` stays for
  the pose peek alone.
- **Guards**: reducer `SetPeekVis` now also requires `TileIsolate` ∈ the
  selected pair; GuiTopBar splits a shared `pairLoaded` into `canVis`
  (+ isolate lock) and `canPose` (+ registered — NOT isolate-gated).
  Tooltips updated.
- Docs: CLAUDE.md peek section rewritten; README peek bullet.
- Green: client type-check.

## Fix: peeks permanently dead — mesh-load completions were dropped (2026-07-29)

- **Root cause**: `MeshView.loadMeshAsync` fired the completion callback
  only for the caller that CREATED the cache entry (or a cache hit after
  the load finished); a caller hitting a still-in-flight entry was
  silently dropped. The tile strip (`buildPaneScene`), the offscreen
  passes (`offscreenMesh`) and `projectUpNormal` all request meshes with
  no-op callbacks and evaluate before the main pass — so THEY created the
  entries and the main scene's real callback (the one emitting
  `LoadFinished`) never fired. `Model.MeshesLoaded` stayed empty forever
  → `peekPairLoaded` false → V/B keys AND top-bar buttons permanently
  refused (also: the `loading-done` marker never appeared).
- **Fix**: per-name pending-callback list (`pendingFinished`) — the entry
  creator seeds it, in-flight cache hits append, the load task fires all;
  loaded cache hits keep firing immediately (dataset-revisit path).
- **CSS**: `.tb-btn-tiny:disabled` (opacity 0.45, no hover) — a disabled
  peek button previously looked identical to an enabled one, masking the
  bug as "clicks do nothing".
- Green: client type-check.

## Post-A7 polish IV: draft-mark fade, no pin-level delete, overlap gate (2026-07-29)

- **Draft marks fade while armed too**: the draft's already-placed parts
  (white wire-sphere+cross points, area outline) now share the committed
  marks' armed fade (×0.15) in the main 3D AND the tiles — only the armed
  cursor preview stays full.
- **Pin-level ✕ delete REMOVED** (`deleteRow`): deletion is the pair
  workspace pin rows' job alone.
- **Overlap gate on pin-location interactions**: the shader's
  `OverlapPreview` (formerly matrix-hover-only) now also lights at
  Pair/Pin for the selected pair while the ○ New pin button is hovered
  (new `Model.NewPinHover` transient + `SetNewPinHover`; wiped by
  jumpFocus/dataset switch — the click's focus jump hands over to the
  pre-armed centre) or while the CENTRE pick is armed (placement AND
  committed re-pick) — only the overlap region is a valid pin location.
  `overlapPreviewUniforms` rewritten as one AVal.custom (meshIndicesA
  bound outside — the transient-aval trap).
- Green: client type-check; adaptify rerun.

## Post-A7 polish III: isolate-pins suspension, dock resize (2026-07-29)

- **Isolate pins suspends while the centre pick is armed**: aiming a
  whole-pin move needs the full terrain. Derived, not stored —
  `MeshView.anchorGhostOn = AnchorGhostMode && ArmedPick <> Some ArmCentre`
  feeds both the `AnchorGhost` uniform and the GhostOpacity zeroing, so the
  toggle restores itself on disarm with zero bookkeeping.
- **Inspect-dock resize handle** (right edge, pure DOM like the strip's):
  writes `--dockw`/`--charth` custom properties on the PERSISTENT dock div
  (the chart re-mounts per pair, the vars don't). CSS:
  `.inspect-dock { width: var(--dockw, 100%) }`,
  `.cw-chart { height: var(--charth, 160px) }`, `.inspect-handle`.
  Growth keeps the chart's fixed 236×160 aspect until the chart would pass
  the viewport bottom (minus whatever dock rows sit BELOW it — measured
  live as dockBottom−chartBottom), then height clamps and only width grows;
  re-clamped on window resize. Chart canvas already renders at element size
  via ResizeObserver — no JS chart changes.
- Green: client type-check.

## Post-A7 polish II: frustum, isolate&focus sync, peeks, brush cap (2026-07-29)

Ten-item user feedback round; client type-check + full server build green,
Supertests 97/97.

- **Frustum**: near 1 cm / far 1000 m METRIC (× DatasetScale), both View.fs
  projections (render control + overlay tooltips — they must match).
- **Error map default OFF** (`CellMapOn = false` initial).
- **Isolate & focus buttons = the tile isolate**: a ◎-side click toggles
  `Sel.Point` AND `TileIsolate` together (ONE state — the tile lock, the
  shown rule and the button highlight all read it; a Pin-level tile click
  keeps `Sel.Point` in step) and on enable flies the main camera onto the
  correspondence point (`FlyToPoint`, r×2); ◉ Pin releases + `ZoomToPin`.
  `jumpFocus` now resets `Sel.Point` with the isolate (lockstep).
- **Armed dimming**: while ANY pick is armed every committed pin mark fades
  to α×0.15 — main-3D rings/points/centre jacks (ScanPinScene) and the tile
  wire-spheres/area circle/point fills (GuiPanes); the draft and the armed
  cursor preview stay full.
- **Pin rows**: radius slider REMOVED (radius lives in the Pin panel);
  hover = tile-camera preview of that pin (`Model.TilePinHover` transient,
  `tileCamOf` override with the exact SelectPin framing), click = keep.
- **Brush cap 200 → 4000** (`SetBrushedSamples`): 200 truncated inside the
  first pin's gid block (≤300 samples/pin) — the reported "brush only
  selects one pin". JS emits on pointer-up only, no JS-side cap.
- **Esc before the centre**: a centreless draft aborts silently straight to
  Pair — Esc chain gets the branch BEFORE armed-disarm (one Esc out), and
  the SetFocus/FocusAscend exit-guard thresholds on `placingWithCentre`
  (popup only once the centre exists).
- **Draft blob**: the in-flight draft's area (centre + QuickPinRadius)
  joins the `Blobs` mask, so Isolate pins shows the in-edit patch opaque.
- **Peeks fixed**: scope was FocusPair-only (hence "always disabled" at
  Pin). Now `peekPairLoaded` = Pair OR Pin + both meshes resident + no loop
  modal; V = exactly that (isolate never disables); B additionally requires
  `RegGraph.pairEdge` (registered pair). Top-bar buttons mirror via
  `canVis`/`canPose` with per-button disabled hints.

## Post-A7 polish: tile pin circles, white contact rings, true sensor jump (2026-07-29)

- **Tile pin circles in BOTH pair tiles.** `GuiPanes.meshTile`: the selected
  pin's area-sphere outline no longer gates on `AnchorMesh = name` — its
  centre rides the ANCHOR mesh's pose (shared render space), so both pair
  tiles draw it; the anchor tile alone adds a dashed outer ring (r×1.08,
  new `LineGlyphs.addDashedRing`) as the anchorage cue. The in-flight
  draft's area circle got the same lift (both tiles; no dashed ring —
  uncommitted). New helper `renderOn` (any mesh's own-frame local → render
  position); `renderOf` = `renderOn name`.
- **Contact rings render pure white.** `ScanPinScene.pinRings`: the
  sphere∩surface intersection polylines dropped the duplex ink under-stroke
  — single white stroke (α 0.85, 1.6 px), a deliberate user choice over the
  duplex convention; the equator ring stays duplex.
- **Sensor ▾ jumps to the TRUE sensor.** Investigation: the JOB OBJs are
  radial panorama scans whose origin IS the scan station (stored-frame
  vertex means sit ~0–2 m from the origin; Job_0792's origin lies 190 m
  from its siblings' because that scan has its own station), so the
  sensor's world coordinate = the `*centroid.txt` value — already in the
  app as `DatasetCentroids`. The hand-estimated
  `JOB_lowpoly2/pano-centers.txt` was ~1.5 m off (0792: 190 m off — it
  marked the data centre, not the station) — DELETED, along with the whole
  pano-centers layer: `Model.PanoCenters`, `PanoCentersLoaded`,
  `fetchPanoCenters`, server `getPanoCenters` + handler + route (adaptify
  rerun; FS0049 greps clean). `ModelTransforms.panoCenterRender`/
  `firstPanoCenterRender` → `sensorWorld`/`sensorRender`/`firstSensorRender`
  (centroid-backed). `FlyToSensor` = a sensor-VIEWPOINT jump:
  `FlyToPoint(displayedWorld ∘ sensorWorld, 10 m)` — a close orbit at the
  station riding the mesh's displayed pose, replacing the own-bounds
  overview framing. The MeshView sensor origins simplify to the posed mesh
  origin; the coordinate cross reads `DatasetCentroids`.
- Green: client type-check, server build, Supertests 97/97.

## A4–A7 docs reconstructed (2026-07-29)

CLAUDE.md: three-level rail + exit-guard in Navigation; shown-rule =
level scope ∩ tile isolate; Esc chain (pin-exit popup first); camera rule
mentions Sensor ▾; Pins section rewritten (implicit completion, exit-guard,
universal arming incl. ArmProbe, the control panel, centre re-anchoring,
centre-edit added to the cascade-drop rule); inspection = the docked
toolbox (+ Isolate pins, probe-readout survival); Secondary views section
rewritten for the ONE persistent tile strip (crisp line, root-framed
defaults, active-gated per-tile coverage MRTs, adaptive `other`); Misc
gained the top-bar cluster/menus; heatmap + pano-center consumer lines,
render-pipeline/outline/picking mentions and the client compile-order
descriptions updated. README: three-stop intro, Explore + Sensor ▾, new
"tile strip" and "mesh setup (▦ menu)" bullets replacing Survey (Setup),
matrix without the order bar, pins rewritten (no commit, exit-guard,
two-column panel), inspection = docked collapsible toolbox hosting the
probe-as-arm and Isolate pins.

## A7 — persistent toolboxes: inspection dock + view-control cluster (2026-07-29)

Spec: `ScanPin_v14_A7_persistent_toolboxes.md`. Build green after each task.

- **T1 — the inspection toolbox is DOCKED, persistent, collapsible.** The
  floating `.inspect-panel` deleted; `.inspect-dock` (same top-left column
  slot below the rail) gains a thin header bar — the header IS the top-left
  collapse/expand toggle (`Model.InspectOpen` + `ToggleInspectPanel`;
  collapsed = the header edge alone; a view preference that survives
  jumps). Present at Pair AND Pin as before (Pair = full cell, Pin =
  pin-local map + pin-scoped chart, canonical gids — untouched). The
  **Isolate pins** view mode moved INTO the dock (it is an inspection
  instrument): the `AnchorGhostMode` toggle left the pair workspace's
  cw-tools and sits beside the error-map toggle + probe. The chart's
  ResizeObserver re-render already covers the collapse/expand cycle.
- **T2 — the view-control cluster.** The top-left cluster is now: ▤ Cut ·
  ▤ Far · **Sensor ▾** · Peek ◌V/↺B. The jump-to-sensor dropdown
  (`Model.SensorMenuOpen` + `ToggleSensorMenu`, `.tb-menu-left` popover)
  lists every mesh (swatch/number/name) and flies the main camera via the
  existing `FlyToSensor` — the menu form of the explicit fly-to grammar,
  replacing the A4-removed tile double-click. Peek buttons unchanged
  (always visible, disabled-with-why outside Pair).

## A6 — Pin: implicit completion + exit-guard + control panel (2026-07-29)

Spec: `ScanPin_v14_A6_pin_implicit_placement_controlpanel.md`. Build green
after each task; Supertests 97/97.

- **T1 — commit mechanic DELETED; completion implicit.**
  `ScanPinUpdate.landDraft`: the moment the last of {centre, point A,
  point B} lands, the draft mints the pin and placement ends — no separate
  act. `CommitPin` + `AbortPinTransaction` messages, the ✓ Commit / ✕ abort
  buttons and their handlers deleted (no FS0049/25/26). Birth detection in
  the reducer postlude moved from the CommitPin case to the completing
  draft pick (new-key diff): newborn auto-selects, tiles re-frame, cell
  caches invalidate.
- **T2 — exit-guard (supersedes P6's silent rollback).**
  `Model.PinExitPending : FocusLevel option` parks the wanted destination:
  `SetFocus`/`FocusAscend` leaving Pin with an in-flight draft (always
  incomplete under implicit completion) raise the blocking confirm-delete
  popup (`GuiOverlays.pinExitModal`) instead of jumping. `ConfirmPinExit` =
  jumpFocus to the parked stop (the jump itself rolls the draft back);
  `CancelPinExit` = stay. Esc chain gained the popup as its FIRST slot
  (cancel = stay); Esc-ascend and rail jumps share the ONE gate. A complete
  pin (placement idle) exits promptless.
- **T3 — the Pin control panel** (`.pin-panel`, a 2-column grid: rows =
  subjects A/B/pin, columns = Edit | Isolate & focus). Edit = arm-driven:
  ✚ point A / ✚ point B / ◯ Centre / ⌀ Radius — the radius SLIDER stays
  hidden until its edit is clicked (`Model.PinRadiusEditOpen` +
  `ToggleRadiusEdit`; collapses on pin change + focus jump). Isolate &
  focus = the existing `SelectPoint` buttons (◎ point A / ◎ point B /
  ◉ Pin) with hover previews + tile reframe. The SAME arms serve placement
  and committed edits — **the centre-immovable rule is retired**: new
  `EditCentreAt` re-anchors a committed pin onto the hit mesh (`ArmCentre`
  valid with a selected pin; joins every pin-edit postlude: disarm, tile
  reframe, cache invalidation, cascade edge drop). `BeginPinTransaction`
  now clears the pin selection (the DRAFT is the subject; the newborn
  re-selects on completion). The old focusRow/draftBar/editBar deleted; a
  draft-progress cue + delete row remain below the panel.

## A5 — universal arming for all picking (2026-07-29)

Spec: `ScanPin_v14_A5_universal_arming.md`. Build green; no FS0049/25/26.

- **T1+T2 — the probe folded into the ONE arm mechanism.** `ArmTarget` gained
  `ArmProbe`; `Model.ProbeArmed` + `ToggleProbeArmed` DELETED — the probe
  arms via `ToggleArmPick ArmProbe` (valid at Pair AND Pin with a selected
  pair; ArmCentre/ArmPoint stay Pin-only). `armedResolve`: ArmProbe raycasts
  both pair meshes (nearest hit); `GuiPanes.probeValueAt` fetches the exact
  pair value (moved from the View-local probe branch); the main-view unarmed
  probe tap path DELETED — `Sg.OnTap` now only routes armed picks. While
  ANY arm is up (probe included) LMB never orbits and the white cursor
  preview renders in every view (`ArmProbe` glyph = plain cross).
- **Probe landing semantics:** the universal landed-pick-disarms rule now
  applies — `ProbeReadoutComputed` lands only while still armed (Esc between
  click and landing kills it), disarms, and the READOUT SURVIVES the disarm
  (else it could never be read); the next arm, any focus jump and every cell
  invalidation wipe it. The probe tooltip + panel readout follow the
  readout, not the armed flag.
- **T3 — Esc chain:** loop modal > armed-pick disarm (probe included) >
  `FocusAscend`; the separate probe slot is gone. Rail jumps run the same
  cleanup through `jumpFocus` (arm + preview + probe readout), so Esc and
  its jump redundancy stay consistent; the A6 incomplete-pin guard slots in
  next.

## A4 — drop Setup · 3-level rail · persistent tile strip · top-bar menus (2026-07-29)

Spec: `ScanPin_v14_A4_rail_tiles_menus.md`. All four tasks landed, build green
after each; Supertests 97/97 (the five reorder tests left with the deleted
code). Crisp line adopted for A4–A7: **tiles do VISIBILITY; arming does
PICKING; tiles never pick unarmed.**

- **T1 — Setup level deleted.** `FocusSetup` case removed (rail =
  Matrix·Pair·Pin; `FocusLevel.parent/enabled` compact; initial + dataset
  switch land on Matrix; `FocusAscend` stops at Matrix). The Setup survey
  view (`GuiRail.surveyRow`/`rootOverview` incl. the row double-click
  fly-to-sensor and the ◉ Isolate button) deleted outright.
  `MeshVisibility.shown` restructured: level scope (Matrix all + hover-pair
  narrow, Pair sel-pair, Pin pinFocus) **intersected** with the isolate lens
  at every level. `pinShown`: Matrix = all. Greps + build: no FocusSetup
  remains, no FS0049/25/26.
- **T2 — ONE persistent tile strip** (`GuiPanes.meshTile`/`tileStrip`,
  `.mesh-tiles`): mounted once per dataset, present at every level. Matrix =
  all meshes; Pair/Pin = the pair's two. Off-scope tiles hide via
  `.tile-off` = `position:absolute` + `visibility:hidden` (render controls
  keep their viewports — never display:none) with scenes Sg.Active-gated.
  Tile-click = isolate/de-isolate (`ToggleTileIsolate`), hover = preview
  (`SetTileIsolateHover`) — `SetupIsolate*` renamed `TileIsolate*`, now a
  strip-wide transient lens wiped on any focus jump. While a pick is ARMED
  the tile click lands the pick instead (unchanged arm doctrine). Default
  tile camera framing = the REFERENCE ROOT's bounds (comparable small
  multiples); own bounds only rootless. The old `paneControl`/`surveyTile`/
  `setupTiles`/`panes` deleted; pin marks now pair-scoped (`marksOn`) so
  they show at Pair AND Pin; `buildPaneScene` takes an adaptive
  `other : aval<string option>` + always-real coverage MRT (checkerboard
  fallback gone); `buildCoverageNode`/`coverageOffscreen` gained an `active`
  gate — per-tile MRTs render only while a placement is in flight on that
  tile's pair (main view passes constant true). Tile ☆ Set-reference button
  and tile double-click-fly deleted (→ T3 menu / A7 dropdown).
- **T3 — hidden top-bar mesh menu** (`▦` button, `tb-mesh-popover`,
  `Model.MeshMenuOpen` + `ToggleMeshMenu`): per-mesh rows (swatch, number,
  name, ☆/★ Set-reference = the ONLY root-change path again, Tex/Dst/Shp/Inc
  heatmap bar) + the Shape ≥ threshold slider (shown while any Shp is on).
- **T4 — reorder-matrix toggle deleted:** the matrix order bar,
  `Model.MatrixOrder`, `SetMatrixOrder`, the `MatrixOrder` DU and
  `MatrixNav.orderMeshes` (RegistrationModel.fs) all gone; the matrix reads
  `MeshNames` in sensor order. Supertests' reorder block reduced to the
  still-live `hopDepth` check (`hopDepthTests`).

## Feature round: armed picking, pin focus, far cut, inspect panel, tile refocus (2026-07-29)

Seven-item feedback round; all landed in one build.

- **Far cut slider** (`▤ Far`, beside `▤ Cut`): `Model.FarCutFrac` +
  `SetFarCut`; OFF is the slider's RIGHT end (≥ 2.495 — a small fraction
  cuts nearly everything, so 0-as-off can't work like the near cut).
  Shader: `FarCutDist`/`FarCutBand` uniforms (shares `CutFwd`), discard
  beyond + the same flat-ink intersection band just before the plane; the
  outline G-buffer discards with it. Panes bind constants 0.
- **Pin-level focus buttons** (`◉ Pin` / point-on-A / point-on-B): reuse
  the until-now-unused `Sel.Point` as the focused correspondence side
  (None = whole pin). They control 3D visibility (side → that mesh alone),
  hover-preview via new `Model.PinFocusHover` (`PinHover` DU), and
  re-frame the tiles (pin ↔ point). `MeshVisibility.shown` gained a
  `pinFocus` param; `MeshVisibility.pinFocusMesh` resolves hover > armed
  pick > `Sel.Point`. `shownCtx`/`shownNow` extended.
- **Arm-based picking — 3D is the primary picking surface**:
  `ArmTarget = ArmCentre | ArmPoint of mesh`, `Model.ArmedPick`. Arm
  buttons live in the Pin rail (draft bar: Centre/A/B replacing the old
  Area/Points sub-tools — `DraftTool`/`SetDraftTool` DELETED, no
  FS0049/25/26; edit bar: A/B re-pick, centre immovable). While armed:
  LMB never orbits (reducer swallows left rotate-begins), a click in ANY
  view picks — the ARM TARGET is the attribution (ArmPoint raycasts its
  own mesh alone — this supersedes the old tile-=-attribution doctrine;
  ArmCentre raycasts both pair meshes, nearest hit anchors), via the
  shared `GuiPanes.armedResolve/armedPick`. Disarm = landed pick / Esc
  (new chain slot: loop modal > pick disarm > probe disarm > ascend) /
  re-click. Arming an A/B pick isolates its mesh in the main 3D (hover
  preview). `BeginPinTransaction` auto-arms the centre pick.
- **Synchronized cursor preview** (`Model.ArmPreview`, metric world):
  all-white uncommitted marks (centre = QuickPinRadius sphere outline,
  point = wire-sphere+cross) rendered in the main 3D (`ScanPinScene`)
  AND both pin tiles from the same model state. Main-view hover feeds it
  from the GPU pick (throttled 40 ms, no server traffic — isolation
  makes the frontmost solid surface the armed set); tiles server-raycast
  (70 ms throttle); reducer drops stale/disarmed landings. The in-flight
  DRAFT now also renders in the main 3D (white area sphere + point
  glyphs) — picks must land visibly on the primary surface.
- **Detached inspection panel** (`GuiRail.inspectPanel`, `.left-col`
  fixed flex column: rail on top, panel floating below): the ONE diagram
  + error-map toggle + probe moved out of the pair workspace; visible at
  Pair AND Pin. At Pin the diagram narrows to the selected pin — gids
  stay CANONICAL (indices into the full CellError concatenation) so the
  brush addresses the same 3D samples; the x-range stays the full cell's
  (shared-scale rule). The chart boot JS gained a ResizeObserver
  re-render (the panel hides via display:none). The error map at Pin is
  pin-LOCAL: `cellPaint` masks vertices outside the pin's ROI sphere
  with a NEW 3e30 keep-base sentinel (shader: ≥2e30 keeps base colour;
  1e30 stays the no-Z-overlap pale grey).
- **Gold reference outline in every ortho tile** (setup tiles + pin
  tiles): a per-tile root-only coverage pass
  (`MeshView.buildRootCoverageNode`, channel 0, strip-visibility-gated)
  + `OutlineView.rootCoverageOffscreen`/`buildRootOutline` — the
  `OutlineCoverageEdge` composite reused with slot-0-only mask and gold
  in `CoverageColors[0]`; DepthTest.None in passOne = unobscured.
  `Primitives.refGoldV3d` added as the ONE F# mirror of `--ref-gold`
  (SceneGraph's 3D root outline now reads it too). REVIEW: "reference
  mesh" interpreted as the registration ROOT everywhere.
- **Camera rule + tile auto-refocus**: RULE — no GUI interaction moves
  the main 3D camera without an explicit prompt (already held; now
  documented), but the TILES SHALL refocus: new transaction → pair
  overlap area (XY bbox intersection, union fallback); DraftAreaAt /
  CommitPin / SetInnerRadius / EditPointAt / SelectPin → tight on the
  pin (r×3); SelectPoint side → tight on that point (r×1.5), `◉ Pin` →
  pin. `frameTiles` inverts the tan 30° half-width mapping so frames
  land exact.
- Build green (FS0044 ×4 pre-existing; the [FShade] PropertySet warnings
  are library-AOT noise), Supertests 102/102. Owed to the browser pass:
  armed pick end-to-end on co-located pairs, LMB suppression feel,
  preview sync + throttles, far-cut band look, pin-local map mask, root
  outline in tiles (incl. root-mesh tiles), refocus framings, the
  detached panel's chart on show/resize.

## Fix: peek buttons never visible (2026-07-29)

- User report: the top-bar peek buttons never appear. Cause 1 — they were
  `showWhen`-HIDDEN except at Pair-with-loaded-meshes: undiscoverable
  chrome. Now ALWAYS visible, `disabled` (with a why-tooltip) whenever a
  peek couldn't land; the enable guard is unchanged.
- Cause 2 (latent, also killed the V/B KEYS): `Model.MeshesLoaded` resets
  on every dataset switch, but `loadMeshAsync`'s cache-hit path never fired
  the completion callback — after REVISITING a dataset the loaded-guard
  stayed false forever. The cache-hit branch now calls `finished()` when
  the mesh data is present (an in-flight first load still reports through
  its task); `LoadFinished` gained a `wasNew` guard so re-emissions can't
  append duplicate loading-done marker divs.
- Build green. Browser-verify: buttons visible at every level, enabled at
  Pair; peeks alive after a dataset round-trip.

## Polish: Pin panel = a right-edge tile strip, shared ortho camera (2026-07-29)

- **The Pin panel is now the TWIN of the Setup strip**: the pair's two
  picking tiles stacked VERTICALLY (mesh A above B) in a thin fixed
  right-edge column (244 px, `.pin-panes` restyled; `.pin-pane` gets the
  tile `aspect-ratio: 3/2`), the same left-edge width-resize handle
  (`stripResizeHandle`, shared with `setupTiles`; the A|B split divider is
  DELETED — one width knob rules both tiles). The central-area overlay is
  gone: the MAIN 3D stays visible at Pin (it already isolates the pair via
  the visibility rule) — picking still happens ONLY in the tiles.
- **The shared 2D camera is now ORTHOGRAPHIC top-down** (tiles AND pin
  tiles — `cam2dView`/`cam2dProj` rebuilt): `TileCam.Radius` drives the
  ortho half-width via tan 30°, so stored cameras keep exactly the framing
  the earlier 60°-fov perspective gave, and `unitsPerPx` (pan/zoom math) is
  unchanged; the eye rides `Radius + scene-Z-extent` above the centre plane
  with `far = Radius + 2·zext + 10`, so no terrain near-clips at any zoom;
  ortho Frustum built as a record literal (`isOrtho = true`). `pickRay`
  is viewProj-generic, so tile picks work unmodified under ortho.
- Rail/UI copy switched from "pane" to "tile" wording. Rendering (shipped
  shader, overlap gate, markers) and pick routing untouched.
- Build green (FS0044 ×4 pre-existing). Owed to the browser pass: ortho
  framing/depth (near/far margins over real datasets), tile-strip picking
  at the small default size, both strips' resize feel.

## Polish: hold-to-peek buttons in the top bar (2026-07-29)

- `.tb-peeks` (GuiTopBar, between the Cut slider and the right group —
  tb-right keeps its margin-left:auto): "Peek" sublabel + two hold-down
  buttons "◌ V" (vis peek) and "↺ B" (pose peek), press = `SetPeek* true`,
  release = false, `pointerCapture = true` so the release lands even when
  the cursor slides off mid-hold; `classWhen tb-btn-active` mirrors the held
  state (keys and buttons share it — the reducer's idempotence absorbs
  overlap). Shown only when a peek could land: Focus = Pair ∧ both pair
  meshes GPU-resident (the same guard the reducer enforces). Keys unchanged.
- Build green. Owed to the browser pass: hold/release feel incl.
  capture-release edge cases (context menu, touch).

## Polish: Pin panes = minimal small-multiples variant (2026-07-29)

- The two Pin viewports are now a two-tile variant of the small multiples:
  the pane orbit cameras are GONE (`PaneSide`, `PaneCamA/B`,
  `PaneCamMessage` + handler + the SelectPair sensor re-seeding all
  DELETED — no FS0049/25/26, leftover grep clean); panes drive the SAME
  top-down 2D controller as the survey tiles, extracted into shared helpers
  (`GuiPanes.tileCamOf`/`cam2dView`/`cam2dProj`/`cam2dAtts`) over the ONE
  per-mesh `Model.TileCams` map — a mesh keeps its pan/zoom across levels
  (tile at Setup ⇄ pane at Pin), fov unified at 60°.
- `cam2dAtts` takes an optional `onPick`: a drag-free pointer-up (≤4 px,
  jitter-tolerant via a moved flag) is a click — the panes pass their
  raycast pick (placement/edit routing unchanged; rendering — shipped
  shader, overlap gate, markers — unchanged), tiles pass None.
- **A|B split divider** (`.panes-divider` + OnBoot JS): drag resizes the
  pane split (15–85 % clamp), pure DOM like the tile-strip handle; pane A
  is looked up LAZILY inside the handler (the boot-time later-sibling
  capture gotcha).
- Build green (FS0044 ×4 pre-existing), Supertests 102/102. Owed to the
  browser pass: pane pan/zoom + click-pick feel, divider drag, marker
  readability under the top-down view.

## Polish: matrix root mark, white-map fix, tile resize + 2D cam, pick tooltips (2026-07-28)

Feedback round on the A1–A3 build.

- **Matrix shows the reference root**: row/col heads of the root mesh get
  `.pmx-head-root` (gold-pale fill + gold-dark inset ring, token colours) +
  a "— the reference root ★" title (`GuiRail.headRoot`).
- **"White instead of textured" on cell click — root cause + fix**: entering
  a pair auto-paints the error map (CellMapOn defaults on); in a cell with
  NO pin samples `ErrorRange.ofSamples Seq.empty` returned the ±0.5 m cap
  default, so typical cm-scale differences all normalized to the diverging
  ramp's near-white centre AND the no-Z-overlap sentinel kept the
  InspectPlain near-white base → the whole MOV read as a white wash. Fix:
  (a) `ErrorRange.ofDistances` — a pinless cell's scale now comes from the
  per-vertex distance distribution itself (per-sign 95th percentile, 1 mm
  floors, same ±cap); `MeshView.cellRange` is the ONE selector (pin samples
  win when any exist) shared by the map uniforms AND the legend; (b) the
  shader paints sentinel fragments PALE GREY (grey = no-data; near-white
  stays reserved for "difference ≈ 0"). SHADER EDIT — browser-verify.
- **"Error map toggle does nothing"**: wiring audited end-to-end
  (compactToggle → `ToggleCellMap` → `CellMapOn` → `cellPaint`/enc/
  InspectPlain) — structurally sound; the dead feel is attributed to the
  white-wash bug above (ON state ≈ indistinguishable from bright rock
  texture). With the range fix the ON state is unmistakable. VERIFY in the
  browser; if it still sticks, suspect the checkbox click region.
- **Tile strip resize handle** (`.tiles-handle` + OnBoot JS): left-edge
  ew-resize drag, width clamped 160–600 px, pure DOM (layout chrome, not
  model state); tiles switched from fixed height to `aspect-ratio: 3/2`, so
  the render controls reflow with the width.
- **Tile 2D camera** (`TileCam { Centre; Radius }`, `Model.TileCams` map +
  `SetTileCam`; reset on dataset switch): custom 2D controller per tile —
  drag pans in the XY plane (anchored at drag start, no incremental drift;
  screen right = +X, screen down = −Y under the top-down sky=+Y view),
  wheel zooms TO THE CURSOR (the point under it stays put: centre' =
  centre + off − off·k, off from the 60°-fov units-per-CSS-px at the centre
  plane), radius clamped in the reducer. Default framing (bounds) applies
  until the first interaction.
- **Pick tooltips**: `.pick-tip` floating readouts riding the 3D points —
  the armed probe's value at its click point and the hovered brushed
  sample's value (gid → canonical CellError position) — projected in View
  with the main camera (CSS-px viewport, behind-eye culled); the rail
  readouts stay.
- Build green (FS0044 ×4 pre-existing), Supertests 102/102 (ofDistances
  compiles into the shared RegistrationModel).

## Docs reconstructed after A1–A3 (2026-07-28)

- CLAUDE.md: "Navigation & visibility" → "Navigation: the focus rail,
  selection & visibility" (rail, `FocusSelection` manager — the old blanket
  "no selection state" axiom is superseded by SCOPED selection; global blobs
  and cross-panel emitters stay banned); Pins bullet (panes = the picking
  surface, no edit arming); Peek keys (Pair-scope only, three node families
  — peeks now REFUSED at Pin so a pose blink can't move the picking surface
  mid-placement); new "Secondary views" section (panes + tiles,
  `buildPaneScene`, visibility-not-display, checkerboard Coverage binding);
  suitability MRT references removed; cache-invalidation bullet (caches ride
  the selection); compile order (+GuiPanes.fs). README: rail intro, Setup
  (tiles strip), Matrix (select+highlight), Pair (rows select/dbl-click),
  Pin (two-pane placement/edit, overlap-while-placing, leave = abort), peeks.
- Stale "focus tile/pano" comments updated in Model.fs / MeshView.fs /
  MeshShaders.fs / SceneGraph.fs (the survey tiles are the referent now).
- The three spec files (`ScanPin_v14_A1/A2/A3_*.md`) left in the repo root
  for review — delete at review time per convention.
- Final state: build green, Supertests 102/102.

## A3: Setup survey tiles — small multiples (2026-07-28)

Amendment `ScanPin_v14_A3` — per-mesh small multiples, Setup only.

- **T1 — the tile strip** (`GuiPanes.setupTiles`/`surveyTile`, `.setup-tiles`
  CSS): a fixed right-edge column, ONE tile per mesh — input-less top-down
  thumbnail (fixed `CameraView.lookAt` over the mesh bounds, sky = +Y because
  a look-down view cannot use +Z), displayed pose, LIVE per-mesh survey
  heatmap; identity chip + ★; the explicit **☆ Set reference** button on
  every tile (the only root-change path, same rule as the rows); double-click
  flies the main camera to the sensor. Mounted ONCE per dataset (keyed on
  MeshNames — never rebuilt on focus jumps), scoped to Setup by
  visibility + `Sg.Active` like the Pin panes; Matrix/Pair/Pin show none.
- **Shared builder generalized:** `MeshView.buildPaneScene model name active
  overlap size` — `overlap = Some (other, cov0, cov1)` engages the Pin
  panes' isolate-overlap gate, `None` (tiles) binds the checkerboard default
  texture into the Coverage slots (the sampler must be fed even when the
  gate never reads; IBackendTexture→ITexture goes through `AVal.map` — aval
  is invariant). The builder now feeds the REAL survey-heatmap uniforms
  (HeatmapMode/SensorOrigin/RangeMax/ShapeThreshold + the shared
  `shapeBufOf` shape buffer), so tiles AND panes honour the per-mesh
  Tex/Dst/Shp/Inc switches; the deliberate v13 small-multiples value (N
  scalar views side by side) is back, scoped to Setup.
- NOT a resurrection of the selection-framed focus panel: tiles are
  input-less (no selection emitters, no per-tile cameras, no tile picking) —
  the A1 axiom (scoped selection, no panel-to-panel emitters) holds.
- Build green (FS0044 ×4 pre-existing); Supertests 102/102. Owed to the
  browser pass: N-tile render-control cost on this backend (the known
  "nested controls don't scale" risk — tiles are the bounded ≤8-mesh case;
  fallback = mount-per-Setup-visit), tile framing/heatmap look, strip vs
  overlays layout (orientation indicator sits under the strip at Setup).

## A2: Pin level two-pane picking surface + placement fix (2026-07-28)

Amendment `ScanPin_v14_A2` — builds the Pin level, fixes broken placement.
P6 atomic-pin semantics unchanged.

- **REVIEW finding (placement was broken — root cause):** the old main-view
  placement pick resolved `raycastNearestNamed()` = the NEAREST hit among all
  shown meshes and let the hit attribute the point. With a co-located pair
  the upper surface always wins, so the occluded mesh's correspondence is
  structurally unreachable — "no way to pick the second". The raycast
  machinery itself is sound (double-tap recentre and the probe use the same
  path successfully); the defect was nearest-hit ATTRIBUTION, which the panes
  remove by construction (pane = mesh = attribution).
- **T1 — deletions:** the crosshatch/weave suitability overlay
  (`SuitabilityCoverage` + `SuitabilityComposite` shaders,
  `MeshView.buildSuitabilityNode`, `OutlineView.buildSuitability`, SceneGraph
  wiring) and the whole Pair-bolted placement path: main-view Sg.OnTap
  placement/edit pick routing, the flashlight (`placementHover` cval +
  throttled raycast + `previewBlob` + `ghostPreview`), the main-view
  `draftMarkers`, the Pair workspace draft bar, the crosshair pickModeOn, and
  the ENTIRE point-edit arming machinery (`PinEditState`/`Edit` field,
  `BeginPointEdit`/`CancelPointEdit`, row ·N edit buttons, Esc-chain entries)
  — a pane click IS the arming. `EditPointAt` (the atomic replace) stays. No
  FS0049/25/26; leftover-symbol grep clean.
- **T2 — the panes** (new `GuiPanes.fs`, `.pin-panes` overlay over the
  central area): two side-by-side secondary render controls, mesh A | mesh B
  of the selected pair, each with its OWN orbit camera (`Model.PaneCamA/B` +
  `PaneCamMessage`, re-seeded to the sensor framing on pair change), identity
  chip top-left. Hidden by `visibility` (never display:none — a collapsed
  render control loses its viewport) + `Sg.Active` gating so hidden panes
  cost ~nothing; controls are (re)built per selected pair. Pane meshes render
  through the SHIPPED `MeshShader.shade` (lean constant uniforms) so
  rendering modes and the ghost floor behave identically.
- **T3 — transaction on the panes:** pane clicks (click-vs-drag: pointer-up
  within 4 px) raycast ONLY the pane's mesh server-side (`Sg.OnTap` is
  unreliable in secondary controls — Dom + raycast per the gotcha), the hit
  lands directly in the mesh's own frame: Area tool → `DraftAreaAt` (either
  pane; the pane's mesh anchors), Points tool → `DraftPointAt` (pane A ⇒
  point A, pane B ⇒ point B, free order); the "N of 2" cue + tool bar +
  ✓ Commit/✕ abort moved into the Pin rail column (`pinLevelView`);
  `BeginPinTransaction` now jumps focus to Pin; **leaving Pin mid-placement
  aborts** (`jumpFocus`: Focus=Pin → other ⇒ Placement=Idle; Esc =
  FocusAscend, so Esc-aborts falls out). Commit auto-selects the newborn
  (A1). Existing pin: select in Pair (dbl-click descends), pane click
  re-picks that mesh's point; radius + delete in the Pin rail. Committed
  markers in panes = mesh-colour icosphere fill + white wire outline
  (`ScanPinScene.sphereShell` made public); draft = all-white wire marks.
- **T4 — armed placement = isolate-overlap:** each pane renders its own
  coverage MRT from ITS camera (`OutlineView.coverageOffscreen info model
  paneView paneProj` — the shipped shared machinery) and feeds the shipped
  `OverlapPreview` gate in the pane's mesh shader while `Placement` is
  active: solid only where BOTH pair channels cover the pixel, rest at the
  ghost floor; >8-channel pairs disable the gate outright; disarm restores
  the full pane view.
- Main-view simplifications that fell out: `pinBlobUniforms` lost the
  flashlight append; `anchorGhost`/GhostOpacity lost their placement
  special-cases; Esc chain = loop modal > probe > FocusAscend.
- Build green (FS0044 ×4 pre-existing only). Owed to the browser pass —
  HIGH-RISK items: MeshShader compile in the secondary pane contexts (FShade
  is browser-verified only), per-pane coverage MRT + overlap gate, pane
  orbit/wheel/pick feel, pane marker rendering, per-pair control rebuild
  cost, main-view GPU cost under the overlay.

## A1: four-level focus rail + selection manager + per-level visibility (2026-07-28)

Amendment `ScanPin_v14_A1` — navigation/selection/visibility only.

- **T1 — the focus rail** (`FocusLevel = FocusSetup | FocusMatrix | FocusPair
  | FocusPin`, Model.fs; `GuiRail.railLevels` + `.rail-levels`/`.rail-stop`
  CSS): four stops above the rail body, free jumps among ENABLED stops
  (`SetFocus`, reducer re-guards via `FocusLevel.enabled`): Setup/Matrix
  always, Pair needs a chosen pair, Pin a chosen pin or an in-flight
  placement. Levels are scopes, not tool modes — the pair toolkit (pins,
  Solve, inspect, peeks) stays inside the Pair workspace. DELETED:
  `NavLevel`, `MatrixHome` (+ tabs/`matrixHomeView`, `.pmx-home`/`.pmx-tabs`
  CSS), `SetMatrixHome`/`DescendPair`/`NavAscend`, the workspace ‹ back
  button. No FS0049/25/26 from the case deletions; grep clean.
- **T2 — selection manager** (`FocusSelection = { Pair; Pin; Point }`, ONE
  plain-record aval `Model.Sel`): matrix cell click = `SelectPair` (selects +
  enters Pair); pin row click = `SelectPin` (enables the Pin stop; commit
  auto-selects the newborn pin; deleting the selected pin clears it).
  MEMORY: re-selecting the remembered pair keeps its pin + in-cell caches
  (no refetch on Pair⇄Matrix jumps — caches now ride the SELECTION, not the
  visit); `.pmx-sel`/`.cw-pin-sel` highlights. CASCADE: a new pair clears
  pin+point + caches + rolls back a placement bound to the old pair; root
  designation (`SetRegRoot`) and dataset switch clear ALL selection.
  `normalizeFocus` postlude demotes focus to the nearest enabled ancestor
  whenever a step retracts its subject (pin deleted, placement aborted).
- **T3 — per-level visibility**: `MeshVisibility.shown focus selPair isolate
  hoverPair name` — Setup/Matrix all meshes (Setup isolate / matrix hover
  narrow transiently), Pair/Pin isolate the selected pair; ascend restores by
  construction. `MeshVisibility.pinShown` mirrors it for pins (blobs +
  scene nodes). All consumers rewired: MeshView `shownCtx` (ONE context aval
  → N cheap per-mesh projections), View `shownNow`, cell paint, legend,
  peeks (`Sel.Pair` + pair-scope), overlap preview (Matrix scope),
  `ensureCellError`/`ensureCellDist` gates (Pair/Pin scope).
- **T4 — Esc + free nav coexist**: Esc = `FocusAscend` (one level, innermost-
  cancel chain unchanged ahead of it); rail jumps and Esc share ONE
  `jumpFocus` path (kills level-scoped transients: isolate, hover, peeks,
  probe; leaving pair scope rolls back placement/point-edit), selection
  memory untouched by jumps.
- A1-scope Pin level = an identity stub (`pinLevelView`) — A2 builds the
  two-pane surface.
- Build green (only pre-existing FS0044 ×4). Owed to the browser pass: rail
  enable/disable + jump feel, selection memory (pair re-entry restores pin +
  chart instantly), cascade clears, Esc chain order.

## Polish: matrix-cell hover = 3D overlap preview (2026-07-28)

- Hovering ANY real matrix cell (incl. impossible — it shows why) previews
  the pair's overlap area in 3D: only pixels covered by BOTH meshes'
  screen-space footprints keep normal rendering, the rest drops to the ghost
  floor. Screen-space by design (the camera-ray test ≈ the vertical Z-pierce
  from top-down); the server-mask variant stays the upgrade path if the
  view-dependence ever bothers.
- Zero new passes/fetches: the footprint coverage MRT now renders ONCE
  (`OutlineView.coverageOffscreen`, typed `aval<IBackendTexture>` — NOT
  aval<ITexture>, aval isn't covariant) and is shared via SceneGraph with
  the footprint composite AND the forward mesh shader.
- Shader: `OverlapPreview` + 4 `OverlapSel*` channel-selector V4fs in
  `MeshShader.shade` — coverage sampled at gl_FragCoord/ViewportSize,
  both-channels > 0.12 (one additive 0.25 layer) ⇒ solid, else the ghost
  path (α-gated depth/picks follow free). Selectors built in
  `MeshView.overlapPreviewUniforms`: home-scope gated, pair mesh beyond the
  8-channel cap disables the preview outright.
- State: `Model.MatrixHoverPair` + `SetMatrixHoverPair`; threaded through
  `MeshVisibility.shown nav isolate hoverPair name` (other meshes ghost) and
  View `shownNow`. Wiped on cell leave, DescendPair (the click leaves no
  mouse-leave!), tab switch, dataset switch. Cell hover ring + cursor CSS.
- Owed to the browser pass: the shader edit (sampler bindings, the
  both-channel gate) AND that footprint contours still render after the
  shared-MRT refactor.

## Polish: setup rows two-line + isolate, matrix diagonal (2026-07-28)

- **Setup survey rows are two-line** (GuiRail `surveyRow` + `.pmx-root-*`
  CSS): head line = swatch · number · name · ★; control line = the buttons.
  Root designation moved OFF the name click onto an explicit **☆ Set
  reference / ★ Reference** button (gold `setup-ref-on` state, token
  colours); the name-click root hazard is gone. Double-click on the head
  line = the focus interaction (FlyToSensor). The ◎ fly-to-sensor button and
  the name-dblclick ZoomToMesh are gone — `ZoomToMesh` message + handler
  DELETED (no emitters left; ZoomToPin stays).
- **Setup isolate** (replaces the focus button): `◉ Isolate` per row —
  hover = transient preview (`SetupIsolateHover`), click = lock
  (`SetupIsolate`), click again clears; the reducer wipes both on leaving
  Setup (`SetMatrixHome` away) and on dataset switch. Threaded through THE
  visibility rule: `MeshVisibility.shown nav isolate name` (signature
  changed) — render MeshActive (MeshView `isolateA`, hover wins over lock)
  and the event-time raycast candidates (View `shownNow`) both follow, so
  non-isolated meshes drop to the ghost floor and are unpickable.
- **Pair matrix carries a cosmetic diagonal**: full n×n grid (head row/col
  0-based now), `j < i` void, `j = i` = `.pmx-diag` inert placeholder
  (dashed border + 45° slash), upper triangle unchanged. Last row/first
  column exist solely for the diagonal.
- Gold literals in `.pmx-root-on`/`.pmx-root-star` (#b45309) replaced with
  `var(--ref-gold-dark)` per the token rule.
- Docs updated alongside (README Setup/matrix bullets; CLAUDE.md visibility
  rule). Build green, no FS0049/25/26 from the ZoomToMesh case deletion.
- Owed to the browser pass: two-line row layout, hover/lock isolate feel,
  diagonal look.

---

# ═══ SPEC PHASE COMPLETE — POLISHING/ADJUSTING PHASE (from 2026-07-28) ═══

Everything below this line is the v14 spec-implementation record (P0–P9 +
dead-code pass + doc reconstruction), committed and pushed as `8379bb6`.
Everything above is the polishing phase: user-driven adjustments against the
running app. The docs freeze is over — CLAUDE.md/README.md now update
normally alongside changes; keep logging entries here as work lands.

Open from the spec phase: the ONE whole-app browser pass (per-phase owed
items listed in the entries below, incl. the OutlineGBuffer bias-removal
shader edit).

## v14 documentation reconstruction (2026-07-27) — FREEZE LIFTED

README.md + CLAUDE.md rewritten from this log; every per-phase DOC DEBT item
resolved.

- **README**: "What you can do" rebuilt around the v14 shape — navigator
  hierarchy (Setup survey/root ★ + pair matrix ⇄ cell workspace), atomic pin
  transaction, pairwise solve into the registration tree, loop dialog,
  in-cell chart/brush/probe/error map, V/B peek keys, Space-hold clean view,
  near-plane Cut. Server blurb now names the pairwise error measure.
- **CLAUDE.md**: state rules rewritten (registration graph + invariants/
  hazards, navigation + ONE Esc chain, atomic pins, in-cell caches, peeks);
  render section updated (passTwo, near cut, in-cell error range, ONE chart,
  legend two-state, suitability trigger, OutlineWidthPx, OutlineMask now
  all-on); coordinate rules updated to composed poses + own-frame pin
  geometry; pano-centre consumers corrected; API list + compile orders
  regenerated from the fsprojs; NEW "F# pitfalls" section (deleted-DU-case
  catch-all patterns, Trafo3d struct equality, apply-a-first composition,
  Result shadowing). Durable sections (comment discipline, DepthMask ban,
  outline-pass rationale, adaptive-perf rules, Dom/FShade gotchas) kept.
- Removed with the rewrite: every v13 `(TODO: …)` marker, the three-mode
  rail/selection/focus-panel/dock/slice-cell/probe-star/Before-After
  descriptions, `/query/probe|slice` + MeshProbe/ProbeModel/FocusScene/
  FocusShaders/GuiFocus/GuiInspector references.
- Final state: client + server builds green, Supertests 102/102, integration
  29/29. Outstanding: the ONE whole-app browser pass (per-phase owed items
  above, incl. the OutlineGBuffer bias-removal shader edit).

## v14 dead-code elimination pass (2026-07-27)

Post-P9 cull of everything the rework orphaned. Client + server builds green,
Supertests 102/102 (−3 dip tests), integration 29/29 on a fresh :8002.

**Server:**

- `/api/query/slice` DELETED (route + `sliceHandler` + `SliceMeshDto`/
  `SliceRequest`) — the parked P3 decision resolved as prune: no client
  consumer since the slice-cell matrix died, integration coverage alone
  doesn't justify the endpoint.
- `MeshAnalysis.fs` → only `contactRings` (`dipAzimuth` + `planeSlices`
  deleted); `MeshAnalysisCore.fs` lost `dipFromMoments`/`dipOfPoints`
  (slice-azimuth fitting machinery — `traceLevelSet` + `decimate` stay,
  contact rings use them).
- `region-distance` mode 1 (vertical Δz) deleted: `RegionDistanceRequest.Mode`
  field + the Δz branch — the endpoint has exactly ONE metric again (M3C2
  closest-point with the Z-overlap support gate).
- `bboxes` no longer computes/returns `spacing` (mean edge length) — its only
  consumer was the slice-cell window; the edge-sampling loop went with it.
  Handler is now purely bbox + cache warmer.

**Client:**

- `Model.DebugLog` + the gear-popover log display (GuiTopBar), `.tb-gear-log`
  CSS. `ModelTransforms.edgeRender`, `ScanPin.pointOn`,
  `ScanPinUpdate.activeScale`, `Query.closestPoint` wrapper.
- `Query.regionDistance` no longer sends `"mode":0`; `fetchBboxes` +
  `SceneBoundsLoaded` payload narrowed to `(string * Box3d)[]`.
- `MeshView.buildReferenceOutlineNode` (reference gold outline — no reference
  concept in v14), `OutlineDepthBias` uniform + its shader term
  (`OutlineGBuffer.shade` writes plain `v.fc.Z`), `OutlineView.maskAllOn`.
- `LineShader.LineGlyphs`: `addDashedRing`/`addDashedRingXY`/`addArrow`/
  `addArrowXY`/`addCrossXY` deleted (kept: duplex, basisFromNormal, addRing/XY,
  addWireSphere, addCross, addBoxOutline).
- `Primitives`: `numberedFriendly`, `pinInkCss`, `showWhenNot`, mesh-level
  `shortName` (the `friendlyName` fallback now uses `meshLocal`).
- CSS orphan sweep: `.tb-gear-log`, `.placement-tooltip`,
  `@keyframes corr-flash-pop` removed; compound-selector orphans the
  class-token sweep can't see caught by hand (`.focus-set.btn-active`,
  `.tb-regview-btn/.tb-regview-peek`, `.rail-btn-primary`); `--dock-h`
  (permanent 0 since P5) dissolved into plain offsets; stale rail/roster
  comments + a §-spec-ref comment fixed.
- **Latent bug found + fixed**: the gear popover still had an "Isolate pins"
  copy whose gate matched the P6-deleted `AnchorPlacement` case — F# silently
  reparsed it as a catch-all VARIABLE pattern (warning FS0049 only), so
  `placing` was constantly true and the toggle permanently inert. Deleted (the
  cell workspace hosts the real toggle). Full-rebuild warning tally now clean
  of FS0049/FS0025/FS0026 — NEW PITFALL for CLAUDE.md: deleting a DU case
  turns its remaining patterns into catch-alls, and only warnings betray it.

**Tests:** Supertests dip-fit trio deleted (dipOfPoints gone);
`tools/integration.mjs` slice §5 + Δz lift test deleted, sections renumbered,
the `lift` matrix moved to the pair-error-at section (its other consumer).

Browser verification still owed (with the per-phase items): the
`OutlineGBuffer` shader edit renders correctly (bias removal).

## v14 P9 — loops + forced path resolution (2026-07-27) — FINAL PHASE

Spec: `ScanPin_v14_P9_loops_path_resolution.md`.

- **T1 invariant change**: `RegGraph.tryAddEdge` now returns `EdgeAddResult` —
  `EdgeAdded` / `EdgeClosesLoop (cycleEdges, residual)` / `EdgeRejected`.
  Both-endpoints-connected is TRANSIENTLY accepted (exactly one fundamental
  cycle), never committed; disconnect-adds stay impossible. New pure
  machinery: `treePath` (directed a→b steps via LCA, up = Transform, down =
  Inverse), `loopResidual` (tNew ∘ path(ref→mov) — identity iff the paths
  agree), `residualRotationDeg` + `residualAt` (displacement at a probe
  point — rigid conjugation preserves translation length, so an injected 5 cm
  reads 5 cm exactly, test-pinned). `SolvePair` no longer refuses redundant
  pairs (orientation from `pairRefMov`); `PairSolved` distinguishes a
  same-pair re-solve (parent check — `Map.containsKey` alone would misroute
  a redundant child that keys an edge elsewhere) from a loop-closing add.
- **T2 the blocking modal** (`GuiOverlays.loopModal`, `.modal-scrim` z-300):
  "Two paths now connect these meshes" + "These paths disagree by X.X° and
  Y cm" (displacement read at the MOV mesh's centroid); rows = the NEW edge
  + every cycle edge, each with its single-scalar quality, WEAKEST
  pre-selected; pick exactly one to remove; Confirm → `ConfirmLoopResolution`
  (remove + recompose + invalidate cell caches + `bumpPairSolve`), Cancel/Esc
  → `CancelLoopResolution` (redundant edge discarded, prior tree stands).
  Esc chain: the modal now WINS (modal > transaction > point-edit > probe >
  ascend); peek keys refuse while it is open; dataset switch clears it. State
  = `Model.LoopPending` (`LoopPending` record in RegistrationModel; the
  committed graph never holds the loop). No 3D choreography, no standing
  overlay.
- **T3 tree restored**: `RegGraph.resolveLoop (mov, ref, tNew, q, removeChild)`
  — removes ONE cycle edge; the detached subtree (containing exactly one
  endpoint — the cycle crossed the removed edge once) re-hangs through the
  new edge, its internal path reversing like a reroot; the new edge INVERTS
  when the REF side detaches. Result proven in tests: spanning tree over the
  same members, every kept edge constraint pose(c) = T∘pose(p) holds (unique
  poses), an exactly-agreeing loop resolves with poses unchanged, MOV-edge
  and REF-side removals both correct.
- Supertests 105/105 (+12 loop/resolve; all old cycle-rejection tests updated
  to the transient semantics). Client + server builds green. Browser pass
  owed: modal look/flow on a real redundant solve.

**v14 phases complete (P0–P9).** Outstanding before doc reconstruction:
ONE whole-app browser pass (owed items listed per phase above) + the parked
decision on the server `/query/slice` endpoint (client-consumer-less since
P3; kept for integration coverage — prune or re-consume).

## v14 P8 — peek system, blink comparator (2026-07-27)

Spec: `ScanPin_v14_P8_peek_system.md`.

- **T1**: already clean — the ensemble Peek button + hotkey I fell in P2
  (grep-verified; only stale comments remained, fixed).
- **T2 two spring-loaded keys**, cell scope only, zero config, REF/MOV from
  the tree (`MatrixNav.pairRefMov`, nearer-root = REF), "before" = as-loaded
  always: **V** (visibility) — the MOV blinks OFF outright while held, the
  REF alone answers "same rock?" (design call: hidden means Sg.Active off —
  never the ghost floor, a blink needs a clean swap; pin annotations stay);
  **B** (pose) — the MOV displays AS-LOADED instead of composed while held,
  REF static ("did registration help?"). `Model.PeekVis`/`PeekPose` +
  `SetPeekVis`/`SetPeekPose` from view key down/up (reducer idempotence
  absorbs key repeat); presses REFUSED unless in a cell AND both pair meshes
  are resident (`MeshesLoaded`) — releases always land; peeks clear on
  descend/ascend/dataset.
- **T3 perceptual constraints**: instant swap — vis peek = Sg.Active flip on
  the main surface + outline G-buffer + footprint coverage + suitability
  nodes (`MeshView.peekVisHiddenAt` in all four actives — no silhouette or
  footprint remnant); pose peek = a trafo change through the ONE
  `displayedMeshT`/`displayedWorldAt` pair (now `peekMovAt`-aware, so
  surface-riding pin markers follow the blink; the offscreen outline pass
  shares displayedMeshT and follows automatically). Zero refetch: the error
  map rides MOV's surface during the pose peek (registered-pose values,
  purely visual — documented). Both states GPU-resident by construction
  (same geometry; residency gated at the press). No camera writes, no UI
  reads the peek state (zero reflow), whole meshes swap, wheel stays orbit
  zoom, no indicator control (eyes-free), no auto-blink/cycling/config.
  Reducer-side queries keep the COMMITTED pose (`ModelTransforms` untouched
  by peeks).
- Supertests 93/93; client + server builds green. Browser pass owed: blink
  feel (V), pose jump (B), residency refusal on a cold pair.

## v14 P7 — in-cell error inspection (2026-07-27)

Spec: `ScanPin_v14_P7_incell_error_inspection.md`. Consumes P0's
`/query/pair-error` + `/query/pair-error-at` + the kept `/query/region-distance`.

- **T1**: already-clean verification — the two-diagram design, slice/stretch/
  ordinate/dot-glyph machinery and the M3C2|Δz toggle all fell in P3/P5
  (grep-verified zero remnants; the client hardcodes region-distance mode 0,
  no metric UI exists — the measure stays opaque).
- **T2 the ONE diagram** (cell workspace, `GuiRail.chartJs` + `chartData`):
  the MOV mesh's error across the pair's pins, pin-source-STACKED (48-bin
  histogram, achromatic pin ramp, per-pin median ticks, mean-LoD band, mm
  axis), titled "Mesh N error vs M — across pins"; per-edge before/after diff
  = fill (current poses) + near-black step outline (edge-BEFORE poses via
  P2's `composeEdge`) with a "fill now · line before" key — registered pairs
  only. Placeholder furniture when pinless. Data: `Model.CellError`/
  `CellErrorBefore` per-pin `Query.pairError` batches at displayed poses
  (before-batch pin ROIs follow the anchor mesh's edge-before pose), samples
  stored MOV-relative-to-REF (sign flipped at landing when MOV = meshA);
  REF/MOV from new `MatrixNav.pairRefMov` (edge parent, else hop depth,
  unconnected = MOV, tie → key order). Lazy single-flight postludes
  (`ensureCellError`/`ensureCellDist`), ONE `cellErrorGen` guard bumped by
  `invalidateCellError` on descend/ascend/pin edits/solve/edge-drop/reroot/
  dataset.
- **T3**: diagram x-drag brush → `SetBrushedSamples` gid set (≤200; gid =
  index into the canonical CellError sample concatenation) → white ink-under-
  stroked 3D glyphs (`brushedSampleNode`); bidirectional: 3D hover (screen-
  space nearest ≤12 px, 80 ms throttle) → `SetHoverSample` → amber diagram
  cross-highlight + amber 3D glyph + exact value via pair-error-at into the
  workspace readout. Armed probe: ⊕ toggle by the map toggle; while armed any
  3D pick → pair-error-at → "probe ±N mm" readout — fully transient
  (`ProbeReadout` wiped on disarm/ascend; no persistence, no diagram link).
  False-colour map: `CellDist` = region-distance MOV vs REF at displayed
  poses, painted on MOV ONLY (never the reference; `InspectPlain` swaps only
  MOV's base to near-white), toggleable ("Error map"), suppressed by a
  non-empty brush (brush = sole focus, established grammar); range = new
  `ErrorRange.ofSamples` (pin-ROI samples, span-0, cap ±0.5 m) shared by map
  uniforms + diagram + legend; legend gains the diverging branch back
  ("Difference N vs M"). NO isolation in inspect: both pair meshes render
  as-is (the P5 nav rule is scope, not isolation). Esc chain: transaction >
  point-edit > probe disarm > ascend.
- Supertests 93/93 (+4 pairRefMov). Client + server builds green. Browser
  pass owed: diagram rendering/brush feel, glyph highlight, readouts, map on
  MOV with legend, before/after outline after a solve.

## v14 P6 — atomic pin placement, in-cell (2026-07-27)

Spec: `ScanPin_v14_P6_atomic_pin_placement.md`.

- **T1 DELETED**: `Correspondence`/`MeshAnchor`/`AnchorSource` types + the
  `ScanPin.Correspondence` field; auto-seeding wholesale (`seedAnchorsCore`/
  `seedAnchors`, `AnchorsSeeded`/`AnchorSeedFailed`, `updateCorr`/`allPinIds`,
  SetRegRoot's re-seed); ROI machinery (`roiReach`+`fixedProbeLength`, InRoi);
  the whole star readiness/solve-filtering stack (`Readiness`, `ReadinessPin`/
  `ReadinessInput`/`Diagnostic`/`Severity`, `RegConditioning` — server RegMath
  keeps its own conditioning) + their Supertests sections; the old
  `PlacementState.AnchorPlacement` click-place flow with its ≥2-overlap
  hard-prohibit (`countOverlap`, `placementValid`, ghost fade,
  `placementTooltip`) — atomic placement picks points explicitly, so the
  prohibition is meaningless.
- **T2 atomic pin + transaction**: `ScanPin` = { Pair (PairCell.key order),
  AnchorMesh + CentreLocal (the pin RIDES its placement mesh), InnerRadius,
  PointA + PointB (own-frame, NON-optional — no partial pin exists),
  ShortName, CreatedAt, ContactRings }. `PlacementState.PlacementActive of
  DraftTool × PinDraft` — modal, FREE ORDER: ◯ Area / ✚ Points sub-tools
  re-armable, clicks route by tool (area = raycast whatever pair mesh is
  under the cursor → anchor there; point = the HIT MESH attributes the slot,
  re-pick replaces), "area ✓ · N of 2 points" cue lives only in the draft
  bar, ✓ Commit enabled only complete (the one birth path), ✕/Esc abort =
  full rollback (the draft never touches the pin map). Points are
  UNCONSTRAINED (outside-ROI legal; ROI scopes analysis only). Draft renders
  all-white (uncommitted layer): area wire sphere + point wire-sphere/cross;
  flashlight ghost only while ToolArea aims.
- **T3 anchoring/solve/edit**: pin world centre = displayedWorld(AnchorMesh)
  ∘ CentreLocal everywhere (scene nodes token-read poses; blobs/rings/zoom/
  flags updated; rings now fetch the pair's two meshes only). `SolvePair`
  (cell toolkit ⌖ button, enabled ≥3 pair pins): orientation = existing edge
  (re-solve) else un-treed mesh MOV → treed REF; both-in-tree ⇒ cycle refusal
  toast, neither ⇒ connect-to-root-first toast. Pairs feed `/query/lsq-pairs`
  at the AS-LOADED baselines (edge transform = child-baseline onto
  parent-baseline, P1 convention — ancestor registration composes on top);
  `Query.lsqPairs` re-added returning transform + residuals; landing
  (`PairSolved`, `pairSolveGen`-guarded) → `tryAddEdge`/`withEdge` with
  quality = `RegGraph.solveQuality` (1/(1+rms/0.05), halves at 5 cm rms) +
  recompose. ANY pin edit (radius/point re-pick/delete) with a registered
  pair → `RegGraph.removeEdgeCascading` (the edge + its whole subtree — a
  stranded component would break the invariant) + recompose + toast + gen
  bump (in-flight solves land dead). Edit mode = `PinEditState.EditPoint`
  (pin row ·N buttons arm; single-mesh raycast replaces the point atomically;
  never partial). Committed markers = MESH-COLOURED FILL + WHITE OUTLINE
  (mini icosphere + white wire sphere) on both pair meshes. ONE Esc chain:
  transaction abort > point-edit disarm > ascend.
- Cell workspace: New pin / ⌖ Solve (n/3) / Isolate hide during a
  transaction; pin rows = name · r log-slider · point re-pick per mesh · ✕.
- Supertests 89/89 (+8: leaf/mid-tree cascade + reference-equal branch,
  withEdge payload, quality bounds/monotonicity/calibration). Client+server
  builds green. Browser pass owed: full transaction flow, draft visuals,
  marker attribution colours, solve round-trip, edit invalidation toast.
- DOC DEBT (frozen): CLAUDE.md correspondence/seeding/readiness bullets,
  placement suitability hard-prohibit, pin-anchor conventions — superseded.

## v14 P5 — descend/ascend hierarchy; rail + global selection deleted (2026-07-27)

Spec: `ScanPin_v14_P5_hierarchy_descend_ascend.md`. The navigation rework —
the largest P-phase deletion. The app is now: 3D view + top bar + the left
navigator (matrix-home ⇄ cell-workspace) + overlays. The remaining pair tools
land P6–P8.

- **T1 DELETED wholesale** (with every consumer branch):
  - `WorkflowStep` (type/module/field/message; all per-mode branching:
    Inspect gates in MeshView/legend, Correspondence gates for isolation +
    constellation, per-mode isolation defaults in SetWorkflowStep).
  - The GLOBAL SELECTION model: `ActiveSelection`/`Selection`/`HoverTarget`,
    `SetSelection`/`SetHovered`, every panel's selection consequence, hover
    peeks (`wheelIsolation`), 3D pin-dot select taps.
  - `MeshSolo` + `enterSolo`/`exitSolo` + `LocateState`/`LocateBackup`/
    `BackOutLocate` (the locate machinery).
  - Whole FILES: `GuiFocus.fs`, `FocusScene.fs`, `FocusShaders.fs` (the
    selection-framed focus panel), `GuiInspector.fs` (the dock: charts,
    manager, XYZ editor, shift readout, brush bridge), `ProbeModel.fs`.
  - With their only drivers gone: sample BRUSHING (`BrushedSamples`,
    `SetBrushedSamples`, `ScanPinScene.brushSamples` + glyph builders,
    `BrushDotPx` gear), the exact-point probe (`PointProbe`, `probeValueAt`),
    correspondence ARMING/PICKING (`CorrArm`/`CorrPreview`/`ToggleCorrArm`/
    `CorrPreviewComputed`/`PickCorrespondenceAt`/`CorrFlash` + the 3D aim
    ghost/arrow + flash overlay + `raycastMesh`), the client PIN-PROBE layer
    (`ScanPin.Probe`, `ProbeState`/`ProbeResult` DTOs, `ensureProbe`,
    `Query.probe` — dead against the P0-deleted endpoint anyway; `ScanPin.axis`
    → `axisWith` now globalUp-or-world-up), the Inspect difference stack
    client-side (`FocusDist`, `ensureFocusDist`, `FocusDistComputed`,
    `Query.regionDistance`... KEPT `Query.regionDistance`? NO — wait, see
    below), `MeshView.inspectRange`/`inspectField` + per-mesh outline gating
    (`outlineFlagAt` → mask all-on, depth bias 0), pin messages without hosts
    (`SetInnerRadius`, `DeletePin`, `ProbeComputed/Failed`), the orphaned
    query wrappers `Query.lsqPairs` (dead since P1; P6 re-adds) and
    `Query.regionDistance` (its ensureFocusDist caller went with the Inspect
    stack; P8 re-adds), `ClickGate` + `ReadinessView` adapter (Readiness
    ENGINE kept — tested, P6-bound), the selection circle + constellation +
    hover pulses in ScanPinScene, `SceneGraph.focusedOutline`, the top-bar
    focused-mesh coordinate branch, `FocusScene.dpr` publisher.
  - Legend reworked to Range-heatmap-only; ~106 orphaned CSS blocks pruned
    (.ins-*, .focus-*, .rail-step*, .mx remnants, tb-regview, dock resize...);
    `--dock-h` → 0 (dock gone; bottom overlays reach the bottom edge).
- **T2 hierarchy**: `NavLevel = NavHome | NavCell of a*b` (`Model.Nav`,
  dataset-reset), `DescendPair` (Possible/Registered cell click — impossible
  cells are inert holes) and `NavAscend` (Esc + the workspace ‹ button; at
  home a no-op). `MeshVisibility.shown` REDEFINED on Nav: home = all, cell =
  the pair only — consumed by render MeshActive, raycast candidates and the
  placement overlap count. Cell-workspace panel: persistent A↔B header
  (swatch·num·name chips + ↔), live pair-state line (registered quality /
  not yet / insufficient), back control. Esc order: armed placement cancels
  first, then ascend (the single backward primitive).
- **T3 per-pair toolkit**: "○ New pin" + "Isolate pins" moved INTO the cell
  workspace (no global hosts remain); placement is pair-scoped through the
  ONE visibility rule (countOverlap + raycasts filter by Nav, so the ≥2-mesh
  rule = "both meshes of THIS pair"). No global mode state exists.
- Green: client + server builds, Supertests 98/98 (pure layers untouched).
  Browser pass owed: whole-app navigation flow, workspace header, ghosting of
  non-pair meshes in a cell, placement inside a cell.
- DOC DEBT (frozen): CLAUDE.md sections on the three-mode rail, ONE-selection
  model, focus panel, dock, brushing, exact-point probe, solo/visibility,
  Esc grammar — all superseded by the hierarchy.

## v14 P4 — reference-root designation (2026-07-27)

Spec: `ScanPin_v14_P4_root_designation.md`.

- **T1 old star reference picker DELETED**: the roster ★/☆ toggle button
  (ClickGate single/double, toggle-off-to-None) is gone; the roster keeps a
  display-only `rail-root-mark` ★ on the current root.
- **T2 overview root designation** — a STATE of matrix-home, not a rail mode:
  `MatrixHome = HomeOverview | HomePairs` (+ `SetMatrixHome`, default
  Overview, reset per dataset), tabs "Setup | Pairs" atop the navigator;
  Setup = mesh survey rows (swatch·num·name·★, gold row accent) where click =
  `SetRegRoot name` (message narrowed from string option — None only ever
  meant pre-load).
- **True re-rooting** (`RegGraph.reroot`): designating a TREE MEMBER now
  keeps the registration — every edge on the path new-root→old-root reverses
  (Child/Parent swap = the REF/MOV flip, Transform inverted, Quality kept;
  path children removed before reversed re-adds — interleaving would drop
  edges), off-path edges untouched (subtrees follow their path node). All
  poses recompose relative to the new root: pose'(m) = pose(m)∘pose(newRoot)⁻¹.
  Reducer: member+edges → reroot + recompose + toast "registration kept";
  non-member with edges → clear + toast (a registered tree cannot hang off an
  outside mesh); no edges → plain designation. All paths re-seed anchors +
  invalidate probes/rings.
- Supertests 98/98 (+10: REF/MOV flip exactly on the path, off-path
  reference-equality, quality carry, pose'(m)=pose(m)∘pose(B)⁻¹ over all
  members, invariant survives (adds ok, cycles rejected), degenerate no-ops).
  Client + server builds green. Browser pass owed: tabs, gold root rows,
  roster ★ mark.
- DOC DEBT (frozen): CLAUDE.md roster-★/reference-picker wording, "reference
  change wipes solve" rule (now: member re-root PRESERVES registration).

## v14 P3 — mesh×mesh matrix navigator, read-only (2026-07-27)

Spec: `ScanPin_v14_P3_matrix_navigator.md`. Old pins×meshes matrix deleted
wholesale; new upper-triangular mesh×mesh navigator (read-only — descend/root
actions arrive P4/P5).

- **T1 pins×meshes matrix DELETED** — and with it the entire client slice-cell
  ecosystem (its only consumer): GuiRail's `SliceDiagram` module + boot JS,
  `refProfile`, `CellInfo`, matrixHead/matrixRow/matrixRows/matrixView, the
  window/vert-extent global scales, all `.mx-*` CSS (27 rules);
  `ScanPin.Slice` + `SliceState`/`PinSlice`/`SliceMesh` DTOs +
  `SliceComputed`/`SliceFailed` + `ensureSlices` + `invalidateSlices` +
  `Query.slice` + the slice helpers (`sliceWindow`/`sliceOffsets`/
  `sliceClipRadius`/`sliceCentreIndex`/`sliceNormalOf`) + `Model.MeshSpacing`
  + the four gear slice tunables (fields, messages, sliders). SERVER
  `/query/slice` + MeshAnalysis.planeSlices/dipAzimuth KEPT (integration
  tests cover them; the P7/P8 per-edge cell diagrams are their likely
  consumer — prune at rework end if none materializes).
- **T2 mesh×mesh navigator** (`GuiRail.pairMatrixView`, `.pmx-*` CSS): rows/
  cols = meshes, upper triangle only, no diagonal; cell (A,B) IS the pair
  edge. Emphasis ramp: impossible = background hole (`.pmx-imp`) < possible =
  outlined vessel (`.pmx-pos`) < registered = filled (`.pmx-reg`), fill
  strength = the edge's ONE quality scalar (achromatic ink alpha 0.30+0.65·q
  — colour families stay free). `RegEdge.Quality : float` added ([0,1], 1 =
  best; `tryAddEdge` takes it; P5's solve writes it). Pure state fn
  `PairCell.state` (RegistrationModel): registered (either orientation, via
  new `RegGraph.pairEdge`) beats overlap verdict; unfetched overlap reads
  impossible. Overlap data: `Model.PairOverlaps` (unordered `PairCell.key`),
  lazy `ensurePairOverlaps` postlude — all missing pairs fetched in parallel
  at the as-loaded baselines via new `Query.pairOverlap` (P0 endpoint),
  single-flight per generation (dataset switch bumps), ONE
  `PairOverlapComputed` batch. Mounted in Register + Inspect rail bodies.
- **T3 reordering**: `MatrixOrder` (OrderSensor | OrderCoverage |
  OrderConnected) + `MatrixNav.hopDepth`/`orderMeshes` (canonical tiebreak;
  coverage = bbox XY footprint; connected = root, hop distance, unconnected
  last) + `Model.MatrixOrder`/`SetMatrixOrder` + the "Order
  Sensor·Cover·Root" compactButtonBar above the matrix. Contents proven
  order-invariant in tests.
- Supertests 88/88 (+12: pair-cell states, quality carry, hop depth, the
  three orders, permutation + content-invariance); client + server builds
  green. Browser pass owed: navigator layout/ramp legibility, overlap sweep
  landing (Hessigheim pairs should all read possible), order toggle.
- DOC DEBT (frozen): CLAUDE.md matrix bullet (pin×mesh, slice cells, gold
  column, dim cross), slice-cache state rules, slice gear tunables, matrix
  selection-driver wording — all superseded.

## v14 P2 — per-edge before/after (2026-07-27)

Spec: `ScanPin_v14_P2_per_edge_before_after.md`. Pose semantics only; the
per-edge peek/diagram UI consumes it in P5/P7/P8.

- **T1 ensemble before/after DELETED wholesale.** Gone: `RegView` type+module
  and both model fields (`RegView`, `RegPeekHeld`), messages
  `SetRegView`/`SetRegPeek`, the top-bar Before/After/Peek cluster, hotkey I,
  `applyRegView` + `swapProbeViews` (the swap-in-place machinery), the whole
  Other-pose cache layer — `ScanPin.ProbeOther`/`SliceOther` fields +
  `ProbeOther*`/`SliceOther*` messages + their fetch halves in
  `ensureProbe`/`ensureSlices` (slice requests now send transformOther=None),
  `Model.FocusDistOther` + `FocusDistOtherComputed` + the other-pose branch of
  `ensureFocusDist`, every peek-selected cache read (3D inspectField, focus
  overlay/scalar, matrix cell slice choice), the dock charts' two-pose
  machinery (ha/hb halves, fill-vs-outline, "fill Before · line After" key —
  series now carry ONE `h` histogram), the After-refusal gates + toasts
  (placement/resize/arm/pick/XYZ-editor read-only) — with no global After
  state, editing is always legal. Displayed pose = composed graph pose else
  as-loaded baseline (`ModelTransforms.displayedRender`/`displayedWorld`, no
  view param); new `loadWorld` = the baseline in metric world — seeding + the
  pin-resize anchor kill evaluate there (was `displayedWorldAt RegBefore`).
  `MeshView.displayedWorldPeekAt`/`displayedWorldCommittedAt` merged into ONE
  `displayedWorldAt`; `effectiveRegView` deleted.
- **T2 per-edge before/after** (`RegistrationModel.fs`): `EdgeSide =
  EdgeBefore | EdgeAfter` + `RegGraph.composeEdge child side g` — Before =
  the committed graph with THIS edge's transform replaced by identity (pair
  unregistered against each other; every ancestor edge still applied through
  composition), After = committed. Model glue for later phases:
  `ModelTransforms.edgeWorld`/`edgeRender` (baseline fallback outside the
  tree). Supertests (+8, 76/76): on a 2-hop chain R←A←B, edge-B Before keeps
  ancestor A's pose value-identical and parks B at its parent's pose; edge-A
  Before moves the whole A-subtree while B keeps its own edge applied; purity.
- DOC DEBT (frozen): CLAUDE.md Before/After machinery (RegView bullet, probe/
  slice/FocusDist pose-pairing, Peek, Before-only correspondence guardrails,
  charts' pose key) all superseded; correspondence flows now evaluate at the
  as-loaded baseline.

## v14 P1 — graph state model + composed world poses (2026-07-27)

Spec: `ScanPin_v14_P1_graph_state_composed_poses.md`. Client pose/state core;
no new UI. In-app nothing creates edges yet (pair-solve flows = P2), so the app
runs pose-flat like an unsolved v13 session; the Before/After+Peek machinery
re-arms automatically once ComposedPoses is non-empty.

- **T1 star pose model DELETED**: `Model.SolvedTransforms`, `SolveInputs`
  (type + field), messages `SolveCoarse`/`CoarseSolved` + the whole ensemble
  lsq fan-out in the reducer, `ensureSolveValidity` postlude, `solveGen`
  guard, GuiRail's Solve button + `canSolve`. `LoadTransforms` (as-loaded
  baseline, identity per mesh) kept.
- **T2 registration graph** (`RegistrationModel.fs`, WASM-free → Supertests):
  `RegEdge` = { Child (MOV), Parent (REF = nearer root), Transform } — metric
  world, child-onto-parent at baseline (lsq convention); `RegGraph` =
  { Root; Edges : Map<child, RegEdge> } (a parent map ⇔ rooted tree).
  `RegGraph.tryAddEdge` enforces the committed invariant: accepted iff ref in
  tree ∧ mov not — both-in ⇒ cycle rejected, ref-out ⇒ isolated rejected.
  `withEdgeTransform` (re-solve), `children`, `inTree`, `hasEdges`.
  `Model.ReferenceMesh` REPLACED by `Model.RegGraph` (plain record → ONE
  aval); every reference-mesh read is now the graph root ((g).Root — ~30
  sites); `SetReferenceMesh` → `SetRegRoot` (root change clears the graph;
  same seed/invalidate/toast semantics). ReadinessInput keeps its own
  `ReferenceMesh` DTO field (engine input, fed the root).
- **T3 composed worldPose**: `RegGraph.composeAll` (BFS from root; pose(m) =
  edge.Transform * parentPose, apply-left-first; root = identity, absent from
  the map; a root-child's pose IS its edge transform ⇒ star graphs reproduce
  the old star poses exactly) + `RegGraph.composeSubtree` (memoized: only the
  changed child's subtree recomposes, prev entries carried through).
  `Model.ComposedPoses : Map<string, Trafo3d>` = the render-space projection
  (via `ModelTransforms.recomposePoses` — worldToRender per mesh), the
  drop-in successor of SolvedTransforms: `displayedRenderAt`/`displayedMeshT`
  /`displayedWorldPeekAt`/`displayedWorldCommittedAt`/FocusScene.cellZoom/
  SceneGraph bbox outline all read it at RegAfter; every solved-gate
  (SetRegView/SetRegPeek/applyRegView/ensureProbe/ensureSlices/
  ensureFocusDist/top-bar/charts/shift readout) = `ComposedPoses` non-empty;
  GuiRail corrStatus "aligned" = `hasEdges`.
- Supertests: +24 (invariant: no-root/self/cycle/isolated rejections,
  tree growth, children, transform replace; compose: star exactness, chain
  child-first order, sentinel-proven subtree-only recompute, incremental edge
  add). Trafo3d is a struct ⇒ memoization asserted via sentinel preservation,
  not ReferenceEquals. 69/69 green; client + server builds green (adaptify
  re-run).
- DOC DEBT (frozen): CLAUDE.md registration-state bullet (LoadTransforms/
  SolvedTransforms/SolveInputs wording), ReferenceMesh mentions, solve-flow
  description — all superseded by RegGraph/ComposedPoses.

## v14 P0 — server pairwise reference-free error (2026-07-27)

Spec: `ScanPin_v14_P0_server_pairwise_error.md`. Server only; client untouched
(its `/query/probe` calls now 404 at runtime — expected, later phases rework
the client onto the pair endpoints).

- **NEW `PairError.fs`** (replaces `MeshProbe.fs`, deleted with its fsproj
  entry): the established M3C2-style measure symmetrized. Per pin: shared axis
  n = PCA normal of the POOLED A∪B vertex set in the pin sphere (oriented
  n.Z ≥ 0); both surfaces lattice-sampled in the cylinder; sample value =
  signed axial offset of B relative to A (B-sample: t_B − median(t_A);
  A-sample: median(t_B) − t_A) pooled into ONE distribution per pin — swap
  A↔B negates every value. Median over the pooled set;
  lodHalfWidth = 1.96·√(σ_A²+σ_B²). Full derivation + sign convention in the
  module doc-comment. Poses always explicit in the request (server stateless
  w.r.t. the registration tree).
- **Endpoints** (QueryHandlers + Handlers routes; `/api/query/probe` +
  probeHandler + ProbeRequest/ProbeMeshDto DELETED):
  - `POST /api/query/pair-error` `{meshA{name,transform}, meshB{…}, pins:[{id,
    centre,radius}], length, maxPointsPerMesh}` → `{pins:[{id, ok, reason,
    normal, count, median, lodHalfWidth, samples, positions}]}` — per-pin
    ok=false on no overlap, batch never fails on it; samples ≤300/pin with
    aligned flat xyz positions (probe payload convention kept).
  - `POST /api/query/pair-error-at` `{meshA, meshB, point, radius, maxDist}` →
    exact signed value at one picked point: each mesh's surface crossing of
    the line (point + t·n) nearest the point, value = t_B − t_A; 1 mm ray
    origin back-off so exactly-on-surface picks still register.
  - `POST /api/query/pair-overlap` `{meshA, meshB, maxDist, minFraction,
    maxSamples}` → `{sufficient, fracAB, fracBA, maxDist, samplesA, samplesB}`
    — stride-sampled closest-point coverage both directions, sufficient ⇔
    max(frac) ≥ minFraction (default 0.05); maxDist default 1 % of mean posed
    bbox diagonal clamped [0.5, 20] m; posed-bbox pre-reject.
- Kept (comment-only touchups, "probe convention" → Forward wording):
  `region-distance` (already pairwise pose-explicit), `slice` (referenceName
  is azimuth-source only), `lsq-pairs`, `contact-rings`, `ray`, `closest`.
- `tools/integration.mjs` §4 rewritten probe→pair-error (swap symmetry,
  determinism, perturb→lsq-correct shrink, per-pin overlap gate in one batch)
  + new §7 pair-error-at (antisymmetry, +5 m lift tracking, off-surface
  reject) + §8 pair-overlap (co-located sufficient / disjoint insufficient).
- Green: server build, integration 36/36 on :8002 (stale leftover Superserver
  on the port killed first), Supertests 45/45.
- DOC DEBT (frozen): CLAUDE.md API list still shows `/query/probe`, compile
  order still lists `MeshProbe.fs`; probe-pair language ("ScanPin.Probe",
  reference-star wording) all over the state rules — rewrite at rework end.
