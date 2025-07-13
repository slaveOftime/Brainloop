namespace Brainloop.Function.SystemFunctions

open System
open System.Text.Json
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Microsoft.SemanticKernel
open Brainloop.Db
open Brainloop.Function


type TaskForAgentArgs() =
    member val AgentId: int = 0 with get, set
    member val Prompt: string = "" with get, set


type SystemCreateTaskForAgentFunc(loggerFactory: ILoggerFactory) =

    member _.Create(fn: Function) =
        KernelFunctionFactory.CreateFromMethod(
            Func<TaskForAgentArgs, ValueTask>(fun args -> ValueTask.CompletedTask),
            JsonSerializerOptions.createDefault (),
            functionName = SystemFunction.CreateTaskForAgent,
            description = fn.Name + " " + fn.Description,
            loggerFactory = loggerFactory
        )
