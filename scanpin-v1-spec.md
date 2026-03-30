# ScanPin V1 — Interaction Specification

## Purpose of This Document

This is a specification for implementing the first version of the ScanPin interaction in an existing aardvark.dom application. The target reader is a Claude Code AI agent. The document describes the interaction design, data model, and rendering requirements. Low-level graphics operations (picking, shaders, mesh intersection) are marked as `TODO(graphics)` — the human developer will fill these in with existing functions.

The codebase uses **aardvark.dom** (F#, Elm-style architecture: immutable model, message-based updates, incremental scenegraph rendering). See https://github.com/aardvark-community/aardvark.dom/blob/master/src/Demo/Program.fs for the programming style.

---

## Context: What Already Exists

The application is an interactive 3D viewer for geodetic meshes with the following capabilities:

- **Two datasets loaded:**
  - A construction site (airborne LiDAR) at **3 time steps**
  - A glacier (airborne photogrammetry) at **9 time steps**
- **Existing visualization techniques:**
  - Mesh toggling (show/hide individual time steps)
  - Transparency rendering (including ghost rendering of deactivated meshes)
  - Clipping planes
  - False-color difference rendering (pairwise, between two selected meshes)
- **Existing infrastructure (available for reuse):**
  - 3D surface picking (raycast from screen point → surface hit with position, normal, mesh ID)
  - Camera controller (orbit, pan, zoom) with serializable camera state
  - Depth-composited rendering of multiple overlapping meshes

---

## What ScanPin Is

A **ScanPin** is a persistent 3D annotation anchored to a surface location. It defines a volumetric selection (a prism) through all loaded datasets at that location, plus a cutting plane within that prism. The cutting plane intersects each dataset's mesh, producing a set of polylines (elevation profiles) or closed contours (floor plan outlines). These are rendered as a 2D diagram attached to the pin in the 3D scene.

The analogy is a **geological core sample**: the user punches a tube through the terrain at a chosen angle, then slices the core to inspect the cross-section of all datasets at once.

### What ScanPin Is Not (V1 Scope)

- Not a real-time exploration lens (deferred — requires cheap preview computation)
- Not an alignment tool (Phase 2 — architecture should accommodate, but no implementation)
- Not a comparison/aggregation dashboard (Phase 3 — same)

---

## Data Model (F# Types)

These types define the Elm-style model for the ScanPin system. Types marked `// TODO(graphics)` contain fields whose computation requires low-level graphics code that the human developer will supply.

```fsharp
/// Unique identifier for a ScanPin instance.
type ScanPinId = ScanPinId of System.Guid

/// Unique identifier for a loaded dataset (mesh + time step).
type DatasetId = DatasetId of string

/// A 2D polygon defined by ordered vertices in a local coordinate frame
/// perpendicular to the extrusion axis.
type FootprintPolygon = {
    /// Vertices in local 2D coordinates (relative to the anchor point,
    /// projected onto the plane perpendicular to the extrusion axis).
    Vertices : V2d list
}

/// The volumetric selection prism that defines what a ScanPin captures.
type SelectionPrism = {
    /// The point on the mesh surface where the user clicked.
    AnchorPoint : V3d

    /// The extrusion axis direction (unit vector).
    /// Initialized from the camera view direction at time of first click.
    /// Adjustable via arcball gizmo.
    AxisDirection : V3d

    /// The footprint shape in the plane perpendicular to AxisDirection.
    /// Circle mode: represented as a regular polygon (e.g. 32-gon).
    /// Polygon mode: user-defined vertices.
    Footprint : FootprintPolygon

    /// How far the prism extends along +AxisDirection from AnchorPoint.
    /// Should be large enough to encompass all datasets.
    ExtentForward : float

    /// How far the prism extends along -AxisDirection from AnchorPoint.
    ExtentBackward : float
}

/// Defines how the cutting plane is oriented within the prism.
type CutPlaneMode =
    /// The cut plane contains the extrusion axis.
    /// The angle parameter rotates the plane around the axis.
    /// Useful for elevation profiles (vertical cuts through terrain).
    | AlongAxis of angleDegrees : float
    /// The cut plane is perpendicular to the extrusion axis.
    /// The distance parameter sets offset from the anchor point.
    /// Useful for floor plans (horizontal cuts through a building).
    | AcrossAxis of distanceFromAnchor : float

/// The result of intersecting the cut plane with a single dataset's mesh.
/// TODO(graphics): The computation of this type is a mesh-plane intersection.
type CutResult = {
    DatasetId : DatasetId
    /// For AlongAxis mode: a single polyline (elevation profile).
    /// For AcrossAxis mode: a set of closed contours (floor plan outlines).
    Polylines : V2d list list
}

/// The lifecycle of a ScanPin.
type PinPhase =
    /// Pin is being placed and adjusted. All parameters are editable.
    | Placement
    /// Pin is committed. Parameters are frozen. Diagram is persistent.
    | Committed

/// The complete state of a single ScanPin.
type ScanPin = {
    Id : ScanPinId
    Phase : PinPhase
    Prism : SelectionPrism
    CutPlane : CutPlaneMode
    /// The camera state at the time the pin was created.
    /// Used by the "focus" button to restore the creation viewpoint.
    CreationCameraState : CameraState
    /// Computed intersection results, one per visible dataset.
    /// Recomputed when prism, cut plane, or dataset visibility changes.
    /// TODO(graphics): mesh-plane intersection computation.
    CutResults : Map<DatasetId, CutResult>
    /// Color assigned to each dataset for the profile diagram.
    DatasetColors : Map<DatasetId, C4b>
}

/// The top-level ScanPin system state, to be embedded in the application model.
type ScanPinModel = {
    Pins : Map<ScanPinId, ScanPin>
    /// Which pin is currently being placed (at most one at a time).
    ActivePlacement : ScanPinId option
    /// Which pin's diagram is currently selected/focused in the 2D GUI.
    SelectedPin : ScanPinId option
    /// The interaction sub-state during placement.
    PlacementState : PlacementState
}

/// Sub-states of the placement interaction (state machine).
type PlacementState =
    /// Waiting for the user to click the first surface point.
    | Idle
    /// User has clicked the anchor; now defining the footprint.
    /// In circle mode: dragging sets the radius.
    /// In polygon mode: clicking adds vertices.
    | DefiningFootprint of {|
        AnchorPoint : V3d
        AxisDirection : V3d  // locked to camera view direction at first click
        CurrentVertices : V2d list
        FootprintMode : FootprintMode
    |}
    /// Footprint is defined; user is adjusting the cutting plane.
    | DefiningCutPlane of {|
        Prism : SelectionPrism
    |}
    /// All parameters set; user can still adjust via gizmos before committing.
    | Adjusting of {|
        PinId : ScanPinId
    |}

type FootprintMode =
    | CircleMode    // click-drag defines radius
    | PolygonMode   // click vertices, close on double-click or click near first vertex
```

---

## Interaction Flow

### Overview (State Machine)

```
Idle
  │
  ├── [Click on surface] ──► DefiningFootprint
  │                              │
  │   CircleMode:               │   PolygonMode:
  │   click-drag = radius       │   click = add vertex
  │   release = done            │   double-click or close = done
  │                              │
  │                    ◄─────────┘
  │
  ├── [Footprint complete] ──► DefiningCutPlane
  │                              │
  │   Toggle AlongAxis / AcrossAxis mode
  │   Slider sets angle (AlongAxis) or distance (AcrossAxis)
  │   Live preview of cut results in diagram
  │                              │
  ├── [Cut plane set] ──► Adjusting
  │                              │
  │   Arcball gizmo: adjust axis direction
  │   Scale slider: resize footprint proportionally
  │   Cut plane slider: refine angle/distance
  │   The diagram updates live during adjustment
  │                              │
  ├── [Commit button] ──► Pin becomes Committed, returns to Idle
  │
  └── [Cancel / Escape] ──► Discard, return to Idle
```

### Step 1: Anchor Placement

**Trigger:** User clicks on a visible mesh surface while in `Idle` state and a placement tool is active.

**Behavior:**
1. Raycast from mouse position through the scene. Hit the nearest visible surface (respecting depth compositing and transparency — opaque surfaces take priority, but transparent "ghost" meshes are also pickable).
2. Record the hit position as `AnchorPoint`.
3. Record the **camera's forward direction** (not the surface normal) as the initial `AxisDirection`. This is locked in for the duration of footprint definition.
4. Transition to `DefiningFootprint`.

**Rationale for camera-direction axis:** The user is looking at the scene from a chosen angle. The "what you see is what you get" principle means the core sample goes into the surface in the direction the user is looking. This is intuitive for both top-down terrain views (vertical core) and side views of walls (horizontal core).

### Step 2: Footprint Definition

Two modes, selectable before or during placement (e.g., a toolbar toggle).

**Circle mode (default):**
1. After the anchor click, the user **drags** outward from the anchor point.
2. The drag distance (projected onto the plane perpendicular to `AxisDirection`) defines the circle radius.
3. The circle is visualized in real-time as a projected outline on the mesh surface.
4. On mouse release, the footprint is set. Transition to `DefiningCutPlane`.

**Polygon mode:**
1. After the anchor click, the user clicks additional points on the mesh surface.
2. Each click is a `TODO(graphics)` pick operation. The picked 3D point is projected onto the plane perpendicular to `AxisDirection` passing through `AnchorPoint`, producing a 2D vertex.
3. The polygon outline is visualized in real-time, connecting vertices in order with a closing edge back to the first vertex shown as a dashed preview.
4. The polygon closes on **double-click** or when clicking within a snap threshold of the first vertex.
5. Transition to `DefiningCutPlane`.

**Constraints:**
- Minimum radius / minimum polygon area threshold to prevent degenerate selections.
- `ExtentForward` and `ExtentBackward` are computed automatically: project the bounding boxes of all loaded datasets onto the `AxisDirection` and extend the prism to cover all of them, plus a small margin.

### Step 3: Cut Plane Definition

**Trigger:** Footprint definition is complete. The prism is now visible in the 3D scene.

**Behavior:**
1. The default cut plane mode is `AlongAxis` with angle 0° (the cut plane contains the extrusion axis and is oriented toward the camera's right vector at time of anchor placement).
2. A **slider** (in the 2D GUI panel) controls the parameter:
   - `AlongAxis`: angle in degrees, 0°–360°, rotating the cut plane around the extrusion axis.
   - `AcrossAxis`: distance from anchor point, clamped to [`-ExtentBackward`, `ExtentForward`].
3. A **toggle button** switches between `AlongAxis` and `AcrossAxis` modes.
4. The cut plane is visualized in the 3D scene as a translucent quad clipped to the prism boundary.
5. The **profile diagram** appears and updates live as the slider moves (see Diagram Rendering below).
6. Transition to `Adjusting` is immediate — the user can already interact with gizmos during this step.

### Step 4: Adjustment

While in `Adjusting` state, the user can modify any parameter before committing:

**Arcball gizmo (axis direction):**
- A sphere is rendered at the `AnchorPoint`, sized proportionally to the prism footprint.
- The user drags on the sphere surface to rotate the `AxisDirection`.
- The gizmo operates as a standard 3D arcball: mouse delta on the sphere surface maps to a rotation applied to `AxisDirection`.
- `TODO(graphics)`: Picking on the arcball sphere vs. the mesh surface must be disambiguated (arcball takes priority when visible).

**Footprint scale slider:**
- A slider in the 2D GUI scales the footprint uniformly around the anchor point.
- For circle mode, this is equivalent to adjusting the radius.
- For polygon mode, all vertices are scaled relative to the anchor.

**Cut plane slider:**
- Same slider as in Step 3, remains active.

**Move along axis (deferred — note only):**
- Moving the anchor point along the extrusion axis could be useful but adds complexity. Defer to a later iteration unless trivial to implement.

**All adjustments trigger recomputation of `CutResults` and live update of the diagram.**

### Step 5: Commit

**Trigger:** User clicks a "Commit" button in the 2D GUI panel (or presses Enter).

**Behavior:**
1. The pin's `Phase` changes from `Placement` to `Committed`.
2. The `CreationCameraState` is recorded (the current camera state at commit time).
3. The arcball gizmo and adjustment sliders disappear.
4. The pin becomes a persistent annotation in the scene.
5. `PlacementState` returns to `Idle`. The user can place another pin.

### Cancel

**Trigger:** User presses Escape or clicks a "Cancel" button.

**Behavior:**
1. The in-progress pin is discarded (removed from `Pins` map).
2. `PlacementState` returns to `Idle`.

---

## Diagram Rendering

The diagram is the core analytical output of a ScanPin. It renders the cut plane's intersection with all datasets as a 2D graphic.

### Elevation Profile Diagram (AlongAxis mode)

**Coordinate system:**
- Horizontal axis: distance along the cut line (the intersection of the cut plane with the plane perpendicular to the extrusion axis, measured from the anchor point).
- Vertical axis: distance along the extrusion axis from the anchor point (i.e., depth/elevation).

**Visual encoding:**
- Each dataset is rendered as a colored polyline.
- Line colors are assigned per dataset (the `DatasetColors` map); colors should be visually distinct and consistent across all pins.
- Lines are rendered as a **sparkline stack**: all datasets overlaid in a single plot area.
- A legend maps color to dataset name/timestamp.
- For N ≤ 5: overlaid lines with moderate line thickness.
- For N > 5: thinner lines, optionally with hover-highlight to isolate individual datasets.

**Interactivity (V1, minimal):**
- Hover on a dataset line to highlight it and show its name/timestamp.
- `TODO(graphics)`: If a dataset is toggled off in the main viewer, its profile line should be dimmed or hidden in the diagram (follow the same visibility state).

### Floor Plan Diagram (AcrossAxis mode)

**Coordinate system:**
- Both axes are spatial coordinates in the cut plane (the plane perpendicular to the extrusion axis at the specified distance).

**Visual encoding:**
- Each dataset is rendered as a set of colored closed contours (outlines).
- Same color scheme as elevation profile mode.
- For N ≤ 3: overlaid contours are readable.
- For N > 5: this mode becomes cluttered. Show a warning in the GUI: "Floor plan mode works best with ≤ 3 datasets. Consider using elevation profile mode or filtering datasets."

**Interactivity (V1, minimal):**
- Same hover-highlight as elevation profile mode.

### Diagram Computation

`TODO(graphics)`: The intersection of a plane with a triangle mesh produces a set of line segments. These must be:
1. Computed for each visible dataset's mesh.
2. Connected into polylines (for AlongAxis) or closed contours (for AcrossAxis).
3. Projected into the diagram's 2D coordinate system.

This is a standard mesh-plane intersection algorithm. The human developer will supply this as a function with signature approximately:

```fsharp
/// Intersect a mesh with a plane. Returns a list of polylines
/// in the plane's local 2D coordinate system.
/// TODO(graphics): implement using existing mesh intersection utilities.
val intersectMeshWithPlane : mesh:SomeMeshType -> plane:Plane3d -> V2d list list
```

---

## 3D Rendering of ScanPins

### Prism Rendering

- The selection prism is rendered as a **wireframe** outline (edges of the polygonal footprint, extruded along the axis).
- During `Placement` phase: bright outline color (e.g., yellow) with the cut plane shown as a translucent quad.
- During `Committed` phase: subdued outline color (e.g., gray or the pin's assigned color).
- `TODO(graphics)`: The prism wireframe should be rendered with depth test against the scene but with a slight depth bias to prevent z-fighting with mesh surfaces.

### Core Sample Rendering (Deferred — Note Only)

A possible future enhancement: render the mesh geometry inside the prism as an isolated "core sample" that can be inspected separately, rotated independently from the main scene. This is the full geological core sample analogy. **Not in V1** — the profile diagram serves the analytical purpose.

### Diagram Billboard

The profile diagram is rendered as a **billboard** (a screen-facing quad) positioned in the 3D scene near the pin.

**Positioning:**
- The billboard is placed at a point offset from the `AnchorPoint` along the extrusion axis (default: slightly above/in front of the surface).
- The user can **drag the billboard along the extrusion axis** to reposition it (click-drag on the billboard's title bar or border).

**Orientation:**
- The billboard always faces the camera (billboard constraint: rotation around the vertical axis or full camera-facing, whichever looks better — try both).
- `TODO(graphics)`: The billboard needs a "depth cheat" — it should not be occluded by geometry immediately adjacent to the pin, but should still be occluded by geometry that is clearly in front of it from the camera's perspective. One approach: render the billboard with a small depth offset (polygon offset / depth bias) so it "floats" slightly in front of nearby geometry. This is an empirical tuning parameter.

**Size:**
- The billboard has a fixed screen-space size (does not shrink with distance).
- Below a configurable camera distance threshold, the billboard is not rendered; only a pin-head glyph is shown (see below).

### Pin-Head Glyph

When the billboard is hidden (camera too far, or too many pins on screen), a small **pin-head glyph** marks the pin's location:
- A simple 3D marker at the `AnchorPoint` (e.g., a small sphere or disc).
- Colored with the pin's assigned color.
- Always visible (no distance culling, but may be very small at distance).
- Clicking the glyph focuses the camera on the pin (restores `CreationCameraState`).

**Note:** The detailed design of the glyph is left open for now. Start by rendering all billboards always and iterate on the glyph representation once we see how the scene looks with multiple pins.

---

## 2D GUI Panel

A side panel (or overlay) provides controls for the ScanPin system. This is standard aardvark.dom UI (HTML/DOM-based GUI alongside the 3D viewport).

### Pin List

A scrollable list of all pins, ordered by creation time:
- Each entry shows: pin name/ID, a color swatch, a small thumbnail of the diagram.
- **Focus button:** Restores the camera to `CreationCameraState` for that pin.
- **Delete button:** Removes the pin (with confirmation if committed).

### Active Placement Controls

Visible only during `Placement` phase:

- **Footprint mode toggle:** Circle / Polygon.
- **Cut plane mode toggle:** AlongAxis (elevation profile) / AcrossAxis (floor plan).
- **Cut plane slider:** Angle (0°–360°) for AlongAxis, or distance for AcrossAxis.
- **Footprint scale slider:** Uniform scale factor for the footprint.
- **Commit button:** Freezes the pin.
- **Cancel button:** Discards the in-progress pin.

### Dataset Visibility

The existing dataset toggling UI remains unchanged. When a dataset is toggled off:
- Its mesh is rendered as a transparent ghost (existing behavior).
- Its profile line in all ScanPin diagrams is dimmed (reduced opacity) but not removed.

---

## Messages (Elm-Style Update)

```fsharp
type ScanPinMessage =
    // Placement initiation
    | StartPlacement of FootprintMode
    | CancelPlacement

    // Footprint definition
    | SetAnchor of point:V3d * cameraForward:V3d
    | AddFootprintVertex of V3d         // polygon mode: add vertex
    | SetFootprintRadius of float       // circle mode: set radius from drag distance
    | CloseFootprint                    // polygon mode: finish polygon

    // Cut plane
    | SetCutPlaneMode of CutPlaneMode
    | SetCutPlaneAngle of float         // AlongAxis: degrees
    | SetCutPlaneDistance of float       // AcrossAxis: distance from anchor

    // Adjustment
    | SetAxisDirection of V3d           // from arcball gizmo
    | SetFootprintScale of float        // uniform scale slider
    | MoveBillboardAlongAxis of float   // drag billboard: new offset along axis

    // Lifecycle
    | CommitPin
    | DeletePin of ScanPinId
    | FocusPin of ScanPinId             // restore camera to creation state

    // Recomputation trigger
    | RecomputeCutResults of ScanPinId  // triggered when prism, cut plane, or dataset visibility changes
    | CutResultsComputed of ScanPinId * Map<DatasetId, CutResult>  // async result callback
```

---

## Serialization (Persistence)

For V1, implement only the **serialization function** (model → JSON). Deserialization and actual file I/O are deferred.

The serialized representation of a pin must include:
- `Id`
- `Phase` (should always be `Committed` when serializing; `Placement` pins are not persisted)
- `Prism` (anchor point, axis direction, footprint vertices, extents)
- `CutPlane` (mode + parameter value)
- `CreationCameraState`
- `DatasetColors`
- The **identifiers** of all datasets the pin references (the `DatasetId` values), so that when datasets change, the system knows which pins are affected.

`CutResults` are **not** serialized — they are recomputed on load from the prism/cut-plane definition and the currently loaded datasets.

```fsharp
/// Produce a JSON-serializable representation of a committed ScanPin.
/// TODO: implement actual serialization; for now, this is the contract.
val serializePin : ScanPin -> ScanPinJson

/// Produce a JSON-serializable representation of all committed pins.
val serializeAllPins : ScanPinModel -> ScanPinJson list
```

---

## Phase 2/3 Notes (Do Not Implement)

These are future extensions. The V1 architecture should not prevent them, but no implementation is needed now.

**Phase 2 — Point Association for Alignment:**
The user places multiple ScanPins and selects corresponding points in each pin's diagram across multiple datasets. These point correspondences can be used to compute improved alignment (registration) between datasets. This requires: (a) a point-picking interaction in the diagram, (b) a correspondence data structure linking points across pins and datasets, (c) an export format for the correspondences.

**Phase 3 — Derived Comparison Diagrams:**
The user selects multiple committed ScanPins and creates a derived visualization that compares them: e.g., boxplots of elevation differences per pin, parallel coordinates showing how a measurement varies across pins and datasets. This requires: (a) a multi-pin selection UI, (b) a derived diagram renderer, (c) aggregation logic over cut results.

**Phase 3 — Filtering / Level-of-Detail for Many Pins:**
When too many pins are on screen, the system should reduce visual clutter: show only the N most relevant pins as full billboards, reduce the rest to pin-head glyphs. Relevance could be based on: proximity to camera, recency, user-defined priority, or a relevance metric derived from the cut results (e.g., pins with large inter-dataset differences are more interesting).

---

## Implementation Priority

1. **Data model types** (the F# types above).
2. **Placement state machine** (Idle → DefiningFootprint → DefiningCutPlane → Adjusting → Committed).
3. **Circle-mode footprint** (simplest path to a working pin).
4. **AlongAxis cut plane** with slider.
5. **Elevation profile diagram** (sparkline stack of polylines, 2D rendering).
6. **Billboard rendering** in 3D (camera-facing, depth-cheated).
7. **2D GUI panel** (pin list, controls, commit/cancel).
8. **Arcball gizmo** for axis adjustment.
9. **Polygon-mode footprint**.
10. **AcrossAxis cut plane** (floor plan mode).
11. **Serialization function**.
12. **Billboard drag-along-axis** interaction.
13. **Pin-head glyph** and distance-based LOD.

Items 1–7 constitute a **minimum viable ScanPin**. Items 8–13 are important but can follow.
