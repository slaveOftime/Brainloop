namespace Brainloop.Handlers

open System.IO
open System.Text
open IcedTasks
open Microsoft.Extensions.Logging
open Microsoft.SemanticKernel
open Microsoft.SemanticKernel.ChatCompletion
open Brainloop.Db
open Brainloop.Share
open Brainloop.Model
open Brainloop.Agent


type GetTextFromImageHandler(agentService: IAgentService, modelService: IModelService, logger: ILogger<GetTextFromImageHandler>) =

    interface IGetTextFromImageHandler with
        member _.Handle(imageFile, cancellationToken) = valueTask {
            if File.Exists imageFile |> not then failwithf "File is not found %s" imageFile

            let! agent =
                agentService.GetAgentsWithCache()
                |> ValueTask.map (
                    Seq.tryFind (
                        function
                        | { Type = AgentType.GetTextFromImage } -> true
                        | _ -> false
                    )
                )
                |> ValueTask.map (
                    function
                    | None -> failwith $"There is no agent defined as {AgentType.GetTextFromImage}"
                    | Some x -> x
                )

            if agent.AgentModels.Count = 0 then
                failwith "No model is defined for this agent"

            let messages = ChatHistory()
            messages.AddSystemMessage(agent.Prompt)

            let userMessage = ChatMessageContentItemCollection()
            userMessage.Add(ImageContent(File.ReadAllBytes imageFile, "image/png"))
            messages.AddUserMessage(userMessage)

            let text = StringBuilder()

            let mutable index = 0
            let mutable shouldContinue = true
            while index < agent.AgentModels.Count && shouldContinue do
                try
                    text.Clear() |> ignore

                    let model = agent.AgentModels |> Seq.sortBy (fun x -> x.Order) |> Seq.item index
                    let! kernel = modelService.GetKernel(model.ModelId)
                    let changeCompletionService = kernel.GetRequiredService<IChatCompletionService>()

                    logger.LogInformation("Get text from image with {agent} - {model}", agent.Name, model.Model.Model)

                    let results = changeCompletionService.GetStreamingChatMessageContentsAsync(messages, ?cancellationToken = cancellationToken)
                    for result in results do
                        for content in result.Items do
                            match content with
                            | :? StreamingTextContent as x ->
                                match x.Text with
                                | null -> ()
                                | x -> text.Append(x) |> ignore
                            | _ -> ()

                    shouldContinue <- false
                with ex ->
                    index <- index + 1
                    logger.LogError(ex, "Get text from image failed")


            return text.ToString()
        }
