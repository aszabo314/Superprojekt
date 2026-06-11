# Superprojekt — Interactions & Workflows

A walkthrough of everything a user can do in the app, how the individual
interactions chain into larger workflows, and what goes in and out of each
step. The app is a research prototype for comparing multiple 3D surface
reconstructions (meshes from different sensors/pipelines) of the same scene:
inspect them, quantify their disagreement, align them, and fuse them.

Architecture in one sentence: a thin Blazor-WASM client (Elm-style
Model → Update → View, WebGL2 rendering) talks to an ASP.NET/Giraffe server
that owns the heavy geometry (Embree BVH raycasts, closest-point, M3C2
probes, isolines, ridges, sphere contact rings, ICP) at
`http://localhost:5000`.

---

## 1. The big picture — a typical session

The individual interactions below compose into one overarching analysis loop:

```
load dataset ──► navigate / inspect meshes ──► declare per-mesh error metadata
     │                                                  │
     ▼                                                  ▼
place ScanPins on regions of interest ◄────── provenance heatmap shows
     │  (probe quantifies per-mesh                where error budgets are
     │   disagreement at each pin)                exceeded
     ▼
pins double as registration anchors ──► run ICP (traditional or
     │                                   region-restricted by the pins)
     ▼
mesh transforms update ──► probes & contact rings recompute automatically
     │                      (everything pin-derived is invalidated + lazily
     ▼                       re-queried)
fusion mode composites the registered ensemble per-pixel by lowest error
     │
     ▼
save workspace (JSON download) — pins, transforms, settings survive reload
```

Key feedback loops:

- **Pins → registration → pins.** A pin's probe shows how far apart the
  meshes are; running a registration moves the meshes, which invalidates all
  probes and contact rings; they recompute lazily and show the improvement.
- **Error metadata → probe / provenance / fusion.** The per-mesh dataset
  error (sensor type or manual override) feeds the probe's three-source
  error decomposition, the provenance heatmap threshold test, and the
  fusion pass's per-pixel "lowest combined error wins" depth.
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
| Wheel | Zoom along the view direction (smoothly animated, ~120 ms) |
| Wheel **while hovering ≥2 stacked meshes** | Does **not** zoom — cycles the **active picking layer** through the meshes under the cursor; an overlay label near the cursor names the current layer |
| **Alt + wheel** | Always zooms, bypassing layer cycling |
| Double-click on a surface | Fly the orbit centre to the clicked point (350 ms animation). Background double-clicks are ignored (depth-gated) |
| Two-finger touch | Pinch-zoom + rotate (mobile) |
| Right-click | Suppressed (no context menu) |
| **Ctrl + click** | Transient hover probe (§8.6) |
| Click | Mode-dependent: add lasso vertex / place pin / select; otherwise idle |
| **Esc** | Clears, in priority order: hover probe → lasso drawing → pin placement |
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

**Active picking layer.** The wheel-selected layer biases picks to one mesh
when several overlap; it is also the prerequisite/target for the **retarget**
workflow (§10) and the preferred reference for the hover probe.

---

## 4. Mesh management (left panel)

Open with the top-bar hamburger (☰). Per-mesh row: colour swatch (the mesh's
categorical palette colour, used consistently in charts, rings, and cards),
shortened name, and three buttons:

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
- **Error provenance**: *Show heatmap* paints fragments whose combined error
  exceeds the *Threshold* slider, colour-coded by dominant source — blue =
  dataset (sensor), orange = algorithm (registration residual), purple =
  conditioning (local geometry). *Falloff zones only* restricts painting to
  pin falloff regions. While the heatmap is on, hovering a surface shows a
  per-mesh provenance breakdown tooltip (D/A/C bar + numbers).

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
conditioning) with numeric breakdown, probe-length override, and the
reliability slider.

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

## 9. Registration (ICP)

**Input:** a reference mesh + visible moving meshes (+ optionally pins as
anchors). **Output:** rigid per-mesh transforms + per-mesh RMS residuals.

Opened with the floating **⚙ Registration** toggle button. The card:

1. **Solve mode** — *Traditional ICP* (uniform correspondence weights) or
   *Region-restricted* (each committed pin becomes a Gaussian anchor:
   centre = pin centre, sigma = FalloffRadius, weight multiplier = the
   Point payload's reliability slider — so registration trusts the surface
   where you placed trusted pins).
2. **Reference mesh** — pick one (toggle buttons, single selection).
3. **▶ Run** (disabled until a reference is chosen; shows ⏳ while running)
   — solves `POST /api/query/icp` for every visible non-reference mesh in
   parallel (Gauss-Newton point-to-surface, Embree closest-point
   correspondences, trimmed at 3× the median pair distance).
4. **↺ Reset** — clears all transforms back to identity.

On completion, each mesh's transform and RMS land in the model; the RMS
feeds the provenance heatmap's *algorithm* component, and **all pin probes
and contact rings are invalidated** and lazily recompute — so the violin
charts immediately reflect the post-alignment disagreement. The lasso has no
effect on registration.

---

## 10. Retarget (re-host pins onto another mesh)

When pins were placed on one surface but you want them evaluated against
another (e.g. after loading a better reconstruction):

1. Wheel-cycle to make the target mesh the **active picking layer** (§3).
2. Gear popover → **→ Project pins to active layer**.
3. Every committed pin is projected to its closest point on the target
   (`/query/closest`, parallel). A centred modal lists each pin with its
   projection distance `Δ`; rows with `Δ > 2× falloff` (or no projection)
   are flagged red.
4. Accept (✓) / reject (✕) per pin, then **Apply** — accepted pins move to
   their projected centre, re-host to the target mesh, and their probes and
   rings invalidate/recompute. **Cancel** discards everything.

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

**Persisted:** active dataset name, camera pose, all pins (centre, radii,
phase, payload incl. line traces and patch data, host mesh, colours,
creation camera, probe length/lock/range settings), mesh transforms, mesh
visibility, sensor types, dataset-error overrides, the lasso (polygon,
planes, enabled flag), registration mode + reference mesh, and all
view settings (ghost, shading, slope, provenance, fusion, rendering mode).

**Not persisted (recomputed or transient):** probe results and contact
rings (recompute lazily after load), hover probe, panoramas, chart-link
state, solo state, ICP residuals, open/closed UI state, debug log.

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
| `/api/query/closest` | name, point | found, point, distanceSquared | retarget projection |
| `/api/query/isoline` | name, elevation, seed, maxPoints | polyline | Line payload (elevation mode) |
| `/api/query/curvature-ridge` | name, seed, maxPoints | polyline + scalars | Line payload (ridge mode) |
| `/api/query/patch` | name, centre, radius, maxPoints | projected points, refDir, normal | Patch payload |
| `/api/query/contact-rings` | name, centre, radius, maxPoints | rings: [[x,y,z]…]… (closed rings repeat the first point) | pin contact rings |
| `/api/query/probe` | meshes [name + world transform], referenceName, centre, radius, length (0 = auto), maxPointsPerMesh | normal, planarity, length, per-mesh distributions (count/median/IQR/std/KDE), three-source decomposition | pin probe + hover probe |
| `/api/query/icp` | referenceName, movingName, initialTransform, anchor centres/sigmas/weights, regionEps | transform (4×4), convergence per iteration, residuals | registration |

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
| Mesh visibility toggle | all probes (sample set changed); **not** contact rings (display-only) |
| Registration complete / reset | all probes + all contact rings + algorithm RMS overlay |
| Retarget apply | moved pins' probes + rings; pin host mesh |
| Reference mesh change | all probes |
| Dataset switch | pins, lasso, panoramas, chart state, picking layer — all cleared |
| Sensor type / error override | probe error decomposition, provenance heatmap, fusion winner |
| Lasso, solo, ghost settings, isolate pins | rendering only — no recomputation anywhere |
