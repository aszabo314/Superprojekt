# ScanPin v9 — Implementation Spec: matrix navigation, pin identity, visual language

Consolidates the post-interview iteration. Foundational models (§A–D) first, then tasks. Imperative.

## How to use this spec

A **checklist**, executed **in order**; **build green after each task**.

**Binding rules:**
- Implement **every task's behavior**. No skipping, deferring, or partial implementation. If blocked, **STOP and report** the task + blocker.
- **Leeway (HOW, not WHETHER):** names, placement, and plumbing follow existing codebase patterns; where a mechanism exists (`Selection`, `RegView`, `MeshSolo`, `GuiRail`/`GuiFocus`/`GuiInspector`, `ScanPinScene`, `FocusShaders`, `region-distance`, `lsq-pairs`), **extend it, don't rewrite**. Behaviors below are fixed; implementation shape is yours.
- **Prune in-task** anything a change orphans.
- After editing any `[<ModelType>]` file: run `adaptify.sh`; never hand-edit `*.g.fs`.
- **Final output must reproduce the §Completion checklist**, each item `DONE — file:line` or `BLOCKED — reason`. Without it the work is incomplete.
- **Out of scope (do not build now):** matrix row sorting, filtering, Table-Lens compression, name-jump search. Render the matrix as a plain dense list for now.

---

## §A — Pin identity model

Every pin, at creation, is assigned a **triple**, stored on the pin and immutable:
- **glyph** — from a fixed preattentive shape set (e.g. circle, square, triangle, diamond, cross, star…), assigned round-robin/least-used;
- **short name** — a random 2-character pronounceable code, collision-checked against existing pin names and mesh numbers;
- **pin color** — from the **pin palette** (§C), distinct from the mesh palette.

This triple is the pin's identity **everywhere**: the matrix row (§B), the 3D pin flag (a text label at the top of the pin), the focus label, and the color of that pin's distribution samples (§D/T6). Glyph + color is redundant coding (survives greyscale and color-blindness); the name disambiguates beyond the shape set.

## §B — Left-rail matrix model

The left rail is the **pin × mesh matrix** (the navigation backbone), mode-contextual:
- **Rows = pins**: `glyph · name · pin-color · a strip of ≤5 per-mesh cells`.
- **Cell = signed distance to reference** for that (pin, mesh): the ROI-aggregate (median), painted on the **linear-diverging difference colormap** (§C), **before/after aware** (follows `RegView`). **Out-of-ROI** → a faint emptiness glyph (hairline dash/hatch), not blank. There is no "unplaced" state (a pin always has a placed anchor until out-of-ROI).
- The matrix **is** a compact pin×mesh difference heatmap — the same artifact and colormap as the 3D difference.

**Per-mode content (containers invariant):**
- **Overview:** no pins → the rail shows the **mesh roster / columns** (reference ★ designation + per-mesh info). 
- **Correspondence & Inspect:** the full pin×mesh matrix (identical metric in both).

**Selection cascade (the validated flow):** select a **pin row** → tiles (right) become that pin's meshes; select a **cell** (or tile) → the (pin, mesh) selection, focusing all views + tight camera sync (§D). This **replaces** the bottom-dock correspondence list and the separate Overview/Inspect mesh lists.

## §C — Color model

- **Two distinct categorical palettes:** meshes keep theirs; **pins** get a separate non-overlapping qualitative set (ColorBrewer-qualitative-style), capped ~8–12 hues, paired with the §A glyph.
- **Difference colormap → linear-diverging, perceptually uniform** (Kovesi 2015, CET linear-diverging, e.g. blue→grey→red *linear-diverging*) — it avoids the central perceptual flat-spot that kills small-deviation contrast in the current blue-grey-red. Keep the **±LoD₉₅ neutral gate** (within LoD → neutral), then ramp on the linear-diverging map outside it. Apply **everywhere difference is shown**: focus tiles (`FocusShaders.focusColor` mode 1), the soloed-mesh 3D difference, and the matrix cells. (The unsigned variance map may stay sequential.)
- **"Show overlays" modifier** (hold): desaturate the entire scene to **greyscale except the pin color mapping**; reveal each pin's **glyph + name** as a 3D label at the top of its flag and as a label on the focus panel. This makes pin correspondence across views unmistakable.

## §D — Selection & camera model

- One selection (`Selection`) drives everything (already true). Extend so **any** selection/focus change — pin row, cell, tile, 3D pin, correspondence — **tightly syncs all cameras** (3D + focus) onto that spot (fly/recenter). This is the default, not a toggle.

---

## Task 1 — Pin identity (§A)

- Assign glyph + short name + pin-color at pin creation; store on the pin; collision-check the name.
- Render the triple: matrix row (T2), 3D flag **text label at top of pin** (extend `ScanPinScene.pinGlyphs`), focus label, distribution sample color (T6).
- **Verify:** new pins get a unique glyph/name/color; the triple appears in 3D (label), focus, and matrix. Build green.

## Task 2 — Left-rail matrix (§B)

- Build the matrix as the left rail in Correspondence + Inspect; rows = pins with the ≤5-cell strip; cells = before/after-aware signed-distance-to-reference on the §C colormap; out-of-ROI → faint emptiness glyph.
- Overview rail = mesh roster (columns + ★ reference).
- Wire the selection cascade (§B): row → tiles; cell/tile → (pin,mesh) + camera sync.
- **Remove** the bottom-dock correspondence per-mesh list and the separate Overview/Inspect mesh lists; the Correspondence dock reduces to pin meta (name/glyph/radius/`k/n`/Solve).
- **Verify:** the matrix shows the pin×mesh distance heatmap; selecting a row drives the tiles; selecting a cell focuses+syncs; out-of-ROI cells read as empty, not missing. Build green.

## Task 3 — Tiles = mesh browser (§B, Q2)

- The focus tiles are the mesh browser; per-mesh controls (★ reference, visibility, sensor) live **once**, on the **tile control strip** — not duplicated in the matrix (columns show only mesh color+number).
- **Overview:** drop the large focus view; the focus panel is the tile mesh-selector + control strip.
- **Verify:** mesh selection/visibility/reference act from the tiles; Overview has no large focus view; no duplicated mesh controls. Build green.

## Task 4 — Unified correspondence picking (Q3)

- Keep an explicit **arm** button ("edit correspondence") on the selected (pin, mesh). Once armed: clicking in **either** the focus or the 3D view sets the point — **one mode, two surfaces, not two buttons**. Remove the separate focus-pick vs 3D-pick buttons.
- The mode **stays armed until confirmed** (do not drop isolation on the first click); show the **live preview in both** the focus and 3D views; arming brings the linked focus onto this mesh+pin.
- **ROI-clamp** both the manual pick and the auto-seed (no point outside the pin). The **reference mesh is editable** like any other.
- **Verify:** arm once → pick in focus or 3D → point lands, preview visible in both, isolation persists, pick is ROI-clamped, reference editable. Build green.

## Task 5 — Inspect arity by rendering (Q4)

Encode metric arity by render treatment, **no text labels**:
- **single-mesh** (intrinsic): that mesh colored, others out.
- **two-mesh difference** (moving − reference): the **moving mesh in full color + heatmap**, the **reference rendered as an empty outline** for overlap context. 2-mesh analysis lives in the **focus**.
- **all-mesh**: all meshes faint + the **variance aggregate**, in the **3D** view (the overview map).
- **Verify:** the three arities are visually distinct without labels; two-mesh shows reference-as-outline; focus carries the pair, 3D the aggregate. Build green.

## Task 6 — Distribution rebuild (Q5)

- **Sample only within pin ROIs**, on a **density-normalized spatial grid** per ROI (not raw vertex density). Prune invalid/sentinel points.
- Each sample stores `{ position, footprint, value, pinId }`; color each sample by its **pin** (§A color/glyph).
- Add the missing **axis + ±LoD legend/scale**.
- **Bidirectional, per-individual-point brushing:** brush points in the chart → highlight their **surface cells** in 3D/focus; select a region/pin in 3D → highlight its **points** in the chart.
- **Verify:** samples are pin-restricted, grid-normalized, pin-colored, legended; brushing a point lights its surface area and vice-versa. Build green.

## Task 7 — Color system (§C)

- Add the pin palette; wire pin colors (§A) through matrix/3D/focus/samples.
- Replace the difference colormap with the **linear-diverging** map (§C) everywhere difference is shown; keep the LoD gate.
- **Verify:** small deviations near zero are now visible (no central flat-spot); pin colors don't clash with mesh colors. Build green.

## Task 8 — Show-overlays modifier (§C)

- Hold-modifier: greyscale everything **except pin colors**; show pin **glyph + name** as 3D top-of-pin labels and focus labels.
- **Verify:** holding the modifier greys the scene, pins stay colored with glyph+name visible in 3D and focus. Build green.

## Task 9 — Camera sync on selection (§D)

- Any selection/focus change tightly syncs the 3D + focus cameras onto that spot.
- **Verify:** selecting a cell/tile/pin/correspondence flies both cameras to it. Build green.

## Task 10 — Cleanups (grievances)

- **Remove reference-peek entirely** — both the top-bar 👁 Peek (and **R** hotkey) and the focus **⇄ ref** (it exists twice and is now redundant with the reference indicators below).
- **Remove green checkbox/checkmark decorations** that aren't actionable (e.g. roster `overlaps ✓`, any "done" checks).
- **Isolines:** render in a **faint neutral grey**, intensity significantly reduced (no longer the palette color).
- **Focus camera input:** middle-mouse-drag = pan in the focus view, matching the 3D view.
- **Resizable focus panel:** add a drag handle; **aspect-locked** resize.
- **Reference-mesh indicators:** a distinct prominent **outline in 3D** (and/or dim the others) + a **★/indicator glyph** on the reference tile in focus.
- **Displacement legend → focus pane** (low priority): move the displacement glyph legend from the dock into the focus pane.
- **Verify:** peek-ref gone (both); no decorative green checks; isolines subtle; focus pans on middle-drag; focus resizes aspect-locked; reference is clearly indicated in 3D + focus. Build green.

## Task 11 — Audit

- §A–D hold across modes; the matrix is the single pin/correspondence browser; tiles the single mesh browser; one colormap for difference everywhere; camera sync on every selection.
- Removed: bottom correspondence list, separate mesh lists, dual pick buttons, both peek-ref controls, green checks.
- **Verify:** the validated flow (pin row → mesh tile → arm → place) works end-to-end with camera sync and ROI-clamped picks; build + tests green.

---

## Model / message notes (adapt to codebase)

- Pin: + `Glyph`, `ShortName`, `PinColor` (assigned at creation).
- Matrix: a left-rail component reading per-(pin,mesh) ROI-median distance (reuse `region-distance`/probe data), before/after via `RegView`.
- Picking: collapse the two pick messages into one armed mode acting on focus + 3D; keep `Corr*` raycast paths, drop the duplicate button.
- Distribution samples: + per-sample `{pos, footprint, value, pinId}`; brushing reads/writes a brushed-set in `Selection`/`Hovered`.
- Colormap: swap `FocusShaders.focusColor` mode-1 ramp to linear-diverging; reuse for matrix cells + soloed-3D difference.
- Remove: reference-peek (`SetReferencePeek`, `SetFocusPeekReference`, **R** hotkey), the focus pick/3D pick split, decorative `✓`.

## Completion checklist (reproduce and fill in)

- [ ] T1 Pin identity triple (glyph/name/color) assigned + rendered everywhere — DONE file:line / BLOCKED
- [ ] T2 Left-rail matrix; cells = before/after distance; out-of-ROI glyph; cascade; old lists removed — DONE / BLOCKED
- [ ] T3 Tiles = mesh browser; controls consolidated on tile strip; Overview drops large focus — DONE / BLOCKED
- [ ] T4 Unified armed picking (one mode, focus+3D, persistent, preview both, ROI-clamp, reference editable) — DONE / BLOCKED
- [ ] T5 Inspect arity by rendering (single / moving-color+reference-outline / all-mesh aggregate) — DONE / BLOCKED
- [ ] T6 Distribution: pin-ROI samples, normalized grid, pin-colored, legend, bidirectional per-point brushing — DONE / BLOCKED
- [ ] T7 Color: linear-diverging difference map everywhere + LoD gate; pin palette — DONE / BLOCKED
- [ ] T8 Show-overlays greyscale-except-pins + glyph/name labels — DONE / BLOCKED
- [ ] T9 Camera sync on every selection — DONE / BLOCKED
- [ ] T10 Cleanups (peek-ref ×2 removed, green checks, isolines subtle, focus middle-drag, focus resize, reference indicators, displacement legend) — DONE / BLOCKED
- [ ] T11 Audit; build + tests green — DONE / BLOCKED

## Acceptance criteria

- Pins have a stable glyph + short name + distinct color shown in matrix, 3D label, focus, and samples.
- The left rail is the pin×mesh distance matrix and the sole pin/correspondence browser; tiles are the sole mesh browser; bottom correspondence list and separate mesh lists are gone.
- Correspondence picking is one armed mode usable from focus or 3D, persistent, ROI-clamped, reference included; no dual buttons.
- Inspect arity is readable from rendering alone (reference-as-outline for two-mesh; aggregate in 3D).
- Distribution samples are pin-restricted, grid-normalized, pin-colored, legended, and brushable both ways to the surface.
- The difference map is linear-diverging with the LoD gate; pin and mesh palettes don't clash; show-overlays greyscales all but pins.
- Every selection tightly syncs both cameras.
- Peek-ref (both), decorative green checks removed; isolines subtle; focus pans on middle-drag and resizes aspect-locked; reference clearly indicated.
- Final report reproduces the completion checklist with per-item status + locations.
