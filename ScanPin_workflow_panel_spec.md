# Implementation Spec — Registration Workflow Panel

Audience: autonomous coding agent. Implement end-to-end; verify per §8; decisions in §9
are final. Builds on the landed registration workflow (correspondence pins, staged
solve, pending preview, history) and the existing top-bar tool-panel pattern.

## 0. Context & WP0 (first)

Explore the repo and record in `IMPLEMENTATION_NOTES.md`:
- How existing top-bar tools open/dock their workflow panels (chrome, docking side,
  exclusivity, close behavior) — the new panel must follow this pattern exactly.
- Whether a camera fly-to/animation utility exists; if not, note where camera state
  lives in the model.
- Where the registration-card readiness logic (pair counts, conditioning pre-check)
  currently lives — it will be extracted and shared (§2).
- Where coarse/fine solve responses are handled — their diagnostics must be persisted
  (§1).

Constraints: the panel is a **pure view over the model** — it must never issue server
queries on its own. Reuse existing row/badge/button chrome. After each work package:
clean build, tests green, commit `RWP<n>: <summary>`.

## 1. Model additions (WP1)

```
// persist last solve diagnostics (currently transient in the solve handlers)
lastSolve : Map<MeshName, { stage : Coarse|Fine
                            rmsBefore : float; rmsAfter : float
                            conditioning : { eigenvalues : float[3]
                                             collinearityWarning : bool } option
                            perPinResiduals : Map<PinId, float> option   // coarse only
                            timestamp : DateTimeOffset }>

ui.workflowPanelOpen : bool
```

`lastSolve` updates on every solve response (pending or committed); cleared per mesh on
rollback past the step that produced it. Workspace persistence: include `lastSolve`
(versioned, defaults empty on old files).

## 2. Readiness engine (WP2)

Extract into one pure function, shared by this panel and the registration card
(replace the card's inline logic with calls to this — single source of truth):

```
type Severity = Blocker | Warning | Ready | Info
type Diagnostic = { severity : Severity
                    text : string                      // human-readable, actionable
                    action : NavAction option }        // §5

readiness : Model -> { coarse : Diagnostic list; fine : Diagnostic list }
```

Rules (evaluated in order, all matching emitted):
- No reference designated → Blocker "Designate a reference mesh (★)" → action: open
  mesh section / highlight ★ column.
- No correspondence-enabled pins → Blocker "Enable correspondence on ≥3 pins" → action:
  focus pin section.
- Per visible moving mesh with accepted pairs `< 3` → Blocker
  "<mesh>: needs <3−n> more accepted anchor(s)" → action: open the anchor-review modal
  filtered to that mesh's unaccepted/missing anchors.
- Per pin with unaccepted/flagged anchors → Warning "Pin <name>: <k> anchor(s)
  unresolved" → action: select pin + open its card at the correspondence section.
- Client-side collinearity pre-check on accepted reference anchors (λ2/λ1 < 1e-3) →
  Warning "Pins near-collinear — rotation weakly constrained" (lists affected meshes).
- All coarse blockers clear → Ready "Ready for coarse solve" → action: trigger coarse
  solve.
- Fine list: no committed step at all → Info "Run coarse first (recommended)"; otherwise
  Ready "Ready for fine ICP (<mode>)" → action: trigger fine solve. Pending exists →
  Blocker "Commit or discard the pending result first" (both lists) → action: focus
  pending block.

## 3. Panel layout (WP3)

Top-bar tool **"Registration workflow"** (icon: linked-anchors glyph consistent with the
icon set). Panel uses the standard tool-panel chrome/docking from WP0. Four stacked
sections, each collapsible, default all expanded:

### 3.1 Meshes
One row per loaded mesh: colour swatch · name · ★ reference toggle (single-select,
two-way synced with mesh panel + registration card) · visibility eye (synced) · status
chip · last RMS (after-value from `lastSolve`, "—" if none) · frame-camera button (⌖).

Status chip values: `Reference` / `Fine ✓` (fine step committed) / `Coarse ✓` (coarse
only) / `Unregistered` / `Skipped` (last solve ran but mesh had <3 pairs) / `Hidden`
(greyed row). Conditioning warning from `lastSolve` renders an amber badge on the chip.

### 3.2 Correspondence pins
One row per correspondence-enabled pin: pin swatch + name · host mesh ·
**anchor dots** — one dot per visible moving mesh in mesh colour: filled = accepted,
hollow = seeded/unaccepted, red ring = flagged or missing · count `n/M` (accepted /
visible moving meshes) · reliability value · last coarse residual for this pin
(max over meshes, from `lastSolve.perPinResiduals`, "—" if none) · enabled toggle ·
open-card button.

Collapsed footer row "Other pins (k)" expands to non-correspondence pins, each with an
"enable correspondence" quick action (triggers the normal enable + auto-seed flow).

Header aggregate: `pairs per mesh: <mesh: n> …` (the same numbers the readiness engine
uses).

### 3.3 Registration status
- **Pending banner** (iff pending exists): "Previewing <stage> result" + per-mesh
  RMS before→after one-liner + Commit / Discard buttons (same messages as the card).
- **Diagnostics list:** rendered from `readiness` — blockers first (red), warnings
  (amber), then the Ready entry (green) with its action as a primary button. This is the
  literal "what exactly is missing" element; when nothing is missing it reads
  "Ready for coarse solve [▶]" / "Ready for fine ICP [▶]".
- **History summary:** last committed step one-liner (`#2 fine ICP region ·
  RMS 0.041→0.012`) + step count + "open history" (focuses the registration card's
  history block). If log empty: "No registration committed yet."

### 3.4 Error stats
- Table: per moving mesh — last committed RMS before → after, Δ%, stage reached.
- Aggregate line: mean / max current RMS across solved meshes · meshes solved `s/M`.
- Sparkline per mesh: RMS-after across committed steps (data from `registrationLog`),
  rendered with the existing inline-sparkline component (the ICP convergence sparkline's
  renderer, reused).

## 4. Camera fly-to (WP4)

`FlyTo of target : Bounds | Sphere` message:
- Pin row click (anywhere except buttons/toggles) → select pin + `FlyTo(pin sphere)`.
- Mesh ⌖ button → `FlyTo(mesh world bounds)`.
- Animation: keep current orientation; move camera position so the target subtends
  ~25 % of the viewport height (sphere: distance = radius / tan(fovY·0.125); bounds:
  bounding-sphere equivalent); interpolate position + lookAt over 0.5 s ease-in-out
  using the existing animation utility (WP0) or a per-frame lerp hooked into the render
  loop if none exists. Any user navigation input cancels the animation.

## 5. Navigation actions (WP5)

`NavAction` cases used by diagnostics (§2) and rows: `OpenAnchorReview of meshFilter` ·
`SelectPinOpenCard of pinId * scrollTo:Correspondence` · `FocusRegistrationCard of
Stage|Pending|History` · `HighlightReferenceColumn` · `RunCoarse` · `RunFine` ·
`CommitPending` · `DiscardPending`. Implement as messages routed to existing handlers;
focus/highlight = open the target panel/card if closed + 1.5 s pulse outline on the
target element (one reusable pulse helper).

## 6. Sync & update rules (WP6)

- All toggles (★, eye, correspondence enable) dispatch the **existing** messages; the
  panel renders model state only — no duplicated state.
- Panel contents update live on: solve responses, commit/discard/rollback, anchor
  set/accept, pin add/delete/enable, visibility/reference changes (automatic in
  Elm-style render; ensure derived selectors are memoized if the view layer needs it —
  follow existing practice from WP0).
- Panel open state persists in the workspace UI section alongside other panel states
  (if the project persists UI state; else session-only — match existing behavior).

## 7. Integration (WP7)

- Top bar: button placed with the other tool buttons; exclusivity/docking per WP0
  findings; keyboard shortcut consistent with neighbors if a scheme exists.
- Study mode: add feature id `workflowPanel` to the gating list. Default study config:
  not in any phase's `allowedFeatures` (running study design unchanged); demo/Full mode
  unaffected.
- Registration card keeps its full functionality; its readiness line now renders from
  the shared engine (§2) — visual output must remain equivalent (snapshot/golden test if
  harness exists).

## 8. Verification — run everything yourself

1. **Unit, readiness engine:** table-driven cases — no reference; 0/2/3 pins; per-mesh
   pair counts 2 vs 3; flagged anchors; collinear anchors (synthetic colinear points
   trigger warning, spread points don't); pending blocks both stages; fine-before-coarse
   yields Info; all-clear yields exactly one Ready per stage.
2. **Unit, fly-to math:** sphere/bounds → camera distance for fixed fovY matches
   closed-form expectation; orientation preserved.
3. **Unit, model:** `lastSolve` set on solve response, survives commit, cleared on
   rollback past producing step; workspace round-trip with/without `lastSolve`.
4. **View-level (or model-level if no UI harness):** panel selectors produce correct
   rows for a constructed model: chip per stage, anchor dot states (accepted/seeded/
   flagged), `n/M` counts, aggregate pairs line, history one-liner, stats table values.
5. **Integration:** scripted flow against running server — enable correspondence on 3
   pins, accept anchors, assert readiness transitions Blocker→Ready; run coarse via the
   panel's Ready action; assert pending banner content; commit; assert chips/RMS/history
   update; rollback; assert reversal.
6. Build clean; registration card behavior unchanged (existing tests pass); summarize
   in `IMPLEMENTATION_NOTES.md`.

## 9. Fixed decisions (do not revisit)

Panel is read-mostly: it never issues server queries; all mutations go through existing
messages. Single shared readiness engine; card refactored onto it. Anchor dots encode
accepted/seeded/flagged only (no per-dot click action in v1; dot tooltip names the
mesh). Row click = select + fly-to; buttons do everything else. Sparkline reuses the
existing renderer. Feature id `workflowPanel`, excluded from the current study config.
Fly-to keeps orientation (no rotation animation in v1).
