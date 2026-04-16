# Superprojekt — Handoff

Research prototype for interactive 3D mesh / pointcloud visualization. Two F# projects in one solution. The client is intentionally thin (must work on desktop + mobile); the server does all heavy compute.

The canonical source of truth is `CLAUDE.md` at the repo root. This document is a running synthesis — if something here contradicts `CLAUDE.md`, trust `CLAUDE.md` and the actual code.

---

## Repo layout

```
C:\repo\Superprojekt\
  CLAUDE.md                     ← authoritative project notes
  scanpin-v2-spec.md            ← older ScanPin spec with open TODOs
  scanpin-v4-explore-spec.md    ← explore-mode spec (untracked, WIP context)
  adaptify.cmd                  ← use this, NOT `dotnet adaptify`
  src/
    Superprojekt/               ← Blazor WASM client (Aardvark.Dom Elm-style)
    Superserver/                ← ASP.NET Core + Giraffe, mesh + spatial query server
  data/                         ← datasets (not in git): <dataset>/<mesh>/*.obj + centroid + atlas
```

Server runs on `http://localhost:5000` and hosts the client's static files.

---

## Client (Superprojekt) — Aardvark.Dom Elm-style

**SDK:** `Microsoft.NET.Sdk.BlazorWebAssembly`, `net8.0`. `WasmBuildNative=true`, `RunAOTCompilation=false`, `PublishTrimmed=false`, `LocalAdaptify=true`.

### Compile order (Superprojekt.fsproj)

```
WavefrontLoader.fs
CameraModel.fs / CameraModel.g.fs        ← OrbitState [<ModelType>]
OrbitController.fs
ScanPinModel.fs / ScanPinModel.g.fs      ← pin types + JSON export
Stratigraphy.fs                          ← server cylinder-eval query, ring data
StratigraphyView.fs                      ← 2D diagram SVG
PinGeometry.fs                           ← prism wireframe, cut plane quad, between-space surfaces
Model.fs / Model.g.fs                    ← top-level [<ModelType>]
Shader.fs                                ← BlitShader, Shader (flatColor)
Update.fs                                ← Message DU + ScanPinUpdate + Update modules
MeshView.fs                              ← LoadedMesh, async mesh load, off-screen render, composition
ServerActions.fs                         ← init (datasets/centroids/bboxes), triggerFilter
SceneGraph.fs (a.k.a. Revolver)          ← full scene graph, pin 3D, explore heatmap FBO
GuiPins.fs                               ← pins tab + floating pin-diagram (SVG + core sample view)
Gui.fs                                   ← burger button, HUD tabs, overlays
ShaderCache.fs
View.fs                                  ← wires renderControl, camera, input → Revolver
Program.fs                               ← Boot.run gl App.app
```

**`.g.fs` files are Adaptify-generated.** Never edit by hand. Regen happens via MSBuild (LocalAdaptify=true), or explicitly via `adaptify.cmd`. Adaptify generates `AdaptiveX` types from `[<ModelType>]` records in `Model.fs` / `CameraModel.fs` / `ScanPinModel.fs`.

### Elm loop

`Model` (record with `[<ModelType>]`) → `Update.update : Message → Model → Model` → `View.view : AdaptiveModel → Env → Dom` → `Boot.run gl App.app`. `App.app` uses the Adaptify-generated `Unpersist.instance`.

### Key model fields (Model.fs)

See CLAUDE.md for the exhaustive list. Highlights:

- `Camera` — OrbitState (target-radius orbit camera)
- `MeshOrder`, `MeshNames`, `MeshVisible`, `MeshesLoaded`
- `CommonCentroid` — world-space origin all loaded meshes are drawn relative to
- `Datasets`, `ActiveDataset`, `DatasetScales`, `DatasetCentroids`
- `Filtered`, `FilterCenter` — sphere query results per mesh
- `RevolverOn`, `FullscreenOn`, `RevolverCenter` — overlay toggles
- `DifferenceRendering`, `Min/MaxDifferenceDepth`, `GhostSilhouette`, `GhostOpacity`
- `ClipActive`, `ClipBox`, `ClipBounds`
- `ScanPins` — ScanPinModel (pins + placement + selection)
- `CoreSampleViewMode | Rotation | PanZ | Zoom` — secondary 3D inspector state
- `Explore` — ExploreMode (Enabled, SteepnessThreshold, **DisagreementThreshold**, HighlightColor, HighlightAlpha)
- `ReferenceAxis` — `AlongWorldZ | AlongCameraView`, drives explore + pin placement

### Off-screen render pipeline (critical)

Per mesh, two texture slices in a packed `Texture2DArray`: slice `2i` solid, `2i+1` ghost. Rendered every frame into separate FBO attachments by `MeshView.buildMeshTextures`.

**Per-mesh off-screen pass** (shader `BlitShader.clippy`):
- Applies per-dataset `Trafo3d.Translation(delta) * Trafo3d.Scale(scale)` — `DatasetScales` defaults 1.0; `SETSM_glacier` = 0.01. Clip bounds scaled to match.
- Solid slice (`IsGhost=false`): discards outside `[ClipMin, ClipMax]` when `ClipActive`; per-mesh color from `colorMap`; hidden mesh gets infinite clip so depth is still written.
- Ghost slice (`IsGhost=true`): discards *inside* the clip box; tinted semi-transparent.

**Composition pass** (`MeshView.composeMeshTextures`, shader `BlitShader.readArray`): fullscreen quad over all slices.
1. Solid loop — front-most solid among visible meshes (`MeshVisibilityMask` bitmask), tracks minDepth/maxDepth for difference metric.
2. Ghost loop (only if `GhostSilhouette`) — alpha-composited in front.
3. Difference rendering — reconstructs world positions of minDepth/maxDepth via `ViewProjTrafoInv`, maps distance to heat color.
4. Samples `exploreSampler` and blends the explore heatmap on top.

**Revolver / fullscreen overlay** uses `readArraySlice` / `readArraySliceColor` instead of the composition quad (single slice, optional circle clip, offset+scale for magnifier disks).

**Core-sample shader** (`BlitShader.coreClip`): cylindrical clip (`XY length > CoreRadius → discard`) used inside the pin-diagram mini view.

**Explore heatmap** (`BlitShader.exploreHeatmap`, SceneGraph.fs `exploreTex`): screen-space post-process, rendered into its own Rgba8 offscreen texture (WebGL2 has no per-attachment blending → it must be isolated). Algorithm per pixel:
- Reconstruct world normals from depth gradients (`ViewProjTrafoInv` on `(ndc, ndc+dx, ndc+dy)`).
- Keep meshes whose `|dot(n, ReferenceAxis)| < SteepnessThreshold` (i.e. steep relative to reference).
- Welford-accumulate **world-space depth along the view ray** — depth = `length(reconstructWorld(ndc, di) - reconstructWorld(ndc, 0))`.
- Discard if `sqrt(variance) < DisagreementThreshold` (threshold is stddev in meters).
- Output `HighlightColor` modulated by `clamp(stddev / (4·threshold)) · HighlightAlpha`.
- Only runs when `Explore.Enabled`; otherwise cleared to transparent.

### Adaptive performance rule (READ THIS)

> **Never depend on an entire record when you only need a subset of its fields.**

The Elm model replaces whole records on every update. `AVal.map` over a full record fires on *any* field change. For hot paths — especially scene-graph construction — project individual fields into separate aVals *early*, then build the dependency graph from those.

```fsharp
// BAD — rebuilds geometry on every pin change
let geo = pinVal |> AVal.map (fun po -> use po.Prism, po.Stratigraphy ...)

// GOOD — only rebuilds when prism or stratigraphy actually change
let prismVal = pinVal |> AVal.map (Option.map (fun p -> p.Prism))
let stratVal = pinVal |> AVal.map (Option.bind (fun p -> p.Stratigraphy))
let geo = (prismVal, stratVal) ||> AVal.map2 (fun prism strat -> ...)
```

For scene-graph nodes (`Sg.Text`, `sg { }`), this matters even more — rebuilding an `AList` destroys GPU resources (font atlases, draw calls). Split **structure** (slowly changing) from **placement** (fast changing): build static sg lists once, use `Sg.Trafo` with a fast-changing trafo aVal for placement. Push adaptivity down, not up.

### ScanPin system

A ScanPin = a 3D annotation: a 32-gon prism extruded along an axis, with a cutting plane that intersects loaded meshes to produce polylines → rendered as an SVG elevation profile.

- **Placement flow:** Place-pin button → tap on 3D surface → prism at anchor with camera-forward axis → sliders adjust cut plane / angle / radius → commit or discard. Escape cancels.
- **State:** `PlacingMode : FootprintMode option` + `ActivePlacement : ScanPinId option`. The `PlacementState` DU in ScanPinModel.fs is dead code (orphaned from v1).
- **3D rendering** (SceneGraph.fs): `pinDots` = clickable spheres (tap select, double-tap focus); `pinPrisms` = wireframe (thin triangle-quads, *not* GL_LINES) + translucent cut plane quad.
- **Floating diagram** (GuiPins.fs `pinDiagram`): screen-space position via `proj.Forward * view.Forward * V4d(pt,1)`; hidden when `W < 0.1` or `|ndc| > 1.5`. SVG is rendered by JS (OnBoot + MutationObserver) reading a `data-diagram` JSON attribute — Aardvark.Dom doesn't support `yield!` in its CE.
- **Core-sample view:** 280×200 secondary `renderControl` inside `pin-diagram`; `coreSampleTrafo` rotates the prism axis to Z; meshes drawn with `coreClip`. Custom pointer handlers, not OrbitController. `CoreSampleViewMode.TopView` is not yet wired up.
- **Stratigraphy** (Stratigraphy.fs, StratigraphyView.fs, SceneGraph.fs): server `/query/cylinder-eval` on a metric radial ladder (one ring per 0.25 m from wall inward, floor 0.02 m). `StratigraphyData.Rings : StratigraphyColumn[][]` (outer → inner; `Rings.[0]` aliases the legacy `Columns` field).
- **Between-space flood fill:** `floodContinuousBand3D` BFS over `(angleIdx, ringIdx, bracketIdx)`; angular neighbors wrap, radial neighbors clamp. Brackets connect iff `overlap > 0.5 · max(len1, len2)` (symmetric majority-overlap — prevents a fat bracket bridging two disjoint thin ones). `floodContinuousBand` wraps it filtered to `ringIdx = 0`.
- **Between-space 3D volume** (`PinGeometry.buildBetweenSpaceSurfaces`, SceneGraph.fs `betweenSpaceBand`): three translucent surfaces — upper (warm white), lower (cream), side walls. Upper/lower: triangulate grid quads with all four corners in the band. Sides: emit on grid edges where both endpoints are in-band but at least one adjacent quad is incomplete. All use `BlendMode.Blend` + `DepthTest.None` in `passTwo` so the volume is visible through meshes. Field-projected aVals (`prismVal`, `stratVal`, `hoverVal`) keep rebuild minimal.
- **Hover state:** `BetweenSpaceHover { ColumnIdx; HoverZ; Pinned }` on ScanPin, toggled by Shift+Left in the 2D diagram.

**Open TODOs** (see `scanpin-v2-spec.md`): real mesh-plane cut (currently `dummyCutResults` = fake sine waves), arcball gizmo, JSON deserialization (export-only today), top-view mode, summary meshes, boxplot ranking.

### Gui module

- `Gui` = burger button + tabbed HUD. Scene tab = dataset selector + scale input + mesh visibility + ghost. Overlay tab = revolver / fullscreen / mesh order / difference rendering / explore mode. Clip tab = per-axis range sliders.
- `GuiPins` owns the Pins tab + floating diagram (SVG + mini core sample).
- Tab IDs `hud-tab1/2/3/4`, panel IDs `hud-panel1/2/3/4`, radio name `hud-tabs`.
- Active CSS: `.tab-labels label:has(:checked)`, `.tabs:has(#hud-tabN:checked) #hud-panelN`.

---

## Server (Superserver)

**SDK:** `Microsoft.NET.Sdk.Web`, `net8.0`. References the client project for static file hosting.

### Compile order

```
MeshLoader.fs    ← OBJ parsing, centroid files, atlas paths
MeshCache.fs     ← Embree scene + BbTree cache, load-on-demand, permanent
Handlers.fs      ← HTTP handlers + routing
Program.fs       ← ASP.NET startup
```

### Data layout

```
data/
  <dataset>/
    <mesh>/
      *.obj              ← one file per mesh part (sorted = index order)
      *centroid.txt      ← "x y z" (may have # comments); V3d.Zero if absent
      *_atlas.jpg / *.jpg
```

### API

```
GET  /api/datasets                              → string[]
GET  /api/datasets/{dataset}/centroids          → { meshName: [x,y,z] }
GET  /api/datasets/{dataset}/bboxes             → { meshName: { min:[x,y,z], max:[x,y,z] } }
GET  /api/datasets/{dataset}/mesh/{name}        → count of OBJ files
GET  /api/datasets/{dataset}/mesh/{name}/{i}    → binary mesh
GET  /api/datasets/{dataset}/mesh/{name}/{i}/atlas → JPEG
POST /api/query/ray                             → { hit, t, point, triangleId }  Name = "dataset/mesh"
POST /api/query/closest                         → { found, point, distanceSquared, triangleId }
POST /api/query/sphere                          → binary: int32 count | int32[] vertexIndices
POST /api/query/box                             → binary: int32 count | int32[] vertexIndices
POST /api/query/plane-intersection              → { segments: [[u0,v0,u1,v1], ...] }
POST /api/query/grid-eval                       → binary grid of per-cell stats inside a prism
POST /api/query/cylinder-eval                   → binary per-ring per-angle mesh-intersection heights
      Request:  { Radii: float[], AngularResolution, ExtentForward, ExtentBackward, ... }
      Response: int32 angularRes | int32 ringCount | int32 hitCount | hitCount × (int32 ring, int32 angle, int32 nameLen, utf8 name, float64 height)
```

- Client mesh names use `"dataset/meshName"` format everywhere. Server splits on first `/`.
- **All query coordinates are absolute world space.** Server converts `localPos = V3f(worldPos - centroid)`.
- Sphere / box results are **vertex indices** (3 per triangle), not triangle IDs.

---

## Aardvark.Dom gotchas (hard-won)

- `Attribute("for", "...")` on `<label>` is silently dropped → nest `<input>` inside `<label>` instead.
- `Attribute("checked", "")` is dropped → use `Attribute("checked", "checked")`.
- CSS `~` sibling combinator breaks (Aardvark inserts wrapper nodes) → use `:has()` on a known ancestor.
- `RenderControlInfo` and `TraversalState` both have `.Runtime` — annotate `(info : Aardvark.Dom.RenderControlInfo)` when ambiguous.
- `yield!` is not supported in Aardvark.Dom CE builders → use OnBoot JS + MutationObserver for dynamic SVG/canvas.
- `NodeBuilder "svg" { ... }` can create arbitrary HTML elements, but SVG attributes need care.
- `renderControl { ... }` can be nested inside `div { ... }` — it creates a WebGL canvas as a child.
- `AVal.map4` doesn't exist — combine `map2` / `map3`.
- `Dom.Style` for renderControl; `Style` for HTML elements (div, button, …).
- `Css.Custom` doesn't exist — use CSS classes in `style.css` for properties not covered by `Css.*`.
- WebGL2 has **no per-attachment blending** — any pass whose blend state differs from its siblings needs its own FBO (see explore heatmap).

---

## Style / UX

- Light theme, `'Segoe UI'` / `'Inter'`, accent `#1a56db`. Body `#f4f6f8`, panel `#ffffff`, text `#0f172a`.
- Render canvas: `linear-gradient(to top, #d0dce8, #eaf1f8)`.
- All styles in `wwwroot/style.css`. No inline styles except model-dependent ones (cursor, 3D-projected diagram position).
- `.pin-diagram` = fixed-position floating overlay. `.pin-mini-view` = 280×200 renderControl inside it. `.btn-active` = darker blue + inset shadow for toggles.

**Code style:**
- No comments unless the logic is non-obvious.
- No unnecessary abstractions or helpers. Three similar lines beats a premature abstraction.
- Concise. Light theme, high contrast, print-appropriate — the UI is meant for publication figures.
- GUI must be understandable to a non-expert at first glance.

---

## Recent changes (session 2026-04-15)

**Explore mode "Change sensitivity" was broken** — it was a variance threshold over raw NDC depth-buffer values (non-linear, tiny numeric range), and the slider range / label didn't match the semantics. A small nudge past the minimum made everything disappear.

Fix landed across five files:

- **Shader.fs** (`exploreHeatmap`): Welford now accumulates **world-space depth along the view ray** (`length(reconstructWorld(ndc, di) - reconstructWorld(ndc, 0))`). Comparison is `sqrt(variance) < DisagreementThreshold` (stddev in meters). Intensity normalizer rescaled to match.
- **Model.fs**: `ExploreMode.VarianceThreshold` → `DisagreementThreshold`; default `0.05` m.
- **Update.fs**: message `SetVarianceThreshold` → `SetDisagreementThreshold`. Auto-derived default on `ClipBoundsLoaded` is now `clamp 0.001..1.0 (renderDiag * 1e-3)` (≈ 0.1 % of scene diagonal).
- **SceneGraph.fs**: aVal + uniform renamed.
- **Gui.fs**: label changed to "Min depth disagreement". Slider is now **log-scaled 0.001 m → 10 m** across 1000 integer ticks (`disagreementToSlider` / `sliderToDisagreement` helpers). Display: `mm` under 10 cm, `m` above.
- **CLAUDE.md**: model-field list updated.

Build clean (50 pre-existing FShade warnings, 0 errors).

`ExploreMode` is *not* a `[<ModelType>]` — it's a plain record inside `Model`, so renaming the field didn't require `.g.fs` regen.

---

## Git state at handoff

Branch: `master`. `master` is the main branch (no `main`).

Recent commits:
```
122d26f explore overlay
7cabab5 Merge branch 'scanpin-v3'
d7fd850 between-space 3D volume
471a840 hover
d4dd63b between-space hover
2f84a5c labely
c60bf70 girddy
b9f21cb performance
```

Modified but uncommitted (the explore-sensitivity fix above):
```
M CLAUDE.md
M src/Superprojekt/Gui.fs
M src/Superprojekt/Model.fs
M src/Superprojekt/SceneGraph.fs
M src/Superprojekt/Shader.fs
M src/Superprojekt/Update.fs
?? scanpin-v4-explore-spec.md   (untracked — explore-mode design notes)
```

---

## Environment

- OS: Windows 11 (IoT Enterprise LTSC 2024). Shell: bash (Unix syntax, forward slashes in paths).
- User: `aszabo314`, email `e0925269@student.tuwien.ac.at`.
- Run server: standard `dotnet run` in `src/Superserver`.
- Build client: `dotnet build` in `src/Superprojekt`.
- Regenerate Adaptify files: `adaptify.cmd` (NOT `dotnet adaptify`).
- Server serves the client's static output at `http://localhost:5000`.

---

## First things to check when picking this up

1. Read `CLAUDE.md` top to bottom — it's ~200 lines and covers everything.
2. Skim `scanpin-v2-spec.md` for open ScanPin TODOs.
3. `scanpin-v4-explore-spec.md` is the latest design note for explore mode (untracked; context only).
4. If a scene-graph rebuild feels too frequent, re-read the adaptive performance section above — 9 times out of 10 the problem is an `AVal.map` over a full record.
5. If something about `[<ModelType>]` or `.g.fs` looks off after editing `Model.fs` / `CameraModel.fs` / `ScanPinModel.fs`, run `adaptify.cmd`.
