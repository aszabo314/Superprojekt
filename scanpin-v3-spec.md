# ScanPin V3 — Specification

## Purpose

This document specifies the V3 iteration of ScanPin. V2 delivered a core sample inspector with orthographic 3D sub-viewport, summary meshes, and boxplot-based filtering. V3 is a significant redesign based on evaluation findings: the separate 3D sub-viewport was visually confusing and redundant with the main scene. V3 removes the sub-viewport entirely, transfers inspection controls into the 3D scene, and replaces the core sample volume view with an **unwrapped cylinder surface stratigraphy diagram**.

The target reader is a Claude Code AI agent. Low-level graphics operations are marked as `STUB(graphics)`. Server-side computation is marked as `STUB(server)`.

---

## Pre-V3 Tasks (Do First)

Before starting V3 implementation, complete these cleanup tasks from V2:

1. **PinDemo: simplify ranking view.** Remove the boxplot visualization from the ranking list rows. Replace with the dataset name and variance value as plain text.
2. **Merge PinDemo → Superprojekt.** Merge the prototyped functionalities (average/aggregated meshes and ranking view) from the PinDemo project back into the main Superprojekt codebase.
3. **Git housekeeping.** Commit all current work and push to branch `scanpin-v2`. Switch back to `master` and continue V3 work from there.

---

## V2 Recap (What Gets Removed or Changed)

**Removed in V3:**
- The orthographic 3D sub-viewport (the core sample inspector embedded in the HTML panel).
- Side view / top view camera controls for the sub-viewport.
- The sub-viewport rendering of summary meshes (average, Q1, Q3) and filtered individual meshes.
- All `CoreSampleCamera` state.

**Kept from V2:**
- The ScanPin placement state machine (V1).
- The pin list with focus/delete.
- Dataset ranking with drag-reorder and visibility count (moved to a smaller panel alongside the stratigraphy diagram).
- Summary mesh computation stubs (average, Q1, Q3, variance) — repurposed for other uses.
- Cut plane mode toggle and slider logic — but controls are moved to 3D widgets.
- Serialization contract — extended for V3 fields.

**New in V3:**
- Unwrapped cylinder surface stratigraphy diagram (2D rasterized view in HTML panel).
- 3D cut plane slider widget (along cylinder axis) and cap-click angle control.
- Cylinder-based ghost clipping in the main 3D scene.
- Always-visible extracted lines in 3D (cut plane intersection lines and cylinder edge curves).
- Normalization/distortion mode for the stratigraphy diagram.
- Explosion view (experimental).
- Between-space hover highlighting with linked 3D highlight (experimental).

---

## Data Model Changes

### Removed Types

```fsharp
// REMOVED: no longer needed
// type CoreSampleViewMode = SideView | TopView
// type CoreSampleCamera = { AxisRotation; AxisPanOffset; OrthoScale }
```

### New / Modified Types

```fsharp
/// A single z-aligned ray query result for one mesh at one (angle, axisPosition) sample point.
/// A mesh may produce 0, 1, or multiple intersection points per ray (folds, overhangs).
type RayMeshIntersection = {
    DatasetId : DatasetId
    /// Z-values where the ray intersects this mesh's surface.
    /// Multiple values possible for non-heightfield meshes.
    ZValues : float list
}

/// A single column in the stratigraphy diagram (one angular position on the cylinder).
type StratigraphyColumn = {
    /// Angle around the cylinder axis (radians, 0 to 2π).
    Angle : float
    /// All intersection events in this column, sorted by z ascending.
    /// Each event is a (z-value, dataset-id) pair.
    Events : (float * DatasetId) list
}

/// The full stratigraphy data for one ScanPin.
/// STUB(server): computed by casting z-aligned rays at a grid of (angle, axisPosition) points
/// on the cylinder surface.
type StratigraphyData = {
    /// Number of angular samples.
    AngularResolution : int
    /// The axis-position range covered (min z to max z along the extrusion axis).
    AxisMin : float
    AxisMax : float
    /// One column per angular sample.
    Columns : StratigraphyColumn[]
    /// Per-column global min/max z across all datasets (for normalization).
    ColumnMinZ : float[]
    ColumnMaxZ : float[]
}

/// Display mode for the stratigraphy diagram.
type StratigraphyDisplayMode =
    /// True z-values, faithful to the actual geometry.
    | Undistorted
    /// Per-column normalization: local min → bottom of rect, local max → top.
    /// Amplifies small differences. Clamps to a minimum range to avoid division-by-zero artifacts.
    | Normalized

/// State for the explosion view inside the ScanPin cylinder.
type ExplosionState = {
    /// Expansion factor. 0 = no explosion, 1 = full separation.
    /// Each mesh i is displaced along the axis by (i * ExpansionFactor * spacing).
    /// Mesh ordering is by average z-position within the cylinder (lowest = index 0).
    ExpansionFactor : float
    /// Whether explosion is currently active.
    Enabled : bool
}

/// State for the between-space hover highlighting.
type BetweenSpaceHighlight = {
    /// The two datasets bounding the hovered gap.
    LowerDataset : DatasetId
    UpperDataset : DatasetId
    /// The angular position and z-range of the hover point.
    Angle : float
    ZLower : float
    ZUpper : float
    /// Whether a highlight is currently active.
    Active : bool
}

/// Whether the ghost clipping cylinder effect is active for this pin.
type GhostClipMode =
    | GhostClipOff
    | GhostClipOn

/// Whether extracted lines are rendered in 3D for this pin.
type ExtractedLinesMode = {
    /// Lines from the cut plane intersection with each dataset mesh.
    ShowCutPlaneLines : bool
    /// Lines where each dataset mesh intersects the cylinder surface.
    ShowCylinderEdgeLines : bool
}

/// V3 ScanPin state (replaces ScanPinV2 inspector fields).
type ScanPinV3 = {
    // ... all V1 fields (Id, Phase, Prism, CutPlane, CreationCameraState, CutResults, DatasetColors) ...

    /// Stratigraphy data computed from z-aligned ray queries on the cylinder surface.
    /// STUB(server): recomputed when prism changes or datasets change.
    Stratigraphy : StratigraphyData option

    /// Current display mode for the stratigraphy diagram.
    StratigraphyDisplay : StratigraphyDisplayMode

    /// Dataset ranking and filtering (carried over from V2).
    Filter : CoreSampleFilter

    /// Ghost clipping toggle for this pin.
    GhostClip : GhostClipMode

    /// Extracted lines toggle for this pin.
    ExtractedLines : ExtractedLinesMode

    /// Explosion view state (experimental).
    Explosion : ExplosionState

    /// Current between-space hover state.
    BetweenSpaceHover : BetweenSpaceHighlight option
}
```

### Stratigraphy Computation (STUB)

```fsharp
/// Cast z-aligned rays on the cylinder surface to build the stratigraphy data.
///
/// For each (angle, axisPosition) sample point on the cylinder surface:
/// 1. Compute the 3D position on the cylinder wall.
/// 2. Cast a ray from that position along the local z-direction (perpendicular
///    to the cylinder surface at that point, projected onto the z-axis).
///    NOTE: "z-aligned" means aligned with the extrusion axis direction, not world-Z.
/// 3. For each loaded mesh, find all intersection points of the ray with the mesh.
/// 4. Record each intersection as (z-value, dataset-id).
///
/// STUB(server): the ray-mesh intersection queries happen server-side.
/// The client sends: cylinder definition (anchor, axis, radius, extents) + grid resolution.
/// The server returns: StratigraphyData.
val computeStratigraphy : prism:SelectionPrism -> angularRes:int -> Async<StratigraphyData>

/// Query mesh intersections in a local neighborhood for between-space 3D highlighting.
/// Given a point on the cylinder surface and a radius, sample rays in the neighborhood
/// and return intersection data for the two specified datasets.
///
/// STUB(server): the server needs to support filtered queries (only specific mesh IDs).
val queryBetweenSpaceNeighborhood :
    prism:SelectionPrism ->
    centerAngle:float ->
    centerZ:float ->
    neighborhoodRadius:float ->
    lowerDatasetId:DatasetId ->
    upperDatasetId:DatasetId ->
    Async<(V3d * V3d) list>  // pairs of (lower surface point, upper surface point) for triangulation
```

---

## Stratigraphy Diagram (HTML Panel)

The stratigraphy diagram replaces the core sample 3D sub-viewport. It is a 2D rendering of the unwrapped cylinder surface, displayed in the HTML detail panel.

### Layout

```
┌──────────────────────────────────────────┐
│ Pin Title / ID                     [X]   │
├──────────────────────────────────────────┤
│                                          │
│  ┌────────────────────────────────────┐  │
│  │                                    │  │
│  │   Stratigraphy Diagram             │  │
│  │   (OpenGL render target)           │  │
│  │                                    │  │
│  │   Horizontal: angle (0° – 360°)   │  │
│  │   Vertical: axis position (z)      │  │
│  │                                    │  │
│  │   Colored stripes = mesh contacts  │  │
│  │   Filled gaps = between-spaces     │  │
│  │                                    │  │
│  └────────────────────────────────────┘  │
│                                          │
│  [Undistorted]  [Normalized]             │
│                                          │
├──────────────────────────────────────────┤
│ Toggles                                  │
│  [☑ Ghost Clip]  [☑ Cut Lines]           │
│  [☑ Cylinder Edge Lines]                 │
│  [☐ Explosion: ════●════ ]              │
├──────────────────────────────────────────┤
│ Dataset Ranking (scrollable, drag-rank)  │
│  ┌──────────────────────────────────┐    │
│  │ ■ Dataset 2024-03   σ=4.2       │ ☰  │
│  │ ■ Dataset 2024-01   σ=3.8       │ ☰  │
│  │ ■ Dataset 2023-11   σ=3.1       │ ☰  │
│  │ ─ ─ ─ visibility cutoff ─ ─ ─ ─ │    │
│  │ ■ Dataset 2023-09   σ=2.4       │ ☰  │
│  │ ...                              │    │
│  └──────────────────────────────────┘    │
│  Showing top [5 ▼] of 20                │
└──────────────────────────────────────────┘
```

### Visual Encoding

The stratigraphy diagram is rendered using OpenGL to an offscreen framebuffer, displayed in the HTML panel as a render target (texture on a quad, or however aardvark.dom handles embedded render controls).

**Rendering approach:**

1. Discretize the cylinder surface into `AngularResolution` columns (e.g., 360 or 720).
2. For each column, the sorted intersection events define a sequence of z-positions. Each event is a mesh contact.
3. Render as vertical strips of quads (one strip per angular column):

**Mesh contact stripes:**
- At each intersection z-value, draw a thin horizontal band (e.g., 2–4 pixels tall) colored by the dataset's assigned color.
- If a dataset has multiple intersections in the same column (fold/overhang), each gets its own stripe.

**Between-spaces (gaps between consecutive contacts):**
- The area between two consecutive contact stripes is filled with a **neutral alternating color** (light gray / lighter gray, alternating by gap index within the column) to make the layers visually distinct.
- On hover, the hovered between-space is highlighted with a distinct color (e.g., a warm translucent overlay).
- The identity of the bounding datasets and the gap thickness are shown in a tooltip.

**Rationale for neutral fills:** With N=20 meshes and arbitrary overlap patterns, encoding the bounding pair as color would require ~190 distinct colors. Starting with neutral fills and using hover for identity is the conservative, readable choice. Richer encodings (pair-color, thickness-mapped sequential colormap) can be tried later.

**Dataset visibility:**
- Only datasets above the visibility cutoff in the ranking list are rendered in the stratigraphy diagram.
- Hidden datasets' contacts are not drawn; their between-spaces are merged with adjacent gaps.

### Normalization / Distortion Mode

**Undistorted mode (default):**
- The vertical axis maps linearly from `AxisMin` to `AxisMax` (the full z-range across all datasets).
- Faithful to true geometry.

**Normalized mode:**
- Per-column normalization: in each angular column, the local minimum z across all contacts maps to the bottom of the rectangle, and the local maximum maps to the top.
- If the range (max - min) in a column is below a threshold (e.g., 1% of the global range), clamp to a minimum range centered on the data to avoid extreme stretching artifacts.
- This amplifies local differences and utilizes the full vertical extent of the diagram everywhere.
- Between-space thicknesses are distorted accordingly — thick gaps become more visible where the overall range is small.

**Future option (noted, not implemented):** Distortion that straightens the average line (compute per-column average z, then subtract it from all values, centering the diagram on 0). This would show deviations from the mean rather than absolute positions.

### Hover Interaction

- Mouse position on the stratigraphy diagram maps to (angle, z).
- Determine which between-space the cursor is in: find the two consecutive contact events that bracket the cursor's z-position in that angular column.
- Highlight that between-space (and optionally adjacent columns with the same bounding pair) in the diagram.
- Set `BetweenSpaceHover` in the model, which triggers the 3D highlight (see below).
- Show a tooltip: "Between [Dataset A] and [Dataset B], gap: X.Xm".

---

## 3D Scene Changes

### Cut Plane Controls in 3D

**AcrossAxis slider (side mode) — 3D widget along cylinder axis:**

- Render a thin cylinder ("rail") along the extrusion axis, extending the full length of the ScanPin prism. Use a visually distinct color (e.g., white or bright accent).
- On the rail, render a small box or disc ("handle") at the current AcrossAxis distance from the anchor point.
- **Interaction:** Click-drag the handle along the rail to change the `AcrossAxis distanceFromAnchor` value. The handle is constrained to the rail.
- **Picking:** The handle is a pickable object. `STUB(graphics)`: picking on the handle vs. the scene must be disambiguated (handle takes priority when visible).
- **Rendering:** Render the rail and handle **without depth test** (always visible, even through meshes) for the currently edited ScanPin only. Non-edited pins do not show the slider widget.

**AlongAxis control (top mode) — click on cylinder cap:**

- The circular caps (top and bottom) of the ScanPin cylinder are pickable surfaces.
- **Interaction:** The user clicks a point on the cap. The angular position of the click relative to the cylinder center defines the AlongAxis angle. The cut plane rotates to pass through the clicked point and the cylinder axis.
- **Rendering:** Render the cap as a translucent disc (subtle, not visually heavy). The current cut plane angle is shown as a line across the cap diameter.
- `STUB(graphics)`: Picking on the cap surface, computing the angle from the pick point.

**Both controls coexist.** The cut plane mode toggle (AlongAxis / AcrossAxis) determines which control is active. The existing slider in the HTML panel is **removed** — the 3D widgets replace it.

### Ghost Clipping Cylinder

When `GhostClip = GhostClipOn` for the currently edited pin:

- All mesh geometry **outside** the ScanPin cylinder is rendered with the ghost shader (existing transparency/desaturation effect).
- All mesh geometry **inside** the cylinder is rendered normally (full color, full opacity).
- The clipping test in the shader:
  ```
  // Pseudo-code for fragment shader test
  // Point p is the fragment's world position
  // cylinderAnchor, cylinderAxis (unit), cylinderRadius, extentForward, extentBackward
  
  float axisProj = dot(p - cylinderAnchor, cylinderAxis)
  float radialDist = length((p - cylinderAnchor) - axisProj * cylinderAxis)
  
  bool insideCylinder =
      radialDist <= cylinderRadius
      && axisProj >= -extentBackward
      && axisProj <= extentForward
  
  // If insideCylinder: render normally
  // If !insideCylinder: render with ghost shader
  ```
- `STUB(graphics)`: Modify the existing ghost/clipping plane shader to support a cylinder clipping volume. This could be a second shader variant or a uniform-controlled branch in the existing shader. The cylinder parameters are passed as uniforms.
- Only one pin's ghost clip is active at a time (the currently edited/selected pin).
- Togglable per pin via the `GhostClip` field and the checkbox in the detail panel.

### Extracted Lines in 3D

Two sets of lines, each independently togglable per pin:

**Cut plane intersection lines (`ShowCutPlaneLines`):**
- The polylines from the cut plane intersection with each dataset mesh (these already exist as `CutResults` from V1).
- Render as colored line strips (one color per dataset) on the cut plane.
- **Depth test disabled:** lines are always visible, even through meshes. This ensures the user can see the profile even when meshes overlap.
- Line width: 2–3 pixels (or whatever looks clear at typical zoom levels).

**Cylinder edge curves (`ShowCylinderEdgeLines`):**
- The intersection curves where each dataset mesh meets the cylinder's side surface.
- These are the same data as the stratigraphy diagram's contact stripes, but rendered in their true 3D positions on the cylinder wall.
- Render as colored line strips (same dataset colors).
- **Depth test disabled.**
- `STUB(graphics)`: Computing the 3D polylines from the stratigraphy data. For each dataset, connect the intersection points across adjacent angular columns to form a continuous curve on the cylinder surface. Multiple curves per dataset are possible (folds).

### Explosion View (Experimental)

When `Explosion.Enabled = true`:

- Inside the ScanPin cylinder, each dataset's mesh geometry is displaced along the extrusion axis.
- **Ordering:** Datasets are sorted by their average z-position within the cylinder (computed from `DatasetCoreSampleStats.ZMedian` or from the stratigraphy data). Lowest average z = index 0.
- **Displacement:** Dataset at index `i` is displaced by `i * ExpansionFactor * baseSpacing` along the extrusion axis, where `baseSpacing` is a spacing unit derived from the cylinder extent (e.g., `(extentForward + extentBackward) / N`).
- **Slider:** `ExpansionFactor` goes from 0.0 (no explosion) to a reasonable max (e.g., 3.0). Controlled by a slider in the detail panel.
- **Rendering:** The displacement is applied as a vertex shader offset for geometry inside the cylinder. `STUB(graphics)`: Implement as a per-mesh translation along the extrusion axis, applied only to fragments that pass the cylinder clipping test. This could be a uniform per draw call (each mesh knows its explosion index).
- **Scope:** Prototype quality. No picking, no interaction with exploded geometry. Just visual inspection.
- The stratigraphy diagram does NOT reflect the explosion (it always shows true z-values). The explosion is purely a 3D visualization aid.
- The cylinder edge lines and cut plane lines should also be displaced to match the exploded mesh positions.

### Between-Space 3D Highlight (Experimental)

When `BetweenSpaceHover` is active (the user is hovering a between-space in the stratigraphy diagram):

- In the 3D scene, highlight the volumetric gap between the two bounding datasets inside the ScanPin cylinder.
- **Computation:**
  1. From the hover point (angle, z), define a neighborhood (e.g., ±15° angle, ±some z range).
  2. `STUB(server)`: Query both bounding meshes with rays in this neighborhood. For each ray, get the z-values of the upper and lower bounding surfaces.
  3. Triangulate a patch of quads between the two surfaces: for each pair of adjacent sample points, create two triangles connecting the lower surface point to the upper surface point.
  4. Render this patch as a translucent colored volume (e.g., semi-transparent warm color).
- **Rendering:** The highlight patch is rendered with alpha blending, depth test enabled (so it sits correctly between the meshes). `STUB(graphics)`.
- **Scope:** Prototype quality. The neighborhood is range-limited. The highlight may have visual artifacts at the edges of the sampled region — this is acceptable for evaluation.
- **Linked highlighting:** When the 3D highlight is active, the corresponding between-space in the stratigraphy diagram is also highlighted (this is the reverse direction — ensured by the shared `BetweenSpaceHover` state).

---

## Messages (V3 Additions)

```fsharp
type ScanPinMessageV3 =
    // ... all V1 messages (placement, footprint, cut plane, commit, delete, focus) ...

    // Stratigraphy
    | StratigraphyComputed of ScanPinId * StratigraphyData
    | SetStratigraphyDisplay of StratigraphyDisplayMode

    // 3D cut plane controls
    | DragAcrossAxisHandle of float       // new distance from anchor (from 3D slider drag)
    | ClickCylinderCap of V3d             // click position on cap (compute angle from this)

    // Toggles
    | SetGhostClip of ScanPinId * GhostClipMode
    | SetShowCutPlaneLines of ScanPinId * bool
    | SetShowCylinderEdgeLines of ScanPinId * bool

    // Explosion
    | SetExplosionEnabled of ScanPinId * bool
    | SetExplosionFactor of ScanPinId * float

    // Between-space hover
    | HoverBetweenSpace of ScanPinId * angle:float * zLower:float * zUpper:float * DatasetId * DatasetId
    | ClearBetweenSpaceHover
    | BetweenSpaceHighlightComputed of ScanPinId * (V3d * V3d) list  // triangulation data

    // Dataset ranking (carried over from V2)
    | ReorderDatasets of DatasetId list
    | SetVisibleCount of int
```

---

## Removed Controls

The following V2 controls are **removed** in V3:

- Side View / Top View toggle buttons (no sub-viewport).
- Summary Meshes / Filtered Meshes toggle buttons (summary meshes are no longer directly rendered; the stratigraphy diagram replaces this).
- All sub-viewport camera controls (axis rotation, axis pan, zoom).
- The cut plane slider in the HTML panel (replaced by 3D widgets).

---

## Implementation Priority

### Phase 0: Cleanup (do first)
1. Simplify PinDemo ranking view (remove boxplots, show variance as text).
2. Merge PinDemo → Superprojekt.
3. Git: commit, push to `scanpin-v2`, switch to `master`.

### Phase 1: Core stratigraphy diagram
4. **Stratigraphy data model** (new F# types).
5. **Stratigraphy computation stub** — define the server API contract for z-aligned ray queries on the cylinder surface. Implement a mock/placeholder that returns synthetic data for testing.
6. **Stratigraphy diagram renderer** — OpenGL quad rendering to offscreen framebuffer, embedded in the HTML detail panel. Mesh contact stripes + neutral alternating between-space fills.
7. **Normalization toggle** — undistorted and normalized display modes.

### Phase 2: 3D scene enhancements
8. **AcrossAxis 3D slider widget** — rail + handle along cylinder axis, picking, drag interaction, no depth test.
9. **AlongAxis cap-click control** — pickable cylinder caps, angle from click position.
10. **Ghost clipping cylinder shader** — modify existing ghost shader for cylinder clipping volume.
11. **Extracted lines in 3D** — cut plane intersection lines and cylinder edge curves, depth test disabled.

### Phase 3: Experimental features
12. **Explosion view** — per-mesh displacement along axis, slider control.
13. **Between-space hover** in stratigraphy diagram — identify bounding datasets, highlight in diagram, tooltip.
14. **Between-space 3D highlight** — server query for neighborhood, triangulate gap patch, translucent 3D rendering.

### Phase 4: Polish
15. Remove V2 sub-viewport code and related GUI.
16. Update serialization to include V3 fields.
17. Tune visual parameters (stripe thickness, neutral fill colors, ghost clip appearance, line widths).

Items 0–7 are the **minimum viable V3**. Items 8–11 complete the 3D integration. Items 12–14 are experimental features for evaluation.

---

## Phase 4+ Notes (Do Not Implement)

**Embedded thumbnails for non-selected pins:** Each committed pin renders a small static snapshot of its stratigraphy diagram as a billboard in the 3D scene. Not interactive — clicking selects the pin. Implementation: render stratigraphy to offscreen buffer, capture as texture, display on billboard quad. Update on data change.

**Average-line distortion:** A third stratigraphy display mode where the per-column average z is subtracted from all values, centering the diagram on zero. Shows deviations from the mean rather than absolute positions.

**Connected between-space region finding:** Instead of highlighting a fixed neighborhood around the hover point, flood-fill to find the full connected region where the same two datasets bound a gap. More expensive but gives a complete picture of the gap extent.

**Stratigraphy animation:** Animate the stratigraphy diagram over time (for temporally ordered datasets), showing the surface evolution as a moving front. Would require defining a temporal ordering on the datasets.
