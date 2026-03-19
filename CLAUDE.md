# Superprojekt

Research prototype for interactive 3D mesh/pointcloud visualization. Two F# projects:

- **Superserver** — ASP.NET Core + Giraffe, serves mesh data and spatial queries (Embree BVH)
- **Superprojekt** — Blazor WASM client, Aardvark.Dom Elm-style architecture, WebGL rendering

The client is thin and must work on desktop and mobile. The server does all heavy computation.

## Intended workflow (partially implemented)

1. User loads a dataset (multiple meshes or pointclouds) from the server
2. User explores with: juxtaposition overlays, filters, *(mesh difference + false color — not yet)*, *(clipping volume — not yet)*
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
MeshView.fs                  ← LoadedMesh, mesh loading, off-screen render, blit
Interactions.fs              ← triggerFilter
Revolver.fs                  ← revolver/fullscreen overlay scene graph
Gui.fs                       ← DOM panels and controls
View.fs                      ← view function + App module
Program.fs
```

**Key modules:**

`Model` — application state. `[<ModelType>]` triggers Adaptify to generate `AdaptiveModel` in `Model.g.fs` — never edit `.g.fs` manually.

`MeshView` — `loadMeshAsync` fetches binary mesh from server (lazy, cached by name). `buildMeshTextures` renders each mesh off-screen into color+depth textures. `blitQuad` composites them back.

`Revolver` — overlay scene graph: blit quads (with optional dimming), fullscreen tile overlay, circular magnifier disks stacked by mesh order.

`Gui` — burger button + tabbed HUD (Scene tab: visibility, filter; Overlay tab: revolver/fullscreen toggles, mesh order). `debugLog` overlay.

`Interactions` — `triggerFilter`: sphere query on all meshes at tap/long-press position, emits `FilteredMeshLoaded`.

`View` — wires up renderControl, orbit camera, keyboard/pointer events, revolver state (`shiftHeld`, `spaceHeld`, `cursorPosition` as local `cval`s), calls `Revolver.build`.

## Client model fields

```fsharp
Camera, MeshOrder, MeshNames, MeshVisible, CommonCentroid, MenuOpen
Filtered, FilterCenter      // active filter: HashMap<meshName, vertexIndices[]>
DebugLog                    // rolling log, max 20 entries
RevolverOn, FullscreenOn    // GUI toggles (combined with shift/space keys in View)
RevolverCenter              // NDC anchor when revolver activated via GUI (not shift)
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
  <dataset-name>/
    *.obj                   ← one file per mesh part (sorted = index order)
    *_centroid.txt          ← "x y z" absolute world-space centroid
    *_atlas.jpg             ← texture atlas per mesh part
```

**API endpoints:**
```
GET  /api/centroids              → { name: [x,y,z] }
GET  /api/mesh/{name}            → count of OBJ files
GET  /api/mesh/{name}/{i}        → binary: "MESH" | int32 posCount | int32 idxCount | float64×3 centroid | float32×3[] positions | float32×2[] uvs | int32[] indices
GET  /api/mesh/{name}/{i}/atlas  → JPEG
POST /api/query/ray              → { hit, t, point, triangleId }
POST /api/query/closest          → { found, point, distanceSquared, triangleId }
POST /api/query/sphere           → binary: int32 count | int32[] vertexIndices
POST /api/query/box              → binary: int32 count | int32[] vertexIndices
```

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
