# ScanPin V4 — Explore Mode

Implemented. See CLAUDE.md "Explore mode heatmap" for the architecture (offscreen Rgba8 + sampled in `readArray`, normals reconstructed from depth gradients).

`VarianceThreshold` default is auto-derived on `ClipBoundsLoaded` from the union bbox diagonal scaled by the active dataset scale: `clamp 1e-6 1e-2 ((diag * 1e-4)^2)`. User can override via the slider.

`ReferenceAxis` (global) drives both the explore steepness reference and the `SetAnchor` placement axis (`AlongWorldZ` → vertical; `AlongCameraView` → camera-forward).

Highlight color/alpha defaults are not dataset-specific — orange at 0.5 alpha tested adequate against both glacier and construction-site terrains.

When `Explore.Enabled = false`, the offscreen render task is skipped; a one-shot clear runs on the disable transition so stale frames don't leak through the composite sample.
