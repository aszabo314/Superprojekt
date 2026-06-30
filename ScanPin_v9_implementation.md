# ScanPin v9 — Implementation Summary

What landed on the `v9` branch for the *matrix navigation / pin identity / visual
language* remix (`ScanPin_v9_matrix_identity_spec.md`). Build (type-check,
`-p:WasmBuildNative=false`) green; `dotnet run --project src/Supertests` 45/45.

> **Verify in a browser.** Shader edits (linear-diverging maps, `Greyscale`, grey
> isolines, `pow`) compile but FShade/ESSL pitfalls are only caught in-browser
> (per `CLAUDE.md`). All shader code is float32-only / lambda-free.

---

## Foundational models (§A–D)

- **§A Pin identity** — every pin gets an immutable triple at creation: a
  preattentive **Glyph**, a random pronounceable **ShortName** (2 chars,
  collision-checked vs other pins + mesh numbers), and a **PinColor** from a
  dedicated pin palette. Glyph + colour share a least-used palette slot (redundant
  coding); the triple is the pin's identity in the matrix, the 3D flag label, the
  focus chip, and the distribution sample colours.
- **§B Left-rail matrix** — the rail in Correspondence + Inspect is the
  pin × mesh difference heatmap (rows = pins, cells = before/after signed distance
  to the reference on the §C colormap, out-of-ROI → a hatch glyph).
- **§C Colour model** — two non-overlapping categorical palettes (mesh vs pin) +
  a single **linear-diverging** difference map used everywhere difference is shown.
- **§D Selection & camera** — any selection/focus change tightly syncs both cameras
  onto that spot (default, not a toggle).

---

## Task-by-task

### T1 — Pin identity triple
- `Primitives.PinPalette` (10 ColorBrewer-Dark2-style hues + 10 Unicode glyph
  shapes, index-paired) and `Primitives.PinIdentity.shortName` (consonant+vowel,
  collision-checked, guid-seeded).
- `ScanPin` gains `Glyph`/`ShortName`/`PinColor`; assigned in
  `ScanPinUpdate.makeAnchor` (least-used slot, collision-checked name).
- Rendered: 3D flag label (`ScanPinScene.pinLabels` — ShortName in PinColor, an
  always-on-top `Sg.Text`), focus head chip (`GuiFocus.pinChip`), matrix row, and
  distribution sample colours.

### T2 — Left-rail pin × mesh matrix
- `GuiRail.matrixView` — header (mesh colour+number), rows (glyph·name·swatch·cells).
- Cell value = the pin's probe **median** for that mesh (the probe re-centres so
  0 = reference median and refetches on `RegView`, so cells are before/after-aware);
  painted with `Primitives.Diff.color`; out-of-ROI → `mx-cell-empty` hatch; probe
  pending → `mx-cell-pending`.
- Cascade: pin-row click → `SelectPin`; cell click → `FrameCorrespondence` (locate +
  camera sync) when an anchor exists, else select + focus.
- `ScanPinUpdate.ensureProbe` now probes **every** pin (per-pin debounce) so the
  matrix has a cell for each (pin, mesh).
- Removed: the bottom-dock correspondence per-mesh list and the Inspect rail mesh
  list; the Correspondence dock is now pin meta (identity chip · name · radius ·
  k/n · Solve). Inspect channel toggles moved to the Inspect dock.

### T3 — Tiles = mesh browser
- `FocusScene.focusTile` carries the per-mesh control strip (★ reference, visibility,
  sensor); `multiples` lists **all** meshes (hidden dimmed) so hidden ones can be
  re-enabled; the reference tile gets a gold ring + ★.
- Overview drops the large single (`GuiFocus`); the panel is the tile selector.
- Matrix columns show only mesh colour + number; the Overview dock became a compact
  focused-mesh summary.

### T4 — Unified armed correspondence picking
- `CorrSetMode` + `Corr3DPick` collapsed into one `Model.CorrArm : (pin, mesh) option`.
- One arm button (`GuiFocus.setBtn` → `ToggleCorrArm`) on the selected (pin, mesh);
  while armed the mesh is isolated (main-view `wheelIsolation` reads `CorrArm`), the
  linked focus is brought onto it, and clicking in **either** the focus or the 3D
  view sets the point. The mode **stays armed** until disarmed.
- `PickCorrespondenceAt` is now **ROI-clamped** (a pick outside the pin sphere is
  rejected) and **reference-editable** (editing the reference moves its `RefAnchor`).
- Live aim ghost shown in both views (`ScanPinScene.corrPreview` + the focus overlay).

### T5 — Inspect arity by rendering
- `MeshView` `GhostOpacity`: when a mesh is soloed in Inspect, every *other* mesh —
  the reference included — renders as an **empty outline** (fill discarded; the
  always-on outline pass keeps the silhouette). So two-mesh difference = moving
  colour+heatmap with the reference as an outline; all-mesh = the variance aggregate
  in 3D; the focus carries the pair.

### T6 — Distribution rebuild
- **Server** (`MeshProbe`): `sampleAlongAxis` now returns world surface positions
  alongside the axial distances; `ProbeDistribution` gains `Positions` (flattened
  xyz, aligned 1:1 with `Samples`) + `Footprint`. Sampling is the existing
  area-density grid; cylinder test prunes out-of-ROI/sentinel points.
- **Client**: the distribution aggregates **all** pins per moving-mesh lane, sample
  rain coloured by pin, with the axis + ±LoD₉₅ band/legend.
- **3D sample cloud** (`ScanPinScene.sampleBrush`): the hovered pin's samples as
  small crosses at their surface positions, in the pin colour.
- **Bidirectional brushing** at pin granularity via `Selection.Hovered` (`HoverPin`):
  the dock pin legend ↔ the chart highlight ↔ the 3D cloud.
- *Known limit:* per-individual-*sample* mouse-brushing on the chart canvas is not
  wired (the canvas is display-only; see the separate T6 plan).

### T7 — Colour system
- `Primitives.Diff` (linear-diverging, Kovesi CET-D style: neutral → red(+)/blue(−)
  with a near-zero `t^0.6` boost so small deviations stay visible) + the matching
  FShade ramp in `FocusShaders.focusColor` (mode 1) and `MeshShaders` (enc 1). ±LoD
  gate kept (`FocusLod`, probe LoD in the matrix). Pin palette ≠ mesh palette.

### T8 — Show-overlays modifier
- Hold modifier (top-bar **🎨 Overlays** + hotkey **O**) → `Model.ShowOverlaysHeld`
  → `Greyscale` uniform desaturates the mesh shader. Pins are separate geometry, so
  the pin-coloured flag labels stay coloured and read clearly against the grey scene.

### T9 — Camera sync on selection
- Centralized in the reducers: `SetFocusedMesh (Some m)` flies the 3D camera to the
  mesh (the focus single auto-shows it); `SelectPin (Some id)` flies to the pin
  centre; matrix cells use `FrameCorrespondence` + `FocusScene.focusOnWorld`.

### T10 — Cleanups
- **Reference-peek removed entirely**: top-bar 👁 Peek + the **R** hotkey + the focus
  ⇄ ref button + the `ReferencePeekHeld`/`FocusPeekReference` fields/messages/reducers
  + the `peekTarget` shader path.
- Decorative green checks gone (the `overlaps ✓` and `✓ solved` decorations were
  already removed in T2/T3); the remaining ✔/⚠/✖ are workflow-status semaphores.
- Isolines now render in a **faint neutral grey** (the edge pass paints palette
  colour only for depth-break silhouettes; parity-flip isolines get grey).
- Focus **middle-drag = pan** (matches the 3D view).
- **Resizable focus panel**, aspect-locked (a left-edge drag handle; the single's
  height tracks the width).
- **Reference indicators**: a prominent gold bbox outline in 3D + the gold ★ tile.
- Displacement glyph legend moved into the focus pane.

### T11 — Audit
- Build green; tests 45/45; no references to removed symbols remain.

---

## Model / message deltas

- `Model`: + `CorrArm` (replaces `CorrSetMode`+`Corr3DPick`), + `ShowOverlaysHeld`;
  − `ReferencePeekHeld`, − `FocusPeekReference`.
- `Messages`: + `ToggleCorrArm`, + `SetShowOverlays`; − `ToggleCorrSetMode`,
  − `StartCorr3DPick`, − `SetReferencePeek`, − `SetFocusPeekReference`.
- `ScanPin`: + `Glyph`/`ShortName`/`PinColor`.
- `ProbeDistribution` (client + server): + `Positions`, + `Footprint`.

## Known caveats (browser verification)

1. Shader changes are build-clean but need an in-browser compile pass.
2. T6 brushing is pin-granular, not per-individual-sample (canvas is display-only).
3. T8 greyscale targets the main 3D view, not the focus pane.
4. `ScanPinScene.pinLabels` snapshots the immutable identity via `AVal.force` inside
   `ASet.map` — a common pattern, but a runtime/browser verification item.
