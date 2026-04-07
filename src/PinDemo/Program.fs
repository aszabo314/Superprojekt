module PinDemo.Program

open Aardvark.Base
open Aardvark.Application.Slim
open Aardvark.Dom
open Aardvark.Dom.Remote
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.DependencyInjection
open Giraffe

let app : App<unit, unit, unit> =
    {
        initial   = ()
        update    = fun _ () () -> ()
        view      = PanelView.view
        unpersist = { init = (fun () -> ()); update = (fun () () -> ()) }
    }

let private run (ctx : DomContext) =
    App.start ctx app

[<EntryPoint>]
let main _ =
    Aardvark.Init()
    let gl = new OpenGlApplication()
    Host.CreateDefaultBuilder()
        .ConfigureWebHostDefaults(fun w ->
            w.UseSockets()
             .Configure(fun b ->
                 b.UseWebSockets()
                  .UseStaticFiles()
                  .UseGiraffe(DomNode.toRoute gl.Runtime run))
             .ConfigureServices(fun s -> s.AddGiraffe() |> ignore)
             |> ignore)
        .Build()
        .Run()
    0
