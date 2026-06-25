# ScanPin v3 — Coding Spec Addendum: Pin Inspector

Rebuild of the per-pin detail surface. Supersedes §8's flyout/violin and the original §7 pin card. Imperative.

**Two hard rules for this task:**
1. **Implement every section below. Do not skip, defer, or partially implement any part.** If a section is blocked, stop and report the blocker — do not silently omit it.
2. **Prune aggressively.** Every symbol, message, model field, shader path, JS helper, and file made unreachable by the removals below must be deleted in the same change. No dead code, no "left intentionally."

---

## A. REMOVE (entirely, with all backing code)

- The pin detail **flyout / pin card** and its open-on-select path: `CardsPin.fs` (3D-anchored card), its drag/position state, and every message/reducer that serves it.
- **All violin / split-violin** rendering: the violin and split-violin functions in `CardCharts.fs` (and the file if it becomes empty), the before/after split-violin, and any `*Js` chart helpers feeding them.
- The **object-centered orthographic correspondence detail view** (the detail SVG / contour+ridge symbolic mesh / on-screen ruler overlay) — remove completely, not deferred.
- The **linked-view highlight thread** wiring that targeted the violin/detail-SVG (keep only the pin-hover highlight on the 3D glyph).
- Any model state that existed solely for the above (card position, detail-SVG state, violin selection/brush state, linked-thread state).

After removal, the only per-pin surfaces remaining are the 3D glyph (far/triage, unchanged) and the new bottom-dock inspector (below). Confirm `CardsPin.fs` has no remaining callers before deleting.

## B. BUILD — bottom-dock pin inspector

New file `GuiInspector.fs`. A **2D dock** (canvas/SVG, **not** a WebGL control — the ≤2-control budget is untouched). Full viewport width, fixed height (~220px), docked **below the main 3D viewport**. Always mounted. Persistent; not a popover, never overlaps the 3D scene.

- Reads **only the currently selected pin** (`Selection.pinId`). On selection change, repopulates.
- **Empty state** (no pin selected): a neutral, faint centered placeholder affordance. No data, no interpretive text.
- **No prose anywhere in the dock.** No diagnosis, no statements, no sentences. Only numbers, units, marks, glyphs, and terse field identifiers (e.g. `r`, `k/n`, `mm`). This is a hard requirement across every sub-panel.

Horizontal layout, left → right:

### B1. Identity block (fixed left, ~180px)
- Pin name (inline-editable, reuse the placement-flyout name control).
- **ROI radius** (inline log-slider, reuse the placement-flyout radius control → existing radius message).
- **Correspondence `k/n`** (k = moving meshes with a placed correspondence inside the ROI, n = visible moving meshes).
- `⚲` promote/demote correspondence (existing message), `✕` delete pin (existing message).

### B2. Raincloud panel (center, flex-grow — the core)
One **row per visible moving mesh**, stacked, on a **single shared horizontal signed-distance axis**.

- **Shared scale:** one zero-centered axis for all rows. Domain = symmetric about 0, bounded by the robust 1st–99th percentile across *all* rows' before+after samples. Same domain applied to every row. Do not autoscale per row.
- **LoD band:** shaded band around zero, per row, half-width = `1.96·√(σ_ref²/n_ref + σ_M²/n_M)` for that moving mesh.
- **After** (current preview pose if a preview is active, else committed pose): plotted **above** the row line as
  - raw points ("rain") — always; subsample to ≤300 plotted points for legibility but compute stats on the full set,
  - median + IQR box — always,
  - a half-violin (one-sided KDE) curve — **only when N ≥ 20**; below 20, omit the curve (rain + box only). KDE truncated to data range.
- **Before** (committed pose): a **single before-median tick** below the row line, on the same axis. (No before cloud.)
- Row tinted with the mesh's **palette colour**; before tick desaturated.
- Rows with no correspondence / no probe samples: render greyed, axis only, no cloud.

Data source: reuse the existing per-pin probe pipeline that previously fed the violin (per-pin → per-moving-mesh signed-distance sample arrays, before = committed, after = preview). If that pipeline was entangled with removed code, re-expose a clean accessor `pin → meshId → { before: float[]; after: float[]; sigmaRef; sigmaM; nRef; nM }` and populate it from the same probe postlude. Add no new server round-trip if the samples already exist client-side.

### B3. Correspondence readout (right, ~200px)
Per visible moving mesh, one compact line: palette swatch · placed `✓`/`✗` · post-registration **residual at the correspondence point** in `mm` (distance from the moving correspondence point to its reference target after the current pose). Numbers only.

### B4. Intrinsic context (far right, ~120px)
Three small horizontal bars — **incidence**, **range**, **shape** — each = the ROI-averaged intrinsic quality (0–1) for the selected moving mesh (or the selected/topmost row), coloured by the same red→green ramp as the §6 heatmaps. Identifier letters only (`I`/`R`/`S`); no statement labels.

## C. Model / message changes
- Reuse existing selected-pin state; the dock is a view over it. Add dock-local state only if required (e.g. which moving-mesh row is active for B4).
- Add the clean per-pin distribution accessor/cache from B2 if not already present (e.g. `InspectorDistributions`), populated by the existing probe-computed message — do not add a new message if one already fires on probe completion.
- Delete model/messages orphaned by section A in the same change; run `adaptify.sh` after editing any `[<ModelType>]` file; never hand-edit `*.g.fs`.

## D. Acceptance criteria
- `CardsPin.fs`, the violin/split-violin code, the object-centered detail view, and all their backing state/messages are **gone**; build is green with no unreferenced symbols introduced or left by this change.
- A full-width 2D bottom dock exists, always mounted, never overlapping the 3D scene; neutral empty state when no pin is selected.
- Selecting a pin populates: identity block (editable name + radius, `k/n`, promote/delete), raincloud (per-moving-mesh, shared zero-centered scale, LoD band, after = rain + box always + half-violin only at N≥20, before = median tick), correspondence readout (numbers only), three intrinsic bars.
- **No sentences/statements anywhere in the dock.**
- ≤2 live WebGL controls unchanged (the dock is 2D).
- Every section A–C implemented; no part skipped or deferred.
