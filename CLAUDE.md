# Superprojekt — assistant notes

Research prototype for interactive 3D inspection and **registration** of geological mesh datasets (multi-epoch scans of the same terrain). Two F# projects:

- **Superserver** — ASP.NET Core + Giraffe. Serves mesh data and runs the spatial queries (Embree BVH); hosts the WASM client at `http://localhost:5000`.
- **Superprojekt** — Blazor WASM client. Aardvark.Dom Elm-style architecture, WebGL2 rendering. Thin client; heavy compute goes to the server.

See `README.md` for what the app does and how to run it. This file collects the **rules and pitfalls**; behaviour is documented by the code.

## Style

- Light theme, high contrast, print-appropriate.
- GUI must be readable to a non-expert at first glance.
- Colour families are disjoint (§B1): the scalar gradients own red/blue (diverging, variance, range) and red→yellow→green (incidence/shape), plus pale grey = no-data and gold = reference. Mesh identity = distinct vivid hues (teal · orange · purple · green · magenta · brown · cyan · pink · olive — `Primitives.meshPalette`), chosen to stay clear of the diverging map's red/blue ends and near-white centre. Pins are NAME-only (v12 §4): no pin colours, no glyphs — every pin mark and label (3D influence/contact rings, constellation markers, flag name, matrix row-head text on the neutral header, slice-cell centre ring, dock/focus name labels) uses the ONE near-black warm grey `Primitives.pinInk` (#292524 — deliberately not the slice main line's #000 nor the slate UI text). Identity rides on thin marks (swatches, outlines, rings, chart layers) — gradients fill areas; never hand new UI a colour from another family. WHITE is reserved for the transient/selection layer: the armed pick's aim ghost/crosshair is white while uncommitted (the click commits it into the pin-ink marker; a move > 10 cm world also draws a white old→new line-arrow, main 3D + focus Top), and a selected pin gets a dashed white selection circle (radius ×1.12, at the median contact-ring height — `ScanPin.selectionCircleCentre`) in main 3D + focus single + tiles (the matrix dims non-selected cells instead).
- Comments follow **Comment discipline** below.
- Concise code, no unnecessary abstractions, no premature helpers.

## Comment discipline

One test for every comment: does it say something the reader cannot get from the code under it? If not, it is redundant and **forbidden**.

Never write:

- **Restatements** — a comment that paraphrases the line or block below it (`let lastPick = … // the last pick`). Clear naming does that job; if the name isn't clear, fix the name.
- **Spec references** ("implements v12 §5") — specs are transient working documents, deleted at review time; a pointer into one is instantly dead.
- **Absence notes** ("feature X left out per spec") — code never documents what it doesn't contain.

The only reasons a comment exists:

- The code's form or function is genuinely **non-obvious** — a trick, an invariant, a unit, a coordinate frame.
- The code looks wrong or needlessly complicated because an **external library constraint** forces the shape (Aardvark/FShade/WebGL gotchas) — name the constraint so nobody "simplifies" it back.
- The code is **performance-shaped** — deliberately structured for the adaptive/render/query hot path — say what the shape buys, or the next edit flattens it.

Form: as short as possible. State the constraint or invariant, nothing else — no flourishes, no context recap, no justification essays.

## State rules (Elm-style)

- One `[<ModelType>]` `Model`; Adaptify generates the `.g.fs` files. **Never edit `.g.fs` by hand** — run `./adaptify.sh` (or `adaptify.cmd`) after editing a model file.
- ONE selection: `Model.Selection` = `Active : ActiveSelection` (`SelNone`/`SelMesh`/`SelPin`/`SelCell(pin,mesh)`) + the transient `Hovered`. The pin×mesh matrix is the canonical driver (column-head = mesh, row-head = pin, cell = intersection); roster rows, focus tiles, 3D pin dots and 3D surface clicks emit the same `SetSelection` (a 3D background miss ⇒ `SelNone`); a deleted pin degrades `SelCell→SelMesh`, `SelPin→SelNone` (ScanPinUpdate). Every view is a pure follower via `Selection.pin`/`Selection.mesh` — never add a second selection state or panel-to-panel hover emitters. Grammar everywhere: **hover = peek, click = select, double-click = main-3D zoom, drag = edit**. A focus tile maps its click *through* the selection: nothing/mesh selected → `SelMesh` of the tile's mesh, pin/cell selected → `SelCell(pin, tileMesh)`, and re-clicking the tile of the current target = the double-click zoom (`FocusScene.cellZoom` is the shared fly-to-marker, also behind the matrix cell double-click).
- Selection frames the **focus panel**, never the main 3D camera: the focus single + tiles DERIVE their camera from the selection in BOTH projections (`FocusScene.selBaseFrame` — pin AND cell share ONE close-up scale, the pin's influence circle filling the view height, only the centre differs (pin centre vs that mesh's marker); mesh/none → whole-mesh fit (Top) / untouched look-around (360°)) composed with user pan/zoom offsets; there are no imperative focus-camera helpers to call at click sites. The 360° close-up is angular: pan = eye→target (azimuth, elevation), zoom so the vertical half-fov = the pin's angular radius — the pano zoom convention is **VERTICAL fov** (`panoFov`/`panoHalfTans`; the horizontal Frustum arg is derived per aspect), so the close-up fills the height identically in both projections; drag/wheel anchor maths must run on the EFFECTIVE zoom (base ⊕ user). The projection toggle switches the single AND the tiles together. Offsets: mesh/none persists per (mesh, projection), but a pin/cell target mints a FRESH offset on every selection change (`camPair`) — a focused pin/point must ALWAYS open at its close-up, never restore a stale zoomed-out state; don't re-key them back into `camStates`. A cell selection IS the locate: solo + first-locate backup (`BackOutLocate` restores; re-clicking the located cell backs out) — the user's Top/360° choice is respected (never force a projection at a click site). Main-3D framing goes through `ZoomToMesh`/`ZoomToPin`/`FlyToPoint`, only from double-click handlers. A control whose *single* click **toggles** state must route both handlers through `ClickGate` (Primitives.fs) — a double-click's two leading clicks/taps fire first and would toggle twice — and its double handler must itself end in the desired state (select + zoom, never toggle), because a slow double-click can let the deferred single fire in between.
- `MeshVisibility.shown` (Model.fs) is the **single** shown/clickable rule: `MeshSolo : string option` isolation (solo ⇒ only that mesh shows; no solo ⇒ all meshes show — there are no per-mesh hide toggles). Every consumer — render `MeshActive`, raycast candidate sets, ring/constellation gating — goes through it; don't special-case visibility anywhere else.
- Registration state: `LoadTransforms` (immutable per-mesh baseline) / `SolvedTransforms` (presence ⇔ solved; a re-solve replaces it wholesale) / `RegView` (one global Before/After). Displayed pose = `ModelTransforms.displayedRender`/`displayedWorld` for queries (committed view) and `MeshView.displayedMeshT` for rendering (also flips while the spring-loaded Peek hold is down — the peek is purely visual, no query may read it). Correspondence anchors are stored in the mesh's **server frame**, so the Before/After toggle moves them with the mesh — no re-baking.
- Correspondences are **Before-only**: the Before state is the single source of truth — anchors are detected (seeding evaluates at `displayedWorldAt RegBefore` regardless of the view), picked and edited there exclusively; After only *displays* the moved points. Entry points force the view back (`UpdateHelpers.applyRegView RegBefore` in `ToggleCorrArm`, placement, `SetInnerRadius`; `PickCorrespondenceAt` rejects in After as the safety net; the dock XYZ editor disables). Two zones: an anchor must lie within the **pin sphere** (`InnerRadius` — seed accept, pick clamp, and the resize kill in `SetInnerRadius`), while `InRoi` membership (can the probe measure here) uses the wider `ScanPin.roiReach`. A solve records its provenance (`Model.SolveInputs`: refMesh + every (pin, mesh) anchor point consumed); the `ensureSolveValidity` postlude clears the registration the moment any tracked pin/point is deleted or moved. Solve results land as ONE `CoarseSolved(gen, …)` batch guarded by `UpdateHelpers.solveGen` — every registration-clearing path bumps the generation so an in-flight solve can never resurrect a cleared state.
- Probes are per-pose: `ScanPin.Probe` = the **committed** displayed pose (every consumer — matrix, inspect range, brushing — reads this one); `ScanPin.ProbeOther` = the same probe at the opposite Before/After pose, fetched only once a solve exists, consumed **only** by the dock charts' inactive-pose outline. `SetRegView` **swaps** a ready (Probe, ProbeOther) pair in place instead of refetching (and clears `BrushedSamples` — gids index the committed pose's canonical array); everywhere else the two invalidate together (`ScanPinModel.invalidateProbes`). `ScanPin.Slice`/`SliceOther` (vertical cross-section polylines feeding the matrix **slice cells**) carry the **same** pose pairing — but BOTH poses arrive in ONE request per pin (`ensureSlices`; SliceOther rides along once a solve exists) — swapped by `SetRegView`, dropped by `invalidateProbes` (geometry tunables use the slice-only `invalidateSlices`), and the reg peek only *selects* the other cache (never queries). The slice frame is **per pin**: chart u = `PinSlice.UDir`, the section azimuth fitted server-side on the reference (dip direction) — one world-space line shared by every cell of the pin's row, NEVER per cell; helpers in `ScanPin` (`sliceNormalOf`/`sliceWindow`/`sliceOffsets`/`sliceClipRadius`/`sliceToWorld`/`sliceUV`). The Inspect scalar maps carry the pairing too: `SurfaceDistance`/`SurfaceDistanceOther` (variance on the reference) and `FocusDist`/`FocusDistOther` (per-mesh difference) — Other fetched only once a solve exists, swapped wholesale by `SetRegView` (which also cancels the in-flight CTSes + bumps both generations, since a landed result would file under the wrong pose), selected by the reg peek in `MeshView.inspectField`/`FocusScene.focusOverlay` so the paint flips with the geometry.
- Matrix cells are ONE slice-diagram style (`SliceDiagram`, GuiRail.fs — ScanPin v11 §A): grey ground, faint context slices of the cell's **own** mesh, the reference profile ±LoD₉₅ as a grey band (LoD from the pin's probe, `1.96·√(refStd²+std²)` — the same pair statistic everywhere), the mesh's centre profile as a black line + white halo (black = data ink), pin-ink centre ring (near-black, hardcoded in the boot JS); out-of-ROI = the bare hatch glyph (no marks — visibly empty); off-frame lines clip with an edge arrow. ONE global horizontal window (`ScanPin.sliceWindow` = N × the coarsest mesh spacing from bboxes) and ONE global vertical extent (robust percentile over all cells, committed pose) — never reintroduce per-cell/per-mesh scales. Matrix highlighting keeps two disjoint channels: **reference = colour** (gold header + gold column frame, persistent) and **selection = de-emphasis + accent** (cells outside the selected row/column dim via `mx-cell-dim`, headers fill with the pin/mesh accent, the selected cell gets an accent *outline*) — never black outlines, they'd compete with the diagrams.
- Mode-dependent behaviour (per-mode isolation defaults, Inspect focus/pin policies) lives in the **reducer** (`SetWorkflowStep`, `SetSelection`), not in view-layer click handlers — so every entry path behaves identically. Outside Inspect a `SelMesh` selection ghost-emphasizes in the shader chain (`MeshView` `isActive`); in Inspect it routes through the solo overlay. Pin isolation (`AnchorGhostMode`, the blob mask) is **Register-exclusive**: the `SetWorkflowStep` default (on in Correspondence, off elsewhere) is its only automatic driver — selection never mutates it, so in Inspect the meshes always show fully (manual gear toggle + placement flashlight aside). The Register rail surfaces it as the "Isolate pins" checkbox (a `compactToggle`, not a button — v12 §7): checked (the Register default) ⇒ the context floor is 0 and only the pin patches read; unchecked ⇒ `AnchorGhostMode` off, full textured meshes exactly as in Overview (the same code path).
- **Slice mode** (v12 §5–§7): `SliceMode`/`SliceCut`/`SliceStretch` — the pin-centred TO-SCALE ortho section view. ONE frame, `MeshView.sliceCamera`, DIP-ALIGNED: screen-vertical (camera up) = the **pin axis** (`ScanPin.axis`, the local normal at the centre), azimuth = orbit `phi` snapped to 10° steps and projected into the plane ⊥ the axis (drag keeps rotating the orbit; entry inherits the nearest step by construction), zoom locked to the pin, and the **ortho NEAR plane is the cut** — View.fs feeds the same view/proj avals to the offscreen outline pass, so the profile at the cut silhouettes with zero extra code. `SliceCut` stays CONTINUOUS in the model (trackpad deltas accumulate); `sliceCamera` snaps it to the `sliceCutStep` grid (nice 1/2/5, ≥ 1 cm, ≈ 5 % of the radius). The mesh shader fades a few cm behind the cut (`SliceFwd`/`SliceFadeNear`/`SliceFadeDist`; faded fragments drop below the α-depth gate so picks fall through), and the outline COMPOSITE fades silhouettes + isolines over the SAME small window AND caps their opacity at 10 % (`OutlineDistFade` = (far−near)/FadeDist doubles as the slice signal — valid because slice is ortho, where window depth is linear and 0 at the cut; the fade multiplier reads the 16-bit packed G-buffer depth; 0 disables both in perspective); the per-mesh footprint contours stand down entirely. The CUT PROFILE is the one full-strength line: an at-cut depth edge paints as data-ink BLACK above the cap (fading to the capped palette silhouette by half the fade window) — see the outline-pass section for the background-depth substitution that makes profile-vs-background break at all. THE TERRAIN PROFILES OWN THE SLICE VIEW: the pin-flag machinery (pole, base cross, name label), the pin rings (influence ring, axis line, contact rings), the origin cross and the correspondence constellation all stand down in slice mode. Chrome: metric rulers left + below the view measuring distance from the pin centre (`GuiOverlays.sliceAxes`; the vertical ruler ticks TRUE metres, so its spacing widens with stretch), gold badges top-centre ("ortho slice view" + "vertical axis stretched ×N", `sliceBadges`), and in the focus Top views (single + tiles) the WHITE angle indicator at the selected pin — an arrow (~75 % of the pin circle) along the slice view direction + two ⊥ lines tracing the cut: solid white = the cut plane, faint white = the falloff end (FadeDist behind it) (`addPinRingsAndSelectionCircle`; white = the transient/selection layer — gold #b45309, the slice accent, stays on the badges only). The wheel is intercepted in View.fs (`AdjustSliceCut` — zoom stays locked); Esc exits; the `ensureSliceMode` postlude drops the mode whenever the pin selection goes. Stretch is PROJECTION-only vertical exaggeration (ortho half-height ÷ N — never scale geometry: cut/fade/picks stay metric and screen-vertical IS the pin axis) plus a horizontal tighten (half-width ×0.8 — legal only because stretch already gave up 1:1; `MeshView.sliceOrthoHalfSizes` is the ONE hw/hh source for the view proj, ordinates, rulers and dot-glyph sizing — never compute slice ortho extents elsewhere); N is ADAPTIVE and PRE-CALCULATED FOR THE REGION (`ScanPinScene.sliceStretchFactor` — every input is cut- and azimuth-independent, so the frame never moves while scrolling): brushed ⇒ the selected pin's brushed samples fill ~¾ of the view height, else the ~20 on-surface probe samples closest to the pin centre, else the shared inspect span. Ordinates + true-value tooltips are stretch-only hoverable HTML strips (`GuiOverlays.sliceOrdinates`, projected through the SAME matrices; the ordinate drops along the pin axis = the M3C2 direction), with the persistent ×N badge (`stretchBadge`).
- **Placement suitability** (v12 §2): an armed placement auto-shows the fused overlay — `SuitabilityCoverage` (shape-weighted, occlusion-free screen-space coverage MRT, 8-channel cap) into `SuitabilityComposite` (≤1 covered → transparent — no overlap means no overlay, the surface shows through; ≥2 → a diagonal weave cycling through the covered meshes' unmodified palette colours, semi-transparent), drawn before the outline composites so isolines stay readable. Hard-prohibit: `View.countOverlap` (per-mesh closest-point fan-out at the displayed pose; in-range = within `QuickPinRadius`) drives the hover ghost fade + the cursor tooltip, and the click re-verifies before `PlaceAnchor` (< 2 meshes ⇒ refuse + toast).
- Debounce/generation state (CTS + counters) lives at module level in `UpdateHelpers`/`ScanPinUpdate`, **not** in the Elm model.

## Render pipeline (single forward pass)

One forward pass into the main framebuffer — `passZero`: meshes (custom α + α-gated depth) then pin geometry (`DepthTest.LessOrEqual`, blended); `passOne`: coordinate cross, labels, overlay lines (`DepthTest.None`, always on top). The one offscreen consumer is the image-space outline pass.

Contracts the rest of the stack relies on (`MeshShader.shade`):

- **α-gated depth**: fragments with α ≥ 0.99 write their natural window depth; ghost/outside fragments write 1.0 (far) — so ghosts never occlude anything and pixel picks fall straight through them.
- **Ghost colour is uniform**: ghost fragments always use the solid per-mesh palette colour regardless of rendering mode, so a ghost silhouette reads as one shape.
- Solid/ghost/invisible is decided per fragment from `MeshActive` × the global ghost floor (`GhostSilhouette`/`GhostOpacity`; floor off ⇒ ghost fragments are *discarded*, i.e. hidden not translucent) × the pin-isolation blob mask (`Blobs` uniform array, hard cap 32, metric → render at upload). The scalar-field painters (difference/variance/heatmaps) only touch **above-ghost** fragments. **Inspect is de-cluttered (§B5)**: the per-mesh ghost floor is forced to 0 (every non-emphasized fragment discards — context meshes read as outline-only via the outline pre-pass, which renders ALL loaded meshes independently of the main pass) and `InspectPlain` swaps the base surface to plain near-white (no photo texture/palette/slope under the false-colour maps; shading kept).

### One Inspect error range (never per-mesh normalization)

Every Inspect false-colour map reads on the **same pin-derived scale** — do not reintroduce per-mesh/per-tile normalization (robust percentiles, user range sliders):

- `ScanPin.inspectRange` (ScanPinModel.fs) = signed (lo, hi) in metres over every ready pin's ROI probe samples on the moving meshes, always spanning 0, **hard-capped at ±0.5 m**; no pins ⇒ the full ±0.5 m. Adaptive wrapper: `MeshView.inspectRange`.
- Consumers: 3D `MeshShader` (`DistScale` = hi, `DistLoNeg` = |lo|; variance σ saturates at max(|lo|, hi)), focus tiles/single (`FocusHi`/`FocusLoNeg`), and the bottom-centre legend (`GuiOverlays.colorLegend`, Inspect only; while a brush is active it describes the value-coloured dots instead — "Difference (M3C2) · brushed" — hiding only in slice mode + brush, where the dots are neutral).
- The diverging map is **asymmetric piecewise Coolwarm** (Colorcet CET-D01, as shipped by Maple — a deliberate user choice over the earlier yellow-centred map): zero = the ramp's near-white centre #EDE7EA (welded to 0 — grey is reserved for "within LoD / no data", never "0"), each sign runs zero→mid→saturated end (lavender→blue #2151DB / salmon→red #C00206) normalized by its own end with the t^0.6 near-zero boost (`Primitives.Diff.colorSignedV3`, mirrored in `MeshShaders`/`FocusShaders` — keep all three in sync). Values outside clamp to the end colours.
- Difference maps carry **value isolines**: dark derivative-antialiased contours every `Diff.isoStep` metres (a nice 1/2/5 step ≈ span/8 of the shared range, so 0 is always a contour), suppressed where the colour clamps; step is passed as a uniform (3D + focus), not model state. `ddx`/`ddy` exist in FShade, `fwidth` does NOT.
- Intrinsic heatmaps (§B3): **Range** normalizes by the ONE all-mesh end `MeshView.rangeMaxWorld` (legend shown outside Inspect while any Dst is active); **Incidence** uses the geometric (screen-derivative) normal sign-oriented by the stored vertex normal, clamped at 0 — never `abs` (away-facing = never scanned = worst); **Shape** discards fragments below the global `ShapeThreshold` (Overview rail slider).
- The dock is TWO fixed standard charts (v12 §3, GuiInspector.fs), always both mounted side by side, no reflow: **mesh chart** = the selected mesh's error across pins (= the matrix COLUMN; pin series on an achromatic grey ramp, canonical order CreatedAt, guid) · **pin chart** = the selected pin's error across meshes (= the matrix ROW; mesh-palette series). Full furniture always (title, mm x-axis, count y-axis, inset legend, LoD band, zero line); an unselected half renders the same furniture with a placeholder — never a blank panel. 48 bins over ONE shared x-range (1–99% quantiles, both poses, all moving meshes — the charts stay comparable). Solved → fill = emphasized pose + near-black step outline = the other pose's total, with a "fill Before · line After" key (Peek flips only the emphasis). The charts carry NO selection UI (metric toggle only: M3C2|Δz); the shift readout sits beside them (re-render on resize via ResizeObserver).
- Sample brushing is **chart-drag only** (`SetBrushedSamples` from the charts' shared JS bridge — either chart can brush; the set replaces wholesale): an x-range over the *conceptual* samples of that chart — gids stay indices into the canonical array (`ScanPinScene.brushSamples`, committed pose). Brushing = **sole focus**: a non-empty brush suppresses every Inspect error map (3D `inspectField` → enc 0, focus overlay → texture). The brushed dots are screen-aligned circle+cross glyphs (v12 §6 + follow-ups): main 3D = `ScanPinScene.brushedDotSegments`, CONSTANT screen size (`BrushDotPx` gear slider, default 15 px; ortho slice sizes from the frustum, perspective per dot from eye distance) with the vertical glyph axis divided by the stretch factor so exaggeration never distorts them; the DOTS OF INTEREST carry a second, inner circle; focus single + tiles = `brushedDotSegmentsFocus` (XY-aligned, fixed render size, never interest-marked). Outside slice mode the MAIN-3D dots carry the difference viz: each stroke = its sample's signed value through the ONE shared diverging map/range, over a dark under-stroke (the near-white zero end must read on the plain Inspect surface), and the colour legend stays up describing them; in slice mode and the focus views the dots are the NEUTRAL dark grey (values live in the charts/ordinates — legend hidden in slice+brush only). In slice mode the ≤ `maxDotsOfInterest` dots nearest the cut stay full-strength, the rest fade with cut distance, and the interest gids cross-highlight in BOTH charts (amber baseline markers) — `ScanPinScene.sliceRankedBrush` is the ONE ranking shared by dots, ordinates and charts. A plain chart click clears and restores the maps. No 3D hover-reveal of samples.

### `Sg.DepthMask` is forbidden

Buggy in this Aardvark/Aardworx WebGL build — it silently breaks the depth pipeline. Steer ordering with `Sg.DepthTest` + `Sg.Pass` alone. Lines, pin geometry and text therefore all write depth; that violates the textbook "translucent shouldn't write depth" rule but is the only combination that renders correctly in this stack. Leave the in-code reminders in `LineShader.fs` / `SceneGraph.fs`.

### Image-space outline pass (`OutlineView.fs`)

MRT G-buffer (world-Z band parity + window depth → target0, palette colour + coverage → target1) → fullscreen edge-detect painting silhouettes + elevation isolines. Two non-obvious choices — keep them:

- The depth edge is the **second difference** (Laplacian) `|l + r − 2c|`, *not* a first difference: window depth is linear in screen space across any planar primitive, so the Laplacian is ~0 on a smooth slope at any view angle and spikes only at a genuine break. A first difference measures screen-space depth *slope* and lights up every grazing surface as false bands.
- target0 is **`Rgba8`**; the window depth is packed hi (.w) / lo (.z) as 16-bit fixed point. The edge detect reads the HI byte alone — 256 levels (1 LSB ≈ 0.004), so an `OutlineThreshold` below that quantization floor makes the staircase risers of a smooth slope read as false bands. Only the slice-mode distance fade reconstructs the full 16 bits (its FadeDist window is ~2 hi-byte LSBs — an 8-bit fade staircases to on/off).
- The isoline signal is band **parity** (`floor(wp.Z / ContourSpacing) mod 2`) — a step function, so its edge is a plain first difference; because the band index is a pure function of world Z, the contours stay welded to fixed world-Z planes and do not crawl as the camera orbits.
- Slice mode only (`OutlineDistFade` > 0): the edge detect substitutes **far depth for background samples** (mesh id 0) — the FBO clear leaves target0.w = 0, which under the slice ortho is the CUT plane's own depth, so the section profile against empty background would otherwise never register — and the at-cut depth edge paints as data-ink black above the 10 % cap (the slice profile line). Perspective keeps raw values (background 0 vs surface ~1 already breaks hard).
- Per-mesh FOOTPRINT contours come from a second, occlusion-free pass: an additive coverage MRT (one channel per mesh, 2×Rgba8, NO depth — cap 8 meshes) + the `OutlineCoverageEdge` composite, which outlines each channel's covered↔uncovered transition in that mesh's palette colour. The depth-tested combined G-buffer can only ever outline the visible **union** (no depth break where one mesh ends over a co-located one; hidden boundaries aren't in the buffer at all) — never remove the coverage pass in favour of it. Footprint lines obey the same `OutlineMask` flags (> 0.25). In slice mode the footprint composite is inactive (`Sg.Active` gate in `buildCoverage` — the depth-free additive MRT has nothing to fade with); the pass itself is untouched.
- Per-mesh outline gating: the G-buffer writes a mesh id (`MeshId` = (index+1)/255, target0.y) and the edge composite gates lines through the `OutlineMask` slot array (`MeshView.outlineMask`): 1 = silhouette + isolines, 0.5 = **silhouette only** (Inspect pair view — with a mesh isolated, everything except it and the reference keeps just its contour, with a small G-buffer depth push so the co-located pair wins ties).
- `ContourSpacing` is **camera-adaptive** (§B2): ~24 contours across the view derived from the orbit radius, SNAPPED to a nice 1/2/5 world-metre step (discrete ticks — zooming out thins the lines stepwise, orbiting never changes them). The gear's `IsolineBands` sets the densest allowed spacing; the far end caps at ≥4 contours over the scene Z range. The difference-map value isolines need no camera term — their in-shader derivative fade already suppresses overcrowding.

### Picking

- `Sg.OnTap` / `OnDoubleTap` / `OnLongPress` **fire on background misses too** — every handler that builds state from the hit must gate: `if e.Location.Depth < 0.9999 then Some e.WorldPosition else None` (background leaves depth at the clear value 1.0).
- Ghost fragments leave depth at 1.0, so the GPU pixel pick cannot land on them. Anything that needs a 3D point on a possibly-ghosted surface keeps the GPU pick as the fast path and falls back to a server raycast (`View.resolvePick` / `raycastNearest*` / `raycastMesh`) — un-apply the mesh's displayed pose before the query, re-apply it to the hit. Hover-driven raycasts must be throttled (~60 ms) + generation-guarded.
- **Every node without `Sg.NoEvents` writes the GPU pick buffer** (id + `gl_FragCoord.z`, blending forced off there — screen alpha is irrelevant, `DepthTest.None` wins unconditionally). Overlay/composite geometry — especially fullscreen quads like the outline composite — must set `Sg.NoEvents` or it hijacks every pick with its own depth.
- The focus panel picks are **Dom-driven + server raycast** (`FocusScene.worldRayHit`) — `Sg.OnTap` does not fire reliably in the secondary render controls. Its cursor→NDC math must run in **CSS px**: the focus controls read `ViewportSize` ÷ the shared `FocusScene.dpr` published by the main view (binding `RenderControl.ClientSize` in a secondary control blanks it).

## Coordinate systems & transform hierarchy

Three spaces, two transforms. Keep them strictly separate — every boundary crossing goes through a named helper, never bare `* scale` / `± centroid` arithmetic.

**Spaces**

- **Mesh / server frame**: the mesh's stored OBJ coordinates `+ meshCentroid`. **Every `/api/query/*` coordinate — in and out — is in this frame**; the server subtracts the centroid itself.
- **Metric world**: the app's single canonical world (metres). Pin centres/radii, correspondence anchors-as-world, cursor world all live here. Metric world ≡ a mesh's server frame exactly at the load pose.
- **Render space**: what the GPU and cameras use — centroid-recentred, dataset-scaled, then posed.

**Two transforms — dataset first, then workspace:**

1. **Dataset transform** — a *similarity* (uniform scale + translation, never rotation), fixed per dataset. The **only** place `DatasetScale` and `CommonCentroid` enter. Cross it with `ScanPin.renderCentre`/`worldCentre` (points) and `ScanPin.renderLength` (lengths).
2. **Workspace transform** — a *rigid* per-mesh pose (the before/after registration pose). Render form = `ModelTransforms.displayedRender` / `MeshView.displayedMeshT`; metric-world form = `ModelTransforms.displayedWorld`. `RigidTransform.worldToRender`/`renderToWorld` conjugate a rigid pose between the two (the dataset similarity is the conjugator). **`displayedWorld.Backward` maps metric world → the mesh's server frame; `.Forward` maps back.**

**Discipline rules**

- Server queries: convert metric world in with `displayedWorld.Backward`, map results out with `.Forward`. Multi-mesh queries (`probe`, `region-distance`) instead pass each mesh's `displayedWorld.Forward` matrix and let the server place them.
- Scene-graph geometry is render space: convert model values at the boundary (`renderCentre`/`renderLength`, or `worldToRender` for poses).
- Directions need no scale handling (uniform scale ⇒ parallel); only the workspace rotation matters (`TransformDir`).
- Anchors are stored in the mesh's server frame (`displayedWorld.Backward world` at placement time — pose-independent).

## Panorama centre (`pano-centers.txt`)

Each mesh's OBJ origin is *supposed* to be its scan camera, but the data is often not centred on it — so the panorama eye is data-driven: one optional file per dataset, `data/{dataset}/pano-centers.txt`, lines `<mesh-folder> x y z` in **absolute world coords** (same frame as `*centroid.txt`); unlisted meshes fall back to the mesh origin. Served at `/api/datasets/{d}/pano-centers`, held in `Model.PanoCenters`. It is the sensor origin for the incidence/range heatmaps and the position of the focus panel's 360° camera (rendering and pick rays). To add centres: isolate a mesh, read the top-bar **world** coordinate at its visual centre, write a line — no code change.

## Adaptive performance (critical)

In the scene graph, **never depend on an entire record when you only need a subset of its fields**. The Elm-style model replaces whole records on every update, so an `AVal.map` over a full `ScanPin` (or `Model`) fires on *any* field change.

**Rule: project individual fields into separate `aval`s early, then build the dependency graph from those.**

```fsharp
// BAD — rebuilds on ANY pin change (probe result, selection, …)
let geo = pinVal |> AVal.map (fun po -> ... po.ContactRings ... po.InnerRadius ...)
// GOOD — only when the rings or radius actually change
let ringsVal  = pinVal |> AVal.map (Option.map (fun p -> p.ContactRings))
let radiusVal = pinVal |> AVal.map (Option.map (fun p -> p.InnerRadius))
let geo = (ringsVal, radiusVal) ||> AVal.map2 (fun rings r -> ...)
```

For scene-graph nodes (`Sg.Text`, `sg { ... }`) this matters even more: rebuilding an `AList` of sg nodes destroys and recreates GPU resources (font atlases, draw calls). Therefore:

- **Split structure from placement.** Build static sg node lists from slowly-changing data; use adaptive `Sg.Trafo` for fast-changing placement (uniform update, no rebuild).
- **Push adaptivity down.** A parent `AList.ofAVal` that rebuilds all children is expensive; an `AVal`-driven `Sg.Trafo` per stable child is cheap.
- **Don't build in-place-updating *lists* with `AVal.map (… IndexList.ofList) |> AList.ofAVal`.** That mints fresh `Index` keys every recompute, so any element change diffs as remove-all + add-all — churning every row's DOM/GPU resources and intermittently double-rendering in the reconciler. Derive a stable-identity incremental list instead: `AMap.map (project to just the row's inputs) |> AMap.toASet |> ASet.sortBy key |> AList.map row` (the rail pin×mesh matrix is built this way). Small, rarely-changing lists may use the simple form.
- **Never create a *transient* `aval` inside another aval's compute and read it.** `AVal.custom (fun t -> … (makeSomeAval args).GetValue t …)` (or `AVal.force` of a freshly built aval) can drop the dependency edge, so the outer aval **evaluates once and silently freezes**. This bit the focus single (rendered blank because it never saw the meshes load) and the constellation. Fix: **inline** the inner computation so its `model.X.GetValue t` reads hit the outer token directly, or bind the inner aval **once** outside the compute (a stable `let`). Reading a *stable* aval via `.GetValue t` is correct — only per-eval-built avals are the trap.

Sanctioned exception: the 3D pin **flags** (base cross · pole · top ring · name) are screen-constant — every element derives from the ONE per-pin `ScanPin.flagHeightRender` (fixed fraction of the eye distance, clamped to 0.1–20 m metric world, × the gear's `FlagScale`), so their line/trafo avals read `view` and recompute per camera move on purpose (a handful of pins; the label is `Sg.Trafo`-only). Only the name billboards (Z-yaw to the eye); the base cross stays axis-aligned.

## Server query performance

Costly spatial queries (`probe`, `contact-rings`, `region-distance`) scale with mesh count × sample density:

- **Never issue per-mesh requests sequentially** — fan out with `Async.Parallel`; if a multi-mesh operation becomes hot, add a batched server endpoint with `Parallel.For`.
- **Parallelise the heavy server inner loop** when inputs are independent — Embree `Scene.Intersect` is thread-safe.
- **Cap density rather than grow linearly** (`maxPoints` / sample strides / `maxTris`).
- **Debounce user-driven triggers** with a `CancellationTokenSource` + generation counter so the next event cancels the previous and at most one fetch is in flight per invalidation.
- **Mesh caches are warmed at dataset load** by `bboxesHandler`, so the first interactive query never pays the lazy-load cost.

## Client compile order (`Superprojekt.fsproj`)

```
MeshData.fs            mesh fetch/parse, ApiConfig, shared Http.client
ProbeModel.fs          M3C2 probe DTOs
Query.fs               server query wrappers (Async)
CameraModel.fs / .g.fs OrbitState [<ModelType>]
OrbitController.fs     orbit camera + messages (project file, NOT the Aardvark library one)
RegistrationModel.fs   ScanPinId, anchors, readiness engine (WASM-free, shared with Supertests)
ScanPinModel.fs / .g.fs ScanPin + placement state
PinGeometry.fs         icosphere, sphere outline
Model.fs / .g.fs       [<ModelType>] Model + Selection + MeshVisibility + ModelTransforms
LineShader.fs          flat colour + pixel-constant 3D lines
Primitives.fs          widgets, showWhen, observedRender, palettes, Diff colormap, friendly names
Messages.fs            Message DU
ScanPinUpdate.fs       pin sub-reducer + probe/rings postludes
UpdateHelpers.fs       reducer helpers + debounce/generation state + anchor seeding
Update.fs              main reducer + variance/focus-dist postludes
MeshShaders.fs         RenderPass + MeshShader + OutlineGBuffer/OutlineEdge
MeshView.fs            LoadedMesh, buildScene, displayed transforms, pin blobs
OutlineView.fs         offscreen image-space outline pass
ScanPinScene.fs        pin sg nodes + constellation + brushed samples
SceneGraph.fs          scene composition + cross + labels + reference/focus outlines
FocusShaders.fs        focus colour fragment (Inspect/heatmap overlays)
FocusScene.fs          focus render controls (single + tiles), 360°/Top cameras, pick
GuiTopBar.fs           top bar + gear popover
GuiOverlays.fs         toast, scale bar, orientation indicator, wheel label, pin flag tags
GuiRail.fs             three-mode left rail (roster · pin×mesh matrix · Solve)
GuiFocus.fs            focus panel head + FocusScene mounts
GuiInspector.fs        mode-contextual bottom dock (distribution + brush bridge)
View.fs                view function + App module
ShaderCache.fs / Program.fs
```

## Server compile order (`Superserver.fsproj`)

```
MeshLoader.fs     OBJ parse, centroid file, atlas paths
MeshCache.fs      Embree scene + BbTree cache (lazy, permanent)
MeshAnalysisCore.fs  pure level-set tracer + decimate + dip fit (WASM/Embree-free, shared with Supertests)
MeshAnalysis.fs   sphere contact-ring tracing
MeshProbe.fs      N-mesh M3C2 probe
RegMath.fs        weighted Umeyama rigid landmark solve (Jacobi SVD, conditioning)
QueryHandlers.fs  HTTP query handlers
Handlers.fs       routing
Program.fs        ASP.NET startup
```

## API endpoints

```
GET  /api/datasets                              → string[]
GET  /api/datasets/default                      → string (data/default.txt, fallback = first)
GET  /api/datasets/{dataset}/centroids          → { meshName: [x,y,z] }
GET  /api/datasets/{dataset}/pano-centers       → { meshName: [x,y,z] }   (absent file → {})
GET  /api/datasets/{dataset}/bboxes             → { meshName: { min, max, spacing } }   (warms the cache; spacing = mean edge length, feeds the slice-cell window)
GET  /api/datasets/{dataset}/mesh/{name}/{i}    → binary mesh
GET  /api/datasets/{dataset}/mesh/{name}/{i}/atlas → JPEG
POST /api/query/ray                             → { hit, t, point, triangleId }   Name = "dataset/mesh"
POST /api/query/closest                         → { found, point, distanceSquared, triangleId }
POST /api/query/contact-rings                   → sphere–surface intersection polylines
POST /api/query/lsq-pairs                       → weighted rigid solve (absolute world transform + residuals + conditioning; 400 on <3 pairs)
POST /api/query/probe                           → N-mesh M3C2 probe (per-mesh distributions + per-sample positions)
POST /api/query/slice                           → N-mesh vertical cross-sections: mesh∩plane polylines for every mesh × parallel plane offset × both poses (per-mesh transformOther ⇒ paired planesOther) in one request, disc-clipped, returned as flat (u,v) chart-frame pairs; the section azimuth (chart u) is fitted HERE on referenceName's ROI surface (dip direction) and returned
POST /api/query/region-distance                 → per-vertex signed M3C2 distance (mode 0) or vertical Δz (mode 1) of a target mesh to the reference, in the target's served vertex order; both modes share one support rule — a vertex responds only where the vertical world line through it pierces the reference (Z-overlap), else 1e30 sentinel — so M3C2 never fabricates error in non-overlap fringes, and the variance map (which skips sentinels per mesh) only aggregates meshes that overlap there
```

All query coordinates are **absolute world space**; the server computes `localPos = worldPos − meshCentroid`. (Endpoints without consumers were removed — `/query/icp`, `/query/patch`, sphere/box/ray-batch, grid-eval, isoline, curvature-ridge, region-grid; don't re-add one without a consumer.)

## Tests

`src/Supertests` — console runner (no test packages) compiling `RegistrationModel.fs` + `RegMath.fs` + `MeshAnalysisCore.fs` directly: `dotnet run --project src/Supertests`. Integration against a running server (covers lsq-pairs, probe, slice, region-distance): `ASPNETCORE_URLS=http://localhost:8002 dotnet run --project src/Superserver`, then `node tools/integration.mjs`.

## Aardvark.Dom gotchas

- `Attribute("for", "...")` on `<label>` is silently dropped — nest `<input>` inside `<label>`.
- `Attribute("checked", "")` is dropped — use `Attribute("checked", "checked")`.
- CSS `~` sibling combinator breaks (Aardvark inserts wrapper nodes) — use `:has()` on a known ancestor.
- `RenderControlInfo` and `TraversalState` both have `.Runtime` — annotate `(info : Aardvark.Dom.RenderControlInfo)` when ambiguous.
- `yield!` is not supported in Aardvark.Dom CE builders — use OnBoot JS with MutationObserver for dynamic SVG/canvas (the `observedRender` helper).
- **OnBoot may run before a *later-sibling* node is mounted.** Don't capture a following sibling at boot (`querySelector` stored in a closure freezes `null` forever) — look siblings up **lazily** inside the handler that needs them. Boot-time capture is only safe for **ancestors** (`closest(...)`).
- `renderControl { ... }` nests fine inside `div { ... }`; multiple controls coexist. But **`Sg.OnTap` (and other Sg pointer events) do NOT fire reliably in secondary render controls** — use Dom pointer handlers + a server raycast there. Camera input in secondary controls is also Dom-level, **without** pointer capture (capture hijacked later clicks).
- `AVal.map4` does not exist — combine with `AVal.map2`/`AVal.map3`.
- `Dom.Style` for renderControl; `Style` for HTML elements. `Css.Custom` does not exist — use CSS classes in `style.css`.
- **`RenderControl.ViewportSize` is framebuffer pixels**; `RenderControl.ClientSize` is CSS pixels. Anything mixing with DOM coordinates (overlay placement, cursor → NDC) must work in CSS px or it breaks on hi-dpi. ClientSize is `V2i.II` until the first DOM event; the main control binds ClientSize with a ViewportSize fallback, the focus controls divide ViewportSize by the shared `FocusScene.dpr` instead (binding ClientSize there blanks the control).
- `Sg.OnPointerDown(bool, handler)` — the bool is capture-vs-bubble **phase**, not pointer capture. For drags call `e.Context.SetPointerCapture(e.Target, e.PointerId)` in down and release in up.
- `Dom.OnPointerDown((...), pointerCapture = true)` — browser-level pointer capture; use on canvas drags so events keep flowing when the cursor leaves.
- **`Sg.OnTap` / `OnDoubleTap` / `OnLongPress` fire on background misses too.** Always gate on `e.Location.Depth < 0.9999`.
- **FShade shaders must be float32-only.** `float`, `Constant.Pi`, `V3d`/`V2d`, and `member _ : float` uniforms emit GLSL `double`/`dvec3`, which WebGL2 (ESSL3) rejects at runtime. Use `3.1415927f`, `V3f`/`V2f`, `: float32` uniforms, bind `1.0f` not `1.0`.
- **FShade shader bodies must be lambda-free.** A local `let f x = …` inside a `fragment`/`vertex` body reads as an unsupported lambda — inline it.
- **`dotnet build` and `fshadeaot` do NOT catch either shader pitfall** — only the in-browser compile does, so always verify shader changes in a browser. Porting desktop-GL examples is the high-risk case.

## fsproj notes

- Client: `Microsoft.NET.Sdk.BlazorWebAssembly`, `net8.0`, `WasmBuildNative=true`, `LocalAdaptify=true`. Quick type-check: build with `-p:WasmBuildNative=false` (~35 s).
- Server: `Microsoft.NET.Sdk.Web`, `net8.0`; references the client project for static hosting. Runs on `http://localhost:5000`.
- Run Adaptify with `adaptify.cmd` (Windows) or `adaptify.sh` (Unix).

## CSS / design

- Light theme, `'Inter'`/`'Segoe UI'`, accent `#1a56db`. Body bg `#f4f6f8`, panel bg `#ffffff`, text `#0f172a`.
- All styles in `wwwroot/style.css`; no inline styles except model-dependent ones (positions, data-driven colours, cursor).
- Conditional visibility uses `Primitives.showWhen`/`showWhenNot` → `.hidden` (`display: none !important`), not inline display styles.
- The bottom dock's height is the `--dock-h` root var (default 220px, dragged via the dock's top-edge `.dock-resize` handle). Anything anchored to the dock top — the render-control height, the bottom-anchored overlays — must read `var(--dock-h)`, never a hardcoded px offset.
- `.btn-active`: darker blue with inset shadow for toggle buttons.
