namespace Brainloop.Function

open FSharp.Control
open FSharp.Data.Adaptive
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Caching.Memory
open Microsoft.SemanticKernel
open MudBlazor
open Fun.Blazor
open Brainloop.Db


type McpToolsChecker =

    static member Create(name: string, config: McpConfig) =
        html.inject (
            config,
            fun (snackbar: ISnackbar, loggerFactory: ILoggerFactory, memoryCache: IMemoryCache) ->
                let logger = loggerFactory.CreateLogger(nameof (McpToolsChecker))
                let isOpen = cval false
                let isLoadingFunctions = cval false

                let mutable tools = Seq.empty

                let loadOrRefreshTools () = task {
                    isLoadingFunctions.Publish(true)
                    try
                        config.ClearToolsCache(name, memoryCache)
                        let! fns = config.GetTools(name, memoryCache, loggerFactory)
                        tools <- fns
                        isOpen.Publish(true)

                    with ex ->
                        snackbar.ShowMessage(ex, logger)
                    isLoadingFunctions.Publish(false)
                }

                div {
                    style {
                        displayFlex
                        alignItemsCenter
                        gap 8
                    }
                    adapt {
                        let! isLoading = isLoadingFunctions
                        MudButton'' {
                            Disabled isLoading
                            Variant Variant.Outlined
                            OnClick(fun _ -> loadOrRefreshTools ())
                            "Check MCP Tools"
                        }
                        if isLoading then
                            MudProgressCircular'' {
                                Indeterminate true
                                Size Size.Small
                                Color Color.Primary
                            }
                    }
                    adapt {
                        let! isOpen' = isOpen
                        MudDialog'' {
                            Visible isOpen'
                            Options(DialogOptions(BackdropClick = true, CloseOnEscapeKey = true, MaxWidth = MaxWidth.Small, FullWidth = true))
                            OnBackdropClick(fun _ -> isOpen.Publish(false))
                            TitleContent "MCP Tools"
                            DialogContent(
                                MudList'' {
                                    for tool in tools do
                                        let kernelFunction = tool.AsKernelFunction()
                                        MudListItem'' {
                                            MudText'' {
                                                Typo Typo.subtitle1
                                                Color Color.Primary
                                                tool.Name
                                            }
                                            MudText'' {
                                                Typo Typo.body2
                                                tool.Description
                                            }
                                            if kernelFunction.Metadata.Parameters.Count > 0 then
                                                MudChipSet'' {
                                                    Size Size.Small
                                                    for props in kernelFunction.Metadata.Parameters do
                                                        MudTooltip'' {
                                                            Arrow
                                                            Placement Placement.Top
                                                            Text props.Description
                                                            MudChip'' { props.Name }
                                                        }
                                                }
                                        }
                                }
                            )
                            DialogActions [|
                                MudButton'' {
                                    OnClick(fun _ -> isOpen.Publish(false))
                                    "Close"
                                }
                            |]
                        }
                    }
                }
        )
