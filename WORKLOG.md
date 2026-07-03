# Worklog — visibility / linking cleanup pass

## Objectives
1. Clean up automatic visibility state changes (solo/isolation, per-mode defaults, stale caches).
2. Small-multiples ◐ isolate: toggle on/off on same button; exit resets all visibility toggles to ON; vis toggles locked while isolation active; workflow switch ends isolation.
3. Inspect mode: mesh focus (≠ ref) → false-colour on that mesh + isolate {reference, mesh}; pin focus → mesh isolation off, pin isolation on; both zoom 2D + 3D cameras tightly.
4. Audit linked interactions: mesh click ⇒ shared focus + 3D fly; pin click (rail) ⇒ 3D tight fly + focus panel zooms onto pin on current mesh; matrix cell ⇒ 3D very close + focus switches mesh & zooms onto the correspondence.
5. Fix stale state / code smells along the way.

## In progress
- Histogram rework + dock chrome trim implemented, build/JS-smoke green —
  awaiting the user's visual inspection (bar readability, outline contrast,
  head-row density, shift panel appearing only in Displacement).

## One-sided histogram + dock chrome trim (2026-07-03, after user review)
- Violin → one-sided stacked HISTOGRAM (user pick: the mirror halved the
  already-scarce height for zero information): crisp per-bin rects growing up
  from each lane's baseline, pin segments stacked in canonical order. With a
  solve, the inactive pose is a near-black step OUTLINE of its total (shape
  only, no colour/subdivision) over the filled emphasized pose; in-canvas
  caption "fill = after · outline = before" (flips with Peek). Same shared
  count scale across lanes and poses. Median ticks now sit on the baseline.
- Aggressive vertical reclaim, dock height UNCHANGED (220px): dock mode-label
  header row removed (all three modes gain ~22px; the rail names the mode);
  in-canvas title + hint lines removed (padT 26→14, padB 24→20); "Focus
  channel"/"Δ" labels dropped; pin legend chips moved into the single head row
  next to the channel toggles; axis meaning is a muted one-liner at the head
  row's right (`.ins-axis-note`). Chart canvas height ≈ doubled, per-lane data
  height ≈ 4× (no mirror + more canvas).
- Shift panel (`.ins-shift`, 188px) now mounts ONLY in the Displacement
  channel (the "shows in Displacement" stub note is gone) — the chart takes
  the full dock width in Difference. The canvas re-renders on size change via
  ResizeObserver (it only re-rendered on data mutations before — a resize
  would have left a stale-sized canvas).
- Brush-band value labels get a white halo (they now overlap bars). JSON:
  `state` field dropped (no in-canvas header consumes it). style.css → ?v=3
  (layout-affecting CSS change, per the stale-CSS rule).

## Violin distribution chart (2026-07-03)
- The dock's Inspect chart (`GuiInspector.brushChartJs`) is now a stacked
  violin per moving-mesh lane: 48 histogram bins over the shared mm x-range
  (1–99% quantiles over BOTH poses' full probe samples, span-0, 8% pad),
  binned in F# (`distData` ships per-pin `hb`/`ha` count arrays — small JSON,
  full samples never shipped); JS smooths the CUMULATIVE stack outlines
  (1-2-1) so layers never cross, stacks pin layers outward from the lane axis
  in the canonical pin order (CreatedAt, guid), strokes the total silhouette.
  One count→height scale across all lanes AND both halves (comparable areas).
- Before/After: no solve → mirrored halves (classic violin). Solved → fixed
  halves ▼ bottom = Before, ▲ top = After; the non-committed half renders
  muted (12% hue + 88% luminance grey, lifted 60% toward white); Peek flips
  ONLY the emphasis (visual, matching the 3D peek contract). Data: new
  `ScanPin.ProbeOther` = the same probe at the opposite pose (`ensureProbe`
  fires it only when solved, sharing the pin's CTS + debounce);
  `ModelTransforms.displayedRenderAt`/`displayedWorldAt` provide the pose.
  `SetRegView` now SWAPS a ready (Probe, ProbeOther) pair in place — the
  Before/After toggle no longer blanks the matrix/chart — and clears
  `BrushedSamples` (gids re-index); unpaired states invalidate both.
- Brushing unchanged by design: the conceptual samples (canonical
  `brushSamples` gid array, committed pose) still populate `el._dots`; the
  x-range drag, 50 ms-throttled bridge emit, click-clear and `data-brushed`
  echo work as before — the dots are simply not painted. New: brushed count
  next to the range label; a model-side clear drops the local band; without a
  local drag the band is reconstructed from the echoed gids.
- Verified headlessly: typecheck build green, Supertests 43/43, and a node
  harness (scratchpad `check-violin.js` + `smoke-violin.js`) that extracts the
  inline JS from the .fs source, parse-checks it, renders pending/reg=0/reg=1
  against a stubbed canvas, simulates a brush drag (partial gid emit
  verified), click-clear, and echo-band reconstruction.

## Legend placement report (2026-07-03) — stale-CSS root cause
- Headless-browser measurement (real click into Inspect): the legend renders
  exactly as specced — horizontally centred (translateX(-50%) applied), bottom
  edge at viewport−230px = the scale bar's bottom edge, 10px above the dock
  (which is fixed height 220px). The reported "in-flow bottom-left, behind the
  dock" is precisely how the element renders when `.color-legend` CSS is
  missing → the browser had a STALE CACHED style.css.
- Fix: Superserver now serves the hand-edited shell assets (css/html/js incl.
  the index fallback) with `Cache-Control: no-cache` (ETag revalidation each
  load, cheap 304s), so CSS edits always reach the browser.
- Second report (legend below the scale bar, behind the dock) = the same
  missing-rule rendering; the no-cache header can't evict an ALREADY-cached
  copy. index.html now links `style.css?v=2` — a changed URL bypasses both the
  disk cache and the service-worker cache with no user action. Bump `?v=` if a
  stale stylesheet would ever break layout again. Verified end-to-end
  (headless browser, Inspect mode): legend centred, bottom edge =
  viewport−230px = scale-bar bottom edge, 10px above the 220px dock.

## Inspect colours + samples (2026-07-03)
- **One error range** (`ScanPin.inspectRange` + adaptive `MeshView.inspectRange`):
  signed (lo, hi) metres over every ready pin's ROI probe samples on moving
  meshes, spanning 0, hard-capped ±0.5 m; no pins ⇒ ±0.5 m. Drives the 3D
  difference painter (new `DistLoNeg` uniform, asymmetric piecewise — neutral
  welded to 0, each sign normalized by its own end), the variance σ map
  (saturates at max(|lo|, hi)) and the focus tiles/single (`FocusLoNeg`).
  Per-mesh robust-percentile normalization (`robustHi`) and the gear
  "Difference heatmap range" slider (`DiffRangeScale` model field + message)
  are GONE — ranges must stay comparable.
- **Displacement range** (`MeshView.displacementRange`): global max
  |load→solved| over each solved mesh's world-bbox corners (rigid ⇒ exact at a
  corner), uncapped — displacement is not an error metric.
- **Legend** (`GuiOverlays.colorLegend`, bottom centre, Inspect only): active
  map's gradient (variance σ / difference M3C2 or Δz / displacement) as an SVG
  via observedRender — nice-step ticks + exact range ends; mm/cm/m formatting
  by span.
- **Sample dots**: brushed samples render as small solid icosphere dots
  (`Dots.render` in LineShader.fs — vertex-coloured triangle batches), coloured
  by the sample's value on the diverging gradient normalized to its own pin's
  range (`ScanPin.pinErrorRange`) — replaced the pin-coloured crosses.
- **Hover-reveal removed**: the 3D surface-hover → chart brush (View.fs) and
  the hovered-pin ROI sample cloud (`sampleBrush`) are gone with their
  throttles; chart X-range drag is the ONLY brushing path.
- Docs: CLAUDE.md "One Inspect error range" contract section; README workflow
  bullets updated (select vs double-click zoom, one range + legend, chart-only
  brushing).

## Select ≠ zoom separation (2026-07-03)
Single click/tap = selection + visibility only; double click/tap = camera zoom.
- **Reducer**: `SetFocusedMesh`, `SelectPin`, `FrameCorrespondence` no longer emit
  `FlyToPoint`. New `ZoomToMesh` / `ZoomToPin` messages own the 3D framing radius
  conventions (bbox ×0.6 / pin ROI ×4). Dead `FlyTo(target, aspect)` message +
  handler removed (no emitter existed).
- **Double-click sites** (each: emit select + Zoom* and call the 2D FocusScene
  helper): Overview roster row, matrix column head (mesh → `ZoomToMesh` +
  `resetCam` fit), matrix pin row-head (`ZoomToPin` + `zoomOnPin`), matrix cell
  (re-`FrameCorrespondence` + `FlyToPoint` on the anchor + `zoomOnWorldRadius`;
  no-marker cells fall back to mesh framing), focus tile (`ZoomToMesh` +
  `resetCam`), 3D pin dot (`Sg.OnDoubleTap` → select + `ZoomToPin`; 2D zoom
  unreachable there — compile order, matrix pin row is the linked path).
- **ClickGate** (Primitives.fs): defer-and-cancel discriminator used ONLY by the
  two toggle-on-single-click controls (matrix cell locate/back-out, 3D pin dot
  select/deselect) — the `tap`/`click` events fire on both leading clicks of a
  double, which would toggle twice. Double handlers are written to END in
  "selected + zoomed" regardless, so slow double-clicks stay correct.
- `FocusScene.onMeshFocused` (Inspect auto-tight-fit on select) removed — it was
  an automatic zoom on selection, exactly what this pass separates. Mesh
  double-click now resets that mesh's 2D camera to fit in every mode.
- 3D viewport: mesh tap = select only (dropped its 2D zoom side-effect);
  double-tap keeps the existing recenter-on-point behaviour.
- CLAUDE.md grammar line extended (double-click = zoom, ClickGate rule).
- Verified: typecheck build green, Supertests 43/43.

## Polish round 2 (2026-07-02)
- **Reference-marker parity**: the reference's correspondence point (RefAnchor) now
  draws exactly like a moving-mesh marker — same wire-sphere+cross glyph/size in the
  reference mesh's colour in the 3D constellation (hover-linked via its matrix cell),
  and shown in the focus Top overlay when the single shows the reference. Ref-column
  matrix cells now locate (FrameCorrespondence/selectCell read RefAnchor). Pick/place
  already worked (✎ edit point + PickCorrespondenceAt isRef branch) — verified.
- **Distribution chart**: X-axis ruler (nice-step ticks, labels, faint gridlines,
  zero line, axis baseline); brushing reworked from dot-lasso to an X-RANGE drag —
  selects every sample in the range, band edges show exact mm values, click clears.
- **Pin visuals**: centre jack now small + faint neutral (was bright yellow when
  selected / red otherwise); equator + contact rings + focus ROI circles use the
  pin's own colour (was host-mesh colour falling back to fixed blue / red-gold in
  the focus); verdict flag pole is neutral grey (red/green semantics dropped) and
  takes the pin colour while the 🎨 Overlays hold is down.
- **Focus head slimmed**: ⤺ back, ⇄ link, ⟲ reset buttons removed with their code —
  `Model.LinkViews` + `ToggleLinkViews` + both link-views camera paths +
  `FocusScene.recenterOnWorld` + dead CSS (incl. stale `.focus-peek`). Locate
  back-out remains via re-clicking the located matrix cell (BackOutLocate kept).

## Docs cleanup (2026-07-02)
- CLAUDE.md rewritten: kept only rules/pitfalls (state rules, render-pipeline
  contracts, coordinate discipline, adaptive-performance rules, query-perf rules,
  compile order, API reference, Aardvark.Dom gotchas, fsproj/CSS notes); dropped
  the feature-behaviour catalogue (ghosting §-tables, Inspect channels, camera
  controls, GUI placement, Model snapshot — all readable from code). ~350 → ~200 lines.
- README rewritten: fixed stale claims (readiness lives in the Correspondence rail,
  not the top bar; intrinsic heatmaps are Overview-roster per-mesh switches, not in
  the dock; focus-single pan is middle/Shift+left, not plain left; ghost floor is in
  the gear, not a 👻 toggle), documented pano-centers.txt in the dataset layout, and
  dropped the duplicated architecture/pipeline/perf sections (now CLAUDE.md-only).

## Adjustments after user testing (2026-07-02)
- Inspect mesh focus isolates **only the moving mesh** — the reference is no longer
  part of the solo shown-set (a co-located reference occluded the field);
  `MeshVisibility.shown` simplified (step/ref params dropped at all call sites).
- Inspect **matrix-cell locate additionally activates pin isolation**
  (`FrameCorrespondence` sets `AnchorGhostMode` in Inspect, like a pin click);
  Correspondence keeps its default.

## Done
- **Solo redesign** — `MeshSolo : string option`, a pure overlay over `MeshVisible`
  (no destructive mutation, no restore map). One shared rule `MeshVisibility.shown`
  (Model.fs) consumed by render `MeshActive`, 3D raycast candidate sets, Alt-wheel
  cycling, contact-ring + constellation gating. In Inspect the shown-set is
  {isolated mesh, reference}.
- **◐ isolate lifecycle** — re-click exits; `UpdateHelpers.exitSolo` resets every
  visibility toggle to ON; tile vis buttons disabled during isolation (+ reducer
  guard on `SetVisible`); `SetWorkflowStep` ends isolation, drops `LocateBackup`,
  clears `Selection.Hovered`.
- **Inspect policies (reducer-owned)** — `SetFocusedMesh (Some m≠ref)` → auto-solo
  {m, ref} + pin isolation off (focusing ref returns to the ensemble);
  `SelectPin Some` → exit mesh isolation + pin isolation on (`None`/delete → back
  to off). Reference renders as plain solid context during solo (variance encoding
  gated to no-solo); the old "empty outline for others" special case removed —
  others go to the regular ghost floor.
- **Linked cameras** — new FocusScene helpers: `onMeshFocused` (Inspect: tight fit
  of the focused mesh; called from tile / matrix column / 3D click / cell-no-anchor),
  `zoomOnPin` (rail pin-row click: focus panel keeps its mesh, zooms onto the pin,
  same metric half-extent as the 3D `FlyToPoint`), `zoomOnWorldRadius` (matrix cell
  locate: focus switches mesh + zooms onto the correspondence coordinate, replacing
  the fixed ×4 zoom).
- **Matrix cell toggle-off fix** — the un-locate branch now requires an active
  locate (`LocateBackup` present); a pin-row + mesh-column selection no longer
  swallows the first cell click.
- **Stale-cache fixes** — `setMeshVisible` invalidates the variance map, bumps the
  focus-dist generation (newly shown meshes fetch their missing difference fields —
  previously never fetched), and clears `BrushedSamples` (gids index a
  visibility-dependent array). `ensureVariance` skips during solo; `ensureFocusDist`
  fetches the shown set (isolated mesh even if its raw toggle is off).
- **Focused-mesh resolution unified** — `GuiFocus.visibleMeshes` no longer diverges
  from `FocusScene.single` (restore-set branch removed); focusing/locating a hidden
  mesh re-enables its toggle so the single always shows the focused mesh.
- **Dead state removed** — `Selection.SelectedPoint` + `SetSelectedPoint` message
  (write-only), `LocateState.Pin/Mesh` fields, `MeshSoloState` DU.
- **Docs synced** — CLAUDE.md (§A visibility model, §C per-mode table, Inspect
  visualizations, locate, selection-camera sync, model snapshot) + README
  (tiles/isolation lifecycle, Inspect behaviour).
- **Verified** — adaptify regenerated, client typecheck build green
  (`-p:WasmBuildNative=false`), Supertests 43/43 pass.

## Follow-ups / open questions
- Shader verification status: `MeshShader.shade` (+`DistLoNeg`) and the focus
  shaders compiled clean in a real headless-Chromium WebGL2 run (puppeteer,
  `?nocache=true`); the run can't reach `#loading-done` in THIS environment —
  an offscreen framebuffer fails identically on the unmodified HEAD build
  (environmental, headless GL). Still to eyeball live: the new `Dots` shader
  (first brushed selection), legend SVG (all three map kinds), dot
  size/readability, and variance-map contrast on the pin-derived scale (σ is
  usually smaller than the error envelope — if too pale, consider a separate
  σ range).
- `ShaderCache.fs` no longer covers the changed mesh/focus shaders (new
  hashes) — first load live-compiles them (few seconds). Re-run
  `./precompileShaders.sh` on a machine where headless WebGL works to re-bake.
- 3D pin-dot double-tap zooms 3D only — the 2D focus canvas is unreachable from
  ScanPinScene (compiles before FocusScene); the rail pin-row is the fully
  linked path per spec.
- In-browser verification of the select/zoom split pending: single-click cell
  select now lands after the 350 ms ClickGate window — check it doesn't feel
  laggy; check double-click on roster/tiles/cells zooms without visible
  toggle flicker.
- In-browser verification of the full Inspect flow still pending (shader paths
  unchanged, but ghost-floor appearance of "others" during Inspect solo is a
  visual change worth eyeballing).
