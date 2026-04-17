# Handoff — Claude Code Session State

**Date:** 2026-04-17
**Branch:** `master` at `089cc45 current`
**Build status:** Clean, compiles with 0 errors (FShade warnings are pre-existing and ignorable)

---

## What was done this session

### 1. Removed explosion feature (complete)

Deleted all explosion functionality across the codebase:

- `ScanPinModel.fs` — removed `ExplosionState` type, module, and `Explosion` field from `ScanPin`
- `Update.fs` — removed `SetExplosionEnabled`/`SetExplosionFactor` from `ScanPinMessage` DU, removed handler cases, removed `Explosion = ExplosionState.initial` from pin creation
- `Stratigraphy.fs` — removed `explosionOffsetsFromFields` and `explosionOffsets` functions
- `SceneGraph.fs` — removed explosion from `cutResultsDep`/`edgeDeps` tuples, removed offset computation from `cutGeo`/`edgeGeo`
- `MeshView.fs` — removed `explosionOffset` parameter from `renderMesh`, removed `ExplosionOffset` uniform, removed `BlitShader.explode` from shader pipeline, removed `explosionMap` computation
- `Shader.fs` — removed `ExplosionOffset` uniform member and `explode` vertex shader
- `GuiPins.fs` — removed explode toggle button and expansion factor slider
- `Cards.fs` — removed explode toggle button and expansion factor slider

### 2. Removed DepthShade and Isolines (complete)

- `Model.fs` — removed `DepthShadeOn : bool` and `IsolinesOn : bool` fields and initial values
- `Update.fs` — removed `ToggleDepthShade` and `ToggleIsolines` from `Message` DU and handler cases
- `Shader.fs` — removed `DepthShadeOn`, `IsolinesOn`, `IsolineSpacing` uniform members and `depthShade`/`isolines` fragment shaders
- `GuiPins.fs` — removed Depth and Isolines checkboxes
- `Cards.fs` — removed Depth and Isolines checkboxes
- Ran `bash adaptify.sh` to regenerate `Model.g.fs` (removed adaptive members)

### 3. Cut line indicator — switched from WebGL to HTML overlay (complete)

The stratigraphy cut line indicator was invisible because it was rendered as WebGL geometry that had buffer/rendering issues. Replaced with an HTML div overlay:

- `StratigraphyView.fs` — replaced `indicatorGeo`/`buildIndicator`/`buildAngleIndicator` WebGL geometry with `indicatorStyle` that computes CSS for an absolutely-positioned div
  - `AcrossAxis` mode: horizontal line at correct Y% position
  - `AlongAxis` mode: vertical line at correct X% position
  - White 2px line with box-shadow for contrast
  - **Note:** In Normalized mode, AcrossAxis uses global position (not per-column normalization). The old WebGL version handled per-column normalization with a polyline. Could be improved with SVG polyline if needed.
- `wwwroot/style.css` — added `.strat-indicator` class, added `position: relative` to `.card-strat-content .strat-wrapper`
- The `buildIndicator` and `buildAngleIndicator` functions still exist in the file (dead code now) — could be cleaned up

### 4. WebGL empty buffer fix (complete)

Applied dummy vertex buffer guards to prevent `INVALID_OPERATION: drawElements: no buffer is bound to enabled attribute`:

- `StratigraphyView.fs` — base geometry and hover geometry buffers use `safePos`/`safeCol` wrappers that provide `[| V3f.Zero |]`/`[| V4f.Zero |]` when arrays are empty. Also added `Sg.Active` guards on all sg blocks.
- `SceneGraph.fs` — extracted cut/edge line buffers and between-space surface buffers use same dummy buffer pattern

The WebGL error may still occur from other sources — the fix covers all known dynamic-geometry sg blocks in StratigraphyView and SceneGraph. If the error persists, check other renderControl scenes or the main MeshView pipeline.

---

## Known open issues

### Cut line indicator in Normalized mode
In Normalized mode, the AcrossAxis cut line should ideally be a per-column polyline (each column has different Y normalization). Current HTML overlay renders a straight horizontal line using global Y fraction. The old WebGL `buildIndicator` handled this correctly with per-column vertices. Could be fixed by switching the overlay to an SVG polyline that follows per-column normalization.

### WebGL error may still exist
The `drawElements: no buffer is bound to enabled attribute` error was addressed by adding dummy buffers, but the user reported it persisted after the first round of fixes. The source might be elsewhere (main render pipeline in MeshView, or a different renderControl). If it recurs, search for `ArrayBuffer.*\[\|\|` patterns or `Sg.Render` calls where the count can be 0 while vertex attributes use empty arrays.

### Dead code from indicator removal
`buildIndicator` and `buildAngleIndicator` functions in `StratigraphyView.fs` (lines ~152-204 and ~270-286) are no longer called. Safe to delete.

### Cleanup spec status
All 11 items in `scanpin-presentation-cleanup-spec.md` are marked done. The spec is at the project root. The checkmarks were added in a prior session. This session's work was additional cleanup (explosion/depthshade/isoline removal + cut line fix) not in the spec.

---

## Build and run

```bash
# Regenerate adaptive types after model changes
bash adaptify.sh

# Build client
dotnet build src/Superprojekt/Superprojekt.fsproj

# Build server
dotnet build src/Superserver/Superserver.fsproj

# Run server (serves client too)
dotnet run --project src/Superserver/Superserver.fsproj
# Then open http://localhost:5000
```

---

## Key files to know

| File | Role |
|------|------|
| `ScanPinModel.fs` | All ScanPin types (compiled early, before Stratigraphy.fs) |
| `Model.fs` | App state `[<ModelType>]` — triggers Adaptify `.g.fs` generation |
| `Update.fs` | Message DUs + handlers. `ScanPinUpdate` module must precede `Update` module (F# ordering) |
| `Shader.fs` | `BlitShader` (off-screen pipeline) + `Shader` (flat/vertex color, headlight) |
| `MeshView.fs` | Off-screen render pipeline, `renderMesh`, `buildMeshTextures` |
| `SceneGraph.fs` | 3D scene: pin prisms, extracted lines, between-space volumes, hull picking |
| `StratigraphyView.fs` | WebGL stratigraphy diagram + HTML cut line overlay |
| `GuiPins.fs` | Pin tab panel + SVG profile diagram + core sample 3D view |
| `Cards.fs` | Floating card system with stratigraphy + controls content |
| `scanpin-presentation-cleanup-spec.md` | The 11-item cleanup spec (all done) |
| `CLAUDE.md` | Detailed codebase docs — read this first |
