# ScanPin — Placement Modes Specification

## Purpose

Redesign ScanPin placement around three distinct modes that close the gap between explore mode discovery and precise pin configuration. Each mode uses a different gesture optimized for its use case. The old center+radius+length placement is replaced entirely.

The target reader is a Claude Code AI agent. Graphics stubs are marked `STUB(graphics)`.

---

## Overview: Three Placement Modes

| Mode | Gesture | Creates | Use Case |
|---|---|---|---|
| **Profile** (vertical cut) | Click two points on the surface | Pin with vertical cut plane along the two-point line | Elevation profiles, cross-sections through ridges/walls |
| **Plan** (horizontal cut) | Lasso-circle an area | Pin with horizontal cut plane at median terrain height | Floor plans, horizontal slices through structures |
| **Auto** (explore-derived) | Single click on an explore-mode hot spot | Pin with axis, cut plane, and size derived from the heatmap | Fastest path from discovery to characterization |

All three modes produce the same ScanPin data structure. They differ only in how the initial parameters are determined. After placement, the pin enters placement mode and all parameters are adjustable before commit.

---

## Mode Selection

The top bar shows a segmented group of three side-by-side buttons that share a single bounding box with no gaps between them — visually one control with three mutually-exclusive options:

```
┌─────────┬──────┬──────┐
│ Profile │ Plan │ Auto │
└─────────┴──────┴──────┘
```

- Clicking a mode button enters that placement mode (replacing any in-progress placement).
- While placement is active in a mode, that mode's button is highlighted (active/blue). Clicking the highlighted button cancels placement.
- Clicking a different mode while placing switches to that mode (the old pin is discarded).

**Auto mode** is only available (not grayed out) when explore mode is active. If explore mode is off, the Auto button is disabled with a tooltip: "From explore hot-spot (enable explore mode first)."

---

## Profile Mode (Vertical Cut — Two-Point Placement)

### Gesture

1. User selects Profile mode and clicks [+ Pin] (or the mode is already active).
2. **First click** on a mesh surface: places the first point. A marker appears at the click position. The camera does NOT move yet.
3. **Second click** on a mesh surface: places the second point. A preview line is drawn between the two points during mouse movement before the second click.
4. On second click: the ScanPin is created and enters placement mode.

### Parameter Derivation

From the two picked points P1 and P2:

```
center = midpoint(P1, P2)
pointDistance = length(P2 - P1)
cutDirection = normalize(P2 - P1)   // horizontal direction of the cut line

// Cylinder axis: vertical (world Z) by default.
axis = V3d(0, 0, 1)

// Cut plane: vertical plane containing both points and the Z axis.
// AlongAxis mode, angle set so the cut plane normal is perpendicular to cutDirection.
cutPlaneAngle = atan2(cutDirection.Y, cutDirection.X)

// Cylinder radius: enough to contain both points plus margin.
radius = pointDistance * 0.6   // 20% margin on each side

// Cylinder length: auto-derived from dataset depth at center.
// Start slightly above the higher of the two points.
// Extend downward to cover all datasets.
aboveAnchor = 1.0   // 1 meter above the higher picked point
belowAnchor = autoDepth(center)   // from dataset bounding box
```

### Camera Behavior

On pin creation (after second click): camera orbit center moves to `center`, camera zooms to frame the cylinder.

### Preview During Placement

Between first and second click:
- A line is drawn from P1 to the current mouse position on the mesh surface.
- A ghost cylinder outline is shown along this line (transparent, no fill — just the wireframe silhouette) to preview the resulting pin size and position.

### Adjustable Parameters in Flyout

After creation, the placement flyout shows:

```
┌── Placing Profile Pin ────────────┐
│ Radius  [═══●═══] 15.2m          │
│ Depth   [═══●═══] 40.0m          │
│ [■] Ghost Clip                    │
│         [✓ Commit]  [✕ Discard]   │
└───────────────────────────────────┘
```

- **Radius:** Adjustable from the auto-derived value.
- **Depth:** The `belowAnchor` length. Adjustable.
- **Cut plane orientation** is fixed to vertical (AlongAxis) for Profile pins. Not shown as a toggle — it's implicit in the mode choice.
- The cut plane angle can still be fine-tuned by click-dragging on the cylinder hull or the stratigraphy diagram.

---

## Plan Mode (Horizontal Cut — Lasso Placement)

### Gesture

1. User selects Plan mode and clicks [+ Pin].
2. **Click-drag on the mesh surface** to draw a circle (simplified lasso). The circle is defined by center (mouse-down point) and radius (drag distance). A preview circle is shown during drag.
3. On mouse release: the ScanPin is created and enters placement mode.

**Why circle, not freeform lasso:** A freeform lasso requires polygon-to-cylinder conversion and is harder to implement. A circle directly maps to a cylindrical footprint. If freeform lasso is needed later, it can be added as a variant — the data model supports arbitrary polygon footprints already.

### Parameter Derivation

From the drag gesture (center C, drag radius R):

```
center = C   // the mouse-down point on the mesh surface
radius = R   // drag distance projected onto the surface plane

// Cylinder axis: vertical (world Z).
axis = V3d(0, 0, 1)

// Cut plane: horizontal (AcrossAxis mode).
// Height set to the median terrain elevation inside the circle.
cutPlaneHeight = medianElevation(center, radius)

// Cylinder length: cover all datasets vertically.
aboveAnchor = 1.0
belowAnchor = autoDepth(center)
```

### Median Elevation Computation

```fsharp
/// Compute the median elevation across all visible meshes inside the circle.
/// Sample a grid of vertical rays inside the circle.
/// Collect all intersection z-values across all meshes.
/// Return the median.
///
/// STUB(server): uses existing ray query infrastructure.
/// Can be approximate — sample e.g. a 10x10 grid of rays inside the circle.
val medianElevation : center:V3d -> radius:float -> float
```

### Camera Behavior

On pin creation: camera orbit center moves to `center`, camera zooms to frame the cylinder from above (or from the current angle if the user is already looking at the area).

### Preview During Placement

During the drag:
- A circle outline is drawn on the mesh surface, growing with the drag distance.
- A ghost cylinder wireframe extends vertically from the circle to preview the volume.

### Adjustable Parameters in Flyout

```
┌── Placing Plan Pin ───────────────┐
│ Radius  [═══●═══] 25.0m          │
│ Depth   [═══●═══] 40.0m          │
│ [■] Ghost Clip                    │
│         [✓ Commit]  [✕ Discard]   │
└───────────────────────────────────┘
```

- **Radius:** Adjustable from the drag-derived value.
- **Depth:** Adjustable.
- **Cut plane orientation** is fixed to horizontal (AcrossAxis) for Plan pins. Not shown as a toggle.
- The cut plane height can be fine-tuned by click-dragging on the cylinder hull or the stratigraphy diagram.

---

## Auto Mode (Explore-Derived — Single-Click Placement)

### Prerequisite

Explore mode must be active. Auto mode is disabled (grayed out) when explore mode is off.

### Gesture

1. User selects Auto mode and clicks [+ Pin].
2. **Mouse hover over the 3D scene:** A ghost preview of the suggested pin is shown in real-time (see Ghost Preview below).
3. **Single click on a mesh surface:** The ScanPin is created with auto-derived parameters and enters placement mode.

### Parameter Derivation

On click at screen position S, with the explore heatmap and G-buffer available:

#### Step 1: Sample the Neighborhood

Read back from GPU textures in a neighborhood around S (e.g., 20×20 pixel region):
- **Heatmap intensity** (from the explore mode output): which pixels are hot.
- **Surface normals** (from the normal G-buffer array): the normals of the steep+disagreeing faces.
- **Depth values** (from the depth array): for 3D position reconstruction.

`STUB(graphics)`: GPU readback of a small pixel region from the heatmap, normal array, and depth array textures. This should be fast for a small region (400 pixels × N layers). Can be done asynchronously and cached on mouse move.

#### Step 2: Compute Dominant Face Direction

From the sampled normals of hot pixels (those above the heatmap intensity threshold):

```fsharp
/// Average the normals of hot pixels to get the dominant face direction.
/// Weight each normal by its heatmap intensity.
let hotNormals = neighborhood
    |> filter (fun p -> p.heatmapIntensity > threshold)
    |> map (fun p -> p.normal * p.heatmapIntensity)
let N = normalize(sum(hotNormals))
```

N is the direction the interesting faces are pointing. If no hot pixels exist in the neighborhood, fall back to world-Z axis and vertical cut plane (same as Profile mode default).

#### Step 3: Derive Cylinder Axis

The axis should be perpendicular to N (so the cut plane can slice across the interesting faces) and as close to vertical as possible:

```fsharp
let Z = V3d(0, 0, 1)
let axis =
    let projected = Z - V3d.Dot(Z, N) * N   // project Z onto plane perpendicular to N
    if projected.Length < 0.01 then
        // N is nearly vertical — interesting faces are nearly horizontal.
        // Fall back: axis = world Z, use AcrossAxis mode.
        Z
    else
        projected.Normalized
```

**Special case:** If N is nearly vertical (faces are nearly horizontal, like a flat roof or terrain surface), the interesting cut is horizontal (AcrossAxis), not vertical. In this case, axis = Z and mode = AcrossAxis. Otherwise, mode = AlongAxis.

#### Step 4: Derive Cut Plane

**AlongAxis case** (N mostly horizontal — steep faces):
```fsharp
// The cut plane normal should align with N.
// The AlongAxis angle is the rotation that aligns the cut plane normal with N.
let cutPlaneAngle = atan2(N.Y, N.X)   // angle of N projected onto the XY plane
```

**AcrossAxis case** (N mostly vertical — flat faces):
```fsharp
// Cut height = elevation of the click point.
let cutPlaneHeight = clickPoint.Z
```

#### Step 5: Derive Size

```fsharp
// Radius: span of the hot region in the neighborhood.
let hotPositions = hotPixels |> map worldPosition
let hotExtent = boundingBoxDiagonal(hotPositions) / 2.0
let radius = max(hotExtent, minimumRadius)   // ensure a usable minimum

// Length: auto-derived from dataset depth.
let aboveAnchor = 1.0
let belowAnchor = autoDepth(clickPoint)
```

#### Summary

| Parameter | Source |
|---|---|
| Center | Click point on mesh surface |
| Axis direction | `normalize(Z - dot(Z, N) * N)` where N = dominant hot-pixel normal |
| Cut plane mode | AlongAxis if N mostly horizontal, AcrossAxis if N mostly vertical |
| Cut plane angle/height | From N direction (AlongAxis) or click elevation (AcrossAxis) |
| Radius | Extent of the hot region in the neighborhood |
| Length | Auto-derived from dataset bounding box |

### Ghost Preview (Hover)

While in Auto mode and hovering over the scene, show a lightweight preview of what the auto-suggest would produce:

**Preview elements (rendered as ghosts — transparent, no depth test):**
- A cylinder wireframe outline at the suggested position, size, and orientation.
- A cut plane quad inside the cylinder at the suggested angle/height.
- An axis line showing the suggested cylinder axis direction.

**Update frequency:** The preview updates on mouse move, but the GPU readback and computation are debounced (update every ~100ms or when the cursor has moved more than 5px). The preview fades/disappears when the cursor is over a cold region (no significant heatmap signal).

**Visual style:** All preview elements are rendered in a single ghost color (e.g., white at 30% opacity, or the explore mode highlight color at low opacity). Thin lines only — no filled surfaces. The preview should feel like a suggestion, not a committed object.

`STUB(graphics)`: Rendering the ghost preview requires drawing a cylinder wireframe + plane quad at arbitrary position/orientation/size each frame. This is lightweight geometry — a few line strips.

### Camera Behavior

On pin creation: camera orbit center moves to the pin center. Camera does NOT change its angle or zoom — the user was already looking at the area they clicked.

### Adjustable Parameters in Flyout

```
┌── Placing Auto Pin ───────────────┐
│ Radius  [═══●═══] 18.3m          │
│ Depth   [═══●═══] 35.0m          │
│ [■] Ghost Clip                    │
│         [✓ Commit]  [✕ Discard]   │
└───────────────────────────────────┘
```

Same as the other modes. The cut plane mode and angle were set by auto-derivation but can be adjusted on the cylinder hull / stratigraphy diagram. The axis direction can be adjusted with the existing arcball gizmo (from V1).

---

## Interaction Flow Comparison

### Profile Mode
```
Click [+ Profile] → click P1 → move mouse (preview line + ghost) → click P2 → pin created → adjust → commit
```

### Plan Mode
```
Click [+ Plan] → click-drag circle (preview) → release → pin created → adjust → commit
```

### Auto Mode
```
Enable Explore → click [+ Auto] → move mouse (ghost preview follows) → click hot spot → pin created → adjust → commit
```

---

## Data Model Changes

```fsharp
/// The placement mode for ScanPin creation.
type PlacementMode =
    | ProfileMode    // vertical cut, two-point
    | PlanMode       // horizontal cut, lasso-circle
    | AutoMode       // explore-derived, single click

/// Sub-states for Profile mode placement (two-click gesture).
type ProfilePlacementState =
    | WaitingForFirstPoint
    | WaitingForSecondPoint of firstPoint:V3d
    // After second click: pin is created, enters normal Adjusting state.

/// Sub-states for Plan mode placement (click-drag gesture).
type PlanPlacementState =
    | WaitingForDrag
    | Dragging of center:V3d * currentRadius:float
    // After release: pin is created, enters normal Adjusting state.

/// Sub-states for Auto mode placement (hover preview + single click).
type AutoPlacementState =
    | Hovering of preview:AutoPreview option
    // After click: pin is created, enters normal Adjusting state.

/// The auto-derived preview shown during hover in Auto mode.
type AutoPreview = {
    Center : V3d
    Axis : V3d
    Radius : float
    CutPlaneMode : CutPlaneMode
    /// The dominant face normal used to derive the axis and cut plane.
    DominantNormal : V3d
}

/// Updated PlacementState (replaces the V1 state machine).
type PlacementState =
    | Idle
    | ProfilePlacement of ProfilePlacementState
    | PlanPlacement of PlanPlacementState
    | AutoPlacement of AutoPlacementState
    | Adjusting of ScanPinId    // pin created, user adjusting before commit
```

## Messages

```fsharp
type PlacementMessage =
    | SelectPlacementMode of PlacementMode
    | CancelPlacement

    // Profile mode
    | ProfileClickFirst of V3d
    | ProfileClickSecond of V3d
    | ProfilePreviewUpdate of mouseWorldPos:V3d   // for live preview line

    // Plan mode
    | PlanDragStart of V3d
    | PlanDragUpdate of currentRadius:float
    | PlanDragEnd

    // Auto mode
    | AutoHoverUpdate of AutoPreview option   // from debounced GPU readback
    | AutoClick of V3d                         // create pin with current preview params

    // Shared (after pin creation in any mode)
    | CommitPin
    | DiscardPin
```

---

## Implementation Priority

### Near-term scope (this milestone)

1. **Data model migration** — replace `PlacingMode : FootprintMode option` + `ActivePlacement : ScanPinId option` with the single `PlacementState` DU described above. Cascade through `Update.fs`, `View.fs`, `Gui.fs`, `SceneGraph.fs`, `Cards.fs`. **[done]**
2. **Segmented mode-selector UI** in the top bar: three side-by-side buttons (Profile / Plan / Auto) sharing one bounding box with no inter-button gaps — visually one control with three exclusive options. The active mode is highlighted; clicking it cancels, clicking another switches. Auto is disabled (grayed + tooltip) when explore mode is off. **[done]**
3. **Profile mode** (two-point vertical cut) — full gesture, parameter derivation, preview line + ghost cylinder wireframe between clicks, camera orbit center update on creation. **[done]**
4. **`/api/query/ray-batch` server endpoint** — batched ray-intersection (origin+direction array, names array). Server uses `Parallel.For` across rays. **[done]**
5. **Plan mode** (lasso-circle horizontal cut) — full gesture, median-elevation computation via the new batch endpoint (10×10 vertical ray grid), preview circle + ghost cylinder during drag. **[done]**
6. **Normal G-buffer** in the mesh off-screen pipeline. Add a second render target (Rgba16f `Texture2DArray`) that receives world-space normals from `BlitShader.clippy`. This is the prerequisite for Auto mode and is useful independently for lighting / shading tweaks. **[done]** — `BlitShader.clippy` now returns `ClippyFragment { c; n }`, `MeshView.buildMeshTextures` creates a `normalTex` Texture2DArray (Rgba16f) bound to `DefaultSemantic.Normals` on each per-slice FBO, and exposes it as part of the return tuple (`count, colorTex, normalTex, depthTex, meshIndices`).
7. **Remove old placement** (single-click centered + length/radius slider as the primary input). Sliders remain in the placement flyout for fine adjustment only. **[done]** — the `PlacingMode : FootprintMode option` + `ActivePlacement : ScanPinId option` pair was replaced by the single `PlacementState` DU during task 1, which deleted the old click-to-anchor handler in the same edit. The placement flyout retains Radius + Length sliders under a "Placing Pin" heading; they apply to the pin currently in the `AdjustingPin` sub-state and are no longer the primary shape input (gestures set radius/axis/cut-plane at creation). Cut-plane toggles are hidden for Profile/Plan (which lock the plane orientation at gesture time) and visible only for Auto.

### Future work (deferred)

- **Auto mode gesture** — **[done, server-ray variant]**. The spec's original algorithm (GPU readback of heatmap + normal + depth textures) would need WebGL readback plumbing that Aardvark's web backend doesn't expose cleanly. Instead, the implementation uses a server-side ray grid:
  - New server endpoint `/api/query/ray-grid` (Handlers.fs `rayGridHandler`) returns per-ray hit point + world-space surface normal from Embree. Binary response format: `int32 rayCount | per-ray (byte hitFlag | float64 hitX hitY hitZ | float32 nX nY nZ)`.
  - On Auto click, `View.fs` builds a 5×5 world-space ray grid (±2 m transverse to the camera view, all rays from the eye toward the neighborhood around the click) and emits `AutoClick(renderPos, rays, refAxisWorld)`. The update handler creates the pin with placeholder params immediately, then kicks off `Query.rayGrid` in a background task.
  - Derivation weights each hit by `steepness × exp(-d²/25)` where steepness = `max(0, 1 − |dot(n, refAxis)|/0.9)`. Dominant `N` is the weighted average (with normals flipped to the reference-axis hemisphere). If `|dot(N, refAxis)| > 0.95`, axis = refAxis and cut = `AcrossAxis 0`; otherwise axis = `normalize(refAxis − N·dot(refAxis,N))` and cut = `AlongAxis atan2(N·fwd, N·right)` (in the axis frame). Radius = max transverse distance of hot hits from the click point.
  - Result travels back as `AutoDerivationComplete(id, axis, cutPlane, radius)` which updates the placement-phase pin, triggering the normal cut-plane refresh pipeline.
  - Fallback: if fewer than 3 hot hits, the placeholder defaults (world-Z axis, vertical cut, 1 m radius) persist.
- **Auto ghost preview on hover** — debounced (~100ms / 5px) hover preview of the suggested cylinder + axis line + cut-plane quad. Still deferred. Now cheaply feasible via the same `/api/query/ray-grid` endpoint debounced on pointer move; rendering would reuse `buildPrismWireframe` from SceneGraph.fs with translucent material.
