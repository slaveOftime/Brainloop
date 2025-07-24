namespace Brainloop.Function.SystemFunctions

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open System.ComponentModel
open System.ComponentModel.DataAnnotations
open Microsoft.Extensions.Logging
open Microsoft.Extensions.DependencyInjection
open Microsoft.SemanticKernel
open IcedTasks
open FSharp.Control
open Brainloop.Db
open Brainloop.Share
open Brainloop.Function


type InvokeAgentArgs() =
    [<Required>]
    [<Description "Complete context for an agent to finish its task">]
    member val Prompt: string = "" with get, set
    [<Description "Wait the function finish">]
    member val WaitForFinish: bool = false with get, set
    [<Description "If you want to get notification after the agent finish its task">]
    member val CallbackAfterFinish: bool = false with get, set
    [<Description "This is optional, when not provided it call the agent who triggered this tool/function">]
    member val CallbackAgentId: Nullable<int> = Nullable() with get, set


type SystemInvokeAgentFunc
    (dbService: IDbService, logger: ILogger<SystemInvokeAgentFunc>, serviceProvider: IServiceProvider, loggerFactory: ILoggerFactory) =

    member _.Create(author: string, agentId: int, loopId: int64, sourceLoopContentId: int64) =
        let agent = dbService.DbContext.Queryable<Agent>().Where(fun (x: Agent) -> x.Id = agentId).First<Agent>()
        let agentName = if agent.Name.Contains " " then $"\"{agent.Name}\"" else agent.Name
        let functionName = $"call_agent_{agent.Id}"

        KernelFunctionFactory.CreateFromMethod(
            Func<KernelArguments, CancellationToken, Task<unit>>(fun kernelArgs ct -> task {
                logger.LogInformation("Call {agent} for help", agent.Name)

                let arguments = kernelArgs.Get<InvokeAgentArgs>()
                let sourceLoopContentId = kernelArgs.LoopContentId |> ValueOption.defaultValue sourceLoopContentId

                let handler = serviceProvider.GetRequiredService<IChatCompletionForLoopHandler>()

                let task = valueTask {
                    do!
                        handler.Handle(
                            loopId,
                            arguments.Prompt,
                            agentId = agent.Id,
                            author = author,
                            role = LoopContentAuthorRole.Agent,
                            ignoreInput = true,
                            includeHistory = false,
                            sourceLoopContentId = sourceLoopContentId,
                            cancellationToken = ct
                        )

                    if arguments.CallbackAfterFinish then
                        let nextAgentId =
                            arguments.CallbackAgentId
                            |> ValueOption.ofNullable
                            |> function
                                | ValueNone -> kernelArgs.AgentId
                                | x -> x
                        match nextAgentId with
                        | ValueNone -> ()
                        | ValueSome nextAgentId ->
                            logger.LogInformation("Send callback to agent #{nextAgentId} from agent {sourceAgentName}", nextAgentId, agent.Name)
                            do!
                                handler.Handle(
                                    loopId,
                                    "",
                                    agentId = nextAgentId,
                                    author = agent.Name,
                                    role = LoopContentAuthorRole.Agent,
                                    ignoreInput = true,
                                    includeHistory = true,
                                    cancellationToken = ct
                                )

                }

                if arguments.WaitForFinish then do! task
            }),
            JsonSerializerOptions.createDefault (),
            loggerFactory = loggerFactory,
            functionName = functionName,
            parameters = KernelParameterMetadata.FromInstance(InvokeAgentArgs()),
            description =
                $"AgentId={agent.Id}, AgentName={agentName}, AgentDescription={agent.Description}. This function will not return anything, you can specify some parameters to wait untill finish or ask for a callback if it is necessary."
        )
