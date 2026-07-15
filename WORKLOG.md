# Worklog

## WP (2026-07-15d): audit cleanup 2/11 — dead visibility machinery removed

The audit found per-mesh visibility was write-only-true: no message or UI
control could hide a mesh (the toggle UI fell out in an earlier cleanup),
so the whole apparatus was dead. Decision (user): REMOVE, don't restore.

- `Model.MeshVisible` deleted; `MeshVisibility.shown` is now solo-only
  (`Some s ⇒ name = s`, `None ⇒ true`) and lost its map parameter at all
  ~12 call sites. `LocateState.PrevVisible` gone.
- `UpdateHelpers.setMeshVisible`/`allVisible` deleted; `exitSolo` is a
  plain `MeshSolo = None` (behaviour-identical — the old path no-opped on
  the always-all-true map). `ensureVisible` in SetSelection gone.
- `ReadinessInput.VisibleMovingMeshes` → `MovingMeshes` (RegistrationModel
  + Primitives.Readiness.input + Supertests).
- Dead GUI remnants: rail `rail-row-dim` classWhenNot (could never fire),
  focus-tile `ft-hidden`, both CSS rules; GuiFocus/FocusScene.single mesh
  resolution no longer filters by raw toggles.
- Side profit: `GuiOverlays.anyRangeOn` now respects solo via `shown`
  (the Range legend no longer shows for a solo-hidden mesh's heatmap),
  and the latent VarianceComputed stale-landing race lost its only trigger.
- CLAUDE.md: visibility rule rewritten (solo-only), setMeshVisible
  sentence dropped. Adaptify re-run.

Type-check green, Supertests 29/29.

## WP (2026-07-15c): audit cleanup 1/11 — bug batch

Five-agent codebase audit ran first (full rundown in chat); this WP series
executes the cleanup. Commit 1 = the verified bugs:

1. **Solve race.** `SolveCoarse` was the only unguarded async fan-out: a
   stale `CoarseSolved` could land after `ensureSolveValidity` cleared the
   registration (with `SolveInputs = None` it became uncleanable) or after a
   dataset switch. Now: `UpdateHelpers.solveGen` (bumped by SolveCoarse,
   SetReferenceMesh, SetActiveDataset, the validity clear); all per-mesh
   results land as ONE `CoarseSolved(gen, solved, failed)` batch (also kills
   the N-invalidation-cycles churn — one cycle per solve now); handler drops
   stale generations.
2. **Displacement arrows freeze.** `FocusScene.arrowSegs` read
   `loaded.mesh.Value` (plain ref, no dependency edge) — entering Inspect +
   Displacement before the mesh landed froze the arrows at empty forever.
   Added the `loaded.pos.GetValue t` touch (same pattern as `scalarData`).
3. **Pick hijack.** The coordinate-cross tick numbers + X/Y/Z tip `Sg.Text`
   nodes (SceneGraph) lacked `Sg.NoEvents` — clicks on terrain visually
   behind them landed on the label plane. Added.
4. **MeshCache race.** `GetOrAdd` factory could run twice on concurrent cold
   requests (bbox warm-up vs first fetch), leaking the losing Embree
   Device/Scene undisposed. Now `Lazy<LoadedMesh>` with eviction on failure
   (Lazy caches exceptions — a transient load error must stay retryable).
5. **Active dataset invisible.** `.tb-gear-btn.active` CSS rule didn't
   exist; the gear dataset picker never highlighted. Added.
6. **Stuck focus drag.** Releasing a focus-single drag outside the panel
   never cleared `dragging` (no pointer capture there by design) → phantom
   panning. `OnMouseLeave` now ends the drag.

Type-check green, Supertests 29/29.

## WP (2026-07-15b): white pick preview + move arrow · strong pin selection · isoline opacity slider

Type-check green, Supertests 29/29. ONE shader touch (OutlineEdge gains the
`IsolineOpacity` float32 uniform) — browser compile check owed.

1. **Pick-point feedback.** The armed correspondence hover preview (main-3D
   wire sphere+cross, focus-Top aim ghost) is now WHITE = "not committed";
   the click commits it into the pin-coloured marker (no extra state — the
   committed marker was already pin-coloured). If the hovered point is
   > 10 cm (metric world) from the current anchor, a white arrow (thin
   shaft + line-triangle tip) is drawn old → new: main 3D orients the head
   toward the eye (`addArrow`), focus Top draws it in XY with the
   screen-fixed glyph size (`addArrowXY`). Original anchor = RefAnchor on
   the reference, else the mesh anchor at the displayed pose.
2. **Pin selection emphasis.** While a pin is selected (SelPin or SelCell):
   every OTHER pin's marks go luminance-greyscale (`Primitives.c4bToGrey`/
   `v3dToGrey`) — 3D equator+contact rings, flag name label, constellation
   markers, focus single + tile influence circles; the matrix keeps colour
   (it dims, §T4). The selected pin gets a dashed white selection circle
   (72 segs, every other drawn), radius ×1.12, centred at the pin XY lifted
   to the median Z of its contact-ring vertices (`ScanPin.
   selectionCircleCentre`; centre Z until rings land) — rendered on top in
   main 3D, focus single and tiles (Top: XY plane; 360°: eye-facing like
   the influence circles).
3. **Isoline opacity slider.** The grey elevation isolines' alpha (was
   hard-coded 0.45) is now `Model.IsolineOpacity` → `IsolineOpacity`
   uniform in `OutlineEdge.fragment`; gear row "Isoline opacity" 0–1.
   Both `buildFromNode` callers wired (main 3D + the focus single's gold
   reference outline). Silhouettes stay opaque.

User-side browser pass owed: shader compiles in-browser (the new uniform);
white ghost/arrow in main 3D + focus Top (arrow appears only past 10 cm,
head readable at both scales); greyscale flip on select/deselect in all
three views; dashed circle lands on the terrain outline height (also in
360°); isoline slider fades contours live, 0 = gone, silhouettes unaffected.

## WP (2026-07-15): focus-head cleanup · glyphless pin identity · screen-constant pin flags

Client-only batch (no server changes). Type-check green, Supertests 29/29.

1. **"Focus" head label removed** (GuiFocus) — redundant to the matrix focus
   visualization. `.focus-head` gained `min-height: 29px` so the strip no
   longer collapses when every control is hidden (Overview, no pin) —
   containers never move.
2. **Pin glyphs removed.** Identity = PinColor + random 2-char ShortName,
   shown everywhere as ONE colour-filled element with the name inside:
   matrix row head (`.mx-pinname` replaces `.mx-glyph`+`.mx-name`), focus
   pin chip (swatch span dropped, chip itself takes the colour), dock
   identity chip (`.ins-pinident`, `-sw`/`-gn` rules pruned), flag-tip pill
   text, wheel-label / legend suffixes, dock chart layer names, delete
   confirm. Dead code pruned: `ScanPin.Glyph` field, `PinPalette.glyphs`/
   `glyph`, the matrixRow glyph param + row projection slot. Adaptify re-run.
3. **Pin flags are screen-constant** (pole + top ring + name + base cross —
   the wire-box jack). One driver: `ScanPin.flagHeightRender` = 0.10 × eye
   distance (render), clamped in METRIC WORLD to [0.1 m, 20 m], everything
   × the new gear slider `FlagScale` (0.2–5×, `SetFlagScale`, clamp 0.1–10).
   All elements derive from that height (ring 0.16 H, cross arms 0.10 H,
   name 0.30 H at 1.25 H); `flagMagnitude` (pole height ∝ error) removed.
   The name billboards about Z toward the eye (yaw = atan2(dx, −dy) on the
   RotationX(π/2) text frame); the base cross deliberately does NOT rotate.
   The flag segs/trafos now read `view` per frame — sanctioned exception
   noted in CLAUDE.md (adaptive-perf section). The 2D flag-tip pills
   (GuiOverlays) track the same height via `Camera.view.Location`.

User-side browser pass owed: chip styling in all three hosts (matrix head at
64px rowhead, focus head, dock), flag legibility near/far (clamps at 10 cm /
20 m), name billboarding while orbiting, base cross static, gear slider
scaling flag + bounds together, flag-tip pills still landing on the pole tips
during the show-overlays hold.

## WP (2026-07-14): ScanPin v11 — the slice cell (spec: ScanPin_v11_slice_cell_spec.md)

Matrix cells are now cross-section slice diagrams (reference ±LoD₉₅ band vs
the mesh's black profile along the pin's dip line); the residual colour fill
is gone; matrix highlighting reworked (gold reference column, selection cross
by dimming). Client type-check + server build green, Supertests 29/29; new
slice endpoint smoke-tested on :8004 (azimuth unit+horizontal, both poses in
one response, empty meshes → empty cells, legacy request shape still served).

Spec↔convention reconciliations (documented deviations, all in spirit):

- **LoD₉₅ rides the probe, not the slice response.** The per-pin probe (one
  request, both poses, already cached with identical invalidation triggers)
  carries refStd/meshStd; the cell derives `1.96·√(refStd²+std²)` — the exact
  statistic the old residual fill used. Recomputing it in the slice handler
  would duplicate MeshProbe's sampling for no new information. "One request
  per pin" holds: profiles+azimuth in the (one) slice request, LoD in the
  (one) probe request.
- **Both poses in one response** via per-mesh `transformOther` (sent only
  once solved — before a solve there is no second pose and RegView is locked
  anyway); the existing Slice/SliceOther pairing + SetRegView swap machinery
  is unchanged, `ensureSlices` just fills both caches from one fetch.
- **Azimuth is server-side** (`MeshAnalysis.dipAzimuth`): LSQ height fit
  z=ax+by+c over the reference's ROI vertices, dip = gradient direction,
  sign-canonicalised (+X, tie +Y); flat/degenerate → +X fallback. Reference
  rule = the probe's (global ref → host mesh → first mesh). Pose-independent
  because the reference never moves.
- **Window from real data**: bboxes payload gains `spacing` (mean edge
  length, stride-sampled) per mesh; window = SliceNSamples × coarsest
  spacing (`ScanPin.sliceWindow`), pin-diameter fallback until spacings land.
- **Show-overlays consumers follow the new frame** (data change, components
  untouched): the label profile charts + 3D centre-slice lines now cut along
  the per-pin azimuth instead of fixed world-X, and context planes follow the
  window-fraction offsets (2 each side default, was 3 at r/4). The slice clip
  radius stays ≥ the pin sphere so those overlays keep spanning the pin.

Implementation notes:

- T1 server: `SliceMeshDto{Transform,TransformOther}` + `ReferenceName` on the
  request; response `{azimuth, meshes:[{name, planes, planesOther|null}]}`.
  Legacy requests (old fields, no reference) still answered (uDir fallback).
- T2 client: `PinSlice.UDir`; `ScanPin.sliceNormalOf/sliceWindow/sliceOffsets/
  sliceClipRadius/sliceToWorld/sliceUV` (uDir-parameterised; the old
  `sliceUDir`/`sliceNormal` constants removed); `ScanPinModel.invalidateSlices`
  (slice-only, for geometry tunables); Model gains MeshSpacing +
  SliceNSamples/SliceContextCount/SliceContextSpacing/SliceVertPercentile
  (gear rows; the percentile is view-only, the rest refetch). `ensureSlices`
  fires ONE request per pin; on failure both Slice and SliceOther fail
  together so nothing is stranded in Running.
- T2 component: `SliceDiagram` (GuiRail.fs) — data-slice JSON attribute + one
  observedRender SVG boot per cell (cells stay stable divs that only
  re-attribute; identical payloads short-circuit in JS). Band from the ref
  centre polylines ±LoD, context = off-centre planes at 0.16 opacity, main =
  black 1.4px over a white 3px halo, ring at chart (0,0) (= the pin centre) in
  pin colour + dark dot, off-frame arrows at the worst exceedance.
- T3 cells: payload built in an AVal.custom over (rowData projection, peek,
  window, vertical extent); peek selects SliceOther with committed fallback.
  Empty rule: out-of-ROI ∨ zero probe count ∨ no ≥2-pt centre polyline →
  hatch glyph + ring-only payload. The reference column renders its own
  profile inside its own band (the built-in "perfect cell" key). Vertical
  extent: percentile of (ref relief in window + |pair median|) over ready
  (pin, moving) cells, committed pose only, so peek/After moves lines without
  rescaling frames. Cell geometry 34×26 px fixed (landscape), rowhead
  104→64 px so 5 columns fit the 256 px rail.
- T4 highlighting: reference = colour channel (gold header + gold side-rails,
  bottom-closed on the last row); selection = opacity+accent channel
  (mx-cell-dim 0.4 outside the selected row/column, headers inline-filled
  with pin/mesh accent at 0.4 alpha + neutral inset, selected cell uses an
  accent OUTLINE so it coexists with the ref column's inset rails). Old blue
  tints (.mx-col-sel/.mx-row-sel/.mx-cell-colsel) removed.
- T5 prune: `Primitives.Diff.color`/`colorV3` removed (the matrix was their
  last consumer; `colorSignedV3`/`isoStep`/anchor constants stay — the shader
  painters still mirror them). CLAUDE.md (slice bullet, new matrix-cell rule,
  API list), README matrix bullet updated.
- Out of scope honoured: no LOO/influence/quality borders/direction ticks/
  magic lens/split cells/hover-enlarge; Overview pin-flag chart component
  untouched (its data follows the new azimuth — see reconciliations).
- Browser pass owed (no shader changes): cells render + compare across a row;
  before→after drops lines into bands; peek flips cell geometry; tunables
  re-slice live; empty/pending cells; gold column + dim cross + accent
  headers/outline (incl. selected-cell-in-ref-column reading as both); the
  show-overlays profile charts/3D slice lines along the new azimuth.

FOLLOW-UP (user, 2026-07-14): two adjustments after the browser pass.

- Empty cells read as EMPTY: the pin-colour ring dropped from out-of-ROI
  cells — bare hatch background only (`SliceDiagram.ringOnly` removed,
  `CellEmpty` carries no payload).
- Pin isolation (AnchorGhostMode / blob mask) is now Register-EXCLUSIVE: the
  SetWorkflowStep default (on in Correspondence, off elsewhere) is its only
  automatic driver — SetSelection no longer mutates it anywhere (previously
  Inspect SelPin forced it on, SelCell forced it on, SelNone/SelMesh forced
  it off), so in Inspect the meshes always show fully; an Inspect cell locate
  still solos the mesh, it just no longer lights the pin ROI. The Inspect
  DeletePin postlude (reset isolation to off) was orphaned by this and
  removed. Manual gear "Isolate pins" toggle + the placement flashlight are
  untouched.

## WP (2026-07-13b): 360° zoom parity

The selection close-up now behaves identically in both focus projections;
the toggle switches the whole panel and keeps the focused item in view.
Client build green, Supertests 29/29 (client-only).

- Pano zoom convention flipped to VERTICAL fov (`panoFov`/`panoHalfTans`;
  `panoCam` derives the horizontal Frustum argument per aspect) so zoom
  references the view height exactly like Top's vertical half-extent — one
  close-up definition for both projections. Fov floor 4° → 0.05° (a far/small
  pin needs a telescope fov); pano wheel cap 200 → 2000 to match.
- `selBaseFrame` gained an `isPanoA` aval: pano base = look from the fixed
  panorama eye AT the target (azimuth/elevation), zoom = 45°/θ where θ =
  atan(pinRadius·1.05 / distance) → vertical half-fov = the pin's angular
  radius (influence circle fills the height, same as Top).
- Single: `camPair` unified — pano mesh/none keeps its persistent per-mesh
  `panoKey` state; pin/cell targets mint fresh offsets per selection change
  in both projections. Drag ("grab the world") and wheel anchor maths now run
  on the EFFECTIVE zoom (base ⊕ user) — they previously read the raw user
  cval, which was only correct while the pano base was inert.
- Tiles follow the Top/360° toggle (adaptive view/proj switch inside the one
  render control, `isPanoA` = ProjPano minus the Inspect-displacement
  collapse, mirroring the single); tile pin rings switch to the eye-facing
  silhouette in pano.
- The SelCell reducer no longer forces `FocusProjection = ProjTop` — the
  forcing existed because the focus framing maths was Top-only; a locate now
  respects the user's projection (CLAUDE.md rule added: never force a
  projection at a click site). Note: the anchor marker glyphs + aim ghost
  remain Top-only (screen-fixed glyph sizing is ortho maths) — in 360° a
  located cell shows the circle, not the cross glyph.
- Browser pass owed: pano close-up on pin/cell select (single + tiles), wheel
  anchor stability in pano at deep zoom, Top↔360° round-trip keeping the item
  framed, tiles switching projection, displacement channel still collapsing
  to Top everywhere.

## WP (2026-07-13): focus panel + selection polish (9-item batch)

All nine items done; client + server builds green, Supertests 29/29. No server
changes (client-only) → integration suite not rerun.

1. Pin focus hard-zoom: `FocusScene.selBaseFrame` — SelPin extent now
   `max 0.05 (InnerRadius × 1.05)` (influence circle fills the view); SelCell
   keeps the ×4 marker hard-zoom (3D FlyToPoint convention).
   FOLLOW-UP (user: "sometimes clicking another control zooms back out"):
   three causes found + fixed. (a) SelCell base was ×4 + a 0.5 m floor — any
   pin→cell transition (matrix cell, ✎ arm via ToggleCorrArm, tile click
   while pin selected) zoomed out 4×; pin and cell now share the ONE close-up
   extent, only the centre differs. (b) per-target user offsets were restored
   on re-selection — a stale zoomed-out adjustment came back; pin/cell
   targets now mint a FRESH offset pair on every selection change (`camPair`,
   with a structural-equality guard so spurious re-evals don't wipe a live
   adjustment; catches reducer-driven changes too since they all invalidate
   `Selection.Active`); only mesh-fit + pano offsets stay persistent in
   `camStates`. (c) zoom clamps capped at 200 — a small pin on a large mesh
   needs ext/tgt in the hundreds, and the wheel's matching cap would snap a
   deeper close-up OUT on the first wheel event; both caps raised to 2000.
2. Tiles = pure view: ref/visibility/isolate/outline buttons + the
   `focus-tile-ctrls` strip removed. ★ reference picker moved to the Overview
   rail mesh list (GuiRail.meshRow, between name and mode bar). Orphans
   pruned: `OutlineVisible` model field (adaptify rerun), `SetOutlineVisible`,
   `SetVisible`, `ToggleMeshSolo` messages + reducer cases; MeshView outline
   flag/Sg.Active simplified (a mesh never leaves the G-buffer now);
   `outlineBodyShownAt` deleted; CSS pruned. NOTE: per-mesh visibility and
   manual isolation now have NO direct UI (messages removed) — isolation
   remains selection-driven (Inspect policies, cell locate), un-hide via
   selection `ensureVisible`.
3. Tile click follows the selection: none/mesh → SelMesh(tile); pin/cell →
   SelCell(pin, tileMesh); re-click of the current target = the double-click
   zoom (ZoomToMesh / fly-to-marker). Shared `FocusScene.cellZoom` also backs
   the matrix cell double-click (GuiRail duplication removed). Tile
   OnDoubleClick handler dropped — two clicks naturally select-then-zoom; no
   ClickGate needed (re-click zooms, never toggles).
4. Matrix contrast: new `.mx-cell-colsel` (blue side-rails down the selected
   mesh's column; emitted per cell off `Selection.mesh`), `.mx-col-sel` /
   `.mx-row-sel` upgraded to filled bg + full 2px accent ring, `.mx-cell-sel`
   ring 1px→2px; reference column now reads over its full height (filled gold
   header `#fde68a` + `#b45309` ring, gold side-rails on every `.mx-cell-ref`
   via inset shadows instead of border tints). CSS order = ref < colsel <
   active < sel (same specificity — order is load-bearing).
5. Tiles render pin influence circles (top-down rings, pin colour, selection
   = weight/alpha) via a passOne DepthTest.None overlay; `ViewportSize` now
   bound in the tile render control (Lines shader needs it).
6. 360° single: pin circles were absent — now drawn as the approximate sphere
   silhouette (ring facing the eye: basis ⟂ eye→centre). Circles now render
   in BOTH projections and ALL steps (single + tiles, consistent); the anchor
   marker glyphs + aim ghost stay Correspondence+Top (glyph size is ortho
   maths). `addRingXY` generalised to `addRing3D` with explicit basis.
7. Top bar "🗺 Overview" → "🗺 Plan" (+tooltip).
8. Overview dock hint line removed (`ins-ovw*` CSS pruned; the Overview dock
   mode is now empty).
9. Register dock "◌ select a pin" empty-state removed (`.pin-inspector
   .ins-empty` CSS pruned).

Browser pass owed (user): pin hard-zoom framing; tile click matrix
(none/mesh/pin/cell × re-click); circles in tiles + 360°; matrix highlights
(selected column/row/cell, gold reference column, combined states); ★ picker
in the roster; Plan button. No shader changes this WP — ShaderCache still
stale only from the previous WP.

---

# Previous WP — ScanPin v10: selection unification + cleanup

Spec: `ScanPin_v10_selection_and_cleanup_spec.md`. Two specs, hard checkpoint
between them (Spec A report → wait for approval → Spec B).

## Objectives (Spec A — selection unification)
- A1 One `Selection` = {mesh | pin | cell(pin,mesh)} + hovered; matrix is the driver.
- A2 Every view follows the selection (3D emphasis, focus framing, tiles, graph, legend).
- A3 Detail graph = one selection-driven diagram; chart's own selection UI removed.
- A4 Brushing suppresses the false-colour map; only brushed dots show.
- A5 Legend tracks selection + brush + active map continuously.
- A6 End-to-end audit; build + tests green. → CHECKPOINT, wait for approval.

## Objectives (Spec B — cleanup, after approval)
- B1 Disjoint palettes; crosshair/circle/markers share pin colour.
- B2 Camera-adaptive tick-snapped isolines.
- B3 Overview intrinsics: Dst unified+legend, Shp threshold slider, Inc bug.
- B4 Independent outline pre-pass → outline-only representation.
- B5 Inspect de-clutter: false-colour as base, context meshes outline-only.
- B6 Tooltip bug.

## In progress
- Per-mesh FOOTPRINT contours (user report: "only a combined outline of all
  meshes"): root cause is architectural — the combined G-buffer is depth-
  tested, so edges only fire at depth breaks; co-located meshes yield one
  union outline and hidden boundaries aren't in the buffer at all. Fix = a
  second additive coverage pass (channel per mesh, 2×Rgba8 MRT, no depth,
  cap 8 meshes) + `OutlineCoverageEdge` composite painting each channel's
  covered↔uncovered transition in that mesh's colour, gated by the same
  OutlineMask flags. Build green, Supertests 29/29. Browser pass owed: two
  NEW shaders (`OutlineCoverage`, `OutlineCoverageEdge`) compile in-browser;
  each mesh shows its own closed contour in the pair view; additive blend +
  depth-less FBO work on the Aardworx WebGL backend; per-mesh contour also
  respects the ◌ toggle. ShaderCache stale → ./precompileShaders.sh.
- Per-mesh outline toggles (follow-up to B4/B5, user-requested): client build
  green, Supertests 29/29; BOTH outline shaders changed (G-buffer mesh id +
  fragment-depth bias, edge-pass mask) → in-browser compile check + visual
  pass owed (◌ tile toggle both modalities, Inspect pair view: context =
  silhouette only / no isolines, no speckle on the inspected pair where
  epochs are co-located — the 5e-5 depth push may need tuning). ShaderCache
  stale → ./precompileShaders.sh at leisure.

## Done (outline toggles)
- G-buffer (`OutlineGBuffer`): target0.y = MeshId ((index+1)/255, 8-bit exact
  in Rgba8), fragment depth = fc.Z + OutlineDepthBias (silhouette-only
  context meshes get 5e-5 so the co-located inspected pair wins depth ties —
  no per-pixel id/colour alternation).
- Edge pass (`OutlineEdge`): `OutlineMask : Arr<N<32>, V4f>` (Blobs-style),
  slot = centre-pixel id; .X = 1 → silhouette+isolines, 0.5 → silhouette
  only, 0 → nothing.
- Policy (`MeshView.outlineFlagAt`/`outlineBodyShownAt`/`outlineMask`):
  Inspect + MeshSolo ⇒ everything except {solo, reference} = 0.5 (the
  feature: pair view context keeps only its contour — ghosts already gone
  per B5). Toggle modalities resolve from body visibility: `OutlineVisible`
  off + body visible ⇒ flag 0, mesh stays in the G-buffer (occludes); off +
  no body ⇒ Sg.Active false, mesh leaves the buffer (stops occluding).
- Model `OutlineVisible : Map<string,bool>` (sparse false entries, reset on
  dataset load) + `SetOutlineVisible`; ◌ toggle on the focus tile strip.
- `OutlineView.buildFromNode` takes the mask (`maskAllOn` for the focus
  reference overlay; reference node binds dummy MeshId/bias).
  29/29, integration 13/13 on :8004. Final report sent. Awaiting the user's
  browser pass — BOTH mesh shaders changed (MeshShaders: incidence geometric
  normal + shape discard + InspectPlain base; FocusShaders: shape discard) →
  needs the in-browser compile check; ShaderCache entries stale → runtime
  compile fallback, rerun ./precompileShaders.sh at leisure. Eyeball list:
  new palettes everywhere (swatches, rings, chart layers, constellation now
  PIN-coloured, aim ghost pin-coloured), isoline density steps while zooming,
  Dst legend + one range scale across meshes, Shp slider transparency, Inc
  red on artifact triangles, Inspect = plain base + outline-only context,
  Alt-wheel label only while Alt held.

## Done (Spec B implementation notes)
- B1 palettes: meshes = cool/earth 9 (teal/ochre/slate/cyan/brown…), pins =
  vivid warm/purple 10 (orange/fuchsia/violet/pink…, glyph-paired) — both
  clear of the gradient hues (red/blue/green/yellow), no-data grey, gold ★.
  Constellation markers, focus anchor cross+ring and the aim ghost (2D + 3D)
  now use the PIN colour (mesh identity there was the old crosshair/circle
  disagreement); slice-profile lines/dots stay mesh-coloured deliberately.
- B2 ContourSpacing camera-adaptive: ~24 contours per view from orbit radius,
  snapped to nice 1/2/5 world-metre steps; gear IsolineBands = densest cap,
  ≥4 contours at the far end. Difference isolines rely on their existing
  in-shader density fade (no camera term needed).
- B3 Dst: one all-mesh scale (MeshView.rangeMaxWorld) in 3D + focus, legend
  shows outside Inspect while any Dst is on. Shp: ShapeThreshold model field
  (+ SetShapeThreshold, adaptify) → discard below cutoff in both shaders,
  slider in the Overview rail (visible only while a Shp heatmap is on).
  Inc bug: was abs(dot(vertex-normal, toSensor)) — away-facing surfaces and
  smoothed sliver normals read head-on/good; now geometric (screen-derivative)
  face normal, sign-oriented by the stored normal, clamped at 0. Focus CPU
  variant drops abs → clamp 0 (per-vertex; can't do derivatives there).
- B4 outline-only: the outline pass already was an independent offscreen
  pre-pass over ALL loaded meshes (OutlineView G-buffer → composite); added
  the per-mesh body-suppression lever (outlineOnly ⇒ ghost floor 0 ⇒ every
  non-emphasized fragment discards, outline survives).
- B5 Inspect de-clutter: outlineOnly = (WorkflowStep = Inspect) — all ghost
  fills gone, context meshes outline-only; new InspectPlain shader base =
  plain near-white under the false-colour painters (no photo texture /
  palette / slope in Inspect; shading kept).
- B6 tooltip bug: meshWheelLabel showed the persistent ActivePickingLayer
  name at the cursor forever after one Alt-wheel (the layer is never cleared
  — it steers pick priority); now gated on Alt actually held. All other
  tooltips are static per-element titles (audited).
- Docs: CLAUDE.md (palette families, Inspect de-clutter in the ghosting
  contract, adaptive isolines, intrinsic rules), README Inspect bullet.

## Done (Spec A implementation notes)
- A1 `Selection = { Active : ActiveSelection; Hovered }`,
  `ActiveSelection = SelNone | SelMesh | SelPin | SelCell(pin,mesh)`;
  projections `Selection.pin`/`Selection.mesh`. One `SetSelection` message
  replaces `SetFocusedMesh` + `ScanPinMessage.SelectPin` + `FrameCorrespondence`
  (cell selection IS the locate: solo + backup + ProjTop + Inspect pin-ROI).
  ToggleCorrArm aligns Active to its cell. Deleted pin: SelCell→SelMesh,
  SelPin→SelNone. Drivers: matrix col/row/cell, roster, tiles, 3D pin dots,
  3D surface click (background miss → SelNone).
- A2 reducer keeps the per-mode policies (Inspect solo swap etc.); MeshView
  isActive adds selection emphasis outside Inspect (selected mesh solid, rest
  ghost floor). Focus cameras are now DERIVED followers: `selBaseFrame`
  (pin → pin region ×4, cell → that mesh's marker hard-zoom, mesh/none → fit)
  composes with per-(mesh, selection-target)-keyed user pan/zoom offsets;
  tiles frame the selection on their own mesh (small-multiples compare). All
  imperative FocusScene camera helpers (resetCam/focusOnWorld/zoomOnWorldRadius/
  zoomOnPin/currentSingleMesh) deleted with their click-site calls.
- A3 dock chart = ONE selection-driven diagram (mesh→by-pin, pin→by-mesh with
  mesh colours, cell→single pair with median tick, none/ref→ensemble by-pin);
  pin-chip legend UI removed (CSS pruned); metric toggles kept; brush arrays
  restricted to the shown subset (gids stay global canonical).
- A4 brush non-empty ⇒ 3D inspectField → enc 0, focus overlay modes → 0,
  displacement arrows off; brushed dots (shared `ScanPinScene.brushedDotGeometry`,
  now coloured on the SHARED inspect range) render in main 3D + focus single +
  tiles. Click clears (existing bridge) and restores maps.
- A5 legend follows brush ("Brushed samples", diverging over shared range) +
  selection (cell appends pin identity to the Difference title) + channel.
- CSS: `.mx-cell-sel` (selected cell); removed `.ins-dist-leg*`.
