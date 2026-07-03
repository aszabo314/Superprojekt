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
    // no-cache (= revalidate every load) on the hand-edited shell assets — a stale
    // cached style.css silently breaks new overlay widgets; the Blazor framework
    // files stay on their own fingerprinted caching.
    let staticOpts = StaticFileOptions()
    staticOpts.OnPrepareResponse <- fun ctx ->
        let p = ctx.File.Name.ToLowerInvariant()
        if p.EndsWith ".css" || p.EndsWith ".html" || p.EndsWith ".js" then
            ctx.Context.Response.Headers.CacheControl <- "no-cache"
    app.UseStaticFiles(staticOpts) |> ignore
    app.UseGiraffe(Handlers.webApp) |> ignore
    app.MapFallbackToFile("index.html", staticOpts) |> ignore
    app.Run()
    0
