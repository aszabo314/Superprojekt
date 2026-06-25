# ScanPin v3 — Coding Spec: Per-step GUI + Correspondence workflow

Adds step-contextual content to the (fixed) layout and rebuilds manual correspondence picking. Imperative.

**Two hard rules:**
1. **Implement every section. Do not skip, defer, or partially implement.** If a section is blocked, stop and report it — do not silently omit.
2. **Prune aggressively.** Anything made unreachable or left dead by this change is deleted in the same change.

---

## A. Per-step GUI rule (foundational)

**Containers are invariant; their contents are step-contextual.** The four regions — rail, 3D viewport, focus panel, bottom dock — **never move, resize, appear, or disappear** when the workflow step changes. Only what lives *inside* the dock, and what is emphasized/interactive in the viewport and focus panel, changes per step. Content swaps use a short cross-fade, never a hard cut, and never reflow the container.

**Mode indication** (required, since content is contextual): (a) the active rail step is highlighted — the rail is the canonical mode indicator; (b) the **dock header** shows the active mode as a one-word label (`Manual move` / `Correspondences` / `Inspect` …) — a mode identifier, not a statement; (c) the viewport/focus **affordances visibly change** (what is drawn prominently and what is clickable) — this is the strongest mode cue.

**Per-step content map:**

| Step | Bottom dock | Viewport foreground | Focus panel |
|---|---|---|---|
| 1 Reference | light reference/sensor info | meshes + outlines | passive |
| 2 Manual move | **error inspector** (raincloud) | moved mesh solid, others ghosted | textured single (translate-drag) |
| 3 Correspondences | **correspondence manager** (§F) | **constellation glyphs** prominent + interactive; false-color off | textured single + draggable handles (§E) |
| 4 Fine ICP | light solve readout (RMS before/after, run) | ICP residual preview | passive |
| 5 Inspect | **error inspector** (raincloud) | false-color heatmaps + pin glyphs | false-color multiples (compare) |
| 6 Commit | before/after preview + commit/discard | before/after preview | passive |

The focus panel and the dock are always present (no open/close); only their content changes.

## B. Rail change

Split the current `Coarse align` step into **two** steps: **`Manual move`** then **`Correspondences`**. Rail becomes: `Reference · Manual move · Correspondences · Fine ICP · Inspect · Commit`. Update `WorkflowStep` (replace the single coarse case with `ManualMove` + `Correspondences`). `Solve coarse` is an **action inside the Correspondences manager**, not a step.

## C. Correspondence states & ROI membership

For a registration pin, each moving mesh is in exactly one state:
- **placed** — a correspondence point exists inside the ROI.
- **placeable** — mesh has surface inside the ROI but no point yet.
- **out-of-ROI** — mesh has **no** surface within the ROI (`roiRadius`); correspondence not applicable.

Compute membership server-side during seed: closest-point distance from pin centre to each mesh ≤ `roiRadius` (the probe cylinder: radius `InnerRadius`, length `fixedProbeLength`). Store as `Correspondence.InRoi : Map<string,bool>`.
- **Fix the auto-seed ROI clamp** (handoff §F): `seedAnchors` currently does an **un-clamped** closest-point. Clamp to `roiRadius`; a mesh whose closest point is outside → **out-of-ROI**, not seeded.
- **`k/n` counts in-ROI meshes only** (n = moving meshes in ROI; out-of-ROI meshes are excluded from completeness, not counted as missing).

## D. 3D constellation (context, read-only)

For the **selected** registration pin, in the main viewport, draw per mesh a **small, visible billboarded glyph** at its correspondence point, in the mesh's palette colour. Replace the current non-pickable `anchorGlyphs` line-tetra cue.
- **Reference glyph**: same shape, **haloed / slightly larger** — "same kind of point, highlighted."
- **Lines to reference**: thin line from each moving glyph to the reference glyph. The bundle is the constellation; its spread = pre-registration disagreement at that location.
- Selected pin's constellation bright; other pins' dimmed.
- Slight depth bias so glyphs render on top of surfaces; rely on the ghost-isolation modifier to clear meshes when reading the constellation.
- Out-of-ROI meshes: no glyph.

## E. 2D editing in the focus panel (the precise editor)

In the **Correspondences** step, each in-ROI mesh's correspondence point is a **draggable handle** in that mesh's 2D view (large single + small-multiple cells), projected with the active `FocusProjection`.
- **Draw the markers** (currently the focus-2D pick renders nothing — handoff §5): render each handle in the large single **and** in every multiple cell, in mesh colour; the selected/edited handle emphasized; show a placement confirmation on drop.
- **Drag** → server raycast → 3D point → update that mesh's anchor (`PickCorrespondenceAt`, ROI-constrained). Out-of-ROI cells show no handle and are not editable.
- **Reference target crosshair**: in each moving panel, draw a small opaque crosshair at the **projected reference point** (no transparency overlay) — the target the user matches the handle to; the handle-to-crosshair offset is the correspondence's contribution.
- **Peek-reference (modifier, juxtaposition not overlay)**: holding a modifier re-renders the large single as the **reference mesh, textured, in the current panel's own projection+origin** (same frame), then restores on release — a blink-style flip that surfaces the feature offset. No transparency (detailed textures must stay legible).
- Click a multiple cell → promote it to the large single for editing.

## F. Correspondence manager (dock content, step 3)

Per moving mesh, one row: **swatch · state (placed ✓ / placeable ○ / out-of-ROI ⊘) · residual-or-spread · `⟳` re-seed · edit**. No source column.
- **Reference row** distinguished at top.
- **Out-of-ROI** rows greyed, `⊘`, no re-seed/edit.
- Residual: post-solve `Correspondence.Residuals` in mm; before any solve show the **pre-solve spread** (distance of this mesh's point to the reference point) so the column is never empty.
- **`⟳` re-seed** this one mesh (revive `NavAction.ReseedCorrespondence` / add `ReseedMesh pinId meshId`); the old demote→re-promote workaround is no longer the only path.
- **edit** → promote that mesh to the focus large single.
- **`k/n`** (in-ROI; §C) + **`Solve coarse`** button.

## G. Linked highlighting (brushing)

Wire the orphaned thread (handoff §5: `WorkflowPinHover` has no emitter; `Readiness`/`NavTo` have no callers).
- **Pin hover** (rail row, manager) → brighten that pin's 3D constellation (emit `SetWorkflowPinHover`).
- **Mesh-row hover → "isolate this"**: hovering a manager row **ghost-isolates that mesh** in the 3D viewport (ghost all others) **and** brightens its 3D glyph **and** pulses its 2D handle. Add `CorrRowHover : (string*string) option` + `SetCorrRowHover`; drive the existing ghost-isolation path. Restore on leave.
- Bidirectional: selecting/hovering a 3D glyph or 2D handle highlights its manager row.

## H. Model / message changes

- `WorkflowStep`: replace coarse case with `ManualMove` + `Correspondences`.
- `Correspondence`: add `InRoi : Map<string,bool>`.
- Add: `SetCorrRowHover`, `ReseedMesh` (or revive `ReseedCorrespondence` via `NavTo`), `FocusPeekReference : bool` + its set-on-modifier messages.
- Revive or reimplement `Readiness.compute` / `NavTo` to feed the manager's per-mesh diagnostics and re-seed (do not leave them dead); the rail readiness pill may keep its inline form.
- Emit `SetWorkflowPinHover` (currently never emitted).
- Run `adaptify.sh` after editing any `[<ModelType>]` file; never hand-edit `*.g.fs`.

## I. Prune

- Delete the unconstructable `AnchorSource` variants `AnchorPatch2D` / `AnchorViolinAxial` (and their `label`/`tag`/`ofTag`/RegJson cases) — keep `AnchorAuto`, `AnchorPick3D`.
- Replace the old line-tetra `anchorGlyphs` with the §D constellation; remove the old cue.
- If any `Readiness`/`NavTo`/`WorkflowPinHover` machinery is **not** revived per §G, delete it rather than leaving it dead. (Reviving is preferred.)

## J. Acceptance criteria

- Layout containers never move/resize/appear/disappear on step change; only dock content + viewport/focus emphasis change, via cross-fade; dock header names the active mode.
- Rail has six steps incl. separate `Manual move` and `Correspondences`.
- Auto-seed is ROI-clamped; each moving mesh resolves to placed / placeable / out-of-ROI; `k/n` over in-ROI meshes only.
- 3D constellation: per-mesh glyphs + reference glyph (haloed) + lines to reference; selected pin emphasized; out-of-ROI omitted.
- Focus 2D: draggable handles drawn in large single **and** cells, with placement confirmation; reference target crosshair (opaque); modifier peek-to-reference renders the reference in the same frame; drags ROI-constrained.
- Correspondence manager: rows with state/residual-or-spread/re-seed/edit, no source column, reference row, out-of-ROI greyed, `k/n`, `Solve coarse`.
- Linked highlighting works in both directions; manager-row hover isolates that mesh in 3D.
- Dead `AnchorSource` variants and any non-revived orphan machinery removed; build green.
- Every section A–I implemented; nothing skipped.
