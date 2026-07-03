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
- **Three-mode workflow.** The left rail switches **Overview · Correspondence · Inspect**; the containers (rail, 3D view, focus panel, dock) never move, only their content changes. One shared selection links everything: **hover = peek** highlights the same object in every panel, **click = select/focus** (visibility + highlighting only), **double-click = zoom** both cameras tight onto that thing. A global **Before / After** toggle (top bar, enabled once anything is solved) flips the whole app between load and solved poses; **Peek** beside it holds the other state momentarily.
- **Mesh browser.** The focus panel's tiles list all meshes; each tile focuses on click, peek-isolates on hover, and carries the per-mesh controls: reference **★**, visibility, **◐ isolate**. Isolation is a toggle — a second click (or a workflow switch) ends it and resets every visibility toggle to ON; the visibility buttons are locked while a mesh is isolated.
- **Pins.** In Correspondence, **○ Place pin** + a tap drops a ScanPin (a metric-radius region of interest); a flashlight preview shows where it lands even over ghosted terrain. Each pin gets an immutable glyph + 2-char code + colour identity used everywhere. Every pin runs a server **M3C2 probe** against all meshes and renders equator + contact rings.
- **The matrix.** In Correspondence + Inspect the rail is the **pin × mesh matrix**: cells show each (pin, mesh) median signed distance to the reference on a diverging colormap, stable under visibility changes. Row click = select the pin; column click = focus the mesh; cell click = **locate** that correspondence (isolate the mesh + select the pin); clicking the located cell again backs out. Double-click any of them to additionally fly both cameras tight onto it.
- **Register.** Pick a reference **★**; every pin auto-seeds one correspondence marker per mesh (closest-point, ROI-clamped), refinable via the armed **✎ edit point** editor (click in the focus *or* the 3D view; stays armed). **Solve** (rail) runs a weighted rigid landmark solve for every mesh with ≥3 markers — partial overlap is fine, short meshes are flagged, not blocking. No preview/commit or undo; Before/After is the comparison.
- **Inspect.** With nothing isolated, the 3D view paints the all-mesh **variance** map on the reference. Focus a moving mesh (tile, matrix column, or 3D click) and it is isolated alone, painting its own **difference** or **displacement** field; select a pin and mesh isolation swaps for **pin isolation** (only pin regions stay solid); a matrix-cell locate combines both. Every error map shares **one range** — the min/max over all pin regions, capped at ±0.5 m (no pins ⇒ ±0.5 m) — shown in the bottom-centre **legend** (gradient + ticks + exact range ends). The dock shows a stacked **violin** per moving mesh (pin colours stacked outward from the axis); once solved, the bottom half is **Before** and the top half **After**, with the inactive pose muted — the Before/After toggle and Peek flip the emphasis. Drag an X-range across it to mark the samples in that error band in 3D as dots coloured by value; per-mesh intrinsic heatmaps (incidence / range / triangle shape) are switched per mesh in the Overview roster.

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
