[<AutoOpen>]
module Fun.Blazor.Hooks

open System
open FSharp.Data.Adaptive
open Microsoft.JSInterop
open Microsoft.Extensions.DependencyInjection
open IcedTasks
open Blazored.LocalStorage
open Fun.Blazor


type IComponentHook with

    member hook.ShareStore = hook.ServiceProvider.GetRequiredService<IShareStore>()


    member hook.LoadThemeType() = valueTask {
        let localStorageService = hook.ServiceProvider.GetRequiredService<ILocalStorageService>()
        let! themeType = localStorageService.GetItemAsync<ThemeType>("theme-type")
        match themeType with
        | null -> ()
        | x -> hook.ShareStore.ThemeType.Publish(x)
    }

    member hook.SaveThemeType(x: ThemeType) = valueTask {
        let localStorageService = hook.ServiceProvider.GetRequiredService<ILocalStorageService>()
        do! localStorageService.SetItemAsync("theme-type", x)
    }


    member private hook.IsHightlightCode = hook.ShareStore.CreateCVal(nameof hook.IsHightlightCode, false)

    member hook.HighlightCode(?maxCount) = task {
        if not hook.IsHightlightCode.Value then
            try
                hook.IsHightlightCode.Publish true
                let maxCount = defaultArg maxCount 10
                let js = hook.ServiceProvider.GetRequiredService<IJSRuntime>()

                let mutable retryCount = 0
                while retryCount < maxCount do
                    if retryCount > 0 then do! Async.Sleep 300
                    try
                        do! js.HighlightCode()
                        retryCount <- Int32.MaxValue
                    with _ ->
                        retryCount <- retryCount + 1
            finally
                hook.IsHightlightCode.Publish false
    }


    member private hook.NodesPortal = hook.ShareStore.CreateCMap<string, NodeRenderFragment>(nameof (hook.NodesPortal))

    member hook.GetNodeFromPortal(key: string) = hook.NodesPortal |> AMap.tryFind key

    // Node will be removed when hook is disposed
    member hook.AddNodeToPortal(key: string, node: NodeRenderFragment) =
        transact (fun _ -> hook.NodesPortal.Add(key, node) |> ignore)
        hook.OnDispose.Add(fun _ -> transact (fun _ -> hook.NodesPortal.Remove(key) |> ignore))
