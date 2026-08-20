# Worklog

> **Standing rule (2026-07-27):** during a big rework, documentation files
> (CLAUDE.md, README.md) are FROZEN until the rework is finished — log every
> change here as it lands; at the end, reconstruct the net documentation
> updates from this file. Every instance to date is completed and
> reconstructed.

> **Compacted 2026-08-19.** The full pre-study log (2780 lines, v14 P0 through
> user-study mode) is archived offsite; only the index below remains here.

## ⛳ CURRENT POINT (2026-08-19) — user studies in progress

The user studies have begun, running the app as of commit `e001363`
(user-study mode: /study URL with warm-up + confirmed start). We are now in
the **study touch-up phase**: implementing fixes based on what the users are
observed doing/hitting during the sessions. A first list of work items is
about to arrive — log each entry below this marker as it lands.

Still owed from earlier (needs a human): the interactive in-browser verify
pass for F9 (hover/arm flows).

## Docs reconstruction pass: README + CLAUDE.md (2026-08-20, DONE)

User-directed full refresh of both files (verified against the code by three
parallel audit agents), the standing-rule reconstruction for everything since
the last one. README rewritten as a purely functional description (code
references now live in CLAUDE.md alone): port 8002 (5000 was Docker-internal
only), dataset conventions (*sensor.txt, basename-matched atlas), docker-compose
= traefik (no local port), the home TWO-navigator stage (tree + matrix,
redundant-cell state, already-connected warning, assess-quality ribbon), the
reworked pair workspace (⌖/✕/▸ rows, Solve → Finish two-step), the guided
3-step placement (banner, auto-arming, roi-fit refuse/grow), the Edit-only pin
panel, tile orientation controls, brush interactions incl. map/brush
exclusivity, legend mesh-crop, checkpoints + autosave, session log, study
mode, current mouse/key bindings (Space clean view, right-double-click).
CLAUDE.md drift fixed: ArmProbe/probe passages DELETED (pair-error-at's one
consumer = the hovered-dot readout), the ◎/◉ Isolate-&-focus column removed
(Sel.Point rides Pin-level tile clicks; SelectPin = the ▸ button), Isolate
pins = top-bar per-level checkbox (default ON at Pin), InspectOpen per-level,
PendingResolves auto re-solve cascade, CellDistBefore (workspace peek is no
longer an approximation), PairConnectWarn + study-start in the Esc chain,
V-peek accepts effective (not just locked) isolation, roi-fit endpoint +
CheckpointStore.fs in the lists, per-tile pair-coverage MRT wording, camera
rests on the FIRST mesh's sensor, integration = 33 checks on :8002 needing
Hessigheim, fshadeaot PostBuild + precompileShaders noted. Also fixed the one
stale in-app string: the gear camera-readout tooltip still cited the deleted
Sensor ▾ menu (GuiTopBar.fs).

## Panel default widths swapped: left small, tile strip big (2026-08-20, DONE)

Study work item: the left column started big (home `--lefthomew` fallback
600px) and the tile strip small (244px) — swapped so the left starts
smaller than the right. `.mesh-tiles` default width 244 → 600px (+ the home
column's `max-width: calc(100vw - 500px)` guard, so small windows keep room
for the rest); `.left-col-home` fallback 600 → 380px (the resize handle's
home MINIMUM — a literal 244 would sit below the floor and snap on first
drag; the two home navigators need it). The workspace `--leftw` 256px
default already sits under the strip and is untouched. The strip handle's
drag max was 600 = the new default (shrink-only handle) — raised to
`window.innerWidth - 500`, mirroring the home column's guard. CSS +
`GuiPanes.stripResizeHandle` only; client type-check 0 errors.

## Warm-up pin errors >0.5 m: pair-error sheet windowing (2026-08-19, DONE)

User report: on the warm-up dataset ("ScanPin - UserStory") pins showed
errors way past ±0.5 m where the surfaces visibly coincide.

**Root cause** (server, `PairError.pinError`): the client always sends
`length: 0`, so the sampling-cylinder length auto-derived from the posed
bbox union along the pin normal (`autoLengthAlong`) — ~±10 m on this scene
(the meshes span ~18–31 m; the "5×5×5" is only the central ROI, verified
via vertex percentiles — no outliers). The warm-up scene is the first
genuinely 3D dataset (sheets at multiple heights up to ~14 m apart), so
the cylinder pierced SEVERAL sheets per mesh — ground, backsides, walls —
and pooled them all: samples spanned ±10 m and the per-mesh anchor medians
were poisoned (measured median −9.87 m at a spot where the surfaces agree
to 1.7 mm). The 2.5D terrain datasets never hit this — one sheet per mesh,
however long the cylinder.

**Fix** (PairError.fs, server-only): per mesh, samples reduce to the axial
window |t − t₀| ≤ r around that mesh's OWN line crossing nearest the pin
plane (`ownSheet`: two ε-backed rays along ±n — exactly the `atPoint`
nearest-crossing rule, which is why the ⊙ probe never had the bug;
fallback = sample nearest t = 0 when the axis line runs through a hole).
The long auto cylinder stays — it's what still finds a sheet offset by a
pre-registration disagreement. First attempt was a gap-split cluster
(split where consecutive t gaps > r): insufficient — a wall running along
the axis smears samples continuously for metres, no gap to split on.
Rejected: client-side length cap (belt-and-braces) — with the window in
place it adds nothing and a cap below the raw baseline offset would break
the Matrix Before state with "no overlap" failures.

Verified against the live endpoint (probe scripts
`scratchpad/pin-error-probe{,2}.mjs`, `pin-error-study.mjs`): at two
warm-up spots where A/B agree to mm, the client call now returns medians
0.013/−0.066 m (was 0.15 with ±10 m samples / −9.87); study terrain pair
is byte-for-byte identical to the pre-fix baseline (r=0.5 count=2743
median=−0.02129 lod=0.02864; r=1.25 count=11787 median=−0.02262
lod=0.05867); integration suite 32/32 (determinism, swap antisymmetry,
perturbation/correction all hold — the per-mesh rule is symmetric).
`region-distance` (the error map) is structurally immune — closest-point
distance can't jump to a far sheet.

Also noted: both warm-up `*centroid.txt` files changed on 2026-08-19
(identical values — consistent for a same-frame export; not implicated).

## Study touch-ups: Isolate-pins checkbox + map/brush exclusivity (2026-08-19, DONE)

Two study work items:

- **"Isolate pins" looks like a checkbox now** (it read as a button): the
  top-bar control reuses `compactToggle` (`.ct`/`.ct-box`, the same
  primitive as "Error map (3D)" and the gear toggles) wrapped in a `.tb-ct`
  div carrying the tooltip; `tb-ct` CSS keeps it nowrap/bold in the 28 px
  bar. Same message (`ToggleAnchorGhostMode`), same per-level `LevelFlags`
  read.
- **Error map ⇄ brush are mutually clearing** (they were silently exclusive
  — a brush suppressed the map in render while the checkbox stayed on):
  `SetBrushedSamples` with a non-empty set turns `CellMapOn` off;
  `ToggleCellMap` turning ON clears the brush (+ hover/readout); the
  `AssessGlobalQuality` map-on path clears the brush too (jumpFocus only
  clears it when the jump crosses scopes). Clearing the brush model-side is
  safe: the `data-brushed` echo is authoritative, so the JS band nulls
  itself. Both "Error map (3D)" tooltips document the behaviour.

Verified headless on study 12/12 (checkbox renders + toggles; a guided
3-click pin at an overlap spot → 300 chart samples; brush drag → map
checkbox drops off; map re-enable → band+dots gone, Clear brush disabled;
zero console errors). Placement-test gotcha for future scripts: the pin
CENTRE only lands where BOTH pair meshes have surface (the placement
prohibit), and ✚ point picks raycast their OWN mesh alone — use one
known-overlap screen point for all three guided clicks
(`scratchpad/map-brush-excl.mjs`).

## Feathered overlap area everywhere (2026-08-19, DONE)

User request: the D4 T1 feathered overlap area (was placement-gate only)
must be THE overlap definition consistently across the app — hover
previews, correspondence point placement, etc.

- **One gate demand** (`MeshView.gateDemandAt`, shared by the main view and
  every tile): Pair/Pin = the selected pair during ANY pin-location
  interaction — placement transaction in flight, any armed pick (centre
  AND the ✚ correspondence picks, previously ungated), the ○ New pin
  hover, an arm-button hover preview (`PinFocusHover`, which is emitted by
  the ✚/◯ arm buttons alone — the ◎/◉ focus buttons were reworked away
  earlier, so no inspection conflation); Matrix = the HOVERED cell's pair.
- **Matrix hover settles on the feather**: `PlacementGate` wins over
  `OverlapPreview` in the shader (pre-existing priority), so the screen-
  space MRT test survives ONLY as the hover's in-flight fallback — instant
  answer, then the world-space feathered area takes over when the buffers
  land (~2 s on the study meshes).
- **Prox pipeline generalized** (`ensurePairProx`): subject = selected pair
  (workspace) / hovered cell (Matrix); `proxWant`/`proxBusy`/`proxFail` +
  pose-keyed FIFO memo `proxCache` (cap 12) in UpdateHelpers replace
  `pairProxReqGen`+`lastProx`; 180 ms delay debounces hover sweeps; the
  reducer accepts a landing only while still WANTED (a stale hover's
  buffers can't evict the wanted pair's). `resetProxState` on dataset
  switch; `invalidateCellError` clears the fail marker.
- `buildPaneScene` lost its `other` param (the gate reads the demand);
  tiles gate at Matrix hover too. `frameOverlapTiles` adds `FeatherRadius`
  to the framed half-width. Gear slider renamed "Overlap feather (m)".
- Verified headless 9/9 (fetch-on-hover 2 dirs, cache/single-flight on
  re-hover, sweep debounce, placement/armed flows) + screenshots: MRT
  fallback → feathered flip on the matrix hover; armed ✚ pick = isolated
  AND feather-gated (main view + lit tile); gate holds through the whole
  placement transaction. Zero console errors; builds 0 errors, no new
  warnings. The DemoHaus dataset was broken (empty main view) and has been
  DELETED from `src/Superserver/data/`; use study for testing.

## D1–D5 — study touch-up package (2026-08-19, DONE)

Specs: `ScanPin_v14_D1…D5_*.md` (post-dry-run fixes). All 15 tasks done;
28/28 headless checks (playwright vs :8002, full pin-place → solve → brush
flow driven through the tiles), zero console errors, client+server builds
0 errors / pre-existing warnings only.

- **D1 T1 (P0) clear-brush left the band painted** — root cause in `chartJs`:
  the JS-local drag `range` shadowed the model echo forever (`dispRange =
  range` wins), so clearing the state never cleared the canvas. Fix: the echo
  is authoritative — a `data-brushed`/`data-chart` change while not dragging
  nulls the local range. Dots/isolation were already model-driven (cleared
  fine); verified all three go together now.
- **D1 T2 (P0)** `CellMapOn = false` joined the `SetActiveDataset` reset list.
- **D1 T3** NEW `UpdateHelpers.clearInspectForPick` (map off + brush/hover
  cleared, no auto-restore) applied on `ToggleArmPick`'s arm branch and
  `BeginPinTransaction` — every placement/edit pick entry.
- **D1 T4 draggable brush** — pointer-down INSIDE the drawn band grabs it
  (`el._dispRange` kept by render): bin-snapped repositioning, width preserved
  in whole bins, live emits per bin-crossing so the dots follow; grab/grabbing
  cursors; outside-band drags brush fresh as before.
- **D2 T1 MMB rework (OrbitController)** — bare middle remaps to
  `Button.Button4` at the event edge = NEW in-view-plane pan (Right/Up,
  unflattened); Shift+ANY button = the old world-XY helicopter pan (the
  `panButton` path, unchanged math); RMB untouched. Orbit-cue counts Button4
  as panning. Verified numerically via the gear camera readout (bare MMB
  moves centre Z at a tilted view; Shift-drag keeps Z to 1e-6).
- **D2 T2 tile orientation** — `Model.TileRotation` (CCW from north-up, reset
  on dataset switch) + `GuiPanes.tileBasis`; `cam2dView` sky, pan/wheel
  conversions, off-frame edge arrows and crosshair/halo glyphs all go through
  the basis. Strip header `.tiles-head`: "⇱ Align to view" (ground-projected
  forward vs up, longer projection wins — parallel for the roll-free orbit
  cam) + "N ↑" reset (rail-btn-active when north-up).
- **D3 T1 isolation darkening** — `IsoDim` uniform (= `Model.IsoDimStrength`,
  default 0.65): ghost-level fragments mix toward the armed-scrim ink with an
  alpha floor (excluded from `aboveGhost` so painters skip the veil); lit by
  any explicit isolation (lock/previews/armed A/B pick), stood down under
  brush colour isolation / Isolate pins / error-map isolation.
- **D3 T2** visible grips (4×44 px pill, `::after`) on `.tiles-handle` +
  `.left-handle`. **T3** tile chips carry the FULL server folder name
  (`Primitives.meshFolder`, wrapping); the friendly-name shortener DELETED,
  its two gear-menu uses now show folder names. **T4** `.home-nav` frames
  equalized at 2px solid #94a3b8 (1.5px computed to 1px — too weak).
- **D4 T1 feather (analysis + implementation)** — analysis: the placement
  restriction was purely the VISUAL solidity gate (screen-space coverage-MRT
  test "both pair channels cover this pixel"); the pick itself already
  landed anywhere (server raycast + roiFit ≤4× validation). Implemented the
  spec's recommended world-space definition per vertex: NEW
  `/api/query/pair-proximity` (Embree closest-point per target vertex, NO
  Z-overlap gate — the lateral reach is the point), cached in
  `Model.PairProx` (cellErrorGen; pose-keyed memo so radius edits re-land
  free), bound as the `PairProx` vertex attribute; shader `PlacementGate`:
  solid ⇔ other mesh within `FeatherRadius` (default 1.0 m, gear slider,
  live — no refetch). `OverlapPreview` narrowed to the matrix hover alone;
  the per-tile coverage MRTs (which existed only for the old tile gate)
  DELETED — panes bind dummy Coverage samplers and use the same attribute
  gate; gate off until both buffers land (never half-tests).
- **D4 T2** `DatasetDefaults.pinRadius` (Model.fs, one line per override):
  `SetActiveDataset` seeds `QuickPinRadius`; "ScanPin - UserStory" = 1.25 m
  (2.5× the 0.5 default), everything else 0.5.
- **D5 T1 crash protection** — `.ck-auto-bridge` (hidden input, 60 s JS
  interval → input event, the brush-bridge pattern) silently saves the data
  state to the reserved `autosave` checkpoint while `Model.AutoCkOn` (gear
  checkbox, default ON); gear "Restore last checkpoint" loads it via the
  normal ckLoad path (guard toast when absent). **T2** already existed
  (GuiTopBar "Outline thickness (px)" slider — verified live). **T3** both
  tunables shipped: "Placement feather (m)" 0–3 and "Isolation darkening"
  0–1 gear sliders.
- Adaptify ran (TileRotation, FeatherRadius, IsoDimStrength, AutoCkOn,
  PairProx); CLAUDE.md updated in place (tile strip, render contracts,
  outline pass, inspection caches, API list, Misc).
- Noted doc drift for later: CLAUDE.md still documents the DELETED ArmProbe
  probe (State rules / Esc chain / inspection dock) — predates this package.

---

## Archived history (index — full text stored offsite)

Recent packages, in the state the studies run on:

- **2026-08-18 · User-study mode** — `/study` URL: `StudyPhase` warm-up
  (auto-loads "ScanPin - UserStory") → blocking start-confirm modal (in the
  ONE Esc chain) → study dataset + "Study mode" banner; `.study-on` faints
  the debug buttons via CSS (still clickable — the gear dataset switch is the
  exit; boot's first load exempt). Verified headless 13/13.
- **2026-08-17 · F9 — aiming & linking legibility** — gold OUTLINE = hover/
  focus grammar (loud ring + tile ring gold, off-frame tile edge arrows);
  armed correspondence pick gates the tile pair (`.tile-pick-on/off`); the
  sibling point stays full through every dim with a gold halo; `MarkerWeight`
  gear knob scales crosshair/reveal strokes; matrix hover gold-rings the
  connected pins.
- **2026-08-17 · Test-session feedback round** — histogram y-axis pinned
  across the pose peek (`ymax` over both states); median ticks removed;
  matrix + tree scale with the sidebar (CSS zoom fit); right-click clears the
  brush; centre/radius edits no longer unregister the pair (solve inputs =
  point pairs only).
- **2026-08-17 · Study preparation** — study-dataset `model_bbox.txt` files
  regenerated (were source-scan bboxes); Sensor ▾ dropdown removed (the
  measured-stations layer kept); `global.json` SDK floor back to 8.0.0.
- **2026-08-17 · Performance improvements (WP-1…WP-12)** — session-degradation
  audit: postlude guards before allocation, pair-overlap sweep fixed after
  dataset switch, chart bridge caches per-attribute + `g0` gid offsets,
  value-keyed swap bodies + incremental row lists (no `AList.ofAVal` churn),
  module caches given owners/eviction, JsonDocument disposal.

Older packages (titles only):

- 2026-08-06 · Peek-hold feedback + workspace error map flips with the pose peek
- 2026-08-06 · Fix: tile-strip growth loop on fractional-dpr displays
- 2026-08-06 · Measured sensor stations: *sensor.txt + /sensors endpoint
- 2026-08-06 · Camera readout in the ⚙ debug menu
- 2026-08-06 · Sensor jump goes first-person
- 2026-08-06 · GUI touch-ups round 5
- 2026-08-06 · Tile strip: wheel containment + 3D mark parity
- 2026-08-05 · F1–F8 test-session fix package
- 2026-08-04/05 · GUI touch-ups outside the specs, rounds 1–4
- 2026-08-03 · N1–N4 workshop: mesh roster, spanned-state event, rooted
  registration tree, home two-navigator stage + reaching-log
- 2026-07-30 · A8–A13: draft = committed appearance, armed-picking scrim,
  crosshair + intersection reveal marker, hover-preview isolation, brush disc
  locators + colour isolation, global inspect at Matrix, Matrix peek flips
  the error state
- 2026-07-29 · A4–A7: 3-level rail + persistent tile strip, universal arming,
  implicit pin completion + exit-guard, inspection dock + view cluster; peek
  redesign (isolate swap), post-A7 polish I–IV, feature round + fixes
- 2026-07-28 · A1–A3: focus rail + selection manager, two-pane picking
  surface, setup survey tiles; matrix polish rounds; docs reconstructed
- 2026-07-27 · v14 P0–P9: server pairwise error, graph state model, matrix
  navigator, reference-root designation, hierarchy descend/ascend, atomic
  pin placement, in-cell inspection, peek system, loop resolution;
  dead-code pass; docs reconstructed (freeze lifted)
