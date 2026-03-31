# ScanPin V1 — Open TODOs

Status audit of the 13 priority items from the original spec, followed by concrete remaining work.

---

## Implementation Status

| # | Item | Status |
|---|------|--------|
| 1 | Data model types | **Done** — `ScanPinModel.fs` has all types (`ScanPinId`, `SelectionPrism`, `CutPlaneMode`, `ScanPin`, `ScanPinModel`, etc.) |
| 2 | Placement state machine | **Partial** — simplified to `PlacingMode` + `ActivePlacement` instead of full `PlacementIdle → DefiningFootprint → DefiningCutPlane → Adjusting` FSM. The orphaned `PlacementState` DU in `ScanPinModel.fs` is unused dead code. |
| 3 | Circle-mode footprint | **Partial** — anchor click creates a 32-gon prism; radius slider works. But no click-drag radius definition (user must use slider). |
| 4 | AlongAxis cut plane with slider | **Done** — angle slider in GUI, `SetCutPlaneAngle` message, updates cut results. |
| 5 | Elevation profile diagram | **Partial** — SVG diagram renders via OnBoot JS + MutationObserver. Shows polylines, legend, hover highlight, axis labels. But data is **dummy** (`dummyCutResults` generates sine waves, not real mesh intersections). |
| 6 | Billboard rendering in 3D | **Not done** — diagram is an HTML overlay positioned via screen projection, not a 3D billboard quad. No depth cheating, no occlusion. |
| 7 | 2D GUI panel | **Done** — pin list with focus/delete, placement controls (circle/polygon buttons, along/across toggle, sliders), commit/cancel. |
| 8 | Arcball gizmo | **Not done** |
| 9 | Polygon-mode footprint | **Not done** — button exists in GUI but `StartPlacement Polygon` just creates a circle like circle mode. No vertex-clicking interaction. |
| 10 | AcrossAxis cut plane | **Partial** — toggle button and distance slider exist; `SetCutPlaneDistance` message works. But cut results are still dummy data (same sine waves). |
| 11 | Serialization | **Partial** — `serializePin` and `serializeAllPins` produce JSON. No deserialization / import. |
| 12 | Billboard drag-along-axis | **Not done** |
| 13 | Pin-head glyph + distance LOD | **Not done** — all pins render as same-size dots regardless of distance. |

---

## Concrete TODOs

### Critical — Replace dummy data with real computation

- [ ] **Real mesh-plane intersection** — `dummyCutResults` in `Update.fs` generates fake sine-wave polylines. Replace with actual mesh-plane intersection via a server endpoint (POST to Superserver, which has Embree BVH). This is the single most important missing piece — without it, the diagram is decorative.
  - Server side: add a `POST /api/query/plane-intersection` endpoint that takes a plane definition and mesh name, returns polyline segments.
  - Client side: replace `dummyCutResults` call with an async server request; add `CutResultsComputed` message for the async callback.
  - Needs to handle both AlongAxis and AcrossAxis modes.

### Important — Missing interactions

- [ ] **Click-drag radius definition** — Currently the footprint radius is only adjustable via a slider after placement. The spec calls for click-drag on the initial anchor click to set the radius interactively.

- [ ] **Polygon-mode footprint** — The polygon button fires `StartPlacement Polygon` but no vertex-clicking interaction exists. Needs: click to add vertices, double-click or snap-to-first to close, live preview of the polygon outline.

- [ ] **Arcball gizmo for axis direction** — No gizmo exists. The axis is locked to camera forward at placement time with no way to adjust it. Needs a 3D sphere gizmo at the anchor point with drag-to-rotate.

- [ ] **Billboard drag along axis** — The diagram overlay cannot be repositioned. Low priority but spec'd.

### Visual — 3D rendering improvements

- [ ] **Pin-head glyph + LOD** — All pins render as identical small dots. At distance, pins should collapse to a compact glyph; up close, they should show the diagram. Clicking a distant glyph should focus the camera.

- [ ] **True billboard rendering** — The diagram is currently an HTML overlay with screen-space projection. A proper 3D billboard (textured quad, camera-facing, depth-tested with bias) would integrate better with the scene. This is a larger architectural change.

- [ ] **Prism wireframe depth bias** — The prism wireframe (thin triangle-quads in `Revolver.fs:buildPrismWireframe`) has no depth bias, so z-fighting with mesh surfaces is likely on close-up views.

### Data / persistence

- [ ] **Deserialization / import** — `ScanPinSerialize` can export JSON but there is no import path. Committed pins are lost on page reload.

- [ ] **Recompute cut results on parameter change** — Currently `dummyCutResults` is called inline during update. Once real intersection exists, this should be async with proper loading state.

### Cleanup

- [ ] **Remove orphaned `PlacementState` type** — The `PlacementState` DU in `ScanPinModel.fs` (lines 61–70) is dead code, never referenced. The actual placement state uses `PlacingMode : FootprintMode option` + `ActivePlacement : ScanPinId option`.

- [ ] **Dataset visibility → diagram dimming** — When a mesh is toggled off in the scene tab, its polyline in the pin diagram should be dimmed. Currently diagram polylines ignore mesh visibility state.

---

## Phase 2/3 (not V1 scope)

- Point association for alignment (picking corresponding points across datasets in diagrams)
- Derived comparison diagrams (boxplots, parallel coordinates across multiple pins)
- Multi-pin filtering / relevance-based LOD
