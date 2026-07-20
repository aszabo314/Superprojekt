# Worklog

## Slice-mode outline fade fixed (2026-07-20, after the touchups commit)

- Diagnosis: the round-1 `OutlineDistFade` was quantization-broken — the fade
  window (FadeDist ≈ r/20 of a ~6r depth range) spans ~2 LSBs of the Rgba8
  G-buffer depth, so silhouettes/isolines staircased on/off instead of fading;
  and the per-mesh FOOTPRINT contours never faded at all (the coverage MRT is
  depth-free by design — occlusion-free additive accumulation).
- **16-bit depth packing**: `OutlineGBuffer` packs window depth hi/lo into
  target0.w/.z (.z was written as constant 0 — free). The edge detect still
  reads the HI byte alone (identical staircase, OutlineThreshold calibration
  untouched); only the `OutlineEdge` fade multiplier reconstructs
  `c.W + c.Z/255` — smooth falloff over the few-cm window, alpha 0 beyond, no
  G-buffer discard needed (occlusion in the buffer stays correct).
- **Footprints stand down in slice mode**: new `active` param on
  `buildCoverage`, `Sg.Active (not SliceMode)` on the composite ONLY — the
  coverage pass, shaders and non-slice rendering are untouched.
- CLAUDE.md updated (packed-depth bullet, footprint gate, slice fade note).
  Build green — shader change, so the owed browser pass now also covers the
  packed-depth fade (verify no new false bands + footprints returning on exit).

## Slice-mode GUI touchups (2026-07-20)

- **Focus angle indicator gold → white** (arrow + cut-plane trace in
  `addPinRingsAndSelectionCircle`): back on the transient/selection layer with
  the dashed selection circle; gold stays on the slice badges only.
- **Cut trace restructured** (follow-up): the double line is gone — SOLID white
  = the cut plane itself, a fainter thinner white line = the end of the
  transparency falloff (Near + FadeDist, same `traceAt` horizontal-plane
  intersection), so the visible profile band reads as the gap between them.
- **Stretch framing**: vertical fill fraction 0.9 → 0.75 (brushed/probe paths
  of `sliceStretchFactor` — the data sits clear of the top/bottom edges) and
  the ortho half-WIDTH tightened ×0.8 while stretched (1:1 is already given up
  there; true scale untouched). New `MeshView.sliceOrthoHalfSizes` is the ONE
  hw/hh source — View proj, `sliceOrdinates`, `sliceAxes` rulers all read it,
  and `brushedDotSegments` shrinks the horizontal glyph axis by the same
  factor so the marks stay circular.
- **Correspondence constellation hidden in slice mode**: `constellationActive`
  now also gates on `not SliceMode` — the wire-sphere/cross markers + ref lines
  stand down with the flags and origin cross.
- CLAUDE.md updated (white indicator, tighten + ¾ fill, stand-down list).
  Client type-check green. Browser pass owed with the rest of v12.

## Removals: plan mode + displacement viz (2026-07-17)

- **Plan mode gone entirely** (the "🗺 Plan" / hold-O white-out overlay):
  `Model.ShowOverlaysHeld` + `SetShowOverlays` + reducer arm, the top-bar
  button, the O hotkey handlers, the `Whiteout` shader uniform + branch
  (MeshShaders/MeshView), the pin-flag overlays-hold styling reads
  (ScanPinScene), the 2D flag-tip name-tag overlay `GuiOverlays.pinFlagLabels`
  + its mount + `.pin-flag-labels`/`.pfl` CSS. `ScanPin.flagTopRender` stays
  (the 3D flag pole still uses it).
- **Displacement visualization gone entirely** (the load→solved motion viz):
  the `InspectChannel` type/field/message/handler (the dock keeps only the
  M3C2|Δz sub-toggle — Difference is now THE Inspect pair channel), the 3D
  enc-3 shader branch + `MeshView.displacementRange` + the inspectField/
  distScale displacement paths, the focus mode-2 ramp + mode-3 white surface
  (FocusShaders), the focus arrow glyphs (`arrowSegs` + node) +
  `loadSolvedForwards` (orphaned) + the displacement→Top projection collapse
  (single + tiles) + `dispLegend` + `.focus-displeg` CSS, and the legend's
  Displacement branch. `ensureFocusDist` drops its channel gate. The numeric
  SHIFT READOUT (total/vertical/horizontal/rotation numbers in the dock) is
  a readout, not a viz — kept deliberately; say the word if it should go too.
- CLAUDE.md swept (colour families, focus camera bullet, shader contracts,
  dock/brushing bullets, the displacement bullet deleted). Adaptify re-run;
  build + Supertests green.

## ScanPin v12 — follow-up round 3 (2026-07-17 feedback)

- **Dots of interest double-circled**: `brushedBase` now carries an isInterest
  flag; `addGlyph` draws a second inner circle (×0.6) for interest dots in the
  main 3D (focus glyphs never interest-marked).
- **Outline/isoline opacity capped at 10 % in slice mode**: in `OutlineEdge`,
  `OutlineDistFade > 0` doubles as the slice signal — silhouette + isoline
  alpha = min(0.1, base·distFade), inlined (FShade lambda-free rule). Together
  with the flag/cross removal this makes the terrain profiles the dominant ink.
- **Flags + origin cross fully off in slice mode**: `flagsActive` gates
  pole+ring, base cross and name labels (round-2's selected-pin exception
  removed — the whole flag machinery stands down); SceneGraph's origin
  indicator + axis labels gated the same way.
- **Slice rulers** (`GuiOverlays.sliceAxes`, `.slice-axes` overlay spanning the
  3D area): vertical ruler right of the rail (x=268), horizontal above the dock
  edge, both ticking METRIC distance from the pin centre (nice 1/2/5 steps,
  emphasized zero, faint grid lines across the view). The vertical ruler ticks
  TRUE metres — its px spacing widens with the stretch factor, so it doubles as
  a live exaggeration readout; recomputed from the ortho frame + viewport, so
  it survives any projection change.
- **Badges** (`sliceBadges`, replaces stretchBadge): "ortho slice view" while
  slice is active + "vertical axis stretched ×N" while stretch is on, both on
  the gold #b45309 accent.
- **Focus angle indicator restyled**: GOLD instead of white (matches the
  badges), arrow shrunk to ~75 % of the pin circle (half-length 0.75·r, head
  0.15·r), cut-plane trace drawn as a DOUBLE line (±0.045·r offsets along the
  view direction).
- Build green. Browser pass owed as before.

## ScanPin v12 — follow-up round 2 (2026-07-17 feedback)

- **Outline/isoline falloff shrunk**: `OutlineDistFade` now uses the SAME
  window as the mesh surface fade (`SliceCam.FadeDist` = max(5 cm, r/20)) —
  lines vanish with the fill instead of trailing 4 radii behind the cut.
- **Slice mode hides non-selected flags**: pole+ring (`pinFlags`), base cross
  (`pinMarkerLines`) and name label (`pinLabels`, per-pin Active aval) all skip
  pins other than the selected one while `SliceMode` is on.
- **Stretch extents pre-calculated for the region**: `sliceStretchFactor`'s
  inputs are now cut- AND azimuth-independent (axis-direction offsets from the
  pin centre only): brushed ⇒ the SELECTED pin's brushed samples (not the
  cut-ranked interest set); fallback ⇒ the ~20 probe samples closest to the
  pin CENTRE (was: nearest the cut). The frame no longer breathes while the
  slice plane scrolls or the azimuth steps.
- **Focus angle indicator** (Top single + tiles, slice mode, selected pin):
  white arrow through the pin centre along the slice view direction
  (`addArrowXY`) + a white ⊥ segment tracing the cut plane — computed as the
  cut plane ∩ the horizontal plane at the pin height (exact for tilted
  dip-aligned sections). Lives in the shared `addPinRingsAndSelectionCircle`
  (new `SliceCam option` param), so single and tiles cannot drift.
- **Brushed dots → screen-aligned circle+cross glyphs** (line geometry; the
  icosphere path + the whole `LineShader.Dots` module deleted as orphans):
  - main 3D `brushedDotSegments`: constant screen size — new gear slider
    "Brushed dot size (px)" (`BrushDotPx`, default 15, clamp 4–60); ortho
    slice sizes from the frustum (2·hh/vpY per px), perspective per dot from
    its eye distance (90° vfov); view-dependent recompute by design (pin-flag
    precedent, ≤200 dots).
  - stretch compensation: the vertical (screen-up = pin-axis) glyph axis is
    divided by the stretch factor, so exaggeration never distorts the marks.
  - focus single/tiles `brushedDotSegmentsFocus`: same glyph, XY-aligned,
    fixed render size (the focus cameras keep their own zoom conventions — no
    px constancy attempted there).
- Build green; CLAUDE.md updated. Browser pass still owed (now also: falloff
  tightness, indicator geometry, glyph sizing/squish).

## ScanPin v12 — follow-up round (2026-07-17 feedback)

- **"Isolate pins" checkbox** replaces the "Full meshes" button: rendered as a
  `compactToggle` (visual ■/□ checkbox, `.rail-isolate`), bound DIRECTLY to the
  one pin-isolation mode (`AnchorGhostMode`). Checked (Register default) =
  isolated pin patches (context floor 0); unchecked = isolation off entirely →
  full textured meshes via the exact Overview code path. The interim
  `RegisterFullMeshes` field + message are deleted (adaptify re-run).
- **Dip-aligned slice projection**: `SliceCam.Up` = the pin axis (local normal,
  `ScanPin.axis`); the azimuthal eye direction is the world heading projected
  into the plane ⊥ the axis (degenerate-heading fallback). View + ordinate
  overlay both use `lookAt … s.Up`; ordinates now drop along the pin axis (=
  the M3C2 measurement direction), which is more correct than the old world-Z
  drop.
- **Outline/isoline distance falloff in the compositing pass**: yes — done
  there. The G-buffer already stores window depth (target0.w), which under the
  slice ORTHO is linear in eye distance with 0 exactly at the cut → new
  `OutlineDistFade` uniform in `OutlineEdge` multiplies silhouette + isoline
  alpha by `1 − depth·k`; `OutlineView.build` sets k to fade out ~4·pin-radius
  behind the cut, 0 (off) outside slice mode; the focus reference overlay
  passes 0.
- **Adaptive stretch factor** (moved to `ScanPinScene.sliceStretchFactor`, it
  needs the brush ranking): brushed ⇒ the fully-opaque dots of interest fill
  the view height (max |axis-offset| → 90 % of half-height, exact, clamp
  [1,1000]); no brush ⇒ the ~20 on-surface PROBE samples inside the pin sphere
  nearest the cut plane stand in for "mesh vertices at the near plane" (probe
  positions are on-mesh points in the pin area and already client-side — no
  500k-vertex scan; this was the "is this difficult?" part — the probe-sample
  stand-in makes it cheap); no probe ⇒ the old inspect-span formula as last
  resort. Badge formats the exact factor (×%.0f / ×%.1f).
- **Cut snapping**: `MeshView.sliceCutStep` = nice 1/2/5 increment ≈ 5 % of the
  pin radius, never finer than 1 cm. The MODEL keeps the continuous cut (so
  sub-notch trackpad deltas accumulate instead of rounding back); `sliceCamera`
  snaps at read time, so the plane, the fade, the dot ranking and the ordinates
  all click through the same grid.
- Build green. Same in-browser pass still owed (now also: dip-aligned entry,
  outline falloff, adaptive ×N values).

## ScanPin v12 — Task 9: prune + audit

- Plan-mode overlay height-profile diagrams REMOVED (§5): the per-pin profile
  charts on the show-overlays pills (`chartsJson` + chart DOM/CSS in
  GuiOverlays.pinFlagLabels — pills keep the name tags only) AND their 3D
  locator, the centre-slice lines (`pinSliceLines`, ScanPinScene). The matrix
  slice cells (v11) are untouched — the slice caches, `ensureSlices`, the
  server /query/slice, window/offset helpers all stay.
- Orphans pruned with them: `ScanPin.DatasetColors` (field + `assignColors`),
  `ScanPin.sliceToWorld`/`sliceUV` (chart-frame converters with no consumers
  left), `.pfl-chart`/`.pfl-nochart` CSS. Earlier tasks already pruned the pin
  palette/selectionTint (T2), the single dock diagram + `.ins-dist*` (T3), and
  the false-colour-dot legend branch (T6); final grep sweep is clean.
- CLAUDE.md drift fixed: pin identity → name-only pinInk; matrix centre-ring;
  dock section rewritten (two fixed charts + neutral-dot brushing + interest
  cross-highlight); Slice/SliceOther feed matrix cells only; new bullets for
  Register full-mesh toggle, slice mode (camera/cut/stretch/ordinates) and the
  placement suitability overlay + hard-prohibit.
- Green: client type-check, server build, Supertests 45/45, integration.mjs
  22/22 against :8004 (server killed after).
- ONE BROWSER PASS OWED for the whole spec (dotnet build cannot compile
  FShade→ESSL3): suitability overlay shaders (T4), slice fade + ortho cut (T5),
  plus visual checks — Register context toggle, near-black pin marks, the two
  dock charts + placeholders + brushing + amber interest markers, tooltip +
  ghost fade on invalid placement, 10° azimuth stepping + profile silhouette,
  stretch ordinates/tooltips/badge.

## ScanPin v12 — Task 8: chart cross-highlight of the dots of interest (§6 bonus)

- `focusData` (GuiInspector) = the interest gids from the shared
  `sliceRankedBrush` ranking, fed to BOTH charts as `data-focus`; the chart JS
  draws amber baseline markers (white-ringed dots) at those samples' x
  positions and re-renders on attribute mutation — so the highlight follows
  the cut plane live as the slice camera rotates/sweeps. Empty outside slice
  mode. Build green.

## ScanPin v12 — Task 7: slice mode — stretch + ordinates (§6 stage 2)

- `Model.SliceStretch` toggle (+ `ToggleSliceStretch`, top-bar "⇕ Stretch"
  button shown only in slice mode). Factor N is DERIVED, not stored:
  `MeshView.sliceStretchFactor` sizes the shared inspect error span to ~1/3 of
  the slice view height, snapped 1/2/5, clamped [2, 500].
- Exaggeration is implemented PURELY in the ortho projection (half-height ÷ N):
  pitch is locked horizontal with up = +Z, so screen-vertical IS world-Z —
  vertical-only by construction, zero geometry touched, cut/fade/picks stay
  metric, and everything projected through the shared matrices stretches
  automatically. `orthoProjTrafo` moved to MeshView (shared with the overlay).
- `ScanPinScene.sliceRankedBrush` = the ONE slice ranking (gid, renderPos,
  valueMm, |behind-cut|, sorted) shared by the 3D dots (T6 refactored onto it —
  including a fix of a transient-aval read I nearly shipped), the ordinates,
  and T8's chart highlight.
- Ordinates (`GuiOverlays.sliceOrdinates`): per dot of interest a vertical line
  dot → reference (its signed sample value), projected through the SAME
  view/proj — rendered as hoverable HTML strips (`.slice-ord`), tooltip = TRUE
  value in mm/cm (never pixel distance). Gated on stretch ∧ slice; true scale
  never shows them.
- Persistent amber "exaggerated ×N" badge (`.stretch-badge`) top-centre while
  stretch is active.
- Build green.

## ScanPin v12 — Task 6: slice mode — neutral dots of interest (§6 stage 1)

- `brushedDotGeometry` rewritten: dots are ONE neutral dark grey (values live
  in the charts) — the false-colouring on the shared inspect range is gone. New
  `sliceAware` flag: main 3D passes true, the focus views false.
- Slice mode: dots ranked by |distance behind the cut plane|; the nearest
  `maxDotsOfInterest = 12` render at full strength, the rest fade out over
  0.6 × the view half-height (α·0.45 → 0, dropped below 0.04); dots in front
  of the cut are skipped (they're near-clipped like the surfaces, so they
  never waste interest slots).
- Legend: the "Brushed samples" gradient branch DELETED (false-colour-dot
  code); the legend now hides entirely while a brush is active in Inspect —
  no colour scale is in play with neutral dots.
- No ordinates/hover in true scale (§6 explicitly defers them to stretch mode).
- Build green.

## ScanPin v12 — Task 5: slice mode — constrained camera (§5)

- Model: `SliceMode : bool` + `SliceCut : float` (metric, signed, 0 = through
  the pin centre). Messages `SetSliceMode`/`AdjustSliceCut`; reducer clamps the
  cut to ±2.5·InnerRadius with a radius-scaled wheel step; `ensureSliceMode`
  postlude exits the mode whenever the selected pin goes away.
- `MeshView.sliceCamera` = THE slice frame, shared by the main view/proj AND
  the mesh-shader fade uniforms: ortho, pin-centred, half-height 1.6·r (zoom
  locked, influence circle fills ~2/3 of the height), pitch locked horizontal,
  up = +Z. Azimuth = orbit `phi` snapped to 10° — so ENTRY lands on the step
  nearest the current perspective heading by construction, and dragging keeps
  rotating the orbit while the displayed azimuth clicks in 10° steps (pitch
  drags change hidden orbit state only). Eye at 6·r, far at 12·r.
- The CUT = the ortho NEAR plane (near = 6·r − cut): geometry in front is
  GL-clipped, and because View.fs feeds the same view/proj avals to the
  offscreen outline pass, the profile at the cut gets the standard silhouette
  treatment with zero extra outline code.
- Behind-the-cut alpha falloff: new `SliceFwd`/`SliceFadeNear`/`SliceFadeDist`
  uniforms in MeshShader — fade over max(5 cm, r/20) then discard; faded
  fragments drop below the α-gated depth threshold, so picks fall through them.
- Wheel is intercepted in View.fs while slice mode is on (`AdjustSliceCut`
  instead of orbit zoom); Esc exits (shared with placement cancel — both
  no-ops when idle); top-bar "▤ Slice" toggle, disabled without a pin
  (`.tb-btn:disabled` styling added).
- OWED IN-BROWSER: shader fade compiles only in the browser; verify entry snap
  feel, 10° stepping, profile silhouette at the cut, sweep direction.
- Build green.

## ScanPin v12 — Task 4: fused placement-suitability overlay (§2)

- Auto-on strictly while a placement is armed (every piece gates on
  `Placement = AnchorPlacement`); vanishes on cancel/commit.
- New offscreen pass `MeshView.buildSuitabilityNode` + shader
  `SuitabilityCoverage` (MeshShaders.fs): per mesh, additive occlusion-free
  screen-space footprint like the outline coverage MRT, but the written value is
  SHAPE-WEIGHTED — 0.25·(0.2+0.8·quality) — so one channel carries both
  "covered" and an approximate per-mesh shape quality (multi-layer accumulation
  clamps → biased crisp; accepted approximation, noted in the shader).
- `SuitabilityComposite` fullscreen fragment (via `OutlineView.buildSuitability`):
  0 covered channels → transparent; 1 → flat textureless grey α 0.78 (detail
  visibly lost); ≥2 → diagonal weave cycling the covered meshes' palette colours
  (no cap below the 8-channel MRT limit), saturation+separation+alpha modulated
  by the MIN shape across them. Semi-transparent and drawn BEFORE the outline
  composites in SceneGraph, so isolines/footprint contours read through it.
- Hard-prohibit: `View.countOverlap` = closest-point fan-out per mesh at the
  displayed pose, in-range = within QuickPinRadius. Hover (throttled, gen-
  guarded) writes `placementValid`; <2 ⇒ the white ghost/outline fades to ×0.2
  (ScanPinScene) + a red cursor-side tooltip "no overlapping meshes here"
  (GuiOverlays.placementTooltip). The CLICK re-verifies at the actual point and
  refuses with a toast (new `ShowToast` message for view-side guards).
- OWED IN-BROWSER: the two new shaders compile only in the browser (ESSL3) —
  verify placement arming shows the overlay, hatch colours, grey prohibit zones,
  tooltip, and that picks still work (composite is NoEvents).
- Build green.

## ScanPin v12 — Task 3: detail dock = two fixed charts (§3)

- `GuiInspector`: the selection-driven single diagram is GONE. `chartsCore`
  computes BOTH chart payloads in one pass (shared 1–99% quantile x-range over
  all ready pins × moving meshes × both poses — the charts stay comparable):
  - MESH chart = selected mesh across pins (matrix column); pin series on an
    achromatic grey ramp (light→dark, canonical order — legend names them; no
    pin colours per §4).
  - PIN chart = selected pin across meshes (matrix row); mesh-palette series.
- New `chartJs` single-chart renderer with full furniture ALWAYS drawn: bold
  title, x axis (mm, nice-step ticks + "signed error vs reference (mm)" label),
  y axis (counts, nice ticks + gridlines), inset legend (capped + "+N more"),
  LoD band, zero line; solved ⇒ fill = emphasized pose + near-black step
  outline = other pose with a "fill Before · line After" key. Empty half ⇒
  same furniture + centred placeholder ("select a mesh" / "select a pin" /
  "probing pins…" / "reference mesh — no error vs itself") — never blank.
- Both charts mounted side-by-side in fixed 50% halves (`.ins-charts`), no
  reflow; brushing works from EITHER chart via the one shared bridge input
  (gids stay canonical-array indices); hover sentinels (`substHl`) kept per
  chart. Shift readout unchanged. Old `.ins-dist*`/`.ins-ph` CSS pruned.
- Build green.

## ScanPin v12 — Task 2: pins name-only, near-black (§4)

- `Primitives.PinPalette` DELETED; `selectionTint`/`c4bToGrey`/`v3dToGrey`
  deleted too (they existed only for pin-colour de-emphasis — orphaned by the
  removal; the white dashed selection circle is the remaining selected-pin mark).
- New `Primitives.pinInk`/`pinInkV3d`/`pinInkCss` = #292524 (dark warm grey,
  deliberately not the slice line's #000 nor the slate UI text) — the ONE colour
  for every pin mark: 3D influence/contact rings, constellation markers, flag
  pole under the overlays hold, flag name text, focus rings + Top glyphs,
  slice-cell centre-ring (hardcoded in the SliceDiagram boot JS), overlay pill
  border, dock chart pin layers (interim until Task 3 rewrites the chart).
- `ScanPin.PinColor` field removed; `makeAnchor` lost the palette-slot logic;
  GuiRail matrix row projections/payloads/labels reshaped (row head = near-black
  text on the neutral header, selected row = neutral #dbe2ea fill); pin chips in
  the dock + focus head are now bordered neutral labels with near-black names.
- Build green; no `PinColor|PinPalette|selectionTint|c4bToGrey` references left.

## ScanPin v12 — Task 1: Register full-mesh toggle (§7)

- `Model.RegisterFullMeshes : bool` (default false) + `ToggleRegisterFullMeshes`
  message + reducer arm; adaptify re-run.
- Shader plumbing in `MeshView.buildScene`'s GhostOpacity chain: in
  Correspondence with pin isolation on and no placement running, the context
  floor is the toggle — off ⇒ 0 (isolated pins only, context fragments
  discarded), on ⇒ `max GhostOpacity 0.12` (guaranteed visible even if the
  gear's global ghost floor is off). The blob mask (pin emphasis) is untouched
  in both states, so pins stay solid over the ghost context — the spec's
  "without losing pin emphasis". Placement keeps the normal flashlight floor.
- Rail: "Full meshes" toggle button next to "○ New pin" in the Register body
  (`rail-full-meshes`, reuses rail-btn/rail-btn-active styling).
- Client type-check green.
