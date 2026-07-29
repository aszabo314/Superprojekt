# Worklog

> **Standing rules for this rework (2026-07-27):** documentation files
> (CLAUDE.md, README.md) are FROZEN until the entire rework is finished —
> do not edit them per-change. Log every change here as it lands; at the
> end, reconstruct the net documentation updates from this file.
> *(A1–A3 amendment instance completed 2026-07-28 — docs reconstructed.)*
> *(A4–A7 amendment instance completed 2026-07-29 — docs reconstructed.)*

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
