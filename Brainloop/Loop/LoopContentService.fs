namespace Brainloop.Loop

open System
open System.IO
open System.Threading
open System.Collections.Concurrent
open FSharp.Control
open FSharp.Data.Adaptive
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Caching.Memory
open Microsoft.SemanticKernel
open Microsoft.SemanticKernel.ChatCompletion
open IcedTasks
open Fun.Result
open Brainloop.Db
open Brainloop.Share
open Brainloop.Memory


type LoopContentService
    (
        dbService: IDbService,
        memoryService: IMemoryService,
        documentService: IDocumentService,
        memoryCache: IMemoryCache,
        logger: ILogger<LoopContentService>
    ) as this =

    static let contentsLockers = ConcurrentDictionary<int64, SemaphoreSlim>()
    static let getContentsLocker loopId = contentsLockers.GetOrAdd(loopId, fun _ -> new SemaphoreSlim(1))

    let createLoopContentWrappersCacheKey loopId = $"loop-content-wrappers-{loopId}"


    interface ILoopContentService with

        member _.GetOrCreateContentsCache(loopId) =
            // Use memoryCache to keep the object instance in memory directly or reusing
            memoryCache.GetOrCreateAsync<ChangeableIndexList<LoopContentWrapper>>(
                createLoopContentWrappersCacheKey loopId,
                fun _ -> task {
                    return!
                        dbService.DbContext
                            .Queryable<LoopContent>()
                            .Where(fun (x: LoopContent) -> x.LoopId = loopId)
                            .OrderByDescending(fun (x: LoopContent) -> x.Id)
                            .Take(5)
                            .ToListAsync()
                        |> Task.map (Seq.sortBy _.Id >> Seq.map LoopContentWrapper.FromLoopContent >> ChangeableIndexList)
                }
            )
            |> ValueTask.ofTask
            |> ValueTask.map (
                function
                | null -> failwith "Loop content is null"
                | x -> x
            )

        member _.GetContentsCache(loopId) = memoryCache.Get<ChangeableIndexList<LoopContentWrapper>>(createLoopContentWrappersCacheKey loopId)

        member _.RemoveFromCache(loopId) =
            let isStreaming =
                match (this :> ILoopContentService).GetContentsCache(loopId) with
                | null -> false
                | x -> x |> AList.exists (fun x -> x.IsStreaming |> AVal.force) |> AVal.force
            if not isStreaming then
                memoryCache.Remove(createLoopContentWrappersCacheKey loopId)
        // TODO: for cache entry which is not linked to active loops or not streaming, we should remove it after some time


        member _.LoadMoreContentsIntoCache(loopId) = valueTask {
            let locker = getContentsLocker loopId
            do! locker.WaitAsync()
            try
                let! contents = (this :> ILoopContentService).GetOrCreateContentsCache(loopId)
                if contents.Count > 0 then
                    let splitId = contents[0].Id
                    let! newContents =
                        dbService.DbContext
                            .Queryable<LoopContent>()
                            .Where(fun (x: LoopContent) -> x.LoopId = loopId && x.Id < splitId)
                            .OrderByDescending(fun (x: LoopContent) -> x.Id)
                            .Take(5)
                            .ToListAsync()
                    let latestContent = contents |> AList.force
                    for item in newContents do
                        if latestContent |> Seq.exists (fun x -> x.Id = item.Id) |> not then
                            let content = LoopContentWrapper.FromLoopContent(item)
                            transact (fun _ -> contents.InsertAt(0, content) |> ignore)
            finally
                locker.Release() |> ignore
        }

        member _.LoadMoreLatestContentsIntoCache(loopId) = valueTask {
            let locker = getContentsLocker loopId
            do! locker.WaitAsync()
            try
                let! contents = (this :> ILoopContentService).GetOrCreateContentsCache(loopId)
                if contents.Count > 0 then
                    let splitId = contents[contents.Count - 1].Id
                    let! newContents =
                        dbService.DbContext
                            .Queryable<LoopContent>()
                            .Where(fun (x: LoopContent) -> x.LoopId = loopId && x.Id > splitId)
                            .OrderBy(fun (x: LoopContent) -> x.Id)
                            .Take(5)
                            .ToListAsync()
                    let latestContents = contents |> AList.force
                    for item in newContents do
                        if latestContents |> Seq.exists (fun x -> x.Id = item.Id) |> not then
                            let content = LoopContentWrapper.FromLoopContent(item)
                            transact (fun _ -> contents.Add(content) |> ignore)
            finally
                locker.Release() |> ignore
        }


        member _.AddContentToCacheAndUpsert(content) = valueTask {
            let locker = getContentsLocker content.LoopId
            do! locker.WaitAsync()
            try
                let! contents = (this :> ILoopContentService).GetOrCreateContentsCache(content.LoopId)
                let! id = (this :> ILoopContentService).UpsertLoopContent(content)
                transact (fun _ -> contents.Add({ content with Id = id }) |> ignore)
                return id
            finally
                locker.Release() |> ignore
        }


        member _.UpsertLoopContent(content, ?disableVectorize) = valueTask {
            let disableVectorize = defaultArg disableVectorize false
            let! _ = dbService.DbContext.Update<Loop>(content.LoopId).Set((fun x -> x.UpdatedAt), DateTime.Now).ExecuteAffrowsAsync()

            let loopContent = content.ToLoopContent()
            let! loopContentId = valueTask {
                if content.Id = 0 then
                    return! dbService.DbContext.Insert<LoopContent>(loopContent).ExecuteIdentityAsync()
                else
                    let! _ = dbService.DbContext.Update<LoopContent>(loopContent.Id).SetSource(loopContent).ExecuteAffrowsAsync()
                    return content.Id
            }

            if not disableVectorize then
                valueTask {
                    try
                        do! memoryService.VectorizeLoopContent(loopContentId, content.ConvertItemsToTextForVectorization())
                    with ex ->
                        logger.LogError(ex, "Vectorize loop content failed")
                }
                |> ignore

            return loopContentId
        }

        member _.UpdateLoopContent(loopId, loopContentId, content) = valueTask {
            let! contents = (this :> ILoopContentService).GetOrCreateContentsCache(loopId) |> ValueTask.map AList.force
            match contents |> Seq.tryFind (fun x -> x.Id = loopContentId) with
            | None -> ()
            | Some contentWrapper ->
                contentWrapper.ResetContent(content)
                do! (this :> ILoopContentService).UpsertLoopContent(contentWrapper) |> ValueTask.map ignore
        }

        member _.DeleteLoopContent(loopId, loopContentId) = valueTask {
            do! dbService.DbContext.Delete<LoopContent>(loopContentId).ExecuteAffrowsAsync() |> Task.map ignore
            do! memoryService.DeleteLoopContent(loopContentId)

            let! contents = (this :> ILoopContentService).GetOrCreateContentsCache(loopId)
            transact (fun _ -> contents |> Seq.tryFindIndex (fun x -> x.Id = loopContentId) |> Option.iter (contents.RemoveAt >> ignore))

            do! (this :> ILoopContentService).DeleteLoopContentsOfSource(loopId, loopContentId)
        }


        member _.DeleteLoopContentsOfSource(loopId, loopContentId) = valueTask {
            let! contentIds =
                dbService.DbContext
                    .Select<LoopContent>()
                    .Where(fun (x: LoopContent) -> x.SourceLoopContentId = loopContentId)
                    .ToListAsync(fun x -> x.Id)

            if contentIds.Count > 0 then
                do!
                    dbService.DbContext
                        .Delete<LoopContent>()
                        .Where(fun (x: LoopContent) -> x.SourceLoopContentId = loopContentId)
                        .ExecuteAffrowsAsync()
                    |> Task.map ignore

                do! memoryService.DeleteLoopContent(loopContentId)

                let! contents = (this :> ILoopContentService).GetOrCreateContentsCache(loopId)
                transact (fun _ ->
                    let items = contents |> Seq.filter (fun x -> x.SourceLoopContentId <> ValueSome loopContentId) |> Seq.toArray
                    contents.Clear()
                    contents.AddRange(items)
                )

                for contentId in contentIds do
                    do! (this :> ILoopContentService).DeleteLoopContentsOfSource(loopId, contentId)
        }


        member _.ToChatMessageContent(content, ?model) = valueTask {
            let items = ChatMessageContentItemCollection()

            let handleFile (fileName) = valueTask {
                let file = Path.Combine(documentService.RootDir, fileName)
                let ext =
                    match Path.GetExtension(file) with
                    | null -> "*"
                    | x -> x.Substring(1)
                match file with
                | IMAGE ->
                    match model with
                    | None
                    | Some { CanHandleImage = true } ->
                        let! bytes = File.ReadAllBytesAsync(file)
                        items.Add(ImageContent(bytes, mimeType = $"image/{ext}"))
                    | _ -> ()
                | AUDIO ->
                    match model with
                    | None
                    | Some { CanHandleAudio = true } ->
                        let! bytes = File.ReadAllBytesAsync(file)
                        items.Add(AudioContent(bytes, mimeType = $"audio/{ext}"))
                    | _ -> ()
                | VIDEO ->
                    match model with
                    | None
                    | Some { CanHandleVideo = true } ->
                        let! bytes = File.ReadAllBytesAsync(file)
                        items.Add(BinaryContent(bytes, mimeType = $"video/{ext}"))
                    | _ -> ()
                | _ ->
                    let! text = documentService.ReadAsText(file)
                    items.Add(TextContent(text))
            }

            for content in content.Items |> AList.force do
                match content with
                | LoopContentItem.File x -> do! handleFile x.Name
                | LoopContentItem.Excalidraw x -> do! handleFile x.ImageFileName
                | LoopContentItem.ToolCall x -> items.Add(TextContent $"Invoked tool: {x.FunctionName} {x.Description}")
                | LoopContentItem.Text x ->
                    x.Blocks
                    |> Seq.iter (
                        function
                        | LoopContentTextBlock.Think _ -> items.Add(TextContent("Thinking..."))
                        | LoopContentTextBlock.Content text -> items.Add(TextContent(text))
                    )

            return ChatMessageContent(content.AuthorRole.ToSemanticKernelRole(), items, AuthorName = content.Author.KeepLetterAndDigits())
        }
