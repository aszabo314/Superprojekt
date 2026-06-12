# Registration Workflow Panel — implementation notes

Working notes for `ScanPin_workflow_panel_spec.md`. Updated per work package (`RWP<n>` commits).

## RWP0 — repo findings (verified 2026-06-12)

| Spec concern | Finding |
|---|---|
| Tool-panel pattern | Floating draggable cards with shared chrome (`Cards.cardDragHandle` / `cardPos` / `cardStyle`), position held in local `cval`s, **no docking and no exclusivity** — cards coexist; close = titlebar `×` or the toggle. Open state: model field for panels with reducer interactions (`PanoramaOpen` + top-bar toggle) vs local `cval` (registration card's edge toggle button). The new panel follows the panorama pattern (model field — nav actions must open it from the reducer). |
| Camera animation utility | `OrbitMessage.SetTargetCenter(user, AnimationKind, V3d)` + `SetTargetRadius(user, float)` animate centre/radius (Tanh/Exp easing, ~350 ms); orbit state keeps `phi/theta` unchanged → "keep orientation" is free. Any pointer/wheel input overrides the animation (existing `userModified*` machinery). Camera state lives in `Model.Camera : OrbitState` (render space). |
| Readiness logic today | Inline in `GuiCards.registrationCard`: one `AVal.custom` (pins → accepted pair counts per visible moving mesh, `RegConditioning.spreadEigenvalues`/`lambdaRatio` pre-check, pin summaries) + `canSolveCoarse` + `coarseTooltip` + the conditioning badge classes. Extracted in RWP2. |
| Solve responses | `Update.updateCore` `CoarseSolved` / `FineSolved` (per-mesh messages counting down `PendingReg.Expected`). `Query.lsqPairs` already returns the conditioning eigenvalues — currently discarded at the call site (`_eigen`); RWP1 threads them through `CoarseSolved` into `lastSolve`. |
| Sparkline renderer | `GuiCards.spark` (unicode ▁▂▃▄▅▆▇, private) — made public and reused. |
| Pin display name | Pins have no names; the panel uses the same short centre-coordinate label as the pin list / registration card. |

## Spec ↔ codebase reconciliations

1. **`ui.workflowPanelOpen`** → `Model.WorkflowPanelOpen : bool`, session-only (matching `PanoramaOpen`/`MenuOpen`, which are not persisted — §6 "match existing behavior"). `lastSolve` *is* persisted (explicit in §1), workspace version bumped to 3; older files default it empty.
2. **Registration card open state promoted to the model** (`RegistrationCardOpen`): it was a view-local `cval`, but §5's `FocusRegistrationCard` nav action must open the card from the reducer. The edge toggle button and card chrome behave exactly as before.
3. **Readiness engine is pure over a dedicated input DTO** (`ReadinessInput`, built by `Primitives.ReadinessView.input`) rather than over `Model` directly — `Model` drags in WASM-only camera dependencies, and §8.1 requires table-driven unit tests in Supertests (which compiles `RegistrationModel.fs` directly).
4. **`FlyTo` carries the viewport aspect ratio** (`FlyTo of FlyToTarget * aspect`): the reducer has no access to the render-control size, and fovY (the spec's formula input) derives from the fixed 90° *horizontal* fov + aspect. The math lives in pure `FlyToMath` (unit-tested); targets are world-space and converted at the reducer boundary.
5. **`OpenAnchorReview of meshFilter`** re-runs the existing auto-seed flow (Auto/unaccepted anchors only — the established semantics) and filters the review modal's rows via a transient `Model.AnchorReviewFilter`, cleared on apply/cancel.
6. **Pulse helper** is a JS one-liner (`window.SuperPulse(selector)`: add class, remove after 1.5 s) invoked through `JSRuntime` from the nav-action handler — same pattern as `SuperWorkspaceSave`. No pulse state enters the Elm model.
7. **"Skipped" chip**: derived as "a solve response batch exists (`lastSolve` non-empty for sibling meshes) but this visible moving mesh has no entry and <3 accepted pairs" — the spec's "last solve ran but mesh had <3 pairs" without storing extra per-batch state.
8. **Keyboard shortcut** (§7 "if a scheme exists"): no panel shortcut scheme exists — none added.

9. **Anchor dots encode accepted / seeded / missing** — the spec's "flagged" state only exists inside the review modal (`AnchorCandidate.Decision`); a resting anchor carries no flag, so a missing anchor wears the red ring. Dot tooltips name the mesh (no per-dot click, §9).
10. **§8.4 view-level selector tests** run as DOM assertions inside the browser integration (chips, dots, n/M, pairs line, history one-liner, stats values) — there is no UI harness, and the selectors are view closures over `AdaptiveModel` that Supertests cannot compile.

## Per-WP status

- **RWP0** ✅ this file.
- **RWP1** ✅ `LastSolveEntry` map (+ RegJson round-trip, workspace v3, eigenvalues threaded through `CoarseSolved`, cleared on rollback/reset/study-reset), `WorkflowPanelOpen`, `RegistrationCardOpen` promoted to the model.
- **RWP2** ✅ `Readiness.compute` over `ReadinessInput` + `Primitives.ReadinessView` adapter; registration card readiness line / conditioning badge / pin rows / solve gating refactored onto it (equivalent output; solving is now additionally blocked while a preview is pending, per §2).
- **RWP3+6** ✅ `GuiWorkflow.fs` — four collapsible sections per §3, floating-card chrome, all toggles dispatch existing messages, derived selectors as single `AVal.custom`s over individual leaves.
- **RWP4** ✅ `FlyTo` message + pure `FlyToMath`; orbit centre/radius animated, orientation kept, input overrides.
- **RWP5** ✅ `NavTo` routing (anchor-review re-seed with mesh filter, select-pin-open-card, registration-card focus per section, reference highlight, solve/commit/discard passthrough) + `window.SuperPulse`.
- **RWP7** ✅ top-bar ⚲ Workflow toggle, `workflowPanel` feature id (gated; absent from glacier-v1 phases).
- **RWP8** ✅ below.

## Verification results (2026-06-12)

- **Builds**: `dotnet build Superprojekt.sln` — 0 errors.
- **Unit** (`dotnet run --project src/Supertests`): **138/138 passed** (103 prior + 35 new). Readiness: no-reference blocker in both stages with highlight action, zero-pins blocker, per-mesh pair-deficit blockers with filtered-review actions and exact gap counts, exactly one Ready per stage when clear (coarse → RunCoarse; fine only after a committed step, naming the ICP mode), fine Info before any commit, unresolved-anchor warnings with open-card actions, collinear synthetic anchors warn while spread ones don't, pending blocks both stages and kills the Ready entries. Fly-to: fovY and distance match closed forms, bounds → bounding sphere, degenerate radii clamped. lastSolve: JSON round-trip (coarse with conditioning + per-pin residuals, fine without), empty round-trip, rollback clears exactly the producing step's meshes.
- **Integration** (puppeteer against :8002, Hessigheim): **26/26 passed** — panel opens from the top bar; initial diagnostics all blockers; ★ from the panel row syncs the Reference chip; three pins placed/committed; the other-pins footer enables correspondence per pin (auto-seed review accepted each time); pairs line reaches 3/3 per mesh, six anchor dots filled; "Ready for coarse solve" appears and its ▶ runs the solve; pending banner shows per-mesh RMS before→after; panel Commit flips both moving meshes to Coarse ✓ with numeric last-RMS, history one-liner `#1 coarse … (1 step)`, stats rows + `meshes solved 2/2` aggregate; fine stage flips to Ready; rollback via the card reverts chips to Unregistered, clears the lastSolve RMS column to "—" and empties the history line; mesh ⌖ fly-to runs without page errors. Registration suite 19/19 and study suite 26/26 still green.
