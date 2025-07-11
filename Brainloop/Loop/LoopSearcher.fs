namespace Brainloop.Loop

open System
open System.Threading
open System.Threading.Tasks
open FSharp.Control
open FSharp.Data.Adaptive
open Microsoft.Extensions.Logging
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.VectorData
open IcedTasks
open MudBlazor
open Fun.Result
open Fun.Blazor
open Brainloop.Db
open Brainloop.Memory


type LoopSearcher =

    static member Create(?iconOnly: bool) =
        html.inject (fun (hook: IComponentHook) ->
            let iconOnly = defaultArg iconOnly false

            let showSearchDialog () =
                hook.ShowDialog(DialogOptions(MaxWidth = MaxWidth.Small, FullWidth = true), fun ctx -> LoopSearcher.SearchDialog(ctx.Close))

            div {
                style {
                    displayFlex
                    alignItemsCenter
                    gap 2
                    backgroundColor "var(--mud-palette-surface)"
                }
                adapt {
                    if iconOnly then
                        MudIconButton'' {
                            Icon Icons.Material.Filled.Search
                            OnClick(ignore >> showSearchDialog)
                        }
                    else
                        MudTextField'' {
                            style {
                                maxWidth 240
                                minWidth 120
                            }
                            Adornment Adornment.End
                            AdornmentIcon Icons.Material.Filled.Search
                            Variant Variant.Outlined
                            Margin Margin.Dense
                            Placeholder "Search loops or memories"
                            AutoFocus iconOnly
                            onclick (ignore >> showSearchDialog)
                        }
                }
            }
        )


    static member SearchDialog(onClose: unit -> unit) =
        html.inject (fun (hook: IComponentHook, serviceProvider: IServiceProvider) ->
            let logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("LoopSearcher")
            let dbService = serviceProvider.GetRequiredService<IDbService>()
            let snackbar = serviceProvider.GetRequiredService<ISnackbar>()
            let shareStore = serviceProvider.GetRequiredService<IShareStore>()
            let memoryService = serviceProvider.GetRequiredService<IMemoryService>()

            let isSearching = cval false

            let mutable cancellationTokenSource: CancellationTokenSource | null = null

            let search (query: string) = valueTask {
                isSearching.Publish(true)

                try
                    match cancellationTokenSource with
                    | null -> ()
                    | token -> token.Cancel()

                    let source = new CancellationTokenSource()
                    cancellationTokenSource <- source

                    transact (fun _ -> shareStore.SearchResults.Clear())

                    let! loops =
                        dbService.DbContext
                            .Queryable<Loop>()
                            .Where(fun (x: Loop) -> String.IsNullOrEmpty query || x.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
                            .OrderByDescending(fun x -> x.UpdatedAt)
                            .Take(15)
                            .ToListAsync(cancellationToken = source.Token)

                    transact (fun _ ->
                        shareStore.SearchResults.AddRange(
                            loops
                            |> Seq.map (fun x -> {
                                Score = 0.
                                Text = x.Description
                                Result = MemorySearchResult.Loop x
                            })
                        )
                    )

                    // Skip simple query for vectorize search
                    if query.Contains(" ") || query.Length > 3 || query |> Seq.exists (Char.IsAsciiLetterOrDigit >> not) then
                        let loopOptions = VectorSearchOptions<_>()
                        let results =
                            memoryService.VectorSearch(
                                query,
                                top = 8,
                                options = loopOptions,
                                distinguishBySource = true,
                                cancellationToken = source.Token
                            )
                        for result in results do
                            transact (fun _ -> shareStore.SearchResults.Add(result) |> ignore)
                with
                | :? TaskCanceledException -> ()
                | ex -> snackbar.ShowMessage(ex, logger)

                transact (fun _ -> isSearching.Value <- false)
            }


            hook.AddFirstAfterRenderTask(fun _ -> task {
                if shareStore.SearchResults.Value.IsEmpty && String.IsNullOrEmpty shareStore.SearchQuery.Value then
                    isSearching.Publish true
                    do! search ""
                    isSearching.Publish false
            })


            MudDialog'' {
                Header "Search loops or documents" onClose
                DialogContent(
                    div {
                        style {
                            height 720
                            maxHeight "calc(100vh - 200px)"
                            displayFlex
                            flexDirectionColumn
                        }
                        div {
                            style { marginBottom 20 }
                            MudFocusTrap'' {
                                adapt {
                                    let! isSearching = isSearching
                                    let! query, setQuery = hook.ShareStore.SearchQuery.WithSetter()
                                    MudTextField'' {
                                        FullWidth
                                        Adornment Adornment.End
                                        AdornmentIcon Icons.Material.Filled.Search
                                        Variant Variant.Outlined
                                        Margin Margin.Dense
                                        Placeholder "Search loops or memories"
                                        AutoFocus
                                        Value query
                                        ReadOnly isSearching
                                        ValueChanged(fun x ->
                                            setQuery x
                                            search x |> ignore
                                        )
                                    }
                                    if isSearching then
                                        MudProgressLinear'' {
                                            Indeterminate
                                            Color Color.Primary
                                        }
                                }
                            }
                        }
                        div {
                            style {
                                overflowYAuto
                                displayFlex
                                flexDirectionColumn
                                gap 16
                            }
                            adapt {
                                let! results = shareStore.SearchResults
                                for result in results do
                                    LoopSearcher.SearchResultCard(result, onClose = onClose)
                            }
                        }
                    }
                )
            }
        )

    static member private SearchResultCard(result: MemorySearchResultItem, ?onClose: unit -> unit) : NodeRenderFragment =
        html.inject (
            result,
            fun (hook: IComponentHook, shareStore: IShareStore, serviceProvider: IServiceProvider) ->
                let dbService = serviceProvider.GetRequiredService<IDbService>()
                let loopService = serviceProvider.GetRequiredService<ILoopService>()

                let loopStreamingIndicator loopId = adapt {
                    let! isStreaming = loopService.IsStreaming(loopId)
                    if isStreaming then
                        MudProgressLinear'' {
                            Indeterminate
                            Color Color.Primary
                        }
                }

                let loopContentCard (loopContent: LoopContent) =
                    html.inject (
                        $"loop-content-card-{loopContent.Id}",
                        fun () ->
                            let contentWrapper = LoopContentWrapper.FromLoopContent(loopContent)
                            fragment {
                                div {
                                    style {
                                        displayFlex
                                        alignItemsCenter
                                        gap 4
                                        flexWrapWrap
                                    }
                                    loopContent.Author
                                    MudText'' {
                                        style { opacity 0.6 }
                                        Typo Typo.body2
                                        loopContent.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss")
                                    }
                                    MudChip'' {
                                        Size Size.Small
                                        "Loop Content"
                                    }
                                }
                                loopStreamingIndicator loopContent.LoopId
                                LoopContentView.Create(contentWrapper, ignoreToolCall = true)
                            }
                    )

                let loopCard (loop': Loop) =
                    let loopView = fragment {
                        MudText'' {
                            style { textOverflowWithMaxLines 2 }
                            loop'.Description
                        }
                        MudText'' {
                            Typo Typo.body2
                            loop'.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss")
                        }
                    }
                    fragment {
                        region {
                            if String.IsNullOrEmpty loop'.Description then
                                adapt {
                                    let! loopContent =
                                        dbService.DbContext
                                            .Select<LoopContent>()
                                            .Where(fun (x: LoopContent) -> x.LoopId = loop'.Id)
                                            .OrderByDescending(fun (x: LoopContent) -> x.Id)
                                            .FirstAsync<LoopContent | null>()
                                        |> Task.map LoadingState.Loaded
                                        |> AVal.ofTask LoadingState.Loading
                                    match loopContent with
                                    | LoadingState.Loaded null -> loopView
                                    | LoadingState.Loaded loopContent when String.IsNullOrWhiteSpace loopContent.Content -> loopView
                                    | LoadingState.Loaded loopContent -> loopContentCard loopContent
                                    | _ -> MudProgressLinear'' {
                                        Indeterminate
                                        Color Color.Primary
                                      }
                                }
                            else
                                loopView
                        }
                        loopStreamingIndicator loop'.Id
                    }


                MudCard'' {
                    style { cursorPointer }
                    class' "border-hover-primary"
                    preventDefault "onclick" true
                    stopPropagation "onclick" true
                    onclick (fun _ -> task {
                        onClose |> Option.iter (fun f -> f ())

                        match result with
                        | { Result = MemorySearchResult.Loop loop } -> do! hook.ToggleLoop(loop.Id, false)

                        | { Result = MemorySearchResult.LoopContent loopContent }
                        | {
                              Result = MemorySearchResult.File { LoopContent = ValueSome loopContent }
                          } ->
                            do! hook.ToggleLoop(loopContent.LoopId, false)
                            transact (fun _ -> shareStore.LoopContentsFocusing.Add(loopContent.LoopId, loopContent.Id) |> ignore)

                        | _ -> ()
                    })
                    Outlined
                    MudCardContent'' {
                        style {
                            maxHeight "12rem"
                            overflowAuto
                            positionRelative
                        }
                        region {
                            match result with
                            | { Result = MemorySearchResult.Loop loop } -> loopCard loop
                            | { Result = MemorySearchResult.LoopContent loopContent } -> loopContentCard loopContent

                            | {
                                  Result = MemorySearchResult.File {
                                                                       FileName = fileName
                                                                       ChunkText = chunkText
                                                                       PageNumber = pageNumber
                                                                       LoopContent = ValueSome loopContent
                                                                   }
                              } ->
                                loopContentCard loopContent
                                region {
                                    match fileName, pageNumber with
                                    | PDF, ValueSome pageNumber -> MudLink'' {
                                        style { wordbreakBreakAll }
                                        stopPropagation "onclick" true
                                        Color Color.Info
                                        Underline Underline.Always
                                        Target "_blank"
                                        Href $"/api/memory/document/{fileName}#page={pageNumber}"
                                        fileName
                                        MudChip'' {
                                            Size Size.Small
                                            Color Color.Info
                                            "#page"
                                            pageNumber
                                        }
                                      }
                                    | _ -> MarkdownView.Create(chunkText)
                                }

                            | { Result = MemorySearchResult.File file } ->
                                MudLink'' {
                                    style { wordbreakBreakAll }
                                    stopPropagation "onclick" true
                                    download (
                                        match file.FileName with
                                        | PDF -> false
                                        | _ -> true
                                    )
                                    Color Color.Info
                                    Underline Underline.Always
                                    Target "_blank"
                                    Href $"/api/memory/document/{file.FileName}"
                                    file.FileName
                                }
                                MarkdownView.Create(file.ChunkText)
                        }
                        region {
                            if result.Score <> 0 then
                                div {
                                    style {
                                        positionAbsolute
                                        top 0
                                        right 0
                                    }
                                    MudChip'' {
                                        Size Size.Small
                                        sprintf "%.3f" result.Score
                                    }
                                }
                        }
                    }
                }
        )
