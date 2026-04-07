namespace PinDemo

open Aardvark.Base
open Aardvark.Rendering

module HeightfieldMesh =

    /// Build a triangle mesh from a regular height grid. NaN cells are skipped.
    let build (grid : GridSampledSurface) : IndexedGeometry =
        let n = grid.Resolution
        let cell = grid.CellSize
        let o = grid.GridOrigin
        let positions = ResizeArray<V3f>()
        let indices   = ResizeArray<int>()

        // Map (i,j) → vertex index, -1 = no data
        let vmap = Array.create (n * n) -1
        for j in 0 .. n - 1 do
            for i in 0 .. n - 1 do
                let h = grid.Heights.[j * n + i]
                if not (System.Double.IsNaN h) then
                    let x = o.X + float i * cell
                    let y = o.Y + float j * cell
                    vmap.[j * n + i] <- positions.Count
                    positions.Add(V3f(float32 x, float32 y, float32 h))

        let inline tryGet i j =
            if i < 0 || i >= n || j < 0 || j >= n then -1
            else vmap.[j * n + i]

        for j in 0 .. n - 2 do
            for i in 0 .. n - 2 do
                let a = tryGet i j
                let b = tryGet (i + 1) j
                let c = tryGet (i + 1) (j + 1)
                let d = tryGet i (j + 1)
                if a >= 0 && b >= 0 && c >= 0 then
                    indices.Add a; indices.Add b; indices.Add c
                if a >= 0 && c >= 0 && d >= 0 then
                    indices.Add a; indices.Add c; indices.Add d

        let pos = positions.ToArray()
        let idx = indices.ToArray()
        IndexedGeometry(
            Mode = IndexedGeometryMode.TriangleList,
            IndexArray = (idx :> System.Array),
            IndexedAttributes = SymDict.ofList [
                DefaultSemantic.Positions, (pos :> System.Array)
            ])
