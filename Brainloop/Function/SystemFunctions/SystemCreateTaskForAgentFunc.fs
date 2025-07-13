namespace Brainloop.Function.SystemFunctions

open System
open System.Text.Json
open System.Threading.Tasks
open System.ComponentModel
open System.ComponentModel.DataAnnotations
open Microsoft.Extensions.Logging
open Microsoft.SemanticKernel
open Brainloop.Db
open Brainloop.Function
open Brainloop.Share


type CreateTaskForAgentArgs() =
    [<Required>]
    [<Description "Agent Id">]
    member val AgentId: int = 0 with get, set
    [<Required>]
    [<Description "Complete context for an agent to finish its task">]
    member val Prompt: string = "" with get, set


type SystemCreateTaskForAgentFunc(loggerFactory: ILoggerFactory) =

    static member GetArgs(toolCall: LoopContentToolCall) =
        match toolCall.Arguments.TryGetValue "args" with
        | true, (:? string as x) -> JsonSerializer.Deserialize<CreateTaskForAgentArgs>(x, JsonSerializerOptions.createDefault ()) |> ValueOption.ofObj
        | true, (:? JsonElement as x) -> x.Deserialize<CreateTaskForAgentArgs>(JsonSerializerOptions.createDefault ()) |> ValueOption.ofObj
        | _ -> ValueNone

    member _.Create(fn: Function) =
        KernelFunctionFactory.CreateFromMethod(
            Func<CreateTaskForAgentArgs, ValueTask>(fun args -> ValueTask.CompletedTask),
            JsonSerializerOptions.createDefault (),
            functionName = SystemFunction.CreateTaskForAgent,
            description = fn.Name + " " + fn.Description,
            loggerFactory = loggerFactory
        )
