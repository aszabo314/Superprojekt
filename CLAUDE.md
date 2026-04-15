# Superprojekt

Research prototype for interactive 3D mesh/pointcloud visualization. Two F# projects:

- **Superserver** — ASP.NET Core + Giraffe, serves mesh data and spatial queries (Embree BVH)
- **Superprojekt** — Blazor WASM client, Aardvark.Dom Elm-style architecture, WebGL rendering

The client is thin and must work on desktop and mobile. The server does all heavy computation.

## Intended workflow (partially implemented)

1. User loads a dataset (multiple meshes or pointclouds) from the server
2. User explores with: juxtaposition overlays, filters, workspace clipping, mesh difference + false color
3. User places 3D annotations ("ScanPins") with cut-plane diagrams *(partially implemented)*
4. UX: tabbed side panel (implemented); floating pin diagrams near 3D anchor points *(implemented)*
5. User produces screenshots or a report

## Style

- No comments unless the logic is non-obvious
- Concise code; no unnecessary abstractions or helpers
- Light theme, high contrast, print-appropriate — suitable for publication
- GUI must be understandable to a non-expert at first glance

## Adaptive performance (critical)

In the scene graph, **never depend on an entire record when you only need a subset of its fields**. The Elm-style model replaces entire records on every update, so an `AVal.map` over a full `ScanPin` (or similar) will fire on *any* field change — even fields the computation doesn't use.

**Rule: project individual fields into separate `aval`s early, then build the dependency graph from those.**

```fsharp
// BAD — rebuilds geometry on every pin change (cut plane drag, selection, etc.)
let geo = pinVal |> AVal.map (fun po -> ... use po.Prism and po.Stratigraphy ...)

// GOOD — only rebuilds when prism or stratigraphy actually change
let prismVal = pinVal |> AVal.map (fun po -> po |> Option.map (fun p -> p.Prism))
let stratVal = pinVal |> AVal.map (fun po -> po |> Option.bind (fun p -> p.Stratigraphy))
let geo = (prismVal, stratVal) ||> AVal.map2 (fun prism strat -> ...)
```

For scene graph nodes (`Sg.Text`, `sg { ... }`), this matters even more: rebuilding an `AList` of sg nodes destroys and recreates GPU resources (font atlases, draw calls). Instead:
- **Split structure from placement.** Build static sg node lists from slowly-changing data (e.g. tick count/text from prism geometry). Use adaptive `Sg.Trafo` for fast-changing placement (e.g. cut plane position → trafo update is just a uniform, no sg rebuild).
- **Push adaptivity down.** A parent `AList.ofAVal` that rebuilds all children is expensive. An `AVal`-driven `Sg.Trafo` on each stable child is cheap.

## Client architecture

Elm-style: `Model` → `Update.update` → `View.view` → `Boot.run`

**Compile order** (Superprojekt.fsproj):
```
WavefrontLoader.fs
CameraModel.fs / CameraModel.g.fs
OrbitController.fs
ScanPinModel.fs / ScanPinModel.g.fs  ← ScanPin types + serialization
Model.fs / Model.g.fs                ← [<ModelType>], Adaptify-generated .g.fs
Shader.fs                            ← BlitShader (clippy, coreClip, readArray…) + Shader (flatColor)
Update.fs                            ← Message DU, ScanPinUpdate module, Update module
MeshView.fs                          ← LoadedMesh, mesh loading, off-screen render, composition
ServerActions.fs                     ← init (fetch centroids + bboxes), triggerFilter
Revolver.fs                          ← full scene graph; owns buildMeshTextures; pin dots + prism wireframes
GuiPins.fs                           ← ScanPin tab panel + floating diagram overlay (SVG + core sample 3D view)
Gui.fs                               ← burger button, HUD tabs (Scene/Overlay/Clip), overlays
View.fs                              ← view function + App module
ShaderCache.fs
Program.fs
```

**Key modules:**

`Model` — application state. `[<ModelType>]` triggers Adaptify to generate `AdaptiveModel` in `Model.g.fs` — never edit `.g.fs` manually.

`MeshView` — `loadMeshAsync` fetches binary mesh from server (lazy, cached by name). `buildMeshTextures` and `composeMeshTextures` implement the off-screen render pipeline (see below).

`Revolver` — owns the `buildMeshTextures` call; builds the full render scene graph: the composition quad (normal view), fullscreen tile overlay, circular magnifier disks, and ScanPin 3D elements (`pinDots` for clickable markers, `pinPrisms` for wireframe prisms + cut plane quads). `buildPrismWireframe` and `buildCutPlaneQuad` are public — reused by GuiPins for the core sample view.

`GuiPins` — ScanPin-specific GUI: `pinsTabPanel` (placement controls, cut plane sliders, pin list with focus/delete), `pinDiagram` (floating overlay positioned via 3D→screen projection: SVG profile via OnBoot JS + MutationObserver, and a secondary `renderControl` "core sample" 3D view). Also owns `shortName` utility, `coreSampleTrafo` (aligns prism axis to Z).

`Gui` — burger button + tabbed HUD (Scene tab: dataset selector buttons + scale input, mesh visibility, ghost; Overlay tab: revolver/fullscreen toggles, mesh order, difference rendering; Clip tab: enable toggle + per-axis range sliders). Delegates Pins tab to `GuiPins.pinsTabPanel`. Also: `fullscreenInfo` overlay, `debugLog` overlay, `coordinateDisplay`.

`ServerActions` — `init`: fetches dataset list, emits `DatasetsLoaded`. `loadDataset`: fetches centroids + bboxes for a dataset, emits `CentroidsLoaded`/`ClipBoundsLoaded`. `triggerFilter`: sphere query on all meshes at tap/long-press position, emits `FilteredMeshLoaded`.

`View` — wires up renderControl, orbit camera, keyboard/pointer events, revolver state (`shiftHeld`, `spaceHeld`, `cursorPosition` as local `cval`s), calls `Revolver.build`. Passes view trafo + viewport size to `GuiPins.pinDiagram` for perspective-correct positioning.

`ScanPinModel` — data types (`ScanPinId`, `SelectionPrism`, `CutPlaneMode`, `ScanPin`, `ScanPinModel`, etc.) + `ScanPinSerialize` module (JSON export only, no import yet).

`Update` — contains `ScanPinUpdate` module (must appear before `Update` module to avoid F# forward-reference errors). `ScanPinUpdate.update` handles all `ScanPinMessage` cases. Currently uses `dummyCutResults` (fake sine waves) instead of real mesh-plane intersection.

## ScanPin system

A ScanPin is a 3D annotation: a selection prism (32-gon cylinder) extruded along an axis, with a cutting plane that intersects loaded meshes. The cut produces polylines rendered as an SVG elevation profile diagram.

**Placement workflow:** Place pin button → tap on 3D surface → prism created at anchor with camera-forward axis → adjust cut plane (along/across), angle/distance, radius via sliders → commit or discard. Escape cancels.

**State:** `PlacingMode : FootprintMode option` (waiting for anchor click) + `ActivePlacement : ScanPinId option` (pin being edited). The `PlacementState` DU in ScanPinModel.fs is dead code (orphaned from original spec).

**3D rendering** (in Revolver.fs): `pinDots` renders clickable spheres at anchor points (tap=select, double-tap=focus camera). `pinPrisms` renders wireframe (thin triangle-quads, no GL_LINES) + translucent cut plane quad per pin.

**Diagram** (in GuiPins.fs): `pinDiagram` computes screen position via `proj.Forward * view.Forward * V4d(pt, 1.0)` (column-vector convention: clip = proj * view * pos). Hidden when behind camera (W < 0.1) or off-screen (|ndc| > 1.5). SVG rendered by JS reading `data-diagram` JSON attribute, with MutationObserver for reactive updates.

**Core sample 3D view** (in GuiPins.fs): A secondary `renderControl` embedded in `pinDiagram`, stacked below the SVG profile. Shows the selected pin's region as a vertical "core sample" — the prism axis is rotated to Z and centered at the origin via `coreSampleTrafo`. Meshes are rendered with `BlitShader.coreClip` (cylindrical discard: fragments with XY distance > footprint radius are discarded). The prism wireframe and cut plane quad are pre-transformed into core sample space. Uses an orthographic projection (`Frustum.ortho`) with constrained side-view camera: horizontal drag rotates around Z axis (`CoreSampleRotation`), vertical drag pans along Z (`CoreSamplePanZ`), scroll zooms (`CoreSampleZoom`). Camera state stored as three floats on Model; custom pointer handlers in GuiPins.fs (no OrbitController). View matrix built manually via `CameraView(sky, eye, forward, up, right)`. Side/Top view mode toggle (`CoreSampleViewMode`) — TopView not yet wired up.

**Stratigraphy** (Stratigraphy.fs, StratigraphyView.fs, SceneGraph.fs): 2D diagram + 3D between-space picking. `Stratigraphy.compute` queries the server's `/query/cylinder-eval` on a metric radial ladder (one ring per 0.25 m from the prism wall inward, floor 0.02 m). Data is stored in `StratigraphyData.Rings : StratigraphyColumn[][]` (outer→inner; `Rings.[0]` aliases the legacy `Columns` field that drives the 2D diagram — axis stats still come from the outer ring only). `floodContinuousBand3D` BFS-floods a continuous gap across `(angleIdx, ringIdx, bracketIdx)` nodes, seeded at the hovered bracket on the outer ring; angular neighbors wrap, radial neighbors clamp. Two brackets connect iff `overlap > 0.5 * max(len1, len2)` — symmetric majority-overlap rule prevents a fat bracket from bridging two disjoint thin ones. `floodContinuousBand` is a thin wrapper that filters to `ringIdx = 0` for the 2D view. Hover state: `BetweenSpaceHover { ColumnIdx; HoverZ; Pinned }` on ScanPin, toggled by Shift+Left in the diagram.

**Between-space 3D volume** (PinGeometry.fs `buildBetweenSpaceSurfaces`, SceneGraph.fs `betweenSpaceBand`): Renders the continuous-gap volume as three translucent surfaces — upper (warm white), lower (cream), and side walls closing the volume boundary. Upper/lower are triangulated over grid quads where all four `(angle, ring)` corners are in the band; side walls are emitted on any grid edge where both endpoints are in the band but at least one adjacent quad is incomplete. All three draw calls use `BlendMode.Blend` and `DepthTest.None` in `passTwo` so the volume is visible through every mesh. Field-projected aVals (`prismVal`, `stratVal`, `hoverVal`) keep the rebuild cost minimal.

**Open TODOs:** See `scanpin-v2-spec.md` — key gaps: dummy cut results (no real mesh intersection), no arcball gizmo, no deserialization, top view mode, summary meshes, boxplot ranking.

## Off-screen render pipeline

Each mesh has **two texture slices** in a packed `Texture2DArray`: slice `2i` (solid) and slice `2i+1` (ghost). Both are rendered every frame into separate FBO attachments via `buildMeshTextures`.

**Per-mesh off-screen pass** (`MeshView.renderMesh`, shader `BlitShader.clippy`):
- Applies per-dataset scale via `Trafo3d.Translation(delta) * Trafo3d.Scale(scale)` where `DatasetScales` provides scale (default 1.0; SETSM_glacier = 0.01). Clip bounds are scaled to match.
- **Solid slice** (`IsGhost=false`): discards fragments outside `[ClipMin, ClipMax]` (only when `ClipActive`); highlights boundary in a per-mesh color from `colorMap`; respects `active` (mesh visibility drives the clip range — hidden mesh gets infinite clip so it still renders, making its depth available)
- **Ghost slice** (`IsGhost=true`): discards fragments *inside* the clip box (opposite of solid); renders a tinted semi-transparent color for the clipped-away / hidden region

**Composition pass** (`MeshView.composeMeshTextures`, shader `BlitShader.readArray`):

Runs as a fullscreen quad over all mesh slices. Two loops:

1. **Solid loop** — finds the front-most solid fragment among visible meshes (`MeshVisibilityMask` bitmask); tracks `minDepth`/`maxDepth` for difference metric
2. **Ghost loop** (only when `GhostSilhouette=true`) — blends ghost fragments in front of the solid winner using alpha compositing

After the loops, applies **difference rendering**: reconstructs world-space positions of `minDepth`/`maxDepth` via `ViewProjTrafoInv`, computes distance, maps to heat color if `> MinDifferenceDepth`.

Output: single color+depth fragment composited into the main framebuffer.

**Revolver / fullscreen overlay** — instead of the composition quad, `readArraySlice` samples a single slice for fullscreen tile layout; `readArraySliceColor` clips to a circle and applies offset+scale for magnifier disks.

**Core sample shader** (`BlitShader.coreClip`): used by the mini 3D view in GuiPins. Discards fragments where XY distance from origin exceeds `CoreRadius` uniform — a cylindrical clip in core sample space (Z = prism axis, origin = anchor). No box clipping.

**Explore mode heatmap** (`BlitShader.exploreHeatmap`, SceneGraph.fs `exploreTex`): Screen-space post-process that highlights pixels where multiple meshes have steep faces *and* disagree in depth. Renders into a dedicated single-attachment Rgba8 offscreen texture (`CreateTexture2D` + single-color-FBO) so blend state stays isolated — WebGL2 has no per-attachment blending, so the explore pass cannot share an FBO with anything that needs different blend settings. The render task only runs when `model.Explore.Enabled = true`; otherwise the texture is just cleared to transparent. Then `composeMeshTextures` samples the explore texture via `exploreSampler` and alpha-blends it inline at the end of `readArray`, so the final canvas only sees a single draw with one consistent blend state. Algorithm per pixel: reconstruct world-space normals from depth gradients (`ViewProjTrafoInv` on `(ndc, ndc+dx, ndc+dy)`), keep meshes whose `|dot(n, ReferenceAxis)| < SteepnessThreshold`, compute Welford variance of their depths, discard if `< VarianceThreshold`, otherwise output `HighlightColor` modulated by `intensity * HighlightAlpha`. Reference axis comes from `model.ReferenceAxis` (`AlongWorldZ` → `V3d.OOI`; `AlongCameraView` → camera back-Z). Highlight is visible through the `if eCol.W > 0.001` blend in `readArray` even where the underlying scene is empty.

## Client model fields

```fsharp
Camera, MeshOrder, MeshNames, MeshVisible, MeshesLoaded, CommonCentroid, MenuOpen
Filtered, FilterCenter        // active filter: HashMap<meshName, vertexIndices[]>
DebugLog                      // rolling log, max 20 entries
Datasets                      // list of dataset names from server
ActiveDataset                 // currently loaded dataset name
DatasetScales                 // per-dataset render scale (default: {"SETSM_glacier" → 0.01})
DatasetCentroids              // per-dataset mean centroid, populated on CentroidsLoaded
RevolverOn, FullscreenOn      // GUI toggles (combined with shift/space keys in View)
RevolverCenter                // NDC anchor when revolver activated via GUI (not shift)
DifferenceRendering           // heat-color depth difference between mesh layers
MinDifferenceDepth            // world-space distance threshold to start heat coloring
MaxDifferenceDepth            // world-space distance for full heat color saturation
GhostSilhouette               // show semi-transparent ghost of clipped/hidden geometry
ClipActive                    // whether workspace clipping is enabled
ClipBox                       // active clip range (Box3d in world space)
ClipBounds                    // world-space union of all dataset bboxes; Box3d.Invalid until loaded
GhostOpacity                  // ghost silhouette opacity (0.01–1.0, default 0.1)
ScanPins                      // ScanPinModel: pins, active placement, selected pin, placing mode
CoreSampleViewMode            // SideView | TopView toggle for the core sample inspector
CoreSampleRotation            // radians, angle around Z axis in core sample space
CoreSamplePanZ                // vertical pan offset along Z axis
CoreSampleZoom                // ortho half-height scale (zoom level)
Explore                       // ExploreMode: Enabled, SteepnessThreshold, VarianceThreshold, HighlightColor, HighlightAlpha
ReferenceAxis                 // AlongWorldZ | AlongCameraView — global; drives explore heatmap + pin placement
```

## Server architecture

**Compile order** (Superserver.fsproj):
```
MeshLoader.fs    ← OBJ parsing, centroid files, atlas paths
MeshCache.fs     ← Embree scene + BbTree cache (load-on-demand, permanent)
Handlers.fs      ← HTTP handlers + routing
Program.fs       ← ASP.NET startup
```

**Data layout on disk:**
```
data/
  <dataset>/
    <mesh>/
      *.obj                 ← one file per mesh part (sorted = index order)
      *centroid.txt         ← "x y z" (may have # comment lines); V3d.Zero if absent
      *_atlas.jpg or *.jpg  ← texture atlas (both patterns tried)
```

**API endpoints:**
```
GET  /api/datasets                              → string[]
GET  /api/datasets/{dataset}/centroids          → { meshName: [x,y,z] }
GET  /api/datasets/{dataset}/bboxes             → { meshName: { min:[x,y,z], max:[x,y,z] } }
GET  /api/datasets/{dataset}/mesh/{name}        → count of OBJ files
GET  /api/datasets/{dataset}/mesh/{name}/{i}    → binary mesh (same format as before)
GET  /api/datasets/{dataset}/mesh/{name}/{i}/atlas → JPEG
POST /api/query/ray              → { hit, t, point, triangleId }   Name = "dataset/mesh"
POST /api/query/closest          → { found, point, distanceSquared, triangleId }
POST /api/query/sphere           → binary: int32 count | int32[] vertexIndices
POST /api/query/box              → binary: int32 count | int32[] vertexIndices
POST /api/query/plane-intersection → { segments: [[u0,v0,u1,v1], ...] }  2D cut polylines
POST /api/query/grid-eval        → binary grid of per-cell stats within a core sample prism
POST /api/query/cylinder-eval    → binary: per-ring, per-angle mesh-intersection heights
                                    Request: { Radii: float[], AngularResolution, ExtentForward, ExtentBackward, ... }
                                    Response: int32 angularRes | int32 ringCount | int32 hitCount
                                              then hitCount × (int32 ring, int32 angle, int32 nameLen, utf8 name, float64 height)
```

Client mesh names use `"dataset/meshName"` format throughout. Query `Name` field uses the same format; server splits on first `/`.
All query coordinates are **absolute world space**. Server converts: `localPos = V3f(worldPos - centroid)`.
Sphere/box results return **vertex indices** (3 per triangle), not triangle IDs.

## Key Aardvark.Dom gotchas

- `Attribute("for", "...")` on `<label>` is silently dropped — nest `<input>` inside `<label>` instead
- `Attribute("checked", "")` is dropped — use `Attribute("checked", "checked")`
- CSS `~` sibling combinator breaks (Aardvark inserts wrapper nodes) — use `:has()` on a known ancestor
- Active tab CSS: `.tab-labels label:has(:checked)` and `.tabs:has(#hud-tabN:checked) #hud-panelN`
- `RenderControlInfo` and `TraversalState` both have `.Runtime` — annotate `(info : Aardvark.Dom.RenderControlInfo)` when ambiguous
- `yield!` is not supported in Aardvark.Dom computation expression builders — use OnBoot JS with MutationObserver for dynamic SVG/canvas rendering
- `NodeBuilder "svg" { ... }` can create arbitrary HTML elements but SVG attributes need special handling
- `renderControl { ... }` can be nested inside `div { ... }` — it creates a WebGL canvas as a child element
- `AVal.map4` does not exist — combine with `AVal.map2`/`AVal.map3` instead
- `Dom.Style` for renderControl; `Style` for HTML elements (div, button, etc.)
- `Css.Custom` does not exist — use CSS classes in `style.css` for properties not covered by `Css.*`

## CSS / design

- Light theme, `'Segoe UI'`/`'Inter'`, accent `#1a56db`
- Body bg `#f4f6f8`, panel bg `#ffffff`, text `#0f172a`
- Render canvas (`.render-control`): `linear-gradient(to top, #d0dce8, #eaf1f8)`
- All styles in `wwwroot/style.css`; no inline styles except model-dependent ones (e.g. cursor)
- `.pin-diagram`: fixed-position floating overlay, positioned via inline style from 3D projection
- `.pin-mini-view`: 280×200 secondary renderControl inside pin-diagram (core sample 3D view)
- `.btn-active`: darker blue with inset shadow for toggle buttons
- Tab IDs: `hud-tab1/2/3/4`, panel IDs: `hud-panel1/2/3/4`, radio name: `hud-tabs`

## fsproj notes

- Client: `Microsoft.NET.Sdk.BlazorWebAssembly`, `net8.0`, `WasmBuildNative=true`, `LocalAdaptify=true`
- Server: `Microsoft.NET.Sdk.Web`, `net8.0`; references client project for static file hosting
- Server runs on `http://localhost:5000`
- Run Adaptify with `adaptify.cmd` (not `dotnet adaptify`)
