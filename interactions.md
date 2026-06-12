# Superprojekt — Interactions & Workflows

A walkthrough of everything a user can do in the app, how the individual
interactions chain into larger workflows, and what goes in and out of each
step. The app is a research prototype for comparing multiple 3D surface
reconstructions (meshes from different sensors/pipelines) of the same scene:
inspect them, quantify their disagreement, align them, and fuse them.

Architecture in one sentence: a thin Blazor-WASM client (Elm-style
Model → Update → View, WebGL2 rendering) talks to an ASP.NET/Giraffe server
that owns the heavy geometry (Embree BVH raycasts, closest-point, M3C2
probes, isolines, ridges, sphere contact rings, weighted landmark solves,
ICP) at `http://localhost:5000`.

---

## 1. The big picture — a typical session

The individual interactions below compose into one overarching analysis loop:

```
load dataset ──► navigate / inspect meshes ──► declare per-mesh error metadata
     │                                                  │
     ▼                                                  ▼
designate a ★ reference mesh ◄──────────────── provenance heatmap shows
     │  (all error metrics are relative            where error budgets are
     ▼   to it — no absolute ground truth)         exceeded
place ScanPins on regions of interest
     │  (probe quantifies per-mesh disagreement at each pin)
     ▼
promote pins to correspondence landmarks ──► auto-seeded anchors, refined in
     │                                        patches / 3D / violin picks
     ▼
Stage 1 · coarse landmark solve ──► Stage 2 · fine ICP (traditional or
     │                                region-restricted by the pins)
     ▼
PENDING PREVIEW — split violins, diff heatmap, committed ghost, RMS table
     │
     ├── ✕ discard (nothing changes)
     ▼
✓ commit ──► transforms apply + history step ──► probes & rings recompute
     │        (↩ rollback / ↺ reset available)     automatically
     ▼
fusion mode composites the registered ensemble per-pixel by lowest error
     │
     ▼
save workspace (JSON download) — pins, anchors, transforms, history survive
```

Key feedback loops:

- **Pins → registration → pins.** A pin's probe shows how far apart the
  meshes are; its correspondence anchors feed the coarse solve; committing a
  solve moves the meshes, which invalidates all probes and contact rings;
  they recompute lazily and show the improvement.
- **Preview before commit.** Every solve (coarse or fine) lands in a pending
  preview first: the meshes render at the previewed pose, the charts and the
  diff heatmap quantify the change, and only an explicit commit makes it
  permanent (and roll-backable). Discard costs nothing.
- **Error metadata → probe / provenance / fusion.** The per-mesh dataset
  error (sensor type or manual override) feeds the probe's three-source
  error decomposition, the provenance heatmap threshold test, the diff
  mode's detection limit, and the fusion pass's per-pixel "lowest combined
  error wins" depth.
- **Lasso / isolate-pins / solo** are purely visual focus tools — they never
  change what is computed, only what is shown (the lasso explicitly does
  *not* affect registration).

---

## 2. Startup & dataset loading

**Input:** none (open `http://localhost:5000`). **Output:** rendered scene.

Boot sequence (automatic):

1. `GET /api/datasets` → dataset list; `GET /api/datasets/default` → which
   one to open first.
2. For the active dataset, in parallel: centroids (`/centroids`), bounding
   boxes (`/bboxes`), then per-mesh binary geometry + JPEG texture atlases.
   The server warms its Embree/BVH caches during the bbox call, so the first
   interactive query is fast.
3. When scene bounds arrive, one synthetic **panorama pose** is generated at
   the scene-bbox centre (+2 m up). A loader overlay covers the screen until
   all meshes are in.

**Switching datasets** (top-bar dropdown): resets everything scene-specific —
pins, lasso, chart-link state, solo, active picking layer, panoramas, open
cards. View settings (rendering mode, ghost settings, camera speed,
registration mode) persist across the switch.

---

## 3. Navigating the 3D viewport

All canvas input, with exact bindings:

| Input | Effect |
|---|---|
| Left-drag | Orbit (azimuth + elevation; pitch clamped just short of the poles) |
| Middle-drag | Pan the orbit centre in screen space; on release the camera re-anchors to the surface point under the screen centre (depth-gated) |
| Wheel | Zoom along the view direction (smoothly animated, ~120 ms) — always; never hijacked by layer cycling |
| **Option/Alt + wheel** | **Layer isolation mode**: cycles the **active picking layer** through the meshes under the cursor (over all visible meshes when fewer than two are stacked there). While the key is held the chosen mesh renders solid and every other mesh fades to a fixed ghost; an overlay label near the cursor names the current layer. The selection persists after release and keeps steering picks |
| Double-click on a surface | Fly the orbit centre to the clicked point (350 ms animation). Background double-clicks are ignored (depth-gated) |
| Two-finger touch | Pinch-zoom + rotate (mobile) |
| Right-click | Suppressed (no context menu) |
| **Ctrl + click** | Transient hover probe (§8.6) |
| Click | Mode-dependent: anchor pick / add lasso vertex / place pin / select; otherwise idle |
| **Esc** | Clears, in priority order: 3D anchor pick → hover probe → lasso drawing → pin placement |
| **Hold Space** | Temporary fullscreen view (panels hidden, dataset name + mesh legend overlaid); release to return |
| ⟲ top-bar button | Reset camera to the default framing |

Always-on viewport outputs:

- **Coordinate readout** (top bar, `⌖ x, y, z`): absolute world coordinates
  of the surface point under the cursor; `—` over background.
- **Scale bar** (bottom-left): metric, auto-ranging (cm/m/km), correct for
  dataset scaling and zoom.
- **Orientation indicator**: small X/Y/Z axis cross that tracks the camera.

**Picking is depth-gated everywhere.** A click on the sky / through a ghosted
surface hits nothing — ghost fragments deliberately write far-plane depth, so
picks pass through translucent silhouettes to the opaque surface behind, and
background misses can never create pins or fly the camera to infinity.

**Active picking layer.** The Option/Alt-wheel-selected layer biases picks to
one mesh when several overlap; it is also the prerequisite/target for the
**retarget** workflow (§10), the preferred reference for the hover probe, and
while a registration **one-shot anchor pick** is live, Option/Alt + wheel
retargets the pick to the isolated mesh (the reference is skipped).

---

## 4. Mesh management (left panel)

Open with the top-bar hamburger (☰). Per-mesh row: colour swatch (the mesh's
categorical palette colour, used consistently in charts, rings, and cards),
shortened name, and four buttons:

- **★/☆ reference** — designate this mesh as the registration reference
  (single selection, two-way bound to the registration card's selector;
  tooltip: all error metrics are relative to it, there is no absolute ground
  truth). The reference shows a subtle accent bbox outline in 3D and a ★ in
  the fullscreen legend. Changing it invalidates all probes, discards any
  pending solve preview, and re-seeds the correspondence anchors.
- **●/○ visible** — toggle the mesh. Hidden meshes render as a uniform ghost
  silhouette (if the silhouette is enabled) instead of disappearing.
- **◐ solo** — isolate this mesh (saves and restores the previous visibility
  set when toggled off).
- **⌖ focus** — fly the camera to frame this mesh.

Above the list: **All** / **None** bulk visibility, and the **rendering
mode** selector — *Textured* (atlas photos), *Shaded* (geometry-only), *Slope*
(colour by surface slope vs. the slope-threshold setting).

Visibility matters semantically, not just visually: probes sample only
visible meshes, registration solves only visible meshes, and fusion
composites only visible meshes. (Contact rings are the exception — computed
for all meshes, with visibility gating only their display.)

### Appearance settings (gear popover ⚙, top-right)

Ghost silhouette on/off, ghost opacity (default 0.12), **Isolate pins**
(ghost everything outside pin falloff regions — turns pins into spatial
spotlights), shading strength, slope threshold, camera speed; plus dataset
info, per-mesh centroids, and the debug log. The gear popover also hosts
workspace save/load (§12) and the retarget trigger (§10).

---

## 5. Error metadata & provenance heatmap (left panel → Visualization)

**Input:** per-mesh sensor knowledge. **Output:** an error budget that feeds
probes, the heatmap, and fusion.

- **Error metadata**: per mesh, pick a sensor type (Rover / Sat / Photo /
  LiDAR — each with a default accuracy) or override the dataset error with a
  log slider (sub-mm to 10 m); ↺ reverts to the sensor default.
- **Error provenance**: a three-way radio — **Off / Sources / Diff**.
  *Sources* paints fragments whose combined error exceeds the *Threshold*
  slider, colour-coded by dominant source — blue = dataset (sensor), orange
  = algorithm (registration residual), purple = conditioning (local
  geometry). *Diff* is only available while a registration preview is
  pending (§9): it paints the per-fragment **signed change of combined
  error** (preview − committed) on a diverging map — blue = improved, red =
  degraded — and drops everything below the detection limit
  `1.96·√(σ_ref² + σ_M²)` to ghost level, so only statistically meaningful
  change stands out; it auto-reverts to the previous mode on commit/discard.
  *Falloff zones only* restricts the Sources painting to pin falloff
  regions. While a heatmap is on, hovering a surface shows a tooltip — the
  D/A/C breakdown in Sources mode, `Δ / LoD / verdict` in Diff mode.

The algorithm component is populated by running a registration (per-mesh
RMS); conditioning comes from probe results. So the heatmap becomes more
informative as the session progresses.

---

## 6. Lasso (visual clipping polygon)

**Input:** a screen-space polygon. **Output:** a 3D clip volume that ghosts
everything outside it. Purely visual — never affects queries or registration.

1. Top bar **◌ Lasso** → drawing mode (crosshair cursor). Each click adds a
   vertex; an SVG overlay shows the polygon and a dashed segment to the
   cursor.
2. **Double-click commits**: the polygon is extruded through the scene into
   outward-facing half-space planes (up to 32) evaluated per-fragment in the
   mesh shader. Esc / ⊘ cancels.
3. A small draggable **Lasso card** then controls it: **◉/○** enable/disable
   the filter while keeping the polygon, **✎** redraw, **✕** clear.

The lasso composes **conjunctively** with pin isolation: with both active,
only fragments inside the lasso *and* inside a pin's falloff stay opaque.

---

## 7. ScanPins — placement

A ScanPin is the app's central annotation: a metric world-space sphere
(`Centre`, hard-core `InnerRadius`, decaying `FalloffRadius`) that drives
shader isolation, a measurement payload, the M3C2 probe, contact rings, and
registration anchoring.

**Workflow:** top bar **○ Pin** → placement mode (crosshair; a ghost-sphere
preview follows the surface under the cursor) → click a surface to place →
the pin enters the **Adjust Anchor** flyout → **✓ Commit** or **✕ Discard**
(Esc also cancels). Placement clicks are depth-gated; clicking sky does
nothing. Under fusion mode the placement pick is a server-side raycast (it
lands on the per-pixel winning surface, matching what you see). You can also
place pins by clicking inside the panorama view (§11).

**Adjust flyout controls:**

| Control | Range / behaviour |
|---|---|
| Inner radius | log slider 0.01–10 000 m; the hard-truth core (full opacity & probe weight). Changing it preserves the falloff *delta* |
| Falloff + | log slider, *relative*: `FalloffRadius = InnerRadius + delta`. Drawn as a white sphere outline only while adjusting |
| Payload | Point / Line / Patch (§8.1) |
| Reliability (Point) | 0–1, the pin's weight multiplier as a registration anchor |
| Cyl. length (Point) | probe cylinder length, log slider 1–100 m, or **auto** (server-estimated) |
| Line mode (Line) | Elevation isoline (with elevation slider) or curvature Ridge |

Changing radius/payload/length immediately invalidates the pin's probe and
contact rings; both recompute lazily (§8.4).

**Pin list** (left panel): one row per pin — click the coordinates to
select/deselect (opens/closes its card), **⌖** focus (flies the camera back
to the view saved at creation), **✎** edit (reopens the adjust flyout for a
committed pin), **✕** delete.

**3D feedback per pin:** a small clickable marker dot at the centre; a thin
**equator ring** (radius = InnerRadius, ⊥ the pin's probe axis) plus the
cached **contact rings** — the sphere∩mesh intersection curves on every
*visible* mesh — all in the pin's host-mesh palette colour. Unselected:
α 0.6 / 1.5 px; selected: α 1.0 / 2.5 px. Rings are normally depth-tested,
so occlusion reads as the spatial cue.

---

## 8. ScanPins — measurement & analysis

### 8.1 Payloads

- **Point** (default): hosts the M3C2 probe (below) and a reliability weight
  for registration anchoring.
- **Line**: traces a polyline on the host mesh — either an **elevation
  isoline** (slider-driven height) or a **curvature ridge** — plus
  cross-mesh traces of the same feature on every other visible mesh. Server
  queries `/query/isoline` / `/query/curvature-ridge`, debounced 250 ms. The
  card plots arc-length × scalar per mesh; the 3D scene draws the polylines.
- **Patch**: server fits a local tangent plane (`/query/patch`) and returns
  a neighbour sample projected into it; the card shows the orthographic
  footprint with a compass-north arrow, the scene shows the footprint ring.

### 8.2 Pin cards

Selecting a pin opens its floating **card**, anchored in 3D (projected to
screen, following the pin as the camera moves). Card chrome (shared by all
draggable cards): drag handle to move (dragging detaches it to a fixed
screen position; 📌 re-docks), collapse toggle, ✕ close (deselects), click
brings to front.

Card body (Point payload): centre / radii readouts, planarity badge
(planar ✓ / not planar ⚠ from the probe's PCA), the **violin chart** (8.3),
y-range presets (auto / ±0.5 / ±2 / ±10 / fit), lock-order toggle, the
**three-source stacked error bar** (blue dataset / orange algorithm / purple
conditioning) with numeric breakdown, probe-length override, the
reliability slider, and the **correspondence section** (§9.2): a "use as
registration landmark" toggle, the reference-anchor status (⚠ when the
projection is > 2× the falloff radius), one row per other mesh (anchor
source + accepted state + last coarse-solve residual + ⊕ pick-in-3D), and
the ▦ patch-picker entry point.

### 8.3 The M3C2 probe & violin chart

**Input:** pin sphere + visible meshes + their transforms. **Output:** a
per-mesh signed-distance distribution along a common axis.

The server (`POST /api/query/probe`, one batched round-trip) samples every
visible mesh inside a cylinder (radius = InnerRadius, axis = PCA normal of
the reference mesh inside the sphere, length = auto or override) and returns
per-mesh distributions — median, IQR, std, KDE — re-centred so 0 = the
reference mesh's median, plus the dataset/algorithm/conditioning error
decomposition.

The chart is **vertical**: signed distance on the y-axis (positive up,
0 = reference median), one column per mesh in mesh colours, with median
tick, IQR whisker, KDE violin, and an `n=…` count badge.

While a registration preview is pending (§9), each column **splits into
paired half-violins** — committed pose on the left (desaturated), previewed
pose on the right (full colour) — with split median ticks/whiskers and an
arrow from the old to the new median labelled with the Δ. The single-violin
layout returns on commit/discard.

Computation is **lazy and debounced**: nothing runs until the pin's card is
open; any invalidation (radius, centre, payload, length, reference change,
registration transforms, mesh visibility) just resets the state, and a
single 250 ms-debounced query relaunches. Stale responses are dropped.

### 8.4 Chart ↔ 3D linking (accent `#0891b2`)

The chart and the 3D scene cursor-link in both directions:

- **Chart → 3D:** hovering the plot at signed distance *d* renders a
  translucent **elevation-cursor disk** orthogonal to the probe axis at
  `centre + d·axis` (radius = InnerRadius; **Alt** extends it scene-wide),
  and the mesh shader draws a **contact-line band**: meshes intersected by
  that plane darken slightly (×0.85) with a bright smoothstep band within
  ±0.2 m of the plane, clipped to the probe cylinder (unclipped when
  Alt-extended). The band colour exactly matches the disk colour.
- **3D → chart:** hovering the 3D surface inside the pin's probe cylinder
  moves a live cursor line on the chart at the hover point's signed
  distance (and shows the same contact-line band in 3D).
- **Column hover:** hovering a mesh's column ghosts all other meshes
  (α 0.2) so you can see which surface is which; **clicking** a column makes
  the highlight **sticky** (thick border; click again, click another column,
  or click outside the chart to release).

### 8.5 Contact rings

Each pin caches its sphere–surface intersection curves for **all** meshes
(`POST /api/query/contact-rings` fan-out, 250 ms debounced, one CTS per pin
so several pins can recompute concurrently after a registration). Mesh
visibility only gates *rendering* — toggling meshes never recomputes.
Invalidated by radius change, retarget move, registration complete/reset.
Registration transforms are rigid, so the client inverse-transforms the
sphere centre into each mesh's frame and maps the returned rings back.

### 8.6 Hover probe (Ctrl + click)

A throwaway version of the pin probe for quick checks: **Ctrl-click** any
surface → a compact tooltip with the mini violin chart appears at the
cursor (radius = 5 % of the scene diagonal, auto length, reference =
declared reference mesh, else active picking layer, else first visible
mesh). One global slot: the next Ctrl-click supersedes it; Esc or a plain
click dismisses it; it auto-clears 8 s after the result arrives.

---

## 9. Ensemble registration (two-stage, preview-first)

**Input:** a ★ reference mesh + visible moving meshes (+ pins as landmarks
and/or region anchors). **Output:** rigid per-mesh transforms, per-mesh RMS,
and a roll-backable history of committed steps.

Opened with the floating **⚙ Registration** toggle button. The card, top to
bottom: **★ Reference** (mirrors the mesh panel's ★) → **Stage 1 · Coarse**
→ **Stage 2 · Fine** → **Pending result** (only while previewing) →
**History**.

### 9.1 The pending preview (shared by both stages)

Every solve lands in an **uncommitted preview** first — nothing is permanent
until you commit:

- Moving meshes render at the previewed pose; their **committed pose stays
  visible as a slate-tinted ghost** underneath (picks pass through it).
- A banner reads *"Previewing unregistered result — commit or discard"*.
- The card shows a per-mesh table — `RMS before → after (Δ%)`, a unicode
  convergence sparkline for ICP steps, an amber **⚠ collinear** badge when
  the landmark geometry under-constrains rotation — plus any unsolved
  meshes.
- Open pin cards get a second probe under the preview transforms → **split
  half-violins** (§8.3); contact rings and the hover probe follow the
  preview pose; the heatmap's **Diff** mode (§5) becomes available.
- **Blocked while previewing** (greyed with explanatory tooltips, and
  rejected by the reducer with a toast): pin placement, retarget, fusion
  toggle, dataset switch, and all anchor picking (anchors are
  committed-pose points — picking one at a preview pose would
  double-transform on commit).
- **✓ Commit** applies the transforms, appends a history step (before/after
  transforms + RMS), re-bases all correspondence anchors by the applied
  world delta, and fires the full invalidation cascade (all probes + rings
  + algorithm RMS). **✕ Discard** drops the preview; probes stay valid,
  rings recompute back. Starting a new solve replaces the current preview.

### 9.2 Stage 1 · Coarse (landmark solve)

**Input:** correspondence anchors on Point pins. **Output:** one rigid
delta per moving mesh with per-pair residuals + conditioning diagnostics.

**Correspondence anchors.** In a pin's card, toggle *"use as registration
landmark"*. This computes the pin's **reference anchor** (its centre if it
sits on the reference mesh, else the closest-point projection onto the
reference — flagged ⚠ when the projection is > 2× the falloff radius) and
**auto-seeds one anchor per other loaded mesh** (closest point to the
reference anchor, parallel `/query/closest`). A review modal — one row per
pin × mesh with the projection distance Δ, red-flagged when Δ > 2× falloff
or no projection exists — lets you accept ✓ / reject ✕ each; *Apply* marks
the accepted ones usable. Rejected anchors stay unaccepted and can be set
later by hand:

- **▦ Pick in patches** — the patch small-multiples picker. The reference
  patch is sampled first and its tangent frame becomes the shared frame for
  every visible mesh (`/query/patch` with the frame override), so all
  patches are co-oriented and directly comparable. The card shows one
  orthographic footprint per mesh — reference first with a distinct border,
  mesh-colour header swatch, **atlas-textured points** (toggle to a
  height-colour rendering), and a crosshair at the reference anchor's (u,v)
  in every patch. Clicking inside a moving mesh's patch shoots a ray down
  the shared normal against that mesh and sets the anchor (`patch` source);
  a miss shows a toast.
- **⊕ Pick in 3D** (per anchor row) — a one-shot mode: the target mesh
  renders solid (forced visible), the reference at ≈30 % opacity for
  context, everything else ghosted; crosshair cursor; **one depth-gated
  click** on the target surface sets the anchor (`3D` source), then the
  mode auto-advances to the next mesh with an unaccepted anchor for the
  same pin. **Esc** cancels.
- **Shift+click a violin column** — sets that mesh's anchor at
  `refAnchor + d·probeAxis` for the clicked signed distance d (`violin`
  source). Available when the pin's correspondence is enabled.

Accepted anchors render in 3D as small wireframe **tetrahedron glyphs** in
the mesh's palette colour, each connected by a thin line to the pin's
reference anchor (brighter for the selected pin); they follow the preview
pose while a solve is pending. Auto-seeding **re-runs** (only overwriting
auto/unaccepted anchors — manual picks survive) when the reference changes
or a pin is retargeted.

**Solving.** The card's coarse section shows a readiness line (enabled
pins; accepted pairs per visible moving mesh), a client-side conditioning
badge (λ2/λ1 of the accepted reference-anchor spread: ok / weak /
collinear), and the enabled-pin list with a one-click **⊘ exclude** per
pin. **▶ Solve coarse** (enabled when ≥1 visible moving mesh has ≥3
accepted pairs; disabled states explain themselves in the tooltip) POSTs
`/query/lsq-pairs` per qualifying mesh in parallel — a weighted rigid
Umeyama/Arun solve (weights = pin reliability) that returns the delta,
per-pair residuals (shown in each pin's correspondence rows), and a
collinearity warning. Meshes with <3 pairs are listed as unsolved. Hidden
meshes are never solved, but their anchors persist.

### 9.3 Stage 2 · Fine (ICP)

Unchanged math: *Traditional ICP* (uniform weights) or *Region-restricted*
(each committed pin becomes a Gaussian anchor: centre, sigma =
FalloffRadius, multiplier = reliability). `POST /api/query/icp` per visible
moving mesh in parallel (Gauss-Newton point-to-surface, Embree
closest-point correspondences, trimmed at 3× the median pair distance),
starting from the **committed** transforms — so the intended flow is
commit-coarse-then-fine, and a one-time dismissible warning appears if no
coarse step has been committed yet. Results land in the same pending
preview as deltas relative to the committed pose.

### 9.4 History, rollback, reset

Committed steps are listed newest-first: `#n stage mode · RMS a→b`. Only
the **newest** step can be rolled back (**↩** — restores the recorded
before-transforms and algorithm residuals, un-bases the anchors, fires the
invalidation cascade); older rows show the button disabled. **↺ Reset**
rolls back every step to identity transforms + an empty history. Steps
record which reference mesh they were solved against; changing the
reference later never rewrites history. The lasso has no effect on
registration.

### 9.5 The workflow panel (⚲ Workflow, top bar)

A floating panel that puts the whole registration state in one place — a
**pure view over the model**: every toggle dispatches the existing message
(★, eye, correspondence enable are two-way synced with the mesh panel,
registration card and pin cards), and it never issues server queries. Four
collapsible sections:

- **Meshes** — per mesh: colour swatch, ★, eye, a **status chip**
  (`Reference` / `Fine ✓` / `Coarse ✓` / `Skipped` = the last solve ran but
  this mesh lacked 3 accepted pairs / `Unregistered` / `Hidden`), an amber
  badge when the last solve flagged near-collinear anchors, the last
  solve's RMS-after, and ⌖ **fly-to** (frames the mesh at ~25 % of the
  viewport height, orientation kept; any input cancels the animation).
- **Correspondence pins** — per enabled pin: host mesh, an **anchor-dot
  matrix** (one dot per visible moving mesh in its colour: filled =
  accepted, hollow = seeded, red ring = missing; tooltip names the mesh),
  accepted `n/M`, reliability, the worst per-mesh residual of the last
  coarse solve, exclude + open-card buttons. Clicking the row selects the
  pin and flies to its sphere. A collapsed **Other pins** footer enables
  correspondence on any committed Point pin (triggers the normal
  auto-seed + review flow). The header aggregates accepted pairs per mesh.
- **Registration status** — the pending banner (per-mesh RMS before→after
  + Commit/Discard), the **diagnostics list** from the shared readiness
  engine (blockers red, warnings amber, then one green
  "Ready for coarse solve / fine ICP" per stage whose ▶ runs the solve;
  every entry carries a navigation action: open the anchor review filtered
  to the deficient mesh, open the pin card at its correspondence section,
  highlight the ★ column, focus the registration card — targets get a
  1.5 s pulse outline), and a history one-liner.
- **Error stats** — per moving mesh the last committed RMS before→after,
  Δ%, stage reached and an RMS sparkline across committed steps, plus a
  mean/max + solved-`s/M` aggregate.

The same readiness engine drives the registration card's readiness line,
so the two never disagree; solve diagnostics (`lastSolve`: RMS,
conditioning eigenvalues, per-pin residuals) persist in the workspace and
are cleared per mesh when the producing step is rolled back. In study mode
the panel is its own gated feature (`workflowPanel`, not enabled in
glacier-v1).

---

## 10. Retarget (re-host pins onto another mesh)

When pins were placed on one surface but you want them evaluated against
another (e.g. after loading a better reconstruction):

1. Option/Alt + wheel to make the target mesh the **active picking layer** (§3).
2. Gear popover → **→ Project pins to active layer**.
3. Every committed pin is projected to its closest point on the target
   (`/query/closest`, parallel). A centred modal lists each pin with its
   projection distance `Δ`; rows with `Δ > 2× falloff` (or no projection)
   are flagged red.
4. Accept (✓) / reject (✕) per pin, then **Apply** — accepted pins move to
   their projected centre, re-host to the target mesh, and their probes and
   rings invalidate/recompute. Moved pins with correspondence enabled also
   **re-seed their anchors** (auto/unaccepted only — manual picks survive).
   **Cancel** discards everything. Retarget is blocked while a registration
   preview is pending.

---

## 11. Fusion & panorama

**◈ Fusion** (top bar): suppresses the normal meshes and re-renders them
into an offscreen pass where per-fragment **depth = combined error**, so the
lowest-error surface wins depth testing — a per-pixel "best of all meshes"
composite of the registered ensemble. Pins, rings, and labels still render
on top. Until a registration exists a notice explains that fusion shows
only the reference. Picking under fusion is a server-side raycast keeping
the lowest-error hit, so clicks land on the surface you actually see.

**▦ Pano** (top bar): opens the panorama card — a live cylindrical 360°
strip rendered from a synthetic viewpoint (cubemap capture, reprojected; one
pose per dataset at the scene centre; regenerated per dataset, not
persisted). Modes: *Photo* / *Render* / *Blend* (with blend slider).
Committed pins appear as markers in the strip; during pin placement,
clicking the panorama raycasts into the scene and places a pin there.
**✈ Fly to pose** moves the 3D camera to the panorama viewpoint.

---

## 12. Workspace persistence

Gear popover → **💾 Save** downloads a JSON file through the browser;
**📂 Load** opens a file picker and applies one. No server-side store.

**Persisted (workspace JSON v2):** active dataset name, camera pose, all
pins (centre, radii, phase, payload incl. line traces, patch data and the
**correspondence anchors** with source + accepted state, host mesh,
colours, creation camera, probe length/lock/range settings), mesh
transforms, the **registration history** (full steps with before/after
transforms), mesh visibility, sensor types, dataset-error overrides, the
lasso (polygon, planes, enabled flag), registration mode + reference mesh,
and all view settings (ghost, shading, slope, heatmap mode, fusion,
rendering mode). Version-1 workspaces still load (new fields default
empty).

**Not persisted (recomputed or transient):** probe results and contact
rings (recompute lazily after load), the **pending solve preview** (a
preview never survives a save/load cycle — only committed steps do), hover
probe, panoramas, chart-link state, solo state, anchor review / pick /
patch-picker state, open/closed UI state, debug log.

---

## 13. Server API reference (input/output)

All query coordinates are **absolute world space**; the server converts to
mesh-local frames. `Name` is `"dataset/mesh"`.

**Static data:**

```
GET /api/datasets                                → string[]
GET /api/datasets/default                        → string
GET /api/datasets/{ds}/centroids                 → { mesh: [x,y,z] }
GET /api/datasets/{ds}/bboxes                    → { mesh: { min, max } }   (also warms server caches)
GET /api/datasets/{ds}/mesh/{name}               → OBJ part count
GET /api/datasets/{ds}/mesh/{name}/{i}           → binary mesh
GET /api/datasets/{ds}/mesh/{name}/{i}/atlas     → JPEG
```

**Queries (POST, JSON):**

| Endpoint | Request | Response | Used by |
|---|---|---|---|
| `/api/query/ray` | name, origin, direction | hit, t, point, triangleId | picking under fusion, panorama placement (client fans out over meshes via `rayHitMany`) |
| `/api/query/closest` | name, point | found, point, distanceSquared | retarget projection, anchor auto-seeding, patch-picker centre seeding |
| `/api/query/isoline` | name, elevation, seed, maxPoints | polyline | Line payload (elevation mode) |
| `/api/query/curvature-ridge` | name, seed, maxPoints | polyline + scalars | Line payload (ridge mode) |
| `/api/query/patch` | name, centre, radius, maxPoints, optional frameNormal + frameRefDir (skips the local plane fit) | projected points (incl. per-point atlas UVs), refDir, normal (echoes a supplied frame) | Patch payload, patch small-multiples picker |
| `/api/query/contact-rings` | name, centre, radius, maxPoints | rings: [[x,y,z]…]… (closed rings repeat the first point) | pin contact rings |
| `/api/query/probe` | meshes [name + world transform], referenceName, centre, radius, length (0 = auto), maxPointsPerMesh | normal, planarity, length, per-mesh distributions (count/median/IQR/std/KDE), three-source decomposition | pin probe + hover probe |
| `/api/query/icp` | referenceName, movingName, initialTransform, anchor centres/sigmas/weights, regionEps | transform (4×4), convergence per iteration, residuals | fine registration (Stage 2) |
| `/api/query/lsq-pairs` | movingName, pairs [{refPoint, movingPoint, weight}] (world space, current poses) | delta transform (maps current-world moving points onto the reference), perPairResiduals, conditioning {eigenvalues, collinearityWarning}; HTTP 400 on <3 pairs | coarse registration (Stage 1) |

**Study mode (POST unless noted):**

| Endpoint | Request | Response | Notes |
|---|---|---|---|
| `/api/study/session` | { token } or { demo, studyId, condition } | sessionId, condition, demo, resumed (+ lastPhaseId/lastStepId), configPublic | token sessions: balanced FULL/NUM assignment; same token resumes an active session, refuses completed (409) / screened |
| `/api/study/list` (GET) | — | string[] of valid study ids | gear-popover demo picker |
| `/api/study/{sid}/events` | { events: [{t, type, payload}] } | 204 | batched telemetry append |
| `/api/study/{sid}/answers` | { questionId, value, confidence? } | { screened } (+ correct, **tutorial gold only**) | idempotent upsert; 3rd wrong tutorial-gold submission screens out |
| `/api/study/{sid}/transforms` | { label, perMesh: { mesh: [16] } } | 204 | world-space row-major 4×4; TRE vs secret check points scored server-side, **never returned** |
| `/api/study/{sid}/workspace` | { workspaceJson } | 204 | auto-uploaded on entering the exit phase |
| `/api/study/{sid}/advance` | { phaseId, stepId } | 204 / 409 | order-validated progress mirror; repeats of recorded steps are accepted (tutorial retry) |
| `/api/study/{sid}/complete` (GET) | — | { code } / 409 | HMAC code once all non-optional steps advanced + a `final` transforms post exists |
| `/api/study/{studyId}/tokens` | { n } | string[] | **localhost only** |

`secret.json` and the score files are reachable through no route; the
public config is served verbatim and validated to contain no answer keys.

Performance contracts the client relies on: per-mesh requests are issued in
parallel (never sequentially), densities are capped via `maxPoints`, heavy
post-processing stays off the UI update loop, and user-driven triggers are
debounced (250 ms) with cancellation of the superseded request.

---

## 14. Interaction → state cheat sheet

What invalidates what (the glue between workflows):

| Action | Invalidates / triggers |
|---|---|
| Pin radius / centre / payload / probe-length change | that pin's probe + contact rings → lazy recompute |
| Mesh visibility toggle | all probes (sample set changed); **not** contact rings (display-only); never the correspondence anchors (hidden meshes keep theirs, they're just not solved) |
| Coarse / fine solve result arrives | pending preview fills in; rings + preview probes recompute under the effective (previewed) transforms |
| Registration **commit** / **rollback** / **reset** | transforms + history; all probes + all contact rings + algorithm RMS; correspondence anchors re-based by the applied world delta; Diff heatmap reverts |
| Registration **discard** | pending preview dropped; rings recompute back at the committed pose; committed probes stay valid |
| Pending preview active | blocks pin placement, retarget, fusion, dataset switch, anchor picking (tooltips + toast explain) |
| Enable correspondence / reference change / retarget apply | (re-)seed reference anchor + per-mesh anchors (auto/unaccepted only) → review modal |
| Setting an anchor (patch / 3D / violin pick, review apply) | readiness display only — anchors never touch probes |
| Retarget apply | moved pins' probes + rings; pin host mesh; anchor re-seed |
| Reference mesh change | all probes; pending preview discarded; anchor re-seed |
| Dataset switch | pins, lasso, panoramas, chart state, picking layer, pending preview, anchor flows — all cleared (registration history is kept, like the transforms) |
| Sensor type / error override | probe error decomposition, provenance heatmap + diff detection limit, fusion winner |
| Lasso, solo, ghost settings, isolate pins | rendering only — no recomputation anywhere |
| Coarse / fine solve response | also recorded in `lastSolve` (per-mesh RMS + conditioning + per-pin residuals) → workflow-panel chips/stats; cleared per mesh by rolling back the producing step |
| Study: any reducer step | telemetry events derived (before/after diff) → predicate counts + Seq milestones → Next gating refreshed |
| Study: Next | advance posted (idempotent); answer re-posted; phase boundary may switch dataset (scene + registration state reset, predicate counts cleared) |
| Study: registration commit | `commit#n` transforms posted (TRE scored server-side) |
| Study: entering the exit phase | `final` transforms + workspace auto-upload; completion code fetched on the last step |
| Study: tutorial gold wrong ×2 / ×3 | retry step re-shown / screened-out page (server decides) |

---

## 15. User-study mode

The whole app can run as a guided study session. Entry points:

- **Real session:** open `/s/{token}` (tokens are minted per study via the
  localhost-only endpoint). The client posts the token, gets a balanced
  condition (FULL or NUM) and the public study config, resets the scene and
  starts at phase 0 — or resumes an interrupted session at the step after
  its last recorded advance ("progress kept, scene reset" notice). Real
  sessions render **no navigation back** to the full app: no top bar, no
  gear, no dataset switcher, no save/load.
- **Demo preview:** gear popover → *Preview study mode* (condition picker +
  study picker). Identical behaviour, but flagged `demo` everywhere,
  excluded from condition balancing, and an **Exit study** button returns
  to the full app with a reset scene.

**The study bar** (replaces the top bar): progress dots (one per phase),
phase title, the phase's **goal line** (always visible), a tool strip
exposing only the features the current phase allows (layer-panel toggle,
pin placement), a demo badge + exit (demo only), `?` (re-opens the current
step's instructions) and **Next** — enabled only when the current step is
complete (instruction: immediately; guided action: its predicate fires;
question: answered incl. confidence where required, tutorial gold answers
must also be confirmed correct; questionnaire: every item answered).

**Steps** render as either a dim-background **instruction overlay**
("Got it" to dismiss, "Continue →" mirrors Next) or, for guided actions
with an anchor, a **non-blocking tooltip card** pointing at the anchored
UI element with a live ○/✓ checkmark. Questions dock in a right-hand
**task pane**: single choice, scene-click ("Mark in scene" arms a one-shot
depth-gated 3D pick that drops a flag marker; Esc cancels, re-click
replaces), numeric + unit, free text with a minimum length, and Likert
grids (SUS 5-pt, Raw-TLX 0–100 sliders, ICE-T 7-pt), each with an optional
7-point confidence row. Answers post on change (coalesced over 500 ms so
keystrokes and transient selections don't flood the server or burn
tutorial-gold attempts) and again on Next (posted immediately; the final
value wins server-side).

**Feature gating** is two-layered: views consult
`phase.allowedFeatures ∩ ¬condition.disabledFeatures`, and the reducer
no-ops any gated or Full-only message with a "Not available in this step"
toast (silently for pointer-frequency messages). The **NUM condition**
hides the violin chart (the pin card shows a numeric median/IQR table plus
registration RMS before → after instead), the heatmaps, the three-source
bar and the split-violin preview — the solver mechanics are untouched.

**Progress predicates** consume the telemetry event stream (one central
diff of model-before/model-after per reducer step). Event counts accumulate
per dataset epoch (they reset when a phase switches tutorial → main);
ordered `Seq` milestones advance monotonically and survive step re-entry,
so the tutorial retry path never un-completes prior work.

**Tutorial gold checks** are the single place server correctness reaches
the client: a wrong answer shows "not quite", the second wrong answer
re-opens the relevant tutorial step, the third screens the participant out
politely (status `screened`, token dead). Main-phase gold answers are
stored and scored offline only.

**Accuracy scoring** happens server-side on every transforms post
(`commit#n` on each registration commit, `final` on entering the exit
phase or via the study-only **★ Set as final** button in the registration
card): TRE against secret check-point pairs, stable and moving terrain
separately, appended to a per-session score file that no route serves.

**Completion**: entering the exit phase also auto-uploads the workspace;
the final step fetches the completion code (HMAC over the session id),
which the server only issues once every non-optional step has an advance
record and a `final` transforms post exists.

Authoring lives in `src/Superserver/studies/{studyId}/`: `config.json`
(public — phases, steps, predicates, questionnaires, feature lists, a
coarse moving-region polygon for the soft pin warning) and `secret.json`
(planted answers, gold thresholds, TRE check points). Invalid studies are
refused at startup with logged reasons. Session data accumulates under
`studies/{studyId}/data/` as append-only JSONL (gitignored).
