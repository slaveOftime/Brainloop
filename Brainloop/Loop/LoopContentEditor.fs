namespace Brainloop.Loop

open System
open System.IO
open System.Text.Json
open Microsoft.JSInterop
open Microsoft.Extensions.Logging
open Microsoft.Extensions.DependencyInjection
open Microsoft.AspNetCore.Components.Web
open FSharp.Data.Adaptive
open SixLabors.ImageSharp
open SixLabors.ImageSharp.Processing
open IcedTasks
open MudBlazor
open Fun.Blazor
open BlazorMonaco
open BlazorMonaco.Editor
open Brainloop.Share
open Brainloop.Memory


type LoopContentEditor =

    static member Create(contentWrapper: LoopContentWrapper, isEditing: bool cval) =
        html.inject (
            $"editor-{contentWrapper.Id}",
            fun (hook: IComponentHook, JS: IJSRuntime, dialog: IDialogService) ->
                let snackbar = hook.ServiceProvider.GetRequiredService<ISnackbar>()
                let dialogService = hook.ServiceProvider.GetRequiredService<IDialogService>()
                let loopContentService = hook.ServiceProvider.GetRequiredService<ILoopContentService>()
                let loggerFactory = hook.ServiceProvider.GetRequiredService<ILoggerFactory>()
                let logger = loggerFactory.CreateLogger("LoopContentView")

                let hasChanges = cval false
                let items = Collections.Generic.List contentWrapper.Items.Value
                let editorRefs = Collections.Generic.Dictionary<int, StandaloneCodeEditor>()

                let textItemsCount =
                    items
                    |> Seq.map (
                        function
                        | LoopContentItem.Text _ -> true
                        | _ -> false
                    )
                    |> Seq.length

                let cancelChanges () = task {
                    isEditing.Publish(false)
                    do! Async.Sleep 100
                    do!
                        JS.ScrollToElementBottom(
                            Strings.GetLoopContentsContainerDomId(contentWrapper.LoopId),
                            Strings.GetLoopContentContainerDomId(contentWrapper.Id),
                            smooth = true
                        )
                }

                let saveChanges () = task {
                    try
                        for index, item in items |> Seq.toArray |> Seq.indexed do
                            match item with
                            | LoopContentItem.Text _ ->
                                match editorRefs.TryGetValue(index) with
                                | true, editor ->
                                    let! txt = editor.GetValue()
                                    items[index] <- LoopContentItem.Text(LoopContentText txt)
                                | _ -> ()
                            | _ -> ()

                        let newContent = { contentWrapper with Items = clist items }

                        let postSave () = valueTask {
                            transact (fun _ ->
                                contentWrapper.Items.Clear()
                                contentWrapper.Items.AddRange(newContent.Items)
                                isEditing.Value <- false
                                hasChanges.Value <- false
                            )
                            do! Async.Sleep 100
                            do!
                                JS.ScrollToElementBottom(
                                    Strings.GetLoopContentsContainerDomId(contentWrapper.LoopId),
                                    Strings.GetLoopContentContainerDomId(contentWrapper.Id),
                                    smooth = true
                                )
                        }

                        if contentWrapper.IsSecret.Value then
                            dialogService.Show(
                                DialogOptions(MaxWidth = MaxWidth.ExtraSmall, FullWidth = true, CloseOnEscapeKey = false),
                                fun ctx -> LoopContentEditor.EncryptDialog(newContent, ctx.Close, onEncrypted = (postSave >> ignore))
                            )
                        else
                            do! loopContentService.UpsertLoopContent(newContent) |> ValueTask.map ignore
                            do! postSave ()

                    with ex ->
                        snackbar.ShowMessage(ex, logger)
                }


                let actions (onClose: unit -> unit) = region {
                    MudButton'' {
                        Size Size.Small
                        OnClick(fun _ -> task {
                            onClose ()
                            do! cancelChanges ()
                        })
                        "Cancel"
                    }
                    MudButton'' {
                        Size Size.Small
                        OnClick(fun _ -> task {
                            let! result = dialog.ShowMessageBox("Warning", "Are you sure to delete this content?")
                            if result.HasValue && result.Value then
                                try
                                    do! loopContentService.DeleteLoopContent(contentWrapper.LoopId, contentWrapper.Id)
                                with ex ->
                                    snackbar.ShowMessage(ex, logger)
                            onClose ()
                        })
                        "Delete"
                    }
                    adapt {
                        match! contentWrapper.IsSecret with
                        | true -> ()
                        | false ->
                            let! hasChanges = hasChanges
                            MudButton'' {
                                Size Size.Small
                                Color Color.Primary
                                Variant Variant.Outlined
                                Disabled(not hasChanges)
                                StartIcon Icons.Material.Filled.Lock
                                OnClick(fun _ -> task {
                                    onClose ()
                                    contentWrapper.IsSecret.Publish(true)
                                    do! saveChanges ()
                                })
                                "Save"
                            }
                    }
                    adapt {
                        let! hasChanges = hasChanges
                        MudButton'' {
                            Size Size.Small
                            Color Color.Primary
                            Variant Variant.Filled
                            Disabled(not hasChanges)
                            OnClick(fun _ -> task {
                                onClose ()
                                do! saveChanges ()
                            })
                            "Save"
                        }
                    }
                }

                let contentEditors = adapt {
                    let! isDarkMode = hook.ShareStore.IsDarkMode
                    for index, item in Seq.indexed items do
                        match item with
                        | LoopContentItem.Text text ->
                            let txt = text.Text
                            ErrorBoundary'' {
                                ErrorContent(fun error -> MudAlert'' {
                                    Severity Severity.Error
                                    error.ToString()
                                })
                                StandaloneCodeEditor'' {
                                    ConstructionOptions(fun _ ->
                                        StandaloneEditorConstructionOptions(
                                            Value = txt,
                                            FontSize = 16,
                                            Language = "markdown",
                                            AutomaticLayout = true,
                                            GlyphMargin = false,
                                            Folding = false,
                                            LineDecorationsWidth = 0,
                                            LineNumbers = "off",
                                            WordWrap = "on",
                                            Minimap = EditorMinimapOptions(Enabled = false),
                                            AcceptSuggestionOnEnter = "on",
                                            FixedOverflowWidgets = true,
                                            Theme = if isDarkMode then "vs-dark" else "vs-light"
                                        )
                                    )
                                    OnDidChangeModelContent(fun _ -> hasChanges.Publish true)
                                    ref (fun x -> editorRefs[index] <- x)
                                }
                            }
                        | LoopContentItem.ToolCall _ -> MudAlert'' {
                            Dense
                            Severity Severity.Warning
                            "Tool call is not support for editing for now"
                          }
                        | LoopContentItem.File _ -> MudAlert'' {
                            Dense
                            Severity Severity.Warning
                            "Uplaoded file is not support for editing"
                          }
                        | LoopContentItem.Excalidraw x -> MudImage'' {
                            class' "disable-zoom"
                            style { cursorPointer }
                            Fluid
                            Src $"{Strings.DocumentApi}{x.ImageFileName}"
                            onclick (fun _ ->
                                hook.ShowDialog(
                                    DialogOptions(FullScreen = true, FullWidth = true, CloseOnEscapeKey = false),
                                    fun ctx ->
                                        LoopContentEditor.ExcalidrawDialog(
                                            contentWrapper.LoopId,
                                            contentWrapper = contentWrapper,
                                            excalidraw = x,
                                            onClose = ctx.Close
                                        )
                                )
                            )
                          }
                        | LoopContentItem.Secret x -> LoopContentEditor.LockerView(contentWrapper, x)
                }

                div {
                    class' "loop-content-editor"
                    contentEditors
                    div {
                        style {
                            displayFlex
                            justifyContentFlexEnd
                            gap 8
                            padding 8 0
                        }
                        styleElt { ruleset ".loop-content-editor .monaco-editor-container" { height "200px" } }
                        MudIconButton'' {
                            Size Size.Small
                            Icon Icons.Material.Filled.Fullscreen
                            OnClick(fun _ ->
                                dialog.Show(
                                    DialogOptions(FullWidth = true, MaxWidth = MaxWidth.Medium),
                                    fun ctx -> MudDialog'' {
                                        Header "Edit Loop Content" ctx.Close
                                        DialogContent(
                                            div {
                                                style {
                                                    height "calc(100vh - 200px)"
                                                    overflowYAuto
                                                }
                                                styleElt {
                                                    ruleset ".monaco-editor-container" {
                                                        height (if textItemsCount = 1 then "100% !important" else "unset")
                                                    }
                                                }
                                                contentEditors
                                            }
                                        )
                                        DialogActions(actions ctx.Close)
                                    }
                                )
                            )
                        }
                        actions ignore
                    }
                }
        )


    static member ExcalidrawDialog
        (loopId: int64, ?contentWrapper: LoopContentWrapper, ?excalidraw: LoopContentExcalidraw, ?onClose: unit -> unit)
        : NodeRenderFragment =
        html.inject (fun (hook: IComponentHook, serviceProvider: IServiceProvider) ->
            let JS = serviceProvider.GetRequiredService<IJSRuntime>()
            let snackbar = serviceProvider.GetRequiredService<ISnackbar>()
            let dialog = serviceProvider.GetRequiredService<IDialogService>()
            let logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Excalidraw")
            let loopContentService = serviceProvider.GetRequiredService<ILoopContentService>()
            let memoryService = serviceProvider.GetRequiredService<IMemoryService>()
            let documentService = serviceProvider.GetRequiredService<IDocumentService>()
            let browserViewportService = serviceProvider.GetRequiredService<IBrowserViewportService>()

            let elementId =
                match contentWrapper with
                | None -> $"excalidraw-loop-{loopId}"
                | Some wrapper -> $"LC-{wrapper.Id}-excalidraw"

            let isChanged = cval true

            let processImage (image: byte[]) = valueTask {
                let previewStream = new MemoryStream(image)

                use! image = Image.LoadAsync(previewStream)

                let processedStream =
                    let maxWidth = 720
                    if image.Width > maxWidth then
                        let stream = new MemoryStream()
                        // Resize the image to a maximum width while maintaining aspect ratio
                        image.Mutate(fun context -> context.Resize(maxWidth, 0) |> ignore)
                        image.SaveAsPng(stream)
                        stream
                    else
                        previewStream
                processedStream.Seek(0, SeekOrigin.Begin) |> ignore

                return processedStream
            }

            let saveContent () = task {
                let! jsonData = JS.ExportExcalidrawToJson(elementId)
                let! previewImage = JS.ExportExcalidrawToPng(elementId, isDarkMode = false)
                let! previewImageDark = JS.ExportExcalidrawToPng(elementId, isDarkMode = true)

                use! processedStream = processImage previewImage
                use! processedStreamDark = processImage previewImageDark

                let! contentWrapper =
                    match contentWrapper with
                    | Some x -> ValueTask.singleton x
                    | None -> valueTask {
                        let contentWrapper = LoopContentWrapper.Default(loopId)
                        let! loopContentId = loopContentService.AddContentToCacheAndUpsert(contentWrapper)
                        return { contentWrapper with Id = loopContentId }
                      }

                let! fileName = documentService.SaveFile("exalidraw.png", processedStream, loopContentId = contentWrapper.Id)
                let! fileNameDark = documentService.SaveFile("exalidraw.png", processedStreamDark, loopContentId = contentWrapper.Id)

                let newContent =
                    LoopContentItem.Excalidraw {
                        ImageFileName = fileName
                        DarkImageFileName = Some fileNameDark
                        JsonData = jsonData
                    }

                transact (fun () ->
                    match excalidraw with
                    | None -> contentWrapper.Items.Add(newContent) |> ignore
                    | Some x ->
                        let index =
                            contentWrapper.Items
                            |> Seq.tryFindIndex (
                                function
                                | LoopContentItem.Excalidraw y -> x = y
                                | _ -> false
                            )
                            |> Option.defaultWith (fun _ -> Math.Max(0, contentWrapper.Items.Count - 1))
                        contentWrapper.Items.RemoveAt index |> ignore
                        contentWrapper.Items.InsertAt(index, newContent) |> ignore
                )
                do! loopContentService.UpsertLoopContent(contentWrapper, disableVectorize = true) |> ValueTask.map ignore

                valueTask {
                    try
                        match excalidraw with
                        | Some x -> do! memoryService.DeleteFile(x.ImageFileName)
                        | _ -> ()
                        do! memoryService.VectorizeFile(fileName, contentWrapper.Id)
                    with ex ->
                        snackbar.ShowMessage(ex, logger)
                }
                |> ignore

                do! JS.CloseExcalidraw(elementId)

                do! Async.Sleep 100

                do!
                    JS.ScrollToElementBottom(
                        Strings.GetLoopContentsContainerDomId(contentWrapper.LoopId),
                        Strings.GetLoopContentContainerDomId(contentWrapper.Id),
                        smooth = true
                    )
            }


            hook.AddAfterRenderTask(fun _ -> task {
                do! Async.Sleep 100

                let! windowSize = browserViewportService.GetCurrentBrowserWindowSizeAsync()

                let data =
                    match excalidraw with
                    | Some x -> x.JsonData
                    | _ ->
                        contentWrapper
                        |> Option.bind (fun x ->
                            x.Items.Value
                            |> Seq.tryPick (
                                function
                                | LoopContentItem.Excalidraw x -> Some x.JsonData
                                | _ -> None
                            )
                        )
                        |> Option.defaultValue "{}"

                do!
                    JS.OpenExcalidraw(
                        elementId,
                        windowSize.Height,
                        data,
                        hook.ShareStore.IsDarkMode.Value,
                        (fun _ -> task { isChanged.Publish true }),
                        (fun _ -> task {
                            if isChanged.Value then
                                let! isConfirmed =
                                    dialog.ShowConfirm("Save", "Do you want save the content?", "Yes", "No", severity = Severity.Success)
                                if isConfirmed then
                                    let! closeFn = dialog.ShowLoading(message = MudText'' { "Saving ..." })
                                    try
                                        do! saveContent ()
                                        onClose |> Option.iter (fun fn -> fn ())
                                    with ex ->
                                        snackbar.ShowMessage(ex, logger)
                                    closeFn ()
                                else
                                    onClose |> Option.iter (fun fn -> fn ())
                        })
                    )
            })


            div {
                style {
                    width "100%"
                    height "100%"
                    marginTop -32
                    overflowHidden
                }
                div { id elementId }
            }
        )


    // Will save to db after encrypted
    static member EncryptDialog(contentWrapper: LoopContentWrapper, onClose: unit -> unit, ?onEncrypted: unit -> unit) : NodeRenderFragment =
        html.inject (fun (loopContentService: ILoopContentService, dialogService: IDialogService) ->
            let pass = cval ""
            let confirmPass = cval ""

            let encrypt () = task {
                if not (String.IsNullOrWhiteSpace pass.Value) && pass.Value = confirmPass.Value then
                    try
                        let secret = AlwaysSecure.aesEncrypt pass.Value (toJson contentWrapper.Items.Value)
                        transact (fun _ ->
                            contentWrapper.Items.Clear()
                            contentWrapper.Items.Add(LoopContentItem.Secret secret) |> ignore
                            contentWrapper.IsSecret.Value <- true
                        )
                        let! _ = loopContentService.UpsertLoopContent(contentWrapper)
                        onClose ()
                        onEncrypted |> Option.iter (fun fn -> fn ())
                    with ex ->
                        dialogService.ShowMessage("Failed to lock the content", ex.Message, severity = Severity.Error)
            }

            MudDialog'' {
                Header "Lock the content" onClose
                DialogContent(
                    MudFocusTrap'' {
                        div {
                            style {
                                displayFlex
                                flexDirectionColumn
                                gap 12
                            }
                            adapt {
                                let! binding = pass.WithSetter()
                                MudTextField'' {
                                    Label "Password"
                                    Value' binding
                                    Variant Variant.Outlined
                                    InputType InputType.Password
                                    FullWidth
                                    AutoFocus
                                    Adornment Adornment.Start
                                    AdornmentIcon Icons.Material.Filled.Password
                                }
                            }
                            adapt {
                                let! binding = confirmPass.WithSetter()
                                MudTextField'' {
                                    Label "Confirm Password"
                                    Value' binding
                                    Variant Variant.Outlined
                                    InputType InputType.Password
                                    FullWidth
                                    Adornment Adornment.Start
                                    AdornmentIcon Icons.Material.Filled.Password
                                    OnKeyUp(fun e -> task { if e.Key = "Enter" then do! encrypt () })
                                }
                            }
                        }
                    }
                )
                DialogActions [|
                    adapt {
                        let! pass = pass
                        let! confirmPass = confirmPass
                        MudButton'' {
                            Color Color.Primary
                            Variant Variant.Filled
                            Disabled(String.IsNullOrWhiteSpace pass || pass <> confirmPass)
                            OnClick(ignore >> encrypt)
                            "Lock"
                        }
                    }
                |]
            }
        )


    static member DecryptDialog(contentWrapper: LoopContentWrapper, secret: string, onClose: unit -> unit) : NodeRenderFragment =
        html.inject (
            struct ("secret", secret),
            fun (dialogService: IDialogService) ->
                let pass = cval ""
                let turnOffSecret = cval false

                let decrypt () =
                    try
                        let content = AlwaysSecure.aesDecrypt pass.Value secret
                        let items = fromJson<LoopContentItem[]> content |> ValueOption.defaultValue [||]
                        transact (fun _ ->
                            contentWrapper.Items.Clear()
                            contentWrapper.Items.AddRange(items)
                            contentWrapper.IsSecret.Value <- not turnOffSecret.Value
                        )
                        onClose ()
                        if not turnOffSecret.Value then
                            valueTask {
                                do! Async.Sleep(60_000 * 2)
                                // After some times, if the content is not changed and the secret is still on, we should restore it to original value
                                if content = (contentWrapper.Items |> AList.force |> Seq.toList |> toJson) then
                                    transact (fun _ ->
                                        contentWrapper.Items.Clear()
                                        contentWrapper.Items.Add(LoopContentItem.Secret secret) |> ignore
                                    )
                            }
                            |> ignore
                    with ex ->
                        dialogService.ShowMessage("Failed to unlock the content", ex.Message, severity = Severity.Error)

                MudDialog'' {
                    Header "Unlock the content" onClose
                    DialogContent(
                        MudFocusTrap'' {
                            adapt {
                                let! binding = pass.WithSetter()
                                MudTextField'' {
                                    Label "Password"
                                    Value' binding
                                    InputType InputType.Password
                                    Variant Variant.Outlined
                                    FullWidth
                                    AutoFocus
                                    Adornment Adornment.Start
                                    AdornmentIcon Icons.Material.Filled.Password
                                    OnKeyUp(fun e -> if e.Key = "Enter" then decrypt ())
                                }
                            }
                        }
                    )
                    DialogActions [|
                        adapt {
                            let! binding = turnOffSecret.WithSetter()
                            MudSwitch'' {
                                Label "Turn off protection"
                                Color Color.Error
                                Value' binding
                            }
                        }
                        MudButton'' {
                            Color Color.Primary
                            Variant Variant.Filled
                            OnClick(ignore >> decrypt)
                            "Unlock"
                        }
                    |]
                }
        )


    static member LockerView(contentWrapper: LoopContentWrapper, secret: string) : NodeRenderFragment =
        html.inject (fun (dialogService: IDialogService) -> MudPaper'' {
            style {
                padding 8
                displayFlex
                alignItemsCenter
                justifyContentCenter
            }
            Elevation 5
            MudIconButton'' {
                Icon Icons.Material.Filled.Lock
                Color Color.Warning
                OnClick(fun _ ->
                    dialogService.Show(
                        DialogOptions(MaxWidth = MaxWidth.ExtraSmall, FullWidth = true),
                        fun ctx -> LoopContentEditor.DecryptDialog(contentWrapper, secret, ctx.Close)
                    )
                )
            }
        })
