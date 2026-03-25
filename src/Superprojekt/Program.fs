open Aardworx.WebAssembly.Dom
open Aardworx.Rendering.WebGL
open Aardworx.WebAssembly
open Superprojekt
open System.Reflection
open Microsoft.JSInterop

[<EntryPoint>]
let main _ =
    task {
        do! Window.Document.Ready
        
        let query = Window.Location.GetQuery()
            
        let version =
            let v = typeof<Message>.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            if isNull v then "0.0.4"
            else v.InformationalVersion
            
        match Map.tryFind "nocache" query with
        | Some (Some "true") ->
            JSRuntime.Instance.InvokeVoid "localStorage.clear"
        | _ ->
            match LocalStorage.TryGet "super_cache_version" with
            | Some v when v = version -> ()
            | _ ->
                JSRuntime.Instance.InvokeVoid "localStorage.clear"
                for k, v in ShaderCache.cacheContent do
                    LocalStorage.Set(k, v)
                LocalStorage.Set("super_cache_version", version)
            
        
        let gl = new WebGLApplication(CommandStreamMode.Managed, false)
        Boot.run gl App.app
    } |> ignore
    0
