[<AutoOpen>]
module Brainloop.Loop.Hooks

open System
open System.Threading.Tasks
open FSharp.Data.Adaptive
open Microsoft.Extensions.Logging
open Microsoft.Extensions.DependencyInjection
open MudBlazor
open Blazored.LocalStorage
open Fun.Blazor
open Brainloop.Db


type IComponentHook with

    member hook.LoadLoops() = task {
        let dbService = hook.ServiceProvider.GetRequiredService<IDbService>()
        let globalStore = hook.ServiceProvider.GetRequiredService<IGlobalStore>()
        let shareStore = hook.ServiceProvider.GetRequiredService<IShareStore>()

        if globalStore.ActiveLoops.Value.Count = 0 then
            use loopRepo = dbService.LoopRepo
            match!
                loopRepo.Select
                    .Where(fun x -> not x.Closed)
                    .OrderByDescending(fun x -> x.UpdatedAt)
                    .Take(shareStore.MaxActiveLoops.Value)
                    .ToListAsync()
            with
            | ls when ls.Count = 0 ->
                let! result = loopRepo.InsertAsync(Loop.Default)
                transact (fun _ ->
                    globalStore.ActiveLoops.Add(result) |> ignore
                    shareStore.CurrentLoop.Value <- ValueSome result
                )
            | ls ->
                transact (fun _ ->
                    let ls = ls |> Seq.sortBy (fun x -> x.UpdatedAt)
                    globalStore.ActiveLoops.AddRange(ls)
                    shareStore.CurrentLoop.Value <- ls |> Seq.last |> ValueSome
                )

        if shareStore.CurrentLoop.Value.IsNone then
            globalStore.ActiveLoops.Value |> Seq.tryHead |> ValueOption.ofOption |> shareStore.CurrentLoop.Publish
    }


    member hook.ToggleLoop(loopId: int64, close: bool) : Task<unit> = task {
        let dbService = hook.ServiceProvider.GetRequiredService<IDbService>()
        let globalStore = hook.ServiceProvider.GetRequiredService<IGlobalStore>()
        let shareStore = hook.ServiceProvider.GetRequiredService<IShareStore>()
        let loopContentService = hook.ServiceProvider.GetRequiredService<ILoopContentService>()
        let logger = hook.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("ToggleLoop")
        let snackbar = hook.ServiceProvider.GetRequiredService<ISnackbar>()

        try
            let! _ = dbService.DbContext.Update<Loop>(loopId).Set((fun x -> x.Closed), close).ExecuteAffrowsAsync()
            if close then
                loopContentService.RemoveFromCache(loopId)

                transact (fun _ ->
                    globalStore.ActiveLoops
                    |> AList.force
                    |> Seq.tryFindIndex (fun x -> x.Id = loopId)
                    |> Option.iter (globalStore.ActiveLoops.RemoveAt >> ignore)
                    shareStore.CurrentLoop.Value <- ValueNone
                )

            else
                let activeLoops = globalStore.ActiveLoops
                match activeLoops |> AList.force |> Seq.tryFind (fun x -> x.Id = loopId) with
                | Some loop -> transact (fun _ -> shareStore.CurrentLoop.Value <- ValueSome loop)
                | None ->
                    let! newLoop = dbService.DbContext.Select<Loop>().Where(fun (x: Loop) -> x.Id = loopId).FirstAsync<Loop | null>()
                    match newLoop with
                    | null -> ()
                    | newLoop ->
                        let! _ = loopContentService.GetOrCreateContentsCache(loopId)
                        transact (fun _ ->
                            while activeLoops.Count >= shareStore.MaxActiveLoops.Value do
                                loopContentService.RemoveFromCache(activeLoops[0].Id)
                                activeLoops.RemoveAt(0) |> ignore

                            activeLoops.Add(newLoop) |> ignore
                            shareStore.CurrentLoop.Value <- ValueSome newLoop
                        )

        with ex ->
            snackbar.ShowMessage(ex, logger)
    }


    member hook.LoadCurrentLoop() = task {
        let localStorageService = hook.ServiceProvider.GetRequiredService<ILocalStorageService>()
        let! currentLoopId = localStorageService.GetItemAsync<Nullable<int64>>("current-loop-id")
        if currentLoopId.HasValue then do! hook.ToggleLoop(currentLoopId.Value, false)
    }

    member hook.LoadMaxActiveLoops() = task {
        let localStorageService = hook.ServiceProvider.GetRequiredService<ILocalStorageService>()
        let! maxActiveLopps = localStorageService.GetItemAsync<Nullable<int>>("max-active-loops")
        if maxActiveLopps.HasValue then
            hook.ShareStore.MaxActiveLoops.Publish(maxActiveLopps.Value)
    }

    member hook.SetMaxActiveLoops(x: int) = task {
        hook.ShareStore.MaxActiveLoops.Publish(x)
        let localStorageService = hook.ServiceProvider.GetRequiredService<ILocalStorageService>()
        do! localStorageService.SetItemAsync("max-active-loops", x)
    }


    member hook.UpdateLoopInStore(loop: Loop) =
        let shareStore = hook.ServiceProvider.GetRequiredService<IShareStore>()
        let globalStore = hook.ServiceProvider.GetRequiredService<IGlobalStore>()
        transact (fun _ ->
            match globalStore.ActiveLoops |> Seq.tryFindIndex (fun x -> x.Id = loop.Id) with
            | None -> ()
            | Some i -> globalStore.ActiveLoops[i] <- loop

            match shareStore.CurrentLoop.Value with
            | ValueSome cl when cl.Id = loop.Id -> shareStore.CurrentLoop.Value <- ValueSome loop
            | _ -> ()
        )
