namespace Brainloop.Loop

open System
open System.Linq
open System.Threading
open System.Threading.Tasks
open System.Diagnostics
open System.Text
open System.Text.Json.Nodes
open System.Collections.Generic
open System.ClientModel.Primitives
open FSharp.Control
open FSharp.Data.Adaptive
open Microsoft.Extensions.Logging
open Microsoft.SemanticKernel
open Microsoft.SemanticKernel.ChatCompletion
open Microsoft.SemanticKernel.Connectors
open Microsoft.SemanticKernel.Data
open IcedTasks
open Fun.Result
open Fun.Blazor
open Brainloop.Db
open Brainloop.Model
open Brainloop.Memory
open Brainloop.Function
open Brainloop.Agent
open Brainloop.Handler


type LoopService
    (
        dbService: IDbService,
        modelService: IModelService,
        textSearch: ITextSearch,
        memoryService: IMemoryService,
        documentService: IDocumentService,
        functionService: IFunctionService,
        agentService: IAgentService,
        loopContentService: ILoopContentService,
        globalStore: IGlobalStore,
        logger: ILogger<LoopService>
    ) as this =

    member private _.CreateFunctionFilter
        (agent: Agent, targetContent: LoopContentWrapper, onFunctionStarted: unit -> unit, cancellationToken: CancellationToken)
        =
        { new IFunctionInvocationFilter with
            member _.OnFunctionInvocationAsync(context, next) = task {
                let functionWatch = Stopwatch()
                let functionName =
                    match context.Function.PluginName with
                    | null -> context.Function.Name
                    | x -> $"{x}-{context.Function.Name}"

                let! isInWhiteList = agentService.IsFunctionInWhitelist(agent.Id, functionName)

                let toolCall = {
                    AgentId = agent.Id
                    FunctionName = functionName
                    Description = context.Function.Description
                    Arguments = context.Arguments.ToDictionary()
                    Result = ValueNone
                    DurationMs = 0
                    UserAction =
                        ValueSome(
                            cval (
                                if isInWhiteList || SystemFunction.isCreateTaskForAgent functionName then
                                    ToolCallUserAction.Accepted
                                else
                                    ToolCallUserAction.Pending
                            )
                        )
                }

                targetContent.AddContent(LoopContentItem.ToolCall toolCall)

                let updateToolCall (newToolCall) = valueTask {
                    transact (fun _ ->
                        let index =
                            targetContent.Items
                            |> Seq.tryFindIndex (
                                function
                                | LoopContentItem.ToolCall x -> x = toolCall
                                | _ -> false
                            )
                        match index with
                        | Some index when index >= 0 ->
                            targetContent.Items.RemoveAt(index) |> ignore
                            targetContent.Items.InsertAt(index, LoopContentItem.ToolCall newToolCall) |> ignore
                        | _ -> ()
                    )
                    do! loopContentService.UpsertLoopContent(targetContent, disableVectorize = true) |> ValueTask.map ignore
                }

                try
                    match toolCall.UserAction with
                    | ValueNone -> ()
                    | ValueSome isAllowed ->
                        logger.LogInformation("Waiting user permission ...")
                        let taskCompletionSource = TaskCompletionSource()
                        use _ =
                            isAllowed.AddInstantCallback(
                                function
                                | ToolCallUserAction.Pending -> ()
                                | ToolCallUserAction.Declined -> taskCompletionSource.SetCanceled()
                                | ToolCallUserAction.Accepted -> taskCompletionSource.SetResult()
                            )
                        use _ = cancellationToken.Register(ignore >> taskCompletionSource.SetCanceled)
                        targetContent.StreammingCount.Publish((+) 1)
                        do! taskCompletionSource.Task

                    functionWatch.Start()

                    logger.LogInformation("Function {functionName} is invoking", functionName)
                    context.Arguments.Add(Constants.ToolCallAgentId, agent.Id)
                    context.Arguments.Add(Constants.ToolCallLoopId, targetContent.LoopId)
                    context.Arguments.Add(Constants.ToolCallSourceId, targetContent.Id)

                    onFunctionStarted ()
                    targetContent.StreammingCount.Publish((+) 1)
                    do! next.Invoke context
                    targetContent.StreammingCount.Publish((+) 1)

                    logger.LogInformation("Function {functionName} is invoked", functionName)

                with ex ->
                    logger.LogError(ex, "Function {functionName} is failed", functionName)
                    do!
                        updateToolCall {
                            toolCall with
                                Result = ValueSome(ex.ToString())
                                DurationMs = int functionWatch.ElapsedMilliseconds
                                UserAction = ValueNone
                        }
                    targetContent.StreammingCount.Publish((+) 1)
                    raise ex

                do!
                    updateToolCall {
                        toolCall with
                            Result = context.Result.GetValue<obj>() |> ValueOption.ofObj
                            DurationMs = int functionWatch.ElapsedMilliseconds
                            UserAction = ValueNone
                    }
                transact (fun _ ->
                    targetContent.StreammingCount.Value <- targetContent.StreammingCount.Value + 1
                    targetContent.ProgressMessage.Value <- "Function invoked and go back to LLM chat completion"
                )
            }
        }

    member private _.GetPromptExecutionSettings(agent: Agent, model: Model, enableFunctions: bool) : PromptExecutionSettings =
        match model.Provider with
        | ModelProvider.OpenAI -> OpenAI.OpenAIPromptExecutionSettings(Temperature = agent.Temperature, TopP = agent.TopP)
        | ModelProvider.OpenAIAzure _ -> AzureOpenAI.AzureOpenAIPromptExecutionSettings(Temperature = agent.Temperature, TopP = agent.TopP)
        | ModelProvider.Google ->
            Google.GeminiPromptExecutionSettings(
                Temperature = agent.Temperature,
                TopP = agent.TopP,
                TopK = agent.TopK,
                ToolCallBehavior =
                    if enableFunctions then
                        Google.GeminiToolCallBehavior.AutoInvokeKernelFunctions
                    else
                        unbox null
            )
        | ModelProvider.Ollama ->
            Ollama.OllamaPromptExecutionSettings(Temperature = float32 agent.Temperature, TopP = float32 agent.TopP, TopK = agent.TopK)
        | ModelProvider.HuggingFace ->
            HuggingFace.HuggingFacePromptExecutionSettings(Temperature = float32 agent.Temperature, TopP = float32 agent.TopP, TopK = agent.TopK)
        | ModelProvider.MistralAI -> MistralAI.MistralAIPromptExecutionSettings(Temperature = agent.Temperature, TopP = agent.TopP)

    member private _.StartChat
        (
            agent: Agent,
            chatMessages: IList<ChatMessageContent>,
            targetContent: LoopContentWrapper,
            agents: Agent seq,
            ?modelId: int,
            ?cancellationToken: CancellationToken
        ) =
        task {
            let chatWatch = Stopwatch.StartNew()
            let totalChatMessagesCount = chatMessages.Count
            let textSearchProvider = new TextSearchProvider(textSearch)
            let oldItems = targetContent.Items.Value |> Seq.toList
            let oldThinkDurationMs = targetContent.ThinkDurationMs.Value

            let modelsCount =
                match modelId with
                | Some id when agent.AgentModels |> Seq.exists (fun x -> x.ModelId = id) -> 1
                | Some _ -> 0
                | None -> agent.AgentModels.Count

            let mutable shouldContinue = true
            let mutable modelIndex = 0
            while shouldContinue && modelIndex < modelsCount do
                // Try use model with specified order untill success
                let agentModel =
                    match modelId with
                    | Some id -> agent.AgentModels |> Seq.find (fun x -> x.ModelId = id)
                    | _ -> agent.AgentModels |> Seq.sortBy (fun x -> x.Order) |> Seq.item modelIndex
                let model = agentModel.Model
                let mutable stream = LoopContentText()

                let wrapperCancellationTokenSource = new CancellationTokenSource()

                let cancellationTokenSource =
                    match cancellationToken with
                    | None -> wrapperCancellationTokenSource
                    | Some token -> CancellationTokenSource.CreateLinkedTokenSource(wrapperCancellationTokenSource.Token, token)

                let appendTextToStreamAndIncreaseCount (text) = valueTask {
                    let! count = modelService.CountTokens(text)
                    stream.Append(text) |> ignore
                    targetContent.OutputTokens.Publish((+) (int64 count))
                }

                transact (fun _ ->
                    targetContent.Items.Clear()
                    targetContent.Items.Add(LoopContentItem.Text stream) |> ignore
                    targetContent.CancellationTokenSource.Value <- ValueSome wrapperCancellationTokenSource
                    targetContent.StreammingCount.Value <- 0
                    targetContent.ErrorMessage.Value <- ""
                    targetContent.ModelId.Value <- ValueSome model.Id
                    targetContent.ModelName.Value <- model.Name
                    targetContent.ThinkDurationMs.Value <- 0
                    targetContent.TotalDurationMs.Value <- 0
                    targetContent.OutputTokens.Value <- 0
                    targetContent.ProgressMessage.Value <- "Started"
                    targetContent.UpdatedAt.Value <- DateTime.Now
                )

                try
                    chatWatch.Restart()

                    targetContent.ProgressMessage.Publish $"Building kernel for model {model.Model}"
                    let! kernel = modelService.GetKernel(model.Id, timeoutMs = Math.Max(600_000, agent.MaxTimeoutMs))

                    let chatWithFunctions = agent.EnableTools && model.CanHandleFunctions
                    let chatOptions = this.GetPromptExecutionSettings(agent, model, chatWithFunctions)

                    if chatWithFunctions then
                        targetContent.ProgressMessage.Publish "Adding function filters"
                        kernel.FunctionInvocationFilters.Add(
                            this.CreateFunctionFilter(
                                agent,
                                targetContent,
                                (fun _ ->
                                    stream <- LoopContentText()
                                    transact (fun _ ->
                                        targetContent.ProgressMessage.Value <- "Invoking function"
                                        targetContent.Items.Add(LoopContentItem.Text stream) |> ignore
                                        targetContent.StreammingCount.Value <- targetContent.StreammingCount.Value + 1
                                    )
                                ),
                                cancellationTokenSource.Token
                            )
                        )

                        logger.LogInformation("Add tools for {model}", model.Model)
                        targetContent.ProgressMessage.Publish "Adding tools"

                        chatOptions.FunctionChoiceBehavior <- FunctionChoiceBehavior.Auto(autoInvoke = true)

                        let agentIds =
                            agent.AgentFunctions
                            |> Seq.choose (
                                function
                                | { Target = AgentFunctionTarget.Agent id } -> Some id
                                | _ -> None
                            )
                        let agents =
                            if agent.EnableAgentCall then
                                agents |> Seq.filter (fun x -> (agent.EnableSelfCall && x.Id = agent.Id) || agentIds.Contains x.Id)
                            else
                                Seq.empty

                        kernel.Plugins.AddFromFunctions(
                            Constants.AgentPluginName,
                            functions =
                                (agents
                                 |> Seq.map (fun ag ->
                                     functionService.CreateInvokeAgentFunc(agent.Name, ag.Id, targetContent.LoopId, targetContent.Id)
                                 )),
                            description = "Call other agents for help according to their capability and definitions"
                        )
                        |> ignore

                        let toolIds =
                            agent.AgentFunctions
                            |> Seq.choose (
                                function
                                | { Target = AgentFunctionTarget.Function id } -> Some id
                                | _ -> None
                            )
                        let! plugins = functionService.GetKernelPlugins(toolIds, cancellationToken = cancellationTokenSource.Token)
                        kernel.Plugins.AddRange(plugins)

                    //let! aiContext = textSearchProvider.ModelInvokingAsync([||], cancellationToken = cancellationTokenSource.Token)
                    //kernel.Plugins.AddFromAIContext(aiContext, "system_memory_rag")

                    let chatClient = kernel.GetRequiredService<IChatCompletionService>()

                    targetContent.ProgressMessage.Publish "Prepare content"
                    let isSystemPromptAtHead =
                        if chatMessages.Count = 0 then
                            false
                        else
                            chatMessages[0].Role = AuthorRole.System

                    let limit = Math.Max((if isSystemPromptAtHead then 2 else 1), agent.MaxHistory)

                    while chatMessages.Count > limit do
                        chatMessages.RemoveAt(if isSystemPromptAtHead then 1 else 0)

                    for chatMessage in chatMessages do
                        if chatMessage.Role = AuthorRole.Assistant && chatMessage.AuthorName <> agent.Name then
                            chatMessage.Role <- AuthorRole.User

                    logger.LogInformation(
                        "Start chat with {agent} {model} {reducedMessages}/{providedMessages}",
                        agent.Name,
                        model.Name,
                        chatMessages.Count,
                        totalChatMessagesCount
                    )

                    targetContent.ProgressMessage.Publish $"Calling model {model.Model} with {chatMessages.Count} messages"

                    if agent.EnableStreaming then
                        let mutable hasReasoningContent = true
                        let mutable lastUpdate = chatWatch.ElapsedMilliseconds
                        for result in
                            chatClient.GetStreamingChatMessageContentsAsync(
                                ChatHistory(chatMessages),
                                kernel = kernel,
                                executionSettings = chatOptions,
                                cancellationToken = cancellationTokenSource.Token
                            ) do

                            let normalProcess () = valueTask {
                                for content in result.Items do
                                    match content with
                                    | :? StreamingTextContent as x ->
                                        match x.Text with
                                        | null -> ()
                                        | text -> do! appendTextToStreamAndIncreaseCount text
                                    | :? StreamingFunctionCallUpdateContent -> ()
                                    | _ ->
                                        let text = string content
                                        do! appendTextToStreamAndIncreaseCount text
                            }

                            if result <> null then
                                do! Task.Delay 1 // So UI can have chance to accept events
                                // Try parse reasoning data first
                                if hasReasoningContent then
                                    try
                                        let jsonNode = JsonNode.Parse(ModelReaderWriter.Write(result.InnerContent))
                                        let reasoningContent = jsonNode["choices"][0]["delta"]["reasoning_content"]

                                        let reasoningContent =
                                            match reasoningContent with
                                            | null -> failwith "no reasoning content"
                                            | reasoningContent ->
                                                if stream.Length = 0 then
                                                    "<think>" + reasoningContent.ToString()
                                                else
                                                    reasoningContent.ToString()
                                        stream.Append(reasoningContent)
                                    with ex ->
                                        logger.LogDebug(ex, "Failed to parse reasoning content")
                                        hasReasoningContent <- false
                                        if stream.Length > 0 then
                                            let text = "</think>\n\n"
                                            stream.Append(text)
                                            targetContent.ThinkDurationMs.Publish(int chatWatch.ElapsedMilliseconds)

                                        do! normalProcess ()
                                else
                                    do! normalProcess ()

                                if chatWatch.ElapsedMilliseconds - lastUpdate > 200 then
                                    lastUpdate <- chatWatch.ElapsedMilliseconds
                                    targetContent.StreammingCount.Publish((+) 1)

                    else
                        let! result =
                            chatClient.GetChatMessageContentsAsync(
                                ChatHistory(chatMessages),
                                kernel = kernel,
                                executionSettings = chatOptions,
                                cancellationToken = cancellationTokenSource.Token
                            )
                        for contents in result do
                            for content in contents.Items do
                                match content with
                                | :? TextContent as x ->
                                    match x.Text with
                                    | null -> ()
                                    | text -> do! appendTextToStreamAndIncreaseCount text
                                | :? FunctionCallContent as fc -> ()
                                | _ ->
                                    let text = string content
                                    do! appendTextToStreamAndIncreaseCount text

                    shouldContinue <- false
                    logger.LogInformation("Chat completed for {loopContentId}", targetContent.Id)

                    transact (fun _ ->
                        targetContent.ProgressMessage.Value <- ""
                        targetContent.ErrorMessage.Value <-
                            if cancellationTokenSource.IsCancellationRequested then
                                "User cancelled this!"
                            else
                                ""
                    )

                    try
                        do! modelService.UpdateUsedTime(model.Id)
                    with ex ->
                        logger.LogError(ex, "Update used time failed for {model}", model.Name)

                with
                | :? TaskCanceledException as ex ->
                    logger.LogWarning("Complete chat cancelled with {name} {model}", model.Name, model.Model)
                    shouldContinue <- false
                    transact (fun _ ->
                        targetContent.ThinkDurationMs.Value <- oldThinkDurationMs
                        targetContent.ErrorMessage.Value <- ex.Message
                        targetContent.Items.Value <- IndexList.ofList oldItems
                    )
                | ex ->
                    logger.LogError(ex, "Complete chat failed with {name} {model}", model.Name, model.Model)
                    modelIndex <- modelIndex + 1
                    transact (fun _ ->
                        targetContent.ThinkDurationMs.Value <- 0
                        targetContent.ErrorMessage.Value <- ex.Message
                    )

            // Finish and wrapup
            transact (fun _ ->
                targetContent.CancellationTokenSource.Value <- ValueNone
                targetContent.StreammingCount.Value <- -1
                targetContent.TotalDurationMs.Value <- int chatWatch.ElapsedMilliseconds
                targetContent.UpdatedAt.Value <- DateTime.Now
            )

            try
                do! agentService.UpdateUsedTime(agent.Id)
                match targetContent.ModelId.Value with
                | ValueNone -> ()
                | ValueSome modelId -> do! modelService.IncreaseOutputTokens(modelId, targetContent.OutputTokens.Value)
            with ex ->
                logger.LogError(ex, "Update used time failed for {agent}", agent.Name)
        }

    member private _.StartSummarize(agent: Agent, chatMessages: IList<ChatMessageContent>) = valueTask {
        let summaryBuilder = StringBuilder()

        let mutable modelIndex = 0
        let mutable shouldContinue = true

        while shouldContinue && modelIndex < agent.AgentModels.Count do
            let agentModel = agent.AgentModels |> Seq.sortBy (fun x -> x.Order) |> Seq.item modelIndex
            let model = agentModel.Model

            try
                let! kernel = modelService.GetKernel(model.Id, timeoutMs = Math.Max(600_000, agent.MaxTimeoutMs))

                let chatClient = kernel.GetRequiredService<IChatCompletionService>()
                let chatOptions = this.GetPromptExecutionSettings(agent, model, enableFunctions = false)

                logger.LogInformation("Start summary with {agent} {model} {messages}", agent.Name, model.Name, chatMessages.Count)
                let! result = chatClient.GetChatMessageContentAsync(ChatHistory(chatMessages), executionSettings = chatOptions)
                for content in result.Items do
                    match content with
                    | :? TextContent as x -> x.Text
                    | _ -> string content
                    |> summaryBuilder.Append
                    |> ignore

                shouldContinue <- false

                try
                    do! modelService.UpdateUsedTime(model.Id)
                with ex ->
                    logger.LogError(ex, "Update used time failed for {model}", model.Name)

            with ex ->
                logger.LogError(ex, "Complete chat failed with {name} {model}", model.Name, model.Model)
                raise ex

        try
            do! agentService.UpdateUsedTime(agent.Id)
        with ex ->
            logger.LogError(ex, "Update used time failed for {agent}", agent.Name)

        let result = summaryBuilder.ToString()
        if result.StartsWith("<think>") && result.Contains("</think>") then
            return result.Substring(result.IndexOf("</think>") + 8)
        else
            return result
    }


    interface ILoopService with
        member _.Send(loopId, message, ?agentId, ?modelId, ?author, ?role, ?includeHistory, ?ignoreInput, ?sourceLoopContentId, ?cancellationToken) = valueTask {
            let author = author |> Option.defaultValue "ME"
            let role = role |> Option.defaultValue LoopContentAuthorRole.User
            let ignoreInput = ignoreInput |> Option.defaultValue false
            let includeHistory = includeHistory |> Option.defaultValue true

            let! agents =
                agentService.GetAgentsWithCache()
                |> ValueTask.map (
                    List.filter (fun x ->
                        match x.Type with
                        | AgentType.TitleBuilder
                        | AgentType.ImageToText -> false
                        | AgentType.General -> true
                    )
                )
            let agent = agentId |> Option.bind (fun agentId -> agents |> Seq.tryFind (fun x -> x.Id = agentId))

            let message =
                match agent, message with
                | Some x, NullOrEmptyString -> $"@{x.Name}"
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
            | None -> logger.LogInformation("No agent is specified")
            | Some agent ->
                let! contents = loopContentService.GetOrCreateContentsCache(loopId)
                let chatMessages = List<ChatMessageContent>()
                if includeHistory then
                    for item in contents |> AList.force do
                        let! content = item.ToChatMessageContent(documentService)
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
                    do! this.StartChat(agent, chatMessages, outputContent, agents, ?modelId = modelId, ?cancellationToken = cancellationToken)

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
                    let! agents =
                        agentService.GetAgentsWithCache()
                        |> ValueTask.map (
                            List.filter (fun x ->
                                match x.Type with
                                | AgentType.TitleBuilder
                                | AgentType.ImageToText -> false
                                | AgentType.General -> true
                            )
                        )

                    match agents |> Seq.tryFind (fun x -> x.Id = agentId) with
                    | None -> failwith $"Agent {agentId} is not found"
                    | Some agent ->
                        do! loopContentService.DeleteLoopContentsOfSource(loopId, loopContentId)

                        let chatMessages = List<ChatMessageContent>()

                        chatMessages.Add(ChatMessageContent(AuthorRole.System, agent.Prompt))

                        for item in contents |> Seq.takeWhile (fun x -> x.Id <> loopContentId) do
                            let! content = item.ToChatMessageContent(documentService)
                            chatMessages.Add(content)

                        do! this.StartChat(agent, chatMessages, target, agents, ?modelId = modelId)

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
                        let! content = item.ToChatMessageContent(documentService)
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

                        let! result = this.StartSummarize(agent, chatMessages)
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
