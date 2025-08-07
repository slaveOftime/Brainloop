namespace Brainloop.Loop

open System
open System.IO
open Microsoft.JSInterop
open Microsoft.Extensions.Logging
open Microsoft.Extensions.DependencyInjection
open FSharp.Data.Adaptive
open SixLabors.ImageSharp
open SixLabors.ImageSharp.Processing
open IcedTasks
open MudBlazor
open Fun.Blazor
open Brainloop.Share
open Brainloop.Memory


type LoopContentExcalidrawer =

    static member Create
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

