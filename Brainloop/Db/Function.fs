namespace rec Brainloop.Db

open System


[<RequireQualifiedAccess>]
type FunctionType =
    | SystemGetCurrentTime
    | SystemRenderInIframe
    | SystemSendHttp of SystemSendHttpConfig
    | SystemSearchMemory of SystemSearchMemoryConfig
    | SystemReadDocumentAsText
    | SystemExecuteCommand of SystemExecuteCommandConfig
    | SystemGenerateImage of SystemGenerateImageConfig
    | SystemCreateTaskForAgent
    | SystemCreateScheduledTaskForAgent
    | Mcp of McpConfig
    | OpenApi of OpenApiConfig
    | OpenApiUrl of OpenApiUriConfig

[<AutoOpen>]
module FunctionType =
    let (|SYSTEM_FUNCTION|_|) (ty: FunctionType) =
        match ty with
        | FunctionType.SystemGetCurrentTime
        | FunctionType.SystemRenderInIframe
        | FunctionType.SystemSendHttp _
        | FunctionType.SystemSearchMemory _
        | FunctionType.SystemReadDocumentAsText
        | FunctionType.SystemExecuteCommand _
        | FunctionType.SystemGenerateImage _
        | FunctionType.SystemCreateTaskForAgent
        | FunctionType.SystemCreateScheduledTaskForAgent -> true
        | FunctionType.Mcp _
        | FunctionType.OpenApi _
        | FunctionType.OpenApiUrl _ -> false


[<CLIMutable>]
type Function = {
    Id: int
    Name: string
    Description: string
    Proxy: string
    Type: FunctionType
    CreatedAt: DateTime
    UpdatedAt: DateTime
    LastUsedAt: DateTime Nullable
} with

    static member Default = {
        Id = 0
        Name = ""
        Description = ""
        Proxy = ""
        Type = FunctionType.Mcp McpConfig.Default
        CreatedAt = DateTime.Now
        UpdatedAt = DateTime.Now
        LastUsedAt = Nullable()
    }

type OpenApiConfig = {
    JsonSchema: string
    Headers: Map<string, string>
} with

    static member Default = { JsonSchema = ""; Headers = Map.empty }

type OpenApiUriConfig = {
    Url: string
    Headers: Map<string, string>
} with

    static member Default = { Url = ""; Headers = Map.empty }

type McpSseConfig = {
    Url: string
    Authorization: string
    Headers: Map<string, string>
}

type McpStdioConfig = {
    Command: string
    Arguments: string
    WorkingDirectory: string
    Environments: Map<string, string>
}

type McpConfig =
    | SSE of McpSseConfig
    | STDIO of McpStdioConfig

    static member Default =
        STDIO {
            Command = ""
            Arguments = ""
            WorkingDirectory = ""
            Environments = Map.empty
        }
    static member DefaultSSE = SSE { Url = ""; Authorization = ""; Headers = Map.empty }

type SystemSendHttpConfig = {
    ConvertHtmlToMarkdown: bool
} with

    static member Default = { ConvertHtmlToMarkdown = true }

type SystemSearchMemoryConfig = {
    Top: int
} with

    static member Default = { Top = 10 }

type SystemExecuteCommandConfig = {
    Command: string
    Arguments: string
    ArgumentsDescription: Map<string, string>
    WorkingDirectory: string
    Environments: Map<string, string>
} with

    static member Default = {
        Command = ""
        Arguments = ""
        ArgumentsDescription = Map.empty
        WorkingDirectory = ""
        Environments = Map.empty
    }

[<RequireQualifiedAccess>]
type SystemGenerateImageConfig =
    | LLMModel of GenerateImageByLLMModelConfig

    static member Default = LLMModel { ModelId = 0 }

type GenerateImageByLLMModelConfig = { ModelId: int }
