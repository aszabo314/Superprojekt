# ScanPin — Core 2 Vertical Slice Spec

## 1. What this slice is

ScanPin currently has all the UI scaffolding for error visualization (toggles, fields, model state) but no error metric and no actual visualization is wired up. This slice closes that gap with a concrete, scientifically-grounded approach:

1. An **N-mesh M3C2 probe** that, given an Anchor Sphere, samples all visible meshes inside a cylindrical region along the local normal and returns one 1D distribution per mesh.
2. A **ridgeline chart** rendered in the pin card showing the N distributions on a shared signed-distance axis.
3. A **three-source error decomposition** (dataset error / algorithm residual / local conditioning) derived from the probe data and shown as a stacked bar.
4. A **hover probe** that runs a transient version of the same query under the cursor for spatial exploration.

The metric design follows M3C2 (Lague, Brodu, Leroux, ISPRS J. 2013) generalised from pairwise comparison to N meshes. The chart design follows the ridgeline / joyplot convention common in ensemble visualization (Cluster E of the literature synthesis). The three-source decomposition follows the GUM uncertainty-budget framing (Type A / Type B), with category labels carried through to a stacked-bar visualization.

---

## 3. Data model

Names are suggestive. Match existing codebase conventions; the codebase has likely already evolved different names for similar concepts.

### 3.1 The probe data structure

```
type ProbeCylinder = {
  Centre     : Vec3      // pin centre, in world coords
  Axis       : Vec3      // unit normal direction
  Radius     : float     // pin radius
  Length     : float     // along Axis, symmetric around Centre (length/2 each way)
}

type MeshDistribution = {
  MeshId         : MeshId
  PointsAlongAxis: float[]   // signed projection of each point onto the axis,
                              //   centred such that 0 = the cylinder centre
  Count          : int
  Median         : float
  IQR            : float
  Std            : float
  KdePoints      : (float * float)[]  // (x, density) — computed by Phase 2
  Bandwidth      : float
}

type ProbeResult = {
  Cylinder       : ProbeCylinder
  ReferenceMeshId: MeshId            // which mesh's normal was used
  Distributions  : MeshDistribution[] // one per visible mesh; meshes
                                       //   with zero points inside are still
                                       //   included with Count = 0
  ComputedAt     : DateTime
  // Phase 3 outputs:
  Sources        : ThreeSources option
}

type ThreeSources = {
  DatasetError    : float   // metres; reference-mesh-relative scalar
  AlgorithmResid  : float   // metres
  LocalConditioning: float  // metres or unitless; see §5.3
  // Per-mesh detail for inspection
  PerMesh         : Map<MeshId, ThreeSourcesPerMesh>
}

type ThreeSourcesPerMesh = {
  IqrMetres       : float   // dataset error component (per-mesh spread)
  MedianOffset    : float   // algorithm residual component (signed distance to reference median)
  PointCount      : int     // contributes to conditioning
}
```

### 3.2 Attachment to the anchor

Each Anchor Sphere with a **Point payload** owns one `ProbeResult option`. The result is computed lazily on first request and cached.

### 3.3 Hover-probe transient state

```
type HoverProbe = {
  Cursor3D       : Vec3
  Result         : ProbeResult
  Stale          : bool      // true when cursor has moved beyond threshold
}
```

One global slot in the workspace. Cleared on Escape or after a few seconds of inactivity.

---

## 4. The N-mesh M3C2 probe — algorithm

### 4.1 Normal estimation (constrained to pin region)

Given a pin sphere `S` (centre `c`, radius `r`) and a reference mesh `M_ref`:

```
function estimate_normal(S, M_ref):
  P = { all vertices v of M_ref with ||v - c|| < r }
  if |P| < 6:
    return None  // not enough points; warn user
  C = covariance_matrix(P)   // 3x3
  (eigvals, eigvecs) = eigendecomposition(C)
  // eigvecs sorted by eigenvalue ascending: smallest first
  n = eigvecs[0]             // direction of smallest variance
  lambda_3 / lambda_2 ratio = eigvals[0] / eigvals[1]
  if (eigvals[0] / eigvals[1]) > 0.5:
    // The region is not planar enough; the normal is unreliable.
    warn: "non-planar region — normal may be unreliable"
  // Orient consistently: prefer the half-space containing the global up direction.
  if dot(n, world_up) < 0:
    n = -n
  return n, planarity = (eigvals[0] / eigvals[1])
```

For heightfields specifically (current use case), `world_up = (0, 0, 1)` and the normal will be roughly vertical. For non-heightfield data in the future, the convention `dot(n, world_up) > 0` can be replaced or made user-overridable.

The planarity score is stored on the probe result and exposed in the pin card (small badge: "planar / not planar"). This is informational; the probe runs either way.

### 4.2 Cylinder construction

Given centre `c`, axis `n`, radius `r` (pin radius), and length `L`:

```
function cylinder(c, n, r, L):
  return ProbeCylinder { Centre = c, Axis = n, Radius = r, Length = L }
```

The cylinder extends from `c - (L/2)*n` to `c + (L/2)*n`.

`L` is computed once per probe call as:

```
L = min(100.0, 1.1 * union_bbox_extent_along(n))
```

where `union_bbox_extent_along(n)` is the maximum extent of the union of all visible meshes' bounding boxes when projected onto the axis `n`. Expose `L` as user-adjustable in the pin's adjustment flyout (alongside Radius and σ), with the auto-computed value as the default.

### 4.3 Per-mesh sampling and projection

For each visible mesh `M_i`:

```
function sample_mesh(M_i, cylinder):
  hits = []
  for each vertex v in M_i (after applying M_i's transform):
    // 1. axial coordinate
    t = dot(v - cylinder.Centre, cylinder.Axis)
    if abs(t) > cylinder.Length / 2:
      continue
    // 2. radial test
    radial = v - cylinder.Centre - t * cylinder.Axis
    if ||radial|| > cylinder.Radius:
      continue
    hits.append(t)
  return hits
```

For triangle meshes with sparse vertices and large triangles, you may need to also sample the triangle interiors (densify before projection) to avoid empty distributions for low-resolution meshes. **For the slice, implement triangle-interior sampling**.

### 4.4 Per-mesh statistics

For each `MeshDistribution` with `Count > 0`:

```
Median = quantile(PointsAlongAxis, 0.5)
IQR    = quantile(PointsAlongAxis, 0.75) - quantile(PointsAlongAxis, 0.25)
Std    = standard_deviation(PointsAlongAxis)
```

The signed distance axis is re-centred such that `0` corresponds to the *reference mesh's median*, not the cylinder centre. So the reference mesh's distribution will always be centred near `0`, and other meshes' offsets read directly.

```
ref_median = Distributions[ReferenceMeshId].Median
for each MeshDistribution d:
  shift d.PointsAlongAxis by -ref_median
  re-compute Median, IQR, Std after shifting
```

This re-centring happens once per probe; cache the shifted distributions.

### 4.5 KDE per mesh (Phase 2)

For each `MeshDistribution` with `Count >= 4`:

```
h = silverman_bandwidth(PointsAlongAxis)
  = 0.9 * min(Std, IQR/1.34) * Count^(-1/5)

// Evaluate the KDE on a uniform grid covering the chart x-range:
xs = linspace(chart_xmin, chart_xmax, 200)
ys = [ (1 / (Count * h)) * sum( gaussian_kernel((x - t)/h) for t in PointsAlongAxis ) for x in xs ]
KdePoints = zip(xs, ys)
```

For `Count < 4`, store an empty `KdePoints` array and render a placeholder (median + IQR whisker only, no curve).

The chart x-range is computed once across all meshes' distributions for the pin:

```
chart_xmin = min over all i of (Distributions[i].Median - 3 * Distributions[i].IQR)
chart_xmax = max over all i of (Distributions[i].Median + 3 * Distributions[i].IQR)
```

with a hard floor on the range (e.g. ±0.1 m) so the chart doesn't collapse when all meshes are tightly aligned.

---

## 5. Three-source decomposition (Phase 3)

Derived from the probe data without additional sampling.

### 5.1 Dataset error

Per-mesh: `IqrMetres = MeshDistribution.IQR` — the local intrinsic spread of mesh *i*'s points. This is a *proxy* for true dataset error.

Aggregate (for the stacked bar): mean IQR across all meshes with `Count > 0`, or the IQR of the union of all distributions — pick the latter for simplicity:

```
DatasetError = combined_IQR( union of all PointsAlongAxis across meshes )
```

### 5.2 Algorithm residual

Per-mesh: `MedianOffset = Distribution[i].Median - 0`. (Since we already re-centred so the reference is at 0, this is just `Distribution[i].Median`.) Sign-preserving.

Aggregate: RMS of the per-mesh offsets excluding the reference mesh:

```
AlgorithmResid = sqrt( mean( (offset_i)² for i in non-reference meshes with Count > 0 ) )
```

### 5.3 Local conditioning

The "how well-determined is this probe?" term. A geometric-strength proxy:

```
n_meshes_present = count of meshes with Count > 0
total_points     = sum of Count across all meshes
mean_points_per_mesh = total_points / n_meshes_present (if > 0 else 0)

LocalConditioning = c_scale / (n_meshes_present * mean_points_per_mesh + epsilon)
```

with `c_scale` chosen so the conditioning term reads in metres on the same scale as the other two (set `c_scale` empirically against the test dataset; start with `c_scale = 1.0` and tune). A pin with all meshes present and many points each → low conditioning (well-determined). A pin where only one or two meshes have any points → high conditioning (under-determined).

Add: if any mesh in the visible set has `Count = 0` (mesh doesn't reach this pin), flag this in the per-mesh detail so the user sees it as a coverage gap.

### 5.4 The three-source totals

For the stacked bar:

- DatasetError (metres)
- AlgorithmResid (metres)
- LocalConditioning (metres, with the caveat that it's a derived proxy)

These are stored in `ThreeSources` and rendered as described in §7.3.

---

## 6. Invalidation

A pin's probe result is invalidated and recomputed when any of:

- The anchor is moved (centre changes).
- The anchor's radius changes.
- The anchor's payload type is changed away from and back to Point.
- The designated reference mesh changes.
- Any mesh's transform changes (registration solve, manual transform).
- A mesh's visibility is toggled.
- A mesh is added to or removed from the workspace.

Compute lazily: only run the probe when the pin card is open or the probe result is otherwise displayed. Cache aggressively; the probe is expensive enough that re-running on every frame is wrong.

For the hover probe: re-run on each Ctrl-click (no caching — it's a transient query); clear on cursor move beyond ~10 px or on Escape.

---

## 7. UI specifications

### 7.1 Pin card layout for Point payloads

The existing point payload card receives the new content. The card from top to bottom:

1. Pin metadata: anchor name, mesh ID, radius, σ (existing).
2. **NEW: planarity badge** — green "planar" or yellow "not planar" based on the normal-estimation planarity score.
3. **NEW: ridgeline chart** (the main new content; see §7.2).
4. **NEW: three-source stacked bar** (see §7.3).
5. Pin actions: commit, discard, delete (existing).

### 7.2 Ridgeline chart

A 2D chart with:

- **Width:** card-width minus margins.
- **Height:** `30 * n_meshes + 60` pixels, capped at 400 px.
- **X axis:** signed distance, metres. Origin at 0 (where the reference mesh's median sits). Tick marks at sensible intervals; show units.
- **Y axis:** categorical, one row per visible mesh. Row order: auto by mesh median offset (closest to 0 at the top), unless "Lock order" is on.
- **Per row, left to right:**
  - Row label (mesh name), 80 px reserved on the left.
  - KDE curve, filled with the mesh's assigned categorical colour at alpha ≈ 0.4. Median marked with a vertical tick on the curve.
  - IQR whisker (small horizontal line at row baseline, spanning Q1 to Q3).
  - Right-side count badge (e.g. "n=247") at 60 px on the right.
- **Reference line:** vertical dashed line at x = 0, semi-transparent.
- **Empty rows** (Count = 0): render the row with the mesh name greyed out and a hyphen instead of count; no curve.

A small UI affordance below the chart: a "Lock order" toggle and an "x range" expand/shrink (auto / ±0.5 m / ±2 m / ±10 m / fit).

The chart should be rendered in whatever 2D rendering subsystem the existing pin card uses (likely SVG via aardvark.dom or an equivalent). If a chart library is already in the dependencies, use it; otherwise hand-roll the SVG. Don't add a heavy chart library for this slice.

### 7.3 Three-source stacked bar

A single horizontal bar below the ridgeline:

- **Total width** = sum of the three components, scaled to fit the card.
- **Three segments**, in order: DatasetError (red `#c44`), AlgorithmResid (green `#4a4`), LocalConditioning (blue `#46c`).
- **Hover on each segment** → tooltip with the numeric value in metres and the component name.
- **Numeric readout** below the bar showing the three values explicitly: "Data: 0.012 m | Algo: 0.005 m | Cond: 0.003 m" or similar compact format.

Use the colours given as defaults; if the existing codebase has a categorical palette, prefer those palette slots over hard-coded hex. Confirm with the user before locking the colour assignment if there's ambiguity.

### 7.4 Hover probe tooltip

A floating mini-chart at the cursor when Ctrl-click on a mesh surface:

- **Size:** roughly 240 × 160 px.
- **Content:** a compressed ridgeline (no row labels, only colour squares; no count badges; no stacked bar). The compressed chart shows the same data as the pin chart but at a glance.
- **Position:** offset slightly from cursor, kept within viewport.
- **Dismissal:** Escape, or click elsewhere, or after ~5 s of cursor movement away.
- **Probe parameters:** same as a pin probe, with radius = default (e.g. 5% of dataset bounding-box diagonal) and reference mesh = current declared reference. The probe is run with these parameters; the user does not configure them per-hover.

### 7.5 Adjustment flyout additions

The Point payload's adjustment flyout (when adjusting a pin) gains:

- **Cylinder length** slider (alongside Radius and σ), default = auto-computed, slider range from 1 m to 100 m, with an "auto" button to reset.

---

## 8. Phasing and acceptance criteria

Five phases. Implement the phases in order, only break when critical questions arrive. 

### Phase 1 — Probe data structure and core computation

Implement:
- The data types in §3.1 and §3.2.
- The normal estimation routine in §4.1.
- The cylinder construction in §4.2 (including auto-length computation).
- The per-mesh sampling and projection in §4.3.
- The per-mesh statistics in §4.4 (median, IQR, std).
- The reference-mesh re-centring in §4.4.
- Caching and the invalidation rules in §6.

### Phase 2 — KDE and ridgeline chart in pin cards

Implement:
- The KDE computation in §4.5.
- The ridgeline chart layout in §7.2.
- The planarity badge in §7.1.
- The "Lock order" toggle and the x-range expand/shrink control.
- Integration into the Point-payload pin card.

### Phase 3 — Three-source decomposition and stacked bar

Implement:
- The three computations in §5.
- The stacked-bar visualization in §7.3.
- Per-segment hover tooltips and the numeric readout.
- The per-mesh detail accessible via the chart (clicking a row reveals that mesh's `ThreeSourcesPerMesh` values in a small expansion).

### Phase 4 — Hover probe

Implement:
- The Ctrl-click gesture handler (reuse the existing sphere-query gesture if it's still wired up; otherwise add it).
- The transient `HoverProbe` state in §3.3.
- The compressed mini-chart tooltip in §7.4.
- Dismissal rules.

### Phase 5 — Wiring, invalidation, demo workflow

Implement:
- The full invalidation set from §6, end-to-end.
- A single demo workflow: load the Mars Kodiak dataset, place three pins, run a registration, observe the offsets change. Document any rough edges.
- Performance pass: probes should not noticeably block the UI; if they do, move to a worker / background thread.

---
