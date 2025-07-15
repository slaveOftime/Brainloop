namespace rec Brainloop.Db

open System


type SchedulerForAgent = {
    Name: string
    Group: string
    Author: string
    AgentId: int
    LoopId: int64
}

[<RequireQualifiedAccess>]
type NotificationSource = SchedulerForAgent of SchedulerForAgent


[<CLIMutable>]
type Notification = {
    Id: int64
    Viewed: bool
    Source: NotificationSource
    Message: string
    CreatedAt: DateTime
}
