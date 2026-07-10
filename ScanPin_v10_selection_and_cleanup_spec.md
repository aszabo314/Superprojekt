# ScanPin v10 — Implementation Spec (two specs, one checkpoint)

Two specs in one file. Run **Spec A** to completion, **STOP at the checkpoint gate**, report, and wait for approval; then run **Spec B**. Imperative.

## Binding rules (apply to both specs)

- Implement **every task and sub-bullet**, in order; **build green after each task**. No skipping/merging/partial work. If blocked, **STOP and report** the task + blocker.
- **Leeway (HOW, not WHETHER):** follow existing codebase patterns; where a mechanism exists (`Selection`, the matrix, `GuiFocus`, `GuiInspector`, the outline pass, the palettes), **extend it, don't rewrite**. Behaviors are fixed; shape is yours.
- **Prune in-task** anything a change orphans.
- After editing any `[<ModelType>]` file: run `adaptify.sh`; never hand-edit `*.g.fs`.
- Each spec ends with a **completion checklist**; reproduce it filled (`DONE — file:line` / `BLOCKED — reason`). The checkpoint report and the final report each require their checklist.

---

# SPEC A — Selection unification

**Root problem (from the interview): multiple orthogonal selections; selecting anything mostly does nothing elsewhere.** Fix: one selection, driven by the matrix, followed by every view.

## §A0 — The selection model

- **One active selection**, of one of three kinds: **mesh** (a column), **pin** (a row), or **cell = (pin, mesh)** (the intersection) — plus the existing transient **hovered**. The matrix is the canonical driver (column-head = mesh, row-head = pin, cell = intersection), but roster rows, focus tiles, pin markers, and legend chips set the *same* state.
- **No competing selection state anywhere.** Any pre-existing separate selection (notably the distribution chart's own pin legend/selection UI) is removed and replaced by reads of the one selection.
- **Every view is a pure follower** of the selection (and hovered). No panel signals another directly.
- **Grammar change (supersedes "selection never moves a camera"):** selection **frames the focus panel** (2D single + small multiples zoom onto the selection). The **main 3D view stays the stable context** — selection isolates/emphasizes there but the main camera still moves only on double-click/locate. (Overview+detail: main = context, focus = selection-driven detail.)

## Task A1 — Extend `Selection` to {mesh | pin | cell} + wire the matrix as driver

- Represent the active selection as one of mesh / pin / cell(pin,mesh) (+ hovered). Cell selection is first-class.
- Matrix column-head → mesh; row-head → pin; cell → cell. Keep the existing cell "locate" isolation, but route it through the one selection state.
- **Verify:** selecting a column, row, or cell in the matrix sets exactly one coherent active selection; no other selection state exists. Build green.

## Task A2 — Every view follows the selection

Apply this resolution table (all views read the one selection):

| Selection | Main 3D | Focus single + tiles | Detail graph (A3) | Legend |
|---|---|---|---|---|
| **mesh** | isolate/emphasize that mesh | frame that mesh | that mesh's distribution | that mesh's map |
| **pin** | emphasize that pin (footprint/constellation) | frame that pin's region | that pin across meshes | active map |
| **cell** | isolate that mesh + that pin, emphasize the correspondence | **hard-zoom** onto that (pin,mesh) correspondence | that single (pin,mesh) distribution | that pair |

- **Overview:** selecting a mesh now has visible effect (roster ↔ tiles ↔ 3D emphasis + focus framing) — fix the "selection does nothing" report.
- Focus single + tiles **camera-frame the selection** (the grammar change); main 3D camera unchanged except via double-click/locate.
- **Verify:** one selection change updates 3D emphasis, focus framing, tiles, graph, and legend together, in every mode. Build green.

## Task A3 — Detail graph → one selection-driven diagram

- Collapse the multi-lane chart to **one diagram** populated by the current selection (mesh → its distribution; pin → across meshes; cell → the single pair; nothing selected → ensemble aggregate as one diagram).
- **Remove the chart's own selection UI** (the pin legend chips as a navigation control); keep the metric toggles (**Difference | Displacement**, **M3C2 | Δz**) — those configure the view, not selection.
- The chart reads the one selection; selecting in the matrix drives the chart, and vice-versa (chart interactions set the same selection).
- **Verify:** the dock shows a single diagram that changes with the matrix selection; no separate chart selection remains. Build green.

## Task A4 — Brushing = sole focus

- Left-drag an x-range in the chart → the brushed samples become the **only** focus: **suppress the false-color surface map** and show **only the brushed sample dots** (colored by value) in 3D/focus. Plain click clears and restores the map.
- Brushing sets a brush state the legend and 3D read (it is a transient refinement of the selection).
- **Verify:** brushing hides the false-color map and shows only the brushed dots; clearing restores. Build green.

## Task A5 — Legend always tracks selection + brush + active map

- The legend title/range update with the active selection, the active map (Difference/Displacement/Variance/intrinsic), **and while brushing** and while maps change.
- **Verify:** the legend never goes stale — it updates during selection changes, map changes, and brush drags. Build green.

## Task A6 — Audit (Spec A)

- One selection drives all views; no orphan selections; the matrix (mesh/pin/cell) is the sole driver; focus frames selection, main 3D is stable context.
- **Verify:** end-to-end — pick a cell → 3D isolates, focus hard-zooms, graph shows the pair, legend matches; brush → only dots; build + tests green.

## Completion checklist — Spec A

- [ ] A1 `Selection` = {mesh|pin|cell}+hovered; matrix is driver; no competing selection — DONE file:line / BLOCKED
- [ ] A2 All views follow the resolution table; focus frames selection; Overview selection has effect — DONE / BLOCKED
- [ ] A3 Detail graph = one selection-driven diagram; own selection UI removed; metric toggles kept — DONE / BLOCKED
- [ ] A4 Brushing suppresses the false-color map, shows only brushed dots; clears on click — DONE / BLOCKED
- [ ] A5 Legend tracks selection + brush + active map continuously — DONE / BLOCKED
- [ ] A6 Audit; build + tests green — DONE / BLOCKED

---

# ===================== CHECKPOINT (HARD GATE) =====================

**STOP HERE.** Do not start Spec B. Build client + server, run tests, run the app, and verify every Spec A checklist item. **Produce the filled Spec A checklist and a short report, then WAIT for explicit approval.** Only after approval, proceed to Spec B.

# =================================================================

---

# SPEC B — Technical & visual cleanup (selection-agnostic)

## Task B1 — Disjoint color palettes

The pin palette, mesh palette, and scalar-gradient palettes currently overlap (and the crosshair/pin-circle disagree). Terrain textures are greyscale — exploit that.

- Make the **three families mutually non-colliding**: **scalar gradients** (Difference/Variance/Displacement/intrinsic) own the vivid data ranges; **mesh identity** and **pin identity** use hues that don't read as gradient values and don't collide with each other. Reduce simultaneous large-area color — identity colors ride on **thin marks** (outlines, swatches, lines, matrix columns, markers), gradients fill areas. Leaning pins harder on their **glyphs** (freeing color load) is acceptable.
- **Fix:** the 2D picked-point **crosshair, the pin circle, and the correspondence markers all use the pin's color** (currently the crosshair and circle disagree).
- **Verify:** no two families collide confusingly; crosshair/circle/markers share pin color; run once and eyeball a scene with meshes + pins + a difference map. Build green.

## Task B2 — Camera-adaptive isolines

- Isoline **count derives from camera distance** to the orbit center, **snapped to discrete ticks** (no continuous change while orbiting) — fewer lines when zoomed out so they stop obstructing the colors. Applies to the elevation isolines (and the difference-map metric isolines if they over-crowd).
- **Verify:** zooming out reduces isoline count in snapped steps; zoomed-out colors are legible. Build green.

## Task B3 — Overview intrinsic-channel fixes

- **Dst (Range):** unify the scale **across all meshes** (not per-mesh normalized) and show a **color legend** for it (may live in the Overview panel).
- **Shp (Shape):** add a **threshold filter slider** that makes triangles below the goodness threshold **transparent**.
- **Inc (Incidence):** **bug** — some clearly-bad triangles render as good; investigate the incidence computation and fix.
- **Verify:** Dst is comparable across meshes with a legend; the Shp slider hides sub-threshold triangles; Inc no longer marks bad triangles good. Build green.

## Task B4 — Independent outline pre-pass

- Move per-mesh outline generation out of the main pass into a **separate pre-pass**, then **composite** outlines into the main render — so an outline can be shown **without its mesh body** ("outline-only", the lowest-fidelity representation). Keep the current silhouette + isoline look.
- **Verify:** a mesh can render as outline-only (no fill) via the composited pre-pass; normal outlines unchanged otherwise. Build green.

## Task B5 — Inspect de-clutter

- In **Inspect**, drop the photo **textures** and the **ghost fills**; the **false-color map is the base surface**. Non-inspected moving meshes render **outline-only** (via B4) instead of ghosted fills, so the active map is the focus.
- **Verify:** Inspect shows the false-color map cleanly — no competing textures or ghost fills; context meshes are outline-only. Build green.

## Task B6 — Tooltip bug

- Hover tooltips are sometimes wrong — investigate and fix (verify the tooltip reads the hovered entity, not a stale one).
- **Verify:** tooltips match the hovered mesh/pin/cell across panels. Build green.

## Completion checklist — Spec B

- [ ] B1 Three disjoint palettes; crosshair/circle/markers share pin color — DONE file:line / BLOCKED
- [ ] B2 Camera-distance-adaptive, tick-snapped isolines — DONE / BLOCKED
- [ ] B3 Dst unified + legend; Shp threshold slider; Inc bug fixed — DONE / BLOCKED
- [ ] B4 Independent outline pre-pass; outline-only representation possible — DONE / BLOCKED
- [ ] B5 Inspect drops textures + ghosts; false-color map is base; context = outline-only — DONE / BLOCKED
- [ ] B6 Hover tooltip bug fixed — DONE / BLOCKED

## Acceptance criteria (whole file)

- Exactly one selection (mesh | pin | cell), driven by the matrix, followed by 3D, focus, tiles, graph, and legend; focus frames the selection; brushing suppresses the map to show only brushed dots; legend never stale.
- Spec A was checkpointed (built, verified, reported) before Spec B began.
- Three color families are disjoint; crosshair/circle/markers share pin color; isolines thin out with distance; Overview Dst/Shp/Inc fixed; outlines render independently and drive an outline-only representation; Inspect is de-cluttered to the false-color map; tooltips correct.
- Both completion checklists reproduced with per-item status + locations.
