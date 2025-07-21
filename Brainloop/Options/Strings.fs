[<AutoOpen>]
module GloablStrings

open System
open Fun.Result


[<RequireQualifiedAccess>]
module Strings =

    [<Literal>]
    let ToolCallPrefix = "tool-call-"

    [<Literal>]
    let ToolCallAgentId = "tool_call_agent_id"

    [<Literal>]
    let ToolCallLoopId = "tool_call_loop_id"

    [<Literal>]
    let ToolCallLoopContentId = "tool_call_source_id"

    [<Literal>]
    let AgentPluginName = "agents"


    [<Literal>]
    let ModelsMemoryCacheKey = "models"

    [<Literal>]
    let AgentsMemoryCacheKey = "agents"


    [<Literal>]
    let NavActionsRightSectionName = "nav-actions-right"

    [<Literal>]
    let NavActionsSectionName = "nav-actions"

    [<Literal>]
    let NavQuickActionsSectionName = "nav-actions-quick"


    [<Literal>]
    let SchedulerGroupForAgent = "agent-scheduler"


type Strings =

    static member GetLoopContentContainerDomId(id: int64) = $"loop-content-{id}"
    static member GetLoopContentsContainerDomId(id: int64) = $"loop-{id}-contents-inner-container"


type String with
    member this.KeepLetterAndDigits() = this |> Seq.filter (fun x -> Char.IsDigit x || Char.IsLetter x) |> Seq.toArray |> String

    member this.ToAscii() = this |> Seq.map (fun x -> if Char.IsDigit x || Char.IsLetter x then x else '_') |> Seq.toArray |> String


let (|IMAGE|_|) (path: string) =
    match path with
    | SafeStringEndWithCi ".jpg"
    | SafeStringEndWithCi ".jpeg"
    | SafeStringEndWithCi ".gif"
    | SafeStringEndWithCi ".bmp"
    | SafeStringEndWithCi ".tiff"
    | SafeStringEndWithCi ".webp"
    | SafeStringEndWithCi ".svg"
    | SafeStringEndWithCi ".heif"
    | SafeStringEndWithCi ".raw"
    | SafeStringEndWithCi ".ico"
    | SafeStringEndWithCi ".png" -> true
    | _ -> false

let (|AUDIO|_|) (path: string) =
    match path with
    | SafeStringEndWithCi ".mp3"
    | SafeStringEndWithCi ".m4a"
    | SafeStringEndWithCi ".flac"
    | SafeStringEndWithCi ".aac"
    | SafeStringEndWithCi ".ogg"
    | SafeStringEndWithCi ".wma"
    | SafeStringEndWithCi ".aiff"
    | SafeStringEndWithCi ".alac"
    | SafeStringEndWithCi ".opus"
    | SafeStringEndWithCi ".wav" -> true
    | _ -> false

let (|VIDEO|_|) (path: string) =
    match path with
    | SafeStringEndWithCi ".avi"
    | SafeStringEndWithCi ".mov"
    | SafeStringEndWithCi ".mkv"
    | SafeStringEndWithCi ".wmv"
    | SafeStringEndWithCi ".flv"
    | SafeStringEndWithCi ".webm"
    | SafeStringEndWithCi ".avchd"
    | SafeStringEndWithCi ".mpeg"
    | SafeStringEndWithCi ".3gp"
    | SafeStringEndWithCi ".mp4" -> true
    | _ -> false

let (|PDF|_|) (path: string) =
    match path with
    | SafeStringEndWithCi ".pdf" -> true
    | _ -> false
