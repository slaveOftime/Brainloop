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
    [<Description "If you want to get notification after the agent finish its task">]
    member val CallbackAfterFinish: bool = false with get, set


type SystemInvokeAgentFunc
    (dbService: IDbService, logger: ILogger<SystemInvokeAgentFunc>, serviceProvider: IServiceProvider, loggerFactory: ILoggerFactory) =

    member _.Create(author: string, agentId: int, loopId: int64, sourceLoopContentId: int64) =
        let agent = dbService.DbContext.Queryable<Agent>().Where(fun (x: Agent) -> x.Id = agentId).First<Agent>()
        let name = $"call_agent_{agent.Id}"
        KernelFunctionFactory.CreateFromMethod(
            Func<InvokeAgentArgs, KernelArguments, CancellationToken, Task<unit>>(fun args kernelArgs ct -> task {
                logger.LogInformation("Call {agent} for help", agent.Name)

                let sourceLoopContentId = kernelArgs.LoopContentId |> ValueOption.defaultValue sourceLoopContentId

                let handler = serviceProvider.GetRequiredService<IStartChatLoopHandler>()

                valueTask {
                    do!
                        handler.Handle(
                            loopId,
                            args.Prompt,
                            agentId = agent.Id,
                            author = author,
                            role = LoopContentAuthorRole.Agent,
                            ignoreInput = true,
                            sourceLoopContentId = sourceLoopContentId,
                            cancellationToken = ct
                        )

                    match kernelArgs.TryGetValue(Strings.ToolCallAgentId) with
                    | true, (:? int as sourceAgentId) when args.CallbackAfterFinish ->
                        logger.LogInformation("Send callback to agent #{targetAgentId} from agent {sourceAgentName}", sourceAgentId, agent.Name)
                        do!
                            handler.Handle(
                                loopId,
                                "",
                                agentId = sourceAgentId,
                                author = agent.Name,
                                role = LoopContentAuthorRole.Agent,
                                ignoreInput = true,
                                cancellationToken = ct
                            )
                    | _ -> ()

                }
                |> ignore
            }),
            JsonSerializerOptions.createDefault (),
            loggerFactory = loggerFactory,
            functionName = name,
            description = $"@{agent.Name} for help. AgentId={agent.Id}, AgentDescription={agent.Description}. The task will be created immediately and return nothing."
        )
