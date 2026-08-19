# ScanPin v14 · D1 — Brush & error-map behavior (P0 bugs + picking hygiene)

Post-dry-run fixes for the study. Contains both P0 study-blockers. The brush, the error map, and the histogram are one system — fix them together.

## Rules (binding)
- Implement **EVERY task to completion** — do not stop early, do not skip a task, do not add unrequested scope or polish. Build green after each. Blocked → **STOP and report** (task + reason).
- **AGGRESSIVE DELETION** of anything superseded — no dead paths. `adaptify.sh` after `[<ModelType>]` edits; never hand-edit `*.g.fs`. End with the **checklist**.

## Task 1 — [P0] Clear-brush must actually clear the rendered brush
- Bug: pressing "clear brush" clears the state but the brush still draws. Investigate and fix so clearing removes the brush from **both** state and render — the histogram selection, the 3D dots, and the brush's scene color-isolation (scene un-whitens, meshes restore) all go away.
- **Verify:** after clear-brush, no histogram selection, no 3D dots, and no color-isolation remain; the scene returns to normal. Build green.

## Task 2 — [P0] Error-map toggle resets on dataset change
- Bug: the error-map toggle persists across datasets; it must **reset (off)** when the dataset changes.
- **Verify:** switching datasets leaves the error map off. Build green.

## Task 3 — [P1] Disable error-map + clear brush during pin placement/edit
- Whenever a **pin is being placed or a point edited** (an armed placement/edit pick), **turn the error map off and clear the histogram brush** — a terrain pick needs the texture visible, so these modes must not be active then. They do **not** auto-restore afterward (predictable, user re-enables).
- **Verify:** arming a placement or point-edit turns the map off and clears the brush; the terrain texture is visible for the pick. Build green.

## Task 4 — [P1] Draggable brush
- Once a brush is drawn in any error histogram, allow **click-drag to reposition** it along the axis (width preserved, bin-snapped). Dragging updates the 3D dots live.
- **Verify:** a drawn brush can be grabbed and moved to a new position keeping its width; dots follow. Build green.

## Checklist
- [ ] T1 [P0] clear-brush removes selection + dots + color-isolation from render — DONE file:line / BLOCKED
- [ ] T2 [P0] error-map toggle resets off on dataset change — DONE / BLOCKED
- [ ] T3 [P1] placement/edit turns off error map + clears brush (texture visible) — DONE / BLOCKED
- [ ] T4 [P1] draggable brush (width-preserving, bin-snapped, dots follow) — DONE / BLOCKED
