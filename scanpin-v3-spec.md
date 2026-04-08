# ScanPin V3 — Specification (Living)

## Purpose

V3 redesigns the ScanPin inspector. The V2 orthographic 3D sub-viewport was visually
confusing and redundant with the main scene. V3 removes it, transfers inspection
controls into the 3D scene, and replaces the core sample volume view with an
**unwrapped cylinder surface stratigraphy diagram**.

`STUB(graphics)` = low-level WebGL/scenegraph work. `STUB(server)` = server-side
computation that needs an endpoint.

---

## Status

| Phase | Item | Status |
|------|------|--------|
| 0.1 | Simplify PinDemo ranking view (variance text instead of boxplot) | done |
| 0.2 | Merge PinDemo → Superprojekt (ranking view + dataset stats) | done — `RankingState.fs`, ranking section in `GuiPins.pinDiagram` |
| 0.3 | Git: commit, push `scanpin-v2`, switch to `master` | done — `master` fast-forwarded |
| 1.4 | Stratigraphy data model | done — types in `ScanPinModel.fs`, messages in `Update.fs` |
| 1.5 | Stratigraphy computation stub | done — `Stratigraphy.fs` mock; wired into `Update.fs` debounced |
| 1.6 | Stratigraphy diagram renderer | done — `StratigraphyView.fs` (renderControl + vertex-colored quads) |
| 1.7 | Normalization / Undistorted toggle | done — inside `buildGeometry`, buttons in `GuiPins` |
| 2.8 | AcrossAxis 3D slider widget | done — `cutPlaneSlider` block in `Revolver.fs`; geometry helpers `buildLineTube`/`buildHandleBox` in `PinGeometry.fs` |
| 2.9 | AlongAxis cap-click control | done — cap discs + diameter lines in `cutPlaneSlider`; `buildDisc` in `PinGeometry.fs` |
| 2.10 | Ghost clipping cylinder shader | done — `clippy` cylinder branch in `Shader.fs`; uniforms wired in `MeshView.buildMeshTextures`; toggle in `GuiPins.pinDiagram` |
| 2.11 | Extracted lines in 3D | done — `extractedLines` block in `Revolver.fs`; `appendPolylineRibbon` in `PinGeometry.fs`; toggles in `GuiPins.pinDiagram` |
| 3.12 | Explosion view | done — `Stratigraphy.explosionOffsets`; `BlitShader.explode` vertex stage; `ExplosionOffset` uniform in `MeshView`; offsets applied to extracted lines in `Revolver.fs`; toggle + slider in `GuiPins.pinDiagram` |
| 3.13 | Between-space hover in stratigraphy | todo (experimental) |
| 3.14 | Between-space 3D highlight | todo (experimental) |
| 4.15 | Remove V2 sub-viewport code + GUI | todo |
| 4.16 | Update serialization for V3 fields | todo |
| 4.17 | Tune visual parameters | todo |

---

## Already-Built Foundations (do not redo)

- **Types** (in `ScanPinModel.fs`): `RayMeshIntersection`, `StratigraphyColumn`,
  `StratigraphyData`, `StratigraphyDisplayMode = Undistorted | Normalized`,
  `ExplosionState`, `BetweenSpaceHighlight`, `GhostClipMode`, `ExtractedLinesMode`.
  All wired onto `ScanPin` (`Stratigraphy`, `StratigraphyDisplay`, `GhostClip`,
  `ExtractedLines`, `Explosion`, `BetweenSpaceHover`).
- **Messages** (in `Update.fs`): `StratigraphyComputed`, `SetStratigraphyDisplay`,
  `SetGhostClip`, `SetShowCutPlaneLines`, `SetShowCylinderEdgeLines`,
  `SetExplosionEnabled`, `SetExplosionFactor`, `HoverBetweenSpace`,
  `ClearBetweenSpaceHover`. All have ScanPinUpdate handlers.
- **`Stratigraphy.compute`** returns synthetic data. Replace with real server query
  later — keep the same signature.
- **`StratigraphyView.render selectedPin`** is the embedded renderer; reads
  `pin.Stratigraphy` and `pin.StratigraphyDisplay`, respects
  `RankingState.datasetHidden`.
- **`Shader.vertexColor`** — per-vertex color pass-through. Reuse for any 2D quad
  geometry.
- **`RankingState`** — global cvals for `datasetOrder`, `datasetHidden`, `topK`,
  `rankFadeOn`. Used by both the ranking row UI and the stratigraphy renderer.

---

## Phase 2 — 3D scene enhancements (next)

### 2.8 AcrossAxis 3D slider widget

Replaces the HTML cut-plane slider for `CutPlaneMode.AcrossAxis`.

- A thin "rail" cylinder along the prism's `AxisDirection`, full prism length,
  bright accent color (white or `#1a56db`).
- A small box or disc "handle" on the rail at the current `distanceFromAnchor`.
- Click-drag the handle along the rail → emit `ScanPinMsg (SetCutPlaneDistance d)`.
  Constrain `d` to `[-ExtentBackward, ExtentForward]`.
- **`STUB(graphics)`** picking: handle takes priority over scene picks when visible.
- Render rail + handle **without depth test**, only for the currently edited /
  selected pin. Other pins do not show the widget.

### 2.9 AlongAxis cap-click control

Replaces the HTML angle slider for `CutPlaneMode.AlongAxis`.

- Translucent disc on each cap of the prism (top + bottom).
- Click anywhere on a cap → angle from cap center defines the new
  `SetCutPlaneAngle`. The cut plane rotates to pass through the click point and
  the prism axis.
- Show the current cut plane angle as a thin diameter line across the cap.
- **`STUB(graphics)`** picking: cap surface picks return a 3D point on the disc.

**Both controls coexist.** The `CutPlaneMode` toggle determines which is "live".
The HTML slider in the panel is **removed** in this phase too.

### 2.10 Ghost clipping cylinder

When `pin.GhostClip = GhostClipOn` for the currently edited pin:

- Geometry **inside** the prism cylinder: rendered normally.
- Geometry **outside**: rendered with the existing ghost shader.
- Fragment-shader test (uniforms = anchor, axis (unit), radius, extentForward,
  extentBackward):

  ```
  float axisProj   = dot(p - anchor, axis);
  float radialDist = length((p - anchor) - axisProj * axis);
  bool inside = radialDist <= radius
             && axisProj >= -extentBackward
             && axisProj <=  extentForward;
  ```

- **`STUB(graphics)`**: extend the existing ghost/clip shader (in `Shader.fs` /
  `BlitShader`) with a uniform-controlled cylinder branch. Could be a second
  variant or a per-fragment branch.
- Only **one** pin's ghost clip is active at a time (the currently edited /
  selected pin).
- Toggled by a checkbox in `GuiPins.pinDiagram`.

### 2.11 Extracted lines in 3D

Two independently-toggled line sets per pin:

- **Cut plane intersection lines** (`ExtractedLines.ShowCutPlaneLines`): the
  existing `pin.CutResults` polylines. Render as colored line strips on the cut
  plane, **depth test disabled**, line width 2–3 px.
- **Cylinder edge curves** (`ExtractedLines.ShowCylinderEdgeLines`): connect the
  intersection points across adjacent angular columns of `pin.Stratigraphy` to
  form polylines on the cylinder wall. Multiple curves per dataset are possible
  (folds). **`STUB(graphics)`**: build the 3D polylines from
  `StratigraphyData.Columns`. Same colors as the stratigraphy stripes. Depth test
  disabled.

---

## Phase 3 — Experimental

### 3.12 Explosion view

When `pin.Explosion.Enabled = true`:

- Inside the prism cylinder, datasets are sorted by average z (use
  `StratigraphyData` per-column min/max or the existing `GridEval` median).
- Dataset at index `i` is displaced by
  `i * pin.Explosion.ExpansionFactor * baseSpacing` along the prism axis, where
  `baseSpacing = (extentForward + extentBackward) / N`.
- Implemented as a vertex-shader offset, applied **only** to fragments that pass
  the cylinder clipping test (reuses 2.10's machinery).
- Slider in the panel controls `ExpansionFactor` (range `0..3`).
- Stratigraphy diagram does **not** reflect explosion (true z always).
- Cylinder edge lines + cut-plane lines should be displaced to match.
- Prototype quality: no picking on exploded geometry.

### 3.13 Between-space hover in the stratigraphy diagram

- Mouse position on the diagram → (angle, z).
- Find the two consecutive contact events that bracket the cursor's z in that
  angular column → identify the bounding pair of datasets.
- Highlight that between-space band in the diagram (warm translucent overlay).
- Emit `HoverBetweenSpace(pinId, angle, zLo, zHi, lower, upper)` →
  `pin.BetweenSpaceHover`.
- HTML tooltip showing "Between [A] and [B], gap: X.Xm".
- `ClearBetweenSpaceHover` on mouse leave.

### 3.14 Between-space 3D highlight

Driven by `pin.BetweenSpaceHover`:

- Define a neighborhood around (angle, z): e.g. ±15°, ±some z range.
- **`STUB(server)`**: query both bounding meshes for ray hits in the
  neighborhood. Return pairs of (lower surface point, upper surface point).
- Triangulate a quad patch between the two surfaces.
- Render as a translucent warm-colored volume, depth test enabled, alpha
  blending. **`STUB(graphics)`**.
- Prototype quality; edge artifacts acceptable.

---

## Phase 4 — Polish

### 4.15 Remove V2 sub-viewport code + GUI

Files to gut after Phase 2 lands:

- `GuiPins.fs` — the entire core-sample `renderControl` block (the second
  `renderControl { }` inside `pinDiagram`), the side/top view buttons, the
  effect-toggles (depth/isolines/color), the cut-plane indicator overlay.
- `Model.fs` — `CoreSampleViewMode`, `CoreSampleRotation`, `CoreSamplePanZ`,
  `CoreSampleZoom`, `DepthShadeOn`, `IsolinesOn`, `ColorMode` fields.
- `Update.fs` — corresponding messages: `SetCoreSampleRotation`,
  `SetCoreSamplePanZ`, `SetCoreSampleZoom`, `SetCoreSampleViewMode`,
  `ToggleDepthShade`, `ToggleIsolines`, `ToggleColorMode`.
- Anything in `Revolver.fs` / `PinGeometry.fs` that exists *only* for the sub
  viewport. Keep `coreSampleTrafo`, `buildPrismWireframe`, `buildCutPlaneQuad`
  if they're still useful for the new 3D widgets / extracted lines.
- CSS: `.pin-mini-wrapper`, `.pin-mini-view`, `.effect-toggles`,
  `.pin-cut-indicator`.

### 4.16 Serialization

Extend `ScanPinSerialize.serializePin` to write the new fields:
`stratigraphyDisplay`, `ghostClip`, `extractedLines`, `explosion`. (No
deserialization yet — none exists for V1/V2 either.)

### 4.17 Visual tuning

Stripe thickness, neutral fill grays, ghost cylinder appearance, line widths,
slider/handle dimensions. Driven by visual review.

---

## Removed / Won't-Have

- V2 orthographic 3D sub-viewport. (Removed in 4.15.)
- Side/Top view toggle, summary-mesh / filtered-mesh toggle buttons.
- Sub-viewport camera controls (`CoreSampleRotation`, `PanZ`, `Zoom`).
- HTML cut-plane sliders (replaced by 3D widgets in 2.8/2.9).

---

## Out of Scope (Phase 4+ / Not Implemented)

- **Embedded thumbnails** — billboard previews of each non-selected pin's
  stratigraphy in the 3D scene.
- **Average-line distortion** — third stratigraphy mode subtracting per-column
  average z.
- **Connected between-space region finding** — flood-fill the hover region
  instead of using a fixed neighborhood.

---

## Open STUBs (recap)

- `STUB(server)`: real ray-mesh stratigraphy query (replaces `Stratigraphy.compute`
  mock). Inputs: anchor, axis, radius, extents, angularRes. Output: existing
  `StratigraphyData`.
- `STUB(server)`: between-space neighborhood query (3.14).
- `STUB(graphics)`: rail+handle picking + drag (2.8).
- `STUB(graphics)`: cap-disc picking → angle (2.9).
- `STUB(graphics)`: cylinder-clip ghost shader variant (2.10).
- `STUB(graphics)`: cylinder edge curve geometry from stratigraphy data (2.11).
- `STUB(graphics)`: cylinder-clipped vertex offset for explosion (3.12).
- `STUB(graphics)`: triangulated translucent gap patch (3.14).
