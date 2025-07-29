namespace Brainloop.Loop

open System
open System.Text.Json
open Microsoft.JSInterop
open FSharp.Data.Adaptive
open MudBlazor
open Fun.Blazor
open Brainloop.Share


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
                        | LoopContentItem.ToolCall x -> if not ignoreToolCall then LoopToolCallView.Create(contentWrapper, x, index)
                        | LoopContentItem.File x -> LoopContentView.File(x)
                        | LoopContentItem.Excalidraw x -> LoopContentView.Excalidraw(contentWrapper, x)
                        | LoopContentItem.Secret x -> LoopContentEditor.LockerView(contentWrapper, x)
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
                            | LoopContentItem.Secret x -> html.text x
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
