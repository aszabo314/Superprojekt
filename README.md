# Superprojekt

Research prototype for interactive 3D inspection and **registration** of geological mesh datasets (multi-epoch scans of the same terrain). Two F# projects:

- **Superserver** — ASP.NET Core + Giraffe. Serves mesh data and runs spatial queries (Embree BVH ray/closest-point, sphere contact rings, per-vertex signed distance, N-mesh M3C2 probes, and a weighted rigid landmark solve). Also hosts the WASM client.
- **Superprojekt** — Blazor WebAssembly client. Aardvark.Dom Elm-style architecture, WebGL2 rendering. Runs on desktop and mobile browsers; thin client by design — heavy compute lives on the server.

## Run it

You need the .NET 8 SDK and a recent browser. First-time setup:

```bash
dotnet tool restore
dotnet paket restore
```

Start the server (serves both the API and the WASM client at `http://localhost:5000`):

```bash
dotnet run --project src/Superserver
```

Open `http://localhost:5000`. The default dataset (read from `src/Superserver/data/default.txt`) auto-loads on first paint.

### Datasets

Put OBJ files in `src/Superserver/data/<dataset>/<mesh>/`:

```
data/
  default.txt              # contents = name of default dataset (optional)
  <dataset>/
    pano-centers.txt       # optional: "<mesh-folder> x y z" per line — scan-camera positions
    <mesh>/
      *.obj                # one file per mesh part
      *centroid.txt        # "x y z" (V3d.Zero if absent)
      *_atlas.jpg or *.jpg # texture atlas
```

The first request for a mesh parses the OBJ + builds an Embree scene + BbTree; the result is cached for the process lifetime (the dataset bbox fetch warms the cache up front).

### Docker

`Dockerfile` + `docker-compose.yml` package the server + published WASM bundle behind nginx. `docker-compose up` builds and runs on port 80.

## What you can do

- **Explore.** Orbit camera (drag = rotate, scroll = zoom, middle-drag or Shift+left-drag = pan in the XY plane); double-tap to recenter (works on ghosted meshes too); Space = fullscreen; Alt+scroll cycles which mesh under the cursor is isolated. Per-mesh image-space silhouette outlines + global-Z elevation isolines are always on; rendering mode, ghost floor (faint **Ghost** vs **Hidden**), and tuning sliders live in the **⚙ gear** popover.
- **Three-mode workflow.** The left rail switches **Overview · Correspondence · Inspect**; the containers (rail, 3D view, focus panel, dock) never move, only their content changes. One shared selection links everything: **hover = peek** highlights the same object in every panel, **click = select** (one selection — mesh, pin, or (pin, mesh) cell — that every panel follows; the focus panel frames it), **double-click = zoom** the main 3D camera tight onto that thing. A global **Before / After** toggle (top bar, enabled once anything is solved) flips the whole app between load and solved poses; **Peek** beside it holds the other state momentarily.
- **Mesh browser.** The focus panel's tiles list all meshes — purely the small view (a thumbnail in the active **Top / 360°** projection + pin influence circles + identity label); the reference **★** picker lives in the Overview rail's mesh list. The projection toggle switches the whole panel — single and tiles together — and the selection close-up carries over: the same pin/point stays framed in either projection. A tile click follows the selection: nothing or a mesh selected → select this mesh; a pin or cell selected → select that pin's cell on this mesh (the locate); clicking the tile of the already-selected target zooms the main 3D onto it (same as the matrix double-click).
- **Pins.** In Correspondence, **○ Place pin** + a tap drops a ScanPin (a metric-radius region of interest); a flashlight preview shows where it lands even over ghosted terrain. Each pin gets an immutable 2-char code + colour identity used everywhere — shown as a colour-filled chip with the name inside. In 3D each pin carries a screen-constant **flag** (base cross, pole, top ring, camera-facing name; world height clamped to 0.1–20 m, scale multiplier in the ⚙ gear). Every pin runs a server **M3C2 probe** against all meshes and renders equator + contact rings.
- **The matrix.** In Correspondence + Inspect the rail is the **pin × mesh matrix**: each cell is a small **cross-section slice diagram** — the reference profile thickened by ±LoD₉₅ as a grey band, this mesh's profile as a black line, cut along the pin's dip direction (one shared line per row) with a shared window and vertical scale across all cells. Line inside the band = agrees within detection; a parallel gap = datum offset; a wedge = tilt; off-frame = clipped with an edge arrow. The cells follow the Before/After toggle, so a solve visibly drops every line into its band. The reference column is framed gold; selecting anything dims all cells outside the selected row/column (the cross emerges by contrast) and accents the headers. Row click = select the pin; column click = focus the mesh; cell click = **locate** that correspondence (isolate the mesh + select the pin); clicking the located cell again backs out. The focus panel frames whatever is selected by itself; double-click any of them to additionally fly the 3D camera tight onto it. Slice tunables (window, context slices, vertical percentile) live in the ⚙ gear.
- **Register.** Pick a reference **★**; every pin auto-seeds one correspondence marker per mesh (closest-point, ROI-clamped), refinable via the armed **✎ edit point** editor (click in the focus *or* the 3D view; stays armed). **Solve** (rail) runs a weighted rigid landmark solve for every mesh with ≥3 markers — partial overlap is fine, short meshes are flagged, not blocking. No preview/commit or undo; Before/After is the comparison.
- **Inspect.** Inspect drops the photo textures and ghost fills: false-colour maps paint on a plain shaded base and every non-emphasized mesh reduces to its outline. With nothing isolated, the 3D view paints the all-mesh **variance** map on the reference. Focus a moving mesh (tile, matrix column, or 3D click) and it is isolated alone, painting its own **difference** or **displacement** field; select a pin and any mesh isolation clears — the meshes show fully (pin isolation is exclusive to the Register phase); a matrix-cell locate isolates the mesh. Every error map shares **one range** — the min/max over all pin regions, capped at ±0.5 m (no pins ⇒ ±0.5 m) — shown in the bottom-centre **legend** (gradient + ticks + exact range ends). The dock shows ONE stacked **histogram** for the current selection (mesh → stacked by pin, pin → stacked by mesh, cell → that single pair, nothing → the ensemble); once solved, the inactive pose is over-drawn as a black outline of its shape — the Before/After toggle and Peek flip which pose is filled. Drag an X-range across it to brush: the error maps stand down and only the brushed samples show, as dots coloured by value (3D + focus) on the shared range; per-mesh intrinsic heatmaps (incidence / range / triangle shape) are switched per mesh in the Overview roster.

## Architecture

Elm-style: one `[<ModelType>]` `Model` → `Update.update` → `View.view`, wired through Aardvark.Dom's `Boot.run`; `Adaptify` generates the adaptive `*.g.fs` views (never edit those — re-run `adaptify.sh`). The server exposes the mesh + query API listed in `CLAUDE.md`, which also documents the coding conventions, render-pipeline contracts, and platform pitfalls.

## Tests

```bash
dotnet run --project src/Supertests        # pure-logic runner; exit code = failures
```

Integration against a running server:

```bash
ASPNETCORE_URLS=http://localhost:8002 dotnet run --project src/Superserver   # terminal 1
node tools/integration.mjs                                                   # terminal 2
```
