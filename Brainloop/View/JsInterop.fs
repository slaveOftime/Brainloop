[<AutoOpen>]
module Fun.Blazor.JsInterop

open System.Threading.Tasks
open System.Diagnostics.CodeAnalysis
open Microsoft.JSInterop
open Fun.Blazor


let private highlightCode =
    js
        """
        window.highlightCode = () => {
            if (!!Prism) {
                Prism.highlightAll();
            } else {
                setTimeout(Prism.highlightAll, 5000)
            }
        }
        """

let private scrollUtilsCode =
    js
        """
        window.scrollToBottom = (id, smooth) => {
            const container = document.getElementById(id);
            if (!!container) {
                container.scrollTo({
                    top: container.scrollHeight,
                    behavior: smooth ? 'smooth' : 'instant'
                });
            }
        };

        window.scrollToElementBottom = (containerId, elementId, smooth) => {
            const container = document.getElementById(containerId);
            const element = document.getElementById(elementId);
            if (!!container && !!element) {
                container.scrollTo({
                    top: element.offsetTop + element.offsetHeight - container.offsetHeight,
                    behavior: smooth ? 'smooth' : 'instant'
                });
            }
        };

        window.scrollToElementTop = (containerId, elementId, smooth) => {
            const container = document.getElementById(containerId);
            const element = document.getElementById(elementId);
            if (!!container && !!element) {
                container.scrollTo({
                    top: element.offsetTop - container.offsetHeight,
                    behavior: smooth ? 'smooth' : 'instant'
                });
            }
        };
        """

let private simpleUtilsCode =
    js
        """
        window.isInView = (elementId) => {
            try {
                const element = document.getElementById(elementId);
                const rect = element.getBoundingClientRect();
                return (
                    rect.top < window.innerHeight &&
                    rect.bottom > 0 &&
                    rect.left < window.innerWidth &&
                    rect.right > 0
                );
            }
            catch (e) {
                console.error(e);
                return false;
            }
        };

        window.copyInnerText = (id) => {
          const codeBlock = document.getElementById(id);
          const text = codeBlock.innerText;
          navigator.clipboard.writeText(text);
        };

        window.copyText = (text) => {
          navigator.clipboard.writeText(text);
        };

        window.copyElementAsImageOld = async (id) => {
            const element = document.getElementById(id);
            const canvas = await html2canvas(element, {
                useCORS: true,
                allowTaint: true,
            });
            await new Promise((resolve, reject) => {
                canvas.toBlob(async (blob) => {
                    try {
                        const item = new ClipboardItem({ "image/png": blob });
                        await navigator.clipboard.write([item]);
                        resolve(null);
                    } catch (error) {
                        reject(error);
                    }
                });
            });
        };

        window.copyElementAsImage = async (id) => {
            const element = document.getElementById(id);
            const blob = await htmlToImage.toBlob(element);
            const item = new ClipboardItem({ "image/png": blob });
            await navigator.clipboard.write([item]);
        };

        window.getImageFromClipboard = async () => {
            const items = await navigator.clipboard.read();
            for (let index in items) {
                const item = items[index];
                console.log(item);
                if (item.types.includes('image/png')) {
                    const blob = await item.getType('image/png');
                    return await new Promise((resolve) => {
                        const reader = new FileReader();
                        reader.onload = (event) => {
                            resolve(JSON.stringify({ type: "png", data: event.target.result }));
                        };
                        reader.readAsDataURL(blob);
                    });
                }
            }
            return null;
        };

        window.resizeIframe = (id) => {
            const iframe = document.getElementById(id);
            if (!!iframe) {
                iframe.style.height = iframe.contentWindow.document.documentElement.scrollHeight + 'px';
            }
        };

        window.setupOnPasteImageEventHandler = (id, hanlderRef) => {
            const element = document.getElementById(id);
            element.onpaste = (e) => {
                try {
                    const items = (event.clipboardData || event.clipboardData).items;
                    for (let index in items) {
                        const item = items[index];
                        console.log(item);
                        if (item.type.startsWith('image/')) {
                            const blob = item.getAsFile();
                            const reader = new FileReader();
                            reader.onload = function(event) {
                                hanlderRef.invokeMethodAsync("Handle", JSON.stringify({ type: item.type, data: event.target.result }));
                            };
                            reader.readAsDataURL(blob);
                        }
                    }
                } 
                catch (e) {
                    console.error(e);
                }
            };
        };
        """

let private blazorMonacoCode =
    js
        """
        const registerCompletionItemProviders = new Map();
        window.registerMonacoCompletionItemProvider = async (id, language, triggerCharacters, completionItemProviderRef) => {
            registerCompletionItemProviders.set(id, monaco.languages.registerCompletionItemProvider(language, {
                triggerCharacters: triggerCharacters,
                provideCompletionItems: (model, position, context, cancellationToken) => {
                    return completionItemProviderRef.invokeMethodAsync("ProvideCompletionItems", decodeURI(model.uri.toString()), position, context);
                },
                resolveCompletionItem: (completionItem, cancellationToken) => {
                    return completionItemProviderRef.invokeMethodAsync("ResolveCompletionItem", completionItem);
                }
            }));
        };
        window.unregisterMonacoCompletionItemProvider = (id) => {
            if (registerCompletionItemProviders.has(id)) {
                const provider = registerCompletionItemProviders.get(id);
                provider.dispose(); // Dispose of the provider
                registerCompletionItemProviders.delete(id); // Remove it from the map
            } else {
                console.warn(`No provider found ${id}`);
            }
        };

        window.setMonacoOnDidSelectSuggestion = (id, eventHanlderRef) => {
            const editor = window.blazorMonaco.editor.getEditor(id);
            if (editor != null) {
                editor.onDidSelectSuggestion((event) => {
                    console.log(event);
                    return eventHanlderRef.invokeMethodAsync("Handle", event.item.insertText);
                });
            }
        };
        """

let private excalidrawCode =
    js
        """
        const excalidrawHandlers = new Map();
        window.isExcalidrawReady = () => window.renderExcalidraw != null;
        window.openExcalidraw = (id, height, data, isDarkMode, onChangeEventHanlderRef, onCloseEventHanlderRef) => {
            const handler = window.renderExcalidraw({
                elementId: id,
                height: height,
                jsonData: data,
                isDarkMode: isDarkMode,
                onChange: () => {
                    if (onChangeEventHanlderRef) {
                        onChangeEventHanlderRef.invokeMethodAsync("Handle", null);
                    }
                },
                onClose: async () => {
                    if (onCloseEventHanlderRef) {
                        await onCloseEventHanlderRef.invokeMethodAsync("Handle", null);
                        window.closeExcalidraw(id);
                    }
                },
            });
            excalidrawHandlers.set(id, handler);
        };
        window.closeExcalidraw = (id) => {
            const handler = excalidrawHandlers.get(id);
            if (handler) {
                excalidrawHandlers.delete(id);
            } else {
                console.warn(`No excalidraw handler found for id: ${id}`);
            }
        };
        window.exportExcalidrawToJson = (id) => {
            const handler = excalidrawHandlers.get(id);
            if (handler) {
                return handler.exportJson();
            } else {
                console.warn(`No excalidraw handler found for id: ${id}`);
                return null;
            }
        };
        window.exportExcalidrawToPng = async (id, isDarkMode) => {
            const handler = excalidrawHandlers.get(id);
            if (handler) {
                const blob = await handler.exportPngBlob(isDarkMode);
                return await new Promise((resolve, reject) => {
                    const reader = new FileReader();
                    reader.onload = () => resolve(new Uint8Array(reader.result));
                    reader.onerror = e => reject(reader.error);
                    reader.readAsArrayBuffer(blob);
                });
            } else {
                console.warn(`No excalidraw handler found for id: ${id}`);
                return null;
            }
        };
        """


let interopScript = fragment {
    highlightCode
    scrollUtilsCode
    simpleUtilsCode
    blazorMonacoCode
    excalidrawCode
}


type EventHandler [<DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof<EventHandler>)>] (fn: string -> Task<unit>) =
    [<JSInvokable>]
    member _.Handle(data: string) : Task = fn data


type IJSRuntime with
    member js.HighlightCode() = js.InvokeAsync("highlightCode")

    member js.ScrollToBottom(id: string, ?smooth: bool) = js.InvokeVoidAsync("scrollToBottom", id, defaultArg smooth false)
    member js.ScrollToElementBottom(containerId: string, elementId: string, ?smooth: bool) =
        js.InvokeVoidAsync("scrollToElementBottom", containerId, elementId, defaultArg smooth false)
    member js.ScrollToElementTop(containerId: string, elementId: string, ?smooth: bool) =
        js.InvokeVoidAsync("scrollToElementTop", containerId, elementId, defaultArg smooth false)

    member js.IsInView(elementId: string) = js.InvokeAsync<bool>("isInView", elementId)

    member js.CopyInnerText(id: string) = js.InvokeVoidAsync("copyInnerText", id)
    member js.CopyText(text: string) = js.InvokeVoidAsync("copyText", text)

    member js.CopyElementAsImage(id: string) = js.InvokeVoidAsync("copyElementAsImage", id)

    member js.GetImageFromClipboard() = js.InvokeAsync<string>("getImageFromClipboard")

    member js.RegisterCompletionItemProvider
        (providerId: string, language: string, completionItemProvider: BlazorMonaco.Languages.CompletionItemProvider)
        =
        js.InvokeVoidAsync(
            "registerMonacoCompletionItemProvider",
            providerId,
            language,
            completionItemProvider.TriggerCharacters,
            DotNetObjectReference.Create(completionItemProvider)
        )

    member js.UnregisterCompletionItemProvider(providerId: string) = js.InvokeVoidAsync("unregisterMonacoCompletionItemProvider", providerId)

    member js.HandleMonacoOnDidSelectSuggestion(id: string, fn) =
        js.InvokeVoidAsync("setMonacoOnDidSelectSuggestion", id, DotNetObjectReference.Create(EventHandler(ignore >> fn)))


    member js.IsExcalidrawReady() = js.InvokeAsync<bool>("isExcalidrawReady")

    member js.OpenExcalidraw(id: string, height: int, data: string, isDarkMode: bool, onChange, onClose) =
        js.InvokeVoidAsync(
            "openExcalidraw",
            id,
            height,
            data,
            isDarkMode,
            DotNetObjectReference.Create(EventHandler(onChange)),
            DotNetObjectReference.Create(EventHandler(onClose))
        )

    member js.CloseExcalidraw(id: string) = js.InvokeVoidAsync("closeExcalidraw", id)

    member js.ExportExcalidrawToJson(id: string) = js.InvokeAsync<string>("exportExcalidrawToJson", id)

    member js.ExportExcalidrawToPng(id: string, ?isDarkMode: bool) = js.InvokeAsync<byte[]>("exportExcalidrawToPng", id, isDarkMode)
