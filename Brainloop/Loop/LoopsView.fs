namespace Brainloop.Loop

open System
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Microsoft.AspNetCore.Components.Web
open Microsoft.JSInterop
open FSharp.Data.Adaptive
open MudBlazor
open MudBlazor.Services
open Fun.Result
open Fun.Blazor
open Brainloop.Db


type LoopsView =

    static member Create() =
        html.inject (
            "loops-view",
            fun (hook: IComponentHook, loopService: ILoopService, browserViewportService: IBrowserViewportService, JS: IJSRuntime) ->
                let globalStore = hook.ServiceProvider.GetRequiredService<IGlobalStore>()
                let shareStore = hook.ServiceProvider.GetRequiredService<IShareStore>()
                let snackbar = hook.ServiceProvider.GetRequiredService<ISnackbar>()
                let dialog = hook.ServiceProvider.GetRequiredService<IDialogService>()
                let logger = hook.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("LoopsView")

                let hDelta = 20
                let loopWidth = 760
                let marginSize = 12

                let isHovering = cval 0L
                let splittedWidth = cval 0
                let windowWidth = cval 0


                let calculateSplitWidth () = task {
                    let! windowSize = browserViewportService.GetCurrentBrowserWindowSizeAsync()
                    windowWidth.Publish(windowSize.Width)
                    let availableWidth = windowSize.Width - loopWidth - marginSize * 2
                    let loopCount = globalStore.ActiveLoops.Value.Count + 2
                    let delta = availableWidth / Math.Max(1, loopCount - 1)
                    if delta > 10 then splittedWidth.Publish(delta) else splittedWidth.Publish(0)
                }

                let loopTitle (activeLoop: Loop) = adaptiview (key = $"loop-title-{activeLoop.Id}") {
                    let oldSummary =
                        match activeLoop.Description with
                        | SafeString x -> x
                        | _ -> "New loop started"
                    let! summaryState =
                        globalStore.LoopTitles |> AMap.tryFind activeLoop.Id |> AVal.map (Option.defaultValue LoadingState.NotStartYet)
                    span {
                        class' (if summaryState.IsLoadingNow then "loading-shimmer" else "")
                        summaryState.Value |> Option.defaultValue oldSummary
                    }
                }

                let selectedLoopTitle (activeLoop: Loop) = region {
                    div {
                        style {
                            displayFlex
                            justifyContentCenter
                            alignItemsCenter
                            paddingRight 8
                            gap 4
                        }
                        div {
                            style {
                                margin 8 12
                                overflowHidden
                                textOverflowWithMaxLines 1
                                flexGrow 1
                            }
                            adapt {
                                let isEditing = cval false
                                match! isEditing with
                                | true -> MudTextField'' {
                                    Value activeLoop.Description
                                    ValueChanged(fun v ->
                                        loopService.BuildTitle(activeLoop.Id, title = v) |> ignore
                                        isEditing.Publish(false)
                                    )
                                    AutoFocus
                                    FullWidth
                                    OnKeyUp(fun e -> if e.Key = "Escape" then isEditing.Publish(false))
                                  }
                                | false -> MudText'' {
                                    style { backgroundColor "transparent" }
                                    ondblclick (fun _ -> isEditing.Publish(true))
                                    Typo Typo.h6
                                    Color Color.Primary
                                    loopTitle activeLoop
                                  }
                            }
                        }
                        //html.inject (fun () -> adapt {
                        //    let! isLoading =
                        //        globalStore.LoopTitles
                        //        |> AMap.tryFind activeLoop.Id
                        //        |> AVal.map (Option.defaultValue LoadingState.NotStartYet >> (fun x -> x.IsLoadingNow))
                        //    MudIconButton'' {
                        //        class' (if isLoading then "pulse" else "")
                        //        Disabled isLoading
                        //        Size Size.Small
                        //        Icon Icons.Material.Filled.Refresh
                        //        OnClick(fun _ -> task {
                        //            try
                        //                do! loopService.BuildTitle(activeLoop.Id)
                        //            with ex ->
                        //                snackbar.ShowMessage(ex, logger)
                        //        })
                        //    }
                        //})
                        MudTooltip'' {
                            Arrow
                            Placement Placement.Top
                            TooltipContent "Share as image and copy to clipboard"
                            adapt {
                                let! isSharing = shareStore.LoopsSharing |> AMap.tryFind activeLoop.Id |> AVal.map (Option.defaultValue false)
                                MudIconButton'' {
                                    class' (if isSharing then "pulse" else "")
                                    Size Size.Small
                                    Icon Icons.Material.Outlined.Share
                                    Disabled isSharing
                                    OnClick(fun _ -> task {
                                        let! closeFn = dialog.ShowLoading(message = MudText'' { "Sharing loop as image..." })
                                        try
                                            transact (fun _ -> shareStore.LoopsSharing.Add(activeLoop.Id, true) |> ignore)
                                            do! JS.CopyElementAsImage($"loop-{activeLoop.Id}-contents-outer-container")
                                            snackbar.ShowMessage("Copied to clipboard", severity = Severity.Success)
                                        with ex ->
                                            snackbar.ShowMessage(ex, logger)
                                        closeFn ()
                                        transact (fun _ -> shareStore.LoopsSharing.Remove(activeLoop.Id) |> ignore)
                                    })
                                }
                            }
                        }
                        MudIconButton'' {
                            Size Size.Small
                            Icon Icons.Material.Filled.Close
                            OnClick(fun _ -> hook.ToggleLoop(activeLoop.Id, true))
                        }
                    }
                }

                let loopTitle (activeLoop: Loop) isLeft =
                    let titleView color' (extraStyle: Fun.Css.Internal.CombineKeyValue) = MudText'' {
                        style {
                            padding 12
                            extraStyle
                        }
                        class' "loop-sub-title"
                        Align(if isLeft then Align.Left else Align.Right)
                        Typo Typo.subtitle1
                        Color color'
                        loopTitle activeLoop
                    }
                    region {
                        titleView Color.Default (css { textOverflowWithMaxLines 1 })
                        adapt {
                            let! isHovering = isHovering |> AVal.map ((=) activeLoop.Id)
                            MudPopover'' {
                                style { "pointer-events: none" }
                                Open isHovering
                                Fixed
                                TransformOrigin(if isLeft then Origin.TopLeft else Origin.TopRight)
                                AnchorOrigin(if isLeft then Origin.TopLeft else Origin.TopRight)
                                div {
                                    style {
                                        maxWidth 400
                                        borderWidth 1
                                        borderStyleSolid
                                        borderColor "var(--mud-palette-primary)"
                                    }
                                    titleView Color.Primary (css { })
                                }
                            }
                        }
                    }


                hook.AddFirstAfterRenderTask(fun _ -> task {
                    do! hook.LoadLoops()
                    hook.AddDispose(globalStore.ActiveLoops.AddCallback(fun _ _ -> calculateSplitWidth () |> ignore))

                    let observerId = Guid.NewGuid()
                    do!
                        browserViewportService.SubscribeAsync(
                            observerId,
                            Action<_>(fun _ -> calculateSplitWidth () |> ignore),
                            options = ResizeOptions(NotifyOnBreakpointOnly = false)
                        )
                    hook.AddDispose(
                        { new IDisposable with
                            member _.Dispose() = browserViewportService.UnsubscribeAsync(observerId) |> ignore
                        }
                    )
                })


                div {
                    style {
                        height "100%"
                        displayFlex
                        flexDirectionColumn
                        gap 12
                        overflowHidden
                    }
                    div {
                        style {
                            overflowHidden
                            height "100%"
                            positionRelative
                            displayFlex
                            flexDirectionColumn
                            alignItemsCenter
                            justifyContentCenter
                        }
                        class' "loops-container"
                        adapt {
                            let! activeLoops = globalStore.ActiveLoops
                            let! currentLoop, setCurrentLoop = shareStore.CurrentLoop.WithSetter()
                            let! splittedWidth = splittedWidth

                            let activeLoops =
                                // If the splitted width is too small we just display the current one of last one if there is no current one
                                if splittedWidth < 10 then
                                    let currentLoop =
                                        match currentLoop with
                                        | ValueNone -> activeLoops |> Seq.tryLast |> ValueOption.ofOption
                                        | x -> x

                                    match currentLoop with
                                    | ValueNone -> []
                                    | ValueSome x -> [ x ]
                                else
                                    activeLoops |> Seq.toList

                            let activeLoopsLength = activeLoops.Length

                            let currentLoopIndex =
                                match currentLoop with
                                | ValueNone -> activeLoopsLength - 1
                                | ValueSome loop -> activeLoops |> List.tryFindIndex ((=) loop) |> Option.defaultValue (activeLoopsLength - 1)

                            for i, activeLoop in Seq.indexed activeLoops do
                                let loopView pos = MudPaper'' {
                                    key $"loop-{activeLoop.Id}"
                                    style {
                                        width (if activeLoops.Length = 1 then "100%" else $"{loopWidth}px")
                                        maxWidth loopWidth
                                        positionAbsolute
                                        left (
                                            if activeLoops.Length = 1 then
                                                "unset"
                                            else
                                                $"{float marginSize + float ((i + 1) * splittedWidth)}px"
                                        )
                                        bottom (if windowWidth.Value < 400 then 0 else marginSize)
                                        displayFlex
                                        flexDirectionColumn
                                        gap 12
                                        transition "width 0.5s ease, height 0.5s ease, top 0.5s ease, bottom 0.5s ease, opacity 0.5s ease"
                                        match pos with
                                        | ValueNone -> css {
                                            zIndex (activeLoopsLength + 1)
                                            top (if windowWidth.Value < 400 then 0 else 8)
                                            bottom (if windowWidth.Value < 400 then 0 else 8)
                                            opacity 1.0
                                          }
                                        | ValueSome isLeft -> css {
                                            zIndex (if isLeft then i else activeLoopsLength - i - 1)
                                            cursorPointer
                                            opacity 0.95
                                            if isLeft then
                                                css {
                                                    top (32 + (currentLoopIndex - i - 1) * hDelta)
                                                //bottom (32 + (currentLoopIndex - i - 1) * hDelta)
                                                }
                                            else
                                                css {
                                                    top (32 + (i - currentLoopIndex - 1) * hDelta)
                                                //bottom (32 + (i - currentLoopIndex - 1) * hDelta)
                                                }
                                          }
                                    }
                                    class' (if pos.IsSome then "inactive-loop" else "")
                                    onclick (fun _ -> setCurrentLoop (ValueSome activeLoop))
                                    onmouseover (fun _ -> if pos.IsSome then isHovering.Publish(activeLoop.Id))
                                    onmouseout (fun _ -> if pos.IsSome then isHovering.Publish(-1))
                                    Elevation(if pos.IsNone then 12 else 2)
                                    div {
                                        class' "loop-container flow-border"
                                        region {
                                            match pos with
                                            | ValueNone ->
                                                PageTitle'' {
                                                    if String.IsNullOrWhiteSpace activeLoop.Description then
                                                        "Brainloops"
                                                    else
                                                        activeLoop.Description
                                                }
                                                selectedLoopTitle activeLoop
                                            | ValueSome isLeft -> loopTitle activeLoop isLeft
                                        }
                                        ErrorBoundary'' {
                                            ErrorContent(fun error -> MudAlert'' {
                                                Severity Severity.Error
                                                string error
                                            })
                                            LoopView.Create(activeLoop)
                                        }
                                        div {
                                            style {
                                                overflowHidden
                                                flexShrink 0
                                                height (if i = currentLoopIndex then "fit-content" else "0px")
                                            }
                                            LoopUserInput.Create(activeLoop.Id)
                                        }
                                    }
                                }

                                if i < currentLoopIndex && currentLoopIndex <> 0 then loopView (ValueSome true)
                                elif i = currentLoopIndex then loopView ValueNone
                                elif activeLoopsLength > 1 then loopView (ValueSome false)
                        }
                    }
                }
        )
