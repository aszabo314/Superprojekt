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
3. **Readiness engine is pure over a dedicated input DTO** (`ReadinessInput`, built by `Readiness.ofModel`) rather than over `Model` directly — `Model` drags in WASM-only camera dependencies, and §8.1 requires table-driven unit tests in Supertests (which compiles `RegistrationModel.fs` directly).
4. **`FlyTo` carries the viewport aspect ratio** (`FlyTo of FlyToTarget * aspect`): the reducer has no access to the render-control size, and fovY (the spec's formula input) derives from the fixed 90° *horizontal* fov + aspect. The math lives in pure `FlyToMath` (unit-tested); targets are world-space and converted at the reducer boundary.
5. **`OpenAnchorReview of meshFilter`** re-runs the existing auto-seed flow (Auto/unaccepted anchors only — the established semantics) and filters the review modal's rows via a transient `Model.AnchorReviewFilter`, cleared on apply/cancel.
6. **Pulse helper** is a JS one-liner (`window.SuperPulse(selector)`: add class, remove after 1.5 s) invoked through `JSRuntime` from the nav-action handler — same pattern as `SuperWorkspaceSave`. No pulse state enters the Elm model.
7. **"Skipped" chip**: derived as "a solve response batch exists (`lastSolve` non-empty for sibling meshes) but this visible moving mesh has no entry and <3 accepted pairs" — the spec's "last solve ran but mesh had <3 pairs" without storing extra per-batch state.
8. **Keyboard shortcut** (§7 "if a scheme exists"): no panel shortcut scheme exists — none added.

## Per-WP status

- **RWP0** ✅ this file.

## Verification results

(pending RWP8)
