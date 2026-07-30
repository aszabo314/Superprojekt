# Superprojekt — assistant notes

Research prototype for interactive 3D inspection and **registration** of geological mesh datasets (multi-epoch scans of the same terrain). Two F# projects:

- **Superserver** — ASP.NET Core + Giraffe. Serves mesh data and runs the spatial queries (Embree BVH); hosts the WASM client at `http://localhost:5000`.
- **Superprojekt** — Blazor WASM client. Aardvark.Dom Elm-style architecture, WebGL2 rendering. Thin client; heavy compute goes to the server.

See `README.md` for what the app does and how to run it. This file collects the **rules and pitfalls**; behaviour is documented by the code.

## Style

- Light theme, high contrast, print-appropriate.
- GUI must be readable to a non-expert at first glance.
- Colour families are disjoint: the scalar gradients own red/blue (diverging difference) and red→yellow→green (incidence/shape), plus pale grey = no-data and **gold = the reference** (the root everywhere; the pair's REF footprint under brush colour isolation — the `--ref-gold` CSS token family, never fork the hue). Mesh identity = distinct vivid hues (`Primitives.meshPalette`), chosen to stay clear of the diverging map's red/blue ends and near-white centre. Identity rides on thin marks (swatches, outlines, rings, chart layers) — gradients fill areas; never hand new UI a colour from another family.
- 3D marks are **duplex**: a white core with a near-black ink outline (`LineGlyphs.duplex`), readable on any terrain. All-white (no ink) is the *uncommitted* layer — the armed aim previews ONLY (a draft renders as a committed pin) — plus ONE committed exception: the pin's sphere∩surface intersection figures (area contact rings + the correspondence reveals) render single-stroke white (a deliberate user choice). Correspondence point markers (main 3D) = a screen-constant open-centre crosshair in the mesh colour over an ink under-stroke; the TILES keep mesh-colour dot fills + white outlines (a top-down small multiple needs a point mark and occludes nothing). The brushed samples are the one VALUE-coloured mark: flat camera-facing discs on the difference ramp with a thin ink rim (`Discs`) — no duplex, since the fill is the datum.
- Comments follow **Comment discipline** below.
- Concise code, no unnecessary abstractions, no premature helpers.

## Comment discipline

One test for every comment: does it say something the reader cannot get from the code under it? If not, it is redundant and **forbidden**.

Never write:

- **Restatements** — a comment that paraphrases the line or block below it (`let lastPick = … // the last pick`). Clear naming does that job; if the name isn't clear, fix the name.
- **Spec references** ("implements v14 §5") — specs are transient working documents, deleted at review time; a pointer into one is instantly dead.
- **Absence notes** ("feature X left out per spec") — code never documents what it doesn't contain.

The only reasons a comment exists:

- The code's form or function is genuinely **non-obvious** — a trick, an invariant, a unit, a coordinate frame.
- The code looks wrong or needlessly complicated because an **external library constraint** forces the shape (Aardvark/FShade/WebGL gotchas) — name the constraint so nobody "simplifies" it back.
- The code is **performance-shaped** — deliberately structured for the adaptive/render/query hot path — say what the shape buys, or the next edit flattens it.

Form: as short as possible. State the constraint or invariant, nothing else — no flourishes, no context recap, no justification essays.

## State rules (Elm-style)

- One `[<ModelType>]` `Model`; Adaptify generates the `.g.fs` files. **Never edit `.g.fs` by hand** — run `./adaptify.sh` (or `adaptify.cmd`) after editing a model file.

### Registration graph (the core state)

- `RegGraph = { Root : string option; Edges : Map<string, RegEdge> }` (RegistrationModel.fs) — a parent map, i.e. a **rooted tree**; `RegEdge = { Child; Parent; Transform; Quality }`. Child = MOV, Parent = REF (nearer the root). `Transform` is **metric world**, mapping the child's *as-loaded baseline* onto the parent's (the lsq convention); ancestor registration composes on top, so an edge never re-bakes when something above it changes.
- Composition: `pose(m) = edge.Transform * parentPose` (`RegGraph.composeAll`; Aardvark `a * b` applies **a first**). Root = identity, absent from the map. `Model.ComposedPoses` is the render-space projection (`ModelTransforms.recomposePoses` via `RigidTransform.worldToRender`); displayed pose = composed pose else as-loaded baseline (`ModelTransforms.displayedRender`/`displayedWorld`).
- **The committed graph is always a spanning tree.** `tryAddEdge` returns `EdgeAddResult`: `EdgeAdded` (ref in tree ∧ mov not), `EdgeRejected` (isolated / no root), `EdgeClosesLoop (cycleEdges, residual)` — a loop is only ever **transient** state (`Model.LoopPending` + the blocking modal); resolution removes exactly one cycle edge (`RegGraph.resolveLoop`) or discards the new edge. Never commit a graph with a cycle or a second component.
- `RegGraph.reroot` keeps the registration when the new root is a tree member: every edge on the new-root→old-root path reverses (Child/Parent swap, Transform inverted, Quality kept). **Ordering hazard in `reroot`/`resolveLoop`: remove ALL path children from the map before re-adding the reversed edges** — a reversed edge re-uses the next path edge's child as its key, so interleaving remove/add drops edges. Designating a non-member root clears the graph (a tree cannot hang off an outside mesh).
- `RegGraph.removeEdgeCascading` drops the edge **and its whole subtree** — a stranded component would break the invariant.
- Edge before/after: `EdgeSide = EdgeBefore | EdgeAfter`; `composeEdge child side g` — Before = the committed graph with *this one edge* zeroed to identity (ancestors still apply). `ModelTransforms.edgeWorld` is the metric-world form. This per-edge pairing feeds the chart's before-outline and the before pin batches; there is **no global Before/After state**.
- Solve flow: `SolvePair` (≥3 pins) orients via the tree — existing edge ⇒ re-solve same orientation, else un-treed mesh = MOV onto treed REF; pin point pairs feed `/api/query/lsq-pairs` at the **as-loaded baselines**. `PairSolved` (guarded by `pairSolveGen`) must distinguish a same-pair re-solve by checking `e.Parent = parent` — `Map.containsKey child` alone misroutes a redundant pair whose child keys an edge elsewhere. A loop-closing result stages `LoopPending` with the weakest-quality cycle edge pre-selected. `Quality = RegGraph.solveQuality residuals` = 1/(1+rms/0.05).

### Navigation: the focus rail, selection & visibility

- **Three-level focus rail** (`FocusLevel = FocusMatrix | FocusPair | FocusPin`, `Model.Focus`): strictly narrowing scopes of *what is looked at*, never tool modes — the pair toolkit (pins, Solve, inspection, peeks) stays inside its level. Free jumps among **enabled** stops via `SetFocus` (the reducer re-guards `FocusLevel.enabled`: Matrix always, Pair needs a selected pair, Pin a selected pin or an in-flight placement); `FocusAscend` = one level up (Esc; Matrix is the top). Leaving Pin with a **centred** draft goes through the **exit-guard** (see Pins): `SetFocus`/`FocusAscend` park the destination in `Model.PinExitPending` and raise the blocking confirm-delete popup instead of jumping; a centreless draft is worthless and exits silently (the jump rolls it back). The `normalizeFocus` post-step demotes to the nearest enabled ancestor whenever a reducer step retracts a level's subject (pin deleted, selection cleared).
- **Scoped selection manager** (`FocusSelection = { Pair; Pin; Point }` — ONE plain-record aval `Model.Sel`): per-level selection with **memory** (re-entering Pair restores the remembered pair incl. its last pin; the matrix highlights it) and **cascade clear** (a new pair clears pin+point+the in-cell caches; root designation and dataset switch clear all). `SelectPair` = matrix cell click (selects AND enters Pair); `SelectPin` = pin-row click (commit auto-selects the newborn pin; deleting the selected pin clears it); `SelectPoint` = the Pin level's isolate-&-focus buttons (`Some mesh` = that correspondence side, `None` = the whole pin) — **`Sel.Point` and `TileIsolate` are ONE state there**: a side click toggles both together (+ flies the main camera onto the correspondence — part of the fly-to grammar), a Pin-level tile click keeps them in step, and `jumpFocus` resets both (the pair/pin memory itself survives). Selection is deliberately *scoped per level* — never regress to a global selection blob or panel-to-panel hover/selection emitters.
- `MeshVisibility.shown focus selPair isolate hoverPair pinFocus name` (Model.fs) is the **single** shown/clickable rule: the level scope (Matrix = all meshes, the matrix hover narrows to the hovered pair; Pair = the selected pair only; Pin = the pair narrowed by `pinFocus`) **intersected with the tile isolate**. Its (isolate, pinFocus) inputs come from ONE place — `MeshVisibility.effectiveNarrowing`: a transient target (◎-side hover > armed A/B pick > tile hover) **REPLACES the committed `TileIsolate`+`Sel.Point` pair on BOTH components** — hovering another mesh while one is isolated previews THAT mesh isolated (intersecting with the stale lock would show nothing), ◉-Pin hover previews the release, an armed centre/probe keeps the lock but lifts the point narrowing; un-hover falls back to the committed pair, so restore is free. `MeshVisibility.pinShown` mirrors the scope for pins (scene nodes + blobs). Every consumer — render `MeshActive` (one shared `shownCtx` aval → cheap per-mesh projections), raycast candidate sets, coverage gating — goes through it; don't special-case visibility anywhere else. The tile isolate and the matrix hover (`MatrixHoverPair` — also drives the shader's per-pixel overlap preview) are transients — `jumpFocus` (the ONE focus-change path) wipes them, plus the peeks and the armed pick, on every jump.
- ONE `Esc` chain, in the view's key handler: pin exit-guard popup (cancel = stay) > loop modal (cancel) > a **centreless placement aborts straight to Pair** (nothing worth guarding — deliberately skips the disarm step) > armed-pick disarm (probe included — every pick is an arm) > `FocusAscend`. Rail jumps share the exit-guard gate and the `jumpFocus` cleanup, so Esc and its redundancies behave identically. New cancellable states slot into this chain, they don't get their own key.
- **Camera rule**: no GUI interaction moves the **main 3D** camera without an explicit user prompt (the double-click/fly-to grammar only — incl. the top-bar **Sensor ▾** jump-to-sensor dropdown, `FlyToSensor`). The ortho tiles are exempt **by rule** and auto-refocus on pin-flow events (`Update.frameTiles`): a new placement transaction frames the pair's overlap area, placement/edit/pin-select frame the pin (r×3), a focused point frames that point (r×1.5).
- The in-cell caches ride the **pair selection**, not the visit: Pair⇄Matrix jumps keep them (instant re-entry); only a pair change / pin edit / pose change / reroot / dataset switch invalidates.
- REF/MOV of a pair is derived, never stored: `MatrixNav.pairRefMov` (edge parent if registered, else smaller hop depth to the root, unconnected = MOV, tie → key order).

### Pins

- `ScanPin` is **atomic**: `{ Id; ShortName; Pair (PairCell.key order); AnchorMesh; CentreLocal; InnerRadius; PointA; PointB; CreatedAt; ContactRings; RevealA; RevealB }` — points are non-optional; no partial pin exists. Birth only through the placement transaction: `PlacementState.PlacementActive of PinDraft` — centre/points in free order; **completion is implicit** (`ScanPinUpdate.landDraft`): the moment the last of {centre, point A, point B} lands, the draft mints the pin and placement ends — no commit act; the newborn auto-selects. `BeginPinTransaction` jumps focus to the **Pin level** with the centre pick pre-armed and clears the pin selection (the draft is the subject).
- **A draft is a pin with parts missing — there is no pre-commit look.** `PinDraft` owns `Radius` (seeded from `QuickPinRadius`) + `Rings` + `RevealA/B`; every placed part renders through the committed-pin builders the instant it lands (`addAreaFigure`, the centre jack, the crosshair + reveal) in the main view AND the tiles, and the ⌀ Radius edit serves the draft (`SetDraftRadius`). Only the completeness flag (exit-guard) and the missing identity furniture (flag/label arrive at mint) distinguish it. `landDraft` carries Ready rings/reveals into the newborn; in-flight fetches downgrade to None so the pin postlude re-owns them.
- **The exit-guard**: leaving Pin with a **centred** draft — Esc or a rail jump, uniformly — parks the destination in `Model.PinExitPending` and raises the blocking confirm-delete popup (`GuiOverlays.pinExitModal`); confirm = the parked jump (the jump itself rolls the draft back), cancel = stay. The threshold is the centre (`placingWithCentre`): a centreless draft aborts silently straight to Pair (one Esc — the chain skips the disarm step for it). A complete pin (placement idle) exits promptless.
- **Picking is ARM-driven — no pick without an arm** (`Model.ArmedPick : ArmTarget option`, `ArmTarget = ArmCentre | ArmPoint of mesh | ArmProbe`); only camera moves are exempt. While armed, a click in ANY view (main 3D = the primary surface; the tiles = redundant ortho views) lands the pick, and the left button never orbits (the reducer swallows left rotate-begins). **The ARM TARGET is the attribution**: an `ArmPoint` pick raycasts its own mesh alone regardless of the view (this is what keeps co-located pairs pickable), `ArmCentre`/`ArmProbe` raycast both pair meshes and the nearest hit lands (`GuiPanes.armedResolve`/`armedPick`, shared by every view). Disarm = a landed pick, Esc, or re-clicking the arm control. Arming an A/B pick isolates its mesh in the main 3D (button hover previews). Validity is reducer-guarded: ArmCentre/ArmPoint at Pin (a placement or a selected pin), ArmProbe at Pair AND Pin. **Armed = a scrimmed quasi-mode**: every non-pick surface (top bar, left column, the strip's resize handle) goes `pointer-events:none` under a dark veil; only the main 3D, the pair tiles and the arming button stay live, and that button — `.arm-lit`, `z-index` above the veil, the ONE re-enabled element — doubles as the lit cancel. Pure CSS off `body:has(.arm-flag.on)` (View mounts the flag div; body's own class list is boot-managed; veils sit on the NON-scrolling containers `.top-bar`/`.left-col` — a veil on the scrolling rail would scroll away). Arming also closes the top-bar popovers (an open one would float dead outside the bar's veil box).
- **The Pin control panel** (`.pin-panel`, the Pin level's whole UI): a 2-column grid — rows = subjects (correspondence A · correspondence B · the pin), columns = the two verbs. **Edit** (arm-driven): ✚ point A / ✚ point B / ◯ Centre / ⌀ Radius — the radius slider stays hidden until its edit is clicked (`Model.PinRadiusEditOpen`; collapses on pin change and focus jump) and serves the draft too (`SetDraftRadius`). The SAME arms serve placement and committed edits: a committed point re-pick replaces atomically (`EditPointAt`); a committed centre re-pick **re-anchors** the pin onto the hit mesh (`EditCentreAt`) — the centre is not immovable. **Isolate & focus**: ◎ point A / ◎ point B / ◉ Pin — a side click TOGGLES the isolation (`Sel.Point` AND `TileIsolate` together — the same lock a tile click sets; the button highlight reads both) and on enable flies the main camera onto the correspondence point; ◉ Pin releases the isolation and flies to the whole pin (`ZoomToPin`); every click re-frames the tiles (hover previews via `PinFocusHover`). While any pick is ARMED, every existing pin mark — committed points/rings/tile marks AND the draft's already-placed parts — fades to near-invisible so nothing hides the pick spot; ONLY the armed cursor preview stays full. There is no Pin-level delete: deletion lives solely in the pair workspace's pin rows.
- **The correspondence marker = crosshair locator + intersection reveal** (main 3D; committed and draft alike). The **crosshair** (`ScanPinScene.crosshairNode`): camera-aligned, screen-constant (outer radius 0.025 × eye distance), OPEN centre — the centre IS the pick point; mesh-identity colour over an ink under-stroke; `DepthTest.None`. It **never hides** (it is the locator) but is not fully exempt: a point whose mesh isn't solid (isolation/preview) MUTES to the 0.15 fade level; the pair scope (`pinShown`) and the global armed fade apply on top. The **reveal** (`pointReveals`/`draftReveal`): the point's OWN mesh's local geometry — 3 concentric sphere∩surface rings (metric radii ×0.2/×0.6/×1.0 of `Model.RevealRadius`, gear slider `SetRevealRadius`) + 2 world-vertical axis-aligned plane∩surface relief cuts — white fading to transparent with metric distance from the point, normal depth testing. Cached per point side (`RevealState` on `ScanPin`/`PinDraft`), fetched by the `ensureRings` postlude via `/api/query/point-reveal`, stored MESH-LOCAL (rides the pose); invalidated by point re-picks, the radius slider (`invalidateReveals`) and pose changes (`invalidateRings` — the cuts' verticality bakes the pose into the request normals).
- **Reveal visibility rides the mesh** (the crosshair only mutes): `ScanPinScene.markerAlphaAt` evaluates the shown rule twice — solid under the EFFECTIVE narrowing (hover previews replace the lock, so a previewed-solid mesh shows its marks) = full; solid only under the COMMITTED state (lock / `Sel.Point`, peek-swapped) = faded 0.15 (a preview dims, never pops away); solid under neither = HIDDEN (it would float in the air). Pin AREA marks never hide with isolation — every pair pin's rings render at their original locations — but pins ANCHORED to the effective (or previewed) isolated mesh add the tiles' dashed anchorage ring in 3D too (`isoCueMeshAt`, r×1.08 white dashed), and a ◎-side hover fades the pin marks (rings + centre jacks) of pins NOT anchored to the hovered mesh (`anchorHoverDimAt`). Flags/labels stay — they are navigation furniture.
- **The armed cursor preview** (`Model.ArmPreview`, metric world) renders the about-to-be-placed mark in the main 3D AND the tiles from the same model state — all-white uncommitted glyphs (centre = a sphere outline at the radius the landing will COMMIT: the draft's, else the selected pin's; point = wire-sphere+cross, probe = cross). Main-view hover feeds it from the GPU pick (throttled, zero server traffic — the armed isolation makes the frontmost solid surface the armed set); tiles server-raycast throttled; the reducer drops landings once disarmed.
- The pin **rides its anchor mesh**: world centre = `displayedWorld(AnchorMesh)` ∘ `CentreLocal` everywhere (`ScanPin.centreWorldWith`); `CentreLocal`/`PointA`/`PointB` are stored in their mesh's **own frame**, so poses never re-bake stored geometry. Points are unconstrained (outside the area sphere is legal — the radius scopes analysis, not editing).
- **Any pin edit (radius / point re-pick / centre re-pick / delete) on a registered pair drops the edge cascadingly** (+ toast + `invalidateCellError` + `bumpPairSolve` so in-flight solves land dead). A solve's validity is exactly its input pins.

### In-cell inspection caches

- `Model.CellError` / `CellErrorBefore` (per-pin `/query/pair-error` batches at displayed poses; Before uses the edge-before poses via `composeEdge`) and `CellDist` (`/query/region-distance` MOV vs REF) share ONE generation (`cellErrorGen`); `invalidateCellError` bumps it on pair-selection change/pin edit/solve/edge drop/reroot/dataset switch (level jumps alone do NOT — the caches ride the selection). Lazy single-flight postludes: `ensureCellError`/`ensureCellDist` (Update.fs).
- Samples are stored **MOV-relative-to-REF** (sign flipped at landing when MOV was meshA in the request). Brush gids = indices into the canonical CellError sample concatenation — any refetch invalidates the brush; the Pin level's narrowed chart keeps the SAME canonical gids. The reducer caps the brushed set at 4000 gids — generous enough to span every pin of a cell (≤300 samples/pin); a tighter cap silently truncates a wide brush to its first pins' gid blocks.
- `MeshView.cellRange` is THE shared scale selector for the 3D map uniforms, the chart x-range and the legend: `ErrorRange.ofSamples` over the pin samples when any exist (always spans 0, hard cap ±0.5 m), else `ErrorRange.ofDistances` over the per-vertex distance distribution (per-sign 95th percentile, 1 mm floors — a pinless cell on the ±cap default would wash near-white). Never per-mesh normalization, no user range sliders; the Pin level keeps the full cell's range too.
- Brush = **sole focus**, and a whole render MODE: a non-empty brush suppresses the error map and puts the scene into **colour isolation** — every mesh's base AND ghost goes plain near-white and the intrinsic heatmap painters stand down (`MeshShader.ColorIsolate`; Shp keeps its cutoff — a filter, not colour), so the dots are the only coloured signal. `MeshView.brushFrameAt` is THE frame: (REF, MOV) of the selected pair while dots exist. MOV is the anchor and the one solid surface — it enters as the **default isolate** (`MeshVisibility.withBrushIsolate` at every isoLock site; an explicit `TileIsolate`, its hover previews and the peeks still win, so the mode composes); REF keeps only its footprint contour, repainted `--ref-gold` (`MeshView.coverageColorsA`), with `footprintMask` gating that composite to the pair and `outlineMask` gating the G-buffer composite to MOV alone (the G-buffer holds every mesh regardless of visibility, so a ghost's palette silhouette would compete). The dots themselves: flat camera-facing discs (`Discs`, sizing + billboarding in the vertex stage — screen-constant at `screenFrac` of the eye distance, clamped in metric world to [5 mm, 0.5 m]), coloured by their sample on the shared `cellRange` ramp, riding MOV's `markerAlphaAt` visibility (full / 0.15 / gone). The dot buffers depend on the BRUSH alone — the hovered dot's duplex ring is a separate node and the camera only moves uniforms. A plain chart click clears it all. The false-colour map paints the MOV mesh ONLY (the reference never carries error colour; `InspectPlain` swaps only MOV's base to near-white). At the **Pin level** the map is pin-LOCAL: `cellPaint` masks vertices outside the selected pin's ROI sphere with the 3e30 keep-base sentinel (the server's 1e30 no-overlap sentinel stays the pale-grey no-data signal).
- Inspection UI lives in the **docked inspection toolbox** (`GuiRail.inspectPanel`, `.inspect-dock` in the `.left-col` flex column below the rail): collapsible to its thin header edge (the header is the top-left toggle, `Model.InspectOpen` — a view preference, survives jumps), visible at Pair AND Pin — Pin narrows the diagram to the selected pin. It hosts the ONE diagram + error-map toggle (`CellMapOn`, default OFF) + the armed probe (`ArmProbe`; a landed pick disarms but the readout SURVIVES the disarm — wiped by the next arm, any jump and every cell invalidation). The probe button carries **three** states: idle, armed (`.arm-lit`, the blue lit cancel), and **holding a reading** (`.cw-probe-held`, the readout's amber, label "⊗ Clear probe") — in that third state `ToggleArmPick ArmProbe` drops the reading instead of re-arming, so clearing and re-probing are distinct clicks + the **Isolate pins** view mode (`AnchorGhostMode` — SUSPENDED while the centre pick is armed, since aiming a whole-pin move needs the full terrain; the suspension is derived (`MeshView.anchorGhostOn`), so the stored toggle restores on disarm). The pair workspace itself carries no inspection controls. The dock's right-edge **resize handle** (pure DOM, like the strip's) writes `--dockw`/`--charth` custom properties on the persistent dock div — the chart grows at its fixed aspect until it would pass the viewport bottom (minus the dock rows below it), then the height clamps and only the width keeps growing.

### Peek keys

- **V** (isolation: flips to the pair's OTHER mesh) and **B** (pose: MOV shows as-loaded) are spring-loaded — the keys AND the top-bar hold-buttons (`.tb-peeks`, always visible, disabled when the peek can't land; pointer capture so the release lands even off-button) — **pair-workspace scope: Pair AND Pin** (`peekPairLoaded`). V needs both pair meshes GPU-resident **and a pair-mesh isolate LOCK** (`TileIsolate` ∈ the pair — no isolate = nothing to swap); B needs the pair loaded and REGISTERED (`RegGraph.pairEdge` — as-loaded vs as-loaded blinks nothing); both refuse while the loop modal is open; releases always land; peeks clear on any focus jump and on dataset switch. The top-bar buttons mirror exactly these guards (`canVis`/`canPose`, off a shared `pairLoaded` — B does NOT require the isolate).
- The peeks are **purely visual and derived**: the vis peek swaps the shown rule's *effective isolate* to the other pair mesh while held — in the two effective-isolate sites only (`MeshView.buildScene` shownCtx + `View.shownNow`, so shown = clickable holds during the blink), and the Pin level's point narrowing swaps with it, since `Sel.Point` rides the same lock and scope ∩ iso would otherwise go empty — the `TileIsolate` lock itself never moves, so release reverts with zero bookkeeping (tile/focus-button highlights deliberately keep showing the lock). The pose peek: `MeshView.displayedMeshT`/`displayedWorldAt` are peek-aware (rendering + view-side picks + surface-riding pin markers follow the blink); `ModelTransforms.*` (reducer/query side) is peek-**blind** — no query may read a peeked pose. Zero refetch: during the pose peek the error map rides MOV's surface with registered-pose values (accepted approximation).

### Secondary views: the persistent tile strip (GuiPanes.fs)

- ONE right-edge **tile strip** (`GuiPanes.tileStrip`/`meshTile`, `.mesh-tiles`; width-resize handle, pure-DOM chrome), mounted ONCE per dataset and present at EVERY level: Matrix = all meshes (small multiples), Pair/Pin = the selected pair's two. Off-scope tiles hide via `.tile-off` = `position:absolute` + **`visibility:hidden`, never `display:none`** (a collapsed render control loses its viewport) and their scenes are `Sg.Active`-gated, so hidden tiles cost ~nothing per frame.
- **Tiles do VISIBILITY; arming does PICKING** (the A4–A7 crisp line): a drag-free tile click (pointer-up within ~4 px; drags pan, wheel zooms to the cursor) toggles that mesh's **isolation** (`ToggleTileIsolate`; hover = preview via `SetTileIsolateHover`; the lock highlights `.tile-iso`) — while a pick is ARMED the click lands the pick instead, and the ARM TARGET (not the tile) attributes the mesh; tile hover then feeds the armed preview via throttled server raycasts. `Sg.OnTap` stays banned in these controls (unreliable — the documented gotcha); Dom events + server raycasts only.
- Tiles honour the per-mesh survey heatmap switches and carry the shared **top-down ORTHOGRAPHIC 2D camera only** (`Model.TileCams` + `SetTileCam`: XY drag-pan anchored at the drag start, wheel zoom TO the cursor; `Radius` drives the ortho half-width via tan 30°; the eye rides `Radius + scene-Z-extent` above the centre plane so nothing near-clips at any zoom; reset on dataset switch; the pin-flow refocus writes it — see the camera rule) — no orbit, no selection emitters. The DEFAULT framing (no stored cam) is the **reference root's bounds**, so unpanned tiles all show the same area (comparable small multiples); own bounds only without a root. Hovering a pair-workspace pin row preview-frames EVERY tile on that pin (`Model.TilePinHover`, transient — `tileCamOf` overrides with the exact framing a row click makes persistent, so click = "keep what you see").
- **Every tile overlays the reference root's gold footprint outline, unobscured**: a per-tile root-only coverage pass (`MeshView.buildRootCoverageNode`, channel 0, gated by the tile's scope) through `OutlineView.rootCoverageOffscreen` + `buildRootOutline` — the `OutlineCoverageEdge` composite reused with a slot-0-only mask and `Primitives.refGoldV3d` (the ONE F# mirror of `--ref-gold`; the 3D root bbox outline reads it too) in `CoverageColors[0]`, DepthTest.None in passOne.
- Tiles use `MeshView.buildPaneScene` (the shipped `MeshShader.shade` with inspection modes off, survey heatmap live); its `overlap` = (`other : aval<string option>` — Some while the tile's mesh sits in the selected pair — plus a per-tile coverage MRT from the tile camera, `IBackendTexture → ITexture` through `AVal.map`; aval is invariant — the aval itself cannot upcast). The MRT feeds the isolate-overlap gate while a placement is armed (solid only where BOTH pair channels cover the pixel; beyond the 8-channel cap the gate disengages outright) and renders ONLY while the gate can read it — `OutlineView.coverageOffscreen`/`MeshView.buildCoverageNode` take an `active` gate (the main view passes constant true). Pin marks (committed points, area sphere, draft, armed preview) are pair-scoped (`marksOn`) — they show at Pair AND Pin. The selected pin's (and the draft's) area circle draws in BOTH pair tiles — the centre rides the ANCHOR mesh's pose whichever tile draws it — and the anchor tile alone adds a dashed slightly-larger outer ring, the anchorage cue.

### Misc

- **Top bar**: the view-control cluster = ▤ Cut · ▤ Far · **Sensor ▾** (the per-mesh jump-to-sensor dropdown, `SensorMenuOpen` → `FlyToSensor`) · the Peek hold-buttons. Right side: the hidden **▦ mesh menu** (`MeshMenuOpen`) = ☆ Set-reference (the ONLY root-change path) + the per-mesh render toggles (Tex/Dst/Shp/Inc) + the Shape ≥ slider — deliberately out of the workflow rail. The matrix has no reorder control; rows/cols are sensor (acquisition) order.
- Debounce/generation state (CTS + counters) lives at module level in `UpdateHelpers`/`ScanPinUpdate`, **not** in the Elm model.
- The server is stateless w.r.t. the registration tree — every query carries explicit transforms.

## Render pipeline (single forward pass)

One forward pass into the main framebuffer — `passZero`: meshes (custom α + α-gated depth) then pin geometry (`DepthTest.LessOrEqual`, blended); `passOne`: coordinate cross, labels, overlay lines, dark duplex copies (`DepthTest.None`, always on top); `passTwo`: the duplex **white cores**, so they deterministically layer over their dark copies (within-pass order is arbitrary). The main frustum is near **1 cm** / far **1000 m** METRIC (× `DatasetScale` into render units, in View.fs twice — the render control AND the overlay-tooltip projection must match). Offscreen consumers: the image-space outline pass and the footprint coverage MRT (plus one coverage MRT per strip tile, from that tile's camera, gated to armed placements on its pair). The secondary views (the tile strip) are their own render controls — see **Secondary views** above.

Contracts the rest of the stack relies on (`MeshShader.shade`):

- **α-gated depth**: fragments with α ≥ 0.99 write their natural window depth; ghost/outside fragments write 1.0 (far) — so ghosts never occlude anything and pixel picks fall straight through them.
- **Ghost colour is uniform**: ghost fragments always use the solid per-mesh palette colour regardless of rendering mode, so a ghost silhouette reads as one shape — the ONE exception is `ColorIsolate` (brush), which whitens the ghost too: still ghosted, just no longer carrying identity colour.
- Solid/ghost/invisible is decided per fragment from `MeshActive` × the global ghost floor (`GhostSilhouette`/`GhostOpacity`; floor off ⇒ ghost fragments are *discarded*, i.e. hidden not translucent) × the pin-isolation blob mask (`Blobs` uniform array, hard cap 32, metric → render at upload; the in-flight draft's area is a blob too, so the in-edit patch reads opaque under Isolate pins) × the overlap gate (`OverlapPreview`: solid only where the footprint coverage MRT covers the pixel in BOTH active-pair channels — a screen-space test along the camera ray, sampled at `gl_FragCoord`/`ViewportSize`; lit by the matrix hover AND by the pin-location interactions — the ○ New pin hover and the armed centre pick — where only the overlap is a valid spot, `MeshView.overlapPreviewUniforms`). The scalar-field painters (difference/heatmaps) only touch **above-ghost** fragments.
- **Near/far cuts**: `NearCutFrac` (top-bar ▤ Cut slider; 0 = off) discards fragments in front of a camera-forward plane, `FarCutFrac` (▤ Far slider; off at the RIGHT end ≥ 2.495 — a small fraction cuts nearly everything) discards beyond one; both paint a flat-ink intersection band and the outline G-buffer applies the same cuts so silhouettes follow them.

### One in-cell error range (never per-mesh normalization)

- The difference map, chart and legend all read the ONE `MeshView.cellRange` scale (pin samples, else the distance distribution — see State rules) — signed, spans 0, capped ±0.5 m. Shader uniforms: `DistScale` = hi, `DistLoNeg` = |lo|. Two sentinels: 1e30 (server no-Z-overlap) paints PALE GREY — grey = no-data, the near-white centre stays reserved for "difference ≈ 0"; 3e30 (outside the Pin level's pin-local ROI) keeps the base colour.
- The diverging map is **asymmetric piecewise Coolwarm** (Colorcet CET-D01 — a deliberate user choice over the earlier yellow-centred map): zero = the ramp's near-white centre #EDE7EA (welded to 0 — grey is reserved for "no data", never "0"), each sign runs zero→mid→saturated end (lavender→blue #2151DB / salmon→red #C00206) normalized by its own end with the t^0.6 near-zero boost (`Primitives.Diff.colorSignedV3`, mirrored in `MeshShaders` — keep the two in sync). Values outside clamp to the end colours.
- Difference maps carry **value isolines**: dark derivative-antialiased contours every `Diff.isoStep` metres (a nice 1/2/5 step ≈ span/8 of the shared range, so 0 is always a contour), suppressed where the colour clamps; step is passed as a uniform, not model state. `ddx`/`ddy` exist in FShade, `fwidth` does NOT.
- Intrinsic heatmaps (per mesh, the top-bar ▦ mesh menu): **Range** normalizes by the ONE all-mesh end `MeshView.rangeMaxWorld`; **Incidence** uses the geometric (screen-derivative) normal sign-oriented by the stored vertex normal, clamped at 0 — never `abs` (away-facing = never scanned = worst); **Shape** discards fragments below the global `ShapeThreshold` slider.
- The bottom-centre legend (`GuiOverlays.colorLegend`) has exactly two states: the in-cell difference map (wins while the cell map paints **or** brushed dots exist — same ramp, same range, never a second legend) else the Range heatmap while any mesh has it active; hidden otherwise. On the difference state the 3D-hovered dot marks its value on the bar (the tooltip's exact number, the dot's own sample until that fetch lands) in the diagram's hover amber.

### The cell chart

ONE canvas diagram (`GuiRail.chartJs` + `inspectBody`, mounted in the docked inspection toolbox): the MOV mesh's error across the pair's pins, pin-source-stacked 48-bin histogram (achromatic pin ramp, per-pin median ticks, pooled-LoD band, mm axis), full furniture always — a pinless cell renders the same furniture with a placeholder, never a blank panel. The Pin level narrows the series to the selected pin (gids stay canonical, x-range stays the full cell's). Registered pairs overlay the edge-before histogram as a near-black step outline ("fill now · line before"). x-drag brush + hover cross-highlight ride a shared JS bridge; re-render on resize/show via ResizeObserver. The chart carries no selection UI.

### `Sg.DepthMask` is forbidden

Buggy in this Aardvark/Aardworx WebGL build — it silently breaks the depth pipeline. Steer ordering with `Sg.DepthTest` + `Sg.Pass` alone. Lines, pin geometry and text therefore all write depth; that violates the textbook "translucent shouldn't write depth" rule but is the only combination that renders correctly in this stack. Leave the in-code reminders in `LineShader.fs` / `SceneGraph.fs`.

### Image-space outline pass (`OutlineView.fs`)

MRT G-buffer (world-Z band parity + window depth → target0, palette colour + coverage → target1) → fullscreen edge-detect painting silhouettes + elevation isolines. Non-obvious choices — keep them:

- The depth edge is the **second difference** (Laplacian) `|l + r − 2c|`, *not* a first difference: window depth is linear in screen space across any planar primitive, so the Laplacian is ~0 on a smooth slope at any view angle and spikes only at a genuine break. A first difference measures screen-space depth *slope* and lights up every grazing surface as false bands.
- `OutlineWidthPx` (gear slider) widens lines by sampling the depth break at ±width texels while the parity edge stays ±1 — silhouettes thicken, isolines don't.
- target0 is **`Rgba8`**; the window depth is packed hi (.w) / lo (.z) as 16-bit fixed point. The edge detect reads the HI byte alone — 256 levels (1 LSB ≈ 0.004), so an `OutlineThreshold` below that quantization floor makes the staircase risers of a smooth slope read as false bands.
- The isoline signal is band **parity** (`floor(wp.Z / ContourSpacing) mod 2`) — a step function, so its edge is a plain first difference; because the band index is a pure function of world Z, the contours stay welded to fixed world-Z planes and do not crawl as the camera orbits.
- Per-mesh FOOTPRINT contours come from a second, occlusion-free pass: an additive coverage MRT (one channel per mesh, 2×Rgba8, NO depth — cap 8 meshes) + the `OutlineCoverageEdge` composite, which outlines each channel's covered↔uncovered transition in that mesh's palette colour. The depth-tested combined G-buffer can only ever outline the visible **union** (no depth break where one mesh ends over a co-located one; hidden boundaries aren't in the buffer at all) — never remove the coverage pass in favour of it. The main-view MRT renders ONCE per frame (`OutlineView.coverageOffscreen`, shared through SceneGraph) — the footprint composite *and* the forward mesh shader's overlap preview (matrix hover / new-pin hover / armed centre) both sample the same textures; each strip tile renders its own from the tile camera for the armed-placement gate (active-gated — it renders only while the gate can read it). A pair mesh beyond the 8-channel cap disables the preview/gate outright (`MeshView.overlapPreviewUniforms` / `buildPaneScene`) rather than half-testing.
- The G-buffer writes a mesh id (`MeshId` = (index+1)/255, target0.y) and the edge composite gates lines through the `OutlineMask` slot array — all-on by default, narrowed by brush colour isolation (`MeshView.outlineMask` = MOV alone; `footprintMask` = the pair, for the coverage composite, whose `CoverageColors` are adaptive so the REF slot can go gold). Slot index = the display index for BOTH composites.
- `ContourSpacing` is **camera-adaptive**: ~24 contours across the view derived from the orbit radius, SNAPPED to a nice 1/2/5 world-metre step (discrete ticks — zooming out thins the lines stepwise, orbiting never changes them). The gear's `IsolineBands` sets the densest allowed spacing; the far end caps at ≥4 contours over the scene Z range. The difference-map value isolines need no camera term — their in-shader derivative fade already suppresses overcrowding.

### Picking

- `Sg.OnTap` / `OnDoubleTap` / `OnLongPress` **fire on background misses too** — every handler that builds state from the hit must gate: `if e.Location.Depth < 0.9999 then Some e.WorldPosition else None` (background leaves depth at the clear value 1.0).
- Ghost fragments leave depth at 1.0, so the GPU pixel pick cannot land on them. Anything that needs a 3D point on a possibly-ghosted surface keeps the GPU pick as the fast path and falls back to a server raycast (`View.resolvePick` / `raycastNearest*`; armed picks raycast their arm-target candidate set in `GuiPanes.armedResolve`) — un-apply the mesh's displayed pose before the query, re-apply it to the hit. Hover-driven raycasts must be throttled (~60–80 ms) + generation-guarded.
- **Every node without `Sg.NoEvents` writes the GPU pick buffer** (id + `gl_FragCoord.z`, blending forced off there — screen alpha is irrelevant, `DepthTest.None` wins unconditionally). Overlay/composite geometry — especially fullscreen quads like the outline composite — must set `Sg.NoEvents` or it hijacks every pick with its own depth.

## Coordinate systems & transform hierarchy

Three spaces, two transforms. Keep them strictly separate — every boundary crossing goes through a named helper, never bare `* scale` / `± centroid` arithmetic.

**Spaces**

- **Mesh / server frame**: the mesh's stored OBJ coordinates `+ meshCentroid`. **Every `/api/query/*` coordinate — in and out — is in this frame**; the server subtracts the centroid itself.
- **Metric world**: the app's single canonical world (metres). Pin centres/radii/points-as-world, cursor world, graph edge transforms all live here. Metric world ≡ a mesh's server frame exactly at the load pose.
- **Render space**: what the GPU and cameras use — centroid-recentred, dataset-scaled, then posed.

**Two transforms — dataset first, then workspace:**

1. **Dataset transform** — a *similarity* (uniform scale + translation, never rotation), fixed per dataset. The **only** place `DatasetScale` and `CommonCentroid` enter. Cross it with `ScanPin.renderCentre`/`worldCentre` (points) and `ScanPin.renderLength` (lengths).
2. **Workspace transform** — a *rigid* per-mesh pose composed from the registration graph (else the as-loaded baseline). Render form = `ModelTransforms.displayedRender` / `MeshView.displayedMeshT` (the latter also peek-aware); metric-world form = `ModelTransforms.displayedWorld` / `MeshView.displayedWorldAt`. `RigidTransform.worldToRender`/`renderToWorld` conjugate a rigid pose between the two (the dataset similarity is the conjugator). **`displayedWorld.Backward` maps metric world → the mesh's server frame; `.Forward` maps back.**

**Discipline rules**

- Server queries: convert metric world in with `displayedWorld.Backward`, map results out with `.Forward`. Pair queries (`pair-error`, `pair-overlap`, `region-distance`, `lsq-pairs`) instead pass each mesh's world transform explicitly and let the server place them.
- Scene-graph geometry is render space: convert model values at the boundary (`renderCentre`/`renderLength`, or `worldToRender` for poses).
- Directions need no scale handling (uniform scale ⇒ parallel); only the workspace rotation matters (`TransformDir`).
- Pin geometry (`CentreLocal`, `PointA`, `PointB`) is stored in its mesh's **own frame** (`displayedWorld.Backward world` at pick time) — pose-independent, moves with the mesh for free.

## Sensor positions

Each mesh's OBJ origin **is** its scan sensor: the radial-OPC pipeline centres every scan on its station, and `*centroid.txt` records that origin's absolute world coordinate (data-verified — stored-frame vertex means sit at the origin, and Job_0792's origin lies 190 m from its siblings' because that scan has its own station). Sensor world position = `DatasetCentroids[mesh]` (`ModelTransforms.sensorWorld`/`sensorRender`); there is no separate sensor file or endpoint (the old hand-estimated `pano-centers.txt` layer marked visual data centres, not stations, and was deleted). Consumers: the incidence/range heatmap sensor origin (the posed mesh origin), the top-bar Sensor ▾ jump (`FlyToSensor` — a close sensor-**viewpoint** orbit via `FlyToPoint` at 10 m riding the displayed pose, not an overview framing), the dataset-load camera framing, and the coordinate-cross position.

## Adaptive performance (critical)

In the scene graph, **never depend on an entire record when you only need a subset of its fields**. The Elm-style model replaces whole records on every update, so an `AVal.map` over a full `ScanPin` (or `Model`) fires on *any* field change.

**Rule: project individual fields into separate `aval`s early, then build the dependency graph from those.**

```fsharp
// BAD — rebuilds on ANY pin change (rings result, radius, …)
let geo = pinVal |> AVal.map (fun po -> ... po.ContactRings ... po.InnerRadius ...)
// GOOD — only when the rings or radius actually change
let ringsVal  = pinVal |> AVal.map (Option.map (fun p -> p.ContactRings))
let radiusVal = pinVal |> AVal.map (Option.map (fun p -> p.InnerRadius))
let geo = (ringsVal, radiusVal) ||> AVal.map2 (fun rings r -> ...)
```

For scene-graph nodes (`Sg.Text`, `sg { ... }`) this matters even more: rebuilding an `AList` of sg nodes destroys and recreates GPU resources (font atlases, draw calls). Therefore:

- **Split structure from placement.** Build static sg node lists from slowly-changing data; use adaptive `Sg.Trafo` for fast-changing placement (uniform update, no rebuild).
- **Push adaptivity down.** A parent `AList.ofAVal` that rebuilds all children is expensive; an `AVal`-driven `Sg.Trafo` per stable child is cheap.
- **Don't build in-place-updating *lists* with `AVal.map (… IndexList.ofList) |> AList.ofAVal`.** That mints fresh `Index` keys every recompute, so any element change diffs as remove-all + add-all — churning every row's DOM/GPU resources and intermittently double-rendering in the reconciler. Derive a stable-identity incremental list instead: `AMap.map (project to just the row's inputs) |> AMap.toASet |> ASet.sortBy key |> AList.map row`. Small, rarely-changing lists may use the simple form.
- **Never create a *transient* `aval` inside another aval's compute and read it.** `AVal.custom (fun t -> … (makeSomeAval args).GetValue t …)` (or `AVal.force` of a freshly built aval) can drop the dependency edge, so the outer aval **evaluates once and silently freezes** (historically: a panel rendered blank because it never saw the meshes load). Fix: **inline** the inner computation so its `model.X.GetValue t` reads hit the outer token directly, or bind the inner aval **once** outside the compute (a stable `let`). Reading a *stable* aval via `.GetValue t` is correct — only per-eval-built avals are the trap.

Sanctioned exception: the screen-constant pin marks — the 3D **flags** (every element derives from the ONE per-pin `ScanPin.flagHeightRender`: fixed fraction of the eye distance, clamped to 0.1–20 m metric world, × the gear's `FlagScale`; the label is `Sg.Trafo`-only), the centre jacks, the correspondence **crosshairs** and the hovered brush dot's ring — read `view` and recompute per camera move on purpose (a handful of glyphs). Only the name billboards (Z-yaw to the eye). The brushed dots themselves are the counter-example and the rule for anything numerous: their screen-constant sizing and billboarding happen in the VERTEX SHADER (`Discs`), so ≤4000 marks cost three uniform updates per camera move instead of a buffer rebuild.

## Server query performance

Costly spatial queries (`pair-error`, `contact-rings`, `region-distance`) scale with mesh size × sample density:

- **Never issue per-pair/per-pin requests sequentially** — batch into one request where the endpoint supports it (`pair-error` takes all pins of a pair); fan independent fetches out with `Async.Parallel` (the pair-overlap sweep).
- **Parallelise the heavy server inner loop** when inputs are independent — Embree `Scene.Intersect` is thread-safe (`Parallel.For` in the handlers).
- **Cap density rather than grow linearly** (`maxPointsPerMesh` / sample strides / per-pin sample caps).
- **Debounce user-driven triggers** with a `CancellationTokenSource` + generation counter so the next event cancels the previous and at most one fetch is in flight per invalidation.
- **Mesh caches are warmed at dataset load** by `bboxesHandler`, so the first interactive query never pays the lazy-load cost.

## Client compile order (`Superprojekt.fsproj`)

```
MeshData.fs            mesh fetch/parse, ApiConfig, shared Http.client
Query.fs               server query wrappers (Async)
CameraModel.fs / .g.fs OrbitState [<ModelType>]
OrbitController.fs     orbit camera + messages (project file, NOT the Aardvark library one)
RegistrationModel.fs   ScanPinId, RegGraph/RegEdge, PairCell, MatrixNav, ErrorRange (WASM-free, shared with Supertests)
ScanPinModel.fs / .g.fs ScanPin + placement state
PinGeometry.fs         icosphere, sphere outline
Model.fs / .g.fs       [<ModelType>] Model + FocusLevel/FocusSelection + MeshVisibility + ModelTransforms
LineShader.fs          flat colour + pixel-constant 3D lines, Discs, LineGlyphs
Primitives.fs          widgets, showWhen, observedRender, palettes, Diff colormap, friendly names
Messages.fs            Message DU
ScanPinUpdate.fs       pin transaction sub-reducer + rings/reveal postlude
UpdateHelpers.fs       reducer helpers + debounce/generation state
Update.fs              main reducer + cell-error/cell-dist/pair-overlap postludes
MeshShaders.fs         RenderPass + MeshShader + outline/coverage shaders
MeshView.fs            LoadedMesh, buildScene, displayed transforms (peek-aware), pin blobs, offscreen nodes
OutlineView.fs         offscreen image-space outline/coverage passes
ScanPinScene.fs        pin sg nodes: markers, rings, flags, brushed samples
SceneGraph.fs          scene composition + cross + labels + root outline
GuiPanes.fs            the persistent tile strip (isolate buttons + armed pick surface)
GuiTopBar.fs           top bar (cut sliders, sensor dropdown, mesh menu, gear popover)
GuiOverlays.fs         toast, scale bar, colour legend, orientation indicator, loop + pin-exit modals
GuiRail.fs             left navigator: focus rail + per-level views (matrix, pair workspace, pin control panel) + the inspection dock
View.fs                view function, pick routing, Esc/peek keys + App module
ShaderCache.fs / Program.fs
```

## Server compile order (`Superserver.fsproj`)

```
MeshLoader.fs        OBJ parse, centroid file, atlas paths
MeshCache.fs         Embree scene + BbTree cache (lazy, permanent)
MeshAnalysisCore.fs  pure level-set tracer + decimate (WASM/Embree-free, shared with Supertests)
MeshAnalysis.fs      sphere contact-ring tracing
PairError.fs         pairwise symmetric M3C2-style error (pin batches, at-point, overlap)
RegMath.fs           weighted Umeyama rigid landmark solve (Jacobi SVD, conditioning)
QueryHandlers.fs     HTTP query handlers
Handlers.fs          routing
Program.fs           ASP.NET startup
```

## API endpoints

```
GET  /api/datasets                              → string[]
GET  /api/datasets/default                      → string (data/default.txt, fallback = first)
GET  /api/datasets/{dataset}/centroids          → { meshName: [x,y,z] }
GET  /api/datasets/{dataset}/bboxes             → { meshName: { min, max } }   (warms the mesh cache)
GET  /api/datasets/{dataset}/mesh/{name}/{i}    → binary mesh
GET  /api/datasets/{dataset}/mesh/{name}/{i}/atlas → JPEG
POST /api/query/ray                             → { hit, t, point, triangleId }   Name = "dataset/mesh"
POST /api/query/closest                         → { found, point, distanceSquared, triangleId }
POST /api/query/contact-rings                   → sphere–surface intersection polylines
POST /api/query/point-reveal                    → correspondence-marker reveal: concentric sphere rings + plane∩surface cuts around a point, one flat polyline list (point + plane normals in the mesh's server frame — the caller bakes the displayed pose into the normals)
POST /api/query/lsq-pairs                       → weighted rigid solve (absolute world transform + residuals + conditioning; 400 on <3 pairs)
POST /api/query/pair-error                      → per-pin pooled symmetric error of mesh B rel A at explicit poses (median, LoD half-width, samples + positions; per-pin ok=false on no overlap — the batch never fails on it)
POST /api/query/pair-error-at                   → exact signed value at one picked point (1 mm ray back-off so on-surface picks register)
POST /api/query/pair-overlap                    → registerability of a pair at supplied poses (two-way closest-point coverage fractions)
POST /api/query/region-distance                 → per-vertex signed M3C2 distance of a target mesh to a reference mesh, in the target's served vertex order; a vertex responds only where the vertical world line through it pierces the reference (Z-overlap), else 1e30 sentinel — so error is never fabricated in non-overlap fringes
```

All query coordinates are **absolute world space**; the server computes `localPos = worldPos − meshCentroid`; pair queries carry explicit per-mesh transforms. Endpoints without consumers get deleted — don't re-add one without a consumer.

## Tests

`src/Supertests` — console runner (no test packages) compiling `RegistrationModel.fs` + `RegMath.fs` + `MeshAnalysisCore.fs` directly: `dotnet run --project src/Supertests`. The registration-graph invariants (tree, reroot, cascade, loop resolution, composition) live here — extend them when touching `RegGraph`. Integration against a running server (covers lsq-pairs, pair-error, pair-error-at, pair-overlap, region-distance): `ASPNETCORE_URLS=http://localhost:8002 dotnet run --project src/Superserver`, then `node tools/integration.mjs`.

## F# pitfalls (learned the hard way)

- **Deleting a DU case turns its remaining patterns into catch-alls.** `function DeletedCase -> true | _ -> false` still compiles — F# reparses the name as a *variable pattern* that matches everything (only warning FS0049 betrays it), silently inverting the logic. After removing a case, grep for its name AND check the build for FS0049/FS0025/FS0026 before trusting green.
- **`Trafo3d` is a struct** — `obj.ReferenceEquals` on boxed values is always false. Memoization/identity tests must use value equality (or prove non-recompute via sentinel poisoning), never reference identity.
- **Aardvark `a * b` composes apply-a-first** — the opposite of textbook matrix notation. The graph composition `edge.Transform * parentPose` and every conjugation in `RigidTransform` depend on this; sanity-check any new composition with a translation-only case.
- `Error`/`Ok` are shadowed by Aardvark.Base — qualify `Result.Ok`/`Result.Error` in client code.

## Aardvark.Dom gotchas

- `Attribute("for", "...")` on `<label>` is silently dropped — nest `<input>` inside `<label>`.
- `Attribute("checked", "")` is dropped — use `Attribute("checked", "checked")`.
- CSS `~` sibling combinator breaks (Aardvark inserts wrapper nodes) — use `:has()` on a known ancestor.
- `RenderControlInfo` and `TraversalState` both have `.Runtime` — annotate `(info : Aardvark.Dom.RenderControlInfo)` when ambiguous.
- `yield!` is not supported in Aardvark.Dom CE builders — use OnBoot JS with MutationObserver for dynamic SVG/canvas (the `observedRender` helper). CE ambiguity errors (FS0792) around `AList`/`aval` expressions in `div { }` are fixed by binding the expression to a `let` first.
- **OnBoot may run before a *later-sibling* node is mounted.** Don't capture a following sibling at boot (`querySelector` stored in a closure freezes `null` forever) — look siblings up **lazily** inside the handler that needs them. Boot-time capture is only safe for **ancestors** (`closest(...)`).
- `renderControl { ... }` nests fine inside `div { ... }`, but **`Sg.OnTap` (and other Sg pointer events) do NOT fire reliably in secondary render controls** — use Dom pointer handlers + a server raycast if one is ever added again.
- `AVal.map4` does not exist — combine with `AVal.map2`/`AVal.map3`.
- `Dom.Style` for renderControl; `Style` for HTML elements. `Css.Custom` does not exist — use CSS classes in `style.css`.
- **`RenderControl.ViewportSize` is framebuffer pixels**; `RenderControl.ClientSize` is CSS pixels. Anything mixing with DOM coordinates (overlay placement, cursor → NDC) must work in CSS px or it breaks on hi-dpi. ClientSize is `V2i.II` until the first DOM event; the main control binds ClientSize with a ViewportSize fallback.
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
- Conditional visibility uses `Primitives.showWhen` → `.hidden` (`display: none !important`), not inline display styles.
- Gold root markers use the `--ref-gold`/`--ref-gold-dark`/`--ref-gold-pale` tokens — never a literal gold hex.
- Toggle-active states: `.tb-btn-active` (top bar), `.rail-btn-active` (navigator), `.cbb-btn-active` (compact button bars) — darker blue, inset shadow.
