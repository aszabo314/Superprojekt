# ScanPin V3 Cleanup — Remaining Items

Phases A (Card System), B (Interaction Model), C (Hull Picking), D (Visual Polish) are complete.

## Deferred Stubs (V4)

1. **3D text rendering integration** — Major tick marks have dot placeholders. Integrate with aardvark.dom text rendering library.
   ```fsharp
   /// STUB(graphics): integrate with aardvark text rendering library.
   /// For now, render a small colored dot at the label position as a placeholder.
   val renderTextLabel3D : position:V3d -> text:string -> size:float -> color:C4b -> unit
   ```

2. **Radial menu component** — Long-press (>400ms) radial menu for touch interfaces. Card system accommodates popup overlays.

3. **Flashlight mode** — Ctrl+drag to reposition ScanPin anchor across terrain surface in real-time during placement. Requires client-side BVH or throttled server ray queries.
