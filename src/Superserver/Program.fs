open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Giraffe

[<EntryPoint>]
let main args =
    Aardvark.Base.Aardvark.Init()
    let builder = WebApplication.CreateBuilder(args)
    builder.Services.AddCors()    |> ignore
    builder.Services.AddGiraffe() |> ignore
    let app = builder.Build()
    app.UseCors(fun p -> p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader() |> ignore) |> ignore
    app.UseBlazorFrameworkFiles() |> ignore
    app.UseStaticFiles()         |> ignore
    app.UseGiraffe(Handlers.webApp) |> ignore
    app.MapFallbackToFile("index.html") |> ignore
    app.Run()
    0
