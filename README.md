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
    <mesh>/
      *.obj                # one file per mesh part
      *centroid.txt        # "x y z" — the mesh origin's world position (V3d.Zero if absent);
                           # for scan data the origin is the scan-sensor station
      *_atlas.jpg or *.jpg # texture atlas
```

The first request for a mesh parses the OBJ + builds an Embree scene + BbTree; the result is cached for the process lifetime (the dataset bbox fetch warms the cache up front).

### Docker

`Dockerfile` + `docker-compose.yml` package the server + published WASM bundle behind nginx. `docker-compose up` builds and runs on port 80.

## What you can do

The app registers the epochs of a dataset **pairwise**: you build a tree of pairwise registrations, one pair at a time, and inspect the residual error inside each pair. Navigation is a three-stop **focus rail** at the top of the left panel — **Matrix · Pair · Pin** — each stop a strictly smaller focus: pick a pair, work that pair, configure one pin. Jump freely among the enabled stops (Pair unlocks once a pair is chosen, Pin once a pin is chosen or being placed); the rail remembers your last pair and last pin, so hopping out and back restores where you were. `Esc` always backs out of the innermost thing first, then climbs the rail one level — and leaving the Pin stop with a half-placed pin asks before deleting it once its centre is down (before that, `Esc` simply abandons the empty pin and returns to the pair).

- **Explore.** Orbit camera (drag = rotate, scroll = zoom, middle-drag or Shift+left-drag = pan); double-tap recenters on the terrain (works on ghosted meshes too). The main 3D camera never moves on its own — only your explicit double-click/fly-to actions steer it, including the top-bar **Sensor ▾** menu that flies to any mesh's scan-sensor viewpoint (the small top-down tiles, by contrast, re-frame themselves to whatever you focus). Hold **Space** for a clean view — every annotation (pins, flags, cross, outlines) hides while held. Per-mesh image-space silhouette outlines + world-Z elevation isolines are always on; the top-bar **▤ Cut** slider slices away terrain in front of the camera and **▤ Far** slices away terrain beyond a far plane (both with a flat-ink intersection band; Far is off at its right end). Rendering mode (Textured / Shaded / Slope) and the tuning sliders (outline threshold + thickness, isoline density + opacity, ghost floor, shading, flag scale, default pin radius, marker reveal radius) live in the **⚙ gear** popover.
- **The tile strip.** One small top-down view per mesh down the right edge, present at every stop: at Matrix it shows **all** meshes (small multiples, all framed on the reference mesh's area so they compare directly), at Pair/Pin just the pair's two. Each tile follows its mesh's heatmap switch and overlays the reference mesh's footprint outline in gold, always on top. Drag inside a tile to pan, scroll to zoom toward the cursor, drag the strip's left edge to resize it. **Clicking a tile isolates its mesh** in the 3D view (hover previews, click again — or jump levels — to clear); while a pick is armed, a tile click places the pick instead.
- **Mesh setup (▦ menu).** The top-bar **▦** menu holds the out-of-workflow controls: **☆ Set reference** designates a mesh as the **reference root** of the registration tree (the only way to change it) — re-rooting onto a mesh already inside the registered tree *keeps* the registration (path edges reverse), designating an outside mesh clears it; a root change also resets the pair/pin selection. The same rows carry the per-mesh intrinsic heatmap switches (**Tex**tured / **D**i**st**ance range / **Sh**a**p**e quality / **Inc**idence angle).
- **The pair matrix (Matrix).** Rows and columns are meshes in acquisition order; the upper-triangular cell (A,B) *is* the pair. Three states: a background hole = the pair doesn't overlap enough to register (checked server-side); an outlined vessel = possible; a filled cell = registered, fill strength = the solve's quality score. The diagonal is decorative — inert slashed placeholders, since a mesh has no pair with itself. The reference mesh is marked in gold in the matrix heads. Hovering any cell previews the pair in 3D: only the area where both meshes' footprints overlap on screen keeps its normal rendering, everything else drops to the ghost floor — so you see what a registration of that pair would actually work with before committing to it. Clicking a possible or registered cell **selects the pair** and enters its Pair level; the selected cell stays highlighted when you come back.
- **The pair workspace (Pair).** Only the two meshes of the pair render; the panel shows the pair header (A ↔ B), a live status line (registered at quality q / not registered / insufficient overlap), and the pair tools: pin list and solve; the error-inspection toolbox docks below the rail (see *Inspect a pair*). Clicking a pin row selects it (unlocking the **Pin** stop); double-clicking opens it at the Pin level; hovering a row makes every tile preview-frame that pin (click to keep the framing). Rows carry delete; the radius is edited at the Pin level. `Esc` ascends back to the matrix.
- **Pins (Pin).** A ScanPin marks a corresponding surface patch in both meshes of the pair. **○ New pin** (in the pair workspace) enters the **Pin level** with the **centre pick armed** — hovering the button, and the armed centre pick afterwards (including a later centre re-pick), highlights the pair's **overlap region**: the only valid place for a pin. Picking is **arm-driven — nothing places without arming first**: arm one of the picks in the panel's **Edit** column (**◯ Centre**, **✚ point on mesh A**, **✚ point on mesh B**), then click **any view** — the main 3D is the primary picking surface, the tiles are redundant alternatives. Arming is unmissable: everything that isn't a pick surface — the whole left panel, the top bar — falls behind a dark scrim and stops responding, and the button that armed the pick lights up above it as the one way to cancel; only the 3D view and the two tiles take clicks. While a pick is armed the left mouse button picks instead of orbiting, the cursor carries a white preview of what will be placed (synchronized across every view), and arming a point pick shows that mesh alone so co-located surfaces can't steal the click; a landed pick, `Esc`, or clicking the lit button disarms and lifts the scrim. The centre pick lands on whichever mesh you hit (the pin anchors there and rides its pose); the point picks always land on their own mesh, in any order. **The pin exists the moment its three parts are placed** — there is no commit step; leaving the Pin level with a half-placed pin asks before deleting it once the centre is down (confirm to leave, cancel to keep placing) — before the centre, `Esc` just abandons it. While a pick is armed, all existing marks — committed pins and the parts of the pin you're still placing — fade to near-invisible so they never hide the spot you're aiming at. Pins are deleted from the pair workspace's pin list only. **A pin under placement looks exactly like a finished one** — each part snaps to its final appearance the instant it lands (the area ring grows its real intersection figure, the radius is editable right away); only the flag and name arrive when the pin completes. A correspondence point is marked by a small **crosshair** in its mesh's colour — its open centre is the exact point, it stays the same size at any zoom, and it never disappears behind an isolation — plus a white **relief figure** on the surface around it (three concentric rings and two vertical cut lines, fading out with distance) that shows the local terrain shape for comparing the two epochs; its extent is the gear's *marker reveal radius*. The Pin panel is two columns over the same subjects: **Edit** (arm a re-pick of either point, move the centre — the pin re-anchors onto whichever mesh you hit — or open the radius slider) and **Isolate & focus** (point A / point B: click to isolate that mesh — the same lock as clicking its tile — and fly the camera onto the correspondence, click again to release; ◉ Pin: release the isolation and fly to the whole pin; all with hover previews and tile re-framing). While a mesh is isolated, the *other* mesh's relief figures hide (they would float in mid-air) — the crosshairs stay, they are the locators — every pin's area ring stays where it is, and pins anchored **to** the isolated mesh wear a second dashed ring — the same anchorage cue the tiles use; hovering an isolate button previews all of this with a gentle fade of everything that isn't the hovered correspondence's own. The tiles auto-frame themselves: the pair's overlap area when placement opens, the pin tightly once placed. The pin's area circle shows in **both** tiles; a dashed outer ring marks the tile of the mesh the pin is anchored to. Each pin renders its sphere-surface contact rings and a screen-constant flag (pole + name) in the main view.
- **Solve.** With ≥3 pins, **⌖ Solve** runs the weighted rigid landmark solve over the pin point pairs and adds the result as an edge of the registration tree (the un-registered mesh moves onto the treed one; solving an already-registered pair replaces the edge). Editing or deleting any pin of a registered pair drops that edge — and everything registered through it — because the solve is no longer backed by its inputs.
- **Loops.** Solving a pair whose meshes are *both* already in the tree would close a loop; the app measures how much the two paths disagree (angle + displacement) and opens a blocking dialog: keep the new edge and remove one existing edge of the loop (the weakest is pre-selected), or cancel and keep the old tree. The committed registration is always a spanning tree.
- **Inspect a pair.** The **inspection toolbox** docks below the left rail at the Pair *and* Pin levels — click its header to collapse it to a thin edge and back. Its chart shows the moving mesh's error across the pair's pins as a stacked histogram (mm axis, per-pin medians, the pooled level-of-detection band); at the Pin level it narrows to the selected pin alone. Once the pair is registered the chart overlays the *before* distribution as a step outline — the solve visibly collapses the error. Drag an x-range across the chart to **brush**: the brushed samples light up as dots in 3D (error maps stand down while a brush is active); hovering a dot cross-highlights the chart and reads out the exact local error. **Error map** (off by default) paints the signed difference on the moving mesh (diverging colour ramp + value isolines, one shared in-cell range capped at ±0.5 m, pale grey = no data, bottom-centre legend) — at the Pin level it paints only inside the pin's area. **⊕ Probe** arms a click-anywhere exact readout (like every pick, it is an arm: the click lands it and disarms) — the value pops up as a small tooltip right at the picked point (hovered brushed dots get the same tooltip). **Isolate pins** switches the view to the pin patches alone — it lives here too (it steps aside automatically while the centre pick is armed, so you can see where you're moving the pin, and comes back on its own). Drag the toolbox's **right edge** to enlarge the diagram: it grows in proportion until it reaches the bottom of the screen, then keeps widening.
- **Peek keys.** Two spring-loaded comparators, live at the Pair *and* Pin stops: with one of the pair's meshes isolated, hold **V** to flip the isolation to the *other* mesh — same spot, other epoch (it needs an isolated mesh; release snaps back to the one you had). Hold **B** and the moving mesh snaps to its as-loaded pose (did registration help? — needs the pair registered). The top bar carries matching **hold-down buttons** (◌ V / ↺ B) — greyed out until their peek can land. Release restores instantly; nothing refetches.

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
