namespace Brainloop.Notification

open System
open FSharp.Data.Adaptive
open IcedTasks
open Fun.Blazor
open Brainloop.Db
open Brainloop.Share

type AddNotificationHandler(dbService: IDbService, globalStore: IGlobalStore) =

    interface IAddNotificationHandler with
        member _.Handle(source, message) = valueTask {
            let notification = {
                Id = 0
                Viewed = false
                Source = source
                Message = message
                CreatedAt = DateTime.Now
            }

            let! id = dbService.DbContext.Insert<Notification>(notification).ExecuteIdentityAsync()

            transact (fun _ -> globalStore.Notifications.Add({ notification with Id = id }) |> ignore)
        }
