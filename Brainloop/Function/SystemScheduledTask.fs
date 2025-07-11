namespace Brainloop.Function

open System.Text.Json
open Microsoft.Extensions.Logging
open Quartz
open Brainloop.Handler
open Brainloop.Db


type SystemScheduledTaskToCallAgentData = {
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
