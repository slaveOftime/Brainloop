namespace Brainloop.Model

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.AI
open Microsoft.SemanticKernel
open Brainloop.Db


type IModelService =
    abstract member GetModel: id: int -> ValueTask<Model>
    abstract member GetModelFromCache: id: int -> ValueTask<Model>
    abstract member TryGetModelWithCache: id: int -> ValueTask<Model voption>

    abstract member GetModels: unit -> ValueTask<Model list>
    abstract member GetModelsWithCache: unit -> ValueTask<Model list>

    abstract member UpsertModel: model: Model -> ValueTask<unit>
    abstract member DeleteModel: id: int -> ValueTask<unit>
    abstract member UpdateUsedTime: id: int -> ValueTask<unit>
    abstract member IncreaseOutputTokens: modelId: int * delta: int64 -> ValueTask<unit>

    abstract member GetModelsFromSource: model: Model * ?cancellationToken: CancellationToken -> ValueTask<ModelDescription list>
    abstract member GetModelsFromSourceWithCache: model: Model * ?cancellationToken: CancellationToken -> ValueTask<ModelDescription list>

    abstract member GetKernel: modelId: int * ?timeoutMs: int -> ValueTask<Kernel>
    abstract member GetEmbeddingService: modelId: int * ?timeoutMs: int -> ValueTask<IEmbeddingService>

    abstract member CountTokens: text: string -> ValueTask<int>

and IEmbeddingService = IEmbeddingGenerator<string, Embedding<float32>>

and ModelDescription = { Model: string; DisplayName: string }


type private ModelDescriptionFromGoogle = {
    models:
        {|
            name: string
            displayName: string
            inputTokenLimit: int
            outputTokenLimit: int
        |} list
}

type private ModelDescriptionFromHuggingface = {
    modelId: string
    downloads: int
    ``private``: bool
    createdAt: DateTime
}
