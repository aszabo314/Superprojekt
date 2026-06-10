# ScanPin Core 2 slice — implementation log

Working notes for `scanpin_core2_slice_spec.md`. Updated continuously; design decisions recorded as they are made so debugging can trace back to intent.

## Status

- [x] Phase 1 — probe data structure + core computation (server endpoint + client cache/invalidation)
- [x] Phase 2 — KDE + ridgeline chart in pin cards
- [x] Phase 3 — three-source decomposition + stacked bar
- [x] Phase 4 — hover probe
- [x] Phase 5 — wiring, invalidation end-to-end, demo workflow

## Design decisions

### D1 — probe computation runs server-side (one batched endpoint)
The spec's pseudo-code loops over all vertices of all visible meshes plus triangle-interior densification. Project rule: thin client, heavy compute on the server, batched endpoints over per-mesh HTTP loops. New endpoint `POST /api/query/probe` takes the pin sphere, the reference mesh, the visible-mesh list **with their current world-space registration transforms**, and returns everything the UI needs: normal + planarity, cylinder length, per-mesh stats, KDE curves, and the three-source decomposition. One round-trip per probe.

### D2 — raw axis samples never leave the server
`PointsAlongAxis` stays server-side. The client receives per-mesh `count / median / q1 / q3 / std / kde[] / bandwidth`. The KDE is evaluated server-side on a 256-point grid spanning the union of the spec's auto range (`median ± 3·IQR` over all meshes, floored to ±0.1 m) and the data fit range (padded by 3·bandwidth). The client's x-range presets (auto / ±0.5 / ±2 / ±10 / fit) are pure **windows** over that one curve — density outside the evaluated grid is ≈0 by construction, so no re-query on range switch.

### D3 — probe radius = InnerRadius
A pin has InnerRadius (hard truth) and FalloffRadius (decay). The spec says "pin radius"; the probe cylinder uses **InnerRadius** — it is the region the user declared as ground truth, and falloff weighting has no meaning for a distribution sample. Normal estimation uses the same sphere.

### D4 — mesh part 0 only
All existing spatial queries (isoline, ridge, patch, ICP) operate on part index 0 of multi-part meshes. The probe follows that convention.

### D5 — three-source palette = existing provenance palette
Spec suggests red/green/blue but defers to an existing categorical palette. The codebase already has the dataset/algorithm/conditioning triple as blue `#60a5fa` / orange `#f59e0b` / purple `#a78bfa` (legend CSS classes `pc-bar-dataset` etc.). Kept — consistent with the provenance heatmap legend users already see.

### D6 — probe replaces the heuristic provenance block in the Point card
The old `Provenance.sourcesAt` stacked bar in the pin card was the scaffolding placeholder the spec says to close. The Point-payload card now renders the probe-derived three sources. `Provenance` module itself stays — the mesh-shader heatmap and fusion picking still use it.

### D7 — probe state is a per-pin DU, computed lazily, debounced
`ScanPin.Probe : ProbeState = ProbeNone | ProbeRunning | ProbeReady of ProbeResult | ProbeError of string`.
Invalidation = reset to `ProbeNone`. A postlude (`ScanPinUpdate.ensureProbe`) runs after every reducer step: if the *effective* pin (selected or being adjusted — i.e. its card is open) has a Point payload and `ProbeNone`, it flips it to `ProbeRunning` and launches a 250 ms-debounced server query (CancellationTokenSource pattern, same as the line queries). Slider drags therefore coalesce into one request.

### D8 — invalidation triggers (spec §6)
Reset to `ProbeNone` on: `SetInnerRadius` (radius), `CommitRetarget` (centre move), `ChangePayloadType` (payload recreated anyway), `SetProbeLength`, `SetReferenceMesh`, `RegistrationComplete` / `ResetMeshTransforms` (transforms), `SetVisible` / `ToggleMeshSolo` / `ShowAllMeshes` / `HideAllMeshes` (visibility), workspace load. Dataset switch clears all pins already. Hover probe transient state is cleared by the same triggers.

### D9 — reference mesh resolution order
`Registration.ReferenceMesh` if set and visible → pin's `HostMeshName` if visible → first visible mesh. The probe result records which mesh was actually used.

### D10 — triangle-interior sampling, deterministic lattice
Per spec, low-res meshes need interior samples. Candidate triangles come from the per-mesh BbTree (`trianglesInBox` over the cylinder's local-frame AABB). A global spacing is chosen from the total candidate area so a mesh yields ≈ `maxPointsPerMesh` (8192) samples; each triangle gets a barycentric lattice with k(k+1)/2 points (k from its area, clamped 1..64). Deterministic — no RNG, stable across reruns.

### D11 — transforms: client sends world-space rigid transforms
`Model.MeshTransforms` stores **render-space** trafos; the probe request converts them back to world (`renderToWorldRigid` — moved from `Update.fs` to `Model.fs` as `module RigidTransform` so both reducers share it). The server applies the world transform to local samples (`v_world = M · (v_local + centroid)`) — implemented by inverse-transforming the cylinder into the mesh's original frame instead, so sampling stays in the cached untransformed geometry.

### D12 — conditioning c_scale = 1.0 (spec default)
`LocalConditioning = 1 / (n_meshes_present · mean_points_per_mesh + ε)` metres. With healthy probes (~10³ points) this is ~10⁻⁴ m, i.e. visually negligible until coverage degrades — which is the intended reading. Tune later against real data per spec §5.3.

### D13 — row order lock
Unlocked (default): rows sorted by |median offset|, closest to 0 on top (spec). Locked: dataset mesh-list order (stable, matches the left panel).

### D14 — demo dataset
Spec §Phase 5 names "Mars Kodiak", which does not exist in `data/`. Demo uses **Hessigheim** (default dataset; three epochs of the same scene = ideal N-mesh case).

### D16 — ridgeline rendering details
One `observedRender` block (`CardsPin.ridgelineJs`) drives both the pin-card chart and the Phase-4 mini tooltip (`d.mini` flag). Density (KDE y) is normalised to the **global max across rows within the current window**, so peak height compares spread between meshes honestly. Row height adapts `13–30 px` to keep total height ≤ 400 px (spec cap). Empty rows (count 0) render greyed with a `–` badge and sort to the bottom in auto order. Per-mesh detail (spec Phase 3: "clicking a row reveals ThreeSourcesPerMesh") is a row-click that toggles a one-line readout (`offset / IQR / n`) under the chart — those three values are exactly `MedianOffset / IqrMetres / PointCount`, derivable from the row stats, so the JSON carries no separate per-mesh source block.

### D17 — old heuristic provenance block replaced
`CardsPin.pinCardBody`'s Point section no longer shows the `Provenance.sourcesAt` placeholder bar; the probe-derived three-source bar + numeric readout took its slot (per D6). The left-panel / hover-overlay provenance UIs still use `Provenance` + `provBarJs` and are untouched.

### D15 — Probe DTOs are plain records (not [<ModelType>])
They ride inside `ScanPin`, which the Elm update replaces wholesale; per the adaptive-perf rule, the card view projects individual fields into separate `aval`s.

## Bug: pin card invisible after commit (hi-dpi)

Report: "when I commit a scanpin, I cannot see the 2D detail panel." Reproduced with puppeteer driving the real app.

- At `devicePixelRatio = 1` the commit flow was always correct (card visible with full probe content) — which is why headless testing initially missed it.
- At `devicePixelRatio = 2` (any Mac/retina display) the committed card landed at CSS `left: 1524px` in a 1400 px-wide window — entirely off-screen.
- **Root cause:** `Cards.projectToScreen` / `clampToViewport` (and the scale bar, lasso-commit NDC math, and every `pickRay` call) were fed `RenderControl.ViewportSize` = **framebuffer pixels** (CSS × dpr), then mixed with CSS-pixel cursor positions and CSS `left/top` placement. The Aardvark.Dom docs say it outright: `RenderControlInfo.ClientSize` is "CSS pixel size … use for HTML overlay positioning".
- **Fix (`View.fs`):** bind `RenderControl.ClientSize`, derive `overlaySize` (falls back to `ViewportSize` until the first DOM event populates ClientSize, which starts as `V2i.II`), and feed it to: the `viewportSize` cval (cards, scale bar, hover-probe tooltip), `resolveLayerPick`, `resolveFusionPick`, the wheel-hover mesh cycling, and `LassoCommit`. This also silently fixes the scale-bar label (was 2× off on retina) and all cursor-ray picking + lasso volumes on hi-dpi displays. The render `proj` stays on `ViewportSize` (only the aspect ratio matters there).
- Verified at dpr 1 and dpr 2: card at the same CSS position in both, full lifecycle (commit → close → reselect → edit → re-commit) green.
- Red herring chased during the investigation: the pin card's × button appeared dead — the repro script was actually clicking the *hidden lasso card's* × (`.card-btn-close` is shared chrome and the lasso card precedes the pin card in the DOM). Scoped to `.pin-card .card-btn-close` everything works.
- Second real (latent) bug fixed on the way: a pin whose in-flight probe was cancelled because another pin launched one (shared `probeCts`) stayed `ProbeRunning` — "Probing…" — forever, since the cancelled task never emits. `ensureProbe` now tracks `probeOwner` and resets the superseded pin to `ProbeNone` so it lazily recomputes when reselected.

## Coordinate-system audit

Four frames are in play; every probe/ICP computation was re-checked against them (user-requested audit after Phase 5).

| frame | definition | who lives here |
|---|---|---|
| **mesh-local** | OBJ vertices with the *per-mesh* centroid subtracted | `pm.positions : V3f[]`, Embree scene, BbTree — all server caches |
| **original world** | `local + meshCentroid` (absolute UTM-scale doubles) | per-mesh `centroid : V3d`, server query inputs, pin centres |
| **registered world** | `M · originalWorld`, M = per-mesh rigid registration map | probe request transforms, ICP output |
| **render** | `(world − CommonCentroid) × datasetScale` — *project centroid* shifts all meshes to a shared origin | client scene graph, `MeshTransforms`, camera, picking |

Verified invariants:

- **All heavy math runs in mesh-local frame.** The probe pulls the cylinder back per mesh — `cL = M⁻¹·centre − centroid`, `aL = M⁻¹-rotated axis` — and samples/projects entirely in local coordinates (`MeshProbe.fs:153–191`); normal PCA likewise builds its covariance over local positions (`:93–122`). Directions need no centroid handling (translation cancels), so local-frame eigenvectors map to world by `M.TransformDir` alone. The signed-distance values `t` are exactly world-frame distances because rigid maps preserve dot products.
- **float32 only after centroid subtraction.** Every `V3f(...)` conversion happens on local-magnitude values (probe `:94,159`; ICP closest-point `MeshIcp.fs:145` does `V3f(aMoved − refCentroid)`). No absolute UTM coordinate ever enters float32.
- **ICP optimizes locally** — the Gauss-Newton step is linearized around the weighted correspondence centroid (the Phase-5 fix), then recomposed to a world map `t_world = c − R·c + t`. Correspondence *gathering* stays in world doubles, which is benign (5×10⁶ × 1 ulp ≈ 5×10⁻¹⁰ m).
- **One render↔world conversion point.** Client `MeshTransforms` are render-space; the probe request converts each to a world map via `RigidTransform.renderToWorld` (conjugation by the project-centroid shift + dataset scale) with the *per-mesh* dataset scale, mirroring how `RegistrationComplete` stored it. Conjugating a rigid map by a similarity yields a rigid map, so the server's rigidity assumptions (unit axis, invariant radii) hold. Wire format is the row-major `M00…M33` order used by the existing ICP endpoint; validated empirically (explicit identity ≡ `null`, +1 m Z shifts the offset by ≈ `n·Δ`).
- `autoLengthAlong` projects registered-world bbox corners onto the axis; the extent is a difference of like-magnitude doubles (error ~1e-9 m). `XAuto`/`XFit`, quantiles, KDE all operate on frame-independent `t` scalars.

Side observation (pre-existing, untouched): `ResetCamera`/`JumpToMesh` feed `SceneBounds.Size.Length` (world metres) directly into a render-space camera radius without multiplying by the dataset scale — harmless for scale-1 datasets (Hessigheim, VictoriaCrater) but ~100× too far for `SETSM_glacier` (0.01). `FlyToPanorama` does it correctly. Worth a separate fix.

## Progress log

- **(init)** Read spec; read client model/update/view/card stack and server cache/analysis/handler stack. Verified `Sg.OnTap` event exposes `Ctrl` (needed for Phase 4 gesture) and that Aardvark.Base has no public eigensolver → hand-rolled symmetric 3×3 (analytic) on the server.
- **Phase 1 done.** Server: `MeshProbe.fs` (eigensolver, PCA normal + planarity, auto-length over transformed bbox corners, BbTree-pruned lattice sampling, quantile stats, shared-grid KDE, three sources), `POST /api/query/probe` in `QueryHandlers.fs`. Client: `ProbeModel.fs` (new file before `Query.fs` — wire layer returns typed `ProbeResult`), `Query.probe`, 4 new `ScanPin` fields, 5 new `ScanPinMessage`s, `ScanPinUpdate.ensureProbe` postlude wired via `Update.update = updateCore |> ensureProbe`, invalidation hooks on visibility/solo/bulk/reference/registration/reset/retarget/radius/payload/length, persistence of `probeLen`/`probeLock`/`probeRange`, cylinder-length slider + auto button in the placement flyout. `RigidTransform` (world↔render trafos) moved from `Update.fs` into `Model.fs` for reuse. Both projects build. Gotcha hit: Aardvark.Base shadows `Ok`/`Error` — `Result.Ok`/`Result.Error` required (same as Persistence.fs).
- **Phases 2 + 3 done.** `CardsPin.fs`: `ridgelineJs` + `probeBarJs` + `probeRidgeJson`/`probeStateJson` builders; Point card now has probe head (title + planarity badge), ridgeline (`data-ridge` attr), x-range preset bar + Lock-order toggle, ref/length caption, three-source stacked bar (`data-srcs`, native-title tooltips per segment), legend, numeric readout. CSS: `.pc-probe*`, `.pc-planar-*`, `.ridge-detail`, `.lp-probelen-*`. Client builds.
- **Phase 5 done.** Invalidation set verified against spec §6 (see D8; dataset switch and workspace load reset pins wholesale, so "mesh added/removed" is covered). Registration **Run button re-enabled** (was disabled as "TODO" — this slice *is* the feature work it was gated on; the demo requires a solve and `RegistrationComplete` already invalidates probes). End-to-end server validation against the live Hessigheim dataset (port 8002 in dev):
  - 3-mesh probe @ (513895, 5426600, 233.9), r = 5 m, auto length: **109 ms** warm, 8192/8192/3961 samples; reference centred at 0 ✓; the unregistered 2018-11 epoch reads **+9.38 m** median offset; sources dataset 6.58 / algo 6.63 / cond 5e-5.
  - Error paths: tiny radius → `not enough reference-mesh vertices (need ≥ 6)`; reference not in set → rejected. Both reach the card as `ProbeError` text.
  - Transform path: explicit identity reproduces `transform:null` exactly (median −0.067996, n 6294 both ways); a +1 m Z shift moves the offset by ≈ the normal's Z component (population reselection accounts for the remainder on rough terrain).
  - **Bug found + fixed (pre-existing): `MeshIcp.icpStep` diverged on UTM-scale data** — the Gauss-Newton rotation was linearized around the world origin, a ~5×10⁶ m lever arm (first solve produced 428 km translations, RMS 4.3 → 10⁴). Fixed by recentring the linearization on the weighted correspondence centroid and recomposing `t_world = c − R·c + t`. Convergence is now monotonic (4.28 → 3.55 on the 2019-03 pair).
  - **Second ICP hardening: trimmed correspondences** (gate at 3× median pair distance per iteration). Without it, closest-point pairs from non-overlapping mesh regions biased the solve enough to *worsen* the locally-aligned patch by 1 m; with it the trimmed RMS sits at ~0.5 m.
  - Demo workflow (spec asked for "Mars Kodiak" — doesn't exist; used **Hessigheim**, see D14): load dataset → place pins on the overlap area (e.g. around 513895/5426600) → pin card shows the ridgeline with 2018-03 (ref) at 0, 2019-03 near −0.07 m, 2018-11 at +9.4 m → open Registration, pick Hess-201803 as reference, Run → probes invalidate and recompute with the solved transforms; offsets and the algorithm bar change.
  - Rough edges (documented, out of slice scope): (1) global-vs-local disagreement — a global trimmed ICP can shift a locally well-aligned patch by ~0.3 m when the scene changed between epochs (vegetation); the probe correctly *measures* this, which is the point of the tool. (2) The badly-misaligned 2018-11 epoch (~40 m off, partial overlap) stays in a wrong ICP basin — inherent to ICP without coarse initialization. (3) Conditioning at c_scale = 1.0 is visually negligible against metre-scale errors on these datasets (≈10⁻⁴ m with healthy probes) — by design it only grows on coverage gaps; revisit the scale once real workflows exist. (4) The in-browser SVG rendering (ridgeline + tooltip) builds clean against the Aardvark.Dom OnBoot/MutationObserver pattern but has not been visually verified in a browser in this session — first thing to check when running.
- **Phase 4 done.** `HoverProbeState` (ProbeModel.fs) + `Model.HoverProbe` slot; `HoverProbeAt`/`HoverProbeResult`/`ClearHoverProbe` messages; reducer launches an uncached probe (radius = 5% scene-bbox diagonal, auto length, 4096 pts/mesh) with its own CTS and an 8 s auto-clear; `View.fs` Ctrl-gated `Sg.OnTap` branch (works in normal, layer-pick and fusion modes, depth-gated), Escape clears the tip before falling through to lasso/placement cancel, any plain tap dismisses; `GuiOverlays.hoverProbeTooltip` renders the `d.mini` ridgeline in a fixed 244 px tip clamped to the viewport. **Deviation from spec §7.4:** dismissal "after ~5 s of cursor movement away" simplified to a fixed 8 s timeout (+ Escape + click-elsewhere + next Ctrl-click) — cursor-distance tracking would need a global pointer-move hook for marginal value. Colours come from `MeshOrder` (same palette as the mesh list), since the transient probe has no per-pin colour map.
