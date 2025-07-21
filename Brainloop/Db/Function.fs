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
    Group: string | null
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
        Group = null
        CreatedAt = DateTime.Now
        UpdatedAt = DateTime.Now
        LastUsedAt = Nullable()
    }

type OpenApiConfig = {
    JsonSchema: string
    Headers: Map<string, string>
    SensitiveHeaders: List<string> option
} with

    static member Default = {
        JsonSchema = ""
        Headers = Map.empty
        SensitiveHeaders = None
    }

type OpenApiUriConfig = {
    Url: string
    Headers: Map<string, string>
    SensitiveHeaders: List<string> option
} with

    static member Default = { Url = ""; Headers = Map.empty; SensitiveHeaders = None }

type McpSseConfig = {
    Url: string
    Authorization: string
    Headers: Map<string, string>
    SensitiveHeaders: List<string> option
}

type McpStdioConfig = {
    Command: string
    Arguments: string
    WorkingDirectory: string
    Environments: Map<string, string>
    SensitiveEnvironments: List<string> option
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
            SensitiveEnvironments = None
        }
    static member DefaultSSE =
        SSE {
            Url = ""
            Authorization = ""
            Headers = Map.empty
            SensitiveHeaders = None
        }

type SystemSendHttpConfig = {
    ConvertHtmlToMarkdown: bool
    Headers: Map<string, string> option
    SensitiveHeaders: List<string> option
} with

    static member Default = {
        ConvertHtmlToMarkdown = true
        Headers = None
        SensitiveHeaders = None
    }

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
    SensitiveEnvironments: List<string> option
} with

    static member Default = {
        Command = ""
        Arguments = ""
        ArgumentsDescription = Map.empty
        WorkingDirectory = ""
        Environments = Map.empty
        SensitiveEnvironments = None
    }

[<RequireQualifiedAccess>]
type SystemGenerateImageConfig =
    | LLMModel of GenerateImageByLLMModelConfig

    static member Default = LLMModel { ModelId = 0 }

type GenerateImageByLLMModelConfig = { ModelId: int }
