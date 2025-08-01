[<AutoOpen>]
module Brainloop.Function.Utils

open System
open System.Text.Json
open System.ComponentModel
open System.ComponentModel.DataAnnotations
open Microsoft.SemanticKernel
open Brainloop.Db


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

    member this.Get<'T>() =
        this
        |> toJson
        |> fromJson<'T>
        |> function
            | ValueSome x -> x
            | _ -> failwith $"Failed to get arguments {typeof<'T>}"


type KernelParameterMetadata with
    static member FromInstance(args: obj) = seq {
        let argsType = args.GetType()
        for prop in argsType.GetProperties() do
            let defaultValue = prop.GetValue(args)
            let isRequired =
                match prop.PropertyType with
                | _ when prop.GetCustomAttributes(typeof<RequiredAttribute>, ``inherit`` = true) |> Seq.isEmpty |> not -> true
                | t when t.IsValueType -> false
                | _ -> defaultValue <> null
            let description =
                match prop.GetCustomAttributes(typeof<DescriptionAttribute>, ``inherit`` = true) |> Seq.tryHead with
                | Some(:? DescriptionAttribute as x) -> x.Description
                | _ -> ""
            let meta =
                KernelParameterMetadata(
                    prop.Name,
                    JsonSerializerOptions.createDefault (),
                    IsRequired = isRequired,
                    DefaultValue = defaultValue,
                    ParameterType = prop.PropertyType,
                    Description = description
                )

            if prop.PropertyType = typeof<bool> then
                meta.Schema <- KernelJsonSchema.Parse($$"""{"type": "boolean", "description": "{{description}}" }""")

            meta
    }


type Function with

    member fn.McpFunctionName = Function.MakeMcpFunctioName(fn.Id, fn.Name)

    static member MakeMcpFunctioName(id: int, name: string) = name + "-" + string id
