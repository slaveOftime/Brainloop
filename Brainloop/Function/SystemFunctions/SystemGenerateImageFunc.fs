﻿namespace Brainloop.Function.SystemFunctions

open System
open System.ComponentModel
open System.IO
open System.Text
open System.Net.Http
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Microsoft.SemanticKernel
open IcedTasks
open Brainloop.Db
open Brainloop.Model
open Brainloop.Memory
open Brainloop.Share
open Brainloop.Function


type GenerateImageArgs() =
    [<Description "Complete context for generating the image">]
    member val Prompt: string = "" with get, set


type SystemGenerateImageFunc
    (documentService: IDocumentService, modelService: IModelService, logger: ILogger<SystemGenerateImageFunc>, loggerFactory: ILoggerFactory) as this
    =

    member private _.DownloadImageAsync(url: string, ?proxy) = valueTask {
        use httpClient = HttpClient.Create(?proxy = proxy)
        let! response = httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
        return! response.Content.ReadAsStreamAsync()
    }

    member _.Create(fn: Function, config: SystemGenerateImageConfig, ?cancellationToken: CancellationToken) =
        KernelFunctionFactory.CreateFromMethod(
            Func<KernelArguments, ValueTask<string>>(fun kernelArgs -> valueTask {
                try
                    let arguments = kernelArgs.Get<GenerateImageArgs>()
                    logger.LogInformation("Generate image")
                    match config with
                    | SystemGenerateImageConfig.LLMModel config ->
                        let! model = modelService.GetModelWithCache(config.ModelId)
                        let! kernel = modelService.GetKernel(config.ModelId)

                        let sourceLoopContentId = kernelArgs.LoopContentId

                        let textToImageService = kernel.GetRequiredService<TextToImage.ITextToImageService>()
                        let input = TextContent(arguments.Prompt)
                        let executionSettings = PromptExecutionSettings()

                        let! images =
                            textToImageService.GetImageContentsAsync(
                                input,
                                executionSettings = executionSettings,
                                kernel = kernel,
                                ?cancellationToken = cancellationToken
                            )

                        let imageString = StringBuilder()

                        let addImageStream (notes: string) stream = valueTask {
                            let date = DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss")
                            let name = $"tool-generated-{date}.png"
                            let! id =
                                documentService.SaveFile(
                                    name,
                                    stream,
                                    ?loopContentId = ValueOption.toOption sourceLoopContentId,
                                    ?cancellationToken = cancellationToken
                                )
                            imageString.Append("![").Append(name).Append("](/api/memory/document/").Append(id) |> ignore
                            if String.IsNullOrWhiteSpace(notes) |> not then
                                imageString.AppendLine().AppendLine(notes) |> ignore
                        }

                        for image in images do
                            if image.Data.HasValue then
                                use stream = new MemoryStream(image.Data.Value.ToArray())
                                do! addImageStream arguments.Prompt stream
                            else if image.DataUri <> null then
                                use! stream = this.DownloadImageAsync(image.DataUri, proxy = model.Proxy)
                                do! addImageStream arguments.Prompt stream
                            else
                                match image.InnerContent with
                                | :? OpenAI.Images.GeneratedImage as image ->
                                    if image.ImageBytes <> null then
                                        use stream = new MemoryStream(image.ImageBytes.ToArray())
                                        do! addImageStream image.RevisedPrompt stream
                                    else if image.ImageUri <> null then
                                        use! stream = this.DownloadImageAsync(image.ImageUri.ToString(), proxy = model.Proxy)
                                        do! addImageStream image.RevisedPrompt stream
                                | _ -> ()

                        return imageString.ToString()

                with ex ->
                    logger.LogError(ex, "Failed to execute command")
                    raise ex
                    return ""
            }),
            JsonSerializerOptions.createDefault (),
            functionName = fn.Name,
            description = fn.Description,
            loggerFactory = loggerFactory,
            parameters = KernelParameterMetadata.FromInstance(GenerateImageArgs()),
            returnParameter = KernelReturnParameterMetadata(Description = "Markdown string with image content", ParameterType = typeof<string>)
        )
