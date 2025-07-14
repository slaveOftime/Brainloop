namespace rec Brainloop.Db

open System
open System.Collections.Generic


[<RequireQualifiedAccess>]
type AgentType =
    | CreateTitle
    | GetTextFromImage
    | General

[<CLIMutable>]
type Agent = {
    Id: int
    Name: string
    Description: string
    Type: AgentType
    Temperature: float
    TopP: float
    TopK: int
    Prompt: string
    EnableStreaming: bool
    EnableTools: bool
    EnableAgentCall: bool
    EnableSelfCall: bool
    MaxHistory: int
    MaxTimeoutMs: int
    CreatedAt: DateTime
    UpdatedAt: DateTime
    LastUsedAt: DateTime Nullable
    AgentModels: ICollection<AgentModel>
    AgentFunctions: ICollection<AgentFunction>
} with

    static member Default = {
        Id = 0
        Name = ""
        Description = ""
        Type = AgentType.General
        Temperature = 0.8
        TopP = 0.9
        TopK = 40
        Prompt = ""
        EnableStreaming = true
        EnableTools = false
        EnableAgentCall = false
        EnableSelfCall = false
        MaxHistory = 10
        MaxTimeoutMs = 600_000
        CreatedAt = DateTime.Now
        UpdatedAt = DateTime.Now
        LastUsedAt = Nullable()
        AgentModels = [||]
        AgentFunctions = [||]
    }


[<CLIMutable>]
type AgentModel = {
    Id: int
    AgentId: int
    ModelId: int
    Order: int
    Model: Model
} with

    static member Create(agentId, modelId) = {
        Id = 0
        AgentId = agentId
        ModelId = modelId
        Order = 0
        Model = Model.Default
    }


[<RequireQualifiedAccess>]
type AgentFunctionTarget =
    | Agent of int
    | Function of int

[<CLIMutable>]
type AgentFunction = {
    Id: int
    AgentId: int
    Target: AgentFunctionTarget
    CreatedAt: DateTime
}


[<CLIMutable>]
type AgentFunctionWhitelist = {
    Id: int
    AgentId: int
    FunctionName: string
    CreatedAt: DateTime
}
