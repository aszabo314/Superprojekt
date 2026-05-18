# Phase 1 Report — V5 Cleanup (§B of `scanpin_v6_architecture.md`)

Status: **complete**. Awaiting go-ahead before starting Phase 2.

## Commit order

| # | Commit | Section | Hash |
|---|--------|---------|------|
| 1 | Phase 1 setup: V5→V6 spec + working-reference mapping | (setup) | `309edc4` |
| 2 | Phase 1 / B.5: remove revolver disk | B.5 | `49a7526` |
| 3 | Phase 1 / B.3: remove core sample helper | B.3 | `0b08870` |
| 4 | Phase 1 / B.2: remove stratigraphy diagram | B.2 | `cadcc16` |
| 5 | Phase 1 / B.4 + B.1 + B.7: remove placement modes, cut plane, V5 flyout | B.4, B.1, B.7 | `5440cf7` |

`§B.6 — Summary mesh generation` had no source-code footprint
(zero matches for `[sS]ummary` under `src/`); the only mention was in
`description.md`'s "What the app is *not*" list. Recorded in
`docs/v5_to_v6_mapping.md`; no commit.

The original spec wording asks for "one V5 feature per commit where
reasonable." Items B.4, B.1, and B.7 are inseparable as deletions:
placement modes initialise a cut plane, the cut plane field on
`ScanPin` is read by the flyout controls and by the
`Sg`-level cut-plane rendering / drag handler, and the V5 flyout
controls *are* the cut-plane controls (Vertical / Horizontal /
+ Cut). Splitting them produced unstable intermediate states (would
have left `makePin` taking a `CutPlaneMode` argument with no caller
or vice versa), so they ship in one combined commit with the
commit message broken down per spec section.

## Shared utilities preserved as isolated modules

These are the V5 elements the spec calls out as "preserve the
*pattern*, delete the V5 caller" — left in place so Phase 2+ can
reuse them:

| Preserved | Location | Why |
|-----------|----------|-----|
| Floating-card pattern (drag / redock / collapse / close, world-anchored screen projection) | `Cards.fs:renderCards` + `CardSystemModel` in `ScanPinModel.fs` | V6 §D.6.4 floating pin card, §D.7 payload-specific cards |
| Card system data model (`Card`, `CardAttachment`, `CardContent`, `CardSystemModel`) | `ScanPinModel.fs` | Same. `CardContent`'s sole case renamed from `StratigraphyDiagram` to `PinCard` to carry forward without V5 semantics |
| Side-panel placement flyout (container, hide-on-idle, Commit / Discard row, slide-in animation) | `Gui.fs:placementFlyout` | V6 §D.6.4 anchor sphere adjustment flyout |
| Radius slider in flyout | `Gui.fs:placementFlyout` | First control in V6 §D.6.4 — kept literally as-is |
| `PinGeometry.axisFrame`, `appendPolylineSegments` | `PinGeometry.fs` | Generic orthonormal frame + line-segment-list helpers used by every prism / sphere primitive |
| `PinGeometry.buildCylinderHull`, `buildCylinderOutline` | `PinGeometry.fs` | The "translucent hull during adjustment" geometry — still rendered for `AdjustingPin` |
| `Cards.shortName`, `Cards.c4bToHex`, `Cards.parseFloat`, `Cards.checkedIf`, `Cards.niceTicks` | `Cards.fs` | Generic UI utilities. Used by `Gui.topBar` mesh list and `Gui.scaleBar`; reusable by V6 payload-card tick generation |
| `Cards.renderCards`'s `projectToScreen` / `computeCardPos` | `Cards.fs` | Anchor → screen projection for card positioning; unchanged from V5 |
| Mesh-tab `pinSection` (list of placed pins with focus / edit / delete) | `Gui.fs:pinSection` | Same UI pattern V6 §C.7 needs for anchor sphere list |
| `BlitShader.readArraySlice` | `Shader.fs` | Fullscreen mode (Space key) still uses it; revolver-only `readArraySliceColor` was deleted |
| `PlacementState`'s `AdjustingPin` case | `ScanPinModel.fs` | Destination state V6 §D.6.1 single-click placement will dispatch to |
| `RenderingMode` (Textured / Shaded / WhiteSurface), `ColorMode`, `MeshSolo`, `GhostSilhouette` | `Model.fs` | Scene-tab plumbing untouched by Phase 1 |
| `ExploreMode` and its toggle / sliders + heatmap shader | `Model.fs`, `Shader.fs`, `SceneGraph.fs` | Survives intact for V6 §D.4 dual-signal Explore |
| `Workspace clip` (`ClipBox` / `ClipBounds` / `ClipActive` + UI) | `Model.fs`, `Gui.fs:visTechSection`, shader | Survives for V6 §D.3 polygonal-lasso clip (rectangular box continues alongside the lasso) |

## Issues encountered

1. **Cards.fs unicode mixing.** The V5 cut-diagram and stratigraphy
   hover-tip strings mixed F# `↔` *escape literals* (in source)
   with raw UTF-8 multi-byte characters (`·` = U+00B7). The Edit tool's
   string matcher could not match either form against the file because
   the file uses CRLF line endings + UTF-8 + mixed escape/literal
   chars. Worked around by performing the deletion with a small
   Python regex pass (`re.subn` over the UTF-8 byte stream),
   preserving CRLF. Not a recurring problem.

2. **`PinCylinderDrag.isActive` shared state.** V5's cylinder-drag
   pointer handler set this `cval<bool>` to suppress orbit camera
   while the user was dragging the cut plane. With the cut plane
   gone there is no remaining consumer of orbit suppression. Removed
   along with the cylinder-drag handler in View.fs.

3. **`CardContent` losing its only case.** V5's `CardContent` union
   had one constructor: `StratigraphyDiagram of ScanPinId`. Removing
   stratigraphy would have left a zero-case union (illegal in F#).
   The spec explicitly preserves the floating-card pattern for V6
   §D.7, so the case was *renamed* `PinCard of ScanPinId` — a
   placeholder that carries forward the pin reference without
   semantic meaning. Phase 4 will add real payload-specific cases.

4. **`MeshView.cylClip` uniform.** V5 packed (ghost-clip,
   cut-plane) into a single `M44d` uniform sent to the per-mesh
   off-screen `BlitShader.clippy` pass. Deleting the per-pin ghost
   clip and the cut plane left nothing to send. Rather than touch
   the shader signature mid-Phase-1, the uniform is now driven by
   `AVal.constant M44d.Zero`; the shader's existing
   `if cyl.M00 <> 0.0` gate disables the cylinder-clip path on a
   zero matrix.

5. **No way to create a pin in the intermediate state.** With
   §B.4 removing the three placement gestures and Phase 2 not yet
   adding the V6 single-click gesture, the app currently has *no
   user-visible path to create a pin*. The Pins-tab list and the
   pin-card overlay still render for any pre-existing pins, and
   `AdjustingPin` + the Radius slider are still wired up — they
   simply have no creation gesture pointing at them. This is the
   intended Phase 1 end-state and is the natural seam for Phase 2's
   anchor-sphere placement (§D.6.1).

6. **Orphaned CSS.** A few dead style blocks remain
   (`.rank-section`, `.rank-controls`) that were never deleted in
   prior cleanups; they're unreferenced and harmless, left alone
   to keep the diff focused on V5 → V6 mappings.

Nothing escalated to the user — none of the deletions touched §A.2
non-goals or required design decisions beyond what the spec already
pinned down.

## §G smoke-test confirmation

- ✅ `dotnet build src/Superprojekt/Superprojekt.fsproj`: **0 errors**
  (50 warnings, all preexisting — 49 FShade `PropertySet on unknown
  expression` warnings on `op_Dereference` of `ref` cells; 1 stylistic
  `FS0066: This upcast is unnecessary` in `SceneGraph.fs:192`).
- ✅ `dotnet build src/Superserver/Superserver.fsproj`: **0 errors**.
- ✅ Adaptify regeneration of `ScanPinModel.g.fs` and `Model.g.fs`
  succeeded after each model-type change.
- ✅ No references to deleted types (`CutPlaneMode`,
  `StratigraphyData`, `BandCache`, `PlacementMode`,
  `ProfilePlacementState`, `PlanPlacementState`, `AutoPreview`,
  `AutoPlacementState`, `RevolverSettings`, `GhostClipMode`,
  `ExtractedLinesMode`, `CutAspectMode`, `CutLineHover`,
  `CutResult`, `StratigraphyColumn`, `StratigraphyDisplayMode`,
  `BetweenSpaceHover`, `PinCylinderDrag`) appear anywhere in
  `src/`.
- ⚠️ **Live browser smoke test** (dataset loads, scene renders,
  tabs render, no console errors) **not run by the agent** — the
  client is a Blazor WASM app that requires the server running
  on `localhost:5000` and a browser session. Both projects build
  to bytecode cleanly and adaptify generates without complaint;
  no DOM-shape regression is visible from inspection. The user
  should `dotnet run --project src/Superserver` and confirm in
  a browser before signing off Phase 1.

## What survives the user actually sees

- Top bar: hamburger, dataset dropdown, Explore toggle, reset-camera,
  coord readout, gear popover (Reference Axis, Camera speed, dataset
  metadata, debug log).
- Side panel: Meshes section (per-mesh visibility / solo / focus,
  All / None bulk, Rendering Textured/Shaded/White, Ghost silhouette
  toggle); Pins section (empty until V6 placement returns);
  Visualization section (Difference rendering range, Clipping box
  XYZ ranges).
- Pre-existing pins (if any) still render as anchor markers in 3D;
  a placeholder floating card opens on selection. No cut diagram,
  no stratigraphy diagram, no core sample, no revolver, no Profile /
  Plan / Auto mode buttons. Esc cancels in-flight adjustment, Space
  toggles fullscreen mode.

## Pause request

Phase 1 is complete. Phase 2 (Anchor Sphere primitive, §D.6) is
**not started** — awaiting explicit user go-ahead per the prompt's
"After producing the report, **pause and wait for explicit go-ahead**
before starting Phase 2" directive.
