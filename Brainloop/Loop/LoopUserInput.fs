namespace Brainloop.Loop

open System
open System.IO
open System.Text.Json
open System.Collections.Generic
open Microsoft.Extensions.Logging
open Microsoft.Extensions.DependencyInjection
open Microsoft.JSInterop
open Microsoft.AspNetCore.Components.Forms
open FSharp.Data.Adaptive
open IcedTasks
open MudBlazor
open BlazorMonaco
open BlazorMonaco.Editor
open Fun.Result
open Fun.Blazor
open Brainloop.Db
open Brainloop.Memory
open Brainloop.Agent


type LoopUserInput =

    static member Create(loopId: int64) =
        html.inject (
            $"loop-user-input-{loopId}",
            fun (hook: IComponentHook, serviceProvider: IServiceProvider, JS: IJSRuntime, loggerFactory: ILoggerFactory) ->
                let logger = loggerFactory.CreateLogger("LoopUserInput")
                let shareStore = serviceProvider.GetRequiredService<IShareStore>()
                let memoryService = serviceProvider.GetRequiredService<IMemoryService>()
                let documentService = serviceProvider.GetRequiredService<IDocumentService>()
                let snackbar = serviceProvider.GetRequiredService<ISnackbar>()
                let loopService = serviceProvider.GetRequiredService<ILoopService>()
                let loopContentService = serviceProvider.GetRequiredService<ILoopContentService>()

                let mutable inputRef: StandaloneCodeEditor | null = null
                let mutable fileUploadRef: MudFileUpload<IReadOnlyList<IBrowserFile>> | null = null

                let inputId = $"loop-input-{Random.Shared.Next()}"
                let inputVisible = cval false
                let inputFocused = cval false
                let isInProgress = cval false
                let isDragging = cval false
                let hasInput = cval false
                let selectedAgent = cval ValueOption<Agent>.ValueNone
                let selectedModel = cval ValueOption<Model>.ValueNone

                let send () = task {
                    isInProgress.Publish true

                    valueTask {
                        try
                            let! message =
                                match inputRef with
                                | null -> Task.retn ""
                                | ref -> ref.GetValue()
                            do!
                                loopService.Send(
                                    loopId,
                                    message,
                                    ?agentId = (selectedAgent.Value |> ValueOption.map _.Id |> ValueOption.toOption),
                                    ?modelId = (selectedModel.Value |> ValueOption.map _.Id |> ValueOption.toOption)
                                )
                        with ex ->
                            snackbar.ShowMessage(ex, logger)
                    }
                    |> ignore

                    transact (fun _ -> hasInput.Value <- false)

                    match inputRef with
                    | null -> ()
                    | inputRef ->
                        do! inputRef.SetValue("")
                        do! Async.Sleep 200
                        do! JS.ScrollToBottom(LoopUtils.GetLoopContentsContainerDomId(loopId), smooth = true)

                    isInProgress.Publish false
                }

                let uploadStream name (stream: Stream) = valueTask {
                    isInProgress.Publish true
                    try
                        let! loopContentId = loopContentService.UpsertLoopContent(LoopContentWrapper.Default(loopId))
                        let! fileName = documentService.SaveFile(name, stream, loopContentId = loopContentId)
                        let! _ =
                            loopContentService.AddContentToCacheAndUpsert(
                                {
                                    LoopContentWrapper.Default(loopId) with
                                        Id = loopContentId
                                        Items = clist [ LoopContentItem.File { Name = fileName; Size = stream.Length } ]
                                }
                            )

                        valueTask {
                            try
                                do! memoryService.VectorizeFile(fileName, loopContentId)
                            with ex ->
                                snackbar.ShowMessage(ex, logger)
                        }
                        |> ignore

                        do! Async.Sleep 1000
                        do! JS.ScrollToBottom($"loop-{loopId}-contents-container", smooth = true)

                    with ex ->
                        snackbar.ShowMessage(ex, logger)
                    isInProgress.Publish false
                }

                let uploadFiles (e: InputFileChangeEventArgs) = task {
                    isDragging.Publish false
                    let files = e.GetMultipleFiles()
                    for file in files do
                        let stream = file.OpenReadStream(maxAllowedSize = 1024L * 1024L * 100L)
                        do! uploadStream file.Name stream
                }

                let uploadPasteImage () = valueTask {
                    try
                        let! json = JS.GetImageFromClipboard()
                        let data = JsonSerializer.Deserialize<{| ``type``: string; data: string |}>(json)
                        let base64Str = data.data.Split(",")[1]
                        use memoryStream = new MemoryStream(Convert.FromBase64String(base64Str))
                        do! uploadStream $"paste.{data.``type``}" memoryStream
                    with ex ->
                        logger.LogDebug(ex, "Error uploading paste image")
                }

                let registerActionsForEditor () = task {
                    match inputRef with
                    | null -> ()
                    | inputRef ->
                        do!
                            inputRef.AddAction(
                                ActionDescriptor(
                                    Id = "send.to.llm",
                                    Label = "Send to LLM",
                                    Keybindings = [| int KeyMod.CtrlCmd ||| int KeyCode.Enter |],
                                    Run = (fun _ -> if hasInput.Value then send () |> ignore)
                                )
                            )
                }


                hook.RegisterAutoCompleteForAddAgents((fun _ -> inputRef), (fun x -> valueTask { selectedAgent.Publish(Option.toValueOption x) }))

                hook.AddFirstAfterRenderTask(fun _ -> task {

                    selectedAgent.AddLazyCallback(fun agent ->
                        if agent.IsSome then
                            match inputRef with
                            | null -> ()
                            | ref -> ref.Focus() |> ignore
                    )
                    |> hook.AddDispose

                    do! Async.Sleep 200
                    inputVisible.Publish true
                })


                let fileUploader = MudIconButton'' {
                    Variant Variant.Text
                    Icon Icons.Material.Filled.UploadFile
                    OnClick(fun _ -> task {
                        match fileUploadRef with
                        | null -> ()
                        | ref -> do! ref.OpenFilePickerAsync()
                    })
                }

                let userInput = adapt {
                    let! isDarkMode = shareStore.IsDarkMode
                    let! isSending = isInProgress
                    div {
                        class' "loop-user-input"
                        StandaloneCodeEditor'' {
                            Id inputId
                            ConstructionOptions(fun _ ->
                                StandaloneEditorConstructionOptions(
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
                            OnDidInit(ignore >> registerActionsForEditor)
                            OnDidChangeModelContent(fun _ -> hasInput.Publish true)
                            OnDidPaste(fun _ -> task { do! uploadPasteImage () })
                            OnDidFocusEditorWidget(fun _ -> inputFocused.Publish true)
                            OnDidBlurEditorWidget(fun _ -> inputFocused.Publish false)
                            ref (fun x -> inputRef <- x)
                        }
                    }
                    adapt {
                        let! inputFocused = inputFocused
                        styleElt { ruleset $"#{inputId}" { height (if inputFocused then "160px" else "60px") } }
                    }
                }

                let userActions = div {
                    style {
                        displayFlex
                        alignItemsCenter
                        gap 4
                        margin 0 -16 -12 -14
                    }
                    AgentSelector.Create(selectedAgent, selectedModel)
                    div {
                        MudFileUpload'' {
                            ondragenter (fun _ -> isDragging.Publish true)
                            ondragleave (fun _ -> isDragging.Publish false)
                            ondrop (fun _ -> isDragging.Publish false)
                            ondragend (fun _ -> isDragging.Publish false)
                            OnFilesChanged uploadFiles
                            ref (fun x -> fileUploadRef <- x)
                        }
                        fileUploader
                    }
                    adapt {
                        let! isExcalidrawReady = JS.IsExcalidrawReady() |> AVal.ofValueTask false
                        MudIconButton'' {
                            Icon Icons.Material.Filled.Draw
                            Disabled(not isExcalidrawReady)
                            OnClick(fun _ -> task {
                                hook.ShowDialog(
                                    DialogOptions(FullScreen = true, FullWidth = true, CloseOnEscapeKey = false),
                                    fun ctx -> LoopContentEditor.ExcalidrawDialog(loopId, onClose = ctx.Close)
                                )
                            })
                        }
                    }
                    adapt {
                        match! isInProgress with
                        | true -> MudProgressCircular'' {
                            Size Size.Small
                            Color Color.Primary
                            Indeterminate
                          }
                        | _ -> ()
                    }
                    MudSpacer''
                    adapt {
                        let! hasInput = hasInput
                        let! selectedAgent = selectedAgent
                        let! isSending = isInProgress
                        let isDisabled = isSending || (not hasInput && selectedAgent.IsNone)
                        MudIconButton'' {
                            Icon Icons.Material.Filled.Send
                            Disabled isDisabled
                            OnClick(ignore >> send)
                            Color Color.Primary
                            Variant Variant.Text
                        }
                    }
                }

                adapt {
                    match! inputVisible with
                    | false -> ()
                    | true -> MudField'' {
                        Variant Variant.Outlined
                        Margin Margin.Dense
                        style {
                            displayFlex
                            flexDirectionColumn
                            gap 4
                            padding 8
                        }
                        userInput
                        userActions
                      }
                }
        )
