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


type LoopToolCallView =

    static member private AutorRunToggle(agentId, functionName) =
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
                                                includeHistory = false,
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


    static member private ArgumentsView(contentWrapper: LoopContentWrapper, toolCall: LoopContentToolCall, index: int) =
        html.inject (fun (JS: IJSRuntime) -> region {
            if toolCall.Arguments.Count > 0 then
                let codeId = $"{Strings.ToolCallPrefix}args-{contentWrapper.Id}-{index}"
                p {
                    style {
                        marginTop "1rem"
                        marginBottom "0.5rem"
                    }
                    "arguments:"
                }
                for index, KeyValue(argKey, argValue) in Seq.indexed toolCall.Arguments do
                    let codeId = $"{codeId}-{argKey}"
                    adaptiview (key = codeId) {
                        let! isExpanded, setIsExpanded = cval((index = 0)).WithSetter()
                        let! isReadonly =
                            toolCall.UserAction
                            |> ValueOption.map (
                                AVal.map (
                                    function
                                    | ToolCallUserAction.Pending -> false
                                    | _ -> true
                                )
                            )
                            |> ValueOption.defaultValue (AVal.constant true)
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
                                match argValue with
                                | :? string as argValue when argKey <> "html" -> MudTextField'' {
                                    Value argValue
                                    ValueChanged(fun x -> toolCall.Arguments[argKey] <- x)
                                    FullWidth
                                    Margin Margin.Dense
                                    Variant Variant.Outlined
                                    ReadOnly isReadonly
                                    Lines 1
                                    MaxLines 10
                                    AutoGrow
                                  }
                                | :? bool as argValue -> MudSwitch'' {
                                    Value argValue
                                    ValueChanged(fun x -> toolCall.Arguments[argKey] <- x)
                                    ReadOnly isReadonly
                                  }
                                | :? int as argValue -> MudNumericField'' {
                                    Value argValue
                                    ValueChanged(fun x -> toolCall.Arguments[argKey] <- x)
                                    FullWidth
                                    Margin Margin.Dense
                                    Variant Variant.Outlined
                                    ReadOnly isReadonly
                                  }
                                | :? float as argValue -> MudNumericField'' {
                                    Value argValue
                                    ValueChanged(fun x -> toolCall.Arguments[argKey] <- x)
                                    FullWidth
                                    Margin Margin.Dense
                                    Variant Variant.Outlined
                                    ReadOnly isReadonly
                                  }
                                | _ -> pre {
                                    code {
                                        id codeId
                                        class' (
                                            if SystemFunction.isRenderInIframe toolCall.FunctionName && argKey = "html" then
                                                "language-html"
                                            else
                                                "language-json"
                                        )
                                        match JsonSerializer.Prettier(argValue) with
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
        })

    static member private ResultView(contentWrapper: LoopContentWrapper, toolCall: LoopContentToolCall, index: int) =
        html.inject (fun (JS: IJSRuntime, dialogService: IDialogService) -> region {
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
        })

    static member Create(contentWrapper: LoopContentWrapper, toolCall: LoopContentToolCall, index: int) =
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

                let userActionView = div {
                    style {
                        displayFlex
                        alignItemsCenter
                        justifyContentSpaceBetween
                        gap 24
                        flexWrapWrap
                    }
                    LoopToolCallView.AutorRunToggle(toolCall.AgentId, toolCall.FunctionName)
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

                if SystemFunction.isCreateTaskForAgent toolCall.FunctionName then
                    LoopToolCallView.CreateTaskForAgent(contentWrapper, toolCall)
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
                                LoopToolCallView.ArgumentsView(contentWrapper, toolCall, index)
                                userActionView
                                LoopToolCallView.ResultView(contentWrapper, toolCall, index)
                        }
                    }
        )
