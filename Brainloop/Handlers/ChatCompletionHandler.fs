namespace Brainloop.Handlers

open System
open System.Linq
open System.Text
open System.Threading
open System.Threading.Tasks
open System.Diagnostics
open System.Text.Json.Nodes
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
open Brainloop.Function
open Brainloop.Agent
open Brainloop.Share
open Brainloop.Loop


type ChatCompletionHandler
    (modelService: IModelService, agentService: IAgentService, loopContentService: ILoopContentService, logger: ILogger<ChatCompletionHandler>) as this
    =

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

                    for kv in toolCall.Arguments do
                        context.Arguments[kv.Key] <- kv.Value

                    if context.Function.PluginName = SystemFunction.PluginName then
                        context.Arguments.Add(Strings.ToolCallAgentId, agent.Id)
                        context.Arguments.Add(Strings.ToolCallLoopId, targetContent.LoopId)
                        context.Arguments.Add(Strings.ToolCallLoopContentId, targetContent.Id)

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


    member private _.SetSystemPromptsForAgent(agent: Agent, chatMessages: ChatHistory) =
        let sb =
            StringBuilder()
                .Append("Current date is ")
                .Append(DateTime.Now.ToShortDateString())
                .AppendLine()
                .Append("You are an agent named '")
                .Append(agent.Name)
                .AppendLine("'")
                .Append(agent.Prompt)
        chatMessages.Insert(0, ChatMessageContent(AuthorRole.System, sb.ToString()))


    member private _.GetInputTokens(metadata: Collections.Generic.IReadOnlyDictionary<string, obj | null> | null) =
        match metadata with
        | null -> ValueNone
        | metadata ->
            match metadata.TryGetValue "PromptTokenCount" with
            | true, x ->
                match string x with
                | INT64 x -> ValueSome x
                | _ -> ValueNone
            | _ -> ValueNone

    member private _.GetOutputTokens(metadata: Collections.Generic.IReadOnlyDictionary<string, obj | null> | null) =
        match metadata with
        | null -> ValueNone
        | metadata ->
            match metadata.TryGetValue "CandidatesTokenCount" with
            | true, x ->
                match string x with
                | INT64 x -> ValueSome x
                | _ -> ValueNone
            | _ -> ValueNone

    member private _.GetInputOutputTokens(contents: ChatMessageContent, message: string, currentOutputCount: int64) = valueTask {
        match contents.InnerContent with
        | :? OpenAI.Chat.ChatCompletion as x when x.Usage <> null ->
            return int64 x.Usage.InputTokenCount, currentOutputCount + int64 x.Usage.OutputTokenCount
        | :? Google.GeminiChatMessageContent as x when x.Metadata <> null ->
            return int64 x.Metadata.PromptTokenCount, int64 x.Metadata.CandidatesTokenCount
        | _ ->
            let inputTokens = this.GetInputTokens contents.Metadata |> ValueOption.defaultValue 0L
            let! outputTokens =
                match this.GetOutputTokens contents.Metadata with
                | ValueSome x -> ValueTask.singleton x
                | _ -> modelService.CountTokens(message) |> ValueTask.map (int64 >> (+) currentOutputCount)
            return inputTokens, outputTokens
    }
    member private _.GetInputOutputTokens(result: StreamingChatMessageContent, message: string, currentOutputCount: int64) = valueTask {
        match result.InnerContent with
        | :? OpenAI.Chat.StreamingChatCompletionUpdate as x when x.Usage <> null ->
            return int64 x.Usage.InputTokenCount, currentOutputCount + int64 x.Usage.OutputTokenCount
        | :? Google.GeminiStreamingChatMessageContent as x when x.Metadata <> null ->
            return int64 x.Metadata.PromptTokenCount, int64 x.Metadata.CandidatesTokenCount
        | _ ->
            let inputTokens = this.GetInputTokens result.Metadata |> ValueOption.defaultValue 0L
            let! outputTokens =
                match this.GetOutputTokens result.Metadata with
                | ValueSome x -> ValueTask.singleton x
                | _ -> modelService.CountTokens(message) |> ValueTask.map (int64 >> (+) currentOutputCount)
            return inputTokens, outputTokens
    }


    interface IChatCompletionHandler with
        member _.Handle(agentId, contents, targetContent, ?modelId, ?functions, ?cancellationToken) = valueTask {
            let hasSpecificFunctions =
                match functions with
                | Some fns -> Seq.isEmpty fns |> not
                | None -> false

            let oldItems = targetContent.Items |> Seq.toList

            let trryRestoreOldItems () =
                if
                    targetContent.Items.IsEmpty
                    || (targetContent.Items.Count = 1
                        && match targetContent.Items[0] with
                           | LoopContentItem.Text x -> x.Length = 0
                           | _ -> false)
                then
                    targetContent.Items.AddRange(oldItems) |> ignore

            let! agents = agentService.GetAgentsWithCache()

            let agent =
                agents
                |> Seq.tryFind (fun x -> x.Id = agentId)
                |> function
                    | Some a -> a
                    | None -> failwith $"Agent with ID {agentId} not found"

            let chatWatch = Stopwatch.StartNew()

            let modelsCount =
                match modelId with
                | Some _ -> 1
                | None -> agent.AgentModels.Count

            let mutable shouldContinue = true
            let mutable modelIndex = 0
            while shouldContinue && modelIndex < modelsCount do
                // Try use model with specified order untill success
                let! model = valueTask {
                    match modelId with
                    | Some id -> return! modelService.GetModel(id)
                    | None ->
                        let agentModel =
                            match modelId with
                            | Some id -> agent.AgentModels |> Seq.find (fun x -> x.ModelId = id)
                            | _ -> agent.AgentModels |> Seq.sortBy (fun x -> x.Order) |> Seq.item modelIndex
                        return agentModel.Model
                }

                let mutable stream = LoopContentText()

                let wrapperCancellationTokenSource = new CancellationTokenSource()

                let cancellationTokenSource =
                    match cancellationToken with
                    | None -> wrapperCancellationTokenSource
                    | Some token -> CancellationTokenSource.CreateLinkedTokenSource(wrapperCancellationTokenSource.Token, token)

                transact (fun _ ->
                    targetContent.Items.Clear()
                    targetContent.Items.Add(LoopContentItem.Text stream) |> ignore
                    targetContent.IncludedHistoryCount.Value <- Math.Max(0, contents.Count - 1)
                    targetContent.CancellationTokenSource.Value <- ValueSome wrapperCancellationTokenSource
                    targetContent.StreammingCount.Value <- 0
                    targetContent.ErrorMessage.Value <- ""
                    targetContent.ModelId.Value <- ValueSome model.Id
                    targetContent.ThinkDurationMs.Value <- 0
                    targetContent.TotalDurationMs.Value <- 0
                    targetContent.InputTokens.Value <- 0
                    targetContent.OutputTokens.Value <- 0
                    targetContent.ProgressMessage.Value <- "Started"
                    targetContent.UpdatedAt.Value <- DateTime.Now
                )

                try
                    chatWatch.Restart()

                    targetContent.ProgressMessage.Publish $"Building kernel for model {model.Model}"
                    let! kernel = modelService.GetKernel(model.Id, timeoutMs = Math.Max(600_000, agent.MaxTimeoutMs))

                    let chatWithFunctions = (agent.EnableTools || hasSpecificFunctions) && model.CanHandleFunctions
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

                        chatOptions.FunctionChoiceBehavior <-
                            FunctionChoiceBehavior.Auto(
                                autoInvoke = true,
                                options =
                                    FunctionChoiceBehaviorOptions(
                                        AllowParallelCalls = false,
                                        AllowConcurrentInvocation = false,
#if DEBUG
                                        AllowStrictSchemaAdherence = true,
#endif
                                        RetainArgumentTypes = true
                                    )
                            )

                        match functions with
                        | Some fns ->
                            kernel.Plugins.Add(
                                KernelPluginFactory.CreateFromFunctions("userFunctions", functions = fns, description = "Functions provided by user")
                            )
                            |> ignore
                        | None ->
                            logger.LogInformation("Add tools for {model}", model.Model)
                            targetContent.ProgressMessage.Publish "Adding tools"
                            let! plugins = agentService.GetKernelPlugins(agentId, cancellationToken = cancellationTokenSource.Token)
                            kernel.Plugins.AddRange(plugins)

                    //let! aiContext = textSearchProvider.ModelInvokingAsync([||], cancellationToken = cancellationTokenSource.Token)
                    //kernel.Plugins.AddFromAIContext(aiContext, "system_memory_rag")

                    let chatClient = kernel.GetRequiredService<IChatCompletionService>()

                    targetContent.ProgressMessage.Publish "Prepare content"

                    let chatHistory = ChatHistory()
                    for content in contents do
                        let! chatMessage = loopContentService.ToChatMessageContent(content, model = model)
                        if chatMessage.Items.Count > 0 then
                            chatMessage.Role <- if content.Author <> agent.Name then AuthorRole.User else AuthorRole.Assistant
                            chatHistory.Add(chatMessage)
                        else
                            logger.LogWarning("Chat message {contentId} has no items for chat", content.Id)

                    this.SetSystemPromptsForAgent(agent, chatHistory)

                    logger.LogInformation("Start chat with {agent} {model} {messages}", agent.Name, model.Name, contents.Count)

                    targetContent.ProgressMessage.Publish $"Calling model {model.Model} with {contents.Count} messages"

                    if agent.EnableStreaming then
                        let mutable hasReasoningContent = true
                        let mutable lastUpdate = chatWatch.ElapsedMilliseconds
                        for result in
                            chatClient.GetStreamingChatMessageContentsAsync(
                                chatHistory,
                                kernel = kernel,
                                executionSettings = chatOptions,
                                cancellationToken = cancellationTokenSource.Token
                            ) do

                            if result <> null then
                                let appendTextToStreamAndIncreaseCount (text) = valueTask {
                                    let! inputTokens, outputTokens = this.GetInputOutputTokens(result, text, targetContent.OutputTokens.Value)
                                    stream.Append(text) |> ignore
                                    transact (fun _ ->
                                        if targetContent.InputTokens.Value = 0 then
                                            targetContent.InputTokens.Value <- inputTokens
                                        targetContent.OutputTokens.Value <- outputTokens
                                    )
                                }

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
                                chatHistory,
                                kernel = kernel,
                                executionSettings = chatOptions,
                                cancellationToken = cancellationTokenSource.Token
                            )

                        for contents in result do
                            let appendTextToStreamAndIncreaseCount (text) = valueTask {
                                let! inputTokens, outputTokens = this.GetInputOutputTokens(contents, text, targetContent.OutputTokens.Value)
                                stream.Append(text) |> ignore
                                transact (fun _ ->
                                    targetContent.InputTokens.Value <- inputTokens
                                    targetContent.OutputTokens.Value <- outputTokens
                                )
                            }
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
                | :? OperationCanceledException as ex ->
                    logger.LogWarning("Complete chat cancelled with {name} {model}", model.Name, model.Model)
                    shouldContinue <- false
                    transact (fun _ ->
                        targetContent.ErrorMessage.Value <- ex.Message
                        trryRestoreOldItems ()
                    )
                | ex ->
                    logger.LogError(ex, "Complete chat failed with {name} {model}", model.Name, model.Model)

                    modelIndex <- modelIndex + 1

                    let errorMsg =
                        ex.Message
                        + ". "
                        + match ex with
                          | :? HttpOperationException as ex ->
                              logger.LogError("Chat completion failed with {content}", ex.ResponseContent)
                              ex.ResponseContent
                          | _ -> ""

                    transact (fun _ ->
                        targetContent.ThinkDurationMs.Value <- 0
                        targetContent.ErrorMessage.Value <- errorMsg
                        trryRestoreOldItems ()
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
                | ValueSome modelId ->
                    do! modelService.IncreaseTokensUsage(modelId, uint64 targetContent.InputTokens.Value, uint64 targetContent.OutputTokens.Value)
            with ex ->
                logger.LogError(ex, "Update used time failed for {agent}", agent.Name)
        }
