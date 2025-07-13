namespace rec Brainloop.Db

open System
open System.Collections.Generic


[<CLIMutable>]
type Loop = {
    Id: int64
    Description: string
    LoopContents: ICollection<LoopContent>
    Closed: bool
    CreatedAt: DateTime
    UpdatedAt: DateTime
} with

    static member Default = {
        Id = 0L
        Description = ""
        LoopContents = [||]
        Closed = false
        CreatedAt = DateTime.Now
        UpdatedAt = DateTime.Now
    }


[<CLIMutable>]
type LoopContent = {
    Id: int64
    LoopId: int64
    AgentId: int Nullable
    ModelId: int Nullable
    ModelName: string
    SourceLoopContentId: int64 Nullable
    Author: string
    AuthorRole: LoopContentAuthorRole
    Content: string
    ErrorMessage: string | null
    ThinkDurationMs: int
    TotalDurationMs: int
    CreatedAt: DateTime
    UpdatedAt: DateTime
    InputTokens: int64 Nullable
    OutputTokens: int64 Nullable
}


[<RequireQualifiedAccess>]
type LoopContentAuthorRole =
    | User
    | Tool
    | Agent
    | System
