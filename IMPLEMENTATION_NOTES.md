# Ensemble Registration Workflow — implementation notes

Working notes for `ScanPin_registration_implementation_spec.md`. Updated per work package.

## WP0 — repo map (verified 2026-06-11)

| Concern | Location |
|---|---|
| Client Model root | `src/Superprojekt/Model.fs` (`[<ModelType>] Model`), adaptify output `Model.g.fs` |
| Pin types + payloads | `src/Superprojekt/ScanPinModel.fs` (`ScanPin`, `PayloadType`, `PointPayload`, `ContactRingState`, card types) |
| Probe DTOs | `src/Superprojekt/ProbeModel.fs` |
| Messages | `src/Superprojekt/Messages.fs` (`Message`, `ScanPinMessage`, `CardMessage`) |
| Reducer | `src/Superprojekt/Update.fs` (`Update.updateCore` + `ensureProbe`/`ensureRings` postludes in `ScanPinUpdate.fs`) |
| Registration card / retarget modal / panorama card | `src/Superprojekt/GuiCards.fs` (`registrationCard` is opened by a local `cval` toggle button, not a Message) |
| Mesh panel / placement flyout | `src/Superprojekt/GuiPanels.fs` |
| Pin card body + violin chart | `src/Superprojekt/CardsPin.fs` (`ridgelineJs`, `probeRidgeJson`); card chrome `Cards.fs` |
| Mesh shader (ghost rules, provenance heatmap, cursor band) | `src/Superprojekt/MeshShaders.fs` (`MeshShader.shade`); uniforms set in `MeshView.buildScene` |
| Ghost pass | not a separate pass — per-fragment α path in `MeshShader.shade` (`MeshActive=false` → uniform ghost) |
| Scene composition | `src/Superprojekt/SceneGraph.fs`; pin 3D visuals `ScanPinScene.fs`; pixel-constant lines `LineShader.fs` (`Lines.render`) |
| Workspace (de)serialization | `src/Superprojekt/Persistence.fs` (hand-rolled JSON, `"version":1`) |
| Server query handlers + routing | `src/Superserver/QueryHandlers.fs`, `Handlers.fs` |
| ICP | `src/Superserver/MeshIcp.fs` (`runIcp` returns the FULL transform incl. initial, not a delta) |
| Patch / isoline / contact rings | `src/Superserver/MeshAnalysis.fs` (`patch` fits plane from BVH-candidate triangle normals; geodesic Dijkstra footprint) |
| Probe | `src/Superserver/MeshProbe.fs` (`ProbeArgs.Transform` = **world-space** per-mesh transform; client passes `RigidTransform.renderToWorld(MeshTransforms[m]).Forward`) |
| Mesh/atlas fetch | client `MeshData.fs` (atlas URL `…/mesh/{name}/{i}/atlas`); server `MeshLoader.fs`/`MeshCache.fs` |

Key conventions confirmed:

- `Trafo3d` composition is **postfix**: `a * b` applies `a` first (checked against Aardvark.Base 5.3.23 XML docs + usage like `Trafo3d.Scale r * Trafo3d.Translation c`).
- `MeshTransforms : Map<string, Trafo3d>` stores **render-space** trafos; world ↔ render via `RigidTransform.worldToRender/renderToWorld` (conjugation by the centroid-translate + dataset-scale map). All server queries reconstruct world transforms through `renderToWorld`.
- **Found while reading (pre-existing bug):** `MeshView.meshTrafo` composes `meshTransform * base` (registration trafo applied to mesh-local coords *before* the centroid/scale base). For dataset scale 1 and small ICP rotations the error is invisible, but for scaled datasets the rendered translation shrinks by `scale` and large landmark rotations would render inconsistently with every query path. Fixed to `base * meshTransform` in WP6 (one place, used by mesh/fusion/panorama builders).
- No pre-existing test projects anywhere in the solution. WP13 adds `src/Supertests` (console runner, paket-managed, no new packages).
- Local datasets exist (`src/Superserver/data`: Hessigheim, SETSM_glacier, VictoriaCrater) → integration script is feasible. Port 5000 is shadowed by macOS AirPlay; run the server with `ASPNETCORE_URLS=http://localhost:8002`.

## Spec ↔ codebase reconciliations (running list)

1. **`pendingDelta : Map<MeshName, M44d>`** → implemented as `PendingReg : PendingRegistration option` whose `Results : Map<string, PendingMeshResult>` carry the render-space delta `Trafo3d` *plus* the per-mesh rms before/after, convergence, collinearity flag and per-pair residuals that §5/§8.5 need anyway. "pendingDelta non-empty" ⇔ `Results` non-empty.
2. **Delta storage space**: deltas stored render-space (same convention as `MeshTransforms`); effective preview pose = `committed * delta` (postfix). Conjugation preserves composition, so this is exactly the spec's `pendingDelta[m] * committed[m]` in world space.
3. **Anchors are world-space at current committed pose** (spec). Therefore commit/rollback re-bases anchors of moved meshes by the world delta (`bake`/`unbake` in Update). Anchor *picking* is disabled while a preview is pending so an anchor can never be captured at a preview pose and then double-transformed on commit.
4. **Correspondence record** also stores `RefAnchor`/`RefDistance` (projection of pin centre onto the reference) — the spec derives it ad hoc, but solve/visuals/patch-picker all need it.
5. **Types live in `RegistrationModel.fs`** (new file before `ScanPinModel.fs`), with `ScanPinId` moved there, so the pure registration state machine is compilable outside the WASM project for unit tests.
6. **Anchor decisions** use a dedicated `AnchorDecision` DU rather than reusing `RetargetDecision` (defined later in compile order).
7. WP5 "each pin row gets an exclude toggle" — implemented in both plausible places: registration-card coarse readiness list (⊘ per enabled pin) and the pin card correspondence section header toggle.
8. **`ProvenanceHeatmap : bool` → `HeatmapMode` DU** (`HeatOff | HeatProvenance | HeatDiff`) with `HeatmapPrev` for the auto-revert on commit/discard. Old workspaces (`provHeatmap` bool) still load.
9. **Patch picker "textured by default"**: patch points get per-vertex atlas UVs (server addition, backwards-compatible); the card JS samples the mesh atlas through an offscreen canvas. "Shaded" toggle = height colormap (the pre-existing patch rendering), since true hillshade needs a mesh, not a point set.
10. **Anchor glyphs**: rendered as wireframe tetrahedra through the existing pixel-constant `Lines.render` (one adaptive node for all glyphs+links) instead of filled meshes — keeps GPU resource churn at zero when anchors change, per the CLAUDE.md adaptive-performance rules.
11. **Reference outline**: subtle render-space bbox outline (12 edges, accent colour) instead of a screen-space silhouette — the WebGL stack here has no stencil/post pass and CLAUDE.md forbids the fragile paths.
12. **Fine solve rms**: `rmsBefore` = first ICP convergence entry, `rmsAfter` = RMS of final residuals (same numbers the old flow produced).
13. **Reset button** now means "roll back all steps" (spec §8.4); identity transforms restored step by step so anchor un-baking stays exact. The old hard `ResetMeshTransforms` semantics remain reachable only through workspace load.
14. **WP10 violin axial pick**: gated on correspondence enabled; uses the *effective* probe (preview probe while pending, else committed).

## Per-WP status

- **WP0** ✅ this file.
- **WP2** ✅ `RegistrationModel.fs` (new), `ScanPinModel.fs` (PointPayload.Correspondence, ScanPin.ProbePreview), `Model.fs` (PendingReg, RegistrationLog, HeatmapMode/Prev, AnchorReview, AnchorPick, PatchPicker), adaptify regenerated, `Persistence.fs` version 2 (pins.correspondence + registrationLog; old files load with defaults).
- **WP3** ✅ `RegMath.fs` (weighted Umeyama, Jacobi eigen, conditioning), `/api/query/lsq-pairs` (400 on <3 pairs), patch `frameNormal`/`frameRefDir` override + per-point `uv`; client `Query.lsqPairs`, `Query.patchInFrame`.
- **WP1** ✅ ★ per mesh row (two-way with registration card), tooltip, reference bbox outline, reference-change cascade (probes + pending cleared + re-seed).
- **WP4** ✅ ToggleCorrespondence → parallel closest-point seeding → review modal (per pin×mesh rows, Δ flags, accept/reject, Apply); re-seed on reference change & retarget commit (Auto/unaccepted only).
- **WP5** ✅ registration card restructure (reference / coarse stage with readiness + conditioning badge + solve / fine stage with warn-once / pending table with sparkline + commit/discard / history), coarse + fine solves land in PendingReg.
- **WP6** ✅ meshTrafo order fix, preview pose, committed-ghost tint render, banner, guards (placement/retarget/fusion/dataset switch), probes+rings under effective transforms, anchor glyphs + links.
- **WP7** ✅ ProbePreview plumbing + split half-violins with Δ-arrow in `ridgelineJs`.
- **WP9** ✅ patch picker (shared frame, transform-aware, atlas-textured small multiples, crosshair, click→ray→Patch2D anchor, toast on miss).
- **WP10** ✅ one-shot 3D pick (shader-level solo, depth-gated, Esc, auto-advance), Shift+click violin axial pick.
- **WP8** ✅ heatmap Diff mode (signed Δ combined error, LoD mask → ghost, diverging blue/red, hover Δ+LoD tooltip, auto-revert).
- **WP11/12** ✅ rollback newest / reset-all with anchor un-baking, collinearity badges, guard tooltips.
- **WP13** ✅ `src/Supertests` console runner (Umeyama: recovery n=3/4/50, det+1 on planar, weight=duplication, collinearity flag, <3-pairs client-guard; reg-log commit/discard/rollback; correspondence/log JSON round-trip incl. missing-fields defaults) + `tools/integration.mjs` HTTP flow. Results below.

## Verification results (2026-06-11)

- **Builds**: `dotnet build Superprojekt.sln` — 0 errors (56 warnings, all pre-existing: FusionView `GetOutputView` deprecations + FShade AOT infos).
- **Unit tests** (`dotnet run --project src/Supertests`): **38/38 passed**. Umeyama recovers random rigid transforms at n=3/4/50 to ≤5e-15 residual (UTM-offset variant ≤3e-9), reflection-prone mirrored sets yield det(R)=+1 to 1e-16, weight-2 ≡ duplicated pair to 9e-16, collinear sets flag the warning, <3 pairs → None; RegLog commit/rollback restores transforms and residuals exactly; RegJson round-trips correspondence + a two-step log (coarse and fine inputs) and defaults cleanly on missing fields.
- **Integration** (`node tools/integration.mjs`, server on :8002, Hessigheim): **19/19 passed**. lsq-pairs recovers T⁻¹ to 1.9e-9 m (action-on-points metric — raw matrix entries amplify the ~5e5 m UTM lever arm), residuals ≤1e-9, <3 pairs → HTTP 400, collinear pairs flag the warning; ICP from the corrected transform decreases RMS 4.293 → 3.403 over 12 iterations; the probe's moving-mesh median error vs the unperturbed baseline shrinks 0.580 → 0.000 m after the lsq correction (absolute medians are confounded by genuine inter-epoch change in Hessigheim, hence baseline-relative assertions); patch returns UV-carrying points and echoes the requested frame exactly.
- **In-browser** (puppeteer against the served WASM client, since `dotnet build` cannot catch ESSL3 shader errors): meshes render with the modified `MeshShader` (HeatmapMode + diff branch — 337k mesh pixels, no shader compile errors), registration card shows Stage 1 / Stage 2 / History with "Solve coarse" correctly disabled, mesh-panel ★ toggles and mirrors into the card ("★ Reference: Hess-201803"), heatmap radio shows Off/Sources/Diff.

## Observed pre-existing issues (not in spec scope, untouched)

- `View.resolveFusionPick` raycasts meshes in their **untransformed** frames — fusion picking is slightly wrong for registered meshes (it predates this work; fusion is blocked during previews, so the new workflow never hits it).
- `Update.RegistrationFailed` used to flip `Running = false` on the **first** per-mesh failure even with other solves in flight; the new Expected-countdown replaces that.

---

# User Study Mode — implementation notes

Working notes for `ScanPin_study_mode_implementation_spec.md`. Updated per work package (`SWP<n>` commits).

## SWP0 — repo map (verified 2026-06-11)

| Spec concern | Location |
|---|---|
| App root view / boot | `src/Superprojekt/View.fs` (`View.view` builds the whole DOM in one `body { }`; `App.app` record) + `Program.fs` (`Boot.run gl App.app`). **No router exists** — the server serves `index.html` for every non-API path via `MapFallbackToFile`, so `/s/{token}` already reaches the client; the path is readable via `Window.Location.Path` (verified against Aardworx.WebAssembly 1.2.8: `Location` has `Protocol/Host/HostName/Port/Path/Href/Search` + `GetQuery()`). |
| Top bar | `GuiTopBar.topBar` (hamburger, dataset dropdown, Lasso/Pin/Fusion/Pano buttons, camera reset, coord readout, gear) |
| Gear popover | end of `GuiTopBar.fs` (inside `topBar`, `.tb-gear-popover`, gated by `Model.GearPopoverOpen`) |
| Mesh panel | `GuiPanels.leftPanel` (mesh rows incl. ★ reference toggle, pin list, error metadata, provenance/heatmap radio Off/Sources/Diff) |
| Registration card | `GuiCards.registrationCard` — opened by a **local `cval`** (`registrationOpen` in `View.view`), not a Message; "Set as final" button goes here |
| Pin card + violin chart | `CardsPin.pinCardBody` + `ridgelineJs`/`probeRidgeJson` (violin), three-source stacked bar in the same file; card chrome `Cards.fs` (`cardDragHandle/cardPos/cardStyle`), 3D-anchored card system `Cards.renderCards` |
| Provenance heatmap mode state | `Model.HeatmapMode` (`HeatOff\|HeatProvenance\|HeatDiff` in `RegistrationModel.fs`) + `HeatmapPrev`; radio UI in `GuiPanels.fs`; shader uniform in `MeshView.buildScene` |
| Split-violin preview | `ScanPin.ProbePreview` + `preview` param of `CardsPin.probeRidgeJson` (kde2/median2/q12/q32 rows) |
| Message/update loop | `Messages.fs` (`Message`/`ScanPinMessage`/`CardMessage`) → `Update.updateCore` + postludes (`ensureProbe`/`ensureProbePreview`/`ensureRings`); module-level `CancellationTokenSource` refs for debounce |
| Workspace (de)serializer | `Persistence.fs` (hand-rolled JSON v2, `serialize : Model -> string`, `apply : string -> Model -> Result<Model,string>`) |
| Server routing | `Handlers.webApp` (Giraffe `choose`), JSON binding via `ctx.BindJsonAsync<CLIMutable DTO>` (case-insensitive), responses via `json {\| … \|}`; startup `Program.fs` (CORS, Blazor static files, Giraffe, `MapFallbackToFile "index.html"`) |
| Server data root | `MeshLoader.findDataRoot` walks up from `AppContext.BaseDirectory` looking for `data/` → **`studies/` is resolved the same way as a sibling of `data/`** |
| Datasets on disk | `Hessigheim` (3 epochs), `SETSM_glacier` (9 epochs, scale 0.01), `VictoriaCrater` → glacier-v1 uses tutorial=`Hessigheim`, main=`SETSM_glacier` |
| Tests | `src/Supertests` console runner compiles WASM-free client/server files directly (`RegistrationModel.fs`, `RegMath.fs`); integration `tools/integration.mjs` against :8002 |

New-file plan (compile-order positions):

- Client `StudyModel.fs` (after `RegistrationModel.fs`, before `ScanPinModel.fs`): WASM-free — feature ids, `StudyCondition`, config DTOs + `configPublic` JSON parser, `Predicate` + incremental evaluation state, `StudyState`/runtime types, answer drafts. Compiled into Supertests.
- Client `StudyApi.fs` (after `Query.fs`): HTTP wrappers for `/api/study/*`.
- Client `StudyTelemetry.fs` (after `Messages.fs`): mutable event queue + batcher (5 s / 50 events / immediate flush triggers), throttle state — module-level mutables like the existing CTS refs.
- Client `GuiStudy.fs` (after `GuiCards.fs`): study bar, instruction overlay / anchored tooltip, task pane, question widgets.
- Server `StudyConfig.fs` (before `QueryHandlers.fs`): config + secret parsing, startup validation — WASM/Giraffe-free, compiled into Supertests.
- Server `StudyStore.fs` (after `StudyConfig.fs`): JSONL stores with per-sid locks, balanced assignment, TRE scoring, HMAC completion code — Giraffe-free, compiled into Supertests (store root parameterised for temp-dir tests).
- Server `StudyHandlers.fs` (after `QueryHandlers.fs`): HTTP handlers, wired into `Handlers.webApp`.

## Spec ↔ codebase reconciliations (study mode)

1. **`AppMode = Full | Study of StudySession`** → implemented as `Model.Study : StudyShell option` (`None` ⇔ Full; `StudyShell = StudyJoining | StudyFailed | StudyScreened | StudyActive of StudySession`). Equivalent semantics; keeps the adaptify model flat and every existing view/update path untouched in Full mode.
2. **Condition DU** named `CondFull | CondNum` (JSON tags `"FULL"`/`"NUM"`) to avoid clashing with the ubiquitous `Full` identifier; predicate DU cases are P-prefixed (`PEvent`/`PAnd`/`POr`/`PSeq`/`PAnswerSubmitted`/`PAlways`) for the same reason.
3. **"Phase F"** (§8, transforms post trigger) read as the **exit phase** (P6): question-id letters run A=P1 … E=P5, F=P6 (QA*↔P1, QC*↔P3, QD1↔P4, QE-final↔P5), consistent with §10's "auto-upload workspace at final (P5→P6 transition)" and §3's completion requirement. Entering the exit phase posts `final` transforms + uploads the workspace; the P4 "Set as final" button posts the same label.
4. **Predicate event counts are cumulative since the last dataset switch**, not reset per step. The spec's parenthetical "(reset on step entry, except Seq progress)" contradicts its own §9 P4 predicate `And[Event(fineSolved,2); …]` (only one extra solve happens in P4 — the count must carry over from P2) and §9 P2's final `Seq` stage `Event(committed,2)` (which must NOT see the tutorial commit — hence reset at the tutorial→main switch). Cumulative-per-dataset-epoch satisfies every example in §6/§9; Seq milestones stay monotone per step and survive re-entry (the tutorial retry path).
5. **Study bar hosts a gated tool strip** (layers toggle, ○ Pin) — real sessions have no top bar, but `pinPlace`/`meshPanel` are allowed features in several phases and those buttons live in the top bar in Full mode. Registration opens through its pre-existing edge toggle button, gated on `registrationCard`.
6. **Two extra per-session store files**: `advance-{sid}.jsonl` (the §3 progress mirror must be queryable for completion/resume — the spec's store list omits a home for it) and `transforms-{sid}.jsonl` (raw posted transforms; `scores-{sid}.json` holds only the TRE results).
7. **Tutorial gold retry**: config steps gain an optional `retryStep` field (validated within the phase); default = nearest preceding guidedAction. The 2nd wrong answer jumps back to that step (its Seq progress is preserved, so re-walking is read-only); the 3rd is screened server-side from the answers file against `secret.goldFailThreshold`.
8. **Questionnaire steps** synthesize one `LikertGrid` question from `config.questionnaires` (id = questionnaire key, one grid answer per step; scales fixed by key: sus → 5-pt, tlx → 0–100 sliders, icet → 7-pt per §7).
9. **Markdown bodies** render as blank-line-separated paragraphs only — the copy is placeholder English by §13, and the Aardvark.Dom CE has no raw-HTML injection path worth the risk.
10. **Page-hide telemetry flush** uses a hidden DOM bus (`visibilitychange`/`pagehide` → synthetic input → `StudyTelemetry.flushNow`); a true `sendBeacon` would need the queue mirrored into JS. Best effort per spec.
11. **Demo sessions are recorded** in `sessions.jsonl` (flagged `demo:true`) so their answers/events/transforms have a home; they are excluded from balanced assignment, which counts non-demo active+completed only.
12. **`studies/` root** resolves exactly like `data/` (walk up from `AppContext.BaseDirectory`), i.e. `src/Superserver/studies/` in this repo; runtime artefacts (`data/`, `tokens.jsonl`, `server-secret.txt`) are gitignored, `config.json`/`secret.json` are committed.
13. **Virtual route fixes**: `index.html` gains `<base href="/">` (all asset URLs were relative and broke under `/s/{token}` — the app never booted) and `ApiConfig.apiBase` ignores a leading `/s/…` when deriving the API base.
14. **The final config step is `optional: true`** — it displays the completion code, so it can never have an advance record before `complete` is called; §3's "all non-optional steps" check is satisfied by everything before it.
15. **`GET /api/study/list`** added (not in the spec's endpoint table): the gear popover's demo study picker needs the valid study ids.
16. **Gold question T1** is phrased data-independently ("the reference column is zero by definition") — the violin/RMS values on real Hessigheim data shift with genuine inter-epoch change, so a data-dependent planted answer would be fragile. QA2/F1/F2 secret values are coarse placeholders for the researcher to refine (polygon, change values, tolerances).
17. **Update-guard granularity**: blocked high-frequency messages (camera pointer moves, chart hover) no-op silently; discrete actions toast "Not available in this step". Solver *result* messages always pass — only user-action messages are gated.

## Per-WP status (study mode)

- **SWP0** ✅ repo map above.
- **SWP6** ✅ `StudyModel.fs` — predicate engine + config DTOs/parser + runtime types; WASM-free, compiled into client, server and Supertests.
- **SWP2** ✅ `StudyConfig.fs` — secret parsing, startup validation (datasets, features, anchors, questionnaire keys, predicate refs, gold↔secret, retrySteps, forbidden-key scan of the public file).
- **SWP3** ✅ `StudyStore.fs` + `StudyHandlers.fs` + routes — JSONL stores with per-sid locks, balanced assignment, order-validated advance (idempotent repeats), TRE scoring on every transforms post, HMAC-SHA256 completion code, tutorial-gold echo + screening.
- **SWP11** ✅ `POST /api/study/{studyId}/tokens` (localhost-only).
- **SWP1** ✅ `/s/{token}` entry, demo entry from the gear popover, exit-demo, joining/failed/screened pages, deterministic scene reset on entry.
- **SWP4** ✅ runtime reducer (Next gating per step kind, advance posts, dataset switches, gold flow, completion fetch, Set-as-final) + `StudyEvents.derive` diffing (before, after, msg) into the fixed event list.
- **SWP5** ✅ study bar (dots, goal line, tool strip, ?, Next), instruction overlay / anchored tooltip with live checkmark, feature gating in views + update-level guard, NUM RMS table.
- **SWP7** ✅ question widgets (singleChoice / sceneClick / numeric / freeText / likertGrid + confidence), task pane, 3D flag markers.
- **SWP8** ✅ telemetry batcher (5 s/50-event flush, immediate on phaseEnter/stepComplete/page-hide, backoff retry, bounded queue dropping throttled types first, fpsSample).
- **SWP10** ✅ resume (same token → same session at last advance, notice banner), screened-token page, workspace auto-upload at final.
- **SWP9** ✅ `studies/glacier-v1/` config + secret (P0–P6 per §9, placeholder copy, NUM star filter, moving polygon, check-point pairs).
- **SWP12** ✅ below.

## Verification results (study mode, 2026-06-12)

- **Builds**: `dotnet build Superprojekt.sln` — 0 errors (pre-existing warnings only).
- **Unit** (`dotnet run --project src/Supertests`): **103/103 passed** (38 registration + 65 study). Predicate engine: thresholds, And/Or/answer, Seq ordering gate (later-stage events can't complete early stages), monotone progress across count resets, multi-stage advance, JSON parse + reference extraction. Reducer gating: instruction-on-render, guidedAction predicate, question answer+confidence, tutorial-gold confirmation gate (and non-tutorial non-gate), questionnaire completeness, freeText min length, retry-step resolution, NUM/FULL feature filtering, point-in-polygon. Config validation rejects dangling question refs, gold-without-secret, unknown features/events, missing datasets, secret keys in the public file. Store: 100 concurrent token sessions → 50/50 split (≤1 required), token resume/refusal, HMAC code (8 hex, deterministic, sid-dependent), TRE 8.7e-16 at the known transform, gold wrong×3 → screened + status, advance order/idempotence, completion gating (refused until all advances + final transforms, then code).
- **Integration** (`node tools/study-integration.mjs`, server on :8002): **27/27 passed**. Balanced FULL/NUM pair, configPublic key-scan clean, NUM resolved feature set excludes all starred features, secret.json/scores unreachable via four route shapes, out-of-order advance 409, events/transforms/workspace 204 (no scores returned), tutorial gold echoed (wrong/right) and main-phase gold not echoed, full config walk (all 26 steps), mid-study resume at the recorded position, complete 409 mid-study and without final transforms then an 8-hex code, completed/screened token refusal, third-fail screen-out.
- **In-browser** (puppeteer): `/` renders Full mode unchanged (top bar visible, no study chrome); gear → "Preview study mode ▶ glacier-v1" reaches P0 step 1 (study bar "Getting started" + goal line, 7 progress dots, demo badge, intro overlay, Next enabled on the instruction step); Next → guided orbit step shows the anchored tooltip with "○ not done yet" and a disabled Next; Exit study returns to Full mode; `/s/<bad-token>` shows the invalid-link page; `/s/<real-token>` enters the study with **no demo badge and no exit button** (no navigation back, §1).

## Observed pre-existing issues (study mode scope, untouched)

- The fusion-pick transform issue noted above remains; fusion is a Full-only feature and unreachable in study mode.
