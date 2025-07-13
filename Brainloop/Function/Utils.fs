[<AutoOpen>]
module Brainloop.Function.Utils

open System
open Microsoft.SemanticKernel


[<RequireQualifiedAccess>]
module SystemFunction =
    [<Literal>]
    let PluginName = "system"

    [<Literal>]
    let GetCurrentTime = "get_current_time"

    [<Literal>]
    let RenderInIframe = "render_in_iframe"

    [<Literal>]
    let SendHttp = "send_http"

    [<Literal>]
    let SearchMemory = "search_memory"

    [<Literal>]
    let ReadDocumentAsText = "read_document_as_text"

    [<Literal>]
    let ExecuteCommand = "execute_command"

    [<Literal>]
    let GenerateImage = "generate_image"

    [<Literal>]
    let CreateTaskForAgent = "create_task_for_agent"

    [<Literal>]
    let CreateScheduledTaskForAgent = "create_scheduled_task_for_agent"


    let private isFunction (functionName: string) (name: string) =
        if name.StartsWith(PluginName) && name.Length > PluginName.Length + 2 then
            name.AsSpan().Slice(PluginName.Length + 1).SequenceEqual(functionName)
        else
            false

    let isRenderInIframe (name: string) = isFunction RenderInIframe name
    let isCreateTaskForAgent (name: string) = isFunction CreateTaskForAgent name


type KernelArguments with

    member this.AgentId =
        match this.TryGetValue(Strings.ToolCallAgentId) with
        | true, (:? int32 as x) -> ValueSome x
        | _ -> ValueNone

    member this.LoopId =
        match this.TryGetValue(Strings.ToolCallLoopId) with
        | true, (:? int64 as x) -> ValueSome x
        | _ -> ValueNone

    member this.LoopContentId =
        match this.TryGetValue(Strings.ToolCallLoopContentId) with
        | true, (:? int64 as x) -> ValueSome x
        | _ -> ValueNone
