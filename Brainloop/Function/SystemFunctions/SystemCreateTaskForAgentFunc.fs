namespace Brainloop.Function.SystemFunctions

open System
open System.Text
open System.Text.Json
open System.Threading.Tasks
open System.ComponentModel
open System.ComponentModel.DataAnnotations
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Caching.Memory
open Microsoft.Extensions.DependencyInjection
open Microsoft.SemanticKernel
open IcedTasks
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


type SystemCreateTaskForAgentFunc(loggerFactory: ILoggerFactory, serviceProvider: IServiceProvider) =

    static member GetArgs(toolCall: LoopContentToolCall) = toolCall.Arguments |> toJson |> fromJson<CreateTaskForAgentArgs>

    member _.Create(fn: Function, ?excludedAgentIds: int seq) = valueTask {
        let description =
            StringBuilder().Append(fn.Name).Append(' ').AppendLine(fn.Description).AppendLine("Below are agents you can invoke: ").AppendLine()

        let agents = serviceProvider.GetRequiredService<IMemoryCache>().Get<Agent list>(Strings.AgentsMemoryCacheKey)
        let excludedAgentIds = excludedAgentIds |> Option.defaultValue Seq.empty
        match agents with
        | null -> ()
        | agents ->
            for agent in agents |> Seq.filter (fun x -> Seq.contains x.Id excludedAgentIds |> not) do
                description
                    .Append("AgentId=")
                    .Append(agent.Id)
                    .Append(", AgentName=")
                    .Append(agent.Name)
                    .Append(", AgentDescription=")
                    .AppendLine(agent.Description)
                    .AppendLine()
                |> ignore

        return
            KernelFunctionFactory.CreateFromMethod(
                Func<KernelArguments, ValueTask>(fun arguments -> ValueTask.CompletedTask),
                JsonSerializerOptions.createDefault (),
                functionName = SystemFunction.CreateTaskForAgent,
                description = description.ToString(),
                parameters = KernelParameterMetadata.FromInstance(CreateTaskForAgentArgs()),
                loggerFactory = loggerFactory
            )
    }
