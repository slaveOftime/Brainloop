namespace Brainloop.Loop

open System
open System.Text.Json
open System.Threading.Tasks
open Microsoft.JSInterop
open Microsoft.Extensions.Logging
open Microsoft.AspNetCore.Components.Web
open FSharp.Data.Adaptive
open IcedTasks
open MudBlazor
open BlazorMonaco
open BlazorMonaco.Editor
open Fun.Result
open Fun.Blazor
open Brainloop.Db
open Brainloop.Share
open Brainloop.Function
open Brainloop.Agent


type LoopContentView =

    static member private ReasoningBubble = div {
        style {
            displayFlex
            alignItemsCenter
            gap 8
            marginBottom 4
        }
        class' "loading-shimmer pulse"
        MudIcon'' {
            Icon Icons.Material.Outlined.Lightbulb
            Color Color.Primary
        }
        "Reasoning..."
    }


    static member private ToolCallAutorRunToggle(agentId, functionName) =
        html.inject (fun (agentService: IAgentService, snackbar: ISnackbar, loggerFactory: ILoggerFactory) ->
            let logger = loggerFactory.CreateLogger("LoopContentView")
            adapt {
                let! refresher, setRefresher = cval(0).WithSetter()
                let! isAllowed =
                    agentService.IsFunctionInWhitelist(agentId, functionName)
                    |> ValueTask.map LoadingState.Loaded
                    |> ValueTask.toTask
                    |> AVal.ofTask LoadingState.Loading
                let isChecked =
                    match isAllowed with
                    | LoadingState.Loaded x -> x
                    | _ -> false
                MudCheckBox'' {
                    Label "auto run in the next time"
                    Color(if isChecked then Color.Warning else Color.Default)
                    Disabled isAllowed.IsLoadingNow
                    Value isChecked
                    ValueChanged(fun isAllowed -> task {
                        try
                            match isAllowed with
                            | true -> do! agentService.AddFunctionIntoWhitelist(agentId, functionName)
                            | false -> do! agentService.RemoveFunctionFromWhitelist(agentId, functionName)
                        with ex ->
                            snackbar.ShowMessage(ex, logger)
                        setRefresher (refresher + 1)
                    })
                }
            }
        )


    static member private CreateTaskForAgent(contentWrapper: LoopContentWrapper, toolCall: LoopContentToolCall) =
        html.inject (
            toolCall,
            fun (agentService: IAgentService, loopService: ILoopService, shareStore: IShareStore, snackbar: ISnackbar) ->
                let args =
                    try
                        match SystemFunctions.SystemCreateTaskForAgentFunc.GetArgs(toolCall) with
                        | ValueSome x -> Ok x
                        | _ -> Error "Failed parse arguments for creating task for agent"
                    with ex ->
                        Error $"Failed parse arguments for creating task for agent: {ex.Message}"
                match args with
                | Ok args -> adapt {
                    let mutable inputRef: StandaloneCodeEditor | null = null
                    let! isDarkMode = shareStore.IsDarkMode
                    let! agent = agentService.TryGetAgentWithCache(args.AgentId) |> AVal.ofValueTask ValueNone
                    let! prompt, setPrompt = cval(args.Prompt).WithSetter()
                    let! isSending, setIsSending = cval(false).WithSetter()
                    MudField'' {
                        Variant Variant.Outlined
                        Label(
                            match agent with
                            | ValueNone -> $"Call agent #{args.AgentId} for the task"
                            | ValueSome agent -> $"Call agent \"{agent.Name}\""
                        )
                        div {
                            class' "create-task-for-agent-editor"
                            ErrorBoundary'' {
                                ErrorContent(fun error -> MudAlert'' {
                                    Severity Severity.Error
                                    error.ToString()
                                })
                                StandaloneCodeEditor'' {
                                    ConstructionOptions(fun _ ->
                                        StandaloneEditorConstructionOptions(
                                            Value = prompt,
                                            FontSize = 16,
                                            Language = "markdown",
                                            AutomaticLayout = true,
                                            GlyphMargin = false,
                                            Folding = false,
                                            LineDecorationsWidth = 0,
                                            LineNumbers = "off",
                                            WordWrap = "on",
                                            ReadOnly = isSending,
                                            Minimap = EditorMinimapOptions(Enabled = false),
                                            AcceptSuggestionOnEnter = "on",
                                            FixedOverflowWidgets = true,
                                            Theme = if isDarkMode then "vs-dark" else "vs-light"
                                        )
                                    )
                                    OnDidBlurEditorText(fun _ -> task {
                                        match inputRef with
                                        | null -> ()
                                        | inputRef ->
                                            let! value = inputRef.GetValue()
                                            setPrompt value
                                    })
                                    ref (fun x -> inputRef <- x)
                                }
                            }
                        }
                        styleElt { ruleset ".create-task-for-agent-editor .monaco-editor-container" { height "120px" } }
                    }
                    div {
                        style {
                            displayFlex
                            alignItemsCenter
                            justifyContentFlexEnd
                        }
                        MudButton'' {
                            Variant Variant.Filled
                            Color Color.Primary
                            Disabled isSending
                            OnClick(fun _ -> task {
                                setIsSending true
                                try
                                    let! agents = agentService.GetAgentsWithCache()
                                    match agents |> Seq.tryFind (fun x -> x.Id = args.AgentId) with
                                    | None -> snackbar.ShowMessage($"Agent #{args.AgentId} not found", severity = Severity.Error)
                                    | Some agent ->
                                        do!
                                            loopService.Send(
                                                contentWrapper.LoopId,
                                                prompt,
                                                agentId = args.AgentId,
                                                sourceLoopContentId = contentWrapper.Id,
                                                ignoreInput = true,
                                                author = agent.Name,
                                                role = LoopContentAuthorRole.Agent
                                            )
                                with _ ->
                                    snackbar.ShowMessage("Failed to send prompt to agent", severity = Severity.Error)
                                setIsSending false
                            })
                            "Execute"
                        }
                    }
                  }
                | Error errorMessage -> MudAlert'' {
                    Severity Severity.Error
                    errorMessage
                  }
        )

    static member private ToolCall(contentWrapper: LoopContentWrapper, toolCall: LoopContentToolCall, index: int) =
        html.inject (
            toolCall,
            fun (dbService: IDbService, JS: IJSRuntime, dialogService: IDialogService) ->
                let isExpanded =
                    cval (
                        toolCall.UserAction.IsSome
                        || SystemFunction.isRenderInIframe toolCall.FunctionName
                        || SystemFunction.isCreateTaskForAgent toolCall.FunctionName
                    )

                let titleView = div {
                    style {
                        displayFlex
                        alignItemsCenter
                        gap 4
                    }
                    MudIcon'' {
                        Size Size.Small
                        Color Color.Secondary
                        Icon Icons.Material.Filled.Construction
                    }
                    MudTooltip'' {
                        Arrow
                        Placement Placement.Top
                        TooltipContent(
                            div {
                                style { maxWidth 300 }
                                toolCall.Description
                            }
                        )
                        toolCall.FunctionName
                    }
                    if toolCall.DurationMs > 0 then
                        let secs = Math.Round(TimeSpan.FromMilliseconds(toolCall.DurationMs).TotalSeconds, 1)
                        MudChip'' {
                            Size Size.Small
                            if secs = 0. then
                                toolCall.DurationMs
                                "ms"
                            else
                                secs
                                "s"
                        }
                    if toolCall.FunctionName.StartsWith("agents") then
                        MudIconButton'' {
                            Size Size.Small
                            Icon Icons.Material.Outlined.Link
                            OnClick(fun _ -> task {
                                let! followingContentId =
                                    dbService.DbContext
                                        .Select<LoopContent>()
                                        .Where(fun (x: LoopContent) -> x.SourceLoopContentId = contentWrapper.Id)
                                        .FirstAsync(fun (x: LoopContent) -> x.Id)
                                do!
                                    JS.ScrollToElementTop(
                                        Strings.GetLoopContentsContainerDomId(contentWrapper.LoopId),
                                        Strings.GetLoopContentContainerDomId(followingContentId),
                                        smooth = true
                                    )
                            })
                        }
                }

                let argumentsView = region {
                    if toolCall.Arguments.Count > 0 then
                        let codeId = $"{Strings.ToolCallPrefix}args-{contentWrapper.Id}-{index}"
                        p {
                            style { marginBottom "0.5rem" }
                            "arguments:"
                        }
                        for index, KeyValue(argKey, argValue) in Seq.indexed toolCall.Arguments do
                            let codeId = $"{codeId}-{argKey}"
                            adaptiview (key = codeId) {
                                let! isExpanded, setIsExpanded = cval((index = 0)).WithSetter()
                                MudExpansionPanel'' {
                                    Dense
                                    Expanded isExpanded
                                    ExpandedChanged setIsExpanded
                                    TitleContent(
                                        div {
                                            style {
                                                displayFlex
                                                alignItemsCenter
                                                gap 12
                                                paddingRight 12
                                            }
                                            argKey
                                            MudSpacer''
                                            MudIconButton'' {
                                                key "copy"
                                                Size Size.Small
                                                Icon Icons.Material.Outlined.ContentCopy
                                                OnClick(fun _ -> task { do! JS.CopyInnerText(codeId) })
                                            }
                                        }
                                    )
                                    if isExpanded then
                                        let contentString = JsonSerializer.Prettier(argValue)
                                        pre {
                                            code {
                                                id codeId
                                                class' (
                                                    if SystemFunction.isRenderInIframe toolCall.FunctionName && argKey = "html" then
                                                        "language-html"
                                                    else
                                                        "language-json"
                                                )
                                                match contentString with
                                                | null -> "null"
                                                | x -> x
                                            }
                                            script {
                                                key toolCall.Arguments
                                                $"Prism.highlightElement(document.getElementById('{codeId}'))"
                                            }
                                        }
                                }
                            }
                }

                let userActionView = div {
                    style {
                        displayFlex
                        alignItemsCenter
                        justifyContentSpaceBetween
                        gap 24
                        flexWrapWrap
                    }
                    LoopContentView.ToolCallAutorRunToggle(toolCall.AgentId, toolCall.FunctionName)
                    match toolCall.UserAction with
                    | ValueNone -> ()
                    | ValueSome userAction -> adapt {
                        let! userAction, setuserAction = userAction.WithSetter()
                        if userAction = ToolCallUserAction.Accepted then
                            MudProgressCircular'' {
                                Size Size.Small
                                Color Color.Primary
                                Indeterminate
                            }
                        else
                            MudButtonGroup'' {
                                OverrideStyles false
                                Variant Variant.Outlined
                                Size Size.Small
                                MudIconButton'' {
                                    Size Size.Small
                                    Variant Variant.Outlined
                                    Icon Icons.Material.Filled.Stop
                                    OnClick(fun _ -> setuserAction ToolCallUserAction.Declined)
                                }
                                MudIconButton'' {
                                    class' "pulse"
                                    Size Size.Small
                                    Color Color.Warning
                                    Variant Variant.Filled
                                    Icon Icons.Material.Filled.PlayArrow
                                    OnClick(fun _ -> setuserAction ToolCallUserAction.Accepted)
                                }
                            }
                      }
                }

                let resultView = region {
                    match toolCall.Result with
                    | ValueNone -> ()
                    | ValueSome result ->
                        let codeId = $"{Strings.ToolCallPrefix}result-{contentWrapper.Id}-{index}"
                        if SystemFunction.isRenderInIframe toolCall.FunctionName then
                            let htmlDoc = string result
                            div {
                                style { positionRelative }
                                iframe {
                                    id codeId
                                    style { width "100%" }
                                    onload $"window.resizeIframe('{codeId}')"
                                    srcdoc htmlDoc
                                }
                                MudIconButton'' {
                                    style {
                                        positionAbsolute
                                        top 12
                                        right 6
                                    }
                                    Size Size.Small
                                    Icon Icons.Material.Outlined.Fullscreen
                                    OnClick(fun _ -> dialogService.PreviewHtml(htmlDoc))
                                }
                            }
                        else
                            p {
                                style { marginBottom 0 }
                                "result:"
                            }
                            div {
                                style {
                                    marginBottom 12
                                    positionRelative
                                }
                                adaptiview (key = codeId) {
                                    let! jsonContent =
                                        Task.Run<string | null>(fun _ -> JsonSerializer.Prettier(result))
                                        |> Task.map LoadingState.Loaded
                                        |> AVal.ofTask LoadingState.Loading
                                    match jsonContent with
                                    | LoadingState.Loaded jsonContent ->
                                        pre {
                                            code {
                                                id codeId
                                                class' "language-json"
                                                jsonContent
                                            }
                                            script {
                                                key result
                                                $"Prism.highlightElement(document.getElementById('{codeId}'))"
                                            }
                                        }
                                        MudIconButton'' {
                                            key "copy"
                                            style {
                                                positionAbsolute
                                                top 12
                                                right 6
                                                zIndex 1
                                            }
                                            Size Size.Small
                                            Variant Variant.Filled
                                            Icon Icons.Material.Outlined.ContentCopy
                                            OnClick(fun _ -> task { do! JS.CopyInnerText(codeId) })
                                        }
                                    | _ -> MudProgressLinear'' {
                                        Indeterminate
                                        Color Color.Primary
                                      }
                                }
                            }
                }

                if SystemFunction.isCreateTaskForAgent toolCall.FunctionName then
                    LoopContentView.CreateTaskForAgent(contentWrapper, toolCall)
                else
                    adapt {
                        let! isExpanded, setIsExpanded = isExpanded.WithSetter()
                        MudExpansionPanel'' {
                            Dense
                            Expanded isExpanded
                            ExpandedChanged setIsExpanded
                            TitleContent titleView
                            if isExpanded then
                                MudText'' {
                                    Typo Typo.body2
                                    toolCall.Description
                                }
                                argumentsView
                                userActionView
                                resultView
                        }
                    }
        )

    static member private Text(index, contentWrapper: LoopContentWrapper, loopContentText: LoopContentText, ?ignoreReason: bool) =
        let isLastOne = index = contentWrapper.Items.Count - 1
        html.inject (
            struct (loopContentText, isLastOne),
            fun () ->
                let ignoreReason = defaultArg ignoreReason false

                let markdownView (isStreaming) =
                    let blocks = loopContentText.Blocks
                    div {
                        for index, block in Seq.indexed blocks do
                            div {
                                match block with
                                // If think content is not the last content then put it into MudExpansionPanel
                                | LoopContentTextBlock.Think thinkMarkdown when not isLastOne || not isStreaming || index < blocks.Length - 1 ->
                                    if not ignoreReason then
                                        MudExpansionPanel'' {
                                            Dense
                                            Expanded(isLastOne && index = blocks.Length - 1)
                                            TitleContent(
                                                div {
                                                    style {
                                                        displayFlex
                                                        alignItemsCenter
                                                        gap 4
                                                    }
                                                    MudIcon'' {
                                                        Size Size.Small
                                                        Icon Icons.Material.Outlined.Lightbulb
                                                    }
                                                    "Reason content"
                                                    adapt {
                                                        match! contentWrapper.ThinkDurationMs with
                                                        | x when x > 0 -> MudChip'' {
                                                            Size Size.Small
                                                            Math.Round(TimeSpan.FromMilliseconds(x).TotalSeconds, 1)
                                                            "s"
                                                          }
                                                        | _ -> ()
                                                    }
                                                }
                                            )
                                            MarkdownView.Create(thinkMarkdown)
                                        }
                                | LoopContentTextBlock.Think markdown ->
                                    LoopContentView.ReasoningBubble
                                    MarkdownView.Create(markdown)
                                | LoopContentTextBlock.Content markdown -> MarkdownView.Create(markdown)
                            }
                    }

                if isLastOne then
                    adapt {
                        let! streammingCount = contentWrapper.StreammingCount
                        let isStreaming = streammingCount > -1
                        div {
                            class' (if isStreaming then "loop-content-text-streaming" else "")
                            markdownView isStreaming
                            region {
                                if isStreaming && String.IsNullOrEmpty loopContentText.Text then
                                    MudProgressLinear'' {
                                        Indeterminate
                                        Color Color.Primary
                                        style { width 200 }
                                    }
                            }
                        }
                    }
                else
                    markdownView true
        )

    static member private File(loopContentFile: LoopContentFile) = div {
        style { marginRight -6 }
        region {
            match loopContentFile.Name with
            | IMAGE -> MudImage'' {
                Fluid
                Src $"/api/memory/document/{loopContentFile.Name}"
              }
            | AUDIO -> audio {
                controls
                source {
                    type' $"audio/{loopContentFile.Extension}"
                    src $"/api/memory/document/{loopContentFile.Name}"
                }
              }
            | VIDEO -> video {
                controls
                style { width "100%" }
                source {
                    type' $"video/{loopContentFile.Extension}"
                    src $"/api/memory/document/{loopContentFile.Name}"
                }
              }
            | _ -> ()
        }
        div {
            style {
                displayFlex
                alignItemsBaseline
                gap 8
            }
            MudLink'' {
                style { wordbreakBreakAll }
                stopPropagation "onclick" true
                download (
                    match loopContentFile.Name with
                    | PDF -> false
                    | _ -> true
                )
                Typo Typo.body2
                Underline Underline.Always
                Target "_blank"
                Href $"/api/memory/document/{loopContentFile.Name}"
                loopContentFile.Name
            }
            MudChip'' {
                Size Size.Small
                string loopContentFile.Size
                " bytes"
            }
        }
    }

    static member private Excalidraw(contentWrapper: LoopContentWrapper, excalidraw: LoopContentExcalidraw) =
        html.inject (
            excalidraw,
            fun (hook: IComponentHook, shareStore: IShareStore) -> adapt {
                let! isDarkMode = shareStore.IsDarkMode
                MudImage'' {
                    class' "disable-zoom"
                    style { cursorPointer }
                    Fluid
                    Src(
                        match isDarkMode, excalidraw.DarkImageFileName with
                        | true, Some imageFileName -> $"/api/memory/document/{imageFileName}"
                        | _ -> $"/api/memory/document/{excalidraw.ImageFileName}"
                    )
                    onclick (fun _ ->
                        hook.ShowDialog(
                            DialogOptions(FullScreen = true, FullWidth = true, CloseOnEscapeKey = false),
                            fun ctx ->
                                LoopContentEditor.ExcalidrawDialog(
                                    contentWrapper.LoopId,
                                    contentWrapper = contentWrapper,
                                    excalidraw = excalidraw,
                                    onClose = ctx.Close
                                )
                        )
                    )
                }
            }
        )


    static member Create
        (contentWrapper: LoopContentWrapper, ?showRaw: bool aval, ?userScrolledEvent: IEvent<bool>, ?ignoreToolCall: bool, ?ignoreReason: bool)
        =
        html.inject (
            contentWrapper,
            fun (hook: IComponentHook, JS: IJSRuntime) ->
                let ignoreToolCall = defaultArg ignoreToolCall false

                let mutable isUserScrolled = false

                match userScrolledEvent with
                | None -> ()
                | Some evt ->
                    let onUserScrolled = Handler<bool>(fun _ _ -> isUserScrolled <- true)
                    evt.AddHandler(onUserScrolled)
                    hook.OnDispose.Add(fun () -> evt.RemoveHandler(onUserScrolled))

                contentWrapper.Items
                |> AList.toAVal
                |> AVal.addLazyCallback (fun x -> if Seq.isEmpty x then isUserScrolled <- false)
                |> hook.AddDispose

                contentWrapper.StreammingCount
                |> AVal.addInstantCallback (fun count ->
                    let isLastItemToolCall () =
                        match contentWrapper.Items.Value |> Seq.tryLast with
                        | Some(LoopContentItem.ToolCall _) -> true
                        | _ -> false
                    if count > 0 && (not isUserScrolled || isLastItemToolCall ()) then
                        JS.ScrollToElementBottom(
                            Strings.GetLoopContentsContainerDomId(contentWrapper.LoopId),
                            Strings.GetLoopContentContainerDomId(contentWrapper.Id),
                            smooth = true
                        )
                        |> ignore
                )
                |> hook.AddDispose


                let contentRichView = adaptiview (key = "content-rich-container") {
                    let! items = contentWrapper.Items
                    for index, item in Seq.indexed items do
                        match item with
                        | LoopContentItem.Text x -> LoopContentView.Text(index, contentWrapper, x, ?ignoreReason = ignoreReason)
                        | LoopContentItem.ToolCall x -> if not ignoreToolCall then LoopContentView.ToolCall(contentWrapper, x, index)
                        | LoopContentItem.File x -> LoopContentView.File(x)
                        | LoopContentItem.Excalidraw x -> LoopContentView.Excalidraw(contentWrapper, x)
                }

                let contentRawView = pre {
                    key "content-raw-container"
                    style {
                        overflowAuto
                        whiteSpacePreLine
                    }
                    adapt {
                        let! _ = contentWrapper.StreammingCount
                        let! items = contentWrapper.Items
                        for item in items do
                            match item with
                            | LoopContentItem.Text x -> html.text x.Text
                            | LoopContentItem.File x -> LoopContentView.File(x)
                            | LoopContentItem.Excalidraw x -> LoopContentView.Excalidraw(contentWrapper, x)
                            | LoopContentItem.ToolCall x ->
                                if not ignoreToolCall then
                                    let codeId = $"code-{x.GetHashCode()}"
                                    pre {
                                        code {
                                            id codeId
                                            class' "language-json"
                                            JsonSerializer.Serialize(x, JsonSerializerOptions.createDefault ())
                                        }
                                        script {
                                            key x
                                            $"Prism.highlightElement(document.getElementById('{codeId}'))"
                                        }
                                    }
                    }
                }

                adapt {
                    match showRaw with
                    | None -> contentRichView
                    | Some showRaw ->
                        match! showRaw with
                        | false -> contentRichView
                        | true -> contentRawView
                }
        )
