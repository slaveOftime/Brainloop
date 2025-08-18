namespace Brainloop.Handlers

open System
open System.Text
open Microsoft.Extensions.Logging
open Microsoft.SemanticKernel
open Microsoft.SemanticKernel.ChatCompletion
open Microsoft.SemanticKernel.Connectors
open IcedTasks
open Fun.Result
open Brainloop.Db
open Brainloop.Model
open Brainloop.Agent
open Brainloop.Share


type CreateTitleHandler(modelService: IModelService, agentService: IAgentService, dbService: IDbService, logger: ILogger<CreateTitleHandler>) as this
    =

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


    member private _.GetLoopCategoryTreePrompt() = valueTask {
        let! loopCategoryTree = dbService.DbContext.Select<LoopCategory>().ToListAsync(fun x -> x.Id, x.Name, x.ParentId)
        let sb = StringBuilder()
        let rec buildTree (parentId: int Nullable) (path: string) =
            let children = loopCategoryTree |> Seq.filter (fun (_, _, p) -> p = parentId) |> Seq.sortBy (fun (_, name, _) -> name)
            for id, name, _ in children do
                let path = $"{path}/{name}"
                sb.Append(path).Append(" [Id:").Append(id).Append("]").AppendLine() |> ignore
                buildTree (Nullable id) ("  " + path)
        buildTree (Nullable()) ""
        return sb.ToString()
    }


    interface ICreateTitleHandler with
        member _.Handle(agentId, chatMessages, ?cancellationToken) = valueTask {
            let! agent =
                agentService.TryGetAgentWithCache(agentId)
                |> ValueTask.map (
                    function
                    | ValueSome agent -> agent
                    | ValueNone -> failwith $"Agent with ID {agentId} not found"
                )

            let summaryBuilder = StringBuilder()
            let mutable loopCategoryId: int voption = ValueNone

            let mutable modelIndex = 0
            let mutable shouldContinue = true

            let isCancelled () =
                match cancellationToken with
                | Some token -> token.IsCancellationRequested
                | None -> false

            while shouldContinue && modelIndex < agent.AgentModels.Count && not (isCancelled ()) do
                let agentModel = agent.AgentModels |> Seq.sortBy (fun x -> x.Order) |> Seq.item modelIndex
                let model = agentModel.Model

                try
                    let! kernel = modelService.GetKernel(model.Id, timeoutMs = Math.Max(600_000, agent.MaxTimeoutMs))

                    let chatClient = kernel.GetRequiredService<IChatCompletionService>()
                    let chatOptions = this.GetPromptExecutionSettings(agent, model, enableFunctions = false)

                    logger.LogInformation("Start summary with {agent} {model} {messages}", agent.Name, model.Name, chatMessages.Count)
                    let! result =
                        chatClient.GetChatMessageContentAsync(
                            ChatHistory(chatMessages),
                            executionSettings = chatOptions,
                            ?cancellationToken = cancellationToken
                        )
                    for content in result.Items do
                        match content with
                        | :? TextContent as x -> x.Text
                        | _ -> string content
                        |> summaryBuilder.Append
                        |> ignore

                    shouldContinue <- false

                    try
                        let! categoryPrompts = this.GetLoopCategoryTreePrompt()
                        let items = ChatMessageContentItemCollection()
                        items.Add(TextContent("Here is all the categories with their `Id` append to every category's end:"))
                        items.Add(TextContent(categoryPrompts))
                        items.Add(TextContent("Please tell me which category should belong for below content:"))
                        items.Add(TextContent(summaryBuilder.ToString()))
                        items.Add(TextContent("You should analysis and only return the `Id` for me"))

                        let chatHistory = ChatHistory()
                        chatHistory.AddUserMessage(items)

                        let! result =
                            chatClient.GetChatMessageContentAsync(
                                chatHistory,
                                executionSettings = chatOptions,
                                ?cancellationToken = cancellationToken
                            )
                        loopCategoryId <-
                            match string result with
                            | INT32 id -> ValueSome id
                            | x ->
                                logger.LogWarning("Classify failed with {result}", x)
                                ValueNone

                    with ex ->
                        logger.LogError(ex, "Failed to classify title")

                    try
                        do! modelService.UpdateUsedTime(model.Id)
                    with ex ->
                        logger.LogError(ex, "Update used time failed for {model}", model.Name)

                with ex ->
                    logger.LogError(ex, "Complete chat failed with {name} {model}", model.Name, model.Model)
                    modelIndex <- modelIndex + 1

            if modelIndex = agent.AgentModels.Count then
                logger.LogWarning("All models failed to summarize for {agent}", agent.Name)
                failwith "All models failed to summarize"

            try
                do! agentService.UpdateUsedTime(agent.Id)
            with ex ->
                logger.LogError(ex, "Update used time failed for {agent}", agent.Name)

            return {
                Title =
                    let result = summaryBuilder.ToString()
                    if result.StartsWith("<think>") && result.Contains("</think>") then
                        result.Substring(result.IndexOf("</think>") + 8)
                    else
                        result
                LoopCategoryId = loopCategoryId
            }
        }
