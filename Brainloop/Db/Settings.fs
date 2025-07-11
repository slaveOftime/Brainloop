namespace rec Brainloop.Db


[<CLIMutable>]
type AppSettings = {
    Id: int
    Type: AppSettingsType
    TypeName: string
} with

    static member Default = {
        Id = 0
        Type = AppSettingsType.MemorySettings MemorySettings.Default
        TypeName = nameof AppSettingsType.MemorySettings
    }

[<RequireQualifiedAccess>]
type AppSettingsType = MemorySettings of MemorySettings

type MemorySettings = {
    TokensOfChunk: int
    TokensOfChunkOverlap: int
    EmbeddingModelId: int
} with

    static member Default = {
        TokensOfChunk = 100
        TokensOfChunkOverlap = 10
        EmbeddingModelId = 0
    }
