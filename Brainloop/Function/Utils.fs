namespace Brainloop.Function

open System
open System.IO
open System.Linq
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open System.ComponentModel
open System.Collections.Generic
open Microsoft.Extensions.Logging
open Microsoft.Extensions.DependencyInjection
open Microsoft.SemanticKernel
open Quartz
open IcedTasks
open FSharp.Control
open Fun.Result
open Brainloop.Db
open Brainloop.Model
open Brainloop.Memory
open Brainloop.Share


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
