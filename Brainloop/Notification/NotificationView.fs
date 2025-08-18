namespace Brainloop.Notification

open System
open Microsoft.Extensions.DependencyInjection
open FSharp.Data.Adaptive
open Quartz
open Fun.Result
open Fun.Blazor
open MudBlazor
open Brainloop.Db
open Brainloop.Loop


type NotificationView =

    static member private Item(notification: Notification) : NodeRenderFragment =
        html.inject (
            notification,
            fun (hook: IComponentHook, serviceProvider: IServiceProvider) ->
                let globalStore = serviceProvider.GetRequiredService<IGlobalStore>()
                let dialog = serviceProvider.GetRequiredService<IDialogService>()
                let dbContext = serviceProvider.GetRequiredService<IDbService>()
                let schedulerFactory = serviceProvider.GetRequiredService<ISchedulerFactory>()

                MudPaper'' {
                    Elevation 1
                    style { padding 8 }
                    div {
                        MudText'' {
                            style { opacity 0.5 }
                            match notification.Source with
                            | NotificationSource.SchedulerForAgent s -> s.Name + " " + notification.CreatedAt.ToLongTimeString()
                        }
                        MudText'' {
                            Typo Typo.body1
                            match notification.Source with
                            | NotificationSource.SchedulerForAgent s -> $"{notification.Message} by {s.Author}"
                        }
                    }
                    div {
                        style {
                            marginTop 4
                            displayFlex
                            justifyContentFlexEnd
                            alignItemsCenter
                            gap 8
                        }
                        match notification.Source with
                        | NotificationSource.SchedulerForAgent s ->
                            MudButton'' {
                                Size Size.Small
                                Color Color.Primary
                                OnClick(fun _ -> task { do! hook.ToggleLoop(s.LoopId, false) })
                                "Go to loop"
                            }
                            MudIconButton'' {
                                Size Size.Small
                                OnClick(fun _ -> task {
                                    let! confirm =
                                        dialog.ShowConfirm("Warning", "Remove this scheduled task", "Yes", "No", severity = Severity.Warning)
                                    if confirm then
                                        let! scheduler = schedulerFactory.GetScheduler()
                                        do! scheduler.DeleteJob(JobKey(s.Name, s.Group)) |> Task.map ignore
                                })
                                Icon Icons.Material.Filled.FreeCancellation
                            }
                            MudIconButton'' {
                                Size Size.Small
                                OnClick(fun _ -> task {
                                    do!
                                        dbContext.DbContext
                                            .Update<Notification>()
                                            .Where(fun (x: Notification) -> x.Id = notification.Id)
                                            .Set((fun x -> x.Viewed), true)
                                            .ExecuteAffrowsAsync()
                                        |> Task.map ignore

                                    transact (fun _ ->
                                        globalStore.Notifications
                                        |> Seq.tryFindIndex ((=) notification)
                                        |> Option.iter (globalStore.Notifications.RemoveAt >> ignore)
                                    )
                                })
                                Icon Icons.Material.Filled.Done
                            }
                    }
                }
        )


    static member private Dialog(onClose) =
        html.inject (fun (globalStore: IGlobalStore, dbService: IDbService) -> MudDialog'' {
            Header "Notifications" onClose
            DialogContent(
                div {
                    style {
                        maxHeight 720
                        height 500
                        displayFlex
                        flexDirectionColumn
                        gap 12
                        padding 4
                        overflowYAuto
                        overflowXHidden
                    }
                    adapt {
                        let! notifications = globalStore.Notifications
                        region {
                            if notifications.Count = 0 then
                                MudAlert'' {
                                    Severity Severity.Info
                                    "There is no notifications so far"
                                }
                        }
                        region {
                            for notification in notifications |> Seq.sortByDescending _.CreatedAt do
                                NotificationView.Item notification
                        }
                    }
                }
            )
            DialogActions(
                MudButton'' {
                    StartIcon Icons.Material.Filled.DoneAll
                    OnClick(fun _ -> task {
                        do!
                            dbService.DbContext
                                .Update<Notification>()
                                .Where(fun (x: Notification) -> x.Viewed = false)
                                .Set((fun x -> x.Viewed), true)
                                .ExecuteAffrowsAsync()
                            |> Task.map ignore
                        transact (fun _ -> globalStore.Notifications.Clear())
                        onClose ()
                    })
                }
            )
        })


    static member Indicator(?size: Size) =
        html.inject (fun (hook: IComponentHook, serviceProvider: IServiceProvider) ->
            let globalStore = serviceProvider.GetRequiredService<IGlobalStore>()
            let dialog = serviceProvider.GetRequiredService<IDialogService>()
            let snackbar = serviceProvider.GetRequiredService<ISnackbar>()
            let dbContext = serviceProvider.GetRequiredService<IDbService>()
            let size' = defaultArg size Size.Medium

            hook.AddFirstAfterRenderTask(fun _ -> task {
                // Restore old notifications
                let! notifications = dbContext.DbContext.Queryable<Notification>().Where(fun (x: Notification) -> x.Viewed = false).ToListAsync()
                transact (fun _ ->
                    for notification in notifications do
                        if globalStore.Notifications |> Seq.exists (fun x -> x.Id = notification.Id) |> not then
                            globalStore.Notifications.Add notification |> ignore
                )

                let mutable isFirstInit = true
                hook.AddDispose(
                    globalStore.Notifications.AddCallback(fun _ delta ->
                        if isFirstInit then
                            isFirstInit <- false
                        else
                            for _, op in delta do
                                match op with
                                | ElementOperation.Set n ->
                                    snackbar.Add(
                                        html.renderFragment (NotificationView.Item n),
                                        severity = Severity.Normal,
                                        configure =
                                            (fun options ->
                                                options.HideIcon <- true
                                                options.ShowCloseIcon <- false
                                            )
                                    )
                                    |> ignore
                                | _ -> ()
                    )
                )
            })

            adapt {
                let! notificationsCount = globalStore.Notifications |> AList.count
                if notificationsCount > 0 then
                    div {
                        style { positionRelative }
                        MudIconButton'' {
                            OnClick(fun _ ->
                                dialog.Show(
                                    DialogOptions(MaxWidth = MaxWidth.ExtraSmall, FullWidth = true),
                                    fun ctx -> NotificationView.Dialog ctx.Close
                                )
                            )
                            Color(if notificationsCount > 0 then Color.Primary else Color.Default)
                            Icon Icons.Material.Filled.Notifications
                            Size size'
                        }
                        MudChip'' {
                            Size Size.Small
                            Color Color.Primary
                            style {
                                positionAbsolute
                                top -4
                                right -8
                            }
                            if notificationsCount > 99 then "99+" else notificationsCount
                        }
                    }
            }
        )
