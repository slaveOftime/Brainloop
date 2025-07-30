namespace rec Brainloop.Share

open System
open System.IO
open System.Runtime.CompilerServices
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Collections.Generic
open Microsoft.SemanticKernel.ChatCompletion
open FSharp.Data.Adaptive
open Fun.Result
open Brainloop.Db


type LoopContentWrapper = {
    Id: int64
    LoopId: int64
    SourceLoopContentId: int64 voption
    Author: string
    AuthorRole: LoopContentAuthorRole
    CreatedAt: DateTime
    UpdatedAt: DateTime cval
    AgentId: int voption
    ModelId: int voption cval
    Items: LoopContentItem clist
    DirectPrompt: string voption cval
    IncludedHistoryCount: int cval
    IsSecret: bool cval
    ErrorMessage: string cval
    ThinkDurationMs: int cval
    TotalDurationMs: int cval
    StreammingCount: int cval
    CancellationTokenSource: CancellationTokenSource voption cval
    InputTokens: int64 cval
    OutputTokens: int64 cval
    ProgressMessage: string cval
} with

    static member Default(loopId) = {
        Id = 0
        LoopId = loopId
        SourceLoopContentId = ValueNone
        Author = "ME"
        AuthorRole = LoopContentAuthorRole.User
        CreatedAt = DateTime.Now
        UpdatedAt = cval DateTime.Now
        AgentId = ValueNone
        ModelId = cval ValueNone
        Items = clist ()
        DirectPrompt = cval ValueNone
        IncludedHistoryCount = cval 0
        IsSecret = cval false
        ErrorMessage = cval ""
        ThinkDurationMs = cval 0
        TotalDurationMs = cval 0
        StreammingCount = cval -1
        CancellationTokenSource = cval ValueNone
        InputTokens = cval 0L
        OutputTokens = cval 0L
        ProgressMessage = cval ""
    }

    static member FromLoopContent(content: LoopContent) = {
        Id = content.Id
        LoopId = content.LoopId
        SourceLoopContentId = content.SourceLoopContentId |> ValueOption.ofNullable
        CreatedAt = content.CreatedAt
        UpdatedAt = cval content.UpdatedAt
        Author = content.Author
        AuthorRole = content.AuthorRole
        AgentId = ValueOption.ofNullable content.AgentId
        ModelId = cval (ValueOption.ofNullable content.ModelId)
        StreammingCount = cval -1
        Items =
            clist (
                try
                    match JsonSerializer.Deserialize<List<LoopContentItem>>(content.Content, options = JsonSerializerOptions.createDefault ()) with
                    | null -> Seq.empty
                    | x -> x
                with _ -> [ LoopContentItem.Text(LoopContentText content.Content) ]
            )
        DirectPrompt =
            cval (
                match content.DirectPrompt with
                | SafeString x -> ValueSome x
                | _ -> ValueNone
            )
        IncludedHistoryCount = cval (content.IncludedHistoryCount |> ValueOption.ofNullable |> ValueOption.defaultValue 0)
        IsSecret = cval content.IsSecret
        CancellationTokenSource = cval ValueNone
        ErrorMessage = cval content.ErrorMessage
        ThinkDurationMs = cval content.ThinkDurationMs
        TotalDurationMs = cval content.TotalDurationMs
        InputTokens = cval (content.InputTokens |> ValueOption.ofNullable |> ValueOption.defaultValue 0L)
        OutputTokens = cval (content.OutputTokens |> ValueOption.ofNullable |> ValueOption.defaultValue 0L)
        ProgressMessage = cval ""
    }

    member this.ToLoopContent(?id: int64) = {
        LoopContent.Id = defaultArg id this.Id
        LoopId = this.LoopId
        SourceLoopContentId = this.SourceLoopContentId |> ValueOption.toNullable
        Author = this.Author
        AuthorRole = this.AuthorRole
        AgentId = this.AgentId |> ValueOption.toNullable
        ModelId = this.ModelId.Value |> ValueOption.toNullable
        Content = JsonSerializer.Serialize(this.Items.Value, options = JsonSerializerOptions.createDefault ())
        DirectPrompt =
            match this.DirectPrompt.Value with
            | ValueSome(SafeString x) -> x
            | _ -> null
        IncludedHistoryCount = this.IncludedHistoryCount.Value |> Nullable
        IsSecret = this.IsSecret.Value
        ErrorMessage =
            match this.ErrorMessage.Value with
            | SafeString x -> x
            | _ -> null
        CreatedAt = this.CreatedAt
        UpdatedAt = this.UpdatedAt.Value
        ThinkDurationMs = this.ThinkDurationMs.Value
        TotalDurationMs = this.TotalDurationMs.Value
        InputTokens = this.InputTokens.Value |> Nullable
        OutputTokens = this.OutputTokens.Value |> Nullable
    }


    member this.ConvertItemsToString() =
        let sb = StringBuilder()
        for content in this.Items.Value do
            sb.AppendLine(content.ToString()) |> ignore
        sb.ToString()

    member this.ConvertItemsToTextForVectorization() =
        let sb = StringBuilder()
        for content in this.Items.Value do
            match content with
            | LoopContentItem.Text x ->
                x.Blocks
                |> Seq.iter (
                    function
                    | LoopContentTextBlock.Think _ -> ()
                    | LoopContentTextBlock.Content text -> sb.AppendLine(text) |> ignore
                )
            | LoopContentItem.File x -> sb.AppendLine().Append("File: ").Append(x.Name) |> ignore
            | LoopContentItem.Excalidraw _ -> sb.AppendLine("Excalidraw") |> ignore
            | LoopContentItem.ToolCall { Result = ValueSome result } -> sb.AppendLine().AppendLine(JsonSerializer.Prettier result) |> ignore
            | LoopContentItem.ToolCall _ -> ()
            | LoopContentItem.Secret _ -> sb.AppendLine("secret") |> ignore

        sb.ToString()


    member this.ResetContent(x: string) =
        transact (fun _ ->
            this.Items.Clear()
            this.Items.Add(LoopContentItem.Text(LoopContentText x)) |> ignore
            this.StreammingCount.Value <- -1
        )

    member this.AddContent(x: LoopContentItem) = transact (fun _ -> this.Items.Add(x) |> ignore)

    member this.IsStreaming = this.StreammingCount |> AVal.map (fun x -> x > -1)

    member this.IsEncrypted =
        this.Items
        |> AList.tryFirst
        |> AVal.map (
            function
            | Some(LoopContentItem.Secret _) when this.Items.Count = 1 -> true
            | _ -> false
        )

[<RequireQualifiedAccess; Struct>]
type ToolCallUserAction =
    | Pending
    | Accepted
    | Declined

type LoopContentToolCall = {
    AgentId: int
    FunctionName: string
    Description: string
    Arguments: IDictionary<string, obj | null>
    Result: obj voption
    DurationMs: int
    [<JsonIgnore>]
    UserAction: ToolCallUserAction cval voption
}


[<RequireQualifiedAccess; Struct>]
type LoopContentTextBlock =
    | Think of string
    | Content of string

type LoopContentText() =

    let sb = StringBuilder()

    new(x: string) as this =
        LoopContentText()
        then this.Text <- x

    member _.Text
        with get (): string = sb.ToString()
        and set (x: string) = sb.Clear().Append(x) |> ignore

    member _.Clear() = sb.Clear() |> ignore
    member _.Append(x: string) = sb.Append(x) |> ignore

    [<JsonIgnore>]
    member _.Length = sb.Length

    member this.Blocks =
        if String.IsNullOrEmpty this.Text then
            []
        else
            try
                let rec sliceMarkdowns (markdown: string) = seq {
                    if String.IsNullOrWhiteSpace markdown |> not then
                        let startIndex = markdown.IndexOf("<think>")
                        let endIndex = markdown.IndexOf("</think>")
                        if startIndex >= 0 then
                            if startIndex > 0 then
                                let head = markdown.Substring(0, startIndex)
                                if not (String.IsNullOrWhiteSpace head) then LoopContentTextBlock.Content head
                            if endIndex <= startIndex then
                                LoopContentTextBlock.Think(markdown.Substring(startIndex + 7))
                            else
                                LoopContentTextBlock.Think(markdown.Substring(startIndex + 7, endIndex - startIndex - 7))
                                yield! sliceMarkdowns (markdown.Substring(endIndex + 8))
                        else
                            LoopContentTextBlock.Content markdown
                }

                sliceMarkdowns this.Text |> Seq.toList

            with _ -> [ LoopContentTextBlock.Content this.Text ]

type LoopContentFile = {
    Name: string
    Size: int64
} with

    member this.Extension =
        match Path.GetExtension(this.Name) with
        | SafeString x -> x.Substring(1)
        | _ -> ""

type LoopContentExcalidraw = {
    ImageFileName: string
    DarkImageFileName: string option
    JsonData: string
}

[<RequireQualifiedAccess; Struct>]
type LoopContentItem =
    | Text of text: LoopContentText
    | ToolCall of toolCall: LoopContentToolCall
    | File of file: LoopContentFile
    | Excalidraw of excalidraw: LoopContentExcalidraw
    | Secret of string

    override this.ToString() : string =
        match this with
        | LoopContentItem.Secret x -> x
        | LoopContentItem.Excalidraw x -> x.JsonData
        | LoopContentItem.Text x -> x.Text
        | LoopContentItem.File x -> "File: " + x.Name
        | LoopContentItem.ToolCall x ->
            $"Function call input and output:
```json
{JsonSerializer.Prettier(x)}
```"


[<Extension>]
type LoopContentAuthorRoleExtensions =
    [<Extension>]
    static member ToSemanticKernelRole(this: LoopContentAuthorRole) =
        match this with
        | LoopContentAuthorRole.Agent -> AuthorRole.Assistant
        | LoopContentAuthorRole.System -> AuthorRole.System
        | LoopContentAuthorRole.Tool -> AuthorRole.Tool
        | LoopContentAuthorRole.User -> AuthorRole.User
