# ScanPin v11 — Implementation Spec: the slice cell

Replaces the matrix's solid-colour cells (a false-colour of the correspondence distance) with a **cross-section slice diagram**, and fixes matrix highlighting. The residual fill is dropped entirely — it is uninformative for near-registered data and its colour is saturated flat.

## Binding rules

- Implement **every task and sub-bullet**, in order; **build green after each task**. No skipping/merging/partial work. If blocked, **STOP and report** the task + blocker.
- **Leeway (HOW, not WHETHER):** follow existing codebase patterns; reuse existing machinery (the pin cross-section profiles, `region-distance`, the matrix, `RegView`, `Selection`) — **extend, don't rewrite**. Behaviors below are fixed; implementation shape is yours.
- **Prune in-task** anything a change orphans.
- After editing any `[<ModelType>]` file: run `adaptify.sh`; never hand-edit `*.g.fs`.
- **Final output must reproduce the §Completion checklist** (`DONE — file:line` / `BLOCKED — reason`). Without it the work is incomplete.
- **Out of scope (do not build):** leave-one-out error, influence/leverage, intrinsic-quality borders, residual-direction ticks, magic-lens quality picking, split before/after cells, hover-enlarge of cells. **Leave the Overview pin-flag profile chart untouched.**

---

## §A — The slice diagram (definition)

**Geometry**
- **Centre** = the pin centre in **world space** (the reference correspondence point). *All* profiles in a cell are sampled along the **same world-space line** through this centre — do **not** centre each mesh on its own correspondence point, or the misregistration would be hidden by construction.
- **Azimuth** = computed **once per pin**, from the **reference mesh**: the horizontal direction of **maximum z-range** within the pin ROI (≈ the local dip direction). **Shared by every cell in that pin's row** — never per-cell, or rows stop being comparable.
- **Window** (horizontal extent) = **one global constant for the whole dataset**, derived as `N_samples × (coarsest mesh's sample spacing)` — "coarsest" = the largest sample spacing among loaded meshes; `N_samples` is a tunable constant (default ~5). This guarantees even the coarsest mesh shows shape. Same window for every cell.
- **Vertical extent** = **one global value**, shared by all cells: a robust (≈95th percentile) of (reference relief within the window + |mesh offset|) across all cells, symmetric about the reference at centre. Lines leaving the frame are **clipped and marked with a small arrow at the frame edge**.

**Marks (draw order)**
1. **Background:** neutral **grey**. No mesh-colour wash.
2. **Context slices:** `k` parallel slices of **this mesh only** (not the reference), offset perpendicular to the azimuth (default `k = 2` each side; spacing a fraction of the window). **Faint/transparent**, behind everything.
3. **Reference band:** the **reference surface profile thickened by ±LoD₉₅** for this (pin, mesh) pair. Present in **all modes** (Register and Inspect alike).
4. **Main line:** **this mesh's** surface profile — **black, with a white outline/halo** so it reads on any background. Sampled at the mesh's **currently displayed pose**, so it follows `RegView` (before/after).
5. **Centre ring:** a ring in the **pin colour** at the centre with a small dark centre dot/notch — one mark serving both pin identity and "this diagram is centred on a point".

**Reading (no text needed):** *is the black line inside the grey band?* Inside → agrees within detection. Parallel gap → rigid/datum offset. Wedge → tilt. Divergent shape → real change. Jagged → noisy data. Off-frame → clipped arrow.

**Payoff:** the matrix is `RegView`-aware — toggling **before → after** makes every line drop into its band at once.

**Empty cells:** a mesh with no surface in the pin ROI keeps the existing out-of-ROI emptiness glyph (grey background + ring, no band, no line).

**Reference column:** its cells render normally (the reference profile inside its own band — trivially centred), which makes the reference column a built-in visual key for what a perfect cell looks like.

---

## Task 1 — Server: batched pin cross-sections

- Provide per-pin cross-section data in **one request per pin** (reuse the existing pin cross-section machinery): given the pin centre, ROI and the computed azimuth, return for the **reference** and **each mesh**: the main profile polyline, the `k` context polylines (per mesh), and the per-pair `LoD₉₅` half-width.
- Return profiles at **both poses (load and solved)** in the same response so before/after toggling is instant (no refetch).
- Compute and return the **per-pin azimuth** (max z-range on the reference within the ROI).
- **Verify:** one request per pin returns reference + all meshes, main + context lines, both poses, and the azimuth. Build green.

## Task 2 — The slice-diagram component (§A)

- Build **one** component implementing §A exactly (single-mesh: black main line + halo, mesh-only context slices, reference band, grey background, pin ring), parameterised by size.
- Window, azimuth, vertical extent, band, ring and clipping behavior are **identical in every instance** — one uniform style.
- Expose the tunables (`N_samples`, context-slice count/spacing, vertical-extent percentile) in the **debug menu** for on-data tuning.
- **Verify:** the component renders per §A; tunables adjust live. Build green.

## Task 3 — Matrix cell = slice diagram

- Replace the matrix cell's solid distance-colour fill with the slice diagram (§A). **Remove** the cell's residual-colour path (keep the difference colormap where it is still used for surface heatmaps).
- Keep existing cell behavior: `(pin, mesh)` cell selection, hover and locate (v10 selection model) — unchanged. **No hover-enlarge.**
- Out-of-ROI cells keep the emptiness glyph.
- If the profile reads poorly at the current square cell footprint, a modest **landscape aspect** is permitted (matrix layout adapts).
- **Verify:** every cell shows a slice; lines move into their bands on before→after; selection/hover/locate still work; no residual-colour fill remains. Build green.

## Task 4 — Matrix highlighting (reference column + selection cross)

Black is now **data ink** (the main slice line) — do **not** use black outlines for highlighting; they would compete with the diagrams.

- **Reference column (persistent):** a **thick reference-gold border** spanning the whole reference column, plus a **gold column header**. Always visible, independent of selection.
- **Selection cross (by de-emphasis, not added ink):** on a cell/row/column selection, keep the selected **row and column at full opacity** and **dim all other cells** (≈40% opacity). The cross emerges by contrast, adding no strokes inside the diagrams.
- **Headers:** fill the selected **row header (pin)** and **column header (mesh)** with their accent so the selection stays identifiable when scrolled.
- **Selected cell:** a single strong **accent ring/frame** on the intersecting cell.
- The two channels must not collide: **reference = colour (gold)**, **selection = opacity + accent** — a selected cell inside the reference column must read as both.
- Replace the current faint blue tint highlight.
- **Verify:** the reference column is unmistakable at a glance; selecting a cell/row/column produces a clear cross by dimming, with accented headers and a ringed cell; selection inside the reference column reads as both. Build green.

## Task 5 — Prune + audit

- Remove the orphaned matrix residual-fill code, the old faint-blue selection tint, and any now-unused cell colour scaling.
- **Verify:** matrix cells are slice diagrams; one component, one style, one global window/vertical scale, per-pin azimuth; highlighting per Task 4; build + tests green.

---

## Completion checklist (reproduce and fill in)

- [ ] T1 Batched per-pin cross-section endpoint (ref + meshes, main + context, both poses, azimuth) — DONE file:line / BLOCKED
- [ ] T2 Slice-diagram component (§A) + tunables in debug menu — DONE / BLOCKED
- [ ] T3 Matrix cell = slice diagram; residual fill removed; selection/hover/locate intact; out-of-ROI glyph kept; no hover-enlarge — DONE / BLOCKED
- [ ] T4 Reference column in gold; selection cross by de-emphasis + accented headers + ringed cell; no black outlines — DONE / BLOCKED
- [ ] T5 Prune orphaned fill + old blue tint; audit; build + tests green — DONE / BLOCKED

## Acceptance criteria

- Matrix cells are cross-section slice diagrams: grey background, faint mesh-only context slices, reference ±LoD₉₅ band, black main line with white halo, pin-colour centre ring with centre dot.
- All profiles in a cell are sampled along **one world-space line through the pin centre**; the azimuth is **per pin** (max z-range on the reference), shared across the row.
- **Window** and **vertical scale** are **single global values** (window = `N_samples` × coarsest sample spacing); off-frame lines are clipped with an edge arrow.
- The band appears in **all modes**; one uniform component/style across the matrix.
- Cells follow `RegView`: toggling before→after moves the lines into their bands.
- The reference column is persistently gold-bordered; selection produces a clear row/column cross by **dimming** others (no black outlines), with accented headers and a ringed selected cell.
- The old residual-colour cell fill and faint-blue selection tint are gone; no LOO, influence, quality border, direction tick, or hover-enlarge was built; the Overview pin-flag chart is untouched.
- Final report reproduces the completion checklist with per-item status + locations.
