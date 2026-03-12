open Aardworx.WebAssembly.Dom
open Aardworx.Rendering.WebGL
open Aardworx.WebAssembly
open Superprojekt

[<EntryPoint>]
let main _ =
    task {
        do! Window.Document.Ready
        let gl = new WebGLApplication(CommandStreamMode.Managed, false)
        Boot.run gl App.app
    } |> ignore
    0
