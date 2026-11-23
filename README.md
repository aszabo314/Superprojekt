# Superprojekt

Interactive 3D inspection and **pairwise registration** of geological mesh datasets — multi-epoch scans of the same terrain. Two F# projects:

- **Superserver** — ASP.NET Core + Giraffe. Serves mesh data and runs the spatial queries (Embree BVH ray/closest-point, sphere contact rings, the pairwise symmetric M3C2-style error measure, per-vertex signed distance, a weighted rigid landmark solve). Also hosts the client.
- **Superprojekt** — Blazor WebAssembly client. Aardvark.Dom Elm-style architecture, WebGL2 rendering. Thin by design — heavy compute lives on the server.

## Run it

Needs the .NET 8 SDK and a recent browser.

```bash
dotnet tool restore
dotnet paket restore
dotnet run --project src/Superserver     # API + client on http://localhost:8002
```

The default dataset (`src/Superserver/data/default.txt`) loads on first paint; `?dataset=<name>` overrides it (unknown names fall back to the default). `/study` starts user-study mode.

### Datasets

```
src/Superserver/data/
  default.txt              # name of the default dataset (missing/unknown → first dataset)
  <dataset>/
    <mesh>/
      *.obj                # one file per mesh part (alphabetical order = part order)
      *centroid.txt        # "x y z" — world position of the mesh origin (zero if absent);
                           # for scan data the origin is the scan-sensor station
      *sensor.txt          # optional: the measured sensor station (absence is meaningful)
      <objname>_atlas.jpg  # texture atlas per OBJ file (falls back to <objname>.jpg)
```

A mesh is parsed and indexed on first request and cached for the process lifetime; loading a dataset warms the cache up front.

### Docker

`Dockerfile` packages the published client behind nginx on port 80 with `/api/` proxied to the server inside the container. The checked-in `docker-compose.yml` targets a traefik reverse proxy (external network, no published port) — for a plain local run add a `ports:` mapping or run the image with `-p 80:80`.

### Distributable

```bash
tools/publish-bundle.sh [rids…]          # default: win-x64 osx-arm64 linux-x64 → publish/Superprojekt-*
```

One self-extracting executable per platform containing the server, the client and the `study` + `ScanPin - UserStory` datasets. Starting it serves on `http://127.0.0.1:5055` (a free port if that one is taken) and opens the browser. The binaries are unsigned (macOS: right-click → Open; Windows: SmartScreen → Run anyway), and the first launch unpacks the bundle once.

## What you can do

The app registers the epochs of a dataset **pairwise**: you build a tree of pairwise registrations, one pair at a time, and inspect the residual error inside each pair and across the whole tree.

### Navigation

A three-stop **focus rail** tops the left panel — **Matrix · Pair · Pin** — each stop a strictly smaller focus. Pair unlocks once a pair is chosen, Pin once a pin is chosen or being placed; the rail remembers the last pair and pin. `Esc` backs out of the innermost thing first (dialog, half-placed pin, armed pick), then climbs one level.

### 3D view

- **Camera**: left- or right-drag orbits, scroll zooms, middle-drag pans in the view plane, Shift + drag pans horizontally at constant height. Double-click re-centres the orbit on the surface under the cursor (ghosted meshes too); right-double-click does so even while a pick is armed. The main camera never moves on its own.
- **Hold Space** for a clean view without annotations.
- Silhouette outlines and elevation isolines are always on. The top-bar **▤ Cut** / **▤ Far** sliders slice away terrain in front of / beyond a plane. A world-coordinate readout follows the cursor.
- **⚙ gear**: dataset switcher, checkpoints, rendering mode (Textured / Shaded / Slope), tuning sliders, dataset info, camera readout.
- **▦ mesh menu**: **☆ Set reference** designates the root of the registration tree (re-rooting onto a registered mesh keeps the registration; an unregistered one clears it) and the per-mesh heatmaps (Texture / Distance range / Shape quality / Incidence angle).

### Tile strip

One small top-down orthographic view per mesh along the right edge: all meshes at Matrix (framed alike, so they compare directly), the pair's two at Pair/Pin. Each tile overlays the reference footprint in gold. Drag pans, scroll zooms, the left edge resizes the strip; **⇱ Align to view** rotates the tiles to the main camera's heading, **N ↑** resets. **Clicking a tile isolates its mesh** in 3D (hover previews, click again to clear); while a pick is armed the click places the pick instead. A highlighted pin outside a tile's frame is pointed at by a chevron on the border.

### Matrix — the home stage

- **Pair matrix**: rows/columns are meshes in acquisition order; the upper-triangular cell (A, B) *is* the pair. A hole = not enough overlap to register; an outlined cell = possible; a filled cell = registered (fill = solve quality); a faded hint = possible but redundant (already connected through the tree). Hovering a cell previews the pair's true overlap area in 3D; clicking selects the pair and enters Pair (a redundant cell asks first).
- **Registration tree**: the reference root on top, registered meshes at their hop depth, unconnected meshes in a dashed island. Hover a node to isolate its mesh, click an edge to open its pair. Once every mesh is connected, **Assess global quality →** switches on the graph-wide error instruments.

### Pair — the pair workspace

Only the pair's two meshes render. The panel shows the pair header, a status line, the pin list and the finish line: **⌖ Solve** (needs three pins), then **✓ Finish pair**. Each pin row has **⌖** fly to, **✕** delete and **▸** open; hovering a row highlights the pin in 3D, the tiles and the histogram.

### Pin

A pin marks a corresponding surface patch in both meshes: a centre with a radius plus one correspondence point per mesh.

- **＋ New pin** runs a guided three-step placement — centre, point on mesh A, point on mesh B. Each landed click arms the next step; the pin exists the moment all three parts are placed.
- **Nothing places without arming.** While a pick is armed the rest of the UI falls behind a dark scrim, the arming button is the cancel, the left button picks instead of orbiting, and the cursor shows a white preview in the main view and the tiles.
- The centre only lands where the pair genuinely overlaps (the valid region is highlighted); if the other mesh has no surface within reach the click is refused, and if the reach must grow, so does the pin radius. A point pick renders its own mesh alone, so co-located surfaces can't steal the click.
- A correspondence point shows as a small crosshair in its mesh's colour (constant size, the open centre is the exact point) plus a white relief figure — rings and two vertical cut lines — tracing the local terrain shape.
- The **Edit** column re-arms any part of an existing pin: re-pick a point, move the centre, change the radius. **✓ Finish pin** returns to the pair.

### Solve and loops

- **⌖ Solve** runs the rigid landmark solve over the pair's pin points and adds the result as a tree edge (the unregistered mesh moves onto the registered one; re-solving replaces the edge).
- Re-picking a correspondence point or deleting a pin of a registered pair drops that edge and everything registered through it; the dropped pairs then re-solve automatically, one by one, each announced. Centre and radius edits keep the registration.
- Solving a pair whose meshes are both already in the tree would close a loop. The app measures how far the two paths disagree and asks: keep the new edge and remove one existing edge of the loop (the weakest is pre-selected), or cancel. The registration is always a spanning tree.

### Inspecting error

The **inspection toolbox** docks below the rail at every stop (click its header to collapse; widening the left panel enlarges the chart).

- **Chart**: at Pair, the moving mesh's error across the pair's pins as a stacked histogram (mm axis, level-of-detection band); at Pin, the selected pin alone. A registered pair overlays the *before* distribution as an outline.
- **Brush**: drag an x-range (click = one bin, drag the band to slide it, right-click clears). The brushed samples appear in 3D as dots coloured by their error while the scene drains to white; hovering a dot shows its exact local error.
- **Error map**: paints the signed difference on the moving mesh (diverging ramp + isolines, one shared range capped at ±0.5 m, pale grey = no data); at Pin, only inside the pin's area. Map and brush are mutually exclusive, and arming a pick clears both.
- **◍ Isolate pins** (top bar) cuts the view down to the pin patches.
- **At Matrix** the same toolbox reads the whole tree: every registered mesh is measured against its parent, the map paints all of them on one scale, and the chart pools every edge into one distribution — brush it to find which meshes the outliers come from.

### Peek keys

Spring-loaded, as keys and as top-bar hold-buttons. **V** (Pair/Pin, with a mesh isolated) flips the isolation to the pair's other mesh — same spot, other epoch. **B** drops the registration while held: the moving mesh (at Matrix, the whole graph) snaps to its as-loaded pose and the error display follows on a fixed scale, so releasing shows the error collapse.

### Checkpoints and session log

The gear popover saves named snapshots (dataset, registration tree, pins) to browser local storage; an auto-save runs once a minute and **Restore last checkpoint** recovers it. The top-bar **≣** popover lists every navigation action of the session and exports it as JSON.

### Screenshot mode

The top-bar **▣** button switches to a layout for documentation screenshots. Only the layout changes, the workflow stays the same.

- The registration tree, the tile strip, the **▤ Cut** / **▤ Far** sliders and the orientation gizmo are hidden; the rail shrinks to the pair matrix.
- The histogram sits to the right of the rail, titled "Deviation histogram" and without its legend, and is shown or hidden as a whole with the top-bar **Histogram** toggle. **Error map (3D)** and **⊗ Clear brush** move to the top bar.
- At Pair, the pair header, the status line, **＋ New pin**, the pin-count line and **⌖ Solve → ✓ Finish pair** move to the top bar; the rail keeps the pin list. At Pin, everything except the Edit buttons moves there.
- The rail, the histogram and the scale bar resize by dragging their invisible right edges. The scale bar drops its box for a bold label with a white outline, snaps to round distances, and moves anywhere on the page by dragging its invisible left edge.

## User-study mode

`/study` loads a warm-up dataset under a "Warm-up mode" banner with a **Start User Study** button; confirming switches to the study dataset. Switching datasets from the gear menu exits.

## Architecture

Elm-style: one model → update → view. The registration state is a rooted tree of pairwise rigid edges from which all mesh poses are composed; the server is stateless with respect to it — every query carries explicit transforms. `CLAUDE.md` documents the coding conventions, state rules, render-pipeline contracts, API endpoints and platform pitfalls.

## Tests

```bash
dotnet run --project src/Supertests        # pure-logic runner; exit code = failures
```

Integration against a running server (needs the Hessigheim dataset):

```bash
dotnet run --project src/Superserver       # terminal 1 (localhost:8002)
node tools/integration.mjs                 # terminal 2 (optional arg = base URL)
```
