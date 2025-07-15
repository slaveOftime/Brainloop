[<AutoOpen>]
module Fun.Blazor.Hooks

open FSharp.Data.Adaptive
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


    member private hook.NodesPortal = hook.ShareStore.CreateCMap<string, NodeRenderFragment>(nameof (hook.NodesPortal))

    member hook.GetNodeFromPortal(key: string) = hook.NodesPortal |> AMap.tryFind key

    // Node will be removed when hook is disposed
    member hook.AddNodeToPortal(key: string, node: NodeRenderFragment) =
        transact (fun _ -> hook.NodesPortal.Add(key, node) |> ignore)
        hook.OnDispose.Add(fun _ -> transact (fun _ -> hook.NodesPortal.Remove(key) |> ignore))
