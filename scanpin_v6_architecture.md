# ScanPin V6 — Software Architecture Specification

**Audience:** Claude Code, working on the existing ScanPin codebase.
**Purpose:** Single source of truth for the V6 system. All feature-level Claude Code prompts should reference this document for context.
**Baseline:** The V5 codebase (matching `description.md`, the prior application description).
**Goal:** Migrate the codebase from V5 (inspection prototype) to V6 (registration-focused visual analytics system).
**Related artifacts:** `scanpin_extended_abstract.md` (the paper story), `scanpin_paper/` (the LaTeX paper prototype).

---

## Part A — Project Overview

ScanPin is a research prototype for **visual analytics on mesh ensembles in unconstrained outdoor environments**, with co-registration as the primary task. It is a single-page web application running in the browser on desktop and mobile. The implementation language is F# with the aardvark.dom toolkit; the UI is reactive and produces a 3D scene plus 2D floating diagram cards plus side and top panels.

### V6 in one paragraph

V6 keeps V5's scene infrastructure (mesh listing, visibility/solo/focus, ghost silhouettes, scaling, clipping, sphere query, the side-panel/top-bar/floating-card pattern) and adds a registration-aware annotation primitive — the **Anchor Sphere** — with three exchangeable payloads (point, line-on-surface, unwrapped 2D patch), a **dual-signal Explore mode** (feature confidence + disagreement), a **panorama split view** (Photo / Render / Blend), a **registration solver integration** with weighted least-squares from point pairs and region-restricted ICP, a **three-source error provenance** decomposition with per-pin, global, and hover visualizations, a **per-pixel fusion mesh** rendered on GPU with a winner-ID buffer for pickability, and a **retarget workflow** for incoming repeat-scan data. V5's cut-plane workflow, stratigraphy diagram, between-space volume, core sample inspector, revolver disk, and Profile/Plan/Auto placement modes are removed.

### Non-goals for V6

The following are explicitly **out of scope** for V6 and should not be implemented:

- Decoupled rotation/translation solves (single rigid transform per mesh only).
- Live "what-if" preview of registration when anchors are dragged.
- Point-cloud-only datasets (mesh datasets only — point clouds may be loaded as visual references but cannot host payloads or correspondences).
- Top-down core sample mode.
- Summary mesh generation (the V5 placeholder).
- End-to-end report exporter.
- Stratigraphy diagram in any form.
- Continuous between-space hover/volume.

If the agent encounters work that would advance one of these non-goals, stop and ask the user for explicit redirection.

### Tech stack and project layout assumptions

The agent should:

1. Inspect the existing project structure on first contact.
2. Identify the F# project files (`.fsproj`), the aardvark.dom entry points, and the existing module boundaries.
3. Map the V5 features below to the existing modules **before** beginning deletion work.

Where this document refers to "the X module" or "the Y file", treat that as a structural cue, not a path. The agent's first task is to confirm the actual file/module names in the existing codebase and document the mapping in a small internal cheat-sheet before refactoring.

---

## Part B — Migration from V5: Explicit Deletion List

The following V5 features are **removed in V6**. Each entry specifies what to delete, what to do with the orphaned UI affordances, and how to test that nothing downstream broke.

### B.1 Cut plane as a placement-driving primitive — REMOVE

In V5, every ScanPin had a cylindrical column with a cut plane that sliced through it. The cut plane was placed via Profile / Plan / Auto modes. **In V6 the cut plane no longer exists as a primitive.** Its role is replaced by the **Line-on-surface payload** (§D.7.2), which provides 1D structure on the surface itself rather than a planar slice through space.

Delete:
- The cut-plane geometry and rendering.
- The direct-3D cut-plane manipulation (grab and rotate/slide).
- The cut-plane mode switch (vertical/horizontal).
- The "cut diagram" SVG cross-section view *in its V5 form*. The 2D floating diagram pattern survives (it's used by all three V6 payloads), but the specific V5 cut-diagram-of-meshes-intersected-by-plane logic is gone.

### B.2 Stratigraphy diagram (cylindrical unwrap) — REMOVE

Including the flat/normalized toggle, the between-space hover, the bracket-pair labels, the continuous gap volume rendering in the 3D scene, and any storage/state that supports them. Delete completely.

### B.3 Core sample (mini 3D viewport inside pin card) — REMOVE

Delete the embedded mini-viewport, its rotation/zoom controls, the side/top toggle, and the prism wireframe overlay it carried.

### B.4 Profile / Plan / Auto placement modes — REMOVE

In V6, the only placement gestures are **single-click placement** and **lasso placement** (both described in §D.6). Delete:
- The three placement-mode buttons in the top bar.
- The two-click Profile placement workflow.
- The click-and-drag Plan workflow.
- The ray-grid probing logic of Auto and its ghost preview.

The "ghost preview during placement" pattern itself is useful and should be **preserved** for the single-click and lasso placement gestures — the user should see a translucent Anchor Sphere preview before commit.

### B.5 Revolver disk — REMOVE

Including the Shift-hold key binding, the latch-from-GUI option, and the cycle-mesh-order behaviour. In V6, the **mesh-wheel** interaction (§D.1) replaces it.

### B.6 Summary mesh generation — REMOVE

Delete the placeholder code and any UI affordances pointing to it.

### B.7 Pin "adjustment" flyout in its V5 form — REMOVE AND REPLACE

In V5, after placement, a flyout in the side panel offered Radius, Length-above/below, Cut-plane mode, Cut-plane slider, Ghost-clip Solo, Ghost-clip +Cut, Commit, and Discard controls. **The flyout pattern survives in V6** but the controls change. The new flyout (§D.6.4) exposes Radius, Gaussian σ, Payload type switcher, Payload-specific controls, Commit, and Discard. Length-above/below, Cut-plane mode, Cut-plane slider, and the Ghost-clip controls all go.

### B.8 Verification of deletion

After each deletion, run the existing app and confirm:

1. The dataset still loads.
2. The 3D scene still renders all meshes.
3. The Scene, Overlay, Clip, Pins tabs still render (with the relevant V5-only controls absent — those will be rebuilt in V6).
4. No references to deleted types appear in the build log.

If the deletion uncovers shared infrastructure that V6 still needs (e.g. the ghost-clip helper logic might be useful for §D.6.4's clip controls), preserve it as a separate utility before deleting the V5 caller.

---

## Part C — V6 Data Model

The agent should implement the following types. Names are suggestive; match the existing codebase's conventions where they exist.

### C.1 Mesh

```
type Mesh = {
  Id              : MeshId               // stable identifier across loads
  Name            : string
  Vertices        : Vec3[]               // world-space positions
  Triangles       : (int*int*int)[]
  Normals         : Vec3[] option        // computed if missing
  Colour          : Colour option        // per-vertex or per-mesh
  Texture         : TextureRef option
  Curvature       : float[] option       // per-vertex curvature scalar; lazily computed
  DatasetError    : float[] option       // per-vertex dataset error (metres);
                                          // see §D.9 for fallbacks
  Transform       : Mat44 ref            // mesh-to-world; mutated by registration
  CoordinateFrame : CoordinateFrameId    // initially independent; converges through registration
  SensorType      : SensorType           // for fallback dataset-error values
}
```

### C.2 Anchor Sphere

```
type AnchorSphere = {
  Id                : AnchorId
  Centre            : Vec3               // world-space
  Radius            : float              // metres
  Sigma             : float              // Gaussian σ for falloff, σ ≤ Radius
  Payload           : PayloadType
  CorrespondenceLink: CorrespondenceLinkId option
                                          // shared identifier with anchors marking
                                          // the same feature across other meshes
  HostMeshId        : MeshId             // mesh the anchor is anchored to
  CreatedAt         : DateTime
}
```

### C.3 Payload types

```
type PayloadType =
  | Point of PointPayload
  | Line of LinePayload
  | Patch of PatchPayload

and PointPayload = {
  // No additional fields - the sphere alone is the payload.
  // Used for region-of-interest definitions and error provenance hotspots.
  ReliabilityWeight : float              // [0,1], from local feature confidence at placement
}

and LinePayload = {
  Mode      : LineMode                   // ElevationIsoline of float | CurvatureRidge
  Points    : Vec3[]                     // polyline on mesh surface, world coords
  ScalarVals: float[]                    // per-point scalar (elevation or curvature) for axis labelling
}

and LineMode =
  | ElevationIsoline of elevation:float
  | CurvatureRidge

and PatchPayload = {
  CenterOnMesh   : Vec3
  Radius         : float                  // patch radius on the surface
  SourceMeshId   : MeshId                 // which mesh the patch is unwrapped from
                                          // (switchable via mini-control)
  ProjectedPoints: (Vec2 * Vec3)[]        // (2D patch coord, 3D world coord) pairs
  CompassNorth   : Vec2                   // direction vector in patch space pointing to project north
}
```

### C.4 Correspondence link

```
type CorrespondenceLink = {
  Id            : CorrespondenceLinkId
  AnchorIds     : Map<MeshId, AnchorId>   // one anchor per participating mesh
  PointPairs    : PointPair[]             // optional: explicit point pairs derived from anchors
  CreatedAt     : DateTime
}

and PointPair = {
  A            : Vec3                     // point on mesh A in mesh A's coord frame
  B            : Vec3                     // point on mesh B in mesh B's coord frame
  MeshAId      : MeshId
  MeshBId      : MeshId
  Weight       : float                    // [0,1] reliability from feature confidence
}
```

### C.5 Error provenance

```
type ErrorProvenance = {
  DatasetError    : float                 // metres; per-point
  AlgorithmResidual: float                // metres; per-point, interpolated from
                                          //   correspondence residuals with Gaussian falloff
  LocalConditioning: float                // unitless; high = ill-determined
  DominantSource  : ErrorSource           // for global heatmap colouring
}

and ErrorSource = Dataset | Algorithm | Conditioning
```

### C.6 Panorama

```
type Panorama = {
  Id          : PanoramaId
  Image       : Texture                   // equirectangular or cylindrical
  CameraPose  : CameraPose                // intrinsics + extrinsics in world coords
  Synthetic   : bool                      // true if rendered by ScanPin, false if real
  Source      : MeshId option             // for synthetic panoramas, the source mesh
}

and CameraPose = {
  Position    : Vec3
  Forward     : Vec3
  Up          : Vec3
  HFov        : float                     // horizontal field of view, radians
  VFov        : float                     // vertical field of view, radians
}
```

### C.7 Top-level workspace state

```
type Workspace = {
  Dataset       : Dataset
  Meshes        : Mesh[]
  Anchors       : AnchorSphere[]
  Correspondences: CorrespondenceLink[]
  Panoramas     : Panorama[]
  ExploreMode   : ExploreState
  Clip          : ClipState
  FusionMesh    : FusionMeshState
  Registration  : RegistrationState
  SelectedAnchor: AnchorId option
  ActivePickingLayer: MeshId option        // for mesh-wheel cycling
}
```

### C.8 Persistence

The workspace state above must be serialisable to JSON. **Persistence is required in V6** — V5's placeholder for deserialization must be fully implemented. The workspace can be saved to a JSON blob and loaded back into a fresh session, with mesh references rehydrated from the dataset server.

---

## Part D — V6 Feature Specifications

Each subsection has: **Purpose** (what and why), **UI** (where it lives and what the user sees), **Data** (which types are involved), **Algorithm** (the technical work), **Edge cases**, and **Acceptance criteria** (how to verify it works).

### D.1 Mesh-wheel

**Purpose.** Replaces the V5 revolver disk. Provides fast, predictable cycling of the **active picking layer** at the cursor when multiple meshes overlap. The active picking layer is the mesh that picking gestures (click, hover, lasso) interact with by default.

**UI.** Scroll wheel over the 3D viewport cycles the active picking layer through the visible meshes at the cursor (front to back). The picking layer is indicated by a small floating label near the cursor showing the active mesh name. On touch devices, a two-finger vertical swipe substitutes for scroll. Standard zoom (which currently uses scroll) must remain available — bind the scroll wheel to mesh-wheel **only when over the cursor's mesh stack**; over empty space, scroll continues to zoom. A modifier key (Alt) forces zoom regardless of position, as a fallback.

**Data.** `Workspace.ActivePickingLayer`. Changing it triggers no geometry update — only picking behaviour and the cursor-adjacent label change.

**Algorithm.**

```
on scroll over viewport at cursor (cx, cy):
  cast a ray from camera through (cx, cy)
  collect mesh intersections, sorted front-to-back: m1, m2, ..., mn
  if active picking layer == mi:
    set active picking layer = m_{(i mod n) + 1}
  else:
    set active picking layer = m1
```

**Edge cases.** Empty space under cursor → scroll falls back to zoom. Single mesh under cursor → mesh-wheel has nothing to do, defer to zoom. Active picking layer becomes invisible (user toggled visibility off) → reset to first visible mesh.

**Acceptance criteria.**

1. Scrolling over a dense overlap region cycles through layers without changing zoom.
2. Scrolling over empty space zooms as before.
3. Alt+scroll always zooms.
4. The cursor label updates within one frame of the cycle.

### D.2 Ghost silhouette enhancement

**Purpose.** V5 already supports faint translucent outlines of hidden or clipped meshes. The planetary expert specifically asked for these to convey **more terrain information** — surface curvature, ridges, depressions — rather than just a bounding silhouette.

**UI.** The Scene tab's ghost-silhouette control gains a **ghost detail** selector with three options:

- **Outline only** (the V5 behaviour).
- **+ Curvature** (overlays a faint colour-coded curvature gradient on the silhouette).
- **+ Terrain features** (additionally renders ridge and valley lines as thin curves on the silhouette).

**Data.** Mesh.Curvature must be populated for the curvature mode; mesh ridges/valleys are derived on-demand from curvature.

**Algorithm.** Outline is unchanged from V5. For curvature, sample mesh curvature into a low-resolution texture and blend it into the silhouette pass with low opacity. For terrain features, run a simple ridge extraction (local maxima of curvature along a tangent direction) and rasterise the resulting polylines onto the silhouette pass.

**Edge cases.** Mesh without curvature data → compute it lazily on first access. Performance: cache the curvature texture per mesh; invalidate on transform change only if curvature is computed in world space (use mesh-local curvature to avoid invalidation).

**Acceptance criteria.**

1. Three modes selectable; each visually distinct.
2. Performance impact under 5 ms per frame on the largest test dataset.
3. Curvature visible on the Mars test datasets where rock features exist.

### D.3 Polygonal lasso

**Purpose.** TO's step 7 in his manual workflow: cut everything away except a tight area of interest, then re-run the registration with high accuracy.

**UI.** A new entry in the Clip tab — **Lasso mode**. Activating it puts the user into a drawing gesture: each click adds a vertex to the lasso; double-click closes it; Escape cancels. Once closed, the lasso encloses a 2D screen-space region that is back-projected onto every visible mesh; geometry outside the swept volume is clipped.

The rectangular box clip from V5 **survives** alongside the lasso, as a reference implementation. Both modes can be active; if both are on, geometry must satisfy both.

**Data.** ClipState gains a `Lasso : Vec2[] option` (screen-space polygon) and a projection plane reference.

**Algorithm.**

```
on lasso commit (polygon P in screen space):
  for each mesh m:
    for each triangle t in m:
      project centroid of t to screen space (sx, sy)
      retain t if (sx, sy) inside P

    persist the filtered triangle indices as a clip mask
```

Use polygon-point inside-test (ray casting or winding number). Update the clip mask only on commit, not during draw, since redoing the projection per frame is expensive.

**Edge cases.** Lasso self-intersecting → reject and ask for a clean polygon. Empty selection → keep all meshes, warn. Camera moves after commit → keep the mask (the user is now looking at the clipped region from a new angle; that's expected). Provide a "Re-lasso from current view" affordance.

**Acceptance criteria.**

1. Lasso closes cleanly with double-click and Escape cancels.
2. Clipped region updates correctly on commit.
3. Rectangular box clip continues to work.
4. Both modes can be active simultaneously.

### D.4 Dual-signal Explore mode

**Purpose.** Find candidate regions for two distinct tasks: marker placement (high feature confidence) and registration diagnosis (high disagreement). V5 collapsed both into one "steep AND disagreeing" formula; V6 separates them.

**UI.** The Explore tuning card gains a **two-row toggle**:

- **Feature confidence** — checkbox + sensitivity slider + reference axis (world up / camera) + highlight colour.
- **Disagreement** — checkbox + sensitivity slider (real-world metres, log scale) + highlight colour.

When both are on, the user picks a **mix** mode: side-by-side (two heatmaps overlaid in distinct hues), blended (single heatmap of arithmetic mean), or alternating (flicker between the two on a slow cycle).

**Data.** ExploreState gains `FeatureConfidence : SignalState`, `Disagreement : SignalState`, `MixMode : MixMode`.

**Algorithm.**

For feature confidence, the per-pixel score at world point `p` and screen ray direction `d` is:

```
fc(p) = curvature(p) * steepness(p, axis)
```

where `curvature(p)` is sampled from the local mesh's curvature texture and `steepness(p, axis)` is the absolute dot product of the surface normal with the chosen reference axis.

For disagreement, retain the V5 formula:

```
disagreement(p) = depth(frontmost mesh, p) - depth(backmost mesh, p)
```

normalised to the user's sensitivity range.

Both are computed in a fragment shader pass over the visible meshes, rendered to off-screen targets, and composited into the main view.

**Edge cases.** Mesh without curvature → fall back to a fixed value (1.0) or lazily compute. Single mesh visible → disagreement is identically zero; the user should see no overlay rather than an error.

**Acceptance criteria.**

1. Each signal can be toggled independently.
2. The Mars Kodiak outcrop dataset shows clearly visible feature-confidence hotspots at rock corners.
3. Pre-registration, the disagreement signal highlights the mis-registered regions; post-registration, those regions are visibly thinner.

### D.5 Panorama split view

**Purpose.** Geologists prefer working from registered panorama imagery. Supports the M2 (panorama-pick) workflow and provides a Photo / Render / Blend disagreement detector at the image plane.

**UI.** A new docked panel that slides in from the right when at least one panorama exists in the active dataset. The panel contains:

- A **panorama selector dropdown** if more than one panorama is available.
- A **mode selector**: Photo / Render / Blend (with a slider for Blend).
- A **toggle** for showing anchor markers projected into panorama space (default off; user turns on for evaluation).
- The panorama image itself, displayed in cylindrical projection, with mouse/touch pan and zoom.
- A **synchronise camera** button that flies the 3D viewport to the panorama's exact pose.

**Data.** Workspace.Panoramas contains real and synthetic panoramas; the panel reads from this list and the camera-pose linkage is stored per panorama.

**Algorithm.**

Photo mode: render the stored panorama image directly into the panel.

Render mode: set up a virtual camera with the panorama's CameraPose, render the workspace's visible meshes into the panel's framebuffer.

Blend mode: render both into separate textures and composite with the user's slider value.

Anchor projection: for each anchor whose CorrespondenceLink intersects this panorama's scene, transform the anchor's centre into camera space, project to the panorama's image space, and draw an overlay marker.

Click-to-place: convert the click (sx, sy) into a ray in world space using the camera pose, intersect against meshes, and pass the resulting 3D point to the anchor placement pipeline.

**Edge cases.** Panorama camera pose drift relative to mesh transforms after registration → re-project anchors continuously. Real panorama not yet available for Mars datasets → use synthetic panoramas (§D.5.1).

**Acceptance criteria.**

1. Photo / Render / Blend modes selectable and visually distinct.
2. The Blend mode at 0.5 on a Mars dataset clearly shows photo-vs-mesh disagreement where the mesh is mis-registered.
3. Clicking on the panorama places an anchor at the correct 3D location.
4. The synchronise-camera button moves the 3D viewport accurately.

#### D.5.1 Synthetic panorama generation

**Purpose.** Mars panoramas have not been delivered yet; the panorama interaction must be designed and tested against generated panoramas in the meantime.

**Algorithm.**

```
for each Mars dataset (without real panoramas):
  compute the dataset's bounding box B
  generate 3 virtual cameras:
    camera 1: centred on B, ~2m above the surface, looking outward (panoramic sweep)
    camera 2: closer-in, looking at a feature of interest (close-up)
    camera 3: another close-up of a different feature
  for each camera:
    set HFov = 360°, VFov = 60° (Mastcam-Z-like)
    render the dataset's visible meshes into a 2K x 512 cylindrical projection
    store as a Panorama with Synthetic = true
```

The synthetic panoramas should look like rendered terrain (textured if the mesh has texture; shaded otherwise). They are functionally indistinguishable from real panoramas to the rest of the UI.

**Acceptance criteria.** Each Mars dataset produces three synthetic panoramas on load; the panorama panel can render and interact with them.

### D.6 Anchor Sphere primitive

**Purpose.** The central V6 annotation. Replaces the V5 cylinder-plus-cut-plane.

#### D.6.1 Placement gestures

Two gestures, both supported:

- **Single click** anywhere on a mesh → places a sphere at the cursor's ray intersection with the active picking layer mesh, with default radius (5% of dataset's bounding-box diagonal) and σ = radius / 2.
- **Lasso placement** → user draws a closed 2D polygon on the viewport; the sphere is fitted to enclose the back-projection of the polygon (centre = centroid of the projected 3D points; radius = max distance from centre; σ = radius / 2).

Both gestures show a **translucent ghost preview** of the anchor sphere during the gesture. Click-to-place uses a hover preview; lasso uses a continuously-updating preview as the polygon grows.

The V5 "active mode button" pattern survives (a top-bar toggle activates anchor-placement mode); clicking the active mode button again cancels placement.

#### D.6.2 The Gaussian falloff

The defining property of the Anchor Sphere. For each vertex `v` of any mesh inside the sphere's radius:

```
weight(v) = exp(-‖v - centre‖² / (2 σ²))
```

This weight enters:

- The weighted ICP refinement (§D.8).
- The error provenance computation for that vertex (§D.9).
- The fusion mesh weighting (§D.10).
- The "falloff-zone-only" toggle for error metrics — when enabled, only vertices with `weight(v) > ε` (default ε = 0.05) contribute to global metrics.

The Gaussian is **isotropic in 3D**; do not align it to any local normal. Anisotropic falloff is deferred to future work.

#### D.6.3 Visual representation

The anchor sphere is rendered as a translucent volume (alpha modulated by Gaussian, falling to zero at the radius). Inside the radius, an inner hard-edged sphere at the σ contour is rendered with higher opacity, to give the user a clear visual sense of the soft region. The centre is marked with a small filled point.

#### D.6.4 Adjustment flyout

After placement (or when re-entering adjustment via the Pins tab), the side panel switches to an adjustment flyout containing:

- **Radius** slider (continuous, with live-update preview).
- **σ** slider (constrained σ ≤ Radius; auto-adjusts if Radius drops below current σ).
- **Payload type** selector (Point / Line / Patch); switching destroys the current payload and instantiates the new one with default parameters.
- **Payload-specific controls** (§D.7).
- **Reliability weight** slider (for point payloads; auto-populated from feature confidence at placement, manually adjustable).
- **Commit** and **Discard** buttons; **Escape** also discards.

All sliders update the 3D view continuously; payload-specific diagrams refresh shortly after the user stops dragging.

#### D.6.5 Correspondence link management

When placing a second anchor for the same logical feature on a different mesh, the user must explicitly link it:

- After placing anchor A1 on mesh M1, the user clicks "Mark Correspondence" in the adjustment flyout.
- The cursor enters correspondence mode.
- The user clicks (or lassos) on mesh M2 to place anchor A2.
- The two anchors share a new CorrespondenceLink.

Alternatively, anchors can be linked retrospectively from the Pins tab via multi-select + "Group as correspondence."

**Acceptance criteria for all of D.6:**

1. Both placement gestures produce a clean Anchor Sphere with sensible defaults.
2. The Gaussian falloff is visible in the rendered sphere.
3. The adjustment flyout updates the sphere live without lag.
4. Two anchors on different meshes can be linked into a correspondence.
5. Discard and Escape both cleanly remove an in-progress anchor.

### D.7 Payloads

Three payload types share the floating pin card pattern from V5 (draggable, reattachable, collapsible, closeable). The card's content depends on the payload type.

#### D.7.1 Point-with-falloff (0D)

**Purpose.** Region-of-interest definitions; error provenance hotspots.

**Card content.**
- A small numerical readout of the anchor (centre, radius, σ).
- The error provenance stacked bar for this anchor's location.
- The reliability weight (editable slider).

**Algorithm.** Trivial — no derived geometry beyond what the sphere already provides.

#### D.7.2 Line-on-surface (1D)

**Purpose.** Mark elongated features (rock-corner ridges, glacier fronts, terrace edges). Replaces the V5 cut plane in spirit, but the line lives **on** the mesh surface rather than on a planar slice through it, allowing non-planar features.

**Sub-modes.**

- **Elevation isoline** — the user picks an elevation value; the system marches a polyline along that constant-elevation contour on the active picking layer.
- **Curvature ridge** — the user picks a starting point on a high-curvature region; the system marches along the local curvature ridge from that point in both directions, terminating when curvature drops below threshold or the mesh boundary is reached.

**Card content.**

- A 2D plot of the polyline unrolled: x-axis = arc length along the polyline, y-axis = elevation (for isoline mode) or curvature (for ridge mode).
- A mesh switcher: shows the same polyline on different meshes (the system traces an equivalent isoline/ridge on each participating mesh, allowing cross-mesh comparison without a shared coordinate frame).

**Algorithm — isoline marching.**

```
on isoline placement at elevation E on mesh M:
  collect all triangle edges of M that straddle elevation E (one endpoint above, one below)
  link the resulting segments into polylines (connectivity via shared edges)
  return the longest such polyline starting nearest the user's click
```

**Algorithm — curvature ridge marching.**

```
on ridge placement at start point P on mesh M:
  compute principal curvatures at P
  determine the direction d of maximum curvature
  step along d by Δ (~ mesh edge length)
  re-project onto mesh surface (closest-point on local triangle)
  reassess local curvature; if below threshold, terminate
  repeat in -d direction from start
  return concatenated polyline
```

**Cross-mesh tracing.** For both modes, the polyline can be traced on additional participating meshes using the same elevation or by transferring the start point via nearest-point onto the new mesh. Display all traced lines in the card's 2D plot in their assigned mesh colours.

**Edge cases.** Isoline doesn't exist (elevation outside mesh range) → warn the user. Curvature ridge runs into a flat region within a few steps → terminate with a short line and warn. Mesh boundary → terminate cleanly.

**Acceptance criteria.**

1. Both sub-modes produce visible polylines on the surface.
2. The 2D unrolled plot in the card matches the 3D polyline length.
3. Cross-mesh tracing works for at least two meshes in the Mars Kodiak dataset.

#### D.7.3 Unwrapped 2D patch (2D)

**Purpose.** A 2D map of the local surface for precise picking via textures or curvature features.

**Algorithm.** Azimuthal equidistant projection centred on the anchor's centre, with radius equal to the anchor's radius. For each surface point inside the radius, compute its geodesic distance from the centre and its bearing; place at `(d, θ)` in patch coordinates.

```
on patch generation for sphere S on mesh M:
  centre := S.Centre
  pick a tangent plane T at centre (using mesh normal at centre as up)
  pick a reference direction r in T (e.g. project world-north into T)
  for each vertex v of M with ‖v - centre‖ < S.Radius:
    geodesic_dist := geodesic distance from centre to v along surface
    bearing := angle between projection of (v - centre) into T and r
    patch_coord := (geodesic_dist * cos(bearing), geodesic_dist * sin(bearing))
    store (patch_coord, v) in PatchPayload.ProjectedPoints
  rasterise into a 2D image using triangle interpolation
```

The reference direction `r` is **project north** (a fixed world-space direction stored in the dataset). The compass rose marks `r` on both the 3D footprint and the 2D card.

**Card content.**

- The rasterised patch image as the main view.
- A coloured frame around the patch (same colour as the 3D footprint frame).
- A compass rose in one corner.
- A mini-control to swap the source mesh (re-projects from the new mesh).
- Bidirectional hover: hovering over a 2D patch point shows the corresponding 3D point and vice versa.

**Algorithm — geodesic distance.** For small patches, a fast approximation is fine: BFS over mesh vertices with edge lengths as weights, terminating at the radius. For accuracy, use the heat method (Crane et al.) — but only if BFS proves insufficient.

**Edge cases.** Highly curved local surface → azimuthal equidistant introduces distortion at the edges; the user should be informed via a faint distortion-indicating overlay if the patch radius is large relative to the local curvature.

**Acceptance criteria.**

1. The patch image is recognisable as a rendered local view of the mesh.
2. Compass rose and frame appear correctly in both 2D and 3D.
3. Mesh switcher re-projects from a different mesh and the result is consistent.
4. Bidirectional hover works.

### D.8 Registration solver integration

**Purpose.** Expose the existing rigid-transform-from-point-clusters and ICP solvers as an interactive workflow.

**UI.** A new top-level panel — **Registration** — accessible from the side panel. Contains:

- **Solve mode** selector: Traditional ICP / Region-restricted ICP / Point-pair + refinement.
- **Reference mesh** selector: one mesh is declared the reference (its transform is identity).
- **Run** button.
- **Residuals** display: a histogram of per-correspondence residuals after the most recent solve, plus an overall RMS.
- **Convergence log**: a scrollable list of solve iterations with their residual reductions.

**Data.** RegistrationState holds the solve mode, the reference mesh ID, the most recent solve outputs, and the convergence history.

**Algorithm.**

```
on Run with mode Point-pair + refinement:
  // Stage 1: point-pair solve
  for each non-reference mesh m:
    collect all PointPairs where m is one side and reference is the other
    if count(pairs) >= 4:
      apply weighted least-squares to compute T such that
        T(m's points) ≈ reference's corresponding points
        weighted by pair weights
      set m.Transform := T

  // Stage 2: region-restricted ICP refinement
  for each non-reference mesh m:
    for each Anchor Sphere S in the workspace:
      restrict m's points to those with weight(v|S) > ε
      add to ICP input with their Gaussian weights
    run weighted ICP between m's restricted points and reference's restricted points
    update m.Transform with the ICP result
```

For traditional ICP mode, skip stage 1 and use all of mesh m's points (no restriction).

For region-restricted ICP, skip stage 1 and start ICP directly with the region restriction.

**Edge cases.** Fewer than 4 point pairs → disable Point-pair + refinement, warn user. No anchors at all → disable region-restricted, run traditional. Solve diverges → revert to pre-solve transform, log the divergence, do not silently keep a bad result.

**Acceptance criteria.**

1. Each mode runs end-to-end on the Mars Kodiak dataset.
2. Residuals histogram updates after each solve.
3. Convergence log records each iteration.

### D.9 Error provenance

**Purpose.** Decompose registration error at each location into three sources and show the result at three granularities.

#### D.9.1 Three sources

**Dataset error** — per-vertex uncertainty from sensor metadata. Sources, in priority order:

1. If mesh has per-vertex `DatasetError`, use it.
2. Otherwise, if mesh has a global accuracy figure (e.g. HiRISE), use that uniformly.
3. Otherwise, use a per-sensor default value from the table below.

```
SensorType -> default dataset error (metres):
  RoverStereo -> distance-dependent: 0.001 + 0.001 * dist_from_camera
  Satellite   -> 0.25 (HiRISE-like; will be overridden by metadata if present)
  Photogrammetry -> 0.008
  LiDAR       -> 0.0005
  Unknown     -> 0.01 (with a UI warning to the user)
```

The user can **override** the dataset-error value per mesh from a small per-mesh panel under the Scene tab → "Error metadata" expander. This includes the ability to **fabricate** values for development.

**Algorithm residual** — propagated from per-correspondence residuals.

```
for each vertex v of mesh m:
  residuals := []
  for each PointPair (a, b, w) involving mesh m:
    distance d := ‖v - a‖ if v on mesh M of pair, else continue
    weight := exp(-d² / (2σ_pair²)) * w
    residuals.append((residual_of(a,b), weight))
  algorithm_residual(v) := weighted_average(residuals)
```

where `σ_pair` is taken from the host anchor's σ.

**Local conditioning** — how well-determined the registration is at this location.

Fast heuristic (for live updates):
```
for each vertex v:
  count nearby point-pairs (within k Gaussian σ)
  compute angular diversity: 1 - max|cos angle| among pair vectors
  conditioning(v) := 1 / (density * angular_diversity + ε)
```

Principled version (for final reports):
```
for each vertex v:
  collect the local Jacobian rows from all anchor falloff zones containing v
  form the local weighted normal matrix N = Jᵀ W J
  conditioning(v) := condition_number(N) = σ_max(N) / σ_min(N)
```

Run the fast heuristic continuously; run the principled version on user request (a "Compute Detailed Conditioning" button in the Registration panel).

#### D.9.2 Three visual granularities

**Per-pin stacked bar.** In each anchor's floating card, a horizontal stacked bar showing absolute (in metres) and relative (percentage) contributions from each source. Colour: red = dataset, green = algorithm, blue = conditioning.

**Global heatmap toggle.** In the Overlay tab, a "Show error provenance heatmap" toggle paints the visible meshes by their dominant error source at each point. Colour: red/green/blue categorical map. Pixels where total error is below threshold are unpainted (transparent). The threshold is exposed as a slider.

**Per-point hover readout.** When the user Ctrl-clicks or long-presses on a surface (re-using the V5 sphere query gesture), the resulting readout shows the same stacked bar plus numerical values for that point.

**Falloff-zone toggle.** A global toggle in the Overlay tab — "Restrict metrics to anchor falloff zones" — clips all global error metrics to vertices with falloff weight > 0.05 from at least one anchor.

**Acceptance criteria.**

1. Three sources computed and visible per anchor.
2. Global heatmap toggle renders correctly.
3. Hover readout works at any picked surface point.
4. Falloff-zone toggle visibly filters the heatmap.

### D.10 Fusion mesh

**Purpose.** A per-pixel composite of the registered ensemble where each pixel picks the source with the lowest combined error.

**UI.** A new top-bar toggle: **Fusion mode**. When on, the 3D viewport renders the fusion mesh instead of the individual meshes. Visibility toggles in the Scene tab continue to apply (only visible meshes participate in fusion).

**Algorithm.** GPU only, no CPU rasterisation.

Render pass:

```
fragment shader, run once per pixel:
  for each visible mesh m:
    sample m's depth at this pixel; if no hit, skip
    compute total_error(m, pixel) = w_d * dataset(m) + w_a * algo(m) + w_c * cond(m)
    record (m, total_error)

  pick m* with min total_error
  output color := m*'s color sample at this pixel
  output winner_id_buffer[pixel] := m*'s ID
```

The winner-ID buffer is a separate render target (e.g. R32_UINT format), readable by the CPU for picking.

**Pickability.** On click in fusion mode, sample the winner-ID buffer at the click pixel, get the responsible mesh M, then ray-cast against M to get the world-space point. Annotations placed on fusion inherit M's identity and its per-vertex provenance at that point.

**Performance.** The fragment shader iterates over visible meshes per pixel. With N meshes and 1080p resolution, this is roughly N × 2M operations per frame. For N ≤ 10 (typical), expect 60 fps on a mid-range GPU. For larger N, batch by region with a coarse pass.

**Edge cases.** No registration done yet → fusion shows the reference mesh only, with a banner telling the user to register first. All meshes equally low error at a pixel → tie-break by mesh ID. Mesh visibility off → that mesh does not participate in fusion at all.

**Acceptance criteria.**

1. Toggling fusion mode produces a coherent rendered surface from multiple meshes.
2. Clicking on the fusion mesh resolves to a specific source mesh.
3. Annotations placed on fusion carry the source mesh ID.
4. Performance stays at interactive frame rates on the test datasets.

### D.11 Retarget

**Purpose.** When a new mesh of the same site arrives, transfer the established registration to it.

**UI.** A new "Retarget" entry in the dataset loader. When the user loads a new mesh into an existing workspace, the system asks "Is this a new pass of an existing feature?" If yes, the retarget workflow begins:

1. The system runs auto-registration with the existing CorrespondenceLinks as initial seeds.
2. Each existing anchor is projected onto the new mesh by **nearest-point** matching first; if the nearest-point distance is large (> 2 × Radius), fall back to normal-aligned matching.
3. The projected anchors form a tentative correspondence with the existing ones.
4. A registration solve runs in Point-pair + refinement mode.
5. The user enters a validation pass: each tentative anchor is highlighted in turn, and the user accepts, adjusts, or rejects each one. Adjustments use the standard adjustment flyout. Rejections drop the anchor.
6. After validation, a final solve runs.

**Data.** A "retargeted-from" link is stored on the new anchor pointing to the source anchor, for provenance.

**Algorithm.** The projection and re-solve use existing logic; the retarget workflow is mostly UI orchestration.

**Edge cases.** Nearest-point matching fails for many anchors → warn the user that the meshes may not overlap sufficiently. New mesh has a very different scale → ask user to confirm before proceeding. User rejects all anchors → fall back to a clean registration from scratch.

**Acceptance criteria.**

1. Loading a new mesh into an existing workspace offers the retarget option.
2. Tentative anchor projections appear on the new mesh.
3. Validation pass walks through each anchor.
4. Final solve produces a sensible transform for the new mesh.

### D.12 2D-3D linkage details

**Purpose.** The 2D floating pin card and the 3D Anchor Sphere must feel like one object. Communicate orientation, extent, and identity unambiguously.

**Linkage elements.**

- **Compass rose**: a small 8-spoke compass with a labelled N (project north) rendered in both the 3D footprint of the sphere and the 2D card. Both rotate consistently when the camera moves or the patch is rotated.
- **Coloured frame**: the 2D card's border colour is shared with a coloured rectangle drawn on the mesh in 3D, showing the patch's extent in world space. Each anchor gets a unique colour from a categorical palette (V5 palette can be reused).
- **Bidirectional hover**: hovering over the 2D card highlights the corresponding 3D point with a small marker; hovering over the 3D anchor highlights the corresponding 2D card point.
- **Reattach affordance**: same as V5 (pin icon in the card header). Reattached cards track their 3D anchor; unpinned cards stay where the user puts them.

**Acceptance criteria.**

1. Compass rose visible in both views, aligned to project north.
2. Coloured frame visible in 3D, matching card border.
3. Bidirectional hover works.
4. Reattach behaves as in V5.

### D.13 Persistence

**Purpose.** Saved workspaces must be reloadable in fresh sessions.

**Format.** JSON serialisation of the Workspace state (§C.7), with mesh references stored as `(DatasetId, MeshId)` pairs that are rehydrated against the active dataset on load. Anchor Sphere centres are stored in world space (mesh-independent) along with their HostMeshId for re-attachment.

**API.**

- **Save**: serialise workspace, return JSON. UI: a "Save" button in the top bar's hamburger menu produces a downloadable `.scanpin.json` file.
- **Load**: parse JSON, rehydrate. UI: a "Load" button opens a file picker.

**Edge cases.** Loaded workspace references a dataset not currently available → warn user, offer to switch datasets. Loaded anchor's HostMeshId is no longer present → orphan the anchor (mark it visually and let the user delete or re-link).

**Acceptance criteria.**

1. Save produces a valid JSON.
2. Load restores anchors, correspondences, panoramas, and explore mode state.
3. Round-trip save-load is idempotent.

---

## Part E — Algorithms in detail

The following pseudocode complements §D and is given separately because they are the most likely sources of implementation hiccups.

### E.1 Weighted least-squares from point pairs

Given pairs `[(a_i, b_i, w_i)]`, find rotation R and translation t minimising
`Σ w_i ‖R a_i + t - b_i‖²`.

```
1. compute weighted centroids: ā = Σ w_i a_i / Σ w_i, b̄ = Σ w_i b_i / Σ w_i
2. centre points: a_i' = a_i - ā, b_i' = b_i - b̄
3. compute weighted covariance matrix: H = Σ w_i a_i' b_i'ᵀ
4. SVD: H = U Σ Vᵀ
5. R = V diag(1, 1, det(V Uᵀ)) Uᵀ
6. t = b̄ - R ā
```

### E.2 Weighted ICP

Standard point-to-point ICP with per-vertex Gaussian weights from anchor falloff. Use existing project ICP code; extend the input to accept weights.

```
repeat until convergence or max_iter:
  for each point p_m on mesh m (with weight w_m from anchors):
    find nearest point p_r on reference (with weight w_r from anchors)
    register pair (p_m, p_r) with weight w_m * w_r
  run weighted least-squares (E.1) to get incremental R, t
  apply (R, t) to mesh m
  update residuals
```

Convergence criterion: residual reduction < 1e-6 between iterations or max 50 iterations.

### E.3 Condition number computation (principled)

For the local normal matrix N = Jᵀ W J at a region:

```
1. compute Jacobian rows from each anchor's contribution
2. assemble weighted normal matrix
3. eigen-decomposition (3x3 for rotation-only, 6x6 for rigid)
4. condition number = σ_max / σ_min
```

For very ill-conditioned regions (condition > 1e6), clamp to 1e6 to avoid overflow in display.

### E.4 Synthetic panorama rendering

```
on dataset load (if Mars and no real panoramas):
  bbox := compute bounding box of visible meshes
  for i in 1..3:
    pose := generate camera pose i (centred near bbox, rover-height)
    set up offscreen render target (2048 x 512, RGB)
    set up camera with HFov=360°, VFov=60°
    render visible meshes into target as a cylindrical projection
    store as Panorama with Synthetic = true
```

### E.5 Lasso polygon clipping

```
on lasso commit (polygon P, projection plane π):
  for each visible mesh m:
    for each triangle t in m:
      centroid c := mean of t's vertices
      (sx, sy) := project c onto π
      if point_in_polygon((sx, sy), P):
        retain t
      else:
        mark t as clipped
    store clip mask per mesh
```

Use the winding-number algorithm for `point_in_polygon`.

### E.6 Anchor projection for retarget

```
for each existing anchor A on old mesh M_old:
  for each candidate mesh M_new:
    find nearest point p_new on M_new to A.Centre
    if ‖p_new - A.Centre‖ > 2 * A.Radius:
      fall back to normal-aligned: find p_new whose normal is most aligned with M_old's normal at A.Centre
    create tentative anchor A' on M_new at p_new with same Radius, σ, payload type
    link A and A' via a new CorrespondenceLink
```

---

## Part F — Implementation Order (Phasing)

Build the system in nine phases. Each phase is independently testable.

### Phase 1: Cleanup (≈ 1 week)

Delete the V5 features listed in §B. Verify the app still loads, datasets switch, and the scene renders. No V6 features yet.

### Phase 2: Anchor Sphere primitive (≈ 1.5 weeks)

Implement the AnchorSphere type, single-click placement with ghost preview, Gaussian rendering, adjustment flyout with Radius and σ sliders, and the Pins tab integration. Skip payloads — use a placeholder Point payload with no card content.

### Phase 3: Mesh-wheel and lasso (≈ 0.5 weeks)

Implement mesh-wheel (D.1) and polygonal lasso (D.3). These are small, self-contained, and unblock several later phases.

### Phase 4: Payloads (≈ 2 weeks)

Implement Point, Line-on-surface (both sub-modes), and Patch payloads (D.7). Floating pin cards with payload-specific content. 2D-3D linkage (D.12).

### Phase 5: Dual-signal Explore mode (≈ 1 week)

Implement D.4. Feature confidence requires curvature, which is also needed for ghost silhouette enhancement (D.2) — implement both in this phase.

### Phase 6: Registration solver integration (≈ 1.5 weeks)

Implement D.8. Registration panel, three solve modes, residuals histogram. Uses existing solver code under the hood.

### Phase 7: Error provenance (≈ 1.5 weeks)

Implement D.9. Three sources, three granularities, falloff-zone toggle. Requires Phase 6 to be working.

### Phase 8: Fusion mesh (≈ 1 week)

Implement D.10. GPU fragment shader, winner-ID buffer, pickability. Requires Phase 7.

### Phase 9: Panorama, Retarget, Persistence (≈ 1.5 weeks)

Implement D.5 (panorama panel + synthetic generation), D.11 (retarget), and D.13 (persistence). These are mostly UI work on top of the established core.

**Buffer (≈ 1 week)** for polish, evaluation prep, bugfixes.

Total: ~12 weeks, matching the 3-month deadline. The phasing puts foundational changes first and the most user-facing features last, so an early evaluation walkthrough can use Phase 6's output.

---

## Part G — Testing and Acceptance Criteria

For each phase:

- **Smoke test**: app loads, dataset switches, no console errors.
- **Feature acceptance**: the per-feature acceptance criteria from §D.
- **Regression test**: previously-passing features still pass.

Beyond per-feature tests:

- **Round-trip persistence test**: save a workspace, reload, confirm anchors, correspondences, and registration state are identical.
- **Performance baseline**: on the Mars Kodiak dataset (3 meshes, ~1M vertices total), fusion mode at 1080p must run at ≥ 30 fps on a mid-range GPU.
- **Expert walkthrough rehearsal**: at the end of Phase 9, run the full Mars use case (§5 of the extended abstract) end-to-end. If any step is broken or feels awkward, fix before formal evaluation.

---

## Part H — Hiccup Recovery / Debugging Guide

When something doesn't work as expected, consult this section before changing scope.

### H.1 aardvark.dom-specific issues

The reactive update model can produce subtle bugs when state changes don't trigger expected re-renders. If a UI element doesn't update:

1. Check whether the change is wrapped in an `AVal` / `AMap` / `AList`.
2. Confirm the dependency graph: is the affected UI element subscribed to the changed state?
3. Inspect the project's existing patterns for similar update flows.

### H.2 Registration solve issues

If a solve produces garbage:

- Confirm point pairs are not all coplanar or collinear (rank-deficient input).
- Check correspondence link validity — the pair's mesh IDs must match the actual mesh references.
- Verify weights are normalised to [0, 1] and not all zero.
- For ICP divergence, confirm the initial transform is reasonable (it should not be a wild starting point).

### H.3 Gaussian falloff scaling

A common bug: weights are computed in mesh-local coordinates but used against world coordinates, leading to nonsensical falloff sizes after a mesh transform. Make sure weights are always computed in **world coordinates** after applying mesh transforms.

### H.4 Fusion mesh visual glitches

If the fusion mesh flickers or has noisy edges:

- Check the winner-ID buffer's precision (32-bit unsigned integer is required).
- Confirm depth comparison is correct for each mesh.
- Verify the error-source weights `w_d`, `w_a`, `w_c` are not producing degenerate cases (e.g. all zero).

### H.5 Panorama projection mismatches

If a click on the panorama doesn't place an anchor at the expected location:

- Verify camera intrinsics: HFov and VFov must match the panorama's rendering settings.
- Verify camera extrinsics: position, forward, up must be in world coordinates.
- Test with a synthetic panorama first (where the pose is fully under the system's control).

### H.6 Performance issues

If a feature is slow:

- Profile to find the bottleneck before optimising.
- Common culprits: per-frame curvature recomputation (cache per mesh), unnecessary GPU readback (rare in this app), polygon-in-polygon tests in lasso (vectorise).

### H.7 Open questions

If the agent encounters a genuinely under-specified situation — a UI choice not pinned down here, an algorithm that admits multiple reasonable implementations, a dataset behaviour that doesn't match the spec — **stop and ask the user**. Do not invent a design decision that affects user-visible behaviour.

The non-goals from §A.2 are the only situations where the agent should silently decline to act and surface the conflict to the user.

---

## Part I — Glossary

- **Anchor Sphere** — the V6 annotation primitive. A 3D sphere with centre, radius, Gaussian σ, and one of three payloads.
- **Correspondence Link** — a shared identifier across Anchor Spheres that mark the same logical feature on different meshes.
- **Co-registration** — the task of aligning multiple meshes into a single coordinate system.
- **Dataset error** — per-vertex sensor accuracy metadata for a mesh.
- **Disagreement signal** — one of two Explore-mode signals; depth difference between meshes.
- **Falloff zone** — the region inside an Anchor Sphere where Gaussian weight is above a threshold (default ε = 0.05).
- **Feature confidence** — one of two Explore-mode signals; surface curvature × steepness.
- **Fusion mesh** — a per-pixel composite of the registered ensemble, source determined by lowest combined error.
- **Line-on-surface** — a 1D payload type. Polyline on the mesh surface, either an elevation isoline or a curvature ridge.
- **Local conditioning** — the third error source. How well-determined the registration solve is at a location.
- **Mesh-wheel** — V6's replacement for V5's revolver disk. Cycles active picking layer via scroll wheel.
- **Patch** — the 2D payload type. An azimuthal-equidistant projection of the local surface.
- **Point payload** — the 0D payload type. The sphere alone, used for region-of-interest definitions and error provenance hotspots.
- **Reliability weight** — a per-correspondence weight in `[0,1]` derived from local feature confidence at placement time.
- **Retarget** — the workflow for applying an established registration to a newly arriving mesh.
- **σ (sigma)** — the Gaussian falloff standard deviation of an Anchor Sphere, with σ ≤ Radius.
- **Winner-ID buffer** — a GPU render target accompanying the fusion mesh colour buffer, labelling each pixel by its contributing source mesh.

---

## Cross-references

- **Paper story:** `scanpin_extended_abstract.md`
- **LaTeX paper prototype:** `scanpin_paper/`
- **V5 baseline:** `description.md` (the previous application description)
- **User feedback summary:** `scanpin_v5_user_feedback_summary.md`
