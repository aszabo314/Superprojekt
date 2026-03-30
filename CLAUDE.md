# Superprojekt

Research prototype for interactive 3D mesh/pointcloud visualization. Two F# projects:

- **Superserver** — ASP.NET Core + Giraffe, serves mesh data and spatial queries (Embree BVH)
- **Superprojekt** — Blazor WASM client, Aardvark.Dom Elm-style architecture, WebGL rendering

The client is thin and must work on desktop and mobile. The server does all heavy computation.

## Intended workflow (partially implemented)

1. User loads a dataset (multiple meshes or pointclouds) from the server
2. User explores with: juxtaposition overlays, filters, workspace clipping, mesh difference + false color
3. User places 3D annotations with alternative renderings and statistics *(not yet)*; separate 2D diagrams/charts provide data insights *(not yet)*
4. UX: tabbed side panel (implemented); extra page area for diagrams *(not yet)*
5. User produces screenshots or a report

## Style

- No comments unless the logic is non-obvious
- Concise code; no unnecessary abstractions or helpers
- Light theme, high contrast, print-appropriate — suitable for publication
- GUI must be understandable to a non-expert at first glance

## Client architecture

Elm-style: `Model` → `Update.update` → `View.view` → `Boot.run`

**Compile order** (Superprojekt.fsproj):
```
CameraModel.fs / CameraModel.g.fs
OrbitController.fs
Model.fs / Model.g.fs        ← [<ModelType>], Adaptify-generated .g.fs
Shader.fs                    ← BlitShader + Shader modules
Update.fs                    ← Message DU + update function
MeshView.fs                  ← LoadedMesh, mesh loading, off-screen render, composition
ServerActions.fs             ← init (fetch centroids + bboxes), triggerFilter
Revolver.fs                  ← builds full scene graph; owns buildMeshTextures call
Gui.fs                       ← DOM panels and controls
View.fs                      ← view function + App module
Program.fs
```

**Key modules:**

`Model` — application state. `[<ModelType>]` triggers Adaptify to generate `AdaptiveModel` in `Model.g.fs` — never edit `.g.fs` manually.

`MeshView` — `loadMeshAsync` fetches binary mesh from server (lazy, cached by name). `buildMeshTextures` and `composeMeshTextures` implement the off-screen render pipeline (see below).

`Revolver` — owns the `buildMeshTextures` call; builds the full render scene graph: the composition quad (normal view), fullscreen tile overlay, and circular magnifier disks.

`Gui` — burger button + tabbed HUD (Scene tab: dataset selector buttons + scale input, mesh visibility, ghost; Overlay tab: revolver/fullscreen toggles, mesh order, difference rendering; Clip tab: enable toggle + per-axis range sliders). `fullscreenInfo` overlay (shown when fullscreen is on). `debugLog` overlay.

`ServerActions` — `init`: fetches dataset list, emits `DatasetsLoaded`. `loadDataset`: fetches centroids + bboxes for a dataset, emits `CentroidsLoaded`/`ClipBoundsLoaded`. `triggerFilter`: sphere query on all meshes at tap/long-press position, emits `FilteredMeshLoaded`.

`View` — wires up renderControl, orbit camera, keyboard/pointer events, revolver state (`shiftHeld`, `spaceHeld`, `cursorPosition` as local `cval`s), calls `Revolver.build`.

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

## CSS / design

- Light theme, `'Segoe UI'`/`'Inter'`, accent `#1a56db`
- Body bg `#f4f6f8`, panel bg `#ffffff`, text `#0f172a`
- Render canvas (`.render-control`): `linear-gradient(to top, #d0dce8, #eaf1f8)`
- All styles in `wwwroot/style.css`; no inline styles except model-dependent ones (e.g. cursor)

## fsproj notes

- Client: `Microsoft.NET.Sdk.BlazorWebAssembly`, `net8.0`, `WasmBuildNative=true`, `LocalAdaptify=true`
- Server: `Microsoft.NET.Sdk.Web`, `net8.0`; references client project for static file hosting
- Server runs on `http://localhost:5000`
