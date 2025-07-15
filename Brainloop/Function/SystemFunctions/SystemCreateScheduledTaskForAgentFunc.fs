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
open Quartz
open IcedTasks
open FSharp.Control
open Fun.Result
open Brainloop.Db
open Brainloop.Share
open Brainloop.Function


type CreateScheduleTaskForAgentArgs() =
    [<Required>]
    [<Description "Scheduler identity name">]
    member val Identity: string = "" with get, set
    [<Required>]
    [<Description "Agent Id">]
    member val AgentId: int = 0 with get, set
    [<Required>]
    [<Description "Complete context for an agent to finish its task">]
    member val Prompt: string = "" with get, set
    [<Description "Give a specific time like: 2025/7/13 12:28:38">]
    member val SpecificTime: DateTime Nullable = Nullable() with get, set
    [<Description "CRON expression for the scheduler. Support for specifying both a day-of-week and a day-of-month value is not complete (you must currently use the ? character in one of these fields). For example, at 08:00 AM everyday should be: 0 0 8 * * ?">]
    member val CronExpression: string | null = null with get, set

type SystemScheduledTaskToCallAgentData = {
    Identity: string
    Author: string
    AgentId: int
    LoopId: int64
    Trigger: ScheduleTrigger
    Prompt: string
}

and [<RequireQualifiedAccess>] ScheduleTrigger =
    | CRON of string
    | DATIME of DateTime


type SystemScheduledTaskToCallAgentJob
    (startChatLoopHandler: IChatCompletionForLoopHandler, addNotificationHandler: IAddNotificationHandler, logger: ILogger<SystemScheduledTaskToCallAgentJob>) as this
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
                            NotificationSource.SchedulerForAgent {
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
            Func<CreateScheduleTaskForAgentArgs, KernelArguments, ValueTask<unit>>(fun args kernelArgs -> valueTask {
                try
                    match kernelArgs.TryGetValue(Strings.ToolCallLoopId) with
                    | true, (:? int64 as loopId) ->
                        let! scheduler = schedulerFactory.GetScheduler()

                        let sourceAgentId = kernelArgs.AgentId |> ValueOption.defaultValue args.AgentId

                        let agentName =
                            dbService.DbContext.Queryable<Agent>().Where(fun (x: Agent) -> x.Id = sourceAgentId).First(fun (x: Agent) -> x.Name)

                        let data = {
                            Identity = args.Identity
                            Author = agentName
                            AgentId = args.AgentId
                            LoopId = loopId
                            Trigger =
                                match args.CronExpression, args.SpecificTime.HasValue with
                                | null, true -> ScheduleTrigger.DATIME args.SpecificTime.Value
                                | SafeString x, _ -> ScheduleTrigger.CRON x
                                | _ ->
                                    failwith
                                        $"Condition is not valid {nameof args.CronExpression}: {args.CronExpression} or {nameof args.SpecificTime}: {args.SpecificTime}"
                            Prompt = args.Prompt
                        }

                        let triggerBuilder = TriggerBuilder.Create().WithIdentity(args.Identity, Strings.SchedulerGroupForAgent)

                        let triggerBuilder =
                            match data.Trigger with
                            | ScheduleTrigger.CRON x -> triggerBuilder.WithCronSchedule(x)
                            | ScheduleTrigger.DATIME x -> triggerBuilder.StartAt(x)

                        let trigger = triggerBuilder.Build()

                        let job =
                            JobBuilder
                                .Create<SystemScheduledTaskToCallAgentJob>()
                                .WithIdentity(args.Identity, Strings.SchedulerGroupForAgent)
                                .UsingJobData("data", JsonSerializer.Serialize(data, JsonSerializerOptions.createDefault ()))
                                .Build()

                        do! scheduler.ScheduleJob(job, trigger, ?cancellationToken = cancellationToken) |> Task.map ignore

                        let addNotificationHandler = serviceProvider.GetRequiredService<IAddNotificationHandler>()
                        do!
                            addNotificationHandler.Handle(
                                NotificationSource.SchedulerForAgent {
                                    Name = args.Identity
                                    Group = Strings.SchedulerGroupForAgent
                                    Author = data.Author
                                    AgentId = data.AgentId
                                    LoopId = data.LoopId
                                },
                                $"Scheduled {args.Identity}"
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
            description =
                fn.Name
                + " "
                + fn.Description
                + "\nYou must specify SpecificTime or CronExpression accordingly. SpecificTime has higher priority if both are provided.",
            loggerFactory = loggerFactory
        )
