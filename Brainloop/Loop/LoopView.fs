namespace Brainloop.Loop

open System
open FSharp.Control.Reactive
open FSharp.Data.Adaptive
open Microsoft.JSInterop
open Microsoft.Extensions.Logging
open Microsoft.Extensions.DependencyInjection
open Microsoft.AspNetCore.Components.Web
open IcedTasks
open MudBlazor
open Fun.Result
open Fun.Blazor
open Brainloop.Db
open Brainloop.Share
open Brainloop.Memory
open Brainloop.Agent
open Brainloop.Model


type LoopView =

    static member Create(currentLoop: Loop) =
        html.inject (
            $"loop-view-{currentLoop.Id}",
            fun (hook: IComponentHook, snackbar: ISnackbar, JS: IJSRuntime) ->
                let shareStore = hook.ServiceProvider.GetRequiredService<IShareStore>()
                let loopContentService = hook.ServiceProvider.GetRequiredService<ILoopContentService>()
                let logger = hook.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("LoopView")

                let containerId = Strings.GetLoopContentsContainerDomId(currentLoop.Id)
                let contents = cval ValueOption<LoopContentWrapper clist>.None
                let isLoadingMore = cval false
                let touchStartY = cval 0.
                let userScrolledEvent = Event<bool>()


                let loadAndScrollToContent (contents: LoopContentWrapper clist) (forLatest: bool) (scrollToContentId: int64) = valueTask {
                    isLoadingMore.Publish(true)
                    try
                        let getCheckId () = if forLatest then contents[contents.Count - 1].Id else contents[0].Id
                        let isContentLoaded () = contents |> AList.force |> Seq.exists (fun x -> x.Id = scrollToContentId)

                        let mutable lastCheckedId = ValueNone
                        while contents.Count > 0 && lastCheckedId <> ValueSome(getCheckId ()) && not (isContentLoaded ()) do
                            let contentId = getCheckId ()
                            lastCheckedId <- ValueSome contentId
                            let! isInView = JS.IsInView(Strings.GetLoopContentContainerDomId(scrollToContentId))
                            if not isInView then
                                if forLatest then
                                    do! loopContentService.LoadMoreLatestContentsIntoCache(currentLoop.Id)
                                else
                                    do! loopContentService.LoadMoreContentsIntoCache(currentLoop.Id)

                        if isContentLoaded () then
                            do! JS.ScrollToElementTop(containerId, Strings.GetLoopContentContainerDomId(scrollToContentId), smooth = true)

                    with ex ->
                        snackbar.ShowMessage(ex, logger)
                    isLoadingMore.Publish(false)
                }

                let loadMoreNewContents () = task {
                    isLoadingMore.Publish(true)

                    let bottomId = contents.Value |> ValueOption.bind (Seq.tryLast >> Option.map _.Id >> ValueOption.ofOption)
                    match bottomId with
                    | ValueNone -> ()
                    | ValueSome bottomId ->
                        let! isInView = JS.IsInView(Strings.GetLoopContentContainerDomId(bottomId))
                        if isInView then
                            do! loopContentService.LoadMoreLatestContentsIntoCache(currentLoop.Id)

                    isLoadingMore.Publish(false)
                }

                let loadMoreOldContents () = task {
                    isLoadingMore.Publish(true)

                    let topId = contents.Value |> ValueOption.bind (Seq.tryHead >> Option.map _.Id >> ValueOption.ofOption)
                    match topId with
                    | ValueNone -> ()
                    | ValueSome topId ->
                        let! isInView = JS.IsInView(Strings.GetLoopContentContainerDomId(topId))
                        if isInView then
                            do! loopContentService.LoadMoreContentsIntoCache(currentLoop.Id)

                    isLoadingMore.Publish(false)
                }

                let loadContentsAndFillScreen (contents: LoopContentWrapper clist) = valueTask {
                    if contents.Count > 0 then
                        isLoadingMore.Publish(true)

                        let getContentId () = contents[0].Id

                        let mutable lastCheckedId = ValueNone
                        while contents.Count > 0 && lastCheckedId <> ValueSome(getContentId ()) do
                            lastCheckedId <- ValueSome(getContentId ())
                            do! loadMoreOldContents ()

                        do! Async.Sleep 100
                        do! JS.ScrollToBottom(containerId)

                        isLoadingMore.Publish(false)
                }

                hook.AddFirstAfterRenderTask(fun _ -> task {
                    let! contents' = loopContentService.GetOrCreateContentsCache(currentLoop.Id).AsTask()
                    contents.Publish(ValueSome contents')

                    do! JS.ScrollToBottom(containerId)
                    do! loadContentsAndFillScreen contents'

                    // Load contents if user scrolled
                    userScrolledEvent.Publish
                    |> Observable.throttle (TimeSpan.FromMilliseconds 200L)
                    |> Observable.subscribe (
                        function
                        | true -> loadMoreOldContents () |> ignore
                        | false -> loadMoreNewContents () |> ignore
                    )
                    |> hook.AddDispose

                    // Load and scroll to specific content
                    shareStore.LoopContentsFocusing
                    |> AMap.tryFind currentLoop.Id
                    |> AVal.addInstantCallback (fun x ->
                        match contents.Value, x with
                        | ValueSome contents, Some contentId ->
                            transact (fun _ -> shareStore.LoopContentsFocusing.Remove currentLoop.Id |> ignore)
                            valueTask {
                                if contents |> Seq.exists (fun x -> x.Id = contentId) then
                                    do! JS.ScrollToElementBottom(containerId, Strings.GetLoopContentContainerDomId(contentId))
                                else if contents.Count > 0 && contents[0].Id > contentId then
                                    do! loadAndScrollToContent contents false contentId
                                else if contents.Count > 0 && contents[contents.Count - 1].Id < contentId then
                                    do! loadAndScrollToContent contents true contentId
                            }
                            |> ignore
                        | _ -> ()
                    )
                    |> hook.AddDispose
                })


                div {
                    style {
                        positionRelative
                        overflowHidden
                        height "100%"
                        displayFlex
                        flexDirectionColumn
                        gap 12
                    }
                    div {
                        id containerId
                        onmousewheel (
                            function
                            | :? WheelEventArgs as e when e.DeltaY < -10 -> userScrolledEvent.Trigger(true)
                            | :? WheelEventArgs as e when e.DeltaY > 10 -> userScrolledEvent.Trigger(false)
                            | _ -> ()
                        )
                        ontouchstart (fun e ->
                            match e.Touches with
                            | [| touch |] -> touchStartY.Publish(touch.ClientY)
                            | _ -> ()
                        )
                        ontouchend (fun e ->
                            match e.ChangedTouches with
                            | [| touch |] ->
                                let deltaY = touch.ClientY - touchStartY.Value
                                if Math.Abs(deltaY) > 10. then userScrolledEvent.Trigger(deltaY > 0)
                            | _ -> ()
                        )
                        class' "loop-contents-container"
                        style {
                            positionRelative
                            overflowYAuto
                            overflowXHidden
                            height "100%"
                        }
                        div {
                            id $"loop-{currentLoop.Id}-contents-outer-container"
                            style { backgroundColor "var(--mud-palette-surface)" }
                            styleElt {
                                ruleset ".loop-content-container .show-onhover" { opacity 0 }
                                ruleset ".loop-content-container:hover .show-onhover" {
                                    opacity 1
                                    transition "opacity 1s ease-in-out"
                                }
                            }
                            adapt {
                                match! contents with
                                | ValueNone -> ()
                                | ValueSome contents -> LoopView.Contents(contents, userScrolledEvent = userScrolledEvent.Publish)
                            }
                        }
                    }
                }
        )


    static member private Contents(contents: LoopContentWrapper alist, ?userScrolledEvent) : NodeRenderFragment =
        let getContentWrapper (id) = contents |> AList.filter (fun x -> x.Id = id) |> AList.tryFirst |> AVal.map ValueOption.ofOption

        adapt {
            let! contents = contents
            let mutable previousOne: LoopContentWrapper voption = ValueNone
            region {
                for contentWrapper in contents do
                    let isFollowing =
                        match previousOne with
                        | ValueSome u ->
                            u.AuthorRole = contentWrapper.AuthorRole
                            && u.Author = contentWrapper.Author
                            && contentWrapper.SourceLoopContentId.IsNone
                            && (contentWrapper.UpdatedAt.Value - u.UpdatedAt.Value).TotalMinutes < 10
                        | _ -> false
                    previousOne <- ValueSome contentWrapper
                    html.inject (
                        contentWrapper.Id,
                        (fun () ->
                            LoopView.Content(
                                contentWrapper,
                                isFollowing,
                                ?userScrolledEvent = userScrolledEvent,
                                ?sourceContent = (contentWrapper.SourceLoopContentId |> ValueOption.map getContentWrapper |> ValueOption.toOption)
                            )
                        )
                    )
            }
        }

    static member private Content
        (contentWrapper: LoopContentWrapper, isFollowing: bool, ?userScrolledEvent: IEvent<bool>, ?sourceContent: LoopContentWrapper voption aval)
        : NodeRenderFragment =
        html.inject (
            contentWrapper.Id,
            fun () ->
                let contentId = Strings.GetLoopContentContainerDomId(contentWrapper.Id)
                let sourceContent = sourceContent |> Option.defaultWith (fun _ -> AVal.constant ValueNone)

                let viewRaw = cval false
                let isEditing = cval false

                let contentDetail = adaptiview (key = "content") {
                    let! isEditing' = isEditing
                    let isUserContent = contentWrapper.AuthorRole = LoopContentAuthorRole.User
                    div {
                        style {
                            padding 0 8
                            textAlignLeft
                            width (if isEditing' || not isUserContent then "100%" else "fit-content")
                            maxWidth "100%"
                            if isUserContent then
                                css {
                                    marginLeft "auto"
                                    marginRight 0
                                }
                        }
                        if isEditing' then
                            LoopContentEditor.Create(contentWrapper, isEditing = isEditing)
                        else
                            LoopContentView.Create(contentWrapper, viewRaw, ?userScrolledEvent = userScrolledEvent)
                    }
                    adapt {
                        let! isStreamming = contentWrapper.StreammingCount |> AVal.map (fun x -> x > -1)
                        if isStreamming then
                            span { "data-flowing", true }
                            adapt {
                                match! contentWrapper.ProgressMessage with
                                | SafeString message -> MudText'' {
                                    Typo Typo.body2
                                    style {
                                        opacity 0.5
                                        marginLeft 8
                                    }
                                    message.ToLower()
                                  }
                                | _ -> ()
                            }
                    }
                }

                div {
                    id contentId
                    key contentWrapper.Id
                    class' "loop-content-container"
                    style { padding 4 0 }
                    LoopView.ContentHeader(contentWrapper, sourceContent)
                    div {
                        style {
                            backgroundColor "transparent"
                            overflowXAuto
                            if contentWrapper.AuthorRole = LoopContentAuthorRole.User then
                                css {
                                    borderRightWidth 4
                                    borderRightColor "#c4c4c426"
                                    backgroundImage "linear-gradient(to right, #00000000, #c4c4c41c);"
                                }
                            else
                                css {
                                    borderLeftWidth 4
                                    borderLeftColor "#c4c4c426"
                                }
                        }
                        class' (
                            "loop-content-body "
                            + if contentWrapper.AuthorRole = LoopContentAuthorRole.User then
                                  "border-hover-primary"
                              else if contentWrapper.SourceLoopContentId.IsSome then
                                  "border-hover-primary"
                              else
                                  ""
                        )
                        contentDetail
                    }
                    adapt {
                        let! items = contentWrapper.Items
                        let! isStreamming = contentWrapper.IsStreaming
                        let! hasError = contentWrapper.ErrorMessage |> AVal.map (String.IsNullOrEmpty >> not)
                        div {
                            class' (if isStreamming || hasError then "" else "show-onhover")
                            LoopView.ContentFooter(contentWrapper, viewRaw, isEditing)
                        }
                        script {
                            key (isStreamming, items)
                            $$"""
                            setTimeout(
                                _ => { mediumZoom('#{{contentId}} img:not(.disable-zoom)', { background: 'var(--mud-palette-surface)' }); },
                                500
                            );
                            """
                        }
                    }
                }
        )

    static member private ContentHeader(contentWrapper: LoopContentWrapper, sourceContent: LoopContentWrapper voption aval) : NodeRenderFragment = div {
        style {
            displayFlex
            alignItemsCenter
            gap 4
            paddingTop 4
            paddingBottom 4
            overflowXAuto
            match contentWrapper.AuthorRole with
            | LoopContentAuthorRole.User -> css {
                flexDirectionRowReverse
                "justify-items: flex-end"
              }
            | _ -> css { }
        }
        LoopView.AuthorBtn(contentWrapper)
        LoopView.SourceBtn(contentWrapper, sourceContent)
        LoopView.ModelBtn(contentWrapper)
        LoopView.StopBtn(contentWrapper)
        LoopView.TokenUsage(contentWrapper)
        adapt {
            match! contentWrapper.TotalDurationMs with
            | x when x > 0 -> MudChip'' {
                Size Size.Small
                Math.Round(TimeSpan.FromMilliseconds(x).TotalSeconds, 1)
                "s"
              }
            | _ -> ()
        }
        adapt {
            let! updatedAt = contentWrapper.UpdatedAt
            MudText'' {
                style {
                    whiteSpaceNowrap
                    fontSize "0.75rem"
                }
                Typo Typo.body2
                match contentWrapper.AuthorRole with
                | LoopContentAuthorRole.System -> ()
                | _ -> updatedAt.ToString("yy/M/d H:m:s").ToString()
            }
        }
    }

    static member private ContentFooter(contentWrapper: LoopContentWrapper, viewRaw: bool cval, isEditing: bool cval) : NodeRenderFragment = div {
        style {
            displayFlex
            alignItemsCenter
            gap 12
            overflowXAuto
            paddingTop 4
            whiteSpaceNowrap
            match contentWrapper.AuthorRole with
            | LoopContentAuthorRole.User -> css {
                flexDirectionRowReverse
                "justify-items: flex-end"
              }
            | _ -> css { }
        }
        LoopView.RetryBtn(contentWrapper)
        adapt {
            match! contentWrapper.ErrorMessage with
            | SafeString error -> MudTooltip'' {
                Arrow
                TooltipContent(
                    div {
                        style { maxWidth 300 }
                        error
                    }
                )
                Color Color.Error
                Placement Placement.Top
                MudIconButton'' {
                    Icon Icons.Material.Filled.Error
                    Size Size.Small
                    Color Color.Error
                }
              }
            | _ -> ()
        }
        LoopView.CopyBtn(contentWrapper)
        LoopView.EditBtn(contentWrapper, isEditing)
        LoopView.LockBtn(contentWrapper, isEditing)
        region {
            match contentWrapper.AuthorRole with
            | LoopContentAuthorRole.System -> ()
            | _ -> adapt {
                let! viewRaw, setViewRaw = viewRaw.WithSetter()
                MudIconButton'' {
                    Size Size.Small
                    Color(if viewRaw then Color.Primary else Color.Default)
                    Icon(if viewRaw then Icons.Material.Filled.RawOn else Icons.Material.Filled.RawOff)
                    OnClick(fun _ -> setViewRaw (not viewRaw))
                }
              }
        }
        region {
            match contentWrapper.SourceLoopContentId with
            | ValueSome sourceId ->
                html.inject (fun (shareStore: IShareStore) -> MudIconButton'' {
                    Size Size.Small
                    Icon Icons.Material.Filled.AdsClick
                    OnClick(fun _ -> transact (fun _ -> shareStore.LoopContentsFocusing.Add(contentWrapper.LoopId, sourceId) |> ignore))
                })
            | ValueNone -> ()
        }
        html.inject (fun (hook: IComponentHook, dbService: IDbService) -> MudIconButton'' {
            Size Size.Small
            Icon Icons.Material.Filled.AirlineStops
            OnClick(fun _ -> task {
                let! id =
                    dbService.DbContext
                        .Insert<Loop>(
                            {
                                Loop.Default with
                                    SourceLoopContentId = Nullable contentWrapper.Id
                            }
                        )
                        .ExecuteIdentityAsync()
                do! hook.ToggleLoop(id, false)
            })
        })
        LoopView.StopBtn(contentWrapper)
        LoopView.TokenUsage(contentWrapper)
    }

    static member private AuthorBtn(contentWrapper: LoopContentWrapper) : NodeRenderFragment =
        html.inject (fun (hook: IComponentHook) -> MudButton'' {
            style {
                flexShrink 0
                whiteSpaceNowrap
            }
            Size Size.Small
            OnClick(fun _ ->
                match contentWrapper.AgentId with
                | ValueNone -> ()
                | ValueSome agentId ->
                    hook.ShowDialog(
                        DialogOptions(MaxWidth = MaxWidth.Medium, FullWidth = true, BackdropClick = false, CloseOnEscapeKey = false),
                        fun ctx ->
                            AgentCard.Dialog(
                                agentId,
                                fun _ ->
                                    ctx.Close()
                                    hook.StateHasChanged()
                            )
                    )
            )
            MudText'' {
                Typo Typo.subtitle2
                Color(
                    if contentWrapper.AuthorRole = LoopContentAuthorRole.User then
                        Color.Primary
                    else
                        Color.Default
                )
                contentWrapper.Author
            }
        })

    static member private SourceBtn(contentWrapper: LoopContentWrapper, sourceContent: LoopContentWrapper voption aval) : NodeRenderFragment = adapt {
        match! sourceContent with
        | ValueSome x when x.Author <> contentWrapper.Author && x.AuthorRole <> LoopContentAuthorRole.User ->
            html.inject (fun (dialog: IDialogService) -> MudChip'' {
                style {
                    flexShrink 0
                    whiteSpaceNowrap
                }
                Size Size.Small
                OnClick(fun _ ->
                    dialog.Show(
                        DialogOptions(MaxWidth = MaxWidth.Small, FullWidth = true),
                        fun ctx -> MudDialog'' {
                            Header "Modify Prompt" ctx.Close
                            DialogContent(
                                adapt {
                                    let! isStreaming = contentWrapper.IsStreaming
                                    let! directPrompt, setDirectPrompt = contentWrapper.DirectPrompt.WithSetter()
                                    match directPrompt with
                                    | ValueSome x -> MudTextField'' {
                                        FullWidth
                                        Value x
                                        ValueChanged(ValueSome >> setDirectPrompt)
                                        Label "Prompt"
                                        Lines 1
                                        MaxLines 30
                                        AutoGrow
                                        AutoFocus
                                        Required
                                        ReadOnly isStreaming
                                        Variant Variant.Outlined
                                      }
                                    | _ -> html.none
                                }
                            )
                            DialogActions [| LoopView.RetryBtn(contentWrapper, onClicked = ctx.Close) |]
                        }
                    )
                )
                MudTooltip'' {
                    Arrow
                    Placement Placement.Top
                    TooltipContent(
                        adapt {
                            let! directPrompt = contentWrapper.DirectPrompt
                            match directPrompt with
                            | ValueSome(SafeString x) -> div {
                                style { maxWidth 300 }
                                x
                              }
                            | _ -> html.none
                        }
                    )
                    "by "
                    x.Author
                }
            })
        | _ -> ()
    }

    static member private EditBtn(contentWrapper: LoopContentWrapper, isEditing: bool cval) : NodeRenderFragment =
        html.inject (fun (JS: IJSRuntime) -> adapt {
            let! isEditing' = isEditing
            let! isEncrypted = contentWrapper.IsEncrypted
            let! isStreaming = contentWrapper.IsStreaming
            if not isStreaming && not isEditing' && not isEncrypted then
                LoopView.DeleteBtn(contentWrapper)
                MudIconButton'' {
                    Size Size.Small
                    Icon Icons.Material.Filled.Edit
                    Disabled isStreaming
                    OnClick(fun _ -> task {
                        isEditing.Publish true
                        do! Async.Sleep 100
                        do!
                            JS.ScrollToElementBottom(
                                Strings.GetLoopContentsContainerDomId(contentWrapper.LoopId),
                                Strings.GetLoopContentContainerDomId(contentWrapper.Id),
                                smooth = true
                            )
                    })
                }
        })

    static member private ModelBtn(contentWrapper: LoopContentWrapper) : NodeRenderFragment =
        html.inject (fun (hook: IComponentHook, modelService: IModelService) -> adapt {
            let! modelId = contentWrapper.ModelId
            let! modelName =
                match modelId with
                | ValueNone -> AVal.constant ""
                | ValueSome modelId ->
                    modelService.TryGetModelWithCache(modelId)
                    |> ValueTask.map (ValueOption.map _.Model >> ValueOption.defaultValue "")
                    |> AVal.ofValueTask ""
            if not (String.IsNullOrEmpty modelName) then
                MudChip'' {
                    style {
                        flexShrink 0
                        whiteSpaceNowrap
                    }
                    Size Size.Small
                    OnClick(fun _ ->
                        match contentWrapper.ModelId.Value with
                        | ValueNone -> ()
                        | ValueSome modelId ->
                            hook.ShowDialog(
                                DialogOptions(MaxWidth = MaxWidth.Medium, FullWidth = true),
                                fun ctx ->
                                    ModelCard.Dialog(
                                        modelId,
                                        fun _ ->
                                            ctx.Close()
                                            hook.StateHasChanged()
                                    )
                            )
                    )
                    modelName
                }
        })

    static member private RetryBtn(contentWrapper: LoopContentWrapper, ?onClicked: unit -> unit) : NodeRenderFragment =
        html.inject (fun (hook: IComponentHook, serviceProvider: IServiceProvider) ->
            let logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("LoopContentRegen")
            let JS = serviceProvider.GetRequiredService<IJSRuntime>()
            let snackbar = serviceProvider.GetRequiredService<ISnackbar>()
            let loopService = serviceProvider.GetRequiredService<ILoopService>()
            let agentService = serviceProvider.GetRequiredService<IAgentService>()

            let isSending = cval false
            let refreshAgentsListCount = cval 0
            let selectedModel = cval ValueOption<Model>.None

            let resend modelId = task {
                isSending.Publish true
                try
                    do! loopService.Resend(contentWrapper.LoopId, contentWrapper.Id, ?modelId = modelId)
                    do!
                        JS.ScrollToElementBottom(
                            Strings.GetLoopContentsContainerDomId(contentWrapper.LoopId),
                            Strings.GetLoopContentContainerDomId(contentWrapper.Id)
                        )
                with ex ->
                    snackbar.ShowMessage(ex, logger)
                isSending.Publish false
            }

            hook.AddDispose(
                selectedModel.AddLazyCallback(fun model ->
                    match model with
                    | ValueSome x -> resend (Some x.Id) |> ignore
                    | _ -> ()
                    selectedModel.Publish ValueNone
                )
            )

            region {
                match contentWrapper.AgentId with
                | ValueNone -> ()
                | ValueSome agentId -> adapt {
                    let! _ = refreshAgentsListCount
                    let! isSending = isSending
                    let! cancellationTokenSource = contentWrapper.CancellationTokenSource
                    let! agent = agentService.TryGetAgentWithCache(agentId) |> AVal.ofValueTask ValueNone

                    MudIconButton'' {
                        Icon Icons.Material.Filled.Repeat
                        Size Size.Small
                        Disabled(isSending || cancellationTokenSource.IsSome)
                        OnClick(fun _ ->
                            onClicked |> Option.iter (fun fn -> fn ())
                            resend (contentWrapper.ModelId.Value |> ValueOption.toOption)
                        )
                    }
                    ModelSelector.CreateMenu(selectedModel)
                  }
            }
        )

    static member private StopBtn(contentWrapper: LoopContentWrapper) : NodeRenderFragment = adapt {
        match! contentWrapper.CancellationTokenSource with
        | ValueSome cancellationTokenSource -> MudIconButton'' {
            Size Size.Small
            Color Color.Warning
            Icon Icons.Material.Filled.Stop
            OnClick(fun _ -> cancellationTokenSource.Cancel())
          }
        | _ -> ()
    }

    static member private LockBtn(contentWrapper: LoopContentWrapper, isEditing: bool aval) : NodeRenderFragment =
        html.inject (fun (dialogService: IDialogService) -> adapt {
            let! isEditing = isEditing
            let! isEncrypted = contentWrapper.IsEncrypted
            match isEditing, isEncrypted with
            | false, false -> MudIconButton'' {
                Size Size.Small
                Icon Icons.Material.Outlined.Lock
                OnClick(fun _ ->
                    dialogService.Show(
                        DialogOptions(MaxWidth = MaxWidth.ExtraSmall, FullWidth = true),
                        fun ctx -> LoopContentSensitive.EncryptDialog(contentWrapper, ctx.Close)
                    )
                )
              }
            | _ -> ()
        })

    static member private CopyBtn(contentWrapper: LoopContentWrapper) : NodeRenderFragment =
        html.inject (fun (serviceProvider: IServiceProvider) ->
            let logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("LoopContentCopy")
            let JS = serviceProvider.GetRequiredService<IJSRuntime>()
            let snackbar = serviceProvider.GetRequiredService<ISnackbar>()
            let dialog = serviceProvider.GetRequiredService<IDialogService>()

            fragment {
                MudIconButton'' {
                    Size Size.Small
                    Icon Icons.Material.Outlined.CopyAll
                    OnClick(fun _ -> task {
                        let! closeFn = dialog.ShowLoading(message = MudText'' { "Copy as text ..." })
                        try
                            do! Threading.Tasks.Task.Run<unit>(fun _ -> task { do! JS.CopyText(contentWrapper.ConvertItemsToString()) })
                            snackbar.ShowMessage("Copied successfully", severity = Severity.Success)
                        with ex ->
                            snackbar.ShowMessage(ex, logger)
                        closeFn ()
                    })
                }
                MudIconButton'' {
                    Size Size.Small
                    Icon Icons.Material.Outlined.Share
                    OnClick(fun _ -> task {
                        let! closeFn = dialog.ShowLoading(message = MudText'' { "Copy as image ..." })
                        try
                            do! JS.CopyElementAsImage(Strings.GetLoopContentContainerDomId(contentWrapper.Id))
                            snackbar.ShowMessage("Copied successfully", severity = Severity.Success)
                        with ex ->
                            snackbar.ShowMessage(ex, logger)
                        closeFn ()
                    })
                }
            }
        )

    static member private DeleteBtn(contentWrapper: LoopContentWrapper) : NodeRenderFragment =
        html.inject (fun (serviceProvider: IServiceProvider) ->
            let logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("LoopContentDelete")
            let dialog = serviceProvider.GetRequiredService<IDialogService>()
            let snackbar = serviceProvider.GetRequiredService<ISnackbar>()
            let loopContentService = serviceProvider.GetRequiredService<ILoopContentService>()
            let documentService = serviceProvider.GetRequiredService<IDocumentService>()

            MudIconButton'' {
                Size Size.Small
                Icon Icons.Material.Outlined.Delete
                OnClick(fun _ -> task {
                    let! result = dialog.ShowConfirm("Warning", "Are you sure to delete this content?", "Yes", "No", severity = Severity.Error)
                    if result then
                        try
                            do! loopContentService.DeleteLoopContent(contentWrapper.LoopId, contentWrapper.Id)
                            for item in contentWrapper.Items.Value do
                                match item with
                                | LoopContentItem.File x -> do! documentService.DeleteFile(x.Name)
                                | LoopContentItem.Excalidraw x -> do! documentService.DeleteFile(x.ImageFileName)
                                | _ -> ()
                        with ex ->
                            snackbar.ShowMessage(ex, logger)
                })
            }
        )

    static member private TokenUsage(contentWrapper: LoopContentWrapper) : NodeRenderFragment = adapt {
        let! inputTokens = contentWrapper.InputTokens
        let! outputTokens = contentWrapper.OutputTokens
        if inputTokens > 0 || outputTokens > 0 then
            MudChip'' {
                Size Size.Small
                if inputTokens > 0 then
                    "↑"
                    int inputTokens
                    " "
                if outputTokens > 0 then
                    "↓"
                    int outputTokens
                " tokens"
            }
    }
