namespace Superprojekt

// OIT pipeline removed. This file intentionally kept as a no-op placeholder
// to avoid touching the .fsproj — meshes now render with a single forward
// draw call each (see MeshView.MeshShader.shade) and translucent overlays
// (pins, ground grid, coordinate cross) use plain alpha blending with
// depth-test ON / depth-mask OFF.
