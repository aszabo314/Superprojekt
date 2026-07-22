# ScanPin v13 — Implementation Spec

Isolated fixes from the 2026-07-21 expert session, for the expert study. Ordered **P0 → P1 → P2** (P0 is study-blocking / participant-facing). Imperative.

## Binding rules

- Implement **every task and sub-bullet**, in order; **build green after each task**. No skipping/merging/partial work. If blocked, **STOP and report** the task + blocker.
- **Leeway (HOW, not WHETHER):** follow existing codebase patterns and names (`meshPalette`, `pinInk`, the diverging/variance maps, `Isolate pins`, the orbit camera flights, `Esc` handling) — **extend, don't rewrite**. Behaviors are fixed; shape is yours.
- **Prune in-task** anything a removal orphans.
- After editing any `[<ModelType>]` file: run `adaptify.sh`; never hand-edit `*.g.fs`.
- **Final output must reproduce the §Completion checklist** (`DONE — file:line` / `BLOCKED — reason`).

## DO NOT (out of scope — design track)

Do **not** touch, and do **not** let any task drift into: transitive registration · the transposition set/basket & main-view mesh scrolling · manual per-mesh correspondence sequencing · explicit place/edit/back workflow modes · the navigable peek space · the one-diagram pivot · the collapse/"new truth" mechanism. **Do NOT remove auto-seeding** (its replacement needs the workflow-mode design; removing it now leaves no picking flow).

---

# P0 — correctness & participant-facing

## Task 1 — A1: false-color target

- **The reference mesh is never coloured by error** (no error vs itself). Remove the **Variance σ on the reference** visualization and its legend branch.
- Default Inspect (no selection) paints **every moving mesh with its own difference-vs-reference field** simultaneously (the reference stays the plain Inspect base). Selecting a moving mesh continues to paint its difference on it.
- If the variance computation is used elsewhere, keep the computation but stop painting it on the reference; otherwise remove it.
- **Verify:** in Inspect with nothing selected, moving meshes show their difference fields and the reference is neutral; no variance-on-reference or its legend remains. Build green.

## Task 2 — A2: deterministic before/after

- Displayed pose is a **deterministic function of solve state**: show **After whenever an After exists**, else **Before**. No incidental/automatic pose switches anywhere.
- The **only** automatic transition is **registration invalidation → Before**, and it is made **explicit** (keep the "Registration cleared…" toast; add a visible Before-fallback indicator).
- Editing entry points must **not silently switch to Before**. If the user arms an edit while After is shown, **refuse** and prompt to switch (reuse the existing "edited in the Before state — switch the view" toast) rather than auto-switching.
- **Verify:** after a solve the view is After and stays there; arming an edit in After is refused (no silent flip); invalidation drops to Before with an explicit cue. Build green.

## Task 3 — E1/E2/E3: legibility

- **E1:** disambiguate the two near-identical mesh identity hues (the green/teal-cyan collision) — change one in `meshPalette` so all nine read distinctly.
- **E2:** render mesh outlines **much thicker** by default; add an **outline-thickness slider to the ⚙ debug menu** (tune per participant).
- **E3:** make pin rings, flag names, and correspondence markers **high-contrast on the grey texture** — white core with a dark outline (readable both ways). Keep the pure-white *armed/uncommitted* aim ghost distinct (thinner, no dark outline) so committed vs armed stay separable.
- **Verify:** mesh colours are all distinct; outlines are clearly thicker and slider-tunable; pin/point marks read clearly on grey. Build green.

## Task 4 — F1: global Escape / cancel

- Bind **Esc globally** as deselect/cancel in **every** mode where a selection can be made — clears the current selection, disarms an armed placement **and** the edit-point editor, and cancels a brush. (Keep its existing placement-cancel behavior.)
- **Verify:** Esc cancels/deselects from every state incl. edit-point mode. Build green.

---

# P1 — clarity & onboarding

## Task 5 — B1: remove 3D mesh-picking

- Remove **mesh-surface click-to-select** and the **Alt+wheel isolate-cycle**. Mesh **selection and visibility are controlled only in the 2D GUI** (roster, matrix column-heads, tiles).
- Keep: clicking a **pin's marks** to select a pin; background-click to clear; double-tap-to-recenter (camera, not selection).
- **Verify:** clicking a mesh surface does nothing to selection/visibility; Alt+wheel no longer isolates; 2D selection still works. Build green.

## Task 6 — B2: remove the 360° focus view

- Remove the **360° projection** and the Top/360° toggle; the focus panel is **Top only**. (Its viewpoint role is covered by Task 9.)
- **Verify:** focus panel shows Top only; no 360° toggle or code remains. Build green.

## Task 7 — B3 + D1: replace slice mode with in-view slicing

- **Remove standalone slice mode entirely:** the ▤ Slice button, ⇕ Stretch, the orthographic slice camera + controller, slice chrome (rulers/badges/angle indicator), ordinates, and slice-only wheel/Esc branches. (Keep the **matrix slice-cell diagram** and its tunables — that is not slice mode.)
- **D1 — near-plane slicing in the main 3D view:** add a **near-plane control** (slider or equivalent) that cuts the scene **in place** in the current view. Render a **thick, clearly readable intersection line** where the plane cuts the meshes. No separate view; the user stays in the current spatial context.
- **Verify:** slice mode is gone; the near-plane control cuts in the main view with a thick intersection line; camera/context unchanged. Build green.

## Task 8 — B4: remove the diff-z toggle

- Remove the **M3C2 | Δz** toggle; M3C2 is the sole metric, kept **under the hood** (no UI surface). Drop the metric from legend suffixes/labels where it was exposed.
- **Verify:** no metric toggle or metric name is user-visible; difference maps/charts still work on M3C2. Build green.

## Task 9 — C1: sensor-position viewpoint

- Add an action (per mesh, in the 2D GUI — e.g. a roster control) that flies the **main 3D camera to that mesh's sensor/origin viewpoint** — a transient navigation aid (also helps reference selection). Reuse the dataset-load scan-camera framing.
- **Verify:** the action flies to a mesh's sensor viewpoint. Build green.

## Task 10 — C2: no surprising camera motion

- Audit all main-camera motion. The main 3D camera moves **only** on explicit focus/zoom actions: double-click zoom (roster/matrix/tile), matrix cell fly, the sensor viewpoint (Task 9), and dataset load.
- **Remove** the **recenter-on-pin-placement** flight and any other incidental camera side-effect (selection, editing, mode switch, etc. must not move the main camera).
- **Verify:** placing a pin does not move the camera; no action moves the main camera except the explicit focus/zoom set above. Build green.

## Task 11 — C3: orbit-center cue

- While orbiting (rotate drag), **temporarily show the orbit center** so rotation is legible; hide it when idle.
- **Verify:** a center marker appears during rotation and clears afterward. Build green.

## Task 12 — F2: confirm the correspondence pick

- On a successful correspondence commit, give clear confirmation: the **high-contrast marker** (Task 3) plus a **brief confirmation animation**, so it's obvious what was placed.
- **Verify:** committing a pick shows the marker with a short confirmation animation. Build green.

## Task 13 — F3: full meshes visible while placing

- Arming **○ New pin** forces **full-mesh visibility** (pin isolation OFF during placement). (Isolation stays available elsewhere; do not wire it to workflow modes here.)
- **Verify:** arming placement shows full meshes, not isolated pins. Build green.

## Task 14 — G1: project-wide up-normal

- Compute **one average up-normal per project**. If it is significant (terrain-like data), use it as the **global pin/flag orientation**; otherwise fall back to the current per-pin normal.
- **Verify:** on terrain data all flags share the global up orientation; on non-terrain data the per-pin fallback holds. Build green.

---

# P2 — deferrable bonuses

## Task 15 — F4: no isolation in Inspect

- In Inspect, render meshes **as-is** — selecting a mesh **emphasizes** it (and paints its difference) but does **not** hide the others; remove Inspect mesh-isolation.
- **Verify:** selecting a mesh in Inspect no longer hides the rest. Build green.

## Task 16 — H1: exact-point error probing

- Allow picking an **exact 3D point** to read its error value (alongside the automatic samples); ideally **highlight that point in the dock chart**.
- **Verify:** picking a point reports its error and highlights it in the chart. Build green.

---

## Completion checklist (reproduce and fill in)

- [ ] T1 A1 reference never coloured; moving meshes show difference; variance-on-reference removed — DONE file:line / BLOCKED
- [ ] T2 A2 deterministic After-iff-solved; no silent switches; invalidation→Before explicit — DONE / BLOCKED
- [ ] T3 E1/E2/E3 mesh hues disambiguated; thicker outlines + slider; high-contrast marks — DONE / BLOCKED
- [ ] T4 F1 global Esc deselect/cancel incl. edit-point — DONE / BLOCKED
- [ ] T5 B1 3D mesh-picking + Alt+wheel isolate removed; 2D-only selection/visibility — DONE / BLOCKED
- [ ] T6 B2 360° focus view removed (Top only) — DONE / BLOCKED
- [ ] T7 B3+D1 slice mode removed; in-view near-plane slice with thick intersection line — DONE / BLOCKED
- [ ] T8 B4 diff-z toggle removed; M3C2 under the hood — DONE / BLOCKED
- [ ] T9 C1 sensor-position viewpoint action — DONE / BLOCKED
- [ ] T10 C2 camera moves only on explicit focus/zoom; placement recenter removed — DONE / BLOCKED
- [ ] T11 C3 orbit-center cue while rotating — DONE / BLOCKED
- [ ] T12 F2 pick confirmation marker + animation — DONE / BLOCKED
- [ ] T13 F3 full meshes visible while placing — DONE / BLOCKED
- [ ] T14 G1 project up-normal pin/flag orientation (terrain) + per-pin fallback — DONE / BLOCKED
- [ ] T15 F4 no isolation in Inspect (P2) — DONE / BLOCKED
- [ ] T16 H1 exact-point error probe (P2) — DONE / BLOCKED

## Acceptance criteria

- Reference is never error-coloured; Inspect shows moving-mesh differences; before/after is deterministic (After iff solved) with only an explicit invalidation→Before.
- Mesh hues all distinct; outlines thick + slider-tunable; pin/point marks high-contrast; Esc cancels/deselects everywhere.
- No 3D mesh-picking or Alt+wheel isolate; focus is Top-only; standalone slice mode gone, replaced by an in-view near-plane cut with a thick line; no metric toggle.
- Sensor-viewpoint action exists; the main camera moves only on explicit focus/zoom (no placement recenter); orbit center shows while rotating; picks confirm visibly; placement shows full meshes; flags use the project up-normal on terrain.
- P2: Inspect does not isolate; exact-point probing works.
- Auto-seeding and all design-track items are untouched.
- Final report reproduces the completion checklist with per-item status + locations.
