open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Giraffe

#if BUNDLE
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.Hosting

let private openBrowser (url : string) =
    let psi =
        if System.OperatingSystem.IsWindows () then
            System.Diagnostics.ProcessStartInfo(FileName = url, UseShellExecute = true)
        elif System.OperatingSystem.IsMacOS () then
            System.Diagnostics.ProcessStartInfo("open", url)
        else
            System.Diagnostics.ProcessStartInfo("xdg-open", url)
    try System.Diagnostics.Process.Start psi |> ignore with _ -> ()
#endif

[<EntryPoint>]
let main args =
    Aardvark.Base.Aardvark.Init()
#if BUNDLE
    // Content root = the single-file self-extraction dir (where wwwroot/ and
    // data/ land), never the launch cwd — a double-click cwd is arbitrary.
    let builder =
        WebApplication.CreateBuilder(
            WebApplicationOptions(Args = args, ContentRootPath = System.AppContext.BaseDirectory))
#else
    let builder = WebApplication.CreateBuilder(args)
#endif
#if BUNDLE
    // Fixed port: checkpoints/autosave live in origin-scoped localStorage, so a
    // stable port is what keeps them across relaunches. Ephemeral fallback if taken.
    let port =
        try
            let l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 5055)
            l.Start (); l.Stop (); 5055
        with _ -> 0
    builder.WebHost.UseUrls (sprintf "http://127.0.0.1:%d" port) |> ignore
#endif
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
#if BUNDLE
    app.Start()
    let url = app.Urls |> Seq.head
    printfn "Superprojekt running at %s — close this window (or Ctrl+C) to stop." url
    openBrowser url
    app.WaitForShutdown()
#else
    app.Run()
#endif
    0
