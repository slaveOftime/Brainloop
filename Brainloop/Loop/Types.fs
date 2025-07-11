namespace Brainloop.Loop

open System
open System.IO
open System.Runtime.CompilerServices
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks
open System.Collections.Generic
open FSharp.Data.Adaptive
open Microsoft.SemanticKernel
open Microsoft.SemanticKernel.ChatCompletion
open IcedTasks
open Fun.Result
open Brainloop.Db
open Brainloop.Memory


[<Extension>]
type LoopContentAuthorRoleExtensions =
    [<Extension>]
    static member ToSemanticKernelRole(this: LoopContentAuthorRole) =
        match this with
        | LoopContentAuthorRole.Agent -> AuthorRole.Assistant
        | LoopContentAuthorRole.System -> AuthorRole.System
        | LoopContentAuthorRole.Tool -> AuthorRole.Tool
        | LoopContentAuthorRole.User -> AuthorRole.User


[<Extension>]
type StringExtensions =
    [<Extension>]
    static member KeepLetterAndDigits(thius: string) = thius |> Seq.filter (fun x -> Char.IsDigit x || Char.IsLetter x) |> Seq.toArray |> String


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
        with get () = sb.ToString()
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
    ModelName: string cval
    Items: LoopContentItem clist
    ErrorMessage: string cval
    ThinkDurationMs: int cval
    TotalDurationMs: int cval
    StreammingCount: int cval
    CancellationTokenSource: CancellationTokenSource voption cval
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
        ModelName = cval String.Empty
        Items = clist ()
        ErrorMessage = cval ""
        ThinkDurationMs = cval 0
        TotalDurationMs = cval 0
        StreammingCount = cval -1
        CancellationTokenSource = cval ValueNone
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
        ModelName = cval content.ModelName
        StreammingCount = cval -1
        Items =
            clist (
                try
                    match JsonSerializer.Deserialize<List<LoopContentItem>>(content.Content, options = JsonSerializerOptions.createDefault ()) with
                    | null -> Seq.empty
                    | x -> x
                with _ -> [ LoopContentItem.Text(LoopContentText content.Content) ]
            )
        CancellationTokenSource = cval ValueNone
        ErrorMessage = cval content.ErrorMessage
        ThinkDurationMs = cval content.ThinkDurationMs
        TotalDurationMs = cval content.TotalDurationMs
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
        ModelName = this.ModelName.Value
        Content = JsonSerializer.Serialize(this.Items.Value, options = JsonSerializerOptions.createDefault ())
        ErrorMessage =
            match this.ErrorMessage.Value with
            | SafeString x -> x
            | _ -> null
        CreatedAt = this.CreatedAt
        UpdatedAt = this.UpdatedAt.Value
        ThinkDurationMs = this.ThinkDurationMs.Value
        TotalDurationMs = this.TotalDurationMs.Value
        OutputTokens = this.OutputTokens.Value |> Nullable
    }

    member _.ConvertItemToString(content, sb: StringBuilder) =
        match content with
        | LoopContentItem.Excalidraw x -> sb.Append(x.JsonData) |> ignore
        | LoopContentItem.Text x -> sb.Append(x.Text) |> ignore
        | LoopContentItem.File x -> sb.AppendLine().Append("File: ").Append(x.Name) |> ignore
        | LoopContentItem.ToolCall x ->
            sb
                .AppendLine()
                .AppendLine("Function call input and output:")
                .AppendLine("```json")
                .AppendLine(JsonSerializer.Prettier(x))
                .AppendLine()
                .AppendLine("```")
            |> ignore

    member this.ConvertItemsToString() =
        let sb = StringBuilder()
        for content in this.Items.Value do
            this.ConvertItemToString(content, sb)
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
            | LoopContentItem.ToolCall _ -> ()
        sb.ToString()

    member this.ToChatMessageContent(documentService: IDocumentService) = valueTask {
        let items = ChatMessageContentItemCollection()

        let handleFile (fileName) = valueTask {
            let file = Path.Combine(documentService.RootDir, fileName)
            let ext =
                match Path.GetExtension(file) with
                | null -> "*"
                | x -> x.Substring(1)
            match file with
            | IMAGE ->
                let! bytes = File.ReadAllBytesAsync(file)
                items.Add(ImageContent(bytes, mimeType = $"image/{ext}"))
            | AUDIO ->
                let! bytes = File.ReadAllBytesAsync(file)
                items.Add(AudioContent(bytes, mimeType = $"audio/{ext}"))
            | VIDEO ->
                let! bytes = File.ReadAllBytesAsync(file)
                items.Add(BinaryContent(bytes, mimeType = $"video/{ext}"))
            | _ ->
                let! text = documentService.ReadAsText(file)
                items.Add(TextContent(text))
        }

        for content in this.Items |> AList.force do
            match content with
            | LoopContentItem.File x -> do! handleFile x.Name
            | LoopContentItem.Excalidraw x -> do! handleFile x.ImageFileName
            | LoopContentItem.ToolCall x -> items.Add(TextContent $"Tool function is invoked {x.FunctionName} {x.Description}")
            | LoopContentItem.Text x ->
                x.Blocks
                |> Seq.iter (
                    function
                    | LoopContentTextBlock.Think _ -> items.Add(TextContent("Thinking..."))
                    | LoopContentTextBlock.Content text -> items.Add(TextContent(text))
                )
        // TODO: get file id by /api/memory/document/{id}/image and add it into items

        return ChatMessageContent(this.AuthorRole.ToSemanticKernelRole(), items, AuthorName = this.Author.KeepLetterAndDigits())
    }

    member this.ResetContent(x: string) =
        transact (fun _ ->
            this.Items.Clear()
            this.Items.Add(LoopContentItem.Text(LoopContentText x)) |> ignore
            this.StreammingCount.Value <- -1
        )

    member this.AddContent(x: LoopContentItem) = transact (fun _ -> this.Items.Add(x) |> ignore)

    member this.IsStreaming = this.StreammingCount |> AVal.map (fun x -> x > -1)


type LoopUtils =

    static member GetLoopContentContainerDomId(id: int64) = $"loop-content-{id}"
    static member GetLoopContentsContainerDomId(id: int64) = $"loop-{id}-contents-inner-container"


type ILoopContentService =
    abstract member GetOrCreateContentsCache: loopId: int64 -> ValueTask<ChangeableIndexList<LoopContentWrapper>>
    abstract member GetContentsCache: loopId: int64 -> ChangeableIndexList<LoopContentWrapper> | null
    abstract member RemoveFromCache: loopId: int64 -> unit

    abstract member LoadMoreContentsIntoCache: loopId: int64 -> ValueTask<unit>
    abstract member LoadMoreLatestContentsIntoCache: loopId: int64 -> ValueTask<unit>

    abstract member AddContentToCacheAndUpsert: content: LoopContentWrapper -> ValueTask<int64>

    abstract member UpsertLoopContent: content: LoopContentWrapper * ?disableVectorize: bool -> ValueTask<int64>
    abstract member UpdateLoopContent: loopId: int64 * loopContentId: int64 * content: string -> ValueTask<unit>
    abstract member DeleteLoopContent: loopId: int64 * loopContentId: int64 -> ValueTask<unit>
    abstract member DeleteLoopContentsOfSource: loopId: int64 * loopContentId: int64 -> ValueTask<unit>

type ILoopService =
    abstract member Send:
        loopId: int64 *
        message: string *
        ?agentId: int *
        ?modelId: int *
        ?author: string *
        ?role: LoopContentAuthorRole *
        ?includeHistory: bool *
        ?ignoreInput: bool *
        ?sourceLoopContentId: int64 *
        ?cancellationToken: CancellationToken ->
            ValueTask<unit>

    abstract member Resend: loopId: int64 * toMessageId: int64 * ?modelId: int -> ValueTask<unit>

    abstract member BuildTitle: loopId: int64 * ?title: string -> ValueTask<unit>

    abstract member IsStreaming: loopId: int64 -> aval<bool>
