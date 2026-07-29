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

The app registers the epochs of a dataset **pairwise**: you build a tree of pairwise registrations, one pair at a time, and inspect the residual error inside each pair. Navigation is a four-stop **focus rail** at the top of the left panel — **Setup · Matrix · Pair · Pin** — each stop a strictly smaller focus: survey the dataset, pick a pair, work that pair, configure one pin. Jump freely among the enabled stops (Pair unlocks once a pair is chosen, Pin once a pin is chosen or being placed); the rail remembers your last pair and last pin, so hopping out and back restores where you were. `Esc` always backs out of the innermost thing first, then climbs the rail one level.

- **Explore.** Orbit camera (drag = rotate, scroll = zoom, middle-drag or Shift+left-drag = pan); double-tap recenters on the terrain (works on ghosted meshes too). Hold **Space** for a clean view — every annotation (pins, flags, cross, outlines) hides while held. Per-mesh image-space silhouette outlines + world-Z elevation isolines are always on; the top-bar **▤ Cut** slider slices away terrain in front of the camera (near-plane cut with a flat-ink intersection band). Rendering mode (Textured / Shaded / Slope) and the tuning sliders (outline threshold + thickness, isoline density + opacity, ghost floor, shading, flag scale, default pin radius) live in the **⚙ gear** popover.
- **Survey (Setup).** The rail opens on **Setup**: one two-line row per mesh — the name line (colour swatch, number, name, ★ when it is the root) above a control line — plus a **survey-tile strip** down the right edge: one small top-down view per mesh (small multiples), following that mesh's heatmap switch, so you can compare all epochs at a glance — drag inside a tile to pan, scroll to zoom toward the cursor, and drag the strip's left edge to resize it. Double-click a row or a tile to fly the camera to that mesh's scan-sensor viewpoint. The reference mesh is marked in gold in the matrix heads too. **☆ Set reference** (on the row *and* the tile) designates the mesh as the **reference root** of the registration tree (the only way to change it) — re-rooting onto a mesh already inside the registered tree *keeps* the registration (path edges reverse), designating an outside mesh clears it; a root change also resets the pair/pin selection. **◉ Isolate** shows the mesh alone: hovering the button previews, clicking locks the isolation while you stay in Setup, clicking again — or leaving Setup — clears it. The control line also carries the per-mesh intrinsic heatmap switches (**Tex**tured / **D**i**st**ance range / **Sh**a**p**e quality / **Inc**idence angle).
- **The pair matrix (Matrix).** Rows and columns are meshes; the upper-triangular cell (A,B) *is* the pair. Three states: a background hole = the pair doesn't overlap enough to register (checked server-side); an outlined vessel = possible; a filled cell = registered, fill strength = the solve's quality score. The diagonal is decorative — inert slashed placeholders, since a mesh has no pair with itself. Hovering any cell previews the pair in 3D: only the area where both meshes' footprints overlap on screen keeps its normal rendering, everything else drops to the ghost floor — so you see what a registration of that pair would actually work with before committing to it. Clicking a possible or registered cell **selects the pair** and enters its Pair level; the selected cell stays highlighted when you come back. The order bar re-sorts rows/columns by sensor order, coverage footprint, or tree distance from the root.
- **The pair workspace (Pair).** Only the two meshes of the pair render; the panel shows the pair header (A ↔ B), a live status line (registered at quality q / not registered / insufficient overlap), and the pair tools: pin list, solve, error inspection. Clicking a pin row selects it (unlocking the **Pin** stop); double-clicking opens it in the Pin panes. Rows carry a radius slider and delete. `Esc` ascends back to the matrix.
- **Pins (Pin).** A ScanPin marks a corresponding surface patch in both meshes of the pair. **○ New pin** (in the pair workspace) opens the **Pin level: two picking tiles, mesh A above mesh B**, in a thin right-edge strip just like the survey tiles — same top-down view per mesh (drag pans, scroll zooms toward the cursor; each mesh keeps its view between its Setup tile and its Pin tile), same left-edge drag to resize the strip — while the main 3D keeps showing the pair beside it. The tiles are where you pick. Pick the **◯ Area** (a sphere, in either tile — the pin anchors to that tile's mesh and rides its pose from then on) and **✚ two points** — one **in tile A** (→ mesh A) and one **in tile B** (→ mesh B), in any order; a click picks, a drag pans. While placing, each tile shows only the area where the two meshes overlap in its view (the rest turns ghost) — the valid placement region. **✓ Commit** creates the pin, **✕** aborts in place, and leaving the Pin level (Esc or a rail jump) also rolls the draft back completely — no partial pin ever exists. The draft renders all-white; committed point markers are mesh-coloured with a white outline. To move an existing pin's points: select it in Pair, descend to Pin, and click the tile whose point you want to re-pick. Each pin renders its sphere-surface contact rings and a screen-constant flag (pole + name) in the main view.
- **Solve.** With ≥3 pins, **⌖ Solve** runs the weighted rigid landmark solve over the pin point pairs and adds the result as an edge of the registration tree (the un-registered mesh moves onto the treed one; solving an already-registered pair replaces the edge). Editing or deleting any pin of a registered pair drops that edge — and everything registered through it — because the solve is no longer backed by its inputs.
- **Loops.** Solving a pair whose meshes are *both* already in the tree would close a loop; the app measures how much the two paths disagree (angle + displacement) and opens a blocking dialog: keep the new edge and remove one existing edge of the loop (the weakest is pre-selected), or cancel and keep the old tree. The committed registration is always a spanning tree.
- **Inspect a pair.** The workspace's chart shows the moving mesh's error across the pair's pins as a stacked histogram (mm axis, per-pin medians, the pooled level-of-detection band). Once the pair is registered the chart overlays the *before* distribution as a step outline — the solve visibly collapses the error. Drag an x-range across the chart to **brush**: the brushed samples light up as dots in 3D (error maps stand down while a brush is active); hovering a dot cross-highlights the chart and reads out the exact local error. **Error map** paints the signed difference on the moving mesh (diverging colour ramp + value isolines, one shared in-cell range capped at ±0.5 m, pale grey = no data, bottom-centre legend); **⊕ Probe** arms a click-anywhere exact readout — the value pops up as a small tooltip right at the picked point (hovered brushed dots get the same tooltip).
- **Peek keys.** Two spring-loaded comparators at the Pair level: hold **V** and the moving mesh blinks off (is this the same rock?); hold **B** and it snaps to its as-loaded pose (did registration help?). The top bar carries matching **hold-down buttons** (◌ V / ↺ B) while a pair is open. Release restores instantly; nothing refetches.

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
