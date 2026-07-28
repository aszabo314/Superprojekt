# Superprojekt

Research prototype for interactive 3D inspection and **registration** of geological mesh datasets (multi-epoch scans of the same terrain). Two F# projects:

- **Superserver** — ASP.NET Core + Giraffe. Serves mesh data and runs spatial queries (Embree BVH ray/closest-point, sphere contact rings, the pairwise symmetric M3C2-style error measure, per-vertex signed distance, and a weighted rigid landmark solve). Also hosts the WASM client.
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

Open `http://localhost:5000`. The default dataset (read from `src/Superserver/data/default.txt`) auto-loads on first paint; `?dataset=<name>` in the URL overrides it (unknown names fall back to the file).

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

The app registers the epochs of a dataset **pairwise**: you build a tree of pairwise registrations, one pair at a time, and inspect the residual error inside each pair. Everything is organized around one navigation hierarchy — a mesh×mesh **matrix** at the top, a per-pair **cell workspace** one level down. `Esc` always backs out of the innermost thing first.

- **Explore.** Orbit camera (drag = rotate, scroll = zoom, middle-drag or Shift+left-drag = pan); double-tap recenters on the terrain (works on ghosted meshes too). Hold **Space** for a clean view — every annotation (pins, flags, cross, outlines) hides while held. Per-mesh image-space silhouette outlines + world-Z elevation isolines are always on; the top-bar **▤ Cut** slider slices away terrain in front of the camera (near-plane cut with a flat-ink intersection band). Rendering mode (Textured / Shaded / Slope) and the tuning sliders (outline threshold + thickness, isoline density + opacity, ghost floor, shading, flag scale, default pin radius) live in the **⚙ gear** popover.
- **Survey (Setup tab).** The left navigator opens on the **Setup** view: one two-line row per mesh — the name line (colour swatch, number, name, ★ when it is the root) above a control line. Double-click the name line to fly the camera to that mesh's scan-sensor viewpoint. **☆ Set reference** designates the mesh as the **reference root** of the registration tree (the only way to change it) — re-rooting onto a mesh already inside the registered tree *keeps* the registration (path edges reverse), designating an outside mesh clears it. **◉ Isolate** shows the mesh alone: hovering the button previews, clicking locks the isolation while you stay in Setup, clicking again — or leaving Setup — clears it. The control line also carries the per-mesh intrinsic heatmap switches (**Tex**tured / **D**i**st**ance range / **Sh**a**p**e quality / **Inc**idence angle).
- **The pair matrix (Pairs tab).** Rows and columns are meshes; the upper-triangular cell (A,B) *is* the pair. Three states: a background hole = the pair doesn't overlap enough to register (checked server-side); an outlined vessel = possible; a filled cell = registered, fill strength = the solve's quality score. The diagonal is decorative — inert slashed placeholders, since a mesh has no pair with itself. Hovering any cell previews the pair in 3D: only the area where both meshes' footprints overlap on screen keeps its normal rendering, everything else drops to the ghost floor — so you see what a registration of that pair would actually work with before descending. The order bar re-sorts rows/columns by sensor order, coverage footprint, or tree distance from the root.
- **Descend into a pair.** Clicking a possible or registered cell descends into the **cell workspace**: only the two meshes of the pair render, the panel shows the pair header (A ↔ B), a live status line (registered at quality q / not registered / insufficient overlap), and the pair tools. `Esc` or the ‹ button ascends back to the matrix.
- **Pins.** A ScanPin marks a corresponding surface patch in both meshes of the pair. **○ New pin** starts an atomic transaction: pick the **◯ Area** (a sphere on either mesh — the pin anchors to whichever mesh you hit and rides its pose from then on) and **✚ two points**, one on each mesh (the mesh you hit attributes the point; re-picking replaces it), in any order; **✓ Commit** creates the pin, **✕**/`Esc` rolls the draft back completely — no partial pin ever exists. The draft renders all-white; committed point markers are mesh-coloured with a white outline. Pin rows in the workspace give a radius slider, per-mesh point re-pick, and delete. Each pin renders its sphere-surface contact rings and a screen-constant flag (pole + name).
- **Solve.** With ≥3 pins, **⌖ Solve** runs the weighted rigid landmark solve over the pin point pairs and adds the result as an edge of the registration tree (the un-registered mesh moves onto the treed one; solving an already-registered pair replaces the edge). Editing or deleting any pin of a registered pair drops that edge — and everything registered through it — because the solve is no longer backed by its inputs.
- **Loops.** Solving a pair whose meshes are *both* already in the tree would close a loop; the app measures how much the two paths disagree (angle + displacement) and opens a blocking dialog: keep the new edge and remove one existing edge of the loop (the weakest is pre-selected), or cancel and keep the old tree. The committed registration is always a spanning tree.
- **Inspect a pair.** The workspace's chart shows the moving mesh's error across the pair's pins as a stacked histogram (mm axis, per-pin medians, the pooled level-of-detection band). Once the pair is registered the chart overlays the *before* distribution as a step outline — the solve visibly collapses the error. Drag an x-range across the chart to **brush**: the brushed samples light up as dots in 3D (error maps stand down while a brush is active); hovering a dot cross-highlights the chart and reads out the exact local error. **Error map** paints the signed difference on the moving mesh (diverging colour ramp + value isolines, shared range capped at ±0.5 m, bottom-centre legend); **⊕ Probe** arms a click-anywhere exact readout.
- **Peek keys.** Two spring-loaded comparators inside a cell: hold **V** and the moving mesh blinks off (is this the same rock?); hold **B** and it snaps to its as-loaded pose (did registration help?). Release restores instantly; nothing refetches.

## Architecture

Elm-style: one `[<ModelType>]` `Model` → `Update.update` → `View.view`, wired through Aardvark.Dom's `Boot.run`; `Adaptify` generates the adaptive `*.g.fs` views (never edit those — re-run `adaptify.sh`). Registration state is a rooted tree of pairwise rigid edges (`RegistrationModel.RegGraph`); mesh poses are composed from it. The server exposes the mesh + query API listed in `CLAUDE.md`, which also documents the coding conventions, render-pipeline contracts, and platform pitfalls.

## Tests

```bash
dotnet run --project src/Supertests        # pure-logic runner; exit code = failures
```

Integration against a running server:

```bash
ASPNETCORE_URLS=http://localhost:8002 dotnet run --project src/Superserver   # terminal 1
node tools/integration.mjs                                                   # terminal 2
```
