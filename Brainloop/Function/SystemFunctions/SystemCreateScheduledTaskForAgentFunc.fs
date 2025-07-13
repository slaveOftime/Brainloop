namespace Brainloop.Function.SystemFunctions

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open System.ComponentModel
open Microsoft.Extensions.Logging
open Microsoft.Extensions.DependencyInjection
open Microsoft.SemanticKernel
open Quartz
open IcedTasks
open FSharp.Control
open Fun.Result
open Brainloop.Db
open Brainloop.Share
open Brainloop.Function


type private ScheduleTaskForAgentArgs() =
    member val Id: string = "" with get, set
    member val AgentId: int = 0 with get, set
    member val Prompt: string = "" with get, set
    [<Description "CRON expression for the scheduler. Support for specifying both a day-of-week and a day-of-month value is not complete (you must currently use the ? character in one of these fields).">]
    member val CronExpression: string = "" with get, set

type private SystemScheduledTaskToCallAgentData = {
    Identity: string
    Author: string
    AgentId: int
    LoopId: int64
    CronExpression: string
    Prompt: string
}


type SystemScheduledTaskToCallAgentJob
    (startChatLoopHandler: IStartChatLoopHandler, addNotificationHandler: IAddNotificationHandler, logger: ILogger<SystemScheduledTaskToCallAgentJob>) as this
    =
    interface IJob with
        member _.Execute(context: IJobExecutionContext) = task {
            try
                match context.JobDetail.JobDataMap.Get("data") |> string |> fromJson<SystemScheduledTaskToCallAgentData> with
                | ValueNone -> ()
                | ValueSome data ->
                    do!
                        startChatLoopHandler.Handle(
                            data.LoopId,
                            data.Prompt,
                            includeHistory = false,
                            author = data.Author,
                            role = LoopContentAuthorRole.Agent,
                            ignoreInput = true,
                            agentId = data.AgentId,
                            cancellationToken = context.CancellationToken
                        )

                    do!
                        addNotificationHandler.Handle(
                            NotificationSource.Scheduler {
                                Name = context.JobDetail.Key.Name
                                Group = context.JobDetail.Key.Group
                                Author = data.Author
                                AgentId = data.AgentId
                                LoopId = data.LoopId
                            },
                            $"{data.Identity} is finished"
                        )

            with ex ->
                logger.LogError(ex, "Error executing FunctionScheduleTaskToCallAgentJob")
                raise ex
        }


type SystemCreateScheduledTaskForAgentFunc
    (
        dbService: IDbService,
        logger: ILogger<SystemCreateScheduledTaskForAgentFunc>,
        schedulerFactory: ISchedulerFactory,
        serviceProvider: IServiceProvider,
        loggerFactory: ILoggerFactory
    ) =

    member _.Create(fn: Function, ?cancellationToken: CancellationToken) =
        KernelFunctionFactory.CreateFromMethod(
            Func<ScheduleTaskForAgentArgs, KernelArguments, ValueTask<unit>>(fun args kernelArgs -> valueTask {
                try
                    match kernelArgs.TryGetValue(Strings.ToolCallLoopId) with
                    | true, (:? int64 as loopId) ->
                        let! scheduler = schedulerFactory.GetScheduler()

                        let sourceAgentId =
                            match kernelArgs.TryGetValue(Strings.ToolCallAgentId) with
                            | true, (:? int as x) -> x
                            | _ -> args.AgentId

                        let agentName =
                            dbService.DbContext.Queryable<Agent>().Where(fun (x: Agent) -> x.Id = sourceAgentId).First(fun (x: Agent) -> x.Name)

                        let data = {
                            Identity = args.Id
                            Author = agentName
                            AgentId = args.AgentId
                            LoopId = loopId
                            CronExpression = args.CronExpression
                            Prompt = args.Prompt
                        }

                        let trigger =
                            TriggerBuilder
                                .Create()
                                .WithIdentity(args.Id, Strings.SchedulerGroupForAgent)
                                .WithCronSchedule(data.CronExpression)
                                .StartNow()
                                .Build()

                        let job =
                            JobBuilder
                                .Create<SystemScheduledTaskToCallAgentJob>()
                                .WithIdentity(args.Id, Strings.SchedulerGroupForAgent)
                                .UsingJobData("data", JsonSerializer.Serialize(data, JsonSerializerOptions.createDefault ()))
                                .Build()

                        do! scheduler.ScheduleJob(job, trigger, ?cancellationToken = cancellationToken) |> Task.map ignore

                        let addNotificationHandler = serviceProvider.GetRequiredService<IAddNotificationHandler>()
                        do!
                            addNotificationHandler.Handle(
                                NotificationSource.Scheduler {
                                    Name = args.Id
                                    Group = Strings.SchedulerGroupForAgent
                                    Author = data.Author
                                    AgentId = data.AgentId
                                    LoopId = data.LoopId
                                },
                                $"Scheduled {args.Id}"
                            )

                    | _ ->
                        logger.LogWarning("No loopId found in the arguments for scheduling task for agent {agentId}", args.AgentId)
                        raise (ArgumentException("No loopId found in the arguments for scheduling task"))

                with ex ->
                    logger.LogError(ex, "Failed to schedule task for agent {agentId}", args.AgentId)
                    raise ex
            }),
            JsonSerializerOptions.createDefault (),
            functionName = SystemFunction.CreateScheduledTaskForAgent,
            description = fn.Name + " " + fn.Description,
            loggerFactory = loggerFactory
        )
