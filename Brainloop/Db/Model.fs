namespace rec Brainloop.Db

open System


[<CLIMutable>]
type Model = {
    Id: int
    Name: string
    Model: string
    Provider: ModelProvider
    CanHandleText: bool
    CanHandleImage: bool
    CanHandleVideo: bool
    CanHandleAudio: bool
    CanHandleFunctions: bool
    // If this is turn on, other capabilities are ignored
    CanHandleEmbedding: bool
    EmbeddingDimensions: int
    Api: string
    ApiKey: string
    ApiProps: ModelApiProps voption
    Proxy: string
    CreatedAt: DateTime
    UpdatedAt: DateTime
    LastUsedAt: DateTime Nullable
    InputTokens: int64 Nullable
    OutputTokens: int64 Nullable
} with

    static member Default = {
        Id = 0
        Name = ""
        Model = ""
        Provider = ModelProvider.OpenAI
        CanHandleText = true
        CanHandleImage = false
        CanHandleVideo = false
        CanHandleAudio = false
        CanHandleFunctions = false
        CanHandleEmbedding = false
        EmbeddingDimensions = 768
        Api = ""
        ApiKey = ""
        ApiProps = ValueNone
        Proxy = ""
        CreatedAt = DateTime.Now
        UpdatedAt = DateTime.Now
        LastUsedAt = Nullable()
        InputTokens = Nullable()
        OutputTokens = Nullable()
    }

[<RequireQualifiedAccess>]
type ModelProvider =
    | OpenAI
    | OpenAIAzure of OpenAIAzure
    | Ollama
    | Google
    | HuggingFace
    | MistralAI

    override this.ToString() =
        match this with
        | OpenAI -> "OpenAI"
        | OpenAIAzure _ -> "OpenAI Azure"
        | Ollama -> "Ollama"
        | Google -> "Google"
        | HuggingFace -> "Hugging Face"
        | MistralAI -> "Mistral AI"

type OpenAIAzure = {
    DepoymentId: string
} with

    static member Default = { DepoymentId = "" }



type ModelApiProps = {
    Headers: Map<string, string>
    SensitiveHeaders: Map<string, string> option
} with

    static member Default = {
        Headers = Map.empty<string, string>
        SensitiveHeaders = None
    }
