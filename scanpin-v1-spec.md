# ScanPin V1 — Open TODOs

Status audit of the 13 priority items from the original spec, followed by concrete remaining work.

---

## Implementation Status

| # | Item | Status |
|---|------|--------|
| 1 | Data model types | **Done** — `ScanPinModel.fs` has all types (`ScanPinId`, `SelectionPrism`, `CutPlaneMode`, `ScanPin`, `ScanPinModel`, etc.) |
| 2 | Placement state machine | **Partial** — simplified to `PlacingMode` + `ActivePlacement` instead of full `PlacementIdle → DefiningFootprint → DefiningCutPlane → Adjusting` FSM. The orphaned `PlacementState` DU in `ScanPinModel.fs` is unused dead code. |
| 3 | Circle-mode footprint | **Done** — anchor click creates a 32-gon prism; radius slider works. |
| 4 | AlongAxis cut plane with slider | **Done** — angle slider in GUI, `SetCutPlaneAngle` message, updates cut results. |
| 5 | Elevation profile diagram | **Partial** — SVG diagram renders via OnBoot JS + MutationObserver. Shows polylines, legend, hover highlight, axis labels. But data is **dummy** (`dummyCutResults` generates sine waves, not real mesh intersections). |
| 6 | Diagram HTML overlay | **Done** — positioned via screen projection. V1 uses HTML overlay (not 3D billboard). |
| 7 | 2D GUI panel | **Done** — pin list with focus/delete, placement controls (circle button, along/across toggle, sliders), commit/cancel. |
| 8 | Arcball gizmo | **Not done** |
| 9 | Polygon-mode footprint | **Deferred** — not needed for V1 prototype. |
| 10 | AcrossAxis cut plane | **Partial** — toggle button and distance slider exist; `SetCutPlaneDistance` message works. But cut results are still dummy data (same sine waves). |
| 11 | Serialization | **Deferred** — export works, import not needed for V1 prototype. |
| 12 | Billboard drag-along-axis | **Not done** |
| 13 | Pin-head glyph + distance LOD | **Not done** — all pins render as same-size dots regardless of distance. |

---

## Concrete TODOs

### Critical — Replace dummy data with real computation

- [ ] **Real mesh-plane intersection** — `dummyCutResults` in `Update.fs` generates fake sine-wave polylines. Replace with actual mesh-plane intersection via a server endpoint (POST to Superserver, which has Embree BVH). This is the single most important missing piece — without it, the diagram is decorative.
  - Server side: add a `POST /api/query/plane-intersection` endpoint that takes a plane definition and mesh name, returns polyline segments.
  - Client side: replace `dummyCutResults` call with an async server request; add `CutResultsComputed` message for the async callback.
  - Needs to handle both AlongAxis and AcrossAxis modes.

### Critical — Core sample 3D view in diagram overlay

- [x] **Secondary RenderControl per pin ("core sample")** — a small 3D viewport in the floating diagram overlay (stacked below the SVG profile). Shows an isolated "core sample" of the pin region.
  - **Core sample transform:** the prism's axis is rotated to align with the Z axis, and the anchor point is translated to the origin. This makes the cylinder a vertical rod — a geological core sample analogy. The mesh data appears slanted inside the rod because the original surface was not perpendicular to the drill axis.
  - **Clipping:** axis-aligned box in the rotated space: `[-R, R] x [-R, R] x [-extBack, extFwd]` where R is the footprint radius. The existing `clippy` shader handles this since the clip box is now axis-aligned with the cylinder. Boundary edges are color-highlighted per mesh.
  - **Content:** all loaded meshes (direct single-pass render, no off-screen pipeline), plus prism wireframe and cut plane quad — all transformed into the core sample coordinate system.
  - **Camera:** own orbit camera centered at the origin (the core sample center). Initialized to match the main camera's phi/theta with a radius proportional to the footprint. User can orbit independently.
  - **Lifecycle:** visible when a pin is selected; hidden when deselected. Updates reactively when pin parameters change (radius, cut plane angle/distance).

### Important — Missing interactions

- [ ] **Arcball gizmo for axis direction** — No gizmo exists. The axis is locked to camera forward at placement time with no way to adjust it. Needs a 3D sphere gizmo at the anchor point with drag-to-rotate.

- [ ] **Billboard drag along axis** — The diagram overlay cannot be repositioned. Low priority but spec'd.

### Visual — 3D rendering improvements

- [ ] **Pin-head glyph + LOD** — All pins render as identical small dots. At distance, pins should collapse to a compact glyph; up close, they should show the diagram. Clicking a distant glyph should focus the camera.

### Cleanup

- [ ] **Remove orphaned `PlacementState` type** — The `PlacementState` DU in `ScanPinModel.fs` is dead code, never referenced.

- [ ] **Dataset visibility → diagram dimming** — When a mesh is toggled off in the scene tab, its polyline in the pin diagram should be dimmed.

---

## Phase 2/3 (not V1 scope)

- Polygon-mode footprint (vertex-clicking interaction)
- Click-drag radius definition
- Persistence (deserialization / import of pins)
- Prism wireframe depth bias fix
- True 3D billboard rendering (replace HTML overlay with textured quad)
- Point association for alignment (picking corresponding points across datasets in diagrams)
- Derived comparison diagrams (boxplots, parallel coordinates across multiple pins)
- Multi-pin filtering / relevance-based LOD
