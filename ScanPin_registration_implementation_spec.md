# Implementation Spec — Ensemble Registration Workflow

Audience: autonomous coding agent. Implement everything below end-to-end. Do not ask for
confirmation; all design decisions are final. Verify your own work (§9) at every step.

## 0. Context & ground rules

- Stack: Blazor-WASM client (Elm-style Model → Update → View, WebGL2), ASP.NET/Giraffe
  server (Embree BVH, closest-point, M3C2 probe, isolines, patch, contact rings, ICP),
  Aardvark platform. Server at `http://localhost:5000`.
- **WP0 (first):** explore the repo. Locate: client Model/Update/View types, pin types and
  payloads, registration card, retarget modal, mesh panel, probe/violin chart, provenance
  heatmap shader path, ghost pass, workspace (de)serialization; server query handlers and
  routing. Record paths in `IMPLEMENTATION_NOTES.md` and keep it updated per work package.
- Conventions to preserve: all query coordinates world-space (server converts to mesh-local);
  per-mesh requests parallel; 250 ms debounce + cancellation of superseded requests; heavy
  work off the UI loop; depth-gated picking; existing invalidation cascade semantics.
- All transforms rigid 4×4 (rotation + translation only, no scale).
- After each work package: build client + server clean (zero new warnings where feasible),
  run all tests, commit with message `WP<n>: <summary>`.

## 1. Client data model (WP2)

```
type AnchorSource = Auto | Patch2D | Pick3D | ViolinAxial

type Anchor = { point : V3d            // world space
                source : AnchorSource
                accepted : bool }      // Auto starts false

// extend Point payload (NOT a new payload type)
Pin.correspondence : { enabled : bool
                       anchors : Map<MeshName, Anchor> } option

// model root
pendingDelta     : Map<MeshName, M44d>      // empty = no preview
registrationLog  : RegStep list

type RegStep = { step : int; stage : Coarse | Fine; mode : string
                 timestamp : DateTimeOffset; referenceMesh : MeshName
                 inputs : RegInputs        // pinIds+reliabilities+anchorSources | icp params+anchor pins
                 outputs : Map<MeshName, { transformAfter : M44d
                                           transformBefore : M44d
                                           rmsBefore : float; rmsAfter : float }> }
```

- Effective preview transform per mesh: `pendingDelta[m] * committed[m]`. Commit sets
  `committed[m] := pendingDelta[m] * committed[m]`, clears `pendingDelta`, appends a RegStep
  (store both before/after absolute transforms), fires the registration-complete
  invalidation cascade (all probes + contact rings + algorithm RMS).
- Discard clears `pendingDelta` only; nothing recomputes.
- Reference anchor of a pin = pin centre if host = reference mesh, else closest-point
  projection onto the reference (flag in UI if projection distance > 2× falloff).

## 2. Server endpoints (WP3, WP9)

### 2.1 New: `POST /api/query/lsq-pairs`

```
req : { movingName : string
        pairs : [{ refPoint : V3; movingPoint : V3; weight : float }] }   // world space, current poses
resp: { transform : M44                       // delta: maps current-world moving pts onto ref
        perPairResiduals : float[]            // |T(m_i) − r_i| post-solve
        conditioning : { eigenvalues : float[3]          // weighted covariance of movingPoints, desc
                         collinearityWarning : bool } }  // λ2/λ1 < 1e-3
```

Weighted rigid absolute orientation (Umeyama/Arun, no scale):
`m̄ = Σwᵢmᵢ/Σwᵢ`, `r̄ = Σwᵢrᵢ/Σwᵢ`; `H = Σ wᵢ (mᵢ−m̄)(rᵢ−r̄)ᵀ`; SVD `H = UΣVᵀ`;
`R = V·diag(1,1,det(V·Uᵀ))·Uᵀ`; `t = r̄ − R·m̄`. Reject (HTTP 400) fewer than 3 pairs.

### 2.2 Modify: `POST /api/query/patch`

Add optional request fields `frameNormal : V3`, `frameRefDir : V3`. When present, skip local
plane fitting and project into the supplied frame (origin = request centre). Response
unchanged. Backwards compatible when fields absent.

## 3. Reference designation (WP1)

- Mesh panel: ★ toggle per row, single-selection, two-way bound to the existing reference
  selection in the registration card. Reference mesh: persistent subtle outline in 3D,
  ★ in legend. Tooltip: "All error metrics are relative to this mesh (no absolute ground truth)."
- Reference change: existing probe invalidation **plus** clear `pendingDelta` and re-run
  auto-seed (§4) for all correspondence-enabled pins.

## 4. Anchor auto-seed + review modal (WP4)

- Enabling `correspondence` on a pin (toggle in pin card) seeds, for every loaded mesh ≠
  reference, `Anchor { point = closestPoint(mesh, refAnchor); source = Auto; accepted = false }`
  via parallel `/query/closest` (reuse retarget machinery).
- Review modal (clone retarget modal): one row per (pin × mesh) seeded anchor with projection
  distance Δ; rows with Δ > 2× falloff or no projection flagged red. Accept ✓ / reject ✕ per
  row; Apply sets `accepted`; rejected anchors remain unaccepted (resolvable later via §7/§8).
- Re-seed triggers: reference change; pin centre/retarget move (only that pin, only
  unaccepted or Auto anchors — never overwrite accepted Patch2D/Pick3D/ViolinAxial anchors).

## 5. Registration card restructure + coarse solve (WP5)

Layout (top to bottom):

1. `★ Reference: <mesh>` (mirror of §3).
2. **Stage 1 · Coarse (landmarks).** Readiness line: count of enabled pins; accepted pairs
   per moving mesh; conditioning badge (client-side λ2/λ1 of accepted ref-anchor spread as a
   pre-check; authoritative value comes from the solve response). `▶ Solve coarse` enabled
   iff ≥1 visible moving mesh has ≥3 accepted pairs.
   - Solve: per visible moving mesh with ≥3 accepted pairs, POST `/lsq-pairs` in parallel
     (pairs = (refAnchor, anchors[mesh].point, pin reliability), points in current world
     poses). Meshes with <3 pairs are skipped and listed "unsolved".
   - Results → `pendingDelta`; rmsBefore = weighted RMS of pair distances pre-solve,
     rmsAfter = RMS of `perPairResiduals`.
3. **Stage 2 · Fine (ICP).** Existing mode radio (Traditional / Region-restricted) and solve,
   unchanged math. `initialTransform` = current committed transform (which includes any
   committed coarse step). Results → `pendingDelta` (delta relative to committed),
   rms from ICP residuals. If no Coarse RegStep exists, show a one-time inline warning,
   do not block.
4. **Pending result** (visible iff `pendingDelta` non-empty): per-mesh table
   `RMS before → after (Δ%)`, convergence sparkline for ICP steps; `✓ Commit` / `✕ Discard`.
5. **History**: RegStep list, newest first: `#n <stage> <mode> · RMS a→b`, `↩ Roll back`
   (§8.4). Reset button = roll back all.

Per-pair residuals after a coarse solve are shown per pin in its card correspondence section;
each pin row gets a one-click "exclude from correspondence" toggle (sets enabled=false).

## 6. Preview rendering & state guards (WP6)

While `pendingDelta` non-empty:

- Moving meshes render at effective preview pose; additionally render committed pose via the
  existing ghost pass (ghost opacity, distinct tint). Ghost-pass depth behavior unchanged
  (picks pass through).
- Thin viewport banner: "Previewing unregistered result — commit or discard".
- Disable: pin placement, retarget, fusion toggle, dataset switch (tooltip explains why).
- Contact rings + probes for any open cards recompute under effective preview transforms
  (pass preview transforms in the existing request fields); on discard they recompute back.
  Debounce/cancellation rules unchanged.

Correspondence visuals (always, not only preview): accepted anchors render as small
tetrahedron glyphs in mesh palette colour; thin line from each anchor to its pin's reference
anchor (α 0.6, 1 px; selected pin: α 1.0). Lines/glyphs follow effective preview transforms.

## 7. Violin split + patch picker (WP7, WP9)

### 7.1 Split violin (WP7)

While `pendingDelta` non-empty and a pin card is open: issue two probe queries (committed
transforms; effective preview transforms). Each mesh column renders paired half-violins —
committed left (desaturated), preview right (full colour) — plus an arrow from old median to
new median with a Δ label. Existing single-violin layout returns on commit/discard.
Chart↔3D linking keeps working against the preview-pose geometry.

### 7.2 Patch small-multiples picker (WP9)

Pin card correspondence section, `▦ Pick in patches` button:

- Query the reference patch first (no frame override) → take its `normal`, `refDir` as the
  shared frame. Then query `/query/patch` for every visible mesh, centre = that mesh's
  current anchor (Auto seed if none), with `frameNormal`/`frameRefDir` set. Parallel,
  debounced, cancellable.
- Render small multiples in the card: orthographic point/heightfield footprint per mesh,
  reference first with distinct border, mesh-colour header swatch. Textured rendering
  default; toggle for shaded. Crosshair overlay at the reference anchor's (u,v) on every
  patch.
- Click in mesh M's patch: (u,v) → world ray `origin = centre + u·refDir + v·(normal×refDir)
  + h·normal` (h = patch height above surface), direction = −normal → `/query/ray` against M
  → on hit, set `anchors[M] = { point = hit; source = Patch2D; accepted = true }`.
  Miss = no-op with brief toast.
- Setting any anchor invalidates that pin's card readiness display only (no probe recompute
  — anchors don't affect probes).

## 8. Fallback picks, heatmap diff, rollback, guards (WP8, WP10–12)

### 8.1 3D fallback pick (WP10)

Per anchor row, `⊕ Pick in 3D`: one-shot mode — target mesh solo'd, reference mesh forced
visible at α ≈ 0.3, all others ghosted; crosshair cursor; one depth-gated click sets
`{ point; source = Pick3D; accepted = true }`; Esc cancels; on completion restore previous
visibility (reuse solo save/restore) and auto-advance to the next mesh with an unaccepted
anchor for the same pin (skip if none).

### 8.2 Violin axial pick (WP10)

In the violin chart, Shift+click on mesh M's column at signed distance d sets
`anchors[M] = { point = refAnchor + d·probeAxis; source = ViolinAxial; accepted = true }`.
Only available when the pin has correspondence enabled.

### 8.3 Heatmap diff mode (WP8)

Provenance heatmap gains a third radio `Diff`, enabled iff `pendingDelta` non-empty:
per-fragment signed change of combined error (preview − committed), diverging colormap
(blue = improved, red = degraded, neutral mid; do not use rainbow). Mask fragments where
`|Δ| < 1.96·sqrt(σ_ref² + σ_M²)` (σ from per-mesh dataset error metadata) — render those at
context/ghost level. Hover tooltip shows Δ and the LoD value. Mode auto-reverts to the
previous heatmap mode on commit/discard.

### 8.4 Rollback (WP11)

`↩ Roll back` on the newest RegStep: restore `transformBefore` for every mesh in
`outputs`, pop the step, fire the registration-complete invalidation cascade. Only the
newest step is roll-backable (older rows show the button disabled). Reset = roll back
repeatedly to empty log + identity transforms.

### 8.5 Guards (WP12)

- Coarse solve button disabled states with tooltips: no reference; <3 accepted pairs on all
  moving meshes.
- `collinearityWarning` from any solve → amber badge on that mesh's pending row.
- Anchors on hidden meshes persist; hidden meshes are not solved (visibility is semantic).
- Reference change mid-session: §3 behavior; RegSteps keep their recorded `referenceMesh`.
- Pending state survives nothing destructive: dataset switch is blocked during preview (§6).

## 9. Persistence (WP2, finalize in WP11)

Workspace JSON additions: `Pin.correspondence` (enabled + anchors incl. source/accepted),
`registrationLog` (full RegStep records). `pendingDelta` is **not** persisted. Loading a
workspace with these fields restores them; loading an old workspace without them yields
empty defaults (must not fail). Bump/handle any schema version marker the project uses.

## 10. Verification (WP13) — run all of this yourself

1. **Unit tests, server math:** weighted Umeyama — (a) random rigid transform on random
   point sets (n = 3, 4, 50) recovered to 1e-9 with uniform weights; (b) reflection case
   (near-planar set) yields det(R) = +1; (c) weights: duplicate a pair vs weight 2.0 gives
   identical transforms; (d) collinear points set `collinearityWarning`; (e) <3 pairs → 400.
2. **Unit tests, client:** commit/discard/rollback state machine (committed transforms,
   log length, cascade flags); effective-transform composition; workspace round-trip with
   and without new fields.
3. **Integration (server running, synthetic or sample dataset):** script the API flow —
   seed anchors via `/query/closest`, perturb one mesh's transform by a known rigid T,
   build pairs, call `/lsq-pairs`, assert recovered delta ≈ T⁻¹ within tolerance; then call
   `/query/icp` with that result as `initialTransform` and assert RMS decreases; call
   `/query/probe` with pre- and post-transforms and assert the moving mesh's median
   |distance| shrinks. Patch frame override: same frame in → co-oriented projections out
   (assert returned frame echoes request).
4. **Build:** client + server compile clean; run any pre-existing test suites; no
   regressions.
5. Final commit; summarize file-level changes and test results in `IMPLEMENTATION_NOTES.md`.

## 11. Fixed decisions (do not revisit)

Rigid 6-DOF, no scale. Correspondence extends the Point payload. LSQ solved server-side.
Star topology (each moving mesh vs reference, independent, parallel). Patch picker textured
by default with shaded toggle. Fine-without-coarse warns once, never blocks. Stage order
suggested, not enforced.
