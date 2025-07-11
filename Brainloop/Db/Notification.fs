namespace rec Brainloop.Db

open System


type NotificationScheduler = {
    Name: string
    Group: string
    Author: string
    AgentId: int
    LoopId: int64
}

[<RequireQualifiedAccess>]
type NotificationSource = Scheduler of NotificationScheduler


[<CLIMutable>]
type Notification = {
    Id: int64
    Viewed: bool
    Source: NotificationSource
    Message: string
    CreatedAt: DateTime
}
