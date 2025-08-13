namespace Brainloop.Model

open System
open System.Net.Http
open System.Net.Http.Json
open System.Net.Http.Headers
open System.Text.Json
open System.ClientModel
open System.ClientModel.Primitives
open Microsoft.Extensions.Caching.Memory
open Microsoft.SemanticKernel
open OpenAI
open IcedTasks
open OllamaSharp
open Fun.Result
open Brainloop.Db
open Brainloop.Share


type ModelService(dbService: IDbService, memoryCache: IMemoryCache) as this =

    static let tokenCounter = lazy (SharpToken.GptEncoding.GetEncoding("cl100k_base"))


    member _.CreateBaseHttpClient(model: Model, ?timeoutMs: int) =
        HttpClient.Create(
            ?headers = (model.ApiProps |> ValueOption.map _.Headers |> ValueOption.toOption),
            baseUrl = model.Api,
            proxy = model.Proxy,
            ?timeoutMs = timeoutMs,
            ?addtionalRequestBody = Option.ofObj model.Request
        )

    member _.CreateHttpClient(model: Model, ?timeoutMs: int) =
        let httpClient = this.CreateBaseHttpClient(model, ?timeoutMs = timeoutMs)

        match model.ApiKey with
        | SafeString key -> httpClient.DefaultRequestHeaders.Authorization <- AuthenticationHeaderValue key
        | _ -> ()

        httpClient


    member _.CreateKernelBuilder(model: Model, ?timeoutMs: int) =
        let kernelBuilder = Kernel.CreateBuilder()
        let httpClient = this.CreateHttpClient(model, ?timeoutMs = timeoutMs)

        match model.Provider with
        | ModelProvider.Ollama ->
            kernelBuilder
                .AddOllamaTextGeneration(model.Model, httpClient = httpClient)
                .AddOllamaChatCompletion(model.Model, httpClient = httpClient)
                .AddOllamaEmbeddingGenerator(model.Model, httpClient = httpClient)
            |> ignore
        | ModelProvider.OpenAI ->
            kernelBuilder
                .AddOpenAIChatCompletion(model.Model, new Uri(model.Api), model.ApiKey, httpClient = httpClient)
                .AddOpenAIEmbeddingGenerator(model.Model, model.ApiKey, httpClient = httpClient)
                .AddOpenAITextToImage(model.Model, model.ApiKey, httpClient = httpClient)
                .AddOpenAITextToAudio(model.Model, model.ApiKey, httpClient = httpClient)
                .AddOpenAIAudioToText(model.Model, model.ApiKey, httpClient = httpClient)
            |> ignore
        | ModelProvider.OpenAIAzure config ->
            kernelBuilder
                .AddAzureOpenAIChatCompletion(config.DepoymentId, model.Api, model.ApiKey, httpClient = httpClient, modelId = model.Model)
                .AddAzureOpenAIEmbeddingGenerator(config.DepoymentId, model.Api, model.ApiKey, httpClient = httpClient, modelId = model.Model)
                .AddAzureOpenAITextToImage(config.DepoymentId, model.Api, model.ApiKey, httpClient = httpClient, modelId = model.Model)
                .AddAzureOpenAITextToAudio(config.DepoymentId, model.Api, model.ApiKey, httpClient = httpClient, modelId = model.Model)
                .AddAzureOpenAIAudioToText(config.DepoymentId, model.Api, model.ApiKey, httpClient = httpClient, modelId = model.Model)
            |> ignore
        | ModelProvider.Google ->
            kernelBuilder
                .AddGoogleAIGeminiChatCompletion(model.Model, model.ApiKey, httpClient = httpClient)
                .AddGoogleAIEmbeddingGenerator(model.Model, model.ApiKey, httpClient = httpClient)
            |> ignore
        | ModelProvider.HuggingFace ->
            kernelBuilder
                .AddHuggingFaceChatCompletion(model.Model, new Uri(model.Api), apiKey = model.ApiKey, httpClient = httpClient)
                .AddHuggingFaceEmbeddingGenerator(model.Model, new Uri(model.Api), apiKey = model.ApiKey, httpClient = httpClient)
                .AddHuggingFaceTextGeneration(model.Model, new Uri(model.Api), apiKey = model.ApiKey, httpClient = httpClient)
                .AddHuggingFaceImageToText(model.Model, new Uri(model.Api), apiKey = model.ApiKey, httpClient = httpClient)
            |> ignore
        | ModelProvider.MistralAI ->
            kernelBuilder
                .AddMistralChatCompletion(model.Model, model.ApiKey, endpoint = new Uri(model.Api), httpClient = httpClient)
                .AddMistralEmbeddingGenerator(model.Model, model.ApiKey, endpoint = new Uri(model.Api), httpClient = httpClient)
            |> ignore

        kernelBuilder


    interface IModelService with

        member _.GetModel(id) = valueTask {
            let db = dbService.DbContext
            let! model = db.Queryable<Model>().Where(fun (x: Model) -> x.Id = id).FirstAsync<Model | null>()
            return
                match model with
                | null -> failwithf "Model with id %d not found" id
                | x -> x
        }

        member _.TryGetModelWithCache(id) =
            (this :> IModelService).GetModelsWithCache() |> ValueTask.map (Seq.tryFind (fun x -> x.Id = id) >> ValueOption.ofOption)

        member _.GetModelWithCache(id) =
            (this :> IModelService).TryGetModelWithCache(id)
            |> ValueTask.map (
                function
                | ValueNone -> failwithf "Model with id %d not found" id
                | ValueSome x -> x
            )

        member _.GetModels() = valueTask {
            let db = dbService.DbContext
            return! db.Queryable<Model>().ToListAsync() |> Task.map Seq.toList
        }

        member _.GetModelsWithCache() =
            memoryCache.GetOrCreateAsync(Strings.ModelsMemoryCacheKey, (fun entry -> task { return! (this :> IModelService).GetModels() }))
            |> ValueTask.ofTask
            |> ValueTask.map (
                function
                | null -> []
                | x -> x
            )


        member _.UpsertModel(model) = valueTask {
            memoryCache.Remove(Strings.ModelsMemoryCacheKey)
            memoryCache.Remove(Strings.AgentsMemoryCacheKey)

            let db = dbService.DbContext

            let! modelId = valueTask {
                if model.Id <> 0 then
                    do! db.Update<Model>().SetSource(model).ExecuteAffrowsAsync() |> Task.map ignore
                    return model.Id
                else
                    return! db.Insert<Model>(model).ExecuteIdentityAsync() |> Task.map int
            }

            // If this is a embedding model and the app settings is not set for memory, then use this automatically
            if model.CanHandleEmbedding then
                let! hasMemorySettings = db.Select<AppSettings>().AnyAsync(fun (x: AppSettings) -> x.TypeName = nameof AppSettingsType.MemorySettings)
                if not hasMemorySettings then
                    do!
                        db
                            .InsertOrUpdate<AppSettings>()
                            .SetSource(
                                {
                                    AppSettings.Default with
                                        Type = AppSettingsType.MemorySettings { MemorySettings.Default with EmbeddingModelId = modelId }
                                }
                            )
                            .ExecuteAffrowsAsync()
                        |> Task.map ignore
        }

        member _.DeleteModel(id) = valueTask {
            memoryCache.Remove(Strings.ModelsMemoryCacheKey)
            memoryCache.Remove(Strings.AgentsMemoryCacheKey)

            let db = dbService.DbContext
            db.Transaction(fun () ->
                db.Delete<Model>().Where(fun (x: Model) -> x.Id = id).ExecuteAffrows() |> ignore
                db.Delete<AgentModel>().Where(fun (x: AgentModel) -> x.ModelId = id).ExecuteAffrows() |> ignore
            )
        }

        member _.UpdateUsedTime(id) = valueTask {
            let db = dbService.DbContext
            match! db.Update<Model>(id).Set((fun (x: Model) -> x.LastUsedAt), DateTime.Now).ExecuteAffrowsAsync() with
            | 1 -> ()
            | _ -> failwith "Failed to update model"
        }

        member _.IncreaseTokensUsage(modelId, inputDelta, outputDelta) = valueTask {
            let db = dbService.DbContext
            let! model = db.Select<Model>().Where(fun (x: Model) -> x.Id = modelId).FirstAsync<Model | null>()
            match model with
            | null -> ()
            | model ->
                let oldInputCount = if model.InputTokens.HasValue then model.InputTokens.Value else 0
                let oldOutputCount = if model.OutputTokens.HasValue then model.OutputTokens.Value else 0
                do!
                    db
                        .Update<Model>(modelId)
                        .Set((fun (x: Model) -> x.InputTokens), Nullable(oldInputCount + int64 inputDelta))
                        .Set((fun (x: Model) -> x.OutputTokens), Nullable(oldOutputCount + int64 outputDelta))
                        .ExecuteAffrowsAsync()
                    |> Task.map ignore
        }


        member _.GetModelsFromSource(model, ?cancellationToken) = valueTask {
            let model = { model with Request = null }
            match model.Provider with
            | ModelProvider.Ollama ->
                use httpClient = this.CreateHttpClient(model, timeoutMs = 10_000)
                let ollamaClient = new OllamaApiClient(httpClient)
                return!
                    ollamaClient.ListLocalModelsAsync(?cancellationToken = cancellationToken)
                    |> Task.map (
                        Seq.sortByDescending _.ModifiedAt
                        >> Seq.map (fun x -> {
                            Model = x.Name
                            DisplayName = x.Name + " size=" + string x.Size + " modified=" + x.ModifiedAt.ToString("yyyy-MM-dd")
                        })
                        >> Seq.toList
                    )

            | ModelProvider.MistralAI
            | ModelProvider.OpenAI ->
                use httpClient = this.CreateHttpClient(model, timeoutMs = 10_000)
                return!
                    OpenAIClient(
                        ApiKeyCredential(model.ApiKey),
                        OpenAIClientOptions(Endpoint = Uri(model.Api), Transport = new HttpClientPipelineTransport(httpClient))
                    )
                        .GetOpenAIModelClient()
                        .GetModelsAsync(?cancellationToken = cancellationToken)
                    |> Task.map (fun x ->
                        x.Value
                        |> Seq.sortByDescending _.CreatedAt
                        |> Seq.map (fun x -> {
                            Model = x.Id
                            DisplayName =
                                x.Id
                                + (if String.IsNullOrEmpty x.OwnedBy then "" else " owner=" + x.OwnedBy)
                                + (" created=" + x.CreatedAt.ToString("yyyy-MM-dd"))
                        })
                        |> Seq.toList
                    )

            | ModelProvider.OpenAIAzure _ -> return []

            | ModelProvider.Google ->
                use httpClient = this.CreateBaseHttpClient(model)
                return!
                    httpClient.GetFromJsonAsync<ModelDescriptionFromGoogle>(
                        $"models?key={model.ApiKey}",
                        options = JsonSerializerOptions.createDefault ()
                    )
                    |> Task.map (
                        function
                        | null -> []
                        | x -> x.models
                    )
                    |> Task.map (
                        Seq.map (fun x -> {
                            Model = if x.name.StartsWith("models/") then x.name.Substring(7) else x.name
                            DisplayName = $"{x.displayName} input={x.inputTokenLimit} output={x.outputTokenLimit}"
                        })
                        >> Seq.toList
                    )

            | ModelProvider.HuggingFace ->
                use httpClient = this.CreateBaseHttpClient(model)
                return!
                    httpClient.GetFromJsonAsync<ModelDescriptionFromHuggingface list>(
                        "https://huggingface.co/api/models",
                        options = JsonSerializerOptions.createDefault ()
                    )
                    |> Task.map (
                        function
                        | null -> []
                        | x -> x
                    )
                    |> Task.map (
                        Seq.sortByDescending _.downloads
                        >> Seq.map (fun x -> {
                            Model = x.modelId
                            DisplayName =
                                x.modelId
                                + (" downloads=" + string x.downloads)
                                + (if x.``private`` then " private" else "")
                                + (" created=" + x.createdAt.ToString("yyyy-MM-dd"))
                        })
                        >> Seq.toList
                    )
        }

        member _.GetModelsFromSourceWithCache(model, ?cancellationToken) =
            match model.Provider with
            | ModelProvider.Ollama ->
                // Ollama models are not cached, as they are local and can change frequently
                (this :> IModelService).GetModelsFromSource(model, ?cancellationToken = cancellationToken)
            | _ ->
                memoryCache.GetOrCreateAsync(
                    $"models-{model.Provider}-{model.Api}",
                    (fun entry -> task {
                        entry.AbsoluteExpirationRelativeToNow <- TimeSpan.FromMinutes(10L)
                        return! (this :> IModelService).GetModelsFromSource(model, ?cancellationToken = cancellationToken)
                    })
                )
                |> ValueTask.ofTask
                |> ValueTask.map (
                    function
                    | null -> []
                    | x -> x
                )


        member _.GetKernel(modelId, ?timeoutMs) = valueTask {
            let! model = (this :> IModelService).GetModelsWithCache() |> ValueTask.map (Seq.tryFind (fun x -> x.Id = modelId))
            return
                match model with
                | None -> failwithf "Kernel for model with id %d not found" modelId
                | Some model -> this.CreateKernelBuilder(model, ?timeoutMs = timeoutMs).Build()
        }

        member _.GetEmbeddingService(modelId, ?timeoutMs) = valueTask {
            let! kernel = (this :> IModelService).GetKernel(modelId, ?timeoutMs = timeoutMs)
            return kernel.GetRequiredService<IEmbeddingService>()
        }


        member _.CountTokens(text) = valueTask { return tokenCounter.Value.CountTokens(text) }
