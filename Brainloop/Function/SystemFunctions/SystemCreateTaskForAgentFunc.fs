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
open Fun.Result
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

    static member GetArgs(toolCall: LoopContentToolCall) =
        match toolCall.Arguments.TryGetValue "arguments" with
        | true, x ->
            JsonSerializer.Deserialize<CreateTaskForAgentArgs>(string x, JsonSerializerOptions.createDefault ())
            |> ValueOption.ofObj
        | _ ->
            match toolCall.Arguments.TryGetValue "agentId", toolCall.Arguments.TryGetValue "prompt" with
            | (true, agentId), (true, prompt) ->
                match string agentId, string prompt with
                | INT32 agentId, SafeString prompt -> ValueSome(CreateTaskForAgentArgs(AgentId = agentId, Prompt = prompt))
                | _ -> ValueNone
            | _ -> ValueNone

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
                Func<CreateTaskForAgentArgs, ValueTask>(fun arguments -> ValueTask.CompletedTask),
                JsonSerializerOptions.createDefault (),
                functionName = SystemFunction.CreateTaskForAgent,
                description = description.ToString(),
                loggerFactory = loggerFactory
            )
    }
