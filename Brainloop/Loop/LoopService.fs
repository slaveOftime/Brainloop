namespace Brainloop.Loop

open System
open System.Linq
open System.Collections.Generic
open FSharp.Control
open FSharp.Data.Adaptive
open Microsoft.Extensions.Logging
open Microsoft.SemanticKernel
open Microsoft.SemanticKernel.ChatCompletion
open IcedTasks
open Fun.Result
open Fun.Blazor
open Brainloop.Db
open Brainloop.Memory
open Brainloop.Agent
open Brainloop.Share


type LoopService
    (
        dbService: IDbService,
        chatCompletionHandler: IChatCompletionHandler,
        buildTitleHandler: ICreateTitleHandler,
        memoryService: IMemoryService,
        agentService: IAgentService,
        loopContentService: ILoopContentService,
        globalStore: IGlobalStore,
        logger: ILogger<LoopService>
    ) as this =

    member _.AddSourceLoopContents(chatMessages: List<LoopContentWrapper>, loopId: int64, historyToInclude: int) =
        if historyToInclude > chatMessages.Count then
            let sourceLoopContentId =
                dbService.DbContext.Select<Loop>().Where(fun (x: Loop) -> x.Id = loopId).First(fun x -> x.SourceLoopContentId)
            if sourceLoopContentId.HasValue && sourceLoopContentId.Value > 0L then
                let sourceLoopContentId = sourceLoopContentId.Value
                let sourceLoopId =
                    dbService.DbContext.Select<LoopContent>().Where(fun (x: LoopContent) -> x.Id = sourceLoopContentId).First(fun x -> x.LoopId)
                if sourceLoopId >= 0L then
                    let sourceContents =
                        dbService.DbContext
                            .Select<LoopContent>()
                            .Where(fun (x: LoopContent) -> x.LoopId = sourceLoopId && x.Id <= sourceLoopContentId)
                            .OrderBy(fun x -> x.Id)
                            .Take(historyToInclude - chatMessages.Count)
                            .ToList()
                            .OrderByDescending(fun x -> x.Id)
                    for item in sourceContents do
                        chatMessages.Insert(0, LoopContentWrapper.FromLoopContent(item))
                    this.AddSourceLoopContents(chatMessages, sourceLoopId, historyToInclude)

    interface ILoopService with
        member _.Send
            (loopId, message, ?agentId, ?modelId, ?author, ?role, ?includeHistory, ?ignoreInput, ?sourceLoopContentId, ?functions, ?cancellationToken)
            =
            valueTask {
                let author = author |> Option.defaultValue "ME"
                let role = role |> Option.defaultValue LoopContentAuthorRole.User
                let ignoreInput = ignoreInput |> Option.defaultValue false
                let includeHistory = includeHistory |> Option.defaultValue true

                let! agent =
                    match agentId with
                    | Some agentId -> agentService.TryGetAgentWithCache(agentId)
                    | None -> ValueTask.fromResult (ValueNone)

                let message =
                    match agent, message with
                    | ValueSome x, NullOrEmptyString -> if x.Name.Contains(' ') then $"@\"{x.Name}\"" else $"@{x.Name}"
                    | _ -> message

                let mutable inputContentId = ValueNone

                if not ignoreInput && String.IsNullOrEmpty message |> not then
                    let inputContent = {
                        LoopContentWrapper.Default(loopId) with
                            Items = clist [ LoopContentItem.Text(LoopContentText message) ]
                            Author = author
                            AuthorRole = role
                            SourceLoopContentId = sourceLoopContentId |> Option.toValueOption
                    }

                    let! id = loopContentService.AddContentToCacheAndUpsert(inputContent)
                    inputContentId <- ValueSome id

                match agent with
                | ValueNone -> logger.LogInformation("No agent is specified")
                | ValueSome agent ->
                    let chatMessages = List<LoopContentWrapper>()
                    let! contents = loopContentService.GetOrCreateContentsCache(loopId)
                    let historyToInclude = Math.Max(0, agent.MaxHistory)
                    if includeHistory && historyToInclude > 0 then
                        let mutable previousCount = -1
                        while historyToInclude > contents.Count && previousCount <> contents.Count do
                            previousCount <- contents.Count
                            do! loopContentService.LoadMoreContentsIntoCache(loopId)
                        for item in contents |> AList.force |> _.TakeLast(historyToInclude) do
                            chatMessages.Add(item)
                        this.AddSourceLoopContents(chatMessages, loopId, historyToInclude)

                    if inputContentId.IsNone then
                        chatMessages.Add(
                            {
                                LoopContentWrapper.Default loopId with
                                    Author = author
                                    AuthorRole = role
                                    Items = clist [ LoopContentItem.Text(LoopContentText message) ]
                            }
                        )

                    let outputContent = {
                        LoopContentWrapper.Default(loopId) with
                            Items = clist ()
                            DirectPrompt =
                                (match ignoreInput, message with
                                 | true, SafeString x -> cval (ValueSome x)
                                 | _ -> cval ValueNone)
                            Author = agent.Name
                            AuthorRole = LoopContentAuthorRole.Agent
                            AgentId = ValueSome agent.Id
                            SourceLoopContentId =
                                match inputContentId with
                                | ValueSome _ -> inputContentId
                                | _ -> sourceLoopContentId |> Option.toValueOption
                    }
                    let! outputContentId = loopContentService.AddContentToCacheAndUpsert(outputContent)
                    let outputContent = { outputContent with Id = outputContentId }

                    do!
                        chatCompletionHandler.Handle(
                            agent.Id,
                            chatMessages,
                            outputContent,
                            ?modelId = modelId,
                            ?functions = functions,
                            ?cancellationToken = cancellationToken
                        )

                    do! loopContentService.UpsertLoopContent({ outputContent with Id = outputContentId }) |> ValueTask.map ignore

                let! contents = loopContentService.GetOrCreateContentsCache(loopId)
                if contents.Count <= 2 then (this :> ILoopService).BuildTitle(loopId) |> ignore
            }


        member _.Resend(loopId, loopContentId, ?modelId) = valueTask {
            let! contents = loopContentService.GetOrCreateContentsCache(loopId)
            match contents |> Seq.tryFind (fun x -> x.Id = loopContentId) with
            | None -> failwith "No message found"
            | Some target ->
                match target.AgentId with
                | ValueNone -> failwith $"Agent {target.Author} is not found"
                | ValueSome agentId ->
                    match! agentService.TryGetAgentWithCache(agentId) with
                    | ValueNone -> failwith $"Agent {agentId} is not found"
                    | ValueSome agent ->
                        do! loopContentService.DeleteLoopContentsOfSource(loopId, loopContentId)

                        let chatMessages = List<LoopContentWrapper>()
                        let historyToInclude =
                            match target.DirectPrompt.Value with
                            | ValueSome _ -> target.IncludedHistoryCount.Value
                            | ValueNone -> agent.MaxHistory
                            |> fun x -> Math.Max(0, x)

                        let filterContents () = contents |> Seq.takeWhile (fun x -> x.Id <> loopContentId)

                        let mutable previousCount = -1
                        let mutable currentCount = filterContents () |> Seq.length
                        while historyToInclude > currentCount && previousCount <> currentCount do
                            previousCount <- currentCount
                            do! loopContentService.LoadMoreContentsIntoCache(loopId)
                            currentCount <- filterContents () |> Seq.length

                        if historyToInclude > 0 then
                            for item in filterContents () |> _.TakeLast(historyToInclude) do
                                chatMessages.Add(item)
                            this.AddSourceLoopContents(chatMessages, loopId, historyToInclude)

                        match target.DirectPrompt.Value with
                        | ValueSome(SafeString prompt) ->
                            chatMessages.Add {
                                LoopContentWrapper.Default loopId with
                                    Author = target.Author
                                    AuthorRole = target.AuthorRole
                                    Items = clist [ LoopContentItem.Text(LoopContentText prompt) ]
                            }
                        | _ -> ()

                        do! chatCompletionHandler.Handle(agent.Id, chatMessages, target, ?modelId = modelId)

                        let! _ = loopContentService.UpsertLoopContent(target)

                        if contents.Count <= 2 then (this :> ILoopService).BuildTitle(loopId) |> ignore
        }


        member _.BuildTitle(loopId, ?title) = valueTask {
            let updateTitle (title: string) = valueTask {
                do!
                    dbService.DbContext
                        .Update<Loop>(loopId)
                        .Set((fun x -> x.Description), title)
                        .Set((fun x -> x.UpdatedAt), DateTime.Now)
                        .ExecuteAffrowsAsync()
                    |> Task.map ignore

                transact (fun _ -> globalStore.LoopTitles.Add(loopId, LoadingState.Loaded title) |> ignore)

                valueTask {
                    try
                        do! memoryService.VectorizeLoop(loopId, title)
                    with ex ->
                        logger.LogError(ex, "Failed to vectorize loop {loopId} with title {title}", loopId, title)
                }
                |> ignore
            }

            match title with
            | Some title -> do! updateTitle title
            | _ ->
                try
                    let! agent =
                        agentService.GetTitleBuilderAgent()
                        |> ValueTask.map (
                            function
                            | null -> failwith "There is no system agent defined to use"
                            | x -> x
                        )

                    let! contents = loopContentService.GetOrCreateContentsCache(loopId)

                    let chatMessages = List<ChatMessageContent>()

                    let mutable index = 0
                    while index < contents.Count && chatMessages.Count < 2 do
                        let item = contents[index]
                        index <- index + 1
                        let! content = loopContentService.ToChatMessageContent(item)
                        content.Role <- AuthorRole.User
                        if content.Items.Count > 0 then chatMessages.Add(content)

                    if chatMessages.Count > 0 then
                        transact (fun _ ->
                            let summary =
                                globalStore.LoopTitles.Value
                                |> HashMap.tryFind loopId
                                |> Option.map LoadingState.start
                                |> Option.defaultValue LoadingState.Loading
                            globalStore.LoopTitles.Add(loopId, summary) |> ignore
                        )

                        chatMessages.Add(ChatMessageContent(AuthorRole.User, agent.Prompt))

                        let! result = buildTitleHandler.Handle(agent.Id, chatMessages)
                        do! updateTitle result

                    else
                        failwith "Contents are not enough for summarizing"

                finally
                    transact (fun _ ->
                        let summary =
                            globalStore.LoopTitles.Value
                            |> HashMap.tryFind loopId
                            |> Option.map (
                                function
                                | LoadingState.Reloading x
                                | LoadingState.Loaded x -> LoadingState.Loaded x
                                | _ -> LoadingState.NotStartYet
                            )
                            |> Option.defaultValue LoadingState.NotStartYet
                        globalStore.LoopTitles.Add(loopId, summary) |> ignore
                    )
        }

        member _.IsStreaming(loopId) =
            match loopContentService.GetContentsCache(loopId) with
            | null -> AVal.constant false
            | x -> x |> AList.exists (fun x -> x.IsStreaming.Value)


        member _.SetCategory(loopId, categoryId) =
            dbService.DbContext.Update<Loop>().Where(fun x -> x.Id = loopId).Set((fun x -> x.LoopCategoryId), categoryId).ExecuteAffrowsAsync()
            |> ValueTask.ofTask
            |> ValueTask.map ignore
