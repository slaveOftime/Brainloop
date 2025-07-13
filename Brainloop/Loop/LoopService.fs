namespace Brainloop.Loop

open System
open System.IO
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
        buildTitleHandler: IBuildTitleHandler,
        memoryService: IMemoryService,
        documentService: IDocumentService,
        agentService: IAgentService,
        loopContentService: ILoopContentService,
        globalStore: IGlobalStore,
        logger: ILogger<LoopService>
    ) as this =
    
    member private _.ToChatMessageContent(content: LoopContentWrapper) = valueTask {
        let items = ChatMessageContentItemCollection()

        let handleFile (fileName) = valueTask {
            let file = Path.Combine(documentService.RootDir, fileName)
            let ext =
                match Path.GetExtension(file) with
                | null -> "*"
                | x -> x.Substring(1)
            match file with
            | IMAGE ->
                let! bytes = File.ReadAllBytesAsync(file)
                items.Add(ImageContent(bytes, mimeType = $"image/{ext}"))
            | AUDIO ->
                let! bytes = File.ReadAllBytesAsync(file)
                items.Add(AudioContent(bytes, mimeType = $"audio/{ext}"))
            | VIDEO ->
                let! bytes = File.ReadAllBytesAsync(file)
                items.Add(BinaryContent(bytes, mimeType = $"video/{ext}"))
            | _ ->
                let! text = documentService.ReadAsText(file)
                items.Add(TextContent(text))
        }

        for content in content.Items |> AList.force do
            match content with
            | LoopContentItem.File x -> do! handleFile x.Name
            | LoopContentItem.Excalidraw x -> do! handleFile x.ImageFileName
            | LoopContentItem.ToolCall x -> items.Add(TextContent $"Tool function is invoked {x.FunctionName} {x.Description}")
            | LoopContentItem.Text x ->
                x.Blocks
                |> Seq.iter (
                    function
                    | LoopContentTextBlock.Think _ -> items.Add(TextContent("Thinking..."))
                    | LoopContentTextBlock.Content text -> items.Add(TextContent(text))
                )

        return ChatMessageContent(content.AuthorRole.ToSemanticKernelRole(), items, AuthorName = content.Author.KeepLetterAndDigits())
    }

    interface ILoopService with
        member _.Send(loopId, message, ?agentId, ?modelId, ?author, ?role, ?includeHistory, ?ignoreInput, ?sourceLoopContentId, ?cancellationToken) = valueTask {
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
                | ValueSome x, NullOrEmptyString -> $"@{x.Name}"
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
                let! contents = loopContentService.GetOrCreateContentsCache(loopId)
                let chatMessages = List<ChatMessageContent>()
                if includeHistory then
                    for item in contents |> AList.force do
                        let! content = this.ToChatMessageContent(item)
                        chatMessages.Add(content)

                chatMessages.Add(ChatMessageContent(role.ToSemanticKernelRole(), message, AuthorName = author))

                let outputContent = {
                    LoopContentWrapper.Default(loopId) with
                        Items = clist ()
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

                match agent.Type with
                | AgentType.General ->
                    chatMessages.Insert(0, ChatMessageContent(AuthorRole.System, agent.Prompt))
                    chatMessages.Insert(
                        0,
                        ChatMessageContent(
                            AuthorRole.System,
                            $"Your name is '{agent.Name}', when user ask with @'{agent.Name}', you should response it accordingly."
                        )
                    )
                    do!
                        chatCompletionHandler.Handle(
                            agent.Id,
                            chatMessages,
                            outputContent,
                            ?modelId = modelId,
                            ?cancellationToken = cancellationToken
                        )

                | _ -> failwith "Agent type not supported"

                let! _ = loopContentService.UpsertLoopContent({ outputContent with Id = outputContentId })

                if chatMessages.Count = 2 then
                    (this :> ILoopService).BuildTitle(loopId) |> ignore
        }


        member _.Resend(loopId, loopContentId, ?modelId) = valueTask {
            let! contents = loopContentService.GetOrCreateContentsCache(loopId) |> ValueTask.map AList.force
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

                        let chatMessages = List<ChatMessageContent>()

                        chatMessages.Add(ChatMessageContent(AuthorRole.System, agent.Prompt))

                        for item in contents |> Seq.takeWhile (fun x -> x.Id <> loopContentId) do
                            let! content = this.ToChatMessageContent(item)
                            chatMessages.Add(content)

                        do! chatCompletionHandler.Handle(agent.Id, chatMessages, target, ?modelId = modelId)

                        let! _ = loopContentService.UpsertLoopContent(target)

                        if chatMessages.Count = 2 then
                            (this :> ILoopService).BuildTitle(loopId) |> ignore
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

                    let! contents = loopContentService.GetOrCreateContentsCache(loopId) |> ValueTask.map AList.force

                    let chatMessages = List<ChatMessageContent>()
                    for item in contents.Take(2) do
                        let! content = this.ToChatMessageContent(item)
                        chatMessages.Add(content)

                    if chatMessages.Count > 1 then
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

                    else if contents.Count > 0 then
                        let text =
                            contents.[0].Items.Value
                            |> Seq.tryPick (
                                function
                                | LoopContentItem.Text x -> Some(String.Concat(x))
                                | _ -> None
                            )
                        match text with
                        | Some text -> do! updateTitle text
                        | _ -> failwith "Contents are not valid for summarizing"
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


    interface IStartChatLoopHandler with
        member _.Handle
            (loopId, message, ?agentId, ?modelId, ?author, ?role, ?includeHistory, ?ignoreInput, ?sourceLoopContentId, ?cancellationToken)
            =
            (this :> ILoopService)
                .Send(
                    loopId,
                    message,
                    ?agentId = agentId,
                    ?modelId = modelId,
                    ?author = author,
                    ?role = role,
                    ?includeHistory = includeHistory,
                    ?ignoreInput = ignoreInput,
                    ?sourceLoopContentId = sourceLoopContentId,
                    ?cancellationToken = cancellationToken
                )
