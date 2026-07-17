# ScanPin v12 — Implementation Spec

Refinements from the 2026-07-16 demo feedback, plus the new **slice mode** measurement view. Imperative.

## Binding rules

- Implement **every task and sub-bullet**, in order; **build green after each task**. No skipping/merging/partial work. If blocked, **STOP and report** the task + blocker.
- **Leeway (HOW, not WHETHER):** follow existing codebase patterns; reuse existing machinery (pin isolation / ghost floor, the intrinsic **shape** channel, `region-distance`, `RegView`, `Selection`, the matrix, outline rendering, near-plane clip, the histogram dock) — **extend, don't rewrite**. Behaviors are fixed; shape is yours.
- **Prune in-task** anything a change orphans.
- After editing any `[<ModelType>]` file: run `adaptify.sh`; never hand-edit `*.g.fs`.
- **Final output must reproduce the §Completion checklist** (`DONE — file:line` / `BLOCKED — reason`). Without it the work is incomplete.
- **Out of scope (do not build):** any pin colour or pin glyph; false-coloured 3D dots; horizontal stretch; hoverable ordinates in true-scale; a hatch colour cap; hover-enlarge.

---

## Task 1 — Register: full-mesh toggle (§7)

- Add a toggle in Register mode that shows the **full meshes** as context (reuse the pin-isolation layer: toggle = pin isolation off). Pins stay emphasized; full meshes render at the ghost floor behind them. Default = current (isolated pins only).
- **Verify:** toggling shows/hides the surrounding full meshes without losing pin emphasis. Build green.

## Task 2 — Pins: name-only identity, near-black (§4)

- **Remove pin colour and pin glyph entirely** (both dropped). Pins are identified by **name** only.
- Pin GUI elements — the **name label** and the **slice cell centre-ring** — use a **near-black dark neutral** (a very dark warm grey, *not* pure `#000`, so it stays distinct from the slice main line's true black). Matrix row headers use near-black text on the neutral header.
- Delete the pin palette and every remaining pin-colour reference.
- **Verify:** no pin colour/glyph anywhere; pin labels + centre-rings are near-black; the pin palette is gone. Build green.

## Task 3 — Detail dock: two charts (§3)

- Replace the single selection-driven diagram with **two fixed, standard, well-labelled charts** (matplotlib-grade titles, axes, legends), always both present, side by side, no reflow:
  - **Mesh chart** = the selected mesh's error distribution across its pins (= the selection's matrix **column**), before/after.
  - **Pin chart** = the selected pin's error distribution across meshes (= the selection's matrix **row**), before/after.
- **Empty state:** if only a mesh (or only a pin) is selected, the other chart shows **full furniture (title/axes/legend) with a placeholder** ("select a pin" / "select a mesh") — never a blank or collapsed panel.
- Remove the old single-diagram path and its selection UI.
- **Verify:** both charts always render with correct furniture; each populates from its axis of the selection or shows its placeholder; before/after is clearly readable. Build green.

## Task 4 — Placement suitability overlay (§2)

A **fused** overlap+shape overlay, **auto-on only while a pin placement is armed** (vanishes otherwise).

- **Overlap by mesh colour:** in regions covered by **≥2 meshes**, paint the reference surface with a **hatch/stipple woven from the present meshes' colours** (no colour cap). Where **< 2 meshes** reach, render **flat, textureless grey** (the region visibly loses detail).
- **Shape modulates richness:** within valid (≥2) regions, modulate hatch crispness/saturation by the **min shape-quality across the present meshes** — crisp/saturated where all are well-formed, muted where any is poor. Net gradient: flat grey (invalid) → muted hatch (valid, poor shape) → crisp hatch (valid, good shape).
- **Ghost-composited:** the hatch is fed through the **ghost/transparency** rendering so **isolines and shape indicators remain visible through it** (semi-transparent overlay, not opaque).
- **Hard prohibit** placement where < 2 meshes are in range: the hover placement indicator goes **very transparent** and a **cursor-side tooltip** reads "no overlapping meshes here"; placement is refused.
- **Verify:** arming placement shows the fused overlay; overlap hatches in mesh colours; invalid areas are flat grey and block placement with the tooltip; isolines/shape show through; the overlay disappears when not arming. Build green.

## Task 5 — Slice mode: constrained camera (§5)

Slice mode is the app's **to-scale measurement view**. Activated when a **pin is selected** and slice mode is toggled on.

- Camera switches to **orthographic, centred on the pin centre**.
- **Entry azimuth:** snap to the **10°-step azimuth closest to the current perspective camera's horizontal view direction** (minimal visual jump).
- **Constrained controller (its own mode):** mouse rotates **azimuth only** around the pin centre, **snapped to 10° steps**; **pitch locked, zoom locked**. Scroll wheel **pushes the near clip plane forward/back** through the pin, sweeping the visible height profile.
- The height profile is highlighted by the **existing outline rendering**; an **alpha falloff behind the cut** keeps only the profile + a few cm visible.
- **Easy exit** back to the regular perspective 3D view.
- **Verify:** selecting a pin + slice mode gives an orthographic pin-centred view entered at the nearest 10° azimuth; rotation snaps to 10°, pitch/zoom locked; scroll sweeps the near plane; profile is outlined with alpha falloff; exit restores the normal view. Build green.

## Task 6 — Slice mode: dots of interest (§6, stage 1)

- Brushed sample dots render in 3D as **neutral** dots (**remove the false-colouring**).
- In slice mode, dots **closest to the cut plane** (the "dots of interest", capped at a small maximum count) render at **full strength**; all other dots **fade with distance from the cut** (the same proximity→opacity rule as the surfaces).
- **No ordinate lines and no hover in true-scale** (distances are sub-pixel there).
- **Verify:** brushed dots are neutral; in slice mode the near-cut dots stay bright while others fade by depth; no ordinates/tooltips appear in true-scale. Build green.

## Task 7 — Slice mode: stretch + ordinates (§6, stage 2)

- Add a **stretch toggle** (optional) that applies **vertical-only exaggeration** (no horizontal stretch), blowing up the vertical scale until small disagreements become interactable.
- In stretch mode only: each **dot of interest** drops an **ordinate line to the reference** (distance to reference, per the active error measure); the ordinate is **hoverable** → tooltip with the **true** error value (HTML overlay; **not** the stretched pixel distance).
- A persistent **"exaggerated ×N"** badge is shown whenever stretch is active.
- **Verify:** stretch exaggerates only vertically; ordinates appear for dots of interest and hover shows the true value; the ×N badge is always visible while stretched; ordinates never appear outside stretch. Build green.

## Task 8 — Slice mode: histogram cross-highlight (§6, bonus)

- Dots of interest stay **highlighted in the detail charts** (Task 3) as the slice camera moves.
- **Verify:** moving the slice updates which samples are highlighted in the charts. Build green.

## Task 9 — Prune + audit

- **Remove the plan-mode experimental overlay height-profile diagrams** (§5). Keep the **matrix-cell slice** (v11) untouched.
- Remove orphaned pin-colour code (Task 2), the old single-diagram path (Task 3), and false-colour dot code (Task 6).
- **Verify:** plan-mode height-profile overlays gone; matrix slice intact; no orphaned code; build + tests green.

---

## Completion checklist (reproduce and fill in)

- [ ] T1 Register full-mesh toggle (context via pin-isolation off) — DONE file:line / BLOCKED
- [ ] T2 Pins name-only, near-black label + centre-ring; pin palette/glyph removed — DONE / BLOCKED
- [ ] T3 Two standard charts (mesh=column, pin=row), always present, empty-state furniture, no reflow — DONE / BLOCKED
- [ ] T4 Fused placement overlay auto-on-while-arming: mesh-colour hatch (≥2), flat grey + hard-prohibit (<2), shape modulates richness, ghost-composited — DONE / BLOCKED
- [ ] T5 Slice camera: ortho pin-centred, entry azimuth = nearest 10° to camera, azimuth-only 10° snap, pitch/zoom locked, scroll sweeps near plane, outline + alpha falloff, easy exit — DONE / BLOCKED
- [ ] T6 Neutral dots; dots of interest (capped) bright, others fade by cut proximity; no true-scale ordinates — DONE / BLOCKED
- [ ] T7 Vertical-only stretch toggle; ordinates + true-value hover tooltips in stretch only; persistent ×N badge — DONE / BLOCKED
- [ ] T8 Dots of interest cross-highlight in the detail charts as the camera moves — DONE / BLOCKED
- [ ] T9 Plan-mode height-profile overlays removed; matrix slice intact; prune; build + tests green — DONE / BLOCKED

## Acceptance criteria

- Register has a full-mesh context toggle; pins carry no colour or glyph (name only, near-black); the pin palette is gone.
- The dock shows two standard labelled charts (mesh=column, pin=row), always both present with empty-state furniture and no reflow.
- Arming placement shows a fused overlay: mesh-colour hatch where ≥2 meshes overlap, flat grey elsewhere (placement hard-prohibited with a cursor tooltip), shape modulating richness, composited through the ghost layer so isolines/shape stay visible.
- Slice mode is an orthographic, pin-centred, to-scale view: entry azimuth snaps to the nearest 10° to the current camera, rotation is azimuth-only in 10° steps, pitch/zoom locked, scroll sweeps the near plane, profile outlined with alpha falloff, easy exit.
- Dots are neutral; near-cut dots-of-interest stay bright while others fade by depth; ordinate lines + true-value hover exist **only** in vertical-stretch mode, which carries a persistent "exaggerated ×N" badge.
- Plan-mode overlay height-profiles removed; matrix-cell slice untouched.
- Final report reproduces the completion checklist with per-item status + locations.
