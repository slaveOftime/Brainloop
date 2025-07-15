namespace Brainloop.Handlers

open System.IO
open FSharp.Control
open Microsoft.Extensions.Logging
open IcedTasks
open Fun.Result
open Brainloop.Db
open Brainloop.Share
open Brainloop.Memory


type RebuildMemoryHandler
    (dbService: IDbService, memoryService: IMemoryService, documentService: IDocumentService, logger: ILogger<RebuildMemoryHandler>) =

    interface IRebuildMemoryHandler with
        member _.Handle(?cancellationToken) = valueTask {
            let isCancelled () =
                match cancellationToken with
                | None -> false
                | Some x -> x.IsCancellationRequested

            try
                logger.LogInformation("Rebuilding memory")
                do! memoryService.Clear()

                let pageSize = 100
                let maxDegreeOfParallelism = 8

                let mutable page = 0
                let mutable shouldContinue = true
                while shouldContinue && not (isCancelled ()) do
                    logger.LogInformation("Rebuilding memory for loops {page}", page)
                    let! loops = dbService.DbContext.Select<Loop>().OrderBy(fun (x: Loop) -> x.Id).Skip(page * pageSize).Take(pageSize).ToListAsync()

                    let tasks = seq {
                        for loop in loops do
                            async { do! memoryService.VectorizeLoop(loop.Id, loop.Description) }
                    }
                    do!
                        Async.Parallel(tasks, maxDegreeOfParallelism = maxDegreeOfParallelism)
                        |> Async.Ignore
                        |> fun x -> Async.StartImmediateAsTask(x, ?cancellationToken = cancellationToken)

                    page <- page + 1
                    shouldContinue <- loops.Count >= pageSize


                page <- 0
                shouldContinue <- true
                while shouldContinue && not (isCancelled ()) do
                    logger.LogInformation("Rebuilding memory for loop contents {page}, page")
                    let! loopContents =
                        dbService.DbContext
                            .Select<LoopContent>()
                            .OrderBy(fun (x: LoopContent) -> x.Id)
                            .Skip(page * pageSize)
                            .Take(pageSize)
                            .ToListAsync()

                    let tasks = seq {
                        for loopContent in loopContents do
                            async {
                                do!
                                    memoryService.VectorizeLoopContent(
                                        loopContent.Id,
                                        LoopContentWrapper.FromLoopContent(loopContent).ConvertItemsToTextForVectorization()
                                    )
                            }
                    }
                    do!
                        Async.Parallel(tasks, maxDegreeOfParallelism = maxDegreeOfParallelism)
                        |> Async.Ignore
                        |> fun x -> Async.StartImmediateAsTask(x, ?cancellationToken = cancellationToken)

                    page <- page + 1
                    shouldContinue <- loopContents.Count >= pageSize


                let loopDirectory (dir: string) (recurseSubDirectories: bool) = valueTask {
                    logger.LogInformation("Rebuilding memory for documents in {dir}, recursive {recurseSubDirectories}", dir, recurseSubDirectories)

                    let tasks = seq {
                        let files = Directory.GetFiles(dir, "*.*", EnumerationOptions(RecurseSubdirectories = recurseSubDirectories))
                        for file in files do
                            async {
                                match Path.GetFileName file with
                                | null -> ()
                                | fileName ->
                                    if fileName.StartsWith("LC-") then
                                        match fileName.Split('-')[1] with
                                        | INT64 loopContentId -> do! memoryService.VectorizeFile(fileName, loopContentId)
                                        | _ -> do! memoryService.VectorizeFile(fileName)
                                    else
                                        do! memoryService.VectorizeFile(fileName)
                            }
                    }
                    do!
                        Async.Parallel(tasks, maxDegreeOfParallelism = maxDegreeOfParallelism)
                        |> Async.Ignore
                        |> fun x -> Async.StartImmediateAsTask(x, ?cancellationToken = cancellationToken)
                }

                do! loopDirectory documentService.RootDir false

            with ex ->
                logger.LogError(ex, "Rebuild memory failed")
        }
