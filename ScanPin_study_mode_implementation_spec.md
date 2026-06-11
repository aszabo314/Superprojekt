# Implementation Spec — User Study Mode

Audience: autonomous coding agent. Implement end-to-end; do not ask for confirmation;
verify your own work (§12). All design decisions are final (§13). Builds on the landed
registration workflow (correspondence pins, coarse/fine solve, pending preview, commit/
rollback, registration log, workspace persistence).

## 0. Context & ground rules

- Stack: Blazor-WASM Elm-style client + ASP.NET/Giraffe server, WebGL2, Aardvark.
  Server `http://localhost:5000`. No database — file-based stores only.
- **WP0 (first):** explore the repo. Locate: app root view/routing, top bar, gear
  popover, mesh panel, registration card, pin card + violin chart, provenance heatmap
  mode state, split-violin preview, three-source bar, message/update loop, workspace
  (de)serializer, server routing. Record paths in `IMPLEMENTATION_NOTES.md`; update per WP.
- Preserve all existing conventions (world-space queries, parallel requests, 250 ms
  debounce + cancellation, depth-gated picking, invalidation cascade).
- After each work package: clean build, all tests green, commit `SWP<n>: <summary>`.

## 1. App modes & routing (WP1)

```
type AppMode = Full | Study of StudySession
type StudySession = { sessionId : string; token : string; condition : Full|Num
                      demo : bool; config : StudyConfigPublic; runtime : StudyRuntime }
```

- Route `/` → Full (unchanged app). Route `/s/{token}` → Study: client calls
  `POST /api/study/session` (§3) and enters study mode; on failure show a static error
  page (invalid/expired token).
- Gear popover (Full mode only) gains **"Preview study mode"**: condition picker
  (FULL/NUM) + study picker → enters Study with `demo = true`. Demo sessions: an
  **Exit study** button is shown (returns to Full, resets scene); all telemetry/answers
  tagged `demo: true`.
- Real (non-demo) study sessions render no navigation back to Full (no gear, no top-bar
  except the study bar, §5).

## 2. Study configuration (WP2)

Server-side per study, two files:

- `studies/{studyId}/config.json` — **public** (served to client minus planted answers):

```
{ studyId, title, datasetTutorial, datasetMain,
  conditions: { FULL: { disabledFeatures: [] },
                NUM:  { disabledFeatures: ["violinChart","heatmap","heatmapDiff",
                                           "threeSourceBar","splitViolinPreview"] } },
  phases: [ Phase ],
  questionnaires: { sus: [...10 items], tlx: [...6 scales], icet: [...items] } }

Phase = { id, title, goalLine,                       // goalLine always visible in study bar
          dataset?: "tutorial"|"main",               // switching handled by runtime
          allowedFeatures: [string],                 // UI whitelist for this phase
          steps: [ Step ] }

Step = { id, kind: "instruction"|"guidedAction"|"question"|"questionnaire",
         body: string (markdown),
         anchor?: string,                            // element id for guided tooltip
         completion: Predicate,                      // see §6
         question?: Question }

Question = { id, type: "singleChoice"|"sceneClick"|"numeric"|"freeText"|"likertGrid",
             options?: [string], unit?: string, confidence: bool,
             gold?: bool }                           // gold flag public; answers are not
```

- `studies/{studyId}/secret.json` — **never served**: planted answers keyed by question
  id (incl. defect-region polygon for sceneClick scoring, true change values), hidden
  check-point pairs `{ stable: [...], moving: [...] }` per moving mesh, gold pass
  thresholds.
- Startup validation: every step's question/anchor/predicate references resolve; every
  gold question has a secret answer; both datasets exist. Invalid config → server
  refuses to serve that study (log reason).

## 3. Server endpoints & stores (WP3)

File stores under `studies/{studyId}/data/`: `sessions.jsonl` (one record per session:
id, token, condition, demo, createdAt, status), per-session `events-{sid}.jsonl`,
`answers-{sid}.jsonl`, `workspace-{sid}.json`, `scores-{sid}.json`. Append-only; writes
serialized per session (lock per sid).

```
POST /api/study/session            { token }                → { sessionId, condition, configPublic }
POST /api/study/{sid}/events       { events: [Event] }      → 204     (batch append)
POST /api/study/{sid}/answers      { questionId, value, confidence? } → 204
POST /api/study/{sid}/transforms   { label, perMesh: {mesh: M44} }    → 204
POST /api/study/{sid}/workspace    { workspaceJson }        → 204
POST /api/study/{sid}/advance      { phaseId, stepId }      → 204     (server-side progress mirror)
GET  /api/study/{sid}/complete                              → { code } | 409 if required steps missing
```

- **Condition assignment:** balanced — count non-demo sessions per condition with
  status ∈ {active, completed}; assign the smaller (tie → random). Atomic under the
  store lock.
- **Scoring:** on every `transforms` post, compute TRE against secret check points
  (stable and moving separately) and append to `scores-{sid}.json` with the label
  (e.g. `commit#2`, `final`). **Never return scores to the client.**
- **Completion code:** HMAC-SHA256(serverSecret, sid) truncated to 8 chars; issued only
  if all non-optional steps have `advance` records and a `final` transforms post exists.
- Secret file and scores are never reachable via any route (add a route test, §12).

## 4. Client study runtime (WP4)

```
StudyRuntime = { phaseIx : int; stepIx : int
                 answersDraft : Map<QuestionId, AnswerDraft>
                 goldFails : int
                 stepSatisfied : bool          // predicate state for current step
                 eventQueue / batcher state }
```

- Reducer rules: **Next** enabled iff `stepSatisfied` (instruction steps: satisfied on
  render; guidedAction: predicate; question: answer present incl. confidence if
  required; questionnaire: all items answered). Next → post `advance`, move to next
  step/phase; phase change may switch dataset (tutorial ↔ main) via existing dataset
  load path, then restores study chrome.
- No back navigation between phases; within questionnaires, items are editable until
  Next.
- Gold check evaluation is **server-side only** (client never sees correctness);
  exception: tutorial gold checks must gate progress, so for tutorial steps only,
  `answers` response returns `{ correct: bool }`; 2 fails on one check → show the
  relevant tutorial step again; 3rd fail → polite screen-out page (session status
  `screened`).

## 5. Reduced UI shell (WP5)

Replace normal chrome entirely in Study mode; reuse existing primitives underneath.

- **Study bar (top, always):** progress dots (one per phase, current highlighted),
  phase title, **goalLine** (single sentence, always visible — "the current goal"),
  `?` button (reopens current step's instruction overlay), Next button (state per §4).
  Demo only: condition badge + Exit.
- **Instruction overlay:** dim-background panel rendering the step `body` (markdown);
  for `guidedAction` steps with `anchor`, render instead as a **tooltip card pointing
  at the anchored element** (existing popover/card chrome), non-blocking, with a live
  checkmark when the predicate fires.
- **Task pane (right, dockable card chrome):** question forms for the current step —
  components in §7.
- **Feature gating:** single function `featureVisible(featureId, mode)` consulted by
  views; in Study mode = `phase.allowedFeatures ∩ ¬condition.disabledFeatures`.
  Hidden: hamburger/left panel (unless allowed), lasso, fusion, pano, dataset dropdown,
  save/load, retarget, gear (real sessions). Update-level guard: messages originating
  from gated features no-op + toast "not available in this step". Feature ids (fixed):
  `navigation, layerCycle, pinPlace, pinEdit, pinCard, violinChart, hoverProbe,
  heatmap, heatmapDiff, threeSourceBar, splitViolinPreview, registrationCard,
  coarseSolve, fineSolve, commit, rollback, meshPanel, errorMetadata, contactRings`.
- **NUM condition:** `violinChart` hidden → pin card shows an "RMS table" block instead
  (per-mesh RMS before/after from the registration card data); `heatmap`/`heatmapDiff`/
  `threeSourceBar`/`splitViolinPreview` hidden. Solver mechanics untouched.

## 6. Predicate engine (WP6)

Telemetry events (§8) double as the predicate input stream.

```
Predicate = Event of eventType * minCount
          | And of [Predicate] | Or of [Predicate] | Seq of [Predicate]
          | AnswerSubmitted of questionId
```

Evaluated incrementally in the update loop per step (reset on step entry, except `Seq`
progress). Examples used by the default config: `Event("orbit",1)`,
`Event("layerCycled",1)`, `Event("pinCommitted",3)`, `And[Event("coarseCommitted",1)]`,
`Seq[Event("solveAlternativeRun",1); Event("rollbackUsed",1); Event("finalRestored",1)]`.

## 7. Question & questionnaire widgets (WP7)

- **singleChoice:** radio list (+ optional 7-pt confidence row).
- **sceneClick:** "Mark in scene" button → one-shot depth-gated click mode (reuse pin
  placement picking sans pin); stores world point; renders a flag marker; re-click
  replaces.
- **numeric:** number input + unit label + 7-pt confidence.
- **freeText:** textarea, min length configurable (default 0).
- **likertGrid:** SUS (10×5-pt), Raw-TLX (6 sliders 0–100), ICE-T (config items, 7-pt).
- All answers post immediately on change (idempotent upsert by questionId) and again on
  Next (final value wins server-side by timestamp).

## 8. Telemetry (WP8)

Event = `{ t (ms since session start), type, payload }`. Types (fixed list):

`sessionStart, phaseEnter, stepEnter, stepComplete, orbit (throttled 5 s), zoom
(throttled), layerCycled, soloToggled, meshVisToggled, pinPlaced, pinCommitted,
pinDeleted, anchorSet {pinId, mesh, source}, anchorAccepted, correspondenceToggled,
coarseSolved {rmsBefore, rmsAfter, perMesh}, fineSolved {…}, previewShown,
committed {stage}, rolledBack, discarded, heatmapMode {mode}, cardOpened {pinId},
cardClosed, chartHover (throttled 5 s), questionShown, answerChanged {questionId},
flagMarked {questionId, point}, fpsSample (every 30 s), error {message}`

- Batcher: flush every 5 s or 50 events; immediate flush on phaseEnter/stepComplete and
  on page hide/unload (beacon-style best effort). Retry with exponential backoff; queue
  bounded (drop oldest throttled-type events first).
- On `committed` and on entering Phase F, also post current per-mesh transforms via
  `/transforms` (labels `commit#n`, `final`).

## 9. Default study config content (WP9) — the story walkthrough

Author `studies/glacier-v1/config.json` with this structure (instruction bodies as
concise placeholder English; final copy edited by the researcher later):

- **P0 Onboarding** (`dataset: tutorial`; allowed: navigation, layerCycle, pinPlace,
  pinCard, violinChart*, registrationCard, coarseSolve, fineSolve, commit)
  1. instruction: hardware notice + story framing (§3 of the study design).
  2. guidedAction anchor=viewport: "orbit and zoom" — `Event(orbit,1) && Event(zoom,1)`.
  3. guidedAction anchor=viewport: "cycle overlapping layers" — `Event(layerCycled,1)`.
  4. guidedAction anchor=pinButton: "place and commit a pin" — `Event(pinCommitted,1)`.
  5. guidedAction anchor=pinList: "open its card" — `Event(cardOpened,1)`.
  6. question gold T1 (singleChoice, chart reading; NUM variant asks RMS-table reading).
  7. guidedAction anchor=registrationCard: guided coarse solve + commit —
     `Seq[Event(coarseSolved,1); Event(committed,1)]`.
  8. questions gold T2, T3 (singleChoice).
- **P1 Inspect** (`dataset: main`; allowed: navigation, layerCycle, meshPanel, pinPlace,
  pinCard, hoverProbe, violinChart*, heatmap*)
  1. instruction: the three epochs, the moving tongue, the stable bedrock.
  2. question QA1 singleChoice gold + confidence.
  3. question QA2 sceneClick + confidence.
- **P2 Register** (allowed: + registrationCard, coarseSolve, fineSolve, commit,
  pinEdit, contactRings, splitViolinPreview*)
  1. instruction: stable-terrain principle (one line) + goal "≥3 pins on bedrock,
     coarse then fine, commit both".
  2. guidedAction: `Seq[Event(pinCommitted,3); Event(coarseSolved,1);
     Event(committed,1); Event(fineSolved,1); Event(committed,2)]`.
     (Soft warning toast if a correspondence pin centre lands inside the moving-region
     polygon from configPublic — polygon is coarse and non-secret; event `pinInMoving`.)
- **P3 Evaluate** (allowed: + heatmapDiff*, threeSourceBar*)
  questions QC1 (singleChoice rank via ordered select), QC2 gold, QC3 gold, each with
  confidence.
- **P4 Alternatives** (allowed: + rollback)
  1. instruction: colleague's all-terrain suggestion.
  2. guidedAction: `Seq[Event(solveAlternativeRun,1); Event(rolledBack|committed)…]`
     — concretely: one more solve of the other ICP mode + a final state restore;
     completion = `And[Event(fineSolved,2); Event(finalRestored,1)]` where
     `finalRestored` fires when history depth changes after the second solve (emit it
     from the rollback/commit handler when the user confirms "Set as final" — add a
     **Set as final** button to the registration card, visible only in study mode,
     which posts `final` transforms and emits the event).
  3. question QD1 freeText (min 20 chars).
- **P5 Measure change** (allowed: navigation, pinPlace, pinCard, violinChart*)
  Two flag markers pre-rendered from configPublic. Per flag: numeric question (metres,
  signed) + confidence. Then QE-final freeText insight question.
- **P6 Exit:** questionnaire sus, tlx, icet; demographics (singleChoice items);
  final instruction step shows completion code fetched from `/complete`.

`*` = present in FULL, auto-removed in NUM by the condition filter.

## 10. Workspace & persistence interplay (WP10)

- Study mode disables manual save/load; the system still auto-uploads the workspace
  JSON at `final` (Phase P5→P6 transition) via `/workspace`.
- Study session state (phase/step, answers draft) is held in memory only; a page reload
  re-creates the session? **No:** on reload, client re-posts the token; server finds the
  existing active session for that token and returns it with last `advance` position;
  client resumes at that step with a fresh scene and an instruction overlay "your
  progress was kept; the 3D scene was reset". (Pins/transforms are lost on reload —
  acceptable; the event log records `sessionResumed`.) One token = one session;
  completed/screened tokens get a "study already completed" page.

## 11. Link generation utility (WP11)

CLI or admin endpoint (localhost-only): `POST /api/study/{studyId}/tokens { n }` →
n fresh random tokens appended to `tokens.jsonl` (token, createdAt, used flag). Session
creation validates token exists and is unused (demo bypasses tokens).

## 12. Verification (WP12) — run everything yourself

1. **Unit:** predicate engine (Event/And/Or/Seq incl. reset semantics); reducer gating
   (Next disabled/enabled per step kind); balanced assignment under 100 concurrent
   simulated session creations (counts differ by ≤1); HMAC completion code; config
   validation rejects: dangling question ref, missing secret answer, unknown feature id;
   TRE scoring against synthetic transforms with known answer (tolerance 1e-9).
2. **Route security tests:** `secret.json` and `scores-*` unreachable via any HTTP
   route; configPublic response contains no `secret`/planted-answer fields (assert by
   key scan).
3. **Integration (server running, scripted client):** create session via token → walk
   the entire default config as FULL by posting synthetic events/answers/transforms in
   order → assert: out-of-order `advance` rejected or ignored, gold tutorial fail path
   screens out after 3 fails (separate session), `complete` 409s before final
   transforms, completion code issued after; repeat as NUM and assert configPublic's
   resolved feature set excludes the starred features. Resume test: re-post token
   mid-study → same session, correct position.
4. **Client build smoke:** app compiles; `/` renders Full mode unchanged (no study
   chrome); demo entry from gear popover reaches P0 step 1 (verify via a UI-model-level
   test if a harness exists, else via manual-equivalent programmatic model construction
   in tests).
5. Final commit; summarize changes + test results in `IMPLEMENTATION_NOTES.md`.

## 13. Fixed decisions (do not revisit)

Two conditions only (FULL/NUM). File-based JSONL stores, no DB. Balanced assignment by
active+completed count. Scores never sent to the client; tutorial gold correctness is
the single exception. One token = one session, resumable, scene not restored. Demo mode
flagged, token-free, exits freely. Feature gating ids as listed in §5. Instruction copy
= placeholder English in config, edited later. "Set as final" button exists only in
study mode. No back navigation across phases.
