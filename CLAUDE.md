# Superprojekt — assistant notes

Interactive 3D inspection and **pairwise registration** of geological mesh datasets (multi-epoch scans of the same terrain). The project is complete; git history is the record of how it got here.

- **Superserver** — ASP.NET Core + Giraffe. Serves mesh data, runs the spatial queries (Embree BVH), hosts the WASM client. Dev: `http://localhost:8002`.
- **Superprojekt** — Blazor WASM client. Aardvark.Dom Elm-style architecture, WebGL2 rendering. Thin client; heavy compute goes to the server.

`README.md` says what the app does and how to run it. This file holds the **rules and pitfalls**; behaviour is documented by the code.

## Style

- Light theme, high contrast, print-appropriate. GUI readable to a non-expert at first glance.
- Concise code, no unnecessary abstractions, no premature helpers.
- **Colour families are disjoint.** Scalar gradients own red/blue (diverging difference) and red→yellow→green (incidence/shape); pale grey = no data. **Gold FILL = the reference** (the root everywhere; the pair's REF footprint under brush colour isolation) — the `--ref-gold` token family, never fork the hue. **Gold OUTLINE/GLOW = transient hover/focus** — always an outline, never a fill, so a highlighted pin can't read as the reference. Mesh identity = distinct vivid hues (`Primitives.meshPalette`), clear of the diverging ramp's ends and near-white centre, carried on thin marks only (swatches, outlines, rings, chart layers) — gradients fill areas. Never give new UI a colour from another family.
- **3D marks are duplex**: white core + near-black ink outline (`LineGlyphs.duplex`). All-white = the *uncommitted* layer (armed aim previews only — a draft renders as a committed pin), with one committed exception: the pin's sphere∩surface figures (contact rings + correspondence reveals) are single-stroke white. Correspondence points = a screen-constant open-centre triplex crosshair (white rim / ink / mesh-identity core; widths × `Model.MarkerWeight`, which also scales the reveal's lines); the tiles render the same glyph top-down. Brushed samples are the one value-coloured mark: flat camera-facing discs on the difference ramp with a thin ink rim (`Discs`).

## Comment discipline

A comment must say something the reader cannot get from the code under it; otherwise it is forbidden. Never write restatements (fix the name instead), references to specs/phases/tickets (they are dead on arrival), or absence notes (code never documents what it doesn't contain).

A comment exists only because the code is genuinely **non-obvious** (a trick, invariant, unit, coordinate frame), is shaped by an **external library constraint** (name it, so nobody "simplifies" it back), or is **performance-shaped** (say what the shape buys). Keep it as short as possible: the constraint or invariant, nothing else.

## State rules (Elm-style)

One `[<ModelType>]` `Model`; Adaptify generates the `.g.fs` files. **Never edit `.g.fs` by hand** — run `./adaptify.sh` / `adaptify.cmd` after editing a model file. Debounce/generation state (CTS + counters) lives at module level in `UpdateHelpers`/`ScanPinUpdate`, not in the model.

### Registration graph (the core state)

- `RegGraph = { Root : string option; Edges : Map<string, RegEdge> }` (RegistrationModel.fs) is a parent map, i.e. a **rooted tree**. `RegEdge = { Child; Parent; Transform; Quality }`: Child = MOV, Parent = REF (nearer the root). `Transform` is **metric world**, mapping the child's *as-loaded baseline* onto the parent's (the lsq convention); ancestor registration composes on top, so an edge never re-bakes when something above it changes.
- Composition: `pose(m) = edge.Transform * parentPose` (`RegGraph.composeAll`; Aardvark `a * b` applies **a first**). Root = identity, absent from the map. `Model.ComposedPoses` is the render-space projection (`ModelTransforms.recomposePoses`); displayed pose = composed pose, else the as-loaded baseline (`ModelTransforms.displayedRender`/`displayedWorld`).
- **The committed graph is always a spanning tree.** `tryAddEdge` returns `EdgeAdded` (ref in tree ∧ mov not), `EdgeRejected` (isolated / no root) or `EdgeClosesLoop (cycleEdges, residual)`. A loop is only ever transient (`Model.LoopPending` + the blocking modal); resolution removes exactly one cycle edge (`RegGraph.resolveLoop`) or discards the new edge. Never commit a cycle or a second component.
- `RegGraph.reroot` keeps the registration when the new root is a tree member: every edge on the new-root→old-root path reverses (Child/Parent swap, Transform inverted, Quality kept). Designating a non-member root clears the graph. **Ordering hazard in `reroot`/`resolveLoop`: remove ALL path children from the map before re-adding the reversed edges** — a reversed edge re-uses the next path edge's child as its key, so interleaving drops edges.
- `RegGraph.removeEdgeCascading` drops the edge **and its whole subtree** — a stranded component would break the invariant.
- Before/after is **per edge**, never a global state: `composeEdge child side g` with `EdgeBefore` = the committed graph with *this one edge* zeroed to identity (ancestors still apply); `ModelTransforms.edgeWorld` is the metric-world form. It feeds the chart's before-outline and the before pin batches.
- Solve: `SolvePair` (≥3 pins) orients via the tree — an existing edge re-solves in the same orientation, else the un-treed mesh is MOV onto the treed REF. Pin point pairs feed `/api/query/lsq-pairs` at the **as-loaded baselines**. `PairSolved` (guarded by `pairSolveGen`) must detect a same-pair re-solve by checking `e.Parent = parent` — `Map.containsKey child` alone misroutes a redundant pair whose child keys an edge elsewhere. A loop-closing result stages `LoopPending` with the weakest-quality cycle edge pre-selected. `Quality = RegGraph.solveQuality residuals` = 1/(1+rms/0.05).
- REF/MOV of a pair is derived, never stored: `MatrixNav.pairRefMov` (edge parent if registered, else smaller hop depth to the root; unconnected = MOV; tie → key order).

### Focus rail, selection, visibility

- **Three-level focus rail** (`FocusLevel = FocusMatrix | FocusPair | FocusPin`, `Model.Focus`): strictly narrowing scopes of *what is looked at*, never tool modes. `SetFocus` jumps among **enabled** stops (the reducer re-guards `FocusLevel.enabled`: Matrix always, Pair needs a selected pair, Pin a selected pin or an in-flight placement); `FocusAscend` = one level up. `jumpFocus` is the ONE focus-change path: it wipes the transients (tile isolate, matrix hover, peeks, armed pick). `normalizeFocus` demotes to the nearest enabled ancestor whenever a reducer step retracts a level's subject.
- **Scoped selection** (`Model.Sel : FocusSelection = { Pair; Pin; Point }`, one plain-record aval): per-level selection with memory (re-entering Pair restores the pair and its last pin) and cascade clear (a new pair clears pin + point + the in-cell caches; root designation and dataset switch clear all). `SelectPair` = matrix cell click (selects and enters Pair); `SelectPin` = the pin row's ▸ button (a newborn pin auto-selects; deleting the selected pin clears it). **`Sel.Point` and `TileIsolate` are one state at Pin**: a Pin-level tile click on a pair mesh sets both, `jumpFocus` resets both. Never regress to a global selection blob or panel-to-panel hover/selection emitters.
- **`MeshVisibility.shown` (Model.fs) is the single shown/clickable rule**: the level scope (Matrix = all meshes, narrowed by the matrix hover to the hovered pair and by `matrixScope` to the graph map's painted set; Pair = the selected pair; Pin = the pair narrowed by `pinFocus`) intersected with the tile isolate. Its (isolate, pinFocus) inputs come only from `MeshVisibility.effectiveNarrowing`: a transient target (arm-button hover `PinFocusHover` > armed A/B pick > tile hover) **replaces** the committed `TileIsolate` + `Sel.Point` pair on both components — intersecting with a stale lock would show nothing; un-hover falls back to the committed pair. An armed centre pick keeps the lock but lifts the point narrowing. `MeshVisibility.pinShown` mirrors the scope for pins. Every consumer (render `MeshActive`, raycast candidate sets, coverage gating) goes through it; don't special-case visibility anywhere else.
- At Matrix the hovers link to the pins (`ScanPinScene.matrixLinkNode`): a hovered cell gold-rings its pair's pins, a hovered mesh (tree node / tile) every pin on a pair touching it. `MatrixHoverPair` also drives the overlap preview.
- **ONE `Esc` chain** (the view's key handler): pin exit-guard popup (cancel = stay) > loop modal > already-connected warn (`PairConnectWarn`) > study start-confirm > a centreless placement aborts straight to Pair (skips the disarm step) > armed-pick disarm > `FocusAscend`. Rail jumps share the exit-guard gate and the `jumpFocus` cleanup. New cancellable states slot into this chain; they don't get their own key.
- **Already-connected warning** (`Model.PairConnectWarn`, `GuiOverlays.pairConnectModal`): clicking a cell whose meshes the tree already connects indirectly parks the pair behind a blocking confirm; confirm = enter Pair (a later solve there raises the loop modal).
- **Camera rule**: no GUI interaction moves the main 3D camera without an explicit user prompt (double-click / fly-to only). The ortho tiles are exempt and auto-refocus on pin-flow events (`Update.frameTiles`): a new placement frames the pair's overlap area (+ `FeatherRadius`), placement/edit/pin-select frame the pin (r × 3).

### Pins

- `ScanPin` is **atomic**: `{ Id; ShortName; Pair (PairCell.key order); AnchorMesh; CentreLocal; InnerRadius; PointA; PointB; CreatedAt; ContactRings; RevealA; RevealB }` — points are non-optional, no partial pin exists. Birth only through the placement transaction `PlacementState.PlacementActive of PinDraft`, a guided chain: each landing auto-arms the next missing part (centre → point A → point B), the Edit arms can re-aim any part out of order, and **completion is implicit** (`ScanPinUpdate.landDraft`): the moment the last part lands the draft mints the pin, which auto-selects. `BeginPinTransaction` jumps to Pin with the centre pick pre-armed and clears the pin selection.
- A centre landing validates via `/api/query/roi-fit`: the smallest radius ≥ the current one whose sphere captures ≥ 20 vertices of the OTHER pair mesh, capped ×4 — past the cap the pick is refused, a larger fit grows the pin radius (toast either way).
- **A draft is a pin with parts missing — there is no pre-commit look.** `PinDraft` owns `Radius` (seeded from `QuickPinRadius`), `Rings`, `RevealA/B`; every placed part renders through the committed-pin builders in the main view and the tiles. `landDraft` carries Ready rings/reveals into the newborn; in-flight fetches downgrade to None so the pin postlude re-owns them.
- **Exit-guard**: leaving Pin with a **centred** draft (`placingWithCentre`) — Esc or a rail jump, uniformly — parks the destination in `Model.PinExitPending` and raises the blocking confirm (`GuiOverlays.pinExitModal`); confirm = the parked jump (which rolls the draft back), cancel = stay. A centreless draft exits silently.
- **Picking is arm-driven — no pick without an arm** (`Model.ArmedPick : ArmTarget option`, `ArmCentre | ArmPoint of mesh`); only camera moves are exempt. While armed, a click in any view lands the pick and the left button never orbits. **The arm target is the attribution**: `ArmPoint` raycasts its own mesh alone regardless of the view (this keeps co-located pairs pickable), `ArmCentre` raycasts both pair meshes and the nearest hit lands (`GuiPanes.armedResolve`/`armedPick`, shared by every view). Disarm = a landed pick, Esc, or re-clicking the arm control. Arming is reducer-guarded to the Pin level, closes the top-bar popovers, and an A/B arm isolates its mesh in the main view.
- **Armed = a scrimmed quasi-mode**, pure CSS off `body:has(.arm-flag.on)`: every non-pick surface goes `pointer-events:none` under a dark veil; only the main 3D, the pair tiles and the arming button (`.arm-lit`, above the veil — it doubles as the cancel) stay live. Veils sit on the NON-scrolling containers `.top-bar`/`.left-col` (a veil on the scrolling rail would scroll away). An armed *point* pick also gates the tile pair: the armed mesh's tile lights (`.tile-pick-on`), the other is veiled and inert (`.tile-pick-off`); the centre pick gates nothing.
- **Pin panel** (`.pin-panel`): one Edit column of arm buttons (✚ point A / ✚ point B / ◯ Centre / ⌀ Radius; hover previews via `PinFocusHover`) plus ✕ Cancel / ✓ Finish pin. The same arms serve placement and committed edits: a point re-pick replaces atomically (`EditPointAt`), a centre re-pick **re-anchors** the pin onto the hit mesh (`EditCentreAt`), the radius slider (`Model.PinRadiusEditOpen`) also serves the draft (`SetDraftRadius`). There are no isolate/focus buttons at Pin (view steering = tile clicks) and no Pin-level delete (deletion lives in the pair workspace's pin rows).
- **While any pick is armed every existing pin mark fades to near-invisible**; only the armed cursor preview stays full, plus one exemption: during a correspondence pick, the OTHER side's already-placed point of the pin being edited (`ScanPinScene.armedSiblingAt`) stays full with a gold halo in the main view and its tile — it is the reference mark the user aims against.
- **Correspondence marker = crosshair + reveal** (committed and draft alike). The crosshair (`ScanPinScene.crosshairNode`) is camera-aligned, screen-constant (outer radius 0.025 × eye distance), open-centred, `DepthTest.None`; it **never hides** (it is the locator) but mutes to 0.15 when its mesh isn't solid. The reveal (`pointReveals`/`draftReveal`) is the point's OWN mesh's local geometry — 3 concentric sphere∩surface rings (×0.2/×0.6/×1.0 of `Model.RevealRadius`) + 2 world-vertical plane∩surface cuts, white fading with metric distance, normal depth testing. Reveals are cached per point side (`RevealState`), fetched by the `ensureRings` postlude via `/api/query/point-reveal`, stored MESH-LOCAL; invalidated by point re-picks, the radius slider (`invalidateReveals`) and pose changes (`invalidateRings` — the cuts' verticality bakes the pose into the request normals).
- **Mark visibility under isolation** (`ScanPinScene.markerAlphaAt`): mesh solid under the EFFECTIVE narrowing = full; solid only under the COMMITTED state = 0.15 (a preview dims, never pops away); solid under neither = reveal hidden. Pin AREA marks never hide with isolation, but pins anchored to the isolated mesh add the dashed anchorage ring (`isoCueMeshAt`), and a side arm-button hover fades the marks of pins NOT anchored to the hovered mesh (`anchorHoverDimAt`). Flags/labels always stay.
- **Armed cursor preview** (`Model.ArmPreview`, metric world): the about-to-be-placed mark in all-white, rendered in the main view and the tiles from the same state. Main-view hover feeds it from the GPU pick (throttled, no server traffic), tiles from throttled server raycasts; the reducer drops landings once disarmed.
- **The pin rides its anchor mesh**: world centre = `displayedWorld(AnchorMesh)` ∘ `CentreLocal` (`ScanPin.centreWorldWith`); `CentreLocal`/`PointA`/`PointB` are stored in their mesh's **own frame**, so poses never re-bake stored geometry. Points may lie outside the area sphere (the radius scopes analysis, not editing).
- **Only edits that change the solve's inputs — a point re-pick or a pin delete — drop a registered pair's edge cascadingly** (+ toast + `invalidateCellError` + `bumpPairSolve`, so in-flight solves land dead). The dropped subtree re-solves: `Model.PendingResolves` queues its (child, parent) pairs parent-first (`subtreeDependents`) and `continuePendingResolves` drains ONE re-solve per commit, each toasted; the queue clears on reroot, checkpoint load and dataset switch. Centre/radius edits keep the edge and only invalidate the inspection caches.

### Inspection caches — two scopes, one shape

The same toolbox and instruments at every level, resolved against the selected pair inside the workspace and against the **whole tree** at Matrix. At graph scope every quantity is **parent-relative**; only established edges contribute, and a zero-edge graph is legitimately empty.

- **Pair scope**: `Model.CellError`/`CellErrorBefore` (per-pin `/query/pair-error` batches at the displayed / edge-before poses) and `CellDist`/`CellDistBefore` (`/query/region-distance` MOV vs REF; both land in ONE message).
- **Graph scope**: `Model.GraphError`/`GraphErrorBefore` (one `pair-error` batch per edge, requested **parent-first** so the endpoint's B-rel-A convention already yields child-rel-parent — no sign flip) and `GraphDist`/`GraphDistBefore` (one `region-distance` per edge, child vs parent). Edges fan out with `Async.Parallel`; edges sort by child and pins by `(CreatedAt, ShortName)`, so the gid stream is deterministic. *After* = composed poses; *Before* = both endpoints at their as-loaded baselines with the pin ROIs re-placed there. Both states land in ONE message, so a peek never catches a half-landed flip.
- **One generation** (`cellErrorGen`) for all caches; `invalidateCellError` bumps it and clears every one on pair-selection change / pin edit / solve / edge drop / reroot / dataset switch. Level jumps alone do NOT invalidate — the caches ride the selection. Lazy single-flight postludes in Update.fs: `ensureCellError`/`ensureCellDist`/`ensureGraphError`/`ensureGraphDist`/`ensurePairProx`.
- **`Model.PairProx`** (same generation): per subject-pair mesh, each vertex's distance to the OTHER pair mesh at the displayed poses (`/query/pair-proximity`, both directions in one landing) — the feathered overlap gate's data, one pair resident at a time. Subject = the selected pair in the workspace, the hovered cell's pair at Matrix. `ensurePairProx` is debounced (180 ms) single-flight off `proxWant`; a landing is accepted only while still wanted. `FeatherRadius` is a live uniform compare (never refetches); landings are memoized in a pose-keyed FIFO (`proxCache`, cap 12, cleared on dataset switch).
- **`MeshView.inspectBlocksAt` is THE canonical sample stream** of the current scope, in gid order: `InspectBlock = { Mov; Ref; Pin; Err }`, one block per pin. Every gid-addressed consumer (3D dots, the hover search, the exact-value fetch, tooltip, legend tick) walks this one stream.
- Samples are stored **MOV-relative-to-REF** (sign flipped at landing when MOV was meshA in the request). Brush gids index that stream, so any refetch invalidates the brush; the Pin level's narrowed chart keeps the same canonical gids. `Update.clearBrushAcross` wipes brush + hover when a jump crosses Matrix ⇄ workspace — in `jumpFocus` AND `normalizeFocus`. The brushed set is capped at 12000 gids (the widest scope is the whole graph; a tighter cap silently truncates).
- **The before/after states are not sample-aligned** (the server re-estimates normals and re-samples per pose), so a brush held across the Matrix peek highlights the same *region* of the distribution, not the same samples. No mapping is fabricated and the brush is not cleared.
- **`MeshView.inspectRangeAt` is THE shared scale** for the map uniforms, chart x-range and legend: `ErrorRange.ofSamples` over the scope's pin samples when any exist (always spans 0, cap ±0.5 m), else `ErrorRange.ofDistances` (per-sign 95th percentile, 1 mm floors). Never per-mesh normalization, no user range sliders. At Matrix it is deliberately peek-blind — read from the *before* state and held across the flip; renormalizing per state would hide the improvement.
- **The error map paints only the MOVING side** (`InspectPlain` swaps MOV's base to near-white); the reference never carries error colour. At Matrix every registered child paints against its parent on the one shared ramp; the reference root and unregistered meshes drop out of the shown rule while the map paints (`MeshView.graphMapScopeAt` → `matrixScope`) and keep only outlines — a white surface there would read as "registered and fine". At Pin the map is pin-local: `cellPaint` masks vertices outside the selected pin's ROI with the 3e30 keep-base sentinel.
- **The map is placement-hostile**: `CellMapOn` resets OFF on every dataset switch, and arming any pick (`ToggleArmPick` / `BeginPinTransaction` → `clearInspectForPick`) turns the map off and clears the brush; neither auto-restores.
- **Brush and map are mutually clearing in the reducer**: a non-empty `SetBrushedSamples` switches `CellMapOn` off; turning the map on (`ToggleCellMap` / `AssessGlobalQuality`) clears the brush. An active brush (`MeshView.brushActiveAt`) is a whole render mode — **colour isolation** (`MeshShader.ColorIsolate`): every mesh's base and ghost go near-white and the intrinsic heatmaps stand down, so the dots are the only colour. `MeshView.brushFrameAt` is the spatial frame, pair-only: MOV is the one solid surface and enters as the default isolate (`MeshVisibility.withBrushIsolate`; an explicit `TileIsolate`, hover previews and peeks still win), REF keeps only its footprint contour in `--ref-gold` (`MeshView.coverageColorsA`), `footprintMask` gates that composite to the pair and `outlineMask` gates the G-buffer composite to MOV. At graph scope there is no frame — nothing is isolated and the masks stay all-on.
- **Dots** are `Discs`: screen-constant (clamped in metric world to [5 mm, 0.5 m]), coloured on the `inspectRangeAt` ramp, each riding its own block's `markerAlphaAt b.Mov`. Dot buffers depend on the brush alone; the hovered dot's ring is a separate node. The hovered dot's exact value comes from `/query/pair-error-at` (`Model.HoverReadout`).
- **UI**: the docked inspection toolbox (`GuiRail.inspectPanel`, `.inspect-dock`), collapsible per level (`Model.InspectOpen : LevelFlags`; Matrix defaults collapsed) — `graphBody` at Matrix, `inspectBody` at Pair/Pin. It hosts the hover readout, the one diagram, the error-map toggle (`CellMapOn`, default off) and Clear brush, nothing else. Its width rides the left column's one resize handle (`.left-handle`, pure DOM, View.fs), which writes `--leftw`/`--lefthomew` on the column and the aspect-true `--charth` on the dock.

### Peek keys

- **V** (isolation → the pair's OTHER mesh) and **B** (pose: as-loaded ⇄ registered) are spring-loaded: the keys and the top-bar hold-buttons (`.tb-peeks`, pointer capture so the release lands off-button). **V is workspace-only** (`peekPairLoaded`) and its button is hidden at Matrix; it needs both pair meshes GPU-resident and an effective pair-mesh isolation. **B reaches Matrix**, where it flips the whole graph (`Update.peekPoseOk`: ≥1 edge, every edge mesh resident); in the workspace it needs the pair loaded and registered (`RegGraph.pairEdge`). Both refuse while the loop modal is open; releases always land; peeks clear on any focus jump and dataset switch. The buttons mirror these guards (`canVis`/`canPose`).
- **Peeks are purely visual and derived.** The vis peek swaps the *effective isolate* in the two effective-isolate sites only (`MeshView.buildScene` shownCtx + `View.shownNow`, so shown = clickable holds), and the Pin level's point narrowing swaps with it; the `TileIsolate` lock never moves. Pose peek: `MeshView.displayedMeshT`/`displayedWorldAt` are peek-aware (rendering, view-side picks, surface-riding markers); `ModelTransforms.*` (reducer/query side) is peek-**blind** — no query may read a peeked pose.
- **At Matrix the pose peek is a state toggle**: `MeshView.graphSideAt` = `PeekPose ? EdgeBefore : EdgeAfter` selects the resident cache for the map, pooled histogram and dots, while range and legend stay pinned to the before state so "after" visibly collapses toward neutral. Read it only inside Matrix branches. In the workspace the peek is zero-refetch too (`CellDistBefore` is resident).

### Tile strip (GuiPanes.fs)

- ONE right-edge strip (`GuiPanes.tileStrip`/`meshTile`, `.mesh-tiles`), mounted once per dataset and present at every level: Matrix = all meshes, Pair/Pin = the pair's two. Off-scope tiles hide via `.tile-off` = `position:absolute` + **`visibility:hidden`, never `display:none`** (a collapsed render control loses its viewport); their scenes are `Sg.Active`-gated.
- **Tiles do visibility; arming does picking.** A drag-free tile click toggles that mesh's isolation (`ToggleTileIsolate`; hover = `SetTileIsolateHover`; lock = `.tile-iso`); while armed the click lands the pick and the arm target (not the tile) attributes the mesh. `Sg.OnTap` is banned in these controls — Dom events + server raycasts only.
- Tiles carry a shared **top-down orthographic camera only** (`Model.TileCams`/`SetTileCam`; the eye rides `Radius + scene-Z-extent` above the centre plane so nothing near-clips). Default framing = the reference root's bounds, so unpanned tiles compare directly. Hovering a pair pin row preview-frames every tile on that pin (`Model.TilePinHover`; `tileCamOf` overrides with the framing a click makes persistent). The highlight subject (`ScanPinScene.highlightPin`) shows as a gold ring; outside the frame a chevron at the border points at it (`GuiPanes.addEdgeArrow`).
- Every tile overlays the reference root's gold footprint outline: a per-tile root-only coverage pass (`MeshView.buildRootCoverageNode` → `OutlineView.rootCoverageOffscreen` + `buildRootOutline`) with `Primitives.refGoldV3d`, the ONE F# mirror of `--ref-gold`.
- Tiles render through `MeshView.buildPaneScene` (`MeshShader.shade`, inspection modes off, survey heatmap live) with the same feathered overlap gate demand as the main view (`MeshView.gateDemandAt`). They bind `Coverage0/1` to a dummy texture — `OverlapPreview` never lights there. Pin marks are pair-scoped (`marksOn`); the selected pin's area circle draws in both pair tiles and the anchor tile adds the dashed anchorage ring.
- **Shared tile orientation** (`Model.TileRotation`, CCW from north-up): every screen↔world conversion in the strip goes through `GuiPanes.tileBasis` — never assume screen +X/+Y = world +X/+Y in tile code.
- Tile chips show the full server folder name (`Primitives.meshFolder`); mesh names are never shortened anywhere.
- An explicit isolation darkens the non-isolated meshes in the main view (`IsoDim` uniform = `Model.IsoDimStrength`, computed in `buildScene`); it stands down while brush colour isolation, Isolate pins or the error map own the scene.

### Misc

- **Top bar**: ▤ Cut · ▤ Far · **◍ Isolate pins** (`ToggleAnchorGhostMode`; `AnchorGhostMode : LevelFlags`, per level, default ON at Pin only; suspended while the centre pick is armed — derived in `buildScene`, so the stored flag restores on disarm) · the peek hold-buttons. Right side: study banner, cursor world readout, the **▦ mesh menu** (`MeshMenuOpen`: ☆ Set-reference — the ONLY root-change path — plus the per-mesh Tex/Dst/Shp/Inc toggles and the Shape ≥ slider), the **⚙ gear** (dataset switcher, checkpoints — `CheckpointStore.fs`, localStorage — rendering mode, tuning sliders, dataset info, camera readout) and the **≣ session log** (`ReachLog`/`ReachLogOpen`, JSON export). Matrix rows/cols are acquisition order; there is no reorder control.
- **Mouse**: left orbits (picks while armed), right always orbits, bare MIDDLE pans in the view plane (remapped to `Button.Button4` at the event edge), Shift + any button = the world-XY constant-Z pan (the `Button.Middle` path). **Space** (held) hides every annotation overlay — purely visual.
- **Per-dataset defaults** (`DatasetDefaults`, Model.fs): name-keyed config seeded on every `SetActiveDataset` (currently `pinRadius`: "ScanPin - UserStory" = 1.9 m, else 0.5).
- **Auto-save**: a hidden 1-min interval bridge (`.ck-auto-bridge`) silently saves to the reserved `autosave` checkpoint while `Model.AutoCkOn`; "Restore last checkpoint" loads it through the normal apply path.
- **User-study mode** (`Model.Study : StudyPhase`; `/study` is a virtual route — `ApiConfig.studyUrl` detects it, `apiBase` strips it, the server's index fallback serves it): warm-up auto-loads `StudyMode.warmupDataset` with the `.tb-study` banner; Start raises the blocking confirm (`Model.StudyStartPending`, `GuiOverlays.studyStartModal`), whose confirm emits `[SetActiveDataset "study"; SetStudyPhase StudyLive]`. Any other dataset switch exits (the `SetActiveDataset` reducer zeroes `Study` whenever a previous dataset existed). `.tb-right.study-on` only faints the ▦/⚙/≣ buttons — they stay clickable, being the exit path.
- **Screenshot mode** (`Model.ScreenshotMode`, the top-bar ▣ button) is layout-only, driven by CSS off `body:has(.shot-flag.on)` — one block at the end of `style.css`. It hides the tree navigator (`.home-nav-tree`), the cut sliders, the orientation indicator and the tile strip (visibility, plus `meshTile`'s `shownA`, so the tile scenes stop); the chart payload carries the fixed `shotTitle` and empty legend names. `.left-col` becomes a row: the rail, then the histogram panel, shown/hidden wholesale by `Model.ShotInspectOpen` (which replaces the per-level `InspectOpen` in `inspectPanel`). The Pair/Pin chrome is built per mount by `GuiRail.pairChrome`/`pinChrome`: the rail's instance sits in `.shot-moved` wrappers (`display: contents`, hidden in the mode) and `GuiRail.shotLevelBar` seats a second instance in the top bar — the view passes it into `GuiTopBar.topBar`, since GuiRail compiles later. The top bar wraps in the mode and publishes its height as `--tbh`. Widths are pure DOM (`.shot-handle` → `--shotrailw`/`--shotdockw`/`--shotcharth`); the scale bar's target length is `Model.ScaleBarTargetPx`, set through the hidden `.sb-bridge` input, and its position is DOM-only too (`.sb-move` → `--sbx`/`--sby`).
- The server is stateless w.r.t. the registration tree — every query carries explicit transforms.

## Render pipeline

One forward pass into the main framebuffer. `passZero`: meshes (custom α + α-gated depth), then pin geometry (`DepthTest.LessOrEqual`, blended). `passOne`: coordinate cross, labels, overlay lines, the dark duplex copies (`DepthTest.None`). `passTwo`: the duplex white cores, so they deterministically layer over their dark copies (within-pass order is arbitrary). The main frustum is near 1 cm / far 1000 m METRIC (× `DatasetScale`), set in View.fs twice — the render control and the overlay-tooltip projection must match. Offscreen: the image-space outline pass, the footprint coverage MRT, and one root-only coverage MRT per tile. The tiles are their own render controls.

Contracts of `MeshShader.shade`:

- **α-gated depth**: fragments with α ≥ 0.99 write their natural window depth; ghost fragments write 1.0 (far), so ghosts never occlude and pixel picks fall through them.
- **Ghost colour is uniform**: the solid per-mesh palette colour regardless of rendering mode — except under `ColorIsolate`, which whitens the ghost too.
- **Solid / ghost / invisible** is decided per fragment from `MeshActive` × the ghost floor (`GhostSilhouette`/`GhostOpacity`; floor off ⇒ ghost fragments are *discarded*) × the pin-isolation blob mask (`Blobs` uniform array, hard cap 32, metric → render at upload; the draft's area is a blob too) × the overlap gates. The scalar-field painters only touch **above-ghost** fragments; `IsoDim` veil fragments are excluded from `aboveGhost`.
- **Two gates share the solidity slot; `PlacementGate` wins.** `PlacementGate` is THE feathered overlap area, with one demand rule (`MeshView.gateDemandAt`): in the workspace the selected pair during any pin-location interaction (a placement in flight, any armed pick, the ＋ New pin hover, an arm-button hover), at Matrix the hovered cell's pair. Solid only where the OTHER pair mesh has surface within `FeatherRadius` metres, read from the per-vertex `PairProx` attribute (world-space, view-independent); OFF until both proximity buffers land — never a half-test (`MeshView.placementGateUniforms`). `OverlapPreview` (`MeshView.overlapPreviewUniforms`) is only the matrix hover's in-flight fallback while those buffers load: solid where the coverage MRT covers the pixel in both hovered-pair channels.
- **Near/far cuts**: `NearCutFrac` (0 = off) and `FarCutFrac` (off at the right end, ≥ 2.495) discard beyond a camera-forward plane and paint a flat-ink intersection band; the outline G-buffer applies the same cuts.

### Error range and colour

- Signed, spans 0, capped ±0.5 m, one scale per scope (`inspectRangeAt`). Uniforms: `DistScale` = hi, `DistLoNeg` = |lo|. Sentinels: 1e30 (server: no Z-overlap) paints pale grey; 3e30 (outside the pin-local ROI) keeps the base colour.
- The diverging map is asymmetric piecewise Coolwarm (Colorcet CET-D01): zero = near-white #EDE7EA (grey is reserved for no-data, never 0), each sign normalized by its own end with a t^0.6 near-zero boost. `Primitives.Diff.colorSignedV3` is mirrored in `MeshShaders` — **keep the two in sync**.
- Difference maps carry value isolines every `Diff.isoStep` metres (a nice 1/2/5 step ≈ span/8, so 0 is always a contour), suppressed where the colour clamps. `ddx`/`ddy` exist in FShade, `fwidth` does NOT.
- Intrinsic heatmaps: **Range** normalizes by the one all-mesh end `MeshView.rangeMaxWorld`; **Incidence** uses the screen-derivative normal sign-oriented by the vertex normal, clamped at 0 — never `abs` (away-facing = never scanned = worst); **Shape** discards below `ShapeThreshold`.
- The legend (`GuiOverlays.colorLegend`) has exactly two states: the difference map (while the scope's map paints or brushed dots exist — same ramp and range) else the Range heatmap; hidden otherwise. Pointing at a mesh crops it to that mesh's 5th–95th-percentile extent.

### The cell chart

ONE canvas diagram (`GuiRail.chartJs`) with a body per scope; an empty scope renders the same furniture with a placeholder. The model echo (`data-brushed`) is authoritative — a change while not dragging nulls the JS-local band, so a cleared brush can never stay painted. Re-renders on resize/show via ResizeObserver.

- **Pair/Pin (`inspectBody`)**: pin-stacked 48-bin histogram of the MOV mesh's error (achromatic pin ramp, pooled-LoD band, mm axis); Pin narrows the series (gids and x-range stay the full cell's). Registered pairs overlay the edge-before histogram as a step outline.
- **Matrix (`graphBody`)**: one monochrome series of pooled SAMPLES (not bin-wise added counts) over all edges — no per-edge colour, no before-outline; the pose peek swaps the whole distribution on the fixed before-state axis and binning.
- The count axis never rescales on the pose peek: the payload's `ymax` carries both states' maxima.

### `Sg.DepthMask` is forbidden

Buggy in this Aardvark/Aardworx WebGL build — it silently breaks the depth pipeline. Steer ordering with `Sg.DepthTest` + `Sg.Pass` alone; lines, pin geometry and text therefore all write depth. Leave the in-code reminders in `LineShader.fs` / `SceneGraph.fs`.

### Image-space outline pass (`OutlineView.fs`)

MRT G-buffer (world-Z band parity + window depth → target0, palette colour + coverage → target1) → fullscreen edge detect painting silhouettes + elevation isolines. Keep these choices:

- The depth edge is the **second difference** `|l + r − 2c|`: window depth is linear in screen space across a planar primitive, so it is ~0 on any smooth slope and spikes only at a real break. A first difference lights up every grazing surface.
- `OutlineWidthPx` samples the depth break at ±width texels while the parity edge stays ±1 — silhouettes thicken, isolines don't.
- target0 is `Rgba8`; depth is packed hi (.w) / lo (.z) as 16-bit fixed point and the edge detect reads the HI byte alone, so an `OutlineThreshold` below that quantization floor (1 LSB ≈ 0.004) produces false bands.
- The isoline signal is band **parity** (`floor(wp.Z / ContourSpacing) mod 2`), a pure function of world Z — contours stay welded to world-Z planes and don't crawl with the camera. `ContourSpacing` is camera-adaptive (~24 contours across the view) but SNAPPED to a 1/2/5 metre step; `IsolineBands` sets the densest allowed spacing.
- Per-mesh **footprint contours** need the second, occlusion-free pass: an additive coverage MRT (one channel per mesh, 2×Rgba8, no depth — cap 8 meshes) + the `OutlineCoverageEdge` composite. The depth-tested G-buffer can only outline the visible union — never remove the coverage pass in favour of it. The main-view MRT renders once per frame (`OutlineView.coverageOffscreen`), shared by the footprint composite and the `OverlapPreview` fallback; a hovered-pair mesh beyond the 8-channel cap disables the preview rather than half-testing.
- The G-buffer writes a mesh id (`MeshId` = (index+1)/255) and the composites gate lines through slot arrays indexed by display index (`OutlineMask`, `CoverageColors`).

### Picking

- `Sg.OnTap` / `OnDoubleTap` / `OnLongPress` **fire on background misses too** — every handler that builds state from the hit must gate: `if e.Location.Depth < 0.9999 then Some e.WorldPosition else None`.
- Ghost fragments leave depth at 1.0, so the GPU pick cannot land on them. Anything needing a point on a possibly-ghosted surface keeps the GPU pick as the fast path and falls back to a server raycast (`View.resolvePick` / `raycastNearest*`; armed picks via `GuiPanes.armedResolve`) — un-apply the mesh's displayed pose before the query, re-apply it to the hit. Hover-driven raycasts are throttled (~60–80 ms) and generation-guarded.
- **Every node without `Sg.NoEvents` writes the GPU pick buffer.** Overlay/composite geometry — especially fullscreen quads — must set `Sg.NoEvents` or it hijacks every pick.

## Coordinate systems

Three spaces, two transforms. Every boundary crossing goes through a named helper, never bare `* scale` / `± centroid` arithmetic.

- **Mesh / server frame**: stored OBJ coordinates `+ meshCentroid`. **Every `/api/query/*` coordinate, in and out, is in this frame**; the server subtracts the centroid itself.
- **Metric world**: the app's canonical world (metres) — pin geometry as world, cursor world, graph edge transforms. Equal to a mesh's server frame exactly at the load pose.
- **Render space**: centroid-recentred, dataset-scaled, then posed — what the GPU and cameras use.

1. **Dataset transform** — a similarity (uniform scale + translation, no rotation), fixed per dataset; the only place `DatasetScale` and `CommonCentroid` enter. Cross it with `ScanPin.renderCentre`/`worldCentre` (points) and `ScanPin.renderLength` (lengths).
2. **Workspace transform** — a rigid per-mesh pose composed from the registration graph. Render form: `ModelTransforms.displayedRender` / `MeshView.displayedMeshT`; metric-world form: `ModelTransforms.displayedWorld` / `MeshView.displayedWorldAt` (the `MeshView` ones are peek-aware). `RigidTransform.worldToRender`/`renderToWorld` conjugate between the two. **`displayedWorld.Backward` maps metric world → the mesh's server frame; `.Forward` maps back.**

Rules: server queries convert in with `displayedWorld.Backward` and out with `.Forward`; pair queries (`pair-error`, `pair-overlap`, `region-distance`, `lsq-pairs`) instead pass each mesh's world transform explicitly. Scene-graph geometry is render space — convert at the boundary. Directions need only the rotation (`TransformDir`). Pin geometry is stored in its mesh's own frame (`displayedWorld.Backward world` at pick time).

**Sensors**: each mesh's OBJ origin is its scan-sensor station and `*centroid.txt` records that origin's world coordinate. A re-centred re-export loses this, so an optional `*sensor.txt` records the measured station (`/api/datasets/{dataset}/sensors` → `Model.DatasetSensors`; absence is meaningful). Sensor position = the measured station else the centroid (`ModelTransforms.sensorWorld`/`sensorRender`). The dataset-load camera rests on the first mesh's sensor (`firstSensorRender`); the incidence/range heatmap origin and the coordinate cross bypass the measured override.

## Adaptive performance (critical)

In the scene graph, **never depend on an entire record when you only need some fields**. The model replaces whole records on every update, so an `AVal.map` over a full `ScanPin` (or `Model`) fires on *any* field change. Project individual fields into separate `aval`s early and build the dependency graph from those:

```fsharp
// BAD — rebuilds on ANY pin change
let geo = pinVal |> AVal.map (fun po -> ... po.ContactRings ... po.InnerRadius ...)
// GOOD — only when the rings or radius actually change
let ringsVal  = pinVal |> AVal.map (Option.map (fun p -> p.ContactRings))
let radiusVal = pinVal |> AVal.map (Option.map (fun p -> p.InnerRadius))
let geo = (ringsVal, radiusVal) ||> AVal.map2 (fun rings r -> ...)
```

Rebuilding an `AList` of sg nodes destroys and recreates GPU resources, therefore:

- **Split structure from placement.** Static sg node lists from slowly-changing data; adaptive `Sg.Trafo` for fast-changing placement.
- **Push adaptivity down.** A parent `AList.ofAVal` rebuilding all children is expensive; an `AVal`-driven `Sg.Trafo` per stable child is cheap.
- **No in-place-updating lists via `AVal.map (… IndexList.ofList) |> AList.ofAVal`** — fresh `Index` keys every recompute diff as remove-all + add-all. Use `AMap.map (project to the row's inputs) |> AMap.toASet |> ASet.sortBy key |> AList.map row`. Small, rarely-changing lists may use the simple form.
- **Never create a transient `aval` inside another aval's compute and read it** (`AVal.custom (fun t -> … (makeSomeAval args).GetValue t …)`, or `AVal.force` of a fresh aval): the dependency edge can drop and the outer aval silently freezes after one evaluation. Inline the inner computation or bind the inner aval once outside. Reading a *stable* aval via `.GetValue t` is fine.
- **Screen-constant marks size in the vertex shader**: flags, centre jacks and crosshairs go through `GlyphLines` (glyph centre + unit offsets; `h = clamp(GlyphMinR, GlyphMaxR, GlyphFrac·eyeDist) × GlyphScale`), brushed dots through `Discs` — a camera move costs uniforms, never a re-tessellation. Only two single glyphs (the hovered dot's ring, the loud-highlight ring) re-tessellate per camera move. Extend `GlyphLines`/`Discs` rather than adding a CPU per-frame rebuild.

Patterns to maintain:

- **Postlude guards before allocation.** Reducer postludes run on EVERY message (incl. per-frame `Rendered` and per-mousemove `PointerMove`): cheap single-flight/focus/cache guards first, list building only after (`ensureCellDist` is the template). Never let a per-message path scale with session content before its guard.
- **Reference-keyed memos in `*At` helpers** (`graphMapScopeAt`, `inspectRangeAt`, `cellPaint`): model records and fetched buffers are reference-stable between real changes. Function-local memos need a `ref` cell; never key on structs.
- **Fades are uniforms, never vertex data**: per-pin dims ride the `MarkDim` uniform (`Lines.renderWith`), presence is `Sg.Active`, many-marks nodes carry a slot attribute + the `DiscAlpha` slot array. A hover transient must not re-upload buffers.
- **`AVal.bind`-gate hidden UI.** `showWhen` only toggles CSS — the subtree stays live. Payload-heavy content behind a closed state gates its data avals on the open flag (`chartData`, the legend, the session log).
- **Value-keyed swaps**: a swap-in/out DOM body keys through `AMap.ofAVal (HashMap.single key ())` so the mounted node survives unrelated churn; row lists stay incremental and project to exactly the row's inputs (`pinList`).
- **JS bridges re-parse only changed attributes** (`chartJs`; `observedRender`'s `raw === last`); large payloads carry no derivable redundancy.
- **`JsonDocument` is pooled — always dispose** (`Query.post`'s parser callback or a local `use doc`); parsers fully materialise.
- **Module caches have owners**: `MeshView.meshes`/`displayedTCache` are dataset-scoped (evicted in `loadMeshAsync`); the figure-debounce CTS dicts are pin-scoped (`DeletePin`, `resetFigureDebounce`). Debounces use a tokenless `Task.Delay` + an `IsCancellationRequested` check — cancellation exceptions are expensive on Mono WASM.
- **Event handlers force only stable avals** (`displayedMeshT` is memoized per mesh for this; cmap/clist via `.Content`). Never build a transient adaptive object inside an event callback.

## Server query performance

`pair-error`, `contact-rings` and `region-distance` scale with mesh size × sample density:

- Never issue per-pair/per-pin requests sequentially — batch where the endpoint supports it (`pair-error` takes all pins of a pair), fan independent fetches out with `Async.Parallel`.
- Parallelise the heavy server inner loop when inputs are independent (`Parallel.For`; Embree `Scene.Intersect` is thread-safe).
- Cap density rather than grow linearly (`maxPointsPerMesh`, sample strides, per-pin sample caps).
- Debounce user-driven triggers with a `CancellationTokenSource` + generation counter — at most one fetch in flight per invalidation.
- `bboxesHandler` warms the mesh caches at dataset load.

## Client compile order (`Superprojekt.fsproj`)

```
MeshData.fs            mesh fetch/parse, ApiConfig, shared Http.client
Query.fs               server query wrappers (Async)
CameraModel.fs / .g.fs OrbitState [<ModelType>]
OrbitController.fs     orbit camera + messages (project file, NOT the Aardvark library one)
RegistrationModel.fs   ScanPinId, RegGraph/RegEdge, PairCell, MatrixNav, ErrorRange (WASM-free, shared with Supertests)
ScanPinModel.fs / .g.fs ScanPin + placement state
CheckpointStore.fs     checkpoint save/load (localStorage)
PinGeometry.fs         icosphere, sphere outline
Model.fs / .g.fs       Model + FocusLevel/FocusSelection + InspectBlock + MeshVisibility + ModelTransforms
LineShader.fs          flat colour + pixel-constant 3D lines, Discs, LineGlyphs
Primitives.fs          widgets, showWhen, observedRender, palettes, Diff colormap, meshFolder
Messages.fs            Message DU
ScanPinUpdate.fs       pin transaction sub-reducer + rings/reveal postlude
UpdateHelpers.fs       reducer helpers + debounce/generation state
Update.fs              main reducer + inspection-cache postludes
MeshShaders.fs         RenderPass + MeshShader + outline/coverage shaders
MeshView.fs            LoadedMesh, buildScene, displayed transforms (peek-aware), pin blobs, offscreen nodes
OutlineView.fs         offscreen image-space outline/coverage passes
ScanPinScene.fs        pin sg nodes: markers, rings, flags, brushed samples
SceneGraph.fs          scene composition + cross + labels + root outline
GuiPanes.fs            tile strip (isolation + armed pick surface)
GuiTopBar.fs           top bar (cut sliders, isolate-pins, peeks, mesh menu, gear, session log)
GuiOverlays.fs         toast, scale bar, legend, orientation indicator, placement banner, modals
GuiRail.fs             left navigator: focus rail + per-level views + inspection dock
View.fs                view function, pick routing, Esc/peek keys + App module
ShaderCache.fs / Program.fs
```

## Server compile order (`Superserver.fsproj`)

```
MeshLoader.fs        OBJ parse, centroid file, atlas paths
MeshCache.fs         Embree scene + BbTree cache (lazy, permanent)
MeshAnalysisCore.fs  pure level-set tracer + decimate (Embree-free, shared with Supertests)
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
GET  /api/datasets/{dataset}/sensors            → { meshName: [x,y,z] }   measured stations only
GET  /api/datasets/{dataset}/bboxes             → { meshName: { min, max } }   warms the mesh cache
GET  /api/datasets/{dataset}/mesh/{name}/{i}    → binary mesh
GET  /api/datasets/{dataset}/mesh/{name}/{i}/atlas → JPEG
POST /api/query/ray                             → { hit, t, point, triangleId }   Name = "dataset/mesh"
POST /api/query/closest                         → { found, point, distanceSquared, triangleId }
POST /api/query/contact-rings                   → sphere–surface intersection polylines
POST /api/query/point-reveal                    → concentric sphere rings + plane∩surface cuts around a point (the caller bakes the displayed pose into the plane normals)
POST /api/query/lsq-pairs                       → weighted rigid solve: world transform + residuals + conditioning (400 on <3 pairs)
POST /api/query/pair-error                      → per-pin symmetric error of mesh B rel A at explicit poses: median, LoD half-width, samples + positions; per-pin ok=false on no overlap
POST /api/query/pair-error-at                   → exact signed value at one picked point
POST /api/query/pair-overlap                    → registerability of a pair at supplied poses (two-way closest-point coverage fractions)
POST /api/query/roi-fit                         → smallest radius ≥ Radius capturing ≥ MinVerts vertices of the other mesh, capped ×MaxFactor (ok=false past the cap)
POST /api/query/region-distance                 → per-vertex signed distance of a target mesh to a reference, in served vertex order; 1e30 where the vertical line through the vertex misses the reference (no Z-overlap)
POST /api/query/pair-proximity                  → per-vertex unsigned distance to the other mesh's surface, served vertex order, deliberately NO Z-overlap gate
```

All query coordinates are absolute world space (`localPos = worldPos − meshCentroid`); pair queries carry explicit per-mesh transforms. `pair-error` measures, per mesh, only the axial sheet nearest the pin plane and pairs every sample against the other mesh's crossing at its own lateral position — never per-mesh footprint medians, which fabricate decimetres of error on rough or truly 3D scenes (see the `PairError.fs` header). Endpoints without consumers get deleted; don't add one without a consumer.

## Tests

- `dotnet run --project src/Supertests` — console runner (no test packages) compiling `RegistrationModel.fs` + `RegMath.fs` + `MeshAnalysisCore.fs` directly. The registration-graph invariants (tree, reroot, cascade, loop resolution, composition) live here — extend them when touching `RegGraph`.
- Integration (needs the Hessigheim dataset): `dotnet run --project src/Superserver`, then `node tools/integration.mjs [baseUrl]` — covers lsq-pairs, pair-error, pair-error-at, pair-overlap, region-distance, roi-fit.
- Scripted UI runs: the pin centre only lands where BOTH pair meshes have surface and point picks raycast their own mesh alone — use one known-overlap screen point for all three guided clicks.

## Build notes

- Client: `Microsoft.NET.Sdk.BlazorWebAssembly`, `net8.0`, `WasmBuildNative=true`, `LocalAdaptify=true`; a PostBuild target runs `fshadeaot`. Quick type-check: build with `-p:WasmBuildNative=false` (~35 s).
- Server: `Microsoft.NET.Sdk.Web`, `net8.0`; references the client project for static hosting. Dev port 8002 (launchSettings); the Docker image puts nginx on 80 in front of dotnet on internal 5000.
- `precompileShaders.cmd`/`.sh` regenerate `ShaderCache.fs` (puppeteer drives a server on :5432).
- **Distributable** (`tools/publish-bundle.sh` → `-p:Bundle=true`, defines `BUNDLE`): a single-file self-extracting exe with the `study` + `ScanPin - UserStory` datasets. The `BundleStaticWebAssets` target grafts the client's static web assets into the bundle — the SDK computes them only after the bundle is sealed, so don't remove it. Under `BUNDLE`, Program.fs pins the content root to `AppContext.BaseDirectory` (the extraction dir; a double-click cwd is arbitrary) and binds fixed port 5055 — checkpoints live in origin-scoped localStorage, so a stable port keeps them across relaunches.

## F# pitfalls

- **Deleting a DU case turns its remaining patterns into catch-alls.** `function DeletedCase -> true | _ -> false` still compiles — the name becomes a *variable pattern* matching everything (only warning FS0049 betrays it). After removing a case, grep for its name and check the build for FS0049/FS0025/FS0026.
- **`Trafo3d` is a struct** — `obj.ReferenceEquals` on boxed values is always false. Use value equality for memoization/identity tests.
- **Aardvark `a * b` applies a first** — the opposite of textbook matrix notation. Sanity-check any new composition with a translation-only case.
- `Error`/`Ok` are shadowed by Aardvark.Base — qualify `Result.Ok`/`Result.Error` in client code.

## Aardvark.Dom gotchas

- `Attribute("for", "...")` on `<label>` is silently dropped — nest `<input>` inside `<label>`.
- `Attribute("checked", "")` is dropped — use `Attribute("checked", "checked")`.
- CSS `~` sibling combinator breaks (Aardvark inserts wrapper nodes) — use `:has()` on a known ancestor.
- `RenderControlInfo` and `TraversalState` both have `.Runtime` — annotate `(info : Aardvark.Dom.RenderControlInfo)` when ambiguous.
- `yield!` is not supported in the CE builders — use OnBoot JS with MutationObserver for dynamic SVG/canvas (`observedRender`). CE ambiguity errors (FS0792) around `AList`/`aval` expressions in `div { }` are fixed by binding the expression to a `let` first.
- **OnBoot may run before a later-sibling node is mounted.** Look siblings up lazily inside the handler; boot-time capture is only safe for ancestors (`closest(...)`).
- **Sg pointer events (`Sg.OnTap` …) do NOT fire reliably in secondary render controls** — use Dom pointer handlers + a server raycast.
- `AVal.map4` does not exist — combine `AVal.map2`/`AVal.map3`.
- `Dom.Style` for renderControl, `Style` for HTML elements. `Css.Custom` does not exist — use CSS classes.
- **`RenderControl.ViewportSize` is framebuffer pixels; `ClientSize` is CSS pixels.** Anything mixing with DOM coordinates (overlay placement, cursor → NDC) must use CSS px or it breaks on hi-dpi. ClientSize is `V2i.II` until the first DOM event; the main control falls back to ViewportSize.
- `Sg.OnPointerDown(bool, handler)` — the bool is capture-vs-bubble **phase**, not pointer capture. For drags call `e.Context.SetPointerCapture(e.Target, e.PointerId)` in down and release in up.
- `Dom.OnPointerDown((...), pointerCapture = true)` — browser-level pointer capture; use it on canvas drags.
- **FShade shaders must be float32-only.** `float`, `Constant.Pi`, `V3d`/`V2d` and `member _ : float` uniforms emit GLSL `double`, which WebGL2 rejects at runtime. Use `3.1415927f`, `V3f`/`V2f`, `: float32` uniforms, bind `1.0f` not `1.0`.
- **FShade shader bodies must be lambda-free** — a local `let f x = …` inside a `fragment`/`vertex` body reads as an unsupported lambda; inline it.
- **`dotnet build` and `fshadeaot` catch neither shader pitfall** — only the in-browser compile does, so always verify shader changes in a browser.

## CSS

- `'Inter'`/`'Segoe UI'`, accent `#1a56db`, body bg `#f4f6f8`, panel bg `#ffffff`, text `#0f172a`.
- All styles in `wwwroot/style.css`; no inline styles except model-dependent ones (positions, data-driven colours, cursor).
- Conditional visibility uses `Primitives.showWhen` → `.hidden` (`display: none !important`), not inline display styles.
- Gold uses the `--ref-gold`/`--ref-gold-dark`/`--ref-gold-pale` tokens — never a literal hex.
- Toggle-active states: `.tb-btn-active` (top bar), `.rail-btn-active` (navigator), `.cbb-btn-active` (compact button bars).
