namespace Brainloop.Loop

open System
open Microsoft.JSInterop
open Microsoft.Extensions.Logging
open Microsoft.Extensions.DependencyInjection
open Microsoft.AspNetCore.Components.Web
open FSharp.Data.Adaptive
open IcedTasks
open MudBlazor
open Fun.Blazor
open BlazorMonaco
open BlazorMonaco.Editor
open Brainloop.Share
open Brainloop.Function


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
                                fun ctx -> LoopContentSensitive.EncryptDialog(newContent, ctx.Close, onEncrypted = (postSave >> ignore))
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
                                    ref (fun x ->
                                        hook.RegisterAutoCompleteForAddFunction(fun _ -> x)
                                        editorRefs[index] <- x
                                    )
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
                                        LoopContentExcalidrawer.Create(
                                            contentWrapper.LoopId,
                                            contentWrapper = contentWrapper,
                                            excalidraw = x,
                                            onClose = ctx.Close
                                        )
                                )
                            )
                          }
                        | LoopContentItem.Secret x -> LoopContentSensitive.LockerView(contentWrapper, x)
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
                        styleElt {
                            ruleset ".loop-content-editor .monaco-editor-container" {
                                height (if items.Count <= 1 then "calc(100vh - 450px)" else "250px")
                            }
                        }
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
