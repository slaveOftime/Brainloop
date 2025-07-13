namespace Brainloop.Loop

open System
open System.Text
open Microsoft.Extensions.Logging
open Microsoft.SemanticKernel
open Microsoft.SemanticKernel.ChatCompletion
open Microsoft.SemanticKernel.Connectors
open IcedTasks
open Brainloop.Db
open Brainloop.Model
open Brainloop.Agent
open Brainloop.Handler


type BuildTitleHandler(modelService: IModelService, agentService: IAgentService, logger: ILogger<ChatCompletionHandler>) as this =

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


    interface IBuildTitleHandler with
        member _.Handle(agentId, chatMessages, ?cancellationToken) = valueTask {
            let! agent =
                agentService.TryGetAgentWithCache(agentId)
                |> ValueTask.map (
                    function
                    | ValueSome agent -> agent
                    | ValueNone -> failwith $"Agent with ID {agentId} not found"
                )

            let summaryBuilder = StringBuilder()

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
