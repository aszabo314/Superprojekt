# ScanPin V2 — Core Sample Detail Panel Specification

## Purpose

This document specifies the V2 iteration of the ScanPin detail panel. V1 delivered the placement state machine, cut plane definition, profile diagram, and billboard rendering. V2 replaces the profile diagram with a richer **core sample inspector**: a small orthographic 3D view embedded in the HTML overlay panel, with two viewing modes, aggregation techniques for large N, and user-controlled filtering.

The target reader is a Claude Code AI agent. Low-level graphics operations are marked as `STUB(graphics)` — the human developer will implement these. Server-side computation is marked as `STUB(server)`.

---

## V1 Recap (What Exists)

- Placement state machine (Idle → DefiningFootprint → DefiningCutPlane → Adjusting → Committed).
- Circle and polygon footprint modes.
- AlongAxis and AcrossAxis cut plane modes with slider control.
- Elevation profile sparkline stack diagram (2D).
- Billboard rendering (HTML overlay, one selected pin at a time).
- 2D GUI panel with pin list, focus/delete, placement controls.
- Serialization stub.

---

## V2 Scope

### In Scope
1. Core sample 3D inspector (orthographic, embedded in the detail panel).
2. Side view mode (axis-only rotation, pan along axis).
3. Top view mode (plan view looking down the extrusion axis).
4. Statistical summary meshes (average, upper quartile Q3, lower quartile Q1) for both views.
5. Boxplot distribution display per dataset with drag-to-rank and top-K filtering.
6. Both aggregation techniques (summary meshes and boxplot filtering) toggleable in side view.

### Not In Scope (Noted for Future)
- Embedded thumbnails for non-selected pins (see Phase 3 Notes at end).
- Core sample as a separable, independently orbitable 3D object.
- AcrossAxis mode enhancements.

---

## Data Model Additions

```fsharp
/// Per-dataset statistics within a core sample, computed on a regular grid.
/// STUB(server): requires sampling each mesh at grid points along the extrusion axis.
type DatasetCoreSampleStats = {
    DatasetId : DatasetId
    /// Distribution of z-values (projected onto extrusion axis) within the core.
    ZMin : float
    ZQ1 : float        // 25th percentile
    ZMedian : float
    ZQ3 : float        // 75th percentile
    ZMax : float
    ZVariance : float   // used for initial interest ranking
}

/// A mesh surface sampled on a regular 2D grid perpendicular to the extrusion axis.
/// Each grid point stores a height value (distance along extrusion axis).
/// STUB(server): the grid query function that samples mesh heights.
type GridSampledSurface = {
    /// Grid origin in the plane perpendicular to extrusion axis,
    /// in core-local coordinates (anchor point = origin).
    GridOrigin : V2d
    /// Grid cell size (uniform in both directions).
    CellSize : float
    /// Grid dimensions.
    ResolutionU : int
    ResolutionV : int
    /// Height values, row-major. NaN for cells outside the footprint or with no data.
    Heights : float[]
}

/// Statistical summary meshes derived from all datasets within the core sample.
/// STUB(server): compute by sampling all N dataset meshes on the same grid,
/// then taking per-cell statistics across the N values.
type SummaryMeshes = {
    /// Per-cell average across all N datasets.
    Average : GridSampledSurface
    /// Per-cell 25th percentile (lower quartile).
    Q1 : GridSampledSurface
    /// Per-cell 75th percentile (upper quartile).
    Q3 : GridSampledSurface
    /// Per-cell variance across all N datasets.
    /// Used for false-color heatmap in top view.
    Variance : GridSampledSurface
}

/// Which datasets are currently visible in the core sample inspector.
type CoreSampleFilter = {
    /// Ordered list of dataset IDs, ranked by interest (first = most interesting).
    /// Initial order: descending by ZVariance.
    RankedDatasets : DatasetId list
    /// How many of the top-ranked datasets to render as actual meshes.
    VisibleCount : int  // default: 5
}

/// The viewing mode of the core sample inspector.
type CoreSampleViewMode =
    | SideView
    | TopView

/// Which aggregation technique is active in the inspector.
type AggregationMode =
    /// Render summary meshes (average, Q1, Q3).
    | SummaryMeshMode
    /// Render top-K individual meshes based on boxplot ranking.
    | FilteredMeshMode

/// Camera state for the core sample inspector (orthographic, constrained).
type CoreSampleCamera = {
    /// Rotation angle around the extrusion axis (degrees, 0-360).
    /// Only meaningful in SideView — controls which "side" of the core you see.
    AxisRotation : float
    /// Pan offset along the extrusion axis (for scrolling up/down the core in side view).
    AxisPanOffset : float
    /// Zoom level (orthographic scale factor).
    OrthoScale : float
}

/// Extended ScanPin state for V2.
type ScanPinV2 = {
    // ... all V1 fields ...

    /// Per-dataset statistics within this core sample.
    /// STUB(server): computed when prism is defined or datasets change.
    DatasetStats : Map<DatasetId, DatasetCoreSampleStats>

    /// Aggregated summary meshes for this core sample.
    /// STUB(server): computed from all datasets on a shared grid.
    SummaryMeshes : SummaryMeshes option

    /// Current filter/ranking state.
    Filter : CoreSampleFilter

    /// Current inspector view mode.
    ViewMode : CoreSampleViewMode

    /// Current aggregation technique.
    AggregationMode : AggregationMode

    /// Camera state for the core sample inspector.
    InspectorCamera : CoreSampleCamera
}
```

### Grid Sampling (STUB)

The core computation for both summary meshes and per-dataset stats requires a **grid query function**:

```fsharp
/// Sample a mesh's height at a regular grid of points within the core sample footprint.
/// The grid lies in the plane perpendicular to the extrusion axis.
/// For each grid point, cast a ray along the extrusion axis and record the
/// intersection height (distance from anchor along axis).
///
/// STUB(server): implement as a server-side function.
/// Input: mesh reference, prism definition (anchor, axis, footprint), grid resolution.
/// Output: GridSampledSurface.
val sampleMeshOnGrid : meshId:DatasetId -> prism:SelectionPrism -> resolution:int -> Async<GridSampledSurface>

/// Compute summary meshes from N individual grid samples.
/// For each grid cell, compute mean, Q1, Q3, variance across the N height values.
/// Cells where fewer than 2 datasets have data are set to NaN.
///
/// STUB(server): straightforward per-cell statistics once individual grids are computed.
val computeSummaryMeshes : grids:GridSampledSurface list -> SummaryMeshes

/// Compute per-dataset core sample statistics (for boxplot display).
/// For each dataset's grid, compute min, Q1, median, Q3, max, variance of all non-NaN heights.
///
/// Can be computed client-side from the GridSampledSurface data.
val computeDatasetStats : grid:GridSampledSurface -> datasetId:DatasetId -> DatasetCoreSampleStats
```

---

## Core Sample Inspector — Layout

The detail panel is an HTML overlay (as in V1) displayed next to the selected pin. The panel has this structure:

```
┌─────────────────────────────────────────┐
│ Pin Title / ID                    [X]   │
├─────────────────────────────────────────┤
│                                         │
│   ┌─────────────────────────────────┐   │
│   │                                 │   │
│   │   Orthographic 3D Viewport      │   │
│   │   (core sample rendering)       │   │
│   │                                 │   │
│   └─────────────────────────────────┘   │
│                                         │
│   [Side View]  [Top View]               │
│   [Summary Meshes]  [Filtered Meshes]   │
│                                         │
├─────────────────────────────────────────┤
│ Cut Plane Controls                      │
│   Mode: [AlongAxis ▼]  Slider: [═══●]  │
├─────────────────────────────────────────┤
│ Dataset Ranking  (if FilteredMeshMode)  │
│   ┌───────────────────────────────┐     │
│   │ ▐██▌  Dataset 2024-03  σ=4.2 │ ☰   │
│   │ ▐█▌   Dataset 2024-01  σ=3.8 │ ☰   │
│   │ ▐█▌   Dataset 2023-11  σ=3.1 │ ☰   │
│   │ ▐▌    Dataset 2023-09  σ=2.4 │ ☰   │  <- scrollable
│   │ ▐▌    Dataset 2023-07  σ=1.9 │ ☰   │
│   │ ─ ─ ─ ─ ─ visibility cutoff ─│     │
│   │ ▐▌    Dataset 2023-05  σ=1.2 │ ☰   │
│   │ ▐▌    Dataset 2023-03  σ=0.8 │ ☰   │
│   │ ...                          │     │
│   └───────────────────────────────┘     │
│   Showing top [5 ▼] of 20 datasets     │
└─────────────────────────────────────────┘
```

**Key layout decisions:**
- The 3D viewport is an aardvark sub-viewport embedded in the HTML panel (or rendered to a texture and displayed as an `<img>` — whichever is more practical in aardvark.dom). `STUB(graphics)`: the human developer decides the embedding approach.
- The dataset ranking list is a **scrollable div** with a fixed height (roughly 5–6 visible rows). Each row is **drag-reorderable** (drag handle icon ☰ on the right).
- A horizontal dashed line or visual separator in the ranking list marks the visibility cutoff (datasets above the line are rendered, below are hidden).
- A dropdown below the list lets the user change the `VisibleCount` (e.g., 3, 5, 8, All).

---

## Core Sample Inspector — Side View

### Camera

- **Projection:** Orthographic.
- **View direction:** Perpendicular to the extrusion axis, at the angle set by `AxisRotation`.
- **Up vector:** The extrusion axis direction.
- **Interaction:**
  - **Left-drag horizontally:** Rotates `AxisRotation` (spin the core sample around its axis). This does NOT change the cut plane slider — the cut plane slider remains an independent control.
  - **Left-drag vertically (or scroll):** Pans `AxisPanOffset` along the extrusion axis (scroll up/down the core).
  - **Scroll wheel (with modifier, e.g., Ctrl+scroll):** Zoom (`OrthoScale`).
- **Framing:** The orthographic frustum is sized to fill the viewport with the core sample's footprint width and a reasonable vertical extent. Allow slight aspect distortion (stretch to fill) rather than letterboxing.

### Rendering — Summary Mesh Mode

When `AggregationMode = SummaryMeshMode`:

- Render three surfaces within the core sample prism:
  - **Average mesh:** Solid, neutral color (e.g., medium gray).
  - **Q3 mesh (upper quartile):** Translucent, warm color (e.g., semi-transparent orange).
  - **Q1 mesh (lower quartile):** Translucent, cool color (e.g., semi-transparent blue).
- The space between Q1 and Q3 represents the interquartile range — the "bulk" of the data.
- The cut plane is rendered as a thin line or translucent slab overlaid on the side view.
- `STUB(graphics)`: Render the grid-sampled surfaces as triangle meshes (standard heightfield-to-mesh conversion). Transparency compositing for Q1/Q3 over the average.

### Rendering — Filtered Mesh Mode

When `AggregationMode = FilteredMeshMode`:

- Render the top K meshes (those above the visibility cutoff in the ranking list).
- Each mesh is rendered with its assigned dataset color.
- Meshes are clipped to the core sample prism boundary.
- `STUB(graphics)`: Clipping individual meshes to the prism. Could use stencil buffer or geometry clipping.
- The cut plane is rendered as in Summary Mesh Mode.

### Boxplot Display

Regardless of aggregation mode, a **horizontal boxplot strip** is shown for each dataset in the ranking list:

- The boxplot is rendered inline in the ranking list row, to the left of the dataset label.
- It shows: whiskers at ZMin/ZMax, box from ZQ1 to ZQ3, median line at ZMedian.
- The horizontal scale is shared across all boxplots (so they are visually comparable).
- The boxplot gives the user an at-a-glance sense of each dataset's elevation distribution within the core, enabling informed ranking decisions.

---

## Core Sample Inspector — Top View

### Camera

- **Projection:** Orthographic.
- **View direction:** Along the extrusion axis (looking "down" into the core).
- **Up vector:** Derived from the initial camera orientation at pin creation (or a default world-up projected into the perpendicular plane).
- **Interaction:**
  - **Left-drag:** Rotates the AlongAxis cut plane preview (a line through the center of the footprint, showing where the cut plane would intersect).
  - **Scroll:** Zoom.
  - No pan in top view (the footprint should always be centered and visible).
- **Framing:** The orthographic frustum fits the footprint polygon with a small margin.

### Rendering — Summary Mesh Mode

- Render the **Q3 mesh** (upper quartile) as a solid surface, viewed from above. This is the surface the user sees when looking down.
- Map the **Variance** grid onto this surface as a **false-color heatmap** (low variance = cool/blue, high variance = warm/red). This tells the user "where the datasets disagree most."
- The cut plane is rendered as a **line** across the top view (the intersection of the AlongAxis plane with the footprint polygon). The user can see where the line passes and whether it crosses high-variance regions.
- `STUB(graphics)`: Rendering a heightfield mesh with a secondary attribute (variance) mapped to color. Standard vertex-color or texture-based approach.

**Alternative rendering options (noted for experimentation, not mandatory in V2):**
- Isolines of the variance field overlaid on the Q3 surface.
- Transparent rendering of Q3 with Q1 visible underneath (ghosted).
- These are left to the implementer's judgment; the variance heatmap on Q3 is the primary specification.

### Rendering — Filtered Mesh Mode

- Render the top K meshes as **wireframe or transparent overlays**, viewed from above.
- For small K (≤ 3): transparent solid fill with colored outlines.
- For larger K: wireframe only to reduce visual clutter.
- The cut plane line is overlaid as in Summary Mesh Mode.
- `STUB(graphics)`: Wireframe rendering of meshes clipped to the prism, viewed orthographically.

---

## Messages (V2 Additions)

```fsharp
type ScanPinMessageV2 =
    // ... all V1 messages ...

    // Inspector view
    | SetViewMode of CoreSampleViewMode
    | SetAggregationMode of AggregationMode

    // Inspector camera
    | SetAxisRotation of float          // degrees, from horizontal drag in side view
    | SetAxisPanOffset of float         // from vertical drag / scroll in side view
    | SetInspectorZoom of float         // from Ctrl+scroll

    // Dataset ranking
    | ReorderDatasets of DatasetId list  // new ordering from drag-drop
    | SetVisibleCount of int            // dropdown: how many to show

    // Computation results (async from server stubs)
    | DatasetStatsComputed of ScanPinId * Map<DatasetId, DatasetCoreSampleStats>
    | SummaryMeshesComputed of ScanPinId * SummaryMeshes
```

---

## Interaction Summary

### Side View Workflow

1. User selects a committed pin → detail panel opens with side view (default).
2. The core sample is rendered orthographically, showing either summary meshes or filtered individual meshes.
3. User drags horizontally to rotate the core, getting different perspectives.
4. User adjusts the cut plane slider (existing V1 control) to position the cutting plane.
5. The cut plane is visible as a line/slab in the side view.
6. If in Filtered Mesh Mode, user reviews boxplots in the ranking list, drag-reorders to prioritize interesting datasets, adjusts visibility count.
7. User switches to Summary Mesh Mode to see the aggregate view (average ± quartiles).

### Top View Workflow

1. User switches to top view via the mode toggle.
2. The core sample is rendered from above, showing the Q3 surface with variance heatmap (summary mode) or wireframe/transparent overlays (filtered mode).
3. User sees where the high-variance regions are and adjusts the AlongAxis cut plane to pass through them.
4. User switches back to side view to inspect the resulting profile.

### Switching Between Modes

- **Side View ↔ Top View:** Toggle buttons. Camera state is independent per mode (switching preserves each mode's camera).
- **Summary Meshes ↔ Filtered Meshes:** Toggle buttons. Both modes use the same camera state. The boxplot ranking list is only shown in Filtered Mesh Mode but the ranking state is preserved when switching.

---

## V1 Controls That Remain Unchanged

- Cut plane mode toggle (AlongAxis / AcrossAxis) and slider — unchanged, still in the detail panel.
- Pin list with focus/delete — unchanged.
- Placement state machine — unchanged (V2 only affects the detail panel of committed pins).
- Billboard positioning — unchanged.
- Serialization — extend to include new fields (ViewMode, AggregationMode, Filter ranking, InspectorCamera).

---

## Implementation Priority

1. **Data model additions** (the F# types above).
2. **Core sample inspector viewport** — orthographic rendering of the prism contents embedded in the detail panel. `STUB(graphics)`: viewport embedding approach.
3. **Side view camera** — axis-only rotation (horizontal drag), axis pan (vertical drag), zoom.
4. **Filtered Mesh Mode in side view** — render top-K clipped meshes with dataset colors.
5. **Boxplot computation and display** — per-dataset stats, inline boxplots in ranking list.
6. **Drag-reorder ranking list** — scrollable div with drag handles, visibility cutoff line, count dropdown.
7. **Summary Mesh Mode in side view** — average/Q1/Q3 rendering. `STUB(server)`: grid sampling and statistics.
8. **Top view camera** — look-down-axis, cut plane line overlay.
9. **Top view Summary Mesh Mode** — Q3 surface with variance heatmap. `STUB(graphics)`: false-color heightfield.
10. **Top view Filtered Mesh Mode** — wireframe/transparent overlays.

Items 1–6 constitute a **minimum viable V2**. Items 7–10 add the aggregation and top view capabilities.

---

## Phase 3 Notes (Do Not Implement)

**Embedded thumbnails for non-selected pins:**
Each committed pin renders a small static thumbnail of its profile diagram (or a miniaturized snapshot of the core sample side view). This thumbnail is displayed as a small billboard in the 3D scene near the pin, always visible. It is not interactive — clicking it selects the pin and opens the full detail panel. Implementation approach: render the inspector viewport to an offscreen framebuffer, capture as a texture, display on a billboard quad. Update the thumbnail when cut results change. This provides visual presence for 5–8 pins simultaneously without overlapping interactive panels.
