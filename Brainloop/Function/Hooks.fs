[<AutoOpen>]
module Brainloop.Function.Hooks

open System
open System.Threading.Tasks
open System.Collections.Generic
open Microsoft.Extensions.DependencyInjection
open Microsoft.SemanticKernel
open Microsoft.JSInterop
open IcedTasks
open Fun.Result
open Fun.Blazor
open BlazorMonaco.Editor
open BlazorMonaco.Languages
open Brainloop.Db


type IComponentHook with

    member hook.RegisterAutoCompleteForAddFunction
        (
            getInputRef: unit -> (StandaloneCodeEditor | null),
            ?startLoading: unit -> unit,
            ?loadingFinished: unit -> unit
        ) =
        hook.AddFirstAfterRenderTask(fun _ -> task {
            let js = hook.ServiceProvider.GetRequiredService<IJSRuntime>()
            let functionService = hook.ServiceProvider.GetRequiredService<IFunctionService>()
            let providerId = Random.Shared.Next().ToString()
            let plugins = List<KernelPlugin>()

            do!
                js.RegisterCompletionItemProvider(
                    providerId,
                    "markdown",
                    CompletionItemProvider(
                        System.Collections.Generic.List [ ">" ],
                        CompletionItemProvider.ProvideDelegate(fun modelUri _ context -> task {
                            let! model = BlazorMonaco.Editor.Global.GetModel(js, modelUri)
                            match context.TriggerCharacter, getInputRef () with
                            | _, null -> return CompletionList()
                            | ">", editor ->
                                let! editorModel = editor.GetModel()
                                if model.Id <> editorModel.Id then
                                    return CompletionList()
                                else
                                    let! plugins = valueTask {
                                        if plugins.Count = 0 then
                                            startLoading |> Option.iter (fun f -> f ())
                                            try
                                                let! fns = functionService.GetFunctions() |> ValueTask.map (Seq.map _.Id)
                                                let! results = functionService.GetKernelPlugins(fns)
                                                plugins.AddRange(results)
                                            with _ ->
                                                ()
                                            loadingFinished |> Option.iter (fun f -> f ())

                                        return plugins
                                    }
                                    return
                                        CompletionList(
                                            Suggestions =
                                                System.Collections.Generic.List [
                                                    for plugin in plugins do
                                                        for fn in plugin do
                                                            let label = $"[{plugin.Name.ToAscii()} -> {fn.Name.ToAscii()}]"
                                                            CompletionItem(
                                                                LabelAsString = label,
                                                                Kind = CompletionItemKind.Function,
                                                                Command = Command(),
                                                                InsertText = label
                                                            )
                                                ]
                                        )
                            | _ -> return CompletionList()
                        })
                    )
                )

            hook.OnDispose.Add(fun _ -> js.UnregisterCompletionItemProvider(providerId) |> ignore)
        })
