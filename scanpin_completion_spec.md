# ScanPin — Completion Spec (Delta)

**Audience:** Claude Code, working on the current ScanPin codebase.
**Purpose:** Specify only the features that are missing, incomplete, or vestigial. This is a *delta*, not a full spec.
**Reference:** `scanpin_v6_architecture.md` holds the full design rationale and section numbers (cited here as e.g. "arch §D.10"). Where this delta and the architecture doc disagree, **this delta wins** — the codebase has been rewritten and some architecture-doc details (exact type layouts, module names) are stale. Treat everything here as *intended behaviour*, not prescribed structure.

**Working rule:** Where a routine already exists but is disconnected (noted below), wire it up rather than rewriting it. Verify before assuming absence. If a feature's status is genuinely ambiguous, check the running app first, then ask.

---

## Part 1 — Features to complete (UI exists, behaviour missing)

These four have user-facing controls already wired to model state, but nothing downstream consumes that state. The work is to build the consumer.

### 1.1 Fusion mesh rendering and pickability

**Status:** The `Fusion` top-bar toggle flips a model flag. No renderer reads it.

**Intended behaviour.** When fusion mode is on, the 3D viewport renders a single composite surface instead of the individual meshes. At each pixel, the composite shows the source mesh with the **lowest combined error** at that location (combined error = weighted sum of the three provenance sources from 1.2). Only currently-visible meshes participate.

The composite is produced on the GPU in a single pass that outputs two targets: the colour buffer, and a **winner-ID buffer** labelling each pixel with the contributing mesh. The winner-ID buffer makes the composite pickable — a click resolves to the responsible mesh, and an annotation placed on the fusion surface inherits that mesh's identity and its per-location provenance.

No CPU-side rasterisation of a unified mesh. The fusion is a *view*, not a new geometry asset.

**Acceptance criteria.**
1. Toggling fusion mode shows a coherent composite from the visible meshes.
2. Where two meshes overlap, the pixel reflects the lower-error source.
3. Clicking the fusion surface resolves to a specific source mesh.
4. An annotation placed in fusion mode records the resolved source mesh and its provenance.
5. Before any registration has run, fusion mode shows the reference mesh alone with a notice to register first.
6. Interactive frame rate on the primary multi-mesh test dataset.

**Note.** If `BlitShader` was intended as the fusion compositing pass, repurpose it here; otherwise it is dead (see Part 3).

### 1.2 Error provenance — computation wiring and three visualizations

**Status:** A provenance routine that resolves error sources at a location exists but its call site is unreachable; the algorithm-residual map is populated but unread; the heatmap toggle, threshold slider, and falloff-zone toggle all write model state with no consumer.

**Intended behaviour.** Decompose registration error at any surface location into three sources:

- **Dataset error** — per-vertex sensor uncertainty. Use per-vertex metadata when present; fall back to a global figure if the mesh has one; otherwise use a per-sensor default. Provide a per-mesh override so the user can insert or fabricate values where metadata is absent.
- **Algorithm residual** — the registration solve residual at the location, interpolated from nearby correspondence residuals weighted by anchor falloff.
- **Local conditioning** — how well-determined the solve is locally. Use the fast heuristic (local correspondence density × angular diversity) for live updates; the principled condition-number version may be computed on explicit request only.

Wire the existing provenance routine to three visualizations:

1. **Per-pin stacked bar** — in each pin card, absolute (metres) and relative (%) contribution of the three sources at that pin.
2. **Global heatmap** — when the toggle is on, paint visible meshes by *dominant* error source (categorical colour: dataset / algorithm / conditioning). Pixels below the threshold slider value are left unpainted.
3. **Per-point hover readout** — reuse the existing surface-probe gesture; show the three numeric values plus the stacked bar at the probed point.

The **falloff-zone-only** toggle, when on, restricts all global metrics (heatmap and any aggregate readouts) to surface points inside at least one anchor's falloff zone (Gaussian weight above a small threshold).

**Acceptance criteria.**
1. Three sources computed and shown per pin.
2. Global heatmap renders by dominant source and respects the threshold slider.
3. Hover readout works at any probed surface point.
4. Falloff-zone toggle visibly filters the heatmap and aggregate metrics.
5. The dataset-error per-mesh override is reachable from the UI and takes effect.

### 1.3 Active picking layer — make picking respect it

**Status:** Wheel cycling sets the active picking layer and the overlay shows its name, but the only effect is that new pins inherit it as host mesh. Picking is not actually restricted.

**Intended behaviour.** Click, hover, and lasso picking should **prefer the active picking layer**: if the active layer's surface is under the cursor, pick it; otherwise fall back to the frontmost surface under the cursor. The cursor-adjacent overlay already communicates which layer is active. This makes the wheel a genuine disambiguation control in dense overlap, not just a label.

**Acceptance criteria.**
1. With overlapping meshes, cycling the active layer changes which surface a click lands on.
2. Where the active layer is absent under the cursor, picking falls back to the frontmost surface rather than failing.
3. Hover highlighting follows the same rule.

### 1.4 Registration convergence feedback

**Status:** The convergence chart binds to a log and a residuals field, but no mid-run progress is ever emitted; only the final residuals arrive. The progress message type exists but has no emitter and its handler is a no-op.

**Decision required, then implement one path:**

- **Path A (preferred if cheap):** Emit per-iteration progress from the solve so the chart fills in as the registration converges. Remove the no-op handler in favour of a real one.
- **Path B (fallback):** Drop the "convergence" framing. Replace the chart with a final-state residuals display (histogram of per-correspondence residuals + overall RMS). Remove the unused progress message type and its dead handler.

Pick Path A if the solver can report iterations without significant restructuring; otherwise Path B. Either way, **no orphaned progress plumbing should remain.**

**Acceptance criteria.**
1. Either the chart updates during a solve (A) or it cleanly shows final residuals only (B).
2. No unreferenced progress message type or no-op handler remains.

---

## Part 2 — Features to implement (no evidence of a start; verify, then build)

None of these appear in the audit. Confirm they are genuinely absent before building.

### 2.1 Workspace persistence

**Status:** README confirms none exists.

**Intended behaviour.** Serialise the full working state — meshes (by dataset + id reference, not geometry), anchors, correspondences, panoramas, registration transforms, and explore/clip state — to a JSON blob, and restore it in a fresh session. Anchor positions persist in world space with a host-mesh reference for re-attachment.

UI: a Save action producing a downloadable file, and a Load action via file picker.

**Edge cases.** Loaded workspace references an unavailable dataset → warn and offer to switch. Loaded anchor's host mesh is gone → mark the anchor as orphaned; let the user delete or re-link.

**Acceptance criteria.**
1. Save produces valid JSON.
2. Load restores anchors, correspondences, panoramas, registration transforms, and explore/clip state.
3. Save→load round-trip is idempotent.

### 2.2 Retarget workflow

**Status:** No evidence of implementation. The `closestPoint` server query is currently orphaned but is the foundation for this feature — **keep it.**

**Intended behaviour.** When a new mesh of the same site is loaded into an existing workspace, offer to retarget. If accepted: project each existing correspondence anchor onto the new mesh (nearest-point first; fall back to normal-aligned matching when the nearest-point distance exceeds roughly twice the anchor radius), form tentative correspondences, run a registration solve, then walk the user through a validation pass (accept / adjust / reject each tentative anchor) before a final solve. Record a "retargeted-from" link on each new anchor for provenance.

**Edge cases.** Many projections fail → warn that overlap may be insufficient. Large scale mismatch → confirm before proceeding. All anchors rejected → fall back to a clean registration.

**Acceptance criteria.**
1. Loading a new mesh into an existing workspace offers retarget.
2. Tentative projected anchors appear on the new mesh.
3. The validation pass steps through each anchor.
4. The final solve produces a sensible transform.

### 2.3 Panorama split view and synthetic generation

**Status:** Not mentioned anywhere in the audit. **Verify first** — this is a substantial feature and its complete absence vs. silent completion changes the work enormously.

**Intended behaviour (if absent).** A docked panel beside the 3D viewport, shown when the dataset has registered panoramas. Modes: **Photo** (the image), **Render** (live render of the visible meshes from the panorama's exact camera pose), **Blend** (slider mix of the two — a photo-vs-mesh disagreement detector). Anchor markers projected into panorama space, toggleable. Clicking the panorama raycasts through the camera pose into the meshes to place an anchor. A button to fly the 3D camera to the panorama's pose.

For datasets without real panoramas (the Mars data), generate **synthetic panoramas** by rendering from a few virtual cameras around the dataset with wide-FOV cylindrical projection, stored with their poses, so the interaction works end-to-end before real imagery arrives.

**If panorama click-to-place uses a server-side ray query,** the orphaned `rayHit` / `rayBatch` wrappers may be the right foundation — verify before removing them (see Part 3).

**Acceptance criteria.**
1. Panel appears for datasets with (real or synthetic) panoramas.
2. Photo / Render / Blend modes are distinct; Blend reveals photo-vs-mesh disagreement.
3. Clicking the panorama places an anchor at the correct 3D location.
4. The fly-to-pose button aligns the 3D camera with the panorama.
5. Synthetic panoramas are produced for the Mars datasets on load.

---

## Part 3 — Removals (vestigial after the V6 migration)

Delete the following. Each is a leftover from a V5 concept that V6 dropped.

- **Pin cut-plane cross-section diagram.** The cut plane was removed in V6 (replaced by the line-on-surface payload). The cross-section in the pin card has no data source and renders empty scaffolding. Remove it.
- **Orphaned server queries that map to removed concepts:**
  - plane-intersection and plane-intersection-batch → cut plane removed.
  - ray-grid → V5 "Auto" placement removed.
  - cylinder-eval → cylinder primitive removed.
- **RankingState module.** Leftover from V5 ranking / top-K aggregation; the summary-mesh concept it served was removed in V6.
- **BspTree placeholder module** — empty body. Remove unless something depends on it after the query cleanup above; verify.
- **BlitShader** — comments only. If not repurposed for the fusion compositing pass (1.1), remove.
- **Arcball-gizmo TODO for pin axis.** V6 anchor spheres are isotropic; there is no axis to manipulate. Drop the TODO and any associated dead UI.
- **Top-view core-sample inspector references.** The core-sample inspector was removed in V6; the type no longer exists. Remove any remaining references in docs or dead UI.

**Keep despite appearing orphaned (needed by Part 2):**
- **closest-point query** — required for retarget (2.2).
- **ray-hit / ray-batch queries** — verify against panorama click-to-place (2.3) before removing; remove only if panorama picking is fully client-side.

---

## Part 4 — Verification checklist (spec required it; audit is silent)

These V6 features are neither flagged as broken nor confirmed working by the audit. Before declaring the system complete, verify each behaves as intended; if any is incomplete, treat it as additional work and flag it.

- **Dual-signal Explore mode** (arch §D.4): independent feature-confidence and disagreement signals, separately toggleable, with mix modes. Confirm both signals exist and the curvature-based feature-confidence channel is real (it shares curvature with the next item).
- **Ghost silhouette enhancement** (arch §D.2): outline / +curvature / +terrain-feature modes.
- **Polygonal lasso region restriction** (arch §D.3): alongside the rectangular clip box, for second-pass registration scoping.
- **Patch payload 2D↔3D linkage** (arch §D.12): shared compass rose, coloured frame echoed in both views, bidirectional hover, source-mesh switcher.
- **Line-on-surface payload** (arch §D.7.2): both isoline and curvature-ridge sub-modes, cross-mesh tracing, and the unrolled arc-length plot in the card. (Server calls confirm the core exists; confirm the card and cross-mesh comparison are complete.)

---

## Suggested order of work

1. **Part 3 removals first** — clears dead state and reduces confusion before building.
2. **1.2 error provenance** — it is the input to 1.1; build it first.
3. **1.1 fusion mesh** — depends on 1.2.
4. **1.3 active picking restriction** and **1.4 convergence feedback** — small, independent.
5. **2.1 persistence** — independent, can slot anywhere.
6. **2.2 retarget** — depends on a working registration solve (present).
7. **2.3 panorama** — verify first; largest unknown.
8. **Part 4 verification pass** — last, before declaring complete.
